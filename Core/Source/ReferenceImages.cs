using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.Metadata;

namespace ILens;

/// <summary>
/// The libraries hosts read as references from under an allowed root. Each is read whole into
/// memory with its file closed at once, so that a build can replace the file while a host holds
/// the library, and read once for each version of its file: every host that reads that version
/// shares it, and it is kept until the last of them lets go of it.
/// </summary>
/// <remarks>
/// A library opened as the decompiler's own reference search opens one stays open for lazy reads
/// until its host is closed, and Windows refuses to write over a file open so: a project could
/// not rebuild a library while a host of a library that references it lived (AssemblyHost's
/// ReferenceResolver). Read whole, a library costs the process's private memory what its file
/// measures, where a mapped one costs it nothing, and references are large beside the libraries
/// that name them: of 472 libraries of game mods, the libraries their references name beside
/// them or in the game's folder measured 21.8 MB at the median against a median own file of about
/// 190 KB (measured 2026-11-14). Read for each host, they would leave room under the memory
/// budget for about a fifth of the hosts it keeps open with mapped references; shared, the
/// mods of one game read the game's libraries once.
///
/// A library outside every allowed root, among the installed .NET libraries, in the Global
/// Assembly Cache, among the Windows metadata files or in a NuGet package folder, is not held
/// here: no project's build writes over it, and reading it whole would add a library such as
/// System.Private.CoreLib to the process's memory.
/// </remarks>
public sealed class ReferenceImages
{
    private readonly object _lock = new();  // guards _byPath and the holds of every Image
    private readonly Dictionary<string, Image> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A hold on the library at <paramref name="path"/> as its file is now, or null where there is
    /// no file there. A version another hold keeps is shared where the file still has its stamp;
    /// otherwise the file is read, and a former version stays in memory only until its last hold
    /// is released. The stamp is read before the file, so that a hold's stamp is never newer than
    /// the library it holds, and a file that changes while it is read shows another stamp at the
    /// next call. A file that cannot be read or parsed leaves the hold's library null, as the
    /// decompiler's own reference search leaves such a reference unresolved; the hold carries the
    /// file's stamp where that lasts as long as the file, and none where the reading failed with
    /// an I/O error, such as a build holding the file, so that the next call reads it again.
    /// </summary>
    public Hold Take(string path)
    {
        lock (_lock)
        {
            if (FileStamp.TryOf(path) is not { } stamp)
                return null;
            if (!_byPath.TryGetValue(path, out var image) || image.Stamp != stamp)
            {
                var library = Read(path, out bool passing);
                if (library is null)
                    return new Hold(this, path, passing ? null : stamp, null);
                _byPath[path] = image = new Image(path, stamp, library);
                Console.Error.WriteLine($"[ILens] read reference {path} ({stamp.Length} bytes)");
            }
            image.Holds++;
            return new Hold(this, path, stamp, image);
        }
    }

    /// <summary>
    /// The library at <paramref name="path"/>, read whole into memory with its file closed at once,
    /// or null where the file cannot be read or is no portable executable, which the decompiler's
    /// own reference search also leaves unresolved; <paramref name="passing"/> says whether the
    /// failure was an I/O error, which can pass while the file stays as it is. A file without
    /// .NET metadata fails, as that search lets it fail.
    /// </summary>
    /// <remarks>
    /// A PEReader built with PEStreamOptions.PrefetchEntireImage reads the image into memory of
    /// its own and, as PEStreamOptions documents, disposes the stream before its constructor
    /// returns. AfterALibraryItReferencesWasReplaced_TheSameServer_AnswersFromTheNewBuild in
    /// RebuiltAssemblyTests copies a build over such a library, which fails where the file stays
    /// open.
    /// </remarks>
    private static PEFile Read(string path, out bool passing)
    {
        passing = false;
        PEReader reader = null;
        try
        {
            reader = new PEReader(new FileStream(path, FileMode.Open, FileAccess.Read), PEStreamOptions.PrefetchEntireImage);
            return new PEFile(path, reader, MetadataReaderOptions.Default);
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            reader?.Dispose();
            passing = e is IOException;
            return null;
        }
        catch
        {
            reader?.Dispose();
            throw;
        }
    }

    private void Release(Image image)
    {
        lock (_lock)
        {
            if (--image.Holds > 0)
                return;
            if (_byPath.TryGetValue(image.Path, out var kept) && kept == image)
                _byPath.Remove(image.Path);
        }
        image.Library.Dispose();
    }

    /// <summary>
    /// A host's hold on a library, or a search pass's. The library stays in memory while any hold
    /// on it is held. Disposing twice releases once.
    /// </summary>
    public sealed class Hold : IDisposable
    {
        private readonly ReferenceImages _images;
        private readonly Image _image;
        private int _released;

        internal Hold(ReferenceImages images, string path, FileStamp? stamp, Image image)
        {
            _images = images;
            _image = image;
            Path = path;
            Stamp = stamp;
        }

        /// <summary>The library's file, as the reference search found it.</summary>
        public string Path { get; }

        /// <summary>
        /// The stamp the file had before it was read, or null where its reading failed with an
        /// I/O error, which the next call is to try again (<see cref="ReferenceStamps"/>).
        /// </summary>
        public FileStamp? Stamp { get; }

        /// <summary>The library, or null where its file could not be read or parsed.</summary>
        public MetadataFile Library => _image?.Library;

        public void Dispose()
        {
            if (_image is not null && Interlocked.Exchange(ref _released, 1) == 0)
                _images.Release(_image);
        }
    }

    internal sealed class Image(string path, FileStamp stamp, PEFile library)
    {
        public string Path => path;
        public FileStamp Stamp => stamp;
        public PEFile Library => library;
        public int Holds { get; set; }  // guarded by the store's _lock
    }
}
