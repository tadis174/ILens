using System.Collections.Concurrent;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for an assembly that is replaced at its path while a server has read it, as
/// a project's rebuild replaces a library in place, and for a library it references that is
/// replaced so. SearchText.dll stands for the build read first and CallShapes.dll for the
/// rebuild, copied over it as a build copies its output, which gives the file the write time of
/// the build it copies; RebuiltReferenceAssembly writes the library, its reference and the
/// reference's rebuild. Each test starts a server of its own on a folder of its own, since the
/// replacement changes what the server holds.
/// </summary>
public sealed class RebuiltAssemblyTests
{
    // CallShapes.Gauge is a class of the rebuild alone. The first call reads the build before it.
    [Theory]
    [InlineData("find_types", "name_contains", "Gauge", "CallShapes.Gauge")]
    [InlineData("list_members", "type_name", "CallShapes.Gauge", "members of public class CallShapes.Gauge")]
    [InlineData("decompile", "type_name", "CallShapes.Gauge", "public class Gauge")]
    public async Task AfterTheFileWasReplaced_TheSameServer_AnswersFromTheNewBuild(
        string tool, string argument, string value, string expected)
    {
        await using var folder = await Folder.Start();
        Assert.Contains("SearchText.Reader", await folder.Ask("find_types"));

        folder.Rebuild();

        Assert.Contains(expected, await folder.Ask(tool, new() { [argument] = value }));
    }

    // The server logs each time it reads an assembly and each time it closes one. A second call
    // on a file that has not changed answers from the host that read it; after the file was
    // replaced, that host is closed and the file read again. The log arrives apart from the
    // answers, so a call on another file after each step marks where the step's lines end, and
    // the lines name a file by its name alone (ILensServerFixture.HostLines).
    [Fact]
    public async Task AFileThatDidNotChange_IsReadOnce_AndAReplacedOne_IsReadAgain()
    {
        var log = new ConcurrentQueue<string>();
        await using var folder = await Folder.Start(log.Enqueue);
        string marker = folder.Copy("SearchText.dll", "Marker.dll");
        string lastMarker = folder.Copy("SearchText.dll", "LastMarker.dll");

        await folder.Ask("find_types");
        await folder.Ask("find_types");
        await folder.Ask("find_types", assembly: marker);
        folder.Rebuild();
        await folder.Ask("find_types");
        await folder.Ask("find_types", assembly: lastMarker);

        string[] expected =
        [
            "loaded Target.dll",
            "loaded Marker.dll",
            "closed Target.dll (its file changed)",
            "loaded Target.dll",
            "loaded LastMarker.dll",
        ];
        Assert.Equal(expected, await ILensServerFixture.HostLines(log, until: "loaded LastMarker.dll"));
    }

    // Shop.Derived derives from Parts.Base, which Parts.dll beside Shop.dll defines and the server
    // reads as Shop.dll's reference. A build copies its rebuild over Parts.dll while the server
    // holds Shop.dll, and the rebuild names Base's method Latest where the build before named it
    // Before (RebuiltReferenceAssembly). The copy fails where the server keeps Parts.dll open.
    [Fact]
    public async Task AfterALibraryItReferencesWasReplaced_TheSameServer_AnswersFromTheNewBuild()
    {
        await using var folder = await Folder.Start();
        string shop = RebuiltReferenceAssembly.Write(folder.Root);
        var inherited = new Dictionary<string, object?> { ["type_name"] = "Shop.Derived", ["include_inherited"] = true };
        Assert.Contains("Before()", await folder.Ask("list_members", inherited, shop));

        File.Copy(RebuiltReferenceAssembly.WriteRebuild(folder.Root), Path.Combine(folder.Root, "Parts.dll"), overwrite: true);

        string answer = await folder.Ask("list_members", inherited, shop);
        Assert.Contains("Latest()", answer);
        Assert.DoesNotContain("Before()", answer);
    }

    // As for the assembly's own file: a second call while Parts.dll is as the host read it answers
    // from that host, which read Parts.dll once, and after the rebuild was copied over Parts.dll,
    // the host of Shop.dll is closed with the reason and both read again, although Shop.dll did
    // not change.
    [Fact]
    public async Task ALibraryWhoseReferenceDidNotChange_IsReadOnce_AndOneWhoseReferenceWasReplaced_IsReadAgain()
    {
        var log = new ConcurrentQueue<string>();
        await using var folder = await Folder.Start(log.Enqueue);
        string shop = RebuiltReferenceAssembly.Write(folder.Root);
        string marker = folder.Copy("SearchText.dll", "Marker.dll");
        string lastMarker = folder.Copy("SearchText.dll", "LastMarker.dll");

        await folder.Ask("find_types", assembly: shop);
        await folder.Ask("find_types", assembly: shop);
        await folder.Ask("find_types", assembly: marker);
        File.Copy(RebuiltReferenceAssembly.WriteRebuild(folder.Root), Path.Combine(folder.Root, "Parts.dll"), overwrite: true);
        await folder.Ask("find_types", assembly: shop);
        await folder.Ask("find_types", assembly: lastMarker);

        string[] expected =
        [
            "read reference Parts.dll",
            "loaded Shop.dll",
            "loaded Marker.dll",
            "closed Shop.dll (its reference Parts.dll changed)",
            "read reference Parts.dll",
            "loaded Shop.dll",
            "loaded LastMarker.dll",
        ];
        Assert.Equal(expected, await ILensServerFixture.HostLines(log, until: "loaded LastMarker.dll"));
    }

    // A build that deletes Parts.dll before it writes the rebuild: a call between the two gets a
    // host built without Parts.dll, whose answer lists none of Base's members, and once the
    // rebuild is there, the next call answers from it.
    [Fact]
    public async Task AReferenceThatWasGoneForAMoment_IsReadOnceItIsBack()
    {
        await using var folder = await Folder.Start();
        string shop = RebuiltReferenceAssembly.Write(folder.Root);
        string parts = Path.Combine(folder.Root, "Parts.dll");
        var inherited = new Dictionary<string, object?> { ["type_name"] = "Shop.Derived", ["include_inherited"] = true };
        Assert.Contains("Before()", await folder.Ask("list_members", inherited, shop));

        File.Delete(parts);
        Assert.DoesNotContain("Before()", await folder.Ask("list_members", inherited, shop));
        File.Copy(RebuiltReferenceAssembly.WriteRebuild(folder.Root), parts);

        Assert.Contains("Latest()", await folder.Ask("list_members", inherited, shop));
    }

    // A build that still holds Parts.dll, here open for writing without sharing, when the server
    // reads Shop.dll: the server cannot read Parts.dll and answers without Base's members, and
    // once the build has let go of the file, the next call reads it, although its length and
    // write time are what they were.
    [Fact]
    public async Task AReferenceABuildHeld_IsReadAtTheNextCall()
    {
        await using var folder = await Folder.Start();
        string shop = RebuiltReferenceAssembly.Write(folder.Root);
        var inherited = new Dictionary<string, object?> { ["type_name"] = "Shop.Derived", ["include_inherited"] = true };
        using (new FileStream(Path.Combine(folder.Root, "Parts.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.DoesNotContain("Before()", await folder.Ask("list_members", inherited, shop));

        Assert.Contains("Before()", await folder.Ask("list_members", inherited, shop));
    }

    /// <summary>A server on a folder of its own, whose Target.dll is the build read first.</summary>
    private sealed class Folder : IAsyncDisposable
    {
        private readonly string _root;

        private Folder(string root, McpClient client)
        {
            _root = root;
            Client = client;
        }

        public McpClient Client { get; }

        /// <summary>The folder, the server's one allowed root.</summary>
        public string Root => _root;

        public string Target => Path.Combine(_root, "Target.dll");

        /// <summary>
        /// Starts the server, passing each line it writes to its error output to
        /// <paramref name="errorLines"/> where one is given.
        /// </summary>
        public static async Task<Folder> Start(Action<string>? errorLines = null)
        {
            string root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "ILens-rebuilt-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                File.Copy(Fixture("SearchText.dll"), Path.Combine(root, "Target.dll"));
                var client = await ILensServerFixture.ConnectAsync(new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "ILens-rebuilt",
                    Command = ILensServerFixture.ResolveILensExecutable(),
                    Arguments = ["--allow-root", root],
                    StandardErrorLines = errorLines,
                }));
                return new Folder(root, client);
            }
            catch
            {
                await ILensServerFixture.DeleteWhenReleased(root);
                throw;
            }
        }

        /// <summary>Copies the rebuild over Target.dll.</summary>
        public void Rebuild() => File.Copy(Fixture("CallShapes.dll"), Target, overwrite: true);

        /// <summary>Copies the fixture <paramref name="fixture"/> into the folder as <paramref name="name"/>.</summary>
        public string Copy(string fixture, string name)
        {
            string copy = Path.Combine(_root, name);
            File.Copy(Fixture(fixture), copy);
            return copy;
        }

        /// <summary>The text of a tool's answer about <paramref name="assembly"/>, Target.dll where none is given.</summary>
        public async Task<string> Ask(string tool, Dictionary<string, object?>? arguments = null, string? assembly = null)
        {
            var all = arguments is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(arguments);
            all["assembly"] = assembly ?? Target;
            CallToolResult result = await Client.CallToolAsync(tool, all);
            return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await ILensServerFixture.DeleteWhenReleased(_root);
        }

        /// <summary>The path of a fixture assembly, which Tests.csproj's ProjectReference puts in the test output.</summary>
        private static string Fixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, name);
            return File.Exists(path) ? path : throw new FileNotFoundException($"Fixture not found: {path}.");
        }
    }
}
