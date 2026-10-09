using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for how analyze attributes code the compiler moves out of the method it
/// is written in. The fixture under Tests/Fixtures/CallShapes/ compiles to CallShapes.dll
/// alongside the shared inspection target; each of its callers makes the same uses of
/// <c>CallShapes.Target</c> and <c>CallShapes.Widget</c> in a different compiler-generated
/// shape.
/// </summary>
public sealed class CallShapesTests : IClassFixture<ILensServerFixture>
{
    private const string Record = "CallShapes.Target.Record(string, int)";

    // Every member that makes the fixture's uses, each named the way analyze prints it.
    private static readonly string[] CallerReferences =
    [
        "CallShapes.Callers.Direct()",
        "CallShapes.Callers.InLambda()",
        "CallShapes.Callers.InCapturingLambda(int)",
        "CallShapes.Callers.InPropertyGetter",
        "CallShapes.Callers.Item[int]",
        "CallShapes.Callers.Item[int, int]",
        "CallShapes.Callers.InLocalFunction()",
        "CallShapes.Callers.InIterator()",
        "CallShapes.Callers.InAsyncMethod()",
        "CallShapes.Outer.Nested.InLambda()",
        "CallShapes.Outer.Nested.InLocalFunction()",
    ];

    // The same members as type and member name, for the questions asked about a caller.
    // The two indexers are left out: their shared name Item names neither on its own.
    public static TheoryData<string, string> CallerMembers => new()
    {
        { "CallShapes.Callers", "Direct" },
        { "CallShapes.Callers", "InLambda" },
        { "CallShapes.Callers", "InCapturingLambda" },
        { "CallShapes.Callers", "InPropertyGetter" },
        { "CallShapes.Callers", "InLocalFunction" },
        { "CallShapes.Callers", "InIterator" },
        { "CallShapes.Callers", "InAsyncMethod" },
        { "CallShapes.Outer.Nested", "InLambda" },
        { "CallShapes.Outer.Nested", "InLocalFunction" },
    };

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public CallShapesTests(ILensServerFixture server)
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

    // One row per ILSpyX analyzer behind an incoming question, since each treats generated
    // code differently: a plain call, a field read, a field write, a virtual call, an
    // instantiation, and a use of the type as a whole.
    public static TheoryData<string, string?, string> IncomingQuestions => new()
    {
        { "CallShapes.Target", "Record", "used_by" },
        { "CallShapes.Target", "Counter", "read_by" },
        { "CallShapes.Target", "Counter", "assigned_by" },
        { "CallShapes.Widget", "Ping", "used_by" },
        { "CallShapes.Widget", null, "instantiated_by" },
        { "CallShapes.Widget", null, "used_by" },
    };

    [Theory]
    [MemberData(nameof(IncomingQuestions))]
    public async Task Analyze_IncomingKinds_ReportEachUseUnderTheMemberItIsWrittenIn(
        string typeName, string? memberName, string kind)
    {
        // A use inside a lambda, local function, iterator or async method lives in a
        // method the compiler generated. Reporting it under that method would name
        // something that appears nowhere in source, such as "<>c.<InLambda>b__1_0".
        // The exact set also rules out reporting it twice, under both names.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["kind"] = kind,
        };
        if (memberName is not null)
            arguments["member_name"] = memberName;

        string text = await CallToolText("analyze", arguments);

        Assert.Equal(CallerReferences.Order(), ResultLines(text).Order());
    }

    [Fact]
    public async Task Analyze_HidesWhatTheCompilerGeneratedUnlessAskedFor_AndSortsTheRest()
    {
        // An iterator's body moves into a state machine the compiler generates, and what that
        // body uses includes the state machine's own members.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Callers",
            ["member_name"] = "InIterator",
            ["kind"] = "uses",
        };

        string[] hidden = ResultLines(await CallToolText("analyze", arguments));
        string[] shown = ResultLines(await CallToolText("analyze", new(arguments) { ["include_compiler_generated"] = true }));

        Assert.DoesNotContain(hidden, line => line.Contains("<InIterator>"));
        Assert.Contains(shown, line => line.Contains("<InIterator>"));
        Assert.Contains(Record, hidden);
        Assert.Equal(hidden.Order(StringComparer.Ordinal), hidden);
    }

    [Theory]
    [MemberData(nameof(CallerMembers))]
    public async Task Analyze_Uses_CoversCodeTheCompilerMovedOutOfTheMember(
        string typeName, string memberName)
    {
        // Uses scans every body that belongs to the member. For an iterator or async
        // method the method's own body only constructs the state machine, so scanning
        // it alone misses the call entirely. Direct is the control: its call sits in
        // its own body.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = "uses",
        });

        Assert.Contains(Record, ResultLines(text));
    }

    // A field and the lines decompile gives it after its header: the initializer the compiler
    // moved into a constructor is moved back, a field only a constructor body assigns has none,
    // and a using declaration appears only for the field that needs it.
    public static TheoryData<string, string, string[]> FieldDeclarations => new()
    {
        { "CallShapes.Settings", "_retries", ["private int _retries = 3;"] },
        { "CallShapes.Settings", "Label", ["public static readonly string Label = \"settings\";"] },
        { "CallShapes.Settings", "_log", ["using System.Text;", "private readonly StringBuilder _log = new StringBuilder();"] },
        { "CallShapes.Settings", "Twice", ["public readonly int Twice;"] },
        { "CallShapes.Gauge", "_slots", ["private readonly int[] _slots = new int[8];"] },
    };

    [Theory]
    [MemberData(nameof(FieldDeclarations))]
    public async Task Decompile_OnAField_ShowsItsDeclarationWithTheInitializer(
        string typeName, string fieldName, string[] expected)
    {
        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["member_name"] = fieldName,
        });

        // The declaration alone: the constructors and the other fields decompiled beside it to
        // recover the initializer are gone again, and so are the using declarations they need.
        var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        Assert.Equal($"// {typeName}.{fieldName}", lines[0]);
        Assert.Equal(expected, lines[1..]);
    }

    [Fact]
    public async Task Decompile_OnAConstructor_ReachesItByItsILName()
    {
        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Settings",
            ["member_name"] = ".ctor",
        });

        Assert.Equal("// CallShapes.Settings..ctor(int) → void", text.Split('\n')[0]);
        Assert.Contains("Twice = value * 2;", text);
    }

    private const string SettingsMembers =
        "7 members of public class CallShapes.Settings:\n" +
        "1 method:\n" +
        "  public Settings(int value)\n" +
        "3 properties:\n" +
        "  [Obsolete] public int Attempts { get; }\n" +
        "  public string Log { get; }\n" +
        "  public int Retries { get; }\n" +
        "2 fields:\n" +
        "  public static readonly string Label\n" +
        "  public readonly int Twice\n" +
        "1 nested type:\n" +
        "  CallShapes.Settings.Section";

    [Fact]
    public async Task ListMembers_ShowsTheDeclarationTheConstructorsTheNestedTypesAndWhatIsObsolete()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Settings",
        });

        Assert.Equal(SettingsMembers, text);
    }

    [Fact]
    public async Task ListMembers_CutByLimit_CountsEveryMemberAndLeavesOutTheGroupsAfterTheCut()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Settings",
            ["limit"] = 2,
        });

        Assert.Equal(
            string.Join("\n", SettingsMembers.Split('\n').Take(5)) + "\n... (5 more; raise limit to see all)",
            text);
    }

    [Fact]
    public async Task ListMembers_WithNothingLeft_NamesTheTypeItLookedAt()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Settings",
            ["name_contains"] = "NoSuchMember",
        });

        Assert.Equal("No members of public class CallShapes.Settings", text);
    }

    [Fact]
    public async Task ListMembers_HidesCompilerGeneratedMembersUnlessAskedFor()
    {
        // Level is an auto-property, so the compiler adds a private field for its value.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Gauge",
            ["kinds"] = new[] { "field" },
            ["accessibility"] = "all",
        };

        string hidden = await CallToolText("list_members", arguments);
        string shown = await CallToolText("list_members", new(arguments) { ["include_compiler_generated"] = true });

        Assert.DoesNotContain("<Level>k__BackingField", hidden);
        Assert.Contains("  private int <Level>k__BackingField", shown.Split('\n'));
    }

    [Fact]
    public async Task FindMethods_ListsConstructorsUnderTheirILName()
    {
        // Settings' properties are accessors, which find_methods leaves out, so its constructor
        // is the one method it lists.
        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Settings",
        });

        Assert.Equal("1 method:\nCallShapes.Settings..ctor(int) → void", text);
    }

    [Fact]
    public async Task FindMethods_HidesWhatTheCompilerGeneratedUnlessAskedFor_AndSortsTheRest()
    {
        // A local function compiles to a method named after the method it is written in.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Callers",
            ["accessibility"] = "all",
        };

        string[] hidden = ResultLines(await CallToolText("find_methods", arguments));
        string[] shown = ResultLines(await CallToolText("find_methods", new(arguments) { ["include_compiler_generated"] = true }));

        Assert.DoesNotContain(hidden, line => line.Contains("<InLocalFunction>"));
        Assert.Contains(shown, line => line.Contains("<InLocalFunction>"));
        string Reference(string line) => line.StartsWith("static ") ? line["static ".Length..] : line;
        Assert.Equal(hidden.OrderBy(Reference, StringComparer.Ordinal), hidden);
    }

    /// <summary>The result lines of an <c>analyze</c> response, with its header line dropped.</summary>
    private static string[] ResultLines(string analyzeOutput) =>
        analyzeOutput.Split('\n').Skip(1).ToArray();

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
