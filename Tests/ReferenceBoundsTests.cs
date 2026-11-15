using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the bounds of the reference search: what a file in an allowed folder
/// names, a dependency manifest beside an assembly or the name the assembly gives a reference,
/// leads ILens neither to an assembly outside every allowed folder and outside the NuGet package
/// folders nor to a share on another computer, which Windows would connect to. Each test lays out
/// a folder of its own: the allow-root Users, which holds the assemblies asked about, written by
/// <see cref="WidgetUserAssembly"/>; Outside beside it, which is no root and holds ExternalLib;
/// and Packages, which the server takes for its NuGet package folder through NUGET_PACKAGES, so
/// that no test depends on the packages of the machine it runs on.
/// </summary>
public sealed class ReferenceBoundsTests
{
    private const string Gadget = "WidgetUser.Gadget";

    // A member of ExternalLib.Widget, which list_members lists among Gadget's where ILens read ExternalLib.
    private const string InheritedMember = "public virtual void Draw()  [from ExternalLib.Widget]";

    private const string ManifestLeadsOut = "names a folder outside the NuGet package folders";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AManifestNamingAFolderOutsideThePackageFolders_DoesNotLeadThere(bool throughSteps)
    {
        // The folder is named by its absolute path, or by .. steps from the package folder, which
        // the decompiler joins with the package's name and version.
        await using var layout = new Layout();
        string assembly = WidgetUserAssembly.Write(layout.Users, "Read.dll");
        if (throughSteps)
            WriteManifest(assembly, "../Outside", "ExternalLib.dll");
        else
            WriteManifest(assembly, "Planted/1.0.0", Path.Combine(layout.Outside, "ExternalLib.dll"));
        await using var client = await layout.StartServer();

        string members = await ListMembers(client, assembly);

        Assert.DoesNotContain(InheritedMember, members);
        Assert.EndsWith(NotRead("ExternalLib"), members);
    }

    // A manifest saved as UTF-16, as Windows PowerShell 5.1's Out-File saves one, is read as the
    // decompiler reads it, by its byte-order mark.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AManifestNamingAPackage_FindsItInThePackageFolder(bool utf16)
    {
        await using var layout = new Layout();
        string assembly = WidgetUserAssembly.Write(layout.Users, "Packaged.dll");
        layout.WritePackage();
        WriteManifest(assembly, "ExternalLib/1.0.0", "lib/netstandard2.0/ExternalLib.dll", utf16 ? Encoding.Unicode : null);
        await using var client = await layout.StartServer();

        Assert.Contains(InheritedMember, await ListMembers(client, assembly));
        Assert.Equal($"No symbols instantiating {Gadget}", await Analyze(client, assembly, Gadget, "instantiated_by"));
    }

    // Neither the decompiler nor ILSpyX's analyzers read a manifest for an assembly built for the
    // .NET Framework, so one that leads out does not keep an analysis from running.
    [Fact]
    public async Task AManifestBesideAnAssemblyForTheNetFramework_IsNotRead()
    {
        await using var layout = new Layout();
        string assembly = WidgetUserAssembly.Write(layout.Users, "Framework.dll", targetFramework: ".NETFramework,Version=v4.8");
        WriteManifest(assembly, "Planted/1.0.0", Path.Combine(layout.Outside, "ExternalLib.dll"));
        await using var client = await layout.StartServer();

        Assert.EndsWith(NotRead("ExternalLib"), await ListMembers(client, assembly));
        Assert.Equal($"No symbols instantiating {Gadget}", await Analyze(client, assembly, Gadget, "instantiated_by"));
    }

    // A manifest another program holds open, as a build that writes it does, fails the call, and a
    // later call reads it again, rather than an assembly answered without its packages until the
    // server closes it.
    [Fact]
    public async Task AManifestAnotherProgramHolds_FailsTheCallUntilItIsReleased()
    {
        await using var layout = new Layout();
        string assembly = WidgetUserAssembly.Write(layout.Users, "Held.dll");
        layout.WritePackage();
        WriteManifest(assembly, "ExternalLib/1.0.0", "lib/netstandard2.0/ExternalLib.dll");
        await using var client = await layout.StartServer();

        string whileHeld;
        using (File.Open(Path.ChangeExtension(assembly, ".deps.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            whileHeld = await ListMembers(client, assembly);

        Assert.StartsWith("Error: IOException: ", whileHeld);
        Assert.Contains(InheritedMember, await ListMembers(client, assembly));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReferenceNamedByAPath_IsNotLookedUp(bool absolute)
    {
        await using var layout = new Layout();
        string reference = absolute ? Path.Combine(layout.Outside, "ExternalLib") : @"..\Outside\ExternalLib";
        string assembly = WidgetUserAssembly.Write(layout.Users, "Named.dll", reference);
        await using var client = await layout.StartServer();

        string members = await ListMembers(client, assembly);

        Assert.DoesNotContain(InheritedMember, members);
        Assert.EndsWith(NotRead(reference), members);
    }

    // Instantiated By, Exposed By and Applied To are answered by ILSpyX's analyzers, whose reference
    // search of their own reads the manifest and looks up each name the assembly gives a reference.
    [Fact]
    public async Task AnAnalysisWhoseSearchWouldFollowWhatTheAssemblyNames_IsAnError()
    {
        await using var layout = new Layout();
        string manifested = WidgetUserAssembly.Write(layout.Users, "Read.dll");
        WriteManifest(manifested, "Planted/1.0.0", Path.Combine(layout.Outside, "ExternalLib.dll"));
        string named = WidgetUserAssembly.Write(layout.Users, "Named.dll", @"..\Outside\ExternalLib");
        await using var client = await layout.StartServer();

        Assert.Equal(
            NotRun("Read.dll", $"its dependency manifest, Read.deps.json, {ManifestLeadsOut}"),
            await Analyze(client, manifested, Gadget, "instantiated_by"));
        Assert.Equal(
            NotRun("Named.dll", @"it names a reference ..\Outside\ExternalLib, which is not a file name"),
            await Analyze(client, named, Gadget, "exposed_by"));
    }

    [Fact]
    public async Task AManifestThatIsNotJson_LeavesTheAssemblyReadable()
    {
        await using var layout = new Layout();
        string assembly = WidgetUserAssembly.Write(layout.Users, "Broken.dll");
        File.WriteAllText(Path.ChangeExtension(assembly, ".deps.json"), "{ \"targets\": {");
        await using var client = await layout.StartServer();

        Assert.EndsWith(NotRead("ExternalLib"), await ListMembers(client, assembly));
        Assert.Equal(
            NotRun("Broken.dll", "its dependency manifest, Broken.deps.json, is not valid JSON"),
            await Analyze(client, assembly, Gadget, "instantiated_by"));
    }

    // An assembly whose manifest names a share on another computer and one whose reference names
    // one are asked every tool, and no connection to either computer shows among the machine's TCP
    // connections while they answer. 192.0.2.0/24 is a block for documentation that is never
    // routed, so no connection could be made even where one is tried; a lookup shows as a
    // connection to port 445 waiting to be accepted for about 21 seconds. The test first looks up a
    // share of a third computer itself, which shows that the watch sees such a lookup here; on a
    // machine that makes no connection for it, the test ends without deciding. Windows fails a
    // lookup of a computer whose lookup just failed at once, so each run takes addresses of its own.
    [Fact]
    public async Task NoToolCall_ConnectsToAComputerTheAssemblyNames()
    {
        int host = Random.Shared.Next(10, 248);
        IPAddress[] computers = [IPAddress.Parse($"192.0.2.{host}"), IPAddress.Parse($"192.0.2.{host + 1}")];
        var control = IPAddress.Parse($"192.0.2.{host + 2}");
        await using var layout = new Layout();
        string manifested = WidgetUserAssembly.Write(layout.Users, "Shared.dll");
        WriteManifest(manifested, "Planted/1.0.0", $@"\\{computers[0]}\ilens\ExternalLib.dll");
        string named = WidgetUserAssembly.Write(layout.Users, "Remote.dll", $@"\\{computers[1]}\ilens\ExternalLib");
        await using var client = await layout.StartServer();

        await using var watch = new ConnectionWatch([.. computers, control]);
        _ = Task.Run(() => File.Exists($@"\\{control}\ilens\control.dll"));
        if (!await watch.SeesAsync(control, TimeSpan.FromSeconds(10)))
            return;
        foreach (var (assembly, other) in new[] { (manifested, named), (named, manifested) })
            foreach (var (tool, arguments) in EveryTool(assembly, other))
                await client.CallToolAsync(tool, arguments);
        var seen = await watch.StopAsync();

        Assert.DoesNotContain(seen, connection => !connection.Contains($" to {control}:"));
    }

    /// <summary>A call of every tool, and of analyze for every kind, on <paramref name="assembly"/>.</summary>
    private static IEnumerable<(string Tool, Dictionary<string, object?> Arguments)> EveryTool(string assembly, string other)
    {
        yield return ("find_types", new() { ["assembly"] = assembly });
        yield return ("find_methods", new() { ["assembly"] = assembly, ["type_name"] = Gadget });
        yield return ("list_members", new() { ["assembly"] = assembly, ["type_name"] = Gadget, ["include_inherited"] = true });
        yield return ("decompile", new() { ["assembly"] = assembly, ["type_name"] = Gadget });
        yield return ("search_code", new() { ["assembly"] = assembly, ["regex"] = "Gadget" });
        foreach (var kind in new[] { "used_by", "instantiated_by", "exposed_by", "extension_methods", "applied_to", "implemented_by" })
            yield return ("analyze", new() { ["assembly"] = assembly, ["type_name"] = Gadget, ["kind"] = kind });
        foreach (var kind in new[] { "used_by", "call_sites", "overridden_by", "uses", "implements" })
            yield return ("analyze", new() { ["assembly"] = assembly, ["type_name"] = Gadget, ["member_name"] = ".ctor", ["kind"] = kind });
        yield return ("analyze", new() { ["assembly"] = assembly, ["type_name"] = "ExternalLib.Widget", ["kind"] = "used_by" });
        foreach (var kind in new[] { "read_by", "assigned_by" })
            yield return ("analyze", new() { ["assembly"] = assembly, ["type_name"] = "ExternalLib.Widget", ["member_name"] = "Size", ["kind"] = kind });
        yield return ("compare", new() { ["assembly_a"] = assembly, ["assembly_b"] = other });
        yield return ("find_harmony_dependencies", new() { ["assembly"] = assembly });
    }

    /// <summary>The line list_members ends with where ILens did not read ExternalLib.</summary>
    private static string NotRead(string reference) =>
        $"Not shown: what ExternalLib.Widget and its base types declare; ILens could not load {reference}, Version=1.0.0.0";

    /// <summary>The error of an analysis ILSpyX's analyzers answer, on an assembly that leads out.</summary>
    private static string NotRun(string file, string reason) =>
        $"Error: InvalidOperationException: This analysis is not run on {file}: the reference search it runs on " +
        $"follows what the assembly names, and {reason}.";

    /// <summary>
    /// Write a dependency manifest beside <paramref name="assembly"/>, as the SDK writes one for
    /// .NET 10, naming the library keyed <paramref name="library"/> with one runtime file, in UTF-8
    /// without a byte-order mark unless <paramref name="encoding"/> gives another.
    /// </summary>
    private static void WriteManifest(string assembly, string library, string runtimeFile, Encoding? encoding = null)
    {
        const string Target = ".NETCoreApp,Version=v10.0";
        var manifest = new Dictionary<string, object>
        {
            ["runtimeTarget"] = new Dictionary<string, object> { ["name"] = Target },
            ["targets"] = new Dictionary<string, object>
            {
                [Target] = new Dictionary<string, object>
                {
                    [library] = new Dictionary<string, object>
                    {
                        ["runtime"] = new Dictionary<string, object> { [runtimeFile] = new Dictionary<string, object>() },
                    },
                },
            },
            ["libraries"] = new Dictionary<string, object>
            {
                [library] = new Dictionary<string, object> { ["type"] = "package", ["path"] = library.ToLowerInvariant() },
            },
        };
        File.WriteAllText(Path.ChangeExtension(assembly, ".deps.json"), JsonSerializer.Serialize(manifest),
            encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static Task<string> ListMembers(McpClient client, string assembly) =>
        Ask(client, "list_members", new() { ["assembly"] = assembly, ["type_name"] = Gadget, ["include_inherited"] = true });

    private static Task<string> Analyze(McpClient client, string assembly, string type, string kind) =>
        Ask(client, "analyze", new() { ["assembly"] = assembly, ["type_name"] = type, ["kind"] = kind });

    /// <summary>The text of a tool's answer, an error's included.</summary>
    private static async Task<string> Ask(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(tool, arguments);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }

    /// <summary>
    /// The folders of one test: Users, the allow-root; Outside, which holds ExternalLib; and
    /// Packages, the server's NuGet package folder. Disposing it deletes them once the server has
    /// let go of its files.
    /// </summary>
    private sealed class Layout : IAsyncDisposable
    {
        private readonly string _root =
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ILens-bounds-" + Guid.NewGuid().ToString("N"))).FullName;

        public Layout()
        {
            Users = Directory.CreateDirectory(Path.Combine(_root, "Users")).FullName;
            Outside = Directory.CreateDirectory(Path.Combine(_root, "Outside")).FullName;
            Packages = Directory.CreateDirectory(Path.Combine(_root, "Packages")).FullName;
            File.Copy(Path.Combine(AppContext.BaseDirectory, "References", "ExternalLib.dll"), Path.Combine(Outside, "ExternalLib.dll"));
        }

        public string Users { get; }
        public string Outside { get; }
        public string Packages { get; }

        /// <summary>Put ExternalLib into Packages, where a package ExternalLib 1.0.0 built for .NET Standard 2.0 lies.</summary>
        public void WritePackage()
        {
            string package = Directory.CreateDirectory(
                Path.Combine(Packages, "externallib", "1.0.0", "lib", "netstandard2.0")).FullName;
            File.Copy(Path.Combine(AppContext.BaseDirectory, "References", "ExternalLib.dll"), Path.Combine(package, "ExternalLib.dll"));
        }

        /// <summary>A server whose one allow-root is Users and whose NuGet package folder is Packages.</summary>
        public Task<McpClient> StartServer() =>
            ILensServerFixture.ConnectAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-bounds",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", Users],
                EnvironmentVariables = new Dictionary<string, string?> { ["NUGET_PACKAGES"] = Packages },
            }));

        public async ValueTask DisposeAsync() => await ILensServerFixture.DeleteWhenReleased(_root);
    }

    /// <summary>
    /// Records each TCP connection the machine holds to one of the computers given while it runs,
    /// read every 50 ms: a connection waiting to be accepted lasts about 21 seconds, so none is missed.
    /// </summary>
    private sealed class ConnectionWatch : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, bool> _seen = new();
        private readonly Task _watching;

        public ConnectionWatch(IReadOnlyCollection<IPAddress> computers) =>
            _watching = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var connection in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                            if (computers.Contains(connection.RemoteEndPoint.Address))
                                _seen.TryAdd($"{connection.State} to {connection.RemoteEndPoint}", true);
                    }
                    catch (NetworkInformationException) { }
                    try { await Task.Delay(50, _stop.Token); }
                    catch (OperationCanceledException) { }
                }
            });

        /// <summary>Whether a connection to <paramref name="computer"/> shows within <paramref name="time"/>.</summary>
        public async Task<bool> SeesAsync(IPAddress computer, TimeSpan time)
        {
            var until = DateTime.UtcNow + time;
            while (DateTime.UtcNow < until)
            {
                if (_seen.Keys.Any(connection => connection.Contains($" to {computer}:")))
                    return true;
                await Task.Delay(50);
            }
            return false;
        }

        /// <summary>Stop watching and return what was seen.</summary>
        public async Task<IReadOnlyCollection<string>> StopAsync()
        {
            _stop.Cancel();
            await _watching;
            return [.. _seen.Keys];
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stop.IsCancellationRequested)
                await StopAsync();
            _stop.Dispose();
        }
    }
}
