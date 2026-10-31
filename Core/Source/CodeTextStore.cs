using System.Reflection.Metadata;

namespace ILens;

/// <summary>
/// The decompiled text search_code keeps, per assembly and top-level type, so that only the
/// first search over a type pays for decompiling it.
/// </summary>
/// <remarks>
/// The texts live here and not in the hosts. The registry closes a host whenever the process is
/// over its memory budget, and a text that went with its host would have to be decompiled again,
/// which takes most of a minute for a large assembly, against a fraction of a second for a
/// search through the kept text.
///
/// The store has a share of the memory budget of its own and counts the packed texts against
/// it, a fixed size, where the budget itself reads the process's memory, which depends on when
/// the collector last ran. A search can therefore not lose what it has decompiled by chance
/// between two of its calls. When a text does not fit, room is made in this order: the whole
/// text of another assembly no call is searching, the one searched longest ago first; then, of
/// the assembly being searched, the types outside the scope of the one call that is searching
/// it. A scope that does not fit by itself is an error.
///
/// A text is kept for a file as it was when it was decompiled, known by its length and write
/// time. A host maps its file, which keeps the file from being replaced, so a file can only
/// change while its host is closed, and the next search then finds the difference here.
/// </remarks>
public sealed class CodeTextStore(long maxBytes)
{
    private readonly object _lock = new();  // guards everything below and the contents of every AssemblyText
    private readonly Dictionary<string, AssemblyText> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;   // the packed bytes of every text in _byPath
    private long _clock;   // counts searches, to tell which assembly was searched longest ago

    /// <summary>The most the kept texts may measure together, packed.</summary>
    public long MaxBytes => maxBytes;

    /// <summary>
    /// The hold one search_code call has on the texts of the assembly at
    /// <paramref name="assemblyPath"/>, a normalized path, for the top-level types in
    /// <paramref name="scope"/>. A text kept for an earlier version of the file is dropped first.
    /// </summary>
    public Search Open(string assemblyPath, IReadOnlyList<TypeDefinitionHandle> scope)
    {
        var file = new FileInfo(assemblyPath);
        lock (_lock)
        {
            if (_byPath.TryGetValue(assemblyPath, out var texts)
                && (texts.FileLength != file.Length || texts.FileWritten != file.LastWriteTimeUtc))
            {
                Drop(assemblyPath, texts);
                texts = null;
            }
            if (texts is null)
                _byPath[assemblyPath] = texts = new AssemblyText(file.Length, file.LastWriteTimeUtc);
            texts.Searches++;
            texts.LastSearched = ++_clock;
            return new Search(this, assemblyPath, texts, scope);
        }
    }

    // Called under _lock. A call still searching the dropped text goes on with it, uncounted.
    private void Drop(string assemblyPath, AssemblyText texts)
    {
        _byPath.Remove(assemblyPath);
        _bytes -= texts.Bytes;
        texts.Dropped = true;
    }

    /// <summary>What is kept of one assembly. Every member is guarded by the store's lock.</summary>
    internal sealed class AssemblyText(long fileLength, DateTime fileWritten)
    {
        public long FileLength => fileLength;
        public DateTime FileWritten => fileWritten;
        public Dictionary<TypeDefinitionHandle, byte[]> Texts { get; } = new();
        public Dictionary<TypeDefinitionHandle, string> Failed { get; } = new();
        public object PassLock { get; } = new();
        public long Bytes { get; set; }
        public int Searches { get; set; }       // calls holding this text
        public long LastSearched { get; set; }
        public bool Dropped { get; set; }       // no longer in the store, so no longer counted
    }

    /// <summary>
    /// One call's hold on an assembly's texts. While it is held, the store does not drop the
    /// assembly's text to make room for another's.
    /// </summary>
    public sealed class Search : IDisposable
    {
        private readonly CodeTextStore _store;
        private readonly string _assemblyPath;
        private readonly AssemblyText _texts;
        private readonly IReadOnlyList<TypeDefinitionHandle> _scope;
        private readonly HashSet<TypeDefinitionHandle> _inScope;
        private bool _released;

        internal Search(
            CodeTextStore store, string assemblyPath, AssemblyText texts, IReadOnlyList<TypeDefinitionHandle> scope)
        {
            _store = store;
            _assemblyPath = assemblyPath;
            _texts = texts;
            _scope = scope;
            _inScope = scope.ToHashSet();
        }

        /// <summary>
        /// Held by the call that decompiles for the assembly, so that two searches do not
        /// decompile the same types side by side.
        /// </summary>
        public object PassLock => _texts.PassLock;

        /// <summary>
        /// True once a text of the scope did not fit into the store's share with everything
        /// else that could go dropped: the scope needs more than the share by itself.
        /// </summary>
        public bool OverShare { get; private set; }

        /// <summary>The types of the scope that are neither decompiled nor known to fail.</summary>
        public List<TypeDefinitionHandle> Missing()
        {
            lock (_store._lock)
                return _scope.Where(type => !_texts.Texts.ContainsKey(type) && !_texts.Failed.ContainsKey(type)).ToList();
        }

        /// <summary>
        /// Keep <paramref name="packed"/> as the text of <paramref name="type"/>, making room for
        /// it as the store's remarks describe. False when there is no room to make; the text is
        /// then not kept and <see cref="OverShare"/> is set.
        /// </summary>
        public bool Keep(TypeDefinitionHandle type, byte[] packed)
        {
            lock (_store._lock)
            {
                _texts.Texts[type] = packed;
                _texts.Bytes += packed.Length;
                if (_texts.Dropped)
                    return true;
                _store._bytes += packed.Length;

                while (_store._bytes > _store.MaxBytes)
                {
                    var idle = _store._byPath
                        .Where(entry => entry.Value.Searches == 0)
                        .OrderBy(entry => entry.Value.LastSearched)
                        .FirstOrDefault();
                    if (idle.Value is not null)
                    {
                        _store.Drop(idle.Key, idle.Value);
                        Console.Error.WriteLine($"[ILens] dropped the kept text of {idle.Key} (search share full)");
                        continue;
                    }

                    var outside = _texts.Searches == 1
                        ? _texts.Texts.Keys.Where(kept => !_inScope.Contains(kept)).ToList()
                        : [];
                    if (outside.Count > 0)
                    {
                        foreach (var kept in outside)
                            Remove(kept);
                        Console.Error.WriteLine(
                            $"[ILens] dropped {outside.Count} kept types of {_assemblyPath} (search share full)");
                        continue;
                    }

                    Remove(type);
                    OverShare = true;
                    return false;
                }
                return true;
            }
        }

        // Called under the store's lock, for a text that is counted.
        private void Remove(TypeDefinitionHandle type)
        {
            int length = _texts.Texts[type].Length;
            _texts.Texts.Remove(type);
            _texts.Bytes -= length;
            _store._bytes -= length;
        }

        /// <summary>
        /// Records that <paramref name="type"/>, printed as <paramref name="name"/>, could not be
        /// decompiled. It is not tried again: what failed once fails the same way the next time.
        /// </summary>
        public void Fail(TypeDefinitionHandle type, string name)
        {
            lock (_store._lock)
                _texts.Failed[type] = name;
        }

        /// <summary>
        /// The scope as it is kept at this moment: the packed texts of its decompiled types, the
        /// names of those that failed, and whether every type is one or the other.
        /// </summary>
        public (IReadOnlyList<byte[]> Texts, IReadOnlyList<string> Failed, int Done) Read()
        {
            lock (_store._lock)
            {
                var texts = new List<byte[]>();
                var failed = new List<string>();
                foreach (var type in _scope)
                {
                    if (_texts.Texts.TryGetValue(type, out var packed))
                        texts.Add(packed);
                    else if (_texts.Failed.TryGetValue(type, out var name))
                        failed.Add(name);
                }
                return (texts, failed, texts.Count + failed.Count);
            }
        }

        public void Dispose()
        {
            lock (_store._lock)
            {
                if (_released)
                    return;
                _released = true;
                _texts.Searches--;
            }
        }
    }
}
