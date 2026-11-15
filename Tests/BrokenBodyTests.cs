using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for answers about an assembly in which the code of some methods breaks off
/// inside an instruction, which <see cref="BrokenBodyAssembly"/> writes. Every pass over the
/// method bodies reads such code up to the break, and every answer that read it ends with a line
/// naming the methods, since a use past the break is missing from the answer. Who uses a method
/// is asked in <see cref="MethodUserTests"/>.
/// </summary>
public sealed class BrokenBodyTests : IClassFixture<ILensServerFixture>
{
    private const string NotCheckedBoth =
        "\nNot checked: the code of 2 methods past where it breaks off inside an instruction: " +
        "BrokenBody.Shapes.Cut(), BrokenBody.Shapes.CutAddress()";

    private readonly ILensServerFixture _server;

    public BrokenBodyTests(ILensServerFixture server)
    {
        _server = server;
    }

    // Each kind that reads the code of every method of the assembly names both methods.
    // CutAddress passes the address of Count to a method that writes through it before its code
    // breaks off, and the field questions list it as taking the address, since the rest of its
    // code cannot be followed.
    [Theory]
    [InlineData("BrokenBody.Shapes", "Plain", "call_sites",
        "2 calls to BrokenBody.Shapes.Plain():\n" +
        "BrokenBody.Shapes.Cut()\n" +
        "  Plain()\n" +
        "BrokenBody.User.Use(Shapes)\n" +
        "  P_0.Plain()" + NotCheckedBoth)]
    [InlineData("BrokenBody.Shapes", "Count", "read_by",
        "1 reader of BrokenBody.Shapes.Count [declared]:\n" +
        "BrokenBody.Shapes.CutAddress() (may read: takes its address)" + NotCheckedBoth)]
    [InlineData("BrokenBody.Shapes", "Count", "assigned_by",
        "1 writer of BrokenBody.Shapes.Count [declared]:\n" +
        "BrokenBody.Shapes.CutAddress() (may write: takes its address)" + NotCheckedBoth)]
    [InlineData("BrokenBody.Shapes", null, "instantiated_by",
        "No symbols instantiating BrokenBody.Shapes" + NotCheckedBoth)]
    [InlineData("BrokenBody.Derived", null, "used_by",
        "No users of BrokenBody.Derived" + NotCheckedBoth)]
    [InlineData("System.Object", ".ctor", "used_by",
        "1 user of System.Object..ctor():\n" +
        "BrokenBody.Shapes..ctor()" + NotCheckedBoth)]
    public async Task AnAnswerThatReadsEveryMethod_EndsWithTheMethodsWhoseCodeBreaksOff(
        string typeName, string? memberName, string kind, string expected)
    {
        var path = BrokenBodyAssembly.Write();
        try
        {
            Assert.Equal(expected, await Analyze(path, typeName, memberName, kind));
        }
        finally
        {
            BrokenBodyAssembly.TryDelete(path);
        }
    }

    // uses reads the code of the method asked about alone, so it names that method where its
    // code breaks off, and nothing where it does not.
    [Theory]
    [InlineData("BrokenBody.Shapes", "Cut",
        "1 symbol used by BrokenBody.Shapes.Cut() [declared]:\n" +
        "BrokenBody.Shapes.Plain()\n" +
        "Not checked: the code of BrokenBody.Shapes.Cut() past where it breaks off inside an instruction")]
    [InlineData("BrokenBody.Shapes", "CutAddress",
        "2 symbols used by BrokenBody.Shapes.CutAddress() [declared]:\n" +
        "BrokenBody.Shapes.Count\n" +
        "BrokenBody.Shapes.Touch(ref int)\n" +
        "Not checked: the code of BrokenBody.Shapes.CutAddress() past where it breaks off inside an instruction")]
    [InlineData("BrokenBody.User", "Use",
        "2 symbols used by BrokenBody.User.Use(Shapes) [declared]:\n" +
        "BrokenBody.Shapes.Plain()\n" +
        "BrokenBody.Shapes.Virtual()")]
    public async Task Uses_NamesTheMethodAskedAbout_WhereItsCodeBreaksOff(string typeName, string memberName, string expected)
    {
        var path = BrokenBodyAssembly.Write();
        try
        {
            Assert.Equal(expected, await Analyze(path, typeName, memberName, "uses"));
        }
        finally
        {
            BrokenBodyAssembly.TryDelete(path);
        }
    }

    // The line names five methods and counts the rest, sorted by the printed name.
    [Fact]
    public async Task TheLine_NamesFiveMethods_AndCountsTheRest()
    {
        var path = BrokenBodyAssembly.Write(moreCuts: 5);
        try
        {
            Assert.Equal(
                "No users of BrokenBody.Derived\n" +
                "Not checked: the code of 7 methods past where it breaks off inside an instruction: " +
                "BrokenBody.Shapes.Cut(), BrokenBody.Shapes.Cut1(), BrokenBody.Shapes.Cut2(), " +
                "BrokenBody.Shapes.Cut3(), BrokenBody.Shapes.Cut4() and 2 more",
                await Analyze(path, "BrokenBody.Derived", null, "used_by"));
        }
        finally
        {
            BrokenBodyAssembly.TryDelete(path);
        }
    }

    // An accessor whose code breaks off is named as its property, as the answers name its uses,
    // and the line counts members, since not all it names are methods.
    [Fact]
    public async Task TheLine_NamesAnAccessorAsItsProperty_AndCountsMembers()
    {
        var path = BrokenBodyAssembly.Write(cutGetter: true);
        try
        {
            Assert.Equal(
                "No users of BrokenBody.Derived\n" +
                "Not checked: the code of 3 members past where it breaks off inside an instruction: " +
                "BrokenBody.Shapes.Cut(), BrokenBody.Shapes.CutAddress(), BrokenBody.Shapes.Size",
                await Analyze(path, "BrokenBody.Derived", null, "used_by"));
        }
        finally
        {
            BrokenBodyAssembly.TryDelete(path);
        }
    }

    // compare names the property whose getter breaks off as well, and counts members.
    [Fact]
    public async Task Compare_NamesAnAccessorAsItsProperty()
    {
        var a = BrokenBodyAssembly.Write(cutGetter: true);
        var b = BrokenBodyAssembly.Write(cutGetter: true);
        try
        {
            Assert.Equal(
                "No changed types\n" +
                "Not compared: the code of 3 members past where it breaks off inside an instruction: " +
                "BrokenBody.Shapes.Cut() in A and B, BrokenBody.Shapes.CutAddress() in A and B, BrokenBody.Shapes.Size in A and B",
                await CallToolText("compare", new() { ["assembly_a"] = a, ["assembly_b"] = b }));
        }
        finally
        {
            BrokenBodyAssembly.TryDelete(a);
            BrokenBodyAssembly.TryDelete(b);
        }
    }

    // The decompiled code of a method ends where its code breaks off, so a line past the break
    // is not there to match.
    [Fact]
    public async Task SearchCode_EndsWithTheMethodsWhoseCodeBreaksOff()
    {
        var path = BrokenBodyAssembly.Write();
        try
        {
            string text = await CallToolText("search_code", new()
            {
                ["assembly"] = path,
                ["regex"] = "Plain",
            });

            Assert.Equal(
                "3 lines matching Plain:\n" +
                "BrokenBody.Shapes.Cut()\n" +
                "  Plain();\n" +
                "BrokenBody.Shapes.Plain()\n" +
                "  public void Plain()\n" +
                "BrokenBody.User.Use(Shapes)\n" +
                "  P_0.Plain();" + NotCheckedBoth,
                text);
        }
        finally
        {
            BrokenBodyAssembly.TryDelete(path);
        }
    }

    // compare reads each method's code up to where it breaks off, in either build, so a change
    // past that does not show. Both copies break off in the same places.
    [Theory]
    [InlineData(null,
        "No changed types\n" +
        "Not compared: the code of 2 methods past where it breaks off inside an instruction: " +
        "BrokenBody.Shapes.Cut() in A and B, BrokenBody.Shapes.CutAddress() in A and B")]
    [InlineData("Cut",
        "// BrokenBody.Shapes.Cut() → void — bodies are identical in both assemblies.\n" +
        "Not compared: the code of BrokenBody.Shapes.Cut() in A and B past where it breaks off inside an instruction")]
    public async Task Compare_NamesTheMethodsWhoseCodeBreaksOff_WithTheBuilds(string? memberName, string expected)
    {
        var a = BrokenBodyAssembly.Write();
        var b = BrokenBodyAssembly.Write();
        try
        {
            var arguments = new Dictionary<string, object?> { ["assembly_a"] = a, ["assembly_b"] = b };
            if (memberName is not null)
            {
                arguments["type_name"] = "BrokenBody.Shapes";
                arguments["member_name"] = memberName;
            }
            Assert.Equal(expected, await CallToolText("compare", arguments));
        }
        finally
        {
            BrokenBodyAssembly.TryDelete(a);
            BrokenBodyAssembly.TryDelete(b);
        }
    }

    // The code of ManualPatches.Apply applies four patches by calling Harmony.Patch and breaks
    // off after them, at its ret. Every patch is read as before, and the line names the method.
    [Fact]
    public async Task HarmonyDependencies_KeepWhatAMethodNamesBeforeItsCodeBreaksOff()
    {
        var original = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, "HarmonyMod.dll");
        var path = BrokenBodyAssembly.CutCopy(original, "HarmonyMod.ManualPatches", "Apply");
        try
        {
            string whole = await CallToolText("find_harmony_dependencies", new() { ["assembly"] = original, ["limit"] = 1000 });
            string cut = await CallToolText("find_harmony_dependencies", new() { ["assembly"] = path, ["limit"] = 1000 });

            Assert.Contains("[PatchCall in HarmonyMod.ManualPatches.Apply]", whole);
            Assert.Equal(
                whole + "\nNot checked: the code of HarmonyMod.ManualPatches.Apply(Harmony) " +
                "past where it breaks off inside an instruction",
                cut);
        }
        finally
        {
            BrokenBodyAssembly.TryDelete(path);
        }
    }

    private Task<string> Analyze(string path, string typeName, string? memberName, string kind)
    {
        var arguments = new Dictionary<string, object?> { ["assembly"] = path, ["type_name"] = typeName, ["kind"] = kind };
        if (memberName is not null)
            arguments["member_name"] = memberName;
        return CallToolText("analyze", arguments);
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
