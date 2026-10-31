using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for <c>analyze</c> with <c>used_by</c> on a type the assembly defines, where
/// an attribute on a type or member uses it. The ExternalUser fixture's Labels.cs applies one
/// attribute class of its own in each way an attribute can use a type: as its own class, by a
/// value of an enum, and by a typeof on its own, in an array, of a type built from the type, and
/// beside a value of an enum whose underlying type is not in the assembly.
/// </summary>
public sealed class AttributeUseTests : IClassFixture<ILensServerFixture>
{
    private const string Labels = "ExternalUser.Labels";

    // Everything the fixture applies LabelAttribute to, as an answer lists it.
    private static readonly string[] Labeled =
    [
        $"{Labels}.OtherEnumAlone",
        $"{Labels}.OtherEnumAlone.Method()",
        $"{Labels}.RankAsObject",
        $"{Labels}.RankFixed",
        $"{Labels}.RankInArray",
        $"{Labels}.RankNamed",
        $"{Labels}.RankOnMembers.Event",
        $"{Labels}.RankOnMembers.Field",
        $"{Labels}.RankOnMembers.Method()",
        $"{Labels}.RankOnMembers.Parameter(int)",
        $"{Labels}.RankOnMembers.Property",
        $"{Labels}.RankOnMembers.Return()",
        $"{Labels}.RankOnMembers.TypeParameter<T>()",
        $"{Labels}.RankOnTypeParameter<>",
        $"{Labels}.Subject.Own()",
        $"{Labels}.TypeofAlone",
        $"{Labels}.TypeofAsObject",
        $"{Labels}.TypeofAsTypeArgument",
        $"{Labels}.TypeofBesideFrameworkEnum",
        $"{Labels}.TypeofBesideOtherEnum",
        $"{Labels}.TypeofInArray",
        $"{Labels}.TypeofInNamedArray",
        $"{Labels}.TypeofOnMethods.Alone()",
        $"{Labels}.TypeofOnMethods.AsTypeArgument()",
        $"{Labels}.TypeofOnMethods.BesideOtherEnum()",
        $"{Labels}.TypeofOnMethods.InArray()",
    ];

    private readonly ILensServerFixture _server;
    private readonly string _userPath;

    public AttributeUseTests(ILensServerFixture server)
    {
        _server = server;
        _userPath = Path.Combine(Path.GetDirectoryName(server.AssemblyPath)!, "ExternalUser.dll");
    }

    // A type of the fixture, the kind asked about it, and the whole answer.
    public static TheoryData<string, string, string> Questions => new()
    {
        // An enum: the three members whose signatures name it, then everything that carries an
        // attribute given a value of it. The value is a fixed argument, a named one, one passed
        // as an object and an element of an array; it sits on a class, a field, a property, an
        // event, a method, a parameter, a return value and a type parameter of a method and of
        // a class.
        {
            $"{Labels}.Rank", "used_by",
            $"15 users of {Labels}.Rank:\n" + string.Join("\n",
                $"{Labels}.LabelAttribute..ctor(Rank)",
                $"{Labels}.LabelAttribute..ctor(Rank[])",
                $"{Labels}.LabelAttribute.Named",
                $"{Labels}.RankAsObject",
                $"{Labels}.RankFixed",
                $"{Labels}.RankInArray",
                $"{Labels}.RankNamed",
                $"{Labels}.RankOnMembers.Event",
                $"{Labels}.RankOnMembers.Field",
                $"{Labels}.RankOnMembers.Method()",
                $"{Labels}.RankOnMembers.Parameter(int)",
                $"{Labels}.RankOnMembers.Property",
                $"{Labels}.RankOnMembers.Return()",
                $"{Labels}.RankOnMembers.TypeParameter<T>()",
                $"{Labels}.RankOnTypeParameter<>")
        },
        // A class named by typeof: on its own, as an object, in an array given as a fixed and as
        // a named argument, as a type argument, and beside a value of an enum of ExternalLib and
        // of the framework, on a class and on a method. Its own method Own, whose attribute
        // names it too, is not one of its users.
        {
            $"{Labels}.Subject", "used_by",
            $"11 users of {Labels}.Subject:\n" + string.Join("\n",
                $"{Labels}.TypeofAlone",
                $"{Labels}.TypeofAsObject",
                $"{Labels}.TypeofAsTypeArgument",
                $"{Labels}.TypeofBesideFrameworkEnum",
                $"{Labels}.TypeofBesideOtherEnum",
                $"{Labels}.TypeofInArray",
                $"{Labels}.TypeofInNamedArray",
                $"{Labels}.TypeofOnMethods.Alone()",
                $"{Labels}.TypeofOnMethods.AsTypeArgument()",
                $"{Labels}.TypeofOnMethods.BesideOtherEnum()",
                $"{Labels}.TypeofOnMethods.InArray()")
        },
        // The attribute class: every place it is applied uses it, also with a value of an enum
        // another assembly defines, so who uses it and where it is applied list the same.
        {
            $"{Labels}.LabelAttribute", "used_by",
            $"26 users of {Labels}.LabelAttribute:\n" + string.Join("\n", Labeled)
        },
        {
            $"{Labels}.LabelAttribute", "applied_to",
            $"26 symbols marked with {Labels}.LabelAttribute:\n" + string.Join("\n", Labeled)
        },
    };

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task Analyze_OnATypeAnAttributeUses_ListsWhatCarriesTheAttribute(
        string typeName, string kind, string expected)
    {
        CallToolResult result = await _server.Client.CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["assembly"] = _userPath,
            ["type_name"] = typeName,
            ["kind"] = kind,
        });
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        Assert.True(result.IsError != true, $"analyze returned an error: {text}");
        Assert.Equal(expected, text);
    }
}
