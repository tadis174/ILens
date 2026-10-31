using System.ComponentModel;
using System.Reflection.Metadata;
using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class SearchCodeTool
{
    private static readonly Noun LineNoun = Noun.Regular("line");
    private static readonly Noun TypeNoun = Noun.Regular("type");

    // The longest the backtracking engine may work on one line before the search gives up.
    private static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(1);

    [McpServerTool(Name = "search_code", ReadOnly = true),
     Description("Search the decompiled C# for lines that match a regular expression: in one type (type_name), one " +
        "namespace, or the whole assembly when neither is given. Lists each member holding a match, sorted, and " +
        "beneath it the matching lines as decompile prints them. Signatures, attributes and field initializers are " +
        "searched as well as bodies, and a lambda's lines belong to the member it is written in. The first search " +
        "decompiles what it covers and keeps it, about a minute for a large assembly: a call that runs out of time " +
        "says how far it came, and repeating it continues from there.")]
    public static string SearchCode(
        AssemblyHostRegistry registry,
        [Description(ParameterDescriptions.Assembly)] string assembly,
        [Description(".NET regular expression, applied to one line at a time and case-sensitive; start it with (?i) " +
            @"to ignore case. E.g. 'File\.Delete\('.")] string regex,
        [Description(ParameterDescriptions.TypeName)] string type_name = null,
        [Description(ParameterDescriptions.Namespace)] string @namespace = null,
        [Description(ParameterDescriptions.IncludeCompilerGenerated)] bool include_compiler_generated = false,
        [Description(ParameterDescriptions.Limit)] int limit = ListOutput.DefaultLimit)
    {
        // IsNullOrEmpty rather than null, so an LLM passing "" for "omit" names no scope.
        bool byType = !string.IsNullOrEmpty(type_name), byNamespace = !string.IsNullOrEmpty(@namespace);
        if (byType && byNamespace)
            throw new ArgumentException(
                $"{nameof(type_name)} and {nameof(@namespace)} are two scopes; give one of them.");
        if (string.IsNullOrEmpty(regex))
            throw new ArgumentException($"{nameof(regex)} is empty.");
        var matches = Matcher(regex);

        // The call's time runs from here, so that loading the assembly counts toward it.
        using var time = new CodeTextPass.Limit(registry.SearchTimeLimit);
        using var lease = registry.GetOrLoad(assembly);
        var host = lease.Host;
        // A reference assembly's bodies are placeholders, which would read as its code.
        if (host.IsReferenceAssembly)
            throw AssemblyHost.ReferenceAssemblyError("there is no code to search",
                "ask the build the library runs as. find_methods and list_members read declarations alone and answer here.");
        var scope = byType ? Scope.OfType(host.Resolver.ResolveType(type_name), include_compiler_generated)
            : Scope.OfTypes(host, @namespace, include_compiler_generated);

        // One call at a time decompiles for an assembly. A second one waits for it as long as
        // its own time allows, and then finds the types the first has done.
        using var search = registry.CodeTexts.Open(host.AssemblyPath, scope.TopLevelTypes);
        if (Monitor.TryEnter(search.PassLock, time.Left))
        {
            try
            {
                var missing = search.Missing();
                if (missing.Count > 0)
                {
                    try
                    {
                        host.DecompileForSearch(missing, search, time);
                    }
                    finally
                    {
                        registry.HandBackTransientMemory();
                    }
                }
            }
            finally
            {
                Monitor.Exit(search.PassLock);
            }
        }
        if (search.OverShare)
            throw new ArgumentException(
                $"The decompiled text of {Path.GetFileName(host.AssemblyPath)} needs more than the " +
                $"{SizeText(registry.CodeTexts.MaxBytes)} kept for searches. Narrow the search with " +
                $"{nameof(type_name)} or {nameof(@namespace)}, or raise --max-total-size.");

        // Matches from a part of the scope would be printed again by the call that finishes,
        // and are no beginning of its sorted list, so an unfinished call says only how far it is.
        var (texts, failed, done) = search.Read();
        if (done < scope.TopLevelTypes.Count)
            return $"Not finished: {done} of {TypeNoun.Count(scope.TopLevelTypes.Count)} decompiled. " +
                "Repeat the call to continue; what is done is kept.";

        var hits = new List<(string Owner, string Line)>();
        foreach (var packed in texts)
        {
            var text = CodeTextPass.Read(packed);
            foreach (var range in text.Ranges.Where(scope.Covers))
            {
                for (int number = range.First; number <= range.Last; number++)
                {
                    var line = text.Lines[number - 1].Trim();
                    if (line.Length > 0 && Matches(matches, line, range.Owner))
                        hits.Add((range.Owner, line));
                }
            }
        }
        return Format(hits, failed, limit, $"matching {regex}{scope.Label}");
    }

    /// <summary>
    /// The test a line has to pass. The pattern runs on .NET's non-backtracking engine, which
    /// answers in time linear in the line whatever the pattern, so no pattern can run away on
    /// it. That engine refuses lookarounds and backreferences; a pattern with one runs on the
    /// backtracking engine under <see cref="LineTimeout"/>. A pattern .NET cannot parse throws
    /// here, with .NET's message, which names the position.
    /// </summary>
    private static Func<string, bool> Matcher(string regex)
    {
        try
        {
            return new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking).IsMatch;
        }
        catch (NotSupportedException)
        {
            return new Regex(regex, RegexOptions.CultureInvariant, LineTimeout).IsMatch;
        }
    }

    // A size as the error names it, in whole MB, or in KB below one.
    private static string SizeText(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024 / 1024} MB" : $"{bytes / 1024} KB";

    private static bool Matches(Func<string, bool> matches, string line, string owner)
    {
        try
        {
            return matches(line);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ArgumentException(
                $"The pattern took longer than {LineTimeout.TotalSeconds:0} s on one line of {owner}. A pattern " +
                "without a lookaround or a backreference cannot run away.");
        }
    }

    /// <summary>
    /// The answer: the header with the number of matching lines, then each member that holds
    /// one, sorted by its printed name, with its lines beneath it in source order, and at the
    /// end the types that could not be searched. The limit counts matching lines.
    /// </summary>
    private static string Format(
        List<(string Owner, string Line)> hits, IReadOnlyList<string> failed, int limit, string subject)
    {
        // The sort is stable, so a member's lines keep the order they were found in.
        var answer = new StringBuilder(ListOutput.Header(hits.Count, LineNoun, subject))
            .Append(ListOutput.Grouped(hits.OrderBy(hit => hit.Owner, StringComparer.Ordinal).ToList(), limit));

        // Without this line an answer is a proof of absence, so it also follows an empty one.
        if (failed.Count > 0)
            answer.Append($"\nNot searched: {TypeNoun.Count(failed.Count)} that failed to decompile: ")
                .AppendJoin(", ", failed.Order(StringComparer.Ordinal));
        return answer.ToString();
    }

    /// <summary>
    /// What a search covers: the top-level types whose text it needs, and of their lines those
    /// <see cref="Covers"/> admits. <see cref="Label"/> is the scope as the header names it.
    /// </summary>
    private sealed record Scope(
        IReadOnlyList<TypeDefinitionHandle> TopLevelTypes, HashSet<TypeDefinitionHandle> Within, bool IncludeGenerated,
        string Label)
    {
        /// <summary>
        /// One type with the types nested in it. A type is decompiled with its outermost type,
        /// so that is the text needed. A generated type named here is searched whatever
        /// <paramref name="includeGenerated"/> says: it was asked for by name, and an empty
        /// answer would read as "no match".
        /// </summary>
        public static Scope OfType(ITypeDefinition type, bool includeGenerated)
        {
            var outermost = type;
            while (outermost.DeclaringTypeDefinition is { } declaring)
                outermost = declaring;
            var within = new HashSet<TypeDefinitionHandle>();
            void Add(ITypeDefinition nested)
            {
                within.Add((TypeDefinitionHandle)nested.MetadataToken);
                foreach (var inner in nested.NestedTypes)
                    Add(inner);
            }
            Add(type);
            return new Scope([(TypeDefinitionHandle)outermost.MetadataToken], within,
                includeGenerated || CompilerGeneratedFilter.IsCompilerGenerated(type),
                $" in {ReferenceFormatter.FormatTypeName(type)}");
        }

        /// <summary>
        /// The top-level types of exactly <paramref name="namespace"/>, or of the whole assembly
        /// when it is empty, each with its nested types.
        /// </summary>
        public static Scope OfTypes(AssemblyHost host, string @namespace, bool includeGenerated)
        {
            bool byNamespace = !string.IsNullOrEmpty(@namespace);
            if (byNamespace)
                host.Resolver.RequireNamespace(@namespace);
            var types = host.TypeSystem.MainModule.TopLevelTypeDefinitions
                .Where(type => !byNamespace || type.Namespace == @namespace)
                .Where(type => includeGenerated || !CompilerGeneratedFilter.IsCompilerGenerated(type))
                .Select(type => (TypeDefinitionHandle)type.MetadataToken)
                .ToList();
            return new Scope(types, null, includeGenerated, byNamespace ? $" in namespace {@namespace}" : "");
        }

        public bool Covers(CodeTextPass.LineRange range) =>
            (IncludeGenerated || !range.Generated) && (Within is null || Within.Contains(range.Type));
    }
}
