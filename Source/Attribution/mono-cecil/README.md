# Mono.Cecil MIT license

`LICENSE` covers `Mono.Cecil`, the IL reading library that backs
`ICSharpCode.Decompiler`.

## Provenance

- Source URL: https://raw.githubusercontent.com/jbevain/cecil/0.11.6/LICENSE.txt
- Vendored from: `jbevain/cecil`, tag `0.11.6`

Tag-pinned to the version of the package we depend on.

## Refresh procedure

When the `Mono.Cecil` version bumps (transitively, via `ICSharpCode.ILSpyX`):

```powershell
Invoke-WebRequest `
  -Uri https://raw.githubusercontent.com/jbevain/cecil/<new-tag>/LICENSE.txt `
  -OutFile LICENSE
```
