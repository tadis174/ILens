using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the members a class inherits from a base class another assembly
/// defines, and for <c>applied_to</c> on an attribute class whose base attribute class another
/// assembly defines. The ExternalUser fixture's DerivedWidget derives from ExternalLib's Widget
/// and overrides its Draw, its LocalTagAttribute derives from ExternalLib's TagAttribute, and
/// ExternalLib's own types derive from classes of the core library. ExternalLib.dll lands only
/// in the References folder of the test output, so the shared server cannot load it for
/// ExternalUser and names the base class where it stopped, while the second server, which has
/// that folder as an allow-root, reads Widget's members. Both servers find the installed .NET
/// libraries.
/// </summary>
public sealed class InheritedMemberTests
    : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    // ExternalUser's reference to ExternalLib, which sets no version of its own.
    private const string LibraryOrigin = "ExternalLib, Version=1.0.0.0";

    private readonly ILensServerFixture _shared;
    private readonly ReferencesServerFixture _references;
    private readonly string _userPath;
    private readonly string _libraryPath;

    public InheritedMemberTests(ILensServerFixture shared, ReferencesServerFixture references)
    {
        _shared = shared;
        _references = references;
        string testOutput = Path.GetDirectoryName(shared.AssemblyPath)!;
        _userPath = Path.Combine(testOutput, "ExternalUser.dll");
        _libraryPath = Path.Combine(testOutput, "References", "ExternalLib.dll");
    }

    [Fact]
    public async Task ListMembers_WithInherited_ListsWhatABaseFromAnotherAssemblyDeclares()
    {
        // DerivedWidget's override of Draw hides Widget's, and Widget's constructors are not
        // inherited. Its property Size hides Widget's field of that name, and both are listed,
        // as they are for two classes of one assembly. The private field the compiler adds to
        // Widget for the subscribers of Changed is generated, so it is neither listed nor counted
        // among what the defaults leave out.
        string text = await CallToolText(_references.Client, "list_members", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["include_inherited"] = true,
        });

        Assert.Equal(
            "10 members of public class ExternalUser.DerivedWidget : Widget:\n" +
            "5 methods:\n" +
            "  public DerivedWidget()\n" +
            "  public override void Draw()\n" +
            "  public void Paint()  [from ExternalLib.Widget]\n" +
            "  public void Paint(int times)  [from ExternalLib.Widget]\n" +
            "  public void Touch()  [from ExternalLib.Widget]\n" +
            "2 properties:\n" +
            "  public string Label { get; set; }  [from ExternalLib.Widget]\n" +
            "  public int Size { get; set; }\n" +
            "2 fields:\n" +
            "  public static int Count  [from ExternalLib.Widget]\n" +
            "  public int Size  [from ExternalLib.Widget]\n" +
            "1 event:\n" +
            "  public event EventHandler Changed  [from ExternalLib.Widget]",
            text);
    }

    [Fact]
    public async Task ListMembers_WithInherited_NamesABaseItCannotLoad()
    {
        string text = await CallToolText(_shared.Client, "list_members", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["include_inherited"] = true,
        });

        Assert.Equal(
            "3 members of public class ExternalUser.DerivedWidget : Widget:\n" +
            "2 methods:\n" +
            "  public DerivedWidget()\n" +
            "  public override void Draw()\n" +
            "1 property:\n" +
            "  public int Size { get; set; }\n" +
            "Not shown: what ExternalLib.Widget and its base types declare; ILens could not load " + LibraryOrigin,
            text);
    }

    // ILens's own type system knows the core library's classes only as definitions without
    // members, so these come from the installed .NET libraries.
    [Theory]
    [InlineData("ExternalLib.MarkAttribute", "IsDefaultAttribute", "System.Attribute")]
    [InlineData("ExternalLib.Point", "ToString", "System.ValueType")]
    [InlineData("ExternalLib.Shade", "HasFlag", "System.Enum")]
    public async Task ListMembers_WithInherited_ListsWhatACoreLibraryBaseDeclares(
        string typeName, string memberName, string baseName)
    {
        string text = await CallToolText(_shared.Client, "list_members", new()
        {
            ["assembly"] = _libraryPath,
            ["type_name"] = typeName,
            ["include_inherited"] = true,
            ["name_contains"] = memberName,
        });

        string line = Assert.Single(text.Split('\n'), l => l.Contains($" {memberName}("));
        Assert.EndsWith($"  [from {baseName}]", line);
        Assert.DoesNotContain("Not shown", text);
    }

    // Every tool that takes a member name resolves it through the same walk. Widget declares
    // these members, and its code is in ExternalLib.dll, not in the assembly asked about.
    [Theory]
    [InlineData("decompile", null, "Paint", null, "ExternalLib.Widget.Paint(), ExternalLib.Widget.Paint(int)")]
    [InlineData("decompile", null, "Paint", "int", "ExternalLib.Widget.Paint(int)")]
    [InlineData("analyze", "read_by", "Label", null, "ExternalLib.Widget.Label")]
    [InlineData("analyze", "call_sites", "Paint", null, "ExternalLib.Widget.Paint(), ExternalLib.Widget.Paint(int)")]
    public async Task NamingAMemberABaseFromAnotherAssemblyDeclares_SaysWhereItComesFrom(
        string toolName, string? kind, string memberName, string? parameterType, string members)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _userPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["member_name"] = memberName,
        };
        if (kind is not null)
            arguments["kind"] = kind;
        if (parameterType is not null)
            arguments["parameter_types"] = new[] { parameterType };

        string error = await CallToolError(_references.Client, toolName, arguments);

        Assert.Equal(
            $"Error: ArgumentException: ExternalUser.DerivedWidget inherits {members} from a base class this " +
            $"assembly does not define; ExternalLib.Widget comes from {_libraryPath}.",
            error);
    }

    [Fact]
    public async Task NamingAMemberTheClassDeclares_FindsItThoughABaseFromAnotherAssemblyHasTheNameAsAnotherKind()
    {
        // DerivedWidget's property Size and Widget's field Size are two kinds of member, but
        // Widget's lies further from DerivedWidget and has no code in this assembly.
        string code = await CallToolText(_references.Client, "decompile", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["member_name"] = "Size",
        });

        Assert.Equal("// ExternalUser.DerivedWidget.Size", code.Split('\n')[0]);
        Assert.Contains("int Size { get; set; }", code);
    }

    [Fact]
    public async Task NamingAMemberBeyondABaseItCannotLoad_SaysWhereTheSearchStopped()
    {
        string error = await CallToolError(_shared.Client, "decompile", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["member_name"] = "Paint",
        });

        Assert.Equal(
            "Error: ArgumentException: Member 'Paint' not found on ExternalUser.DerivedWidget, the base types " +
            "ILens could load, or as an extension method; ExternalLib.Widget and its base types were not " +
            $"searched, since ILens could not load {LibraryOrigin}.",
            error);
    }

    [Fact]
    public async Task NamingAMemberNoBaseDeclares_SaysEveryBaseWasSearched()
    {
        // The walk reads Widget and reaches System.Object, so it left nothing out.
        string error = await CallToolError(_references.Client, "decompile", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["member_name"] = "Missing",
        });

        Assert.Equal(
            "Error: ArgumentException: Member 'Missing' not found on ExternalUser.DerivedWidget, its base " +
            "types, or as an extension method.",
            error);
    }

    [Fact]
    public async Task NamingAnOverloadNoBaseDeclares_ListsThoseABaseFromAnotherAssemblyDeclares()
    {
        string error = await CallToolError(_references.Client, "decompile", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["member_name"] = "Paint",
            ["parameter_types"] = new[] { "string" },
        });

        Assert.Equal(
            "Error: ArgumentException: No overload of 'Paint' on ExternalUser.DerivedWidget matches parameter " +
            "types (string). Available: ExternalLib.Widget.Paint(), ExternalLib.Widget.Paint(int).",
            error);
    }

    // An extension method on Widget applies to DerivedWidget. The shared server cannot load
    // Widget and matches the method by the name ExternalUser's reference gives Widget.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decompile_AnExtensionMethodOnABaseFromAnotherAssembly_FindsIt(bool loadsTheBase)
    {
        string code = await CallToolText(loadsTheBase ? _references.Client : _shared.Client, "decompile", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["member_name"] = "Polish",
        });

        Assert.Equal(
            "// ExternalUser.WidgetExtensions.Polish(Widget) → void [extension method on ExternalLib.Widget]",
            code.Split('\n')[0]);
    }

    // LocalTagAttribute is an attribute through ExternalLib's TagAttribute. The shared server
    // cannot load that class, and what is marked with LocalTagAttribute shows it to be one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Analyze_AppliedTo_OnAnAttributeWhoseBaseAnotherAssemblyDefines_ListsWhereItIsApplied(
        bool loadsTheBase)
    {
        string text = await CallToolText(loadsTheBase ? _references.Client : _shared.Client, "analyze", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = "ExternalUser.LocalTagAttribute",
            ["kind"] = "applied_to",
        });

        Assert.Equal("1 symbol marked with ExternalUser.LocalTagAttribute:\nExternalUser.LocalTagged", text);
    }

    [Fact]
    public async Task Analyze_AppliedTo_OnAnAttributeDerivingFromSystemAttribute_ListsWhereItIsApplied()
    {
        // The control: TagAttribute's base class is System.Attribute itself.
        string text = await CallToolText(_shared.Client, "analyze", new()
        {
            ["assembly"] = _libraryPath,
            ["type_name"] = "ExternalLib.TagAttribute",
            ["kind"] = "applied_to",
        });

        Assert.Equal("1 symbol marked with ExternalLib.TagAttribute:\nExternalLib.Tagged", text);
    }

    [Fact]
    public async Task Analyze_AppliedTo_OnAClassThatIsNoAttribute_IsRefusedWhenItsBaseIsRead()
    {
        // The walk reads Widget and reaches System.Object without meeting System.Attribute.
        string error = await CallToolError(_references.Client, "analyze", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["kind"] = "applied_to",
        });

        Assert.Equal(
            "Error: ArgumentException: Analysis kind 'applied_to' requires an attribute type; " +
            "ExternalUser.DerivedWidget does not derive from System.Attribute.",
            error);
    }

    [Fact]
    public async Task Analyze_AppliedTo_OnAClassWhoseBaseItCannotLoad_SaysItDidNotCheck()
    {
        // Nothing is marked with DerivedWidget, which holds for a class that is no attribute
        // and for an attribute applied nowhere. Only Widget, which is not read, tells them apart.
        string text = await CallToolText(_shared.Client, "analyze", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["kind"] = "applied_to",
        });

        Assert.Equal(
            "No symbols marked with ExternalUser.DerivedWidget\n" +
            "Not checked: whether ExternalUser.DerivedWidget is an attribute; its base class ExternalLib.Widget " +
            "was not read, since ILens could not load " + LibraryOrigin,
            text);
    }

    private static async Task<string> CallToolText(
        McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }

    private static async Task<string> CallToolError(
        McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError == true, $"Tool '{toolName}' did not return an error: {text}");
        return text;
    }
}
