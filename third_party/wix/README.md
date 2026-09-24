# WiX Toolset 3.14.1 provenance

The MSI backend uses the official `wix3141rtm` release. The build-only binary subset in `wix3141-tools.zip` was copied, without modifying its entries, from the official `wix314-binaries.zip` asset:

- Release: https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm
- Original asset SHA-256: `6AC824E1642D6F7277D0ED7EA09411A508F6116BA6FAE0AA5F2C7DAA2FF43D31`
- Original asset size: 41,297,555 bytes
- Bundled subset SHA-256: `25AE0BB2A21FAC6B486C4B06155C9F463F2D845E7036BE0E9B1C98F4E48EA494`
- Bundled subset size: 1,038,060 bytes
- Tag source archive: https://codeload.github.com/wixtoolset/wix3/zip/refs/tags/wix3141rtm
- Bundled corresponding source `wix3141-source.zip` SHA-256: `A56184E798885641821666BD389FE6276F99363F65BAE8F88630B17DE297FE9F`
- Source archive size: 13,599,826 bytes
- License: Microsoft Reciprocal License in `LICENSE.TXT` and in the source archive

The subset contains `candle.exe`, `light.exe`, their config files, `wix.dll`, `wconsole.dll`, `winterop.dll`, `darice.cub`, five `Microsoft.Deployment.*` assemblies, and `LICENSE.TXT`. Exact per-file SHA-256 values are in `SHA256SUMS`.

## Redistribution review (2026-09-24)

All 14 subset entries were matched to the original release by the pinned per-file hashes and to files or projects in the `wix3141rtm` source archive. The source locations are `src/tools/candle` (`candle.exe` and config), `src/tools/light` (`light.exe`, config, `darice.cub`), `src/tools/wix` (`wix.dll`), `src/tools/wconsole`, `src/tools/winterop`, and `src/DTF/Libraries/{Compression,Compression.Cab,Resources,WindowsInstaller,WindowsInstaller.Package}`. The 12 applicable source/config/project headers inspected identify the Microsoft Reciprocal License; the root `LICENSE.TXT` is included unchanged. The generated NuGet package was inspected and contains the full tag source archive, license, checksum list and `THIRD-PARTY-NOTICES.md`. No separate license was identified for the shipped subset. Recheck if any shipped file changes. This is an engineering review of the selected files, not a legal guarantee.

The MS-RL grants royalty-free copyright and patent permissions subject to its conditions, including corresponding source and notice preservation. WiX's later Open Source Maintenance Fee was introduced in v6, not v3.14.1. Sources: [upstream license](https://github.com/wixtoolset/wix3/blob/wix3141rtm/LICENSE.TXT), [WiX version history](https://docs.firegiant.com/wix/whatsnew/).

The source archive is provided with the `DotNet.Bundler.Wix` NuGet package. Consumers of the convenience `DotNet.Bundler` package receive it through the `DotNet.Bundler.Wix` dependency. The tools are extracted into a hash-verified local cache; no tool or application runtime is downloaded when bundling.

WiX v3 is out of free community service. This project does not rely on paid FireGiant support or extensions. Build-host framework and ARM64 compatibility remain subject to the Windows VM matrix in `docs/msi-roadmap.md`.
