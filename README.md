# ILens

An MCP server that lets AI agents inspect compiled .NET assemblies — list types, decompile methods, run cross-reference analysis — without burning tokens on full-file dumps.

## Why

The default fallback for an AI agent inspecting a `.dll` is to shell out to `ildasm` (IL dump) or `ilspycmd` (full-C# decompile). Both dump an entire file as text, and that output stays in the agent's context as input tokens on every subsequent turn — quickly thousands of tokens spent inspecting a single class.

ILens turns assembly inspection into bounded, targeted lookups: list types in a namespace, summarize one type's public surface, find methods by name, decompile a single method body, run cross-reference analysis (callers, overrides, implementations, extension methods, attribute usage). Each call returns a focused response — typically 10² to 10³ tokens — making it cheap enough to leave registered as an always-available tool.

Built on [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) and [ICSharpCode.ILSpyX](https://github.com/icsharpcode/ILSpy).

## Installation

Download the latest release ZIP from the [Releases page](https://github.com/tadis174/ILens/releases/latest) and extract it anywhere. The archive contains a self-contained `ILens.exe` (~40 MB) plus `LICENSE`, this `README.md`, `guide.html`, and a `third-party-licenses/` directory.

**System requirements**: Windows 10/11, x64. No .NET runtime install required — the binary bundles its own.

## Integration with Claude Code

Add an entry to `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "ilens": {
      "command": "C:\\path\\to\\ILens.exe",
      "args": [
        "--allow-root", "C:\\path\\to\\dlls"
      ]
    }
  }
}
```

Each `--allow-root` flag adds a directory tree from which assemblies may be loaded. **Without any `--allow-root` flags, the server cannot load any assemblies.** Tool calls supply the assembly path per request; the server validates that the path is inside one of the configured roots, rejects path traversal (`..`), and refuses files larger than 200 MB.

Tool calls then specify which assembly to inspect:

```
list_types(assembly="C:\\path\\to\\dlls\\MyApp.dll", namespaceName="MyApp.Models")
```

The `list_allowed_roots` tool surfaces the configured roots so the agent can discover what's reachable.

> **Heads up**: Claude Code only registers MCP servers at session start. Editing `.mcp.json` inside a running session does not pick up the new server — start a new session.

## Full reference

A self-contained HTML user guide covers what this README does not:

- **Per-tool reference** — every tool, every parameter (with `required` / `optional` flags and types), return shape, and full list of error conditions
- **Claude Desktop integration** — different config path than Claude Code, no per-project `CLAUDE.md`
- **Project-level `CLAUDE.md` snippet** — paste-ready text the consuming project drops into its own `CLAUDE.md` so Claude actively prefers ILens tools over `ildasm` / `Read` / web search
- **Security model** — what's enforced (`--allow-root` mandatory, no traversal, no network, 200 MB cap, read-only), and what's not
- **Troubleshooting** — error string → likely cause mapping

Read it online at **https://tadis174.github.io/ILens/guide.html**, or open the bundled `guide.html` from the release ZIP.

## Building from source

If you'd rather build the binary yourself:

```bash
dotnet tool restore
dotnet build -c Release
dotnet publish -c Release
```

The publish target produces `Build/dist/ILens.exe` along with `LICENSE`, `README.md`, `guide.html`, and `third-party-licenses/`. The working tree must be clean before publishing, and `Build/doc/guide.html` must exist (run the project's `/doc` skill to regenerate it).

## License

ILens is released under the [MIT License](LICENSE). Third-party dependencies are listed in `third-party-licenses/manifest.json`, which is regenerated and included in each release.
