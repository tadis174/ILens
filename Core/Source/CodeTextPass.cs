using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Decompiles whole types for a search over their text, and packs each type's text together with
/// the member every line belongs to.
/// </summary>
/// <remarks>
/// A type is decompiled as <c>decompile</c> answers for it, so a line found here is a line of
/// that answer. The tree is written through ILSpy's writer that records where each node starts
/// and ends, which gives every line an owner: the member declared directly in a type whose
/// declaration spans the line, so an accessor's lines belong to its property and a lambda's to
/// the method it is written in; otherwise the innermost type, for its header, its attributes and
/// its braces; and none for the using and namespace lines, which belong to no type. A declaration
/// spans the comments the decompiler writes above it, its documentation and notes of its own, so
/// that a member's documentation belongs to the member and a type's to the type. A member of a
/// C# 14 extension block owns its lines through the method the compiler made of it, which is
/// what every other answer names: an accessor's lines belong to its accessor's method, a
/// property's other lines to its getter's, or its setter's where it has no getter, and the
/// block's own lines, its header and its braces, to the class.
///
/// Several decompilers run side by side over one type system, as ILSpy's own project export
/// does. On a game assembly of 16 MB, four threads took 44 s against 90 s for one, eight gained
/// nothing over four, and the text was the same byte for byte.
///
/// The owners are written into the packed text as names. The type system they come from does not
/// outlive the pass, and the names held as strings beside the text would take more memory than
/// the text itself: packed together, text and owners of that assembly are 10.5 MB.
/// </remarks>
public static class CodeTextPass
{
    private const int MaxThreads = 4;

    /// <summary>
    /// The lines <see cref="First"/> to <see cref="Last"/> of a type's text, counted from 1, which
    /// one owner holds: <see cref="Owner"/> is the member or type as ILens prints it,
    /// <see cref="Type"/> the type the owner is, or is declared in, and <see cref="Generated"/>
    /// whether <see cref="CompilerGeneratedFilter"/> counts the owner as generated.
    /// </summary>
    public sealed record LineRange(int First, int Last, TypeDefinitionHandle Type, bool Generated, string Owner);

    /// <summary>A kept type read back: its text line by line, and the ranges that have an owner, in line order.</summary>
    public sealed record TypeText(IReadOnlyList<LineRange> Ranges, string[] Lines);

    /// <summary>
    /// The time one call may spend decompiling, counted from when this is created. When it is
    /// up, <see cref="Token"/> stops the decompilers, but not before the call has finished one
    /// type: every call then gets further than the last, also with a limit of nothing, and a
    /// type that alone needs longer than the limit is finished in a call that runs over instead
    /// of being cut in every call.
    /// </summary>
    public sealed class Limit : IDisposable
    {
        private readonly TimeSpan _time;
        private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
        private readonly CancellationTokenSource _stop = new();
        private readonly Timer _timer;
        private readonly object _gate = new();
        private bool _timeUp;      // guarded by _gate
        private int _finished;     // guarded by _gate
        private bool _disposed;    // guarded by _gate

        public Limit(TimeSpan time)
        {
            _time = time;
            // A timer takes no more than uint.MaxValue - 1 milliseconds. A limit beyond that,
            // about 49 days, never ends a call.
            var due = time.TotalMilliseconds < uint.MaxValue ? time : Timeout.InfiniteTimeSpan;
            _timer = new Timer(_ => TimeUp(), null, due, Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token => _stop.Token;

        /// <summary>What is left of the time, as a wait on a lock takes it.</summary>
        public TimeSpan Left
        {
            get
            {
                var left = _time - System.Diagnostics.Stopwatch.GetElapsedTime(_started);
                return left < TimeSpan.Zero ? TimeSpan.Zero
                    : left.TotalMilliseconds > int.MaxValue ? TimeSpan.FromMilliseconds(int.MaxValue)
                    : left;
            }
        }

        /// <summary>Stop the decompilers now, whatever the time: there is no room for what they would add.</summary>
        public void Stop()
        {
            lock (_gate)
                _stop.Cancel();
        }

        /// <summary>The call has decompiled a type, or found that it cannot be decompiled.</summary>
        public void TypeFinished()
        {
            lock (_gate)
            {
                _finished++;
                if (_timeUp)
                    _stop.Cancel();
            }
        }

        private void TimeUp()
        {
            lock (_gate)
            {
                // The timer can fire while the call that owned it is already ending.
                if (_disposed)
                    return;
                _timeUp = true;
                if (_finished > 0)
                    _stop.Cancel();
            }
        }

        public void Dispose()
        {
            lock (_gate)
                _disposed = true;
            _timer.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>
    /// Decompile <paramref name="types"/>, top-level types of <paramref name="typeSystem"/>'s main
    /// module, and hand each to <paramref name="texts"/>: packed, or by its name when it failed
    /// to decompile. A type that <paramref name="limit"/> cuts short is handed over in neither
    /// way, and a later call decompiles it anew. The pass ends early when <paramref name="texts"/>
    /// has no room for a text.
    /// </summary>
    public static void Run(
        DecompilerTypeSystem typeSystem, DecompilerSettings settings, IReadOnlyList<TypeDefinitionHandle> types,
        CodeTextStore.Search texts, Limit limit)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(MaxThreads, Environment.ProcessorCount) };
        Parallel.ForEach(
            Partitioner.Create(types.ToArray(), loadBalance: true),
            options,
            () => new CSharpDecompiler(typeSystem, settings) { CancellationToken = limit.Token },
            (type, loop, decompiler) =>
            {
                if (limit.Token.IsCancellationRequested)
                {
                    loop.Stop();
                    return decompiler;
                }
                var definition = typeSystem.MainModule.GetDefinition(type);
                try
                {
                    if (texts.Keep(type, Pack(AssemblyHost.DecompileTopLevelType(decompiler, definition).Tree, settings)))
                    {
                        limit.TypeFinished();
                    }
                    else
                    {
                        limit.Stop();
                        loop.Stop();
                    }
                }
                catch (OperationCanceledException)
                {
                }
                // Running out of memory says nothing about the type, so it ends the call and
                // a later one tries the type again.
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    texts.Fail(type, ReferenceFormatter.FormatTypeName(definition));
                    limit.TypeFinished();
                }
                return decompiler;
            },
            _ => { });
    }

    /// <summary>
    /// A type's ranges and text as <see cref="Read"/> takes them: one row per range, an empty row,
    /// then the text, compressed. The fastest level packs the 28 million characters of a large
    /// assembly in a third of a second.
    /// </summary>
    private static byte[] Pack(SyntaxTree tree, DecompilerSettings settings)
    {
        var text = AssemblyHost.CodeWriter();
        tree.AcceptVisitor(new CSharpOutputVisitor(
            TokenWriter.WrapInWriterThatSetsLocationsInAST(new TextWriterTokenWriter(text)),
            settings.CSharpFormattingOptions));

        var packed = new MemoryStream();
        using (var writer = new StreamWriter(new BrotliStream(packed, CompressionLevel.Fastest), new UTF8Encoding(false)))
        {
            foreach (var range in RangesOf(tree))
                writer.Write(string.Create(CultureInfo.InvariantCulture,
                    $"{range.First}\t{range.Last}\t{MetadataTokens.GetRowNumber(range.Type)}\t{(range.Generated ? 1 : 0)}\t{range.Owner}\n"));
            writer.Write('\n');
            writer.Write(text.GetStringBuilder());
        }
        return packed.ToArray();
    }

    /// <summary>The ranges of a written tree, whose nodes carry their lines.</summary>
    private static List<LineRange> RangesOf(SyntaxTree tree)
    {
        // The owner of each line, the outermost declaration first, so that a nested type takes
        // its lines from the type around it and a member its lines from its type.
        var owners = new Dictionary<int, LineRange>();
        void Own(EntityDeclaration declaration, ITypeDefinition type, bool generated, string name,
            EntityDeclaration after)
        {
            // A declaration's first line is the least line on which one of its leaves, or one of
            // the comments that lead it, starts, and its last line is that of its last token.
            // Library behavior, read in ICSharpCode.Decompiler 11.2.0.10050: the comments the
            // decompiler writes above a declaration, its documentation and notes of its own, are
            // trivia that lead it, kept apart from its children, so that its descendants do not
            // reach them; the writer writes them once it has opened the declaration, before its
            // first token. A leaf it gives no line is passed over: the target of an attribute on
            // a return value, [return: ...], which it writes as a keyword. The cases, in
            // SearchCodeTests: TheDecompilersNoteAboveADeclaration_StandsUnderIt,
            // DocumentationLines_StandUnderWhatTheyDocument_AndHeaderLinesUnderTheirClass and
            // LinesOutsideBodies_StandUnderTheMemberOrTypeTheyBelongTo.
            int first = declaration.Descendants.Where(node => node.FirstChild is null)
                .Concat(declaration.LeadingTrivia.OfType<Comment>())
                .Select(node => node.StartLocation.Line).Where(line => line > 0)
                .DefaultIfEmpty(0).Min();
            if (first < 1)
                return;
            // An accessor's keyword and braces are no nodes of the tree, so an accessor written
            // after another begins on the line after that one's last.
            if (after is not null && after.EndLocation.Line > 0 && after.EndLocation.Line + 1 < first)
                first = after.EndLocation.Line + 1;
            var range = new LineRange(first, declaration.EndLocation.Line,
                (TypeDefinitionHandle)type.MetadataToken, generated, OneLine(name));
            for (int line = range.First; line <= range.Last; line++)
                owners[line] = range;
        }

        var declarations = tree.Descendants.OfType<EntityDeclaration>().ToList();
        foreach (var declaration in declarations)
        {
            if (declaration is not ExtensionDeclaration && declaration.GetSymbol() is ITypeDefinition type)
                Own(declaration, type, CompilerGeneratedFilter.IsCompilerGenerated(type), ReferenceFormatter.FormatTypeName(type),
                    after: null);
        }
        // A member is named in full where another member's name would read alike. A block's
        // members come after its class's, and a property's accessors after the property.
        var members = declarations.OfType<TypeDeclaration>()
            .SelectMany(declaration => declaration.Members)
            .Select(member => (Declaration: member, Symbol: member.GetSymbol() as IMember, After: (EntityDeclaration)null))
            .Where(member => member.Symbol is { DeclaringTypeDefinition: not null })
            .Concat(declarations.OfType<ExtensionDeclaration>().SelectMany(BlockOwners))
            .ToList();
        var name = ReferenceFormatter.NamesFor(members.Select(member => member.Symbol));
        foreach (var (member, symbol, after) in members)
            Own(member, symbol.DeclaringTypeDefinition, CompilerGeneratedFilter.IsCompilerGenerated(symbol), name(symbol),
                after);

        // Neighboring lines of one owner as one range. A type's own lines lie around its
        // members, so a type gives several.
        var ranges = new List<LineRange>();
        LineRange current = null;
        foreach (var (line, owner) in owners.OrderBy(entry => entry.Key))
        {
            if (ReferenceEquals(owner, current) && ranges[^1].Last == line - 1)
                ranges[^1] = ranges[^1] with { Last = line };
            else
                ranges.Add(owner with { First = line, Last = line });
            current = owner;
        }
        return ranges;
    }

    /// <summary>
    /// The owners of a C# 14 extension block's lines: each member of the block with the method
    /// the compiler made of it, a property with its getter's method, or its setter's where it has
    /// no getter, and after the property each of its accessors with its own, a setter beginning
    /// after its getter. A member the type system maps to no method is left out, so its lines
    /// stay with the class.
    /// </summary>
    /// <remarks>
    /// Library behavior, read in ICSharpCode.Decompiler 11.2.0.10050: a block is an
    /// ExtensionDeclaration among its class's members, without a symbol of its own, and each of
    /// its members carries the member of the compiler's grouping class, which the type system's
    /// ExtensionInfo maps to the method the compiler made of it (CSharpDecompiler.DoDecompile,
    /// TypeSystemAstBuilder.ConvertExtension). The case:
    /// SearchCode_ListsEachLineOfABlockMemberUnderItsMethod in ExtensionBlockTests.
    /// </remarks>
    private static IEnumerable<(EntityDeclaration Declaration, IMember Symbol, EntityDeclaration After)> BlockOwners(
        ExtensionDeclaration block)
    {
        foreach (var member in block.Members)
        {
            var symbol = member.GetSymbol() as IMember;
            var extensions = symbol?.ResolveExtensionInfo();

            if (symbol is IProperty property && member is PropertyDeclaration declaration)
            {
                var getter = TypeWalker.ImplementationOf(extensions, property.Getter);
                var setter = TypeWalker.ImplementationOf(extensions, property.Setter);
                if ((getter ?? setter) is { } owner)
                    yield return (member, owner, null);
                if (getter != null && declaration.Getter is { } getterDeclaration)
                    yield return (getterDeclaration, getter, null);
                if (setter != null && declaration.Setter is { } setterDeclaration)
                    yield return (setterDeclaration, setter, declaration.Getter);
            }
            else if (symbol is IMethod method && TypeWalker.ImplementationOf(extensions, method) is { } owner)
            {
                yield return (member, owner, null);
            }
        }
    }

    // A name is one row of the table. Only an obfuscated name can hold a tab or a line end.
    private static string OneLine(string name) =>
        name.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>Unpack what <see cref="Pack"/> wrote.</summary>
    public static TypeText Read(byte[] packed)
    {
        using var reader = new StreamReader(
            new BrotliStream(new MemoryStream(packed), CompressionMode.Decompress), Encoding.UTF8);
        var ranges = new List<LineRange>();
        for (var row = reader.ReadLine(); !string.IsNullOrEmpty(row); row = reader.ReadLine())
        {
            var cells = row.Split('\t', 5);
            ranges.Add(new LineRange(
                int.Parse(cells[0], CultureInfo.InvariantCulture),
                int.Parse(cells[1], CultureInfo.InvariantCulture),
                MetadataTokens.TypeDefinitionHandle(int.Parse(cells[2], CultureInfo.InvariantCulture)),
                cells[3] == "1",
                cells[4]));
        }
        return new TypeText(ranges, reader.ReadToEnd().Split('\n'));
    }
}
