#!/bin/sh
# bundler-updater POSIX 降级实现：与 bundler-updater apply 同协议。
# 覆盖无 Native AOT 引导件的宿主（老 macOS/裸 POSIX 便携件场景）。
# 安装目标是目录或单文件（AppImage 单件走文件级换包）。
# 退出码：0 成功；2 用法错误；3 等待宿主退出超时；4 备份/换包失败（已尽力回滚）。

set -u

INSTALL_DIR=""
PAYLOAD_DIR=""
WAIT_PID=""
APP_PATH=""
BACKUP_DIR=""
RETAIN_DIR=""
BACKUP_CMP=""
RETAIN_CMP=""
LOG_FILE=""
KEEP_PAYLOAD=0
ROLLBACK=0
WAIT_TIMEOUT=120

usage() {
    echo "Usage:" >&2
    echo "  bundler-updater.sh apply --install-dir <dir|file> --payload <dir|file>" >&2
    echo "      [--wait-pid <pid>] [--app <path>] [--backup-dir <dir|file>]" >&2
    echo "      [--keep-payload] [--rollback] [--retain-backup-to <dir>] [--log <file>] [--wait-timeout <seconds>]" >&2
}

log() {
    if [ -n "$LOG_FILE" ]; then
        printf '%s\n' "$1" >>"$LOG_FILE"
    else
        printf '%s\n' "$1"
    fi
}

restart_app() {
    # macOS .app 目录件走 LaunchServices open，其余 nohup 分离。
    case "$APP_PATH" in
        *.app) if [ "$(uname -s)" = "Darwin" ]; then
            /usr/bin/open -n "$APP_PATH" || true
        else
            (cd "$(dirname "$INSTALL_DIR")" && nohup "$APP_PATH" >/dev/null 2>&1 &)
        fi ;;
        *) (cd "$(dirname "$INSTALL_DIR")" && nohup "$APP_PATH" >/dev/null 2>&1 &) ;;
    esac
}

# 同卷 mv=rename 原子就位；跨卷 mv 退化为 copy+unlink，busybox 半途失败会在
# 目标名上留下"存在≠完整"的半成品（备份槽的半成品会被恢复路径误当全本还原）。
# 与 AOT 侧 MoveTree 两段式同义：先复制到同级临时名再原子 mv 就位，
# 目标槽只呈现"未开始"或"全本"两态，半成品只活在 .partial-* 名下随失败清走。
move_node() {
    if mv "$1" "$2" 2>/dev/null; then
        return 0
    fi
    # 直 mv 半途可能已把部分条目落进目标名——先清槽再两段式。
    rm -rf "$2"
    _mn_staged="$2.partial-$$"
    rm -rf "$_mn_staged"
    if [ -d "$1" ]; then
        mkdir -p "$_mn_staged" || { rm -rf "$_mn_staged"; return 1; }
        cp -a "$1"/. "$_mn_staged"/ || { rm -rf "$_mn_staged"; return 1; }
    else
        cp -p "$1" "$_mn_staged" || { rm -rf "$_mn_staged"; return 1; }
    fi
    if mv "$_mn_staged" "$2"; then
        rm -rf "$1"
        return 0
    fi
    rm -rf "$_mn_staged"
    return 1
}

[ "${1:-}" = "apply" ] || { usage; exit 2; }
shift

while [ $# -gt 0 ]; do
    case "$1" in
        --keep-payload) KEEP_PAYLOAD=1; shift ;;
        --rollback) ROLLBACK=1; shift ;;
        --install-dir|--payload|--wait-pid|--app|--backup-dir|--retain-backup-to|--log|--wait-timeout)
            [ $# -ge 2 ] || { echo "bundler-updater: option '$1' requires a value." >&2; exit 2; }
            case "$1" in
                --install-dir) INSTALL_DIR=$2 ;;
                --payload) PAYLOAD_DIR=$2 ;;
                --wait-pid) WAIT_PID=$2 ;;
                --app) APP_PATH=$2 ;;
                --backup-dir) BACKUP_DIR=$2 ;;
                --retain-backup-to) RETAIN_DIR=$2 ;;
                --log) LOG_FILE=$2 ;;
                --wait-timeout) WAIT_TIMEOUT=$2 ;;
            esac
            shift 2 ;;
        *) echo "bundler-updater: unknown option '$1'." >&2; usage; exit 2 ;;
    esac
done

# 字面前缀判与派生路径都会被 ../.// 等拼写骗过：规范化只做在嵌套判里时，
# ${INSTALL_DIR}.bundler-backup 字面仍含 ..——retain 删掉中间目录后 mv 失解析。
# 故入参先按物理路径统一规范化（存在的目录直接解析，不存在的解析父目录再接回末段），
# 之后的派生、比较、文件操作全程只用规范化值，与 AOT 侧 GetFullPath 同义。
# 逐段链接解算（与 AOT 侧 CanonicalPath→ResolveLinkChain 同形）：每个路径段单独链走。
# readlink 跳走不依赖 stat 语义（lstat 口径下环链也报存在），悬挂链接
# （目标已搬进备份）照样解——崩溃恢复靠它找回 marker/备份。
# 每跳目标父链规范化再接回叶名：/var 类中间段自身是链接时，
# 字面拼写与比较对象的物理名错位会逃逸互嵌/等值判。
# 环判一律拼写等值重访（同 AOT resolving/visited 的 HashSet 语义）：
# 链走重访本链拼写（a→b→a），或嵌套 norm_seg 递归时重入栈上在解析的拼写
#（a→a/child 后代自指走父链规范化重入时命中）；40 跳=SYMLOOP_MAX 硬限——
# 耗尽后仍是链接即判环。命中即 return 1，调用方按无效输入拒。
# 已解拼写集用位置参数逐帧下传（resolve_link 与 norm_seg 互递归时栈上祖先可见）：
# 字符串集靠分隔符会切路径里的 | 等合法字符，位置参数任意拼写都安全。
# 只在确认是链接后才入栈——普通父级段解析即完不在解析中，入栈会被父链重走误报环。
_seen() {
    _c=$1; shift
    for _e do [ "$_c" = "$_e" ] && return 0; done
    return 1
}
resolve_link() {
    _rl=$1; shift
    _rlh=0
    while [ -L "$_rl" ]; do
        _seen "$_rl" "$@" && return 1
        set -- "$@" "$_rl"
        _rlh=$((_rlh + 1))
        [ "$_rlh" -gt 40 ] && return 1
        _rlt=$(readlink "$_rl") || break
        case "$_rlt" in
            /*) _rln=$_rlt ;;
            *) _rln="$(dirname "$_rl")/$_rlt" ;;
        esac
        _rln=$(norm_lexical "$_rln")
        _seen "$_rln" "$@" && return 1
        _rld=$(dirname "$_rln"); _rlb=$(basename "$_rln")
        _rlp=$(norm_seg "$_rld" "$@") || return 1
        _rl="${_rlp%/}/$_rlb"
    done
    printf '%s\n' "$_rl"
}

# 逐段规范化：任一段环链则整条判死（与 AOT CanonicalPath 遇 null 传播同义）。
norm_seg() {
    _ns_rest=$(norm_lexical "$1"); shift
    _ns_rest=${_ns_rest#/}
    _ns_out=
    while [ -n "$_ns_rest" ]; do
        _ns_seg=${_ns_rest%%/*}
        _ns_rest=${_ns_rest#"$_ns_seg"}
        _ns_rest=${_ns_rest#/}
        _ns_cur="$_ns_out/$_ns_seg"
        _ns_cur=$(resolve_link "$_ns_cur" "$@") || return 1
        _ns_out=$_ns_cur
    done
    printf '%s\n' "${_ns_out:-/}"
}

norm_path() {
    _np="${1%/}"
    norm_seg "$_np" || { echo "bundler-updater: path '$_np' resolves to a cyclic link." >&2; return 1; }
}

# 输出路径（backup/retain）只物理化父目录、叶段留拼写：叶段为符号链接时
# 若按物理名 rm -rf 会清掉链接目标——配置路径之外的真实目录；字面拼写只删链接本身。
# 父级不可解（环链）即拒——字面回退会把环链展开成永不存在的假字面链，
# 绕过拒绝拖到写 marker 后才失败（与 AOT CanonicalParentPath 可空化同义）。
norm_parent() {
    _np="${1%/}"
    _nd=$(dirname "$_np")
    _nb=$(basename "$_np")
    _nr=$(norm_seg "$_nd") || { echo "bundler-updater: path '$_nd' resolves to a cyclic link." >&2; return 1; }
    printf '%s/%s\n' "${_nr%/}" "$_nb"
}

# 父目录缺席时物理解析走不通——退回词法折叠消掉 ./.. 段，
# 否则 .. 留字面会绕开嵌套判，而 retain 的 mkdir -p 又恰好把逃逸路径做实。
norm_lexical() {
    _nl=$1
    case "$_nl" in /*) ;; *) _nl="$PWD/$_nl" ;; esac
    _saved_ifs=$IFS
    IFS='/'
    set -f
    # 故意不带引号：按 / 拆段
    set -- $_nl
    set +f
    IFS=$_saved_ifs
    _out=
    for _s do
        case "$_s" in
            ""|.) ;;
            ..) _out=${_out%/*} ;;
            *) _out="$_out/$_s" ;;
        esac
    done
    printf '%s\n' "${_out:-/}"
}

[ -n "$INSTALL_DIR" ] || { echo "bundler-updater: --install-dir is required." >&2; exit 2; }
[ "$ROLLBACK" = 1 ] || [ -n "$PAYLOAD_DIR" ] || { echo "bundler-updater: --payload is required unless --rollback." >&2; exit 2; }
INSTALL_DIR="$(norm_path "$INSTALL_DIR")" || exit 2
[ -z "$PAYLOAD_DIR" ] || PAYLOAD_DIR="$(norm_path "$PAYLOAD_DIR")" || exit 2
[ -z "$BACKUP_DIR" ] || BACKUP_DIR="$(norm_parent "$BACKUP_DIR")" || exit 2
[ -z "$RETAIN_DIR" ] || RETAIN_DIR="$(norm_parent "$RETAIN_DIR")" || exit 2
# 关系判另取全物理名：叶段为符号链接时字面拼写会逃逸同址/互嵌判
#（叶链指向 install/payload 在字面层面不同名）；文件操作仍走上面的叶字面拼写。
[ -z "$RETAIN_DIR" ] || RETAIN_CMP="$(norm_path "$RETAIN_DIR")" || exit 2
# 重启目标与日志同样按调用方 cwd 规范化成绝对路径——脚本的工作目录不是用户的 cwd。
[ -z "$APP_PATH" ] || APP_PATH="$(norm_path "$APP_PATH")" || exit 2
[ -z "$LOG_FILE" ] || LOG_FILE="$(norm_path "$LOG_FILE")" || exit 2
[ -n "$BACKUP_DIR" ] || BACKUP_DIR="${INSTALL_DIR%/}.bundler-backup"
# 备份比较名一律全物理化——默认备份位同样可能预置叶链（与 RETAIN_CMP 物理名不同名会同址逃逸）。
BACKUP_CMP="$(norm_path "$BACKUP_DIR")" || exit 2
MARKER="${INSTALL_DIR%/}.bundler-swap"

# 备份目录与安装/载荷同址或互嵌同样是抹数据的形状（换包前会 rm 旧备份）——与 AOT 侧同拒。
for _p in "$INSTALL_DIR" "$PAYLOAD_DIR"; do
    [ -n "$_p" ] || continue
    case "$BACKUP_CMP" in
        "$_p"|"$_p"/*) { echo "bundler-updater: backup/install/payload directories must not nest inside each other." >&2; exit 2; } ;;
    esac
    case "$_p" in
        "$BACKUP_CMP"/*) { echo "bundler-updater: backup/install/payload directories must not nest inside each other." >&2; exit 2; } ;;
    esac
done

# 载荷与安装同址或互嵌同样是抹数据的形状（备份移走后 swap 会以空载荷覆盖再删源）——与 AOT 侧同拒。
if [ -n "$PAYLOAD_DIR" ]; then
    case "$INSTALL_DIR" in
        "$PAYLOAD_DIR"|"$PAYLOAD_DIR"/*) { echo "bundler-updater: backup/install/payload directories must not nest inside each other." >&2; exit 2; } ;;
    esac
    case "$PAYLOAD_DIR" in
        "$INSTALL_DIR"/*) { echo "bundler-updater: backup/install/payload directories must not nest inside each other." >&2; exit 2; } ;;
    esac
fi

# 保留目录与安装/载荷/备份目录同址或互嵌同样是抹数据的形状——与 AOT 侧 SameOrInside 同拒。
if [ -n "$RETAIN_DIR" ]; then
    _r="$RETAIN_CMP"
    for _p in "$INSTALL_DIR" "$BACKUP_CMP" "$PAYLOAD_DIR"; do
        [ -n "$_p" ] || continue
        case "$_r" in
            "$_p"|"$_p"/*) { echo "bundler-updater: retained-backup directory must not nest inside install/payload/backup directories." >&2; exit 2; } ;;
        esac
        case "$_p" in
            "$_r"/*) { echo "bundler-updater: retained-backup directory must not nest inside install/payload/backup directories." >&2; exit 2; } ;;
        esac
    done
fi

# 备份的最终去向：--retain-backup-to 给了目录就迁过去当回滚点，不给就删——默认不保留；
# 换包期备份无论如何都建（崩溃恢复与原子性的载体）。$1=备份路径，$2=保留目录内文件名（文件级换包用）。
retain_or_remove_backup() {
    if [ -n "$RETAIN_DIR" ]; then
        if [ -n "${2:-}" ]; then
            target="$RETAIN_DIR/$2"
        else
            target="$RETAIN_DIR"
        fi
        mkdir -p "$(dirname "$target")" || return 4
        rm -rf "$target" || return 4
        log "bundler-updater: retain backup '$1' → '$target'"
        move_node "$1" "$target" || return 4
        return 0
    fi
    rm -rf "$1" 2>/dev/null || true
    return 0
}

if [ -n "$WAIT_PID" ]; then
    log "bundler-updater: waiting for pid $WAIT_PID to exit"
    waited=0
    while kill -0 "$WAIT_PID" 2>/dev/null; do
        waited=$((waited + 1))
        [ "$waited" -le "$WAIT_TIMEOUT" ] || { echo "bundler-updater: the target process did not exit in time." >&2; exit 3; }
        sleep 1
    done
fi

# marker 槽叶链一律删——真 marker 是本脚本写的普通文件；叶链会让 -f 顺链假触发
# 崩溃恢复、printf> 顺链写穿污染保护区外目标（与 AOT DeleteLinkNodeIfPresent 同义）。
[ -L "$MARKER" ] && rm -f "$MARKER"
# 崩溃恢复先于存在性检查：上轮死在备份与换包之间时安装目标可能缺失/半成品，先还原。
RECOVERED=0
if [ -f "$MARKER" ]; then
    log "bundler-updater: interrupted swap detected, restoring backup first"
    if [ -f "$BACKUP_DIR" ]; then
        rm -f "$INSTALL_DIR"
        move_node "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
    elif [ -d "$BACKUP_DIR" ]; then
        rm -rf "$INSTALL_DIR"
        move_node "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
    else
        { echo "bundler-updater: swap marker exists but backup missing — cannot recover." >&2; exit 4; }
    fi
    rm -f "$MARKER"
    RECOVERED=1
fi

# 文件级语义：安装目标是单文件（AppImage 单件）或回滚备份是文件——
# 目录级换包会清掉宿主目录里的无关文件，故单件替换。
if [ -f "$INSTALL_DIR" ] || { [ "$ROLLBACK" = 1 ] && [ -f "$BACKUP_DIR" ]; }; then
    if [ "$ROLLBACK" = 1 ]; then
        [ "$RECOVERED" = 1 ] && { log "bundler-updater: crash recovery already restored the backup"; exit 0; }
        [ -f "$BACKUP_DIR" ] || { echo "bundler-updater: no rollback backup at '$BACKUP_DIR'." >&2; exit 4; }
        log "bundler-updater: rollback '$BACKUP_DIR' → '$INSTALL_DIR'"
        cp -f "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback copy failed." >&2; exit 4; }
        [ -n "$APP_PATH" ] && { log "bundler-updater: restart '$APP_PATH'"; restart_app; }
        log "bundler-updater: done"
        exit 0
    fi
    [ -f "$PAYLOAD_DIR" ] || { echo "bundler-updater: file-swap payload must be a file." >&2; exit 2; }
    [ "$PAYLOAD_DIR" = "$INSTALL_DIR" ] && { echo "bundler-updater: install and payload must not be the same file." >&2; exit 2; }
    log "bundler-updater: backup '$INSTALL_DIR' → '$BACKUP_DIR'"
    # 备份父目录缺席时自建——与 AOT 侧 CopyTree 的隐式补链同义（POSIX mv 不会补）。
    mkdir -p "$(dirname "$BACKUP_DIR")" || exit 4
    rm -f "$BACKUP_DIR" || exit 4
    printf 'swap in progress' >"$MARKER" || exit 4
    move_node "$INSTALL_DIR" "$BACKUP_DIR" || { rm -f "$MARKER"; exit 4; }
    SWAP_FAILED=0
    if [ "$KEEP_PAYLOAD" = 1 ]; then
        log "bundler-updater: copy in '$PAYLOAD_DIR' → '$INSTALL_DIR'"
        cp -f "$PAYLOAD_DIR" "$INSTALL_DIR" || SWAP_FAILED=1
    else
        log "bundler-updater: swap in '$PAYLOAD_DIR' → '$INSTALL_DIR'"
        move_node "$PAYLOAD_DIR" "$INSTALL_DIR" || SWAP_FAILED=1
    fi
    if [ "$SWAP_FAILED" = 1 ]; then
        log "bundler-updater: swap failed, restoring backup"
        rm -f "$INSTALL_DIR"
        move_node "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
        rm -f "$MARKER"
        exit 4
    fi
    rm -f "$MARKER"
    # 文件级备份保留时按安装文件真名落在保留目录里；迁移失败降级留瞬备不阻断换包。
    retain_or_remove_backup "$BACKUP_DIR" "$(basename "$INSTALL_DIR")" || \
        log "bundler-updater: WARN retain/remove backup failed; transient backup left at '$BACKUP_DIR'."
    [ -n "$APP_PATH" ] && { log "bundler-updater: restart '$APP_PATH'"; restart_app; }
    [ "$KEEP_PAYLOAD" = 1 ] || rm -f "$PAYLOAD_DIR" 2>/dev/null || true
    log "bundler-updater: done"
    exit 0
fi

[ -d "$INSTALL_DIR" ] || { echo "bundler-updater: install directory '$INSTALL_DIR' does not exist." >&2; exit 2; }
[ "$ROLLBACK" = 1 ] || [ -d "$PAYLOAD_DIR" ] || { echo "bundler-updater: payload directory '$PAYLOAD_DIR' does not exist." >&2; exit 2; }

# macOS .app 三项门禁：签名完好/身份连续/剥 quarantine——动备份前拒绝，安装目录零变更。
case "$PAYLOAD_DIR" in
    *.app) IS_APP=1 ;;
    *) if [ -f "$PAYLOAD_DIR/Contents/Info.plist" ]; then IS_APP=1; else IS_APP=0; fi ;;
esac
if [ "$IS_APP" = 1 ] && [ "$ROLLBACK" != 1 ] && [ "$(uname -s)" = "Darwin" ]; then
    OLD_TEAM=$(codesign -dv --verbose=4 "$INSTALL_DIR" 2>&1 | sed -n 's/^TeamIdentifier=//p' | head -1)
    NEW_TEAM=$(codesign -dv --verbose=4 "$PAYLOAD_DIR" 2>&1 | sed -n 's/^TeamIdentifier=//p' | head -1)
    [ "$NEW_TEAM" = "not set" ] && NEW_TEAM=""
    [ "$OLD_TEAM" = "not set" ] && OLD_TEAM=""
    if [ -n "$NEW_TEAM" ]; then
        codesign --verify --deep --strict "$PAYLOAD_DIR" 2>/dev/null || \
            { echo "bundler-updater: payload failed codesign verification." >&2; exit 4; }
        if [ -n "$OLD_TEAM" ] && [ "$NEW_TEAM" != "$OLD_TEAM" ]; then
            echo "bundler-updater: cross-identity update refused ('$OLD_TEAM' → '$NEW_TEAM')." >&2; exit 4
        fi
        log "bundler-updater: codesign verified, team '$NEW_TEAM'"
    elif [ -n "$OLD_TEAM" ]; then
        echo "bundler-updater: signed → unsigned downgrade refused." >&2; exit 4
    fi
    # bundle id 一致兜底：未签场景防“拿别的应用来换”。
    OLD_BID=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$INSTALL_DIR/Contents/Info.plist" 2>/dev/null || true)
    NEW_BID=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$PAYLOAD_DIR/Contents/Info.plist" 2>/dev/null || true)
    if [ -n "$OLD_BID" ] && [ -n "$NEW_BID" ] && [ "$OLD_BID" != "$NEW_BID" ]; then
        echo "bundler-updater: bundle identifier mismatch ('$OLD_BID' vs '$NEW_BID')." >&2; exit 4
    fi
    xattr -dr com.apple.quarantine "$PAYLOAD_DIR" 2>/dev/null && log "bundler-updater: quarantine stripped" || true
fi

# 回滚：备份复制回安装目录（备份保留可重试），不再二次备份。
if [ "$ROLLBACK" = 1 ]; then
    if [ ! -d "$BACKUP_DIR" ]; then
        # 崩线恢复刚把备份还原回安装目录——备份移入即耗尽，安装目录已是目标态。
        [ "$RECOVERED" = 1 ] && { log "bundler-updater: crash recovery already restored the backup"; exit 0; }
        echo "bundler-updater: no rollback backup at '$BACKUP_DIR'." >&2; exit 4
    fi
    log "bundler-updater: rollback '$BACKUP_DIR' → '$INSTALL_DIR'"
    rm -rf "$INSTALL_DIR" || exit 4
    mkdir -p "$INSTALL_DIR" || exit 4
    cp -a "$BACKUP_DIR"/. "$INSTALL_DIR"/ || { echo "bundler-updater: rollback copy failed." >&2; exit 4; }
    [ -n "$APP_PATH" ] && { log "bundler-updater: restart '$APP_PATH'"; restart_app; }
    log "bundler-updater: done"
    exit 0
fi

log "bundler-updater: backup '$INSTALL_DIR' → '$BACKUP_DIR'"
# 备份父目录缺席时自建——与 AOT 侧 CopyTree 的隐式补链同义。
mkdir -p "$(dirname "$BACKUP_DIR")" || exit 4
rm -rf "$BACKUP_DIR" || exit 4
printf 'swap in progress' >"$MARKER" || exit 4
move_node "$INSTALL_DIR" "$BACKUP_DIR" || { rm -f "$MARKER"; exit 4; }

# --keep-payload 用复制换入（与 AOT copy 语义一致），否则 move_node 就位。
SWAP_FAILED=0
if [ "$KEEP_PAYLOAD" = 1 ]; then
    log "bundler-updater: copy in '$PAYLOAD_DIR' → '$INSTALL_DIR'"
    mkdir -p "$INSTALL_DIR" || exit 4
    cp -a "$PAYLOAD_DIR"/. "$INSTALL_DIR"/ || SWAP_FAILED=1
else
    log "bundler-updater: swap in '$PAYLOAD_DIR' → '$INSTALL_DIR'"
    move_node "$PAYLOAD_DIR" "$INSTALL_DIR" || SWAP_FAILED=1
fi
if [ "$SWAP_FAILED" = 1 ]; then
    log "bundler-updater: swap failed, restoring backup"
    rm -rf "$INSTALL_DIR"
    move_node "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
    rm -f "$MARKER"
    exit 4
fi
rm -f "$MARKER"

retain_or_remove_backup "$BACKUP_DIR" "" || \
    log "bundler-updater: WARN retain/remove backup failed; transient backup left at '$BACKUP_DIR'."

[ -n "$APP_PATH" ] && { log "bundler-updater: restart '$APP_PATH'"; restart_app; }
[ "$KEEP_PAYLOAD" = 1 ] || rm -rf "$PAYLOAD_DIR" 2>/dev/null || true
log "bundler-updater: done"
exit 0
