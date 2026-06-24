using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for find_harmony_dependencies. The fixture under
/// Tests/Fixtures/HarmonyMod/ compiles to HarmonyMod.dll alongside the
/// shared inspection target — exercising every pattern the scanner must
/// extract (typed and string-targeted attributes, overload-pinning param
/// arrays, TargetMethod/TargetMethods bodies, AccessTools/Traverse calls).
/// </summary>
public sealed class HarmonyTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;
    private readonly string _harmonyModPath;

    public HarmonyTests(ILensServerFixture server)
    {
        _server = server;
        _harmonyModPath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "HarmonyMod.dll");
        if (!File.Exists(_harmonyModPath))
            throw new FileNotFoundException(
                $"HarmonyMod fixture not found: {_harmonyModPath}. " +
                "Tests/Fixtures/HarmonyMod/ is expected to land here via Tests.csproj's " +
                "ProjectReference.");
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnNonHarmonyAssembly_ReturnsEmptyLists()
    {
        // ICSharpCode.Decompiler.dll doesn't use Harmony — every type is filtered
        // out by the scanner's HarmonyPatch attribute check.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _server.AssemblyPath,
        });
        Assert.Contains("\"patches\": []", text);
        Assert.Contains("\"fieldAccesses\": []", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_ExtractsTypedAttributePatch()
    {
        // TypedNamedPatch: [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
        // The named member is public, so the scanner classifies it TypedAttribute
        // (vs. StringTargeted for the private case below).
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("HarmonyMod.TargetB", text);
        Assert.Contains("SomeMethod", text);
        Assert.Contains("TypedAttribute", text);
        Assert.Contains("HarmonyMod.TypedNamedPatch", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_ExtractsStringTargetedPrivate()
    {
        // StringTargetedPatch: [HarmonyPatch(typeof(TargetA), "PrivateMethod")]
        // PrivateMethod is private — the scanner classifies it StringTargeted.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("PrivateMethod", text);
        Assert.Contains("StringTargeted", text);
        Assert.Contains("Postfix", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_ExtractsOverloadPinningParamTypes()
    {
        // OverloadPinnedPatch carries a param-types array narrowing Compute to
        // the (string) overload. The scanner must surface that array.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("Compute", text);
        Assert.Contains("System.String", text);
        Assert.Contains("OverloadPinnedPatch", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_ResolvesParameterlessConstructorTarget()
    {
        // TASK-23. ParameterlessConstructorPatch: [HarmonyPatch(typeof(TargetB),
        // MethodType.Constructor)]. ILSpy's high-level decoder empties the args
        // (the cross-assembly MethodType enum), so the scanner falls back to a
        // lenient raw-metadata decode and maps MethodType.Constructor → ".ctor".
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("ParameterlessConstructorPatch", text);
        Assert.Contains("HarmonyMod.TargetB", text);
        Assert.Contains(".ctor", text);
        // The bug this fixes produced targetType ":" "?" for this patch; assert the
        // recovered fully-qualified target name is present (it would be absent if
        // the lenient fallback didn't run).
        Assert.DoesNotContain("\"targetType\": \"?\"", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_ResolvesConstructorTargetWithParamTypes()
    {
        // TASK-23 acceptance #2. ConstructorPatch: [HarmonyPatch(typeof(TargetA),
        // MethodType.Constructor, new Type[] { typeof(int) })]. The lenient decode
        // must recover both the .ctor target and the param-type array.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("HarmonyMod.ConstructorPatch", text);
        Assert.Contains("HarmonyMod.TargetA", text);
        Assert.Contains("System.Int32", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_UnmanglesGenericConstructorParamType()
    {
        // TASK-23. GenericConstructorPatch pins a List<int> constructor; the raw
        // attribute blob stores the param type as a reflection name
        // (List`1[[Int32, …]]). NormalizeTypeName must render it in angle-bracket
        // form rather than leaking the backtick/bracket mangling.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("GenericConstructorPatch", text);
        Assert.Contains("List<", text);
        Assert.DoesNotContain("List`1", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_ResolvesTargetMethodBody()
    {
        // TargetMethodPatch's body is `return AccessTools.Method(typeof(TargetA),
        // "Compute", new Type[] { typeof(int) })`. The scanner decompiles it and
        // pulls out (TargetA, Compute, [int]) with resolutionKind=TargetMethod.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("TargetMethodPatch", text);
        Assert.Contains("\"TargetMethod\"", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_ResolvesTargetMethodsBody()
    {
        // TargetMethodsPatch yields two AccessTools.Method calls; both should
        // surface as resolutionKind=TargetMethods.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("TargetMethodsPatch", text);
        Assert.Contains("\"TargetMethods\"", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_FlagsDynamicTargetMethodAsDynamic()
    {
        // DynamicTargetPatch's body has two AccessTools.Method calls under a
        // conditional. The scanner should mark this DynamicTargetMethod so the
        // caller knows the binding is branch-dependent.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("DynamicTargetPatch", text);
        Assert.Contains("\"DynamicTargetMethod\"", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_ExtractsAccessToolsField()
    {
        // FieldAccessPatch initializes a static field with
        // AccessTools.Field(typeof(TargetA), "viewHeight"). It lands inside
        // the patch class's .cctor; the scanner walks all class methods.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("viewHeight", text);
        Assert.Contains("AccessToolsField", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_ExtractsFieldRefAccess()
    {
        // FieldRefAccessPatch uses AccessTools.FieldRefAccess<TargetB, int>("counter").
        // The generic type T becomes the contextType.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("counter", text);
        Assert.Contains("FieldRefAccess", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnFixture_ExtractsTraverseField()
    {
        // TraverseFieldPatch calls Traverse.Create(__instance).Field("Prop").
        // __instance is the patch method's first parameter; its declared type
        // (TargetA) becomes the contextType.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
        });
        Assert.Contains("Prop", text);
        Assert.Contains("TraverseField", text);
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
