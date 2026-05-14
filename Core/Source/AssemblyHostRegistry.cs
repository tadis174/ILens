namespace ILens;

/// <summary>
/// LRU cache of <see cref="AssemblyHost"/> instances keyed by normalized path.
/// Validates incoming paths through <see cref="PathGuard"/> before loading, and
/// bounds total memory by an aggregate budget — the sum of the cached assemblies'
/// on-disk sizes — evicting least-recently-used hosts until a new load fits.
/// </summary>
public sealed class AssemblyHostRegistry
{
    // Aggregate memory budget used when --max-total-size is not given.
    private const long DefaultMaxTotalBytes = 200L * 1024 * 1024;

    private readonly PathGuard _guard;
    private readonly long _maxTotalBytes;
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _byPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private long _currentTotalBytes;  // sum of CacheEntry.Bytes in _lru; guarded by _lock

    public AssemblyHostRegistry(PathGuard guard, long? maxTotalBytes = null)
    {
        _guard = guard;
        _maxTotalBytes = maxTotalBytes ?? DefaultMaxTotalBytes;
    }

    /// <summary>
    /// Validate, then load (or return from cache) the host for an assembly.
    /// Throws <see cref="ArgumentException"/> if the path fails validation or if
    /// the assembly alone exceeds the total memory budget. A successful load may
    /// evict least-recently-used hosts to keep the cache within budget.
    /// Callers needing symbol resolution use <see cref="AssemblyHost.Resolver"/>.
    /// </summary>
    public AssemblyHost GetOrLoad(string requestedPath)
    {
        var normalized = _guard.Validate(requestedPath);
        AssemblyHost host;
        long bytes;
        List<CacheEntry> evicted = null;

        lock (_lock)
        {
            if (_byPath.TryGetValue(normalized, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Host;
            }

            bytes = new FileInfo(normalized).Length;

            // A single assembly larger than the whole budget can never fit, even
            // with an empty cache — reject it rather than load-then-evict-itself.
            if (bytes > _maxTotalBytes)
                throw new ArgumentException(
                    $"Assembly is {bytes / 1024 / 1024} MB, which exceeds the " +
                    $"{_maxTotalBytes / 1024 / 1024} MB total memory budget on its own: " +
                    $"{normalized}. Raise the budget with --max-total-size <MB>.");

            host = new AssemblyHost(normalized);
            var entry = new CacheEntry(normalized, host, bytes);
            var newNode = new LinkedListNode<CacheEntry>(entry);
            _lru.AddFirst(newNode);
            _byPath[normalized] = newNode;
            _currentTotalBytes += bytes;

            // Evict least-recently-used hosts (from the back) until the cache is
            // within budget. The just-added entry is at the front, and the budget
            // precheck above guarantees it alone fits — so the `_lru.Count > 1`
            // guard is belt-and-suspenders: the just-requested host is never
            // evicted. Collect under the lock; Dispose outside it (below).
            while (_currentTotalBytes > _maxTotalBytes && _lru.Count > 1)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _byPath.Remove(last.Value.NormalizedPath);
                _currentTotalBytes -= last.Value.Bytes;
                (evicted ??= new()).Add(last.Value);
            }
        }

        // Logging and Dispose run outside _lock: Dispose blocks on the per-host
        // _decompilerLock (waits for any in-flight decompile/analyze to finish), so
        // holding the registry lock across it would stall every concurrent tool call.
        // Each Dispose is wrapped so a single corrupt PE can't poison eviction of the rest.
        Console.Error.WriteLine($"[ILens] loaded {normalized} ({bytes} bytes)");

        if (evicted != null)
        {
            foreach (var entry in evicted)
            {
                try
                {
                    entry.Host.Dispose();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[ILens] error disposing evicted host {entry.NormalizedPath}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
                Console.Error.WriteLine(
                    $"[ILens] evicted {entry.NormalizedPath} (over memory budget)");
            }
        }

        return host;
    }

    private sealed record CacheEntry(string NormalizedPath, AssemblyHost Host, long Bytes);
}
