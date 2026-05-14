namespace ILens;

/// <summary>
/// Validates assembly paths supplied by MCP clients against a startup-configured
/// allow-list of root directories. Enforces normalization, separator-bounded
/// prefix matching, raw '..' rejection, and reparse-point (symbolic link and
/// junction) resolution. Assembly size and the total memory budget are the
/// <see cref="AssemblyHostRegistry"/>'s concern, not this class's.
/// </summary>
public sealed class PathGuard
{
    // The Windows kernel caps a single path resolution at 63 reparse points;
    // mirror that as the reparse-resolution fixpoint cap.
    private const int MaxReparseDepth = 63;
    private readonly IReadOnlyList<string> _allowedRoots;

    public PathGuard(IEnumerable<string> allowedRoots)
    {
        _allowedRoots = allowedRoots
            .Select(r => Path.GetFullPath(r)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();

        foreach (var root in _allowedRoots)
        {
            if (!Directory.Exists(root))
            {
                var reason = File.Exists(root)
                    ? "is a file, not a directory"
                    : "does not exist";
                throw new ArgumentException(
                    $"--allow-root path {reason}: {root}.");
            }
        }
    }

    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    /// <summary>
    /// Validate a requested assembly path. Returns the resolved absolute path on success.
    /// Throws <see cref="ArgumentException"/> with a descriptive message on rejection.
    /// </summary>
    public string Validate(string requested)
    {
        if (_allowedRoots.Count == 0)
            throw new ArgumentException(
                "No allowed roots configured. The server cannot load any assemblies. " +
                "Launch with one or more --allow-root <directory> flags.");

        if (string.IsNullOrWhiteSpace(requested))
            throw new ArgumentException("Assembly path is empty.");

        // Defense-in-depth: reject raw '..' segments before normalization
        if (ContainsParentSegment(requested))
            throw new ArgumentException(
                $"Path contains '..' which is not allowed: {requested}.");

        var normalized = Path.GetFullPath(requested);

        var matchedRoot = _allowedRoots.FirstOrDefault(r => IsUnderRoot(normalized, r));
        if (matchedRoot == null)
            throw new ArgumentException(
                $"Path is not under any allowed root: {normalized}. " +
                $"Allowed roots: {string.Join(", ", _allowedRoots)}.");

        if (!File.Exists(normalized))
            throw new ArgumentException(
                $"Assembly file does not exist: {normalized}.");

        // Path.GetFullPath above is lexical only — it does not follow symbolic
        // links or junctions. Resolve every component to its real target and
        // re-check containment, so a link planted inside an allowed root cannot
        // point the loader at a file outside every root. This is layered on top
        // of the lexical containment check, the same way the raw-'..' check is
        // layered before it.
        var resolved = ResolveReparsePoints(normalized);
        if (!_allowedRoots.Any(r => IsUnderRoot(resolved, r)))
            throw new ArgumentException(
                $"Path resolves (via a symbolic link or junction) to a location " +
                $"outside any allowed root: {resolved}. " +
                $"Allowed roots: {string.Join(", ", _allowedRoots)}.");

        return resolved;
    }

    private static bool ContainsParentSegment(string path)
    {
        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment == "..")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Canonicalize a path by resolving symbolic links and junctions in every
    /// component. <see cref="Path.GetFullPath"/> is lexical only, and
    /// <see cref="File.ResolveLinkTarget"/> resolves just the leaf — so this
    /// walks the components from the root down. A resolved target can itself
    /// contain unresolved reparse points, so the walk repeats to a fixpoint.
    /// The input must be an existing, normalized absolute path.
    /// </summary>
    private static string ResolveReparsePoints(string path)
    {
        try
        {
            var current = path;
            for (int depth = 0; depth < MaxReparseDepth; depth++)
            {
                var root = Path.GetPathRoot(current)!;
                var components = current[root.Length..].Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);

                var built = root;
                var resolvedSomething = false;
                for (int i = 0; i < components.Length; i++)
                {
                    built = Path.Combine(built, components[i]);
                    var isLeaf = i == components.Length - 1;
                    var target = isLeaf
                        ? File.ResolveLinkTarget(built, returnFinalTarget: true)
                        : Directory.ResolveLinkTarget(built, returnFinalTarget: true);
                    if (target != null)
                    {
                        // Splice the resolved target in front of the still-unprocessed
                        // components and restart — the target may itself be linked.
                        current = components[(i + 1)..]
                            .Aggregate(target.FullName, (acc, c) => Path.Combine(acc, c));
                        resolvedSomething = true;
                        break;
                    }
                }

                if (!resolvedSomething)
                    return current;
            }

            throw new ArgumentException(
                $"Path has too many levels of symbolic links or junctions " +
                $"(possible cycle): {path}.");
        }
        catch (IOException ex)
        {
            throw new ArgumentException(
                $"Path could not be resolved through its symbolic links or " +
                $"junctions: {path}. {ex.Message}");
        }
    }

    private static bool IsUnderRoot(string normalizedPath, string normalizedRoot)
    {
        if (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
