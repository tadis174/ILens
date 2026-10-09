using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for analyze's call_sites kind against the CallShapes fixture: <c>Senders</c> passes
/// arguments to <c>Mailbox.Send</c> in every way that can reach a parameter, and <c>Callers</c>
/// calls <c>Target.Record</c> from every shape the compiler moves out of the method it is
/// written in.
/// </summary>
public sealed class CallSiteTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public CallSiteTests(ILensServerFixture server)
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

    [Fact]
    public async Task FindCallSites_NonDefaultOnly_ListsExactlyTheCallsThatPassAnotherValue()
    {
        // Omitted, explicitly passed and lambda-passed defaults drop out; a named, reordered or
        // other-overload value, a variable, and a value from inside an iterator stay.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Mailbox",
            ["member_name"] = "Send",
            ["kind"] = "call_sites",
            ["parameter_name"] = "delay",
            ["non_default_only"] = true,
        });

        Assert.Equal(
            "5 of 8 calls to CallShapes.Mailbox.Send (2 overloads) pass delay other than its default 0:\n" +
            "CallShapes.Senders.InIterator(Mailbox): 900\n" +
            "CallShapes.Senders.Named(Mailbox): 600\n" +
            "CallShapes.Senders.OtherOverload(Mailbox): 6\n" +
            "CallShapes.Senders.Reordered(Mailbox): 60\n" +
            "CallShapes.Senders.Variable(Mailbox, int): ticks",
            text);
    }

    [Fact]
    public async Task FindCallSites_ByParameter_ShowsEveryArgumentAndMarksDefaults()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Mailbox",
            ["member_name"] = "Send",
            ["kind"] = "call_sites",
            ["parameter_name"] = "delay",
        });

        Assert.Equal(
            "8 calls to CallShapes.Mailbox.Send (2 overloads), argument for delay:\n" +
            "CallShapes.Senders.ExplicitDefault(Mailbox): 0 (default)\n" +
            "CallShapes.Senders.InIterator(Mailbox): 900\n" +
            "CallShapes.Senders.InLambda(Mailbox): 0 (default)\n" +
            "CallShapes.Senders.Named(Mailbox): 600\n" +
            "CallShapes.Senders.Omitted(Mailbox): 0 (default)\n" +
            "CallShapes.Senders.OtherOverload(Mailbox): 6\n" +
            "CallShapes.Senders.Reordered(Mailbox): 60\n" +
            "CallShapes.Senders.Variable(Mailbox, int): ticks",
            text);
    }

    [Fact]
    public async Task FindCallSites_WithoutParameter_ShowsEachCallWithEveryArgument()
    {
        // Named arguments come back positional; trailing arguments equal to their default are
        // omitted, as everywhere in decompiled code.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Mailbox",
            ["member_name"] = "Send",
            ["kind"] = "call_sites",
            ["parameter_count"] = 4,
        });

        Assert.StartsWith(
            "7 calls to CallShapes.Mailbox.Send(string, string, int, bool):\n", text);
        Assert.Contains(
            "CallShapes.Senders.Named(Mailbox): box.Send(\"named\", null, 600)", text);
        Assert.Contains(
            "CallShapes.Senders.Reordered(Mailbox): box.Send(\"reordered\", null, 60)", text);
    }

    [Fact]
    public async Task FindCallSites_OnAParamsParameter_ShowsTheExpandedArguments()
    {
        // An empty expansion passes an empty array, which must not read as a null argument.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Mailbox",
            ["member_name"] = "Tag",
            ["kind"] = "call_sites",
            ["parameter_name"] = "tags",
        });

        Assert.Equal(
            "2 calls to CallShapes.Mailbox.Tag(string, string[]), argument for tags:\n" +
            "CallShapes.Senders.TagNone(Mailbox): (none)\n" +
            "CallShapes.Senders.TagTwo(Mailbox): \"x\", \"y\"",
            text);
    }

    [Fact]
    public async Task FindCallSites_OnAConstructor_FindsTheNewExpressions()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Parcel",
            ["member_name"] = ".ctor",
            ["kind"] = "call_sites",
            ["parameter_name"] = "weight",
        });

        Assert.Equal(
            "1 call to CallShapes.Parcel..ctor(string, int), argument for weight:\n" +
            "CallShapes.Senders.OtherOverload(Mailbox): 1 (default)",
            text);
    }

    [Theory]
    [InlineData("box", "box", "box", "box")]
    [InlineData("mark", "\"stamped\"", "\"once\"", "\"static\"")]
    [InlineData("copies", "2", "1 (default)", "3")]
    public async Task FindCallSites_OnAnExtensionMethod_ShowsTheArgumentEachParameterReceives(
        string parameterName, string stamped, string stampedOnce, string stampedStatically)
    {
        // In a call in extension form, the receiver is the argument for the first parameter, and
        // the arguments in the parentheses are those for the parameters after it.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Stamps",
            ["member_name"] = "Stamp",
            ["kind"] = "call_sites",
            ["parameter_name"] = parameterName,
        });

        Assert.Equal(
            $"3 calls to CallShapes.Stamps.Stamp(Mailbox, string, int), argument for {parameterName}:\n" +
            $"CallShapes.Senders.Stamped(Mailbox): {stamped}\n" +
            $"CallShapes.Senders.StampedOnce(Mailbox): {stampedOnce}\n" +
            $"CallShapes.Senders.StampedStatically(Mailbox): {stampedStatically}",
            text);
    }

    [Fact]
    public async Task FindCallSites_OnAnExtensionMethod_NonDefaultOnly_JudgesEachParametersOwnArgument()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Stamps",
            ["member_name"] = "Stamp",
            ["kind"] = "call_sites",
            ["parameter_name"] = "copies",
            ["non_default_only"] = true,
        });

        Assert.Equal(
            "2 of 3 calls to CallShapes.Stamps.Stamp(Mailbox, string, int) pass copies other than its default 1:\n" +
            "CallShapes.Senders.Stamped(Mailbox): 2\n" +
            "CallShapes.Senders.StampedStatically(Mailbox): 3",
            text);
    }

    [Fact]
    public async Task FindCallSites_OnAnExtensionMethod_WritesEachCallInItsStaticForm()
    {
        // The static form puts each argument where the parameter it binds to stands.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Stamps",
            ["member_name"] = "Stamp",
            ["kind"] = "call_sites",
        });

        Assert.Contains("CallShapes.Senders.Stamped(Mailbox): Stamps.Stamp(box, \"stamped\", 2)", text);
    }

    // Each accessor of Gauge, Meter and Tape with every call the fixture makes to it, each
    // written with property, indexer or event syntax rather than as an invocation.
    [Theory]
    [InlineData("CallShapes.Gauge", "get_Level",
        "4 calls to CallShapes.Gauge.get_Level():\n" +
        "CallShapes.Gauge.Doubled(): Level\n" +
        "CallShapes.GaugeUsers.CompoundAssign(Gauge): gauge.Level += 3\n" +
        "CallShapes.GaugeUsers.Increment(Gauge): gauge.Level++\n" +
        "CallShapes.GaugeUsers.Read(Gauge): gauge.Level")]
    [InlineData("CallShapes.Gauge", "set_Level",
        "5 calls to CallShapes.Gauge.set_Level(int):\n" +
        "CallShapes.GaugeUsers.Assign(Gauge): gauge.Level = 5\n" +
        "CallShapes.GaugeUsers.CompoundAssign(Gauge): gauge.Level += 3\n" +
        "CallShapes.GaugeUsers.Deconstruct(Gauge): gauge.Level = 10\n" +
        "CallShapes.GaugeUsers.Increment(Gauge): gauge.Level++\n" +
        "CallShapes.GaugeUsers.Initialize(): Level = 7")]
    [InlineData("CallShapes.Gauge", "get_Item",
        "1 call to CallShapes.Gauge.get_Item(int):\n" +
        "CallShapes.GaugeUsers.ReadSlot(Gauge): gauge[2]")]
    [InlineData("CallShapes.Gauge", "set_Item",
        "2 calls to CallShapes.Gauge.set_Item(int, int):\n" +
        "CallShapes.GaugeUsers.AssignSlot(Gauge): gauge[4] = 8\n" +
        "CallShapes.GaugeUsers.Deconstruct(Gauge): gauge[1] = 11")]
    [InlineData("CallShapes.Gauge", "add_Changed",
        "1 call to CallShapes.Gauge.add_Changed(EventHandler):\n" +
        "CallShapes.GaugeUsers.Subscribe(Gauge, EventHandler): gauge.Changed += handler")]
    [InlineData("CallShapes.Gauge", "remove_Changed",
        "1 call to CallShapes.Gauge.remove_Changed(EventHandler):\n" +
        "CallShapes.GaugeUsers.Unsubscribe(Gauge, EventHandler): gauge.Changed -= handler")]
    [InlineData("CallShapes.Meter", "get_Reading",
        "1 call to CallShapes.Meter.get_Reading():\n" +
        "CallShapes.Meter..ctor(int): Reading")]
    [InlineData("CallShapes.Tape", "get_Item",
        "1 call to CallShapes.Tape.get_Item(int):\n" +
        "CallShapes.GaugeUsers.AssignByReference(Tape): tape[3]")]
    public async Task FindCallSites_OnAnAccessor_ShowsTheCallsMadeThroughItsSyntax(
        string typeName, string methodName, string expected)
    {
        // Code calls an accessor by reading, assigning, incrementing or subscribing, never by
        // invoking it. Every such call must be shown, and none reported as not shown.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["member_name"] = methodName,
            ["kind"] = "call_sites",
        });

        Assert.Equal(expected, text);
    }

    // Overrides.cs: C# compiles a call on Creature or on any type that overrides its members as
    // a call to Creature's declaration, and a call through base. as a call to the declaration it
    // names. Every call must be shown under the method the IL names, and none reported as not
    // shown. OverrideTests asks about the overrides.
    [Theory]
    [InlineData("CallShapes.Creature", "Die",
        "5 calls to CallShapes.Creature.Die(int):\n" +
        "CallShapes.Animal.Die(int): base.Die(cause)\n" +
        "CallShapes.Undertaker.OnAnimal(Animal): animal.Die(2)\n" +
        "CallShapes.Undertaker.OnCreature(Creature): creature.Die(1)\n" +
        "CallShapes.Undertaker.OnDog(Dog): dog.Die(3)\n" +
        "CallShapes.Undertaker.OnGhost(Ghost): ghost.Die(4)")]
    [InlineData("CallShapes.Creature", "get_Health",
        "3 calls to CallShapes.Creature.get_Health():\n" +
        "CallShapes.Animal.Health: base.Health\n" +
        "CallShapes.Undertaker.ReadAnimalHealth(Animal): animal.Health\n" +
        "CallShapes.Undertaker.ReadCreatureHealth(Creature): creature.Health")]
    [InlineData("CallShapes.Creature", "set_Health",
        "2 calls to CallShapes.Creature.set_Health(int):\n" +
        "CallShapes.Animal.Health: base.Health = value\n" +
        "CallShapes.Undertaker.SetDogHealth(Dog): dog.Health = 5")]
    public async Task FindCallSites_OnAVirtualMember_ShowsTheCallsOnEveryOverridingType(
        string typeName, string methodName, string expected)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["member_name"] = methodName,
            ["kind"] = "call_sites",
        });

        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task FindCallSites_OnASetter_ShowsTheValueEachCallPasses()
    {
        // A compound assignment or an increment passes the setter the computed value.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Gauge",
            ["member_name"] = "set_Level",
            ["kind"] = "call_sites",
            ["parameter_name"] = "value",
        });

        Assert.Equal(
            "5 calls to CallShapes.Gauge.set_Level(int), argument for value:\n" +
            "CallShapes.GaugeUsers.Assign(Gauge): 5\n" +
            "CallShapes.GaugeUsers.CompoundAssign(Gauge): gauge.Level + 3\n" +
            "CallShapes.GaugeUsers.Deconstruct(Gauge): 10\n" +
            "CallShapes.GaugeUsers.Increment(Gauge): gauge.Level + 1\n" +
            "CallShapes.GaugeUsers.Initialize(): 7",
            text);
    }

    [Fact]
    public async Task FindCallSites_OnAnIndexerSetter_ShowsTheIndexEachCallPasses()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Gauge",
            ["member_name"] = "set_Item",
            ["kind"] = "call_sites",
            ["parameter_name"] = "slot",
        });

        Assert.Equal(
            "2 calls to CallShapes.Gauge.set_Item(int, int), argument for slot:\n" +
            "CallShapes.GaugeUsers.AssignSlot(Gauge): 4\n" +
            "CallShapes.GaugeUsers.Deconstruct(Gauge): 1",
            text);
    }

    [Fact]
    public async Task FindCallSites_ReportsACallTheDecompiledCodeDoesNotShowAsACall()
    {
        // The IL calls op_Addition, the C# says `box + parcel`. The call must not vanish from
        // the answer: a count of zero would read as proof that nothing calls it.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Mailbox",
            ["member_name"] = "op_Addition",
            ["kind"] = "call_sites",
        });

        Assert.Contains(
            "Not shown: 1 of 1 call in CallShapes.Senders.AsOperator(Mailbox, Parcel), " +
            "which the decompiled C# does not show as a call", text);
    }

    [Fact]
    public async Task FindCallSites_AttributesCallsInGeneratedCodeToTheirSourceMember()
    {
        // Callers calls Target.Record from each shape the compiler moves out of the method it
        // is written in, and from ordinary bodies as controls; each call must come back under
        // its source member with nothing left unshown, and each of the two indexers under its
        // own indices.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Target",
            ["member_name"] = "Record",
            ["kind"] = "call_sites",
            ["parameter_name"] = "label",
        });

        string[] lines = text.Split('\n');
        Assert.Equal("11 calls to CallShapes.Target.Record(string, int), argument for label:", lines[0]);
        Assert.DoesNotContain(lines, line => line.StartsWith("Not shown", StringComparison.Ordinal));
        Assert.Contains("CallShapes.Callers.InIterator(): \"iterator\"", lines);
        Assert.Contains("CallShapes.Callers.InAsyncMethod(): \"async\"", lines);
        Assert.Contains("CallShapes.Callers.InPropertyGetter: \"property\"", lines);
        Assert.Contains("CallShapes.Callers.Item[int]: \"indexer\"", lines);
        Assert.Contains("CallShapes.Callers.Item[int, int]: \"two-index indexer\"", lines);
        Assert.Contains("CallShapes.Outer.Nested.InLocalFunction(): \"nested local function\"", lines);
    }

    [Fact]
    public async Task FindCallSites_WithoutMatchingCalls_SaysSo()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Callers",
            ["member_name"] = "Direct",
            ["kind"] = "call_sites",
        });

        Assert.Equal("No calls to CallShapes.Callers.Direct()", text);
    }

    [Theory]
    [InlineData(null, true, "needs parameter_name")]
    [InlineData("weight", false, "has a parameter named 'weight'")]
    [InlineData("label", true, "'label' has no default value")]
    public async Task FindCallSites_RejectsAQuestionItCannotAnswer(
        string? parameterName, bool nonDefaultOnly, string expected)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Mailbox",
            ["member_name"] = "Send",
            ["kind"] = "call_sites",
            ["non_default_only"] = nonDefaultOnly,
        };
        if (parameterName is not null)
            arguments["parameter_name"] = parameterName;

        CallToolResult result = await _server.Client.CallToolAsync("analyze", arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        Assert.True(result.IsError, $"Expected a rejection, got: {text}");
        Assert.Contains(expected, text);
    }

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
