using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for how a value the metadata stores is written, as C# writes it: a parameter's
/// default in list_members' declarations, in compare's line for a changed declaration and in the
/// argument call_sites shows for a call that leaves it out, and a constant's value. CallShapes'
/// Defaults.cs holds a default and a constant of each kind, BaseShift's Defaults.cs changes
/// defaults in BaseShift2 and numbers an enum anew under a default and a constant that name the
/// same member, and ExternalUser's Defaults.cs gives a default and a constant of an enum of
/// ExternalLib, which the shared server cannot load and the second server can.
/// </summary>
public sealed class DefaultValueTests
    : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    private const string Defaults = "CallShapes.Defaults";
    private const string Shifted = "BaseShift.Defaults";

    private readonly ILensServerFixture _shared;
    private readonly ReferencesServerFixture _references;
    private readonly string _directory;

    public DefaultValueTests(ILensServerFixture shared, ReferencesServerFixture references)
    {
        _shared = shared;
        _references = references;
        _directory = Path.GetDirectoryName(shared.AssemblyPath)!;
        foreach (var name in new[] { "CallShapes.dll", "BaseShift.dll", "BaseShift2.dll", "ExternalUser.dll" })
        {
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fixture not found: {path}. Tests/Fixtures/ is expected to land here via Tests.csproj's " +
                    "ProjectReference.");
        }
    }

    // Each default reads as the decompiled code declares it: a number with the suffix its type
    // needs and in the invariant culture, a character and a string with their escapes, null, a
    // struct's default, a special value by its name, and an enum's value by its member's name, a
    // combination of flags with '|' and a value no member has as a cast. A null default of a
    // nullable enum is null, and a default of one names the member. An in parameter, an indexer
    // and a constructor take theirs like any other.
    [Fact]
    public async Task ListMembers_WritesEachDefaultAsCSharpDeclaresIt()
    {
        string text = await CallToolText(_shared.Client, "list_members", new()
        {
            ["assembly"] = Path.Combine(_directory, "CallShapes.dll"),
            ["type_name"] = $"{Defaults}.Fallbacks",
        });

        Assert.Equal(
            new[]
            {
                $"22 members of public class {Defaults}.Fallbacks:",
                "21 methods:",
                "  public Fallbacks(int size = 1)",
                "  public void Calls()",
                "  public void Count(int count = 5)",
                "  public void Flag(bool flag = true)",
                "  public void Frame(Edges edges = Edges.Left | Edges.Top)",
                "  public void Go(Pace pace = Pace.Fast)",
                "  public void Letter(char letter = '\\'')",
                "  public void Maybe(Pace? pace = Pace.Slow)",
                "  public void Missing(double value = double.NaN)",
                "  public void Most(int value = int.MaxValue)",
                "  public void NoPace(Pace? pace = null)",
                "  public void NoText(string text = null)",
                "  public void Odd(Pace pace = (Pace)7)",
                "  public void Pay(decimal amount = 1.5m)",
                "  public void Place(Dot dot = default)",
                "  public void Ratio(double ratio = 2.0)",
                "  public void Read(in int value = 3)",
                "  public void Scale(float scale = 0.5f)",
                "  public void Stack(Tier tier = Tier.High)",
                "  public void Text(string text = \"a\\\"b\")",
                "  public void Wide(long value = 5000000000L)",
                "1 property:",
                "  public int Item[int row, int column = 0] { get; }",
            },
            text.Split('\n'));
    }

    // A constant of a class reads as C# declares it, a bool as true or false and an enum's value by
    // its member's name. The static constructor that sets the decimal is private.
    [Fact]
    public async Task ListMembers_WritesEachConstantAsCSharpDeclaresIt()
    {
        string text = await CallToolText(_shared.Client, "list_members", new()
        {
            ["assembly"] = Path.Combine(_directory, "CallShapes.dll"),
            ["type_name"] = $"{Defaults}.Presets",
        });

        Assert.Equal(
            new[]
            {
                $"12 members of public class {Defaults}.Presets:",
                "1 method:",
                "  public Presets()",
                "11 fields:",
                "  public const long Big = 5000000000L",
                "  public const float Half = 0.5f",
                "  public const int Most = int.MaxValue",
                "  public const bool Off = false",
                "  public const bool On = true",
                "  public const string Path = \"a\\\"b\\\\c\\n\"",
                "  public const decimal Price = 1.25m",
                "  public const Edges Sides = Edges.Left | Edges.Right",
                "  public const char Tab = '\\t'",
                "  public const Pace Usual = Pace.Slow",
                "  public const double Whole = 2.0",
                "Not shown: 1 internal or private",
            },
            text.Split('\n'));
    }

    // A constant of the type that declares it, and PI and E of Math, are written by their values, as
    // the decompiled code writes them, since by a special value's name they would read as
    // themselves. Math's Tau is written through PI, as there.
    [Theory]
    [InlineData("System.Int32", "  public const int MaxValue = 2147483647")]
    [InlineData("System.Double", "  public const double NaN = 0.0 / 0.0")]
    [InlineData("System.Math", "  public const double PI = 3.141592653589793")]
    [InlineData("System.Math", "  public const double Tau = Math.PI * 2.0")]
    public async Task ListMembers_AConstantThatASpecialValueNames_IsWrittenByItsValue(string typeName, string line)
    {
        string text = await CallToolText(_shared.Client, "list_members", new()
        {
            ["assembly"] = _shared.CoreLibPath,
            ["type_name"] = typeName,
            ["kinds"] = new[] { "field" },
        });

        Assert.Contains(line, text.Split('\n'));
    }

    // An enum's own member is written as the number it stands for, since its name would only
    // repeat the member's.
    [Fact]
    public async Task ListMembers_OnAnEnum_WritesItsMembersAsNumbers()
    {
        string text = await CallToolText(_shared.Client, "list_members", new()
        {
            ["assembly"] = Path.Combine(_directory, "CallShapes.dll"),
            ["type_name"] = $"{Defaults}.Pace",
        });

        Assert.Equal(
            $"3 members of public enum {Defaults}.Pace:\n" +
            "3 fields:\n" +
            "  public const Pace Fast = 2\n" +
            "  public const Pace Slow = 1\n" +
            "  public const Pace Stop = 0",
            text);
    }

    // The argument of a call that leaves it out, and the default the header names, read as the
    // declaration writes the default. A call that passes the default is decompiled without it.
    [Theory]
    [InlineData(false,
        $"3 calls to {Defaults}.Fallbacks.Go(Pace), argument for pace:\n" +
        $"{Defaults}.Fallbacks.Calls()\n" +
        "  Pace.Fast (default)\n" +
        "  Pace.Slow\n" +
        "  Pace.Fast (default)")]
    [InlineData(true,
        $"1 of 3 calls to {Defaults}.Fallbacks.Go(Pace) passes pace other than its default Pace.Fast:\n" +
        $"{Defaults}.Fallbacks.Calls()\n" +
        "  Pace.Slow")]
    public async Task CallSites_WritesTheDefaultACallLeavesOutAsTheDeclarationDoes(bool nonDefaultOnly, string expected)
    {
        string text = await CallToolText(_shared.Client, "analyze", new()
        {
            ["assembly"] = Path.Combine(_directory, "CallShapes.dll"),
            ["type_name"] = $"{Defaults}.Fallbacks",
            ["member_name"] = "Go",
            ["kind"] = "call_sites",
            ["parameter_name"] = "pace",
            ["non_default_only"] = nonDefaultOnly,
        });

        Assert.Equal(expected, text);
    }

    // The shared server cannot load ExternalLib for ExternalUser, so a value of its enum is the
    // number with the enum's name, as valid C# as the member's name the second server writes.
    [Theory]
    [InlineData(false, "(Glaze)2", "(Glaze)1")]
    [InlineData(true, "Glaze.Frosted", "Glaze.Clear")]
    public async Task ListMembers_AValueOfAnotherLibrarysEnum_NamesTheMemberWhereILensReadsTheLibrary(
        bool withLibrary, string fired, string usual)
    {
        string text = await CallToolText(Server(withLibrary), "list_members", new()
        {
            ["assembly"] = Path.Combine(_directory, "ExternalUser.dll"),
            ["type_name"] = "ExternalUser.Defaults.Kiln",
        });

        Assert.Equal(
            "4 members of public class ExternalUser.Defaults.Kiln:\n" +
            "3 methods:\n" +
            "  public Kiln()\n" +
            $"  public void Fire(Glaze glaze = {fired})\n" +
            "  public void Run()\n" +
            "1 field:\n" +
            $"  public const Glaze Usual = {usual}",
            text);
    }

    [Theory]
    [InlineData(false, "(Glaze)2")]
    [InlineData(true, "Glaze.Frosted")]
    public async Task CallSites_AnOmittedValueOfAnotherLibrarysEnum_IsWrittenAsListMembersWritesIt(
        bool withLibrary, string argument)
    {
        string text = await CallToolText(Server(withLibrary), "analyze", new()
        {
            ["assembly"] = Path.Combine(_directory, "ExternalUser.dll"),
            ["type_name"] = "ExternalUser.Defaults.Kiln",
            ["member_name"] = "Fire",
            ["kind"] = "call_sites",
            ["parameter_name"] = "glaze",
        });

        Assert.Equal(
            "1 call to ExternalUser.Defaults.Kiln.Fire(Glaze), argument for glaze:\n" +
            "ExternalUser.Defaults.Kiln.Run()\n" +
            $"  {argument} (default)",
            text);
    }

    // A default that changes, comes or goes changes the declaration, while Pick's default and the
    // constant Usual name the same member of Pace after its members were numbered anew, which
    // shows at Pace alone.
    [Fact]
    public async Task Compare_ChangedTypes_ListsATypeWhoseDefaultsChanged()
    {
        string text = await CallToolText(_shared.Client, "compare", Builds(new() { ["namespace"] = Shifted }));

        Assert.Equal(
            new[]
            {
                "2 changed types:",
                $"~ {Shifted}.Pace [members,values]",
                $"~ {Shifted}.Valves [declarations]",
            },
            text.Split('\n'));
    }

    [Theory]
    [InlineData("Valves",
        $"7 member changes in {Shifted}.Valves:\n" +
        "~ public Valves(int size = 1) → public Valves(int size = 2)\n" +
        "~ public void Gain(int width) → public void Gain(int width = 1)\n" +
        "~ public int Item[int row, int column = 0] { get; } → public int Item[int row, int column = 1] { get; }\n" +
        "~ public void Lose(int width = 1) → public void Lose(int width)\n" +
        "~ public void Name(string name = \"before\") → public void Name(string name = \"after\")\n" +
        "~ public void Open(int width = 5) → public void Open(int width = 6)\n" +
        "~ public void Pay(decimal amount = 1.5m) → public void Pay(decimal amount = 2.5m)")]
    [InlineData("Pace",
        $"3 member changes in {Shifted}.Pace:\n" +
        "+ public const Pace Crawl = 1\n" +
        "~ public const Pace Fast = 2 → 3\n" +
        "~ public const Pace Slow = 1 → 2")]
    public async Task Compare_MemberChanges_ListsEachChangedDefault(string typeName, string expected)
    {
        string text = await CallToolText(_shared.Client, "compare",
            Builds(new() { ["type_name"] = $"{Shifted}.{typeName}" }));

        Assert.Equal(expected, text);
    }

    // The line in the member list's form stands before the verdict where the default changed, and
    // a default or a constant that names the same member of the renumbered enum reads alike.
    [Theory]
    [InlineData("Valves", "Open",
        "// public void Open(int width = 5) → public void Open(int width = 6)\n" +
        $"// {Shifted}.Valves.Open(int) → void — bodies are identical in both assemblies.")]
    [InlineData("Valves", "Pick", $"// {Shifted}.Valves.Pick(Pace) → void — bodies are identical in both assemblies.")]
    [InlineData("Picks", "Usual", $"// {Shifted}.Picks.Usual — Pace.Slow in both assemblies.")]
    public async Task Compare_OneMember_SaysWhetherItsDefaultChanged(string typeName, string memberName, string expected)
    {
        string text = await CallToolText(_shared.Client, "compare", Builds(new()
        {
            ["type_name"] = $"{Shifted}.{typeName}",
            ["member_name"] = memberName,
        }));

        Assert.Equal(expected, text);
    }

    // The same ExternalUser.dll in two folders, ExternalLib.dll beside the second alone: the first
    // writes Glaze's values as numbers with the enum's name, the second by its members' names. The
    // two store the same values, so the comparison finds no change in the default, as it finds
    // none in the constant.
    [Fact]
    public async Task Compare_ADefaultWrittenWithoutItsEnumsLibraryInOneBuild_IsUnchangedWhereItStoresTheSameValue()
    {
        string root = Path.Combine(Path.GetTempPath(), "ILens-defaults-" + Guid.NewGuid().ToString("N"));
        string without = Directory.CreateDirectory(Path.Combine(root, "Without")).FullName;
        string with = Directory.CreateDirectory(Path.Combine(root, "With")).FullName;
        File.Copy(Path.Combine(_directory, "ExternalUser.dll"), Path.Combine(without, "ExternalUser.dll"));
        File.Copy(Path.Combine(_directory, "ExternalUser.dll"), Path.Combine(with, "ExternalUser.dll"));
        File.Copy(Path.Combine(_directory, "References", "ExternalLib.dll"), Path.Combine(with, "ExternalLib.dll"));
        try
        {
            await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-defaults",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", root],
            })))
            {
                foreach (var (folder, fired) in new[] { (without, "(Glaze)2"), (with, "Glaze.Frosted") })
                    Assert.Contains($"  public void Fire(Glaze glaze = {fired})", (await CallToolText(client, "list_members", new()
                    {
                        ["assembly"] = Path.Combine(folder, "ExternalUser.dll"),
                        ["type_name"] = "ExternalUser.Defaults.Kiln",
                    })).Split('\n'));

                var builds = new Dictionary<string, object?>
                {
                    ["assembly_a"] = Path.Combine(without, "ExternalUser.dll"),
                    ["assembly_b"] = Path.Combine(with, "ExternalUser.dll"),
                    ["type_name"] = "ExternalUser.Defaults.Kiln",
                };
                Assert.Equal("No member changes in ExternalUser.Defaults.Kiln", await CallToolText(client, "compare", builds));
                builds["member_name"] = "Fire";
                Assert.Equal("// ExternalUser.Defaults.Kiln.Fire(Glaze) → void — bodies are identical in both assemblies.",
                    await CallToolText(client, "compare", builds));
            }
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    private McpClient Server(bool withLibrary) => withLibrary ? _references.Client : _shared.Client;

    private Dictionary<string, object?> Builds(Dictionary<string, object?> arguments)
    {
        arguments["assembly_a"] = Path.Combine(_directory, "BaseShift.dll");
        arguments["assembly_b"] = Path.Combine(_directory, "BaseShift2.dll");
        return arguments;
    }

    /// <summary>
    /// Calls an ILens MCP tool, fails the test if it returned an error, and returns the
    /// concatenated text content.
    /// </summary>
    private static async Task<string> CallToolText(
        McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
