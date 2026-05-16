# K4os.Compression.LZ4 MIT license

`k4os-lz4-LICENSE` covers `K4os.Compression.LZ4`, an LZ4 implementation by Milosz
Krajewski. Pulled in transitively via `ICSharpCode.ILSpyX` for handling
LZ4-compressed sections in .NET assembly metadata.

## Provenance

- Source URL: https://raw.githubusercontent.com/MiloszKrajewski/K4os.Compression.LZ4/master/LICENSE
- Vendored from: `MiloszKrajewski/K4os.Compression.LZ4`, branch `master`

Branch-tracking — upstream tags don't follow a stable `vX.Y.Z` pattern that
maps cleanly to NuGet versions. Drift detection fires on any upstream LICENSE
change, which is the signal we want.

## Refresh procedure

```powershell
Invoke-WebRequest `
  -Uri https://raw.githubusercontent.com/MiloszKrajewski/K4os.Compression.LZ4/master/LICENSE `
  -OutFile k4os-lz4-LICENSE
```
