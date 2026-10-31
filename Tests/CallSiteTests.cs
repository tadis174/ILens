using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for analyze's call_sites kind against the CallShapes fixture: <c>Senders</c> passes
/// arguments to <c>Mailbox.Send</c> in every way that can reach a parameter, <c>Callers</c>
/// calls <c>Target.Record</c> from every shape the compiler moves out of the method it is
/// written in, <c>Ringers</c> calls <c>Bell.Ring</c> once and several times from one method,
/// and the classes of <c>Forms.cs</c> make calls in every form C# writes without call syntax.
/// </summary>
public sealed class CallSiteTests : IClassFixture<ILensServerFixture>
{
    private const string Forms = "CallShapes.Forms.";

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
            "CallShapes.Senders.InIterator(Mailbox)\n  900\n" +
            "CallShapes.Senders.Named(Mailbox)\n  600\n" +
            "CallShapes.Senders.OtherOverload(Mailbox)\n  6\n" +
            "CallShapes.Senders.Reordered(Mailbox)\n  60\n" +
            "CallShapes.Senders.Variable(Mailbox, int)\n  ticks",
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
            "CallShapes.Senders.ExplicitDefault(Mailbox)\n  0 (default)\n" +
            "CallShapes.Senders.InIterator(Mailbox)\n  900\n" +
            "CallShapes.Senders.InLambda(Mailbox)\n  0 (default)\n" +
            "CallShapes.Senders.Named(Mailbox)\n  600\n" +
            "CallShapes.Senders.Omitted(Mailbox)\n  0 (default)\n" +
            "CallShapes.Senders.OtherOverload(Mailbox)\n  6\n" +
            "CallShapes.Senders.Reordered(Mailbox)\n  60\n" +
            "CallShapes.Senders.Variable(Mailbox, int)\n  ticks",
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
        string[] lines = text.Split('\n');
        AssertCalls(lines, "CallShapes.Senders.Named(Mailbox)", "box.Send(\"named\", null, 600)");
        AssertCalls(lines, "CallShapes.Senders.Reordered(Mailbox)", "box.Send(\"reordered\", null, 60)");
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
            "CallShapes.Senders.TagNone(Mailbox)\n  (none)\n" +
            "CallShapes.Senders.TagTwo(Mailbox)\n  \"x\", \"y\"",
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
            "CallShapes.Senders.OtherOverload(Mailbox)\n  1 (default)",
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
            $"CallShapes.Senders.Stamped(Mailbox)\n  {stamped}\n" +
            $"CallShapes.Senders.StampedOnce(Mailbox)\n  {stampedOnce}\n" +
            $"CallShapes.Senders.StampedStatically(Mailbox)\n  {stampedStatically}",
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
            "CallShapes.Senders.Stamped(Mailbox)\n  2\n" +
            "CallShapes.Senders.StampedStatically(Mailbox)\n  3",
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

        AssertCalls(text.Split('\n'), "CallShapes.Senders.Stamped(Mailbox)", "Stamps.Stamp(box, \"stamped\", 2)");
    }

    // Each accessor of Gauge, Meter and Tape with every call the fixture makes to it, each
    // written with property, indexer or event syntax rather than as an invocation.
    [Theory]
    [InlineData("CallShapes.Gauge", "get_Level",
        "4 calls to CallShapes.Gauge.get_Level():\n" +
        "CallShapes.Gauge.Doubled()\n  Level\n" +
        "CallShapes.GaugeUsers.CompoundAssign(Gauge)\n  gauge.Level += 3\n" +
        "CallShapes.GaugeUsers.Increment(Gauge)\n  gauge.Level++\n" +
        "CallShapes.GaugeUsers.Read(Gauge)\n  gauge.Level")]
    [InlineData("CallShapes.Gauge", "set_Level",
        "5 calls to CallShapes.Gauge.set_Level(int):\n" +
        "CallShapes.GaugeUsers.Assign(Gauge)\n  gauge.Level = 5\n" +
        "CallShapes.GaugeUsers.CompoundAssign(Gauge)\n  gauge.Level += 3\n" +
        "CallShapes.GaugeUsers.Deconstruct(Gauge)\n  gauge.Level = 10\n" +
        "CallShapes.GaugeUsers.Increment(Gauge)\n  gauge.Level++\n" +
        "CallShapes.GaugeUsers.Initialize()\n  Level = 7")]
    [InlineData("CallShapes.Gauge", "get_Item",
        "1 call to CallShapes.Gauge.get_Item(int):\n" +
        "CallShapes.GaugeUsers.ReadSlot(Gauge)\n  gauge[2]")]
    [InlineData("CallShapes.Gauge", "set_Item",
        "2 calls to CallShapes.Gauge.set_Item(int, int):\n" +
        "CallShapes.GaugeUsers.AssignSlot(Gauge)\n  gauge[4] = 8\n" +
        "CallShapes.GaugeUsers.Deconstruct(Gauge)\n  gauge[1] = 11")]
    [InlineData("CallShapes.Gauge", "add_Changed",
        "1 call to CallShapes.Gauge.add_Changed(EventHandler):\n" +
        "CallShapes.GaugeUsers.Subscribe(Gauge, EventHandler)\n  gauge.Changed += handler")]
    [InlineData("CallShapes.Gauge", "remove_Changed",
        "1 call to CallShapes.Gauge.remove_Changed(EventHandler):\n" +
        "CallShapes.GaugeUsers.Unsubscribe(Gauge, EventHandler)\n  gauge.Changed -= handler")]
    [InlineData("CallShapes.Meter", "get_Reading",
        "1 call to CallShapes.Meter.get_Reading():\n" +
        "CallShapes.Meter..ctor(int)\n  Reading")]
    [InlineData("CallShapes.Tape", "get_Item",
        "1 call to CallShapes.Tape.get_Item(int):\n" +
        "CallShapes.GaugeUsers.AssignByReference(Tape)\n  tape[3]")]
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
        "CallShapes.Animal.Die(int)\n  base.Die(cause)\n" +
        "CallShapes.Undertaker.OnAnimal(Animal)\n  animal.Die(2)\n" +
        "CallShapes.Undertaker.OnCreature(Creature)\n  creature.Die(1)\n" +
        "CallShapes.Undertaker.OnDog(Dog)\n  dog.Die(3)\n" +
        "CallShapes.Undertaker.OnGhost(Ghost)\n  ghost.Die(4)")]
    [InlineData("CallShapes.Creature", "get_Health",
        "3 calls to CallShapes.Creature.get_Health():\n" +
        "CallShapes.Animal.Health\n  base.Health\n" +
        "CallShapes.Undertaker.ReadAnimalHealth(Animal)\n  animal.Health\n" +
        "CallShapes.Undertaker.ReadCreatureHealth(Creature)\n  creature.Health")]
    [InlineData("CallShapes.Creature", "set_Health",
        "2 calls to CallShapes.Creature.set_Health(int):\n" +
        "CallShapes.Animal.Health\n  base.Health = value\n" +
        "CallShapes.Undertaker.SetDogHealth(Dog)\n  dog.Health = 5")]
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
            "CallShapes.GaugeUsers.Assign(Gauge)\n  5\n" +
            "CallShapes.GaugeUsers.CompoundAssign(Gauge)\n  gauge.Level + 3\n" +
            "CallShapes.GaugeUsers.Deconstruct(Gauge)\n  10\n" +
            "CallShapes.GaugeUsers.Increment(Gauge)\n  gauge.Level + 1\n" +
            "CallShapes.GaugeUsers.Initialize()\n  7",
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
            "CallShapes.GaugeUsers.AssignSlot(Gauge)\n  4\n" +
            "CallShapes.GaugeUsers.Deconstruct(Gauge)\n  1",
            text);
    }

    [Fact]
    public async Task FindCallSites_ShowsACallWrittenAsAnOperator()
    {
        // The IL calls op_Addition, the C# says `box + parcel`.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Mailbox",
            ["member_name"] = "op_Addition",
            ["kind"] = "call_sites",
        });

        Assert.Equal(
            "1 call to CallShapes.Mailbox.op_Addition(Mailbox, Parcel):\n" +
            "CallShapes.Senders.AsOperator(Mailbox, Parcel)\n  box + parcel",
            text);
    }

    // Forms.cs: a call C# writes without call syntax is shown in the form the code has, under
    // the member that makes it. A conversion the code does not write is shown as the cast that
    // calls it, an entry of a collection initializer and a property pattern in their braces, a
    // foreach by its head, and the base constructor call a constructor leaves out as ": base()".
    [Theory]
    [InlineData("Coin", "op_Addition", null,
        "3 calls to " + Forms + "Coin.op_Addition(Coin, Coin):\n" +
        Forms + "Sums.Add(Coin, Coin)\n  a + b\n" +
        Forms + "Sums.AddNumber(Coin)\n  a + 5\n" +
        Forms + "Sums.AddTo(Coin, Coin)\n  a += b")]
    [InlineData("Coin", "op_Addition", "right",
        "3 calls to " + Forms + "Coin.op_Addition(Coin, Coin), argument for right:\n" +
        Forms + "Sums.Add(Coin, Coin)\n  b\n" +
        Forms + "Sums.AddNumber(Coin)\n  5\n" +
        Forms + "Sums.AddTo(Coin, Coin)\n  b")]
    [InlineData("Coin", "op_UnaryNegation", null,
        "1 call to " + Forms + "Coin.op_UnaryNegation(Coin):\n" + Forms + "Sums.Negate(Coin)\n  -a")]
    [InlineData("Coin", "op_Increment", null,
        "1 call to " + Forms + "Coin.op_Increment(Coin):\n" + Forms + "Sums.Step(Coin)\n  ++a")]
    [InlineData("Coin", "op_Equality", null,
        "1 call to " + Forms + "Coin.op_Equality(Coin, Coin):\n" + Forms + "Sums.Same(Coin, Coin)\n  a == b")]
    [InlineData("Coin", "op_Explicit", null,
        "1 call to " + Forms + "Coin.op_Explicit(Coin) → string:\n" + Forms + "Sums.Text(Coin)\n  (string)a")]
    [InlineData("Coin", "op_Implicit", null,
        "4 calls to " + Forms + "Coin.op_Implicit(int) → Coin:\n" +
        Forms + "Sums.AddNumber(Coin)\n  (Coin)5\n" +
        Forms + "Sums.Boxed(int)\n  (Coin)cents\n" +
        Forms + "Sums.FromNumber(int)\n  (Coin)cents\n" +
        Forms + "Tellers.Stocked(int)\n  (Coin)cents")]
    [InlineData("Tray<>", "Add", null,
        "1 call to " + Forms + "Tray<>.Add(T):\n" + Forms + "Tellers.Stocked(int)\n  { (Coin)cents }")]
    [InlineData("Cabinet", "get_Coins", null,
        "1 call to " + Forms + "Cabinet.get_Coins():\n" + Forms + "Tellers.Stocked(int)\n  Coins = { (Coin)cents }")]
    [InlineData("Label", "op_Addition", null,
        "1 call to " + Forms + "Label.op_Addition(string, Label):\n" + Forms + "Notes.Append(string, Label)\n  text += label")]
    [InlineData("Label", "op_Implicit", null,
        "1 call to " + Forms + "Label.op_Implicit(Label) → string:\n" + Forms + "Notes.Append(string, Label)\n  (string)(text + label)")]
    [InlineData("Label", "op_Implicit", "label",
        "1 call to " + Forms + "Label.op_Implicit(Label) → string, argument for label:\n" +
        Forms + "Notes.Append(string, Label)\n  text + label")]
    [InlineData("Verdict", "op_Implicit", null,
        "2 calls to " + Forms + "Verdict.op_Implicit(Verdict) → bool:\n" +
        Forms + "Notes.Unless(Verdict)\n  (bool)verdict\n" +
        Forms + "Notes.When(Verdict)\n  (bool)verdict")]
    [InlineData("Till", "GetEnumerator", null,
        "1 call to " + Forms + "Till.GetEnumerator():\n" + Forms + "Tellers.Each(Till)\n  foreach (Coin item in till)")]
    [InlineData("Till.Walker", "MoveNext", null,
        "1 call to " + Forms + "Till.Walker.MoveNext():\n" + Forms + "Tellers.Each(Till)\n  foreach (Coin item in till)")]
    [InlineData("Till.Walker", "get_Current", null,
        "1 call to " + Forms + "Till.Walker.get_Current():\n" + Forms + "Tellers.Each(Till)\n  foreach (Coin item in till)")]
    [InlineData("Till", "Add", null,
        "4 calls to " + Forms + "Till.Add (2 overloads):\n" +
        Forms + "Tellers.Filled(Coin, Coin)\n  { a }\n  { b, \"note\" }\n" +
        Forms + "Tellers.WithSpare(Coin, Coin)\n  { a }\n  { b }")]
    [InlineData("Till", "Add", "coin",
        "4 calls to " + Forms + "Till.Add (2 overloads), argument for coin:\n" +
        Forms + "Tellers.Filled(Coin, Coin)\n  a\n  b\n" +
        Forms + "Tellers.WithSpare(Coin, Coin)\n  a\n  b")]
    [InlineData("Till", "get_Empty", null,
        "2 calls to " + Forms + "Till.get_Empty():\n" +
        Forms + "Tellers.Open(object)\n  { Empty: false }\n" +
        Forms + "Till.Deconstruct(out int, out bool)\n  Empty")]
    [InlineData("Till", "get_Count", null,
        "3 calls to " + Forms + "Till.get_Count():\n" +
        Forms + "Tellers.Open(object)\n  { Count: var count }\n" +
        Forms + "Till.Deconstruct(out int, out bool)\n  Count\n" +
        Forms + "Till.Empty\n  Count")]
    [InlineData("Till", "Deconstruct", null,
        "1 call to " + Forms + "Till.Deconstruct(out int, out bool):\n" +
        Forms + "Tellers.Split(Till)\n  var (num2, flag2) = till")]
    [InlineData("Till", "Deconstruct", "empty",
        "1 call to " + Forms + "Till.Deconstruct(out int, out bool), argument for empty:\n" +
        Forms + "Tellers.Split(Till)\n  flag2")]
    [InlineData("Desk", ".ctor", null,
        "3 calls to " + Forms + "Desk..ctor():\n" +
        Forms + "FrontDesk..ctor()\n  : base()\n" +
        Forms + "FrontDesk..ctor(int)\n  : base()\n" +
        Forms + "SpareDesk..ctor()\n  : base()")]
    [InlineData("SizedDesk", ".ctor", null,
        "2 calls to " + Forms + "SizedDesk..ctor(int):\n" +
        Forms + "WideDesk..ctor()\n  : base()\n" +
        Forms + "WideDesk..ctor(int)\n  : base(size)")]
    [InlineData("SizedDesk", ".ctor", "size",
        "2 calls to " + Forms + "SizedDesk..ctor(int), argument for size:\n" +
        Forms + "WideDesk..ctor()\n  3 (default)\n" +
        Forms + "WideDesk..ctor(int)\n  size")]
    public async Task FindCallSites_ShowsACallWrittenWithoutCallSyntax(
        string typeName, string memberName, string? parameterName, string expected)
    {
        Assert.Equal(expected, await CallSitesInForms(typeName, memberName, parameterName));
    }

    // An initializer that fills the collection a property holds reads the property once for
    // each entry and never sets it.
    [Theory]
    [InlineData("get_Spare",
        "2 calls to " + Forms + "Till.get_Spare():\n" +
        Forms + "Tellers.WithSpare(Coin, Coin)\n  Spare = { a }\n  Spare = { b }")]
    [InlineData("set_Spare", "No calls to " + Forms + "Till.set_Spare(Till)")]
    [InlineData("set_Count",
        "1 call to " + Forms + "Till.set_Count(int):\n" + Forms + "Tellers.WithSpare(Coin, Coin)\n  Count = 2")]
    public async Task FindCallSites_OnAPropertyAnInitializerFills_ShowsTheGetterForEachEntry(
        string accessor, string expected)
    {
        Assert.Equal(expected, await CallSitesInForms("Till", accessor));
    }

    // The IL of a property pattern and of a collection initializer names the member of the base
    // class. Whether such a call is one to an override is decided by the type the pattern tests
    // and the type the initializer fills, as the receiver decides it for a call that is written.
    [Theory]
    [InlineData("Drawer", "get_Stuck",
        "2 calls to " + Forms + "Drawer.get_Stuck():\n" +
        Forms + "Drawers.Shut(object)\n  { Stuck: false }\n" +
        Forms + "Drawers.ShutLocked(object)\n  { Stuck: false }")]
    [InlineData("LockedDrawer", "get_Stuck",
        "1 call to " + Forms + "LockedDrawer.get_Stuck():\n" +
        Forms + "Drawers.ShutLocked(object)\n  { Stuck: false }\n" +
        "Override: code can call " + Forms + "Drawer.get_Stuck() instead; listed are those calls whose receiver's type is " +
        Forms + "LockedDrawer or derives from it.")]
    [InlineData("Drawer", "Add",
        "2 calls to " + Forms + "Drawer.Add(Coin):\n" +
        Forms + "Drawers.Fill(Coin)\n  { a }\n" +
        Forms + "Drawers.FillLocked(Coin)\n  { a }")]
    [InlineData("LockedDrawer", "Add",
        "1 call to " + Forms + "LockedDrawer.Add(Coin):\n" +
        Forms + "Drawers.FillLocked(Coin)\n  { a }\n" +
        "Override: code can call " + Forms + "Drawer.Add(Coin) instead; listed are those calls whose receiver's type is " +
        Forms + "LockedDrawer or derives from it.")]
    public async Task FindCallSites_OnAnOverride_CountsACallWithoutCallSyntaxByItsReceiver(
        string typeName, string memberName, string expected)
    {
        Assert.Equal(expected, await CallSitesInForms(typeName, memberName));
    }

    // What stays unread is counted in one sentence with the members beneath it: the creation of
    // a delegate, which calls the delegate's constructor, and the true operator of a condition.
    // The count says how many calls the answer did not look at, also when the limit shows no
    // member, and a member's own count is written unless it is its one call.
    [Theory]
    [InlineData("Rate", ".ctor", null,
        "0 calls to " + Forms + "Rate..ctor(object, nint):\n" +
        "Not shown: 3 calls in 2 members, which the decompiled C# does not show as a call:\n" +
        "  " + Forms + "Rates.One()\n" +
        "  " + Forms + "Rates.Two(): 2 of 2")]
    [InlineData("Rate", ".ctor", 1,
        "0 calls to " + Forms + "Rate..ctor(object, nint):\n" +
        "Not shown: 3 calls in 2 members, which the decompiled C# does not show as a call:\n" +
        "  " + Forms + "Rates.One()\n" +
        "... (1 more; raise limit to see all)")]
    [InlineData("Rate", ".ctor", 0,
        "0 calls to " + Forms + "Rate..ctor(object, nint):\n" +
        "Not shown: 3 calls in 2 members, which the decompiled C# does not show as a call:\n" +
        "... (2 more; raise limit to see all)")]
    [InlineData("Coin", "op_True", null,
        "0 calls to " + Forms + "Coin.op_True(Coin):\n" +
        "Not shown: 1 call in 1 member, which the decompiled C# does not show as a call:\n" +
        "  " + Forms + "Sums.IfSet(Coin)")]
    public async Task FindCallSites_CountsTheCallsItCannotRead_WithTheirMembersUnderTheLimit(
        string typeName, string memberName, int? limit, string expected)
    {
        Assert.Equal(expected, await CallSitesInForms(typeName, memberName, limit: limit));
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
        AssertCalls(lines, "CallShapes.Callers.InIterator()", "\"iterator\"");
        AssertCalls(lines, "CallShapes.Callers.InAsyncMethod()", "\"async\"");
        AssertCalls(lines, "CallShapes.Callers.InPropertyGetter", "\"property\"");
        AssertCalls(lines, "CallShapes.Callers.Item[int]", "\"indexer\"");
        AssertCalls(lines, "CallShapes.Callers.Item[int, int]", "\"two-index indexer\"");
        AssertCalls(lines, "CallShapes.Outer.Nested.InLocalFunction()", "\"nested local function\"");
    }

    // Repeats.cs: a caller is named once, whatever the number of its calls, with its calls
    // beneath it in the order of its source, one in a lambda among them.
    [Theory]
    [InlineData(null, false,
        "6 calls to CallShapes.Repeats.Bell.Ring(int):\n" +
        "CallShapes.Repeats.Ringers.Once(Bell)\n  bell.Ring()\n" +
        "CallShapes.Repeats.Ringers.Thrice(Bell)\n  bell.Ring(3)\n  bell.Ring()\n  bell.Ring(2)\n" +
        "CallShapes.Repeats.Ringers.WithLambda(Bell)\n  bell.Ring(5)\n  bell.Ring(4)")]
    [InlineData("times", false,
        "6 calls to CallShapes.Repeats.Bell.Ring(int), argument for times:\n" +
        "CallShapes.Repeats.Ringers.Once(Bell)\n  1 (default)\n" +
        "CallShapes.Repeats.Ringers.Thrice(Bell)\n  3\n  1 (default)\n  2\n" +
        "CallShapes.Repeats.Ringers.WithLambda(Bell)\n  5\n  4")]
    [InlineData("times", true,
        "4 of 6 calls to CallShapes.Repeats.Bell.Ring(int) pass times other than its default 1:\n" +
        "CallShapes.Repeats.Ringers.Thrice(Bell)\n  3\n  2\n" +
        "CallShapes.Repeats.Ringers.WithLambda(Bell)\n  5\n  4")]
    public async Task FindCallSites_NamesACallerOnce_WithItsCallsBeneathIt(
        string? parameterName, bool nonDefaultOnly, string expected)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Repeats.Bell",
            ["member_name"] = "Ring",
            ["kind"] = "call_sites",
            ["non_default_only"] = nonDefaultOnly,
        };
        if (parameterName is not null)
            arguments["parameter_name"] = parameterName;

        Assert.Equal(expected, await CallToolText("analyze", arguments));
    }

    // The limit counts calls, not lines: it can end the list inside a caller's calls, and a
    // caller none of whose calls is shown is not named.
    [Theory]
    [InlineData(1,
        "CallShapes.Repeats.Ringers.Once(Bell)\n  bell.Ring()\n" +
        "... (5 more; raise limit to see all)")]
    [InlineData(3,
        "CallShapes.Repeats.Ringers.Once(Bell)\n  bell.Ring()\n" +
        "CallShapes.Repeats.Ringers.Thrice(Bell)\n  bell.Ring(3)\n  bell.Ring()\n" +
        "... (3 more; raise limit to see all)")]
    [InlineData(4,
        "CallShapes.Repeats.Ringers.Once(Bell)\n  bell.Ring()\n" +
        "CallShapes.Repeats.Ringers.Thrice(Bell)\n  bell.Ring(3)\n  bell.Ring()\n  bell.Ring(2)\n" +
        "... (2 more; raise limit to see all)")]
    public async Task FindCallSites_WithALimit_CountsCallsAndCanEndInsideACallersCalls(int limit, string expected)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Repeats.Bell",
            ["member_name"] = "Ring",
            ["kind"] = "call_sites",
            ["limit"] = limit,
        });

        Assert.Equal("6 calls to CallShapes.Repeats.Bell.Ring(int):\n" + expected, text);
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

    /// <summary>
    /// Asserts that <paramref name="lines"/> name <paramref name="caller"/> once, with exactly
    /// <paramref name="calls"/> beneath it, in that order.
    /// </summary>
    private static void AssertCalls(string[] lines, string caller, params string[] calls)
    {
        int at = Assert.Single(Enumerable.Range(0, lines.Length), index => lines[index] == caller);
        Assert.Equal(
            calls.Select(call => "  " + call),
            lines.Skip(at + 1).TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal)));
    }

    /// <summary>The answer to call_sites on a member of a type of Forms.cs.</summary>
    private Task<string> CallSitesInForms(
        string typeName, string memberName, string? parameterName = null, int? limit = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = Forms + typeName,
            ["member_name"] = memberName,
            ["kind"] = "call_sites",
        };
        if (parameterName is not null)
            arguments["parameter_name"] = parameterName;
        if (limit is not null)
            arguments["limit"] = limit;
        return CallToolText("analyze", arguments);
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
