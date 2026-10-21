namespace Hub.Core.Diagnostics;

public sealed record SpecRow(string Label, string Value);

/// <summary>One block of the System page's specs ("cpu", "displays", …).</summary>
public sealed record SpecSection(string Title, IReadOnlyList<SpecRow> Rows);

public static class SpecsText
{
    /// <summary>Plain text for "copy specs": the title, then indented "label: value" lines; empty sections are left out.</summary>
    public static string Format(IEnumerable<SpecSection> sections)
    {
        var blocks = sections.Where(s => s.Rows.Count > 0)
            .Select(s => s.Title + "\n" + string.Concat(s.Rows.Select(r => $"  {r.Label}: {r.Value}\n")));
        return string.Join("\n", blocks);
    }
}
