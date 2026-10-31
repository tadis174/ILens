using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the types analyze lists when asked what a method uses: a type the
/// code names itself, in a cast, a type test, an array creation, a <c>default</c> of a struct
/// or a <c>typeof</c>, and not a type that is only a local's or stands in the signature of a
/// member the code uses. The fixture's <c>CallShapes.TypeUses.Namer</c> has one method for
/// each, named after it. <c>ExternalUser.TypeNames.Namer</c> names types of ExternalLib the
/// same ways, an assembly that lies where the shared server does not search.
/// </summary>
public sealed class TypeUseTests : IClassFixture<ILensServerFixture>
{
    private const string TypeUses = "CallShapes.TypeUses";
    private const string TypeNames = "ExternalUser.TypeNames";

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;
    private readonly string _externalUserPath;

    public TypeUseTests(ILensServerFixture server)
    {
        _server = server;
        _callShapesPath = FixturePath("CallShapes.dll", "Tests/Fixtures/CallShapes/");
        _externalUserPath = FixturePath("ExternalUser.dll", "Tests/Fixtures/ExternalUser/");

        string FixturePath(string fileName, string source)
        {
            string path = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fixture not found: {path}. {source} is expected to land here via Tests.csproj's " +
                    "ProjectReference.");
            return path;
        }
    }

    // Each method names one type and uses no member, so the type is its whole answer.
    [Theory]
    [InlineData("Cast(object)", "Bolt")]
    [InlineData("Is(object)", "Bolt")]
    [InlineData("As(object)", "Bolt")]
    [InlineData("Array()", "Bolt")]
    [InlineData("Default()", "Slot")]
    [InlineData("Unboxed(object)", "Slot")]
    public async Task Uses_ListsATypeTheCodeNames(string method, string type)
    {
        Assert.Equal(
            $"1 symbol used by {TypeUses}.Namer.{method} [declared]:\n" +
            $"{TypeUses}.{type}",
            await Uses(_callShapesPath, $"{TypeUses}.Namer", method));
    }

    // A typeof also calls the method that turns its token into a Type.
    [Fact]
    public async Task Uses_ListsATypeNamedInATypeof_BesideTheCallThatReadsIt()
    {
        Assert.Equal(
            $"2 symbols used by {TypeUses}.Namer.Token() [declared]:\n" +
            $"{TypeUses}.Bolt\n" +
            "System.Type.GetTypeFromHandle(RuntimeTypeHandle)",
            await Uses(_callShapesPath, $"{TypeUses}.Namer", "Token()"));
    }

    // A type built from others gives a line for each named type in it: the generic class and
    // its argument, and for a nullable set to null, Nullable<> and the struct.
    [Theory]
    [InlineData("Constructed(object)", $"{TypeUses}.Bolt", $"{TypeUses}.Rack<>")]
    [InlineData("Nothing()", $"{TypeUses}.Slot", "System.Nullable<>")]
    public async Task Uses_ListsEachNamedTypeAConstructedTypeIsBuiltFrom(string method, string first, string second)
    {
        Assert.Equal(
            $"2 symbols used by {TypeUses}.Namer.{method} [declared]:\n{first}\n{second}",
            await Uses(_callShapesPath, $"{TypeUses}.Namer", method));
    }

    [Fact]
    public async Task Uses_ListsATypeOfTheFramework()
    {
        Assert.Equal(
            $"1 symbol used by {TypeUses}.Namer.Framework(object) [declared]:\n" +
            "System.IDisposable",
            await Uses(_callShapesPath, $"{TypeUses}.Namer", "Framework(object)"));
    }

    // Part is the type of a local in one method and of a parameter of the method both call.
    // Neither method's code names Part, so neither answer has a line for it, while Part's
    // users include both: used_by on a type also counts locals and signatures.
    [Fact]
    public async Task Uses_LeavesOutATypeThatIsOnlyALocalsOrInASignature_WhichUsedByOnTheTypeCounts()
    {
        Assert.Equal(
            $"2 symbols used by {TypeUses}.Namer.LocalOnly() [declared]:\n" +
            $"{TypeUses}.Maker.Make()\n" +
            $"{TypeUses}.Maker.Take(Part)",
            await Uses(_callShapesPath, $"{TypeUses}.Namer", "LocalOnly()"));
        Assert.Equal(
            $"1 symbol used by {TypeUses}.Namer.SignatureOnly() [declared]:\n" +
            $"{TypeUses}.Maker.Take(Part)",
            await Uses(_callShapesPath, $"{TypeUses}.Namer", "SignatureOnly()"));

        string[] users = (await Analyze(_callShapesPath, $"{TypeUses}.Part", null, "used_by")).Split('\n');
        Assert.Contains($"{TypeUses}.Namer.LocalOnly()", users);
        Assert.Contains($"{TypeUses}.Namer.SignatureOnly()", users);
    }

    // The other direction: every method whose answer names Bolt, or Slot, is a user of it,
    // and nothing else is.
    [Fact]
    public async Task UsedBy_OnATypeAUsesAnswerNames_ListsTheMethod()
    {
        Assert.Equal(
            $"6 users of {TypeUses}.Bolt:\n" +
            $"{TypeUses}.Namer.Array()\n" +
            $"{TypeUses}.Namer.As(object)\n" +
            $"{TypeUses}.Namer.Cast(object)\n" +
            $"{TypeUses}.Namer.Constructed(object)\n" +
            $"{TypeUses}.Namer.Is(object)\n" +
            $"{TypeUses}.Namer.Token()",
            await Analyze(_callShapesPath, $"{TypeUses}.Bolt", null, "used_by"));
        Assert.Equal(
            $"3 users of {TypeUses}.Slot:\n" +
            $"{TypeUses}.Namer.Default()\n" +
            $"{TypeUses}.Namer.Nothing()\n" +
            $"{TypeUses}.Namer.Unboxed(object)",
            await Analyze(_callShapesPath, $"{TypeUses}.Slot", null, "used_by"));
    }

    // ExternalLib.dll lies in a folder the server does not search, so its types are known by
    // name alone. They are listed like any other.
    [Theory]
    [InlineData("Cast(object)", "ExternalLib.Casing")]
    [InlineData("Is(object)", "ExternalLib.Casing")]
    [InlineData("Array()", "ExternalLib.Casing")]
    [InlineData("Default()", "ExternalLib.Notch")]
    public async Task Uses_ListsATypeOfAnAssemblyItCannotLoad_ByItsName(string method, string type)
    {
        Assert.Equal(
            $"1 symbol used by {TypeNames}.Namer.{method} [declared]:\n{type}",
            await Uses(_externalUserPath, $"{TypeNames}.Namer", method));
    }

    [Fact]
    public async Task Uses_ListsATypeOfAnAssemblyItCannotLoad_InATypeofAndAsAGenericType()
    {
        Assert.Equal(
            $"2 symbols used by {TypeNames}.Namer.Token() [declared]:\n" +
            "ExternalLib.Casing\n" +
            "System.Type.GetTypeFromHandle(RuntimeTypeHandle)",
            await Uses(_externalUserPath, $"{TypeNames}.Namer", "Token()"));
        Assert.Equal(
            $"2 symbols used by {TypeNames}.Namer.Constructed(object) [declared]:\n" +
            "ExternalLib.Tray<>\n" +
            $"{TypeNames}.Own",
            await Uses(_externalUserPath, $"{TypeNames}.Namer", "Constructed(object)"));
    }

    /// <summary>What <paramref name="method"/>, written with its parameter list, uses.</summary>
    private Task<string> Uses(string assembly, string typeName, string method) =>
        Analyze(assembly, typeName, method[..method.IndexOf('(')], "uses");

    private async Task<string> Analyze(string assembly, string typeName, string? memberName, string kind)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = assembly,
            ["type_name"] = typeName,
            ["kind"] = kind,
        };
        if (memberName is not null)
            arguments["member_name"] = memberName;

        CallToolResult result = await _server.Client.CallToolAsync("analyze", arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"analyze returned an error: {text}");
        return text;
    }
}
