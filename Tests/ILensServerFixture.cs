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

    /// <summary>
    /// The runtime's System.Private.CoreLib.dll, the inspection target for type shapes
    /// ICSharpCode.Decompiler.dll does not show: it defines System.ValueTuple itself, so ILSpy
    /// reads a tuple in its signatures as a tuple type, and it declares function pointers.
    /// </summary>
    public string CoreLibPath { get; } = typeof(object).Assembly.Location;

    /// <summary>The directories the server was started with as allow-roots.</summary>
    public IReadOnlyList<string> AllowRoots { get; private set; } = [];

    public async Task InitializeAsync()
    {
        // ILens may load assemblies only from under an --allow-root directory. The
        // test output directory is both that root and where the ICSharpCode.Decompiler
        // package reference dropped the DLL the tests point ILens at. The runtime
        // directory is the second root, for the tests that inspect CoreLib.
        string allowRoot = AppContext.BaseDirectory;
        AssemblyPath = Path.Combine(allowRoot, "ICSharpCode.Decompiler.dll");
        if (!File.Exists(AssemblyPath))
            throw new FileNotFoundException(
                $"Inspection target not found: {AssemblyPath}. The ICSharpCode.Decompiler " +
                "package reference in Tests.csproj is expected to place it in the test output.");

        AllowRoots = [allowRoot, Path.GetDirectoryName(CoreLibPath)!];
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ILens",
            Command = ResolveILensExecutable(),
            Arguments = [.. AllowRoots.SelectMany(root => new[] { "--allow-root", root })],
        });
        Client = await ConnectAsync(transport);
    }

    public async Task DisposeAsync()
    {
        if (Client is not null)
            await Client.DisposeAsync();
    }

    /// <summary>
    /// Connects a client to the ILens server that the transport starts, as every client of the
    /// suite does. The client waits up to ten minutes for the server's answer to its first
    /// request, where the MCP SDK waits 60 s. A server answers within 2 s of its start on a machine
    /// under light load, but while other programs keep the CPU fully busy, starts in a full run can
    /// take longer than 60 s, and at the SDK's limit their tests fail without being broken. A server
    /// that exits fails its test at once, so the limit only ends one that stays alive without
    /// answering. A run that still fails here with "Initialization timed out" had a start of over ten
    /// minutes: a server that hangs before it answers, or a machine busier than in any run measured,
    /// which is worth measuring before the limit is raised.
    /// </summary>
    public static Task<McpClient> ConnectAsync(IClientTransport transport) =>
        McpClient.CreateAsync(transport, new McpClientOptions
        {
            InitializationTimeout = TimeSpan.FromMinutes(10),
        });

    /// <summary>
    /// Resolves the ILens executable to drive. <c>run-test.ps1</c> sets
    /// <c>ILENS_E2E_EXE</c>; absent that (a bare <c>dotnet test</c>), falls back to
    /// the most recent <c>ILens.exe</c> under the Core project's build output.
    /// Exposed publicly so tests that need a custom-configured second server
    /// (e.g. with <c>--max-total-size</c>) can spawn one without duplicating
    /// this lookup logic. Such a test connects its client through
    /// <see cref="ConnectAsync"/>, as every client of the suite does, so that
    /// a busy machine does not fail its start.
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

    /// <summary>
    /// Deletes the folder of a test that started a server of its own, once that server lets go of
    /// its files. The server can still hold a reference it mapped for a moment after the client is
    /// disposed, so the delete is retried for up to five seconds. It never throws: an exception
    /// here would hide the assertion that failed.
    /// </summary>
    public static async Task DeleteWhenReleased(string root)
    {
        for (int attempt = 0; attempt < 50 && Directory.Exists(root); attempt++)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
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
