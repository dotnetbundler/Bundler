# Bundled NSIS plug-in

This project builds the Unicode `win-x86` Native AOT plug-in used by the NSIS
installer at runtime. It uses `NsisPlugin` 1.0.2, links the same SemVer source
used by `DotNet.Bundler.Nsis` tests, queries explicitly configured legacy MSI
products through the native Windows Installer API, and starts an installed app
with the interactive desktop user's token after an elevated install.

It is intentionally excluded from `Bundler.slnx`: normal library and MSBuild task
builds retain the .NET 8 SDK baseline, while rebuilding the native plug-in requires
.NET 10 and the Windows native toolchain.

```powershell
powershell -File tools/Bundler.Nsis.Plugin/Build.ps1
```

Commit the regenerated `third_party/nsis/plugins/x86-unicode/DotNetBundlerNsis.dll`
with any semantic-version or MSI-query implementation change and update its
recorded checksum.
