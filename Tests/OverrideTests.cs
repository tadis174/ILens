using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for what analyze answers about an override. C# compiles a call to an
/// override as a call to the declaration that introduced its slot, unless the call goes through
/// <c>base.</c>, which names the override itself. The CallShapes fixture's Overrides.cs
/// overrides a method and a property its Creature declares, the ExplicitMembers fixture's
/// Circle overrides members of System.Object, and the ExternalUser fixture's DerivedWidget
/// overrides a method of a class whose assembly ILens does not load.
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
        "CallShapes.Dog.Die(int): base.Die(cause + 1)\n" +
        "CallShapes.Undertaker.OnAnimal(Animal): animal.Die(2)\n" +
        "CallShapes.Undertaker.OnDog(Dog): dog.Die(3)\n" +
        "Override: code calls CallShapes.Creature.Die(int) instead; listed are those calls whose receiver's type is " +
        "CallShapes.Animal or derives from it.")]
    [InlineData("CallShapes.Dog", "Die",
        "1 call to CallShapes.Dog.Die(int):\n" +
        "CallShapes.Undertaker.OnDog(Dog): dog.Die(3)\n" +
        "Override: code calls CallShapes.Creature.Die(int) instead; listed are those calls whose receiver's type is " +
        "CallShapes.Dog or derives from it.")]
    [InlineData("CallShapes.Ghost", "Die",
        "1 call to CallShapes.Ghost.Die(int):\n" +
        "CallShapes.Undertaker.OnGhost(Ghost): ghost.Die(4)\n" +
        "Override: code calls CallShapes.Creature.Die(int) instead; listed are those calls whose receiver's type is " +
        "CallShapes.Ghost or derives from it.")]
    [InlineData("CallShapes.Animal", "get_Health",
        "1 call to CallShapes.Animal.get_Health():\n" +
        "CallShapes.Undertaker.ReadAnimalHealth(Animal): animal.Health\n" +
        "Override: code calls CallShapes.Creature.get_Health() instead; listed are those calls whose receiver's type is " +
        "CallShapes.Animal or derives from it.")]
    [InlineData("CallShapes.Animal", "set_Health",
        "1 call to CallShapes.Animal.set_Health(int):\n" +
        "CallShapes.Undertaker.SetDogHealth(Dog): dog.Health = 5\n" +
        "Override: code calls CallShapes.Creature.set_Health(int) instead; listed are those calls whose receiver's type is " +
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
        "Override: code calls CallShapes.Creature.Die(int) instead, so only references to this override are listed; " +
        "'call_sites' lists those calls whose receiver's type is CallShapes.Animal or derives from it.")]
    [InlineData("CallShapes.Dog", "Die", "used_by",
        "No users of CallShapes.Dog.Die(int) [declared]\n" +
        "Override: code calls CallShapes.Creature.Die(int) instead, so only references to this override are listed; " +
        "'call_sites' lists those calls whose receiver's type is CallShapes.Dog or derives from it.")]
    [InlineData("CallShapes.Animal", "Health", "used_by",
        "No users of CallShapes.Animal.Health [declared] (via get_Health, set_Health)\n" +
        "Override: code calls CallShapes.Creature.Health instead, so only references to this override are listed; " +
        "'call_sites' on get_Health and set_Health lists those calls whose receiver's type is CallShapes.Animal or derives from it.")]
    [InlineData("CallShapes.Animal", "Health", "read_by",
        "No readers of CallShapes.Animal.Health [declared] (via get_Health)\n" +
        "Override: code calls CallShapes.Creature.Health instead, so only references to this override are listed; " +
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
    // line names the class the override's own class inherits it from.
    [Theory]
    [InlineData("ExplicitMembers.dll", "ExplicitMembers.Circle", "GetHashCode", "call_sites",
        "No calls to ExplicitMembers.Circle.GetHashCode()\n" +
        "Override: code calls the System.Object member it overrides instead, which another assembly defines, " +
        "so those calls are not counted here.")]
    [InlineData("ExplicitMembers.dll", "ExplicitMembers.Circle", "GetHashCode", "used_by",
        "No users of ExplicitMembers.Circle.GetHashCode() [declared]\n" +
        "Override: code calls the System.Object member it overrides instead, which another assembly defines, " +
        "so only references to this override are listed.")]
    [InlineData("ExternalUser.dll", "ExternalUser.DerivedWidget", "Draw", "used_by",
        "No users of ExternalUser.DerivedWidget.Draw() [declared]\n" +
        "Override: code calls the ExternalLib.Widget member it overrides instead, which another assembly defines, " +
        "so only references to this override are listed.")]
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
