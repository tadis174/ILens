using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the fields a compiler adds beside the member they belong to, which count
/// as generated: the subscribers of an event, which C# keeps in a private field of the event's
/// name and VB in one named after the event with Event appended, VB's field for an
/// auto-implemented property, an event's subscribers in a field without [CompilerGenerated],
/// as the C# compiler before Roslyn wrote it, which <see cref="UnmarkedEventAssembly"/> writes,
/// and the field value__ every enum keeps its value in. list_members and compare leave them out
/// unless include_compiler_generated is set, uses names the event where code reads or writes its
/// subscribers, used_by on a VB event lists the code that raises it, and used_by on an enum's
/// underlying type names the enum where its field names that type. CallShapes.Gauge,
/// CallShapes.Events, CallShapes.Defaults' Pace and Blank, BaseShift's Sides, Kinds and Retyped,
/// BaseShift.Values' Width and Season, and VbModules.Events hold the cases.
/// </summary>
public sealed class GeneratedFieldTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;
    private readonly string _vbModulesPath;
    private readonly string _baseShiftPath;
    private readonly string _baseShift2Path;

    public GeneratedFieldTests(ILensServerFixture server)
    {
        _server = server;
        var directory = Path.GetDirectoryName(_server.AssemblyPath)!;
        _callShapesPath = Path.Combine(directory, "CallShapes.dll");
        _vbModulesPath = Path.Combine(directory, "VbModules.dll");
        _baseShiftPath = Path.Combine(directory, "BaseShift.dll");
        _baseShift2Path = Path.Combine(directory, "BaseShift2.dll");
        foreach (var path in new[] { _callShapesPath, _vbModulesPath, _baseShiftPath, _baseShift2Path })
        {
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fixture not found: {path}. Tests/Fixtures/ is expected to land here via Tests.csproj's " +
                    "ProjectReference.");
        }
    }

    [Fact]
    public async Task ListMembers_LeavesOutAnEventsSubscribers_UnlessGeneratedCodeIsIncluded()
    {
        var question = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Gauge",
        };

        string all = await CallToolText("list_members", new(question) { ["accessibility"] = "all" });
        string generated = await CallToolText("list_members",
            new(question) { ["accessibility"] = "all", ["include_compiler_generated"] = true });
        string defaults = await CallToolText("list_members", question);
        string fields = await CallToolText("list_members", new(question) { ["kinds"] = new[] { "field" } });

        Assert.Equal(
            "7 members of public class CallShapes.Gauge:\n" +
            "3 methods:\n" +
            "  public Gauge()\n" +
            "  public int Doubled()\n" +
            "  public void Raise()\n" +
            "2 properties:\n" +
            "  public int Item[int slot] { get; set; }\n" +
            "  public int Level { get; set; }\n" +
            "1 field:\n" +
            "  private readonly int[] _slots\n" +
            "1 event:\n" +
            "  public event EventHandler Changed",
            all);
        Assert.Contains(
            "3 fields:\n" +
            "  private int <Level>k__BackingField\n" +
            "  private EventHandler Changed\n" +
            "  private readonly int[] _slots\n",
            generated);
        // The field the event's subscribers are kept in counts among the compiler-generated
        // members, beside the auto-property's backing field, not among the private ones the
        // defaults leave out; an empty answer names both counts.
        Assert.EndsWith("\nNot shown: 1 internal or private", defaults);
        Assert.Equal(
            "No members of public class CallShapes.Gauge\n" +
            "Not shown: 1 internal or private, 2 compiler-generated; include_compiler_generated with accessibility all lists them",
            fields);
    }

    [Fact]
    public async Task ListMembers_LeavesOutAStaticEventsSubscribers()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Events.Bulletin",
            ["accessibility"] = "all",
        });

        Assert.Equal(
            "3 members of public static class CallShapes.Events.Bulletin:\n" +
            "2 methods:\n" +
            "  public static void Announce()\n" +
            "  public static void Silence()\n" +
            "1 event:\n" +
            "  public static event EventHandler Announced",
            text);
    }

    [Fact]
    public async Task ListMembers_LeavesOutTheFieldsVisualBasicAdds_UnlessGeneratedCodeIsIncluded()
    {
        var question = new Dictionary<string, object?>
        {
            ["assembly"] = _vbModulesPath,
            ["type_name"] = "VbModules.Events.Bell",
            ["accessibility"] = "all",
        };

        string all = await CallToolText("list_members", question);
        string generated = await CallToolText("list_members", new(question) { ["include_compiler_generated"] = true });

        Assert.Equal(
            "4 members of public class VbModules.Events.Bell:\n" +
            "2 methods:\n" +
            "  public Bell()\n" +
            "  public void Ring()\n" +
            "1 property:\n" +
            "  public int Size { get; set; }\n" +
            "1 event:\n" +
            "  public event EventHandler Changed",
            all);
        Assert.Contains(
            "2 fields:\n" +
            "  private EventHandler ChangedEvent\n" +
            "  private int _Size\n",
            generated);
    }

    [Fact]
    public async Task AnEventsSubscribersWithoutTheMark_AreLeftOut_AndARaiseNamesTheEvent()
    {
        // No compiler today writes the field without [CompilerGenerated], so the assembly is
        // written here.
        var path = UnmarkedEventAssembly.Write();
        try
        {
            var question = new Dictionary<string, object?>
            {
                ["assembly"] = path,
                ["type_name"] = "Unmarked.Ticker",
                ["accessibility"] = "all",
            };

            string all = await CallToolText("list_members", question);
            string generated = await CallToolText("list_members", new(question) { ["include_compiler_generated"] = true });
            string uses = await CallToolText("analyze", new()
            {
                ["assembly"] = path,
                ["type_name"] = "Unmarked.Ticker",
                ["member_name"] = "Tick",
                ["kind"] = "uses",
            });
            string usedBy = await CallToolText("analyze", new()
            {
                ["assembly"] = path,
                ["type_name"] = "Unmarked.Ticker",
                ["member_name"] = "Ticked",
                ["kind"] = "used_by",
            });

            Assert.Equal(
                "3 members of public class Unmarked.Ticker:\n" +
                "2 methods:\n" +
                "  public Ticker()\n" +
                "  public void Tick()\n" +
                "1 event:\n" +
                "  public event EventHandler Ticked",
                all);
            Assert.Contains("1 field:\n  private EventHandler Ticked\n", generated);
            Assert.Equal(
                "3 symbols used by Unmarked.Ticker.Tick() [declared]:\n" +
                "System.EventArgs.Empty\n" +
                "System.EventHandler.Invoke(object, EventArgs)\n" +
                "Unmarked.Ticker.Ticked",
                uses);
            Assert.Equal(
                "1 user of Unmarked.Ticker.Ticked [declared] (via add_Ticked, remove_Ticked, the subscriber list):\n" +
                "Unmarked.Ticker.Tick()",
                usedBy);
        }
        finally
        {
            UnmarkedEventAssembly.TryDelete(path);
        }
    }

    [Fact]
    public async Task Compare_LeavesOutTheSubscribersOfAnEventOnlyOneBuildHas_UnlessGeneratedCodeIsIncluded()
    {
        var question = new Dictionary<string, object?>
        {
            ["assembly_a"] = _baseShiftPath,
            ["assembly_b"] = _baseShift2Path,
            ["type_name"] = "BaseShift.Sides.Panel",
        };

        string text = await CallToolText("compare", question);
        string generated = await CallToolText("compare", new(question) { ["include_compiler_generated"] = true });

        Assert.Equal(
            "6 member changes in BaseShift.Sides.Panel:\n" +
            "- public string Caption { get; set; }\n" +
            "- public event EventHandler Closed\n" +
            "~ public void Draw()\n" +
            "+ public int Item[int column] { get; }\n" +
            "+ public event EventHandler Resized\n" +
            "+ public string Title { get; set; }",
            text);
        Assert.Contains("- private EventHandler Closed\n", generated);
        Assert.Contains("+ private EventHandler Resized\n", generated);
    }

    [Theory]
    // A public field that becomes an event: the field goes and the event comes.
    [InlineData("BaseShift.Kinds.Till", "- public EventHandler Changed\n+ public event EventHandler Changed\n")]
    // An event that changes its type: the event as each build declares it.
    [InlineData("BaseShift.Retyped.Till", "+ public event Action Opened\n- public event EventHandler Opened\n")]
    public async Task Compare_ListsAnEventWithoutItsSubscribers(string typeName, string lines)
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = _baseShiftPath,
            ["assembly_b"] = _baseShift2Path,
            ["type_name"] = typeName,
        });

        // No line names the field that holds the event's subscribers.
        Assert.Contains(lines, text);
        Assert.DoesNotContain("private EventHandler", text);
        Assert.DoesNotContain("private Action", text);
    }

    // The field an enum keeps its value in is declared by no source and shown by no decompiled
    // code; its type is the underlying type the enum's heading names.
    [Fact]
    public async Task ListMembers_LeavesOutTheFieldAnEnumKeepsItsValueIn_UnlessGeneratedCodeIsIncluded()
    {
        var question = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Defaults.Pace",
        };

        string text = await CallToolText("list_members", question);
        string generated = await CallToolText("list_members", new(question) { ["include_compiler_generated"] = true });

        Assert.Equal(
            "3 members of public enum CallShapes.Defaults.Pace:\n" +
            "3 fields:\n" +
            "  public const Pace Fast = 2\n" +
            "  public const Pace Slow = 1\n" +
            "  public const Pace Stop = 0",
            text);
        Assert.Equal(
            "4 members of public enum CallShapes.Defaults.Pace:\n" +
            "4 fields:\n" +
            "  public const Pace Fast = 2\n" +
            "  public const Pace Slow = 1\n" +
            "  public const Pace Stop = 0\n" +
            "  public int value__",
            generated);
    }

    // An enum without members holds that field alone, so its list is empty and says what it left out.
    [Fact]
    public async Task ListMembers_OnAnEnumWithoutMembers_CountsItsValueFieldAsGenerated()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Defaults.Blank",
        });

        Assert.Equal(
            "No members of public enum CallShapes.Defaults.Blank\n" +
            "Not shown: 1 compiler-generated; include_compiler_generated lists it",
            text);
    }

    // Width keeps its members and changes its underlying type, which its two headings show; the
    // field's two types are listed only with generated code.
    [Fact]
    public async Task Compare_ListsTheValueFieldOfAnEnumWhoseUnderlyingTypeChanges_OnlyWithGeneratedCode()
    {
        var question = new Dictionary<string, object?>
        {
            ["assembly_a"] = _baseShiftPath,
            ["assembly_b"] = _baseShift2Path,
            ["type_name"] = "BaseShift.Values.Width",
        };

        string text = await CallToolText("compare", question);
        string generated = await CallToolText("compare", new(question) { ["include_compiler_generated"] = true });

        const string declaration =
            "Declaration: public enum BaseShift.Values.Width : byte → public enum BaseShift.Values.Width : short";
        Assert.Equal($"No member changes in BaseShift.Values.Width\n{declaration}", text);
        Assert.Equal(
            "2 member changes in BaseShift.Values.Width:\n" +
            "- public byte value__\n" +
            "+ public short value__\n" +
            declaration,
            generated);
    }

    // The field names the underlying type, which the enum's declaration writes after its name,
    // so the enum is the user of that type.
    [Fact]
    public async Task UsedBy_OnAnEnumsUnderlyingType_NamesTheEnum()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _baseShiftPath,
            ["type_name"] = "System.Byte",
            ["kind"] = "used_by",
        });

        Assert.Equal(
            "2 users of System.Byte:\n" +
            "BaseShift.Values.Prices..cctor()\n" +
            "BaseShift.Values.Width",
            text);
    }

    [Fact]
    public async Task Compare_OnAnEnumOnlyOneBuildHas_ListsItsConstantsAlone()
    {
        var question = new Dictionary<string, object?>
        {
            ["assembly_a"] = _baseShiftPath,
            ["assembly_b"] = _baseShift2Path,
            ["type_name"] = "BaseShift.Values.Season",
        };

        string text = await CallToolText("compare", question);
        string generated = await CallToolText("compare", new(question) { ["include_compiler_generated"] = true });

        Assert.Equal(
            "2 member changes in BaseShift.Values.Season, which only B has:\n" +
            "+ public const Season Autumn = 1\n" +
            "+ public const Season Spring = 0",
            text);
        Assert.Equal(
            "3 member changes in BaseShift.Values.Season, which only B has:\n" +
            "+ public const Season Autumn = 1\n" +
            "+ public const Season Spring = 0\n" +
            "+ public int value__",
            generated);
    }

    [Theory]
    [InlineData("CallShapes.Gauge", "Raise",
        "3 symbols used by CallShapes.Gauge.Raise() [declared]:\n" +
        "CallShapes.Gauge.Changed\n" +
        "System.EventArgs.Empty\n" +
        "System.EventHandler.Invoke(object, EventArgs)")]
    // The field is named with the class's type parameter, and so is the event.
    [InlineData("CallShapes.Events.Feed<>", "Post",
        "2 symbols used by CallShapes.Events.Feed<>.Post(T) [declared]:\n" +
        "CallShapes.Events.Feed<T>.Posted\n" +
        "System.Action<T>.Invoke(T)")]
    [InlineData("CallShapes.Events.Bulletin", "Announce",
        "3 symbols used by CallShapes.Events.Bulletin.Announce() [declared]:\n" +
        "CallShapes.Events.Bulletin.Announced\n" +
        "System.EventArgs.Empty\n" +
        "System.EventHandler.Invoke(object, EventArgs)")]
    // Clearing the subscribers writes the field, which is a use of the event as well.
    [InlineData("CallShapes.Events.Bulletin", "Silence",
        "1 symbol used by CallShapes.Events.Bulletin.Silence() [declared]:\n" +
        "CallShapes.Events.Bulletin.Announced")]
    public async Task Uses_NamesTheEventWhereCodeReadsOrWritesItsSubscribers(string typeName, string memberName, string expected)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = "uses",
        });

        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task OnAVisualBasicEvent_UsedByListsTheCodeThatRaisesIt_AndUsesNamesTheEvent()
    {
        string usedBy = await CallToolText("analyze", new()
        {
            ["assembly"] = _vbModulesPath,
            ["type_name"] = "VbModules.Events.Bell",
            ["member_name"] = "Changed",
            ["kind"] = "used_by",
        });
        string uses = await CallToolText("analyze", new()
        {
            ["assembly"] = _vbModulesPath,
            ["type_name"] = "VbModules.Events.Bell",
            ["member_name"] = "Ring",
            ["kind"] = "uses",
        });

        Assert.Equal(
            "2 users of VbModules.Events.Bell.Changed [declared] (via add_Changed, remove_Changed, the subscriber list):\n" +
            "VbModules.Events.Bell.Ring()\n" +
            "VbModules.Events.Listener.Attach(Bell)",
            usedBy);
        Assert.Equal(
            "3 symbols used by VbModules.Events.Bell.Ring() [declared]:\n" +
            "System.EventArgs.Empty\n" +
            "System.EventHandler.Invoke(object, EventArgs)\n" +
            "VbModules.Events.Bell.Changed",
            uses);
    }

    /// <summary>
    /// Calls an ILens MCP tool, fails the test if it returned an error, and returns the
    /// concatenated text content.
    /// </summary>
    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
