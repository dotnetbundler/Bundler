# appimagetool 第三方来源登记

> 用途：`DotNet.Bundler.AppImage` 后端内嵌的 AppImage 打包工具与 x86_64/aarch64 type2 runtime。
> 供应方式：固定文件内嵌为 EmbeddedResource 随 NuGet 包分发，运行时按 SHA-256 校验后落临时目录执行，不联网下载。

| 文件 | 来源 | SHA-256 | 说明 |
| --- | --- | --- | --- |
| `appimagetool-x86_64.AppImage` | https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage | `a6d71e2b6cd66f8e8d16c37ad164658985e0cf5fcaa950c90a482890cb9d13e0` | 官方 continuous 构建，`--version` 报 git 8c8c91f / build 295 / 2025-12-04 |
| `appimagetool-aarch64.AppImage` | https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-aarch64.AppImage | `1b00524ba8c6b678dc15ef88a5c25ec24def36cdfc7e3abb32ddcd068e8007fe` | 同上，aarch64 Linux 宿主用 |
| `runtime-x86_64` | https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64 | `1cc49bcf1e2ccd593c379adb17c9f85a36d619088296504de95b1d06215aebbf` | type2 runtime；所钉 appimagetool 构建不内嵌 runtime，缺省会联网下载——故始终经 `--runtime-file` 供应 |
| `runtime-aarch64` | https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-aarch64 | `7d5d772b7c32f0c84caf0a452a3072a5709027d7eac5856feb89a7a7a8881372` | type2 runtime，x86_64 宿主经 `--runtime-file` 交叉产 aarch64 AppImage 用 |

- 许可：均为 MIT（`LICENSE-appimagetool`、`LICENSE-runtime` 同目录）。
- 复核方式：重新下载上述 URL 后 `sha256sum` 比对；上游 continuous tag 会滚动，复核对不上时登记新哈希而非静默更新。
- 已实测：`ARCH=aarch64` + `--runtime-file runtime-aarch64` 在 x86_64 宿主产出 aarch64 ELF AppImage（决策 4 结论：可交叉）。
- 已实测：所钉构建的 mksquashfs 仅支持 zstd（`--comp gzip`/`--comp xz` 均报 "Compressor not supported"）——决策 10 按此为"仅 zstd"。
