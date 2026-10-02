# Linux .rpm 集成测试

LINUX-RPM-1..4 与 SIGN-1 的本机真实验证入口：真实 .NET payload → `BundlerFormats=rpm` → `rpm -qip`/`--queryformat` 逐字段断言（含关系字段三件套与 FILEFLAGS %doc/%license）→ `desktop-file-validate` → `rpm2cpio`/`cpio` 载荷回读 → docker `fedora`/`rockylinux`/`opensuse` 三容器真实 `rpm -i`/`rpm -ql` 逐路径/`rpm -e` 零残留 + rpmlint 豁免基线门控 → 可选 GPG 签名腿（测试密钥现生成，容器内 `rpm --import` + `rpm -K` 验签断言）。

## 运行

```bash
bash tests/Linux.Rpm.Integration/Verify.sh
```

## 前置条件

- Linux 宿主（脚本自带 `uname` 检查，非 Linux 直接拒绝）；
- dotnet SDK（打包 `DotNet.Bundler*` 包供 fixture 消费）；
- `sha256sum`/`unzip`/`gzip`（必需）；
- 可选：`rpm`、`rpm2cpio`+`cpio`、`rpmlint`、`docker`、`desktop-file-validate`（缺失时对应断言记 SKIP）；
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
- RPM-2 落位断言：`.desktop`（生成/覆盖两态）+ hicolor 图标（`48x48`/`48x48@2`）+ metainfo + `changelog.gz` + `/usr/share/licenses/<pkg>/` + `/etc/<pkg>/` 自定义文件逐项进 `rpm -qpl` 清单；
- FILEFLAGS 断言：license 文件 flag 128、doc 文件 flag 2；目录占有规则断言（包叶子目录占有、`/usr/share/icons` 子树与共享系统目录不占有）；
- 关系字段变体：Requires/Provides/Conflicts/Obsoletes/Recommends/Suggests 与 License/Group/Url 覆盖经 `rpm -qp --requires/--provides/--conflicts/--obsoletes/--recommends/--suggests` 与 `--queryformat` 逐项断言；
- 失败变体扩展：非法依赖子句（`!=`）与相对 `BundlerRpmFile` 目标均使 publish 失败；
- 容器断言扩展：装后 `rpm -ql` 逐路径 + `rpm -qd` %doc 可见 + 卸载带走包自有叶子目录；
- RPM-3 断言：`-p:BundlerTestRpmScripts=1` 变体在容器内真实执行 `%pre`/`%post`/`%preun`/`%postun`（标记文件逐行断言）、systemd unit 落位与 `daemon-reload` 合成；`%config(noreplace)` 语义——`rpm -e` 对修改过的 `/etc` 文件保留 `.rpmsave`、`rpm -U` 升级（Release=1→2）原地保留；CRLF scriptlet/越界 ConfigFiles/非 gzip 压缩三失败变体；
- `tests/Rpm.Api.PackageFixture`（直接 API 消费 `DotNet.Bundler.Rpm` NuGet 包）冒烟；
- `rpmlint` 硬断言门控（LINUX-RPM-4 起生效）：出现 `rpmlint-exemptions.txt` 之外的 tag 即失败；当前豁免 7 项（no-signature/no-packager-tag/no-group-tag/no-changelogname-tag/invalid-license/binary-or-shlib-defines-rpath/no-manual-page-for-binary）。

## 已知边界

linux-arm64 产物结构断言（`*.aarch64.rpm`、`ARCH=aarch64`、载荷结构——结构已测、真机安装仍登记 OI）
- docker 发行版装卸矩阵：`fedora:latest`+`rockylinux:9`+`opensuse/leap:latest` 三容器真实 `rpm -i/-e`（镜像不可拉取时记 SKIP）

arm64 真机安装属外部待验收——登记 `docs/linux-rpm-open-items.md`；GPG 签名已实现（SIGN-1，本脚本含签名腿断言）。
