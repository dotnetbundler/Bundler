#!/bin/sh
# syspkg-verify.sh <deb|rpm|apk> —— UPDATE 系统包扩展 B4 容器验收腿（进不了 dotnet test 的件）。
# 在对应发行版容器内以 root 直跑：root 语境 = pkexec/sudo 提权成功后的等价执行体语境；
# SUDO_UID/SUDO_GID、PKEXEC_UID 注入复现"提权后降权回原用户"语义；图形提权交互面
# （pkexec→zenity/kdialog→sudo -S）不在容器覆盖内，仍挂 special-acceptance 外部验收。
# 每腿同场景跑 AOT 引导件（musl 静态件，glibc/musl 容器通吃）与 tools/posix sh 降级件
# 双实现奇偶：包管理器真装真账本 + conffile/降级/缺席退化 + 降权重启 env 回灌 + 退出码面。
#
# env 输入：SYSPKG_AOT=AOT 引导件路径（默认 /opt/updater/bundler-updater）
#           SYSPKG_SH=sh 件路径（默认 /src/src/Bundler.Updater.Bootstrap/tools/posix/bundler-updater.sh）
#           SYSPKG_EV=证据落盘目录（默认 /ev，宿主挂载点）；SYSPKG_WORK=包构建工作目录（/work）
# 出口：任一硬断言失败 → rc=1；证据（RESULTS.txt 结论表 + console.log 全量输出 +
# 逐腿 --log/账本快照/探针记录）全部落 SYSPKG_EV 供上传工件留痕。
set -u

LEG=${1:?"usage: syspkg-verify.sh <deb|rpm|apk>"}
case "$LEG" in deb|rpm|apk) ;; *) echo "unknown leg: $LEG" >&2; exit 2 ;; esac

AOT=${SYSPKG_AOT:-/opt/updater/bundler-updater}
SHIMPL=${SYSPKG_SH:-/src/src/Bundler.Updater.Bootstrap/tools/posix/bundler-updater.sh}
EV=${SYSPKG_EV:-/ev}
W=${SYSPKG_WORK:-/work}
PKG=bundler-syspkg-probe
APP=/usr/bin/$PKG
CONF=/etc/$PKG/probe.conf
STAMPS=/var/lib/$PKG.stamps
REC=/tmp/syspkg-rec
RESULTS="$EV/RESULTS.txt"
CONSOLE="$EV/console.log"
FAILS=""

mkdir -p "$EV" "$W" "$REC" || exit 2
chmod 0777 "$REC" 2>/dev/null || true
: >"$RESULTS"; : >"$CONSOLE"

note() { echo "$*" | tee -a "$RESULTS"; }
pass() { echo "PASS  $*" | tee -a "$RESULTS"; }
fail() { echo "FAIL  $*" | tee -a "$RESULTS"; FAILS="$FAILS $1"; }
info() { echo "INFO  $*" | tee -a "$RESULTS"; }

# 调 pkg-apply 并把全量输出并进 console.log。带 env 覆盖的调用一律走 env(1) 显式前缀：
#   rc_of env SUDO_UID=.. PATH=/opt/shim "$AOT" pkg-apply ...
# 不借函数帧 env 赋值——POSIX 未规定函数返回后是否回渗，dash/bash 实测会把
# SUDO_UID 等污染进后续相位。
rc_of() { "$@" >>"$CONSOLE" 2>&1; }

expect_rc() { # $1 phase-id $2 expected $3 actual
    if [ "$2" = "$3" ]; then pass "$1 rc=$3"; else fail "$1" "rc=$3 expected=$2"; fi
}
want_line() { # $1 phase-id $2 file $3 fixed-pattern $4 desc —— 证据断言（文件里要有该行）
    if grep -qF "$3" "$2" 2>/dev/null; then pass "$1 $4"; else fail "$1" "$4 —— '$3' 未命中 $2"; fi
}
want_file() { # $1 phase-id $2 path $3 desc
    if [ -f "$2" ]; then pass "$1 $3"; else fail "$1" "$3 —— ${2} 缺失"; fi
}

# nohup 分离 spawn 是异步落盘——探针记录轮询等待，最多 ~20s。
wait_lines() { # $1=file $2=min-lines
    _wl_i=0
    while [ "$_wl_i" -lt 40 ]; do
        if [ -f "$1" ]; then
            _wl_n=$(wc -l <"$1" 2>/dev/null || echo 0)
            [ "$_wl_n" -ge "$2" ] && return 0
        fi
        _wl_i=$((_wl_i + 1))
        sleep 0.5
    done
    return 1
}
want_rec() { # $1 phase-id $2 rec-file $3 expect-substr（形如 uid=1000）$4 desc
    if wait_lines "$2" 1 && grep -qF "$3" "$2" 2>/dev/null; then
        pass "$1 $4"
    else
        fail "$1" "${4} —— 探针记录 '$3' 未命中（${2}）"
    fi
}

# PATH 白名单化（tests/Integration SeedShim 先例）：shim 目录内只放给定工具的符号链接，
# 实现侧 command -v/PATH 探测只看得到 shim 内工具——包管理器与降权工具靠"给不给进
# shim"分流成各退化腿。sh 支路协议基础工具固定入组（awk/sed/tr/ls/dirname/env/nohup/id/
# mkdir——前六件协议解析用，id/mkdir 喂探针）。
# stat 属协议工具：_pa_env_validate 的 st_uid 复核走 stat -c %u（缺 stat=
# 无法复核→拒，shim 腿曾被空值拒成 rc3）；timeout 喂读侧 timeout 5 cat 上限。
BASETOOLS="env nohup dirname awk ls sed tr id mkdir cat stat timeout"
# dpkg 在 PATH 内自查运行依赖（缺一个整体报 "expected programs not found" 退 2）：
# sh/rm/tar/diff + dpkg-deb/dpkg-split/ldconfig/start-stop-daemon（实跑见
# "unable to execute split package reassembly (dpkg-split)"）——shim 给 dpkg 必须一并给。
DPKGTOOLS="sh rm tar diff dpkg dpkg-deb dpkg-split dpkg-query ldconfig start-stop-daemon"
makeshim() { # $1=dir; rest=tool 名（缺失宿主工具即 FAIL——shim 缺口属环境问题）
    _ms_d=$1; shift
    rm -rf "$_ms_d"; mkdir -p "$_ms_d"
    for _ms_t do
        _ms_p=$(command -v "$_ms_t" 2>/dev/null || true)
        if [ -n "$_ms_p" ]; then
            ln -sf "$_ms_p" "$_ms_d/$_ms_t"
        else
            fail "shim" "工具 '$_ms_t' 宿主缺失，无法入 shim"
        fi
    done
}

# 会话 env 件：HOME/DISPLAY 属最小集语义验证，MYVAR 标记归属相位，SYSPKG_REC 指
# 挥探针把记录落到本相位专属件（$REC 目录 0777，相位间共写）。件被实现读毕即删——
# 一相位一件不复用：跨相位复用会走 minimal-env WARN，MYVAR/SYSPKG_REC 不注入，
# 探针落共享 default.txt，后续相位的 uid=0 断言会被前序相位记录喂成假绿。
mk_env() { # $1=file $2=rec-file $3=myvar-tag $4=caller-uid（可选）
    printf 'HOME=/home/tester\nDISPLAY=:99\nMYVAR=%s\nSYSPKG_REC=%s\n' "$3" "$2" >"$1"
    # --env-file 属主复核（A2）：声明 SUDO_UID/PKEXEC_UID 的相位须把件 chown
    # 给该 uid——本脚本以 root 写件，不 chown 会按"异属主"被 rc3 拒（真实语义：
    # 件本由非特权客户端写出）。无 uid 相位保持 root 属主（调用者=root euid）。
    if [ -n "${4:-}" ]; then chown "$4" "$1" || echo "WARN: chown $4 $1 failed" >&2; fi
}

# 账本快照进证据（每相位一帧）。
snap() { # $1=tag
    case "$LEG" in
        deb) { dpkg -l "$PKG" 2>&1; dpkg -s "$PKG" 2>&1; } >"$EV/ledger-$1.txt" ;;
        rpm) { rpm -qai "$PKG" 2>&1; rpm -ql "$PKG" 2>&1; } >"$EV/ledger-$1.txt" ;;
        apk) { apk list --installed "$PKG" 2>&1; apk info -a "$PKG" 2>&1; } >"$EV/ledger-$1.txt" ;;
    esac
}

ledger_ver() { # 当前账本登记版本（未装→空）
    case "$LEG" in
        deb) dpkg -s "$PKG" 2>/dev/null | sed -n 's/^Version: //p' ;;
        rpm) rpm -q --qf '%{VERSION}-%{RELEASE}\n' "$PKG" 2>/dev/null ;;
        apk) apk list --installed "$PKG" 2>/dev/null | sed -n "s/^$PKG-\([^ ]*\).*/\1/p" ;;
    esac
}
expect_ver() { # $1 phase-id $2 expected $3 desc
    _ev_v=$(ledger_ver)
    if [ "$_ev_v" = "$2" ]; then pass "$1 $3（账本=${_ev_v}）"; else fail "$1" "$3 —— 账本='${_ev_v}' expected='$2'"; fi
    snap "$1"
}

# 探针（被打包进 --app 目标）：单行落 uid/gid/pwd/会话 env 实测值——
# 降权腿与 env 回灌的判卷面。落点经 SYSPKG_REC 注入（env 件回灌顺带送达）。
# mkdir 用 || true 容错：shim PATH 相位下 mkdir 可能不在白名单内。
write_probe() {
    cat >"$W/probe.sh" <<'EOF'
#!/bin/sh
_r=${SYSPKG_REC:-/tmp/syspkg-rec/default.txt}
mkdir -p "$(dirname "$_r")" 2>/dev/null || true
{ printf 'uid=%s gid=%s pwd=%s' "$(id -u)" "$(id -g)" "$PWD"
  printf ' HOME=%s DISPLAY=%s MYVAR=%s\n' "${HOME:-}" "${DISPLAY:-}" "${MYVAR:-}"
} >>"$_r" 2>/dev/null || true
EOF
    chmod 0755 "$W/probe.sh"
}

# ---- deb：dpkg-deb 手工打最小真包（含 conffiles + postinst）----
# scriptlet 与 probe 一律只用 sh 内建件：shim PATH 相位下包管理器仍以 shim 环境
# 拉起 maintainer script，外部命令会失联。
mk_deb() { # $1=version $2=out.deb
    _md_r="$W/debroot-$1"; rm -rf "$_md_r"
    mkdir -p "$_md_r/usr/bin" "$_md_r/etc/$PKG" "$_md_r/DEBIAN"
    cp "$W/probe.sh" "$_md_r/usr/bin/$PKG"; chmod 0755 "$_md_r/usr/bin/$PKG"
    printf 'conf-content v%s\n' "$1" >"$_md_r$CONF"
    { echo "Package: $PKG"; echo "Version: $1"; echo "Architecture: all"
      echo "Maintainer: Bundler CI <noreply@bundler.invalid>"
      echo "Installed-Size: 4"; echo "Section: utils"; echo "Priority: optional"
      echo "Description: syspkg-verify probe package"; } >"$_md_r/DEBIAN/control"
    printf '%s\n' "$CONF" >"$_md_r/DEBIAN/conffiles"
    { echo '#!/bin/sh'
      echo "echo \"postinst:\$*\" >> $STAMPS 2>/dev/null || true"
    } >"$_md_r/DEBIAN/postinst"
    chmod 0755 "$_md_r/DEBIAN/postinst"
    dpkg-deb --build --root-owner-group "$_md_r" "$2" >>"$CONSOLE" 2>&1
}

# ---- rpm：rpmbuild 最小 spec（%config(noreplace) + %post）----
mk_rpm() { # $1=version $2=out.rpm
    _mr_top="$W/rpmbuild"; rm -rf "$_mr_top"
    mkdir -p "$_mr_top/BUILD" "$_mr_top/RPMS" "$_mr_top/SOURCES" "$_mr_top/SPECS" "$_mr_top/tmp"
    cp "$W/probe.sh" "$_mr_top/probe.sh"
    cat >"$_mr_top/SPECS/$PKG.spec" <<EOF
Name: $PKG
Version: $1
Release: 1
Summary: syspkg-verify probe
License: MIT
BuildArch: noarch

%description
syspkg-verify probe package

%install
mkdir -p %{buildroot}/usr/bin %{buildroot}/etc/$PKG
install -m 0755 %{_topdir}/probe.sh %{buildroot}/usr/bin/$PKG
printf 'conf-content v%s\n' '%{version}' > %{buildroot}/etc/$PKG/probe.conf

%files
/usr/bin/$PKG
%config(noreplace) /etc/$PKG/probe.conf

%post
echo "post:\$1" >> $STAMPS 2>/dev/null || true
EOF
    rpmbuild --define "_topdir $_mr_top" --define "_tmppath $_mr_top/tmp" \
        -bb "$_mr_top/SPECS/$PKG.spec" >>"$CONSOLE" 2>&1
    cp "$_mr_top"/RPMS/*/*.rpm "$2"
}

# ---- apk：三段 gzip 拼装，用 abuild-tar 产段——
#   data    : tar | abuild-tar --hash | gzip   每文件加 APK-TOOLS.checksum.SHA1
#             pax 扩展头（裸 tar 无此头 → apk 报 missing embedded checksum 拒装）
#   control : tar | abuild-tar --cut  | gzip   --cut 去掉 EOA 尾零块
#   sig     : tar | abuild-tar --cut  | gzip   成员 .SIGN.RSA.<key基名>.rsa.pub
#             内容=对 control.tar.gz 的 PKCS1v15-SHA1 裸签（abuild 同谱）
# 与 src/Bundler.AlpineApk/ApkPackageWriter.cs 段语义对齐。$3=1 时签名。
mk_apk() { # $1=pkgver(如 2.0.0-r0) $2=out.apk $3=sign(0/1)
    _ma="$W/apk-$1$3"; rm -rf "$_ma"
    mkdir -p "$_ma/root/usr/bin" "$_ma/root/etc/$PKG" "$_ma/ctl" "$_ma/sig"
    cp "$W/probe.sh" "$_ma/root/usr/bin/$PKG"; chmod 0755 "$_ma/root/usr/bin/$PKG"
    printf 'conf-content v%s\n' "$1" >"$_ma/root$CONF"
    # --format=posix：GNU tar posix 逐文件产 'x' pax 头（atime/ctime），abuild-tar
    # --hash 把 APK-TOOLS.checksum.SHA1 追加进该 'x' 头（paxhdr 是输入 x 头的
    # memcpy——输入无 x 头它会写全零头，apk3 即判 V2PKG_FORMAT）。control/sig
    # 段不走 --hash，ustar 即可（无 x 头噪音）。
    ( cd "$_ma/root" && tar --format=posix -cf - usr etc | abuild-tar --hash | gzip -9 ) \
        >"$_ma/data.tar.gz"
    _ma_dh=$(sha256sum "$_ma/data.tar.gz" | awk '{print $1}')
    _ma_sz=$(cd "$_ma/root" && find . -type f -exec wc -c {} \; | awk '{s+=$1} END {print s}')
    _ma_now=$(date +%s)
    { echo "pkgname = $PKG"; echo "pkgver = $1"
      echo "pkgdesc = syspkg-verify probe"; echo "url = https://bundler.invalid"
      echo "size = $_ma_sz"; echo "arch = x86_64"; echo "origin = $PKG"
      echo "license = MIT"; echo "builddate = $_ma_now"
      echo "datahash = $_ma_dh"; } >"$_ma/ctl/.PKGINFO"
    { echo '#!/bin/sh'
      echo "echo post-install >> $STAMPS 2>/dev/null || true"
    } >"$_ma/ctl/.post-install"
    ( cd "$_ma/ctl" && tar --format=ustar -cf - .PKGINFO .post-install | abuild-tar --cut | gzip -9 ) \
        >"$_ma/control.tar.gz"
    if [ "$3" = 1 ]; then
        openssl dgst -sha1 -sign "$W/syspkg-key.rsa" \
            -out "$_ma/sig/.SIGN.RSA.syspkg-key.rsa.rsa.pub" "$_ma/control.tar.gz"
        ( cd "$_ma/sig" && tar --format=ustar -cf - ".SIGN.RSA.syspkg-key.rsa.rsa.pub" | abuild-tar --cut | gzip -9 ) \
            >"$_ma/sig.tar.gz"
        cat "$_ma/sig.tar.gz" "$_ma/control.tar.gz" "$_ma/data.tar.gz" >"$2"
    else
        cat "$_ma/control.tar.gz" "$_ma/data.tar.gz" >"$2"
    fi
    # 证据留档：段成员清单——apk3 拒装时可直接看出是否混入 pax_global_header 之类。
    # 末句恒 rc0（|| true）取舍：证据块成败不该回传假失败；且 POSIX 函数 rc 本来就
    # 只是末命令 rc，前段产包步骤失败本函数也兜不住——包好坏由后续 apk add 相位
    # 断言接住（坏包装不进账本即 FAIL），mk_apk rc 只表"产包流程走完"。
    { echo "[$1 sign=$3]"; for _s in sig control data; do
        [ -f "$_ma/$_s.tar.gz" ] && { echo "  $_s:"; tar tzf "$_ma/$_s.tar.gz" 2>&1 | sed 's/^/    /'; }
      done; } >>"$CONSOLE" 2>&1 || true
}

# ============================ debian 腿 ============================
# 场景面：apt-get 腿（D1）、conffile 更新（D2）、--rollback 降级（D3）、缺 apt 退化
# dpkg -i + su 降权（D4）、sudo 降权（D5）、root 直跑（D6）、rc6 安装已提交（D7/D8）、
# rc5 全缺席（D9）。

leg_deb() {
    note "== deb 腿环境 =="
    command -v dpkg-deb >/dev/null 2>&1 || { fail deb-env "dpkg-deb 缺席"; return; }
    command -v apt-get >/dev/null 2>&1 || fail deb-env "apt-get 缺席（debian 镜像应带）"
    useradd -m tester 2>/dev/null || true
    T_UID=$(id -u tester 2>/dev/null || true); T_GID=$(id -g tester 2>/dev/null || true)
    [ -n "$T_UID" ] && [ -n "$T_GID" ] || { fail deb-env "tester 用户创建失败"; return; }
    # sudo 降权腿用料——debian:stable 基镜像不带 sudo，显式装
    apt-get update -qq >>"$CONSOLE" 2>&1 && \
        apt-get install -y -qq sudo >>"$CONSOLE" 2>&1 && \
        info "deb-env sudo 已装（sudo 降权腿料）" || fail deb-env "sudo 安装失败"
    write_probe
    mk_deb 1.0 "$W/v1.deb" && mk_deb 2.0 "$W/v2.deb" && mk_deb 3.0 "$W/v3.deb" \
        && pass "deb-env 三包产出" || { fail deb-env "dpkg-deb 产包失败"; return; }

    # 基线 v1（用户侧首装不走 pkg-apply——包管理器直装）
    dpkg -i "$W/v1.deb" >>"$CONSOLE" 2>&1 && pass "deb-env v1 基线直装" || { fail deb-env "v1 基线装失败"; return; }
    snap base-v1
    want_line deb-env "$STAMPS" "postinst" "postinst scriptlet 已跑（账本外证据）"

    # D1：AOT + SUDO_UID → apt-get 腿 + 默认 PATH 降权链（setpriv 优先）+ env 回灌
    mk_env "$W/env.d1" "$REC/d1.txt" d1-aot "$T_UID"
    rc_of env SUDO_UID="$T_UID" SUDO_GID="$T_GID" "$AOT" pkg-apply \
        --pkg "$W/v2.deb" --format deb --app "$APP" \
        --env-file "$W/env.d1" --log "$EV/d1-aot.log"
    expect_rc D1 0 $?
    expect_ver D1 2.0 "apt-get 腿真装 v2"
    want_line D1 "$EV/d1-aot.log" "via apt-get" "apt-get 腿被选用"
    want_rec D1 "$REC/d1.txt" "uid=$T_UID" "降权重启探针 uid=tester（setpriv 腿）"
    want_rec D1 "$REC/d1.txt" "MYVAR=d1-aot" "env 件回灌到达 --app"
    want_rec D1 "$REC/d1.txt" "HOME=/home/tester" "HOME 回灌为原用户家目录"

    # D2：AOT conffile——用户改 /etc/<pkg>/probe.conf 后 v2→v3 更新不丢（--force-confold）。
    # root 直跑相位（无 uid env）——专属 env.d2/rec d2.txt 把探针记录封在本相位。
    echo "# user-edit" >>"$CONF"
    mk_env "$W/env.d2" "$REC/d2.txt" d2-aot
    rc_of "$AOT" pkg-apply --pkg "$W/v3.deb" --format deb --app "$APP" \
        --env-file "$W/env.d2" --log "$EV/d2-aot.log"
    expect_rc D2 0 $?
    expect_ver D2 3.0 "conffile 场景更新"
    want_line D2 "$CONF" "# user-edit" "conffile 用户修改在更新后保留"
    want_rec D2 "$REC/d2.txt" "uid=0" "root 直跑语境探针 uid=0（相位专属件）"
    want_rec D2 "$REC/d2.txt" "MYVAR=d2-aot" "env 件回灌到达 --app"

    # D3：AOT --rollback 重放 v1——退化链两腿同 argv（无 --allow-downgrades），
    # apt -y 拒降级 → dpkg -i 原生放行（§2.4 已按此实态口径登记）。
    # 此处如实留证实际走的哪条腿。root 直跑相位——专属 env.d3/rec d3.txt。
    mk_env "$W/env.d3" "$REC/d3.txt" d3-aot
    rc_of "$AOT" pkg-apply --rollback --pkg "$W/v1.deb" --format deb --app "$APP" \
        --env-file "$W/env.d3" --log "$EV/d3-aot.log"
    expect_rc D3 0 $?
    expect_ver D3 1.0 "rollback 重放旧包账本回到 v1"
    want_rec D3 "$REC/d3.txt" "uid=0" "root 直跑语境探针 uid=0（相位专属件）"
    want_rec D3 "$REC/d3.txt" "MYVAR=d3-aot" "env 件回灌到达 --app"
    if grep -qF "apt-get failed" "$EV/d3-aot.log" 2>/dev/null && \
       grep -qF "via dpkg" "$EV/d3-aot.log" 2>/dev/null; then
        info "D3 降级实走 dpkg 退化腿（apt -y 缺 --allow-downgrades 拒装旧版）"
    elif grep -qF "via apt-get" "$EV/d3-aot.log" 2>/dev/null && ! grep -qF "via dpkg" "$EV/d3-aot.log" 2>/dev/null; then
        info "D3 降级由 apt-get 直装完成"
    fi

    # D4：sh 支路 + 无 apt-get shim（退化 dpkg -i）+ su 降权腿（shim 不给 setpriv/sudo/runuser）
    makeshim "$W/shim-su" $BASETOOLS $DPKGTOOLS su
    mk_env "$W/env.d4" "$REC/d4.txt" d4-sh "$T_UID"
    rc_of env PATH="$W/shim-su" SUDO_UID="$T_UID" SUDO_GID="$T_GID" \
        /bin/sh "$SHIMPL" pkg-apply --pkg "$W/v2.deb" --format deb --app "$APP" \
        --env-file "$W/env.d4" --log "$EV/d4-sh.log"
    expect_rc D4 0 $?
    expect_ver D4 2.0 "sh 支路 dpkg 退化腿真装"
    want_line D4 "$EV/d4-sh.log" "apt-get not found — skipping" "缺 apt 退化证据"
    want_line D4 "$EV/d4-sh.log" "via dpkg" "dpkg -i 腿被选用"
    want_rec D4 "$REC/d4.txt" "uid=$T_UID" "su 降权腿重启探针 uid=tester"

    # D5：sh 支路 + sudo 降权腿（shim 只给 sudo 不给 setpriv/runuser/su）v2→v3
    makeshim "$W/shim-sudo" $BASETOOLS $DPKGTOOLS sudo
    mk_env "$W/env.d5" "$REC/d5.txt" d5-sh "$T_UID"
    rc_of env PATH="$W/shim-sudo" SUDO_UID="$T_UID" SUDO_GID="$T_GID" \
        /bin/sh "$SHIMPL" pkg-apply --pkg "$W/v3.deb" --format deb --app "$APP" \
        --env-file "$W/env.d5" --log "$EV/d5-sh.log"
    expect_rc D5 0 $?
    expect_ver D5 3.0 "sh 支路 v3"
    want_rec D5 "$REC/d5.txt" "uid=$T_UID" "sudo -u 降权腿重启探针 uid=tester"
    want_rec D5 "$REC/d5.txt" "MYVAR=d5-sh" "sudo env_reset 下 env 件经内层 env 回灌"

    # D6：AOT root 直跑（无 SUDO_UID=提权后语境基线形）——--rollback 重放 v1，重启不降权。
    # minimal-env 相位（无 --env-file）探针落共享 default.txt——先清场把
    # uid=0 证据封闭到本相位（此前相位各有专属 rec 件，此处清完只剩本相位写入）。
    rm -f "$REC/default.txt"
    rc_of "$AOT" pkg-apply --rollback --pkg "$W/v1.deb" --format deb --app "$APP" \
        --log "$EV/d6-aot.log"
    expect_rc D6 0 $?
    expect_ver D6 1.0 "root 直跑 rollback"
    want_rec D6 "$REC/default.txt" "uid=0" "无 SUDO_UID 时按当前权限（root）直接 spawn"

    # D7：AOT rc6——--app 指目录件 → 安装已提交但重启失败（账本确已翻页）
    rc_of "$AOT" pkg-apply --pkg "$W/v2.deb" --format deb --app /etc --log "$EV/d7-aot.log"
    expect_rc D7 6 $?
    expect_ver D7 2.0 "rc6 时安装已提交（账本=2.0）"

    # D8：sh 支路 rc6 同断言
    rc_of /bin/sh "$SHIMPL" pkg-apply --pkg "$W/v3.deb" --format deb --app /etc \
        --log "$EV/d8-sh.log"
    expect_rc D8 6 $?
    expect_ver D8 3.0 "sh 支路 rc6 安装已提交（账本=3.0）"
    # 回摆 v1 基线（留尾账本=v1）；conffile 已被用户编辑——裸 dpkg -i 会交互
    # prompt 撞 stdin EOF，须同实现口径给 noninteractive+confold。
    DEBIAN_FRONTEND=noninteractive dpkg -i --force-confdef --force-confold \
        "$W/v1.deb" >>"$CONSOLE" 2>&1 || true

    # D9：包管理器全缺席 → rc5（AOT 与 sh 双腿，shim 内不给 apt-get/dpkg；
    # 双腿分相 id -AOT/-SH，RESULTS 直读是哪条实现过的）
    makeshim "$W/shim-none" $BASETOOLS
    rc_of env PATH="$W/shim-none" "$AOT" pkg-apply --pkg "$W/v2.deb" --format deb --app "$APP"
    expect_rc D9-AOT 5 $?
    rc_of env PATH="$W/shim-none" /bin/sh "$SHIMPL" pkg-apply --pkg "$W/v2.deb" --format deb --app "$APP"
    expect_rc D9-SH 5 $?
}

# ============================ fedora 腿 ============================
# 场景面：dnf install 腿 + PKEXEC_UID 分支（R1）、%config(noreplace) 更新（R2）、
# --rollback --oldpackage 降级（R3）、shim-rpm 降级+降权器缺席 rc6（R3B）、
# 缺 dnf/zypper 退化 rpm -U + runuser 降权（R4）、root 直跑（R5）、rc6（R6）、
# rc5 全缺席（R7-AOT/R7-SH）。

leg_rpm() {
    note "== rpm 腿环境 =="
    command -v rpmbuild >/dev/null 2>&1 || {
        dnf install -y rpm-build >>"$CONSOLE" 2>&1 || { fail rpm-env "rpm-build 安装失败"; return; }
    }
    command -v dnf >/dev/null 2>&1 || fail rpm-env "dnf 缺席"
    if command -v zypper >/dev/null 2>&1; then info "rpm-env zypper 在场（意外）"; else info "rpm-env zypper 缺席（预期——退化腿真跑）"; fi
    command -v useradd >/dev/null 2>&1 || dnf install -y shadow-utils >>"$CONSOLE" 2>&1 || true
    useradd -m tester 2>/dev/null || true
    T_UID=$(id -u tester 2>/dev/null || true); T_GID=$(id -g tester 2>/dev/null || true)
    [ -n "$T_UID" ] && [ -n "$T_GID" ] || { fail rpm-env "tester 用户创建失败"; return; }
    write_probe
    mk_rpm 1.0 "$W/v1.rpm" && mk_rpm 2.0 "$W/v2.rpm" && mk_rpm 3.0 "$W/v3.rpm" \
        && pass "rpm-env 三包产出" || { fail rpm-env "rpmbuild 产包失败"; return; }

    rpm -i "$W/v1.rpm" >>"$CONSOLE" 2>&1 && pass "rpm-env v1 基线直装" || { fail rpm-env "v1 基线装失败"; return; }
    snap base-v1
    want_line rpm-env "$STAMPS" "post:" "%post scriptlet 已跑"

    # R1：AOT + PKEXEC_UID（pkexec 语境 env 名分支）→ dnf 腿 + 降权 + env 回灌
    mk_env "$W/env.r1" "$REC/r1.txt" r1-aot "$T_UID"
    rc_of env PKEXEC_UID="$T_UID" "$AOT" pkg-apply \
        --pkg "$W/v2.rpm" --format rpm --app "$APP" \
        --env-file "$W/env.r1" --log "$EV/r1-aot.log"
    expect_rc R1 0 $?
    expect_ver R1 2.0-1 "dnf install ./x.rpm 腿真装 v2"
    want_line R1 "$EV/r1-aot.log" "via dnf" "dnf 腿被选用"
    want_rec R1 "$REC/r1.txt" "uid=$T_UID" "PKEXEC_UID 取回降权 uid=tester"
    want_rec R1 "$REC/r1.txt" "MYVAR=r1-aot" "env 回灌到达 --app"

    # R2：AOT %config(noreplace)——用户改 conf 后 v2→v3：保留用户件，新件落 .rpmnew。
    # root 直跑相位——专属 env.r2/rec r2.txt 把探针记录封在本相位。
    echo "# user-edit" >>"$CONF"
    mk_env "$W/env.r2" "$REC/r2.txt" r2-aot
    rc_of "$AOT" pkg-apply --pkg "$W/v3.rpm" --format rpm --app "$APP" \
        --env-file "$W/env.r2" --log "$EV/r2-aot.log"
    expect_rc R2 0 $?
    expect_ver R2 3.0-1 "conffile 场景更新"
    want_line R2 "$CONF" "# user-edit" "%config(noreplace) 用户修改保留"
    want_file R2 "$CONF.rpmnew" "%config(noreplace) 新配置落 .rpmnew"
    want_rec R2 "$REC/r2.txt" "uid=0" "root 直跑语境探针 uid=0（相位专属件）"
    want_rec R2 "$REC/r2.txt" "MYVAR=r2-aot" "env 件回灌到达 --app"

    # R3：AOT --rollback v3→v1——dnf 对本地旧版件的裁决以实跑为准：dnf5 实测
    # 原地降级成功（via dnf）；若环境拒降则走 zypper/rpm 退化。两路都以
    # 账本 1.0-1 收口，跑的是哪条按日志定——两口径都如实留证。
    # root 直跑相位——专属 env.r3/rec r3.txt。
    mk_env "$W/env.r3" "$REC/r3.txt" r3-aot
    rc_of "$AOT" pkg-apply --rollback --pkg "$W/v1.rpm" --format rpm --app "$APP" \
        --env-file "$W/env.r3" --log "$EV/r3-aot.log"
    expect_rc R3 0 $?
    expect_ver R3 1.0-1 "rollback 降级回 v1"
    want_rec R3 "$REC/r3.txt" "uid=0" "root 直跑语境探针 uid=0（相位专属件）"
    want_rec R3 "$REC/r3.txt" "MYVAR=r3-aot" "env 件回灌到达 --app"
    if grep -qF "via rpm" "$EV/r3-aot.log" 2>/dev/null; then
        want_line R3 "$EV/r3-aot.log" "zypper not found" "zypper 缺席退化证据"
    else
        want_line R3 "$EV/r3-aot.log" "via dnf" "dnf 原地降级腿选用"
    fi

    # R3B：shim 只给 rpm（dnf/zypper 缺席）——证 `rpm -U --oldpackage` 降级链：
    # rpm -U 装旧件缺 --oldpackage 会报 "newer already installed" 非零，账本回到
    # 1.0-1 即降级链真走通的证据。相位 id 独立于 R3（不再覆写 ledger-R3.txt）。
    # 前提：账本从 R3 的 v1 回摆 v3——重置步显式断 rc+账本，前提不成立即 FAIL，
    # 不让后续断言跑在错误前提上假绿。
    # 实测语义：SUDO_UID 在场但 shim 内无任何降权器（setpriv/sudo/runuser/su 全缺）
    # → 重启失败 rc6——安装已提交（账本翻页）但 app 未重启，rc 与账本自洽。
    rpm -U "$W/v3.rpm" >>"$CONSOLE" 2>&1 \
        && pass "R3B-pre 前提 rpm -U 回摆 v3" || fail "R3B-pre" "rpm -U v3 前提回摆失败"
    expect_ver R3B-pre 3.0-1 "前提账本=v3"
    makeshim "$W/shim-rpm" $BASETOOLS rpm
    rc_of env PATH="$W/shim-rpm" SUDO_UID="$T_UID" SUDO_GID="$T_GID" "$AOT" pkg-apply \
        --rollback --pkg "$W/v1.rpm" --format rpm --app "$APP" \
        --log "$EV/r3b-aot.log"
    expect_rc R3B 6 $?
    expect_ver R3B 1.0-1 "shim-rpm 腿 --oldpackage 降级（rc6=安装提交+降权器缺席重启失败）"
    want_line R3B "$EV/r3b-aot.log" "zypper not found" "zypper 缺席退化证据"
    want_line R3B "$EV/r3b-aot.log" "via rpm" "rpm -U 退化腿选用"

    # R4：sh 支路 + runuser 降权腿（shim 给 rpm+runuser，不给 dnf/zypper/setpriv/sudo/su）
    makeshim "$W/shim-runuser" $BASETOOLS rpm runuser
    mk_env "$W/env.r4" "$REC/r4.txt" r4-sh "$T_UID"
    rc_of env PATH="$W/shim-runuser" SUDO_UID="$T_UID" SUDO_GID="$T_GID" \
        /bin/sh "$SHIMPL" pkg-apply --pkg "$W/v2.rpm" --format rpm --app "$APP" \
        --env-file "$W/env.r4" --log "$EV/r4-sh.log"
    expect_rc R4 0 $?
    expect_ver R4 2.0-1 "sh 支路 rpm -U 退化腿真装"
    want_line R4 "$EV/r4-sh.log" "dnf not found — skipping" "缺 dnf 退化证据"
    want_line R4 "$EV/r4-sh.log" "via rpm" "rpm -U 腿被选用"
    want_rec R4 "$REC/r4.txt" "uid=$T_UID" "runuser 降权腿重启探针 uid=tester"

    # R5：AOT root 直跑 --rollback（无 uid env=按当前权限 spawn）——minimal-env
    # 相位探针落共享 default.txt，先清场把 uid=0 证据封闭到本相位。
    rm -f "$REC/default.txt"
    rc_of "$AOT" pkg-apply --rollback --pkg "$W/v1.rpm" --format rpm --app "$APP" \
        --log "$EV/r5-aot.log"
    expect_rc R5 0 $?
    expect_ver R5 1.0-1 "root 直跑 rollback"
    want_rec R5 "$REC/default.txt" "uid=0" "无 uid env 时按当前权限（root）spawn"

    # R6：AOT rc6——安装已提交重启失败
    rc_of "$AOT" pkg-apply --pkg "$W/v2.rpm" --format rpm --app /etc --log "$EV/r6-aot.log"
    expect_rc R6 6 $?
    expect_ver R6 2.0-1 "rc6 时安装已提交（账本=2.0-1）"
    rpm -e "$PKG" >>"$CONSOLE" 2>&1 || true

    # R7：包管理器全缺席 → rc5（shim 不给 dnf/zypper/rpm；双腿分相 id -AOT/-SH）
    makeshim "$W/shim-none" $BASETOOLS
    rc_of env PATH="$W/shim-none" "$AOT" pkg-apply --pkg "$W/v2.rpm" --format rpm --app "$APP"
    expect_rc R7-AOT 5 $?
    rc_of env PATH="$W/shim-none" /bin/sh "$SHIMPL" pkg-apply --pkg "$W/v2.rpm" --format rpm --app "$APP"
    expect_rc R7-SH 5 $?
}

# ============================ alpine 腿 ============================
# 场景面：keys 空目录 → --allow-untrusted 真腿（K1a AOT / K1b sh）、keys 非空时
# unsigned 被拒的闸方向（K1N）、--rollback 降级语义实测留证（K2）、签名公钥信任链
# 裸 apk add（K3 sh）、root 直跑（K4）、rc6/降级分叉（K5）、rc5 全缺席（K6）。

leg_apk() {
    note "== apk 腿环境 =="
    # abuild 包供 abuild-tar（apk 段拼装必需——裸 tar 无 checksum pax 头/EOA 口径不合）
    command -v abuild-tar >/dev/null 2>&1 || {
        apk add --no-cache abuild openssl >>"$CONSOLE" 2>&1 || { fail apk-env "abuild/openssl 安装失败"; return; }
    }
    command -v abuild-tar >/dev/null 2>&1 || { fail apk-env "abuild-tar 缺席"; return; }
    command -v openssl >/dev/null 2>&1 || { fail apk-env "openssl 缺席"; return; }
    adduser -D tester 2>/dev/null || true
    T_UID=$(id -u tester 2>/dev/null || true); T_GID=$(id -g tester 2>/dev/null || true)
    [ -n "$T_UID" ] && [ -n "$T_GID" ] || { fail apk-env "tester 用户创建失败"; return; }
    write_probe
    # 签名料：RSA 私钥 + 公钥按 abuild 命名约定落 /etc/apk/keys/<名>.rsa.pub——
    # 签名成员 .SIGN.RSA.<私钥基名>.rsa.pub ↔ keys 目录同名公钥件。
    openssl genrsa -out "$W/syspkg-key.rsa" 2048 >>"$CONSOLE" 2>&1 || { fail apk-env "genrsa 失败"; return; }
    openssl rsa -in "$W/syspkg-key.rsa" -pubout -out "$W/syspkg-key.rsa.rsa.pub" >>"$CONSOLE" 2>&1 \
        || { fail apk-env "公钥导出失败"; return; }
    # mk_apk 尾块恒 rc0（证据留档 || true）——&& 链无判别力，逐件 -s 断言接住产包失败。
    # -s 只证"存在且非空"；签名/未签形态的有效性由下游 K1N（签名闸拒收）与
    # K1B/K3（账本翻页）行为断言兜住，不在此闸重复校验。
    mk_apk 1.0.0-r0 "$W/v1u.apk" 0; mk_apk 2.0.0-r0 "$W/v2u.apk" 0; mk_apk 3.0.0-r0 "$W/v3u.apk" 0
    mk_apk 1.0.0-r0 "$W/v1s.apk" 1; mk_apk 2.0.0-r0 "$W/v2s.apk" 1
    _apk_missing=""
    for _p in v1u v2u v3u v1s v2s; do
        [ -s "$W/$_p.apk" ] || _apk_missing="$_apk_missing $_p.apk"
    done
    [ -z "$_apk_missing" ] && pass "apk-env 五包产出（unsigned×3 + signed×2）" \
        || { fail apk-env "apk 产出件缺失：$_apk_missing"; return; }

    # 降权器实测（前台同步——分离 spawn 的沉默失败在此现形）：
    # alpine busybox 提供 setpriv/su；逐条实跑把真实 argv 可行性钉进 console。
    { echo "== 降权器前台实测 =="
      for _t in setpriv su; do
          echo "  $_t -> $(command -v "$_t" 2>/dev/null || echo absent)"
      done
      if command -v setpriv >/dev/null 2>&1; then
          setpriv --reuid="$T_UID" --regid="$T_GID" --clear-groups env MARK=ok \
              /bin/sh -c 'id -u; echo MARK=$MARK' 2>&1 || echo "  setpriv rc=$?"
      fi
      su -s /bin/sh -c 'id -u' tester 2>&1 || echo "  su rc=$?"
    } >>"$CONSOLE" 2>&1

    # K0：放空 /etc/apk/keys——触发 --allow-untrusted 腿（TOFU 边界实态）
    [ -d /etc/apk/keys ] && mv /etc/apk/keys /etc/apk/keys.stock
    mkdir -p /etc/apk/keys
    apk add --allow-untrusted "$W/v1u.apk" >>"$CONSOLE" 2>&1 \
        && pass "apk-env unsigned v1 基线直装" || { fail apk-env "v1 基线装失败"; return; }
    snap base-v1
    want_line apk-env "$STAMPS" "post-install" ".post-install scriptlet 已跑"

    # K1a：AOT unsigned v1→v2 + SUDO_UID 降权——keys 空目录 → apk add --allow-untrusted
    mk_env "$W/env.k1" "$REC/k1.txt" k1-aot "$T_UID"
    rc_of env SUDO_UID="$T_UID" SUDO_GID="$T_GID" "$AOT" pkg-apply \
        --pkg "$W/v2u.apk" --format apk --app "$APP" \
        --env-file "$W/env.k1" --log "$EV/k1a-aot.log"
    expect_rc K1A 0 $?
    expect_ver K1A 2.0.0-r0 "unsigned 包经 --allow-untrusted 腿真装"
    want_rec K1A "$REC/k1.txt" "uid=$T_UID" "降权重启探针 uid=tester"
    want_rec K1A "$REC/k1.txt" "MYVAR=k1-aot" "env 回灌到达 --app"
    info "K1A keys 空目录 → --allow-untrusted 证据=装成（无该旗标本件必被 apk 拒）"

    # K1b：sh 支路同边界（busybox ash 实跑）——重置账本到 v1 再 unsigned v1→v2。
    # 重置步显式断 rc+账本：前提不成立即 FAIL，后续断言不跑在错误前提上假绿。
    apk del "$PKG" >>"$CONSOLE" 2>&1 \
        && pass "K1B-pre 前提 apk del 清账本" || fail "K1B-pre" "apk del 前提失败"
    apk add --allow-untrusted "$W/v1u.apk" >>"$CONSOLE" 2>&1 \
        && pass "K1B-pre 前提 v1 基线直装" || fail "K1B-pre" "v1 基线装失败"
    expect_ver K1B-pre 1.0.0-r0 "前提账本=v1"
    mk_env "$W/env.k1b" "$REC/k1b.txt" k1b-sh "$T_UID"
    rc_of env SUDO_UID="$T_UID" SUDO_GID="$T_GID" /bin/sh "$SHIMPL" pkg-apply \
        --pkg "$W/v2u.apk" --format apk --app "$APP" \
        --env-file "$W/env.k1b" --log "$EV/k1b-sh.log"
    expect_rc K1B 0 $?
    expect_ver K1B 2.0.0-r0 "sh 支路 unsigned --allow-untrusted 腿真装"
    want_rec K1B "$REC/k1b.txt" "uid=$T_UID" "sh 支路降权重启探针 uid=tester"

    # K1N：负面对照——keys 非空后 unsigned 新包应被 apk 签名闸拒（rc=4 账本不翻页）。
    # 专属 env.k1n/rec k1n.txt：rc4 无重启探针不写 rec；env 件未被消费成会话
    # env，但双侧清理是全出口统一执行（finally→CleanupEnvFile / _pa_env_cleanup）。
    cp "$W/syspkg-key.rsa.rsa.pub" /etc/apk/keys/
    mk_env "$W/env.k1n" "$REC/k1n.txt" k1n-aot
    rc_of "$AOT" pkg-apply --pkg "$W/v3u.apk" --format apk --app "$APP" \
        --env-file "$W/env.k1n" --log "$EV/k1n-aot.log"
    _kn_rc=$?
    if [ "$_kn_rc" -eq 4 ] && [ "$(ledger_ver)" = "2.0.0-r0" ]; then
        pass "K1N keys 非空时 unsigned 包被 apk 签名闸拒（rc=4，账本未翻页）"
    else
        fail "K1N" "unsigned+keys 非空 rc=$_kn_rc 账本=$(ledger_ver)（预期 rc=4 且账本=2.0.0-r0）"
    fi
    snap k1n

    # K2：AOT --rollback 重放签名 v1——观察 apk 对旧版本地件的降级语义（special-acceptance 在册项）。
    # 自洽结局都算证据：rc0+账本 v1=原生放行；rc≠0+账本守 v2=拒绝降级；
    # rc0+账本守 v2=受理但 no-op 不翻页。只有 rc 与账本互斥才 FAIL。
    # 专属 env.k2/rec k2.txt：rc0 放行时 root 直跑探针记录落本相位件，不串 default.txt。
    mk_env "$W/env.k2" "$REC/k2.txt" k2-aot
    rc_of "$AOT" pkg-apply --rollback --pkg "$W/v1s.apk" --format apk --app "$APP" \
        --env-file "$W/env.k2" --log "$EV/k2-aot.log"
    _k2_rc=$?
    _k2_v=$(ledger_ver)
    if [ "$_k2_rc" -eq 0 ] && [ "$_k2_v" = "1.0.0-r0" ]; then
        pass "K2 apk 降级语义=原生放行（rc=0，账本=1.0.0-r0）"
    elif [ "$_k2_rc" -ne 0 ] && [ "$_k2_v" = "2.0.0-r0" ]; then
        pass "K2 apk 降级语义=拒绝旧版重放（rc=${_k2_rc}，账本守 2.0.0-r0）"
    elif [ "$_k2_rc" -eq 0 ] && [ "$_k2_v" = "2.0.0-r0" ]; then
        pass "K2 apk 降级语义=受理但不翻页（rc=0 no-op，账本守 2.0.0-r0）"
    else
        fail "K2" "rollback rc=${_k2_rc} 账本=${_k2_v} —— 账本与 rc 不自洽"
    fi
    snap k2

    # K3：sh 支路 + 签名信任链——keys 有预置公钥 → 裸 `apk add` 腿（busybox ash 实跑）。
    # 重置步显式断 rc+账本（同 K1B 口径）。
    apk del "$PKG" >>"$CONSOLE" 2>&1 \
        && pass "K3-pre 前提 apk del 清账本" || fail "K3-pre" "apk del 前提失败"
    apk add --allow-untrusted "$W/v1u.apk" >>"$CONSOLE" 2>&1 \
        && pass "K3-pre 前提 v1 基线直装" || fail "K3-pre" "v1 基线装失败"
    expect_ver K3-pre 1.0.0-r0 "前提账本=v1"
    mk_env "$W/env.k3" "$REC/k3.txt" k3-sh "$T_UID"
    rc_of env SUDO_UID="$T_UID" SUDO_GID="$T_GID" /bin/sh "$SHIMPL" pkg-apply \
        --pkg "$W/v2s.apk" --format apk --app "$APP" \
        --env-file "$W/env.k3" --log "$EV/k3-sh.log"
    expect_rc K3 0 $?
    expect_ver K3 2.0.0-r0 "sh 支路签名包经裸 apk add 真装（公钥验签过）"
    want_rec K3 "$REC/k3.txt" "uid=$T_UID" "busybox su/setpriv 降权腿 uid=tester"
    want_rec K3 "$REC/k3.txt" "MYVAR=k3-sh" "sh 支路 env 回灌到达 --app"

    # K4：AOT root 直跑（无 uid env）重放 v2s——当前权限直接 spawn。minimal-env
    # 相位探针落共享 default.txt，先清场把 uid=0 证据封闭到本相位。
    rm -f "$REC/default.txt"
    rc_of "$AOT" pkg-apply --pkg "$W/v2s.apk" --format apk --app "$APP" --log "$EV/k4-aot.log"
    expect_rc K4 0 $?
    want_rec K4 "$REC/default.txt" "uid=0" "无 uid env 时按当前权限（root）spawn"

    # K5：AOT rc6 分叉——旧版重放若被 apk 拒则 rc4（与 K2 互证），若放行则 rc6（已提交重启失败）
    rc_of "$AOT" pkg-apply --pkg "$W/v1s.apk" --format apk --app /etc --log "$EV/k5-aot.log"
    _k5_rc=$?
    if [ "$_k5_rc" -eq 6 ]; then
        pass "K5 rc6 重启失败面（账本=$(ledger_ver)，安装已提交）"
    elif [ "$_k5_rc" -eq 4 ]; then
        pass "K5 apk 拒旧版重放 → rc4（与 K2 降级观察互证）"
    else
        fail "K5" "rc=${_k5_rc} —— 预期 6（已提交重启失败）或 4（降级被拒）"
    fi
    snap k5

    # K6：包管理器缺席 → rc5（shim 不给 apk；双腿分相 id -AOT/-SH）
    makeshim "$W/shim-none" $BASETOOLS
    rc_of env PATH="$W/shim-none" "$AOT" pkg-apply --pkg "$W/v2s.apk" --format apk --app "$APP"
    expect_rc K6-AOT 5 $?
    rc_of env PATH="$W/shim-none" /bin/sh "$SHIMPL" pkg-apply --pkg "$W/v2s.apk" --format apk --app "$APP"
    expect_rc K6-SH 5 $?
}

# ============================ 入口 ============================

note "== syspkg-verify 腿=${LEG} =="
{ echo "date: $(date -u '+%Y-%m-%d %H:%M:%S UTC' 2>/dev/null || date)"
  echo "uname: $(uname -a)"
  echo "os-release:"; sed -n '1,6p' /etc/os-release 2>/dev/null
  echo "aot: $AOT"
  echo "sh : $SHIMPL"
  echo "id : $(id)"
} >>"$EV/env.txt"
cat /etc/os-release >>"$CONSOLE" 2>/dev/null || true
for _t in dpkg dpkg-deb apt-get dnf zypper rpm rpmbuild apk setpriv sudo runuser su nohup env openssl; do
    _p=$(command -v "$_t" 2>/dev/null || true)
    echo "tool ${_t}: ${_p:-缺失}" >>"$EV/env.txt"
done

if [ ! -f "$AOT" ]; then fail env "AOT 引导件缺席：${AOT}"; note "FAILS=${FAILS}"; exit 1; fi
if [ ! -f "$SHIMPL" ]; then fail env "sh 降级件缺席：${SHIMPL}"; note "FAILS=${FAILS}"; exit 1; fi

# musl 静态件在本容器的真跑证明（参数错→rc3 属协议内预期，证明 exec 通了）
"$AOT" pkg-apply --pkg /nonexistent --format "$LEG" --app /nonexistent-app >>"$CONSOLE" 2>&1
_probe_rc=$?
if [ "$_probe_rc" -eq 3 ]; then pass "env AOT 件在本容器可跑（rc3=参数面实跑）"; else fail env "AOT 件首跑 rc=${_probe_rc}（预期 3）"; fi

case "$LEG" in
    deb) leg_deb ;;
    rpm) leg_rpm ;;
    apk) leg_apk ;;
esac

# 探针记录落证据件——分离 spawn 的写面在 /tmp，收尾拷进工件。
{ cp -r "$REC" "$EV/rec" 2>/dev/null || true
  echo "== 探针记录目录 =="
  find "$REC" -type f -exec sh -c 'echo "--- $1"; cat "$1"' _ {} \; 2>/dev/null || true
} >>"$CONSOLE" 2>&1

echo "================================" | tee -a "$RESULTS"
if [ -n "$FAILS" ]; then
    note "结果：失败相位 →${FAILS}"
    exit 1
fi
note "结果：全相位 PASS"
exit 0
