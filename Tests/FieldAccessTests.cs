using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for read_by and assigned_by on fields used through the address the
/// compiler takes. The fixture under Tests/Fixtures/FieldAccess/ compiles to FieldAccess.dll
/// alongside the shared inspection target; each method of its <c>Holder</c> uses one field in
/// one way, and no method loads or stores a field directly unless its name says so.
/// </summary>
public sealed class FieldAccessTests : IClassFixture<ILensServerFixture>
{
    private const string Holder = "FieldAccess.Holder";

    // A field, a kind, and every line of the answer after its header, in the order analyze
    // sorts them. A method absent from a list is one the answer must leave out: IgnoreNumber
    // and NothingOnTally take an address without reading or writing through it.
    public static TheoryData<string, string, string, string[]> Questions => new()
    {
        // A call on a number reads it. A reference passed to a method of the assembly counts as
        // what that method's body does with it, and an out argument writes it. A reference held
        // in a local is followed to its uses, and a lambda's use counts for the method it is in.
        {
            Holder, "Number", "assigned_by",
            [
                "FieldAccess.Holder.BumpNumber()",
                "FieldAccess.Holder.FillNumber()",
                "FieldAccess.Holder.OverwriteNumber()",
                "FieldAccess.Holder.ParseNumber(string) (may write: passes it by ref to System.Int32.TryParse(string, ref int))",
                "FieldAccess.Holder.SetThroughRefLocal(bool)",
            ]
        },
        {
            Holder, "Number", "read_by",
            [
                "FieldAccess.Holder.BumpNumber()",
                "FieldAccess.Holder.LookAtNumber()",
                "FieldAccess.Holder.NumberLater()",
                "FieldAccess.Holder.NumberText()",
                "FieldAccess.Holder.ParseNumber(string) (may read: passes it by ref to System.Int32.TryParse(string, ref int))",
            ]
        },
        { Holder, "Total", "assigned_by", [] },
        { Holder, "Total", "read_by", ["FieldAccess.Holder.TotalText()"] },
        // A call on an enum runs on a boxed copy.
        { Holder, "Tone", "assigned_by", [] },
        { Holder, "Tone", "read_by", ["FieldAccess.Holder.ToneText()"] },
        // A struct of the assembly: reading or writing a member, clearing it, a call the struct
        // does not implement itself, a readonly member, an in argument, and calls that count as
        // what the called method's body does with the value.
        {
            Holder, "Tally", "assigned_by",
            [
                "FieldAccess.Holder.ClearTally()",
                "FieldAccess.Holder.IncrementTally()",
                "FieldAccess.Holder.ResetTally()",
                "FieldAccess.Holder.SetTallyValue()",
            ]
        },
        {
            Holder, "Tally", "read_by",
            [
                "FieldAccess.Holder.IncrementTally()",
                "FieldAccess.Holder.PassTallyIn()",
                "FieldAccess.Holder.PeekTally()",
                "FieldAccess.Holder.PeekTallyReadonly()",
                "FieldAccess.Holder.TallyText()",
                "FieldAccess.Holder.TallyValue()",
            ]
        },
        { Holder, "Ice", "assigned_by", [] },
        { Holder, "Ice", "read_by", ["FieldAccess.Holder.IceTwice()"] },
        // A framework struct whose methods never change its value.
        { Holder, "Maybe", "assigned_by", ["FieldAccess.Holder.ClearMaybe()"] },
        { Holder, "Maybe", "read_by", ["FieldAccess.Holder.HasMaybe()"] },
        // What no body in the assembly shows counts as both, and the line says what received
        // the address: a method of another assembly, methods that call each other, a reference
        // returned from the method, and a call on a type parameter.
        {
            Holder, "Cursor", "assigned_by",
            ["FieldAccess.Holder.AdvanceCursor() (may write: calls System.Collections.Generic.List<int>.Enumerator.MoveNext() on it)"]
        },
        {
            Holder, "Shared", "assigned_by",
            [
                "FieldAccess.Holder.AddShared() (may write: passes it by ref to System.Threading.Interlocked.Increment(ref int))",
                "FieldAccess.Holder.SetAndAddShared()",
            ]
        },
        {
            Holder, "Shared", "read_by",
            [
                "FieldAccess.Holder.AddShared() (may read: passes it by ref to System.Threading.Interlocked.Increment(ref int))",
                "FieldAccess.Holder.SetAndAddShared() (may read: passes it by ref to System.Threading.Interlocked.Increment(ref int))",
            ]
        },
        { Holder, "Loop", "assigned_by", ["FieldAccess.Holder.SpinLoop() (may write: calls FieldAccess.Ring.Spin(int) on it)"] },
        { Holder, "Kept", "assigned_by", ["FieldAccess.Holder.KeptRef() (may write: keeps a reference to it)"] },
        { "FieldAccess.Box<>", "Item", "assigned_by", ["FieldAccess.Box<>.ItemText() (may write: calls T.ToString() on it)"] },
    };

    private readonly ILensServerFixture _server;
    private readonly string _fieldAccessPath;

    public FieldAccessTests(ILensServerFixture server)
    {
        _server = server;
        _fieldAccessPath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "FieldAccess.dll");
        if (!File.Exists(_fieldAccessPath))
            throw new FileNotFoundException(
                $"FieldAccess fixture not found: {_fieldAccessPath}. " +
                "Tests/Fixtures/FieldAccess/ is expected to land here via Tests.csproj's " +
                "ProjectReference.");
    }

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task Analyze_OnAFieldUsedThroughItsAddress_CountsWhatEachUseDoes(
        string typeName, string fieldName, string kind, string[] expected)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _fieldAccessPath,
            ["type_name"] = typeName,
            ["member_name"] = fieldName,
            ["kind"] = kind,
        });

        Assert.Equal(expected, ResultLines(text));
    }

    /// <summary>The result lines of an <c>analyze</c> response, with its header line dropped.</summary>
    private static string[] ResultLines(string analyzeOutput) =>
        analyzeOutput.Split('\n').Skip(1).ToArray();

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
