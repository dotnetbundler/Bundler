# UpdateCompat/v1 冻件出处

由 `alpha.83`（squash `888a127`，context 提交 `bb49bba`）代码真实生成，2026-10-08：
`git worktree` → `dotnet build src/Bundler.Cli -c Release` → `bundler update-keygen` →
`bundler bundle --input-dir pub --rid linux-x64 --formats zip --update.feedUrl/--update.channel stable/--update.signingKeyFile`。

- `bootstrap/bundler-updater.sh` = `bb49bba` 的 tools/posix 引导件脚本（锁协议前形态）。
- `feed/` = v1 产出的 feed 目录（清单+sig、zip+blockmap+sig+sha256）；zip 内嵌的 `bundler-updater` 与 `bb49bba` tools/linux-x64 二进制 sha 相同（e3cba9f2…）——即 v1 装机件。
- v1 密钥对不入库（私钥仓规禁入库）；冻件 zip 侧车内烙的 v1 公钥仍参与腿 1 验签，腿内需签名的材料一律运行期生成。
- 该冻件永不更新；将来有意断代时改断言并另冻 v2。
