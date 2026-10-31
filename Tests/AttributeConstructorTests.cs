using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for <c>analyze</c> on the constructor of an attribute class. Applying an
/// attribute uses one of its constructors without any call, so who uses the constructor is the
/// code that calls it and what carries an attribute applied through it, and the list of its
/// calls says how many such uses it leaves out. The ExternalUser fixture's Applied.cs applies an
/// attribute class of its own and one of ExternalLib through two constructors each, one of which
/// code also calls.
/// </summary>
public sealed class AttributeConstructorTests : IClassFixture<ILensServerFixture>
{
    private const string Applied = "ExternalUser.Applied";
    private const string Stamp = $"{Applied}.StampAttribute";
    private const string Labels = "ExternalUser.Labels";
    private const string Generics = "ExternalUser.Generics";
    private const string OnAssembly = "assembly ExternalUser";
    private const string OnModule = "module ExternalUser.dll";

    private readonly ILensServerFixture _server;
    private readonly string _userPath;
    private readonly string _callShapesPath;

    public AttributeConstructorTests(ILensServerFixture server)
    {
        _server = server;
        var directory = Path.GetDirectoryName(server.AssemblyPath)!;
        _userPath = Path.Combine(directory, "ExternalUser.dll");
        _callShapesPath = Path.Combine(directory, "CallShapes.dll");
    }

    // An attribute class, the parameter types that select one of its constructors or null where
    // it has only one, the kind asked, and the whole answer. The classes named ExternalLib are
    // ones ILens knows only from the fixture's references to them; the others the fixture defines.
    public static TheoryData<string, string[]?, string, string> Questions => new()
    {
        // Every kind of carrier, the method a lambda and a local function with the attribute are
        // written in, and beside them the code that calls the constructor. Both() does both.
        {
            Stamp, ["int"], "used_by",
            $"10 users of {Stamp}..ctor(int) [declared]:\n" +
            $"{Applied}.InCode.Both()\n{Applied}.InCode.Make()\n{Applied}.InCode.OnLambda()\n" +
            $"{Applied}.InCode.OnLocalFunction()\n{Applied}.Ranked\n{Applied}.Ranked.Accessor\n" +
            $"{Applied}.Ranked.Field\n{Applied}.Ranked.Parameter(int)\n{Applied}.Ranked.Property\n" +
            $"{Applied}.Ranked.Return()"
        },
        // Each constructor has its own users, which the list for the class does not tell apart.
        { Stamp, [], "used_by", $"1 user of {Stamp}..ctor() [declared]:\n{Applied}.Plain" },
        { Stamp, ["string"], "used_by", $"No users of {Stamp}..ctor(string) [declared]" },
        // On an event and on a type parameter of a method and of a class.
        {
            $"{Labels}.LabelAttribute", ["Rank"], "used_by",
            $"9 users of {Labels}.LabelAttribute..ctor(Rank) [declared]:\n" +
            $"{Labels}.RankFixed\n{Labels}.RankOnMembers.Event\n{Labels}.RankOnMembers.Field\n" +
            $"{Labels}.RankOnMembers.Method()\n{Labels}.RankOnMembers.Parameter(int)\n" +
            $"{Labels}.RankOnMembers.Property\n{Labels}.RankOnMembers.Return()\n" +
            $"{Labels}.RankOnMembers.TypeParameter<T>()\n{Labels}.RankOnTypeParameter<>"
        },
        // On the assembly and on the module; the module's second attribute has named arguments
        // alone, which go through the constructor without parameters.
        {
            "ExternalUser.LocalScopeAttribute", ["System.Type"], "used_by",
            $"2 users of ExternalUser.LocalScopeAttribute..ctor(Type) [declared]:\n{OnAssembly}\n{OnModule}"
        },
        {
            "ExternalUser.LocalScopeAttribute", [], "used_by",
            $"1 user of ExternalUser.LocalScopeAttribute..ctor() [declared]:\n{OnModule}"
        },
        // An attribute class with type parameters, whose every use names the constructor of one
        // construction of the class.
        {
            $"{Generics}.OrderedAttribute<,>", null, "used_by",
            $"6 users of {Generics}.OrderedAttribute<,>..ctor(TSecond, Type) [declared]:\n" +
            $"{Generics}.AfterAClass\n{Generics}.AfterAConstructedType\n" +
            $"{Generics}.AfterAConstructedTypeWithAnEnumValue\n{Generics}.AfterAStruct\n" +
            $"{Generics}.AfterAnEnum\n{Generics}.NamesALibraryClass"
        },
        // An attribute class of another assembly.
        {
            "ExternalLib.SealAttribute", ["int"], "used_by",
            $"2 users of ExternalLib.SealAttribute..ctor(int):\n{Applied}.Sealed.Make()\n{Applied}.Sealed.Method()"
        },
        { "ExternalLib.SealAttribute", [], "used_by", $"1 user of ExternalLib.SealAttribute..ctor():\n{Applied}.Sealed" },
        {
            "ExternalLib.ScopeAttribute", null, "used_by",
            $"2 users of ExternalLib.ScopeAttribute..ctor(Type):\n{OnAssembly}\n{OnModule}"
        },
        {
            "ExternalLib.MarkAttribute", [], "used_by",
            "3 users of ExternalLib.MarkAttribute..ctor():\n" +
            "ExternalUser.Marked\nExternalUser.Marked.MarkedMethod()\nExternalUser.Marked.MarkedParameter(int)"
        },
        // The list of calls keeps to calls and counts the attributes, not what carries them:
        // nine attributes go through this constructor.
        {
            Stamp, ["int"], "call_sites",
            $"2 calls to {Stamp}..ctor(int):\n" +
            $"{Applied}.InCode.Both()\n  new StampAttribute(9)\n{Applied}.InCode.Make()\n  new StampAttribute(7)\n" +
            "Not shown: 9 uses as an attribute; used_by lists them"
        },
        {
            Stamp, [], "call_sites",
            $"No calls to {Stamp}..ctor()\nNot shown: 1 use as an attribute; used_by lists them"
        },
        { Stamp, ["string"], "call_sites", $"No calls to {Stamp}..ctor(string)" },
        // Every overload at once.
        {
            Stamp, null, "call_sites",
            $"2 calls to {Stamp}..ctor (3 overloads):\n" +
            $"{Applied}.InCode.Both()\n  new StampAttribute(9)\n{Applied}.InCode.Make()\n  new StampAttribute(7)\n" +
            "Not shown: 10 uses as an attribute; used_by lists them"
        },
        {
            $"{Generics}.OrderedAttribute<,>", null, "call_sites",
            $"No calls to {Generics}.OrderedAttribute<,>..ctor(TSecond, Type)\n" +
            "Not shown: 6 uses as an attribute; used_by lists them"
        },
        // The compiler adds this attribute class to the fixture and puts it on the row that says
        // Applied.Keyed implements its interface, through this constructor alone. That row
        // belongs to no type or member, so nothing carries the attribute, and the list of calls
        // counts no use that the list of users could not name.
        {
            "System.Runtime.CompilerServices.NullableAttribute", ["byte[]"], "used_by",
            "No users of System.Runtime.CompilerServices.NullableAttribute..ctor(byte[]) [declared]"
        },
        {
            "System.Runtime.CompilerServices.NullableAttribute", ["byte[]"], "call_sites",
            "No calls to System.Runtime.CompilerServices.NullableAttribute..ctor(byte[])"
        },
    };

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task Analyze_OnAnAttributesConstructor_CountsWhereTheAttributeIsApplied(
        string typeName, string[]? parameterTypes, string kind, string expected)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _userPath,
            ["type_name"] = typeName,
            ["member_name"] = ".ctor",
            ["kind"] = kind,
        };
        if (parameterTypes is not null)
            arguments["parameter_types"] = parameterTypes;

        Assert.Equal(expected, await Analyze(arguments));
    }

    // The compiler marks the methods of an iterator's class as hidden from the debugger, and
    // nothing else in the fixture carries that attribute.
    [Fact]
    public async Task UsedBy_OnAConstructorOnlyGeneratedCodeIsMarkedThrough_SaysWhatItLeftOut()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "System.Diagnostics.DebuggerHiddenAttribute",
            ["member_name"] = ".ctor",
            ["kind"] = "used_by",
        };

        string[] lines = (await Analyze(arguments)).Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("No users of System.Diagnostics.DebuggerHiddenAttribute..ctor()", lines[0]);
        Assert.Matches(@"^Not shown: \d+ compiler-generated; include_compiler_generated lists them$", lines[1]);

        arguments["include_compiler_generated"] = true;
        arguments["limit"] = 0;
        string withGenerated = await Analyze(arguments);
        Assert.Matches(new Regex(@"^\d+ users of System\.Diagnostics\.DebuggerHiddenAttribute\.\.ctor\(\):"), withGenerated);
    }

    private async Task<string> Analyze(Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync("analyze", arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        Assert.True(result.IsError != true, $"analyze returned an error: {text}");
        return text;
    }
}
