using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class ListAllowedRootsTool
{
    [McpServerTool(Name = "list_allowed_roots", ReadOnly = true),
     Description("List the directories from which assemblies can be loaded. " +
        "Any 'assembly' parameter passed to other tools must point to a file inside one of these.")]
    public static string ListAllowedRoots(PathGuard guard)
    {
        if (guard.AllowedRoots.Count == 0)
            return "No allowed roots configured. The server cannot load any assemblies.";
        return string.Join("\n", guard.AllowedRoots);
    }
}
