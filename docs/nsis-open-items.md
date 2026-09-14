# NSIS external validation and open inputs

This file records work that cannot be honestly completed using repository-only fixtures. It is not a substitute for implementation work that can be automated locally.

| Item | Current reason | Input needed | Impact |
| --- | --- | --- | --- |
| Real Authenticode end-to-end signing | The signing configuration and pipeline are not implemented yet; the repository also has no signing certificate/private key or timestamp service contract. | First implement signing command generation and execution, then provide a test certificate source or CI signing identity, its non-secret selector, and the timestamp service URL. | Unit tests can cover command generation, but Windows trust-chain and timestamp verification require an external signing identity. |
| Production WiX migration | No historical UpgradeCode/ProductCode or released MSI fixture has been supplied. | Real legacy identifiers and representative x86/x64, current-user/per-machine MSI packages or registry exports. | A generic configurable migration mechanism can be tested with disposable fixtures; recognition of an actual released product remains unverified. |
| Elevated per-machine install/uninstall | Normal local tests must not silently trigger UAC. | An elevated Windows CI worker or an explicit manual test run. | Installer compilation and registry-routing logic can be automated without elevation; actual HKLM/Program Files writes need elevated validation. |
