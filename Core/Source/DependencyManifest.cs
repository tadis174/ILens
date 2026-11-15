using System.Text.Json;

namespace ILens;

/// <summary>
/// The package folders that the dependency manifest beside an assembly, its <c>.deps.json</c>,
/// names for the assembly's references, kept only where they lie under a NuGet package root: the
/// folder the NUGET_PACKAGES variable names and the user's <c>.nuget\packages</c>.
/// </summary>
/// <remarks>
/// <para>
/// The SDK writes a manifest beside a build for .NET Core or .NET Standard. It names each package
/// by its name and version and the package's files by their paths inside the package, so every
/// folder it leads to lies under a package root, where a library's build output finds the
/// packages that are not copied beside it. A folder elsewhere comes from a file planted beside
/// the assembly, through an absolute path, <c>..</c> steps or a share on another computer, and
/// asking whether such a folder exists can make Windows connect to that computer. The reference
/// search is therefore built without the assembly's file name, which keeps the decompiler from
/// reading the manifest, and is given the folders kept here instead.
/// </para>
/// <para>
/// Library behavior, read in ICSharpCode.Decompiler 11.2.0.10050: the constructor of
/// DotNetCorePathFinder that takes the assembly's file name reads <c>&lt;name&gt;.deps.json</c>
/// beside it (LoadPackageInfos) for a target that UniversalAssemblyResolver.FindAssemblyFileCore
/// reads as .NET Core or .NET Standard with a version. For each lookup path, each library the
/// manifest lists and each file the library's entry under the assembly's target lists as runtime,
/// it takes Path.Combine(lookup path, package name, version, folder of the file), lowercased, and
/// adds it, where it exists, to the folders TryResolveDotNetCore searches after its search
/// directories, wherever it lies. The cases: ReferenceBoundsTests.
/// </para>
/// <para>
/// Library behavior, read in ICSharpCode.ILSpyX 11.2.0.10050: LoadedAssembly.GetTargetFrameworkIdAsync
/// answers what DetectTargetFrameworkId detects where no override is set, and ILens sets none, so
/// ILSpyX's own search reads the manifest for the same targets as the decompiler's. The case:
/// ReferenceBoundsTests, with an assembly built for the .NET Framework.
/// </para>
/// </remarks>
internal sealed class DependencyManifest
{
    // A key of the manifest's libraries without a version, as DotNetCorePathFinder names it.
    private const string UnknownVersion = "<UNKNOWN>";

    private DependencyManifest(IReadOnlyList<string> packageFolders, string leadsOut)
    {
        PackageFolders = packageFolders;
        LeadsOut = leadsOut;
    }

    /// <summary>What an assembly without a manifest beside it has.</summary>
    public static DependencyManifest None { get; } = new([], null);

    /// <summary>
    /// The folders to search for the assembly's references after the allow-roots, in the order the
    /// decompiler would search them.
    /// </summary>
    public IReadOnlyList<string> PackageFolders { get; }

    /// <summary>
    /// Why the manifest leads out of the package roots, or null where it does not: it names a
    /// folder elsewhere under one of its targets, or it is a link, is not valid JSON or cannot be
    /// read, so that what it names could not be checked. The decompiler's search reads none of it,
    /// but ILSpyX's analyzers search on their own and read it whole.
    /// </summary>
    public string LeadsOut { get; }

    /// <summary>
    /// The manifest beside <paramref name="assemblyPath"/>, for an assembly whose target is
    /// <paramref name="targetFramework"/> as DetectTargetFrameworkId writes it, which ILSpyX takes
    /// for the target as well. For a target whose references the decompiler does not look for in
    /// a manifest, ILSpyX's analyzers do not either, and the manifest is not read.
    /// </summary>
    /// <exception cref="IOException">
    /// The manifest could not be opened, as while a build writes it. The host is then not built,
    /// and a later call reads the manifest again, as the decompiler's own reading of it did.
    /// </exception>
    public static DependencyManifest Beside(string assemblyPath, string targetFramework)
    {
        if (!SearchesManifest(targetFramework))
            return None;
        var file = Path.Combine(Path.GetDirectoryName(assemblyPath)!,
            Path.GetFileNameWithoutExtension(assemblyPath) + ".deps.json");
        if (!File.Exists(file))
            return None;
        var name = Path.GetFileName(file);

        // A manifest that is a link is not followed, since opening it would follow the link
        // wherever it leads, a share on another computer included; the SDK writes none as a link.
        // The attributes read here are the link's own.
        if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            return new([], $"its dependency manifest, {name}, is a link, which ILens does not follow");

        JsonDocument document;
        try
        {
            // File.ReadAllText reads the manifest as the decompiler read it, telling UTF-8, UTF-16
            // and UTF-32 apart by a byte-order mark.
            document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return new([], $"its dependency manifest, {name}, is not valid JSON");
        }
        catch (UnauthorizedAccessException)
        {
            return new([], $"its dependency manifest, {name}, cannot be read");
        }

        using (document)
        {
            var lookupPaths = LookupPaths().ToList();
            var root = document.RootElement;

            // Every target of the manifest counts here, not only the assembly's, so that the check
            // does not rest on which of them a search takes: one the SDK wrote leads out under none.
            var leadsOut = EveryEntry(root).Any(entry => lookupPaths.Any(lookup =>
                Folder(lookup, entry.Library, entry.File) is not { } folder || !LiesUnder(folder, lookup)))
                ? $"its dependency manifest, {name}, names a folder outside the NuGet package folders"
                : null;

            var folders = new List<string>();
            var seen = new HashSet<string>();
            foreach (var lookup in lookupPaths)
                foreach (var (library, runtimeFile) in SearchedEntries(root, targetFramework))
                    if (Folder(lookup, library, runtimeFile) is { } folder && seen.Add(folder)
                        && LiesUnder(folder, lookup) && Directory.Exists(folder))
                        folders.Add(folder);
            return new(folders, leadsOut);
        }
    }

    /// <summary>The package roots, as DotNetCorePathFinder takes them: the variable's first.</summary>
    private static IEnumerable<string> LookupPaths()
    {
        var variable = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(variable))
            yield return variable;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    /// <summary>
    /// Whether the decompiler looks for the references of an assembly with this target in its
    /// manifest: one it reads as .NET Core or .NET Standard with a version, as
    /// UniversalAssemblyResolver.ParseTargetFramework reads it.
    /// </summary>
    private static bool SearchesManifest(string targetFramework)
    {
        var parts = (targetFramework ?? "").Split(',');
        var identifier = parts[0].Trim();
        if (!identifier.Equals(".NETCoreApp", StringComparison.OrdinalIgnoreCase)
            && !identifier.Equals(".NETStandard", StringComparison.OrdinalIgnoreCase))
            return false;
        Version version = null;
        foreach (var part in parts.Skip(1))
        {
            var pair = part.Trim().Split('=');
            if (pair.Length == 2 && pair[0].Trim().Equals("Version", StringComparison.OrdinalIgnoreCase))
                version = Version.TryParse(pair[1].TrimStart('v', 'V', ' ', '\t'), out var parsed) ? parsed : null;
        }
        return version is not null;
    }

    /// <summary>
    /// The libraries and their runtime files the decompiler takes from the manifest: each library
    /// under <c>libraries</c>, in order, with the files its entry under the assembly's target lists.
    /// </summary>
    private static IEnumerable<(string Library, string File)> SearchedEntries(JsonElement root, string targetFramework)
    {
        if (!(Child(root, "targets") is { } targets && Child(targets, targetFramework) is { } target
            && Child(root, "libraries") is { } libraries))
            yield break;
        foreach (var library in libraries.EnumerateObject())
            if (Child(target, library.Name) is { } entry && Child(entry, "runtime") is { } runtime)
                foreach (var runtimeFile in runtime.EnumerateObject())
                    yield return (library.Name, runtimeFile.Name);
    }

    /// <summary>Every library and runtime file under every target of the manifest.</summary>
    private static IEnumerable<(string Library, string File)> EveryEntry(JsonElement root)
    {
        if (Child(root, "targets") is not { } targets)
            yield break;
        foreach (var target in targets.EnumerateObject())
            if (target.Value.ValueKind == JsonValueKind.Object)
                foreach (var entry in target.Value.EnumerateObject())
                    if (Child(entry.Value, "runtime") is { } runtime)
                        foreach (var runtimeFile in runtime.EnumerateObject())
                            yield return (entry.Name, runtimeFile.Name);
    }

    /// <summary>The object <paramref name="name"/> holds in <paramref name="element"/>, or null.</summary>
    private static JsonElement? Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child)
            && child.ValueKind == JsonValueKind.Object
            ? child
            : null;

    /// <summary>
    /// The folder the decompiler takes for <paramref name="runtimeFile"/> of the library keyed
    /// <paramref name="library"/>, <c>name/version</c>, under <paramref name="lookup"/>, or null
    /// where it could not take one: a file without a folder part, such as a root, ends its search
    /// with an exception.
    /// </summary>
    private static string Folder(string lookup, string library, string runtimeFile)
    {
        var parts = library.Split('/');
        try
        {
            return Path.Combine(lookup, parts[0], parts.Length > 1 ? parts[1] : UnknownVersion,
                Path.GetDirectoryName(runtimeFile)!).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="folder"/> lies in <paramref name="root"/> or below it, read from the
    /// paths alone, as Windows normalizes them, without asking the file system.
    /// </summary>
    private static bool LiesUnder(string folder, string root)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Equals(top, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
