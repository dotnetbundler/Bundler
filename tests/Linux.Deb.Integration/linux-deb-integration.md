# Linux .deb 集成测试

LINUX-DEB-1 的本机真实验证入口：真实 .NET payload → `BundlerFormats=deb` → `ar`/`dpkg-deb` 结构断言 → `dpkg-deb -I/-c` 元数据与清单核对 → `md5sums`/`sha256` 校验 → 免密 `sudo dpkg -i/-r` 真实装卸烟雾。

## 运行

```bash
bash tests/Linux.Deb.Integration/Verify.sh
```

## 前置条件

- Linux 宿主（脚本自带 `uname` 检查，非 Linux 直接拒绝），dpkg/apt 系发行版；
- dotnet SDK（打包 `DotNet.Bundler*` 包供 fixture 消费）；
- `ar`/`tar`/`md5sum`/`sha256sum`/`dpkg-deb`/`unzip`；
- 免密 `sudo`（`sudo -n true`）用于真实 `dpkg -i/-r`；缺失时该段自动 SKIP，其余断言不受影响。

## 断言范围

- `artifacts/packages` 中 `DotNet.Bundler.Deb` 包产出与 `DotNet.Bundler.MSBuild` 包内 `DotNet.Bundler.Deb.dll` 装载；
- `BundlerFormats=deb` 经 MSBuild 产出 `artifacts/linux-x64/deb/<package>_<version>_<arch>.deb` + `.sha256` 侧车；
- `ar t` 三成员结构、`dpkg-deb -I` 可识别、`dpkg-deb -c` 清单含 `usr/lib` 载荷、`docs/readme.txt` 资源与相对 `usr/bin` 符号链接、主程序 `0755`；
- control 核心字段（`Package`/`Version`/`Architecture`/`Maintainer`/`Priority`/`Homepage`/`Installed-Size`/`Description`）与 `md5sums` 真实性；
- 覆盖变体（`BundlerDebPackageName`/`BundlerDebVersion`/`BundlerDebMaintainer`/`BundlerDebInstallRoot`/`BundlerDebBinLink`）逐项回读断言——`InstallRoot` 非 `/usr` 下时链接目标转绝对路径；
- SemVer 预发布映射变体（`2.5.0-beta.3+build.1` → `2.5.0~beta.3+build.1-1`）；
- 失败路径：相对 `InstallRoot` 使 publish 失败且无 `.deb` 产物；
- `tests/Deb.Api.PackageFixture`（直接 API 消费 `DotNet.Bundler.Deb` NuGet 包）冒烟；
- 真实装卸：`sudo dpkg -i` 后 `dpkg -s`/`dpkg -L`/`/usr/bin` 链接启动均通过，`sudo dpkg -r` 后零残留断言。

产物仅落在 `artifacts/linux-deb-integration`（脚本用 `.bundler-identity` 标记自建目录，退出时整体清理）。

真实 `dpkg -i/-r` 修改宿主 dpkg 数据库；其余发行版矩阵（rpm 系宿主构建、Debian 老版本、ARM64 机器）与 lintian 告警审计属外部待验收（`docs/linux-deb-open-items.md` / `linux-deb-manual-testing.md`）。
