using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for a type name the inspected assembly gives types of its own while it
/// references a type of that name from another library. ExternalUser declares, in ExternalLib's
/// namespace ExternalLib.Namesakes, two generic types named like ExternalLib's Marker, whose
/// nested class each has too, one named like its Gauge, one with another count of type
/// parameters than its Crate&lt;T&gt;, and one without type parameters named like its
/// Lever&lt;T&gt;; and its own IsExternalInit beside the one the init accessor of ExternalLib's
/// Badge names. Its class Readings uses ExternalLib's types. ILens reads the types the assembly
/// defines and those it references together, so a name without type parameters means the one
/// type without them, defined or referenced.
/// </summary>
public sealed class NamesakeTypeTests : IClassFixture<ILensServerFixture>
{
    private const string Origin = "ExternalLib, Version=1.0.0.0";

    private readonly ILensServerFixture _server;
    private readonly string _externalUserPath;

    public NamesakeTypeTests(ILensServerFixture server)
    {
        _server = server;
        _externalUserPath = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, "ExternalUser.dll");
        if (!File.Exists(_externalUserPath))
            throw new FileNotFoundException(
                $"Fixture not found: {_externalUserPath}. Tests/Fixtures/ExternalUser/ is expected to land " +
                "here via Tests.csproj's ProjectReference.");
    }

    [Theory]
    // Two generic types of the assembly bear the name, the case of a profiling library that
    // defines generic markers named like the engine's marker it uses.
    [InlineData("ExternalLib.Namesakes.Marker", "ExternalUser.Namesakes.Readings.Mark(Marker)")]
    // One generic type of the assembly bears the name, which was answered about without an error.
    [InlineData("ExternalLib.Namesakes.Gauge", "ExternalUser.Namesakes.Readings.Read(Gauge)")]
    // A nested class each of the generic types has, beside the one the referenced type has.
    [InlineData("ExternalLib.Namesakes.Marker.Scope", "ExternalUser.Namesakes.Readings.Enter(Scope)")]
    // The spelling with an arity of 0, which reached the referenced type before.
    [InlineData("ExternalLib.Namesakes.Marker`0", "ExternalUser.Namesakes.Readings.Mark(Marker)")]
    public async Task UsedBy_OnANameOnlyGenericTypesOfTheAssemblyBear_AnswersForTheReferencedType(
        string typeName, string user)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = typeName,
            ["kind"] = "used_by",
        });

        string referenced = typeName.Replace("`0", "");
        Assert.Equal($"1 user of {referenced}:", Header(text));
        Assert.Equal(user, ResultLines(text).Single());
    }

    // Every tool that needs the type's code, with the parameters it requires beside the type.
    public static TheoryData<string, string?, string?> ToolsNeedingTheCode => new()
    {
        { "decompile", null, null },
        { "list_members", null, null },
        { "find_methods", null, null },
        { "search_code", "regex", "Begin" },
    };

    [Theory]
    [MemberData(nameof(ToolsNeedingTheCode))]
    public async Task NamingSuchATypeToAToolThatNeedsItsCode_SaysWhereItComesFromAndNamesTheAssemblysOwn(
        string toolName, string? parameter, string? value)
    {
        // The caller may have meant one of the assembly's generic types and left out its type
        // parameters, so the error names them.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Namesakes.Marker",
        };
        if (parameter is not null)
            arguments[parameter] = value;

        var (result, text) = await CallToolExpectingError(toolName, arguments);

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            $"Error: ArgumentException: ExternalLib.Namesakes.Marker is not defined in this assembly; it comes from {Origin}. " +
            "This assembly defines ExternalLib.Namesakes.Marker<>, ExternalLib.Namesakes.Marker<,>.",
            text);
    }

    [Fact]
    public async Task NamingSuchATypeBesideOneGenericTypeOfTheAssembly_NamesThatOne()
    {
        var (result, text) = await CallToolExpectingError("list_members", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Namesakes.Gauge",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            $"Error: ArgumentException: ExternalLib.Namesakes.Gauge is not defined in this assembly; it comes from {Origin}. " +
            "This assembly defines ExternalLib.Namesakes.Gauge<>.",
            text);
    }

    [Fact]
    public async Task NamingSuchATypeWithItsArity_KeepsTheErrorWithoutTheAssemblysOwn()
    {
        // A caller who wrote the type parameters out meant the type that has them.
        var (result, text) = await CallToolExpectingError("list_members", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Namesakes.Crate<>",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            $"Error: ArgumentException: ExternalLib.Namesakes.Crate<> is not defined in this assembly; it comes from {Origin}.",
            text);
    }

    [Fact]
    public async Task Compare_OnSuchAName_SaysWhereItComesFrom()
    {
        var (result, text) = await CallToolExpectingError("compare", new()
        {
            ["assembly_a"] = _externalUserPath,
            ["assembly_b"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Namesakes.Marker",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            $"Error: ArgumentException: Type 'ExternalLib.Namesakes.Marker' is not defined in either assembly; it comes from {Origin}.",
            text);
    }

    // Each tool that resolves a type in its own way: analyze, which answers a referenced type,
    // list_members, which needs a type the assembly defines, and compare, for which a type one
    // build lacks is a result.
    public static TheoryData<string> ToolsResolvingAType => new() { "analyze", "list_members", "compare" };

    [Theory]
    [MemberData(nameof(ToolsResolvingAType))]
    public async Task ANameGenericTypesOfBothKindsBear_IsAnErrorNamingThemInTwoGroups(string toolName)
    {
        // The assembly defines Crate<T, U> and references ExternalLib's Crate<T>. Neither is the
        // type without type parameters a name without them means, so the caller has to pick.
        var arguments = toolName == "compare"
            ? new Dictionary<string, object?> { ["assembly_a"] = _externalUserPath, ["assembly_b"] = _externalUserPath }
            : new Dictionary<string, object?> { ["assembly"] = _externalUserPath };
        arguments["type_name"] = "ExternalLib.Namesakes.Crate";
        if (toolName == "analyze")
            arguments["kind"] = "used_by";

        var (result, text) = await CallToolExpectingError(toolName, arguments);

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            "Error: ArgumentException: Type name 'ExternalLib.Namesakes.Crate' matches 2 types: this assembly defines " +
            $"ExternalLib.Namesakes.Crate<,>; it references ExternalLib.Namesakes.Crate<> from {Origin}. Name one as listed.",
            text);
    }

    [Fact]
    public async Task ACountOfTypeParametersNoTypeHas_ListsTheTypesOfTheNameInTwoGroups()
    {
        var (result, text) = await CallToolExpectingError("list_members", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Namesakes.Crate<,,>",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(
            "Error: ArgumentException: Type not found: ExternalLib.Namesakes.Crate<,,>. Types of that name: this assembly " +
            $"defines ExternalLib.Namesakes.Crate<,>; it references ExternalLib.Namesakes.Crate<> from {Origin}.",
            text);
    }

    [Theory]
    // The assembly's own generic types, named with their type parameters.
    [InlineData("ExternalLib.Namesakes.Marker<>", "public class ExternalLib.Namesakes.Marker<T>")]
    [InlineData("ExternalLib.Namesakes.Marker`2", "public class ExternalLib.Namesakes.Marker<T, U>")]
    [InlineData("ExternalLib.Namesakes.Crate<,>", "public class ExternalLib.Namesakes.Crate<T, U>")]
    // The assembly's own type without type parameters, beside ExternalLib's Lever<T>.
    [InlineData("ExternalLib.Namesakes.Lever", "public class ExternalLib.Namesakes.Lever")]
    // The assembly's own IsExternalInit, beside a reference to ExternalLib's, which the init
    // accessor of ExternalLib's Badge names: the definition is what the name means.
    [InlineData("System.Runtime.CompilerServices.IsExternalInit", "internal static class System.Runtime.CompilerServices.IsExternalInit")]
    public async Task ANameATypeOfTheAssemblyAnswers_StillMeansThatType(string typeName, string declaration)
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = typeName,
            ["accessibility"] = "all",
        });

        // The header ends with the type, "N members of <type>:" or "No members of <type>", so a
        // generic namesake whose declaration starts alike does not pass.
        Assert.Matches($" of {Regex.Escape(declaration)}:?$", Header(text));
    }

    [Fact]
    public async Task UsedBy_OnAReferencedGenericTypeNamedWithItsArity_AnswersForIt()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Namesakes.Crate<>",
            ["kind"] = "used_by",
        });

        Assert.Equal("1 user of ExternalLib.Namesakes.Crate<>:", Header(text));
        Assert.Equal("ExternalUser.Namesakes.Readings.Fill(Crate<int>)", ResultLines(text).Single());
    }

    /// <summary>The header line of a response.</summary>
    private static string Header(string output) => output.Split('\n')[0];

    /// <summary>The result lines of an <c>analyze</c> response, with its header line dropped.</summary>
    private static string[] ResultLines(string analyzeOutput) =>
        analyzeOutput.Split('\n').Skip(1).ToArray();

    /// <summary>
    /// Calls an ILens MCP tool, fails the test if it returned an error, and returns the
    /// concatenated text content.
    /// </summary>
    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }

    /// <summary>
    /// Calls an ILens MCP tool that is expected to fail, returning the raw result alongside
    /// its text so a test can assert on both.
    /// </summary>
    private async Task<(CallToolResult Result, string Text)> CallToolExpectingError(
        string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return (result, text);
    }
}
