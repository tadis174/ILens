using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for a virtual call that names an override. Such a call runs through the slot
/// the overridden declaration introduced, so it is a call to that declaration, whichever
/// declaration of the slot the compiler wrote. The CallShapes fixture's NamedOverrides.cs has
/// <c>Operator</c> call the generic <c>Send</c> on a receiver of each class that declares or
/// inherits an override of it, where a C# compiler names the nearest override. The same for a
/// method without a type parameter and for a property's getter is tested on an assembly written
/// through <see cref="NamedOverrideAssembly"/>.
/// </summary>
public sealed class NamedOverrideTests : IClassFixture<ILensServerFixture>
{
    private const string Shapes = "CallShapes.NamedOverrides.";

    private readonly ILensServerFixture _server;

    public NamedOverrideTests(ILensServerFixture server)
    {
        _server = server;
    }

    // Every member that calls Send is a user of Relay's declaration, whatever class its receiver
    // has: the call on a Relay names the declaration, and each other one names an override. The
    // method group and the call in a lambda count like the rest. Beacon's own Send is none: it
    // calls Booster's override through base., which is no virtual call.
    [Fact]
    public async Task UsedBy_OnTheDeclaration_ListsTheMembersWhoseVirtualCallNamesAnOverride()
    {
        string text = await Analyze(FixturePath("CallShapes.dll"), Shapes + "Relay", "Send", "used_by");

        Assert.Equal(
            "8 users of " + Shapes + "Relay.Send<T>(T) [declared]:\n" +
            Shapes + "Operator.Both(Relay, Booster)\n" +
            Shapes + "Operator.Group(Booster)\n" +
            Shapes + "Operator.Later(Tower)\n" +
            Shapes + "Operator.OnBeacon(Beacon)\n" +
            Shapes + "Operator.OnBooster(Booster)\n" +
            Shapes + "Operator.OnBuoy(Buoy)\n" +
            Shapes + "Operator.OnRelay(Relay)\n" +
            Shapes + "Operator.OnTower(Tower)", text);
    }

    // The list of calls holds each of them once, and nothing after it says a call was not shown.
    [Fact]
    public async Task CallSites_OnTheDeclaration_ListsTheCallsThatNameAnOverride()
    {
        string text = await Analyze(FixturePath("CallShapes.dll"), Shapes + "Relay", "Send", "call_sites");

        Assert.Equal(
            "8 calls to " + Shapes + "Relay.Send<T>(T):\n" +
            Shapes + "Operator.Both(Relay, Booster)\n  relay.Send(7)\n  booster.Send(8)\n" +
            Shapes + "Operator.Later(Tower)\n  tower.Send(6)\n" +
            Shapes + "Operator.OnBeacon(Beacon)\n  beacon.Send(4)\n" +
            Shapes + "Operator.OnBooster(Booster)\n  booster.Send(2)\n" +
            Shapes + "Operator.OnBuoy(Buoy)\n  buoy.Send(5)\n" +
            Shapes + "Operator.OnRelay(Relay)\n  relay.Send(1)\n" +
            Shapes + "Operator.OnTower(Tower)\n  tower.Send(3)", text);
    }

    // A call is the override's when its receiver's type is the override's class or derives from
    // it. The call on a Beacon names Beacon's override and is a call to Booster's as well, like
    // the call on a Tower, which names Booster's. Buoy overrides beside them.
    [Theory]
    [InlineData("Booster",
        "6 calls to " + Shapes + "Booster.Send<T>(T):\n" +
        Shapes + "Beacon.Send<T>(T)\n  base.Send(signal)\n" +
        Shapes + "Operator.Both(Relay, Booster)\n  booster.Send(8)\n" +
        Shapes + "Operator.Later(Tower)\n  tower.Send(6)\n" +
        Shapes + "Operator.OnBeacon(Beacon)\n  beacon.Send(4)\n" +
        Shapes + "Operator.OnBooster(Booster)\n  booster.Send(2)\n" +
        Shapes + "Operator.OnTower(Tower)\n  tower.Send(3)\n")]
    [InlineData("Beacon",
        "1 call to " + Shapes + "Beacon.Send<T>(T):\n" +
        Shapes + "Operator.OnBeacon(Beacon)\n  beacon.Send(4)\n")]
    [InlineData("Buoy",
        "1 call to " + Shapes + "Buoy.Send<T>(T):\n" +
        Shapes + "Operator.OnBuoy(Buoy)\n  buoy.Send(5)\n")]
    public async Task CallSites_OnAnOverride_ListsTheCallsOnItsClassWhicheverOverrideTheyName(
        string typeName, string expectedCalls)
    {
        string text = await Analyze(FixturePath("CallShapes.dll"), Shapes + typeName, "Send", "call_sites");

        Assert.Equal(
            expectedCalls +
            "Override: code can call " + Shapes + "Relay.Send<T>(T) instead; listed are those calls whose receiver's type is " +
            Shapes + typeName + " or derives from it.", text);
    }

    // Who uses an override is what names that override: the calls on a Booster and on a Tower,
    // which declares none of its own, and Beacon's call through base. The call on a Beacon names
    // Beacon's override and is no use of Booster's.
    [Theory]
    [InlineData("Booster",
        "6 users of " + Shapes + "Booster.Send<T>(T) [declared]:\n" +
        Shapes + "Beacon.Send<T>(T)\n" +
        Shapes + "Operator.Both(Relay, Booster)\n" +
        Shapes + "Operator.Group(Booster)\n" +
        Shapes + "Operator.Later(Tower)\n" +
        Shapes + "Operator.OnBooster(Booster)\n" +
        Shapes + "Operator.OnTower(Tower)\n")]
    [InlineData("Beacon",
        "1 user of " + Shapes + "Beacon.Send<T>(T) [declared]:\n" +
        Shapes + "Operator.OnBeacon(Beacon)\n")]
    public async Task UsedBy_OnAnOverride_ListsWhatNamesThatOverride(string typeName, string expectedUsers)
    {
        string text = await Analyze(FixturePath("CallShapes.dll"), Shapes + typeName, "Send", "used_by");

        Assert.Equal(
            expectedUsers +
            "Override: code can call " + Shapes + "Relay.Send<T>(T) instead, so only references to this override are listed; " +
            "'call_sites' lists those calls whose receiver's type is " + Shapes + typeName + " or derives from it.", text);
    }

    // The control: Ping has no type parameter, so the call on a Booster names Relay's
    // declaration, and nothing names Booster's override.
    [Theory]
    [InlineData("Relay", "used_by",
        "1 user of " + Shapes + "Relay.Ping(int) [declared]:\n" +
        Shapes + "Operator.PingBooster(Booster)")]
    [InlineData("Booster", "used_by",
        "No users of " + Shapes + "Booster.Ping(int) [declared]\n" +
        "Override: code can call " + Shapes + "Relay.Ping(int) instead, so only references to this override are listed; " +
        "'call_sites' lists those calls whose receiver's type is " + Shapes + "Booster or derives from it.")]
    [InlineData("Booster", "call_sites",
        "1 call to " + Shapes + "Booster.Ping(int):\n" +
        Shapes + "Operator.PingBooster(Booster)\n  booster.Ping(9)\n" +
        "Override: code can call " + Shapes + "Relay.Ping(int) instead; listed are those calls whose receiver's type is " +
        Shapes + "Booster or derives from it.")]
    public async Task AMethodWithoutATypeParameter_CountsForTheDeclarationItsCallNames(string typeName, string kind, string expected)
    {
        Assert.Equal(expected, await Analyze(FixturePath("CallShapes.dll"), Shapes + typeName, "Ping", kind));
    }

    // In the written assembly each virtual call names the Lift, or the getter of Reach, of its
    // parameter's own class, and Derrick's Lift calls Gantry's without virtual dispatch. The
    // answers are those a C# compiler's code gets, which names Crane's declaration in each
    // virtual call: the declaration lists every virtual use, an override the calls on its class
    // and below, and the call that is not virtual counts for Gantry's Lift alone.
    [Theory]
    [InlineData("NamedOverride.Crane", "Lift", "used_by",
        "4 users of NamedOverride.Crane.Lift() [declared]:\n" +
        "NamedOverride.Yard.Group(Gantry)\n" +
        "NamedOverride.Yard.OnCrane(Crane)\n" +
        "NamedOverride.Yard.OnDerrick(Derrick)\n" +
        "NamedOverride.Yard.OnGantry(Gantry)")]
    [InlineData("NamedOverride.Crane", "Lift", "call_sites",
        "3 calls to NamedOverride.Crane.Lift():\n" +
        "NamedOverride.Yard.OnCrane(Crane)\n  crane.Lift()\n" +
        "NamedOverride.Yard.OnDerrick(Derrick)\n  derrick.Lift()\n" +
        "NamedOverride.Yard.OnGantry(Gantry)\n  gantry.Lift()")]
    [InlineData("NamedOverride.Gantry", "Lift", "used_by",
        "3 users of NamedOverride.Gantry.Lift() [declared]:\n" +
        "NamedOverride.Derrick.Lift()\n" +
        "NamedOverride.Yard.Group(Gantry)\n" +
        "NamedOverride.Yard.OnGantry(Gantry)\n" +
        "Override: code can call NamedOverride.Crane.Lift() instead, so only references to this override are listed; " +
        "'call_sites' lists those calls whose receiver's type is NamedOverride.Gantry or derives from it.")]
    [InlineData("NamedOverride.Gantry", "Lift", "call_sites",
        "3 calls to NamedOverride.Gantry.Lift():\n" +
        "NamedOverride.Derrick.Lift()\n  base.Lift()\n" +
        "NamedOverride.Yard.OnDerrick(Derrick)\n  derrick.Lift()\n" +
        "NamedOverride.Yard.OnGantry(Gantry)\n  gantry.Lift()\n" +
        "Override: code can call NamedOverride.Crane.Lift() instead; listed are those calls whose receiver's type is " +
        "NamedOverride.Gantry or derives from it.")]
    [InlineData("NamedOverride.Crane", "Reach", "read_by",
        "2 readers of NamedOverride.Crane.Reach [declared] (via get_Reach):\n" +
        "NamedOverride.Yard.ReachOfCrane(Crane)\n" +
        "NamedOverride.Yard.ReachOfGantry(Gantry)")]
    [InlineData("NamedOverride.Crane", "get_Reach", "call_sites",
        "2 calls to NamedOverride.Crane.get_Reach():\n" +
        "NamedOverride.Yard.ReachOfCrane(Crane)\n  crane.Reach\n" +
        "NamedOverride.Yard.ReachOfGantry(Gantry)\n  gantry.Reach")]
    [InlineData("NamedOverride.Gantry", "get_Reach", "call_sites",
        "1 call to NamedOverride.Gantry.get_Reach():\n" +
        "NamedOverride.Yard.ReachOfGantry(Gantry)\n  gantry.Reach\n" +
        "Override: code can call NamedOverride.Crane.get_Reach() instead; listed are those calls whose receiver's type is " +
        "NamedOverride.Gantry or derives from it.")]
    public async Task InAnAssemblyWhoseVirtualCallsNameAnOverride_AMethodWithoutATypeParameterIsAnsweredAlike(
        string typeName, string memberName, string kind, string expected)
    {
        var path = NamedOverrideAssembly.Write();
        try
        {
            Assert.Equal(expected, await Analyze(path, typeName, memberName, kind));
        }
        finally
        {
            NamedOverrideAssembly.TryDelete(path);
        }
    }

    private Task<string> Analyze(string assembly, string typeName, string memberName, string kind) =>
        CallToolText("analyze", new()
        {
            ["assembly"] = assembly,
            ["type_name"] = typeName,
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
