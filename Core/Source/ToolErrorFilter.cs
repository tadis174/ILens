using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ILens;

/// <summary>
/// CallTool filter that converts any unhandled tool exception — including parameter-binding
/// failures thrown before the tool body runs — into a readable, agent-actionable error response.
///
/// Without this, the SDK's outer pipeline catches the exception and replies with the opaque
/// "An error occurred invoking 'X'." with no type or message. An agent receiving that has no
/// information to decide whether to retry, change arguments, or give up.
///
/// OperationCanceledException (when cancellation was requested) and McpProtocolException are
/// re-thrown so the framework's cancellation and protocol-error paths still work as designed.
/// </summary>
internal static class ToolErrorFilter
{
    public static void Install(McpServerOptions options)
    {
        // Defense in depth: today's SDK initializes Filters and Request, but a future
        // version could make them lazy. Coalesce so we don't NRE during configuration.
        options.Filters ??= new();
        options.Filters.Request ??= new();
        options.Filters.Request.CallToolFilters ??=
            new List<McpRequestFilter<CallToolRequestParams, CallToolResult>>();

        options.Filters.Request.CallToolFilters.Add(next => async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (McpProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock>
                    {
                        new TextContentBlock { Text = $"Error: {ex.GetType().Name}: {ex.Message}" }
                    }
                };
            }
        });
    }
}
