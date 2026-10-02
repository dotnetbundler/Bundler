# Alpine `.apk` 集成验证脚本

`tests/Alpine.Apk.Integration/Verify.sh` 是 Alpine `.apk` 后端的本机自动化验收：无特定宿主时 `SKIP` 并 `exit 0`（门禁原则同其他后端）。

## 入口

```bash
bash tests/Alpine.Apk.Integration/Verify.sh
```

门禁依赖：`dotnet`、`tar`、`gzip`、`sha256sum`、`openssl`、`python3`、`unzip`；可跳过任何缺失项（需 docker 时同样 `SKIP`）。

## 覆盖

- `dotnet pack` 与 `dotnet publish` 真实产出 `.apk`（默认 RID `linux-musl-x64`）。
- 结构断言：gzip 魔数 + 段数（未签 2 段、签名 3 段）、控制段/数据段零块规则、`.PKGINFO` 逐字段、`datahash` 与 `size` 自算对照、载荷路径清单（`/usr/lib` + `/usr/bin` 链接 + `docs/` + `/etc` 映射）。
- 契约测试：`APK-1` 默认最小配置、`APK-2` 元数据/脚本/任意映射、`APK-3` 签名段与独立 `openssl dgst` 验签、`APK-4` 确定性三产逐字节 + `API` 包直接消费 + 失败变体。
- docker 腿：`alpine:latest` 容器内 `apk add --allow-untrusted` 实装 → 脚本标记 → 运行 → `apk del` 清理；升级腿——v1 `-r0` 装、v2 `-r1` 升，断言 v2 `.pre-upgrade`/`.post-upgrade` 标记执行且 v2 `.post-install` 不触发；未签名拒绝断言；公钥入 `/etc/apk/keys/` 后免 `--allow-untrusted` 实装断言。
- aarch64 腿：`-r linux-musl-arm64` 产出 `arch = aarch64`，若宿主支持 `binfmt`（`tonistiigi/binfmt`）则 `apk add` + 运行断言，否则 `SKIP`。

## 产物与调试

`artifacts/alpine-apk-integration/` 下保留各腿产物与日志；脚本结束自动清理临时目录与演示密钥。
