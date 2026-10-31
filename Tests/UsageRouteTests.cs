using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the line that ends an answer about who uses a member when the member
/// implements an interface member: code can call the interface's member instead, and the answer
/// lists only what names the member asked about. The line follows what <c>implements</c> answers,
/// so the cases are those of its tests: the ExplicitMembers fixture's Sources.cs and
/// Inherited.cs, whose Twins.cs adds a method that implements for two interfaces; the classes
/// the compiler generates in CallShapes; the assembly <see cref="RowImplementationAssembly"/>
/// writes; and ExternalUser's Dial, whose interface IGauge only the second server can load.
/// </summary>
public sealed class UsageRouteTests
    : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    private const string Sources = "ExplicitMembers.";
    private const string Inherited = "ExplicitMembers.Inherited.";
    private const string Twins = "ExplicitMembers.Twins.";

    private const string GaugeNotLoaded = "; ILens could not load ExternalLib, Version=1.0.0.0";
    private const string NotChecked = "Not checked: whether it implements an interface member" + GaugeNotLoaded;

    private readonly ILensServerFixture _shared;
    private readonly ReferencesServerFixture _references;
    private readonly string _explicitMembersPath;
    private readonly string _callShapesPath;
    private readonly string _userPath;

    public UsageRouteTests(ILensServerFixture shared, ReferencesServerFixture references)
    {
        _shared = shared;
        _references = references;
        string testOutput = Path.GetDirectoryName(shared.AssemblyPath)!;
        _explicitMembersPath = Path.Combine(testOutput, "ExplicitMembers.dll");
        _callShapesPath = Path.Combine(testOutput, "CallShapes.dll");
        _userPath = Path.Combine(testOutput, "ExternalUser.dll");
    }

    private static string Through(string interfaceMember) =>
        $"Implementation: code can call {interfaceMember} instead; ask about that member for its uses.";

    // A member that implements, the question, the header of the answer and the interface
    // member the line names. Nothing in the fixture names any of these members itself.
    public static TheoryData<string, string, string, string, string> ImplementingMembers => new()
    {
        // The ordinary case: a public method of a class that lists the interface.
        { Sources + "Source", "Get", "used_by", "No users of ExplicitMembers.Source.Get() [declared]", Sources + "ISource.Get()" },
        { Sources + "Source", "Get", "call_sites", "No calls to ExplicitMembers.Source.Get()", Sources + "ISource.Get()" },
        // The form C# names after the interface.
        {
            Sources + "SplitSource", "ExplicitMembers.ISource.Get", "used_by",
            "No users of ExplicitMembers.SplitSource.ExplicitMembers.ISource.Get() [declared]", Sources + "ISource.Get()"
        },
        // A member of a class without the interface, which classes below it bind.
        { Inherited + "PlainBase", "Get", "used_by", $"No users of {Inherited}PlainBase.Get() [declared]", Inherited + "IPart.Get()" },
        { Inherited + "TagBase", "Tag", "used_by", $"No users of {Inherited}TagBase.Tag() [declared]", Inherited + "ITag.Tag()" },
        // A property, an event and an accessor name the interface's member of their kind.
        {
            Inherited + "PlainBase", "Name", "read_by",
            $"No readers of {Inherited}PlainBase.Name [declared] (via get_Name)", Inherited + "IPart.Name"
        },
        {
            Inherited + "PlainBase", "Changed", "used_by",
            $"No users of {Inherited}PlainBase.Changed [declared] (via add_Changed, remove_Changed)", Inherited + "IPart.Changed"
        },
        { Inherited + "Holder", "get_Name", "call_sites", $"No calls to {Inherited}Holder.get_Name()", Inherited + "IPart.get_Name()" },
        {
            Twins + "Both", "Level", "assigned_by",
            $"No writers of {Twins}Both.Level [declared] (via set_Level)", Twins + "ILeft.Level"
        },
    };

    [Theory]
    [MemberData(nameof(ImplementingMembers))]
    public async Task UsageOfAMemberThatImplements_EndsWithTheInterfaceMemberCodeCanCall(
        string typeName, string memberName, string kind, string header, string interfaceMember)
    {
        // Without the line, the empty answer would read as proof that nothing reaches the member.
        Assert.Equal(
            header + "\n" + Through(interfaceMember),
            await Analyze(_shared.Client, _explicitMembersPath, typeName, memberName, kind));
    }

    // Each has the name and parameters of an interface member and implements none, as the
    // comment beside its class in the fixture says; implements answers the same for each.
    [Theory]
    [InlineData(Sources + "HidingSource", "Get")]
    [InlineData(Sources + "TypedSource", "Get")]
    [InlineData(Sources + "InternalSource", "Get")]
    [InlineData(Sources + "HidingBase", "Get")]
    [InlineData(Sources + "OverridingHidden", "Get")]
    [InlineData(Sources + "SplitSource", "Get")]
    [InlineData(Inherited + "Stranger", "Name")]
    [InlineData(Inherited + "Hider", "Get")]
    public async Task UsageOfAMemberThatOnlySharesAnInterfaceMembersName_HasNoSuchLine(string typeName, string memberName)
    {
        string text = await Analyze(_shared.Client, _explicitMembersPath, typeName, memberName, "used_by");

        Assert.DoesNotContain(text.Split('\n'), line => line.StartsWith("Implementation:", StringComparison.Ordinal));
    }

    // OverridingSource's Get runs in the place of Source's, which ISource.Get is bound to, so
    // code reaches it through the interface's member and through the declaration it overrides.
    [Fact]
    public async Task UsageOfAnOverrideThatImplements_HasBothLines()
    {
        const string type = Sources + "OverridingSource";

        Assert.Equal(
            "No users of ExplicitMembers.OverridingSource.Get() [declared]\n" +
            Through(Sources + "ISource.Get()") + "\n" +
            "Override: code can call ExplicitMembers.Source.Get() instead, so only references to this override are listed; " +
            "'call_sites' lists those calls whose receiver's type is ExplicitMembers.OverridingSource or derives from it.",
            await Analyze(_shared.Client, _explicitMembersPath, type, "Get", "used_by"));
        Assert.Equal(
            "No calls to ExplicitMembers.OverridingSource.Get()\n" +
            Through(Sources + "ISource.Get()") + "\n" +
            "Override: code can call ExplicitMembers.Source.Get() instead; listed are those calls whose receiver's type is " +
            "ExplicitMembers.OverridingSource or derives from it.",
            await Analyze(_shared.Client, _explicitMembersPath, type, "Get", "call_sites"));
    }

    [Theory]
    [InlineData("used_by", "No users of ExplicitMembers.Twins.Both.Reset() [declared]")]
    [InlineData("call_sites", "No calls to ExplicitMembers.Twins.Both.Reset()")]
    public async Task UsageOfAMemberThatImplementsForTwoInterfaces_NamesBothMembers(string kind, string header)
    {
        Assert.Equal(
            header + "\n" +
            $"Implementation: code can call {Twins}ILeft.Reset(), {Twins}IRight.Reset() instead; " +
            "ask about those members for their uses.",
            await Analyze(_shared.Client, _explicitMembersPath, Twins + "Both", "Reset", kind));
    }

    // The MoveNext of the class the compiler generates for an iterator and for an async method
    // implements by its metadata row and carries the interface member's plain name.
    [Theory]
    [InlineData("CallShapes.Callers.<InIterator>d__10", "used_by",
        "No users of CallShapes.Callers.<InIterator>d__10.MoveNext() [declared]",
        "System.Collections.IEnumerator.MoveNext()")]
    [InlineData("CallShapes.AsyncUses.Clerk.<AwaitOnly>d__5", "call_sites",
        "No calls to CallShapes.AsyncUses.Clerk.<AwaitOnly>d__5.MoveNext()",
        "System.Runtime.CompilerServices.IAsyncStateMachine.MoveNext()")]
    public async Task UsageOfAGeneratedMoveNext_NamesTheInterfaceMember(
        string typeName, string kind, string header, string interfaceMember)
    {
        Assert.Equal(
            header + "\n" + Through(interfaceMember),
            await Analyze(_shared.Client, _callShapesPath, typeName, "MoveNext", kind));
    }

    // The compiler has an iterator's non-generic GetEnumerator call the generic one by name, so
    // an implementation C# names after the interface can have a user, and the line does not
    // say that code reaches it through the interface alone. The implementation's name is the
    // one its metadata row holds, the interface member is written as ILens writes a member.
    [Fact]
    public async Task UsageOfAnExplicitImplementationCodeNames_ListsTheUserAboveTheLine()
    {
        const string type = "CallShapes.Callers.<InIterator>d__10";
        const string generic = "System.Collections.Generic.IEnumerable<System.Int32>.GetEnumerator";

        Assert.Equal(
            $"1 user of {type}.{generic}() [declared]:\n" +
            $"{type}.System.Collections.IEnumerable.GetEnumerator()\n" +
            Through("System.Collections.Generic.IEnumerable<int>.GetEnumerator()"),
            await Analyze(_shared.Client, _callShapesPath, type, generic, "used_by"));
    }

    // The forms C# cannot write: a method that implements under a name of its own, a private one
    // of that kind, a method that stands for a property's getter, and beside them a method of
    // the interface member's name that the row keeps from implementing it.
    [Theory]
    [InlineData("RowBound.Worker", "Start", "used_by",
        "No users of RowBound.Worker.Start() [declared]", "RowBound.IJob.Run()")]
    [InlineData("RowBound.Step", "Step_Order", "call_sites",
        "No calls to RowBound.Step.Step_Order()", "RowBound.IStep.Order()")]
    [InlineData("RowBound.Worker", "RowBound.IJob.get_Size", "used_by",
        "No users of RowBound.Worker.RowBound.IJob.get_Size() [declared]", "RowBound.IJob.get_Size()")]
    [InlineData("RowBound.Worker", "Run", "used_by", "No users of RowBound.Worker.Run() [declared]", null)]
    public async Task UsageOfAMemberThatImplementsByItsRowAlone_NamesTheInterfaceMember(
        string typeName, string memberName, string kind, string header, string? interfaceMember)
    {
        var path = RowImplementationAssembly.Write();
        try
        {
            Assert.Equal(
                interfaceMember is null ? header : header + "\n" + Through(interfaceMember),
                await Analyze(_shared.Client, path, typeName, memberName, kind));
        }
        finally
        {
            RowImplementationAssembly.TryDelete(path);
        }
    }

    // A member of Dial and the question, the header of the answer, and the member of IGauge it
    // implements, or null for Tap, which implements nothing.
    public static TheoryData<string, string, string, string?> GaugeMembers => new()
    {
        { "Read", "used_by", "No users of ExternalUser.Dial.Read() [declared]", "ExternalLib.IGauge.Read()" },
        { "Read", "call_sites", "No calls to ExternalUser.Dial.Read()", "ExternalLib.IGauge.Read()" },
        { "Unit", "read_by", "No readers of ExternalUser.Dial.Unit [declared] (via get_Unit)", "ExternalLib.IGauge.Unit" },
        {
            "Moved", "used_by",
            "No users of ExternalUser.Dial.Moved [declared] (via add_Moved, remove_Moved)", "ExternalLib.IGauge.Moved"
        },
        { "Tap", "used_by", "No users of ExternalUser.Dial.Tap() [declared]", null },
    };

    [Theory]
    [MemberData(nameof(GaugeMembers))]
    public async Task UsageOfAMemberOfAClassWhoseInterfaceWasNotRead_SaysItWasNotChecked(
        string memberName, string kind, string header, string? implemented)
    {
        // Nothing in ExternalUser.dll says what IGauge declares. Where its assembly does not
        // load, a missing line would read as proof that the member implements nothing.
        string loaded = await Analyze(_references.Client, _userPath, "ExternalUser.Dial", memberName, kind);
        string notLoaded = await Analyze(_shared.Client, _userPath, "ExternalUser.Dial", memberName, kind);

        Assert.Equal(implemented is null ? header : header + "\n" + Through(implemented), loaded);
        Assert.Equal(header + "\n" + NotChecked, notLoaded);
    }

    [Fact]
    public async Task UsageWithOneInterfaceReadAndOneNot_NamesWhatItFoundAndSaysMoreWasNotChecked()
    {
        // Reset implements ILocalGauge.Reset, which the assembly defines. IGauge might declare
        // a Reset as well.
        const string found =
            "No users of ExternalUser.Dial.Reset() [declared]\n" +
            "Implementation: code can call ExternalUser.ILocalGauge.Reset() instead; ask about that member for its uses.";

        Assert.Equal(found, await Analyze(_references.Client, _userPath, "ExternalUser.Dial", "Reset", "used_by"));
        Assert.Equal(
            found + "\nNot checked: whether it implements more interface members" + GaugeNotLoaded,
            await Analyze(_shared.Client, _userPath, "ExternalUser.Dial", "Reset", "used_by"));
    }

    // A class binds an interface member by name only to a public member that is not static, so
    // whatever IGauge declares, neither the private Tick nor the static Zero implements it, and
    // nothing is left unchecked.
    [Theory]
    [InlineData("Tick")]
    [InlineData("Zero")]
    public async Task UsageOfAMemberNoBaseTypeCanBind_HasNoNotCheckedLine(string memberName)
    {
        Assert.Equal(
            $"No users of ExternalUser.Dial.{memberName}() [declared]",
            await Analyze(_shared.Client, _userPath, "ExternalUser.Dial", memberName, "used_by"));
    }

    [Fact]
    public async Task UsageOfAnExplicitImplementation_NamesItsMemberThoughTheInterfaceDoesNotLoad()
    {
        // The implementation's own metadata row names the member, so nothing is left unchecked.
        Assert.Equal(
            "No users of ExternalUser.SealedDial.ExternalLib.IGauge.Read() [declared]\n" + Through("ExternalLib.IGauge.Read()"),
            await Analyze(_shared.Client, _userPath, "ExternalUser.SealedDial", "Read", "used_by"));
    }

    private static async Task<string> Analyze(McpClient client, string assembly, string typeName, string memberName, string kind)
    {
        CallToolResult result = await client.CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["assembly"] = assembly,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
        });
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"analyze returned an error: {text}");
        return text;
    }
}
