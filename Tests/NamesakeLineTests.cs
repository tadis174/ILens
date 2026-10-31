using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the lines compare writes in full where their short form would let one name
/// stand for two classes: a member whose condition's class, and two members of one name whose type,
/// gave way to a class of the same name in another namespace while something else changed as well,
/// where the two short lines differ in the other change and read as if the class had stayed.
/// BaseShift.Namesakes holds the types, and BaseShift2 the same types with those changes.
/// </summary>
public sealed class NamesakeLineTests : IClassFixture<ILensServerFixture>
{
    private const string Namesakes = "BaseShift.Namesakes";
    private const string Alpha = Namesakes + ".Alpha";
    private const string Beta = Namesakes + ".Beta";
    private readonly ILensServerFixture _server;
    private readonly string _directory;

    public NamesakeLineTests(ILensServerFixture server)
    {
        _server = server;
        _directory = Path.GetDirectoryName(_server.AssemblyPath)!;
        foreach (var name in new[] { "BaseShift.dll", "BaseShift2.dll" })
        {
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fixture not found: {path}. Tests/Fixtures/ is expected to land here via Tests.csproj's " +
                    "ProjectReference.");
        }
    }

    [Fact]
    public async Task Compare_ChangedTypes_ListsTheTypesWhoseMembersChanged()
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = Path.Combine(_directory, "BaseShift.dll"),
            ["assembly_b"] = Path.Combine(_directory, "BaseShift2.dll"),
            ["namespace"] = Namesakes,
        });

        Assert.Equal(
            new[]
            {
                "10 changed types:",
                $"~ {Namesakes}.Conditions [declarations]",
                $"~ {Namesakes}.Fits [members]",
                $"+ {Namesakes}.Fresh",
                $"~ {Namesakes}.Kinds [members]",
                $"~ {Namesakes}.Limits [members]",
                $"~ {Namesakes}.LimitsBase [members]",
                $"~ {Namesakes}.Painted [bodies]",
                $"~ {Namesakes}.Palette [members]",
                $"~ {Namesakes}.PaletteBase [members]",
                $"~ {Namesakes}.Retyped [members]",
            },
            text.Split('\n'));
    }

    // The lines of one member's name are read together, and each that names a Badge or a Handler
    // beside the other class of that name among them is written in full, on both sides of a changed
    // declaration. Mix and Fit name both Badge classes in each build, so their lines are written in
    // full although the classes stayed. Keep's line names one Badge and stays short, and so do the
    // lines of Hold, whose other Badge only an overload the list does not name holds, and Painted's,
    // whose type Color shares its short name only with the member's own name, which the line writes
    // after its interface in either form.
    [Theory]
    [InlineData("Conditions", new[]
    {
        "~ public void Keep<T>(T item) where T : Badge → internal void Keep<T>(T item) where T : Badge",
        $"~ public void Mix<T>({Alpha}.Badge badge) where T : {Beta}.Badge → " +
            $"internal void Mix<T>({Alpha}.Badge badge) where T : {Beta}.Badge",
        $"~ public void Shape<T>(T item) where T : {Alpha}.Badge → internal void Shape<T>(T item) where T : {Beta}.Badge",
    })]
    [InlineData("Retyped", new[]
    {
        $"+ internal event {Beta}.Handler Changed",
        $"- public event {Alpha}.Handler Changed",
        "- public void Hold(Badge badge, int count)",
        "+ public void Hold(Badge badge, long count)",
        $"+ internal {Beta}.Badge Label {{ get; set; }}",
        $"- public {Alpha}.Badge Label {{ get; set; }}",
        $"- public void Swap({Alpha}.Badge badge, int count)",
        $"+ public void Swap({Beta}.Badge badge, long count)",
        $"+ internal {Beta}.Badge Tag",
        $"- public {Alpha}.Badge Tag",
        $"+ internal void Take({Beta}.Badge badge)",
        $"- public void Take({Alpha}.Badge badge)",
    })]
    [InlineData("Kinds", new[] { $"- public {Alpha}.Badge Tag", $"+ public {Beta}.Badge Tag {{ get; set; }}" })]
    [InlineData("Fits", new[]
    {
        $"+ public int Fit<T>({Alpha}.Badge badge) where T : {Beta}.Badge",
        $"- public void Fit<T>({Alpha}.Badge badge) where T : {Beta}.Badge",
    })]
    [InlineData("Painted", new[] { $"~ private Color {Namesakes}.IPainted.Color {{ get; }} [{Namesakes}.IPainted.get_Color]" })]
    public async Task Compare_MemberChanges_WriteALineInFull_WhereANameStandsForTwoClasses(string typeName, string[] lines)
    {
        string text = await CallToolText("compare", TypeArguments(typeName));

        var header = lines.Length == 1 ? "1 member change" : $"{lines.Length} member changes";
        Assert.Equal(new[] { $"{header} in {Namesakes}.{typeName}:" }.Concat(lines), text.Split('\n'));
    }

    [Fact]
    public async Task Compare_MemberChanges_OfATypeOnlyOneBuildHas_WriteALineInFull_WhereANameStandsForTwoClasses()
    {
        // The list of a type only B has reads the lines of one name together as well, so Mix,
        // which names both Badge classes, is written in full as in a type both builds have.
        string text = await CallToolText("compare", TypeArguments("Fresh"));

        Assert.Equal(
            new[]
            {
                $"2 member changes in {Namesakes}.Fresh, which only B has:",
                "+ public Fresh()",
                $"+ public void Mix<T>({Alpha}.Badge badge) where T : {Beta}.Badge",
            },
            text.Split('\n'));
    }

    // The line before the verdict names both declarations as the member list writes them, in full
    // by the same test. The verdict names the method as decompile's header does, which shows no
    // condition, so only the first line holds both classes.
    [Theory]
    [InlineData("Shape",
        $"// public void Shape<T>(T item) where T : {Alpha}.Badge → internal void Shape<T>(T item) where T : {Beta}.Badge\n" +
        $"// {Namesakes}.Conditions.Shape<T>(T) → void — bodies are identical in both assemblies.")]
    [InlineData("Mix",
        $"// public void Mix<T>({Alpha}.Badge badge) where T : {Beta}.Badge → " +
        $"internal void Mix<T>({Alpha}.Badge badge) where T : {Beta}.Badge\n" +
        $"// {Namesakes}.Conditions.Mix<T>(Badge) → void — bodies are identical in both assemblies.")]
    [InlineData("Keep",
        "// public void Keep<T>(T item) where T : Badge → internal void Keep<T>(T item) where T : Badge\n" +
        $"// {Namesakes}.Conditions.Keep<T>(T) → void — bodies are identical in both assemblies.")]
    public async Task Compare_Bodies_OfAChangedDeclaration_NameBothDeclarationsInFull_WhereANameStandsForTwoClasses(
        string memberName, string expected)
    {
        string text = await CallToolText("compare", Arguments("Conditions", memberName));

        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("Tag",
        $"Error: ArgumentException: {Namesakes}.Retyped.Tag is a field, which has no body, and A and B have " +
        $"different fields of that name: public {Alpha}.Badge Tag → internal {Beta}.Badge Tag; no constructor of " +
        $"{Namesakes}.Retyped sets it, and assigned_by lists what does.")]
    [InlineData("Label",
        $"Error: ArgumentException: {Namesakes}.Retyped.Label is a property, and A and B have different properties " +
        $"of that name: public {Alpha}.Badge Label {{ get; set; }} → internal {Beta}.Badge Label {{ get; set; }}; " +
        "compare its accessors, get_Label, set_Label.")]
    [InlineData("Changed",
        $"Error: ArgumentException: {Namesakes}.Retyped.Changed is an event, and A and B have different events of " +
        $"that name: public event {Alpha}.Handler Changed → internal event {Beta}.Handler Changed; compare its " +
        "accessors, add_Changed, remove_Changed.")]
    public async Task Compare_AMemberWithoutBody_NamesBothDeclarationsInFull_WhereANameStandsForTwoClasses(
        string memberName, string expected)
    {
        CallToolResult result = await _server.Client.CallToolAsync("compare", Arguments("Retyped", memberName));
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Equal(expected, text);
    }

    // Two methods are told apart by the types their declarations name, not by their names, which
    // carry their class. A constant found in a base class is named under the type asked about, and
    // the values of two constants, which name their enums, are written in full with their names.
    [Theory]
    [InlineData("Retyped", "Swap",
        $"// {Namesakes}.Retyped.Swap({Alpha}.Badge, int) → void in A and " +
        $"{Namesakes}.Retyped.Swap({Beta}.Badge, long) → void in B are different methods — " +
        "bodies are identical in both assemblies.")]
    [InlineData("Limits", "Max",
        $"// {Namesakes}.Limits.Max → {Alpha}.Level in A and " +
        $"{Namesakes}.Limits.Max → {Beta}.Level [inherited from {Namesakes}.LimitsBase] in B are different " +
        $"constants — {Alpha}.Level.High in A, {Beta}.Level.High in B.")]
    public async Task Compare_TwoMembersOfOneName_AreNamedInFull_WhereANameStandsForTwoClasses(
        string typeName, string memberName, string expected)
    {
        string text = await CallToolText("compare", Arguments(typeName, memberName));

        Assert.Equal(expected, text);
    }

    // A short name that a type shares only with the line's own name, or with a condition the line
    // does not show, makes no line stand for two classes: Palette's constant Mode of the type Mode,
    // which the line writes under its class, and two methods Fit whose conditions name the other
    // Badge, which their names leave out. Fit's diff follows the line.
    [Theory]
    [InlineData("Palette", "Mode",
        $"// {Namesakes}.Palette.Mode → Mode in A and {Namesakes}.Palette.Mode → Mode " +
        $"[inherited from {Namesakes}.PaletteBase] in B are different constants — Mode.Fast in A, Mode.Fast in B.")]
    [InlineData("Fits", "Fit",
        $"// {Namesakes}.Fits.Fit<T>(Badge) → void in A and {Namesakes}.Fits.Fit<T>(Badge) → int in B " +
        "are different methods\n")]
    public async Task Compare_TwoMembersOfOneName_AreNamedShort_WhereOnlyANameOrAConditionSharesTheShortName(
        string typeName, string memberName, string expectedStart)
    {
        string text = await CallToolText("compare", Arguments(typeName, memberName));

        Assert.StartsWith(expectedStart, text);
    }

    [Fact]
    public async Task Compare_MembersOfDifferentKinds_WriteBothDeclarationsInFull_WhereANameStandsForTwoClasses()
    {
        // The property's code follows the line, under a line that names its build.
        string text = await CallToolText("compare", Arguments("Kinds", "Tag"));

        Assert.StartsWith(
            $"// {Namesakes}.Kinds.Tag is a field in A and a property in B: " +
            $"public {Alpha}.Badge Tag → public {Beta}.Badge Tag {{ get; set; }}\n\n// B: {Namesakes}.Kinds.Tag\n",
            text);
    }

    private Dictionary<string, object?> TypeArguments(string typeName) => new()
    {
        ["assembly_a"] = Path.Combine(_directory, "BaseShift.dll"),
        ["assembly_b"] = Path.Combine(_directory, "BaseShift2.dll"),
        ["type_name"] = $"{Namesakes}.{typeName}",
    };

    private Dictionary<string, object?> Arguments(string typeName, string memberName)
    {
        var arguments = TypeArguments(typeName);
        arguments["member_name"] = memberName;
        return arguments;
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
