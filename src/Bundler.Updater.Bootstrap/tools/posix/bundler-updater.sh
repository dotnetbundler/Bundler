#!/bin/sh
# bundler-updater POSIX 降级实现：与 bundler-updater apply 同协议。
# 覆盖无 Native AOT 引导件的宿主（老 macOS/裸 POSIX 便携件场景）。
# 退出码：0 成功；2 用法错误；3 等待宿主退出超时；4 备份/换包失败（已尽力回滚）。

set -u

INSTALL_DIR=""
PAYLOAD_DIR=""
WAIT_PID=""
APP_PATH=""
BACKUP_DIR=""
LOG_FILE=""
KEEP_PAYLOAD=0
WAIT_TIMEOUT=120

usage() {
    echo "Usage:" >&2
    echo "  bundler-updater.sh apply --install-dir <dir> --payload <dir>" >&2
    echo "      [--wait-pid <pid>] [--app <path>] [--backup-dir <dir>]" >&2
    echo "      [--keep-payload] [--log <file>] [--wait-timeout <seconds>]" >&2
}

log() {
    if [ -n "$LOG_FILE" ]; then
        printf '%s\n' "$1" >>"$LOG_FILE"
    else
        printf '%s\n' "$1"
    fi
}

[ "${1:-}" = "apply" ] || { usage; exit 2; }
shift

while [ $# -gt 0 ]; do
    case "$1" in
        --keep-payload) KEEP_PAYLOAD=1; shift ;;
        --install-dir|--payload|--wait-pid|--app|--backup-dir|--log|--wait-timeout)
            [ $# -ge 2 ] || { echo "bundler-updater: option '$1' requires a value." >&2; exit 2; }
            case "$1" in
                --install-dir) INSTALL_DIR=$2 ;;
                --payload) PAYLOAD_DIR=$2 ;;
                --wait-pid) WAIT_PID=$2 ;;
                --app) APP_PATH=$2 ;;
                --backup-dir) BACKUP_DIR=$2 ;;
                --log) LOG_FILE=$2 ;;
                --wait-timeout) WAIT_TIMEOUT=$2 ;;
            esac
            shift 2 ;;
        *) echo "bundler-updater: unknown option '$1'." >&2; usage; exit 2 ;;
    esac
done

[ -n "$INSTALL_DIR" ] && [ -n "$PAYLOAD_DIR" ] || { echo "bundler-updater: --install-dir and --payload are required." >&2; exit 2; }
[ -d "$INSTALL_DIR" ] || { echo "bundler-updater: install directory '$INSTALL_DIR' does not exist." >&2; exit 2; }
[ -d "$PAYLOAD_DIR" ] || { echo "bundler-updater: payload directory '$PAYLOAD_DIR' does not exist." >&2; exit 2; }
[ -n "$BACKUP_DIR" ] || BACKUP_DIR="${INSTALL_DIR%/}.bundler-backup"

if [ -n "$WAIT_PID" ]; then
    log "bundler-updater: waiting for pid $WAIT_PID to exit"
    waited=0
    while kill -0 "$WAIT_PID" 2>/dev/null; do
        waited=$((waited + 1))
        [ "$waited" -le "$WAIT_TIMEOUT" ] || { echo "bundler-updater: the target process did not exit in time." >&2; exit 3; }
        sleep 1
    done
fi

# macOS .app 三项门禁：签名完好/身份连续/剥 quarantine——动备份前拒绝，安装目录零变更。
case "$PAYLOAD_DIR" in
    *.app) IS_APP=1 ;;
    *) if [ -f "$PAYLOAD_DIR/Contents/Info.plist" ]; then IS_APP=1; else IS_APP=0; fi ;;
esac
if [ "$IS_APP" = 1 ] && [ "$(uname -s)" = "Darwin" ]; then
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

# 崩溃恢复：marker 存在即上一轮死在备份与换包之间——安装目录可能是半成品，先还原。
MARKER="${INSTALL_DIR%/}.bundler-swap"
if [ -f "$MARKER" ]; then
    log "bundler-updater: interrupted swap detected, restoring backup first"
    [ -d "$BACKUP_DIR" ] || { echo "bundler-updater: swap marker exists but backup missing — cannot recover." >&2; exit 4; }
    rm -rf "$INSTALL_DIR"
    mv "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
    rm -f "$MARKER"
fi

log "bundler-updater: backup '$INSTALL_DIR' → '$BACKUP_DIR'"
rm -rf "$BACKUP_DIR" || exit 4
printf 'swap in progress' >"$MARKER" || exit 4
mv "$INSTALL_DIR" "$BACKUP_DIR" || { rm -f "$MARKER"; exit 4; }

log "bundler-updater: swap in '$PAYLOAD_DIR' → '$INSTALL_DIR'"
if ! mv "$PAYLOAD_DIR" "$INSTALL_DIR"; then
    log "bundler-updater: swap failed, restoring backup"
    rm -rf "$INSTALL_DIR"
    mv "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
    rm -f "$MARKER"
    exit 4
fi
rm -f "$MARKER"

if [ -n "$APP_PATH" ]; then
    log "bundler-updater: restart '$APP_PATH'"
    # macOS .app 目录件走 LaunchServices open，其余 nohup 分离。
    case "$APP_PATH" in
        *.app) if [ "$(uname -s)" = "Darwin" ]; then
            /usr/bin/open -n "$APP_PATH" || true
        else
            (cd "$INSTALL_DIR" && nohup "$APP_PATH" >/dev/null 2>&1 &)
        fi ;;
        *) (cd "$INSTALL_DIR" && nohup "$APP_PATH" >/dev/null 2>&1 &) ;;
    esac
fi
[ "$KEEP_PAYLOAD" = 1 ] || rm -rf "$PAYLOAD_DIR" 2>/dev/null || true
log "bundler-updater: done"
exit 0
