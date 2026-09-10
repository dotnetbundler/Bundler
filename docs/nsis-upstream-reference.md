# NSIS upstream reference

DotNet.Bundler is not intended to clone Tauri. Upstream code is consulted only where Windows or NSIS behavior is ambiguous, and each adopted behavior is reviewed against this project's .NET/MSBuild architecture.

Reference snapshot:

- Repository: `tauri-apps/tauri`
- Branch: `dev`
- Commit: `6eda56cd820fd47f90da191cbef42902d5eb6b2b`
- Checked: 2026-09-10

Relevant upstream paths:

- `crates/tauri-bundler/src/bundle/windows/nsis/installer.nsi`
- `crates/tauri-bundler/src/bundle/windows/nsis/utils.nsh`
- `crates/tauri-bundler/src/bundle/windows/nsis/mod.rs`
- `crates/tauri-bundler/src/bundle/windows/sign.rs`
- `crates/tauri-bundler/src/bundle/settings.rs`
- `crates/tauri-utils/src/config.rs`

## Decisions

### Install scope

Adopted the three public modes (`currentUser`, `perMachine`, and `both`) because they map directly to Windows installation scope. The `both` implementation uses the standard NSIS `MultiUser.nsh`; fixed modes use explicit execution levels and shell contexts. Target x64 and arm64 installers use the 64-bit registry view.

The upstream initialization order and `MULTIUSER_USE_PROGRAMFILES64` behavior were used to confirm ambiguous NSIS details. DotNet.Bundler retains its existing `%LOCALAPPDATA%\Programs` current-user default instead of copying Tauri's directory layout.

### Artwork and metadata

The available Tauri fields were checked to avoid naming gaps, but DotNet.Bundler implements these as ordinary bundle metadata plus NSIS-specific artwork overrides. The uninstaller header falls back to the installer header; the installer and uninstaller icons can be configured independently. No Tauri runtime behavior is copied for these static assets.

## External validation still required

See `docs/nsis-open-items.md` for tests that need credentials, production identifiers, or platform conditions unavailable in the repository.
