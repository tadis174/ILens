using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for <c>analyze</c> with <c>used_by</c> where an attribute is given a value
/// of an enum another assembly defines that does not take four bytes. An attribute's bytes do
/// not say how many bytes the value takes, and the shared server cannot load ExternalLib, which
/// defines the enums, so the answers show that the width is worked out from the bytes. The
/// ExternalUser fixture's Widths.cs applies an attribute class of its own with values of
/// ExternalLib's Grain, one byte, and Reach, eight.
/// </summary>
public sealed class AttributeWidthTests : IClassFixture<ILensServerFixture>
{
    private const string Widths = "ExternalUser.Widths";

    private readonly ILensServerFixture _server;
    private readonly string _userPath;

    public AttributeWidthTests(ILensServerFixture server)
    {
        _server = server;
        _userPath = Path.Combine(Path.GetDirectoryName(server.AssemblyPath)!, "ExternalUser.dll");
    }

    // A type and the whole answer to who uses it. The types named ExternalLib are ones ILens
    // knows only from the fixture's references to them; the others the fixture defines.
    public static TheoryData<string, string> Questions => new()
    {
        // A class of the fixture named by typeof beside a value: the value after the typeof,
        // before it, as a named argument before the typeof, of an attribute class's type
        // argument, and of the enum that takes eight bytes.
        {
            $"{Widths}.Measured",
            $"5 users of {Widths}.Measured:\n" + string.Join("\n",
                $"{Widths}.ValueAfterTypeof",
                $"{Widths}.ValueBeforeTypeof",
                $"{Widths}.ValueNamed",
                $"{Widths}.ValueOfATypeArgument",
                $"{Widths}.WideValue")
        },
        // Named only beside a value, in an attribute on the assembly.
        { $"{Widths}.OnAssembly", $"1 user of {Widths}.OnAssembly:\nassembly ExternalUser" },
        // The enum of one byte: the members whose signatures name it, then everything that
        // carries a value of it, also the two classes whose attribute reads two ways, since a
        // parameter of the enum is in the constructor's signature and not in the bytes.
        {
            "ExternalLib.Grain",
            "11 users of ExternalLib.Grain:\n" + string.Join("\n",
                $"{Widths}.GaugeAttribute..ctor(Grain, Type)",
                $"{Widths}.GaugeAttribute..ctor(Type, Grain)",
                $"{Widths}.GaugeAttribute.Named",
                $"{Widths}.LibraryClassReadTwoWays",
                $"{Widths}.NamesLibraryClass",
                $"{Widths}.ReadTwoWays",
                $"{Widths}.ValueAfterTypeof",
                $"{Widths}.ValueBeforeTypeof",
                $"{Widths}.ValueNamed",
                $"{Widths}.ValueOfATypeArgument",
                "assembly ExternalUser")
        },
        // The enum of eight bytes.
        {
            "ExternalLib.Reach",
            $"2 users of ExternalLib.Reach:\n{Widths}.GaugeAttribute..ctor(Type, Reach)\n{Widths}.WideValue"
        },
        // A class of ExternalLib named by typeof beside a value.
        { "ExternalLib.OnlyBesideGrain", $"1 user of ExternalLib.OnlyBesideGrain:\n{Widths}.NamesLibraryClass" },
        // A class whose name has the length at which the attribute's bytes fit a second width,
        // read with which the attribute names another type. The answer cannot tell which
        // reading is right, so it does not list the carrier and says what it did not check: for
        // a class of the fixture, where two bytes fit, and for one of ExternalLib, where eight do.
        {
            $"{Widths}.NamedAtTheLengthWhereReadingTheValueAsTwoBytesFits",
            $"No users of {Widths}.NamedAtTheLengthWhereReadingTheValueAsTwoBytesFits\n" +
            $"Not checked: whether {Widths}.GaugeAttribute on {Widths}.ReadTwoWays names it; " +
            "its bytes fit more than one size of ExternalLib.Grain"
        },
        {
            "ExternalLib.NamedWhereEightBytesFits",
            "No users of ExternalLib.NamedWhereEightBytesFits\n" +
            $"Not checked: whether {Widths}.GaugeAttribute on {Widths}.LibraryClassReadTwoWays names it; " +
            "its bytes fit more than one size of ExternalLib.Grain"
        },
    };

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task Analyze_UsedBy_ReadsAnAttributeWithAValueOfAnotherAssemblysEnumOfAnyWidth(
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
