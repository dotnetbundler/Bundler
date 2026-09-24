# Third-party notices

## NsisToolset 3.12-r1 / NSIS 3.12

- Project: Nullsoft Scriptable Install System
- Toolset project: https://github.com/dotnetbundler/NsisToolset
- Toolset release: https://github.com/dotnetbundler/NsisToolset/releases/tag/v3.12-r1
- Release archive SHA-256: `41F15B7F7E3A0349185606EDE939C7B2E5B31FF76F0EB479143D6659EF1EDDBC`
- Upstream license file: `licenses/nsis/COPYING` in the generated NuGet package

NSIS and its bundled compression modules use multiple licenses. Refer to the included upstream `COPYING` file for the authoritative terms.

## NsisPlugin 1.0.2

- Project: https://github.com/dotnetbundler/NsisPlugin
- NuGet package: `NsisPlugin` 1.0.2
- License: MIT
- License file: `licenses/nsis-plugin/LICENSE` in the generated NuGet package

The bundled `DotNetBundlerNsis.dll` Native AOT plug-in is built with NsisPlugin.

## WiX Toolset 3.14.1

- Project: https://github.com/wixtoolset/wix3
- Release: https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm
- Binary subset SHA-256: `ABE572B353CD4151B1C69907BB5C5E84886138E518607432C9723B454853B358` (includes the original WiX UI extension)
- License: Microsoft Reciprocal License, `licenses/wix/LICENSE.TXT` in the WiX package
- Corresponding source: `licenses/wix/wix3141-source.zip` in the WiX package

The source and notices are distributed with the WiX backend package. See `third_party/wix/README.md` for provenance, original archive and per-file checksums.
