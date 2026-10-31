using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for what analyze answers about an override. C# compiles a call to an
/// override as a call to the declaration that introduced its slot, unless the call goes through
/// <c>base.</c>, which names the override itself, or the method has a type parameter of its
/// own, which <see cref="NamedOverrideTests"/> covers, or the override returns a more specific
/// type, which <see cref="ReturnOverrideTests"/> covers. The CallShapes fixture's Overrides.cs
/// overrides a method and a property its Creature declares, the ExplicitMembers fixture's
/// Circle overrides members of System.Object, the ExternalUser fixture's DerivedWidget
/// overrides a method of a class whose assembly ILens does not load, and the InterfaceShapes
/// fixture's Follower overrides a method, a property and an event of its Control.
/// </summary>
public sealed class OverrideTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;

    public OverrideTests(ILensServerFixture server)
    {
        _server = server;
    }

    // Animal overrides Creature's declarations, Dog overrides Animal's Die, and Ghost overrides
    // Creature's Die beside Animal. A call is the override's when its receiver's type is the
    // override's class or derives from it, or when it goes through base. to the override itself.
    [Theory]
    [InlineData("CallShapes.Animal", "Die",
        "3 calls to CallShapes.Animal.Die(int):\n" +
        "CallShapes.Dog.Die(int)\n  base.Die(cause + 1)\n" +
        "CallShapes.Undertaker.OnAnimal(Animal)\n  animal.Die(2)\n" +
        "CallShapes.Undertaker.OnDog(Dog)\n  dog.Die(3)\n" +
        "Override: code can call CallShapes.Creature.Die(int) instead; listed are those calls whose receiver's type is " +
        "CallShapes.Animal or derives from it.")]
    [InlineData("CallShapes.Dog", "Die",
        "1 call to CallShapes.Dog.Die(int):\n" +
        "CallShapes.Undertaker.OnDog(Dog)\n  dog.Die(3)\n" +
        "Override: code can call CallShapes.Creature.Die(int) instead; listed are those calls whose receiver's type is " +
        "CallShapes.Dog or derives from it.")]
    [InlineData("CallShapes.Ghost", "Die",
        "1 call to CallShapes.Ghost.Die(int):\n" +
        "CallShapes.Undertaker.OnGhost(Ghost)\n  ghost.Die(4)\n" +
        "Override: code can call CallShapes.Creature.Die(int) instead; listed are those calls whose receiver's type is " +
        "CallShapes.Ghost or derives from it.")]
    [InlineData("CallShapes.Animal", "get_Health",
        "1 call to CallShapes.Animal.get_Health():\n" +
        "CallShapes.Undertaker.ReadAnimalHealth(Animal)\n  animal.Health\n" +
        "Override: code can call CallShapes.Creature.get_Health() instead; listed are those calls whose receiver's type is " +
        "CallShapes.Animal or derives from it.")]
    [InlineData("CallShapes.Animal", "set_Health",
        "1 call to CallShapes.Animal.set_Health(int):\n" +
        "CallShapes.Undertaker.SetDogHealth(Dog)\n  dog.Health = 5\n" +
        "Override: code can call CallShapes.Creature.set_Health(int) instead; listed are those calls whose receiver's type is " +
        "CallShapes.Animal or derives from it.")]
    public async Task FindCallSites_OnAnOverride_ListsTheCallsOnItsClassAndNamesTheDeclarationCodeCalls(
        string typeName, string methodName, string expected)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = FixturePath("CallShapes.dll"),
            ["type_name"] = typeName,
            ["member_name"] = methodName,
            ["kind"] = "call_sites",
        });

        Assert.Equal(expected, text);
    }

    // A usage question lists what names the override itself, alike for Animal, one level below
    // the declaration code calls, and for Dog, two levels below.
    [Theory]
    [InlineData("CallShapes.Animal", "Die", "used_by",
        "1 user of CallShapes.Animal.Die(int) [declared]:\n" +
        "CallShapes.Dog.Die(int)\n" +
        "Override: code can call CallShapes.Creature.Die(int) instead, so only references to this override are listed; " +
        "'call_sites' lists those calls whose receiver's type is CallShapes.Animal or derives from it.")]
    [InlineData("CallShapes.Dog", "Die", "used_by",
        "No users of CallShapes.Dog.Die(int) [declared]\n" +
        "Override: code can call CallShapes.Creature.Die(int) instead, so only references to this override are listed; " +
        "'call_sites' lists those calls whose receiver's type is CallShapes.Dog or derives from it.")]
    [InlineData("CallShapes.Animal", "Health", "used_by",
        "No users of CallShapes.Animal.Health [declared] (via get_Health, set_Health)\n" +
        "Override: code can call CallShapes.Creature.Health instead, so only references to this override are listed; " +
        "'call_sites' on get_Health and set_Health lists those calls whose receiver's type is CallShapes.Animal or derives from it.")]
    [InlineData("CallShapes.Animal", "Health", "read_by",
        "No readers of CallShapes.Animal.Health [declared] (via get_Health)\n" +
        "Override: code can call CallShapes.Creature.Health instead, so only references to this override are listed; " +
        "'call_sites' on get_Health and set_Health lists those calls whose receiver's type is CallShapes.Animal or derives from it.")]
    public async Task UsageOfAnOverride_ListsWhatNamesItAndNamesTheDeclarationCodeCalls(
        string typeName, string memberName, string kind, string expected)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = FixturePath("CallShapes.dll"),
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
        });

        Assert.Equal(expected, text);
    }

    // The declaration code calls is in another assembly, whose calls ILens cannot count, so the
    // line names the class the override's own class inherits it from. Where that class was not
    // read, the member it overrides may implement an interface member, which the last line says.
    [Theory]
    [InlineData("ExplicitMembers.dll", "ExplicitMembers.Circle", "GetHashCode", "call_sites",
        "No calls to ExplicitMembers.Circle.GetHashCode()\n" +
        "Override: code can call the System.Object member it overrides instead, which another assembly defines, " +
        "so those calls are not counted here.")]
    [InlineData("ExplicitMembers.dll", "ExplicitMembers.Circle", "GetHashCode", "used_by",
        "No users of ExplicitMembers.Circle.GetHashCode() [declared]\n" +
        "Override: code can call the System.Object member it overrides instead, which another assembly defines, " +
        "so only references to this override are listed.")]
    [InlineData("ExternalUser.dll", "ExternalUser.DerivedWidget", "Draw", "used_by",
        "No users of ExternalUser.DerivedWidget.Draw() [declared]\n" +
        "Override: code can call the ExternalLib.Widget member it overrides instead, which another assembly defines, " +
        "so only references to this override are listed.\n" +
        "Not checked: whether it implements an interface member; ILens could not load ExternalLib, Version=1.0.0.0")]
    public async Task AnalyzeAnOverrideOfAMemberFromAnotherAssembly_NamesTheClassItIsInheritedFrom(
        string fixture, string typeName, string memberName, string kind, string expected)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = FixturePath(fixture),
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
        });

        Assert.Equal(expected, text);
    }

    // The InterfaceShapes fixture's Follower overrides an event, a property and a method of
    // Control. Who overrides a member is asked of a method, a property, an event and of an
    // accessor alike, and the answer about an event that overrides names the declaration as the
    // answer about a method does. That last case needs the walk from an event up the events it
    // overrides to end: the decompiler library gives an event itself as its first base member
    // in ILens's type system.
    [Theory]
    [InlineData("Control", "PlainChanged", "overridden_by",
        "1 override of InterfaceShapes.Control.PlainChanged [declared]:\n" +
        "InterfaceShapes.Follower.PlainChanged")]
    [InlineData("Control", "add_PlainChanged", "overridden_by",
        "1 override of InterfaceShapes.Control.add_PlainChanged(EventHandler) [declared]:\n" +
        "InterfaceShapes.Follower.add_PlainChanged(EventHandler)")]
    [InlineData("Control", "PlainValue", "overridden_by",
        "1 override of InterfaceShapes.Control.PlainValue [declared]:\n" +
        "InterfaceShapes.Follower.PlainValue")]
    [InlineData("Control", "set_PlainValue", "overridden_by",
        "1 override of InterfaceShapes.Control.set_PlainValue(int) [declared]:\n" +
        "InterfaceShapes.Follower.set_PlainValue(int)")]
    [InlineData("Control", "Defaulted", "overridden_by",
        "1 override of InterfaceShapes.Control.Defaulted() [declared]:\n" +
        "InterfaceShapes.Follower.Defaulted()")]
    [InlineData("Control", "Fixed", "overridden_by",
        "No overrides of InterfaceShapes.Control.Fixed() [declared]")]
    [InlineData("Follower", "Defaulted", "overridden_by",
        "No overrides of InterfaceShapes.Follower.Defaulted() [declared]")]
    [InlineData("Follower", "PlainChanged", "used_by",
        "No users of InterfaceShapes.Follower.PlainChanged [declared] (via add_PlainChanged, remove_PlainChanged)\n" +
        "Override: code can call InterfaceShapes.Control.PlainChanged instead, so only references to this override are " +
        "listed; 'call_sites' on add_PlainChanged and remove_PlainChanged lists those calls whose receiver's type is " +
        "InterfaceShapes.Follower or derives from it.")]
    public async Task AMethodAPropertyAnEventAndAnAccessor_AreAnsweredAlike(
        string typeName, string memberName, string kind, string expected)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = FixturePath("InterfaceShapes.dll"),
            ["type_name"] = "InterfaceShapes." + typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
        });

        Assert.Equal(expected, text);
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
