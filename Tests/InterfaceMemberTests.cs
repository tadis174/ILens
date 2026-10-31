using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for how list_members and compare write the members of an interface. A
/// declaration line shows no body, so an interface member's modifiers are all that tells its
/// shape: none for a member the implementing type supplies, <c>virtual</c> for one with a
/// default implementation, <c>sealed</c>, and <c>static</c>, <c>static abstract</c> or
/// <c>static virtual</c>, the same for a method, a property and an event. The fixture under
/// Tests/Fixtures/InterfaceShapes/ compiles to InterfaceShapes.dll alongside the shared
/// inspection target.
/// </summary>
public sealed class InterfaceMemberTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;
    private readonly string _fixtures;

    public InterfaceMemberTests(ILensServerFixture server)
    {
        _server = server;
        _fixtures = Path.GetDirectoryName(_server.AssemblyPath)!;
        var shapes = Path.Combine(_fixtures, "InterfaceShapes.dll");
        if (!File.Exists(shapes))
            throw new FileNotFoundException(
                $"InterfaceShapes fixture not found: {shapes}. " +
                "Tests/Fixtures/InterfaceShapes/ is expected to land here via Tests.csproj's " +
                "ProjectReference.");
    }

    [Fact]
    public async Task ListMembers_OnAnInterface_WritesEachShapeOfMemberInAFormOfItsOwn()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = Path.Combine(_fixtures, "InterfaceShapes.dll"),
            ["type_name"] = "InterfaceShapes.IEvery<>",
            ["accessibility"] = "all",
        });

        Assert.Equal(
            "25 members of public interface InterfaceShapes.IEvery<TSelf> where TSelf : IEvery<TSelf>:\n" +
            "10 methods:\n" +
            "  public virtual void Defaulted()\n" +
            "  public sealed void Fixed()\n" +
            "  protected void Guarded()\n" +
            "  private void Hidden()\n" +
            "  internal void Inner()\n" +
            "  public void Plain()\n" +
            "  public static void Shared()\n" +
            "  public static virtual void SharedDefaulted()\n" +
            "  public static abstract void SharedPlain()\n" +
            "  public static abstract TSelf op_Addition(TSelf left, TSelf right)\n" +
            "9 properties:\n" +
            "  public virtual int DefaultedValue { get; }\n" +
            "  public sealed int FixedValue { get; }\n" +
            "  protected int GuardedValue { get; }\n" +
            "  private int HiddenValue { get; }\n" +
            "  public int Item[int index] { get; }\n" +
            "  public int PlainValue { get; set; }\n" +
            "  public static virtual int SharedDefaultedValue { get; }\n" +
            "  public static abstract int SharedPlainValue { get; }\n" +
            "  public static int SharedValue { get; }\n" +
            "6 events:\n" +
            "  public virtual event EventHandler DefaultedChanged\n" +
            "  public sealed event EventHandler FixedChanged\n" +
            "  public event EventHandler PlainChanged\n" +
            "  public static event EventHandler SharedChanged\n" +
            "  public static virtual event EventHandler SharedDefaultedChanged\n" +
            "  public static abstract event EventHandler SharedPlainChanged",
            text);
    }

    [Fact]
    public async Task ListMembers_OnAnInterface_WritesAnOrdinaryMethodPropertyAndEventAlike()
    {
        // The three kinds of member an interface built for an older framework can have, none
        // with a modifier.
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = Path.Combine(_fixtures, "ExplicitMembers.dll"),
            ["type_name"] = "ExplicitMembers.IShape",
        });

        Assert.Equal(
            "3 members of public interface ExplicitMembers.IShape:\n" +
            "1 method:\n" +
            "  public double Area()\n" +
            "1 property:\n" +
            "  public string Name { get; }\n" +
            "1 event:\n" +
            "  public event EventHandler Changed",
            text);
    }

    [Fact]
    public async Task ListMembers_OnAnInterfaceThatImplementsItsBaseInterface_WritesThoseMembersAsAClassDoes()
    {
        // An implementation of a base interface's member takes no modifier, as a class's
        // explicit implementation takes none, and the one that makes Close abstract again
        // takes abstract.
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = Path.Combine(_fixtures, "InterfaceShapes.dll"),
            ["type_name"] = "InterfaceShapes.IBufferedFeed",
            ["accessibility"] = "all",
        });

        Assert.Equal(
            "4 members of public interface InterfaceShapes.IBufferedFeed : IFeed:\n" +
            "2 methods:\n" +
            "  private abstract void InterfaceShapes.IFeed.Close()\n" +
            "  private void InterfaceShapes.IFeed.Open()\n" +
            "1 property:\n" +
            "  private int InterfaceShapes.IFeed.Count { get; }\n" +
            "1 event:\n" +
            "  private event EventHandler InterfaceShapes.IFeed.Moved",
            text);
    }

    // The control: the members of a class keep the modifiers a class writes.
    [Theory]
    [InlineData("Control",
        "9 members of public abstract class InterfaceShapes.Control:\n" +
        "5 methods:\n" +
        "  protected Control()\n" +
        "  public virtual void Defaulted()\n" +
        "  public void Fixed()\n" +
        "  public abstract void Plain()\n" +
        "  public static void Shared()\n" +
        "2 properties:\n" +
        "  public virtual int DefaultedValue { get; }\n" +
        "  public abstract int PlainValue { get; set; }\n" +
        "2 events:\n" +
        "  public virtual event EventHandler DefaultedChanged\n" +
        "  public abstract event EventHandler PlainChanged")]
    [InlineData("Follower",
        "5 members of public class InterfaceShapes.Follower : Control:\n" +
        "3 methods:\n" +
        "  public Follower()\n" +
        "  public sealed override void Defaulted()\n" +
        "  public override void Plain()\n" +
        "1 property:\n" +
        "  public override int PlainValue { get; set; }\n" +
        "1 event:\n" +
        "  public override event EventHandler PlainChanged")]
    public async Task ListMembers_OnAClass_KeepsTheModifiersAClassWrites(string type, string expected)
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = Path.Combine(_fixtures, "InterfaceShapes.dll"),
            ["type_name"] = $"InterfaceShapes.{type}",
        });

        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task Compare_MemberChanges_OnAnInterface_WritesItsMembersAsListMembersDoes()
    {
        // In BaseShift2, IGrowing declares a method, a property and an event more.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = Path.Combine(_fixtures, "BaseShift.dll"),
            ["assembly_b"] = Path.Combine(_fixtures, "BaseShift2.dll"),
            ["type_name"] = "BaseShift.Members.IGrowing",
        });

        Assert.Equal(
            new[]
            {
                "3 member changes in BaseShift.Members.IGrowing:",
                "+ public void Added()",
                "+ public event EventHandler AddedChanged",
                "+ public int AddedValue { get; }",
            },
            text.Split('\n'));
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
