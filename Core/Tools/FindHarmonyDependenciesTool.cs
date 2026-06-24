using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class FindHarmonyDependenciesTool
{
    [McpServerTool(Name = "find_harmony_dependencies", ReadOnly = true),
     Description("Extract the full reflective surface of a Harmony-patching assembly as JSON: every " +
        "patch target (from [HarmonyPatch] attributes and TargetMethod/TargetMethods bodies) and every " +
        "reflective field access (AccessTools.Field, AccessTools.FieldRefAccess, Traverse.Field). " +
        "Used to answer \"do my patches still bind on game version X?\" — pair with compare_method against " +
        "two versions of the host game's assembly. " +
        "Output schema: { patches: [{ targetType, targetMember, paramTypes?, methodType?, patchType, " +
        "resolutionKind, patchClass, patchSite }], fieldAccesses: [{ contextType, fieldName, accessor, " +
        "patchSite }] }. " +
        "resolutionKind ∈ TypedAttribute | StringTargeted | Attribute | TargetMethod | TargetMethods | DynamicTargetMethod. " +
        "accessor ∈ AccessToolsField | FieldRefAccess | TraverseField. " +
        "Note: the TypedAttribute vs StringTargeted distinction needs to probe the target type for member visibility, " +
        "so it only fires when the target lives in the same assembly being scanned. Cross-assembly targets (the " +
        "typical case for a mod patching a host game's DLLs) fall back to Attribute regardless.")]
    public static string FindHarmonyDependencies(
        AssemblyHostRegistry registry,
        [Description("Path to the Harmony-patching assembly to scan (must be under an allowed root).")] string assembly)
    {
        var host = registry.GetOrLoad(assembly);
        var scanner = new HarmonyDependencyScanner(host);
        var result = scanner.Scan();

        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Generic param types render as List<T>; the default HTML-safe encoder
            // would escape the angle brackets (< / >). This output is
            // read by an agent, not embedded in HTML, so emit them literally.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }
}
