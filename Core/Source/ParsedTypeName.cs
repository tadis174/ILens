using System.Globalization;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// A type name as a caller writes it, split into its dot-separated segments so that each
/// can carry its own generic arity. <c>ITypeDefinition.FullName</c> drops the arity —
/// <c>Task</c> and <c>Task&lt;TResult&gt;</c> are both <c>System.Threading.Tasks.Task</c> —
/// so a lookup by <c>FullName</c> alone cannot tell such types apart.
/// </summary>
/// <remarks>
/// A segment states its arity in any of three forms, read alike: C#'s unbound form
/// <c>Func&lt;,&gt;</c>, which is how ILens prints a generic type; an argument list
/// <c>Func&lt;int, bool&gt;</c>, whose argument count is the arity; and the reflection form
/// <c>Func`2</c>. Segments are separated by '.' or by '+', which .NET reflection uses for a
/// nested type. A '&lt;' opens an argument list only right after a name character, so a
/// compiler-generated name such as <c>&lt;&gt;c__DisplayClass5_0</c> stays one plain name.
/// </remarks>
public sealed class ParsedTypeName
{
    /// <summary>
    /// One segment of a name. <see cref="Arity"/> is null when the caller wrote none;
    /// <see cref="Arguments"/> is set only for an argument list, not for the unbound or
    /// reflection forms.
    /// </summary>
    public sealed record Segment(string Name, int? Arity, IReadOnlyList<string> Arguments);

    public IReadOnlyList<Segment> Segments { get; }

    private ParsedTypeName(IReadOnlyList<Segment> segments) => Segments = segments;

    /// <summary>The name without any arity, the form <c>ITypeDefinition.FullName</c> takes.</summary>
    public string BareName => string.Join(".", Segments.Select(s => s.Name));

    public bool HasArity => Segments.Any(s => s.Arity.HasValue);

    public bool HasArguments => Segments.Any(s => s.Arguments != null);

    /// <summary>Parse a caller's type name, or return <c>null</c> when it is malformed.</summary>
    public static ParsedTypeName Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var segments = new List<Segment>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : '\0';
            if (c == '<' && OpensArguments(text, i))
                depth++;
            else if (c == '>' && depth > 0)
                depth--;
            else if (i == text.Length || (depth == 0 && c is '.' or '+'))
            {
                if (depth != 0)
                    return null;
                var segment = ParseSegment(text[start..i].Trim());
                if (segment == null)
                    return null;
                segments.Add(segment);
                start = i + 1;
            }
        }
        return new ParsedTypeName(segments);
    }

    private static bool OpensArguments(string text, int index) =>
        index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_');

    private static Segment ParseSegment(string text)
    {
        var open = Enumerable.Range(0, text.Length)
            .FirstOrDefault(i => text[i] == '<' && OpensArguments(text, i), -1);
        if (open >= 0)
        {
            var name = text[..open].Trim();
            if (name.Length == 0 || text[^1] != '>')
                return null;
            var inner = text[(open + 1)..^1];
            if (inner.All(c => c == ',' || char.IsWhiteSpace(c)))
                return new Segment(name, inner.Count(c => c == ',') + 1, null);
            var arguments = SplitArguments(inner);
            return arguments.Any(a => a.Length == 0) ? null : new Segment(name, arguments.Count, arguments);
        }

        var tick = text.IndexOf('`');
        if (tick >= 0)
        {
            var name = text[..tick];
            return name.Length > 0 && int.TryParse(text[(tick + 1)..], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var arity)
                ? new Segment(name, arity, null)
                : null;
        }

        return text.Length == 0 ? null : new Segment(text, null, null);
    }

    /// <summary>Split an argument list on the commas that are not nested in brackets.</summary>
    private static List<string> SplitArguments(string inner)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            switch (inner[i])
            {
                case '<' or '[': depth++; break;
                case '>' or ']': depth--; break;
                case ',' when depth == 0:
                    arguments.Add(inner[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        arguments.Add(inner[start..].Trim());
        return arguments;
    }

    /// <summary>
    /// How many type parameters each segment of <paramref name="name"/> declares: none for
    /// each namespace part, then one entry per type from the outermost to the type itself.
    /// A nested type counts only its own, not those it shares with the type enclosing it.
    /// </summary>
    public static int[] SegmentArities(FullTypeName name)
    {
        var top = name.TopLevelTypeName;
        var arities = new List<int>();
        if (!string.IsNullOrEmpty(top.Namespace))
            arities.AddRange(top.Namespace.Split('.').Select(_ => 0));
        arities.Add(top.TypeParameterCount);
        for (var level = 0; level < name.NestingLevel; level++)
            arities.Add(name.GetNestedTypeAdditionalTypeParameterCount(level));
        return arities.ToArray();
    }

    /// <summary>
    /// True if every segment that states an arity agrees with <paramref name="arities"/>.
    /// Segments align from the end, so a short name compares against the type itself.
    /// </summary>
    public bool ArityAgrees(IReadOnlyList<int> arities) =>
        Aligned(arities, (segment, arity) => segment.Arity is not { } stated || stated == arity);

    /// <summary>
    /// True if every segment written without an arity names a non-generic type in
    /// <paramref name="arities"/>, the way C# and reflection read a name without type
    /// parameters.
    /// </summary>
    public bool NonGenericWhereUnstated(IReadOnlyList<int> arities) =>
        Aligned(arities, (segment, arity) => segment.Arity.HasValue || arity == 0);

    private bool Aligned(IReadOnlyList<int> arities, Func<Segment, int, bool> agrees)
    {
        var offset = arities.Count - Segments.Count;
        if (offset < 0)
            return false;
        for (var i = 0; i < Segments.Count; i++)
            if (!agrees(Segments[i], arities[offset + i]))
                return false;
        return true;
    }
}
