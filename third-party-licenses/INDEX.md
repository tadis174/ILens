# Third-party software bundled with ILens

ILens is MIT-licensed (see top-level `LICENSE`). The release archive bundles
the third-party components below. Per-source LICENSE files are in this
directory; the .NET runtime additionally bundles non-Microsoft components
whose attributions are in `dotnet-runtime-THIRD-PARTY-NOTICES.txt`.

## .NET Foundation
- License: `dotnet-foundation-LICENSE` (MIT)
- Source: https://github.com/dotnet/runtime, https://github.com/dotnet/extensions, https://github.com/dotnet/maintenance-packages, https://dot.net/
- Notes: `Microsoft.NET.ILLink.Tasks` is part of the SDK and runs at build time as the IL trimmer; its code is not bundled into the published binary, so it is intentionally not listed below. The `Source:` list includes both the canonical source repos and the umbrella `dot.net` URL that most `Microsoft.Extensions.*` / `System.*` packages declare as their NuGet `projectUrl`.
- Packages:
  - Microsoft.Extensions.*
  - System.*

## .NET runtime third-party notices
- License: `dotnet-runtime-THIRD-PARTY-NOTICES.txt` (various — see file)
- Source: https://github.com/dotnet/runtime
- Notes: covers non-Microsoft components bundled into the .NET 10 runtime
  (ICU, LZMA SDK, zlib, etc.). Required because `<SelfContained>true</SelfContained>`
  ships the runtime inside the binary.
- Packages: (none — this file attributes runtime components, not NuGet packages)

## ICSharpCode (ILSpy)
- License: `icsharpcode-ilspy-LICENSE` (MIT)
- Source: https://github.com/icsharpcode/ILSpy
- Packages:
  - ICSharpCode.Decompiler
  - ICSharpCode.ILSpyX

## Model Context Protocol C# SDK
- License: `model-context-protocol-LICENSE` (Apache-2.0 / MIT — transition document; covers both)
- Source: https://github.com/modelcontextprotocol/csharp-sdk, https://csharp.sdk.modelcontextprotocol.io/
- Notes: see `model-context-protocol/README.md` for the Apache §4(d) NOTICE accountability check. The packages declare the docs site (`csharp.sdk.modelcontextprotocol.io`) as their NuGet `projectUrl`; the GitHub repo is the source of the LICENSE.
- Packages:
  - ModelContextProtocol
  - ModelContextProtocol.Core

## Mono.Cecil
- License: `mono-cecil-LICENSE` (MIT)
- Source: https://github.com/jbevain/cecil
- Packages:
  - Mono.Cecil

## K4os.Compression.LZ4
- License: `k4os-lz4-LICENSE` (MIT)
- Source: https://github.com/MiloszKrajewski/K4os.Compression.LZ4
- Notes: transitive dependency via `ICSharpCode.ILSpyX`
- Packages:
  - K4os.Compression.LZ4
