namespace ILens;

/// <summary>
/// What tells one version of a file from the next: its length and the time it was last written.
/// </summary>
/// <remarks>
/// The write time carries the comparison: a build's length usually comes in steps of 512 bytes,
/// the alignment of its sections, unless a signature follows them, so two builds of one library
/// often have the same length, as both builds of a class renamed between them had 4,096 bytes
/// (measured 2026-11-14). A build that writes a library anew changes its write time, also where
/// it copies the library over the old one, since a copy takes the write time of the file it
/// copies, which the build has just written. A file that changes and keeps its length and its
/// write time reads as unchanged: on a file system that keeps write times in steps of two
/// seconds, as FAT32 does, a rebuild within two seconds of the build read before, and a file a
/// tool writes with a fixed time. The module's version id would close that gap at the cost of
/// reading the metadata at every call, and would miss a tool that rewrites an assembly and keeps
/// the id; a hash of the content would read the whole file at every call.
/// </remarks>
public readonly record struct FileStamp(long Length, DateTime WrittenUtc)
{
    /// <summary>The stamp of the file at <paramref name="path"/> as it is now, both values read at once.</summary>
    public static FileStamp Of(string path)
    {
        var file = new FileInfo(path);
        return new FileStamp(file.Length, file.LastWriteTimeUtc);
    }

    /// <summary>
    /// The stamp of the file at <paramref name="path"/> as it is now, or null where there is no
    /// file there; whether it is there and both values are read at once. A symbolic link to a
    /// file gives its target's stamp.
    /// </summary>
    /// <remarks>
    /// FileInfo reads a file's length and write time with GetFileAttributesEx, which gives a
    /// symbolic link's own, and a rebuild of its target leaves those as they are, while a reader
    /// that opens the link reads the target. Creating such a link needed administrator rights
    /// where this was written, on 2026-11-14, so the link's case is read in the code, not measured.
    /// </remarks>
    public static FileStamp? TryOf(string path)
    {
        var file = new FileInfo(path);
        if (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            try
            {
                if (file.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target)
                    file = target;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return file.Exists ? new FileStamp(file.Length, file.LastWriteTimeUtc) : null;
    }
}

/// <summary>
/// The libraries a host read as references from under an allowed root, each with the
/// <see cref="FileStamp"/> its file had before the host read it, also a file whose content could
/// not be read. A build can replace such a library while the host lives, since the host holds it
/// in memory (<see cref="ReferenceImages"/>), so the registry compares each with its file at every
/// call, as it compares the host's own file, and search_code keeps a text for one set of them
/// (<see cref="CodeTextStore"/>). A file without a stamp is one the host holds nothing of and
/// looks for again: one whose reading failed in a way that can pass, such as a build holding the
/// file, which changes as soon as the file is there, and one that was gone when the host was
/// built in place of a host that had read it, which changes once the file is back. Two sets are
/// equal where they name the same files with the same stamps, whatever the order in which their
/// hosts read them.
/// </summary>
public sealed class ReferenceStamps : IEquatable<ReferenceStamps>
{
    private readonly (string Path, FileStamp? Stamp)[] _files;

    /// <summary>The set of <paramref name="files"/>, the first of each path where one is named twice.</summary>
    public ReferenceStamps(IEnumerable<(string Path, FileStamp? Stamp)> files) =>
        _files = files
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// The path of the first library whose file is no longer as the host read it, replaced, gone
    /// or there again, or null where every one is.
    /// </summary>
    public string Changed() =>
        _files.FirstOrDefault(file => FileStamp.TryOf(file.Path) != file.Stamp).Path;

    /// <summary>
    /// The libraries whose files are gone now, which the host built in place of this one looks
    /// for again (<see cref="AssemblyHost"/>): a build may be writing such a file anew.
    /// </summary>
    public IReadOnlyList<string> Gone() =>
        _files.Where(file => FileStamp.TryOf(file.Path) is null).Select(file => file.Path).ToList();

    public bool Equals(ReferenceStamps other) =>
        other is not null
        && _files.Length == other._files.Length
        && _files.Zip(other._files).All(pair =>
            string.Equals(pair.First.Path, pair.Second.Path, StringComparison.OrdinalIgnoreCase)
            && pair.First.Stamp == pair.Second.Stamp);

    public override bool Equals(object obj) => Equals(obj as ReferenceStamps);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (path, stamp) in _files)
        {
            hash.Add(path, StringComparer.OrdinalIgnoreCase);
            hash.Add(stamp);
        }
        return hash.ToHashCode();
    }
}
