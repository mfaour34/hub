using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Hub.Core.Audio;
using Hub.Core.Backup;

namespace Hub.Tests;

public class BackupFilesTests
{
    static AudioBackup Audio() => new()
    {
        Profiles =
        {
            ["Game"] = [new("IEM Target", -4, [new(FilterType.LowShelf, 120, -6, 0.7), new(FilterType.Peaking, 2000, 3, 1.2)], false, false, "IEM_Target.txt")],
            ["Media"] = [new("Rock: live/loud?", 0, [new(FilterType.Peaking, 1000, 2, 1)], true, true)],
        },
        Devices =
        [
            new("Speakers (Wireless Headset)", new() { ["Game"] = "Competitive", ["Media"] = "Flat" }, new() { ["Game"] = 4 }),
            new("Headphones (USB-C Adapter)", new() { ["Game"] = "IEM Target" }, []),
        ],
        DefaultDevice = "Speakers (Wireless Headset)",
        AppChannels = { ["brave"] = "media", ["cs2"] = "game", ["spotify"] = "music" },
        Volumes = { ["Game"] = 0.8 },
        ChatMix = -0.25,
        HeadsetEq = [new("Footsteps", [-4, -2, -1, 4, 4])],
    };

    static MouseBackup Mouse() => new()
    {
        Model = "Razer Viper V3 Pro", DpiStages = [400, 800, 1600], ActiveStage = 2, PollingHz = 2000, IdleMinutes = 5, LowBatteryPercent = 15,
        Lift = new(true, 1, 3, 2), DynamicSensitivity = new(true, 3, [[0, 1], [24, 1.5], [100, 1.5]]), Rotation = new(false, 0),
        Buttons = { ["4"] = new(1, [4]), ["96"] = new(6, [6]) },
    };

    static string Apo(ProfileBackup p) => new EqPresetData(p.Name, p.GainDb, p.Filters).ToEqualizerApo(autoHeadroom: false);

    [Fact]
    public void Builds_json_for_everything_and_equalizer_apo_text_for_every_profile()
    {
        var files = BackupFiles.Build(Audio(), Mouse(), [new("USB-C adapter", "Game", "Preamp: -4 dB\r\n")], Apo);

        Assert.Equal(["README.md", "audio.json", "in use/USB-C adapter - game.txt", "mouse.json",
            "profiles/game/IEM Target.txt", "profiles/media/Rock_ live_loud_.txt"], files.Keys);
        Assert.Contains("Filter: ON LSC Fc 120 Hz Gain -6 dB Q 0.7", files["profiles/game/IEM Target.txt"]);
        Assert.Equal("Preamp: -4 dB\n", files["in use/USB-C adapter - game.txt"]); // one line ending everywhere
        Assert.Contains("\"type\": \"LowShelf\"", files["audio.json"]); // readable, not enum numbers
        Assert.DoesNotContain('\r', files["audio.json"]);
    }

    [Fact]
    public void Reads_back_exactly_what_it_wrote()
    {
        var files = BackupFiles.Build(Audio(), Mouse(), [], Apo);
        var (audio, mouse) = BackupFiles.Parse(files["audio.json"], files["mouse.json"]);

        Assert.Equal(files["audio.json"], BackupJson.Write(audio));
        Assert.Equal(files["mouse.json"], BackupJson.Write(mouse));
        Assert.Equal(Audio().Profiles["Game"][0].Filters, audio.Profiles["Game"][0].Filters);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("  ends with dots...  ", "ends with dots")]
    [InlineData("...", "profile")]
    public void File_names_work_everywhere(string name, string expected) => Assert.Equal(expected, BackupFiles.SafeFileName(name));

    [Fact]
    public void Two_names_that_end_up_the_same_file_both_get_one()
    {
        var a = Audio();
        a.Profiles["Game"].Add(new("IEM Target?", 0, [], true, true));
        var files = BackupFiles.Build(a, null, [], Apo);
        Assert.Contains("profiles/game/IEM Target.txt", files.Keys);
        Assert.Contains("profiles/game/IEM Target_.txt", files.Keys);
        Assert.DoesNotContain("mouse.json", files.Keys);
    }

    public static TheoryData<string, Action<AudioBackup>> BadAudio => new()
    {
        { "Unknown channel", a => a.Profiles["Bass"] = [] },
        { "more than 20 filters", a => a.Profiles["Game"][0].Filters.AddRange(Enumerable.Repeat(new EqFilter(FilterType.Peaking, 100, 1, 1), 20)) },
        { "frequency", a => a.Profiles["Game"][0].Filters[0] = new(FilterType.Peaking, 5, 1, 1) },
        { "Q 0", a => a.Profiles["Game"][0].Filters[0] = new(FilterType.Peaking, 100, 1, 0) },
        { "Two Game profiles", a => a.Profiles["Game"].Add(a.Profiles["Game"][0] with { Name = "iem target" }) },
        { "default device", a => a.DefaultDevice = "Gone" },
        { "unknown channel “loud”", a => a.AppChannels["x"] = "loud" },
        { "tame level", a => a.Devices[0].Tame["Game"] = 40 },
        { "volume", a => a.Volumes["Chat"] = 3 },
        { "Headset EQ", a => a.HeadsetEq[0] = new("x", [1, 2, 3]) },
        { "missing or invalid name", a => a.Profiles["Game"][0] = a.Profiles["Game"][0] with { Name = " " } },
        { "newer Hub", a => a.Version = 2 },
    };

    [Theory]
    [MemberData(nameof(BadAudio))]
    public void Refuses_audio_hub_would_not_accept(string problem, Action<AudioBackup> spoil)
    {
        var a = Audio();
        spoil(a);
        var e = Assert.Throws<InvalidDataException>(() => BackupFiles.Parse(BackupJson.Write(a), null));
        Assert.Contains(problem, e.Message);
    }

    public static TheoryData<string, Action<MouseBackup>> BadMouse => new()
    {
        { "DPI", m => m.DpiStages.Add(50) },
        { "active DPI stage", m => m.ActiveStage = 4 },
        { "Polling rate 300", m => m.PollingHz = 300 },
        { "Button “1” can't be set", m => m.Buttons["1"] = new(1, [1]) },
        { "Button 4's assignment", m => m.Buttons["4"] = new(99, [1]) },
        { "Lift-off", m => m.Lift = new(true, 1, 3, 3) },
        { "Rotation", m => m.Rotation = new(true, 60) },
        { "curve", m => m.DynamicSensitivity = new(true, 3, [[0, 0], [1, 10], [2, 0], [3, 10], [100, 0]]) }, // 5× the editor's maximum
        { "curve", m => m.DynamicSensitivity = new(true, 3, [[0, 1], [50, 1.5], [40, 1.2], [100, 1.5]]) },
        { "curve", m => m.DynamicSensitivity = new(true, 3, [[0, 1], [100, 1.5]]) },
        { "Button 4's assignment", m => m.Buttons["4"] = new(1, [4, 4]) },
        { "Button 4's assignment", m => m.Buttons["4"] = new(2, [0, 0xFF]) },
        { "Button 4's assignment", m => m.Buttons["4"] = new(0, [1]) },
    };

    [Theory]
    [MemberData(nameof(BadMouse))]
    public void Refuses_mouse_settings_the_mouse_would_not_accept(string problem, Action<MouseBackup> spoil)
    {
        var m = Mouse();
        spoil(m);
        var e = Assert.Throws<InvalidDataException>(() => BackupFiles.Parse(BackupJson.Write(Audio()), BackupJson.Write(m)));
        Assert.Contains(problem, e.Message);
    }

    [Fact]
    public void Takes_the_button_shapes_hub_writes()
    {
        Assert.True(BackupFiles.IsButton(new(2, [0, 0x68])));  // F13
        Assert.True(BackupFiles.IsButton(new(6, [7])));        // DPI cycle down
        Assert.True(BackupFiles.IsButton(new(0, [])));         // disabled
        Assert.True(BackupFiles.IsButton(new(11, [1])));       // double click
        Assert.False(BackupFiles.IsButton(new(1, [1, 2, 3, 4, 5, 6])));
    }

    [Fact]
    public void Refuses_an_empty_filter_with_a_readable_message()
    {
        var node = JsonNode.Parse(BackupJson.Write(Audio()))!;
        node["profiles"]!["Game"]![0]!["filters"]!.AsArray().Add(null);
        var json = node.ToJsonString();
        Assert.Contains("empty or unknown filter", Assert.Throws<InvalidDataException>(() => BackupFiles.Parse(json, null)).Message);
    }

    [Fact]
    public void Refuses_files_too_large_or_not_json()
    {
        Assert.Contains("too large", Assert.Throws<InvalidDataException>(() => BackupFiles.Parse(new string(' ', BackupFiles.MaxFileBytes + 1), null)).Message);
        Assert.Contains("isn't valid", Assert.Throws<InvalidDataException>(() => BackupFiles.Parse("{\"profiles\": 5}", null)).Message);
    }

    [Fact]
    public void Says_what_a_restore_changes()
    {
        var now = Audio();
        now.Profiles["Game"].Add(new("Newer", 0, [], true, true));
        now.Devices[1].Presets["Game"] = "Newer";
        now.AppChannels["brave"] = "game";
        var nowMouse = Mouse();
        nowMouse.DpiStages[2] = 3200;

        var lines = BackupFiles.Diff(now, nowMouse, Audio(), Mouse());

        Assert.Equal(
        [
            "Game profile “Newer” is removed (not in this backup)",
            "Headphones (USB-C Adapter) · game: Newer → IEM Target",
            "brave plays on Media",
            "Mouse DPI stages: 400 / 800 / 3200 → 400 / 800 / 1600 (stage 2 active)",
        ], lines);
        Assert.Empty(BackupFiles.Diff(Audio(), Mouse(), Audio(), Mouse()));
        Assert.Equal(["Mouse settings: skipped, the mouse isn't connected"], BackupFiles.Diff(Audio(), null, Audio(), Mouse()));
    }
}

public class DeviceRestoreTests
{
    [Fact]
    public void Matches_devices_by_name_and_keeps_the_rest_waiting_for_their_device()
    {
        var map = new Dictionary<string, DeviceAudioSetup>
        {
            ["old-headset-id"] = new() { Name = "Speakers (Wireless Headset)", Presets = { ["Game"] = "Flat" } },
            ["other"] = new() { Name = "Monitor", Presets = { ["Game"] = "Flat" } },
        };
        var s = new DeviceSetups(map, "other", new DeviceAudioSetup());
        s.Restore(
            [
                new("Speakers (Wireless Headset)", new() { ["Game"] = "Competitive" }, []),
                new("Headphones (USB-C adapter)", new() { ["Game"] = "IEM Target" }, new() { ["Game"] = 2 }),
                new("Not plugged in", new() { ["Game"] = "Zero" }, []),
            ],
            "Headphones (USB-C adapter)", [("usb-id", "Headphones (USB-C adapter)")]);

        Assert.Equal("Competitive", map["old-headset-id"].Presets["Game"]); // the setup Hub already had
        Assert.Equal("IEM Target", map["usb-id"].Presets["Game"]); // a Windows device with that name
        Assert.Equal(2, map["usb-id"].Tame["Game"]);
        Assert.Equal("usb-id", s.DefaultId);
        Assert.Equal("Flat", map["other"].Presets["Game"]); // not in the backup: untouched
        Assert.True(map.ContainsKey("name:Not plugged in"));

        // the device shows up later under its own id and picks up its restored setup
        Assert.Equal("Zero", s.For("new-id", "Not plugged in").Presets["Game"]);
        Assert.False(map.ContainsKey("name:Not plugged in"));
    }

    [Fact]
    public void A_device_windows_lists_twice_gets_the_backup_under_both_ids()
    {
        var map = new Dictionary<string, DeviceAudioSetup>
        {
            ["old-id"] = new() { Name = "Speakers (Wireless Headset)", Presets = { ["Game"] = "Flat" } },
            ["new-id"] = new() { Name = "Speakers (Wireless Headset)", Presets = { ["Game"] = "Flat" } },
            ["name:speakers (wireless headset)"] = new() { Name = "Speakers (Wireless Headset)" },
        };
        var s = new DeviceSetups(map, "new-id", new DeviceAudioSetup());
        s.Restore([new("Speakers (Wireless Headset)", new() { ["Game"] = "Competitive" }, [])], "Speakers (Wireless Headset)", []);

        Assert.Equal("Competitive", map["old-id"].Presets["Game"]);
        Assert.Equal("Competitive", map["new-id"].Presets["Game"]);
        Assert.Equal("new-id", s.DefaultId); // already one of them: kept
        Assert.Equal(2, map.Count); // the stale placeholder went
    }

    [Fact]
    public void A_waiting_setup_is_found_whatever_the_case_of_the_name()
    {
        var map = new Dictionary<string, DeviceAudioSetup>();
        var s = new DeviceSetups(map, null, new DeviceAudioSetup());
        s.Restore([new("USB Headphones", new() { ["Game"] = "Zero" }, [])], null, []);
        Assert.Equal("Zero", s.For("id", "usb headphones").Presets["Game"]);
    }

    [Fact]
    public void A_restored_default_that_appears_later_stays_the_default()
    {
        var map = new Dictionary<string, DeviceAudioSetup>();
        var s = new DeviceSetups(map, null, new DeviceAudioSetup());
        s.Restore([new("Headset", new() { ["Game"] = "Competitive" }, [])], "Headset", []);
        Assert.Equal("name:Headset", s.DefaultId);

        s.For("headset-id", "Headset");
        Assert.Equal("headset-id", s.DefaultId);
        Assert.Equal("Competitive", s.For("iem", "IEMs").Presets["Game"]); // new devices copy it
    }
}

public class GitHubBackupTests
{
    const string Token = "github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz";

    sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, Uri Uri, string? Auth, string? Body)> Requests = [];
        public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add((req.Method, req.RequestUri!, req.Headers.Authorization?.ToString(),
                req.Content is null ? null : await req.Content.ReadAsStringAsync(ct)));
            var key = $"{req.Method} {req.RequestUri!.PathAndQuery}";
            return Routes.TryGetValue(key, out var f) ? f() : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") };
        }

        public void Json(string route, string json, HttpStatusCode status = HttpStatusCode.OK, (string, string)? header = null) =>
            Routes[route] = () =>
            {
                var r = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                if (header is { } h) r.Headers.TryAddWithoutValidation(h.Item1, h.Item2);
                return r;
            };
    }

    static (GitHubBackup, FakeGitHub) Client()
    {
        var fake = new FakeGitHub();
        return (new GitHubBackup(new HttpClient(fake), "your-name/hub-settings", Token), fake);
    }

    const string PrivateRepo = """{"private":true,"visibility":"private","default_branch":"main"}""";

    static FakeGitHub LinkableRepo(FakeGitHub fake)
    {
        fake.Json("GET /repos/your-name/hub-settings", PrivateRepo, header: ("github-authentication-token-expiration", "2027-01-30 10:00:00 UTC"));
        fake.Json("POST /repos/your-name/hub-settings/git/blobs", """{"sha":"e69de29"}""", HttpStatusCode.Created);
        fake.Json("GET /user/repos?per_page=100&page=1",
            """[{"full_name":"your-name/hub-settings","private":true},{"full_name":"your-name/hub","private":false}]""");
        fake.Json("GET /repos/your-name/hub-settings/git/trees/main?recursive=1", """{"tree":[{"path":"README.md","type":"blob"}]}""");
        return fake;
    }

    [Theory]
    [InlineData("ghp_abcdefabcdefabcdefabcdefabcdefabcdef", "classic token")]
    [InlineData("hello", "fine-grained")]
    [InlineData("github_pat_abc\r\nX-Evil: 1", "fine-grained")]
    public void Only_takes_fine_grained_tokens(string token, string problem) =>
        Assert.Contains(problem, Assert.Throws<ArgumentException>(() => GitHubBackup.CheckToken(token)).Message);

    [Theory]
    [InlineData("2027-01-30 10:00:00 UTC", "2027-01-30T10:00:00+00:00")]
    [InlineData("2027-01-30 10:00:00 +0100", "2027-01-30T10:00:00+01:00")]
    [InlineData("2027-01-30 10:00:00 -0700", "2027-01-30T10:00:00-07:00")]
    public void Reads_when_the_token_expires(string header, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), GitHubBackup.ParseExpiry(header));

    [Theory]
    [InlineData("https://github.com/your-name/hub-settings.git", "your-name/hub-settings")]
    [InlineData(" your-name/hub-settings ", "your-name/hub-settings")]
    public void Takes_the_repo_as_a_name_or_its_link(string input, string repo) => Assert.Equal(repo, GitHubBackup.CheckRepo(input));

    [Theory]
    [InlineData("your-name/../other")]
    [InlineData("your-name/hub?x=1")]
    [InlineData("your-name")]
    [InlineData("a/b/c")]
    public void Refuses_repo_names_that_could_change_the_request(string repo) =>
        Assert.Throws<ArgumentException>(() => GitHubBackup.CheckRepo(repo));

    [Fact]
    public async Task Links_a_private_repo_with_a_token_for_just_that_repo()
    {
        var (gh, fake) = Client();
        LinkableRepo(fake);

        var link = await gh.CheckAsync();

        Assert.Equal("main", link.Branch);
        Assert.Equal(new DateTimeOffset(2027, 1, 30, 10, 0, 0, TimeSpan.Zero), link.TokenExpires);
        Assert.All(fake.Requests, r =>
        {
            Assert.Equal("https", r.Uri.Scheme);
            Assert.Equal("api.github.com", r.Uri.Host);
            Assert.Equal($"Bearer {Token}", r.Auth);
        });
    }

    [Fact]
    public async Task Refuses_a_public_repo()
    {
        var (gh, fake) = Client();
        fake.Json("GET /repos/your-name/hub-settings", """{"private":false,"visibility":"public"}""");
        Assert.Contains("isn't private", (await Assert.ThrowsAsync<InvalidOperationException>(() => gh.CheckAsync())).Message);
        fake.Json("GET /repos/your-name/hub-settings", """{"private":true,"visibility":"internal"}"""); // the whole enterprise can read it
        Assert.Contains("isn't private", (await Assert.ThrowsAsync<InvalidOperationException>(() => gh.CheckAsync())).Message);
    }

    [Fact]
    public async Task Refuses_a_token_that_cannot_write()
    {
        var (gh, fake) = Client();
        LinkableRepo(fake);
        fake.Json("POST /repos/your-name/hub-settings/git/blobs", """{"message":"Resource not accessible by personal access token"}""", HttpStatusCode.Forbidden);
        Assert.Contains("not write", (await Assert.ThrowsAsync<InvalidOperationException>(() => gh.CheckAsync())).Message);
    }

    [Theory]
    [InlineData("GET /repos/your-name/hub-settings/hooks", "Webhooks")]
    [InlineData("GET /repos/your-name/hub-settings/keys", "Administration")]
    [InlineData("GET /user/keys", "Git SSH keys")]
    public async Task Refuses_a_token_with_more_permissions_than_contents(string route, string permission)
    {
        var (gh, fake) = Client();
        LinkableRepo(fake).Json(route, "[]");
        Assert.Contains(permission, (await Assert.ThrowsAsync<InvalidOperationException>(() => gh.CheckAsync())).Message);
    }

    [Fact]
    public async Task Refuses_a_repo_that_already_holds_other_files()
    {
        var (gh, fake) = Client();
        LinkableRepo(fake).Json("GET /repos/your-name/hub-settings/git/trees/main?recursive=1",
            """{"tree":[{"path":"README.md","type":"blob"},{"path":".bashrc","type":"blob"},{"path":"profiles/game/x.txt","type":"blob"}]}""");
        Assert.Contains(".bashrc", (await Assert.ThrowsAsync<InvalidOperationException>(() => gh.CheckAsync())).Message);
    }

    [Fact]
    public async Task Refuses_a_token_that_can_change_other_repos_too()
    {
        var (gh, fake) = Client();
        LinkableRepo(fake).Json("GET /user/repos?per_page=100&page=1",
            """[{"full_name":"your-name/hub-settings","private":true},{"full_name":"your-name/other-private","private":true}]""");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => gh.CheckAsync());
        Assert.Contains("your-name/other-private", e.Message);
    }

    [Fact]
    public async Task Errors_never_contain_the_token()
    {
        var (gh, fake) = Client();
        fake.Json("GET /repos/your-name/hub-settings", $$"""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => gh.CheckAsync());
        Assert.Contains("didn't accept the token", e.Message);
        Assert.DoesNotContain(Token, e.ToString());
    }

    [Fact]
    public async Task Does_not_follow_redirects()
    {
        var (gh, fake) = Client();
        fake.Routes["GET /repos/your-name/hub-settings"] = () =>
            new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/") }, Content = new StringContent("") };
        Assert.Contains("redirect", (await Assert.ThrowsAsync<InvalidOperationException>(() => gh.CheckAsync())).Message);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Backs_up_as_one_commit_that_replaces_every_file()
    {
        var (gh, fake) = Client();
        fake.Json("GET /repos/your-name/hub-settings/git/ref/heads/main", """{"object":{"sha":"p1"}}""");
        fake.Json("GET /repos/your-name/hub-settings/git/commits/p1", """{"tree":{"sha":"oldtree"}}""");
        fake.Json("POST /repos/your-name/hub-settings/git/trees", """{"sha":"newtree"}""", HttpStatusCode.Created);
        fake.Json("POST /repos/your-name/hub-settings/git/commits", """{"sha":"c2"}""", HttpStatusCode.Created);
        fake.Json("PATCH /repos/your-name/hub-settings/git/refs/heads/main", """{"object":{"sha":"c2"}}""");

        var sha = await gh.PushAsync("main", new Dictionary<string, string> { ["audio.json"] = "{}\n", ["profiles/game/a b.txt"] = "Preamp: 0 dB\n" }, "Hub backup from PC");

        Assert.Equal("c2", sha);
        var tree = JsonNode.Parse(fake.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/git/trees")).Body!)!;
        Assert.Null(tree["base_tree"]); // a full tree: files no longer in the backup are removed
        Assert.Equal(["audio.json", "profiles/game/a b.txt"], tree["tree"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
        var commit = JsonNode.Parse(fake.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/git/commits")).Body!)!;
        Assert.Equal("p1", commit["parents"]![0]!.GetValue<string>());
        var update = JsonNode.Parse(fake.Requests.Last().Body!)!;
        Assert.False(update["force"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Makes_no_commit_when_nothing_changed()
    {
        var (gh, fake) = Client();
        fake.Json("GET /repos/your-name/hub-settings/git/ref/heads/main", """{"object":{"sha":"p1"}}""");
        fake.Json("GET /repos/your-name/hub-settings/git/commits/p1", """{"tree":{"sha":"same"}}""");
        fake.Json("POST /repos/your-name/hub-settings/git/trees", """{"sha":"same"}""", HttpStatusCode.Created);

        Assert.Null(await gh.PushAsync("main", new Dictionary<string, string> { ["audio.json"] = "{}\n" }, "Hub backup"));
        Assert.DoesNotContain(fake.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/git/commits"));
    }

    [Fact]
    public async Task Lists_only_hubs_own_backups_and_reads_only_its_two_files()
    {
        var (gh, fake) = Client();
        fake.Json("GET /repos/your-name/hub-settings/commits?sha=main&per_page=50", """
            [{"sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","commit":{"message":"Hub backup from PC","committer":{"date":"2026-11-01T20:30:00Z"}}},
             {"sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","commit":{"message":"Initial commit","committer":{"date":"2026-11-01T20:00:00Z"}}}]
            """);
        fake.Routes["GET /repos/your-name/hub-settings/contents/audio.json?ref=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"] =
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":1}") };

        var list = await gh.ListAsync("main");
        Assert.Equal(["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"], list.Select(b => b.Sha));
        Assert.Equal("{\"version\":1}", await gh.ReadAsync(list[0].Sha, "audio.json"));
        Assert.Null(await gh.ReadAsync(list[0].Sha, "mouse.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => gh.ReadAsync(list[0].Sha, "../../user"));
        await Assert.ThrowsAsync<ArgumentException>(() => gh.ReadAsync("main", "audio.json"));
    }
}
