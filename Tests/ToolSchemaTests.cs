using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;

namespace Tests;

/// <summary>
/// The conventions every tool's schema follows, checked on the tool list a client receives, and
/// the instructions the server sends with them, so a tool added or changed later cannot drift
/// from either unnoticed.
/// </summary>
public sealed class ToolSchemaTests : IClassFixture<ILensServerFixture>
{
    /// <summary>
    /// The most every tool's name, description and input schema may measure together. Every
    /// session that registers ILens pays for these characters, so growth beyond this is a
    /// decision to raise the ceiling, not a side effect.
    /// </summary>
    private const int Ceiling = 16384;

    /// <summary>
    /// The tools that list nothing and so take no limit: decompile answers with one type or
    /// member, and list_allowed_roots with the few roots the server was started with.
    /// </summary>
    private static readonly string[] ToolsWithoutLimit = ["decompile", "list_allowed_roots"];

    /// <summary>Tool and parameter names the conventions replaced, which must not come back.</summary>
    private static readonly string[] ReplacedNames =
    [
        "list_types", "search_types", "summarize_type", "decompile_type", "decompile_method",
        "decompile_property", "decompile_event", "find_call_sites", "list_changed_types",
        "compare_type", "compare_method", "pattern", "name_pattern", "returns", "declaring_type",
        "declaring_type_pattern", "declaring_namespace", "namespace_name", "namespace_filter",
        "exclude_compiler_generated", "method_name", "property_name", "event_name",
    ];

    private static readonly Regex SnakeCase = new("^[a-z][a-z0-9]*(_[a-z0-9]+)*$");
    private static readonly Regex SnakeCaseWord = new(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b");
    private static readonly Regex Shouting = new(@"\b[A-Z]{4,}\b");

    private readonly ILensServerFixture _server;

    public ToolSchemaTests(ILensServerFixture server) => _server = server;

    private sealed record Parameter(string Tool, string Name, JsonElement Schema)
    {
        public string Type => Schema.TryGetProperty("type", out var type) ? type.GetRawText() : "";
        public string Description => Schema.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "";
        public string[] EnumValues => Schema.TryGetProperty("enum", out var values)
            ? values.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
            : Schema.TryGetProperty("items", out var items) && items.TryGetProperty("enum", out var itemValues)
                ? itemValues.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
                : [];
    }

    private async Task<IList<McpClientTool>> Tools() => await _server.Client.ListToolsAsync();

    private static IEnumerable<Parameter> ParametersOf(McpClientTool tool) =>
        tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(property => new Parameter(tool.Name, property.Name, property.Value))
            : [];

    [Fact]
    public async Task Names_AreSnakeCase()
    {
        foreach (var tool in await Tools())
        {
            Assert.Matches(SnakeCase, tool.Name);
            foreach (var parameter in ParametersOf(tool))
            {
                Assert.True(SnakeCase.IsMatch(parameter.Name), $"{tool.Name}.{parameter.Name} is not snake_case");
                Assert.All(parameter.EnumValues, value => Assert.True(SnakeCase.IsMatch(value),
                    $"{tool.Name}.{parameter.Name} has the enum value {value}, which is not snake_case"));
            }
        }
    }

    [Fact]
    public async Task ReplacedNames_AppearNowhere()
    {
        foreach (var tool in await Tools())
        {
            Assert.DoesNotContain(tool.Name, ReplacedNames);
            foreach (var parameter in ParametersOf(tool))
                Assert.True(!ReplacedNames.Contains(parameter.Name), $"{tool.Name} takes the replaced name {parameter.Name}");
        }
    }

    [Fact]
    public async Task SharedParameters_HaveOneTypeAndOneDescription()
    {
        var byName = (await Tools()).SelectMany(ParametersOf).GroupBy(parameter => parameter.Name);
        foreach (var group in byName.Where(group => group.Count() > 1))
        {
            Assert.True(group.Select(parameter => parameter.Type).Distinct().Count() == 1,
                $"{group.Key} has several types: {string.Join(", ", group.Select(p => $"{p.Tool} {p.Type}"))}");
            Assert.True(group.Select(parameter => string.Join(",", parameter.EnumValues)).Distinct().Count() == 1,
                $"{group.Key} has several sets of values across {string.Join(", ", group.Select(p => p.Tool))}");
            Assert.True(group.Select(parameter => parameter.Description).Distinct().Count() == 1,
                $"{group.Key} is described differently across {string.Join(", ", group.Select(p => p.Tool))}");
        }
    }

    [Fact]
    public async Task EveryListingTool_TakesALimit()
    {
        var withoutLimit = (await Tools())
            .Where(tool => ParametersOf(tool).All(parameter => parameter.Name != "limit"))
            .Select(tool => tool.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(ToolsWithoutLimit.Order(StringComparer.Ordinal), withoutLimit);
    }

    [Fact]
    public async Task Descriptions_UseNoEmphasisAndNameOnlyWhatExists()
    {
        var tools = await Tools();
        var names = tools.Select(tool => tool.Name)
            .Concat(tools.SelectMany(ParametersOf).Select(parameter => parameter.Name))
            .Concat(tools.SelectMany(ParametersOf).SelectMany(parameter => parameter.EnumValues))
            .ToHashSet(StringComparer.Ordinal);

        var descriptions = tools.Select(tool => (Where: tool.Name, Text: tool.Description ?? ""))
            .Concat(tools.SelectMany(ParametersOf).Select(parameter => (Where: $"{parameter.Tool}.{parameter.Name}", Text: parameter.Description)))
            .Append((Where: "the server instructions", Text: InstructionsWithoutDirectories()));
        foreach (var (where, text) in descriptions)
        {
            Assert.True(!Shouting.IsMatch(text) && !text.Contains("**"), $"{where} emphasizes: {text}");
            foreach (Match word in SnakeCaseWord.Matches(text))
                Assert.True(names.Contains(word.Value), $"{where} names {word.Value}, which is no tool, parameter or value");
        }
    }

    [Fact]
    public async Task NamesDescriptionsAndSchemas_StayUnderTheCeiling()
    {
        int size = (await Tools()).Sum(tool =>
            tool.Name.Length + (tool.Description?.Length ?? 0) + tool.ProtocolTool.InputSchema.GetRawText().Length);

        Assert.True(size <= Ceiling, $"The tool list is {size} characters, over the ceiling of {Ceiling}.");
    }

    [Fact]
    public async Task Instructions_NameEveryToolAndTheDirectoriesTheServerReads()
    {
        // Claude Code loads the instructions at session start and the tool descriptions only on
        // demand, so the instructions are where a model first learns which tool answers what.
        string instructions = _server.Client.ServerInstructions ?? "";

        Assert.True(instructions.Length <= InstructionsLimit,
            $"The instructions are {instructions.Length} characters, over Claude Code's limit of {InstructionsLimit}.");
        foreach (var tool in await Tools())
            Assert.Matches($@"\b{tool.Name}\b", instructions);
        foreach (var root in _server.AllowRoots)
            Assert.Contains(Normalized(root), instructions);
    }

    [Fact]
    public async Task Instructions_WithMoreDirectoriesThanFit_CountTheRestAndKeepTheTools()
    {
        // Forty directories with long names cannot all be listed within the limit. The list must
        // stop early and count the rest, rather than push the tools past the cut.
        string parent = Path.Combine(Path.GetTempPath(), "ILens-roots-" + Guid.NewGuid().ToString("N"));
        var roots = Enumerable.Range(0, 40)
            .Select(i => Directory.CreateDirectory(Path.Combine(parent, $"a-directory-with-a-deliberately-long-name-{i:D2}")).FullName)
            .ToList();
        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-roots",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = [.. roots.SelectMany(root => new[] { "--allow-root", root })],
            });
            await using var client = await McpClient.CreateAsync(transport);
            string instructions = client.ServerInstructions ?? "";

            int shown = roots.Count(root => instructions.Contains(root));
            Assert.True(instructions.Length <= InstructionsLimit,
                $"The instructions are {instructions.Length} characters, over Claude Code's limit of {InstructionsLimit}.");
            Assert.InRange(shown, 1, roots.Count - 1);
            Assert.Contains($"and {roots.Count - shown} more, which list_allowed_roots lists", instructions);
            Assert.Contains("find_harmony_dependencies:", instructions);
            Assert.EndsWith("passed to the next tool.", instructions);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The length at which Claude Code cuts a server's instructions by default.</summary>
    private const int InstructionsLimit = 2048;

    /// <summary>
    /// The server's instructions with the directories it reads taken out, since a directory name
    /// is the machine's, not the server's wording.
    /// </summary>
    private string InstructionsWithoutDirectories() =>
        _server.AllowRoots.Aggregate(_server.Client.ServerInstructions ?? "",
            (text, root) => text.Replace(Normalized(root), ""));

    /// <summary>A directory as the server prints it: a full path without a trailing separator.</summary>
    private static string Normalized(string directory) =>
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
