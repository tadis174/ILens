namespace ILens;

/// <summary>
/// Validates assembly paths supplied by MCP clients against a startup-configured
/// allow-list of root directories. Enforces normalization, separator-bounded
/// prefix matching, raw '..' rejection, and reparse-point (symbolic link and
/// junction) resolution. Assembly size and the total memory budget are the
/// <see cref="AssemblyHostRegistry"/>'s concern, not this class's.
/// </summary>
/// <remarks>
/// A root may itself be given through a link, such as a folder moved to another drive with a
/// junction left at its old place. Each root is therefore kept in two spellings, the one given
/// and the one its links resolve to when the server starts, and a file under it may be asked
/// for in either. The resolved spelling is not read again: what is allowed is the folder the
/// link pointed to at startup, so a link moved to another target while the server runs leads
/// to a refusal instead of widening what the server reads.
/// A path that lies under neither spelling is refused before the file system is read, also
/// when a link elsewhere would lead it into a root. Resolving it first would have the server
/// follow links for any path a client names.
/// </remarks>
public sealed class PathGuard
{
    // The Windows kernel caps a single path resolution at 63 reparse points;
    // mirror that as the reparse-resolution fixpoint cap.
    private const int MaxReparseDepth = 63;
    private readonly IReadOnlyList<Root> _roots;

    public PathGuard(IEnumerable<string> allowedRoots)
    {
        AllowedRoots = allowedRoots
            .Select(r => Path.GetFullPath(r)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();

        foreach (var root in AllowedRoots)
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

        _roots = AllowedRoots
            .Select(root => new Root(root, ResolveReparsePoints(root, leafIsDirectory: true)))
            .ToList();
    }

    /// <summary>The roots as they were given, which is how the server names them to a client.</summary>
    public IReadOnlyList<string> AllowedRoots { get; }

    /// <summary>A root as it was given and as its links resolved when the server started.</summary>
    private readonly record struct Root(string Given, string Resolved)
    {
        /// <summary>Whether a normalized path lies under either spelling of the root.</summary>
        public bool Covers(string normalizedPath) =>
            IsUnderRoot(normalizedPath, Given) || IsUnderRoot(normalizedPath, Resolved);

        /// <summary>
        /// The root for a refusal: with the folder it resolved to where that is another path, since
        /// a refusal after its link was moved cannot be understood without the folder allowed.
        /// </summary>
        public override string ToString() =>
            Given.Equals(Resolved, StringComparison.OrdinalIgnoreCase) ? Given : $"{Given} (resolved: {Resolved})";
    }

    /// <summary>
    /// Validate a requested assembly path. Returns the resolved absolute path on success.
    /// Throws <see cref="ArgumentException"/> with a descriptive message on rejection.
    /// </summary>
    public string Validate(string requested)
    {
        if (_roots.Count == 0)
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

        if (!_roots.Any(r => r.Covers(normalized)))
            throw new ArgumentException(
                $"Path is not under any allowed root: {normalized}. " +
                $"Allowed roots: {string.Join(", ", _roots)}.");

        if (!File.Exists(normalized))
            throw new ArgumentException(
                $"Assembly file does not exist: {normalized}.");

        // Path.GetFullPath above is lexical only — it does not follow symbolic
        // links or junctions. Resolve every component to its real target and
        // re-check containment, so a link planted inside an allowed root cannot
        // point the loader at a file outside every root. This is layered on top
        // of the lexical containment check, the same way the raw-'..' check is
        // layered before it.
        var resolved = ResolveReparsePoints(normalized, leafIsDirectory: false);
        if (!ResolvesIntoARoot(resolved))
            throw new ArgumentException(
                $"Path resolves (via a symbolic link or junction) to a location " +
                $"outside any allowed root: {resolved}. " +
                $"Allowed roots: {string.Join(", ", _roots)}.");

        return resolved;
    }

    /// <summary>
    /// Whether the decompiler may open a file it found for an assembly reference. A file under an
    /// allowed root must resolve inside one, as a requested path must, so a link planted in a root
    /// cannot point the decompiler at a file outside every root. A file outside every root is one
    /// the search for the installed .NET libraries found, which is not this class's concern.
    /// </summary>
    public bool AdmitsReference(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (!_roots.Any(r => r.Covers(normalized)))
            return true;

        try
        {
            return ResolvesIntoARoot(ResolveReparsePoints(normalized, leafIsDirectory: false));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a path with its links resolved lies under the resolved spelling of a root. The
    /// spelling a root was given in does not count here: no resolved path lies under a link.
    /// </summary>
    private bool ResolvesIntoARoot(string resolvedPath) =>
        _roots.Any(r => IsUnderRoot(resolvedPath, r.Resolved));

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
    /// The input must be an existing, normalized absolute path, whose last
    /// component is a directory when <paramref name="leafIsDirectory"/>.
    /// </summary>
    private static string ResolveReparsePoints(string path, bool leafIsDirectory)
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
                    var isFile = i == components.Length - 1 && !leafIsDirectory;
                    var target = isFile
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
        // Whether a component is a link is read in the folder above it, which fails with
        // UnauthorizedAccessException when that folder may not be listed.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
