using System.Text;

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
/// What the two default filters of a list left out of it: the matches the accessibility asked
/// for does not permit, and the compiler-generated ones, which count apart whatever their
/// accessibility.
/// </summary>
public sealed class LeftOut
{
    /// <summary>The matches that asking for every accessibility would add.</summary>
    public int Inaccessible { get; private set; }

    /// <summary>The compiler-generated matches.</summary>
    public int Generated { get; private set; }

    /// <summary>Whether a generated match is also one the accessibility asked for does not permit.</summary>
    public bool GeneratedNeedAll { get; private set; }

    public LeftOut()
    {
    }

    public LeftOut(int inaccessible, int generated, bool generatedNeedAll)
    {
        Inaccessible = inaccessible;
        Generated = generated;
        GeneratedNeedAll = generatedNeedAll;
    }

    /// <summary>
    /// Counts one match of the question and answers whether both filters let it through.
    /// <paramref name="hiddenAsGenerated"/> is false for a generated match the call includes.
    /// </summary>
    public bool Admits(bool permitted, bool hiddenAsGenerated)
    {
        if (hiddenAsGenerated)
        {
            Generated++;
            GeneratedNeedAll |= !permitted;
            return false;
        }
        if (!permitted)
        {
            Inaccessible++;
            return false;
        }
        return true;
    }
}

/// <summary>
/// The lines every tool that answers with a list writes around it: a header with the total
/// first ("73 calls to AstNode.AddChild(T, Role&lt;T&gt;):"), the empty result in the header's
/// form ("No calls to ..."), one truncation line naming how many entries the limit cut, and a
/// line naming what the default filters left out, for the tools that filter by accessibility
/// and under an empty answer of analyze. A list whose entries belong to members is written
/// grouped, each member once with its entries beneath it.
/// The total counts every entry, not the entries shown, so it agrees with the truncation line;
/// a member's line in a grouped list is no entry.
/// </summary>
public static class ListOutput
{
    /// <summary>The default of every tool's limit parameter.</summary>
    public const int DefaultLimit = 100;

    /// <summary>
    /// The line that says what the two default filters left out, or null when there is nothing
    /// to say. Without it an answer reads as complete: "No methods" as "there is no such
    /// method", and a list without a private method as "the type has none".
    /// Under a list the line counts only what the accessibility filter left out. What the
    /// compiler generated has no declaration anyone wrote; a caller who wants it searches by
    /// its generated name, which ends in an empty answer. Under an empty answer the line counts
    /// both and names the settings that list everything it counts, so that one more call is
    /// enough: most generated matches are private as well, and one setting alone would answer
    /// empty again.
    /// </summary>
    public static string NotShown(LeftOut leftOut, AccessibilityFilter accessibility, bool empty)
    {
        var parts = new List<string>();
        if (leftOut.Inaccessible > 0)
            parts.Add($"{leftOut.Inaccessible} " +
                (accessibility == AccessibilityFilter.Public ? "non-public" : "internal or private"));
        if (!empty)
            return parts.Count == 0 ? null : $"Not shown: {parts[0]}";

        if (leftOut.Generated > 0)
            parts.Add($"{leftOut.Generated} compiler-generated");
        if (parts.Count == 0)
            return null;

        var accessibilityAll = $"{ParameterNames.Accessibility} {AccessibilityFilter.All.ToSchemaName()}";
        var settings = leftOut.Generated == 0 ? accessibilityAll
            : leftOut.Inaccessible > 0 || leftOut.GeneratedNeedAll
                ? $"{ParameterNames.IncludeCompilerGenerated} with {accessibilityAll}"
                : ParameterNames.IncludeCompilerGenerated;
        var pronoun = leftOut.Inaccessible + leftOut.Generated == 1 ? "it" : "them";
        return $"Not shown: {string.Join(", ", parts)}; {settings} lists {pronoun}";
    }

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
    /// How many of <paramref name="total"/> lines <paramref name="limit"/> shows. A negative
    /// limit shows none, like 0, so the truncation line never counts more lines than there are.
    /// </summary>
    public static int Shown(int total, int limit) => Math.Clamp(limit, 0, total);

    /// <summary>
    /// The first <paramref name="limit"/> of <paramref name="lines"/>, one per line, followed by
    /// the truncation line when the limit cut any. Empty for no lines.
    /// </summary>
    private static string Lines(IReadOnlyList<string> lines, int limit)
    {
        var shown = Shown(lines.Count, limit);
        var listed = lines.Take(shown);
        return string.Join("\n", shown < lines.Count ? listed.Append(Truncation(lines.Count - shown)) : listed);
    }

    /// <summary>
    /// The lines of a list whose entries belong to members, such as the calls a member makes or
    /// its lines that a search matches: each owner once, its entries indented beneath it, then
    /// the truncation line when the limit cut any. Every line starts with a line break, so the
    /// text follows a header as it is, and it is empty for no entries.
    /// <paramref name="entries"/> come in the order to print, those of one owner together. The
    /// limit counts entries, so it can cut inside an owner's.
    /// Grouped, since an owner's name is often longer than the entry that follows it: against
    /// one line per entry, each with its owner, the grouped form measured 4 to 9 % smaller for
    /// most search patterns and 5 to 14 % smaller summed over the lists of calls to the methods
    /// of two assemblies. An owner with one entry costs one character more.
    /// </summary>
    public static string Grouped(IReadOnlyList<(string Owner, string Entry)> entries, int limit)
    {
        var shown = Shown(entries.Count, limit);
        var lines = new StringBuilder();
        for (var i = 0; i < shown; i++)
        {
            if (i == 0 || entries[i].Owner != entries[i - 1].Owner)
                lines.Append('\n').Append(entries[i].Owner);
            lines.Append("\n  ").Append(entries[i].Entry);
        }
        if (shown < entries.Count)
            lines.Append('\n').Append(Truncation(entries.Count - shown));
        return lines.ToString();
    }

    /// <summary>
    /// The lines of a list that stands beneath a sentence of its own, such as the members a
    /// note under an answer names: the first <paramref name="limit"/> of
    /// <paramref name="lines"/>, each indented by two spaces, then the truncation line when the
    /// limit cut any. Every line starts with a line break, so the text follows its sentence as
    /// it is. The sentence is the caller's, since it stays when the limit shows no line.
    /// </summary>
    public static string Beneath(IReadOnlyList<string> lines, int limit)
    {
        var shown = Shown(lines.Count, limit);
        var text = new StringBuilder();
        for (var i = 0; i < shown; i++)
            text.Append("\n  ").Append(lines[i]);
        if (shown < lines.Count)
            text.Append('\n').Append(Truncation(lines.Count - shown));
        return text.ToString();
    }

    /// <summary>
    /// A whole list answer: the <see cref="Header"/>, then <see cref="Lines"/>, then
    /// <paramref name="notShown"/>, the line from <see cref="NotShown"/> or another line that says
    /// what the answer leaves out, when there is one.
    /// </summary>
    public static string Format(IReadOnlyList<string> lines, int limit, Noun noun, string subject = null,
        string notShown = null)
    {
        var header = Header(lines.Count, noun, subject);
        var answer = lines.Count == 0 ? header : header + "\n" + Lines(lines, limit);
        return notShown == null ? answer : answer + "\n" + notShown;
    }
}
