using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for who uses a method: what names the method itself, and nothing that names
/// another method of its name. The CallShapes fixture's Hiding.cs declares each member of
/// <c>Shadowed</c> anew in <c>Shadowing</c>, two levels down, in each way a member can hide
/// another, and <c>OfShadowed</c> and <c>OfShadowing</c> use the members of one class each.
/// The tests on a method body that breaks off write such an assembly through
/// <see cref="BrokenBodyAssembly"/>.
/// </summary>
public sealed class MethodUserTests : IClassFixture<ILensServerFixture>
{
    private const string Hiding = "CallShapes.Hiding.";

    private readonly ILensServerFixture _server;

    public MethodUserTests(ILensServerFixture server)
    {
        _server = server;
    }

    // A call on a Shadowed names Shadowed's member, also when the object is a Shadowing, so
    // OfShadowed's methods use no member of Shadowing. A property, an event and an indexer are
    // asked through their accessors, which hide like any method.
    [Theory]
    [InlineData("Shadowing", "WithNew", "used_by",
        "2 users of " + Hiding +"Shadowing.WithNew() [declared]:\n" +
        Hiding +"OfShadowing.AsDelegate(Shadowing)\n" +
        Hiding +"OfShadowing.WithNew(Shadowing)")]
    [InlineData("Shadowing", "WithoutNew", "used_by",
        "1 user of " + Hiding +"Shadowing.WithoutNew() [declared]:\n" +
        Hiding +"OfShadowing.WithoutNew(Shadowing)")]
    [InlineData("Shadowing", "OverVirtual", "used_by",
        "1 user of " + Hiding +"Shadowing.OverVirtual() [declared]:\n" +
        Hiding +"OfShadowing.OverVirtual(Shadowing)")]
    [InlineData("Shadowing", "StaticOverInstance", "used_by",
        "1 user of " + Hiding +"Shadowing.StaticOverInstance() [declared]:\n" +
        Hiding +"OfShadowing.StaticOverInstance()")]
    [InlineData("Shadowing", "Internal", "used_by",
        "1 user of " + Hiding +"Shadowing.Internal() [declared]:\n" +
        Hiding +"OfShadowing.Internal(Shadowing)")]
    [InlineData("Shadowing", "Level", "used_by",
        "2 users of " + Hiding +"Shadowing.Level [declared] (via get_Level, set_Level):\n" +
        Hiding +"OfShadowing.ReadLevel(Shadowing)\n" +
        Hiding +"OfShadowing.SetLevel(Shadowing)")]
    [InlineData("Shadowing", "Level", "read_by",
        "1 reader of " + Hiding +"Shadowing.Level [declared] (via get_Level):\n" +
        Hiding +"OfShadowing.ReadLevel(Shadowing)")]
    [InlineData("Shadowing", "Level", "assigned_by",
        "1 writer of " + Hiding +"Shadowing.Level [declared] (via set_Level):\n" +
        Hiding +"OfShadowing.SetLevel(Shadowing)")]
    [InlineData("Shadowing", "set_Level", "used_by",
        "1 user of " + Hiding +"Shadowing.set_Level(int) [declared]:\n" +
        Hiding +"OfShadowing.SetLevel(Shadowing)")]
    [InlineData("Shadowing", "Changed", "used_by",
        "2 users of " + Hiding +"Shadowing.Changed [declared] (via add_Changed, remove_Changed, the subscriber list):\n" +
        Hiding +"OfShadowing.Subscribe(Shadowing)\n" +
        Hiding +"OfShadowing.Unsubscribe(Shadowing)")]
    [InlineData("Shadowing", "add_Changed", "used_by",
        "1 user of " + Hiding +"Shadowing.add_Changed(EventHandler) [declared]:\n" +
        Hiding +"OfShadowing.Subscribe(Shadowing)")]
    [InlineData("Shadowing", "Item", "used_by",
        "2 users of " + Hiding +"Shadowing.Item[int] [declared] (via get_Item, set_Item):\n" +
        Hiding +"OfShadowing.ReadIndex(Shadowing)\n" +
        Hiding +"OfShadowing.SetIndex(Shadowing)")]
    [InlineData("Shadowing", "get_Item", "used_by",
        "1 user of " + Hiding +"Shadowing.get_Item(int) [declared]:\n" +
        Hiding +"OfShadowing.ReadIndex(Shadowing)")]
    // Bin<int>.Put is named through a reference, as every member of a generic type's instance.
    [InlineData("IntBin", "Put", "used_by",
        "1 user of " + Hiding +"IntBin.Put(int) [declared]:\n" +
        Hiding +"OfShadowing.Put(IntBin)")]
    // Starter's WithNew implements IStarter's as well; a call through the interface names the
    // interface's member, which the last line points to.
    [InlineData("Starter", "WithNew", "used_by",
        "1 user of " + Hiding +"Starter.WithNew() [declared]:\n" +
        Hiding +"OfShadowing.OnStarter(Starter)\n" +
        "Implementation: code can call " + Hiding + "IStarter.WithNew() instead; ask about that member for its uses.")]
    public async Task UsageOfAMemberThatHidesABaseClassMember_ListsNoCodeThatUsesOnlyTheHiddenMember(
        string typeName, string memberName, string kind, string expected)
    {
        Assert.Equal(expected, await Analyze(typeName, memberName, kind));
    }

    // These share a name with a member of Shadowed and hide nothing, or nothing a call could
    // be taken for: a method that starts a slot of its own, a static method over a static one,
    // another parameter list, a base class member that is private, and a constructor.
    [Theory]
    [InlineData("AsNewVirtual",
        "1 user of " + Hiding +"Shadowing.AsNewVirtual() [declared]:\n" +
        Hiding +"OfShadowing.AsNewVirtual(Shadowing)")]
    [InlineData("StaticOverStatic",
        "1 user of " + Hiding +"Shadowing.StaticOverStatic() [declared]:\n" +
        Hiding +"OfShadowing.StaticOverStatic()")]
    [InlineData("OtherParameters",
        "1 user of " + Hiding +"Shadowing.OtherParameters(string) [declared]:\n" +
        Hiding +"OfShadowing.OtherParameters(Shadowing)")]
    [InlineData("Private",
        "1 user of " + Hiding +"Shadowing.Private() [declared]:\n" +
        Hiding +"OfShadowing.Private(Shadowing)")]
    [InlineData("Shadowing",
        "1 user of " + Hiding +"Shadowing..ctor(int) [declared]:\n" +
        Hiding +"OfShadowing.Create()")]
    public async Task UsedBy_OnAMemberNamedLikeABaseClassMember_ListsItsOwnUsers(string memberName, string expected)
    {
        Assert.Equal(expected, await Analyze("Shadowing", memberName, "used_by"));
    }

    // The hidden member keeps its users: the call on a Shadowing seen as a Shadowed, and the
    // method group, which is no call and still names the method.
    [Theory]
    [InlineData("Shadowed", "WithNew", "used_by",
        "3 users of " + Hiding +"Shadowed.WithNew() [declared]:\n" +
        Hiding +"OfShadowed.AsDelegate(Shadowed)\n" +
        Hiding +"OfShadowed.ThroughShadowing(Shadowing)\n" +
        Hiding +"OfShadowed.WithNew(Shadowed)")]
    [InlineData("Shadowed", "Level", "used_by",
        "2 users of " + Hiding +"Shadowed.Level [declared] (via get_Level, set_Level):\n" +
        Hiding +"OfShadowed.ReadLevel(Shadowed)\n" +
        Hiding +"OfShadowed.SetLevel(Shadowed)")]
    [InlineData("Bin<>", "Put", "used_by",
        "1 user of " + Hiding +"Bin<>.Put(T) [declared]:\n" +
        Hiding +"OfShadowed.Put(Bin<int>)")]
    [InlineData("IStarter", "WithNew", "used_by",
        "1 user of " + Hiding +"IStarter.WithNew() [declared]:\n" +
        Hiding +"OfShadowing.ThroughInterface(IStarter)")]
    public async Task UsageOfAHiddenMember_ListsTheCodeThatNamesIt(
        string typeName, string memberName, string kind, string expected)
    {
        Assert.Equal(expected, await Analyze(typeName, memberName, kind));
    }

    // The list of calls names the same code as the list of users, less the method group.
    [Theory]
    [InlineData("Shadowing", "WithNew",
        "1 call to " + Hiding +"Shadowing.WithNew():\n" +
        Hiding +"OfShadowing.WithNew(Shadowing)\n  shadowing.WithNew()")]
    [InlineData("Shadowed", "WithNew",
        "2 calls to " + Hiding +"Shadowed.WithNew():\n" +
        Hiding +"OfShadowed.ThroughShadowing(Shadowing)\n  ((Shadowed)shadowing).WithNew()\n" +
        Hiding +"OfShadowed.WithNew(Shadowed)\n  shadowed.WithNew()")]
    [InlineData("Shadowing", "set_Level",
        "1 call to " + Hiding +"Shadowing.set_Level(int):\n" +
        Hiding +"OfShadowing.SetLevel(Shadowing)\n  shadowing.Level = 1")]
    public async Task CallSites_OnAMemberThatHidesOrIsHidden_ListsTheCallsThatNameIt(
        string typeName, string memberName, string expected)
    {
        Assert.Equal(expected, await Analyze(typeName, memberName, "call_sites"));
    }

    // The code of BrokenBody.Shapes.Cut calls Plain and then breaks off inside a call
    // instruction. What it names up to there counts, and the question about another method is
    // answered from every other body.
    [Theory]
    [InlineData("BrokenBody.Shapes", "Plain",
        "2 users of BrokenBody.Shapes.Plain() [declared]:\n" +
        "BrokenBody.Shapes.Cut()\n" +
        "BrokenBody.User.Use(Shapes)")]
    [InlineData("BrokenBody.Shapes", "Virtual",
        "1 user of BrokenBody.Shapes.Virtual() [declared]:\n" +
        "BrokenBody.User.Use(Shapes)")]
    [InlineData("BrokenBody.Derived", "Virtual",
        "No users of BrokenBody.Derived.Virtual() [declared]\n" +
        "Override: code can call BrokenBody.Shapes.Virtual() instead, so only references to this override are listed; " +
        "'call_sites' lists those calls whose receiver's type is BrokenBody.Derived or derives from it.")]
    [InlineData("BrokenBody.Shapes", "Cut",
        "No users of BrokenBody.Shapes.Cut() [declared]")]
    public async Task UsedBy_InAnAssemblyWithAMethodBodyThatBreaksOff_IsAnsweredFromTheOtherBodies(
        string typeName, string memberName, string expected)
    {
        var path = BrokenBodyAssembly.Write();
        try
        {
            string text = await CallToolText("analyze", new()
            {
                ["assembly"] = path,
                ["type_name"] = typeName,
                ["member_name"] = memberName,
                ["kind"] = "used_by",
            });

            Assert.Equal(expected, text);
        }
        finally
        {
            BrokenBodyAssembly.TryDelete(path);
        }
    }

    private Task<string> Analyze(string typeName, string memberName, string kind) =>
        CallToolText("analyze", new()
        {
            ["assembly"] = FixturePath("CallShapes.dll"),
            ["type_name"] = Hiding +typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
        });

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
