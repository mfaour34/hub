using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hub.Core.Backup;

/// <summary>A backup in the repo: one commit Hub made.</summary>
public sealed record BackupCommit(string Sha, DateTimeOffset Date, string Message);

/// <summary>What linking found out about the repo and token.</summary>
public sealed record LinkCheck(string Repo, string Branch, DateTimeOffset? TokenExpires);

/// <summary>
/// Backs up to and reads from one private GitHub repo through the REST API, with a fine-grained token that can only
/// write to that repo. The token only ever goes to api.github.com over HTTPS: the client has a fixed base address,
/// redirects are not followed (the caller's handler must have AllowAutoRedirect off), and it never appears in errors.
/// </summary>
public sealed partial class GitHubBackup
{
    public static readonly Uri Api = new("https://api.github.com/");
    /// <summary>Commit messages Hub writes start with this, so restore only lists Hub's own backups.</summary>
    public const string CommitPrefix = "Hub backup";

    readonly HttpClient _http;
    readonly string _repo;
    readonly string _token;

    /// <param name="http">Its handler must not follow redirects; the base address is ignored.</param>
    public GitHubBackup(HttpClient http, string repo, string token)
    {
        _http = http;
        _repo = CheckRepo(repo);
        _token = CheckToken(token);
    }

    /// <summary>"owner/name", as GitHub allows them; anything else could change the request URL.</summary>
    public static string CheckRepo(string repo)
    {
        repo = repo.Trim();
        if (repo.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) repo = repo["https://github.com/".Length..].TrimEnd('/');
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        if (!RepoName().IsMatch(repo) || repo.Split('/')[1] is "." or "..")
            throw new ArgumentException("Enter the repo as owner/name, e.g. your-name/hub-settings");
        return repo;
    }

    /// <summary>Only fine-grained tokens can be limited to one repo; classic tokens reach every repo of the account.</summary>
    public static string CheckToken(string token)
    {
        token = token.Trim();
        if (token.StartsWith("ghp_", StringComparison.Ordinal) || token.StartsWith("gho_", StringComparison.Ordinal))
            throw new ArgumentException("That's a classic token, which can reach all your repos. Make a fine-grained token for just this repo.");
        if (!token.StartsWith("github_pat_", StringComparison.Ordinal) || token.Length > 255 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("That doesn't look like a fine-grained GitHub token (they start with github_pat_)");
        return token;
    }

    /// <summary>
    /// Refuses a repo that isn't private or already holds files Hub didn't write, a token that can't write to it, and
    /// a token that can reach other private repos or has permissions beyond the repo's contents.
    /// </summary>
    public async Task<LinkCheck> CheckAsync(CancellationToken ct = default)
    {
        var (repo, headers) = await GetRepoAsync(ct);
        var branch = repo["default_branch"]?.GetValue<string>() ?? "main";

        // GitHub can't tell Hub what a fine-grained token was granted, so each check asks GitHub to do something.
        // A blob nothing points to proves "Contents: write" without changing the repo (GitHub cleans it up).
        await SendAsync(HttpMethod.Post, $"repos/{_repo}/git/blobs", new JsonObject { ["content"] = "", ["encoding"] = "utf-8" }, ct,
            forbidden: "The token can read the repo but not write to it. Give it “Contents: Read and write”.");

        foreach (var (path, permission) in ExtraPermissionProbes)
            if (await StatusAsync(path.Replace("{repo}", _repo), ct) == HttpStatusCode.OK)
                throw new InvalidOperationException($"The token also has “{permission}”. Make one with only “Contents: Read and write” on this repo.");

        // a token for "All repositories" (or several) lists other private repos; public ones are visible to any token
        var others = new List<string>();
        for (int page = 1; ; page++)
        {
            if (page > 10) throw new InvalidOperationException("The token can see too many repos. Make one with “Only select repositories” set to just this repo.");
            var (list, _) = await SendAsync(HttpMethod.Get, $"user/repos?per_page=100&page={page}", null, ct);
            var repos = list.AsArray();
            others.AddRange(repos.Where(r => r?["private"]?.GetValue<bool>() == true)
                .Select(r => r!["full_name"]!.GetValue<string>())
                .Where(n => !n.Equals(_repo, StringComparison.OrdinalIgnoreCase)));
            if (repos.Count < 100) break;
        }
        if (others.Count > 0)
            throw new InvalidOperationException($"The token can also reach {string.Join(", ", others.Take(3))}{(others.Count > 3 ? $" and {others.Count - 3} more" : "")}. "
                + "Make one with “Only select repositories” set to just this repo.");

        await CheckNoForeignFilesAsync(branch, ct);
        return new LinkCheck(_repo, branch, Expiry(headers));
    }

    /// <summary>Endpoints that answer 200 only when the token has a permission Hub doesn't need.</summary>
    static readonly (string Path, string Permission)[] ExtraPermissionProbes =
    [
        ("repos/{repo}/hooks", "Webhooks"),
        ("repos/{repo}/keys", "Administration"),
        ("repos/{repo}/actions/secrets/public-key", "Secrets"),
        ("repos/{repo}/actions/workflows", "Actions"),
        ("user/keys", "Git SSH keys"),
        ("user/emails", "Email addresses"),
    ];

    /// <summary>Each backup replaces every file, so a repo that already holds other files isn't one to back up to.</summary>
    async Task CheckNoForeignFilesAsync(string branch, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, $"repos/{_repo}/git/trees/{Branch(branch)}?recursive=1", null);
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return; // empty repo
        await ThrowIfFailed(res, null, null, ct);
        var tree = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!["tree"]!.AsArray();
        var foreign = tree.Where(e => e?["type"]?.GetValue<string>() == "blob")
            .Select(e => e!["path"]!.GetValue<string>())
            .Where(path => !IsHubFile(path)).ToList();
        if (foreign.Count > 0)
            throw new InvalidOperationException($"{_repo} already has files Hub didn't write ({string.Join(", ", foreign.Take(3))}{(foreign.Count > 3 ? ", …" : "")}). "
                + "Each backup replaces every file, so use an empty private repo.");
    }

    static bool IsHubFile(string path) =>
        path is BackupFiles.AudioFile or BackupFiles.MouseFile or "README.md" or ".gitattributes" or ".gitignore" or "LICENSE"
        || path.StartsWith("profiles/", StringComparison.Ordinal) || path.StartsWith("in use/", StringComparison.Ordinal);

    /// <summary>
    /// Before every backup: the repo must still exist and be private (visibility can change after linking). Returns
    /// when the token expires, as GitHub reports it now.
    /// </summary>
    public async Task<DateTimeOffset?> EnsurePrivateAsync(CancellationToken ct = default) => Expiry((await GetRepoAsync(ct)).Headers);

    async Task<(JsonNode Repo, HttpResponseHeaders Headers)> GetRepoAsync(CancellationToken ct)
    {
        var (repo, headers) = await SendAsync(HttpMethod.Get, $"repos/{_repo}", null, ct, notFound: $"GitHub doesn't show {_repo} to this token. Check the name and that the token has access to it.");
        // "internal" repos report private: true but the whole enterprise can read them
        if (repo["visibility"]?.GetValue<string>() is { } v ? v != "private" : repo["private"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"{_repo} isn't private. Backups only go to a private repo.");
        return (repo, headers);
    }

    async Task<HttpStatusCode> StatusAsync(string path, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, path, null);
        using var res = await _http.SendAsync(req, ct);
        return res.StatusCode;
    }

    /// <summary>A branch name in a URL path, keeping its slashes.</summary>
    static string Branch(string branch) => string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));

    /// <summary>GitHub's "github-authentication-token-expiration" header: "2026-12-01 10:00:00 UTC" or "… +0100".</summary>
    static DateTimeOffset? Expiry(HttpResponseHeaders headers) =>
        headers.TryGetValues("github-authentication-token-expiration", out var v) ? ParseExpiry(v.First()) : null;

    public static DateTimeOffset? ParseExpiry(string value)
    {
        var m = ExpiryFormat().Match(value.Trim());
        if (!m.Success) return null;
        var zone = m.Groups[3].Value is "UTC" ? "+00:00" : m.Groups[3].Value.Insert(3, ":");
        return DateTimeOffset.TryParse($"{m.Groups[1].Value}T{m.Groups[2].Value}{zone}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>
    /// Makes the repo hold exactly <paramref name="files"/> in one commit on <paramref name="branch"/>. Returns the
    /// commit, or null when nothing changed since the last backup.
    /// </summary>
    public async Task<string?> PushAsync(string branch, IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default)
    {
        var refPath = $"repos/{_repo}/git/ref/heads/{Branch(branch)}";
        var (head, _) = await SendAsync(HttpMethod.Get, refPath, null, ct,
            notFound: $"{_repo} has no {branch} branch yet. Add a README on GitHub so the repo isn't empty, then back up again.");
        var parent = head["object"]!["sha"]!.GetValue<string>();
        var (parentCommit, _) = await SendAsync(HttpMethod.Get, $"repos/{_repo}/git/commits/{parent}", null, ct);

        var tree = new JsonObject
        {
            // no base tree: files left out (e.g. a deleted profile's .txt) disappear
            ["tree"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject
            {
                ["path"] = f.Key, ["mode"] = "100644", ["type"] = "blob", ["content"] = f.Value,
            }).ToArray()),
        };
        var (newTree, _) = await SendAsync(HttpMethod.Post, $"repos/{_repo}/git/trees", tree, ct);
        var treeSha = newTree["sha"]!.GetValue<string>();
        if (treeSha == parentCommit["tree"]?["sha"]?.GetValue<string>()) return null;

        var (commit, _) = await SendAsync(HttpMethod.Post, $"repos/{_repo}/git/commits",
            new JsonObject { ["message"] = message, ["tree"] = treeSha, ["parents"] = new JsonArray(parent) }, ct);
        var sha = commit["sha"]!.GetValue<string>();
        await SendAsync(HttpMethod.Patch, $"repos/{_repo}/git/refs/heads/{Branch(branch)}",
            new JsonObject { ["sha"] = sha, ["force"] = false }, ct);
        return sha;
    }

    /// <summary>Hub's backups on the branch, newest first.</summary>
    public async Task<List<BackupCommit>> ListAsync(string branch, CancellationToken ct = default)
    {
        var (list, _) = await SendAsync(HttpMethod.Get, $"repos/{_repo}/commits?sha={Uri.EscapeDataString(branch)}&per_page=50", null, ct);
        return list.AsArray()
            .Select(c => new BackupCommit(c!["sha"]!.GetValue<string>(),
                DateTimeOffset.Parse(c["commit"]!["committer"]!["date"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                c["commit"]!["message"]!.GetValue<string>()))
            .Where(c => c.Message.StartsWith(CommitPrefix, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>A file as it was in a backup; null when the backup doesn't have it.</summary>
    public async Task<string?> ReadAsync(string sha, string path, CancellationToken ct = default)
    {
        if (!Sha().IsMatch(sha)) throw new ArgumentException("Not a backup id");
        if (path is not (BackupFiles.AudioFile or BackupFiles.MouseFile)) throw new ArgumentException("Hub only reads audio.json and mouse.json");
        using var req = Request(HttpMethod.Get, $"repos/{_repo}/contents/{path}?ref={sha}", null);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfFailed(res, null, null, ct);
        if (res.Content.Headers.ContentLength > BackupFiles.MaxFileBytes) throw new InvalidDataException($"{path} is too large to be a Hub backup");
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[BackupFiles.MaxFileBytes + 1];
        int n = 0, r;
        while (n < buffer.Length && (r = await s.ReadAsync(buffer.AsMemory(n), ct)) > 0) n += r;
        if (n > BackupFiles.MaxFileBytes) throw new InvalidDataException($"{path} is too large to be a Hub backup");
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body)
    {
        var req = new HttpRequestMessage(method, new Uri(Api, path));
        if (req.RequestUri!.Host != Api.Host || req.RequestUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Hub only talks to api.github.com");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.UserAgent.ParseAdd("Hub-settings-backup");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    async Task<(JsonNode Body, HttpResponseHeaders Headers)> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct,
        string? notFound = null, string? forbidden = null)
    {
        using var req = Request(method, path, body);
        using var res = await _http.SendAsync(req, ct);
        await ThrowIfFailed(res, notFound, forbidden, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        return (JsonNode.Parse(text) ?? new JsonObject(), res.Headers);
    }

    /// <summary>Turns GitHub's answer into a message for the user (GitHub never echoes the token back).</summary>
    static async Task ThrowIfFailed(HttpResponseMessage res, string? notFound, string? forbidden, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        string? detail = null;
        try { detail = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["message"]?.GetValue<string>(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        throw new InvalidOperationException((int)res.StatusCode switch
        {
            401 => "GitHub didn't accept the token. It may have expired or been revoked; link the repo again with a new one.",
            403 when res.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.First() == "0" => "GitHub's rate limit was reached. Try again in a few minutes.",
            403 when forbidden is not null => forbidden,
            403 => $"GitHub refused: {detail ?? "the token isn't allowed to do that"}. Give the token “Contents: Read and write” on this repo.",
            404 when notFound is not null => notFound,
            404 => "GitHub couldn't find that in the repo.",
            >= 300 and < 400 => "GitHub answered with a redirect, which Hub doesn't follow.",
            409 => $"GitHub reported a conflict: {detail ?? "the repo changed meanwhile"}. Try again.",
            422 => $"GitHub rejected the backup: {detail ?? "invalid request"}.",
            _ => $"GitHub error {(int)res.StatusCode}{(detail is null ? "" : $": {detail}")}",
        });
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoName();

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2}:\d{2})\s*(UTC|[+-]\d{4})$")]
    private static partial Regex ExpiryFormat();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha();
}
