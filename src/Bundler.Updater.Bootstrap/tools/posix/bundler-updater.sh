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
norm_path() {
    _np="${1%/}"
    if [ -d "$_np" ]; then
        (cd "$_np" && pwd -P)
    else
        _nd=$(dirname "$_np")
        _nb=$(basename "$_np")
        _nr=$( (cd "$_nd" 2>/dev/null && pwd -P) || norm_lexical "$_nd")
        printf '%s/%s\n' "${_nr%/}" "$_nb"
    fi
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
INSTALL_DIR="$(norm_path "$INSTALL_DIR")"
[ -z "$PAYLOAD_DIR" ] || PAYLOAD_DIR="$(norm_path "$PAYLOAD_DIR")"
[ -z "$BACKUP_DIR" ] || BACKUP_DIR="$(norm_path "$BACKUP_DIR")"
[ -z "$RETAIN_DIR" ] || RETAIN_DIR="$(norm_path "$RETAIN_DIR")"
# 重启目标与日志同样按调用方 cwd 规范化成绝对路径——脚本的工作目录不是用户的 cwd。
[ -z "$APP_PATH" ] || APP_PATH="$(norm_path "$APP_PATH")"
[ -z "$LOG_FILE" ] || LOG_FILE="$(norm_path "$LOG_FILE")"
[ -n "$BACKUP_DIR" ] || BACKUP_DIR="${INSTALL_DIR%/}.bundler-backup"
MARKER="${INSTALL_DIR%/}.bundler-swap"

# 备份目录与安装/载荷同址或互嵌同样是抹数据的形状（换包前会 rm 旧备份）——与 AOT 侧同拒。
for _p in "$INSTALL_DIR" "$PAYLOAD_DIR"; do
    [ -n "$_p" ] || continue
    case "$BACKUP_DIR" in
        "$_p"|"$_p"/*) { echo "bundler-updater: backup/install/payload directories must not nest inside each other." >&2; exit 2; } ;;
    esac
    case "$_p" in
        "$BACKUP_DIR"/*) { echo "bundler-updater: backup/install/payload directories must not nest inside each other." >&2; exit 2; } ;;
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
    _r="$RETAIN_DIR"
    for _p in "$INSTALL_DIR" "$BACKUP_DIR" "$PAYLOAD_DIR"; do
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
        mv "$1" "$target" || return 4
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

# 崩溃恢复先于存在性检查：上轮死在备份与换包之间时安装目标可能缺失/半成品，先还原。
RECOVERED=0
if [ -f "$MARKER" ]; then
    log "bundler-updater: interrupted swap detected, restoring backup first"
    if [ -f "$BACKUP_DIR" ]; then
        rm -f "$INSTALL_DIR"
        mv "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
    elif [ -d "$BACKUP_DIR" ]; then
        rm -rf "$INSTALL_DIR"
        mv "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
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
    mv "$INSTALL_DIR" "$BACKUP_DIR" || { rm -f "$MARKER"; exit 4; }
    SWAP_FAILED=0
    if [ "$KEEP_PAYLOAD" = 1 ]; then
        log "bundler-updater: copy in '$PAYLOAD_DIR' → '$INSTALL_DIR'"
        cp -f "$PAYLOAD_DIR" "$INSTALL_DIR" || SWAP_FAILED=1
    else
        log "bundler-updater: swap in '$PAYLOAD_DIR' → '$INSTALL_DIR'"
        mv "$PAYLOAD_DIR" "$INSTALL_DIR" || SWAP_FAILED=1
    fi
    if [ "$SWAP_FAILED" = 1 ]; then
        log "bundler-updater: swap failed, restoring backup"
        rm -f "$INSTALL_DIR"
        mv "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
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
mv "$INSTALL_DIR" "$BACKUP_DIR" || { rm -f "$MARKER"; exit 4; }

# --keep-payload 用复制换入（与 AOT copy 语义一致），否则 mv 就位。
SWAP_FAILED=0
if [ "$KEEP_PAYLOAD" = 1 ]; then
    log "bundler-updater: copy in '$PAYLOAD_DIR' → '$INSTALL_DIR'"
    mkdir -p "$INSTALL_DIR" || exit 4
    cp -a "$PAYLOAD_DIR"/. "$INSTALL_DIR"/ || SWAP_FAILED=1
else
    log "bundler-updater: swap in '$PAYLOAD_DIR' → '$INSTALL_DIR'"
    mv "$PAYLOAD_DIR" "$INSTALL_DIR" || SWAP_FAILED=1
fi
if [ "$SWAP_FAILED" = 1 ]; then
    log "bundler-updater: swap failed, restoring backup"
    rm -rf "$INSTALL_DIR"
    mv "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
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
