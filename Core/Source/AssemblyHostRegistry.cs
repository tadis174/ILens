using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ILens;

/// <summary>
/// LRU cache of <see cref="AssemblyHost"/> instances keyed by normalized path, each answering for
/// its file as it was when the host read it. Validates incoming paths through
/// <see cref="PathGuard"/> before loading, and keeps the process's memory within a budget: when a
/// call ends with the process above it, the least-recently-used hosts that no call holds are
/// closed until it is back under three quarters of the budget.
/// </summary>
/// <remarks>
/// A host reads its file whole into memory, so a project can rebuild the file in place while the
/// host lives: copying a new build over the file succeeded while its host lived (measured
/// 2026-11-14). Each call therefore compares the file's <see cref="FileStamp"/> with the one the
/// host read it with, and gets a new host where they differ, as after such a rebuild. The old host
/// leaves the cache at once, so that no later call gets it, and is closed once no call holds it;
/// a call that holds it answers from the file as that host read it. A library the host read as a
/// reference from under an allowed root is held in memory too, read once for every host that
/// reads that version of it (<see cref="ReferenceImages"/>), and compared at each call in the
/// same way (<see cref="AssemblyHost.ReferenceStamps"/>), so that a host whose base class a
/// rebuilt library defines answers from the rebuild. A library whose file is gone gets the host
/// replaced as well, and the new host looks for it again at each call, since a build may delete a
/// file before it writes it anew. A reference outside every allowed root stays open until its
/// host is closed, which keeps it from being replaced meanwhile, and a reference no host of the
/// assembly found is looked for again only by a host built for another reason.
///
/// The budget is the process's private memory as Windows charges it, read when a lease is
/// released. A host's file size is no measure of what it holds: the decompiler builds a type
/// system over every reference it can read, and each host builds its own, so a mod of 0.1 MB
/// holds about 1.75 MB once opened (measured on 335 game mods, 2026-10-06). With the game's
/// assemblies in an allow-root, each mod's host held about 3.5 MB beside the game's libraries,
/// which the hosts share: 20 mods read 68 of them once, 32.8 MB (measured 2026-11-14). A closed
/// host's memory is free only once the garbage collector has run, so the check forces a
/// collection before it reads again.
///
/// A host is handed out as a <see cref="Lease"/>, and only a host that no lease holds is closed,
/// chosen under the registry's lock. The type system reads the assembly's image lazily, well
/// outside the host's decompiler lock, so a host closed while a call still held it would have
/// that call read freed memory. The budget can therefore be exceeded while calls run, such as a
/// comparison holding one assembly while it loads the other, and an assembly that alone needs
/// more than the budget is closed whenever the calls on it end.
///
/// The registry also owns what outlives a host and is set once for the server: the libraries the
/// hosts share, the decompiled text search_code keeps, which has a share of the budget of its own,
/// and the time a search may spend decompiling in one call.
/// </remarks>
public sealed class AssemblyHostRegistry
{
    // Memory budget used when --max-total-size is not given.
    private const long DefaultMaxTotalBytes = 200L * 1024 * 1024;

    // The time a search may spend decompiling in one call when --max-search-seconds is not
    // given. A client built on the MCP SDK's default gives up on a request after 60 s, and a
    // call needs about 1.5 s beyond its limit to stop, pack and search (measured on a game
    // assembly of 16 MB); the rest is room for a slower machine.
    private static readonly TimeSpan DefaultSearchTimeLimit = TimeSpan.FromSeconds(45);

    // The part of the memory budget the texts kept for search_code may take, packed: a quarter,
    // which at the default holds the texts of four assemblies of 16 MB at 10.5 MB each.
    private const int SearchShare = 4;

    private readonly PathGuard _guard;
    private readonly ReferenceImages _references = new();
    private readonly long _maxTotalBytes;
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _byPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly object _enforceLock = new();  // held while a thread brings the process back under the budget

    public AssemblyHostRegistry(PathGuard guard, long? maxTotalBytes = null, TimeSpan? searchTimeLimit = null)
    {
        _guard = guard;
        _maxTotalBytes = maxTotalBytes ?? DefaultMaxTotalBytes;
        SearchTimeLimit = searchTimeLimit ?? DefaultSearchTimeLimit;
        CodeTexts = new CodeTextStore(_maxTotalBytes / SearchShare);
    }

    /// <summary>The time a search_code call may spend decompiling before it answers how far it came.</summary>
    public TimeSpan SearchTimeLimit { get; }

    /// <summary>The decompiled text search_code keeps, which outlives the hosts it was read from.</summary>
    public CodeTextStore CodeTexts { get; }

    /// <summary>
    /// Hand back to Windows the memory a call needed only while it ran, before its lease is
    /// released and the budget is read. A search that decompiles builds a type system of its
    /// own and drops it: several hundred MB that an ordinary collection would keep committed,
    /// so that the reading would find the process over the budget and close every host no call
    /// holds for memory that is free already. Measured after such a pass: 0.07 to 0.21 s, down
    /// to 83 to 174 MB.
    /// </summary>
    public void HandBackTransientMemory() => Collect(aggressive: true);

    /// <summary>
    /// Validate, then load the host for an assembly, or return the cached one while the file and
    /// the references held in memory are as that host read them, leased to the caller until the
    /// returned lease is disposed. Throws
    /// <see cref="ArgumentException"/> if the path fails validation or if the assembly's file
    /// alone is larger than the memory budget. Callers needing symbol resolution use
    /// <see cref="AssemblyHost.Resolver"/>.
    /// </summary>
    public Lease GetOrLoad(string requestedPath)
    {
        var normalized = _guard.Validate(requestedPath);
        CacheEntry entry;
        FileStamp stamp;
        IReadOnlyList<string> gone = [];

        lock (_lock)
        {
            // Read under the lock, so that a call that waited for it compares the file as it is
            // then, and before a host reads the file, so that a host's stamp is never newer than
            // what it holds: a file that changes while a host reads it shows another stamp at the
            // next call.
            stamp = FileStamp.Of(normalized);
            if (_byPath.TryGetValue(normalized, out var node))
            {
                var host = node.Value.Host;
                var changed = host.FileStamp != stamp ? "its file changed"
                    : host.ReferenceStamps.Changed() is { } reference ? $"its reference {Path.GetFileName(reference)} changed"
                    : null;
                if (changed is null)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    node.Value.Leases++;
                    return new Lease(this, node.Value);
                }

                // The file or a reference changed since the host read it. A host no call holds is
                // closed here, under the lock, unlike in EnforceBudget: the new host is built under
                // it anyway, which takes far longer, and closing first frees the old images for it.
                // The new host watches the references whose files are gone.
                gone = host.ReferenceStamps.Gone();
                _lru.Remove(node);
                _byPath.Remove(normalized);
                node.Value.LeftBecause = changed;
                if (node.Value.Leases == 0)
                    Close(node.Value, changed);
            }

            // A host holds its assembly's whole image in memory, so one whose file alone is
            // larger than the budget can never fit — reject it rather than load it.
            if (stamp.Length > _maxTotalBytes)
                throw new ArgumentException(
                    $"Assembly is {stamp.Length / 1024 / 1024} MB, which exceeds the " +
                    $"{_maxTotalBytes / 1024 / 1024} MB total memory budget on its own: " +
                    $"{normalized}. Raise the budget with --max-total-size <MB>.");

            entry = new CacheEntry(normalized, new AssemblyHost(normalized, _guard, stamp, _references, gone)) { Leases = 1 };
            _byPath[normalized] = _lru.AddFirst(entry);
        }

        Console.Error.WriteLine($"[ILens] loaded {normalized} ({stamp.Length} bytes)");
        return new Lease(this, entry);
    }

    private void Release(CacheEntry entry)
    {
        bool close;
        lock (_lock)
        {
            entry.Leases--;
            // An entry that left the cache while calls held it left because its file or a
            // reference changed: the budget closes only entries that no lease holds.
            close = entry.Leases == 0 && !IsCached(entry);
        }
        if (close)
            Close(entry, entry.LeftBecause);
        EnforceBudget();
    }

    // Called under _lock.
    private bool IsCached(CacheEntry entry) =>
        _byPath.TryGetValue(entry.NormalizedPath, out var node) && node.Value == entry;

    /// <summary>
    /// When the process is above the budget, collect garbage and close least-recently-used hosts
    /// that no call holds until the process is under three quarters of the budget or no such host
    /// is left. The lower mark leaves room for the calls that follow, so that not every call ends
    /// in a collection. One thread at a time: a call that ends while another enforces waits, then
    /// reads again, since the other may have chosen its hosts before this call's host was free.
    /// </summary>
    /// <remarks>
    /// An ordinary collection keeps the memory it frees committed for later use and does not hand
    /// it back to Windows on its own: after a heap of 600 MB with 17 MB still live, it kept 528 MB
    /// for at least 5 s. Private bytes therefore fall less than the closed hosts held, and a round
    /// goes on closing until they are under the mark, or, with nothing left to close, ends with one
    /// aggressive collection, which hands the kept memory back. Judging instead by what the process
    /// uses, private bytes less what the collector keeps unused, closed fewer hosts per round, so
    /// rounds came 2.4 times as often, and 335 mods with the game's assemblies took 60 s instead of
    /// 46 s, against 40 s with nothing closed (measured 2026-10-06).
    /// </remarks>
    private void EnforceBudget()
    {
        if (ProcessMemory.PrivateBytes() <= _maxTotalBytes)
            return;
        lock (_enforceLock)
        {
            long before = ProcessMemory.PrivateBytes();
            if (before <= _maxTotalBytes)
                return;
            long target = _maxTotalBytes / 4 * 3;
            int closed = 0;
            bool aggressive = false;
            while (true)
            {
                Collect(aggressive);
                long now = ProcessMemory.PrivateBytes();
                var batch = now > target ? TakeLeastRecentlyUsed(now, target) : [];
                if (batch.Count == 0)
                {
                    if (now > target && !aggressive)
                    {
                        aggressive = true;
                        continue;
                    }
                    Console.Error.WriteLine(
                        $"[ILens] over the {_maxTotalBytes / 1024 / 1024} MB memory budget: " +
                        $"{before / 1024 / 1024} MB, {now / 1024 / 1024} MB after closing {closed} assemblies");
                    return;
                }
                // Logging and Dispose run outside _lock so a slow teardown cannot stall
                // every concurrent tool call.
                foreach (var e in batch)
                    Close(e, "over memory budget");
                closed += batch.Count;
            }
        }
    }

    /// <summary>
    /// Remove from the cache, least recently used first, as many hosts that no call holds as
    /// should bring the process from <paramref name="now"/> down to <paramref name="target"/>,
    /// counting each open host at an equal share of <paramref name="now"/>. When the batch frees
    /// less than that, the next pass, after another collection, closes more.
    /// </summary>
    private List<CacheEntry> TakeLeastRecentlyUsed(long now, long target)
    {
        lock (_lock)
        {
            if (_lru.Count == 0)
                return [];
            long perHost = Math.Max(1, now / _lru.Count);
            long wanted = (now - target + perHost - 1) / perHost;
            var taken = new List<CacheEntry>();
            for (var node = _lru.Last; node != null && taken.Count < wanted;)
            {
                var previous = node.Previous;
                if (node.Value.Leases == 0)
                {
                    _lru.Remove(node);
                    _byPath.Remove(node.Value.NormalizedPath);
                    taken.Add(node.Value);
                }
                node = previous;
            }
            return taken;
        }
    }

    // Blocking and compacting, so that what closed hosts held is free when private bytes are read
    // again; pending finalizers run too, which free native memory nobody disposed. Aggressive also
    // hands back the memory the collector would keep, but only as a last resort: used for every
    // collection, it doubled the time of a run over 335 mods (measured 2026-10-06).
    private static void Collect(bool aggressive)
    {
        GC.Collect(GC.MaxGeneration, aggressive ? GCCollectionMode.Aggressive : GCCollectionMode.Forced,
            blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    // Logs why a host is closed, then disposes it. Each Dispose is wrapped so a single corrupt PE
    // can't poison closing the rest.
    private static void Close(CacheEntry entry, string reason)
    {
        Console.Error.WriteLine($"[ILens] closed {entry.NormalizedPath} ({reason})");
        try
        {
            entry.Host.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ILens] error disposing closed host {entry.NormalizedPath}: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// A tool call's hold on a host. The registry does not close a host while any lease on it
    /// is held. Disposing twice releases once.
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private readonly AssemblyHostRegistry _registry;
        private readonly CacheEntry _entry;
        private int _released;

        internal Lease(AssemblyHostRegistry registry, CacheEntry entry)
        {
            _registry = registry;
            _entry = entry;
        }

        public AssemblyHost Host => _entry.Host;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _registry.Release(_entry);
        }
    }

    internal sealed class CacheEntry(string normalizedPath, AssemblyHost host)
    {
        public string NormalizedPath { get; } = normalizedPath;
        public AssemblyHost Host { get; } = host;
        public int Leases { get; set; }      // guarded by the registry's _lock
        public string LeftBecause { get; set; }  // why GetOrLoad took it out of the cache; guarded by _lock
    }

    /// <summary>
    /// The process's private bytes as Windows charges them, read in about a microsecond.
    /// <see cref="System.Diagnostics.Process.PrivateMemorySize64"/> takes milliseconds, since
    /// .NET enumerates every process on the system to fill it in.
    /// </summary>
    private static class ProcessMemory
    {
        public static long PrivateBytes()
        {
            if (!K32GetProcessMemoryInfo(GetCurrentProcess(), out var counters, (uint)Marshal.SizeOf<Counters>()))
                throw new Win32Exception();
            return (long)counters.PrivateUsage;
        }

        // PROCESS_MEMORY_COUNTERS_EX
        [StructLayout(LayoutKind.Sequential)]
        private struct Counters
        {
            public uint Size;
            public uint PageFaultCount;
            public nuint PeakWorkingSetSize;
            public nuint WorkingSetSize;
            public nuint QuotaPeakPagedPoolUsage;
            public nuint QuotaPagedPoolUsage;
            public nuint QuotaPeakNonPagedPoolUsage;
            public nuint QuotaNonPagedPoolUsage;
            public nuint PagefileUsage;
            public nuint PeakPagefileUsage;
            public nuint PrivateUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32GetProcessMemoryInfo(nint process, out Counters counters, uint size);

        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentProcess();
    }
}
