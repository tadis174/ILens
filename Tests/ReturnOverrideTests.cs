using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for an override that returns a more specific type than the member it
/// overrides. The compiler writes such an override as a new virtual member that names the
/// member it overrides in a row of its own, so its flags do not say that it overrides, and a
/// call on an object of its class names the override. Every answer treats it as the override
/// it is. The InterfaceShapes fixture's Returns.cs holds the shapes: Letter overrides the
/// members of Sheet that way, Note overrides Letter's, once with Letter's return type and once
/// with a more specific one again, Slip overrides Note's, Seal's override is sealed and Draft's
/// abstract, and as the control Blank declares Copy anew and Card overrides that one. The
/// fixture targets .NET 10, since the compiler refuses such an override for netstandard2.0.
/// </summary>
public sealed class ReturnOverrideTests : IClassFixture<ILensServerFixture>
{
    private const string Returns = "InterfaceShapes.Returns.";

    // Sheet's Copy implements IPrintable's, and each override of it runs in its place.
    private const string ThroughTheInterface =
        "Implementation: code can call " + Returns + "IPrintable.Copy() instead; ask about that member for its uses.";

    private readonly ILensServerFixture _server;

    public ReturnOverrideTests(ILensServerFixture server)
    {
        _server = server;
    }

    // The member list writes override, as the decompiled code does, sealed override for a
    // sealed one, which has no modifier at all by its flags, and abstract override for an
    // abstract one. A member declared anew stays virtual.
    [Theory]
    [InlineData("Letter",
        "5 members of public class InterfaceShapes.Returns.Letter : Sheet:\n" +
        "4 methods:\n" +
        "  public Letter()\n" +
        "  public override Letter Copy()\n" +
        "  public override Letter Fold()\n" +
        "  public override object Stamp()\n" +
        "1 property:\n" +
        "  public override Letter Next { get; }")]
    [InlineData("Note",
        "4 members of public class InterfaceShapes.Returns.Note : Memo:\n" +
        "3 methods:\n" +
        "  public Note()\n" +
        "  public override Letter Copy()\n" +
        "  public override Note Fold()\n" +
        "1 property:\n" +
        "  public sealed override Letter Next { get; }")]
    [InlineData("Seal",
        "2 members of public class InterfaceShapes.Returns.Seal : Sheet:\n" +
        "2 methods:\n" +
        "  public Seal()\n" +
        "  public sealed override Seal Copy()")]
    [InlineData("Draft",
        "2 members of public abstract class InterfaceShapes.Returns.Draft : Sheet:\n" +
        "2 methods:\n" +
        "  protected Draft()\n" +
        "  public abstract override Draft Copy()")]
    [InlineData("Blank",
        "2 members of public class InterfaceShapes.Returns.Blank : Sheet:\n" +
        "2 methods:\n" +
        "  public Blank()\n" +
        "  public virtual Sheet Copy()")]
    [InlineData("NumberTray",
        "2 members of public class InterfaceShapes.Returns.NumberTray : Tray<int>:\n" +
        "2 methods:\n" +
        "  public NumberTray()\n" +
        "  public override List<int> Items()")]
    // The overridden method is System.Exception's, which the mark on the override shows
    // without that class.
    [InlineData("Jam",
        "2 members of public class InterfaceShapes.Returns.Jam : Exception:\n" +
        "2 methods:\n" +
        "  public Jam()\n" +
        "  public override Jam GetBaseException()")]
    public async Task ListMembers_WritesAnOverrideWithAMoreSpecificReturnTypeAsAnOverride(string type, string expected)
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = FixturePath("InterfaceShapes.dll"),
            ["type_name"] = Returns + type,
        });

        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task ListMembers_WritesAnInheritedOneAndTheCopyMethodOfARecordAsOverridesToo()
    {
        string inherited = await CallToolText("list_members", new()
        {
            ["assembly"] = FixturePath("InterfaceShapes.dll"),
            ["type_name"] = Returns + "Memo",
            ["include_inherited"] = true,
        });
        string generated = await CallToolText("list_members", new()
        {
            ["assembly"] = FixturePath("InterfaceShapes.dll"),
            ["type_name"] = Returns + "Invoice",
            ["include_compiler_generated"] = true,
        });

        Assert.Contains("  public override Letter Copy()  [from InterfaceShapes.Returns.Letter]\n", inherited);
        Assert.Contains("  public override Letter Next { get; }  [from InterfaceShapes.Returns.Letter]", inherited);
        Assert.Contains("  public override Invoice <Clone>$()\n", generated);
    }

    // compare writes a member's line as list_members does. The other assembly holds no such
    // type, so every member is listed as one only the first has.
    [Fact]
    public async Task Compare_WritesAnOverrideWithAMoreSpecificReturnTypeAsListMembersDoes()
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = FixturePath("InterfaceShapes.dll"),
            ["assembly_b"] = FixturePath("ExplicitMembers.dll"),
            ["type_name"] = Returns + "Seal",
        });

        Assert.Equal(
            "2 member changes in InterfaceShapes.Returns.Seal, which only A has:\n" +
            "- public Seal()\n" +
            "- public sealed override Seal Copy()", text);
    }

    // Listed is what overrides the member, directly or through further overrides, whatever each
    // returns. Card's Copy overrides Blank's, which is declared anew, and is no override of
    // Sheet's. An accessor is answered with the accessors that override it.
    [Theory]
    [InlineData("Sheet", "Copy",
        "6 overrides of InterfaceShapes.Returns.Sheet.Copy() [declared]:\n" +
        Returns + "Draft.Copy()\n" +
        Returns + "Letter.Copy()\n" +
        Returns + "Note.Copy()\n" +
        Returns + "Seal.Copy()\n" +
        Returns + "Sketch.Copy()\n" +
        Returns + "Slip.Copy()")]
    [InlineData("Letter", "Copy",
        "2 overrides of InterfaceShapes.Returns.Letter.Copy() [declared]:\n" +
        Returns + "Note.Copy()\n" +
        Returns + "Slip.Copy()")]
    [InlineData("Note", "Copy",
        "1 override of InterfaceShapes.Returns.Note.Copy() [declared]:\n" +
        Returns + "Slip.Copy()")]
    [InlineData("Seal", "Copy",
        "No overrides of InterfaceShapes.Returns.Seal.Copy() [declared]")]
    [InlineData("Draft", "Copy",
        "1 override of InterfaceShapes.Returns.Draft.Copy() [declared]:\n" +
        Returns + "Sketch.Copy()")]
    [InlineData("Blank", "Copy",
        "1 override of InterfaceShapes.Returns.Blank.Copy() [declared]:\n" +
        Returns + "Card.Copy()")]
    [InlineData("Sheet", "Fold",
        "2 overrides of InterfaceShapes.Returns.Sheet.Fold() [declared]:\n" +
        Returns + "Letter.Fold()\n" +
        Returns + "Note.Fold()")]
    [InlineData("Sheet", "Stamp",
        "1 override of InterfaceShapes.Returns.Sheet.Stamp() [declared]:\n" +
        Returns + "Letter.Stamp()")]
    [InlineData("Sheet", "Next",
        "2 overrides of InterfaceShapes.Returns.Sheet.Next [declared]:\n" +
        Returns + "Letter.Next\n" +
        Returns + "Note.Next")]
    [InlineData("Sheet", "get_Next",
        "2 overrides of InterfaceShapes.Returns.Sheet.get_Next() [declared]:\n" +
        Returns + "Letter.get_Next()\n" +
        Returns + "Note.get_Next()")]
    [InlineData("Tray<>", "Items",
        "2 overrides of InterfaceShapes.Returns.Tray<>.Items() [declared]:\n" +
        Returns + "ListTray<>.Items()\n" +
        Returns + "NumberTray.Items()")]
    [InlineData("Form", "<Clone>$",
        "1 override of InterfaceShapes.Returns.Form.<Clone>$() [declared]:\n" +
        Returns + "Invoice.<Clone>$()")]
    public async Task OverriddenBy_ListsWhatOverridesTheMemberWhateverItReturns(string type, string member, string expected)
    {
        Assert.Equal(expected, await Analyze(Returns + type, member, "overridden_by"));
    }

    // The control on the hiding rule beyond the fixture: the ExplicitMembers fixture's
    // OverridingHidden overrides HidingBase's Get, which hides Source's.
    [Fact]
    public async Task OverriddenBy_LeavesOutAnOverrideOfAMemberThatHidesTheOneAskedAbout()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = FixturePath("ExplicitMembers.dll"),
            ["type_name"] = "ExplicitMembers.Source",
            ["member_name"] = "Get",
            ["kind"] = "overridden_by",
        });

        Assert.Equal(
            "1 override of ExplicitMembers.Source.Get() [declared]:\n" +
            "ExplicitMembers.OverridingSource.Get()", text);
    }

    // Every member that calls Copy through the slot Sheet's declaration introduced is a user of
    // that declaration: the call on a Letter, a Memo and a Note names Letter's override, and
    // the calls on a Slip, a Seal, a Draft and a Sketch each name that class's. The calls on a
    // Blank and a Card run through the slot Blank's Copy introduced, the call through the
    // interface names the interface's member, and Slip's own call through base. is not virtual.
    [Fact]
    public async Task UsedBy_OnTheDeclaration_ListsTheUsesOnAnObjectOfAClassThatOverridesIt()
    {
        Assert.Equal(
            "10 users of InterfaceShapes.Returns.Sheet.Copy() [declared]:\n" +
            Returns + "Clerk.InLambda(Letter)\n" +
            Returns + "Clerk.Later(Letter)\n" +
            Returns + "Clerk.OnDraft(Draft)\n" +
            Returns + "Clerk.OnLetter(Letter)\n" +
            Returns + "Clerk.OnMemo(Memo)\n" +
            Returns + "Clerk.OnNote(Note)\n" +
            Returns + "Clerk.OnSeal(Seal)\n" +
            Returns + "Clerk.OnSheet(Sheet)\n" +
            Returns + "Clerk.OnSketch(Sketch)\n" +
            Returns + "Clerk.OnSlip(Slip)\n" +
            ThroughTheInterface,
            await Analyze(Returns + "Sheet", "Copy", "used_by"));
    }

    [Fact]
    public async Task CallSites_OnTheDeclaration_ListsTheCallsOnAnObjectOfAClassThatOverridesIt()
    {
        Assert.Equal(
            "9 calls to InterfaceShapes.Returns.Sheet.Copy():\n" +
            Returns + "Clerk.InLambda(Letter)\n  letter.Copy()\n" +
            Returns + "Clerk.OnDraft(Draft)\n  draft.Copy()\n" +
            Returns + "Clerk.OnLetter(Letter)\n  letter.Copy()\n" +
            Returns + "Clerk.OnMemo(Memo)\n  memo.Copy()\n" +
            Returns + "Clerk.OnNote(Note)\n  note.Copy()\n" +
            Returns + "Clerk.OnSeal(Seal)\n  seal.Copy()\n" +
            Returns + "Clerk.OnSheet(Sheet)\n  sheet.Copy()\n" +
            Returns + "Clerk.OnSketch(Sketch)\n  sketch.Copy()\n" +
            Returns + "Clerk.OnSlip(Slip)\n  slip.Copy()\n" +
            ThroughTheInterface,
            await Analyze(Returns + "Sheet", "Copy", "call_sites"));
    }

    // Who uses such an override is what names it, and the line under the answer names the
    // declaration that introduced the member, through every override between the two. The
    // override runs in the place of Sheet's Copy, so it implements the interface's member too,
    // and the answer has both lines.
    [Theory]
    [InlineData("Letter",
        "5 users of InterfaceShapes.Returns.Letter.Copy() [declared]:\n" +
        Returns + "Clerk.InLambda(Letter)\n" +
        Returns + "Clerk.Later(Letter)\n" +
        Returns + "Clerk.OnLetter(Letter)\n" +
        Returns + "Clerk.OnMemo(Memo)\n" +
        Returns + "Clerk.OnNote(Note)\n")]
    [InlineData("Note",
        "1 user of InterfaceShapes.Returns.Note.Copy() [declared]:\n" +
        Returns + "Slip.Copy()\n")]
    [InlineData("Slip",
        "1 user of InterfaceShapes.Returns.Slip.Copy() [declared]:\n" +
        Returns + "Clerk.OnSlip(Slip)\n")]
    [InlineData("Seal",
        "1 user of InterfaceShapes.Returns.Seal.Copy() [declared]:\n" +
        Returns + "Clerk.OnSeal(Seal)\n")]
    [InlineData("Draft",
        "1 user of InterfaceShapes.Returns.Draft.Copy() [declared]:\n" +
        Returns + "Clerk.OnDraft(Draft)\n")]
    public async Task UsedBy_OnSuchAnOverride_ListsWhatNamesItAndNamesTheDeclaration(string type, string expectedUsers)
    {
        Assert.Equal(
            expectedUsers +
            ThroughTheInterface + "\n" +
            "Override: code can call " + Returns + "Sheet.Copy() instead, so only references to this override are listed; " +
            "'call_sites' lists those calls whose receiver's type is " + Returns + type + " or derives from it.",
            await Analyze(Returns + type, "Copy", "used_by"));
    }

    // A call is the override's when its receiver's type is the override's class or derives
    // from it, whichever override the call names.
    [Theory]
    [InlineData("Letter",
        "5 calls to InterfaceShapes.Returns.Letter.Copy():\n" +
        Returns + "Clerk.InLambda(Letter)\n  letter.Copy()\n" +
        Returns + "Clerk.OnLetter(Letter)\n  letter.Copy()\n" +
        Returns + "Clerk.OnMemo(Memo)\n  memo.Copy()\n" +
        Returns + "Clerk.OnNote(Note)\n  note.Copy()\n" +
        Returns + "Clerk.OnSlip(Slip)\n  slip.Copy()\n")]
    [InlineData("Note",
        "3 calls to InterfaceShapes.Returns.Note.Copy():\n" +
        Returns + "Clerk.OnNote(Note)\n  note.Copy()\n" +
        Returns + "Clerk.OnSlip(Slip)\n  slip.Copy()\n" +
        Returns + "Slip.Copy()\n  base.Copy()\n")]
    public async Task CallSites_OnSuchAnOverride_ListsTheCallsOnItsClassAndNamesTheDeclaration(string type, string expectedCalls)
    {
        Assert.Equal(
            expectedCalls +
            ThroughTheInterface + "\n" +
            "Override: code can call " + Returns + "Sheet.Copy() instead; listed are those calls whose receiver's type is " +
            Returns + type + " or derives from it.",
            await Analyze(Returns + type, "Copy", "call_sites"));
    }

    // The control: the Copy declared anew introduced a slot of its own, whose calls are its
    // own, and the override of it names it as the declaration.
    [Theory]
    [InlineData("Blank", "used_by",
        "2 users of InterfaceShapes.Returns.Blank.Copy() [declared]:\n" +
        Returns + "Clerk.OnBlank(Blank)\n" +
        Returns + "Clerk.OnCard(Card)")]
    [InlineData("Card", "used_by",
        "No users of InterfaceShapes.Returns.Card.Copy() [declared]\n" +
        "Override: code can call " + Returns + "Blank.Copy() instead, so only references to this override are listed; " +
        "'call_sites' lists those calls whose receiver's type is " + Returns + "Card or derives from it.")]
    public async Task AMemberDeclaredAnew_KeepsItsOwnUses(string type, string kind, string expected)
    {
        Assert.Equal(expected, await Analyze(Returns + type, "Copy", kind));
    }

    // A property overrides that way through its getter.
    [Theory]
    [InlineData("Sheet", "Next", "used_by",
        "3 users of InterfaceShapes.Returns.Sheet.Next [declared] (via get_Next):\n" +
        Returns + "Clerk.NextOfLetter(Letter)\n" +
        Returns + "Clerk.NextOfNote(Note)\n" +
        Returns + "Clerk.NextOfSheet(Sheet)")]
    [InlineData("Letter", "Next", "read_by",
        "2 readers of InterfaceShapes.Returns.Letter.Next [declared] (via get_Next):\n" +
        Returns + "Clerk.NextOfLetter(Letter)\n" +
        Returns + "Clerk.NextOfNote(Note)\n" +
        "Override: code can call " + Returns + "Sheet.Next instead, so only references to this override are listed; " +
        "'call_sites' on get_Next lists those calls whose receiver's type is " + Returns + "Letter or derives from it.")]
    [InlineData("Sheet", "get_Next", "call_sites",
        "3 calls to InterfaceShapes.Returns.Sheet.get_Next():\n" +
        Returns + "Clerk.NextOfLetter(Letter)\n  letter.Next\n" +
        Returns + "Clerk.NextOfNote(Note)\n  note.Next\n" +
        Returns + "Clerk.NextOfSheet(Sheet)\n  sheet.Next")]
    [InlineData("Note", "get_Next", "call_sites",
        "1 call to InterfaceShapes.Returns.Note.get_Next():\n" +
        Returns + "Clerk.NextOfNote(Note)\n  note.Next\n" +
        "Override: code can call " + Returns + "Sheet.get_Next() instead; listed are those calls whose receiver's type is " +
        Returns + "Note or derives from it.")]
    public async Task AProperty_IsAnsweredAsAMethodIs(string type, string member, string kind, string expected)
    {
        Assert.Equal(expected, await Analyze(Returns + type, member, kind));
    }

    // In a class with a type parameter the override names the base class's member through a
    // reference row.
    [Fact]
    public async Task UsedBy_OnTheDeclarationInAGenericClass_ListsTheUsesOnTheOverridingClasses()
    {
        Assert.Equal(
            "3 users of InterfaceShapes.Returns.Tray<>.Items() [declared]:\n" +
            Returns + "Clerk.ItemsOfListTray(ListTray<string>)\n" +
            Returns + "Clerk.ItemsOfNumberTray(NumberTray)\n" +
            Returns + "Clerk.ItemsOfTray(Tray<int>)",
            await Analyze(Returns + "Tray<>", "Items", "used_by"));
    }

    // The declaration the override of another assembly's method overrides is not read, and the
    // line says so.
    [Fact]
    public async Task UsedBy_OnAnOverrideOfAnotherAssemblysMethod_SaysThatItOverrides()
    {
        Assert.Equal(
            "1 user of InterfaceShapes.Returns.Jam.GetBaseException() [declared]:\n" +
            Returns + "Clerk.OnJam(Jam)\n" +
            "Override: code can call the System.Exception member it overrides instead, which another assembly defines, " +
            "so only references to this override are listed.",
            await Analyze(Returns + "Jam", "GetBaseException", "used_by"));
    }

    // Such an override implements the interface member that the member it overrides implements,
    // as an override with the same return type does. Card's Copy overrides one that only hides
    // the implementing member.
    [Theory]
    [InlineData("Letter", "Copy",
        "1 interface member implemented by InterfaceShapes.Returns.Letter.Copy() [declared]:\n" +
        Returns + "IPrintable.Copy()")]
    [InlineData("Slip", "Copy",
        "1 interface member implemented by InterfaceShapes.Returns.Slip.Copy() [declared]:\n" +
        Returns + "IPrintable.Copy()")]
    [InlineData("Seal", "Copy",
        "1 interface member implemented by InterfaceShapes.Returns.Seal.Copy() [declared]:\n" +
        Returns + "IPrintable.Copy()")]
    [InlineData("Letter", "Stamp",
        "1 interface member implemented by InterfaceShapes.Returns.Letter.Stamp() [declared]:\n" +
        Returns + "IPrintable.Stamp()")]
    [InlineData("Card", "Copy",
        "No interface members implemented by InterfaceShapes.Returns.Card.Copy() [declared]")]
    public async Task Implements_FollowsSuchAnOverrideToTheMemberItOverrides(string type, string member, string expected)
    {
        Assert.Equal(expected, await Analyze(Returns + type, member, "implements"));
    }

    private Task<string> Analyze(string typeName, string memberName, string kind) =>
        CallToolText("analyze", new()
        {
            ["assembly"] = FixturePath("InterfaceShapes.dll"),
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
