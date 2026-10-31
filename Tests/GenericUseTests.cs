using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for what analyze names when asked what a method of a generic class, or a
/// generic method, uses: each member as the code names it, with its type arguments, and a type
/// parameter by its name. The fixture's <c>CallShapes.GenericUses</c> holds <c>Crate&lt;TItem&gt;</c>,
/// which <c>Shelf&lt;T&gt;</c> uses through its own type parameter and <c>Packer</c> through a
/// method's type parameter and with <c>int</c>, and <c>Cached&lt;T&gt;</c>, whose property's
/// getter calls its own setter.
/// </summary>
public sealed class GenericUseTests : IClassFixture<ILensServerFixture>
{
    private const string GenericUses = "CallShapes.GenericUses";
    private const string Collections = "System.Collections.Generic";

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public GenericUseTests(ILensServerFixture server)
    {
        _server = server;
        _callShapesPath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "CallShapes.dll");
        if (!File.Exists(_callShapesPath))
            throw new FileNotFoundException(
                $"CallShapes fixture not found: {_callShapesPath}. " +
                "Tests/Fixtures/CallShapes/ is expected to land here via Tests.csproj's " +
                "ProjectReference.");
    }

    // Shelf<T> names its own members, Crate and the dictionary on types built over T. The
    // type parameter is written T throughout, also in Crate, whose own is named TItem.
    [Fact]
    public async Task Uses_OnAMethodOfAGenericClass_WritesTheClassesTypeParameterByItsName()
    {
        Assert.Equal(
            $"6 symbols used by {GenericUses}.Shelf<>.Stock(T) [declared]:\n" +
            $"{GenericUses}.Crate<T>.Fill(T, List<T>)\n" +
            $"{GenericUses}.Shelf<T>.Label(T)\n" +
            $"{GenericUses}.Shelf<T>._crate\n" +
            $"{GenericUses}.Shelf<T>._index\n" +
            $"{Collections}.Dictionary<T, List<T>>.set_Item(T, List<T>)\n" +
            $"{Collections}.List<T>..ctor()",
            await Analyze($"{GenericUses}.Shelf<>", "Stock", "uses"));
    }

    // The method numbers its type parameter U as its own first, the class of its lambda as
    // the class's first. Both name Crate<U>.Make(), which is one symbol.
    [Fact]
    public async Task Uses_OnAGenericMethodWithALambda_ListsAMemberBothUseOnce()
    {
        Assert.Equal(
            $"2 symbols used by {GenericUses}.Packer.Twice<U>() [declared]:\n" +
            $"{GenericUses}.Crate<U>.Make()\n" +
            "System.Func<U>..ctor(object, nint)",
            await Analyze($"{GenericUses}.Packer", "Twice", "uses"));
    }

    [Fact]
    public async Task Uses_OnAMethodWithoutTypeParameters_WritesTheTypeArguments()
    {
        Assert.Equal(
            $"4 symbols used by {GenericUses}.Packer.Concrete() [declared]:\n" +
            $"{GenericUses}.Crate<int>..ctor()\n" +
            $"{GenericUses}.Crate<int>.Fill(int, List<int>)\n" +
            $"{GenericUses}.Crate<int>.Make()\n" +
            $"{Collections}.List<int>..ctor()",
            await Analyze($"{GenericUses}.Packer", "Concrete", "uses"));
    }

    // The iterator's class has two type parameters, T of Shelf and U of the method, and its
    // code names the fields of the class over both. The method that creates it names the same
    // fields with U numbered as its own.
    [Fact]
    public async Task Uses_OnAGenericIteratorOfAGenericClass_NamesEveryTypeParameter_AndTheFieldBothPartsNameOnce()
    {
        Assert.Equal(
            $"3 symbols used by {GenericUses}.Shelf<>.Each<U>(U) [declared]:\n" +
            $"{GenericUses}.Crate<T>.Content\n" +
            $"{GenericUses}.Shelf<T>.Label(T)\n" +
            $"{GenericUses}.Shelf<T>._crate",
            await Analyze($"{GenericUses}.Shelf<>", "Each", "uses"));

        string[] generated = (await Analyze($"{GenericUses}.Shelf<>", "Each", "uses", includeGenerated: true)).Split('\n');

        Assert.DoesNotContain(generated, line => line.Contains('!'));
        string instance = Assert.Single(generated, line => line.EndsWith(".<>4__this"));
        Assert.StartsWith($"{GenericUses}.Shelf<T>.<Each>d__", instance);
        Assert.EndsWith("<U>.<>4__this", instance);
    }

    [Fact]
    public async Task Uses_OnAPropertyOfAGenericClass_WritesTheTypeParameterByItsName()
    {
        Assert.Equal(
            $"2 symbols used by {GenericUses}.Shelf<>.Count [declared] (via get_Count):\n" +
            $"{GenericUses}.Shelf<T>._index\n" +
            $"{Collections}.Dictionary<T, List<T>>.get_Count()",
            await Analyze($"{GenericUses}.Shelf<>", "Count", "uses"));
    }

    // The getter calls the property's own setter, which the code names on Cached<T>. The
    // property is not among what it uses, as in a class without type parameters.
    [Fact]
    public async Task Uses_OnAPropertyWhoseGetterCallsItsSetter_DoesNotListThePropertyItself()
    {
        Assert.Equal(
            $"2 symbols used by {GenericUses}.Cached<>.Value [declared] (via get_Value, set_Value):\n" +
            $"{GenericUses}.Cached<T>._made\n" +
            $"{GenericUses}.Cached<T>._value",
            await Analyze($"{GenericUses}.Cached<>", "Value", "uses"));
    }

    // A line names the class's own method as Shelf<T>.Label(T). The type and the parameter
    // list of that line lead to the method when passed to the next question.
    [Fact]
    public async Task ALineOfAUsesAnswer_PassedBackAsTypeNameAndParameterTypes_ReachesTheMember()
    {
        string[] used = (await Analyze($"{GenericUses}.Shelf<>", "Stock", "uses")).Split('\n');
        string line = Assert.Single(used, candidate => candidate.Contains(".Label("));
        string typeName = line[..line.IndexOf(".Label(")];
        string parameterTypes = line[(line.IndexOf('(') + 1)..^1];

        Assert.Equal($"{GenericUses}.Shelf<T>", typeName);
        Assert.Equal(
            $"2 users of {GenericUses}.Shelf<>.Label(T) [declared]:\n" +
            $"{GenericUses}.Shelf<>.Each<U>(U)\n" +
            $"{GenericUses}.Shelf<>.Stock(T)",
            await Analyze(typeName, "Label", "used_by", parameterTypes: [parameterTypes]));
    }

    private async Task<string> Analyze(
        string typeName, string memberName, string kind, bool includeGenerated = false, string[]? parameterTypes = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
            ["include_compiler_generated"] = includeGenerated,
        };
        if (parameterTypes is not null)
            arguments["parameter_types"] = parameterTypes;

        CallToolResult result = await _server.Client.CallToolAsync("analyze", arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"analyze returned an error: {text}");
        return text;
    }
}
