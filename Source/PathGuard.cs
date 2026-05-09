namespace ILens;

/// <summary>
/// Validates assembly paths supplied by MCP clients against a startup-configured
/// allow-list of root directories. Enforces normalization, separator-bounded
/// prefix matching, raw '..' rejection, and a hard size cap.
/// </summary>
public sealed class PathGuard
{
    private const long MaxSizeBytes = 200L * 1024 * 1024;
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
                throw new ArgumentException(
                    $"--allow-root path does not exist: {root}.");
        }
    }

    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    /// <summary>
    /// Validate a requested assembly path. Returns the normalized absolute path on success.
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

        var size = new FileInfo(normalized).Length;
        if (size > MaxSizeBytes)
            throw new ArgumentException(
                $"Assembly exceeds {MaxSizeBytes / 1024 / 1024} MB size cap " +
                $"(actual: {size / 1024 / 1024} MB): {normalized}.");

        return normalized;
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

    private static bool IsUnderRoot(string normalizedPath, string normalizedRoot)
    {
        if (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
