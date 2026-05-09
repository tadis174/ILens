# ILens

An MCP server that lets AI agents inspect compiled .NET assemblies — list types, decompile methods, run cross-reference analysis — without burning tokens on full-file dumps.

## Why

The default fallback for an AI agent inspecting a `.dll` is to shell out to `ildasm` (IL dump) or `ilspycmd` (full-C# decompile). Both dump an entire file as text, and that output stays in the agent's context as input tokens on every subsequent turn — quickly thousands of tokens spent inspecting a single class.

ILens turns assembly inspection into bounded, targeted lookups: list types in a namespace, summarize one type's public surface, find methods by name, decompile a single method body, run cross-reference analysis (callers, overrides, implementations, extension methods, attribute usage). Each call returns a focused response — typically 10² to 10³ tokens — making it cheap enough to leave registered as an always-available tool.

Built on [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) and [ICSharpCode.ILSpyX](https://github.com/icsharpcode/ILSpy).

## Installation

Download the latest release ZIP from the [Releases page](https://github.com/tadis174/ILens/releases/latest) and extract it anywhere. The archive contains a self-contained `ILens.exe` (~40 MB) plus `LICENSE`, this `README.md`, `guide.html`, and a `third-party-licenses/` directory.

**System requirements**: Windows 10/11, x64. No .NET runtime install required — the binary bundles its own.

## Setup

### Step 1: Register the server

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

### Step 2: Tell Claude to use it

Without this, Claude won't actually pick ILens — it'll keep reaching for `ildasm` via Bash, reading `.dll` files directly, or web-searching for source. Drop the following block into your project's `CLAUDE.md` and replace `<allow-root>` with the directory configured above:

````markdown
## Inspecting .NET assemblies

Assemblies under `<allow-root>` are reachable through the `ilens` MCP server.
Prefer ILens tools over running `ildasm` / `ilspycmd` via Bash, reading `.dll`
files directly, or web-searching for source.

- Discovery: `search_types` (substring match), `list_types` (whole namespace),
  `find_methods` (signature search).
- Reading: `summarize_type` (public surface, no bodies), `list_members`
  (filtered surface), `decompile_type` (full C#), `decompile_method` (single
  method body).
- Cross-references: `analyze` with `kind` set to one of `UsedBy`,
  `InstantiatedBy`, `ExposedBy`, `ExtensionMethods`, `AppliedTo`,
  `OverriddenBy`, `ImplementedBy`, `Uses`, `Implements`, `ReadBy`,
  `AssignedBy`. Valid kinds depend on the symbol category.
````

#### Per-line walkthrough

- The first paragraph is the load-bearing prefer-MCP rule. Without it, models default to `Bash`.
- The discovery bullet routes "I don't know the full name" tasks to the right tool: a partial name goes to `search_types`, a known namespace goes to `list_types`, a method shape goes to `find_methods`.
- The reading bullet escalates from cheapest to most expensive: `summarize_type` first, `list_members` when only part of the surface is needed, `decompile_method` for one method body, `decompile_type` only when full source is required.
- The `analyze` bullet enumerates the `kind` enum so the model picks values the schema accepts — using one that does not apply to the symbol category produces an error like `Analysis kind 'ReadBy' is not valid for Method`.

## Full reference

A self-contained HTML user guide covers what this README does not:

- **Per-tool reference** — every tool, every parameter (with `required` / `optional` flags and types), return shape, and full list of error conditions
- **Alternative integration paths** — `claude mcp add` CLI command, Claude Desktop config
- **Security model** — what's enforced (`--allow-root` mandatory, no traversal, no network, 200 MB cap, read-only), and what's not
- **Troubleshooting** — error string → likely cause mapping

Read it online at **https://tadis174.github.io/ILens/guide.html**, or open the bundled `guide.html` from the release ZIP.

## Building from source

If you'd rather build the binary yourself:

```bash
dotnet build -c Release
dotnet publish -c Release
```

The publish target produces `Build/dist/ILens.exe` along with `LICENSE`, `README.md`, `guide.html`, and `third-party-licenses/`. The working tree must be clean before publishing, and `Build/doc/guide.html` must exist (run the project's `/doc` skill to regenerate it).

## License

ILens is released under the [MIT License](LICENSE). Third-party dependencies are listed in `third-party-licenses/manifest.json`, which is regenerated and included in each release.
