using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for what a tool answers to a value its parameter does not take, and for the
/// values with exactly one reading, which are taken as the form the schema has, several
/// parameter types written as one text among them. Asked on the fixture CallShapes.dll. Every
/// value is sent as the JSON written in its row, so a row shows what a caller would have sent.
/// </summary>
public sealed class ArgumentValueTests : IClassFixture<ILensServerFixture>
{
    private const string Accessibilities = "public, public_protected, all";
    private const string MemberKinds = "method, property, field, event, nested_type";
    private const string AnalysisKinds =
        "used_by, instantiated_by, exposed_by, extension_methods, applied_to, overridden_by, " +
        "implemented_by, uses, implements, read_by, assigned_by, call_sites";

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public ArgumentValueTests(ILensServerFixture server)
    {
        _server = server;
        _callShapesPath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "CallShapes.dll");
        if (!File.Exists(_callShapesPath))
            throw new FileNotFoundException(
                $"CallShapes fixture not found: {_callShapesPath}. " +
                "Tests/Fixtures/CallShapes/ is expected to land here via Tests.csproj's " +
                "ProjectReference.");
    }

    // Tool, parameter, the value as JSON, and what the error says the parameter takes.
    public static TheoryData<string, string, string, string> RefusedValues => new()
    {
        // A name the parameter does not list.
        { "find_types", "accessibility", "\"Bogus\"", $"Valid values: {Accessibilities}." },
        { "find_types", "accessibility", "\"\"", $"Valid values: {Accessibilities}." },
        { "compare", "format", "\"json\"", "Valid values: csharp, il." },
        { "analyze", "kind", "\"used-by\"", $"Valid values: {AnalysisKinds}." },
        // A value comes back as it was written, also where it holds a character that JSON may
        // write as an escape: a letter outside ASCII, an angle bracket, an apostrophe or a plus.
        { "find_types", "accessibility", "\"café\"", $"Valid values: {Accessibilities}." },
        { "find_types", "accessibility", "\"it's+1\"", $"Valid values: {Accessibilities}." },
        { "find_types", "name_contains", "[\"List<int>\"]", "Expected a string." },
        // A number in place of a name, also one a name stands at, and several names in one text.
        { "find_types", "accessibility", "2", $"Valid values: {Accessibilities}." },
        { "find_types", "accessibility", "\"2\"", $"Valid values: {Accessibilities}." },
        { "find_types", "accessibility", "99", $"Valid values: {Accessibilities}." },
        { "find_types", "accessibility", "\"public, all\"", $"Valid values: {Accessibilities}." },
        { "analyze", "kind", "99", $"Valid values: {AnalysisKinds}." },
        { "find_types", "accessibility", "true", $"Valid values: {Accessibilities}." },
        { "find_types", "accessibility", "[\"all\"]", $"Valid values: {Accessibilities}." },
        // The same in a list of names.
        { "list_members", "kinds", "[\"bogus\"]", $"Expected a list of: {MemberKinds}." },
        { "list_members", "kinds", "\"bogus\"", $"Expected a list of: {MemberKinds}." },
        { "list_members", "kinds", "[\"field\",\"bogus\"]", $"Expected a list of: {MemberKinds}." },
        { "list_members", "kinds", "\"field,method\"", $"Expected a list of: {MemberKinds}." },
        { "list_members", "kinds", "[\"field,method\"]", $"Expected a list of: {MemberKinds}." },
        { "list_members", "kinds", "[99]", $"Expected a list of: {MemberKinds}." },
        { "list_members", "kinds", "[null]", $"Expected a list of: {MemberKinds}." },
        { "list_members", "kinds", "{\"a\":1}", $"Expected a list of: {MemberKinds}." },
        // A whole number.
        { "find_types", "limit", "\"abc\"", "Expected an integer." },
        { "find_types", "limit", "\"\"", "Expected an integer." },
        { "find_types", "limit", "0.5", "Expected an integer." },
        { "find_types", "limit", "\"0.5\"", "Expected an integer." },
        { "find_types", "limit", "true", "Expected an integer." },
        { "find_types", "limit", "[0]", "Expected an integer." },
        { "find_types", "limit", "99999999999", "Expected an integer of at most 2147483647." },
        { "find_types", "limit", "-99999999999", "Expected an integer of at least -2147483648." },
        { "find_methods", "parameter_count", "\"two\"", "Expected an integer." },
        // A switch.
        { "find_types", "include_compiler_generated", "\"yes\"", "Expected true or false." },
        { "find_types", "include_compiler_generated", "1", "Expected true or false." },
        // A text.
        { "find_types", "name_contains", "5", "Expected a string." },
        { "find_types", "name_contains", "true", "Expected a string." },
        { "find_types", "name_contains", "[\"Vault\"]", "Expected a string." },
        { "find_types", "name_contains", "{\"a\":1}", "Expected a string." },
        { "find_types", "assembly", "5", "Expected a string." },
        // A list of texts.
        { "find_methods", "parameter_types", "[1]", "Expected a list of strings." },
        { "find_methods", "parameter_types", "1", "Expected a list of strings." },
        { "find_methods", "parameter_types", "[null]", "Expected a list of strings." },
        { "find_methods", "parameter_types", "[[\"int\"]]", "Expected a list of strings." },
        { "find_methods", "parameter_types", "{\"a\":\"int\"}", "Expected a list of strings." },
    };

    [Theory]
    [MemberData(nameof(RefusedValues))]
    public async Task AValueTheParameterDoesNotTake_IsRefusedNamingTheParameterAndWhatItTakes(
        string tool, string parameter, string json, string expected)
    {
        var arguments = RequiredOf(tool);
        arguments[parameter] = Json(json);

        var (isError, text) = await Call(tool, arguments);

        Assert.True(isError, $"Expected a refusal, got: {text}");
        Assert.Equal($"Error: ArgumentException: Invalid value {json} for argument '{parameter}'. {expected}", text);
    }

    [Fact]
    public async Task SeveralValuesTheirParametersDoNotTake_AreAllNamedInOneAnswer()
    {
        // The binder stops at the first value it cannot convert, which would cost the caller a
        // call per wrong value. The sentences come in the order the tool declares its parameters.
        var arguments = RequiredOf("find_types");
        arguments["limit"] = Json("\"abc\"");
        arguments["accessibility"] = Json("\"Bogus\"");

        var (isError, text) = await Call("find_types", arguments);

        Assert.True(isError, $"Expected a refusal, got: {text}");
        Assert.Equal(
            "Error: ArgumentException: " +
            $"Invalid value \"Bogus\" for argument 'accessibility'. Valid values: {Accessibilities}. " +
            "Invalid value \"abc\" for argument 'limit'. Expected an integer.",
            text);
    }

    [Fact]
    public async Task ALongRefusedValue_ComesBackCut()
    {
        string json = "[" + string.Join(",", Enumerable.Range(100, 30)) + "]";
        var arguments = RequiredOf("find_types");
        arguments["name_contains"] = Json(json);

        var (isError, text) = await Call("find_types", arguments);

        Assert.True(isError, $"Expected a refusal, got: {text}");
        Assert.Equal(
            $"Error: ArgumentException: Invalid value {json[..40]}... for argument 'name_contains'. Expected a string.",
            text);
    }

    // Tool, parameter, a value with one reading as JSON, and the form the schema has for it.
    public static TheoryData<string, string, string, string> ValuesWithOneReading => new()
    {
        // A listed name in another case, without its underscores or with spaces around it.
        { "find_types", "accessibility", "\"ALL\"", "\"all\"" },
        { "find_types", "accessibility", "\" all \"", "\"all\"" },
        { "analyze", "kind", "\"InstantiatedBy\"", "\"instantiated_by\"" },
        { "analyze", "kind", "\"instantiatedby\"", "\"instantiated_by\"" },
        { "analyze", "kind", "\"INSTANTIATED_BY\"", "\"instantiated_by\"" },
        { "analyze", "kind", "\"Instantiated_By\"", "\"instantiated_by\"" },
        // The same in a list, and for a lone name in place of a list.
        { "list_members", "kinds", "[\"Field\",\"NESTED_TYPE\"]", "[\"field\",\"nested_type\"]" },
        { "list_members", "kinds", "[\"NestedType\"]", "[\"nested_type\"]" },
        { "list_members", "kinds", "\"Field\"", "[\"field\"]" },
        // A whole number as text, with a zero fraction or with an exponent.
        { "find_types", "limit", "\"3\"", "3" },
        { "find_types", "limit", "\" 3 \"", "3" },
        { "find_types", "limit", "3.0", "3" },
        { "find_types", "limit", "\"3.0\"", "3" },
        { "find_types", "limit", "3e0", "3" },
        { "find_methods", "parameter_count", "\"1\"", "1" },
        { "find_methods", "parameter_count", "1.0", "1" },
        // A switch as text.
        { "find_types", "include_compiler_generated", "\"true\"", "true" },
        { "find_types", "include_compiler_generated", "\" TRUE \"", "true" },
        // Several parameter types in one text, on its own and as an element of a list.
        { "find_methods", "parameter_types", "\"string, int\"", "[\"string\",\"int\"]" },
        { "find_methods", "parameter_types", "[\"string, int\"]", "[\"string\",\"int\"]" },
    };

    [Theory]
    [MemberData(nameof(ValuesWithOneReading))]
    public async Task AValueWithOneReading_IsAnsweredAsTheFormTheSchemaHas(
        string tool, string parameter, string json, string listedJson)
    {
        var without = RequiredOf(tool);
        var tolerated = RequiredOf(tool);
        tolerated[parameter] = Json(json);
        var listed = RequiredOf(tool);
        listed[parameter] = Json(listedJson);

        string expected = await CallToolText(tool, listed);

        Assert.Equal(expected, await CallToolText(tool, tolerated));
        // Two answers that ignore the parameter would be equal as well, so the value has to
        // change the answer.
        Assert.NotEqual(await CallToolText(tool, without), expected);
    }

    [Theory]
    [InlineData("decompile")]
    [InlineData("analyze")]
    [InlineData("compare")]
    public async Task SeveralParameterTypesInOneText_SelectTheOverloadTheirListDoes(string tool)
    {
        // Left as one pattern, the text matches no overload, and the tool answers that
        // CallShapes.Target has no Record(string, int) while listing exactly that one.
        Dictionary<string, object?> arguments = tool == "compare"
            ? new() { ["assembly_a"] = _callShapesPath, ["assembly_b"] = _callShapesPath }
            : new() { ["assembly"] = _callShapesPath };
        arguments["type_name"] = "CallShapes.Target";
        arguments["member_name"] = "Record";
        if (tool == "analyze")
            arguments["kind"] = "used_by";

        string asList = await CallToolText(tool, new(arguments) { ["parameter_types"] = Json("[\"string\",\"int\"]") });
        string asText = await CallToolText(tool, new(arguments) { ["parameter_types"] = Json("\"string, int\"") });

        Assert.Equal(asList, asText);
    }

    [Fact]
    public async Task ACommaInsideBrackets_StaysInsideItsPattern()
    {
        // A text is split only at the commas outside angle and square brackets, so a type's own
        // arguments stay with it: one pattern alone, and two patterns in one text.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = "System.Collections.Generic.Dictionary<,>",
        };

        string one = await CallToolText("find_methods",
            new(arguments) { ["parameter_types"] = Json("\"IDictionary<TKey, TValue>\"") });
        string two = await CallToolText("find_methods",
            new(arguments) { ["parameter_types"] = Json("\"IDictionary<TKey, TValue>, IEqualityComparer<TKey>\"") });

        Assert.Equal(
            "1 method:\nSystem.Collections.Generic.Dictionary<,>..ctor(IDictionary<TKey, TValue>) → void",
            one);
        Assert.Equal(
            "1 method:\nSystem.Collections.Generic.Dictionary<,>..ctor(IDictionary<TKey, TValue>, IEqualityComparer<TKey>) → void",
            two);
    }

    [Theory]
    [InlineData("find_types", "namespace")]
    [InlineData("find_types", "accessibility")]
    [InlineData("find_types", "include_compiler_generated")]
    [InlineData("find_types", "limit")]
    [InlineData("find_methods", "parameter_count")]
    [InlineData("find_methods", "parameter_types")]
    [InlineData("list_members", "kinds")]
    public async Task NullForAnOptionalParameter_IsAnsweredAsTheParameterLeftOut(string tool, string parameter)
    {
        // The binder takes null for a text, a list or a nullable number and refuses it for the
        // rest, a difference that comes from the parameter's type in the server alone.
        var withNull = RequiredOf(tool);
        withNull[parameter] = Json("null");

        Assert.Equal(await CallToolText(tool, RequiredOf(tool)), await CallToolText(tool, withNull));
    }

    [Fact]
    public async Task ARequiredParameterLeftOutOrNull_IsNamedAsMissing()
    {
        var nothing = new Dictionary<string, object?>();
        var kindIsNull = RequiredOf("analyze");
        kindIsNull["kind"] = Json("null");

        Assert.Equal(
            (true, "Error: ArgumentException: Missing required argument 'assembly'."),
            await Call("find_types", nothing));
        Assert.Equal(
            (true, "Error: ArgumentException: Missing required argument 'assembly_a'. Missing required argument 'assembly_b'."),
            await Call("compare", nothing));
        Assert.Equal(
            (true, "Error: ArgumentException: Missing required argument 'kind'."),
            await Call("analyze", kindIsNull));
    }

    /// <summary>The arguments a tool requires, with values it answers without an error.</summary>
    private Dictionary<string, object?> RequiredOf(string tool) => tool switch
    {
        "find_types" or "find_methods" => new() { ["assembly"] = _callShapesPath },
        "list_members" => new() { ["assembly"] = _callShapesPath, ["type_name"] = "CallShapes.Vault" },
        "analyze" => new() { ["assembly"] = _callShapesPath, ["type_name"] = "CallShapes.Widget", ["kind"] = "used_by" },
        "compare" => new() { ["assembly_a"] = _callShapesPath, ["assembly_b"] = _callShapesPath },
        _ => throw new ArgumentException($"No required arguments are written down for {tool}."),
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<(bool IsError, string Text)> Call(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return (result.IsError == true, text);
    }

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        var (isError, text) = await Call(toolName, arguments);
        Assert.False(isError, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
