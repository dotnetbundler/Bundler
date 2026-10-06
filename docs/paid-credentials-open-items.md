# 付费凭证外部待办清单

## 一、付费凭证类

> 本文件集中登记"需要付费凭证/会员资格才能验收"的待办项，并逐项给出平替方案；这些项暂缓处理。
> 明细仍归各格式 `-open-items.md` 登记，本表只做付费子集索引与替代路径，不重复格式细节。
> 设备/硬件类外部项（ARM64 真机、Intel Mac、干净宿主矩阵等）不登记于此——按用户裁决由借用设备或虚拟机届时验收，不构成付费阻塞。
> 其余零成本外部项（干净 VM、发行版容器矩阵、静态托管、GPG 密钥等）同不在此登记。

| 付费条件 | 市价 | 覆盖的待办项 |
| --- | --- | --- |
| Authenticode 代码签名证书（OV/EV） | OV 约 ¥700–3000/年，EV 约 ¥2000–5000/年 | NSIS 生产签名全链、MSI-OI-06、SmartScreen 声誉 |
| Apple Developer Program | $99/年 | MAC-APP-OI-01/05/07、MAC-DMG-OI-02、MAC-PKG-OI-02/03、UPDATE-OI-02 |
| RFC 3161 时间戳服务 | 免费 | MSI-OI-06 的时间戳子项、NSIS 时间戳接受度——非付费项，见末节 |

| 项 | 付费条件 | 影响 | 平替方案 |
| --- | --- | --- | --- |
| NSIS 生产 Authenticode 全链签名 | Authenticode 证书 | 公开信任链、SmartScreen 声誉、时间戳接受度未验；签名管线（顺序/隔离/PE 签名/提供方调用/失败清理/秘密隐藏）本机已验 | ① Azure Trusted Signing 约 ¥70/月起按量计费，含时间戳，最低成本云签名；② SignPath 对合格 OSS 免费；③ 自签名+真实 RFC 3161 服务器验时间戳链路（零成本，见末节）；④ 最便宜的 OV 证书转售渠道（Certum/SSL.com 等） |
| MSI-OI-06 生产证书与时间戳 | Authenticode 证书 | 链、时间戳、签名验证的生产信任证据缺；测试证书已验管线 | 同上四条；时间戳子项可自签名腿单独消解 |
| SmartScreen 声誉 | EV 证书或 OV 证书+装机量积累 | 未签名/OV 件会吃 SmartScreen 警告 | ① EV 证书立即获声誉（贵）；② OV 证书靠分发量自然积累（免费但慢）；③ 接受警告期并在文档如实声明 |
| MAC-APP-OI-01 `.app` 签名+公证全链 | Apple Developer Program $99/年 | 签名→公证→staple→`spctl` 全链未验；当前仅 ad-hoc 签名腿 | ① 无真实平替——Developer ID 证书只发付费账号；② 管线侧 ad-hoc/自签名已验，缺的仅 Apple 信任链；③ 未签名分发可给用户 `xattr -d com.apple.quarantine`/右键打开的文档级绕行 |
| MAC-DMG-OI-02 DMG 本体 codesign | 同上 | 签名 DMG `codesign --verify`/`spctl` 未验 | 同 MAC-APP-OI-01 |
| MAC-PKG-OI-02/03 `.pkg` 签名+公证 | 同上（需 Developer ID Installer 型证书） | `.pkg` `pkgutil --check-signature`/`spctl` 与 `stapler validate` 未验 | 同 MAC-APP-OI-01；Installer 与 Application 证书不同型，一份 $99 会员两种都发 |
| MAC-APP-OI-05 证书撤销/到期边界 | 同上 | 撤销/过期场景试验缺可抛弃证书 | 依赖 $99 会员发证后再做；免费侧可用自签名链模拟过期但非真实 Apple 链 |
| MAC-APP-OI-07 provisioning profile | 同上 | universal links/受管 entitlement 场景 `embedded.provisionprofile` 未验；普通 deep link 不受阻 | 无平替——provisioning profile 只在付费账号下签发；不启用 universal links 则本项不阻塞 |
| UPDATE-OI-02 签名 `.app` 更新链 | 同上 | mac 换包前新旧签名身份一致性腿只能跑未签名腿 | 同 MAC-APP-OI-01；凭证到位后一次验收 |

## 二、免费可单独消解项：时间戳

RFC 3161 时间戳副签名协议不需要付费凭证。
用自签名证书 + 公共时间戳服务器（如 `http://timestamp.digicert.com`）即可真实跑通签名→取时间戳→嵌入副签名全链。
该腿证明的是时间戳管线与嵌入正确性，不证明 CA 信任链——信任链证据仍归付费凭证项。
对应 NSIS"时间戳接受度"与 MSI-OI-06 的时间戳子项可在零成本下先行消解。
