using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ILens;

/// <summary>
/// CallTool filter that rejects arguments a tool does not declare, instead of letting the
/// SDK silently drop them.
///
/// The MCP SDK binds each argument by name against the tool's input schema and ignores any
/// key it does not recognize. Because every ILens filter parameter is optional, a call whose
/// filter names are all wrong binds to nothing and runs completely unfiltered — <c>find_methods</c>
/// with a misremembered <c>typeName</c> / <c>methodName</c> pair returned every method in the
/// assembly, with no indication that neither filter had been applied. Silent substitution is the
/// worst failure mode for a tool whose value is being authoritative: an error costs one
/// round-trip, a plausible wrong answer costs a wrong decision, and callers use ILens precisely
/// because they cannot read the IL to notice the difference.
///
/// The check is schema-driven, so it covers every tool and needs no per-tool maintenance —
/// a new parameter is accepted the moment it appears in the signature.
/// </summary>
internal static class UnknownArgumentFilter
{
    /// <summary>
    /// Install the filter. Must be registered <em>after</em> <see cref="ToolErrorFilter"/>:
    /// the SDK builds its pipeline back-to-front (<c>McpServerImpl.BuildFilterPipeline</c>
    /// wraps from the last filter inward), so the first-registered filter is the outermost.
    /// ToolErrorFilter has to sit outside this one to catch and format what it throws;
    /// registered the other way round, the rejection would escape to the SDK's own handler
    /// and reach the agent as the opaque "An error occurred invoking 'X'."
    /// </summary>
    public static void Install(McpServerOptions options)
    {
        // Same defensive coalescing as ToolErrorFilter — today's SDK initializes these,
        // but a future version could make them lazy.
        options.Filters ??= new();
        options.Filters.Request ??= new();
        options.Filters.Request.CallToolFilters ??=
            new List<McpRequestFilter<CallToolRequestParams, CallToolResult>>();

        options.Filters.Request.CallToolFilters.Add(next => (request, cancellationToken) =>
        {
            Validate(request);
            return next(request, cancellationToken);
        });
    }

    private static void Validate(RequestContext<CallToolRequestParams> request)
    {
        var arguments = request.Params?.Arguments;
        if (arguments is null || arguments.Count == 0)
            return;

        // MatchedPrimitive is the tool the SDK resolved this call to. Anything else means
        // we cannot know the declared parameter set, so we have nothing to validate against
        // and must not invent a rejection.
        if (request.MatchedPrimitive is not McpServerTool tool)
            return;

        var schema = tool.ProtocolTool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        // A schema that opts into extra keys means the tool wants them; honor that rather
        // than override it. No ILens tool does this today — it keeps the filter correct if
        // one ever should.
        if (schema.TryGetProperty("additionalProperties", out var additional) &&
            additional.ValueKind is not JsonValueKind.False)
            return;

        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return;

        // Schema order is declaration order, which matches how the tool's own description
        // presents its parameters — more useful to a caller than an alphabetical list.
        var declared = properties.EnumerateObject().Select(p => p.Name).ToList();
        var unknown = arguments.Keys
            .Where(name => !declared.Contains(name, StringComparer.Ordinal))
            .ToList();

        if (unknown.Count == 0)
            return;

        var subject = unknown.Count == 1 ? "argument" : "arguments";
        throw new ArgumentException(
            $"Unknown {subject} {string.Join(", ", unknown.Select(u => $"'{u}'"))} " +
            $"for tool '{tool.ProtocolTool.Name}'. " +
            $"Valid arguments: {string.Join(", ", declared)}.");
    }
}
