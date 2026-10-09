namespace ILens;

/// <summary>
/// What a list counts, in the singular and the plural, so a header never writes "(s)".
/// </summary>
public readonly record struct Noun(string Singular, string Plural)
{
    /// <summary>A noun whose plural adds "s".</summary>
    public static Noun Regular(string singular) => new(singular, singular + "s");

    /// <summary>"1 call", "73 calls".</summary>
    public string Count(int count) => $"{count} {(count == 1 ? Singular : Plural)}";
}

/// <summary>
/// The lines every tool that answers with a list writes around it: a header with the total
/// first ("73 calls to AstNode.AddChild(T, Role&lt;T&gt;):"), the empty result in the header's
/// form ("No calls to ..."), and one truncation line naming how many lines the limit cut.
/// The total counts every line, not the lines shown, so it agrees with the truncation line.
/// </summary>
public static class ListOutput
{
    /// <summary>The default of every tool's limit parameter.</summary>
    public const int DefaultLimit = 100;

    /// <summary>
    /// The header for <paramref name="total"/> entries. <paramref name="subject"/> is what the
    /// list is about, with its relation ("to AstNode.AddChild(T, Role&lt;T&gt;)"), or null.
    /// With no entries it is the whole answer, so it takes no colon.
    /// </summary>
    public static string Header(int total, Noun noun, string subject = null)
    {
        var tail = string.IsNullOrEmpty(subject) ? "" : " " + subject;
        return total == 0 ? $"No {noun.Plural}{tail}" : $"{noun.Count(total)}{tail}:";
    }

    /// <summary>The line that ends a list the limit cut, <paramref name="more"/> lines short.</summary>
    public static string Truncation(int more) => $"... ({more} more; raise limit to see all)";

    /// <summary>
    /// The first <paramref name="limit"/> of <paramref name="lines"/>, one per line, followed by
    /// the truncation line when the limit cut any. Empty for no lines.
    /// </summary>
    private static string Lines(IReadOnlyList<string> lines, int limit)
    {
        var shown = string.Join("\n", lines.Take(limit));
        return lines.Count > limit ? shown + "\n" + Truncation(lines.Count - limit) : shown;
    }

    /// <summary>
    /// A whole list answer: the <see cref="Header"/>, then <see cref="Lines"/>.
    /// </summary>
    public static string Format(IReadOnlyList<string> lines, int limit, Noun noun, string subject = null)
    {
        var header = Header(lines.Count, noun, subject);
        return lines.Count == 0 ? header : header + "\n" + Lines(lines, limit);
    }
}
