# ILens

An MCP server that lets AI agents inspect compiled .NET assemblies — list types, decompile methods, run cross-reference analysis — without burning tokens on full-file dumps.

## Why

The default fallback for an AI agent inspecting a compiled .NET assembly is to shell out to `ildasm` (IL dump) or `ilspycmd` (full-C# decompile). Both dump an entire file as text, and that output stays in the agent's context as input tokens on every subsequent turn — quickly thousands of tokens spent inspecting a single class.

| Task | `ilspycmd` cost | ILens tool | ILens cost |
|------|----------------:|------------|-----------:|
| List the public types in a namespace | 1,041,476 tokens | `find_types` | 1,109 tokens |
| See the API surface of one mid-sized class | 1,041,476 tokens | `list_members` | 562 tokens |
| Find which types expose a given method | 1,041,476 tokens | `find_methods` | 526 tokens |

*Measured against `ICSharpCode.Decompiler.dll` 9.1.0.7988 on 2026-10-31; token figures are character counts divided by 4, not a real tokenizer.*

ILens turns assembly inspection into bounded, targeted lookups: list types in a namespace, list one type's members without bodies, find methods by name, decompile a single method body, run cross-reference analysis (callers, overrides, implementations, extension methods, attribute usage). Each call returns a focused response — making it cheap enough to leave registered as an always-available tool.

## Scope

ILens is built for inspecting **C# assemblies**, and its output is C#. The metadata-driven tools (`find_types`, `find_methods`, `list_members`, `analyze`) read language-agnostic metadata and work on an assembly from any .NET language. `decompile`, `search_code` and `analyze`'s `call_sites` kind always emit C# — for an assembly compiled from F#, VB.NET, or C++/CLI they still run, but constructs without a C# equivalent may render unfaithfully.

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

Each `--allow-root` flag adds a directory tree from which assemblies may be loaded. **Without any `--allow-root` flags, the server cannot load any assemblies.** Tool calls supply the assembly path per request; the server validates that the path is inside one of the configured roots, rejects path traversal (`..`), resolves symbolic links and junctions before re-checking containment, and keeps its memory within a budget (default 200 MB, `--max-total-size <MB>`) by closing the assemblies used longest ago.

Tool calls then specify which assembly to inspect:

```
find_types(assembly="C:\\path\\to\\dlls\\MyApp.dll", namespace="MyApp.Models")
```

The `list_allowed_roots` tool surfaces the configured roots so the agent can discover what's reachable.

> **Heads up — a first install needs a full restart, not just a new session.** Windows captures `PATH` per-process at launch, so an already-running Claude app holds a stale environment whenever `ilens` is a brand-new entry on user PATH (true for a first-ever winget portable install, or any `install.ps1` run). Claude Code reads `.mcp.json` only at session start, so the new server isn't registered in any running session either. Recipe: close Claude app entirely → install ILens → relaunch Claude app → start a **new** Claude Code session in the relaunched app. Resumed sessions survive a Claude app restart and still hold the old environment, so they won't find `ilens`. Subsequent `winget upgrade Tadis.ILens` runs don't change PATH; they only need a fresh Claude Code session.

### Step 2: Clients that do not read server instructions

When a client connects, ILens sends it MCP server instructions: what ILens is for, which directories it can read, and which tool answers which question. Claude Code reads them at the start of every session, in the terminal and in the Claude desktop app's Code tab, so with Claude Code, Step 1 is the whole setup.

Claude Desktop's chat does not read server instructions. For it, and for any other client that does not, add this paragraph to the client's instructions and replace `<allow-root>` with the directory configured above. It names no tool, so it stays correct when a release renames or merges tools:

```markdown
## Inspecting .NET assemblies

Assemblies under `<allow-root>` are reachable through the `ilens` MCP server.
Prefer its tools over running `ildasm` or `ilspycmd` in a shell, reading
assembly files directly, or searching the web for source.
```

If your project's `CLAUDE.md` or a client's instructions hold the block an earlier ILens README asked you to paste, delete it: it names tools that no longer exist. A client that does not read server instructions takes the paragraph above in its place.

## Full reference

A self-contained HTML user guide covers what this README does not:

- **Per-tool reference** — every tool, every parameter (with `required` / `optional` flags and types), return shape, and full list of error conditions
- **Alternative integration paths** — `claude mcp add` CLI command, Claude Desktop config
- **Security model** — what's enforced (`--allow-root` mandatory, no traversal, no network, 200 MB memory budget, read-only), and what's not
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
