# ILens

An MCP server that lets AI agents inspect compiled .NET assemblies — list types, decompile methods, run cross-reference analysis — without burning tokens on full-file dumps.

## Why

The default fallback for an AI agent inspecting a compiled .NET assembly is to shell out to `ildasm` (IL dump) or `ilspycmd` (full-C# decompile). Both dump an entire file as text, and that output stays in the agent's context as input tokens on every subsequent turn — quickly thousands of tokens spent inspecting a single class.

| Task | `ilspycmd` cost | ILens tool | ILens cost |
|------|----------------:|------------|-----------:|
| List the public types in a namespace | 1,041,476 tokens | `list_types` | 1,196 tokens |
| See the API surface of one mid-sized class | 1,041,476 tokens | `summarize_type` | 895 tokens |
| Find which types expose a given method | 1,041,476 tokens | `find_methods` | 618 tokens |

*Measured against `ICSharpCode.Decompiler.dll` 9.1.0.7988 on 2026-07-18; token figures are character counts divided by 4, not a real tokenizer.*

ILens turns assembly inspection into bounded, targeted lookups: list types in a namespace, summarize one type's public surface, find methods by name, decompile a single method body, run cross-reference analysis (callers, overrides, implementations, extension methods, attribute usage). Each call returns a focused response — making it cheap enough to leave registered as an always-available tool.

## Scope

ILens is built for inspecting **C# assemblies**, and its output is C#. The metadata-driven tools (`list_types`, `search_types`, `list_members`, `find_methods`, `analyze`) read language-agnostic metadata and work on an assembly from any .NET language. The decompiling tools (`decompile_type`, `decompile_method`, `decompile_property`, `decompile_event`, `summarize_type`) always emit C# — for an assembly compiled from F#, VB.NET, or C++/CLI they still run, but constructs without a C# equivalent may render unfaithfully.

## Built on ILSpy

ILens would be nothing without [ILSpy](https://github.com/icsharpcode/ILSpy). The decompiler, the analyzer, the type system that lets ILens speak about a .NET assembly in C# terms at all — none of that is ILens's. It belongs to the ILSpy team. The two libraries ILens links — `ICSharpCode.Decompiler` for the decompiler proper, `ICSharpCode.ILSpyX` for the analyzer infrastructure — represent more than a decade of careful work; this project is, candidly, a thin MCP shim on top.

That work is MIT-licensed, which is the only reason ILens can exist. If ILens is useful to you, please consider giving [ILSpy](https://github.com/icsharpcode/ILSpy) a star. They earned it.

To be clear: bug reports and feature requests about ILens belong on [the ILens issue tracker](https://github.com/tadis174/ILens/issues), not ILSpy's. Please don't take downstream problems to the upstream maintainers — they have enough work of their own.

## Installation

**System requirements**: Windows 10/11, x64. The binary is self-contained — no separate .NET runtime install required.

```powershell
winget install Tadis.ILens
```

Standard winget portable install. The binary lands under `%LOCALAPPDATA%\Microsoft\WinGet\Packages\` and `ilens` becomes available on your user PATH automatically. No admin elevation needed.

See the [user guide](https://tadis174.github.io/ILens/guide.html) for alternative installers, updating, and installation troubleshooting.

## Setup

### Step 1: Register the server

Add an entry to `.mcp.json` in your project root. The installer adds `ilens` to your PATH, so the `command` field references the binary by name — no full path needed:

```json
{
  "mcpServers": {
    "ilens": {
      "command": "ilens",
      "args": [
        "--allow-root", "C:\\path\\to\\dlls"
      ]
    }
  }
}
```

Each `--allow-root` flag adds a directory tree from which assemblies may be loaded. **Without any `--allow-root` flags, the server cannot load any assemblies.** Tool calls supply the assembly path per request; the server validates that the path is inside one of the configured roots, rejects path traversal (`..`), resolves symbolic links and junctions before re-checking containment, and bounds total memory with an aggregate budget (default 200 MB, `--max-total-size <MB>`).

Tool calls then specify which assembly to inspect:

```
list_types(assembly="C:\\path\\to\\dlls\\MyApp.dll", namespaceName="MyApp.Models")
```

The `list_allowed_roots` tool surfaces the configured roots so the agent can discover what's reachable.

> **Heads up — a first install needs a full restart, not just a new session.** Windows captures `PATH` per-process at launch, so an already-running Claude app holds a stale environment whenever `ilens` is a brand-new entry on user PATH (true for a first-ever winget portable install, or any `install.ps1` run). Claude Code reads `.mcp.json` only at session start, so the new server isn't registered in any running session either. Recipe: close Claude app entirely → install ILens → relaunch Claude app → start a **new** Claude Code session in the relaunched app. Resumed sessions survive a Claude app restart and still hold the old environment, so they won't find `ilens`. Subsequent `winget upgrade Tadis.ILens` runs don't change PATH; they only need a fresh Claude Code session.

### Step 2: Tell Claude to use it

Without this, Claude won't actually pick ILens — it'll keep reaching for `ildasm` via Bash, reading assembly files directly, or web-searching for source. Drop the following block into your project's `CLAUDE.md` and replace `<allow-root>` with the directory configured above:

````markdown
## Inspecting .NET assemblies

Assemblies under `<allow-root>` are reachable through the `ilens` MCP server.
Prefer ILens tools over running `ildasm` / `ilspycmd` via Bash, reading assembly
files directly, or web-searching for source.

- Discovery: `search_types` (substring match), `list_types` (whole namespace),
  `find_methods` (signature search).
- Reading: `summarize_type` (public surface, no bodies), `list_members`
  (filtered surface), `decompile_type` (full C#), `decompile_method` (single
  method body), `decompile_property` / `decompile_event` (full property or
  event declaration with accessor bodies, by unprefixed name).
- Cross-references: `analyze` with `kind` set to one of `UsedBy`,
  `InstantiatedBy`, `ExposedBy`, `ExtensionMethods`, `AppliedTo`,
  `OverriddenBy`, `ImplementedBy`, `Uses`, `Implements`, `ReadBy`,
  `AssignedBy`. Valid kinds depend on the symbol category.
- Comparing two builds: `list_changed_types` (what differs between two
  assemblies), `compare_type` (per-type member and body diff), `compare_method`
  (one method's body as C# or IL).
````

#### Per-line walkthrough

- The first paragraph is the load-bearing prefer-MCP rule. Without it, models default to `Bash`.
- The discovery bullet routes "I don't know the full name" tasks to the right tool: a partial name goes to `search_types`, a known namespace goes to `list_types`, a method shape goes to `find_methods`.
- The reading bullet escalates from cheapest to most expensive: `summarize_type` first, `list_members` when only part of the surface is needed, `decompile_method` for one method body (or `decompile_property` / `decompile_event` for the whole property or event without having to know the IL accessor prefix), `decompile_type` only when full source is required.
- The `analyze` bullet enumerates the `kind` enum so the model picks values the schema accepts — using one that does not apply to the symbol category produces an error like `Analysis kind 'ReadBy' is not valid for Method`.
- The comparison bullet is for "did this change between two builds?" work: `list_changed_types` to find what differs, `compare_type` for one type's member and body diff, `compare_method` to read a single differing body.

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

ILens is released under the [MIT License](LICENSE). Third-party dependencies are documented in `third-party-licenses/INDEX.md`, included in each release alongside the per-source license texts.
