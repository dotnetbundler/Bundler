# NSIS upstream reference

DotNet.Bundler does not clone Tauri's implementation or runtime stack. It aligns the user-visible bundling capabilities that also make sense for a general desktop packager, then implements them through this project's Core, format backends, MSBuild adapter, direct API, and future supported CLI.

The product boundary, capability status vocabulary, stage order, and audit procedure are defined in `docs/roadmap.md`. This file records the pinned upstream evidence and the decisions made from it.

Reference snapshot:

- Repository: `tauri-apps/tauri`
- Branch: `dev`
- Commit: `5d995ed35b029cecd780fdbe614dc6023a89b81b`
- Checked: 2026-09-21

Relevant upstream paths:

- `crates/tauri-bundler/src/bundle/windows/nsis/installer.nsi`
- `crates/tauri-bundler/src/bundle/windows/nsis/utils.nsh`
- `crates/tauri-bundler/src/bundle/windows/nsis/mod.rs`
- `crates/tauri-bundler/src/bundle/windows/sign.rs`
- `crates/tauri-bundler/src/bundle/settings.rs`
- `crates/tauri-utils/src/config.rs`

## Decisions

### Capability alignment, not field copying

Tauri configuration is divided into three categories before it affects this project:

1. General packaging capabilities, such as metadata, artwork, install scope, associations, localization, signing, custom templates, and hooks. These should have an equivalent capability where the target format supports them.
2. Format-specific behavior, such as NSIS compression or WiX identifiers. These belong to the relevant backend and do not become artificial cross-format settings.
3. Tauri runtime/framework behavior, such as WebView2 installation policy and VC runtime deployment. These are not automatically Bundler capabilities.

An upstream field is therefore evidence that a use case exists, not a requirement to reproduce its name, type, or implementation. `NSIS-R1` in `docs/roadmap.md` owns the complete field-by-field matrix.

### Application runtime dependencies

Rejected as a built-in generic facility for the current roadmap. DotNet.Bundler will not discover, download, version-check, install, repair, or uninstall arbitrary application runtimes and prerequisites. Tauri's WebView2 controls are tied to Tauri applications and do not establish a framework-neutral contract.

Callers may still include prepared runtime files in their payload or use documented lifecycle hooks/custom templates. The build-time compiler/toolset required to produce a package remains the responsibility of each format backend and is separate from application runtime deployment.

### Signing

Authenticode is a packaging and distribution capability, not a Tauri-specific runtime feature, so it remains an explicit alignment target. The current implementation signs the exported NSIS uninstaller and final installer. `NSIS-R2` extends this to an ordered payload/uninstaller/installer pipeline and to configurable external signing providers while preserving the existing PFX/certificate-store implementation.

### Install scope

Adopted the three public modes (`currentUser`, `perMachine`, and `both`) because they map directly to Windows installation scope. The `both` implementation uses the standard NSIS `MultiUser.nsh`; fixed modes use explicit execution levels and shell contexts. Target x64 and arm64 installers use the 64-bit registry view.

The upstream initialization order and `MULTIUSER_USE_PROGRAMFILES64` behavior were used to confirm ambiguous NSIS details. DotNet.Bundler retains its existing `%LOCALAPPDATA%\Programs` current-user default instead of copying Tauri's directory layout.

### Artwork and metadata

The available Tauri fields were checked to avoid naming gaps, but DotNet.Bundler implements these as ordinary bundle metadata plus NSIS-specific artwork overrides. The uninstaller header falls back to the installer header; the installer and uninstaller icons can be configured independently. No Tauri runtime behavior is copied for these static assets.

### Lifecycle hooks

DotNet.Bundler uses the four optional `NSIS_HOOK_*` macro names found in the upstream template because their placement is unambiguous and the convention is already documented in the NSIS bundling ecosystem. Only the naming and lifecycle boundaries are adopted; hook contents remain user-owned NSIS code.

### Legacy MSI migration

Tauri's runtime scan by `DisplayName` and `Publisher` was reviewed but rejected because another installed product can legitimately share both values. DotNet.Bundler requires explicit historical MSI ProductCode or UpgradeCode GUIDs and queries them through Windows Installer APIs in the bundled native plug-in. Migration always removes every exact match before writing the NSIS payload. The upstream uninstall-first behavior is retained, while detection is intentionally stricter.

### File associations and deep links

Tauri's file-association macros and ownership-checked deep-link removal were reviewed. The deep-link ownership check is adopted so uninstall cannot remove a protocol that another application subsequently claimed. Directly assigning an extension's default ProgID was rejected: modern Windows requires applications to register as candidates and leaves the effective default to the user. DotNet.Bundler therefore registers application-specific versioned ProgIDs, `OpenWithProgids`, Capabilities, and `RegisteredApplications`, then refreshes the Shell association cache. Custom URL schemes additionally receive a direct protocol key so a newly installed scheme is immediately launchable.

## External validation still required

See `docs/nsis-open-items.md` for tests that need credentials, production identifiers, or platform conditions unavailable in the repository.
