using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for <c>analyze</c> with <c>used_by</c> on an assembly that applies an
/// attribute class with type parameters whose constructor takes a value of its second one,
/// after a first type argument that is a class, a struct, an enum or a constructed type. The
/// decoder of the metadata library fails on such an attribute, so the answers show that it is
/// read without that decoder. The ExternalUser fixture's Generics.cs applies one in each shape,
/// which also puts every other <c>used_by</c> test on a type of that fixture on an assembly
/// that holds them.
/// </summary>
public sealed class GenericAttributeTests : IClassFixture<ILensServerFixture>
{
    private const string Generics = "ExternalUser.Generics";

    private readonly ILensServerFixture _server;
    private readonly string _userPath;

    public GenericAttributeTests(ILensServerFixture server)
    {
        _server = server;
        _userPath = Path.Combine(Path.GetDirectoryName(server.AssemblyPath)!, "ExternalUser.dll");
    }

    // A type and the whole answer to who uses it. The types named System are ones ILens knows
    // only from the fixture's references to them; the others the fixture defines.
    public static TheoryData<string, string> Questions => new()
    {
        // The class each attribute names by typeof after its value.
        {
            $"{Generics}.Topic",
            $"5 users of {Generics}.Topic:\n" + string.Join("\n",
                $"{Generics}.AfterAClass",
                $"{Generics}.AfterAConstructedType",
                $"{Generics}.AfterAConstructedTypeWithAnEnumValue",
                $"{Generics}.AfterAStruct",
                $"{Generics}.AfterAnEnum")
        },
        // The types that stand as the first type argument: a class, also inside a constructed
        // type, a struct and an enum.
        {
            $"{Generics}.Lead",
            $"3 users of {Generics}.Lead:\n" + string.Join("\n",
                $"{Generics}.AfterAClass",
                $"{Generics}.AfterAConstructedTypeWithAnEnumValue",
                $"{Generics}.NamesALibraryClass")
        },
        { $"{Generics}.Stretch", $"1 user of {Generics}.Stretch:\n{Generics}.AfterAStruct" },
        { $"{Generics}.Phase", $"1 user of {Generics}.Phase:\n{Generics}.AfterAnEnum" },
        // The attribute class: everything that carries it.
        {
            $"{Generics}.OrderedAttribute<,>",
            $"6 users of {Generics}.OrderedAttribute<,>:\n" + string.Join("\n",
                $"{Generics}.AfterAClass",
                $"{Generics}.AfterAConstructedType",
                $"{Generics}.AfterAConstructedTypeWithAnEnumValue",
                $"{Generics}.AfterAStruct",
                $"{Generics}.AfterAnEnum",
                $"{Generics}.NamesALibraryClass")
        },
        // A class of another assembly named by typeof after the value.
        { "System.Uri", $"1 user of System.Uri:\n{Generics}.NamesALibraryClass" },
        // An enum of another assembly that is the second type argument, of which the
        // constructor takes a value.
        {
            "System.DateTimeKind",
            $"1 user of System.DateTimeKind:\n{Generics}.AfterAConstructedTypeWithAnEnumValue"
        },
    };

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task Analyze_UsedBy_AnswersWhereAnAttributeTakesALaterTypeParameterAfterAClassStructEnumOrConstructedTypeArgument(
        string typeName, string expected)
    {
        CallToolResult result = await _server.Client.CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["assembly"] = _userPath,
            ["type_name"] = typeName,
            ["kind"] = "used_by",
        });
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        Assert.True(result.IsError != true, $"analyze returned an error: {text}");
        Assert.Equal(expected, text);
    }
}
