using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for compare where a build holds no code for a method. Where neither build has
/// a body, the answer says so and why for each build, instead of finding two texts alike:
/// BaseShift.Bodies has methods that are extern in both assemblies and one that is abstract in
/// BaseShift and extern in BaseShift2. A reference assembly, which carries ReferenceAssemblyAttribute,
/// holds stubs where code would be, so compare reads no body of one in any view; the builds of
/// Plates that <see cref="CodelessAssembly"/> writes have bodies that differ, which a comparison
/// of their bodies would report.
/// </summary>
public sealed class MissingBodyTests : IClassFixture<ILensServerFixture>
{
    private const string Stack = "Plates.Stack";
    private readonly ILensServerFixture _server;
    private readonly string _directory;

    public MissingBodyTests(ILensServerFixture server)
    {
        _server = server;
        _directory = Path.GetDirectoryName(_server.AssemblyPath)!;
        foreach (var name in new[] { "BaseShift.dll", "BaseShift2.dll" })
        {
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fixture not found: {path}. Tests/Fixtures/ is expected to land here via Tests.csproj's " +
                    "ProjectReference.");
        }
    }

    // An interface method without a default implementation is abstract, as C# and the metadata
    // call it; a delegate's methods have their code from the runtime. The two formats answer alike,
    // since there is no body to print in either.
    [Theory]
    [InlineData("BaseShift.Members.IGrowing", "Kept",
        "// BaseShift.Members.IGrowing.Kept() → void — neither assembly has a body for it: abstract in both.")]
    [InlineData("BaseShift.Variance.Mapping<,>", "Invoke",
        "// BaseShift.Variance.Mapping<,>.Invoke(TIn) → TOut — neither assembly has a body for it: " +
        "provided by the runtime in both.")]
    [InlineData("BaseShift.Bodies.Native", "Beep",
        "// BaseShift.Bodies.Native.Beep(uint, uint) → bool — neither assembly has a body for it: extern in both.")]
    [InlineData("BaseShift.Bodies.Native", "Count",
        "// BaseShift.Bodies.Native.Count() → int — neither assembly has a body for it: extern in both.")]
    [InlineData("BaseShift.Bodies.Hatch", "Open",
        "// public abstract void Open() → public void Open()\n" +
        "// BaseShift.Bodies.Hatch.Open() → void — neither assembly has a body for it: abstract in A, extern in B.")]
    public async Task Compare_Bodies_OfAMethodWithoutABodyInEitherBuild_SaysWhyInsteadOfComparing(
        string typeName, string memberName, string expected)
    {
        foreach (var format in new[] { "csharp", "il" })
            Assert.Equal(expected, await CallToolText("compare", new()
            {
                ["assembly_a"] = Path.Combine(_directory, "BaseShift.dll"),
                ["assembly_b"] = Path.Combine(_directory, "BaseShift2.dll"),
                ["type_name"] = typeName,
                ["member_name"] = memberName,
                ["format"] = format,
            }));
    }

    // Shelf's Height is abstract in BaseShift and has a body in BaseShift2, which the diff shows.
    [Theory]
    [InlineData("csharp", "-public abstract int Height();", "+\treturn 0;")]
    [InlineData("il", "-(no body)", "+IL_0000: ldc.i4.0")]
    public async Task Compare_Bodies_OfAMethodWithABodyInOneBuildOnly_ShowsTheDiff(string format, string removed, string added)
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = Path.Combine(_directory, "BaseShift.dll"),
            ["assembly_b"] = Path.Combine(_directory, "BaseShift2.dll"),
            ["type_name"] = "BaseShift.Declarations.Shelf",
            ["member_name"] = "Height",
            ["format"] = format,
        });

        var lines = text.Split('\n');
        Assert.Equal("// public abstract int Height() → public virtual int Height()", lines[0]);
        Assert.Contains(removed, lines);
        Assert.Contains(added, lines);
    }

    // The two builds' Count returns another number, which compare reports where both have code.
    [Fact]
    public async Task Compare_TwoReferenceAssemblies_ComparesNoBodyInAnyView()
    {
        var a = CodelessAssembly.Write(CodelessAssembly.Kind.Marked, second: false);
        var b = CodelessAssembly.Write(CodelessAssembly.Kind.Marked, second: true);
        var aCode = CodelessAssembly.Write(CodelessAssembly.Kind.Code, second: false);
        var bCode = CodelessAssembly.Write(CodelessAssembly.Kind.Code, second: true);
        try
        {
            Assert.Equal("1 changed type:\n~ Plates.Stack [members,bodies]",
                await CallToolText("compare", new() { ["assembly_a"] = aCode, ["assembly_b"] = bCode }));

            foreach (var format in new[] { "csharp", "il" })
                Assert.Equal(
                    "// Plates.Stack.Count() → int — A and B are reference assemblies, which hold no code to compare.",
                    await CallToolText("compare", new()
                    {
                        ["assembly_a"] = a,
                        ["assembly_b"] = b,
                        ["type_name"] = Stack,
                        ["member_name"] = "Count",
                        ["format"] = format,
                    }));
            Assert.Equal(
                new[]
                {
                    "1 changed type:",
                    "~ Plates.Stack [members]",
                    "Not compared: bodies; A and B are reference assemblies, which hold no code",
                },
                (await CallToolText("compare", new() { ["assembly_a"] = a, ["assembly_b"] = b })).Split('\n'));
            Assert.Equal(
                new[]
                {
                    "4 member changes in Plates.Stack:",
                    "- public int Label",
                    "+ public int Label { get; }",
                    "+ public int Peek()",
                    "+ public int Top { get; }",
                    "Not compared: bodies; A and B are reference assemblies, which hold no code",
                },
                (await CallToolText("compare", new() { ["assembly_a"] = a, ["assembly_b"] = b, ["type_name"] = Stack }))
                    .Split('\n'));
            Assert.Equal(
                "Plates.Stack.Size is a field, which has no body; A and B are reference assemblies, which hold no code.",
                await CallToolError("compare", new()
                {
                    ["assembly_a"] = a,
                    ["assembly_b"] = b,
                    ["type_name"] = Stack,
                    ["member_name"] = "Size",
                }));
        }
        finally
        {
            CodelessAssembly.TryDelete(a, b, aCode, bCode);
        }
    }

    // The code of the build that has some is all the code there is, so it is shown, in the format
    // asked; Count returns 1 in the first build and 2 in the second.
    [Theory]
    [InlineData(true, "csharp",
        "// Plates.Stack.Count() → int — A is a reference assembly, which holds no code to compare with B's.",
        "return 2;")]
    [InlineData(false, "csharp",
        "// Plates.Stack.Count() → int — B is a reference assembly, which holds no code to compare with A's.",
        "return 1;")]
    [InlineData(true, "il",
        "// Plates.Stack.Count() → int — A is a reference assembly, which holds no code to compare with B's.",
        "IL_0000: ldc.i4.2")]
    public async Task Compare_Bodies_OfAReferenceAssemblyBesideABuildWithCode_ShowsTheOtherBuildsCode(
        bool firstMarked, string format, string line, string code)
    {
        var a = CodelessAssembly.Write(firstMarked ? CodelessAssembly.Kind.Marked : CodelessAssembly.Kind.Code, second: false);
        var b = CodelessAssembly.Write(firstMarked ? CodelessAssembly.Kind.Code : CodelessAssembly.Kind.Marked, second: true);
        try
        {
            string text = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = Stack,
                ["member_name"] = "Count",
                ["format"] = format,
            });
            Assert.StartsWith(line + "\n\n", text);
            Assert.Contains(code, text);
            Assert.Equal(
                $"Not compared: bodies; {(firstMarked ? "A" : "B")} is a reference assembly, which holds no code",
                (await CallToolText("compare", new() { ["assembly_a"] = a, ["assembly_b"] = b })).Split('\n')[^1]);
        }
        finally
        {
            CodelessAssembly.TryDelete(a, b);
        }
    }

    // A member only a reference assembly has is shown by its declaration, and its stub is not shown
    // as its code; nor is the property's, where a name is a field in A and a property in B.
    [Theory]
    [InlineData("Peek",
        "// Plates.Stack.Peek() → int — only B has it.\n\npublic int Peek()\n// B is a reference assembly, which holds no code.")]
    [InlineData("Top",
        "// Plates.Stack.Top — only B has it.\n\npublic int Top { get; }\n// B is a reference assembly, which holds no code.")]
    [InlineData("Label",
        "// Plates.Stack.Label is a field in A and a property in B: public int Label → public int Label { get; }\n\n" +
        "// B: Plates.Stack.Label\npublic int Label { get; }\n// B is a reference assembly, which holds no code.\n")]
    public async Task Compare_Bodies_OfAMemberOfAReferenceAssemblyAlone_ShowsNoStub(string memberName, string expected)
    {
        var a = CodelessAssembly.Write(CodelessAssembly.Kind.Code, second: false);
        var b = CodelessAssembly.Write(CodelessAssembly.Kind.Marked, second: true);
        try
        {
            Assert.Equal(expected, await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = Stack,
                ["member_name"] = memberName,
            }));
        }
        finally
        {
            CodelessAssembly.TryDelete(a, b);
        }
    }

    // A reference assembly's constructor holds no code that could say what sets a field.
    [Fact]
    public async Task Compare_Bodies_OfAFieldBesideAReferenceAssembly_ReadsNoSetterOfIt()
    {
        var a = CodelessAssembly.Write(CodelessAssembly.Kind.Code, second: false);
        var b = CodelessAssembly.Write(CodelessAssembly.Kind.Marked, second: true);
        try
        {
            Assert.Equal(
                "Plates.Stack.Size is a field, which has no body; in A Plates.Stack..ctor() sets it, and B is a " +
                "reference assembly, which holds no code.",
                await CallToolError("compare", new()
                {
                    ["assembly_a"] = a,
                    ["assembly_b"] = b,
                    ["type_name"] = Stack,
                    ["member_name"] = "Size",
                }));
        }
        finally
        {
            CodelessAssembly.TryDelete(a, b);
        }
    }

    // A copy whose bodies a tool removed without the mark has methods whose rows say nothing of
    // why they have no body.
    [Fact]
    public async Task Compare_Bodies_OfTwoBuildsStrippedOfTheirBodies_SaysNeitherHasABody()
    {
        var a = CodelessAssembly.Write(CodelessAssembly.Kind.Stripped, second: false);
        var b = CodelessAssembly.Write(CodelessAssembly.Kind.Stripped, second: true);
        try
        {
            Assert.Equal("// Plates.Stack.Count() → int — neither assembly has a body for it.",
                await CallToolText("compare", new()
                {
                    ["assembly_a"] = a,
                    ["assembly_b"] = b,
                    ["type_name"] = Stack,
                    ["member_name"] = "Count",
                }));
            Assert.Equal("1 changed type:\n~ Plates.Stack [members]",
                await CallToolText("compare", new() { ["assembly_a"] = a, ["assembly_b"] = b }));
        }
        finally
        {
            CodelessAssembly.TryDelete(a, b);
        }
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

    /// <summary>
    /// Calls an ILens MCP tool, fails the test unless it returned an error, and returns the error's
    /// message without the prefix the error filter puts before it.
    /// </summary>
    private async Task<string> CallToolError(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError, $"Expected an error from '{toolName}', got: {text}");
        const string prefix = "Error: ArgumentException: ";
        Assert.StartsWith(prefix, text);
        return text[prefix.Length..];
    }
}
