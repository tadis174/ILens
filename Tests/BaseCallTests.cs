using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for a call through <c>base.</c> that the compiler makes from a method it
/// adds, and for a call whose instruction names an override. The CallShapes fixture's
/// BaseCalls.cs has <c>Lodge</c> call the members of <c>Gate</c> through <c>base.</c> in a
/// lambda, an iterator, an async method and a local function, <c>Arch</c> call an override it
/// does not override itself, and <c>Porter</c> call a generic method on a class that overrides
/// it. ExternalUser's BaseCalls.cs does the first with a class of ExternalLib. VbModules'
/// Calls.vb and Overrides.vb, and InterfaceShapes' Returns.cs, hold calls of overrides that
/// Visual Basic and C# compile as calls of the override itself, most of them written without
/// call syntax.
/// </summary>
public sealed class BaseCallTests : IClassFixture<ILensServerFixture>
{
    private const string Shapes = "CallShapes.BaseCalls.";
    private const string Overrides = "VbModules.Overrides.";

    private const string ArrayListRoute = "Override: code can call the System.Collections.ArrayList member it " +
        "overrides instead, which another assembly defines, so those calls are not counted here.";

    private readonly ILensServerFixture _server;

    public BaseCallTests(ILensServerFixture server)
    {
        _server = server;
    }

    // The compiler moves each of these calls into a method of its own, which the moved code
    // calls. The member the call is written in is the user, and the added method never is.
    // Open is called from two members through one added method. Latch is the control: its
    // lambda stays in the class and makes the call itself.
    [Theory]
    [InlineData("Open",
        "2 users of " + Shapes + "Gate.Open(int) [declared]:\n" +
        Shapes + "Lodge.Open(int)\n" +
        Shapes + "Lodge.OpenTwice(int)")]
    [InlineData("Lock",
        "1 user of " + Shapes + "Gate.Lock(int) [declared]:\n" +
        Shapes + "Lodge.Lock(int)")]
    [InlineData("Steps",
        "1 user of " + Shapes + "Gate.Steps() [declared]:\n" +
        Shapes + "Lodge.Steps()")]
    [InlineData("Count",
        "1 user of " + Shapes + "Gate.Count() [declared]:\n" +
        Shapes + "Lodge.CountLater()")]
    [InlineData("Level",
        "1 user of " + Shapes + "Gate.Level [declared] (via get_Level):\n" +
        Shapes + "Lodge.Levels()")]
    [InlineData("Weight",
        "3 users of " + Shapes + "Gate.Weight() [declared]:\n" +
        Shapes + "Lodge.WeightThroughBlock()\n" +
        Shapes + "Lodge.WeightThroughGroup()\n" +
        Shapes + "Lodge.WeightThroughLocal()")]
    [InlineData("Latch",
        "1 user of " + Shapes + "Gate.Latch() [declared]:\n" +
        Shapes + "Lodge.LatchLater()")]
    public async Task UsedBy_OnAMethodCalledThroughBaseInMovedCode_ListsTheMemberTheCallIsWrittenIn(
        string memberName, string expected)
    {
        Assert.Equal(expected, await Analyze("CallShapes.dll", Shapes + "Gate", memberName, "used_by"));
    }

    [Fact]
    public async Task UsedBy_WithCompilerGenerated_DoesNotListTheAddedMethod()
    {
        string text = await Analyze("CallShapes.dll", Shapes + "Gate", "Open", "used_by", includeGenerated: true);

        Assert.Equal(
            "2 users of " + Shapes + "Gate.Open(int) [declared]:\n" +
            Shapes + "Lodge.Open(int)\n" +
            Shapes + "Lodge.OpenTwice(int)", text);
    }

    // One line for each call written: two lambdas of one member, a call in a lambda beside one
    // outside it, and a local function that only passes the call on, called twice, called once
    // with a body in braces, and called once beside its use as a delegate, which keeps its own
    // code in the member. No answer ends with a block that says the counts differ.
    [Theory]
    [InlineData("Open",
        "3 calls to " + Shapes + "Gate.Open(int):\n" +
        Shapes + "Lodge.Open(int)\n  base.Open(width)\n" +
        Shapes + "Lodge.OpenTwice(int)\n  base.Open(width)\n  base.Open(width + 1)")]
    [InlineData("Lock",
        "2 calls to " + Shapes + "Gate.Lock(int):\n" +
        Shapes + "Lodge.Lock(int)\n  base.Lock(code)\n  base.Lock(code + 1)")]
    [InlineData("Steps",
        "1 call to " + Shapes + "Gate.Steps():\n" +
        Shapes + "Lodge.Steps()\n  base.Steps()")]
    [InlineData("Count",
        "1 call to " + Shapes + "Gate.Count():\n" +
        Shapes + "Lodge.CountLater()\n  base.Count()")]
    [InlineData("get_Level",
        "1 call to " + Shapes + "Gate.get_Level():\n" +
        Shapes + "Lodge.Levels()\n  base.Level")]
    [InlineData("Weight",
        "5 calls to " + Shapes + "Gate.Weight():\n" +
        Shapes + "Lodge.WeightThroughBlock()\n  base.Weight()\n" +
        Shapes + "Lodge.WeightThroughGroup()\n  base.Weight()\n  base.Weight()\n" +
        Shapes + "Lodge.WeightThroughLocal()\n  base.Weight()\n  base.Weight()")]
    public async Task CallSites_OnAMethodCalledThroughBaseInMovedCode_ListsEachCallUnderItsMember(
        string memberName, string expected)
    {
        Assert.Equal(expected, await Analyze("CallShapes.dll", Shapes + "Gate", memberName, "call_sites"));
    }

    [Theory]
    [InlineData("Open",
        "3 symbols used by " + Shapes + "Lodge.Open(int) [declared]:\n" +
        Shapes + "Gate.Open(int)\n" +
        Shapes + "Runner.Run(Action)\n" +
        "System.Action..ctor(object, nint)")]
    [InlineData("Steps",
        "5 symbols used by " + Shapes + "Lodge.Steps() [declared]:\n" +
        Shapes + "Gate.Steps()\n" +
        "System.Collections.Generic.IEnumerable<int>.GetEnumerator()\n" +
        "System.Collections.Generic.IEnumerator<int>.get_Current()\n" +
        "System.Collections.IEnumerator.MoveNext()\n" +
        "System.IDisposable.Dispose()")]
    [InlineData("CountLater",
        "2 symbols used by " + Shapes + "Lodge.CountLater() [declared]:\n" +
        Shapes + "Gate.Count()\n" +
        "System.Threading.Tasks.Task.Yield()")]
    [InlineData("Levels",
        "1 symbol used by " + Shapes + "Lodge.Levels() [declared]:\n" +
        Shapes + "Gate.get_Level()")]
    [InlineData("WeightThroughLocal",
        "1 symbol used by " + Shapes + "Lodge.WeightThroughLocal() [declared]:\n" +
        Shapes + "Gate.Weight()")]
    public async Task Uses_OnAMemberThatCallsThroughBaseInMovedCode_ListsTheBaseMember(
        string memberName, string expected)
    {
        Assert.Equal(expected, await Analyze("CallShapes.dll", Shapes + "Lodge", memberName, "uses"));
    }

    // The added method is something the member's code names, so its line stays beside the base
    // method's when generated symbols are asked for, as a lambda's method does.
    [Fact]
    public async Task Uses_WithCompilerGenerated_ListsTheBaseMemberAndTheAddedMethod()
    {
        string text = await Analyze("CallShapes.dll", Shapes + "Lodge", "Open", "uses", includeGenerated: true);

        Assert.Contains("\n" + Shapes + "Gate.Open(int)\n", text);
        Assert.Contains("\n" + Shapes + "Lodge.<>n__0(int)\n", text);
    }

    // The added method asked for by its name answers with the members whose code calls it.
    [Fact]
    public async Task UsedBy_OnTheAddedMethodItself_ListsTheMembersThatCallIt()
    {
        string text = await Analyze("CallShapes.dll", Shapes + "Lodge", "<>n__0", "used_by");

        Assert.Equal(
            "2 users of " + Shapes + "Lodge.<>n__0(int) [declared]:\n" +
            Shapes + "Lodge.Open(int)\n" +
            Shapes + "Lodge.OpenTwice(int)", text);
    }

    // Steps, CountLater and OpenTwice name Gate nowhere but in the call the compiler moved.
    [Theory]
    [InlineData("Lodge.Steps()")]
    [InlineData("Lodge.CountLater()")]
    [InlineData("Lodge.OpenTwice(int)")]
    public async Task UsedBy_OnTheBaseType_ListsAMemberThatNamesItOnlyInSuchACall(string member)
    {
        string text = await Analyze("CallShapes.dll", Shapes + "Gate", null, "used_by");

        Assert.Contains("\n" + Shapes + member + "\n", text + "\n");
        Assert.DoesNotContain("<>n__", text);
    }

    // The same for a base class another assembly defines, which is known from the references
    // to it alone.
    [Fact]
    public async Task UsedBy_OnAMethodOfAnotherAssemblysClass_ListsTheMemberTheCallIsWrittenIn()
    {
        string text = await Analyze("ExternalUser.dll", "ExternalLib.BaseCalls.Hatch", "Lift", "used_by");

        Assert.Equal(
            "1 user of ExternalLib.BaseCalls.Hatch.Lift(int):\n" +
            "ExternalUser.BaseCalls.Trapdoor.Later(int)", text);
    }

    [Fact]
    public async Task UsedBy_OnAnotherAssemblysClass_ListsTheMemberTheCallIsWrittenIn()
    {
        string text = await Analyze("ExternalUser.dll", "ExternalLib.BaseCalls.Hatch", null, "used_by");

        Assert.Equal(
            "3 users of ExternalLib.BaseCalls.Hatch:\n" +
            "ExternalUser.BaseCalls.Trapdoor\n" +
            "ExternalUser.BaseCalls.Trapdoor..ctor()\n" +
            "ExternalUser.BaseCalls.Trapdoor.Later(int)", text);
    }

    [Fact]
    public async Task Uses_OnAMemberThatCallsAnotherAssemblysBaseMethodInALambda_ListsThatMethod()
    {
        string text = await Analyze("ExternalUser.dll", "ExternalUser.BaseCalls.Trapdoor", "Later", "uses");

        Assert.Equal(
            "2 symbols used by ExternalUser.BaseCalls.Trapdoor.Later(int) [declared]:\n" +
            "ExternalLib.BaseCalls.Hatch.Lift(int)\n" +
            "System.Action..ctor(object, nint)", text);
    }

    // The instruction of each of these calls names the override itself. Arch calls Close through
    // base. without overriding it; Porter calls the generic Pass on a Vault, where the compiler
    // names Vault's override. Each call is listed once, and nothing after the list says a call
    // was not shown. The other two are controls: a call that names the declaration Vault's Close
    // overrides, and a property read through base.
    [Theory]
    [InlineData("Portal", "Close", "Gate.Close(int)",
        "1 call to " + Shapes + "Portal.Close(int):\n" +
        Shapes + "Arch.Shut()\n  base.Close(3)\n")]
    [InlineData("Vault", "Pass", "Gate.Pass<T>(T)",
        "1 call to " + Shapes + "Vault.Pass<T>(T):\n" +
        Shapes + "Porter.Hand(Vault)\n  vault.Pass(5)\n")]
    [InlineData("Vault", "Close", "Gate.Close(int)",
        "1 call to " + Shapes + "Vault.Close(int):\n" +
        Shapes + "Porter.Shut(Vault)\n  vault.Close(4)\n")]
    [InlineData("Portal", "get_Level", "Gate.get_Level()",
        "1 call to " + Shapes + "Portal.get_Level():\n" +
        Shapes + "Arch.Peek()\n  base.Level\n")]
    public async Task CallSites_OnAnOverrideTheInstructionNames_ListsTheCallOnce(
        string typeName, string memberName, string overridden, string expectedCalls)
    {
        string text = await Analyze("CallShapes.dll", Shapes + typeName, memberName, "call_sites");

        Assert.Equal(
            expectedCalls +
            "Override: code can call " + Shapes + overridden + " instead; listed are those calls whose receiver's type is " +
            Shapes + typeName + " or derives from it.", text);
    }

    // Visual Basic's MyClass calls an override of the calling class without virtual dispatch,
    // and the decompiled C#, which has no such form, writes a plain call: only the instruction
    // says that the call is not virtual. The call is listed once, and nothing after the list says
    // a call was not shown.
    [Fact]
    public async Task CallSites_OnAnOverrideCalledThroughMyClass_ListsTheCallOnce()
    {
        string text = await Analyze("VbModules.dll", "VbModules.Calls.Lamp", "Glow", "call_sites");

        Assert.Equal(
            "1 call to VbModules.Calls.Lamp.Glow():\n" +
            "VbModules.Calls.Lamp.Shine()\n  Glow()\n" +
            "Override: code can call VbModules.Calls.Lantern.Glow() instead; listed are those calls whose " +
            "receiver's type is VbModules.Calls.Lamp or derives from it.", text);
    }

    // A property read and a compound assignment through MyClass call Lamp's override of Light
    // without virtual dispatch, and the decompiled C# writes them as a plain read and a plain
    // increment. Each call is listed once under the override, and nothing after the list says a
    // call was not shown or that the decompiled C# holds a call to Lantern's that the IL lacks.
    // Lantern's Light is the control: the calls through MyClass never reach it.
    [Theory]
    [InlineData("Lamp", "get_Light",
        "2 calls to VbModules.Calls.Lamp.get_Light():\n" +
        "VbModules.Calls.Lamp.Brighten()\n  Light++\n" +
        "VbModules.Calls.Lamp.Brightness()\n  Light\n" +
        "Override: code can call VbModules.Calls.Lantern.get_Light() instead; listed are those calls whose " +
        "receiver's type is VbModules.Calls.Lamp or derives from it.")]
    [InlineData("Lamp", "set_Light",
        "1 call to VbModules.Calls.Lamp.set_Light(int):\n" +
        "VbModules.Calls.Lamp.Brighten()\n  Light++\n" +
        "Override: code can call VbModules.Calls.Lantern.set_Light(int) instead; listed are those calls whose " +
        "receiver's type is VbModules.Calls.Lamp or derives from it.")]
    [InlineData("Lantern", "get_Light", "No calls to VbModules.Calls.Lantern.get_Light()")]
    public async Task CallSites_OnAPropertyCalledThroughMyClass_ListsEachCallOnce(
        string typeName, string memberName, string expected)
    {
        Assert.Equal(expected, await Analyze("VbModules.dll", "VbModules.Calls." + typeName, memberName, "call_sites"));
    }

    // Visual Basic names the override itself in a call on an object of its class, also where the
    // member it overrides is another assembly's: VbModules' Shelf overrides ArrayList's indexer,
    // Count, GetEnumerator and Add, and Spool MemoryStream's Position. InterfaceShapes' Smudge
    // overrides Exception.Data with a more specific return type, which a C# compiler names in a
    // call on an object of its class, also in a property pattern. Each call is listed once, in
    // the form the decompiled C# writes it in, and nothing after the list says a call was not
    // shown.
    [Theory]
    [InlineData("VbModules.dll", Overrides + "Shelf", "get_Count",
        "2 calls to " + Overrides + "Shelf.get_Count():\n" +
        Overrides + "Shelf.Size()\n  Count\n" +
        Overrides + "Users.Measure(Shelf)\n  shelf.Count\n" +
        "Implementation: code can call System.Collections.ICollection.get_Count() instead; ask about that " +
        "member for its uses.\n" + ArrayListRoute)]
    [InlineData("VbModules.dll", Overrides + "Shelf", "get_Item",
        "1 call to " + Overrides + "Shelf.get_Item(int):\n" +
        Overrides + "Users.Store(Shelf)\n  shelf[1]\n" +
        "Implementation: code can call System.Collections.IList.get_Item(int) instead; ask about that " +
        "member for its uses.\n" + ArrayListRoute)]
    [InlineData("VbModules.dll", Overrides + "Shelf", "set_Item",
        "1 call to " + Overrides + "Shelf.set_Item(int, object):\n" +
        Overrides + "Users.Store(Shelf)\n  shelf[0] = RuntimeHelpers.GetObjectValue(shelf[1])\n" +
        "Implementation: code can call System.Collections.IList.set_Item(int, object) instead; ask about " +
        "that member for its uses.\n" + ArrayListRoute)]
    [InlineData("VbModules.dll", Overrides + "Shelf", "GetEnumerator",
        "1 call to " + Overrides + "Shelf.GetEnumerator():\n" +
        Overrides + "Users.Walk(Shelf)\n  foreach (object item in shelf)\n" +
        "Implementation: code can call System.Collections.IEnumerable.GetEnumerator() instead; ask about " +
        "that member for its uses.\n" + ArrayListRoute)]
    [InlineData("VbModules.dll", Overrides + "Shelf", "Add",
        "2 calls to " + Overrides + "Shelf.Add(object):\n" +
        Overrides + "Users.Fill(ref Shelf)\n  { 1 }\n  { 2 }\n" +
        "Implementation: code can call System.Collections.IList.Add(object) instead; ask about that " +
        "member for its uses.\n" + ArrayListRoute)]
    [InlineData("VbModules.dll", Overrides + "Spool", "get_Position",
        "1 call to " + Overrides + "Spool.get_Position():\n" +
        Overrides + "Users.Advance(Spool)\n  spool.Position++\n" +
        "Override: code can call the System.IO.MemoryStream member it overrides instead, which another " +
        "assembly defines, so those calls are not counted here.")]
    [InlineData("VbModules.dll", Overrides + "Spool", "set_Position",
        "2 calls to " + Overrides + "Spool.set_Position(long):\n" +
        Overrides + "Users.Advance(Spool)\n  spool.Position++\n" +
        Overrides + "Users.Rewind(Spool)\n  spool.Position = 0L\n" +
        "Override: code can call the System.IO.MemoryStream member it overrides instead, which another " +
        "assembly defines, so those calls are not counted here.")]
    [InlineData("InterfaceShapes.dll", "InterfaceShapes.Returns.Smudge", "get_Data",
        "2 calls to InterfaceShapes.Returns.Smudge.get_Data():\n" +
        "InterfaceShapes.Returns.Inspector.Entries(Smudge)\n  smudge.Data\n" +
        "InterfaceShapes.Returns.Inspector.IsBlank(object)\n  { Data: { } data }\n" +
        "Override: code can call the System.Exception member it overrides instead, which another assembly " +
        "defines, so those calls are not counted here.")]
    public async Task CallSites_OnAnOverrideOfAnotherAssemblysMember_ListsEachCallOnce(
        string fixture, string typeName, string memberName, string expected)
    {
        Assert.Equal(expected, await Analyze(fixture, typeName, memberName, "call_sites"));
    }

    private Task<string> Analyze(string fixture, string typeName, string? memberName, string kind,
        bool includeGenerated = false)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = FixturePath(fixture),
            ["type_name"] = typeName,
            ["kind"] = kind,
        };
        if (memberName is not null)
            arguments["member_name"] = memberName;
        if (includeGenerated)
            arguments["include_compiler_generated"] = true;
        return CallToolText("analyze", arguments);
    }

    /// <summary>
    /// The path of a fixture assembly, which Tests.csproj's ProjectReference puts beside the
    /// shared inspection target.
    /// </summary>
    private string FixturePath(string fileName)
    {
        string path = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Fixture not found: {path}. Its project under Tests/Fixtures/ is expected to land here via " +
                "Tests.csproj's ProjectReference.");
        return path;
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
