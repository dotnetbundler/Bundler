#!/bin/sh
# bundler-updater POSIX 降级实现：与 bundler-updater apply 同协议。
# 覆盖无 Native AOT 引导件的宿主（老 macOS/裸 POSIX 便携件场景）。
# 安装目标是目录或单文件（AppImage 单件走文件级换包）。
# apply 退出码：0 成功；2 用法错误；3 等待宿主退出超时；4 备份/换包失败（已尽力回滚）。
# pkg-apply 退出码：0 成功；3 参数错；4 安装失败；5 包管理器缺席；6 重启失败（安装已提交）。

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
    echo "      [--lock-timeout <seconds>]" >&2
    echo "  bundler-updater.sh pkg-apply --pkg <file> --format <deb|rpm|apk> --app <path>" >&2
    echo "      [--sha256 <hex>] [--env-file <file>] [--rollback] [--log <file>]" >&2
}

log() {
    # 行内 \r\n 折空格：调用方串（--pkg/--app 原值）嵌行进日志会把一条
    # 记录拆成多行，日志件沦为伪造记录/单行注入面（AOT SanitizeLogLine 同口径）。
    _lm=$(printf '%s' "$1" | tr '\r\n' '  ')
    if [ -n "$LOG_FILE" ]; then
        # 日志件写入失败降级 stderr WARN（首败报一次）并继续——日志是留痕
        # 辅助，不可写不该阻断换包/装包主流程（AOT CreateLog 同口径）。
        # 首用懒开：_pa_log_open 校验+exec 9>> 钉 fd（pkg_apply 段已前置
        # 硬校验 rc3 拒；apply 腿懒开失败走 WARN 降级不写文件日志）。
        if [ -z "${_pa_log_done:-}" ]; then
            _pa_log_done=1
            _pa_log_open || _LOG_BAD=1
        fi
        if [ "${_LOG_BAD:-0}" = 1 ] || ! (printf '%s\n' "$_lm" >&9) 2>/dev/null; then
            [ -n "${_LOG_WARNED:-}" ] && return 0
            _LOG_WARNED=1
            echo "bundler-updater: WARN cannot write log file '$LOG_FILE' — continuing without file logging" >&2
        fi
    else
        printf '%s\n' "$_lm"
    fi
}

# fd 钉住复核：fd 上开定的是"与 check 时记录的 dev:ino 同件"普通件，
# 属主落允许 uid 集。/dev/fd|/proc/self/fd 直 stat 先行（Linux 恒达）；
# devfs 宿主（macOS 对自属 fd 的 /dev/fd/N stat 常 EBADF——CI 实证）
# 报节点自身 dev:ino 或直接失败的两种形态都不符→退 lsof 读内核
# vnode：REG/VREG 类型+inode 等值即同件（跨卷同 ino 不同件需跨卷
# 硬链——POSIX 不容）。lsof 缺席/取不到→拒（fail closed 同口径）。
# $1=fd 号；$2=check 时记录的 dev:ino；$3=允许 uid 列表（空格分隔，
# 空=不复核属主——属主复核抓 chown 竞态，inode 等值不挡属主变更）。
_pa_fd_same() {
    _fs_p=/dev/fd/$1
    [ -e "$_fs_p" ] || _fs_p=/proc/self/fd/$1
    _fs_i=$(stat -Lc '%d:%i' "$_fs_p" 2>/dev/null || stat -Lf '%d:%i' "$_fs_p" 2>/dev/null || true)
    if [ -n "$_fs_i" ] && [ "$_fs_i" = "$2" ] && [ -f "$_fs_p" ]; then
        [ -z "$3" ] && return 0
        _fs_o=$(stat -Lc %u "$_fs_p" 2>/dev/null || stat -Lf %u "$_fs_p" 2>/dev/null || true)
        for _fs_w in $3; do
            [ "$_fs_o" = "$_fs_w" ] && return 0
        done
        return 1
    fi
    command -v lsof >/dev/null 2>&1 || return 1
    # -n 免 DNS 反查：提权语境无解析器会让 vnode 名解析阻塞 lsof 本身。
    _fs_l=$(lsof -n -a -p "$$" -d "$1" -F tiun 2>/dev/null)
    _fs_t=$(printf '%s\n' "$_fs_l" | sed -n 's/^t//p')
    _fs_n=$(printf '%s\n' "$_fs_l" | sed -n 's/^i//p')
    { [ "$_fs_t" = REG ] || [ "$_fs_t" = VREG ]; } && \
        [ -n "$_fs_n" ] && [ "$_fs_n" = "${2##*:}" ] || return 1
    [ -z "$3" ] && return 0
    _fs_u=$(printf '%s\n' "$_fs_l" | sed -n 's/^u//p')
    case $_fs_u in ''|*[!0-9]*)
        # lsof u 字段给不出数字 uid 时按 vnode 名补 stat——名缺失
        #（已删件）即拒：属主拿不到=fail closed。
        _fs_nm=$(printf '%s\n' "$_fs_l" | sed -n 's/^n//p')
        [ -n "$_fs_nm" ] && \
            _fs_u=$(stat -c %u "$_fs_nm" 2>/dev/null || stat -f %u "$_fs_nm" 2>/dev/null || true)
        ;;
    esac
    [ -n "$_fs_u" ] || return 1
    for _fs_w in $3; do
        [ "$_fs_u" = "$_fs_w" ] && return 0
    done
    return 1
}

# --log 开件+校验（pkg_apply 前置硬调、log() 首用懒调）：提权侧 argv 给的
# 写路径按"调用者可写"复核——件在=普通件且属主==调用 uid；件不在=父目录
# 调用者可写（属主匹配或 o+wx 公共可写区）。exec 9>> 一次开定 O_APPEND
# 钉住 inode，其后在 fd 上（/dev/fd/9，devfs 宿主走 _pa_fd_same 的 lsof
# 兜底）复核属主——check→open 换件窗内出现的件属主两不沾（非调用 uid
# 且非 euid）即拒（AOT OpenLogFile 同口径）。
# uid 拿不到=无法复核属主→拒（fail closed，env 件同款口径）。
_pa_log_open() {
    [ -n "$LOG_FILE" ] || return 0
    # PA_UID 只在 pkg_apply 初始化——apply 腿走 id -u 兜底；set -u 下
    # 直引未置变量会让整个脚本带 unbound-variable 夭折（永远走不到
    # WARN 降级路）。
    _lu=${PA_UID:-}
    [ -n "$_lu" ] || _lu=$(id -u 2>/dev/null || true)
    [ -n "$_lu" ] || return 1
    _li=""
    # -e 对悬空链接为假——-h 一并判存在，否则 dangling link 走"件不在"
    # 分支只验父目录，open 会跟随链接以提权身份建出目标件。
    if [ -e "$LOG_FILE" ] || [ -h "$LOG_FILE" ]; then
        [ -f "$LOG_FILE" ] && [ ! -h "$LOG_FILE" ] || return 1
        _lo=$(stat -c %u "$LOG_FILE" 2>/dev/null || stat -f %u "$LOG_FILE" 2>/dev/null || true)
        [ -n "$_lo" ] && [ "$_lo" = "$_lu" ] || return 1
        # check→open 换件窗收口：记下 dev:ino，fd 复核比对同件——窗内
        # 把路径换成指向 root 件的链接会 mismatch 被拒（SEC 回归）。
        _li=$(stat -c '%d:%i' "$LOG_FILE" 2>/dev/null || stat -f '%d:%i' "$LOG_FILE" 2>/dev/null || true)
        [ -n "$_li" ] || return 1
        # POSIX special builtin（exec 与 : 同档）的重定向失败在 dash
        # 属致命错整脚本暴毙（|| 永远走不到），且 exec 上 2>/dev/null
        # 会把全脚本 stderr 永久钉死——预探测放子壳收容（子壳 fatal
        # 只杀子壳回非零、不带走主壳），探测过才 exec 钉 fd。
        # 残余窗登记：(a) 探测→exec 之间换入不可开目标（目录等）时裸
        #   exec 仍按 special builtin 致命暴毙（建件分支 stat→exec
        #   同形窗未单列登记）——毫秒级竞态、后果仅为脚本退出不越权；
        #   件被删不触发（>> 带 O_CREAT 会重建，随后 inode 比对按
        #   干净拒收——复用刚释放 inode 的理论窗内可比中放行，后果
        #   仍良性：euid 属空件作日志）；(b) check(-f)→探测 与
        #   探测→exec 两段窗内换入 fifo 时 >>/exec 无时限阻塞（建件
        #   分支 create→exec 同形；AOT 同位 OpenWithDeadline 5s 收口、
        #   sh 无等价原语——timeout 只能包外部命令，登记为两实现已知
        #   差异，旧实现同款暴露未恶化）；(c) 探测 >> 在换件窗内可建
        #   出 euid 属空件——随后被 inode 复核拒但件留存（良性残留，
        #   不越权）。
        ( : >>"$LOG_FILE" ) 2>/dev/null || return 1
        exec 9>>"$LOG_FILE" || { exec 9>&-; return 1; }
    else
        _ld=${LOG_FILE%/*}
        [ "$_ld" = "$LOG_FILE" ] && _ld=.
        # 根直下路径 /x 的 %/* 得空串——归一 / 再走目录判（AOT
        # GetDirectoryName 返 "/" 同位）。
        [ -z "$_ld" ] && _ld=/
        # 父目录不存在/非目录=良性不可写（rc2 语义）→WARN 降级续行；
        # 目录是符号链接=换件窗违规（rc1 语义）→硬拒（AOT
        # LogUnavailable/UsageException 两级同口径）。
        [ -d "$_ld" ] || return 2
        [ -h "$_ld" ] && return 1
        _lo=$(stat -c %u "$_ld" 2>/dev/null || stat -f %u "$_ld" 2>/dev/null || true)
        _lp=$(stat -c %a "$_ld" 2>/dev/null || stat -f %Lp "$_ld" 2>/dev/null || true)
        if [ -z "$_lo" ] || [ "$_lo" != "$_lu" ]; then
            # 公共可写区（/tmp sticky 类）：others 写+执行两位置位才放行
            case "$_lp" in *[37]) : ;; *) return 1 ;; esac
        fi
        # O_EXCL 原子建件：noclobber 让 > 对已存在路径（含符号链接——
        # O_CREAT|O_EXCL 不跟随，dangling 同拒）失败，check→open 换件
        # 窗在 open 一刻归零（AOT CREAT|NOFOLLOW 同口径）。子壳执行
        # 失败只回非零不致命；建成记 inode 让 fd 复核比对同件。
        (set -C; : >"$LOG_FILE") 2>/dev/null || return 1
        _li=$(stat -c '%d:%i' "$LOG_FILE" 2>/dev/null || stat -f '%d:%i' "$LOG_FILE" 2>/dev/null || true)
        if [ -z "$_li" ]; then
            rm -f "$LOG_FILE" 2>/dev/null || true
            return 1
        fi
        exec 9>>"$LOG_FILE" || { exec 9>&-; return 1; }
    fi
    # 三重复核（普通件+dev:ino 同件+属主落调用 uid 或本进程 euid）收进
    # _pa_fd_same——devfs 宿主的 fd 复核由该函数内 lsof 兜底。
    if ! _pa_fd_same 9 "$_li" "$_lu $(id -u 2>/dev/null)"; then
        exec 9>&-
        return 1
    fi
    return 0
}

restart_app() {
    # macOS .app 目录件走 LaunchServices open，其余 nohup 分离。
    case "$APP_PATH" in
        *.app) if [ "$(uname -s)" = "Darwin" ]; then
            /usr/bin/open -n "$APP_PATH" || true
        else
            _restart_nohup
        fi ;;
        *) _restart_nohup ;;
    esac
}

_restart_nohup() {
    # cwd 对齐 AOT：目录级=INSTALL_DIR 本身，文件级=INSTALL_DIR 的父目录。
    # stdin 一并钉 /dev/null——分离进程不得持有调用方管道任一端（AOT 侧同口径）。
    # 注：后台子壳里的 cd 失败会被 & 吞成"启动即成功"——与 pkg-apply 重启腿同形，
    # 但本腿是尽力而为语义（换包已提交、重启成败不进退出码），不设同步 cd 探测，
    # 与 pkg-apply 的 rc6 硬约束刻意不同（A1 登记）。
    # nohup 与 pkg-apply 重启腿同走 _pa_cmd 解析（BUNDLER_UPDATER_CMD_NOHUP 覆盖缝
    # 双侧同名）；缺席/不可执行即 WARN 跳过——重启是便利步骤不拦已提交的换包。
    _nohup=$(_pa_cmd nohup)
    [ -n "$_nohup" ] || { log "bundler-updater: nohup not found — cannot detach restart"; return 0; }
    if [ -d "$INSTALL_DIR" ]; then
        (cd "$INSTALL_DIR" && "$_nohup" "$APP_PATH" </dev/null >/dev/null 2>&1 &)
    else
        (cd "$(dirname "$INSTALL_DIR")" && "$_nohup" "$APP_PATH" </dev/null >/dev/null 2>&1 &)
    fi
}

# ---- pkg-apply 支路：deb/rpm/apk 本地系统包特权安装（与 AOT PkgApplyPlan 逐字同协议）。
# 客户端经 pkexec/sudo 以 root 拉起；动词只做：包管理器前端退化链装本地包 +
# 降权重启 --app。包管理器账本即状态——不引入 marker/锁/崩溃恢复；
# 包完整性不验签（sh 无 ECDSA），改走 --sha256 字节指纹钉定：客户端留存件
# 验签同趟算出的哈希随 argv 携来，root 侧对暂存副本复核同值才把包交给
# 包管理器——"客户端验的字节"与"root 装的字节"逐位绑定，验后替换窗归零。

# 命令解析：BUNDLER_UPDATER_CMD_<TOOL> env 覆盖先行——覆盖值由 root 直 exec，
# 必须"存在+普通文件+有执行位"才生效（不设防等于把任意路径提权为权威命令）；
# 不可用覆盖按未设置退化 command -v（AOT ResolveCommand 同序同口径）。
# command -v 结果额外要求绝对路径（/*）且 -x：内建/函数名命中 PATH 时不产绝对
# 路径、PATH 空元素或相对段会命中 cwd——两者都不得在 root 语境当工具使。
# env 不在本地解析面：spawn argv 里的内层 `env K=V` 由 nohup 子进程经 execvp
# 按自己的 PATH 解析，从不经本函数——原 env) 覆盖支路是死支路已删（A4）。
_pa_cmd() {
    case "$1" in
        apt-get) _pc=${BUNDLER_UPDATER_CMD_APT_GET:-} ;;
        dpkg) _pc=${BUNDLER_UPDATER_CMD_DPKG:-} ;;
        dnf) _pc=${BUNDLER_UPDATER_CMD_DNF:-} ;;
        zypper) _pc=${BUNDLER_UPDATER_CMD_ZYPPER:-} ;;
        rpm) _pc=${BUNDLER_UPDATER_CMD_RPM:-} ;;
        apk) _pc=${BUNDLER_UPDATER_CMD_APK:-} ;;
        setpriv) _pc=${BUNDLER_UPDATER_CMD_SETPRIV:-} ;;
        sudo) _pc=${BUNDLER_UPDATER_CMD_SUDO:-} ;;
        runuser) _pc=${BUNDLER_UPDATER_CMD_RUNUSER:-} ;;
        su) _pc=${BUNDLER_UPDATER_CMD_SU:-} ;;
        nohup) _pc=${BUNDLER_UPDATER_CMD_NOHUP:-} ;;
        *) _pc="" ;;
    esac
    if [ -n "$_pc" ]; then
        if [ -f "$_pc" ] && [ -x "$_pc" ]; then
            printf '%s\n' "$_pc"
            return 0
        fi
        _pc=""
    fi
    _pc=$(command -v "$1" 2>/dev/null) || _pc=""
    case "$_pc" in
        /*) [ -x "$_pc" ] && { printf '%s\n' "$_pc"; return 0; } ;;
    esac
    return 1
}

# 单 token POSIX 单引号转义：' → '\''（与 AOT ShQuote 同算法，su -c 串用）。
# 复核登记：$() 会剥掉被引值的尾部换行，与 AOT Replace 原样保留有别——
# 客户端写 env 件前已滤掉含 CR/LF 的值，该边缘现实不可达，仅登记不改。
_pa_shq() {
    printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"
}

# uid/gid 值校验+规范化：全数字且落在 int64 内才采，剥前导零回规范拼写——
# 与 AOT ParseUid（digits-only + long.TryParse 溢出返 null）同口径；
# 超界/非数字输入按"值缺席"处理，空输出=拒收。
# 串尾连 'x' 强制词法比对（同长十进制串词法序=数值序），不走 awk 数值双精度。
_pa_uid64() {
    # 值经 ENVIRON 入 awk（_mnt_of 同先例）——-v 会解 \ddd 转义把
    # "1\060" 吃掉成 10 误收（AOT 逐字符数字判同值拒收，口径分叉）。
    _PA_UID64_V=$1 awk 'BEGIN{
        u = ENVIRON["_PA_UID64_V"]
        if (u !~ /^[0-9]+$/) exit 1
        sub(/^0+/, "", u); if (u == "") u = "0"
        if (length(u) > 19 || (length(u) == 19 && (u "x") > "9223372036854775807x")) exit 1
        print u
    }'
}

# 安装腿有界：timeout 在场即包裹（默认 1800s，BUNDLER_UPDATER_PKG_TIMEOUT
# 正数秒覆盖——AOT ExecTimeoutMilliseconds 同名同缝，0/非数字双侧同归默认）；
# pm 挂死会把特权进程拖成永驻。-k 30 给 TERM 30s 善后窗再补 KILL——
# 忽略 TERM 的前端不能赖在界内（AOT Kill(entireProcessTree) 的 TERM-less
# 对应收敛方向一致）。timeout 缺席退裸跑（_pa_env_validate 的 cat 兜底同款
# 残余登记）。
_pa_timed() {
    if command -v timeout >/dev/null 2>&1; then
        timeout -k 30 "$_PA_TMO" "$@"
    else
        "$@"
    fi
}

# SIGKILL 残件兜底：EXIT trap 管不到强杀路径——清扫 24h 前的 bundler-pkg
# 暂存目（sh 建 bundler-pkg.XXXX / AOT 建 bundler-pkg-<guid>——[.-] 双形
# 都收，跨实现残件互不漏；AOT SweepStaleStageDirs 同义）。glob 枚举候选
# + find -prune -mmin 判龄（-maxdepth 是 GNU 专旗，BSD/macOS find 不收）。
# find/rm 缺席的宿主不扫（残余由 OS tmp 清理器兜）。
_pa_sweep_stages() {
    command -v find >/dev/null 2>&1 || return 0
    command -v rm >/dev/null 2>&1 || return 0
    for _d in "${TMPDIR:-/tmp}"/bundler-pkg.[!.]* "${TMPDIR:-/tmp}"/bundler-pkg-*; do
        [ -d "$_d" ] || continue
        [ -n "$(find "$_d" -prune -mmin +1440 -print 2>/dev/null)" ] && \
            rm -rf "$_d" 2>/dev/null || true
    done
}

# sha256 工具链退化：sha256sum → shasum -a 256 → openssl dgst -sha256（全缺席返 1）。
_pa_sha256() {
    if command -v sha256sum >/dev/null 2>&1; then
        _out=$(sha256sum "$1" 2>/dev/null) || return 1
        printf '%s\n' "${_out%% *}"
    elif command -v shasum >/dev/null 2>&1; then
        _out=$(shasum -a 256 "$1" 2>/dev/null) || return 1
        printf '%s\n' "${_out%% *}"
    elif command -v openssl >/dev/null 2>&1; then
        _out=$(openssl dgst -sha256 "$1" 2>/dev/null) || return 1
        printf '%s\n' "${_out##* }"
    else
        return 1
    fi
}

# 暂存根清理：EXIT trap 从建立处起全路径兜底（AOT CleanupStage 同义）。
_pa_stage_cleanup() {
    [ -n "$PA_STAGE" ] && rm -rf "$PA_STAGE" 2>/dev/null
    PA_STAGE=""
}

# 前端退化链：逐级 command -v 预检，安装失败逐级退化。_PA_PM_SEEN 记"有前端在场"——
# 区分退出码 5（全缺席）与 4（在场全败）。
_pa_install() {
    _PA_PM_SEEN=0
    case "$PA_FMT" in
        deb)
            # 两级恒带 DEBIAN_FRONTEND=noninteractive 与 confdef/confold——
            # apt-get 不认裸 dpkg 旗标，经 -o Dpkg::Options 透传。
            if _c=$(_pa_cmd apt-get) && [ -n "$_c" ]; then
                _PA_PM_SEEN=1
                log "bundler-updater: install '$PA_PKG' via apt-get"
                DEBIAN_FRONTEND=noninteractive _pa_timed "$_c" install -y \
                    -o "Dpkg::Options::=--force-confdef" \
                    -o "Dpkg::Options::=--force-confold" \
                    -o "DPkg::Lock::Timeout=120" "$PA_PKG"
                _rc=$?
                [ "$_rc" -eq 0 ] && return 0
                log "bundler-updater: apt-get failed (exit $_rc) — falling back"
            else
                log "bundler-updater: apt-get not found — skipping"
            fi
            if _c=$(_pa_cmd dpkg) && [ -n "$_c" ]; then
                _PA_PM_SEEN=1
                log "bundler-updater: install '$PA_PKG' via dpkg"
                DEBIAN_FRONTEND=noninteractive _pa_timed "$_c" -i \
                    --force-confdef --force-confold "$PA_PKG"
                _rc=$?
                [ "$_rc" -eq 0 ] && return 0
                log "bundler-updater: dpkg failed (exit $_rc) — falling back"
            else
                log "bundler-updater: dpkg not found — skipping"
            fi
            ;;
        rpm)
            if _c=$(_pa_cmd dnf) && [ -n "$_c" ]; then
                _PA_PM_SEEN=1
                log "bundler-updater: install '$PA_PKG' via dnf"
                _pa_timed "$_c" install -y "$PA_PKG"
                _rc=$?
                [ "$_rc" -eq 0 ] && return 0
                log "bundler-updater: dnf failed (exit $_rc) — falling back"
            else
                log "bundler-updater: dnf not found — skipping"
            fi
            if _c=$(_pa_cmd zypper) && [ -n "$_c" ]; then
                _PA_PM_SEEN=1
                log "bundler-updater: install '$PA_PKG' via zypper"
                _pa_timed "$_c" install -y "$PA_PKG"
                _rc=$?
                [ "$_rc" -eq 0 ] && return 0
                log "bundler-updater: zypper failed (exit $_rc) — falling back"
            else
                log "bundler-updater: zypper not found — skipping"
            fi
            if _c=$(_pa_cmd rpm) && [ -n "$_c" ]; then
                _PA_PM_SEEN=1
                log "bundler-updater: install '$PA_PKG' via rpm"
                # --rollback 子形态：rpm 退化链末位换 --oldpackage。
                if [ "$PA_ROLLBACK" = 1 ]; then
                    _pa_timed "$_c" -U --oldpackage "$PA_PKG"
                else
                    _pa_timed "$_c" -U "$PA_PKG"
                fi
                _rc=$?
                [ "$_rc" -eq 0 ] && return 0
                log "bundler-updater: rpm failed (exit $_rc) — falling back"
            else
                log "bundler-updater: rpm not found — skipping"
            fi
            ;;
        apk)
            if _c=$(_pa_cmd apk) && [ -n "$_c" ]; then
                _PA_PM_SEEN=1
                # /etc/apk/keys 有预置公钥才免 --allow-untrusted；缺席/空一律加。
                # ls -A 连点文件也算条目——与 AOT EnumerateFileSystemEntries 同口径
                # （裸 ls 漏掉点文件会把"只有隐藏件"的 keys 目录误判空）。
                # BUNDLER_UPDATER_APK_KEYS_DIR 双侧同名测试缝。
                _keys=${BUNDLER_UPDATER_APK_KEYS_DIR:-/etc/apk/keys}
                log "bundler-updater: install '$PA_PKG' via apk"
                if [ -d "$_keys" ] && [ -n "$(ls -A "$_keys" 2>/dev/null)" ]; then
                    _pa_timed "$_c" add "$PA_PKG"
                else
                    _pa_timed "$_c" add --allow-untrusted "$PA_PKG"
                fi
                _rc=$?
                [ "$_rc" -eq 0 ] && return 0
                log "bundler-updater: apk failed (exit $_rc) — falling back"
            else
                log "bundler-updater: apk not found — skipping"
            fi
            ;;
    esac
    return 1
}

# --env-file 是客户端 <staging>/pkg-<guid>/session.env 一次性输入件：消费完
#（或装失败确定不消费）即删件与 pkg-* 暂存目录——客户端拉起即退后删不了，
# 残件兜底在客户端拉起腿退出删余（Exited 钩/速退内联）与 24h staging 清扫。rm -f/rmdir 全尽力不挡退出码；
# 父目录仅本编排契约的 pkg-* 名才动，外来目录不碰。
_pa_env_cleanup() {
    # 只在读定（校验全过）后清件——校验被拒的路径不归我们删：任意的
    # --env-file 指向（/etc/shadow 类）在拒绝后仍被兜底删件就是"root
    # 删调用方任意件"原语（AOT envFileRead 闸同口径）。
    [ -n "$PA_ENV" ] && [ "${_PA_ENV_OK:-0}" = 1 ] || return 0
    rm -f "$PA_ENV" 2>/dev/null || true
    _d=${PA_ENV%/*}
    [ -n "$_d" ] || _d=/
    case "${_d##*/}" in
        # rmdir 对符号链接报 ENOTDIR 会留下链接本身——AOT Directory.Delete
        # 对链接叶删的是链接节点（不触目标）：rmdir 失败后补 rm -f 对齐同口径
        #（真目录非空同样留弃——非递归契约双侧一致）。
        pkg-*) rmdir "$_d" 2>/dev/null || { [ -h "$_d" ] && rm -f "$_d" 2>/dev/null; } || true ;;
    esac
    return 0
}

# --env-file 预检+预读（TOCTOU 收口，rc3 拒绝口径）：显式给了就必须是"调用者
# 所有的可读普通件"——任一过不了一律拒（调用方判参错），绝不静默降级成
# "件未提供"。open 先钉住 inode：exec 3<> 走 O_RDWR——fifo 以读写开立即
# 返回不阻塞（裸 O_RDONLY 开会挂死等写端，原 timeout 5 cat 兜底窗一并
# 消掉），普通件在 root 身份下不受权限位约束能开；其后类型/属主/读取
# 全走 fd（/dev/fd/3→/proc/self/fd/3→lsof 三级，见 _pa_fd_same）不触路径——旧 -f/-h/-r/stat/cat
# 逐段查与真实 open 之间窗内 fifo 可挂死 root 进程、symlink 可把 root
# 私有件灌进降权 app 的 env（用户经 /proc/<app>/environ 读回）。
# 换件窗由 dev:ino 比对钉死：预探测时 stat 记下路径件的 dev:ino，
# exec 3<> 后 fd 上 stat -L 复核同值——stat→open 之间换件（同主/
# 异主/链接）即 mismatch 被拒；-h 预查只为对齐 AOT 链接拒判面
#（NOFOLLOW→ELOOP 语义）。
# 残余窗登记：-h check→stat 之间换同主普通件（含同 inode 硬链接
# 换位）会静默通过——良性，两实现同款暴露；换链接不通过（stat
# lstat 记链接自身 ino，fd 复核记目标 ino——必 mismatch 拒）；
# 探测→exec 之间换入目录等不可开目标时裸 exec 按 special builtin
# 致命退（dash rc2/bash rc1，不带 usage 文案）——毫秒级竞态不越权；
# 件被删不触发 fatal（<> 带 O_CREAT 会重建空件→inode mismatch
# 干净拒）；探测 <> 对窗内换入的 dangling 链接会在链接目标处建
# 出 euid 属空件（提权语境即任意路径空件残留——拒仍成立、副作用
# 不可安全回收：按链接目标 rm 会删错件，登记不处置）；件被删
# 重建若复用刚释放 inode 可比中放行——euid==调用 uid 时读空件
# 退化最小 env（良性），euid≠uid 时被属主复核拒；<> O_RDWR 对
# fifo 不阻塞，无 log 侧同形阻塞窗。
# 读定内容进 _PA_ENV_RAW 并置 _PA_ENV_OK=1——其后不再触路径，清件闸只认
# 读定态（校验被拒的路径不归我们删，见 _pa_env_cleanup）。
_pa_env_validate() {
    _PA_ENV_RAW=""
    _PA_ENV_OK=0
    if [ -h "$PA_ENV" ]; then
        echo "bundler-updater: --env-file '$PA_ENV' is not a readable regular file owned by the calling user." >&2
        pkg_usage
        return 1
    fi
    # check→open 换件窗收口：记 dev:ino，fd 复核比对同件——窗内换成
    # 指向异主件的链接会 mismatch 被拒（-h 预查对齐链接拒判面，真正
    # 钉住靠 inode 比对）。: 与 exec 同属 POSIX special builtin——
    # 重定向失败在 dash 属致命错且 exec 上 2>/dev/null 会永久静默
    # 全脚本 stderr，预探测放子壳收容（<> 读写模式开 fifo 不阻塞），
    # 探测过才 exec 钉 fd。
    _ei=$(stat -c '%d:%i' "$PA_ENV" 2>/dev/null || stat -f '%d:%i' "$PA_ENV" 2>/dev/null || true)
    if [ -z "$_ei" ]; then
        echo "bundler-updater: --env-file '$PA_ENV' is not a readable regular file owned by the calling user." >&2
        pkg_usage
        return 1
    fi
    ( : <>"$PA_ENV" ) 2>/dev/null || {
        echo "bundler-updater: --env-file '$PA_ENV' is not a readable regular file owned by the calling user." >&2
        pkg_usage
        return 1
    }
    exec 3<>"$PA_ENV" || {
        echo "bundler-updater: --env-file '$PA_ENV' is not a readable regular file owned by the calling user." >&2
        pkg_usage
        return 1
    }
    _ev_uid=${PA_UID:-}
    [ -n "$_ev_uid" ] || _ev_uid=$(id -u 2>/dev/null || true)
    if [ -z "$_ev_uid" ] || ! _pa_fd_same 3 "$_ei" "$_ev_uid"; then
        exec 3>&-
        echo "bundler-updater: --env-file '$PA_ENV' is not a readable regular file owned by the calling user." >&2
        pkg_usage
        return 1
    fi
    _PA_ENV_RAW=$(cat <&3)
    _ev_rc=$?
    exec 3>&-
    if [ "$_ev_rc" -ne 0 ]; then
        echo "bundler-updater: --env-file '$PA_ENV' is not a readable regular file owned by the calling user." >&2
        pkg_usage
        return 1
    fi
    _PA_ENV_OK=1
    return 0
}

# 降权重启：uid 由 PA_UID 携来——解析器缺席（awk 不在场）与值缺席必须分流：
# 前者不能按"本来就 root 直跑"误降成 root spawn，宁可拒重启（rc6，§2.3 不以
# root 重启的口径优先）；值缺席=本来就 root 直跑，按当前权限直接 spawn；
# 在场按 setpriv → sudo → runuser → su 退化链降权拉起 --app。
# env 一律经内层 `env K=V … app` 构造（sudo env_reset 把外层环境剥光），
# 外层 nohup 分离——引导件退出/终端挂断不该带走新装应用（apply 腿同策略）。
_pa_restart() {
    [ "$PA_UID_FAIL" = 0 ] || \
        { echo "bundler-updater: cannot resolve caller uid (awk unavailable) — refusing restart." >&2; return 1; }
    # 重启目标须是可执行普通文件——目录件 -e/-x 双过会让 nohup exec 目录、
    # 输出被 >/dev/null 吞成 rc0 假成功（AOT File.Exists 同拒 → rc6）。
    [ -f "$PA_APP" ] && [ -x "$PA_APP" ] || \
        { echo "bundler-updater: restart target '$PA_APP' is missing or not executable." >&2; return 1; }
    _d=$(dirname "$PA_APP")
    # 会话 env 收进函数位置参数（$@=K=V 序列）：env 件内容已在本动词装包前
    # 完成校验+读定（_PA_ENV_RAW，见 _pa_env_validate）——此步只做行过滤不再
    # 触件。\r 统一折成 \n，与 AOT ReadLines 的 \r\n/\r/\n 三终结符同口径；
    # 解析出零有效对按"件不可用"退化最小集——空 $@ 会让内层 env 零覆盖
    # 继承调用方全套（pkexec 语境=root env）原样灌给降权 app。
    set --
    if [ -n "$PA_ENV" ]; then
        _eifs=$IFS
        IFS='
'
        set -f
        for _l in $(printf '%s\n' "$_PA_ENV_RAW" | tr '\r' '\n'); do
            case "$_l" in
                [A-Za-z_]*=*)
                    _k=${_l%%=*}
                    case "$_k" in
                        *[!A-Za-z0-9_]*|"") ;;
                        *) set -- "$@" "$_l" ;;
                    esac ;;
            esac
        done
        set +f
        IFS=$_eifs
        [ $# -gt 0 ] || log "bundler-updater: WARN env file '$PA_ENV' has no usable assignments — minimal session env"
    else
        log "bundler-updater: no --env-file — restart uses minimal session env"
    fi
    if [ $# -eq 0 ]; then
        # 降权场景 HOME 必须是目标用户的家目录（root 的 $HOME 不可回灌）；
        # 直跑场景才按本进程 HOME。
        if [ -n "$PA_UID" ]; then _h=$PA_HOME; else _h=${HOME:-}; fi
        [ -n "$_h" ] && set -- "$@" "HOME=$_h"
        [ -n "${DISPLAY:-}" ] && set -- "$@" "DISPLAY=$DISPLAY"
    fi
    # nohup 解析排在 env 解析之后——与 AOT LoadEnvPairs→ResolveCommand 同序，
    # 使 WARN 序双侧一致。
    _nohup=$(_pa_cmd nohup)
    [ -n "$_nohup" ] || { log "bundler-updater: nohup not found — cannot detach restart"; return 1; }
    # 分离腿的 stdin 一并钉 /dev/null——子进程不得持有调用方管道读端，
    # 与 >/dev/null 2>&1 合起来三向全断（AOT StartDetached 同口径）。
    # 另注：各腿 spawn 前先同步 cd 探测——后台子壳 `(cd … && … &)` 里的 cd
    # 失败会被 & 吞成"启动即成功"假成功；同步探测失败即如实失败/降下一级，
    # 与 AOT Process.Start 按 WorkingDirectory 同步拒同口径。
    # 残余：探测→spawn 间 µs 级 TOCTOU 窗仍在（两 cd 非同次）——完全闭合须
    # `(cd || exit; spawn&)` 子壳内自判，收益不抵结构扰动，登记边界。
    if [ -z "$PA_UID" ]; then
        log "bundler-updater: restart '$PA_APP'"
        (cd "$_d") 2>/dev/null || return 1
        (cd "$_d" && "$_nohup" env "$@" "$PA_APP" </dev/null >/dev/null 2>&1 &) && return 0
        return 1
    fi
    log "bundler-updater: restart '$PA_APP' as uid $PA_UID"
    # 降权器"在场"≠"能用"——每级先同步预检最小 argv 再分离 spawn：分离只证
    # nohup 分叉成，证不了降权器收下本链 argv 形（busybox setpriv 等在 PATH
    # 里却只认 capabilities 子集，无预检时 spawn"成功"返真、子进程即刻参数
    # 错死在分离层，重启被静默吃掉）。预检三向钉死不透调用方 stdio。
    if _c=$(_pa_cmd setpriv) && [ -n "$_c" ]; then
        "$_c" --reuid="$PA_UID" --regid="$PA_GID" --clear-groups \
            /bin/sh -c : </dev/null >/dev/null 2>&1
        _rc=$?
        if [ "$_rc" -eq 0 ]; then
            if (cd "$_d") 2>/dev/null; then
                (cd "$_d" && "$_nohup" "$_c" --reuid="$PA_UID" --regid="$PA_GID" \
                    --clear-groups env "$@" "$PA_APP" </dev/null >/dev/null 2>&1 &) && return 0
            fi
            log "bundler-updater: WARN spawn via '$_c' failed — trying next"
        else
            log "bundler-updater: WARN drop tool '${_c##*/}' rejected probe (exit $_rc) — trying next"
        fi
    fi
    # sudo 预检加 -n 免密闸——要口令的 sudo 在真实 spawn 里同样败于 EOF，
    # -n 只是把同一结局提前确定（AOT 支路同口径）。
    if _c=$(_pa_cmd sudo) && [ -n "$_c" ]; then
        "$_c" -n -u "#$PA_UID" /bin/sh -c : </dev/null >/dev/null 2>&1
        _rc=$?
        if [ "$_rc" -eq 0 ]; then
            if (cd "$_d") 2>/dev/null; then
                (cd "$_d" && "$_nohup" "$_c" -u "#$PA_UID" env "$@" "$PA_APP" </dev/null >/dev/null 2>&1 &) && return 0
            fi
            log "bundler-updater: WARN spawn via '$_c' failed — trying next"
        else
            log "bundler-updater: WARN drop tool '${_c##*/}' rejected probe (exit $_rc) — trying next"
        fi
    fi
    if [ -n "$PA_NAME" ]; then
        if _c=$(_pa_cmd runuser) && [ -n "$_c" ]; then
            "$_c" -u "$PA_NAME" -- /bin/sh -c : </dev/null >/dev/null 2>&1
            _rc=$?
            if [ "$_rc" -eq 0 ]; then
                if (cd "$_d") 2>/dev/null; then
                    (cd "$_d" && "$_nohup" "$_c" -u "$PA_NAME" -- env "$@" "$PA_APP" </dev/null >/dev/null 2>&1 &) && return 0
                fi
                log "bundler-updater: WARN spawn via '$_c' failed — trying next"
            else
                log "bundler-updater: WARN drop tool '${_c##*/}' rejected probe (exit $_rc) — trying next"
            fi
        fi
        if _c=$(_pa_cmd su) && [ -n "$_c" ]; then
            "$_c" -s /bin/sh -c : "$PA_NAME" </dev/null >/dev/null 2>&1
            _rc=$?
            if [ "$_rc" -eq 0 ]; then
                _sc="exec env"
                for _kv do _sc="$_sc $(_pa_shq "$_kv")"; done
                _sc="$_sc $(_pa_shq "$PA_APP")"
                if (cd "$_d") 2>/dev/null; then
                    (cd "$_d" && "$_nohup" "$_c" -s /bin/sh -c "$_sc" "$PA_NAME" </dev/null >/dev/null 2>&1 &) && return 0
                fi
                log "bundler-updater: WARN spawn via '$_c' failed — trying next"
            else
                log "bundler-updater: WARN drop tool '${_c##*/}' rejected probe (exit $_rc) — trying next"
            fi
        fi
    fi
    log "bundler-updater: cannot drop privileges to uid $PA_UID — no setpriv/sudo/runuser/su available"
    return 1
}

# 词法归一（不触文件系统）：折 ./.. 段、相对路径按 $PWD 绝对化——
# 与 AOT Path.GetFullPath 同口径。定义先于 pkg_apply/apply 两腿共用；
# 输出路径的叶段必须保留拼写（叶链由各自校验判拒，不能物理化）。
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

pkg_apply() {
    PA_PKG=""; PA_FMT=""; PA_APP=""; PA_ENV=""; PA_ROLLBACK=0; PA_SHA256=""; PA_STAGE=""
    # 安装腿超时界（AOT ExecTimeoutMilliseconds 同名同缝）：非数字/零/缺席按默认
    # 1800s——"0" 必须归默认：GNU `timeout 0` 是无界义（禁用超时），不排零会让
    # 同名 env 缝双侧语义分叉（AOT `seconds > 0` 判 0 非法回默认）。
    case "${BUNDLER_UPDATER_PKG_TIMEOUT:-}" in
        ''|*[!0-9]*) _PA_TMO=1800 ;;
        *) if [ "$BUNDLER_UPDATER_PKG_TIMEOUT" -gt 0 ] 2>/dev/null
           then _PA_TMO=$BUNDLER_UPDATER_PKG_TIMEOUT
           else _PA_TMO=1800; fi ;;
    esac
    _pa_sweep_stages
    while [ $# -gt 0 ]; do
        case "$1" in
            --rollback) PA_ROLLBACK=1; shift ;;
            --pkg|--sha256|--format|--app|--env-file|--log)
                [ $# -ge 2 ] || { echo "bundler-updater: option '$1' requires a value." >&2; pkg_usage; exit 3; }
                case "$1" in
                    --pkg) PA_PKG=$2 ;;
                    --sha256) PA_SHA256=$2 ;;
                    --format) PA_FMT=$2 ;;
                    --app) PA_APP=$2 ;;
                    --env-file) PA_ENV=$2 ;;
                    --log) LOG_FILE=$2 ;;
                esac
                shift 2 ;;
            *) echo "bundler-updater: unknown option '$1'." >&2; pkg_usage; exit 3 ;;
        esac
    done
    [ -n "$PA_PKG" ] || { echo "bundler-updater: --pkg is required." >&2; pkg_usage; exit 3; }
    [ -n "$PA_APP" ] || { echo "bundler-updater: --app is required." >&2; pkg_usage; exit 3; }
    # --sha256 词法：恰 64 位十六进制（AOT IsSha256Hex 同口径）——畸形按用法错 rc3。
    case "$PA_SHA256" in
        "") ;;
        *[!0-9a-fA-F]*)
            echo "bundler-updater: --sha256 must be exactly 64 hexadecimal characters." >&2; pkg_usage; exit 3 ;;
        *)
            [ ${#PA_SHA256} -eq 64 ] || \
                { echo "bundler-updater: --sha256 must be exactly 64 hexadecimal characters." >&2; pkg_usage; exit 3; } ;;
    esac
    case "$PA_FMT" in
        deb|rpm|apk) ;;
        *) echo "bundler-updater: --format must be deb, rpm or apk." >&2; pkg_usage; exit 3 ;;
    esac
    # 复核登记（N10）：-f 跟随符号链接——与 AOT File.Exists 同口径；包路径属
    # 调用方受托输入（root 本就受托装这包），刻意不做 --env-file 那套三重校验：
    # env 件内容会越信任边界灌进降权 app 才须硬化，包件本身不越界。
    [ -f "$PA_PKG" ] || { echo "bundler-updater: package '$PA_PKG' does not exist." >&2; pkg_usage; exit 3; }
    case "$PA_PKG" in /*) ;; *) PA_PKG="$PWD/$PA_PKG" ;; esac
    case "$PA_APP" in /*) ;; *) PA_APP="$PWD/$PA_APP" ;; esac
    case "$PA_ENV" in "") ;; /*) ;; *) PA_ENV="$PWD/$PA_ENV" ;; esac
    # --log 与 apply 腿同口径词法归一（AOT GetFullPath 同义）：折 ./.. 段，
    # 叶拼写必须留给 _pa_log_open 的 -h 链接拒判，绝不能走物理化。
    [ -z "$LOG_FILE" ] || LOG_FILE="$(norm_lexical "$LOG_FILE")" || exit 3

    # 原用户 uid：PKEXEC_UID→SUDO_UID——都不在=本来就 root 直跑，按当前权限 spawn。
    # _pa_uid64 统一"全数字且 int64 内"口径并剥前导零规范化——超界垃圾值按
    # 值缺席走兜底（AOT long.TryParse 溢出返 null 同义）；旧侧只查全数字，
    # 超界 uid 会直灌 setpriv 吃沉默假成功。
    # 解析器缺席（awk 不在场）与 uid 值缺席必须分流：awk 不在 uid/passwd 全
    # 解析不出，按值缺席放行会把降权重启静默做成 root spawn——PA_UID_FAIL=1
    # 记状态交重启腿拒重启（rc6，§2.3 不以 root 重启的口径优先）。
    PA_UID=""; PA_GID=""; PA_NAME=""; PA_HOME=""; PA_UID_FAIL=0; _PA_ENV_RAW=""
    # 复核登记（N9）：awk/stat/id/timeout/tr/dirname 等辅助件走裸 command -v，
    # 刻意不进 BUNDLER_UPDATER_CMD_<TOOL> 覆盖面——覆盖缝只给测试垫片要 stub
    # 的包前端/降权器/nohup；PATH 在特权语境本属调用方受托面，辅助件与
    # nohup 同级，扩大覆盖面只增攻击面无收益。
    if command -v awk >/dev/null 2>&1; then
        PA_UID=$(_pa_uid64 "${PKEXEC_UID:-}")
        [ -n "$PA_UID" ] || PA_UID=$(_pa_uid64 "${SUDO_UID:-}")
    elif [ -n "${PKEXEC_UID:-}${SUDO_UID:-}" ]; then
        # 解析器缺席且确有 uid 变量待解——不能按"值缺席"误放成 root spawn；
        # 变量本来就全空（uid 真缺席）仍按直跑腿放行，与 AOT 侧对偶。
        PA_UID_FAIL=1
    fi
    if [ -n "$PA_UID" ]; then
        # passwd 查名/gid/家目录：runuser/su 只收用户名；gid 兜底链 SUDO_GID→passwd→uid。
        # BUNDLER_UPDATER_PASSWD_FILE 双侧同名测试缝。
        # 复核登记（N13）：NF>=7 与 AOT fields.Length>=7 同口径；但 $3==u 走
        # strnum 数值比——uid>2^53 时 awk 双精度可对相近大数别名误中，AOT 是
        # int64 精确等值。真实 passwd uid 远不及此量级，且查名只喂
        # runuser/su 退腿（setpriv 直接用 uid 不吃名），误中代价低、登记不改。
        _pw=$(awk -F: -v u="$PA_UID" 'NF>=7 && $3==u {print $1":"$4":"$6; exit}' \
            "${BUNDLER_UPDATER_PASSWD_FILE:-/etc/passwd}" 2>/dev/null || true)
        if [ -n "$_pw" ]; then
            PA_NAME=${_pw%%:*}
            _rest=${_pw#*:}
            PA_GID=${_rest%%:*}
            PA_HOME=${_rest#*:}
        fi
        _g=$(_pa_uid64 "${SUDO_GID:-}")
        [ -n "$_g" ] && PA_GID=$_g
        # gid 非数字/超界（passwd 坏段等）按缺席回退 uid——
        # 与 AOT `passwd?.Gid ?? uid`（TryParse 失败→null→uid）同口径。
        PA_GID=$(_pa_uid64 "$PA_GID")
        [ -n "$PA_GID" ] || PA_GID=$PA_UID
    fi

    # --env-file 显式给了先校验+读定（fd 钉住三重校验 rc3 拒绝，见
    # _pa_env_validate）——先于装包，与 AOT Run 前置 ReadEnvFile 同序。
    # uid 解析失败（PA_UID_FAIL=1）时跳过校验——重启腿反正会拒（rc6），
    # 内容永不进 env；件未被读定故 _pa_env_cleanup 清件闸不放行（拒绝的
    # 路径不归我们删——校验失败仍删件即"root 删任意件"原语）。
    if [ -n "$PA_ENV" ] && [ "$PA_UID_FAIL" = 0 ]; then
        _pa_env_validate || exit 3
    fi

    # --log 与 env 件同域：提权侧 argv 给的写路径前置复核（件在=普通件+
    # 属主==调用 uid；件不在=父目录调用者可写）——违规（异主件/链接/父目录
    # 不可写）rc3 拒；良性不可写（父目录不存在）WARN 降级续行不写未验件
    # （AOT TryOpenLogFile 两级同口径）。_pa_log_done 预置让 log() 不再懒开。
    if [ -n "$LOG_FILE" ]; then
        _pa_log_done=1
        _pa_log_open
        _lr=$?
        if [ "$_lr" = 2 ]; then
            _LOG_BAD=1
        elif [ "$_lr" != 0 ]; then
            echo "bundler-updater: --log '$LOG_FILE' is not a writable file owned by the calling user." >&2
            pkg_usage
            _pa_env_cleanup
            exit 3
        fi
    fi

    # 客户端验签→root 读件的 TOCTOU 收口：包先复制进 root 私有临时目录，
    # --sha256 给了就对暂存副本复核——客户端留存件验签同趟算出的哈希随 argv
    # 携来，与 root 实际要装的字节逐位绑定，验后替换窗归零；装期间原路径被
    # 改写也不影响本轮回放（装的是快照非引用）。核验/暂存失败按安装失败 rc4。
    PA_STAGE=$(mktemp -d "${TMPDIR:-/tmp}/bundler-pkg.XXXXXX" 2>/dev/null) || PA_STAGE=""
    if [ -z "$PA_STAGE" ]; then
        # mktemp 缺席（裸 busybox/最小镜像）退化 umask-077 建目——root 私有语义保持。
        PA_STAGE="${TMPDIR:-/tmp}/bundler-pkg.$$"
        (umask 077; mkdir "$PA_STAGE" 2>/dev/null) || \
            { echo "bundler-updater: cannot stage package for verification." >&2; _pa_env_cleanup; exit 4; }
    fi
    trap '_pa_stage_cleanup' EXIT
    # 判存→拷贝的类型替换窗：包件在用户可写目录，-f 判存后被换成 fifo/设备会
    # 让 root 的 cp 挂死 open 或无限量读——cp 走 timeout 60s 有界包裹收口
    #（AOT OpenWithDeadline+fstat 同向收敛），拷贝后复核暂存件仍是普通文件。
    if command -v timeout >/dev/null 2>&1; then
        timeout -k 10 60 cp "$PA_PKG" "$PA_STAGE/" >/dev/null 2>&1
    else
        cp "$PA_PKG" "$PA_STAGE/" >/dev/null 2>&1
    fi
    [ $? -eq 0 ] && [ -f "$PA_STAGE/${PA_PKG##*/}" ] || \
        { echo "bundler-updater: cannot stage package for verification." >&2; _pa_env_cleanup; exit 4; }
    PA_PKG="$PA_STAGE/${PA_PKG##*/}"
    if [ -n "$PA_SHA256" ]; then
        _h=$(_pa_sha256 "$PA_PKG" || true)
        if [ -z "$_h" ]; then
            echo "bundler-updater: no sha256 tool available — refusing to verify package." >&2
            _pa_env_cleanup
            exit 4
        fi
        # 客户端恒发小写——比对前双侧归一防大小写假阴性。
        [ "$(printf '%s' "$_h" | tr 'A-F' 'a-f')" = "$(printf '%s' "$PA_SHA256" | tr 'A-F' 'a-f')" ] || {
            echo "bundler-updater: package sha256 mismatch — refusing." >&2
            _pa_env_cleanup
            exit 4
        }
        log "bundler-updater: package sha256 verified"
    fi

    if _pa_install; then
        _pa_restart
        _rc=$?
        _pa_env_cleanup
        [ "$_rc" -eq 0 ] && exit 0
        exit 6
    fi
    # 装失败路径 env 件从未被消费——同按一次性输入清走。
    _pa_env_cleanup
    if [ "$_PA_PM_SEEN" = 1 ]; then
        echo "bundler-updater: package install failed via all available frontends." >&2
        exit 4
    fi
    echo "bundler-updater: no package manager available for format '$PA_FMT'." >&2
    exit 5
}

pkg_usage() {
    echo "Usage:" >&2
    echo "  bundler-updater.sh pkg-apply --pkg <file> --format <deb|rpm|apk> --app <path>" >&2
    echo "      [--sha256 <hex>] [--env-file <file>] [--rollback] [--log <file>]" >&2
}

# 同卷 mv=rename 原子就位（含进程被杀语义）；跨卷 mv 退化为 copy+unlink，
# 中途被杀/失败会在目标名上留"存在≠完整"半成品（恢复路径 -d 判存会还原半本）。
# 故只有同卷才直移，跨卷一律两段式（与 AOT MoveTree/MoveFile 同义）：
# 先复制到同级临时名再原子 mv 就位，目标槽只呈现"未开始"或"全本"两态。
_mnt_of() {  # $1 路径→mountinfo 最长前缀挂载点（\040/\134 转义还原）；取不到返空
    # 单次 awk 扫描（逐行 fork 三进程×行数太贵）。前缀比对在 "t/" 上做：
    # mp="/" 取前缀 "/" 恰匹配一切绝对路径；mp 相等/段边界（/foo vs /foobar）都被兜住。
    # 路径经 ENVIRON 传入而非 -v——-v 对值做转义处理会吃掉路径里的反斜杠。
    # \ddd 转义逐段用 index/substr 解码（AOT UnescapeMountField 同口径，覆盖
    # \040/\011/\012/\134 全部）——不走 gsub：awk 正则/替换串的转义层数在
    # gawk/mawk/busybox 间方言不一，实测各方都不一致。
    _MNT_T="$1/" awk '
        BEGIN { t = ENVIRON["_MNT_T"] }
        function unesc(s,   out,p,idx,o) {
            out=""; p=s
            while ((idx = index(p, "\\")) > 0) {
                out = out substr(p, 1, idx-1)
                o = substr(p, idx+1, 3)
                if (o ~ /^[0-7][0-7][0-7]$/) {
                    out = out sprintf("%c", substr(o,1,1)*64 + substr(o,2,1)*8 + substr(o,3,1))
                    p = substr(p, idx+4)
                } else { out = out "\\"; p = substr(p, idx+1) }
            }
            return out p
        }
        {
            mp = unesc($5)
            pre = (mp == "/") ? "/" : mp "/"
            if (index(t, pre) == 1 && length(mp) > length(best)) best = mp
        }
        END { if (best != "") print best }
    ' </proc/self/mountinfo
}
same_mount() {  # $1,$2 路径属同一挂载点？mountinfo 最长前缀比对（对账 AOT SameVolume
    # 挂载点语义）；mountinfo 缺席平台（macOS 无 bind mount，st_dev 不失盲）退 st_dev 祖先比对。
    if [ -r /proc/self/mountinfo ]; then
        _sm_a=$(_mnt_of "$1"); _sm_b=$(_mnt_of "$2")
        [ -n "$_sm_a" ] && [ "$_sm_a" = "$_sm_b" ]
    else
        _sm_a=$(_dev_of "$(existing_ancestor "$1")" 2>/dev/null)
        _sm_b=$(_dev_of "$(existing_ancestor "$2")" 2>/dev/null)
        [ -n "$_sm_a" ] && [ -n "$_sm_b" ] && [ "$_sm_a" = "$_sm_b" ]
    fi
}
move_node() {
    # 直移快路只走"确证同挂载点"（rename 原子）：st_dev 判卷对 bind mount 失盲——
    # 同 superblock 跨挂载点 rename 仍 EXDEV，mv 内部退 copy+unlink 半途留残件/损源件。
    if same_mount "$1" "$2" && mv "$1" "$2" 2>/dev/null; then
        return 0
    fi
    # 直 mv 半途可能已把部分条目落进目标名——先清槽再两段式。清不掉还继续
    # mv 会把暂存件嵌进目标目录内部并 rc0——静默假成功，必须判败。
    rm -rf "$2" || return 1
    _mn_staged="$2.partial-$$"
    rm -rf "$_mn_staged" || return 1
    if [ -d "$1" ]; then
        mkdir -p "$_mn_staged" || { rm -rf "$_mn_staged"; return 1; }
        cp -a "$1"/. "$_mn_staged"/ || { rm -rf "$_mn_staged"; return 1; }
    else
        cp -p "$1" "$_mn_staged" || { rm -rf "$_mn_staged"; return 1; }
    fi
    if mv "$_mn_staged" "$2"; then
        # 目标已确立后源槽清场失败属"已提交"类——返独立 rc=2 供调用方分流
        # （备份腿据此还原、载荷腿据 AOT catch 同款走还原），不混同未提交失败；
        # 其余失败点 $2 只可能是未开始/残缺，绝不被当全本。
        rm -rf "$1" || return 2
        return 0
    fi
    rm -rf "$_mn_staged"
    return 1
}

# 恢复向搬移（备份→装位）：rc2=装位已提交仅源槽残件清不掉——尽力再清按恢复成功；
# rc1 才是真失败。否则遗留 marker+残件，下轮崩溃恢复会把残件当全本还原
#（AOT MoveRestoring 同义——它按"装位在场即已提交"的两态不变量判，sh 用 rc2 判）。
restore_move() {
    move_node "$1" "$2"
    _rm_rc=$?
    if [ "$_rm_rc" -eq 2 ]; then
        rm -rf "$1" 2>/dev/null || true
        return 0
    fi
    return "$_rm_rc"
}

case "${1:-}" in
    apply) ;;
    pkg-apply) shift; pkg_apply "$@" ;;
    *) usage; exit 2 ;;
esac
shift

while [ $# -gt 0 ]; do
    case "$1" in
        --keep-payload) KEEP_PAYLOAD=1; shift ;;
        --rollback) ROLLBACK=1; shift ;;
        --install-dir|--payload|--wait-pid|--app|--backup-dir|--retain-backup-to|--log|--wait-timeout|--lock-timeout)
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
                --lock-timeout) LOCK_TIMEOUT=$2 ;;
            esac
            case "$1" in
                --wait-pid|--wait-timeout|--lock-timeout)
                    case "$2" in
                        ''|*[!0-9]*) echo "bundler-updater: option '$1' requires a non-negative integer." >&2; exit 2 ;;
                    esac ;;
            esac
            # 与 AOT Parse 同口径：--wait-pid/--wait-timeout 必须为正（0/负数
            # 两侧统一 rc=2 用法拒绝，不再一侧超时一侧放行）。
            case "$1" in
                --wait-pid|--wait-timeout)
                    [ "$2" -gt 0 ] || { echo "bundler-updater: option '$1' requires a positive integer." >&2; exit 2; } ;;
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
    # / 的 ${%/} 清成空串会落进 norm_lexical 的相对路径分支退回 $PWD——
    # install=/ 静默变成调用者 cwd，卷根拒与互嵌判全失准；保 / 原样。
    [ -n "$_np" ] || _np=/
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

[ -n "$INSTALL_DIR" ] || { echo "bundler-updater: --install-dir is required." >&2; exit 2; }
[ "$ROLLBACK" = 1 ] || [ -n "$PAYLOAD_DIR" ] || { echo "bundler-updater: --payload is required unless --rollback." >&2; exit 2; }
INSTALL_RAW=$INSTALL_DIR
PAYLOAD_RAW=$PAYLOAD_DIR
INSTALL_DIR="$(norm_path "$INSTALL_DIR")" || exit 2
[ -z "$PAYLOAD_DIR" ] || PAYLOAD_DIR="$(norm_path "$PAYLOAD_DIR")" || exit 2
# 卷根装位无同级位置放默认备份槽——与 AOT/managed 侧同拒（须在派生槽名之前）。
if [ "$INSTALL_DIR" = "/" ] && [ -z "$BACKUP_DIR" ]; then
    echo "bundler-updater: install directory '/' is a volume root — no sibling location exists for the default backup slot; pass --backup-dir explicitly." >&2
    exit 2
fi
# / 的 ${%/} 清成空会落出相对路径病态槽名——兜底成 / 保持绝对拼写。
INSTALL_BASE=${INSTALL_DIR%/}
[ -n "$INSTALL_BASE" ] || INSTALL_BASE=/
[ -z "$BACKUP_DIR" ] || BACKUP_DIR="$(norm_parent "$BACKUP_DIR")" || exit 2
[ -z "$RETAIN_DIR" ] || RETAIN_DIR="$(norm_parent "$RETAIN_DIR")" || exit 2
# 关系判另取全物理名：叶段为符号链接时字面拼写会逃逸同址/互嵌判
#（叶链指向 install/payload 在字面层面不同名）；文件操作仍走上面的叶字面拼写。
[ -z "$RETAIN_DIR" ] || RETAIN_CMP="$(norm_path "$RETAIN_DIR")" || exit 2
# 重启目标与日志同样按调用方 cwd 规范化成绝对路径——脚本的工作目录不是用户的 cwd。
[ -z "$APP_PATH" ] || APP_PATH="$(norm_path "$APP_PATH")" || exit 2
# LOG_FILE 不能走 norm_path——逐段 resolve_link 会把 --log 的叶链接物理化成
# 目标件，链接拒判（_pa_log_open -h/AOT O_NOFOLLOW→ELOOP）被整体绕过。
# 词法归一即可（AOT Path.GetFullPath 同口径：消 ./.. 段不动链接）。
[ -z "$LOG_FILE" ] || LOG_FILE="$(norm_lexical "$LOG_FILE")" || exit 2
# --log 与 pkg-apply 同域校验（apply 也可能在提权语境下被拉起）：违规
# rc2 拒；良性不可写 WARN 降级续行（AOT TryOpenLogFile 两级同口径）。
# _pa_log_done 预置让 log() 不再懒开。
if [ -n "$LOG_FILE" ]; then
    _pa_log_done=1
    _pa_log_open
    _lr=$?
    if [ "$_lr" = 2 ]; then
        _LOG_BAD=1
    elif [ "$_lr" != 0 ]; then
        echo "bundler-updater: --log '$LOG_FILE' is not a writable file owned by the calling user." >&2
        exit 2
    fi
fi
[ -n "$BACKUP_DIR" ] || BACKUP_DIR="${INSTALL_BASE}.bundler-backup"
# 备份比较名一律全物理化——默认备份位同样可能预置叶链（与 RETAIN_CMP 物理名不同名会同址逃逸）。
BACKUP_CMP="$(norm_path "$BACKUP_DIR")" || exit 2
MARKER="${INSTALL_BASE}.bundler-swap"
LOCK_FILE="${INSTALL_BASE}.bundler-lock"
LOCK_TIMEOUT=${LOCK_TIMEOUT:-30}

# 备份目录与安装/载荷同址或互嵌同样是抹数据的形状（换包前会 rm 旧备份）——与 AOT 侧同拒。
for _p in "$INSTALL_DIR" "$PAYLOAD_DIR"; do
    [ -n "$_p" ] || continue
    case "$BACKUP_CMP" in
        "$_p"|"${_p%/}"/*) { echo "bundler-updater: backup/install/payload directories must not nest inside each other." >&2; exit 2; } ;;
    esac
    case "$_p" in
        "${BACKUP_CMP%/}"/*) { echo "bundler-updater: backup/install/payload directories must not nest inside each other." >&2; exit 2; } ;;
    esac
done

# 载荷与安装同址或互嵌同样是抹数据的形状（备份移走后 swap 会以空载荷覆盖再删源）——与 AOT 侧同拒。
if [ -n "$PAYLOAD_DIR" ]; then
    case "$INSTALL_DIR" in
        "$PAYLOAD_DIR"|"${PAYLOAD_DIR%/}"/*) { echo "bundler-updater: backup/install/payload directories must not nest inside each other." >&2; exit 2; } ;;
    esac
    case "$PAYLOAD_DIR" in
        "${INSTALL_DIR%/}"/*) { echo "bundler-updater: backup/install/payload directories must not nest inside each other." >&2; exit 2; } ;;
    esac
    # 拼写级互嵌与物理判并列：载荷经 install 内链接别名进入时拼写落在 install
    # 之内，解真后物理位置虽在外，但访问路径依赖随换包搬走的别名节点——字面
    # 嵌套一律拒，与 C# 侧 GetFullPath 拼写判同义。
    INSTALL_SP=$(norm_lexical "$INSTALL_RAW")
    PAYLOAD_SP=$(norm_lexical "$PAYLOAD_RAW")
    case "$PAYLOAD_SP" in
        "$INSTALL_SP"|"${INSTALL_SP%/}"/*) { echo "bundler-updater: backup/install/payload directories must not nest inside each other." >&2; exit 2; } ;;
    esac
fi

# 保留目录与安装/载荷/备份目录同址或互嵌同样是抹数据的形状——与 AOT 侧 SameOrInside 同拒。
if [ -n "$RETAIN_DIR" ]; then
    _r="$RETAIN_CMP"
    for _p in "$INSTALL_DIR" "$BACKUP_CMP" "$PAYLOAD_DIR"; do
        [ -n "$_p" ] || continue
        case "$_r" in
            "$_p"|"${_p%/}"/*) { echo "bundler-updater: retained-backup directory must not nest inside install/payload/backup directories." >&2; exit 2; } ;;
        esac
        case "$_p" in
            "${_r%/}"/*) { echo "bundler-updater: retained-backup directory must not nest inside install/payload/backup directories." >&2; exit 2; } ;;
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

# 进程存活探测（kill -0 语义补齐）：kill -0 对存活但异属主的进程返回 EPERM——
# 直接拿失败判死会把在飞的异属主持锁者误判死锁来偷锁、把异属主宿主当已退出
# 跳过等待直接换包。失败后复核存在性：/proc/<pid> 在场即活（无 signal 权 ≠
# 已退出）；无 /proc 的宿主走 `ps -p`；两者皆无时保守按活处理——宁等超时，
# 也不偷锁/不跳等（两误判方向都退化为超时而非误换包）。
pid_alive() {
    kill -0 "$1" 2>/dev/null && return 0
    if [ -d /proc ]; then
        [ -d "/proc/$1" ]
    elif command -v ps >/dev/null 2>&1; then
        ps -p "$1" >/dev/null 2>&1
    else
        return 0
    fi
}

# 并发互斥（与 AOT AcquireLock 同形同协议——同锁件互斥跨实现实例）：锁件
# <install>.bundler-lock 普通文件含持锁 pid；noclobber `>` 原子创建，撞锁按
# pid_alive 判活等到 --lock-timeout 超时拒 rc=3，持锁者已死即夺锁——防并发第二实例
# 把在飞 marker 当崩溃恢复拆掉别人换一半的包。
[ -L "$LOCK_FILE" ] && rm -f "$LOCK_FILE"
_lk_deadline=$(( $(date +%s) + LOCK_TIMEOUT ))
while :; do
    _lk_owner=""
    if ( set -C; : >"$LOCK_FILE" ) 2>/dev/null; then
        printf '%s' "$$" >"$LOCK_FILE"
        # 写入后回验 owner——锁被并发夺走时文件已是他人件，不验就会与持锁者
        # 同入 swap；owner 非本进程即跌入下方等锁/夺锁路径。
        _lk_owner=$(cat "$LOCK_FILE" 2>/dev/null || true)
        if [ "$_lk_owner" = "$$" ]; then
            trap 'rm -f -- "$LOCK_FILE"' EXIT
            break
        fi
    else
        _lk_owner=$(cat "$LOCK_FILE" 2>/dev/null || true)
        # 装位根目录缺席时 noclobber 建锁恒失败、owner 恒空——不分流会空转到
        # --lock-timeout 报 rc3 假竞争；装位根缺席是用法错 rc2（与 AOT
        # ThrowIfInstallRootMissing 同义）。
        [ -e "$LOCK_FILE" ] || [ -d "$(dirname "$INSTALL_DIR")" ] || \
            { echo "bundler-updater: install directory '$INSTALL_DIR' does not exist." >&2; exit 2; }
    fi
    if [ -n "$_lk_owner" ] && ! pid_alive "$_lk_owner"; then
        # 死锁夺锁——删前复读 owner 未变且仍死才删，防双实例互删对方新锁件。
        _lk_owner2=$(cat "$LOCK_FILE" 2>/dev/null || true)
        if [ "$_lk_owner2" = "$_lk_owner" ]; then
            rm -f "$LOCK_FILE" 2>/dev/null
        fi
        continue
    fi
    if [ "$(date +%s)" -ge "$_lk_deadline" ]; then
        echo "bundler-updater: another updater holds the lock — timed out waiting." >&2
        exit 3
    fi
    sleep 0.1 2>/dev/null || sleep 1
done

# 空间预检（对账 AOT CheckFreeSpace）：同挂载点原子 rename 不计；跨挂载点两段式暂存、
# --keep-payload 复制、回滚复制按树体积估到目标槽所在卷；探测失败只 WARN 放行。
# 与 AOT 的口径差：空间池合并 sh 按 st_dev（bind mount 同池则加总），AOT 按挂载点根
# 不加总——sh 侧更严（可能多算需求），方向安全。
existing_ancestor() {  # $1 路径→返回已存在的可写祖先目录（文件取父目录）
    _ea=$1
    while [ ! -e "$_ea" ]; do
        _ea=$(dirname "$_ea") || return 1
    done
    [ -f "$_ea" ] && _ea=$(dirname "$_ea")
    printf '%s' "$_ea"
}
_dev_of() {  # $1 已存在路径→st_dev 设备号（GNU stat -c / BSD stat -f 双探测）
    stat -c %d "$1" 2>/dev/null || stat -f %d "$1" 2>/dev/null
}
same_volume() {  # $1,$2 路径：已存在祖先 st_dev 比对——硬链接/改名探针都不行：
    # 跨卷 mv 静默复制返回 0 误判同卷；exFAT 等无硬链接 FS 把同卷误判跨卷。
    _da=$(_dev_of "$(existing_ancestor "$1")")
    _db=$(_dev_of "$(existing_ancestor "$2")")
    [ -n "$_da" ] && [ -n "$_db" ] && [ "$_da" = "$_db" ]
}
node_bytes() {  # 文件/目录树总字节；不存在=0
    if [ -f "$1" ]; then wc -c <"$1" 2>/dev/null || echo 0
    elif [ -d "$1" ]; then du -sk "$1" 2>/dev/null | awk '{print $1*1024}' || echo 0
    else echo 0; fi
}
volume_check() {  # $1 锚点 $2 需要字节；探测失败放行，明确不够才拒 rc=4
    [ "$2" -gt 0 ] 2>/dev/null || return 0
    # 锚点缺席时上溯最近现存祖先——对账 AOT FindVolume 的 DriveInfo 祖先走查，
    # 缺席锚点直接 df 必败会把可判定场景降级成 WARN 放行。
    _anchor="$1"
    while [ ! -e "$_anchor" ] && [ "$_anchor" != "$(dirname "$_anchor")" ]; do
        _anchor=$(dirname "$_anchor")
    done
    _free=$(df -kP "$_anchor" 2>/dev/null | awk 'NR==2{print $4*1024}')
    [ -n "$_free" ] || { log "bundler-updater: WARN free-space probe failed near '$1'"; return 0; }
    log "bundler-updater: free-space check near '$1' — need ~$2 bytes, $_free available"
    [ "$_free" -ge "$2" ] || { echo "bundler-updater: insufficient free space near '$1': swap requires ~$2 bytes, only $_free available." >&2; return 1; }
}
check_free_space() {
    if [ "$ROLLBACK" = 1 ]; then
        volume_check "$INSTALL_DIR" "$(node_bytes "$BACKUP_DIR")" || exit 4
        return 0
    fi
    _need_backup=0; _need_install=0
    # "会不会复制"按挂载点判（与 move_node 直移门同一谓词）：bind mount 同
    # st_dev 异挂载点仍走两段式复制——按 st_dev 估零需求会中途 ENOSPC。
    same_mount "$INSTALL_DIR" "$BACKUP_DIR" || _need_backup=$(node_bytes "$INSTALL_DIR")
    if [ "$KEEP_PAYLOAD" = 1 ] || ! same_mount "$PAYLOAD_DIR" "$INSTALL_DIR"; then
        _need_install=$(node_bytes "$PAYLOAD_DIR")
    fi
    # 备份槽父级与 install 同卷时两腿合并判——"空间池是否共享"按 st_dev：
    # bind mount 共享同一文件系统的空闲空间，需求要加总。
    if same_volume "$(dirname "$BACKUP_DIR")" "$INSTALL_DIR"; then
        volume_check "$INSTALL_DIR" "$(( _need_backup + _need_install ))" || exit 4
    else
        volume_check "$(dirname "$BACKUP_DIR")" "$_need_backup" || exit 4
        volume_check "$INSTALL_DIR" "$_need_install" || exit 4
    fi
}

if [ -n "$WAIT_PID" ]; then
    log "bundler-updater: waiting for pid $WAIT_PID to exit"
    waited=0
    while pid_alive "$WAIT_PID"; do
        waited=$((waited + 1))
        [ "$waited" -le "$WAIT_TIMEOUT" ] || { echo "bundler-updater: the target process did not exit in time." >&2; exit 3; }
        sleep 1
    done
fi

# marker 槽叶链一律删——真 marker 是本脚本写的普通文件；叶链会让 -f 顺链假触发
# 崩溃恢复、printf> 顺链写穿污染保护区外目标（与 AOT DeleteLinkNodeIfPresent 同义）。
[ -L "$MARKER" ] && rm -f "$MARKER"
# 崩溃恢复先于存在性检查：上轮死在备份与换包之间时安装目标可能缺失/半成品，先还原。
# marker 内容分流（与 AOT 侧同构）：'rollback in progress'=回滚中崩溃——复制还原，
# 备份保留在备份槽可重试；'swap in progress'/其他拼写（旧版引导件 marker）=换包语义——
# 维持移动还原（备份移回安装位即耗尽）。
RECOVERED=0
if [ -f "$MARKER" ]; then
    case "$(cat "$MARKER" 2>/dev/null || true)" in
        "rollback in progress") MARKER_KIND=rollback ;;
        *) MARKER_KIND=swap ;;
    esac
    log "bundler-updater: interrupted ${MARKER_KIND} detected, restoring backup first"
    if [ -f "$BACKUP_DIR" ]; then
        # 槽位节点类型不设限（与 AOT DeleteNodeIfPresent 同义）：备份是文件而
        # 安装槽挂目录节点时 rm -f 删不掉、cp -f 会写进目录内部；删除失败同样
        # 判 rc4——清不掉还继续写就是静默假恢复。
        rm -rf "$INSTALL_DIR" || { echo "bundler-updater: cannot clear install slot." >&2; exit 4; }
        if [ "$MARKER_KIND" = rollback ]; then
            cp -f "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
        else
            restore_move "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
        fi
    elif [ -d "$BACKUP_DIR" ]; then
        rm -rf "$INSTALL_DIR" || { echo "bundler-updater: cannot clear install slot." >&2; exit 4; }
        if [ "$MARKER_KIND" = rollback ]; then
            mkdir -p "$INSTALL_DIR" || exit 4
            cp -a "$BACKUP_DIR"/. "$INSTALL_DIR"/ || { echo "bundler-updater: rollback copy failed." >&2; exit 4; }
        else
            restore_move "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
        fi
    else
        { echo "bundler-updater: swap marker exists but backup missing — cannot recover." >&2; exit 4; }
    fi
    # 恢复成功后 marker 回收失败会留下假现场——下轮再进恢复腿楔形（swap 型
    # 备份已耗尽撞 "backup missing"；rollback 型误还原）。宁可响败不留残。
    rm -f "$MARKER" || exit 4
    RECOVERED=1
fi

# 空间预检在各腿的存在性检查之后（与 AOT 同序）：它是纯只读探测，装位/载荷缺席是
# 用法错 rc2——探测抢跑会颠倒 rc2/rc4 分类序并对不存在的路径产出误导性 WARN。
# 恢复已还原时备份槽已空，node_bytes=0 自然放行不误拒。

# 文件级语义：安装目标是单文件（AppImage 单件）或回滚备份是文件——
# 目录级换包会清掉宿主目录里的无关文件，故单件替换。
# 回滚按备份形态选腿而非装位形态（与 AOT 同义）：异型槽（文件装位+目录
# 备份）走目录腿清槽重建。
if { [ "$ROLLBACK" = 1 ] && [ -f "$BACKUP_DIR" ]; } || \
   { [ "$ROLLBACK" != 1 ] && [ -f "$INSTALL_DIR" ]; }; then
    if [ "$ROLLBACK" = 1 ]; then
        [ "$RECOVERED" = 1 ] && { log "bundler-updater: crash recovery already restored the backup"; exit 0; }
        [ -f "$BACKUP_DIR" ] || { echo "bundler-updater: no rollback backup at '$BACKUP_DIR'." >&2; exit 4; }
        check_free_space
        log "bundler-updater: rollback '$BACKUP_DIR' → '$INSTALL_DIR'"
        # 回滚在动安装目标前同样写 marker（内容与 swap 区分）：复制中途崩溃时
        # 恢复路径按 'rollback' 复制还原——备份保留可重试，故失败不复位 marker。
        printf 'rollback in progress' >"$MARKER" || exit 4
        # 装位挂目录节点时 cp -f 会把备份复制进目录内部并 rc0 静默假成功——
        # 先清槽再写（与恢复腿同义，AOT File.Copy 到目录路径本就抛 rc4）。
        rm -rf "$INSTALL_DIR" || { echo "bundler-updater: cannot clear install slot." >&2; exit 4; }
        cp -f "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback copy failed." >&2; exit 4; }
        rm -f "$MARKER" || exit 4
        [ -n "$APP_PATH" ] && { log "bundler-updater: restart '$APP_PATH'"; restart_app; }
        log "bundler-updater: done"
        exit 0
    fi
    [ -f "$PAYLOAD_DIR" ] || { echo "bundler-updater: file-swap payload must be a file." >&2; exit 2; }
    [ "$PAYLOAD_DIR" = "$INSTALL_DIR" ] && { echo "bundler-updater: install and payload must not be the same file." >&2; exit 2; }
    check_free_space
    log "bundler-updater: backup '$INSTALL_DIR' → '$BACKUP_DIR'"
    # 备份槽异型节点同义清理（AOT DeleteNodeIfPresent）：槽上挂目录节点时
    # rm -f 判败楔形——与装位槽同口径通吃 file/dir。
    # 备份父目录不预建：自定义 --backup-dir 父链缺席时 AOT File.Copy 判败 rc4，
    # sh 侧 staged cp -p 同形判败——parity 要求不补（目录腿经 staged mkdir -p 自补，
    # 与 AOT CreateDirectory 同义对称）。
    rm -rf "$BACKUP_DIR" || exit 4
    printf 'swap in progress' >"$MARKER" || exit 4
    # move_node rc2 = 备份已确立、仅装位源槽清场失败——按 swap-failed 同款还原
    # （对账 AOT catch 按备份在场还原的语义）；rc1 = 备份未确立/残缺，收 marker 保装位。
    move_node "$INSTALL_DIR" "$BACKUP_DIR"
    _mn_rc=$?
    if [ "$_mn_rc" -ne 0 ]; then
        if [ "$_mn_rc" -eq 2 ]; then
            rm -rf "$INSTALL_DIR" || { echo "bundler-updater: cannot clear install slot." >&2; exit 4; }
            restore_move "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
        fi
        rm -f "$MARKER" || exit 4
        exit 4
    fi
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
        rm -rf "$INSTALL_DIR" || { echo "bundler-updater: cannot clear install slot." >&2; exit 4; }
        restore_move "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
        rm -f "$MARKER"
        exit 4
    fi
    rm -f "$MARKER" || exit 4
    # 文件级备份保留时按安装文件真名落在保留目录里；迁移失败降级留瞬备不阻断换包。
    retain_or_remove_backup "$BACKUP_DIR" "$(basename "$INSTALL_DIR")" || \
        log "bundler-updater: WARN retain/remove backup failed; transient backup left at '$BACKUP_DIR'."
    [ -n "$APP_PATH" ] && { log "bundler-updater: restart '$APP_PATH'"; restart_app; }
    [ "$KEEP_PAYLOAD" = 1 ] || rm -f "$PAYLOAD_DIR" 2>/dev/null || true
    log "bundler-updater: done"
    exit 0
fi

# 装位必须存在；回滚时容忍任意节点形态——异型槽（文件装位+目录备份）走
# 本腿清槽重建，装位非目录不是用法错（与 AOT 同义）。
[ -d "$INSTALL_DIR" ] || { [ "$ROLLBACK" = 1 ] && [ -f "$INSTALL_DIR" ]; } || \
    { echo "bundler-updater: install directory '$INSTALL_DIR' does not exist." >&2; exit 2; }
[ "$ROLLBACK" = 1 ] || [ -d "$PAYLOAD_DIR" ] || { echo "bundler-updater: payload directory '$PAYLOAD_DIR' does not exist." >&2; exit 2; }
check_free_space

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
    # PlistBuddy 对缺失/损坏 plist 把诊断写 stdout（stderr 拦不住）——先判存在再按无空格过滤，
    # 失败一律空串跳过比较，与 AOT TryReadBundleId 的 null 语义对齐。
    OLD_BID=""; [ -f "$INSTALL_DIR/Contents/Info.plist" ] && \
        OLD_BID=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$INSTALL_DIR/Contents/Info.plist" 2>/dev/null | grep -v ' ' || true)
    NEW_BID=""; [ -f "$PAYLOAD_DIR/Contents/Info.plist" ] && \
        NEW_BID=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$PAYLOAD_DIR/Contents/Info.plist" 2>/dev/null | grep -v ' ' || true)
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
    # 回滚在删安装目录前同样写 marker（内容与 swap 区分）：崩溃窗口=备份完整
    # 而安装位缺失/半成品——恢复路径按 'rollback' 复制还原（备份保留可重试），
    # 故失败不复位 marker。
    printf 'rollback in progress' >"$MARKER" || exit 4
    rm -rf "$INSTALL_DIR" || exit 4
    mkdir -p "$INSTALL_DIR" || exit 4
    cp -a "$BACKUP_DIR"/. "$INSTALL_DIR"/ || { echo "bundler-updater: rollback copy failed." >&2; exit 4; }
    rm -f "$MARKER" || exit 4
    [ -n "$APP_PATH" ] && { log "bundler-updater: restart '$APP_PATH'"; restart_app; }
    log "bundler-updater: done"
    exit 0
fi

log "bundler-updater: backup '$INSTALL_DIR' → '$BACKUP_DIR'"
# 备份父目录不预建：备份槽父链缺席时 AOT CreateDirectory 与 staged mkdir -p
# 同形自补，本就对称；装位缺席 rc2 已前置拦截。
rm -rf "$BACKUP_DIR" || exit 4
printf 'swap in progress' >"$MARKER" || exit 4
# move_node rc2 = 备份已确立、仅装位源槽清场失败——按 swap-failed 同款还原
# （对账 AOT catch 按备份在场还原的语义）；rc1 = 备份未确立/残缺，收 marker 保装位。
move_node "$INSTALL_DIR" "$BACKUP_DIR"
_mn_rc=$?
if [ "$_mn_rc" -ne 0 ]; then
    if [ "$_mn_rc" -eq 2 ]; then
        rm -rf "$INSTALL_DIR" || { echo "bundler-updater: cannot clear install slot." >&2; exit 4; }
        restore_move "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
    fi
    rm -f "$MARKER" || exit 4
    exit 4
fi

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
    rm -rf "$INSTALL_DIR" || { echo "bundler-updater: cannot clear install slot." >&2; exit 4; }
    restore_move "$BACKUP_DIR" "$INSTALL_DIR" || { echo "bundler-updater: rollback failed." >&2; exit 4; }
    rm -f "$MARKER"
    exit 4
fi
rm -f "$MARKER" || exit 4

retain_or_remove_backup "$BACKUP_DIR" "" || \
    log "bundler-updater: WARN retain/remove backup failed; transient backup left at '$BACKUP_DIR'."

[ -n "$APP_PATH" ] && { log "bundler-updater: restart '$APP_PATH'"; restart_app; }
[ "$KEEP_PAYLOAD" = 1 ] || rm -rf "$PAYLOAD_DIR" 2>/dev/null || true
log "bundler-updater: done"
exit 0
