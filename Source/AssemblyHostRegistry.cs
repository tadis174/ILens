namespace ILens;

/// <summary>
/// LRU cache of <see cref="AssemblyHost"/> instances keyed by normalized path.
/// Validates incoming paths through <see cref="PathGuard"/> before loading.
/// </summary>
public sealed class AssemblyHostRegistry
{
    private const int Capacity = 5;

    private readonly PathGuard _guard;
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _byPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public AssemblyHostRegistry(PathGuard guard)
    {
        _guard = guard;
    }

    /// <summary>
    /// Validate, then load (or return from cache) the host and resolver for an assembly.
    /// Throws <see cref="ArgumentException"/> if the path fails validation.
    /// </summary>
    public (AssemblyHost Host, SymbolResolver Resolver) GetOrLoad(string requestedPath)
    {
        var normalized = _guard.Validate(requestedPath);

        lock (_lock)
        {
            if (_byPath.TryGetValue(normalized, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return (node.Value.Host, node.Value.Resolver);
            }

            var host = new AssemblyHost(normalized);
            var resolver = new SymbolResolver(host.TypeSystem);
            var entry = new CacheEntry(normalized, host, resolver);
            var newNode = new LinkedListNode<CacheEntry>(entry);
            _lru.AddFirst(newNode);
            _byPath[normalized] = newNode;

            Console.Error.WriteLine(
                $"[ILens] loaded {normalized} ({new FileInfo(normalized).Length} bytes)");

            while (_lru.Count > Capacity)
            {
                var evict = _lru.Last!;
                _lru.RemoveLast();
                _byPath.Remove(evict.Value.NormalizedPath);
                evict.Value.Host.Dispose();
                Console.Error.WriteLine(
                    $"[ILens] evicted {evict.Value.NormalizedPath} (LRU full)");
            }

            return (host, resolver);
        }
    }

    private sealed record CacheEntry(
        string NormalizedPath, AssemblyHost Host, SymbolResolver Resolver);
}
