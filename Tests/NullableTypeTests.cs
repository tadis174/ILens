using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for C#'s '?' after a type. A type pattern reads it as C# does: after a value
/// type it is that type's Nullable&lt;T&gt;, so int? fits only the nullable type; after any other
/// type it is the nullable annotation, which is passed over, so string? fits what string fits;
/// and after a type parameter it fits the nullable form, and held exactly the type parameter
/// itself only where the code marks it T?. Every answer writes a nullable value type as C# does,
/// int? for Nullable&lt;int&gt;, and a nullable type before a member's name keeps its own name,
/// System.Nullable&lt;int&gt;. The fixture is the namespace CallShapes.Nullables in
/// Tests/Fixtures/CallShapes/Nullables.cs; string? and an annotated T? are read on the runtime's
/// System.Private.CoreLib, whose code carries nullable annotations.
/// </summary>
public sealed class NullableTypeTests : IClassFixture<ILensServerFixture>
{
    private const string Nullables = "CallShapes.Nullables";

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public NullableTypeTests(ILensServerFixture server)
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

    // A parameter, a return, a field, a property and an event type, a type argument and an
    // array's element.
    [Fact]
    public async Task ListMembers_WritesANullableValueTypeAsCSharpDoes()
    {
        Assert.Equal(
            $"19 members of public class {Nullables}.Meter:\n" +
            "16 methods:\n" +
            "  public Meter()\n" +
            "  public void Fill(int?[] values)\n" +
            "  public void Fill(int[] values)\n" +
            "  public void Hold<T>(T value) where T : struct\n" +
            "  public void Hold<T>(T? value) where T : struct\n" +
            "  public void Keep<T>(T value) where T : class\n" +
            "  public void Keep<T>(T? value) where T : struct\n" +
            "  public void Label(string text)\n" +
            "  public void Log(List<int> values)\n" +
            "  public void Log(List<int?> values)\n" +
            "  public int Read(int value)\n" +
            "  public int Read(int? value)\n" +
            "  public void Store(out int value)\n" +
            "  public void Store(out int? value)\n" +
            "  public static int op_Explicit(Meter meter)\n" +
            "  public static int? op_Explicit(Meter meter)\n" +
            "1 property:\n" +
            "  public int? Reading { get; set; }\n" +
            "1 field:\n" +
            "  public int? Limit\n" +
            "1 event:\n" +
            "  public event Action<int?> Changed",
            await CallToolText("list_members", Question()));
    }

    [Fact]
    public async Task FindMethods_WritesANullableValueTypeAsCSharpDoes()
    {
        Assert.Equal(
            "16 methods:\n" +
            $"{Nullables}.Meter..ctor() → void\n" +
            $"{Nullables}.Meter.Fill(int?[]) → void\n" +
            $"{Nullables}.Meter.Fill(int[]) → void\n" +
            $"{Nullables}.Meter.Hold<T>(T) → void\n" +
            $"{Nullables}.Meter.Hold<T>(T?) → void\n" +
            $"{Nullables}.Meter.Keep<T>(T) → void\n" +
            $"{Nullables}.Meter.Keep<T>(T?) → void\n" +
            $"{Nullables}.Meter.Label(string) → void\n" +
            $"{Nullables}.Meter.Log(List<int>) → void\n" +
            $"{Nullables}.Meter.Log(List<int?>) → void\n" +
            $"{Nullables}.Meter.Read(int) → int\n" +
            $"{Nullables}.Meter.Read(int?) → int\n" +
            $"{Nullables}.Meter.Store(out int) → void\n" +
            $"{Nullables}.Meter.Store(out int?) → void\n" +
            $"static {Nullables}.Meter.op_Explicit(Meter) → int\n" +
            $"static {Nullables}.Meter.op_Explicit(Meter) → int?",
            await CallToolText("find_methods", Question()));
    }

    // The member, a parameter type or none, a return type or none, and the header line. Each
    // method of a pair is reached by the type written as its line writes it.
    public static TheoryData<string, string?, string?, string> Reads => new()
    {
        { "Read", "int?", null, $"// {Nullables}.Meter.Read(int?) → int" },
        { "Read", "int", null, $"// {Nullables}.Meter.Read(int) → int" },
        // The long form is read as well.
        { "Read", "Nullable<int>", null, $"// {Nullables}.Meter.Read(int?) → int" },
        { "Log", "List<int?>", null, $"// {Nullables}.Meter.Log(List<int?>) → void" },
        { "Log", "List<int>", null, $"// {Nullables}.Meter.Log(List<int>) → void" },
        { "Fill", "int?[]", null, $"// {Nullables}.Meter.Fill(int?[]) → void" },
        { "Fill", "int[]", null, $"// {Nullables}.Meter.Fill(int[]) → void" },
        { "Store", "out int?", null, $"// {Nullables}.Meter.Store(out int?) → void" },
        { "Store", "out int", null, $"// {Nullables}.Meter.Store(out int) → void" },
        { "op_Explicit", null, "int?", $"// {Nullables}.Meter.op_Explicit(Meter) → int?" },
        { "op_Explicit", null, "int", $"// {Nullables}.Meter.op_Explicit(Meter) → int" },
        // A class's type parameter beside a struct's nullable form: T? fits the class's type
        // parameter only loosely, since the code does not mark it T?, so it means the nullable form.
        { "Keep", "T?", null, $"// {Nullables}.Meter.Keep<T>(T?) → void" },
        { "Keep", "T", null, $"// {Nullables}.Meter.Keep<T>(T) → void" },
        // A struct's type parameter is a value type, which T? never fits.
        { "Hold", "T?", null, $"// {Nullables}.Meter.Hold<T>(T?) → void" },
        { "Hold", "T", null, $"// {Nullables}.Meter.Hold<T>(T) → void" },
        // After a reference type '?' is the annotation, which is passed over.
        { "Label", "string?", null, $"// {Nullables}.Meter.Label(string) → void" },
    };

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Decompile_ReadsAQuestionMarkAsCSharpDoes(
        string memberName, string? parameterType, string? returnType, string header)
    {
        string text = await CallToolText("decompile", Question(memberName, parameterType, returnType));

        Assert.Equal(header, text.Split('\n')[0]);
    }

    // find_methods holds a pattern loosely: int? still fits only the nullable type, T? fits a type
    // parameter that is no value type also where the code does not mark it T?, and a pattern
    // without a keyword fits a parameter passed by reference as well.
    [Theory]
    [InlineData("parameter_types", "int?", "2 methods:\n" +
        $"{Nullables}.Meter.Read(int?) → int\n" +
        $"{Nullables}.Meter.Store(out int?) → void")]
    [InlineData("parameter_types", "T?", "3 methods:\n" +
        $"{Nullables}.Meter.Hold<T>(T?) → void\n" +
        $"{Nullables}.Meter.Keep<T>(T) → void\n" +
        $"{Nullables}.Meter.Keep<T>(T?) → void")]
    [InlineData("parameter_types", "List<int?>", "1 method:\n" +
        $"{Nullables}.Meter.Log(List<int?>) → void")]
    [InlineData("parameter_types", "string?", "1 method:\n" +
        $"{Nullables}.Meter.Label(string) → void")]
    [InlineData("return_type", "int?", "1 method:\n" +
        $"static {Nullables}.Meter.op_Explicit(Meter) → int?")]
    public async Task FindMethods_ReadsAQuestionMarkAsCSharpDoes(string parameter, string pattern, string expected)
    {
        var question = Question();
        question[parameter] = parameter == "parameter_types" ? new[] { pattern } : pattern;

        Assert.Equal(expected, await CallToolText("find_methods", question));
    }

    // The member, a parameter type or none, a return type or none, and the member's reference.
    [Theory]
    [InlineData("Read", "int?", null, $"{Nullables}.Meter.Read(int?)")]
    [InlineData("op_Explicit", null, "int?", $"{Nullables}.Meter.op_Explicit(Meter) → int?")]
    public async Task Analyze_ReadsAndWritesANullableValueType(
        string memberName, string? parameterType, string? returnType, string reference)
    {
        var question = Question(memberName, parameterType, returnType);
        question["kind"] = "used_by";

        Assert.Equal(
            $"1 user of {reference} [declared]:\n" +
            $"{Nullables}.Reader.Total(Meter, int?)",
            await CallToolText("analyze", question));
    }

    // A nullable type before a member's name keeps its own name, which passes back as type_name;
    // in every other place it is written as C# writes it.
    [Fact]
    public async Task Analyze_Uses_WritesANullableValueTypeAsCSharpDoes_AndANullableTypeBeforeAMemberByItsName()
    {
        Assert.Equal(
            $"13 symbols used by {Nullables}.Reader.Total(Meter, int?) [declared]:\n" +
            $"{Nullables}.Meter.Fill(int?[])\n" +
            $"{Nullables}.Meter.Hold<int>(int?)\n" +
            $"{Nullables}.Meter.Keep<int>(int?)\n" +
            $"{Nullables}.Meter.Log(List<int?>)\n" +
            $"{Nullables}.Meter.Read(int)\n" +
            $"{Nullables}.Meter.Read(int?)\n" +
            $"{Nullables}.Meter.Store(out int?)\n" +
            $"{Nullables}.Meter.op_Explicit(Meter) → int?\n" +
            "System.Collections.Generic.List<int?>..ctor()\n" +
            "System.Collections.Generic.List<int?>.Add(int?)\n" +
            "System.Int32\n" +
            "System.Nullable<>\n" +
            "System.Nullable<int>.GetValueOrDefault()",
            await CallToolText("analyze", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = $"{Nullables}.Reader",
                ["member_name"] = "Total",
                ["kind"] = "uses",
            }));
    }

    [Theory]
    [InlineData("Read", "int?", null, $"{Nullables}.Meter.Read(int?) → int")]
    [InlineData("op_Explicit", null, "int?", $"{Nullables}.Meter.op_Explicit(Meter) → int?")]
    public async Task Compare_ReadsAndWritesANullableValueType(
        string memberName, string? parameterType, string? returnType, string reference)
    {
        var question = Question(memberName, parameterType, returnType);
        question.Remove("assembly");
        question["assembly_a"] = _callShapesPath;
        question["assembly_b"] = _callShapesPath;

        Assert.Equal(
            $"// {reference} — bodies are identical in both assemblies.",
            await CallToolText("compare", question));
    }

    // The runtime's code carries nullable annotations, which the decompiled code writes as '?':
    // string? after a reference type, and T? after a type parameter that may be a class.
    [Theory]
    [InlineData("System.String", "IsNullOrEmpty", new[] { "string?" },
        "// System.String.IsNullOrEmpty(string) → bool", "string? value")]
    [InlineData("System.Collections.Generic.EqualityComparer<>", "Equals", new[] { "T?", "T?" },
        "// System.Collections.Generic.EqualityComparer<>.Equals(T, T) → bool", "Equals(T? x, T? y)")]
    public async Task Decompile_ReadsAQuestionMarkTheDecompiledCodeWritesForAnAnnotation(
        string typeName, string memberName, string[] parameterTypes, string header, string code)
    {
        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["parameter_types"] = parameterTypes,
        });

        Assert.Equal(header, text.Split('\n')[0]);
        Assert.Contains(code, text);
    }

    private Dictionary<string, object?> Question() => new()
    {
        ["assembly"] = _callShapesPath,
        ["type_name"] = $"{Nullables}.Meter",
    };

    /// <summary>A question about a member of Meter, with each part of a hint that is given.</summary>
    private Dictionary<string, object?> Question(string memberName, string? parameterType, string? returnType)
    {
        var question = Question();
        question["member_name"] = memberName;
        if (parameterType is not null)
            question["parameter_types"] = new[] { parameterType };
        if (returnType is not null)
            question["return_type"] = returnType;
        return question;
    }

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
