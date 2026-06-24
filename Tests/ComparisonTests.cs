using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the cross-assembly comparison tools — list_changed_types,
/// compare_type, compare_method. Drives the real ILens server. The shared fixture
/// already loads ICSharpCode.Decompiler.dll as one inspection target; the second
/// target is whatever sibling DLL the test points at under the same allow-root
/// (the test bin directory, which the fixture exposes via AssemblyPath).
/// </summary>
public sealed class ComparisonTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;
    private readonly string _secondAssemblyPath;

    public ComparisonTests(ILensServerFixture server)
    {
        _server = server;
        // ModelContextProtocol.dll lands in the test bin via the package reference
        // in Tests.csproj — picked because it shares an allow-root with the
        // decompiler DLL and contains a public API surface distinct from it.
        _secondAssemblyPath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "ModelContextProtocol.dll");
        if (!File.Exists(_secondAssemblyPath))
            throw new FileNotFoundException(
                $"Second comparison target not found: {_secondAssemblyPath}. " +
                "Expected ModelContextProtocol.dll alongside ICSharpCode.Decompiler.dll " +
                "in the test output.");
    }

    [Fact]
    public async Task ListChangedTypes_OnByteIdenticalAssemblies_ReportsNoChanges()
    {
        // Acceptance #1: comparing an assembly against itself (same path → same
        // bytes → byte-identical PE) returns an empty change set.
        string text = await CallToolText("list_changed_types", new()
        {
            ["assemblyA"] = _server.AssemblyPath,
            ["assemblyB"] = _server.AssemblyPath,
        });
        Assert.Contains("No type changes", text);
    }

    [Fact]
    public async Task ListChangedTypes_AcrossDifferentAssemblies_ReportsChanges()
    {
        // Two unrelated assemblies share almost no types, so the result must be
        // non-empty and dominated by Added/Removed. Shape-only — exact counts
        // drift as either package upstream version moves.
        string text = await CallToolText("list_changed_types", new()
        {
            ["assemblyA"] = _server.AssemblyPath,
            ["assemblyB"] = _secondAssemblyPath,
        });
        Assert.DoesNotContain("No type changes", text);
        // Both directions must show up — Decompiler-only types as Removed,
        // MCP-only types as Added.
        Assert.Contains("Added:", text);
        Assert.Contains("Removed:", text);
    }

    [Fact]
    public async Task ListChangedTypes_WithNamespaceFilter_ScopesTheWalk()
    {
        // Filtering to a namespace that exists in only one assembly should still
        // surface its types as Added/Removed, but exclude everything outside it.
        string text = await CallToolText("list_changed_types", new()
        {
            ["assemblyA"] = _server.AssemblyPath,
            ["assemblyB"] = _secondAssemblyPath,
            ["namespaceFilter"] = "ICSharpCode.Decompiler.TypeSystem",
        });
        // Decompiler-only namespace — every type from it shows up as Removed,
        // and no types from MCP's namespaces leak in.
        Assert.Contains("Removed:", text);
        Assert.DoesNotContain("ModelContextProtocol", text);
    }

    [Fact]
    public async Task CompareType_OnByteIdenticalAssemblies_ReportsIdentical()
    {
        // Acceptance #2: a structured diff on a type that exists identically in
        // both assemblies returns the empty-diff message and does not invoke
        // the C# decompiler (asserting "no decompile" indirectly via the
        // identical-message path).
        string text = await CallToolText("compare_type", new()
        {
            ["assemblyA"] = _server.AssemblyPath,
            ["assemblyB"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
        });
        Assert.Contains("identical in both assemblies", text);
    }

    [Fact]
    public async Task CompareType_OnTypePresentOnlyInOneAssembly_LabelsSide()
    {
        // A type that lives in A but not B emits the "present only in A" header
        // and lists every member of that type as removed.
        string text = await CallToolText("compare_type", new()
        {
            ["assemblyA"] = _server.AssemblyPath,
            ["assemblyB"] = _secondAssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
        });
        Assert.Contains("present only in A", text);
    }

    [Fact]
    public async Task CompareType_OnTypeMissingFromBoth_ReturnsError()
    {
        CallToolResult result = await _server.Client.CallToolAsync("compare_type",
            new Dictionary<string, object?>
            {
                ["assemblyA"] = _server.AssemblyPath,
                ["assemblyB"] = _secondAssemblyPath,
                ["typeName"] = "Some.NameSpace.NoSuchType",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected not-found rejection, got: {text}");
        Assert.Contains("not found", text);
    }

    [Fact]
    public async Task CompareMethod_OnByteIdenticalAssemblies_ReportsIdenticalBodies()
    {
        // Self-comparison on a known method must emit the "identical" message and
        // a single body copy (not two side-by-side blocks).
        string text = await CallToolText("compare_method", new()
        {
            ["assemblyA"] = _server.AssemblyPath,
            ["assemblyB"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["methodName"] = "get_ThrowOnAssemblyResolveErrors",
        });
        Assert.Contains("identical in both assemblies", text);
        // Two side-by-side blocks would carry the "=== A:" / "=== B:" markers;
        // the identical path emits neither.
        Assert.DoesNotContain("=== A:", text);
        Assert.DoesNotContain("=== B:", text);
    }

    [Fact]
    public async Task CompareMethod_WithIlFormat_ReturnsIlDisassembly()
    {
        // Acceptance #3: format=il produces the IL text path. A self-comparison
        // still emits a single body; we assert that it looks like IL — at least
        // one opcode that the disassembler renders. 'ret' is in every method.
        string text = await CallToolText("compare_method", new()
        {
            ["assemblyA"] = _server.AssemblyPath,
            ["assemblyB"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["methodName"] = "get_ThrowOnAssemblyResolveErrors",
            ["format"] = "il",
        });
        Assert.Contains("identical in both assemblies", text);
        Assert.Contains("ret", text);
    }

    [Fact]
    public async Task CompareType_OnRvaShiftedTwin_DoesNotFlagIdenticalBodies()
    {
        // TASK-21 regression. HarmonyMod2.dll is HarmonyMod.dll's source plus
        // an earlier-declared Padding class — every method on TargetA/TargetB
        // produces byte-identical IL but lands at a different file RVA. Without
        // the RVA-header strip in AssemblyHost.DisassembleMethodBody, every
        // such method would surface here as "~ body changed". With the strip,
        // none should.
        var harmonyMod = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!, "HarmonyMod.dll");
        var harmonyMod2 = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!, "HarmonyMod2.dll");
        if (!File.Exists(harmonyMod) || !File.Exists(harmonyMod2))
            throw new FileNotFoundException(
                $"HarmonyMod fixtures not found: {harmonyMod}, {harmonyMod2}. " +
                "Both projects under Tests/Fixtures/ are expected to land here " +
                "via Tests.csproj ProjectReferences.");

        string text = await CallToolText("compare_type", new()
        {
            ["assemblyA"] = harmonyMod,
            ["assemblyB"] = harmonyMod2,
            ["typeName"] = "HarmonyMod.TargetA",
        });
        // Same TargetA source on both sides → no member-set diff, no body diff.
        // The "identical" branch is what compare_type returns only when every
        // member matched and every body-equality check returned true.
        Assert.Contains("identical in both assemblies", text);
    }

    [Fact]
    public async Task CompareMethod_WithUnknownFormat_ReturnsError()
    {
        CallToolResult result = await _server.Client.CallToolAsync("compare_method",
            new Dictionary<string, object?>
            {
                ["assemblyA"] = _server.AssemblyPath,
                ["assemblyB"] = _server.AssemblyPath,
                ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
                ["methodName"] = "get_ThrowOnAssemblyResolveErrors",
                ["format"] = "json",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected unknown-format rejection, got: {text}");
        Assert.Contains("format", text);
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
