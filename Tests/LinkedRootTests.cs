using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for an allow-root given through a link, such as a folder moved to another
/// drive with a junction left at its old place. Each test builds a folder of its own under the
/// temp folder, with copies of the fixtures it reads and the links it needs, and starts a server
/// on it. A junction needs no privilege, so the tests that use one always run.
/// </summary>
public sealed class LinkedRootTests
{
    private const string Assembly = "CallShapes.dll";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARootGivenThroughALink_ReadsAFileUnderItByEitherSpelling(bool symbolic)
    {
        await using var scratch = new Scratch();
        string real = scratch.Folder("Real", Assembly);
        string link = Path.Combine(scratch.Root, "Link");
        if (symbolic)
        {
            // Creating a symbolic link on Windows requires admin or Developer Mode; the test
            // passes silently where it cannot. The junction's row always runs.
            try { Directory.CreateSymbolicLink(link, real); }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }
        }
        else
        {
            CreateJunction(link, real);
        }

        await using McpClient client = await StartServer(link);

        foreach (string folder in new[] { link, real })
        {
            var (isError, text) = await FindVault(client, Path.Combine(folder, Assembly));
            Assert.False(isError, text);
            Assert.Contains("CallShapes.Vault", text.Split('\n'));
        }

        // The server names the root as it was given.
        var (_, roots) = await Call(client, "list_allowed_roots", new());
        Assert.Equal(link, roots);
    }

    [Fact]
    public async Task AReferencesRootGivenThroughAJunction_IsSearchedForReferences()
    {
        // An enum value in an attribute decodes only with the enum's assembly, which sits in
        // the linked root alone.
        await using var scratch = new Scratch();
        string users = scratch.Folder("Users", "ExternalUser.dll");
        string references = scratch.Folder("References", Path.Combine("References", "ExternalLib.dll"));
        string link = Path.Combine(scratch.Root, "Link");
        CreateJunction(link, references);

        await using McpClient client = await StartServer(users, link);

        var (isError, text) = await Call(client, "decompile", new()
        {
            ["assembly"] = Path.Combine(users, "ExternalUser.dll"),
            ["type_name"] = "ExternalUser.Consumer",
            ["member_name"] = "Shaded",
        });
        Assert.False(isError, text);
        Assert.Contains("[Mark(Shade.Dark)]", text);
    }

    [Fact]
    public async Task ALinkInsideALinkedRoot_ThatLeadsOutOfEveryRoot_IsRefused_AndTheRefusalNamesBothSpellings()
    {
        await using var scratch = new Scratch();
        string real = scratch.Folder("Real", Assembly);
        string outside = scratch.Folder("Outside", Assembly);
        string link = Path.Combine(scratch.Root, "Link");
        CreateJunction(link, real);
        CreateJunction(Path.Combine(real, "Escape"), outside);

        await using McpClient client = await StartServer(link);

        foreach (string folder in new[] { link, real })
        {
            var (isError, text) = await FindVault(client, Path.Combine(folder, "Escape", Assembly));
            Assert.True(isError, $"Expected a refusal, got: {text}");
            Assert.Contains("Path resolves (via a symbolic link or junction) to a location outside any allowed root: ", text);
            Assert.Contains($"Allowed roots: {link} (resolved: ", text);
            Assert.EndsWith(scratch.Tail("Real") + ").", text);
        }
    }

    [Fact]
    public async Task ARequestThroughALinkOutsideTheRoots_IsRefused_ThoughTheLinkLeadsIntoARoot()
    {
        // The root is given by its real path, and the link to it lies outside every root. The
        // path is refused as it is written, before anything is read.
        await using var scratch = new Scratch();
        string real = scratch.Folder("Real", Assembly);
        string link = Path.Combine(scratch.Root, "Link");
        CreateJunction(link, real);

        await using McpClient client = await StartServer(real);

        string throughLink = Path.Combine(link, Assembly);
        var (isError, text) = await FindVault(client, throughLink);
        Assert.True(isError, $"Expected a refusal, got: {text}");
        Assert.StartsWith(
            $"Error: ArgumentException: Path is not under any allowed root: {throughLink}. Allowed roots: {real}",
            text);
    }

    [Fact]
    public async Task ALinkMovedWhileTheServerRuns_IsRefused_AndTheFolderItPointedToStaysAllowed()
    {
        await using var scratch = new Scratch();
        string first = scratch.Folder("First", Assembly);
        string second = scratch.Folder("Second", Assembly);
        string link = Path.Combine(scratch.Root, "Link");
        CreateJunction(link, first);

        await using McpClient client = await StartServer(link);

        string throughLink = Path.Combine(link, Assembly);
        var before = await FindVault(client, throughLink);
        Assert.False(before.IsError, before.Text);

        // Deleting a junction without recursion removes the link and leaves its target.
        Directory.Delete(link);
        CreateJunction(link, second);

        var after = await FindVault(client, throughLink);
        Assert.True(after.IsError, $"Expected a refusal, got: {after.Text}");
        Assert.Contains("outside any allowed root: ", after.Text);
        Assert.Contains(scratch.Tail("Second", Assembly) + ". Allowed roots: ", after.Text);
        Assert.Contains($"Allowed roots: {link} (resolved: ", after.Text);
        Assert.EndsWith(scratch.Tail("First") + ").", after.Text);

        var byRealPath = await FindVault(client, Path.Combine(first, Assembly));
        Assert.False(byRealPath.IsError, byRealPath.Text);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ARootWhoseLinksCannotBeRead_EndsTheServerAtStartup_WithALineNamingIt()
    {
        // Whether a folder is a link is read in the folder above it, so a root below a folder
        // that may not be listed cannot be resolved.
        await using var scratch = new Scratch();
        string root = scratch.Folder(Path.Combine("Closed", "Root"));
        var closed = new DirectoryInfo(Path.Combine(scratch.Root, "Closed"));
        var denyListing = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        DirectorySecurity security = closed.GetAccessControl();
        security.AddAccessRule(denyListing);
        closed.SetAccessControl(security);

        try
        {
            var start = new ProcessStartInfo(ILensServerFixture.ResolveILensExecutable())
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("--allow-root");
            start.ArgumentList.Add(root);

            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            string errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(1, process.ExitCode);
            Assert.Contains(
                $"[ILens] Path could not be resolved through its symbolic links or junctions: {root}.", errors);
        }
        finally
        {
            security.RemoveAccessRule(denyListing);
            closed.SetAccessControl(security);
        }
    }

    /// <summary>
    /// A test's folder under the temp folder, deleted with the test. Every link a test makes
    /// lies in it and points into it, so deleting it touches nothing else.
    /// </summary>
    private sealed class Scratch : IAsyncDisposable
    {
        private readonly string _name = "ILens-linked-" + Guid.NewGuid().ToString("N");

        public Scratch() => Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), _name)).FullName;

        public string Root { get; }

        /// <summary>A new folder holding a copy of each named file of the test output.</summary>
        public string Folder(string name, params string[] files)
        {
            string folder = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            foreach (string file in files)
                File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(folder, Path.GetFileName(file)));
            return folder;
        }

        /// <summary>
        /// The end of a path below this folder, from the folder's own name on. A resolved path
        /// ends with it whatever links the temp folder's own path holds.
        /// </summary>
        public string Tail(params string[] parts) => Path.Combine([_name, .. parts]);

        public async ValueTask DisposeAsync() => await ILensServerFixture.DeleteWhenReleased(Root);
    }

    /// <summary>
    /// Makes a junction with cmd's mklink, since .NET creates symbolic links only, which need a
    /// privilege a junction does not.
    /// </summary>
    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new IOException($"mklink /J {link} {target} failed: {output}");
    }

    private static Task<McpClient> StartServer(params string[] roots) =>
        McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ILens-linked",
            Command = ILensServerFixture.ResolveILensExecutable(),
            Arguments = [.. roots.SelectMany(root => new[] { "--allow-root", root })],
        }));

    /// <summary>find_types for the types with Vault in their name, the fixture's Vault class among them.</summary>
    private static Task<(bool IsError, string Text)> FindVault(McpClient client, string assembly) =>
        Call(client, "find_types", new() { ["assembly"] = assembly, ["name_contains"] = "Vault" });

    private static async Task<(bool IsError, string Text)> Call(
        McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(tool, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return (result.IsError == true, text);
    }
}
