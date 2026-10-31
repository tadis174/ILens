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
/// its braces; and none for the using and namespace lines, which belong to no type.
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
                    if (texts.Keep(type, Pack(decompiler.DecompileType(definition.FullTypeName), settings)))
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
        var text = new StringWriter();
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
        void Own(EntityDeclaration declaration, ITypeDefinition type, bool generated, string name)
        {
            // A declaration's first line is the least line any of its nodes starts on, not its
            // own start, which is its first child's. At the end of each node the writer takes out
            // the children it wrote and adds them back in the order it wrote them, and what it
            // does not count as written stays in front: the comments of a member's documentation,
            // which the decompiler puts among its type's children, and the target of an attribute
            // on a return value, [return: ...], which has no location. A type with a documented
            // member would otherwise start on that member's first comment line, above which its
            // attributes and header line would have no owner. The leaves are read, since an inner
            // node's start is its first child's. The children the writer wrote come back last, so
            // the declaration's own end, its last child's, is its last line.
            int first = declaration.Descendants.Where(node => node.FirstChild is null)
                .Select(node => node.StartLocation.Line).Where(line => line > 0)
                .DefaultIfEmpty(0).Min();
            if (first < 1)
                return;
            var range = new LineRange(first, declaration.EndLocation.Line,
                (TypeDefinitionHandle)type.MetadataToken, generated, OneLine(name));
            for (int line = range.First; line <= range.Last; line++)
                owners[line] = range;
        }

        var declarations = tree.Descendants.OfType<EntityDeclaration>().ToList();
        foreach (var declaration in declarations)
        {
            if (declaration.GetSymbol() is ITypeDefinition type)
                Own(declaration, type, CompilerGeneratedFilter.IsCompilerGenerated(type), ReferenceFormatter.FormatTypeName(type));
        }
        // A member is named in full where another member's name would read alike.
        var members = declarations.OfType<TypeDeclaration>()
            .SelectMany(declaration => declaration.Members)
            .Select(member => (Declaration: member, Symbol: member.GetSymbol() as IMember))
            .Where(member => member.Symbol is { DeclaringTypeDefinition: not null })
            .ToList();
        var name = ReferenceFormatter.NamesFor(members.Select(member => member.Symbol));
        foreach (var (member, symbol) in members)
            Own(member, symbol.DeclaringTypeDefinition, CompilerGeneratedFilter.IsCompilerGenerated(symbol), name(symbol));

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
