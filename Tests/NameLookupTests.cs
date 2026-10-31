using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the names a lookup reads by a rule of C#: a name a class and its base
/// class use for different kinds of member, which means the member of the more derived class;
/// an indexer's usual name on a class that gave its indexer another; a constructor asked for
/// by the name of its type, as list_members prints it; and an extension method, which a name
/// reaches past every member and only on the class its first parameter takes. The fixture is
/// the namespace ExplicitMembers.Names in Tests/Fixtures/ExplicitMembers/.
/// </summary>
public sealed class NameLookupTests : IClassFixture<ILensServerFixture>
{
    private const string Names = "ExplicitMembers.Names";

    private readonly ILensServerFixture _server;
    private readonly string _fixturePath;

    public NameLookupTests(ILensServerFixture server)
    {
        _server = server;
        _fixturePath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "ExplicitMembers.dll");
        if (!File.Exists(_fixturePath))
            throw new FileNotFoundException(
                $"ExplicitMembers fixture not found: {_fixturePath}. " +
                "Tests/Fixtures/ExplicitMembers/ is expected to land here via Tests.csproj's " +
                "ProjectReference.");
    }

    // The fixture type and the name decompile is asked for, an optional parameter count, and
    // the header line of the answer, which names what was found.
    public static TheoryData<string, string, int?, string> Lookups => new()
    {
        // The member of the more derived class, whatever kind the base class's member is.
        { "FieldOverMethod", "Value", null, $"// {Names}.FieldOverMethod.Value" },
        { "MethodOverProperty", "Count", null, $"// {Names}.MethodOverProperty.Count() → int" },
        { "PropertyOverField", "Size", null, $"// {Names}.PropertyOverField.Size" },
        { "FieldOverEvent", "Changed", null, $"// {Names}.FieldOverEvent.Changed" },
        // The event's own subscriber list still counts as part of the event.
        { "EventOverMethod", "Run", null, $"// {Names}.EventOverMethod.Run" },
        { "Far", "Value", null, $"// {Names}.Far.Value" },
        { "Middle", "Value", null, $"// {Names}.Middle.Value() → int [inherited from {Names}.Base]" },
        // A hint asks for a method, so it passes the field over.
        {
            "FieldOverMethod", "Value", 0,
            $"// {Names}.FieldOverMethod.Value() → int [inherited from {Names}.Base]"
        },
        // An extension method lies past every member.
        { "Labeled", "Label", null, $"// {Names}.Labeled.Label" },
        {
            "Labeled", "Label", 1,
            $"// {Names}.LabeledExtensions.Label(Labeled) → int [extension method on {Names}.Labeled]"
        },
        // An extension method of the name for another class is passed over.
        {
            "Stamped", "Stamp", 1,
            $"// {Names}.StampExtensions.Stamp(Stamped) → int [extension method on {Names}.Stamped]"
        },
        // The type's name reaches its constructors.
        { "Single", "Single", null, $"// {Names}.Single..ctor(string) → void" },
        { "Sub", "Sub", null, $"// {Names}.Sub..ctor() → void" },
        { "Generic<>", "Generic", null, $"// {Names}.Generic<>..ctor(T) → void" },
        { "Outer.Inner", "Inner", null, $"// {Names}.Outer.Inner..ctor(int) → void" },
        { "Ctors", "Ctors", 1, $"// {Names}.Ctors..ctor(int) → void" },
        // A struct's one declared constructor needs no hint, and its field decompiles.
        { "SpotWithCtor", ".ctor", null, $"// {Names}.SpotWithCtor..ctor(int) → void" },
        { "SpotWithCtor", "SpotWithCtor", null, $"// {Names}.SpotWithCtor..ctor(int) → void" },
        { "Spot", "X", null, $"// {Names}.Spot.X" },
    };

    [Theory]
    [MemberData(nameof(Lookups))]
    public async Task Decompile_ReadsANameAsCSharpDoes(
        string typeName, string memberName, int? parameterCount, string header)
    {
        var arguments = Question(typeName, memberName);
        if (parameterCount is not null)
            arguments["parameter_count"] = parameterCount;

        string text = await CallToolText("decompile", arguments);

        Assert.Equal(header, text.Split('\n')[0]);
    }

    // A fixture type, a member name and an analyze kind, and the header line of the answer.
    public static TheoryData<string, string, string, string> AnalyzeQuestions => new()
    {
        { "FieldOverMethod", "Value", "read_by", $"1 reader of {Names}.FieldOverMethod.Value [declared]:" },
        { "MethodOverProperty", "Count", "used_by", $"No users of {Names}.MethodOverProperty.Count() [declared]" },
        {
            "EventOverMethod", "Run", "used_by",
            $"No users of {Names}.EventOverMethod.Run [declared] (via add_Run, remove_Run, the subscriber list)"
        },
        // A question about calls asks for a method, as a hint does.
        { "FieldOverMethod", "Value", "call_sites", $"1 call to {Names}.Base.Value():" },
        { "Single", "Single", "used_by", $"No users of {Names}.Single..ctor(string) [declared]" },
        { "Generic<>", "Generic", "used_by", $"1 user of {Names}.Generic<>..ctor(T) [declared]:" },
        { "Ctors", "Ctors", "call_sites", $"2 calls to {Names}.Ctors..ctor (2 overloads):" },
        { "SpotWithCtor", ".ctor", "call_sites", $"1 call to {Names}.SpotWithCtor..ctor(int):" },
    };

    [Theory]
    [MemberData(nameof(AnalyzeQuestions))]
    public async Task Analyze_ReadsANameAsCSharpDoes(string typeName, string memberName, string kind, string header)
    {
        var arguments = Question(typeName, memberName);
        arguments["kind"] = kind;

        string text = await CallToolText("analyze", arguments);

        Assert.Equal(header, text.Split('\n')[0]);
    }

    [Fact]
    public async Task Compare_ByTheTypesName_ComparesTheConstructor()
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = _fixturePath,
            ["assembly_b"] = _fixturePath,
            ["type_name"] = $"{Names}.Single",
            ["member_name"] = "Single",
        });

        Assert.Equal($"// {Names}.Single..ctor(string) → void — bodies are identical in both assemblies.", text);
    }

    private const string NotFoundOn = "its base types, or as an extension method";

    private const string NoStructConstructor =
        "; a struct that declares no constructor has none in the assembly, and instantiated_by lists where it is created.";

    // The tool, the fixture type and the name asked for, and the whole error.
    public static TheoryData<string, string, string, string> Refusals => new()
    {
        // The usual name of an indexer the class named Entry: the error names what it has.
        {
            "decompile", "Renamed", "get_Item",
            $"Member 'get_Item' not found on {Names}.Renamed, {NotFoundOn}; its indexer is named Entry: " +
            $"{Names}.Renamed.get_Entry(int), {Names}.Renamed.set_Entry(int, int)."
        },
        {
            "decompile", "Renamed", "Item",
            $"Member 'Item' not found on {Names}.Renamed, {NotFoundOn}; its indexer is named Entry: " +
            $"{Names}.Renamed.get_Entry(int), {Names}.Renamed.set_Entry(int, int)."
        },
        {
            "call_sites", "Renamed", "set_Item",
            $"Method 'set_Item' not found on {Names}.Renamed, {NotFoundOn}; its indexer is named Entry: " +
            $"{Names}.Renamed.get_Entry(int), {Names}.Renamed.set_Entry(int, int)."
        },
        {
            "decompile", "RenamedTwice", "get_Item",
            $"Member 'get_Item' not found on {Names}.RenamedTwice, {NotFoundOn}; its indexer is named Entry: " +
            $"{Names}.RenamedTwice.get_Entry(int), {Names}.RenamedTwice.get_Entry(string), " +
            $"{Names}.RenamedTwice.set_Entry(string, int)."
        },
        {
            "decompile", "RenamedReadOnly", "set_Item",
            $"Member 'set_Item' not found on {Names}.RenamedReadOnly, {NotFoundOn}; its indexer is named Entry: " +
            $"{Names}.RenamedReadOnly.get_Entry(int)."
        },
        {
            "used_by", "DerivedOfRenamed", "get_Item",
            $"Member 'get_Item' not found on {Names}.DerivedOfRenamed, {NotFoundOn}; its indexer is named Entry: " +
            $"{Names}.Renamed.get_Entry(int), {Names}.Renamed.set_Entry(int, int)."
        },
        // A class without an indexer gets no such clause.
        { "decompile", "Single", "get_Item", $"Member 'get_Item' not found on {Names}.Single, {NotFoundOn}." },
        // Several constructors are told apart like any overloads.
        {
            "decompile", "Ctors", "Ctors",
            $"'.ctor' on {Names}.Ctors has 2 matching overloads; specify parameter_count or parameter_types " +
            $"to disambiguate: {Names}.Ctors..ctor(), {Names}.Ctors..ctor(int)."
        },
        // So are two extension methods of the name for the class, in their order, without the
        // one of the name for another class.
        {
            "decompile", "Stamped", "Stamp",
            "'Stamp' on (extension methods) has 2 matching overloads; specify parameter_count or parameter_types " +
            $"to disambiguate: {Names}.StampExtensions.Stamp(Stamped), {Names}.StampExtensions.Stamp(Stamped, string)."
        },
        // A name only another class's extension method has.
        { "decompile", "Stamped", "Mark", $"Member 'Mark' not found on {Names}.Stamped, {NotFoundOn}." },
        // A constructor is not inherited, so the base class's name reaches nothing.
        { "decompile", "Sub", "Ctors", $"Member 'Ctors' not found on {Names}.Sub, {NotFoundOn}." },
        {
            "decompile", "OnlyStatic", "OnlyStatic",
            $"Member 'OnlyStatic' not found on {Names}.OnlyStatic, {NotFoundOn}; " +
            "its static constructor is named '.cctor'."
        },
        // A struct or enum that declares no constructor has none in the file.
        { "decompile", "Spot", ".ctor", $"Member '.ctor' not found on {Names}.Spot, {NotFoundOn}{NoStructConstructor}" },
        { "used_by", "Spot", "Spot", $"Member 'Spot' not found on {Names}.Spot, {NotFoundOn}{NoStructConstructor}" },
        { "call_sites", "Spot", ".ctor", $"Method '.ctor' not found on {Names}.Spot, {NotFoundOn}{NoStructConstructor}" },
        {
            "decompile", "Tint", ".ctor",
            $"Member '.ctor' not found on {Names}.Tint, {NotFoundOn}; an enum has no constructor."
        },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ANameTheTypeLacks_IsRefusedWithWhatTheTypeHasInstead(
        string question, string typeName, string memberName, string error)
    {
        var arguments = Question(typeName, memberName);
        if (question != "decompile")
            arguments["kind"] = question;

        string text = await CallToolError(question == "decompile" ? "decompile" : "analyze", arguments);

        Assert.Equal($"Error: ArgumentException: {error}", text);
    }

    // A type, a hint no constructor of it satisfies, and the whole error. A constructor is not
    // inherited, so the base class's constructor that would satisfy it is not offered.
    public static TheoryData<string, int?, string?, string> ConstructorHints => new()
    {
        {
            "SpotWithCtor", 0, null,
            $"No overload of '.ctor' on {Names}.SpotWithCtor matches 0 parameter(s). " +
            $"Available: {Names}.SpotWithCtor..ctor(int)."
        },
        {
            "Sub", null, "int",
            $"No overload of '.ctor' on {Names}.Sub matches parameter types (int). Available: {Names}.Sub..ctor()."
        },
    };

    [Theory]
    [MemberData(nameof(ConstructorHints))]
    public async Task Decompile_AConstructorHintTheTypeDoesNotSatisfy_ListsItsOwnConstructors(
        string typeName, int? parameterCount, string? parameterType, string error)
    {
        var arguments = Question(typeName, ".ctor");
        if (parameterCount is not null)
            arguments["parameter_count"] = parameterCount;
        if (parameterType is not null)
            arguments["parameter_types"] = new[] { parameterType };

        string text = await CallToolError("decompile", arguments);

        Assert.Equal($"Error: ArgumentException: {error}", text);
    }

    [Fact]
    public async Task TheLists_ShowNoConstructorForAStructOrEnumThatDeclaresNone()
    {
        string members = await CallToolText("list_members", new()
        {
            ["assembly"] = _fixturePath,
            ["type_name"] = $"{Names}.Spot",
            ["accessibility"] = "all",
        });
        Assert.Equal($"1 member of public struct {Names}.Spot:\n1 field:\n  public int X", members);

        string methods = await CallToolText("find_methods", new()
        {
            ["assembly"] = _fixturePath,
            ["type_name"] = $"{Names}.Tint",
            ["accessibility"] = "all",
        });
        Assert.Equal("No methods", methods);

        string withOne = await CallToolText("find_methods", new()
        {
            ["assembly"] = _fixturePath,
            ["type_name"] = $"{Names}.SpotWithCtor",
            ["accessibility"] = "all",
        });
        Assert.Equal($"1 method:\n{Names}.SpotWithCtor..ctor(int) → void", withOne);
    }

    [Fact]
    public async Task InstantiatedBy_OnAStructWithoutAConstructor_ListsWhereItIsCreated()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _fixturePath,
            ["type_name"] = $"{Names}.Spot",
            ["kind"] = "instantiated_by",
        });

        Assert.Equal($"1 symbol instantiating {Names}.Spot:\n{Names}.SpotMaker.Make()", text);
    }

    [Fact]
    public async Task Compare_TheConstructorOfAStructWithoutOne_IsRefused()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly_a"] = _fixturePath,
            ["assembly_b"] = _fixturePath,
            ["type_name"] = $"{Names}.Spot",
            ["member_name"] = ".ctor",
            ["parameter_count"] = 0,
        };

        string text = await CallToolError("compare", arguments);

        Assert.Equal(
            $"Error: ArgumentException: Method '.ctor' not found on {Names}.Spot, {NotFoundOn}{NoStructConstructor}",
            text);
    }

    [Fact]
    public async Task Decompile_TheUsualAccessorNameOnString_NamesItsIndexersAccessor()
    {
        string text = await CallToolError("decompile", new()
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = "System.String",
            ["member_name"] = "get_Item",
        });

        Assert.Equal(
            $"Error: ArgumentException: Member 'get_Item' not found on System.String, {NotFoundOn}; " +
            "its indexer is named Chars: System.String.get_Chars(int).",
            text);
    }

    [Fact]
    public async Task ANameOneTypeDeclaresAsTwoKinds_IsAmbiguous_AndAMemberNamedLikeItsTypeIsNoConstructor()
    {
        // IL allows both and C# neither, so the assembly is written here.
        var path = SameNameAssembly.Write();
        try
        {
            string ambiguous = await CallToolError("decompile", new()
            {
                ["assembly"] = path,
                ["type_name"] = "SameName.Twice",
                ["member_name"] = "Value",
            });
            Assert.Equal(
                "Error: ArgumentException: Member 'Value' on SameName.Twice is ambiguous — it exists as more " +
                "than one member kind (Method, Field). Pass a parameter_count / parameter_types hint if you " +
                "mean the method; the type's decompiled code shows them all.",
                ambiguous);

            string named = await CallToolText("decompile", new()
            {
                ["assembly"] = path,
                ["type_name"] = "SameName.Named",
                ["member_name"] = "Named",
            });
            Assert.Equal("// SameName.Named.Named() → int", named.Split('\n')[0]);
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    private Dictionary<string, object?> Question(string typeName, string memberName) => new()
    {
        ["assembly"] = _fixturePath,
        ["type_name"] = $"{Names}.{typeName}",
        ["member_name"] = memberName,
    };

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }

    private async Task<string> CallToolError(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError, $"Expected an error from '{toolName}', got: {text}");
        return text;
    }
}
