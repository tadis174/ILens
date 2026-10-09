using ModelContextProtocol.Client;

namespace Tests;

/// <summary>
/// Spawns one ILens MCP server whose allow-roots are the test output and its References folder,
/// where the assemblies the fixtures reference sit. The shared server searches only the top of
/// the test output, so for it those references stay unresolved; this one resolves them from its
/// second allow-root.
/// </summary>
public sealed class ReferencesServerFixture : IAsyncLifetime
{
    /// <summary>A connected MCP client driving a live ILens server process.</summary>
    public McpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        string testOutput = AppContext.BaseDirectory;
        string references = Path.Combine(testOutput, "References");
        foreach (string name in new[] { "ExternalLib.dll", "0Harmony.dll" })
        {
            if (!File.Exists(Path.Combine(references, name)))
                throw new FileNotFoundException(
                    $"Reference not found: {Path.Combine(references, name)}. Tests.csproj is expected " +
                    "to place it in the References folder of the test output.");
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ILens-references",
            Command = ILensServerFixture.ResolveILensExecutable(),
            Arguments = ["--allow-root", testOutput, "--allow-root", references],
        });
        Client = await McpClient.CreateAsync(transport);
    }

    public async Task DisposeAsync()
    {
        if (Client is not null)
            await Client.DisposeAsync();
    }
}
