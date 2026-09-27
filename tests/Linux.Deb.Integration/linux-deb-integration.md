# Linux .deb 集成测试

LINUX-DEB-1 的本机真实验证入口：真实 .NET payload → `BundlerFormats=deb` → `ar`/`dpkg-deb` 结构断言 → `dpkg-deb -I/-c` 元数据与清单核对 → `md5sums`/`sha256` 校验 → 免密 `sudo dpkg -i/-r` 真实装卸烟雾。

## 运行

```bash
bash tests/Linux.Deb.Integration/Verify.sh
```

## 前置条件

- Linux 宿主（脚本自带 `uname` 检查，非 Linux 直接拒绝），dpkg/apt 系发行版；
- dotnet SDK（打包 `DotNet.Bundler*` 包供 fixture 消费）；
- `ar`/`tar`/`md5sum`/`sha256sum`/`dpkg-deb`/`unzip`/`gzip`；
- 可选：`desktop-file-validate`、`lintian`（缺失时对应断言记 SKIP/信息级）；
- 免密 `sudo`（`sudo -n true`）用于真实 `dpkg -i/-r`；缺失时该段自动 SKIP，其余断言不受影响。

## 断言范围

- `artifacts/packages` 中 `DotNet.Bundler.Deb` 包产出与 `DotNet.Bundler.MSBuild` 包内 `DotNet.Bundler.Deb.dll` 装载；
- `BundlerFormats=deb` 经 MSBuild 产出 `artifacts/linux-x64/deb/<package>_<version>_<arch>.deb` + `.sha256` 侧车；
- `ar t` 三成员结构、`dpkg-deb -I` 可识别、`dpkg-deb -c` 清单含 `usr/lib` 载荷、`docs/readme.txt` 资源与相对 `usr/bin` 符号链接、主程序 `0755`；
- control 核心字段（`Package`/`Version`/`Architecture`/`Maintainer`/`Priority`/`Homepage`/`Installed-Size`/`Description`）与 `md5sums` 真实性；
- 覆盖变体（`BundlerDebPackageName`/`BundlerDebVersion`/`BundlerDebMaintainer`/`BundlerDebInstallRoot`/`BundlerDebBinLink`）逐项回读断言——`InstallRoot` 非 `/usr` 下时链接目标转绝对路径；
- SemVer 预发布映射变体（`2.5.0-beta.3+build.1` → `2.5.0~beta.3+build.1-1`）；
- 桌面集成（LINUX-DEB-2）：生成的 `.desktop` 落 `usr/share/applications/` 且逐字段断言（含 `MimeType` 并集与 `%u` 占位符）、`desktop-file-validate` 通过、hicolor `48x48` 与 `48x48@2` 图标落位、metainfo/`copyright`/`changelog.gz` 内容与 gzip 可解、`BundlerDebFile` `/etc` 落位；
- 元数据变体：`Depends`/`Recommends`/`Provides`/`Conflicts`/`Replaces`/`Section`/`Priority` 逐项 control 断言；
- `BundlerDebDesktopFile` 覆盖变体：自定义 `.desktop` 原样落位并通过 `desktop-file-validate`；
- 脚本变体（LINUX-DEB-3）：`postinst`/`prerm`/`postrm` 入 control 归档 0755、shebang 断言、conffiles 成员内容，真实 `dpkg -i/-r` 下标记文件断言脚本执行；
- systemd 变体：unit 落 `usr/lib/systemd/system/`、postinst 含 `daemon-reload` 尾段且保留调用方脚本主体；
- 升级变体：`dpkg -i` 1.0.0→1.0.1 版本更新 + 本地修改的 conffile 经 `--force-confold` 保留；
- conffile 语义：`/etc` DebFile 自动登记 conffile——`dpkg -r` 保留、`dpkg -P` 清除（断言已随 DEB-3 更新为两段式）；
- arm64 变体（LINUX-DEB-4）：`linux-arm64` publish → `_arm64.deb` + `Architecture: arm64` + 载荷结构断言（x86_64 宿主不能装测，真机安装属 OI-01）；
- docker 矩阵（LINUX-DEB-4）：`debian:stable`/`ubuntu:latest` 容器内真实 `dpkg -i`/运行/`-r`/`-P` 全绿；
- lintian 基线（LINUX-DEB-4）：豁免清单 `lintian-exemptions.txt` 硬断言，新 tag 即失败；
- 失败路径：相对 `InstallRoot`、非法 `Priority`、非法 `Categories`、非 `gzip` 压缩均使 publish 失败且无 `.deb` 产物；
- `tests/Deb.Api.PackageFixture`（直接 API 消费 `DotNet.Bundler.Deb` NuGet 包）冒烟；
- 真实装卸：`sudo dpkg -i` 后 `dpkg -s`/`dpkg -L`（含 `.desktop`/图标/metainfo/`copyright`/`changelog.gz`/`/etc` 逐项路径断言）与 `/usr/bin` 链接启动均通过，装后 `.desktop` 回读 + `desktop-file-validate` 复核，`sudo dpkg -r` 后零残留断言；
- `lintian` 以信息级跑全包（不阻断），残余发现登记 `docs/linux-deb-open-items.md` LINUX-DEB-OI-07。

产物仅落在 `artifacts/linux-deb-integration`（脚本用 `.bundler-identity` 标记自建目录，退出时整体清理）。

真实 `dpkg -i/-r` 修改宿主 dpkg 数据库；其余发行版矩阵（rpm 系宿主构建、Debian 老版本、ARM64 机器）、真实桌面环境观感与 lintian 残余处置属外部待验收（`docs/linux-deb-open-items.md` / `linux-deb-manual-testing.md`）。
