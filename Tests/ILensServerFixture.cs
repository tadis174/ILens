using ModelContextProtocol.Client;

namespace Tests;

/// <summary>
/// Spawns one ILens MCP server process for a test class and exposes a connected
/// client plus the path to the assembly the e2e tests inspect.
/// </summary>
public sealed class ILensServerFixture : IAsyncLifetime
{
    /// <summary>A connected MCP client driving a live ILens server process.</summary>
    public McpClient Client { get; private set; } = null!;

    /// <summary>
    /// The e2e inspection target — ICSharpCode.Decompiler.dll, placed in the test
    /// output directory by the package reference in Tests.csproj.
    /// </summary>
    public string AssemblyPath { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // ILens may load assemblies only from under an --allow-root directory. The
        // test output directory is both that root and where the ICSharpCode.Decompiler
        // package reference dropped the DLL the tests point ILens at.
        string allowRoot = AppContext.BaseDirectory;
        AssemblyPath = Path.Combine(allowRoot, "ICSharpCode.Decompiler.dll");
        if (!File.Exists(AssemblyPath))
            throw new FileNotFoundException(
                $"Inspection target not found: {AssemblyPath}. The ICSharpCode.Decompiler " +
                "package reference in Tests.csproj is expected to place it in the test output.");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ILens",
            Command = ResolveILensExecutable(),
            Arguments = ["--allow-root", allowRoot],
        });
        Client = await McpClient.CreateAsync(transport);
    }

    public async Task DisposeAsync()
    {
        if (Client is not null)
            await Client.DisposeAsync();
    }

    /// <summary>
    /// Resolves the ILens executable to drive. <c>run-test.ps1</c> sets
    /// <c>ILENS_E2E_EXE</c>; absent that (a bare <c>dotnet test</c>), falls back to
    /// the most recent <c>ILens.exe</c> under the Core project's build output.
    /// Exposed publicly so tests that need a custom-configured second server
    /// (e.g. with <c>--max-total-size</c>) can spawn one without duplicating
    /// this lookup logic.
    /// </summary>
    public static string ResolveILensExecutable()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("ILENS_E2E_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            if (!File.Exists(fromEnv))
                throw new FileNotFoundException($"ILENS_E2E_EXE points to a missing file: {fromEnv}");
            return fromEnv;
        }

        string coreBin = Path.Combine(FindRepoRoot(), "Build", "bin", "Core");
        string? candidate = Directory.Exists(coreBin)
            ? Directory.EnumerateFiles(coreBin, "ILens.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        return candidate ?? throw new InvalidOperationException(
            "No ILens executable found. Set ILENS_E2E_EXE, build Core " +
            "(dotnet build Core/Core.csproj -c Release), or run the suite via Tests/run-test.ps1.");
    }

    /// <summary>Walks up from the test output directory to the directory holding ILens.sln.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ILens.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate the repo root (no ILens.sln above the test output directory).");
    }
}
