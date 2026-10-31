using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the keyword analyze writes on a parameter passed by reference when
/// asked what a method uses. <c>ExternalUser.Keywords.Clerk.Count</c> calls three methods of
/// <c>ExternalLib.Vault</c>, which between them take a parameter with each keyword. A call
/// says only that a parameter is passed by reference, so the keyword is known where the server
/// finds ExternalLib, whose DLL lands only in the References folder of the test output: the
/// second server has that folder as an allow-root, and the shared server does not search it.
/// </summary>
public sealed class ByReferenceUseTests
    : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    private const string Header = "3 symbols used by ExternalUser.Keywords.Clerk.Count(Vault) [declared]:\n";

    private const string Declared = Header +
        "ExternalLib.Vault.Peek(ref readonly int)\n" +
        "ExternalLib.Vault.TryTake(int, out string)\n" +
        "ExternalLib.Vault.Weigh(in int, ref int)";

    private const string Undeclared = Header +
        "ExternalLib.Vault.Peek(ref int)\n" +
        "ExternalLib.Vault.TryTake(int, ref string)\n" +
        "ExternalLib.Vault.Weigh(ref int, ref int)\n" +
        "Not checked: whether ref on a method of ExternalLib.Vault is out, in or ref readonly; ";

    private readonly ILensServerFixture _shared;
    private readonly ReferencesServerFixture _references;
    private readonly string _testOutput;

    public ByReferenceUseTests(ILensServerFixture shared, ReferencesServerFixture references)
    {
        _shared = shared;
        _references = references;
        _testOutput = Path.GetDirectoryName(shared.AssemblyPath)!;
    }

    [Fact]
    public async Task Uses_WritesTheKeywordTheMethodDeclares_WhenItsLibraryLiesInAnotherAllowRoot()
    {
        Assert.Equal(Declared, await Uses(_references.Client, Path.Combine(_testOutput, "ExternalUser.dll")));
    }

    [Fact]
    public async Task Uses_WritesTheKeywordTheMethodDeclares_WhenItsLibraryLiesBesideTheAssembly()
    {
        string root = NewFolder();
        File.Copy(Path.Combine(_testOutput, "ExternalUser.dll"), Path.Combine(root, "ExternalUser.dll"));
        File.Copy(Path.Combine(_testOutput, "References", "ExternalLib.dll"), Path.Combine(root, "ExternalLib.dll"));
        try
        {
            await using var client = await StartServer("ILens-keywords-beside", root);
            Assert.Equal(Declared, await Uses(client, Path.Combine(root, "ExternalUser.dll")));
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    // The call's signature says by reference and no more, so every such parameter is written
    // ref, and the answer says which library would have said the keyword.
    [Fact]
    public async Task Uses_WritesRefAndSaysSo_WhenTheLibraryLiesInNoAllowRoot()
    {
        Assert.Equal(
            Undeclared + "ILens could not load ExternalLib, Version=1.0.0.0",
            await Uses(_shared.Client, Path.Combine(_testOutput, "ExternalUser.dll")));
    }

    // What a property uses is asked of its accessors, and the answer ends the same way.
    [Fact]
    public async Task Uses_OnAProperty_SaysSoAsWell()
    {
        Assert.Equal(
            "2 symbols used by ExternalUser.Keywords.Clerk.First [declared] (via get_First):\n" +
            "ExternalLib.Vault.TryTake(int, ref string)\n" +
            "ExternalUser.Keywords.Clerk._vault\n" +
            "Not checked: whether ref on a method of ExternalLib.Vault is out, in or ref readonly; " +
            "ILens could not load ExternalLib, Version=1.0.0.0",
            await Uses(_shared.Client, Path.Combine(_testOutput, "ExternalUser.dll"), "First"));
    }

    // A library of another version: the class is there and the methods called are not.
    [Fact]
    public async Task Uses_WritesRefAndSaysSo_WhenTheLibraryFoundDoesNotDeclareTheMethod()
    {
        string root = NewFolder();
        File.Copy(Path.Combine(_testOutput, "ExternalUser.dll"), Path.Combine(root, "ExternalUser.dll"));
        EmptyVaultAssembly.Write(root);
        try
        {
            await using var client = await StartServer("ILens-keywords-other-version", root);
            Assert.Equal(
                Undeclared + "ExternalLib.Vault as loaded declares no such method",
                await Uses(client, Path.Combine(root, "ExternalUser.dll")));
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    private static string NewFolder() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ILens-keywords-" + Guid.NewGuid().ToString("N"))).FullName;

    private static async Task<McpClient> StartServer(string name, string root) =>
        await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = ILensServerFixture.ResolveILensExecutable(),
            Arguments = ["--allow-root", root],
        }));

    /// <summary>
    /// What <paramref name="member"/> of <c>Clerk</c> uses, in the ExternalUser at
    /// <paramref name="assembly"/>.
    /// </summary>
    internal static async Task<string> Uses(McpClient client, string assembly, string member = "Count")
    {
        CallToolResult result = await client.CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["assembly"] = assembly,
            ["type_name"] = "ExternalUser.Keywords.Clerk",
            ["member_name"] = member,
            ["kind"] = "uses",
        });
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"analyze returned an error: {text}");
        return text;
    }
}
