# Alpine `.apk` 集成测试

测试体已收编进 `tests/Bundler.IntegrationTests`（`AlpineApkIntegrationTests`，xUnit v3）；
`Verify.sh` 只是薄入口，按 `--filter-class` 转发到对应测试类。无特定宿主时类级门禁记 `SKIP` 并 `exit 0`（门禁原则同其他后端）。

## 入口

```bash
bash tests/Alpine.Apk.Integration/Verify.sh
# 直接入口
dotnet test tests/Bundler.IntegrationTests/Bundler.IntegrationTests.csproj -c Release -- --filter-class AlpineApkIntegrationTests
```

门禁依赖：`dotnet`、`tar`、`gzip`、`sha256sum`、`openssl`、`python3`、`unzip`；缺失项对应断言记 `SKIP`（需 docker 的腿同样 `SKIP`）。

## 覆盖

- `dotnet pack` 与 `dotnet publish` 真实产出 `.apk`（默认 RID `linux-musl-x64`）。
- 结构断言：gzip 魔数 + 段数（未签 2 段、签名 3 段）、控制段/数据段零块规则、`.PKGINFO` 逐字段、`datahash` 与 `size` 自算对照、载荷路径清单（`/usr/lib` + `/usr/bin` 链接 + `docs/` + `/etc` 映射）。
- 契约测试：`APK-1` 默认最小配置、`APK-2` 元数据/脚本/任意映射、`APK-3` 签名段与独立 `openssl dgst` 验签、`APK-4` 确定性三产逐字节 + 失败变体（后端 API 直接消费由 `tests/Bundler.ApiTests` 的 `AlpineApkApiTests` 承担）。
- docker 腿：`alpine:latest` 容器内 `apk add --allow-untrusted` 实装 → 脚本标记 → 运行 → `apk del` 清理；升级腿——v1 `-r0` 装、v2 `-r1` 升，断言 v2 `.pre-upgrade`/`.post-upgrade` 标记执行且 v2 `.post-install` 不触发；未签名拒绝断言；公钥入 `/etc/apk/keys/` 后免 `--allow-untrusted` 实装断言。
- aarch64 腿：`-r linux-musl-arm64` 产出 `arch = aarch64`，若宿主支持 `binfmt`（`tonistiigi/binfmt`）则 `apk add` + 运行断言，否则 `SKIP`。

## 产物与调试

`artifacts/alpine-apk-integration/` 下保留各腿产物与日志；测试工作区带 `.bundler-identity` 身份标记，测试类 Dispose 时自动清理临时目录与演示密钥。
