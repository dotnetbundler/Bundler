# 目标矩阵

`target` 字符串语法：`{os}[-{libc}]-{arch}`，全小写。
`libc` 段仅 linux 有：`glibc` 为默认（不写），`musl` 显式声明才挂——静态件（如 Go 式零依赖）应直接打不带 musl 的 linux 目标。

## 系统

OS 枚举（`TargetOperatingSystem`）只有 `Windows`/`MacOS`/`Linux` 三个值；libc 是独立旁维度（`LibC` 枚举），不是 OS 值——与 Rust target triple 的 env 段、dpkg `musl-linux-*` ABI 限定词的建模方式一致。

| os 词 | 覆盖 |
| --- | --- |
| `windows` | Windows 桌面 |
| `macos` | macOS（含 `universal` 双架构胖件态） |
| `linux` | glibc 系发行版（Debian/Ubuntu/Fedora…）＋ `linux-musl-*` musl 系（Alpine…） |

## 架构

规范词即输入词；别名归一后只存规范词。

| 规范词 | 别名 | deb | rpm | apk | win | mac | appimage |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `x86_64` | `amd64`、`x64` | `amd64` | `x86_64` | `x86_64` | `x64` | `x86_64` | `x86_64` |
| `i686` | `i386`、`x86`、`386` | `i386` | `i686` | `x86` | `x86` | — | `i686` |
| `aarch64` | `arm64` | `arm64` | `aarch64` | `aarch64` | `arm64` | `arm64` | `aarch64` |
| `universal` | — | — | — | — | — | `universal` | — |
| `armv7hf` | `armhf`、`armv7` | `armhf` | `armv7hl` | `armv7` | — | — | — |
| `armv7sf` | `armel` | `armel` | — | `armel` | — | — | — |
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

后端声明服务哪个 libc 变体：`AlpineApk` 声明 `RequiredLibC=Musl`，`AppImage`/`Archive` 声明任意（两族同一后端），其余后端只服务各自 OS。

## 命名契约

- 平台绑定格式：文件名带该平台惯用架构词——`n-v-x64-setup.exe`、`n-v-arm64.msi`、`n-v-universal.dmg`。
- deb/rpm/apk：各自生态原生命名（`n_v-rel_arch.deb` 等），不另加目标段。
- AppImage：`n-v-<arch>[-musl].AppImage`——musl 变体只在同格式同架构存在 glibc+musl 双变体时挂 `-musl`。
- 归档：`{name}-{ver}-{os}-{arch}[-musl].{ext}`——`os` 用目标词，`arch` 用该系统生态惯用词（Windows 同安装器词 `x64|x86|arm64`，macOS 同 .app 词 `x86_64|arm64|universal`，linux 用规范 GNU 词）。
- `.app` 是目录产物不是文件；侧车 `.sha256`/`.sig`/`.blockmap` 跟随各产物；
  `bundler-update-feed.{channel}.json` 恒落输出根。

## 产物名全目录

以 `name=myapp`、`ver=1.0.0`、deb/rpm `release=1`、apk `release=r0` 为例。
每格词形均以官方文档或权威项目实例为依据，不靠约定俗成。

### nsis（Windows，3 架构词）

`myapp-1.0.0-x64-setup.exe`、`myapp-1.0.0-x86-setup.exe`、`myapp-1.0.0-arm64-setup.exe`

- 依据：NSIS `MACHINE` 预定义宏取值 `x86|amd64|arm64`（nsis.sourceforge.io 文档）；微软自身安装器惯用 `x64|x86|arm64`（Visual C++ 运行库下载命名）。

### msi（Windows，3 架构词 × 38 语言）

`myapp-1.0.0-x64.msi` / `-x86.msi` / `-arm64.msi`；非 en-US 语言挂语言段：`myapp-1.0.0-x64-zh-cn.msi`。

- 依据：WiX `candle -arch x86|x64|arm64` 官方三值（wixtoolset.org 文档）。

### .app / .dmg / .pkg（macOS，3 架构词）

`myapp-1.0.0-x86_64.app|dmg|pkg`、`myapp-1.0.0-arm64.app|dmg|pkg`、`myapp-1.0.0-universal.app|dmg|pkg`

- 依据：Apple 生态 `uname -m`/`lipo -archs` 输出 `x86_64|arm64`；universal2 双切片为 Apple 官方分发形态。

### appimage（Linux，3 架构词 + musl 变体）

`myapp-1.0.0-x86_64.AppImage`、`myapp-1.0.0-i686.AppImage`、`myapp-1.0.0-aarch64.AppImage`；
musl：`myapp-1.0.0-x86_64-musl.AppImage`。

- 依据：AppImageSpec `Name-$VERSION-$ARCH.AppImage`（$ARCH 取 `uname -m`，且规范要求名中不含 "linux" 平台段——双变体区分 libc 才挂 `-musl`）。

### deb（Linux glibc，8 架构词）

`myapp_1.0.0-1_{amd64|i386|arm64|armhf|armel|riscv64|ppc64el|s390x}.deb`

- 依据：`dpkg-architecture --list-known` 官方架构表（loongarch 的官方词是 `loong64`，非 `loongarch64`，故未开通）。

### rpm（Linux glibc，8 架构词）

`myapp-1.0.0-1.{x86_64|i686|aarch64|armv7hl|riscv64|loongarch64|ppc64le|s390x}.rpm`

- 依据：rpm 官方 `rpmrc.in` arch_canon 表（`armv7sf` 软浮点无 canonical 词，未开通）。

### alpineapk（Linux musl，9 架构词）

`myapp-1.0.0-r0.{x86_64|x86|aarch64|armv7|armel|riscv64|loongarch64|ppc64le|s390x}.apk`

- 依据：`apk-package(5)` 官方架构列表——`armv7hf→armv7`、`armv7sf→armel`（硬/软浮点分词，`armhf`/`armel` 亦在表）。

### zip / targz（全 OS，按系统生态词）

- Windows：`myapp-1.0.0-windows-{x64|x86|arm64}.zip|.tar.gz`
- macOS：`myapp-1.0.0-macos-{x86_64|arm64|universal}.zip|.tar.gz`
- Linux：`myapp-1.0.0-linux-{x86_64|i686|aarch64|armv7hf|armv7sf|riscv64|loongarch64|ppc64le|s390x}.zip|.tar.gz`；musl 挂尾：`myapp-1.0.0-linux-x86_64-musl.zip`

- 依据：无跨平台归档命名标准，照真实主流项目的系统生态词——Node 官方发布 `node-v*-win-x64.zip`/`darwin-arm64.tar.gz`/`linux-x64-musl.tar.gz`，.NET SDK `osx-arm64.tar.gz`；GNU 词只在 linux 目标用（`uname -m` 原生输出）。

### 侧车与 feed

每件产物挂 `.sha256`；可签格式另挂 `.sig`；nsis/可增量格式挂 `.blockmap`。
更新 feed `bundler-update-feed.{channel}.json` 恒落输出根目录（两种布局都钉根）。

## 输出布局

`outputLayout`（bundler.json）/ `--output-layout`（CLI）/ `BundlerOutputLayout`（MSBuild）：

- `flat`（默认）：全部产物直接落 `outputDirectory/`。
- `byFormat`：产物按 `outputDirectory/<format>/` 分格（feed 仍钉根）。

规划期对全部产物名查重——同目录同名即打包错误，新后端接入自动罩住。
