using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for what an answer says about a class of another assembly that was not
/// read although the reference search found a file for the class's library. ExternalUser is
/// copied into a folder of its own, beside a library the test puts there: an ExternalLib of
/// another version that holds none of the classes, a file named ExternalLib.dll that holds
/// another assembly, or an ExternalLib that forwards a class on. What the same answers say
/// when no file is found is pinned with each answer's own tests.
/// </summary>
public sealed class UnreadLibraryTests : IClassFixture<UnreadLibraryTests.OtherVersionFixture>
{
    private const string Widget = "ExternalLib.Widget";
    private const string Trait = "ExternalLib.TraitAttribute";
    private const string NotShownWidget = "Not shown: what ExternalLib.Widget and its base types declare; ";
    private const string KeywordOf = "Not checked: whether ref on a method of ";
    private const string Keyword = " is out, in or ref readonly; ";

    private readonly OtherVersionFixture _otherVersion;

    public UnreadLibraryTests(OtherVersionFixture otherVersion) => _otherVersion = otherVersion;

    /// <summary>
    /// One server on a folder that holds ExternalUser beside an ExternalLib in version 2.0.0.0
    /// without any class, where ExternalUser was compiled against 1.0.0.0.
    /// </summary>
    public sealed class OtherVersionFixture : IAsyncLifetime
    {
        private string _root = null!;

        public McpClient Client { get; private set; } = null!;
        public string User { get; private set; } = null!;
        public string Library { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _root = NewFolder();
            User = CopyUser(_root);
            Library = ForwardingAssembly.Write(_root, "ExternalLib", new Version(2, 0, 0, 0));
            Client = await StartServer("ILens-unread-other-version", _root);
        }

        public async Task DisposeAsync()
        {
            if (Client is not null)
                await Client.DisposeAsync();
            await ILensServerFixture.DeleteWhenReleased(_root);
        }
    }

    /// <summary>The reason an answer gives for classes the other version does not hold.</summary>
    private string Gone(string classes) =>
        $"ExternalLib as loaded from {_otherVersion.Library}, Version=2.0.0.0, does not hold {classes}; " +
        "referenced is Version=1.0.0.0";

    [Fact]
    public async Task ListMembers_WithInherited_NamesTheLibraryFoundWithoutTheBase()
    {
        Assert.Equal(
            "3 members of public class ExternalUser.DerivedWidget : Widget:\n" +
            "2 methods:\n" +
            "  public DerivedWidget()\n" +
            "  public override void Draw()\n" +
            "1 property:\n" +
            "  public int Size { get; set; }\n" +
            NotShownWidget + Gone(Widget),
            await Ask(_otherVersion.Client, "list_members", new()
            {
                ["assembly"] = _otherVersion.User,
                ["type_name"] = "ExternalUser.DerivedWidget",
                ["include_inherited"] = true,
            }));
    }

    [Fact]
    public async Task NamingAMemberBeyondSuchABase_SaysWhereTheSearchStopped()
    {
        Assert.Equal(
            "Error: ArgumentException: Member 'Paint' not found on ExternalUser.DerivedWidget, the base types " +
            "ILens could load, or as an extension method; ExternalLib.Widget and its base types were not " +
            $"searched, since {Gone(Widget)}.",
            await Ask(_otherVersion.Client, "decompile", new()
            {
                ["assembly"] = _otherVersion.User,
                ["type_name"] = "ExternalUser.DerivedWidget",
                ["member_name"] = "Paint",
            }));
    }

    [Fact]
    public async Task AppliedTo_OnAClassWithSuchABase_SaysItDidNotCheck()
    {
        Assert.Equal(
            "No symbols marked with ExternalUser.DerivedWidget\n" +
            "Not checked: whether ExternalUser.DerivedWidget is an attribute; its base class ExternalLib.Widget " +
            "was not read, since " + Gone(Widget),
            await Analyze(_otherVersion.Client, _otherVersion.User, "ExternalUser.DerivedWidget", null, "applied_to"));
    }

    [Fact]
    public async Task Implements_OnAMemberOfSuchAnInterface_SaysWhatWasNotRead()
    {
        Assert.Equal(
            "No interface members implemented by ExternalUser.Dial.Read() [declared]\n" +
            "Not checked: what ExternalLib.IGauge and its base types declare; " + Gone("ExternalLib.IGauge"),
            await Analyze(_otherVersion.Client, _otherVersion.User, "ExternalUser.Dial", "Read", "implements"));
    }

    // The same interface leaves open whether the member implements, under a question about who
    // uses it.
    [Fact]
    public async Task UsedBy_OnAMemberOfAClassWithSuchAnInterface_SaysWhatWasNotChecked()
    {
        Assert.Equal(
            "No users of ExternalUser.Dial.Read() [declared]\n" +
            "Not checked: whether it implements an interface member; " + Gone("ExternalLib.IGauge"),
            await Analyze(_otherVersion.Client, _otherVersion.User, "ExternalUser.Dial", "Read", "used_by"));
    }

    // One line for the classes of one library, which the reason names again: the line may
    // hold classes of several libraries, each with a reason of its own.
    [Fact]
    public async Task Uses_NamesTheLibraryFoundWithoutTheClasses()
    {
        string text = await Analyze(
            _otherVersion.Client, _otherVersion.User, "ExternalUser.Passing.Shipper", "Ship", "uses");

        Assert.Equal(
            KeywordOf + "ExternalLib.Passing.DepotBase, ExternalLib.Passing.IDepot" + Keyword +
            Gone("ExternalLib.Passing.DepotBase or ExternalLib.Passing.IDepot"),
            text.Split('\n')[^1]);
    }

    [Fact]
    public async Task UsedBy_OnAMethodOfSuchAClass_NamesTheLibraryFoundWithoutIt()
    {
        Assert.Equal(
            "2 users of ExternalLib.Passing.IDepot.Peek(ref Crate):\n" +
            "ExternalUser.Passing.Shipper.Ship(IDepot, DepotBase)\n" +
            "ExternalUser.Passing.Shipper.Show(IDepot)\n" +
            KeywordOf + "ExternalLib.Passing.IDepot" + Keyword + Gone("ExternalLib.Passing.IDepot"),
            await Analyze(_otherVersion.Client, _otherVersion.User, "ExternalLib.Passing.IDepot", "Peek", "used_by"));
    }

    // Whether an attribute class inherits the name is recorded in the library alone, so the
    // classes that were not read are named with the library that does not hold them.
    [Fact]
    public async Task AssignedBy_OnAMemberAttributesSetByName_NamesTheLibraryFoundWithoutTheClasses()
    {
        string text = await Analyze(_otherVersion.Client, _otherVersion.User, Trait, "Kind", "assigned_by");

        Assert.Equal(
            $"Not checked: whether ExternalLib.OwnTraitAttribute, ExternalLib.SubTraitAttribute inherit Kind from {Trait}; " +
            Gone("ExternalLib.OwnTraitAttribute or ExternalLib.SubTraitAttribute"),
            text.Split('\n')[^1]);
    }

    // The reference search finds a file by its name, and the type system knows a library by
    // the assembly name inside the file.
    [Fact]
    public async Task AFileOfTheLibrarysNameThatHoldsAnotherAssembly_IsNamedWithWhatItHolds()
    {
        await OnAFolder(
            root => File.Copy(Path.Combine(AppContext.BaseDirectory, "CallShapes.dll"), Path.Combine(root, "ExternalLib.dll")),
            async (client, root, user) =>
            {
                string reason =
                    $"ILens could not load ExternalLib, Version=1.0.0.0: the file found, {Path.Combine(root, "ExternalLib.dll")}, " +
                    "holds the assembly CallShapes";
                Assert.Equal(NotShownWidget + reason, await NotShownOnDerivedWidget(client, user));
                Assert.Equal(
                    KeywordOf + "ExternalLib.Vault" + Keyword + reason,
                    (await ByReferenceUseTests.Uses(client, user)).Split('\n')[^1]);
            });
    }

    // The library forwards Widget and holds neither that class nor IGauge. The answer about
    // Widget names the library the forward leads to, the answer about IGauge the library
    // itself, whose version is the one referenced.
    [Fact]
    public async Task AForwardedClass_IsFollowedToALibraryThatIsNotLoaded()
    {
        await OnAFolder(
            root => ForwardingAssembly.Write(root, "ExternalLib", new Version(1, 0, 0, 0),
                "ExternalCore", new Version(3, 0, 0, 0), ("ExternalLib", "Widget")),
            async (client, root, user) =>
            {
                Assert.Equal(
                    NotShownWidget + "ILens could not load ExternalCore, Version=3.0.0.0",
                    await NotShownOnDerivedWidget(client, user));
                Assert.Equal(
                    "No interface members implemented by ExternalUser.Dial.Read() [declared]\n" +
                    "Not checked: what ExternalLib.IGauge and its base types declare; ExternalLib as loaded from " +
                    $"{Path.Combine(root, "ExternalLib.dll")}, Version=1.0.0.0, does not hold ExternalLib.IGauge",
                    await Analyze(client, user, "ExternalUser.Dial", "Read", "implements"));
            });
    }

    [Fact]
    public async Task AForwardedClass_IsFollowedToALibraryThatDoesNotHoldIt()
    {
        await OnAFolder(
            root =>
            {
                ForwardingAssembly.Write(root, "ExternalLib", new Version(1, 0, 0, 0),
                    "ExternalCore", new Version(3, 0, 0, 0), ("ExternalLib", "Widget"));
                ForwardingAssembly.Write(root, "ExternalCore", new Version(3, 1, 0, 0));
            },
            async (client, root, user) => Assert.Equal(
                NotShownWidget + $"ExternalCore as loaded from {Path.Combine(root, "ExternalCore.dll")}, Version=3.1.0.0, " +
                "does not hold ExternalLib.Widget; referenced is Version=3.0.0.0",
                await NotShownOnDerivedWidget(client, user)));
    }

    [Fact]
    public async Task AClassForwardedTwice_IsFollowedToTheEnd()
    {
        await OnAFolder(
            root =>
            {
                ForwardingAssembly.Write(root, "ExternalLib", new Version(1, 0, 0, 0),
                    "ExternalCore", new Version(3, 0, 0, 0), ("ExternalLib", "Widget"));
                ForwardingAssembly.Write(root, "ExternalCore", new Version(3, 0, 0, 0),
                    "ExternalDeep", new Version(4, 0, 0, 0), ("ExternalLib", "Widget"));
            },
            async (client, _, user) => Assert.Equal(
                NotShownWidget + "ILens could not load ExternalDeep, Version=4.0.0.0",
                await NotShownOnDerivedWidget(client, user)));
    }

    // An assembly can name one class through two libraries. The one the class is found
    // through is no reason it was not read, the other is.
    [Fact]
    public async Task AClassNamedThroughTwoLibraries_IsMissedInTheOneThatDoesNotHoldIt()
    {
        string root = NewFolder();
        string assembly = TwiceReferencedAssembly.Write(root);
        try
        {
            await using var client = await StartServer("ILens-unread-twice-referenced", root);
            string text = await Ask(client, "list_members", new()
            {
                ["assembly"] = assembly,
                ["type_name"] = "TwiceReferenced.Derived",
                ["include_inherited"] = true,
            });

            Assert.Equal(
                "Not shown: what Shared.Shelf and its base types declare; FirstLib as loaded from " +
                $"{Path.Combine(root, "FirstLib.dll")}, Version=1.0.0.0, does not hold Shared.Shelf",
                text.Split('\n')[^1]);
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    /// <summary>
    /// Runs <paramref name="act"/> against a server whose one allow-root is a new folder that
    /// holds ExternalUser and what <paramref name="arrange"/> puts beside it. <paramref name="act"/>
    /// takes the client, the folder and the path of ExternalUser in it.
    /// </summary>
    private static async Task OnAFolder(Action<string> arrange, Func<McpClient, string, string, Task> act)
    {
        string root = NewFolder();
        string user = CopyUser(root);
        arrange(root);
        try
        {
            await using var client = await StartServer("ILens-unread-library", root);
            await act(client, root, user);
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    /// <summary>The last line of DerivedWidget's members with those it inherits.</summary>
    private static async Task<string> NotShownOnDerivedWidget(McpClient client, string user) =>
        (await Ask(client, "list_members", new()
        {
            ["assembly"] = user,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["include_inherited"] = true,
        })).Split('\n')[^1];

    private static string NewFolder() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ILens-unread-" + Guid.NewGuid().ToString("N"))).FullName;

    private static string CopyUser(string root)
    {
        string user = Path.Combine(root, "ExternalUser.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "ExternalUser.dll"), user);
        return user;
    }

    private static async Task<McpClient> StartServer(string name, string root) =>
        await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = ILensServerFixture.ResolveILensExecutable(),
            Arguments = ["--allow-root", root],
        }));

    private static Task<string> Analyze(McpClient client, string assembly, string typeName, string? memberName, string kind)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = assembly,
            ["type_name"] = typeName,
            ["kind"] = kind,
        };
        if (memberName is not null)
            arguments["member_name"] = memberName;
        return Ask(client, "analyze", arguments);
    }

    /// <summary>The text of a tool's answer, an error's included.</summary>
    private static async Task<string> Ask(McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }
}
