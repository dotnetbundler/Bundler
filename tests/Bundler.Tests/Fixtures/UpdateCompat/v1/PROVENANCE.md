# UpdateCompat/v1 冻件出处

由 `alpha.83`（squash `888a127`，context 提交 `bb49bba`）代码真实生成，2026-10-08：
`git worktree` → `dotnet build src/Bundler.Cli -c Release` → `bundler update-keygen` →
`bundler bundle --input-dir pub --rid linux-x64 --formats zip --update.feedUrl/--update.channel stable/--update.signingKeyFile`。

- `bootstrap/bundler-updater.sh` = `bb49bba` 的 tools/posix 引导件脚本（锁协议前形态）。
- `feed/` = v1 产出的 feed 目录（清单+sig、zip+blockmap+sig+sha256）；zip 内嵌的 `bundler-updater` 与 `bb49bba` tools/linux-x64 二进制 sha 相同（e3cba9f2…）——即 v1 装机件。
- `update.key`/`public-key.txt` = v1 `update-keygen` 产的**测试专用**密钥对（非生产密钥），供腿内构造"当前 feed"时签名用。
- 该冻件永不更新；将来有意断代时改断言并另冻 v2。
