using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for asking who reads or writes a constant. The compiler copies a constant's
/// value into each place that uses it, so no instruction names the constant and an empty answer
/// would prove nothing. ExternalLib declares an integer constant, a decimal constant, which the
/// compiler stores as a static field its static constructor sets, an enum, a constant of a nested
/// class and one a class inherits, and ExternalUser's LimitReader uses one value of each.
/// ExternalLib.dll lands only in the References folder of the test output, so the shared server
/// cannot load it for ExternalUser while the second server can. Both find the installed .NET
/// libraries.
/// </summary>
public sealed class ConstantTests
    : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    private const string Reads =
        "the compiler copies its value into each place that uses it, so the compiled code does not show which " +
        "code reads it.";

    private const string Writes =
        "only its declaration sets it, and the compiler copies its value into each place that uses it.";

    private const string ShadeUsers =
        " Kind 'used_by' on ExternalLib.Shade lists the methods that work with that enum through a variable, " +
        "parameter or member of its type.";

    private const string CannotTell = "ILens cannot tell whether {0} is a constant, whose value the compiler copies " +
        "into each use: no assembly it found defines {1}";

    private readonly ILensServerFixture _shared;
    private readonly ReferencesServerFixture _references;
    private readonly string _userPath;
    private readonly string _libraryPath;

    public ConstantTests(ILensServerFixture shared, ReferencesServerFixture references)
    {
        _shared = shared;
        _references = references;
        string testOutput = Path.GetDirectoryName(shared.AssemblyPath)!;
        _userPath = Path.Combine(testOutput, "ExternalUser.dll");
        _libraryPath = Path.Combine(testOutput, "References", "ExternalLib.dll");
    }

    // A decimal constant is not a literal in the metadata, and its static constructor sets it,
    // which no source code does.
    [Theory]
    [InlineData("ExternalLib.Limits", "Most", "read_by", "ExternalLib.Limits.Most is a constant: " + Reads)]
    [InlineData("ExternalLib.Limits", "Most", "assigned_by", "ExternalLib.Limits.Most is a constant: " + Writes)]
    [InlineData("ExternalLib.Limits", "Rate", "read_by", "ExternalLib.Limits.Rate is a constant: " + Reads)]
    [InlineData("ExternalLib.Limits", "Rate", "assigned_by", "ExternalLib.Limits.Rate is a constant: " + Writes)]
    [InlineData("ExternalLib.Shade", "Dark", "read_by", "ExternalLib.Shade.Dark is a constant: " + Reads + ShadeUsers)]
    [InlineData("ExternalLib.Shade", "Dark", "assigned_by", "ExternalLib.Shade.Dark is a constant: " + Writes + ShadeUsers)]
    // Named through a class that inherits it, the constant is named on the class that declares it.
    [InlineData("ExternalLib.DerivedLimits", "Least", "read_by", "ExternalLib.BaseLimits.Least is a constant: " + Reads)]
    public async Task Analyze_AConstantTheAssemblyDefines_SaysItsValueIsCopiedIntoEachUse(
        string typeName, string memberName, string kind, string expected)
    {
        string error = await CallToolError(_shared.Client, "analyze", new()
        {
            ["assembly"] = _libraryPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
        });

        Assert.Equal("Error: ArgumentException: " + expected, error);
    }

    // ExternalUser names Shade only in signatures and the classes of constants nowhere. The second
    // server reads them from ExternalLib, and a field there takes the read and assign questions
    // alone, as one the inspected assembly defines does.
    [Theory]
    [InlineData("ExternalLib.Limits", "Most", "read_by", "ExternalLib.Limits.Most is a constant in ExternalLib: " + Reads)]
    [InlineData("ExternalLib.Limits", "Rate", "assigned_by", "ExternalLib.Limits.Rate is a constant in ExternalLib: " + Writes)]
    [InlineData("ExternalLib.Shade", "Dark", "read_by", "ExternalLib.Shade.Dark is a constant in ExternalLib: " + Reads + ShadeUsers)]
    // A name nothing references does not say that Deep is nested in Limits, not a class of a
    // namespace ExternalLib.Limits.
    [InlineData("ExternalLib.Limits.Deep", "Floor", "read_by", "ExternalLib.Limits.Deep.Floor is a constant in ExternalLib: " + Reads)]
    [InlineData("ExternalLib.DerivedLimits", "Least", "read_by", "ExternalLib.BaseLimits.Least is a constant in ExternalLib: " + Reads)]
    [InlineData("ExternalLib.Limits", "Most", "used_by",
        "Analysis kind 'used_by' is not valid for Field. Valid kinds for Field: read_by, assigned_by.")]
    public async Task Analyze_AConstantOfAReferencedType_ReadsItFromTheAssemblyThatDeclaresIt(
        string typeName, string memberName, string kind, string expected)
    {
        string error = await CallToolError(_references.Client, "analyze", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
        });

        Assert.Equal("Error: ArgumentException: " + expected, error);
    }

    [Fact]
    public async Task Analyze_AConstantOfACoreType_ReadsItFromTheInstalledLibraries()
    {
        string error = await CallToolError(_shared.Client, "analyze", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = "System.Int32",
            ["member_name"] = "MaxValue",
            ["kind"] = "read_by",
        });

        Assert.StartsWith("Error: ArgumentException: System.Int32.MaxValue is a constant in ", error);
        Assert.EndsWith(": " + Reads, error);
    }

    // The shared server cannot load ExternalLib, so it cannot rule out a constant.
    [Theory]
    [InlineData("ExternalLib.Shade", "Dark",
        "This assembly references no member of ExternalLib.Shade.",
        ", which comes from ExternalLib, Version=1.0.0.0.")]
    [InlineData("ExternalLib.Limits", "Most",
        "This assembly neither defines nor references a type of that name.",
        ".")]
    public async Task Analyze_AMemberOfATypeILensCannotLoad_SaysAConstantIsNotRuledOut(
        string typeName, string memberName, string reason, string origin)
    {
        string text = await CallToolText(_shared.Client, "analyze", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = "read_by",
        });

        Assert.Equal(
            $"No readers of {typeName}.{memberName}\n{reason}\n" +
            string.Format(CannotTell, memberName, typeName) + origin,
            text);
    }

    // ExternalLib rules a constant out: Shade has no member Missing, and DerivedLimits hides the
    // constant Step of its base class with a field that is not one.
    [Theory]
    [InlineData("ExternalLib.Shade", "Missing", "This assembly references no member of ExternalLib.Shade.")]
    [InlineData("ExternalLib.DerivedLimits", "Step", "This assembly neither defines nor references a type of that name.")]
    public async Task Analyze_AMemberNothingReferencesThatIsNoConstant_AnswersWithoutTheLine(
        string typeName, string memberName, string reason)
    {
        string text = await CallToolText(_references.Client, "analyze", new()
        {
            ["assembly"] = _userPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = "read_by",
        });

        Assert.Equal($"No readers of {typeName}.{memberName}\n{reason}", text);
    }

    private static async Task<string> CallToolText(
        McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }

    private static async Task<string> CallToolError(
        McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError == true, $"Tool '{toolName}' did not return an error: {text}");
        return text;
    }
}
