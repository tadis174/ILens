# Model Context Protocol C# SDK attribution

The vendored `model-context-protocol-LICENSE` is the comprehensive licensing document from
`modelcontextprotocol/csharp-sdk`. It includes the full Apache-2.0 license
text, the full MIT license text, and a pointer to CC-BY-4.0 for documentation.

The MCP project is undergoing a transition from MIT to Apache-2.0; older
contributions remain under MIT until each contributor consents to relicense.
The single bundled LICENSE file covers all three regimes — Apache §4(d) is
satisfied because the MIT and CC-BY-4.0 attributions are included alongside
the Apache license terms in the same document.

## Provenance

- Source URL: https://raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/v1.2.0/LICENSE
- Vendored from: `modelcontextprotocol/csharp-sdk`, tag `v1.2.0`

## NOTICE file

Checked at v1.2.0: upstream has no `NOTICE` file at the repo root. The
license-transition explanation at the top of the `LICENSE` file (the
paragraphs before the Apache license body) is what serves as the equivalent
attribution notice. Apache §4(d) requires NOTICE redistribution **if the
work includes one** — the MCP SDK does not, so this requirement is satisfied
vacuously.

## Refresh procedure

When the MCP SDK packages bump:

```powershell
Invoke-WebRequest `
  -Uri https://raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/v<version>/LICENSE `
  -OutFile model-context-protocol-LICENSE
```

Update the `Source URL:` line above. If a `NOTICE` file appears upstream in a
future version, vendor it as `model-context-protocol-NOTICE` next to
`model-context-protocol-LICENSE` and remove the Apache §4(d) note from this README.
