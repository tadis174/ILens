# .NET runtime third-party notices

The bundled file `dotnet-runtime-THIRD-PARTY-NOTICES.txt` covers non-Microsoft
components that ship as part of the .NET runtime itself (ICU, LZMA SDK, zlib,
and others). It is **not** the .NET Foundation MIT license — that's in
`dotnet-foundation-LICENSE` and covers Microsoft's own runtime code.

This file is needed because `Core/Core.csproj` sets `<SelfContained>true</SelfContained>`,
which bundles the entire runtime into the published binary, including the
runtime's own third-party dependencies.

## Provenance

- Source URL: https://raw.githubusercontent.com/dotnet/runtime/v10.0.9/THIRD-PARTY-NOTICES.TXT
- Runtime version: 10.0.9

## Refresh procedure

When `<TargetFramework>` is bumped, or when the SDK installs a newer
10.0.x runtime that the next publish will bundle:

1. Determine the new bundled version: `dotnet --list-runtimes` and pick the
   highest `Microsoft.NETCore.App 10.x` entry.
2. Refetch:
   ```powershell
   Invoke-WebRequest `
     -Uri https://raw.githubusercontent.com/dotnet/runtime/v<version>/THIRD-PARTY-NOTICES.TXT `
     -OutFile dotnet-runtime-THIRD-PARTY-NOTICES.txt
   ```
3. Update the `Source URL:` line and `Runtime version:` line above.
4. Also update the dotnet-foundation tag URL in `dotnet-foundation-README.md`
   so it stays in sync with the runtime tag.
5. Re-run `third-party-licenses/check.ps1` to verify.
