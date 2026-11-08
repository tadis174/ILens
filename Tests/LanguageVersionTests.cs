using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the C# the decompiler writes: C# 14, so that what C# 12 to 14 added reads
/// as its source writes it rather than in the form the compiler stores, but without primary
/// constructors for classes and structs and without first-class span conversions, which would
/// write code its source does not hold. The decompiler call_sites reads keeps inline arrays off,
/// so that every call keeps its count. The decompile tests of what C# 12 to 14 added fail on a
/// build that writes C# 11, those of Coin's constructor and the AsSpan calls on one that switches
/// either feature on, and that of call_sites on one that leaves inline arrays on there. How
/// list_members and search_code answer about an extension block is pinned in ExtensionBlockTests.
/// ExternalLib's Vault, BaseShift's Renumbered and InterfaceShapes' Gatherer and Caller
/// hold what C# 12 and 13 added, InterfaceShapes' Gauge and GaugeExtensions a property written
/// with C# 14's field keyword, two calls to AsSpan and an extension block, and CallShapes' Coin a
/// constructor that only assigns its field.
/// </summary>
public sealed class LanguageVersionTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;
    private readonly string _testOutput;

    public LanguageVersionTests(ILensServerFixture server)
    {
        _server = server;
        _testOutput = Path.GetDirectoryName(_server.AssemblyPath)!;
    }

    // Written as C# 11, a ref readonly parameter reads as ref with the two attributes the compiler
    // stores the keyword as, while the header names it ref readonly.
    [Fact]
    public async Task Decompile_WritesARefReadonlyParameterAsTheHeaderNamesIt()
    {
        string code = await Decompile(Path.Combine("References", "ExternalLib.dll"), "ExternalLib.Vault", "Peek");

        Assert.Equal("// ExternalLib.Vault.Peek(ref readonly int) → int", code.Split('\n')[0]);
        Assert.Contains("public int Peek(ref readonly int slot)", code);
        Assert.DoesNotContain("RequiresLocation", code);
    }

    // Written as C# 11, the lambda loses its parameter's default value, and the method's call to it
    // without an argument does not compile.
    [Fact]
    public async Task Decompile_KeepsTheDefaultValueOfALambdasParameter()
    {
        string code = await Decompile("BaseShift.dll", "BaseShift.Generated.Renumbered", "WithDefault");

        Assert.Contains("(int value = 1) => value + 1;", code);
    }

    // Written as C# 11, a parameter that takes any number of arguments through a collection reads
    // with the attribute the compiler marks it with and without params, unlike list_members.
    [Fact]
    public async Task Decompile_WritesParamsBeforeACollectionAsBeforeAnArray()
    {
        string code = await Decompile("InterfaceShapes.dll", "InterfaceShapes.Params.Gatherer");

        Assert.Contains("public int this[params ReadOnlySpan<int> keys] => keys.Length;", code);
        Assert.Contains("public int Count(params ReadOnlySpan<int> values)", code);
        Assert.Contains("public int Join(string separator, params IEnumerable<string> parts)", code);
        Assert.Contains("public int Fill(params List<int> items)", code);
        Assert.Contains("public int Sum(params int[] values)", code);
        Assert.DoesNotContain("ParamCollection", code);
    }

    // Written as C# 11, the elements of the inline array the compiler builds for a params span
    // read as calls to the compiler's helper.
    [Fact]
    public async Task Decompile_WritesTheElementsOfAnInlineArrayAsIndexes()
    {
        string code = await Decompile("InterfaceShapes.dll", "InterfaceShapes.Params.Caller", "Expanded");

        Assert.Contains("buffer[0] = first;", code);
        Assert.DoesNotContain("InlineArrayElementRef", code);
    }

    // Written as C# 13 or earlier, a property that uses the field keyword reads over the backing
    // field the compiler declares, whose name no C# can write.
    [Fact]
    public async Task Decompile_WritesTheFieldKeywordWhereTheSourceWritesIt()
    {
        string code = await Decompile("InterfaceShapes.dll", "InterfaceShapes.Language.Gauge");

        Assert.Contains("return field;", code);
        Assert.Contains("field = Math.Max(0, value);", code);
        Assert.DoesNotContain("k__BackingField", code);
    }

    // Written as C# 13 or earlier, an extension block reads as the skeleton the compiler stores,
    // classes named <G>$ and <M>$ beside the method it makes of each member.
    [Fact]
    public async Task Decompile_WritesAnExtensionBlockAsItsSourceWritesIt()
    {
        string code = await Decompile("InterfaceShapes.dll", "InterfaceShapes.Language.GaugeExtensions");

        Assert.Contains("extension(Gauge gauge)", code);
        Assert.Contains("public int Doubled => gauge.Level * 2;", code);
        Assert.DoesNotContain("<G>$", code);
    }

    // With primary constructors, Coin would read as a struct with one, since the decompiler cannot
    // tell a primary constructor from an ordinary constructor that only assigns fields.
    [Fact]
    public async Task Decompile_WritesAnOrdinaryConstructorAsItsSourceDeclaresIt()
    {
        string code = await Decompile("CallShapes.dll", "CallShapes.Forms.Coin");

        Assert.Contains("public Coin(int cents)", code);
        Assert.Contains("Cents = cents;", code);
        Assert.DoesNotContain("struct Coin(", code);
    }

    // First-class span conversions would write a call to AsSpan as a cast or leave it out, which
    // hides a call the code makes, also from the text call_sites counts against the IL.
    [Fact]
    public async Task Decompile_KeepsACallToAsSpanOnTheReceiverAndOnAnArgument()
    {
        string trimmed = await Decompile("InterfaceShapes.dll", "InterfaceShapes.Language.Gauge", "Trimmed");
        string same = await Decompile("InterfaceShapes.dll", "InterfaceShapes.Language.Gauge", "Same");

        Assert.Contains("return text.AsSpan().Trim().Length;", trimmed);
        Assert.Contains(".SequenceEqual(right.AsSpan());", same);
    }

    // With inline arrays on, the decompiler call_sites reads writes the elements of the array the
    // compiler builds for a params span as indexes, so the calls to the compiler's helpers count in
    // the IL alone and their callers are reported as not shown.
    [Fact]
    public async Task CallSites_OfTheCompilersInlineArrayHelper_ListsEveryCall()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = Path.Combine(_testOutput, "InterfaceShapes.dll"),
            ["type_name"] = "<PrivateImplementationDetails>",
            ["member_name"] = "InlineArrayAsReadOnlySpan",
            ["kind"] = "call_sites",
        });

        Assert.Equal(
            new[]
            {
                "2 calls to <PrivateImplementationDetails>.InlineArrayAsReadOnlySpan<TBuffer, TElement>(in TBuffer, int):",
                "InterfaceShapes.Params.Caller.Expanded(Gatherer, int, string)",
                "  <PrivateImplementationDetails>.InlineArrayAsReadOnlySpan<InlineArray3<int>, int>(in buffer, 3)",
                "InterfaceShapes.Params.Caller.Indexed(Gatherer, int)",
                "  <PrivateImplementationDetails>.InlineArrayAsReadOnlySpan<InlineArray3<int>, int>(in buffer, 3)",
            },
            text.Split('\n'));
    }

    private Task<string> Decompile(string assembly, string typeName, string? memberName = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = Path.Combine(_testOutput, assembly),
            ["type_name"] = typeName,
        };
        if (memberName is not null)
            arguments["member_name"] = memberName;
        return CallToolText("decompile", arguments);
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
