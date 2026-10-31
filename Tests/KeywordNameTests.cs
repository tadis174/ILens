using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for names C# reads as keywords: a declaration line of list_members and
/// compare writes a name it declares with the '@' C# needs before a reserved keyword, a tool
/// reads a member's or a parameter's name written with it as C# does, and call_sites names a
/// parameter as list_members writes it. CallShapes' Keywords.cs declares each kind of name once
/// as a reserved keyword, beside parameters named after words that are keywords only in a query
/// or an async method; BaseShift's Keywords.cs renames a parameter to one in BaseShift2; and
/// VerbatimNameAssembly writes a field whose name begins with '@', as F# names one.
/// </summary>
public sealed class KeywordNameTests : IClassFixture<ILensServerFixture>
{
    private const string Keywords = "CallShapes.Keywords";

    private readonly ILensServerFixture _server;
    private readonly string _directory;

    public KeywordNameTests(ILensServerFixture server)
    {
        _server = server;
        _directory = Path.GetDirectoryName(server.AssemblyPath)!;
        foreach (var name in new[] { "CallShapes.dll", "BaseShift.dll", "BaseShift2.dll" })
        {
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fixture not found: {path}. Tests/Fixtures/ is expected to land here via Tests.csproj's " +
                    "ProjectReference.");
        }
    }

    // Each name a line declares takes the '@' where it is a reserved keyword: a parameter, the
    // member's own name, a type parameter in the method's brackets and its where clause. A
    // parameter named from or await takes none, also on an async method, where C# accepts it.
    [Fact]
    public async Task ListMembers_WritesTheAtBeforeEachNameALineDeclaresThatIsAReservedKeyword()
    {
        string text = await CallToolText("list_members", Type("Lever"));

        Assert.Equal(
            new[]
            {
                $"9 members of public class {Keywords}.Lever : IOps:",
                "6 methods:",
                "  public Lever(bool @switch)",
                "  public void Hold<@in>() where @in : class",
                "  public void Set(bool @abstract, int from, int await)",
                "  public void Tune(int @default = 3)",
                "  public Task Wait(int await)",
                "  public void @checked()",
                "1 property:",
                "  public int @fixed { get; set; }",
                "1 field:",
                "  public int @base",
                "1 event:",
                "  public event EventHandler @lock",
                "Not shown: 1 internal or private",
            },
            text.Split('\n'));
    }

    // The methods the compiler gives a delegate name their parameter object, the heading writes a
    // type's own type parameter as the line of a member writes a method's, and an explicit
    // implementation takes the '@' on its own name after the interface.
    [Fact]
    public async Task ListMembers_WritesTheAtInADelegatesMethodsATypesHeadingAndAnExplicitImplementation()
    {
        Assert.Equal(
            new[]
            {
                $"4 members of public delegate {Keywords}.Signal:",
                "4 methods:",
                "  public Signal(object @object, nint method)",
                "  public virtual IAsyncResult BeginInvoke(object sender, int @class, AsyncCallback callback, object @object)",
                "  public virtual void EndInvoke(IAsyncResult result)",
                "  public virtual void Invoke(object sender, int @class)",
            },
            (await CallToolText("list_members", Type("Signal"))).Split('\n'));

        Assert.Equal(
            $"1 member of public class {Keywords}.Crate<@out>:\n1 method:\n  public Crate()",
            await CallToolText("list_members", Type("Crate<>")));

        var explicitImplementation = Type("Lever");
        explicitImplementation["accessibility"] = "all";
        explicitImplementation["name_contains"] = "do";
        Assert.Equal(
            $"1 member of public class {Keywords}.Lever : IOps:\n1 method:\n  private void {Keywords}.IOps.@do()",
            await CallToolText("list_members", explicitImplementation));
    }

    // compare writes its lines as list_members does and compares the members as before: a
    // parameter renamed to a keyword is a changed declaration written with '@', and a field and a
    // method named like keywords that stay as they are do not show.
    [Fact]
    public async Task Compare_WritesALineTheWayListMembersWritesIt()
    {
        var types = Builds();
        types["namespace"] = "BaseShift.Keywords";
        Assert.Equal("1 changed type:\n~ BaseShift.Keywords.Relay [declarations]", await CallToolText("compare", types));

        var members = Builds();
        members["type_name"] = "BaseShift.Keywords.Relay";
        Assert.Equal(
            "1 member change in BaseShift.Keywords.Relay:\n~ public void Send(object obj) → public void Send(object @object)",
            await CallToolText("compare", members));

        members["member_name"] = "Send";
        Assert.Equal(
            "// public void Send(object obj) → public void Send(object @object)\n" +
            "// BaseShift.Keywords.Relay.Send(object) → void — bodies are identical in both assemblies.",
            await CallToolText("compare", members));
    }

    // A name written with the '@' a declaration line writes means the keyword, also after a dot
    // and in the brackets of a method's type parameters; the header names the member in the
    // reference form, which writes a name as it stands.
    [Theory]
    [InlineData("@base", "// CallShapes.Keywords.Lever.base")]
    [InlineData("@fixed", "// CallShapes.Keywords.Lever.fixed")]
    [InlineData("@lock", "// CallShapes.Keywords.Lever.lock")]
    [InlineData("@checked", "// CallShapes.Keywords.Lever.checked() → void")]
    [InlineData("Hold<@in>", "// CallShapes.Keywords.Lever.Hold<in>() → void")]
    [InlineData("@do", "// CallShapes.Keywords.Lever.CallShapes.Keywords.IOps.do() → void")]
    [InlineData("CallShapes.Keywords.IOps.@do", "// CallShapes.Keywords.Lever.CallShapes.Keywords.IOps.do() → void")]
    public async Task Decompile_ReadsAMemberNameWrittenWithTheAtAsCSharpDoes(string memberName, string header)
    {
        var arguments = Type("Lever");
        arguments["member_name"] = memberName;
        Assert.Equal(header, (await CallToolText("decompile", arguments)).Split('\n')[0]);
    }

    [Fact]
    public async Task AnalyzeAndCompare_ReadAMemberNameWrittenWithTheAtAsCSharpDoes()
    {
        var usedBy = Type("Lever");
        usedBy["member_name"] = "@checked";
        usedBy["kind"] = "used_by";
        Assert.Equal(
            $"1 user of {Keywords}.Lever.checked() [declared]:\n{Keywords}.Puller.Pull()",
            await CallToolText("analyze", usedBy));

        var assignedBy = Type("Lever");
        assignedBy["member_name"] = "@base";
        assignedBy["kind"] = "assigned_by";
        Assert.Equal(
            $"1 writer of {Keywords}.Lever.base [declared]:\n{Keywords}.Puller.Pull()",
            await CallToolText("analyze", assignedBy));

        var compare = Builds();
        compare["type_name"] = "BaseShift.Keywords.Relay";
        compare["member_name"] = "@checked";
        Assert.Equal(
            "// BaseShift.Keywords.Relay.checked() → void — bodies are identical in both assemblies.",
            await CallToolText("compare", compare));
    }

    // call_sites takes the parameter's name with or without '@' and names it as list_members
    // writes it, in the header, in the header of non_default_only and in its errors.
    [Theory]
    [InlineData("switch")]
    [InlineData("@switch")]
    public async Task CallSites_TakesTheParameterNameEitherWayAndNamesItAsListMembersWritesIt(string parameterName)
    {
        Assert.Equal(
            $"1 call to {Keywords}.Lever..ctor(bool), argument for @switch:\n{Keywords}.Puller.Pull()\n  true",
            await CallToolText("analyze", CallSites(".ctor", parameterName)));

        var nonDefault = CallSites(".ctor", parameterName);
        nonDefault["non_default_only"] = true;
        Assert.Equal(
            $"Error: ArgumentException: '@switch' has no default value in {Keywords}.Lever..ctor(bool), " +
            "so non_default_only has nothing to compare its arguments with.",
            await CallToolError("analyze", nonDefault));
    }

    [Fact]
    public async Task CallSites_NamesAParameterWithTheAtInTheHeaderOfNonDefaultOnlyAndInTheListOfParameters()
    {
        var nonDefault = CallSites("Tune", "@default");
        nonDefault["non_default_only"] = true;
        Assert.Equal(
            $"1 of 1 call to {Keywords}.Lever.Tune(int) passes @default other than its default 3:\n" +
            $"{Keywords}.Puller.Pull()\n  5",
            await CallToolText("analyze", nonDefault));

        Assert.Equal(
            $"Error: ArgumentException: No overload of 'Set' on {Keywords}.Lever has a parameter named 'nope'. " +
            "Its parameters: @abstract, from, await.",
            await CallToolError("analyze", CallSites("Set", "nope")));
    }

    // An '@' before a word that is no keyword stays part of the name: F# begins the name of a
    // closure's instance field with it, which C# cannot declare.
    [Fact]
    public async Task AnAtBeforeAWordThatIsNoKeyword_StaysPartOfTheName()
    {
        var path = VerbatimNameAssembly.Write();
        try
        {
            Assert.Equal(
                "3 members of public class Verbatim.Closure:\n1 method:\n  public Closure()\n" +
                "2 fields:\n  public static int @_instance\n  public int Count",
                await CallToolText("list_members", new() { ["assembly"] = path, ["type_name"] = "Verbatim.Closure" }));

            string decompiled = await CallToolText("decompile", new()
            {
                ["assembly"] = path,
                ["type_name"] = "Verbatim.Closure",
                ["member_name"] = "@_instance",
            });
            Assert.Equal("// Verbatim.Closure.@_instance", decompiled.Split('\n')[0]);
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    private Dictionary<string, object?> Type(string typeName) => new()
    {
        ["assembly"] = Path.Combine(_directory, "CallShapes.dll"),
        ["type_name"] = $"{Keywords}.{typeName}",
    };

    private Dictionary<string, object?> CallSites(string memberName, string parameterName)
    {
        var arguments = Type("Lever");
        arguments["member_name"] = memberName;
        arguments["kind"] = "call_sites";
        arguments["parameter_name"] = parameterName;
        return arguments;
    }

    private Dictionary<string, object?> Builds() => new()
    {
        ["assembly_a"] = Path.Combine(_directory, "BaseShift.dll"),
        ["assembly_b"] = Path.Combine(_directory, "BaseShift2.dll"),
    };

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }

    private async Task<string> CallToolError(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError, $"Expected an error from '{toolName}', got: {text}");
        return text;
    }
}
