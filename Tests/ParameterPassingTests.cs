using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the keyword every answer writes on a parameter passed by reference:
/// <c>in</c>, <c>ref readonly</c>, <c>out</c> or <c>ref</c>. The compiler marks the <c>in</c> or
/// <c>ref readonly</c> parameter of a virtual, abstract or interface method, and of a delegate,
/// in the signature, and such a parameter has to print like that of any other method.
/// <c>ExternalLib.Passing</c> declares the methods and calls them, and
/// <c>ExternalUser.Passing.Shipper</c> calls them from another assembly, where the keyword is
/// known only if the server finds ExternalLib: the second server has its folder as an allow-root,
/// and the shared server does not search it.
/// </summary>
public sealed class ParameterPassingTests
    : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    private const string Lib = "ExternalLib.Passing";
    private const string Ship = "ExternalUser.Passing.Shipper.Ship(IDepot, DepotBase)";

    private readonly McpClient _shared;
    private readonly McpClient _references;
    private readonly string _library;
    private readonly string _user;
    private readonly string _testOutput;

    public ParameterPassingTests(ILensServerFixture shared, ReferencesServerFixture references)
    {
        _shared = shared.Client;
        _references = references.Client;
        _testOutput = Path.GetDirectoryName(shared.AssemblyPath)!;
        _library = Path.Combine(_testOutput, "References", "ExternalLib.dll");
        _user = Path.Combine(_testOutput, "ExternalUser.dll");
    }

    // --- A method the inspected assembly declares --------------------------------

    [Theory]
    [InlineData("IDepot",
        "5 members of public interface ExternalLib.Passing.IDepot:\n" +
        "4 methods:\n" +
        "  public int Peek(in Crate crate)\n" +
        "  public int Probe(ref readonly Crate crate)\n" +
        "  public void Swap(ref Crate crate)\n" +
        "  public bool TryTake(int slot, out Crate crate)\n" +
        "1 property:\n" +
        "  public int Item[in Crate crate] { get; }")]
    [InlineData("DepotBase",
        "9 members of public abstract class ExternalLib.Passing.DepotBase:\n" +
        "9 methods:\n" +
        "  protected DepotBase()\n" +
        "  public int Count(Crate crate)\n" +
        "  public int Count(in Crate crate)\n" +
        "  public abstract int Peek(in Crate crate)\n" +
        "  public int Plain(in Crate crate)\n" +
        "  public int PlainProbe(ref readonly Crate crate)\n" +
        "  public virtual int Probe(ref readonly Crate crate)\n" +
        "  public virtual int Weigh(Crate crate)\n" +
        "  public virtual int Weigh(in Crate crate)")]
    [InlineData("Depot",
        "6 members of public class ExternalLib.Passing.Depot : DepotBase, IDepot:\n" +
        "5 methods:\n" +
        "  public Depot()\n" +
        "  public override int Peek(in Crate crate)\n" +
        "  public override int Probe(ref readonly Crate crate)\n" +
        "  public void Swap(ref Crate crate)\n" +
        "  public bool TryTake(int slot, out Crate crate)\n" +
        "1 property:\n" +
        "  public int Item[in Crate crate] { get; }\n" +
        "Not shown: 1 internal or private")]
    [InlineData("Inspector",
        "4 members of public delegate ExternalLib.Passing.Inspector:\n" +
        "4 methods:\n" +
        "  public Inspector(object @object, nint method)\n" +
        "  public virtual IAsyncResult BeginInvoke(in Crate crate, AsyncCallback callback, object @object)\n" +
        "  public virtual int EndInvoke(in Crate crate, IAsyncResult result)\n" +
        "  public virtual int Invoke(in Crate crate)")]
    public async Task ListMembers_WritesTheKeywordOnAVirtualMethodAsOnAnyOther(string type, string expected)
    {
        string text = await Ask(_shared, "list_members", new()
        {
            ["assembly"] = _library,
            ["type_name"] = $"{Lib}.{type}",
        });

        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task FindMethods_WritesTheKeywordOnAnOverride()
    {
        string text = await Ask(_shared, "find_methods", new()
        {
            ["assembly"] = _library,
            ["type_name"] = $"{Lib}.Depot",
        });

        Assert.Equal(
            "5 methods:\n" +
            "ExternalLib.Passing.Depot..ctor() → void\n" +
            "ExternalLib.Passing.Depot.Peek(in Crate) → int\n" +
            "ExternalLib.Passing.Depot.Probe(ref readonly Crate) → int\n" +
            "ExternalLib.Passing.Depot.Swap(ref Crate) → void\n" +
            "ExternalLib.Passing.Depot.TryTake(int, out Crate) → bool\n" +
            "Not shown: 1 internal or private",
            text);
    }

    // Two virtual overloads that differ only in the keyword print apart, in the error that asks
    // to choose and in the header of the one a hint selects.
    [Fact]
    public async Task Decompile_OnOverloadsThatDifferOnlyInTheKeyword_ListsThemApart()
    {
        string text = await Ask(_shared, "decompile", new()
        {
            ["assembly"] = _library,
            ["type_name"] = $"{Lib}.DepotBase",
            ["member_name"] = "Weigh",
        }, expectError: true);

        Assert.Equal(
            "Error: ArgumentException: 'Weigh' on ExternalLib.Passing.DepotBase has 2 matching overloads; " +
            "specify parameter_count or parameter_types to disambiguate: " +
            "ExternalLib.Passing.DepotBase.Weigh(Crate), ExternalLib.Passing.DepotBase.Weigh(in Crate).",
            text);
    }

    [Fact]
    public async Task Decompile_WithTheKeywordInTheHint_SelectsTheOverloadPassedByReference()
    {
        string text = await Ask(_shared, "decompile", new()
        {
            ["assembly"] = _library,
            ["type_name"] = $"{Lib}.DepotBase",
            ["member_name"] = "Weigh",
            ["parameter_types"] = new[] { "in Crate" },
        });

        Assert.Equal("// ExternalLib.Passing.DepotBase.Weigh(in Crate) → int", text.Split('\n')[0]);
    }

    [Theory]
    [InlineData("IDepot", "Peek", "used_by",
        "1 user of ExternalLib.Passing.IDepot.Peek(in Crate) [declared]:\n" +
        "ExternalLib.Passing.Porter.Carry(IDepot, Depot)")]
    [InlineData("IDepot", "Peek", "call_sites",
        "1 call to ExternalLib.Passing.IDepot.Peek(in Crate):\n" +
        "ExternalLib.Passing.Porter.Carry(IDepot, Depot)\n  depot.Peek(in ToPeek)")]
    [InlineData("IDepot", "Probe", "implemented_by",
        "1 implementation of ExternalLib.Passing.IDepot.Probe(ref readonly Crate) [declared]:\n" +
        "ExternalLib.Passing.Depot.Probe(ref readonly Crate)")]
    public async Task Analyze_NamesAVirtualMethodWithItsKeyword(string type, string member, string kind, string expected)
    {
        Assert.Equal(expected, await Analyze(_shared, _library, $"{Lib}.{type}", member, kind));
    }

    // A field passed by in or ref readonly is only read, whether the method is virtual or not;
    // one passed by ref to a method without a body may be written.
    [Theory]
    [InlineData("ToPeek", "read_by",
        "1 reader of ExternalLib.Passing.Porter.ToPeek [declared]:\nExternalLib.Passing.Porter.Carry(IDepot, Depot)")]
    [InlineData("ToPeek", "assigned_by", "No writers of ExternalLib.Passing.Porter.ToPeek [declared]")]
    [InlineData("ToProbe", "assigned_by", "No writers of ExternalLib.Passing.Porter.ToProbe [declared]")]
    [InlineData("ToPlain", "assigned_by", "No writers of ExternalLib.Passing.Porter.ToPlain [declared]")]
    [InlineData("ToSwap", "assigned_by",
        "1 writer of ExternalLib.Passing.Porter.ToSwap [declared]:\n" +
        "ExternalLib.Passing.Porter.Trade(IDepot) " +
        "(may write: passes it by ref to ExternalLib.Passing.IDepot.Swap(ref Crate))")]
    public async Task Analyze_OnAFieldPassedByReference_CountsAReadOnlyParameterAsARead(
        string field, string kind, string expected)
    {
        Assert.Equal(expected, await Analyze(_shared, _library, $"{Lib}.Porter", field, kind));
    }

    // In BaseShift2 the body of Weigh(in int) differs and that of Weigh(int) does not. Matching
    // each with its own takes the keyword, without which the two have one signature.
    [Fact]
    public async Task Compare_OnOverloadsThatDifferOnlyInTheKeyword_MatchesEachWithItsOwn()
    {
        string text = await Ask(_shared, "compare", new()
        {
            ["assembly_a"] = Path.Combine(_testOutput, "BaseShift.dll"),
            ["assembly_b"] = Path.Combine(_testOutput, "BaseShift2.dll"),
            ["type_name"] = "BaseShift.Members.Scales",
        });

        Assert.Equal(
            "1 member change in BaseShift.Members.Scales:\n" +
            "~ public virtual int Weigh(in int load)",
            text);
    }

    // --- A method of another assembly's class --------------------------------------

    [Theory]
    [InlineData("IDepot", "Peek", "Peek(in Crate)")]
    [InlineData("IDepot", "Probe", "Probe(ref readonly Crate)")]
    [InlineData("IDepot", "TryTake", "TryTake(int, out Crate)")]
    [InlineData("IDepot", "Swap", "Swap(ref Crate)")]
    [InlineData("DepotBase", "Plain", "Plain(in Crate)")]
    public async Task UsedBy_OnAMethodOfAnotherAssembly_NamesItWithTheKeywordItsDeclarationHas(
        string type, string member, string signature)
    {
        string text = await Analyze(_references, _user, $"{Lib}.{type}", member, "used_by");

        Assert.EndsWith($" of {Lib}.{type}.{signature}:", text.Split('\n')[0]);
        Assert.Contains(Ship, text.Split('\n'));
        Assert.DoesNotContain("Not checked", text);
    }

    // The call's signature says by reference and no more, so the header writes ref, and the
    // answer says which library would have said the keyword.
    [Theory]
    [InlineData("IDepot", "Peek", "Peek(ref Crate)")]
    [InlineData("IDepot", "Probe", "Probe(ref Crate)")]
    [InlineData("IDepot", "TryTake", "TryTake(int, ref Crate)")]
    [InlineData("IDepot", "Swap", "Swap(ref Crate)")]
    [InlineData("DepotBase", "Plain", "Plain(ref Crate)")]
    public async Task UsedBy_OnAMethodOfAnotherAssembly_WritesRefAndSaysSo_WhenItsLibraryIsNotFound(
        string type, string member, string signature)
    {
        string text = await Analyze(_shared, _user, $"{Lib}.{type}", member, "used_by");

        var lines = text.Split('\n');
        Assert.EndsWith($" of {Lib}.{type}.{signature}:", lines[0]);
        Assert.Contains(Ship, lines);
        Assert.Equal(
            $"Not checked: whether ref on a method of {Lib}.{type} is out, in or ref readonly; " +
            "ILens could not load ExternalLib, Version=1.0.0.0",
            lines[^1]);
    }

    [Fact]
    public async Task UsedBy_OnAMethodOfAnotherAssembly_IsAnsweredWhole()
    {
        Assert.Equal(
            "2 users of ExternalLib.Passing.IDepot.Peek(in Crate):\n" +
            "ExternalUser.Passing.Shipper.Ship(IDepot, DepotBase)\n" +
            "ExternalUser.Passing.Shipper.Show(IDepot)",
            await Analyze(_references, _user, $"{Lib}.IDepot", "Peek", "used_by"));
        Assert.Equal(
            "2 users of ExternalLib.Passing.IDepot.Peek(ref Crate):\n" +
            "ExternalUser.Passing.Shipper.Ship(IDepot, DepotBase)\n" +
            "ExternalUser.Passing.Shipper.Show(IDepot)\n" +
            "Not checked: whether ref on a method of ExternalLib.Passing.IDepot is out, in or ref readonly; " +
            "ILens could not load ExternalLib, Version=1.0.0.0",
            await Analyze(_shared, _user, $"{Lib}.IDepot", "Peek", "used_by"));
    }

    // The overloads the error lists differ only in how the parameter is passed, for a virtual
    // pair and for one that is not.
    [Theory]
    [InlineData("Weigh", true, "in Crate")]
    [InlineData("Count", true, "in Crate")]
    [InlineData("Weigh", false, "ref Crate")]
    [InlineData("Count", false, "ref Crate")]
    public async Task UsedBy_OnOverloadsOfAnotherAssembly_ListsThemApart(string member, bool libraryFound, string second)
    {
        string text = await Analyze(libraryFound ? _references : _shared, _user, $"{Lib}.DepotBase", member, "used_by",
            expectError: true);

        Assert.Equal(
            $"Error: ArgumentException: '{member}' on ExternalLib.Passing.DepotBase has 2 matching overloads; " +
            "specify parameter_count or parameter_types to disambiguate: " +
            $"ExternalLib.Passing.DepotBase.{member}(Crate), ExternalLib.Passing.DepotBase.{member}({second}).",
            text);
    }

    [Fact]
    public async Task UsedBy_WithTheKeywordInTheHint_SelectsTheOverloadOfAnotherAssemblyPassedByReference()
    {
        string text = await Ask(_references, "analyze", new()
        {
            ["assembly"] = _user,
            ["type_name"] = $"{Lib}.DepotBase",
            ["member_name"] = "Weigh",
            ["kind"] = "used_by",
            ["parameter_types"] = new[] { "in Crate" },
        });

        Assert.Equal($"1 user of ExternalLib.Passing.DepotBase.Weigh(in Crate):\n{Ship}", text);
    }

    // The members a class inherits from another assembly's class are read from that assembly,
    // by a type system that does not tell ref readonly from ref.
    [Fact]
    public async Task ListMembers_WithInheritedMembersOfAnotherAssembly_WritesTheKeywordTheirDeclarationHas()
    {
        string text = await Ask(_references, "list_members", new()
        {
            ["assembly"] = _user,
            ["type_name"] = "ExternalUser.Passing.OwnDepot",
            ["include_inherited"] = true,
        });

        Assert.Equal(
            "12 members of public class ExternalUser.Passing.OwnDepot : Depot:\n" +
            "11 methods:\n" +
            "  public OwnDepot()\n" +
            "  public int Count(Crate crate)  [from ExternalLib.Passing.DepotBase]\n" +
            "  public int Count(in Crate crate)  [from ExternalLib.Passing.DepotBase]\n" +
            "  public override int Peek(in Crate crate)  [from ExternalLib.Passing.Depot]\n" +
            "  public int Plain(in Crate crate)  [from ExternalLib.Passing.DepotBase]\n" +
            "  public int PlainProbe(ref readonly Crate crate)  [from ExternalLib.Passing.DepotBase]\n" +
            "  public override int Probe(ref readonly Crate crate)  [from ExternalLib.Passing.Depot]\n" +
            "  public void Swap(ref Crate crate)  [from ExternalLib.Passing.Depot]\n" +
            "  public bool TryTake(int slot, out Crate crate)  [from ExternalLib.Passing.Depot]\n" +
            "  public virtual int Weigh(Crate crate)  [from ExternalLib.Passing.DepotBase]\n" +
            "  public virtual int Weigh(in Crate crate)  [from ExternalLib.Passing.DepotBase]\n" +
            "1 property:\n" +
            "  public int Item[in Crate crate] { get; }  [from ExternalLib.Passing.Depot]\n" +
            "Not shown: 1 internal or private",
            text);
    }

    // Without its library, a method is known from the call alone. A parameter the signature
    // marks as read-only is still one passed by reference: it is written ref like the others,
    // and the two overloads of Weigh stay two lines.
    [Fact]
    public async Task Uses_WhenTheLibraryIsNotFound_WritesRefForAParameterTheSignatureMarksReadOnly()
    {
        Assert.Equal(
            $"11 symbols used by {Ship} [declared]:\n" +
            "ExternalLib.Passing.Crate\n" +
            "ExternalLib.Passing.Crate.Weight\n" +
            "ExternalLib.Passing.DepotBase.Count(Crate)\n" +
            "ExternalLib.Passing.DepotBase.Count(ref Crate)\n" +
            "ExternalLib.Passing.DepotBase.Plain(ref Crate)\n" +
            "ExternalLib.Passing.DepotBase.Weigh(Crate)\n" +
            "ExternalLib.Passing.DepotBase.Weigh(ref Crate)\n" +
            "ExternalLib.Passing.IDepot.Peek(ref Crate)\n" +
            "ExternalLib.Passing.IDepot.Probe(ref Crate)\n" +
            "ExternalLib.Passing.IDepot.Swap(ref Crate)\n" +
            "ExternalLib.Passing.IDepot.TryTake(int, ref Crate)\n" +
            "Not checked: whether ref on a method of ExternalLib.Passing.DepotBase, ExternalLib.Passing.IDepot " +
            "is out, in or ref readonly; ILens could not load ExternalLib, Version=1.0.0.0",
            await Analyze(_shared, _user, "ExternalUser.Passing.Shipper", "Ship", "uses"));
    }

    // The signature of the call says that the interface method only reads the field, whether
    // or not its library is found.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Analyze_OnAFieldPassedToAReadOnlyParameterOfAnotherAssembly_CountsARead(bool libraryFound)
    {
        var client = libraryFound ? _references : _shared;

        Assert.Equal(
            "1 reader of ExternalUser.Passing.Shipper.Held [declared]:\nExternalUser.Passing.Shipper.Show(IDepot)",
            await Analyze(client, _user, "ExternalUser.Passing.Shipper", "Held", "read_by"));
        Assert.Equal(
            "No writers of ExternalUser.Passing.Shipper.Held [declared]",
            await Analyze(client, _user, "ExternalUser.Passing.Shipper", "Held", "assigned_by"));
    }

    private static Task<string> Analyze(McpClient client, string assembly, string type, string member, string kind,
        bool expectError = false) =>
        Ask(client, "analyze", new()
        {
            ["assembly"] = assembly,
            ["type_name"] = type,
            ["member_name"] = member,
            ["kind"] = kind,
        }, expectError);

    private static async Task<string> Ask(McpClient client, string toolName, Dictionary<string, object?> arguments,
        bool expectError = false)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True((result.IsError == true) == expectError,
            $"Tool '{toolName}' {(expectError ? "was expected to return an error" : "returned an error")}: {text}");
        return text;
    }
}
