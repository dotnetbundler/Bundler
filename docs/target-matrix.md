# 目标矩阵

`target` 字符串语法：`{os}[-{libc}]-{arch}`，全小写。
`libc` 段仅 linux 有：`glibc` 为默认（不写），`musl` 显式声明才挂——静态件（如 Go 式零依赖）应直接打不带 musl 的 linux 目标。

## 系统

| os 词 | 覆盖 |
| --- | --- |
| `windows` | Windows 桌面 |
| `macos` | macOS（含 `universal` 双架构胖件态） |
| `linux` | glibc 系发行版（Debian/Ubuntu/Fedora…） |

## 架构

规范词即输入词；别名归一后只存规范词。

| 规范词 | 别名 | deb | rpm | apk | win | mac | appimage |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `x86_64` | `amd64`、`x64` | `amd64` | `x86_64` | `x86_64` | `x64` | `x86_64` | `x86_64` |
| `i686` | `i386`、`x86`、`386` | `i386` | `i686` | `x86` | `x86` | — | `i686` |
| `aarch64` | `arm64` | `arm64` | `aarch64` | `aarch64` | `arm64` | `arm64` | `aarch64` |
| `universal` | — | — | — | — | — | `universal` | — |
| `armv7hf` | `armhf`、`armv7` | `armhf` | `armv7hl` | `armv7` | — | — | — |
| `armv7sf` | `armel` | `armel` | `armv5tel` | `armhf` | — | — | — |
| `riscv64` | — | `riscv64` | `riscv64` | `riscv64` | — | — | — |
| `loongarch64` | — | — | `loongarch64` | `loongarch64` | — | — | — |
| `ppc64le` | — | `ppc64el` | `ppc64le` | `ppc64le` | — | — | — |
| `s390x` | — | `s390x` | `s390x` | `s390x` | — | — | — |

`universal` 仅 `macos`；`armv7*` 起按格式逐个开通，未开通组合走矩阵拒绝。

## 格式×目标矩阵

| target | 可用格式 |
| --- | --- |
| `windows-*` | `nsis`、`msi`、`zip`、`targz` |
| `macos-*` | `app`、`dmg`、`pkg`、`zip`、`targz` |
| `linux-*`（glibc） | `deb`、`rpm`、`appimage`、`zip`、`targz` |
| `linux-musl-*` | `alpineapk`、`appimage`、`zip`、`targz` |

## 命名契约

- 平台绑定格式：文件名带该平台惯用架构词——`n-v-x64-setup.exe`、`n-v-arm64.msi`、`n-v-universal.dmg`。
- deb/rpm/apk：各自生态原生命名（`n_v-rel_arch.deb` 等），不另加目标段。
- AppImage：`n-v-<arch>[-musl].AppImage`——musl 变体只在同格式同架构存在 glibc+musl 双变体时挂 `-musl`。
- 归档：`{name}-{ver}-{os}-{arch}[-musl].{ext}`（`n-v-linux-x86_64-musl.zip`）。
- `.app` 是目录产物不是文件；侧车 `.sha256`/`.sig`/`.blockmap` 跟随各产物；
  `bundler-update-feed.{channel}.json` 恒落输出根。

## 输出布局

`outputLayout`（bundler.json）/ `--output-layout`（CLI）/ `BundlerOutputLayout`（MSBuild）：

- `flat`（默认）：全部产物直接落 `outputDirectory/`。
- `byFormat`：产物按 `outputDirectory/<format>/` 分格（feed 仍钉根）。

规划期对全部产物名查重——同目录同名即打包错误，新后端接入自动罩住。
