using System.Reflection;
using ILens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

// Read the assembly version once and reuse it. The MCP ServerInfo handshake (below)
// and the --version flag must always agree, so they pull from a single source —
// the AssemblyInformationalVersion baked in at build time, which mirrors csproj's
// <Version>. Hardcoding either to a literal string would drift on each release.
var ilensVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "unknown";
// Strip any "+commit" suffix that .NET may append; we only ship the semver.
var plusIdx = ilensVersion.IndexOf('+');
if (plusIdx >= 0) ilensVersion = ilensVersion.Substring(0, plusIdx);

// --version / -v exits early before any MCP setup, so it's safe to run on any machine.
// Accept the flag at any position so order is forgiving.
if (args.Any(a => a == "--version" || a == "-v"))
{
    Console.WriteLine($"ILens {ilensVersion}");
    return 0;
}

// Strict argument parsing: only --allow-root is accepted. A trailing --allow-root
// without a value, or any unknown flag, fails fast — typos like '--allow-roots'
// must not silently launch a server with no roots.
var allowedRoots = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg == "--allow-root")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("[ILens] --allow-root requires a directory argument");
            return 1;
        }
        allowedRoots.Add(args[++i]);
    }
    else
    {
        Console.Error.WriteLine($"[ILens] unknown argument: {arg}");
        Console.Error.WriteLine("[ILens] usage: ILens [--version] [--allow-root <directory> ...]");
        return 1;
    }
}

PathGuard guard;
try
{
    guard = new PathGuard(allowedRoots);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"[ILens] {ex.Message}");
    return 1;
}

// Fail fast if our analyze-kind table has drifted from ILSpyX's exported analyzers.
try
{
    AnalysisDispatch.SelfCheck();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"[ILens] {ex.Message}");
    return 1;
}

var registry = new AssemblyHostRegistry(guard);

var builder = Host.CreateApplicationBuilder(args);

// MCP protocol uses stdout exclusively for JSON-RPC messages.
// .NET's default console logger writes to stdout, which corrupts the transport.
// Redirect all logging to stderr so it doesn't interfere with the protocol.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(guard);
builder.Services.AddSingleton(registry);
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "ILens",
            Version = ilensVersion
        };
        ToolErrorFilter.Install(options);
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
