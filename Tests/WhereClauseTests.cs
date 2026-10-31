using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the conditions on a type parameter, which C# writes in a where clause:
/// list_members writes them after a generic method's parameters and after a generic type's base
/// types in its heading, as the decompiler writes them, with allows ref struct, which ILSpy does not
/// read, taken from the type parameter's metadata row, and without a clause on an override or an
/// explicit implementation, which take the conditions of the member they override or implement.
/// CallShapes.Constraints holds class, struct, a type and new() and their combinations, and
/// InterfaceShapes.Conditions the other forms, the headings and the overrides. compare's lines for a
/// changed condition on BaseShift's Conditions and Holder are pinned in ComparisonTests.
/// </summary>
public sealed class WhereClauseTests : IClassFixture<ILensServerFixture>
{
    private const string Conditions = "InterfaceShapes.Conditions";
    private readonly ILensServerFixture _server;
    private readonly string _interfaceShapesPath;
    private readonly string _callShapesPath;

    public WhereClauseTests(ILensServerFixture server)
    {
        _server = server;
        var directory = Path.GetDirectoryName(_server.AssemblyPath)!;
        _interfaceShapesPath = Path.Combine(directory, "InterfaceShapes.dll");
        _callShapesPath = Path.Combine(directory, "CallShapes.dll");
        foreach (var path in new[] { _interfaceShapesPath, _callShapesPath })
        {
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fixture not found: {path}. Tests/Fixtures/ is expected to land here via Tests.csproj's " +
                    "ProjectReference.");
        }
    }

    [Fact]
    public async Task ListMembers_WritesTheConditionsOfEachForm()
    {
        // class, struct with an interface, an interface, none, struct alone, struct with
        // System.Enum and new().
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "CallShapes.Constraints.ShapeExtensions",
        });

        Assert.Equal(
            new[]
            {
                "13 members of public static class CallShapes.Constraints.ShapeExtensions:",
                "13 methods:",
                "  public static int Corners<T>(this T shape) where T : IShape",
                "  public static T Echo<T>(this T value)",
                "  public static void Fit(this Plate plate)",
                "  public static void Fit<T>(this T shape) where T : IShape",
                "  public static T Fresh<T>(this T value) where T : new()",
                "  public static void Grow<T>(this ref T shape) where T : struct, IShape",
                "  public static Crate Label(this Crate crate, int number)",
                "  public static Crate Label(this Crate crate, string text)",
                "  public static T Shade<T>(this T shape, int level) where T : IShape",
                "  public static T Shade<T>(this T shape, string name) where T : IShape",
                "  public static string Spell<T>(this T hue) where T : struct, Enum",
                "  public static T Tag<T>(this T value) where T : class",
                "  public static T Zero<T>(this T value) where T : struct",
            },
            text.Split('\n'));
    }

    [Fact]
    public async Task ListMembers_WritesUnmanagedNotnullAndAllowsRefStructAndAClauseForEachTypeParameter()
    {
        // allows ref struct comes last, after a type, struct or new(); a type parameter without a
        // condition has no clause, also beside one that has.
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _interfaceShapesPath,
            ["type_name"] = $"{Conditions}.Forms",
        });

        Assert.Equal(
            new[]
            {
                $"11 members of public class {Conditions}.Forms:",
                "11 methods:",
                "  public Forms()",
                "  public void Borrowed<T>(T item) where T : allows ref struct",
                "  public void Filled<T>(T item) where T : notnull",
                "  public void Made<T>() where T : new(), allows ref struct",
                "  public void Pair<TKey, TValue>(TKey key, TValue value) " +
                    "where TKey : class, IComparable<TKey>, new() where TValue : struct",
                "  public void Plain<T>(T item)",
                "  public void Raw<T>(T item) where T : unmanaged",
                "  public void Second<T, U>(T first, U second) where U : Base",
                "  public void Slotted<T>(T item) where T : ISlot, allows ref struct",
                "  public void Valued<T>(T item) where T : struct, allows ref struct",
                "  public void Within<T, U>(T outer, U inner) where U : T",
            },
            text.Split('\n'));
    }

    [Fact]
    public async Task ListMembers_WritesAGenericTypesConditionsInItsHeadingAlone()
    {
        // The type's conditions stand after its base class, and neither a method that uses its
        // type parameter nor a generic method without a condition of its own has a clause.
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _interfaceShapesPath,
            ["type_name"] = $"{Conditions}.Holder<>",
        });

        Assert.Equal(
            new[]
            {
                $"4 members of public class {Conditions}.Holder<T> : Base where T : class, ISlot, new():",
                "3 methods:",
                "  public Holder()",
                "  public void Move<U>(U item)",
                "  public void Put(T item)",
                "1 nested type:",
                $"  {Conditions}.Holder<>.Inner<>",
            },
            text.Split('\n'));
    }

    // A nested class's heading has the conditions of its own type parameter alone, since those of
    // the class enclosing it stand on that class, as C# declares them. An interface's and a
    // delegate's headings have theirs as a class's does, allows ref struct included.
    [Theory]
    [InlineData("Holder<>.Inner<>", $"2 members of public class {Conditions}.Holder<T>.Inner<U> where U : struct:")]
    [InlineData("IStore<>", $"1 member of public interface {Conditions}.IStore<T> where T : notnull:")]
    [InlineData("Maker<>", $"4 members of public delegate {Conditions}.Maker<T> where T : new():")]
    public async Task ListMembers_WritesTheConditionsInTheHeading(string typeName, string heading)
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _interfaceShapesPath,
            ["type_name"] = $"{Conditions}.{typeName}",
        });

        Assert.Equal(heading, text.Split('\n')[0]);
    }

    // An override, a sealed or abstract one included, and an explicit implementation take the
    // conditions of the member they override or implement: C# lets them repeat only class or
    // struct, or write default, to tell T? apart, and the decompiler writes none, nor does
    // list_members. The methods they override and an implicit implementation write theirs.
    [Theory]
    [InlineData("Shaper", new[]
    {
        "  protected Shaper()",
        "  public virtual void Fit<T>(T item) where T : struct",
        "  public abstract T Shape<T>(T item) where T : class, ISlot",
    })]
    [InlineData("Cutter", new[]
    {
        "  public Cutter()",
        "  public sealed override void Fit<T>(T item)",
        "  public override T Shape<T>(T item)",
    })]
    [InlineData("Trimmer", new[]
    {
        "  protected Trimmer()",
        "  public abstract override T Shape<T>(T item)",
    })]
    [InlineData("Sorter", new[]
    {
        "  public Sorter()",
        $"  private void {Conditions}.ISorter.Sort<T>(T item)",
    })]
    [InlineData("OpenSorter", new[]
    {
        "  public OpenSorter()",
        "  public void Sort<T>(T item) where T : IComparable<T>",
    })]
    public async Task ListMembers_WritesNoClauseOnAnOverrideOrAnExplicitImplementation(string typeName, string[] methods)
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _interfaceShapesPath,
            ["type_name"] = $"{Conditions}.{typeName}",
            ["kinds"] = new[] { "method" },
            ["accessibility"] = "all",
        });

        Assert.Equal(methods, text.Split('\n').Skip(2));
    }

    [Fact]
    public async Task Decompile_ThroughAType_MarksAnExtensionMethodThatAllowsARefStruct()
    {
        // The mark of an extension method found through a type writes its receiver's conditions as
        // a declaration does.
        string code = await CallToolText("decompile", new()
        {
            ["assembly"] = _interfaceShapesPath,
            ["type_name"] = $"{Conditions}.Base",
            ["member_name"] = "Touch",
        });

        Assert.Equal(
            $"// {Conditions}.Touches.Touch<T>(T) → int [extension method on T where T : allows ref struct]",
            code.Split('\n')[0]);
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
}
