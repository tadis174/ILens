using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for a type an assembly forwards to another assembly, as a library does that
/// a type moved out of: every answer about the type's name says where the assembly forwards it,
/// and the forwarded types take part in the lookup of a name beside the types the assembly
/// defines and those it references.
/// <para>
/// ForwardingAssembly writes Forwarder, which forwards classes to Core, references other classes
/// there, Gauge among them, which it also forwards, and defines Box and Dial with one type
/// parameter beside forwarded classes of those names; and Referrer, which only references
/// Kettle in Core, for compare. Core itself is not written.
/// </para>
/// <para>
/// ForwardedExtensionAssembly writes Polishes, which forwards ExternalLib's Kettle to ExternalLib
/// and declares extension methods code can call on Kettle without naming it. ExternalLib lies in
/// a folder below the root, where the reference search for the assemblies at the root does not
/// look, so Polishes reads nothing of Kettle, while compare can read ExternalLib itself.
/// </para>
/// </summary>
public sealed class ForwardedTypeTests : IClassFixture<ForwardedTypeTests.ForwarderFixture>
{
    private const string ToCore = "Core, Version=3.0.0.0";
    private const string Kettle = "ExternalLib.Extended.Kettle";
    private const string FromLib = "ExternalLib, Version=1.0.0.0";

    private readonly ForwarderFixture _fixture;

    public ForwardedTypeTests(ForwarderFixture fixture) => _fixture = fixture;

    /// <summary>
    /// One server on a folder that holds Forwarder, Referrer and Polishes, and ExternalLib in a
    /// folder of its own below it.
    /// </summary>
    public sealed class ForwarderFixture : IAsyncLifetime
    {
        private string _root = null!;

        public McpClient Client { get; private set; } = null!;
        public string Forwarder { get; private set; } = null!;
        public string Referrer { get; private set; } = null!;
        public string Polishes { get; private set; } = null!;
        public string Library { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "ILens-forwarded-" + Guid.NewGuid().ToString("N"))).FullName;
            var version = new Version(1, 0, 0, 0);
            var core = new Version(3, 0, 0, 0);
            Forwarder = ForwardingAssembly.Write(_root, "Forwarder", version, "Core", core,
                forwarded:
                [
                    ("Shop", "Kettle"), ("Shop", "Kettle+Spout"), ("Shop", "Pair`1"), ("Shop", "Pair`2"),
                    ("Shop", "Tuple`1"), ("Shop", "Tuple`2"), ("Shop", "Box"), ("Shop", "Dial`2"),
                    ("Shop", "Crate`1"), ("Shop", "Gauge"),
                ],
                referenced: [("Shop", "Pair`3"), ("Shop", "Crate"), ("Shop", "Gauge")],
                defined: [("Shop", "Box`1"), ("Shop", "Dial`1")]);
            Referrer = ForwardingAssembly.Write(_root, "Referrer", version, "Core", core,
                forwarded: [], referenced: [("Shop", "Kettle")], defined: []);
            Polishes = ForwardedExtensionAssembly.Write(_root);
            Library = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "library")).FullName, "ExternalLib.dll");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "References", "ExternalLib.dll"), Library);

            Client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-forwarded-types",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", _root],
            }));
        }

        public async Task DisposeAsync()
        {
            if (Client is not null)
                await Client.DisposeAsync();
            await ILensServerFixture.DeleteWhenReleased(_root);
        }
    }

    [Theory]
    [InlineData("decompile")]
    [InlineData("list_members")]
    [InlineData("find_methods")]
    [InlineData("search_code")]
    public async Task EachLookupTool_OnAForwardedType_SaysWhereTheAssemblyForwardsIt(string tool)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _fixture.Forwarder,
            ["type_name"] = "Shop.Kettle",
        };
        if (tool == "search_code")
            arguments["regex"] = "Pour";

        Assert.Equal(
            $"Error: ArgumentException: Shop.Kettle is not defined in this assembly, which forwards it to {ToCore}.",
            await Ask(tool, arguments));
    }

    // Spout is nested in Kettle and forwarded with a row of its own. Gauge is also referenced, and
    // the forward comes before what the reference row says.
    [Theory]
    [InlineData("Shop.Kettle.Spout", "Shop.Kettle.Spout")]
    [InlineData("Shop.Kettle+Spout", "Shop.Kettle.Spout")]
    [InlineData("Shop.Pair<,>", "Shop.Pair<,>")]
    [InlineData("Shop.Pair`1", "Shop.Pair<>")]
    [InlineData("Shop.Gauge", "Shop.Gauge")]
    public async Task ListMembers_OnAForwardedType_SaysWhereTheAssemblyForwardsIt(string typeName, string display)
    {
        Assert.Equal(
            $"Error: ArgumentException: {display} is not defined in this assembly, which forwards it to {ToCore}.",
            await ListMembers(typeName));
    }

    [Fact]
    public async Task Analyze_AForwardedType_EndsItsEmptyAnswerWithWhereTheAssemblyForwardsIt()
    {
        Assert.Equal(
            $"No users of Shop.Kettle\nThis assembly forwards it to {ToCore}.",
            await Ask("analyze", new()
            {
                ["assembly"] = _fixture.Forwarder,
                ["type_name"] = "Shop.Kettle",
                ["kind"] = "used_by",
            }));
    }

    // Core is not there to read, so whether Boil is a constant stays open.
    [Fact]
    public async Task Analyze_AMemberOfAForwardedType_EndsItsEmptyAnswerWithWhereTheAssemblyForwardsTheType()
    {
        Assert.Equal(
            "No users of Shop.Kettle.Boil\n" +
            $"This assembly forwards Shop.Kettle to {ToCore}.\n" +
            "ILens cannot tell whether Boil is a constant, whose value the compiler copies into each use: " +
            "no assembly it found defines Shop.Kettle.",
            await Ask("analyze", new()
            {
                ["assembly"] = _fixture.Forwarder,
                ["type_name"] = "Shop.Kettle",
                ["member_name"] = "Boil",
                ["kind"] = "used_by",
            }));
    }

    [Fact]
    public async Task Analyze_ANameTheAssemblyNeitherForwardsNorKnows_SaysSoAsBefore()
    {
        Assert.Equal(
            "No users of Shop.Nothing\nThis assembly neither defines nor references a type of that name.",
            await Ask("analyze", new()
            {
                ["assembly"] = _fixture.Forwarder,
                ["type_name"] = "Shop.Nothing",
                ["kind"] = "used_by",
            }));
    }

    // Without a forward, what the references say holds for both builds, as before.
    [Theory]
    [InlineData(true, true, $"A and B forward it to {ToCore}")]
    [InlineData(true, false, $"in B it comes from {ToCore}; A forwards it to {ToCore}")]
    [InlineData(false, true, $"in A it comes from {ToCore}; B forwards it to {ToCore}")]
    [InlineData(false, false, $"it comes from {ToCore}")]
    public async Task Compare_ATypeNeitherBuildDefines_SaysWhereEachForwardingBuildForwardsIt(
        bool aForwards, bool bForwards, string clauses)
    {
        Assert.Equal(
            $"Error: ArgumentException: Type 'Shop.Kettle' is not defined in either assembly; {clauses}.",
            await Ask("compare", new()
            {
                ["assembly_a"] = aForwards ? _fixture.Forwarder : _fixture.Referrer,
                ["assembly_b"] = bForwards ? _fixture.Forwarder : _fixture.Referrer,
                ["type_name"] = "Shop.Kettle",
            }));
    }

    [Fact]
    public async Task Compare_AMemberOfATypeNeitherBuildDefines_SaysWhereEachForwardingBuildForwardsIt()
    {
        Assert.Equal(
            $"Error: ArgumentException: Type 'Shop.Kettle' is not defined in either assembly; A and B forward it to {ToCore}.",
            await Ask("compare", new()
            {
                ["assembly_a"] = _fixture.Forwarder,
                ["assembly_b"] = _fixture.Forwarder,
                ["type_name"] = "Shop.Kettle",
                ["member_name"] = "Boil",
            }));
    }

    // Tuple is forwarded with one and two type parameters alone; Pair also referenced with three,
    // and Dial defined with one. Box is defined with one type parameter and forwarded without, so
    // the name means the forwarded one; Crate is forwarded with one and referenced without, so
    // the name means the referenced one.
    [Theory]
    [InlineData("Shop.Tuple",
        "Type name 'Shop.Tuple' matches 2 types this assembly forwards: Shop.Tuple<>, Shop.Tuple<,>. Name one as listed.")]
    [InlineData("Shop.Pair",
        $"Type name 'Shop.Pair' matches 3 types: this assembly references Shop.Pair<,,> from {ToCore}; " +
        $"it forwards Shop.Pair<>, Shop.Pair<,> to {ToCore}. Name one as listed.")]
    [InlineData("Shop.Dial",
        $"Type name 'Shop.Dial' matches 2 types: this assembly defines Shop.Dial<>; it forwards Shop.Dial<,> to {ToCore}. " +
        "Name one as listed.")]
    [InlineData("Shop.Box",
        $"Shop.Box is not defined in this assembly, which forwards it to {ToCore}. This assembly defines Shop.Box<>.")]
    [InlineData("Shop.Crate", $"Shop.Crate is not defined in this assembly; it comes from {ToCore}.")]
    [InlineData("Shop.Pair<,,,>",
        $"Type not found: Shop.Pair<,,,>. Types of that name: this assembly references Shop.Pair<,,> from {ToCore}; " +
        $"it forwards Shop.Pair<>, Shop.Pair<,> to {ToCore}.")]
    [InlineData("Shop.Tuple<,,>", "Type not found: Shop.Tuple<,,>. Types of that name: Shop.Tuple<>, Shop.Tuple<,>.")]
    [InlineData("Shop.Nothing", "Type not found: Shop.Nothing.")]
    public async Task ListMembers_ANameForwardedTypesShare_ReadsThemBesideTheDefinedAndReferencedTypes(
        string typeName, string error)
    {
        Assert.Equal($"Error: ArgumentException: {error}", await ListMembers(typeName));
    }

    [Fact]
    public async Task ListMembers_ADefinedTypeBesideForwardedTypesOfItsName_IsStillFound()
    {
        Assert.Equal("No members of public class Shop.Dial<T1>", await ListMembers("Shop.Dial<>"));
    }

    // Polish's receiver is a type parameter without constraints, which Kettle fits whatever it
    // declares, so the lookup through Kettle finds it although Kettle was not read; the line under
    // the header names the library the forward names.
    [Fact]
    public async Task Decompile_AnExtensionMethodFoundThroughAForwardedType_NamesTheLibraryTheForwardNames()
    {
        string text = await Ask("decompile", new()
        {
            ["assembly"] = _fixture.Polishes,
            ["type_name"] = Kettle,
            ["member_name"] = "Polish",
        });

        Assert.StartsWith(
            "// Polishes.Polishing.Polish<T>(T) → void [extension method on T]\n" +
            $"// Not checked: whether {Kettle} or its base types have a member Polish, which C# would take first; " +
            $"ILens could not load {FromLib}\n",
            text.Replace("\r\n", "\n"));
    }

    // An array of a forwarded type is read as one of a referenced type. Kettle was not read, so the
    // decompiler's rule turns Count down, which is for an array of any type, and the answer says
    // what was not checked, naming the library the forward names.
    [Fact]
    public async Task Analyze_TheExtensionMethodsForAnArrayOfAForwardedType_AreAnsweredAsForAReferencedType()
    {
        Assert.Equal(
            $"No extension methods for {Kettle}[]\n" +
            $"Not checked: whether more extension methods apply to {Kettle}[] through what {Kettle} derives from; " +
            $"ILens could not load {FromLib}",
            await Ask("analyze", new()
            {
                ["assembly"] = _fixture.Polishes,
                ["type_name"] = Kettle + "[]",
                ["kind"] = "extension_methods",
            }));
    }

    // ExternalLib's Kettle declares Pour(int); Polishes forwards Kettle and declares an extension
    // method Pour, which C# calls on a Kettle there.
    [Fact]
    public async Task Compare_AMethodTheForwardingBuildDeclaresAsAnExtensionMethod_ComparesTwoMethods()
    {
        string text = await Ask("compare", new()
        {
            ["assembly_a"] = _fixture.Library,
            ["assembly_b"] = _fixture.Polishes,
            ["type_name"] = Kettle,
            ["member_name"] = "Pour",
        });

        Assert.StartsWith(
            $"// Not checked: whether {Kettle} or its base types have a member Pour, which C# would take first; " +
            $"ILens could not load {FromLib}\n" +
            $"// {Kettle}.Pour(int) → void in A and Polishes.Polishing.Pour<T>(T, string) → void " +
            "[extension method on T] in B are different methods",
            text.Replace("\r\n", "\n"));
    }

    private Task<string> ListMembers(string typeName) =>
        Ask("list_members", new() { ["assembly"] = _fixture.Forwarder, ["type_name"] = typeName });

    private async Task<string> Ask(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _fixture.Client.CallToolAsync(toolName, arguments);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }
}
