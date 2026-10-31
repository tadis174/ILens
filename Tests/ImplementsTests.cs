using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for <c>analyze</c> with <c>implements</c>, which names the interface members
/// a method, property, event or accessor implements. The ExternalUser fixture's Dial implements
/// ExternalLib's IGauge and its own ILocalGauge implicitly, and its SealedDial implements IGauge
/// explicitly. ExternalLib.dll lands only in the References folder of the test output, so the
/// shared server cannot load IGauge, while the second server, which has that folder as an
/// allow-root, can. The ExplicitMembers fixture's Sources.cs holds one class for each way a
/// member can have the name and parameters of an interface member without implementing it.
/// </summary>
public sealed class ImplementsTests
    : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    private const string GaugeNotChecked =
        "Not checked: what ExternalLib.IGauge and its base types declare; ILens could not load " +
        "ExternalLib, Version=1.0.0.0";

    private readonly ILensServerFixture _shared;
    private readonly ReferencesServerFixture _references;
    private readonly string _userPath;
    private readonly string _explicitMembersPath;

    public ImplementsTests(ILensServerFixture shared, ReferencesServerFixture references)
    {
        _shared = shared;
        _references = references;
        string testOutput = Path.GetDirectoryName(shared.AssemblyPath)!;
        _userPath = Path.Combine(testOutput, "ExternalUser.dll");
        _explicitMembersPath = Path.Combine(testOutput, "ExplicitMembers.dll");
    }

    // A member of Dial, as analyze names it, and the member of IGauge it implements.
    public static TheoryData<string, string, string> GaugeMembers => new()
    {
        { "Read", "ExternalUser.Dial.Read()", "ExternalLib.IGauge.Read()" },
        { "Unit", "ExternalUser.Dial.Unit", "ExternalLib.IGauge.Unit" },
        { "get_Unit", "ExternalUser.Dial.get_Unit()", "ExternalLib.IGauge.get_Unit()" },
        { "Moved", "ExternalUser.Dial.Moved", "ExternalLib.IGauge.Moved" },
    };

    [Theory]
    [MemberData(nameof(GaugeMembers))]
    public async Task Implements_OnAMemberOfAnInterfaceFromAnotherAssembly_NamesIt_OrSaysWhatWasNotRead(
        string memberName, string subject, string implemented)
    {
        // Nothing in ExternalUser.dll says what IGauge declares. Where its assembly loads, the
        // member is found; where it does not, an empty answer alone would read as proof that
        // the member implements nothing.
        string loaded = await Implements(_references.Client, _userPath, "ExternalUser.Dial", memberName);
        string notLoaded = await Implements(_shared.Client, _userPath, "ExternalUser.Dial", memberName);

        Assert.Equal($"1 interface member implemented by {subject} [declared]:\n{implemented}", loaded);
        Assert.Equal($"No interface members implemented by {subject} [declared]\n{GaugeNotChecked}", notLoaded);
    }

    [Fact]
    public async Task Implements_WithOneInterfaceReadAndOneNot_ListsWhatItFoundAndSaysWhatWasNotRead()
    {
        // Reset implements ILocalGauge.Reset, which the assembly defines. IGauge might declare
        // a Reset as well, so the list does not show that it is complete.
        const string found =
            "1 interface member implemented by ExternalUser.Dial.Reset() [declared]:\nExternalUser.ILocalGauge.Reset()";

        string loaded = await Implements(_references.Client, _userPath, "ExternalUser.Dial", "Reset");
        string notLoaded = await Implements(_shared.Client, _userPath, "ExternalUser.Dial", "Reset");

        Assert.Equal(found, loaded);
        Assert.Equal(found + "\n" + GaugeNotChecked, notLoaded);
    }

    [Fact]
    public async Task Implements_OnAMemberThatImplementsNothing_IsEmptyWithoutALineWhenEveryBaseTypeIsRead()
    {
        string text = await Implements(_references.Client, _userPath, "ExternalUser.Dial", "Tap");

        Assert.Equal("No interface members implemented by ExternalUser.Dial.Tap() [declared]", text);
    }

    [Fact]
    public async Task Implements_OnAMemberNoBaseTypeCanBind_SaysNothingWasLeftUnchecked()
    {
        // A class binds an interface member by name only to a public member that is not static.
        // Tap is one, so IGauge, which was not read, may declare a member it implements; Tick
        // is private and Zero static, and neither has a row, so they implement nothing whatever
        // IGauge declares.
        string publicMember = await Implements(_shared.Client, _userPath, "ExternalUser.Dial", "Tap");
        string privateMember = await Implements(_shared.Client, _userPath, "ExternalUser.Dial", "Tick");
        string staticMember = await Implements(_shared.Client, _userPath, "ExternalUser.Dial", "Zero");

        Assert.Equal($"No interface members implemented by ExternalUser.Dial.Tap() [declared]\n{GaugeNotChecked}", publicMember);
        Assert.Equal("No interface members implemented by ExternalUser.Dial.Tick() [declared]", privateMember);
        Assert.Equal("No interface members implemented by ExternalUser.Dial.Zero() [declared]", staticMember);
    }

    [Fact]
    public async Task Implements_OnAnExplicitImplementation_NamesItsMemberThoughTheInterfaceDoesNotLoad()
    {
        // The implementation's own metadata row names the member, and it implements nothing
        // else, so the answer is complete without IGauge.
        string text = await Implements(_shared.Client, _userPath, "ExternalUser.SealedDial", "Read");

        Assert.Equal(
            "1 interface member implemented by ExternalUser.SealedDial.ExternalLib.IGauge.Read() [declared]:\n" +
            "ExternalLib.IGauge.Read()",
            text);
    }

    [Fact]
    public async Task Implements_OnAMemberOfACoreLibraryInterface_LeavesOutWhatAnExplicitMemberImplements()
    {
        // Bag's public GetEnumerator has the name and parameters of both interfaces' method,
        // but its explicit IEnumerable.GetEnumerator implements the one without type arguments.
        string text = await Implements(_shared.Client, _explicitMembersPath, "ExplicitMembers.Bag", "GetEnumerator");

        Assert.Equal(
            "1 interface member implemented by ExplicitMembers.Bag.GetEnumerator() [declared]:\n" +
            "System.Collections.Generic.IEnumerable<int>.GetEnumerator()",
            text);
    }

    // Each type of Sources.cs has a Get with the name and parameters of ISource.Get; whether it
    // implements it turns on what the comment beside the row says.
    [Theory]
    [InlineData("Source", true)]            // lists ISource and declares the method
    [InlineData("OverridingSource", true)]  // overrides the method that implements it
    [InlineData("HidingSource", false)]     // hides that method without overriding it
    [InlineData("TypedSource", false)]      // lists ISource again, but returns another type
    [InlineData("InternalSource", false)]   // lists ISource again, but is not public
    [InlineData("HidingBase", false)]       // hides the method with a virtual one
    [InlineData("OverridingHidden", false)] // overrides the method that only hides
    [InlineData("SplitSource", false)]      // stands beside an explicit ISource.Get
    [InlineData("OverridingSplit", false)]  // overrides the method that stands beside one
    [InlineData("IRenewedSource", false)]   // an interface, whose Get hides ISource.Get
    public async Task Implements_OnAMemberWithAnInterfaceMembersNameAndParameters_FollowsWhatTheClassBindsItTo(
        string typeName, bool implements)
    {
        string text = await Implements(_shared.Client, _explicitMembersPath, $"ExplicitMembers.{typeName}", "Get");

        Assert.Equal(
            implements
                ? $"1 interface member implemented by ExplicitMembers.{typeName}.Get() [declared]:\nExplicitMembers.ISource.Get()"
                : $"No interface members implemented by ExplicitMembers.{typeName}.Get() [declared]",
            text);
    }

    // Explicit implementations of an interface the assembly defines: a method, a property, an
    // accessor and an event, and the method that stands beside an ordinary one of its name.
    [Theory]
    [InlineData("Circle", "Area", "ExplicitMembers.Circle.ExplicitMembers.IShape.Area()", "ExplicitMembers.IShape.Area()")]
    [InlineData("Circle", "Name", "ExplicitMembers.Circle.ExplicitMembers.IShape.Name", "ExplicitMembers.IShape.Name")]
    [InlineData("Circle", "get_Name", "ExplicitMembers.Circle.ExplicitMembers.IShape.get_Name()", "ExplicitMembers.IShape.get_Name()")]
    [InlineData("Circle", "Changed", "ExplicitMembers.Circle.ExplicitMembers.IShape.Changed", "ExplicitMembers.IShape.Changed")]
    [InlineData("SplitSource", "ExplicitMembers.ISource.Get", "ExplicitMembers.SplitSource.ExplicitMembers.ISource.Get()", "ExplicitMembers.ISource.Get()")]
    public async Task Implements_OnAnExplicitImplementation_NamesTheMemberItIsWrittenFor(
        string typeName, string memberName, string subject, string implemented)
    {
        string text = await Implements(_shared.Client, _explicitMembersPath, $"ExplicitMembers.{typeName}", memberName);

        Assert.Equal($"1 interface member implemented by {subject} [declared]:\n{implemented}", text);
    }

    private static async Task<string> Implements(McpClient client, string assembly, string typeName, string memberName)
    {
        CallToolResult result = await client.CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["assembly"] = assembly,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = "implements",
        });
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"analyze returned an error: {text}");
        return text;
    }
}
