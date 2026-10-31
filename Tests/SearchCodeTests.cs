using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for search_code, the search over the decompiled text of a type, a namespace
/// or a whole assembly. The SearchText fixture holds one known line in each place the search has
/// to find or leave out; most of its classes call <c>Marker.Hit</c>, so one pattern finds a line
/// in each of them. The expected lines are the decompiler's, which differ from the fixture's source
/// where it renames a caught exception or writes a lambda as a delegate.
/// </summary>
public sealed class SearchCodeTests : IClassFixture<ILensServerFixture>
{
    private const string Marker = @"Marker\.Hit\(";

    private readonly ILensServerFixture _server;

    public SearchCodeTests(ILensServerFixture server)
    {
        _server = server;
    }

    // The whole answer for the whole assembly: the members sorted by name, each once, with its
    // lines beneath it. The lambdas, the local function, the iterator and the async method stand
    // under the method each was written in, both accessors under their property, and the two
    // classes the compiler-generated filter counts as generated are left out.
    [Fact]
    public async Task WholeAssembly_ListsEachMemberOnceWithItsLinesBeneathIt()
    {
        string text = await Search(Marker);

        Assert.Equal(
            "13 lines matching Marker\\.Hit\\(:\n" +
            "Catalog.Index.Build()\n" +
            "  Marker.Hit(\"index\");\n" +
            "SearchText.Outer.Inner.Run()\n" +
            "  Marker.Hit(\"inner\");\n" +
            "SearchText.Outer.Run()\n" +
            "  Marker.Hit(\"outer\");\n" +
            "SearchText.Reader.Load(string)\n" +
            "  Marker.Hit(ex2.Message);\n" +
            "SearchText.Settings.IsSet()\n" +
            "  Marker.Hit(\"return attribute\");\n" +
            "SearchText.Settings.Level\n" +
            "  Marker.Hit(\"getter\");\n" +
            "  Marker.Hit(\"setter\");\n" +
            "SearchText.Shapes.InAsyncMethod()\n" +
            "  Marker.Hit(\"async method\");\n" +
            "SearchText.Shapes.InCapturingLambda(string)\n" +
            "  Marker.Hit(where + \" captured\");\n" +
            "SearchText.Shapes.InIterator()\n" +
            "  Marker.Hit(\"iterator\");\n" +
            "SearchText.Shapes.InLambda()\n" +
            "  Marker.Hit(\"lambda\");\n" +
            "SearchText.Shapes.InLocalFunction()\n" +
            "  Marker.Hit(\"local function\");\n" +
            "SearchText.Storage.Shelf.Fill()\n" +
            "  Marker.Hit(\"shelf\");",
            text);
    }

    // A type covers the types nested in it, and a nested type only itself. The header names the
    // scope: the type as ILens prints it.
    [Theory]
    [InlineData("SearchText.Reader", @"catch \(",
        "3 lines matching catch \\( in SearchText.Reader:\n" +
        "SearchText.Reader.Load(string)\n" +
        "  catch (IOException)\n" +
        "  catch (Exception ex2)\n" +
        "SearchText.Reader.TryParse(string, out int)\n" +
        "  catch (FormatException)")]
    [InlineData("SearchText.Outer", Marker,
        "2 lines matching Marker\\.Hit\\( in SearchText.Outer:\n" +
        "SearchText.Outer.Inner.Run()\n" +
        "  Marker.Hit(\"inner\");\n" +
        "SearchText.Outer.Run()\n" +
        "  Marker.Hit(\"outer\");")]
    [InlineData("SearchText.Outer.Inner", Marker,
        "1 line matching Marker\\.Hit\\( in SearchText.Outer.Inner:\n" +
        "SearchText.Outer.Inner.Run()\n" +
        "  Marker.Hit(\"inner\");")]
    public async Task TypeScope_CoversTheTypeAndWhatIsNestedInIt(string typeName, string regex, string expected)
    {
        Assert.Equal(expected, await Search(regex, new() { ["type_name"] = typeName }));
    }

    // A namespace is exact: SearchText leaves out SearchText.Storage, and the other namespace.
    [Fact]
    public async Task NamespaceScope_LeavesOutSubNamespaces()
    {
        string parent = await Search(Marker, new() { ["namespace"] = "SearchText" });
        string child = await Search(Marker, new() { ["namespace"] = "SearchText.Storage" });

        Assert.StartsWith("11 lines matching Marker\\.Hit\\( in namespace SearchText:\n", parent);
        Assert.DoesNotContain("Shelf", parent);
        Assert.DoesNotContain("Catalog", parent);
        Assert.Equal(
            "1 line matching Marker\\.Hit\\( in namespace SearchText.Storage:\n" +
            "SearchText.Storage.Shelf.Fill()\n" +
            "  Marker.Hit(\"shelf\");",
            child);
    }

    // What stands outside method bodies is searched as well: a field's initial value under the
    // field, an attribute under its member, the class's own attribute and header line under the
    // class. IsSet carries an attribute on its return value, a declaration the decompiler's
    // writer hands back without a start line; its attribute line still stands under it.
    [Theory]
    [InlineData("= 3;",
        "1 line matching = 3; in SearchText.Settings:\n" +
        "SearchText.Settings.Retries\n" +
        "  public int Retries = 3;")]
    [InlineData(@"\[Serializable\]|: IDisposable|\[Obsolete|\[return",
        "4 lines matching \\[Serializable\\]|: IDisposable|\\[Obsolete|\\[return in SearchText.Settings:\n" +
        "SearchText.Settings\n" +
        "  [Serializable]\n" +
        "  public class Settings : IDisposable\n" +
        "SearchText.Settings.IsSet()\n" +
        "  [return: MarshalAs(UnmanagedType.Bool)]\n" +
        "SearchText.Settings.OldLevel()\n" +
        "  [Obsolete(\"Use Level.\")]")]
    public async Task LinesOutsideBodies_StandUnderTheMemberOrTypeTheyBelongTo(string regex, string expected)
    {
        Assert.Equal(expected, await Search(regex, new() { ["type_name"] = "SearchText.Settings" }));
    }

    // Where an XML documentation file lies beside the assembly, the decompiler writes a comment
    // above each documented member, inside its class. The class's attribute and header line still
    // stand under the class, and the nested class's under it, as they do without the file, which
    // the test writes beside a copy of the fixture. The search finds a line of the documentation,
    // so its text holds the comments.
    [Fact]
    public async Task HeaderLines_StandUnderTheirClass_AlsoWhereItsMembersAreDocumented()
    {
        const string regex = @"\[Serializable\]|: IDisposable|: IComparable";
        const string expected =
            "4 lines matching \\[Serializable\\]|: IDisposable|: IComparable in SearchText.Documented:\n" +
            "SearchText.Documented\n" +
            "  [Serializable]\n" +
            "  public class Documented : IDisposable\n" +
            "SearchText.Documented.Part\n" +
            "  [Serializable]\n" +
            "  public class Part : IComparable";
        Assert.Equal(expected, await Search(regex, new() { ["type_name"] = "SearchText.Documented" }));

        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ILens-search-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string copy = Path.Combine(root, "SearchText.dll");
            File.Copy(FixturePath("SearchText.dll"), copy);
            File.WriteAllText(Path.Combine(root, "SearchText.xml"), """
                <?xml version="1.0"?>
                <doc>
                  <assembly><name>SearchText</name></assembly>
                  <members>
                    <member name="M:SearchText.Documented.Dispose"><summary>Releases nothing.</summary></member>
                    <member name="T:SearchText.Documented.Part"><summary>A part.</summary></member>
                    <member name="M:SearchText.Documented.Part.CompareTo(System.Object)"><summary>Compares nothing.</summary></member>
                  </members>
                </doc>
                """);

            await using var client = await StartServer("ILens-search-documented", root);
            var arguments = new Dictionary<string, object?> { ["assembly"] = copy, ["type_name"] = "SearchText.Documented" };
            Assert.StartsWith(
                "1 line matching Releases nothing in SearchText.Documented:\n",
                await CallToolText(client, "search_code", new(arguments) { ["regex"] = "Releases nothing" }));
            Assert.Equal(expected, await CallToolText(client, "search_code", new(arguments) { ["regex"] = regex }));
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    // The using lines and the namespace line belong to no type. Outer's text opens with
    // "using SearchText.Storage;" and Shelf's with "namespace SearchText.Storage;", and neither
    // is found.
    [Theory]
    [InlineData("^using |^namespace ")]
    [InlineData(@"SearchText\.Storage")]
    public async Task UsingAndNamespaceLines_AreNotSearched(string regex)
    {
        Assert.Equal($"No lines matching {regex}", await Search(regex));
    }

    // A line found is a line of what decompile answers for the type.
    [Fact]
    public async Task MatchingLines_AreLinesOfTheDecompiledType()
    {
        string found = await Search(Marker, new() { ["type_name"] = "SearchText.Shapes" });
        string decompiled = await CallToolText(_server.Client, "decompile", new()
        {
            ["assembly"] = FixturePath("SearchText.dll"),
            ["type_name"] = "SearchText.Shapes",
        });

        var lines = found.Split('\n').Where(line => line.StartsWith("  ")).Select(line => line.Trim()).ToList();
        Assert.Equal(5, lines.Count);
        Assert.All(lines, line => Assert.Contains(line, decompiled));
    }

    // The pattern is case-sensitive unless it says otherwise. The limit counts matching lines,
    // and a member cut by it keeps the lines that were still counted.
    [Fact]
    public async Task Pattern_IsCaseSensitive_AndTheLimitCountsLines()
    {
        Assert.Equal(@"No lines matching marker\.hit", await Search(@"marker\.hit"));

        string cut = await Search(@"(?i)marker\.hit", new() { ["limit"] = 6 });
        Assert.StartsWith("13 lines matching (?i)marker\\.hit:\nCatalog.Index.Build()\n", cut);
        Assert.EndsWith(
            "SearchText.Settings.Level\n" +
            "  Marker.Hit(\"getter\");\n" +
            "... (7 more; raise limit to see all)",
            cut);
    }

    // The decompiler prints Builder's object creation over five lines. A pattern sees one line
    // at a time, so neither whitespace nor a negated class in it reaches into the next line.
    [Fact]
    public async Task Pattern_SeesOneLineAtATime()
    {
        string across = @"Options\s*\{|Name[^;]*Size";
        Assert.Equal($"No lines matching {across}", await Search(across));

        Assert.Equal(
            "2 lines matching new Options|Size = 2:\n" +
            "SearchText.Builder.Make()\n" +
            "  return new Options\n" +
            "  Size = 2",
            await Search("new Options|Size = 2"));
    }

    // Without the switch a generated class is searched only when type_name names it: Registry is
    // a top-level class marked as generated, Outer.Hidden a nested one. With the switch both are
    // part of every scope that holds them.
    [Fact]
    public async Task GeneratedClasses_AreSearchedWithTheSwitchOrWhenNamed()
    {
        string all = await Search(Marker, new() { ["include_compiler_generated"] = true });
        Assert.StartsWith("15 lines matching ", all);
        Assert.Contains("SearchText.Outer.Hidden.Run()\n  Marker.Hit(\"hidden\");", all);
        Assert.Contains("SearchText.Registry.Run()\n  Marker.Hit(\"registry\");", all);

        Assert.Equal(
            "1 line matching Marker\\.Hit\\( in SearchText.Registry:\n" +
            "SearchText.Registry.Run()\n" +
            "  Marker.Hit(\"registry\");",
            await Search(Marker, new() { ["type_name"] = "SearchText.Registry" }));
        Assert.Equal(
            "1 line matching Marker\\.Hit\\( in SearchText.Outer.Hidden:\n" +
            "SearchText.Outer.Hidden.Run()\n" +
            "  Marker.Hit(\"hidden\");",
            await Search(Marker, new() { ["type_name"] = "SearchText.Outer.Hidden" }));
        Assert.StartsWith(
            "3 lines matching ",
            await Search(Marker, new() { ["type_name"] = "SearchText.Outer", ["include_compiler_generated"] = true }));
    }

    // A pattern with a lookbehind cannot run on the engine that answers in linear time and is
    // answered by the other one, with the same lines. A pattern that runs away on that engine,
    // here on Runaway's long method name, ends as an error naming the member, and the same
    // pattern without its lookahead is answered, since the linear engine cannot run away.
    [Fact]
    public async Task Lookarounds_AreAnswered_AndAPatternThatRunsAwayIsAnError()
    {
        var catalog = new Dictionary<string, object?> { ["namespace"] = "Catalog" };
        string plain = await Search(Marker, new(catalog));
        string lookbehind = await Search(@"(?<=Marker\.)Hit\(", new(catalog));
        Assert.Equal(Body(plain), Body(lookbehind));
        Assert.Contains("Catalog.Index.Build()", lookbehind);

        var (error, isError) = await CallTool(_server.Client, "search_code", Arguments(@"(?=.)(\w+)+!"));
        Assert.True(isError, error);
        Assert.Contains("took longer than 1 s on one line of SearchText.Runaway.", error);

        Assert.Equal(@"No lines matching (\w+)+!", await Search(@"(\w+)+!"));

        static string Body(string answer) => answer[(answer.IndexOf('\n') + 1)..];
    }

    [Theory]
    [InlineData(Marker, "SearchText.Reader", "SearchText", "type_name and namespace are two scopes")]
    [InlineData(Marker, "SearchText.Missing", null, "Type not found: SearchText.Missing.")]
    [InlineData(Marker, null, "Missing", "Namespace not found: Missing.")]
    [InlineData("", null, null, "regex is empty.")]
    [InlineData("(", null, null, "Invalid pattern '(' at offset 1.")]
    public async Task AQuestionThatCannotBeAnswered_IsAnError(
        string regex, string? typeName, string? @namespace, string expected)
    {
        var arguments = Arguments(regex);
        if (typeName is not null)
            arguments["type_name"] = typeName;
        if (@namespace is not null)
            arguments["namespace"] = @namespace;

        var (text, isError) = await CallTool(_server.Client, "search_code", arguments);

        Assert.True(isError, text);
        Assert.Contains(expected, text);
    }

    // With a time limit of nothing a call decompiles the least it can, so the first call over
    // the fixture's twelve classes cannot finish. Every call gets further than the one before,
    // and the last answers what the shared server answers, which has its default limit.
    [Fact]
    public async Task ACallThatRunsOutOfTime_SaysHowFarItCame_AndRepeatingItContinues()
    {
        string binDir = Path.GetDirectoryName(_server.AssemblyPath)!;
        await using var client = await StartServer("ILens-search-limit", binDir, "--max-search-seconds", "0");

        var states = new List<int>();
        string answer = "";
        for (int call = 0; call < 20; call++)
        {
            answer = await CallToolText(client, "search_code", Arguments(Marker));
            var state = Regex.Match(answer,
                @"^Not finished: (\d+) of 12 types decompiled\. Repeat the call to continue; what is done is kept\.$");
            if (!state.Success)
                break;
            states.Add(int.Parse(state.Groups[1].Value));
        }

        Assert.NotEmpty(states);
        Assert.Equal(states.Order().Distinct(), states);
        Assert.Equal(await Search(Marker), answer);
    }

    // The kept text has a quarter of the memory budget, 1 MB of 4 MB here. The whole of the
    // inspection target needs more, which is an error that says so. A namespace of it fits and
    // is answered afterwards, for which the types kept from the search that failed give way.
    [Fact]
    public async Task AScopeWhoseTextDoesNotFitItsShareOfTheBudget_IsAnError_AndANarrowerOneIsAnswered()
    {
        string binDir = Path.GetDirectoryName(_server.AssemblyPath)!;
        await using var client = await StartServer("ILens-search-share", binDir, "--max-total-size", "4");

        var whole = new Dictionary<string, object?> { ["assembly"] = _server.AssemblyPath, ["regex"] = @"catch \(" };
        var (error, isError) = await CallTool(client, "search_code", whole);
        Assert.True(isError, error);
        Assert.Contains("needs more than the 1 MB kept for searches", error);
        Assert.Contains("--max-total-size", error);

        string narrower = await CallToolText(client, "search_code",
            new(whole) { ["namespace"] = "ICSharpCode.Decompiler.CSharp.Syntax" });
        Assert.Matches(@"^\d+ lines matching catch \\\( in namespace ICSharpCode\.Decompiler\.CSharp\.Syntax:\n", narrower);

        Assert.StartsWith("13 lines matching ", await CallToolText(client, "search_code", Arguments(Marker)));
    }

    // A text is kept for the file it was decompiled from. Under a 1 MB budget every host is
    // closed when its call ends, which frees the file to be replaced. The next search has to
    // answer for the new file, in which nothing calls Marker.Hit.
    [Fact]
    public async Task AFileThatChanged_IsDecompiledAnew()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ILens-search-" + Guid.NewGuid().ToString("N"))).FullName;
        string target = Path.Combine(root, "Target.dll");
        File.Copy(FixturePath("SearchText.dll"), target);

        try
        {
            await using (var client = await StartServer("ILens-search-changed", root, "--max-total-size", "1"))
            {
                var arguments = new Dictionary<string, object?> { ["assembly"] = target, ["regex"] = Marker };
                Assert.StartsWith("13 lines matching ", await CallToolText(client, "search_code", arguments));

                File.Copy(FixturePath("CallShapes.dll"), target, overwrite: true);

                Assert.Equal(@"No lines matching Marker\.Hit\(", await CallToolText(client, "search_code", arguments));
            }
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    // The server refuses to start on a time limit it cannot read, with a line that says what
    // the option takes.
    [Theory]
    [InlineData(new[] { "--max-search-seconds" }, "--max-search-seconds requires a number of seconds")]
    [InlineData(new[] { "--max-search-seconds", "soon" }, "--max-search-seconds must be a non-negative integer (seconds): soon")]
    [InlineData(new[] { "--max-search-seconds", "-1" }, "--max-search-seconds must be a non-negative integer (seconds): -1")]
    public async Task ATimeLimitTheServerCannotRead_EndsItAtStartup(string[] arguments, string expected)
    {
        var start = new ProcessStartInfo(ILensServerFixture.ResolveILensExecutable())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        string errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(1, process.ExitCode);
        Assert.Contains(expected, errors);
    }

    private Dictionary<string, object?> Arguments(string regex) =>
        new() { ["assembly"] = FixturePath("SearchText.dll"), ["regex"] = regex };

    /// <summary>The shared server's answer to a search of the fixture, with <paramref name="more"/> as further arguments.</summary>
    private async Task<string> Search(string regex, Dictionary<string, object?>? more = null)
    {
        var arguments = Arguments(regex);
        foreach (var (name, value) in more ?? [])
            arguments[name] = value;
        return await CallToolText(_server.Client, "search_code", arguments);
    }

    /// <summary>
    /// The path of a fixture assembly, which Tests.csproj's ProjectReference puts beside the
    /// shared inspection target.
    /// </summary>
    private string FixturePath(string fileName)
    {
        string path = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Fixture not found: {path}. Its project under Tests/Fixtures/ is expected to land here via " +
                "Tests.csproj's ProjectReference.");
        return path;
    }

    private static async Task<McpClient> StartServer(string name, string root, params string[] options) =>
        await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = ILensServerFixture.ResolveILensExecutable(),
            Arguments = ["--allow-root", root, .. options],
        }));

    private static async Task<(string Text, bool IsError)> CallTool(
        McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return (text, result.IsError == true);
    }

    private static async Task<string> CallToolText(
        McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        var (text, isError) = await CallTool(client, toolName, arguments);
        Assert.False(isError, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
