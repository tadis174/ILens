using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for <c>analyze</c> answers that name the assembly or the module. An
/// attribute written <c>[assembly: …]</c> or <c>[module: …]</c> belongs to no type or member, and
/// the answer names the one that carries it. The ExternalUser fixture's Global.cs applies an
/// attribute ExternalLib defines and one of its own to each of the two, every one naming a class
/// nothing else names.
/// </summary>
public sealed class GlobalAttributeTests : IClassFixture<ILensServerFixture>
{
    private const string OnAssembly = "assembly ExternalUser";
    private const string OnModule = "module ExternalUser.dll";

    private readonly ILensServerFixture _server;
    private readonly string _userPath;

    public GlobalAttributeTests(ILensServerFixture server)
    {
        _server = server;
        _userPath = Path.Combine(Path.GetDirectoryName(server.AssemblyPath)!, "ExternalUser.dll");
    }

    // A type, the kind asked about it, and the whole answer. The types named ExternalLib are ones
    // ILens knows only from the fixture's references to them; the others the fixture defines.
    public static TheoryData<string, string, string> Questions => new()
    {
        {
            "ExternalLib.ScopeAttribute", "applied_to",
            $"2 symbols marked with ExternalLib.ScopeAttribute:\n{OnAssembly}\n{OnModule}"
        },
        // Named only by a typeof argument of an attribute on the assembly, and on the module.
        { "ExternalLib.OnlyOnAssembly", "used_by", $"1 user of ExternalLib.OnlyOnAssembly:\n{OnAssembly}" },
        { "ExternalLib.OnlyOnModule", "used_by", $"1 user of ExternalLib.OnlyOnModule:\n{OnModule}" },
        // An enum, as the type of an argument of an attribute on the assembly.
        {
            "ExternalLib.Tint", "used_by",
            $"2 users of ExternalLib.Tint:\nExternalUser.LocalScopeAttribute..ctor(Type, Tint)\n{OnAssembly}"
        },
        {
            "ExternalUser.LocalScopeAttribute", "applied_to",
            $"2 symbols marked with ExternalUser.LocalScopeAttribute:\n{OnAssembly}\n{OnModule}"
        },
        {
            "ExternalUser.LocalScopeAttribute", "used_by",
            $"2 users of ExternalUser.LocalScopeAttribute:\n{OnAssembly}\n{OnModule}"
        },
        { "ExternalUser.LocalOnAssembly", "used_by", $"1 user of ExternalUser.LocalOnAssembly:\n{OnAssembly}" },
        { "ExternalUser.LocalOnModule", "used_by", $"1 user of ExternalUser.LocalOnModule:\n{OnModule}" },
        // Named beside a value of an enum ExternalLib defines. The enum's underlying type is not
        // in the fixture's assembly, and an attribute with such an argument still has to be read.
        { "ExternalUser.LocalBesideEnum", "used_by", $"1 user of ExternalUser.LocalBesideEnum:\n{OnAssembly}" },
        // Named in an array that follows a value of Grade, an enum of the fixture's own that takes
        // one byte. Read as four, the value would shift the array out of place. One class is
        // the fixture's and one ExternalLib's; the enum's value is a use of the enum.
        { "ExternalUser.LocalAfterEnum", "used_by", $"1 user of ExternalUser.LocalAfterEnum:\n{OnModule}" },
        { "ExternalLib.OnlyAfterEnum", "used_by", $"1 user of ExternalLib.OnlyAfterEnum:\n{OnModule}" },
        {
            "ExternalUser.Grade", "used_by",
            $"2 users of ExternalUser.Grade:\nExternalUser.LocalScopeAttribute.Level\n{OnModule}"
        },
    };

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task Analyze_OnAnAttributeOfTheAssemblyOrTheModule_NamesTheOneThatCarriesIt(
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
