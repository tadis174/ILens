using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for decompiling an assembly whose references sit in another allow-root. The
/// ExternalUser fixture uses ExternalLib, whose DLL lands only in the References folder of the
/// test output. The shared server searches only the top of each allow-root and leaves
/// ExternalLib's types unresolved; the second server has that folder as an allow-root and
/// resolves them. A reference the decompiler may not open is one analyze does not read either,
/// when asked what a method uses.
/// </summary>
public sealed class ReferenceResolutionTests
    : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    // The end of what a method that calls into ExternalLib uses, when ExternalLib was not read.
    private const string LibraryNotRead =
        "ExternalLib.Vault.Weigh(ref int, ref int)\n" +
        "Not checked: whether ref on a method of ExternalLib.Vault is out, in or ref readonly; " +
        "ILens could not load ExternalLib, Version=1.0.0.0";

    private readonly ILensServerFixture _shared;
    private readonly ReferencesServerFixture _references;
    private readonly string _testOutput;

    public ReferenceResolutionTests(ILensServerFixture shared, ReferencesServerFixture references)
    {
        _shared = shared;
        _references = references;
        _testOutput = Path.GetDirectoryName(shared.AssemblyPath)!;
    }

    [Fact]
    public async Task Decompile_ResolvesAReferenceFromAnotherAllowRoot()
    {
        // An enum value in an attribute decodes only with the enum's assembly, and a value of a
        // type ILSpy cannot read draws a comment about its unknown type.
        string user = Path.Combine(_testOutput, "ExternalUser.dll");
        string shaded = await Decompile(_references.Client, user, "ExternalUser.Consumer", "Shaded");
        string made = await Decompile(_references.Client, user, "ExternalUser.Consumer", "MakeWidget");

        Assert.Contains("[Mark(Shade.Dark)]", shaded);
        Assert.DoesNotContain("Unknown result type", made);
    }

    [Fact]
    public async Task Decompile_DoesNotSearchBelowTheTopOfAnAllowRoot()
    {
        // The References folder lies one level below the shared server's allow-root.
        string user = Path.Combine(_testOutput, "ExternalUser.dll");
        string shaded = await Decompile(_shared.Client, user, "ExternalUser.Consumer", "Shaded");
        string made = await Decompile(_shared.Client, user, "ExternalUser.Consumer", "MakeWidget");

        Assert.Contains("Could not decode attribute arguments", shaded);
        Assert.Contains("Unknown result type", made);
    }

    [Fact]
    public async Task AReferenceThatLinksOutOfTheRoots_IsNotOpened()
    {
        // A file at the top of an allow-root that links to a file outside every root is not
        // opened, as PathGuard refuses a requested path that does. HarmonyLib sits in the same
        // root as a plain file, so the root is searched. Creating a symbolic link on Windows
        // requires admin or Developer Mode; the test passes silently where it cannot.
        string root = Path.Combine(Path.GetTempPath(), "ILens-links-" + Guid.NewGuid().ToString("N"));
        string users = Directory.CreateDirectory(Path.Combine(root, "Users")).FullName;
        string links = Directory.CreateDirectory(Path.Combine(root, "Links")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(root, "Outside")).FullName;
        File.Copy(Path.Combine(_testOutput, "ExternalUser.dll"), Path.Combine(users, "ExternalUser.dll"));
        File.Copy(Path.Combine(_testOutput, "HarmonyMod.dll"), Path.Combine(users, "HarmonyMod.dll"));
        File.Copy(Path.Combine(_testOutput, "References", "0Harmony.dll"), Path.Combine(links, "0Harmony.dll"));
        string library = Path.Combine(outside, "ExternalLib.dll");
        File.Copy(Path.Combine(_testOutput, "References", "ExternalLib.dll"), library);

        try
        {
            try
            {
                File.CreateSymbolicLink(Path.Combine(links, "ExternalLib.dll"), library);
            }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-links",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", users, "--allow-root", links],
            });
            await using (var client = await McpClient.CreateAsync(transport))
            {
                string lookups = await Decompile(client, Path.Combine(users, "HarmonyMod.dll"),
                    "HarmonyMod.OtherLookupsPatch", "TargetMethods");
                string shaded = await Decompile(client, Path.Combine(users, "ExternalUser.dll"),
                    "ExternalUser.Consumer", "Shaded");
                string used = await ByReferenceUseTests.Uses(client, Path.Combine(users, "ExternalUser.dll"));

                Assert.Contains("typeof(TargetA).Method(", lookups);
                Assert.Contains("Could not decode attribute arguments", shaded);
                Assert.Contains(LibraryNotRead, used);
            }
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AReferenceTheServerMayNotRead_StaysUnresolved()
    {
        // A reference file the server may not read leaves the reference unresolved, as one it
        // cannot parse does, instead of failing every tool on the assembly that uses it. HarmonyLib
        // sits in the same root and can be read, so the root is searched.
        string root = Path.Combine(Path.GetTempPath(), "ILens-unreadable-" + Guid.NewGuid().ToString("N"));
        string users = Directory.CreateDirectory(Path.Combine(root, "Users")).FullName;
        string locked = Directory.CreateDirectory(Path.Combine(root, "Locked")).FullName;
        File.Copy(Path.Combine(_testOutput, "ExternalUser.dll"), Path.Combine(users, "ExternalUser.dll"));
        File.Copy(Path.Combine(_testOutput, "HarmonyMod.dll"), Path.Combine(users, "HarmonyMod.dll"));
        File.Copy(Path.Combine(_testOutput, "References", "0Harmony.dll"), Path.Combine(locked, "0Harmony.dll"));
        var library = new FileInfo(Path.Combine(locked, "ExternalLib.dll"));
        File.Copy(Path.Combine(_testOutput, "References", "ExternalLib.dll"), library.FullName);
        var denyRead = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        FileSecurity security = library.GetAccessControl();
        security.AddAccessRule(denyRead);
        library.SetAccessControl(security);

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-unreadable",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", users, "--allow-root", locked],
            });
            await using (var client = await McpClient.CreateAsync(transport))
            {
                string lookups = await Decompile(client, Path.Combine(users, "HarmonyMod.dll"),
                    "HarmonyMod.OtherLookupsPatch", "TargetMethods");
                string shaded = await Decompile(client, Path.Combine(users, "ExternalUser.dll"),
                    "ExternalUser.Consumer", "Shaded");
                string used = await ByReferenceUseTests.Uses(client, Path.Combine(users, "ExternalUser.dll"));

                Assert.Contains("typeof(TargetA).Method(", lookups);
                Assert.Contains("Could not decode attribute arguments", shaded);
                Assert.Contains(LibraryNotRead, used);
            }
        }
        finally
        {
            security.RemoveAccessRule(denyRead);
            library.SetAccessControl(security);
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    private static async Task<string> Decompile(McpClient client, string assembly, string type, string member)
    {
        CallToolResult result = await client.CallToolAsync("decompile", new Dictionary<string, object?>
        {
            ["assembly"] = assembly,
            ["type_name"] = type,
            ["member_name"] = member,
        });
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"decompile returned an error: {text}");
        return text;
    }
}
