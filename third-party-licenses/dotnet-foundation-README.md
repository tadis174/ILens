# .NET Foundation MIT license

`dotnet-foundation-LICENSE` is the canonical .NET Foundation MIT license. It covers every NuGet
package shipped under the .NET Foundation umbrella — `Microsoft.Extensions.*`,
`System.*`, and a few satellite packages (e.g. `Microsoft.Extensions.AI.*` from
`dotnet/extensions`, `System.Runtime.CompilerServices.Unsafe` from
`dotnet/maintenance-packages`). The text is identical across these repos
because they all share the .NET Foundation copyright and license.

## Provenance

- Source URL: https://raw.githubusercontent.com/dotnet/runtime/v10.0.8/LICENSE.TXT
- Vendored from: dotnet/runtime, tag `v10.0.8`

The pinned tag matches the runtime version this build bundles. The LICENSE
file does not change between 10.0.x patch versions in practice; using the
tag-specific URL keeps drift detection deterministic.

## Refresh procedure

When the bundled .NET runtime version changes (see `dotnet-runtime-README.md`),
update the tag in the URL above and refetch:

```powershell
Invoke-WebRequest `
  -Uri https://raw.githubusercontent.com/dotnet/runtime/v<new-version>/LICENSE.TXT `
  -OutFile dotnet-foundation-LICENSE
```
