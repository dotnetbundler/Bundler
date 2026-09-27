# Linux .rpm 集成测试

LINUX-RPM-1 的本机真实验证入口：真实 .NET payload → `BundlerFormats=rpm` → `rpm -qip` 元数据断言 → `rpm2cpio`/`cpio` 载荷回读 → docker `fedora:latest` 容器真实 `rpm -i`/`rpm -e` 烟雾。

## 运行

```bash
bash tests/Linux.Rpm.Integration/Verify.sh
```

## 前置条件

- Linux 宿主（脚本自带 `uname` 检查，非 Linux 直接拒绝）；
- dotnet SDK（打包 `DotNet.Bundler*` 包供 fixture 消费）；
- `sha256sum`/`unzip`/`gzip`（必需）；
- 可选：`rpm`、`rpm2cpio`+`cpio`、`rpmlint`、`docker`（缺失时对应断言记 SKIP）；
- docker 可用时 `fedora:latest` 容器内真实 `rpm -i`/`rpm -q`/`rpm -ql`/运行/`rpm -V`/`rpm -e` 零残留断言。

## 断言范围

- `artifacts/packages` 中 `DotNet.Bundler.Rpm` 包产出与 `DotNet.Bundler.MSBuild` 包内 `DotNet.Bundler.Rpm.dll` 装载；
- `BundlerFormats=rpm` 经 MSBuild 产出 `artifacts/linux-x64/rpm/<name>-<version>-<release>.<arch>.rpm` + `.sha256` 侧车；
- `rpm -qip` 可识别：Name/Version/Release/Arch 与 PAYLOADFORMAT=cpio、PAYLOADCOMPRESSOR=gzip、VENDOR 逐项断言；
- `rpm -qp --provides` 自提供（`name = evr` 与 `name(arch) = evr`）、`--requires` 含 rpmlib 三依赖；
- `rpm -qplv` 目录显式条目（`usr/lib/<pkg>` 等）与 `/usr/bin` 符号链接；
- `rpm2cpio | cpio -idm` 载荷回读：主程序 0755 可执行、`docs/readme.txt` 资源、符号链接目标正确、二进制可运行——GNU cpio 不为符号链接自动建缺失父目录（真实 `rpm -i` 无此问题），脚本按 `rpm -qplv` 清单预建；
- 覆盖变体：`BundlerRpmPackageName/Version/Release/Epoch/Architecture/Vendor/InstallRoot/BinLink` 逐项回读断言（含 `noarch`、`/opt` 安装根转绝对链接、`BinLink=none` 关闭链接）；
- SemVer 预发布映射变体：`1.0.0-alpha.2` → `Version: 1.0.0` + `Release: 0.1.alpha.2`；
- 失败路径：非法包名使 publish 失败且不残留 `.rpm`；
- `deb;rpm` 多格式扇出变体：`-p:BundlerTestFormats="deb%3Brpm"` 同次 publish 同时产出 `.deb` 与 `.rpm`；
- `tests/Rpm.Api.PackageFixture`（直接 API 消费 `DotNet.Bundler.Rpm` NuGet 包）冒烟；
- `rpmlint` 信息级跑全包（不阻断）——基线硬断言在 LINUX-RPM-3 落地，残余发现届时入档豁免清单。

## 已知边界

arm64 真机安装、rpm 系其他发行版（rocky/opensuse）容器矩阵、`rpmlint` 基线、GPG 签名——登记 `docs/linux-rpm-open-items.md`，按规划轮决策排后续阶段。
