using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for classes whose own name holds angle brackets, as C++/CLI names the class of
/// a template instance: each tool that takes a type name finds such a class by the name find_types
/// lists it under, a type pattern matches it by its full name, a name without the namespace gets
/// an error that names it, and a generic class of the name before the brackets stays reachable by
/// its own forms. TemplateNameAssembly writes the classes, since no C# compiler can name one so.
/// </summary>
public sealed class TemplateNameTests : IClassFixture<ILensServerFixture>
{
    private const string GuidHandle = "Native.Wrappers.Handle<_GUID>";

    private readonly ILensServerFixture _server;

    public TemplateNameTests(ILensServerFixture server) => _server = server;

    // Every name find_types prints is a name list_members takes.
    [Fact]
    public async Task EachClassFindTypesLists_IsFoundByThatName()
    {
        var path = TemplateNameAssembly.Write();
        try
        {
            string listed = await CallToolText("find_types", new() { ["assembly"] = path });
            Assert.Equal(
                "7 types:\nNative.Wrappers.Handle<>\nNative.Wrappers.Handle<>.Inner\nNative.Wrappers.Handle<Native::Font *>\n" +
                "Native.Wrappers.Handle<_GUID>\nNative.Wrappers.Handle<_GUID>.Inner\nNative.Wrappers.User\ngcroot<System::String ^>",
                listed);

            var headers = new List<string>();
            foreach (var name in listed.Split('\n').Skip(1))
                headers.Add((await CallToolText("list_members", new() { ["assembly"] = path, ["type_name"] = name }))
                    .Split('\n')[0]);
            Assert.Equal(
                [
                    "3 members of public class Native.Wrappers.Handle<T>:",
                    "1 member of public class Native.Wrappers.Handle<T>.Inner:",
                    "1 member of public class Native.Wrappers.Handle<Native::Font *>:",
                    "4 members of public class Native.Wrappers.Handle<_GUID>:",
                    "1 member of public class Native.Wrappers.Handle<_GUID>.Inner:",
                    "5 members of public class Native.Wrappers.User:",
                    "1 member of public class gcroot<System::String ^>:",
                ],
                headers);
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    [Fact]
    public async Task TheToolsThatReadCode_FindTheClassByItsName()
    {
        var path = TemplateNameAssembly.Write();
        try
        {
            string code = await CallToolText("decompile", new() { ["assembly"] = path, ["type_name"] = GuidHandle });
            Assert.Contains("public class Handle<_GUID>", code);
            Assert.Contains("public nint Release()", code);

            Assert.Equal(
                "2 methods:\nNative.Wrappers.Handle<_GUID>..ctor() → void\nNative.Wrappers.Handle<_GUID>.Release() → nint",
                await CallToolText("find_methods", new() { ["assembly"] = path, ["type_name"] = GuidHandle }));

            Assert.Equal(
                "2 lines matching Value in Native.Wrappers.Handle<_GUID>:\nNative.Wrappers.Handle<_GUID>.Release()\n" +
                "  return Value;\nNative.Wrappers.Handle<_GUID>.Value\n  public nint Value;",
                await CallToolText("search_code", new() { ["assembly"] = path, ["type_name"] = GuidHandle, ["regex"] = "Value" }));

            // The assembly defines the class, so analyze lists the members that use it.
            Assert.Equal(
                "2 users of Native.Wrappers.Handle<_GUID>:\nNative.Wrappers.User.Current\nNative.Wrappers.User.Take(Handle<_GUID>)",
                await CallToolText("analyze", new() { ["assembly"] = path, ["type_name"] = GuidHandle, ["kind"] = "used_by" }));
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    [Fact]
    public async Task Compare_FindsTheClassByItsName()
    {
        var (a, b) = (TemplateNameAssembly.Write(), TemplateNameAssembly.Write(changed: true));
        try
        {
            Assert.Equal(
                "1 member change in Native.Wrappers.Handle<_GUID>:\n+ public void Reset()",
                await CallToolText("compare", new() { ["assembly_a"] = a, ["assembly_b"] = b, ["type_name"] = GuidHandle }));
        }
        finally
        {
            BurstShapedAssembly.TryDelete(a, b);
        }
    }

    // A name a class bears as it stands comes first, so Handle<_GUID> is the class, while the forms
    // of the generic class, with an argument the class does not bear among them, still name it.
    [Fact]
    public async Task AGenericClassOfTheNameBeforeTheBrackets_StaysReachableByItsOwnForms()
    {
        var path = TemplateNameAssembly.Write();
        try
        {
            var answers = new List<string>();
            foreach (var name in new[] { "Native.Wrappers.Handle<T>", "Native.Wrappers.Handle<>", "Native.Wrappers.Handle`1", "Native.Wrappers.Handle<int>" })
                answers.Add(await CallToolText("list_members", new() { ["assembly"] = path, ["type_name"] = name }));
            Assert.All(answers, answer => Assert.Equal(
                "3 members of public class Native.Wrappers.Handle<T>:\n1 method:\n  public Handle()\n1 field:\n  public T Item\n" +
                "1 nested type:\n  Native.Wrappers.Handle<>.Inner",
                answer));
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    [Fact]
    public async Task AClassNestedInOne_IsFoundInEitherForm()
    {
        var path = TemplateNameAssembly.Write();
        try
        {
            string dotted = await CallToolText("list_members", new() { ["assembly"] = path, ["type_name"] = $"{GuidHandle}.Inner" });
            Assert.Equal("1 member of public class Native.Wrappers.Handle<_GUID>.Inner:\n1 method:\n  public Inner()", dotted);
            Assert.Equal(dotted, await CallToolText("list_members", new() { ["assembly"] = path, ["type_name"] = $"{GuidHandle}+Inner" }));
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    [Fact]
    public async Task ANameWithoutItsNamespace_GetsAnErrorThatNamesTheClass()
    {
        var path = TemplateNameAssembly.Write();
        try
        {
            var (result, text) = await CallToolExpectingError("list_members", new()
            {
                ["assembly"] = path,
                ["type_name"] = "Handle<_GUID>",
            });
            Assert.True(result.IsError);
            Assert.Equal(
                "Error: ArgumentException: Type not found: Handle<_GUID>. Types named Handle<_GUID>: Native.Wrappers.Handle<_GUID>.",
                text);
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    // A wrong namespace before a nested class still gets the one class the name ends, whether the
    // class it is nested in is generic or bears brackets in its name.
    [Theory]
    [InlineData("Wrong.Handle<>.Inner", "Native.Wrappers.Handle<>.Inner")]
    [InlineData("Wrong.Handle<_GUID>.Inner", "Native.Wrappers.Handle<_GUID>.Inner")]
    public async Task ANestedNameInAWrongNamespace_GetsAnErrorThatNamesTheClassItEnds(string asked, string meant)
    {
        var path = TemplateNameAssembly.Write();
        try
        {
            var (result, text) = await CallToolExpectingError("list_members", new()
            {
                ["assembly"] = path,
                ["type_name"] = asked,
            });
            Assert.True(result.IsError);
            Assert.Equal($"Error: ArgumentException: Type not found: {asked}. Types named Inner: {meant}.", text);
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    // An array of a class the assembly names nowhere is read from the library that defines it, as
    // an array of any other class of that library is. The server holds the library it loads, so
    // the test starts one of its own, which lets go of the folder when it ends.
    [Fact]
    public async Task ExtensionMethods_ForAnArrayOfTheClass_ReadTheClassFromItsLibrary()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ILens-template-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string extender = TemplateNameAssembly.WriteWithExtender(root);
            await using var client = await ILensServerFixture.ConnectAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-template-extender",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", root],
            }));
            Assert.Equal(
                "1 extension method for Native.Wrappers.Handle<_GUID>[]:\nExtending.Extensions.Count<T>(T[])",
                await CallToolText(client, "analyze", new()
                {
                    ["assembly"] = extender,
                    ["type_name"] = $"{GuidHandle}[]",
                    ["kind"] = "extension_methods",
                }));
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    // A pattern names a type by its full name as well as by its short one, and a hint is a filter,
    // so Take is reached only where its parameter's pattern fits the class.
    [Fact]
    public async Task ATypePattern_MatchesTheClassByItsFullName()
    {
        var path = TemplateNameAssembly.Write();
        try
        {
            Dictionary<string, object?> User(string parameter, string pattern) => new()
            {
                ["assembly"] = path,
                ["type_name"] = "Native.Wrappers.User",
                [parameter] = parameter == "parameter_types" ? new[] { pattern } : pattern,
            };
            const string take = "1 method:\nNative.Wrappers.User.Take(Handle<_GUID>) → void";
            Assert.Equal(take, await CallToolText("find_methods", User("parameter_types", GuidHandle)));
            Assert.Equal(take, await CallToolText("find_methods", User("parameter_types", "Wrappers.Handle<_GUID>")));
            Assert.Equal(
                "1 method:\nNative.Wrappers.User.Keep(Handle<int>) → void",
                await CallToolText("find_methods", User("parameter_types", "Handle<int>")));
            Assert.Equal(
                "1 method:\nNative.Wrappers.User.Make() → Handle<Native::Font *>",
                await CallToolText("find_methods", User("return_type", "Native.Wrappers.Handle<Native::Font *>")));

            string code = await CallToolText("decompile", new()
            {
                ["assembly"] = path,
                ["type_name"] = "Native.Wrappers.User",
                ["member_name"] = "Take",
                ["parameter_types"] = new[] { GuidHandle },
            });
            Assert.Equal("// Native.Wrappers.User.Take(Handle<_GUID>) → void", code.Split('\n')[0]);
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    // A typeof argument holds a type's name as text, with no reference row, and analyze matches
    // what such an argument names by the same rule as a name it looks up.
    [Fact]
    public async Task UsedBy_FindsTheClassWhereOnlyATypeofArgumentNamesIt()
    {
        var path = TemplateNameAssembly.WriteNamer();
        try
        {
            Assert.Equal(
                "1 user of Native.Wrappers.Handle<_GUID>:\nNamers.Tagged",
                await CallToolText("analyze", new() { ["assembly"] = path, ["type_name"] = GuidHandle, ["kind"] = "used_by" }));
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    private Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments) =>
        CallToolText(_server.Client, toolName, arguments);

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

    /// <summary>
    /// Calls an ILens MCP tool that is expected to fail, returning the raw result alongside
    /// its text so a test can assert on both.
    /// </summary>
    private async Task<(CallToolResult Result, string Text)> CallToolExpectingError(
        string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return (result, text);
    }
}
