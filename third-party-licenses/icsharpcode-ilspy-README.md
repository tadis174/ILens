# ICSharpCode (ILSpy) MIT license

`icsharpcode-ilspy-LICENSE` covers `ICSharpCode.Decompiler` and `ICSharpCode.ILSpyX` — the
decompilation and analysis engines underlying ILens. Both packages ship from
the same `icsharpcode/ILSpy` repository.

## Provenance

- Source URL: https://raw.githubusercontent.com/icsharpcode/ILSpy/master/LICENSE
- Vendored from: `icsharpcode/ILSpy`, branch `master`

Branch-tracking (rather than tag-pinning) is used here because the upstream
repo doesn't ship LICENSE under per-version tags reliably. Drift detection
fires if anything in the upstream LICENSE changes — which is the signal we
want, since a copyright-line update or relicense would matter.

## Refresh procedure

```powershell
Invoke-WebRequest `
  -Uri https://raw.githubusercontent.com/icsharpcode/ILSpy/master/LICENSE `
  -OutFile icsharpcode-ilspy-LICENSE
```
