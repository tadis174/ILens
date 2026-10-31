using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
// Strip any "+commit" suffix; we only ship the semver. csproj disables
// IncludeSourceRevisionInInformationalVersion, so nothing should append one —
// this stays as a guard in case that property is ever turned back on.
var plusIdx = ilensVersion.IndexOf('+');
if (plusIdx >= 0) ilensVersion = ilensVersion.Substring(0, plusIdx);

// --version / -v exits early before any MCP setup, so it's safe to run on any machine.
// Accept the flag at any position so order is forgiving.
if (args.Any(a => a == "--version" || a == "-v"))
{
    Console.WriteLine($"ILens {ilensVersion}");
    return 0;
}

// Strict argument parsing: only --allow-root, --max-total-size and --max-search-seconds
// are accepted. A flag missing its value, or any unknown token, fails fast — typos like
// '--allow-roots' must not silently launch a server with no roots.
var allowedRoots = new List<string>();
long? maxTotalBytes = null;
TimeSpan? searchTimeLimit = null;
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
    else if (arg == "--max-total-size")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("[ILens] --max-total-size requires a value in MB");
            return 1;
        }
        var raw = args[++i];
        if (!long.TryParse(raw, out var mb) || mb <= 0)
        {
            Console.Error.WriteLine(
                $"[ILens] --max-total-size must be a positive integer (MB): {raw}");
            return 1;
        }
        maxTotalBytes = mb * 1024L * 1024L;
    }
    else if (arg == "--max-search-seconds")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("[ILens] --max-search-seconds requires a number of seconds");
            return 1;
        }
        var raw = args[++i];
        // 0 is allowed: a search then decompiles the least it can per call, one type.
        if (!int.TryParse(raw, out var seconds) || seconds < 0)
        {
            Console.Error.WriteLine(
                $"[ILens] --max-search-seconds must be a non-negative integer (seconds): {raw}");
            return 1;
        }
        searchTimeLimit = TimeSpan.FromSeconds(seconds);
    }
    else
    {
        Console.Error.WriteLine($"[ILens] unknown argument: {arg}");
        Console.Error.WriteLine(
            "[ILens] usage: ILens [--version] [--allow-root <directory> ...] " +
            "[--max-total-size <MB>] [--max-search-seconds <n>]");
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

var registry = new AssemblyHostRegistry(guard, maxTotalBytes, searchTimeLimit);

var builder = Host.CreateApplicationBuilder(args);

// MCP protocol uses stdout exclusively for JSON-RPC messages.
// .NET's default console logger writes to stdout, which corrupts the transport.
// Redirect all logging to stderr so it doesn't interfere with the protocol.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var jsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
jsonOptions.Converters.Insert(0, new JsonStringEnumConverter(SchemaNames.Policy));

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
        options.ServerInstructions = ServerInstructions.For(guard.AllowedRoots);
        // Order matters: the SDK wraps its filter pipeline back-to-front, so the
        // first-registered filter is the outermost. ToolErrorFilter must stay first
        // to catch and format what the others throw, and argument names must be
        // validated before ArgumentValueFilter reads or rewrites any of their values.
        ToolErrorFilter.Install(options);
        UnknownArgumentFilter.Install(options);
        ArgumentValueFilter.Install(options);
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(serializerOptions: jsonOptions);

await builder.Build().RunAsync();
return 0;
