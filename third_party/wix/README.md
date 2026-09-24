# WiX Toolset 3.14.1 provenance

The MSI backend uses the official `wix3141rtm` release. The build-only binary subset in `wix3141-tools.zip` was copied, without modifying its entries, from the official `wix314-binaries.zip` asset:

- Release: https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm
- Original asset SHA-256: `6AC824E1642D6F7277D0ED7EA09411A508F6116BA6FAE0AA5F2C7DAA2FF43D31`
- Original asset size: 41,297,555 bytes
- Bundled subset SHA-256: `ABE572B353CD4151B1C69907BB5C5E84886138E518607432C9723B454853B358`
- Bundled subset size: 1,642,074 bytes
- Tag source archive: https://codeload.github.com/wixtoolset/wix3/zip/refs/tags/wix3141rtm
- Bundled corresponding source `wix3141-source.zip` SHA-256: `A56184E798885641821666BD389FE6276F99363F65BAE8F88630B17DE297FE9F`
- Source archive size: 13,599,826 bytes
- License: Microsoft Reciprocal License in `LICENSE.TXT` and in the source archive

The subset contains `candle.exe`, `light.exe`, their config files, `wix.dll`, `wconsole.dll`, `winterop.dll`, `darice.cub`, five `Microsoft.Deployment.*` assemblies, `WixUIExtension.dll`, and `LICENSE.TXT`. Exact per-file SHA-256 values are in `SHA256SUMS`.

## Redistribution review (2026-09-24)

All 14 subset entries were matched to the original release by the pinned per-file hashes and to files or projects in the `wix3141rtm` source archive. The source locations are `src/tools/candle` (`candle.exe` and config), `src/tools/light` (`light.exe`, config, `darice.cub`), `src/tools/wix` (`wix.dll`), `src/tools/wconsole`, `src/tools/winterop`, and `src/DTF/Libraries/{Compression,Compression.Cab,Resources,WindowsInstaller,WindowsInstaller.Package}`. The 12 applicable source/config/project headers inspected identify the Microsoft Reciprocal License; the root `LICENSE.TXT` is included unchanged. The generated NuGet package was inspected and contains the full tag source archive, license, checksum list and `THIRD-PARTY-NOTICES.md`. No separate license was identified for the shipped subset. Recheck if any shipped file changes. This is an engineering review of the selected files, not a legal guarantee.

WIN-MSI-3 adds the fifteenth entry, the unmodified `WixUIExtension.dll` (3,768,320 bytes, SHA-256 `C6B8227782A7268B54C0C161CA300FAE869A53CE9889D19C16B6B900AC468DC3`) from the same pinned official binary asset. Its matching project and UI sources are in `src/ext/UIExtension/wixext` and `src/ext/UIExtension/wixlib` of the bundled tag source archive. It supplies WiX 3 built-in localized MSI dialogs when an application supplies an RTF license; it is not a paid extension. Its source header and project license were reviewed against the bundled MS-RL. The packaged license, source archive and checksum manifest still accompany the NuGet package; recheck the produced package before release.

The MS-RL grants royalty-free copyright and patent permissions subject to its conditions, including corresponding source and notice preservation. WiX's later Open Source Maintenance Fee was introduced in v6, not v3.14.1. Sources: [upstream license](https://github.com/wixtoolset/wix3/blob/wix3141rtm/LICENSE.TXT), [WiX version history](https://docs.firegiant.com/wix/whatsnew/).

WIN-MSI-4 rechecked this unchanged 15-file subset on 2026-09-25. Release builds now treat WiX compiler/linker warnings as errors, with the documented per-user-only ICE91 exception. The MSI integration pack check compares the packaged license, corresponding source archive, checksum list, this README, and `THIRD-PARTY-NOTICES.md` byte-for-byte by SHA-256 against repository sources. The final package size and SHA-256 are recorded outside the package in `docs/msi-roadmap.md` section 9 to avoid a self-referential hash. No additional WiX or third-party binary was added. WiX v3 remains outside free community service, including security fixes: [official status](https://docs.firegiant.com/wix/wix3/). Reassess that maintenance risk before broad public distribution.

The source archive is provided with the `DotNet.Bundler.Wix` NuGet package. Consumers of the convenience `DotNet.Bundler` package receive it through the `DotNet.Bundler.Wix` dependency. The tools are extracted into a hash-verified local cache; no tool or application runtime is downloaded when bundling.

WiX v3 is out of free community service. This project does not rely on paid FireGiant support or extensions. Build-host framework and ARM64 compatibility remain subject to the Windows VM matrix in `docs/msi-roadmap.md`.
