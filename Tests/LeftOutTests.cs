using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for what find_types, find_methods and list_members say about the matches
/// their two default filters left out, for what analyze says under an answer that is empty only
/// because compiler-generated results were left out, for a compiler-generated class or member
/// named outright, and for the name a constructor is found by. The fixture's
/// <c>CallShapes.Vault</c> has members of every accessibility, and <c>CallShapes.Concealed</c>
/// holds one internal class with a lambda.
/// </summary>
public sealed class LeftOutTests : IClassFixture<ILensServerFixture>
{
    private const string Vault = "CallShapes.Vault";

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public LeftOutTests(ILensServerFixture server)
    {
        _server = server;
        _callShapesPath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "CallShapes.dll");
        if (!File.Exists(_callShapesPath))
            throw new FileNotFoundException(
                $"CallShapes fixture not found: {_callShapesPath}. " +
                "Tests/Fixtures/CallShapes/ is expected to land here via Tests.csproj's " +
                "ProjectReference.");
    }

    [Fact]
    public async Task ListMembers_WithResults_CountsWhatTheAccessibilityFilterLeftOut()
    {
        // Left out: the static constructor, Reseal, Seal, Capacity and _stored. The class the
        // compiler made for Meter's lambda is not counted under an answer with members.
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = Vault,
        });

        Assert.Equal(
            "6 members of public class CallShapes.Vault:\n" +
            "5 methods:\n" +
            "  public Vault()\n" +
            "  public Vault(int stored)\n" +
            "  public Func<int> Meter(int step)\n" +
            "  public int Read()\n" +
            "  protected void Rekey()\n" +
            "1 field:\n" +
            "  public int Count\n" +
            "Not shown: 5 internal or private",
            text);
    }

    [Fact]
    public async Task ListMembers_WithEveryAccessibility_ListsWhatTheLineCountedAndAddsNoLine()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = Vault,
            ["accessibility"] = "all",
        });

        Assert.Equal(
            "11 members of public class CallShapes.Vault:\n" +
            "8 methods:\n" +
            "  static Vault()\n" +
            "  public Vault()\n" +
            "  public Vault(int stored)\n" +
            "  public Func<int> Meter(int step)\n" +
            "  public int Read()\n" +
            "  protected void Rekey()\n" +
            "  internal void Reseal()\n" +
            "  private int Seal(int key)\n" +
            "3 fields:\n" +
            "  internal int Capacity\n" +
            "  public int Count\n" +
            "  private int _stored",
            text);
    }

    [Fact]
    public async Task ListMembers_OnlyPublic_CallsWhatItLeftOutNonPublic()
    {
        // Rekey is protected, so it joins the five that public_protected leaves out.
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = Vault,
            ["accessibility"] = "public",
        });

        Assert.Equal(
            "5 members of public class CallShapes.Vault:\n" +
            "4 methods:\n" +
            "  public Vault()\n" +
            "  public Vault(int stored)\n" +
            "  public Func<int> Meter(int step)\n" +
            "  public int Read()\n" +
            "1 field:\n" +
            "  public int Count\n" +
            "Not shown: 6 non-public",
            text);
    }

    [Fact]
    public async Task ListMembers_WithNothingLeftOut_KeepsItsForm()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Settings.Section",
        });

        Assert.Equal(
            "1 member of public sealed class CallShapes.Settings.Section:\n" +
            "1 method:\n" +
            "  public Section()",
            text);
    }

    // Reseal is internal and Seal private, so a search for "seal" has two matches and shows
    // neither. "No methods" alone would read as "the class has no such method".
    [Theory]
    [InlineData("find_methods", "No methods")]
    [InlineData("list_members", "No members of public class CallShapes.Vault")]
    public async Task AnEmptyAnswer_CountsThePrivateMatches_AndNamesTheSettingThatListsThem(
        string toolName, string header)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = Vault,
            ["name_contains"] = "seal",
        };

        string empty = await CallToolText(toolName, arguments);
        string listed = await CallToolText(toolName, new(arguments) { ["accessibility"] = "all" });

        Assert.Equal($"{header}\nNot shown: 2 internal or private; accessibility all lists them", empty);
        Assert.StartsWith("2 ", listed);
        Assert.DoesNotContain("Not shown", listed);
    }

    // Ledger is internal, and the class the compiler makes for its lambda is generated and
    // private. The empty answer counts both in one line. An answer with a result does not
    // count the generated one.
    [Fact]
    public async Task FindTypes_WithAnInternalAndAGeneratedMatch_CountsBothInOneLine()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["namespace"] = "CallShapes.Concealed",
        };

        Assert.Equal(
            "No types\nNot shown: 1 internal or private, 1 compiler-generated; " +
            "include_compiler_generated with accessibility all lists them",
            await CallToolText("find_types", arguments));
        Assert.Equal(
            "1 type:\nCallShapes.Concealed.Ledger",
            await CallToolText("find_types", new(arguments) { ["accessibility"] = "all" }));
        Assert.Equal(
            "2 types:\nCallShapes.Concealed.Ledger\nCallShapes.Concealed.Ledger.<>c",
            await CallToolText("find_types", new(arguments)
            {
                ["accessibility"] = "all",
                ["include_compiler_generated"] = true,
            }));
    }

    [Fact]
    public async Task FindTypes_WithResults_CountsWhatTheAccessibilityFilterLeftOut()
    {
        var arguments = new Dictionary<string, object?> { ["assembly"] = _callShapesPath, ["limit"] = 0 };

        string[] shown = (await CallToolText("find_types", arguments)).Split('\n');
        string[] every = (await CallToolText("find_types", new(arguments) { ["accessibility"] = "all" })).Split('\n');

        int leftOut = LeadingCount(every[0]) - LeadingCount(shown[0]);
        Assert.True(leftOut > 0, "The fixture holds internal types, such as CallShapes.Concealed.Ledger.");
        Assert.Equal($"Not shown: {leftOut} internal or private", shown[^1]);
        Assert.DoesNotContain(every, line => line.StartsWith("Not shown"));
    }

    // The classes the compiler makes for lambdas are private as well as generated, so either
    // setting alone answers empty again. The line names what one further call needs.
    [Fact]
    public async Task FindTypes_ForAGeneratedName_NamesBothSettings_AndTheCallWithThemListsThatMany()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["name_contains"] = "DisplayClass",
        };

        string empty = await CallToolText("find_types", arguments);
        var line = Regex.Match(empty,
            @"^No types\nNot shown: (\d+) compiler-generated; include_compiler_generated with accessibility all lists them$");
        Assert.True(line.Success, empty);

        Assert.Equal(
            $"No types\nNot shown: {line.Groups[1].Value} compiler-generated; include_compiler_generated lists them",
            await CallToolText("find_types", new(arguments) { ["accessibility"] = "all" }));
        Assert.StartsWith(
            $"{line.Groups[1].Value} types:\n",
            await CallToolText("find_types", new(arguments)
            {
                ["accessibility"] = "all",
                ["include_compiler_generated"] = true,
            }));
    }

    [Fact]
    public async Task ListMembers_ForAGeneratedName_CountsTheGeneratedMatch()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = Vault,
            ["name_contains"] = "DisplayClass",
        });

        Assert.Equal(
            "No members of public class CallShapes.Vault\n" +
            "Not shown: 1 compiler-generated; include_compiler_generated with accessibility all lists it",
            text);
    }

    // A class the compiler generated counts as generated with all its members. Named outright
    // it is listed whatever include_compiler_generated says: it was asked for by name.
    [Fact]
    public async Task ListMembers_OnAGeneratedClassNamedOutright_ListsItsMembers()
    {
        string closure = await ClosureOfMeter();
        string shortName = closure[(closure.LastIndexOf('.') + 1)..];
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = closure,
        };

        Assert.Equal(
            $"3 members of private sealed class {closure}:\n" +
            "2 methods:\n" +
            $"  public {shortName}()\n" +
            "  internal int <Meter>b__0()\n" +
            "1 field:\n" +
            "  public int step",
            await CallToolText("list_members", new(arguments) { ["accessibility"] = "all" }));
        Assert.Equal(
            $"2 members of private sealed class {closure}:\n" +
            "1 method:\n" +
            $"  public {shortName}()\n" +
            "1 field:\n" +
            "  public int step\n" +
            "Not shown: 1 internal or private",
            await CallToolText("list_members", arguments));
    }

    [Fact]
    public async Task FindMethods_OnAGeneratedClassNamedOutright_ListsItsMethods()
    {
        string closure = await ClosureOfMeter();

        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = closure,
            ["accessibility"] = "all",
        });

        Assert.Equal($"2 methods:\n{closure}..ctor() → void\n{closure}.<Meter>b__0() → int", text);
    }

    // The classes the compiler makes for async methods are the only ones in the fixture that
    // implement IAsyncStateMachine, and the fields it adds to the classes for Callers' iterator
    // and async method the only members that expose Callers. "No implementations" alone would
    // read as "there is none". The fixture may gain such a class, so the count is read from
    // the line. The first type comes from another assembly, the second is the fixture's own.
    [Theory]
    [InlineData("System.Runtime.CompilerServices.IAsyncStateMachine", "implemented_by", "implementation",
        "No implementations of System.Runtime.CompilerServices.IAsyncStateMachine")]
    [InlineData("CallShapes.Callers", "exposed_by", "member", "No members exposing CallShapes.Callers")]
    public async Task Analyze_EmptyOnlyForGeneratedResults_CountsThem_AndTheCallWithTheSwitchListsThatMany(
        string typeName, string kind, string counted, string header)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["kind"] = kind,
        };

        string[] empty = (await CallToolText("analyze", arguments)).Split('\n');
        string listed = await CallToolText("analyze", new(arguments) { ["include_compiler_generated"] = true });

        Assert.Equal(header, empty[0]);
        Assert.True(empty.Length >= 2, string.Join('\n', empty));
        var line = Regex.Match(empty[1],
            @"^Not shown: (\d+) compiler-generated; include_compiler_generated lists (it|them)$");
        Assert.True(line.Success, string.Join('\n', empty));
        int count = int.Parse(line.Groups[1].Value);
        Assert.Equal(count == 1 ? "it" : "them", line.Groups[2].Value);
        Assert.StartsWith($"{count} {counted}{(count == 1 ? "" : "s")} ", listed);
        Assert.DoesNotContain("Not shown", listed);
    }

    // The line belongs to the list, so it stands under the header and before what analyze
    // adds below a list.
    [Fact]
    public async Task Analyze_PutsTheLineBeforeTheLinesItAddsBelowAList()
    {
        string[] lines = (await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "System.Runtime.CompilerServices.IAsyncStateMachine",
            ["kind"] = "implemented_by",
        })).Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Not shown: ", lines[1]);
        Assert.StartsWith("Types deriving from a class in another assembly may also implement it", lines[2]);
    }

    // Meter's lambda captures, so Meter also uses the class the compiler made for it, that
    // class's constructor and its field. An answer with a result does not count them.
    [Fact]
    public async Task Analyze_WithResults_AddsNoLineForTheGeneratedOnesItLeftOut()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = Vault,
            ["member_name"] = "Meter",
            ["kind"] = "uses",
        };

        string text = await CallToolText("analyze", arguments);

        Assert.StartsWith("1 symbol used by CallShapes.Vault.Meter(int) [declared]:\n", text);
        Assert.DoesNotContain("Not shown", text);
        Assert.StartsWith(
            "4 symbols used by ",
            await CallToolText("analyze", new(arguments) { ["include_compiler_generated"] = true }));
    }

    // A class or member the compiler generated was asked for by name, so the answer lists
    // what the compiler generated around it whatever include_compiler_generated says: the
    // static constructor of the class made for Ledger's lambda creates the class's one
    // instance and stores it in the field <>9.
    [Theory]
    [InlineData(null, "instantiated_by", "1 symbol instantiating CallShapes.Concealed.Ledger.<>c:")]
    [InlineData("<>9", "assigned_by", "1 writer of CallShapes.Concealed.Ledger.<>c.<>9 [declared]:")]
    public async Task Analyze_OnAGeneratedSymbolNamedOutright_ListsGeneratedResults(
        string? memberName, string kind, string header)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Concealed.Ledger.<>c",
            ["kind"] = kind,
        };
        if (memberName is not null)
            arguments["member_name"] = memberName;

        Assert.Equal(
            $"{header}\nCallShapes.Concealed.Ledger.<>c..cctor()",
            await CallToolText("analyze", arguments));
    }

    // The compiler adds this attribute class to an assembly that needs it and marks it with
    // itself. It is generated, and so is everything that carries it.
    [Fact]
    public async Task Analyze_OnAGeneratedAttributeClass_ListsWhatCarriesIt()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = Path.Combine(Path.GetDirectoryName(_callShapesPath)!, "BaseShift.dll"),
            ["type_name"] = "Microsoft.CodeAnalysis.EmbeddedAttribute",
            ["kind"] = "applied_to",
        });

        Assert.Contains("Microsoft.CodeAnalysis.EmbeddedAttribute", text.Split('\n').Skip(1));
        Assert.DoesNotContain("Not shown", text);
    }

    // list_members prints a constructor under the name of its type, so its name filter finds
    // one by that name, and still by .ctor, the name the other tools print and take.
    [Theory]
    [InlineData("Vault", "\nNot shown: 1 internal or private")]
    [InlineData(".ctor", "")]
    public async Task ListMembers_FindsAConstructorByTheNameOfItsType_AndByItsOwn(string filter, string notShown)
    {
        // The static constructor is private. Its own name is .cctor, which ".ctor" does not match.
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = Vault,
            ["name_contains"] = filter,
        });

        Assert.Equal(
            "2 members of public class CallShapes.Vault:\n" +
            "2 methods:\n" +
            "  public Vault()\n" +
            "  public Vault(int stored)" + notShown,
            text);
    }

    [Fact]
    public async Task ListMembers_FindsTheStaticConstructorByTheNameOfItsType()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = Vault,
            ["name_contains"] = "vault",
            ["accessibility"] = "all",
        });

        Assert.Equal(
            "3 members of public class CallShapes.Vault:\n" +
            "3 methods:\n" +
            "  static Vault()\n" +
            "  public Vault()\n" +
            "  public Vault(int stored)",
            text);
    }

    /// <summary>
    /// The class the compiler made for the lambda in Vault.Meter, by the name ILens prints it
    /// under. The compiler numbers that name, so the tests ask for it.
    /// </summary>
    private async Task<string> ClosureOfMeter()
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = Vault,
            ["kinds"] = new[] { "nested_type" },
            ["accessibility"] = "all",
            ["include_compiler_generated"] = true,
        });

        string[] lines = text.Split('\n');
        Assert.Equal("1 nested type:", lines[1]);
        return lines[2].Trim();
    }

    private static int LeadingCount(string header) => int.Parse(header[..header.IndexOf(' ')]);

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
