# Third-party attribution

This directory holds vendored license files and notices for every third-party
component bundled in an ILens release. Files here are copied into
`Build/dist/third-party-licenses/` at publish time and end up in the release ZIP.

## Per-source pattern

The NuGet packages ILens transitively depends on come from a small number of
upstream source repositories. Instead of vendoring one LICENSE per package,
we vendor one LICENSE per source repository and document the package-to-source
mapping in `INDEX.md`. Same legal coverage, no duplication. (The exact package
count varies as deps shift; `check.ps1`'s success message reports the current
total.)

Layout:

- `<source>/LICENSE` — vendored license text from the upstream repo
- `<source>/NOTICE` — present only if upstream has one (Apache-2.0 §4(d))
- `<source>/README.md` — provenance and refresh procedure for the vendored files

For sources with the .NET Foundation MIT license (`dotnet/runtime`,
`dotnet/extensions`, `dotnet/maintenance-packages`), one vendored LICENSE
covers every package from those repos.

The .NET runtime third-party notices are separate from any LICENSE file —
they cover non-Microsoft components Microsoft included inside the runtime
(ICU, LZMA SDK, zlib, etc.). Bundled because `<SelfContained>true</SelfContained>`
ships the runtime inside the binary.

## INDEX.md format

`INDEX.md` is parsed by `check.ps1` to verify completeness. Format conventions
the parser depends on — do not break them when editing:

- Each source is introduced by an `## ` H2 heading. Heading text is freeform.
- Under each source, exactly these top-level bullets are recognized:
  - `- License: \`<filename>\` (...)` — backtick-wrapped LICENSE filename, parens optional
  - `- Notice: \`<filename>\` (...)` — backtick-wrapped NOTICE filename if vendored
  - `- Source: <url>, <url>, ...` — comma-separated allowlist of NuGet `projectUrl` values
    that the gate accepts as belonging to this source. Any package whose nuspec declares a
    `projectUrl` not on any source's `Source:` line fails the NuGet metadata cross-check.
  - `- Packages:` — followed by indented sub-bullets
- Package sub-bullets use 2-space indentation: `  - <PackageId>` or `  - <PackageId.Prefix>.*`
- A trailing wildcard (`Microsoft.Extensions.*`) matches packages by prefix.
- Anything after the first whitespace on a package line (annotations like
  `(transitive via X)`) is ignored by the parser.
- Other top-level bullets (`- Notes:`, etc.) are ignored by the parser
  but useful for human readers.

The source ID for the LICENSE-files-present check is derived from the License
filename: `dotnet-foundation-LICENSE` → expects `dotnet-foundation/LICENSE`
in this directory.

## Per-source README format

Each `<source>/README.md` documents provenance for the vendored files. The gate's
upstream-drift check reads one specific bullet:

- `- Source URL: <https-url>` — the canonical raw URL to fetch from upstream.
  Drift detection downloads this URL each publish and compares byte-for-byte
  (after line-ending normalization) against the corresponding local file.
  The local filename is derived from the URL's last path component:
  `LICENSE.TXT` / `LICENSE.txt` / `LICENSE` / `LICENSE.md` → `LICENSE`;
  `NOTICE` → `NOTICE`; `THIRD-PARTY-NOTICES.TXT` → `THIRD-PARTY-NOTICES.txt`.

Multiple `- Source URL:` lines per README are allowed for sources that vendor
more than one upstream file. Each URL must map to a distinct local filename.

The `dotnet-runtime/README.md` additionally needs a `Runtime version: <X.Y.Z>`
line — the gate checks this matches the highest installed `Microsoft.NETCore.App 8.x`.

## Pre-publish gate (`check.ps1`)

Runs as part of `/publish` Step 1. Seven checks; all must pass.

1. **Completeness** — every package in `Build/obj/project.assets.json` (filtered
   to those with `compile`/`runtime` entries — i.e. excluding build-time-only
   tooling like the IL trimmer) is represented in `INDEX.md` either by exact
   name or by a matching wildcard.
2. **No stale entries** — every explicit (non-wildcard) entry in `INDEX.md`
   is still present in the build.
3. **LICENSE files present** — every source in `INDEX.md` has its
   `<source>/LICENSE` (or NOTICE / THIRD-PARTY-NOTICES.txt) file on disk.
4. **Apache NOTICE accountability** — every Apache-licensed source has either
   a `NOTICE` file or a `README.md` that documents its absence (Apache-2.0 §4(d)).
5. **Runtime version match** — the .NET runtime version named in
   `dotnet-runtime/README.md` matches the highest installed 8.x runtime the
   SDK will bundle.
6. **Upstream drift** *(network)* — for every per-source `README.md`, fetches
   each `Source URL:` and compares byte-for-byte against the vendored file.
   Hard fail on any difference. Use `-Offline` to skip during local development;
   never use `-Offline` in `/publish`.
7. **NuGet metadata cross-check** *(offline)* — for every package, reads the
   `.nuspec` from the local NuGet cache and verifies the declared `projectUrl`
   appears on some source's `- Source:` line in `INDEX.md`. URLs are compared
   after normalization (lowercased, `http`→`https`, trailing `/` stripped).

Run manually:

```powershell
pwsh Source/Attribution/check.ps1            # all seven checks
pwsh Source/Attribution/check.ps1 -Offline   # skip drift; useful with no network
```

Exits 0 on success, 1 on attribution drift, 2 if `dotnet restore` hasn't run.

## Post-stage gate (`check-staged.ps1`)

Runs as part of `/publish` Step 5.5, after `dotnet publish` has staged
`Build/dist/`. Verifies the staged contents match an allowlist:

- `Build/dist/` root is exactly `ILens.exe`, `LICENSE`, `THIRD-PARTY-NOTICES.txt`,
  `README.md`, `guide.html`. The only allowed subdirectory is `third-party-licenses/`.
- `Build/dist/third-party-licenses/` contains exactly `INDEX.md` plus the
  filenames named on `License:` and `Notice:` lines in INDEX.md. No subdirectories.

Catches: a new `Copy` line added to the csproj publish target without updating
attribution, an asset under `Assets/` accidentally bundled, residuals the
trimmer left behind, stale files from a prior staging run that didn't get wiped.

Run manually:

```powershell
pwsh Source/Attribution/check-staged.ps1
```

Exits 0 on success, 1 on unexpected/missing files, 2 if `Build/dist/` hasn't
been staged yet.

## Adding or removing a third-party component

When packages change (new `<PackageReference>`, version bump that changes a
package's source repo, etc.), the workflow is:

1. `dotnet restore` to refresh `Build/obj/project.assets.json`.
2. Run `check.ps1` — it tells you what's missing or stale.
3. For a new source: create `Source/Attribution/<source>/`, vendor the upstream
   LICENSE (and NOTICE if applicable), write `README.md` with the `Source URL:`
   line, add a section to `INDEX.md`.
4. For a removed source: delete its directory and its INDEX section.
5. For a runtime bump: update `dotnet-runtime/README.md` (`Runtime version:` line
   and `Source URL:` tag) and refetch `THIRD-PARTY-NOTICES.txt`. Then update
   `dotnet-foundation/README.md` to keep the LICENSE tag URL in sync.
6. Re-run `check.ps1` until it exits 0.
