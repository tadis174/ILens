using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ILens;

/// <summary>
/// CallTool filter that wraps a lone scalar in a single-element array when the tool's own
/// input schema declares that parameter as an array.
///
/// Passing <c>"Field"</c> where <c>["Field"]</c> is expected is a routine slip, and the SDK's
/// parameter binder answers it with a raw serializer failure — <c>The JSON value could not be
/// converted to ILens.MemberKind[]. Path: $ | LineNumber: 0 | BytePositionInLine: 7</c> — which
/// names an internal type and a byte offset in a document the caller never wrote, and does not
/// say what shape would have worked. The single-value case has exactly one possible meaning, so
/// accepting it costs nothing: unlike <see cref="UnknownArgumentFilter"/>, which rejects
/// arguments precisely because a dropped filter yields a plausible wrong answer, there is no
/// second reading here to be wrong about.
///
/// The check is schema-driven, so it covers every array parameter on every tool with no per-tool
/// maintenance — <c>kinds</c> on <c>list_members</c> today, and <c>parameterTypes</c> on
/// <c>analyze</c> / <c>decompile_method</c> / <c>find_methods</c>.
/// </summary>
internal static class ArrayCoercionFilter
{
    /// <summary>
    /// Install the filter. Register it <em>after</em> <see cref="ToolErrorFilter"/> so that
    /// filter stays outermost and formats anything thrown here, and after
    /// <see cref="UnknownArgumentFilter"/> so an unrecognized argument is rejected on the
    /// name it was actually given rather than after its value has been rewritten.
    /// </summary>
    public static void Install(McpServerOptions options)
    {
        // Same defensive coalescing as the other filters — today's SDK initializes these,
        // but a future version could make them lazy.
        options.Filters ??= new();
        options.Filters.Request ??= new();
        options.Filters.Request.CallToolFilters ??=
            new List<McpRequestFilter<CallToolRequestParams, CallToolResult>>();

        options.Filters.Request.CallToolFilters.Add(next => (request, cancellationToken) =>
        {
            Coerce(request);
            return next(request, cancellationToken);
        });
    }

    private static void Coerce(RequestContext<CallToolRequestParams> request)
    {
        var arguments = request.Params?.Arguments;
        if (arguments is null || arguments.Count == 0)
            return;

        // MatchedPrimitive is the tool the SDK resolved this call to. Anything else means
        // we cannot know the declared parameter shapes, so there is nothing to coerce against.
        if (request.MatchedPrimitive is not McpServerTool tool)
            return;

        var schema = tool.ProtocolTool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return;

        // Build a copy only once something needs rewriting — the overwhelmingly common case
        // is a well-formed call, which should not pay for a dictionary allocation.
        Dictionary<string, JsonElement> coerced = null;
        foreach (var (name, value) in arguments)
        {
            if (!IsScalar(value)) continue;
            if (!properties.TryGetProperty(name, out var parameter)) continue;
            if (!DeclaresArray(parameter)) continue;

            coerced ??= new Dictionary<string, JsonElement>(arguments);
            coerced[name] = Wrap(value);
        }

        if (coerced != null)
            request.Params.Arguments = coerced;
    }

    /// <summary>
    /// True for the JSON values whose promotion to a one-element array has a single reading.
    /// Null is excluded — for an optional parameter it means "omit", not "a list holding
    /// null" — and so are objects and arrays, where the intent is genuinely unclear.
    /// </summary>
    private static bool IsScalar(JsonElement value) =>
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            or JsonValueKind.True or JsonValueKind.False;

    /// <summary>
    /// True if a schema parameter declares an array. A nullable array is emitted as
    /// <c>"type": ["array", "null"]</c>, so both the string and list forms are checked.
    /// </summary>
    private static bool DeclaresArray(JsonElement parameter)
    {
        if (parameter.ValueKind != JsonValueKind.Object ||
            !parameter.TryGetProperty("type", out var type))
            return false;

        return type.ValueKind switch
        {
            JsonValueKind.String => type.ValueEquals("array"),
            JsonValueKind.Array => type.EnumerateArray().Any(
                t => t.ValueKind == JsonValueKind.String && t.ValueEquals("array")),
            _ => false,
        };
    }

    /// <summary>
    /// Wrap a scalar in a one-element array. The result is cloned off its parsed document
    /// so it stays valid once that document is disposed.
    /// </summary>
    private static JsonElement Wrap(JsonElement scalar)
    {
        using var document = JsonDocument.Parse($"[{scalar.GetRawText()}]");
        return document.RootElement.Clone();
    }
}
