# 联合交换消费腿（Windows）：pwsh -File consume-windows.ps1 -Dir <produce-artifact-dir> [-ArchiveFormatOnly]
# 消费断言只依赖下载的 produce 工件（不依赖构建树/产物）：SHA256SUMS 验同一件 → NSIS/MSI/zip/tar.gz 真装真跑真卸 → 清场。
# -ArchiveFormatOnly=跨 OS 归档交换模式：只验 archive 目录的 zip/tar.gz，做格式级
# 断言（Expand-Archive/tar -xzf 解包 + 结构 + 文本标记）——内嵌 fixture 是 RID 绑定
# 原生件，异 OS 不能"跑"只能"解+验结构"，不执行二进制；该模式常规不需管理员——
# 唯一例外：若归档内含符号链接，Windows 上 bsdtar 建链需提升态或开发者模式
# （CI runner 提升态无碍）。
param([Parameter(Mandatory = $true)][string]$Dir, [switch]$ArchiveFormatOnly)
$ErrorActionPreference = "Stop"
# 完整腿（NSIS/MSI 真装真卸）需管理员——等价原 #requires -RunAsAdministrator 门禁，
# 改为运行期判定以便归档格式交换腿可在非提升环境跑（只解包不断言系统态）。
if (-not $ArchiveFormatOnly -and
    -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "完整消费腿需管理员权限运行（-ArchiveFormatOnly 格式交换腿不需要）"
}
$DIR = (Resolve-Path $Dir).Path
$SUMS = "$DIR/SHA256SUMS"
if (-not (Test-Path $SUMS)) { throw "缺 SHA256SUMS：$DIR" }

# 清单校验（格式 "sha256  ./relpath"，与 produce.ps1 生成约定一致）——
# 畸形行与清单外文件同判失败：只验列名件会让产侧混入/夹带漏检。
$bad = @(); $malformed = @(); $listed = @()
Get-Content $SUMS | ForEach-Object {
    if ($_ -match '^([0-9a-f]{64})  \./(.+)$') {
        $listed += $Matches[2]
        # 清单路径可含 [] 等字符——按通配解析会失配/多配，须 -LiteralPath 字面取件
        $actual = (Get-FileHash -LiteralPath "$DIR/$($Matches[2])" -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $Matches[1]) { $bad += $Matches[2] }
    } elseif ($_.Trim() -ne '') { $malformed += $_ }
}
if ($bad.Count -gt 0) { throw "清单校验失败: $($bad -join ', ')" }
if ($malformed.Count -gt 0) { throw "清单畸形行: $($malformed -join ', ')" }
$extra = Get-ChildItem $DIR -Recurse -File |
    ForEach-Object { $_.FullName.Substring($DIR.Length + 1).Replace('\', '/') } |
    Where-Object { $_ -ne 'SHA256SUMS' -and $_ -notin $listed }
if ($extra.Count -gt 0) { throw "清单外文件: $($extra -join ', ')" }

# 应收件存在性对账：本宿主 produce 腿应收 nsis/msi/archive 三件——
# 缺件或目录空即判败，防产侧布局漂移时"全绿零断言"。
# 归档格式交换模式只验 archive——异 OS 产件其余目录无本宿主可消费格式，非缺失。
$expected = if ($ArchiveFormatOnly) { @("archive") } else { @("nsis", "msi", "archive") }
$missing = $expected | Where-Object { -not (Test-Path "$DIR/$_") }
if ($missing.Count -gt 0) { throw "产件目录缺失: $($missing -join ', ')" }
$empty = $expected | Where-Object {
    -not (Get-ChildItem "$DIR/$_" -Recurse -File | Select-Object -First 1) }
if ($empty.Count -gt 0) { throw "产件目录空: $($empty -join ', ')" }

# /D= 与 _?= 走 NSIS 自有命令行解析——混入正斜杠会被吞成粘连路径而静默装偏，全程纯反斜杠。
$WORK = Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }) "bundler-consume"
New-Item -ItemType Directory -Force $WORK | Out-Null
$failedLegs = @()
$legsRan = 0   # 实际执行腿数——零断言全绿由末段防线判败（与 consume-macos/consume-linux 同口径）
function Leg($name, [scriptblock]$body) {
    $script:legsRan++
    try { & $body; Write-Host "[LEG-PASS] $name" }
    catch { Write-Host "[LEG-FAIL] $name :: $_"; $script:failedLegs += $name }
}

if ($ArchiveFormatOnly) {
# ---- 跨 OS 归档交换：格式级断言（不执行异 RID 二进制）----
# 每份归档独立解包后断言：单一顶层目录 + BundlerArchiveIntegrationFixture 主件
# 在位 + runtimeconfig.json 文本标记（"runtimeOptions"）可读。缺格式即判败。
function Assert-ArchiveTree($root) {
    # __MACOSX 目录与 ._* AppleDouble 旁枝同是 macOS 元数据，macOS 侧产件可能出现——不计入顶层断言。
    $top = @(Get-ChildItem $root -Force | Where-Object { $_.Name -ne "__MACOSX" -and $_.Name -notlike "._*" })
    if ($top.Count -ne 1 -or -not $top[0].PSIsContainer) { throw "归档顶层非单一目录（顶层 $($top.Count) 项）" }
    $d = $top[0].FullName
    $exe = Get-ChildItem $d -File |
        Where-Object { $_.Name -in @("BundlerArchiveIntegrationFixture", "BundlerArchiveIntegrationFixture.exe") }
    if (-not $exe) { throw "归档内无主程序" }
    $j = "$d\BundlerArchiveIntegrationFixture.runtimeconfig.json"
    if (-not (Test-Path $j) -or (Get-Item $j).Length -eq 0) { throw "runtimeconfig.json 缺失/为空" }
    if (-not (Select-String -Path $j -Pattern '"runtimeOptions"' -Quiet)) { throw "runtimeconfig.json 缺 runtimeOptions 文本标记" }
}

$zips = @(Get-ChildItem "$DIR/archive" -Filter *.zip -Recurse -ErrorAction SilentlyContinue)
if ($zips.Count -eq 0) { Write-Host "[LEG-FAIL] archive-zip-xchg（产件目录无 .zip）"; $failedLegs += "archive-zip-xchg" }
else {
    Leg "archive-zip-xchg" {
        $i = 0
        foreach ($z in $zips) {
            $t = "$WORK\xchg-zip-$i"; $i++
            Expand-Archive $z.FullName -DestinationPath $t -Force
            Assert-ArchiveTree $t
        }
    }
}
$tgzs = @(Get-ChildItem "$DIR/archive" -Filter *.tar.gz -Recurse -ErrorAction SilentlyContinue)
if ($tgzs.Count -eq 0) { Write-Host "[LEG-FAIL] archive-targz-xchg（产件目录无 .tar.gz）"; $failedLegs += "archive-targz-xchg" }
else {
    Leg "archive-targz-xchg" {
        $i = 0
        foreach ($a in $tgzs) {
            $t = "$WORK\xchg-targz-$i"; $i++
            New-Item -ItemType Directory -Force $t | Out-Null
            # 钉 System32 的 bsdtar——PATH 里 Git 的 GNU tar 会把 C:\ 误当 host:path 远程语法。
            & "$env:SystemRoot\System32\tar.exe" -xzf $a.FullName -C $t
            if ($LASTEXITCODE -ne 0) { throw "tar rc=$LASTEXITCODE" }
            Assert-ArchiveTree $t
        }
    }
}

} else {

# NSIS：/S /D=<dir> 真装 → 主程序真跑 → Uninstall.exe /S 真卸
$nsis = Get-ChildItem "$DIR/nsis" -Filter *.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $nsis) { Write-Host "[LEG-FAIL] nsis（产件目录无 .exe）"; $failedLegs += "nsis" }
else {
    Leg "nsis" {
        $inst = "$WORK\nsis-inst"
        $p = Start-Process $nsis.FullName -ArgumentList "/S", "/D=$inst" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "installer rc=$($p.ExitCode)" }
        $exe = "$inst\BundlerIntegrationFixture.exe"
        if (-not (Test-Path $exe)) { throw "装后主程序缺位：$exe" }
        $out = & $exe ci-marker
        if ($out -notmatch "DotNet.Bundler integration fixture") { throw "marker 输出异常：$out" }
        # NSIS 卸载器原地直启是 launcher 语义不透传真实 rc——复制出去 + _?= 拿真退出码
        # （与 tests/Integration 的 DirectUninstall 同手法；rc=3010 需重启同样算成功）。
        $uninst = "$inst\Uninstall.exe"
        $copy = "$WORK\nsis-uninstall.exe"
        Copy-Item $uninst $copy
        $p = Start-Process $copy -ArgumentList "/S", "/DELETEAPPDATA", "_?=$inst" -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw "uninstaller rc=$($p.ExitCode)" }
        if (Test-Path $exe) { throw "卸后主程序仍在：$exe" }
    }
}

# MSI：msiexec 静默真装 → 主程序真跑 → /x 真卸
$msi = Get-ChildItem "$DIR/msi" -Filter *.msi -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $msi) { Write-Host "[LEG-FAIL] msi（产件目录无 .msi）"; $failedLegs += "msi" }
else {
    Leg "msi" {
        $log = "$WORK/msi-install.log"
        $p = Start-Process msiexec.exe -ArgumentList "/i", "`"$($msi.FullName)`"", "/qn", "/norestart", "/L*v", "`"$log`"" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "msiexec /i rc=$($p.ExitCode) log=$log" }
        $exe = Get-ChildItem "$env:LOCALAPPDATA/Programs" -Filter "*.exe" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like "*Bundler*Smoke*" -or $_.FullName -like "*bundler*msi*" } |
            Select-Object -First 1
        if (-not $exe) { $exe = Get-ChildItem "${env:ProgramFiles}", "${env:ProgramFiles(x86)}" -Filter "BundlerMsiSmoke*.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 }
        if (-not $exe) { throw "装后找不到主程序" }
        $out = & $exe.FullName
        if ($LASTEXITCODE -ne 0) { throw "主程序 rc=$LASTEXITCODE" }
        $p = Start-Process msiexec.exe -ArgumentList "/x", "`"$($msi.FullName)`"", "/qn", "/norestart" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "msiexec /x rc=$($p.ExitCode)" }
        if (Test-Path $exe.FullName) { throw "卸后主程序仍在：$($exe.FullName)" }
    }
}

# zip/tar.gz 归档（fixture 产两件）：解开直接跑；缺任一件即判败——空 glob 静默跳过等于零断言。
# 口径记账（不对称，有意）：本腿 Select-Object -First 1 只取每格式的头一件跑；
# POSIX 腿（consume-macos/consume-linux 非 xchg 路）把目录内全部归档合解进同一棵树再跑。
# 产侧约定每格式只产一件，差的是"多件的冗余覆盖"而非断言强度——维持现状，审计留痕。
$zip = Get-ChildItem "$DIR/archive" -Filter *.zip -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $zip) { Write-Host "[LEG-FAIL] archive-zip（产件目录无 .zip）"; $failedLegs += "archive-zip" }
else {
    Leg "archive-zip" {
        $t = "$WORK/archive"
        Expand-Archive $zip.FullName -DestinationPath $t -Force
        $exe = Get-ChildItem $t -Filter "BundlerArchiveIntegrationFixture*.exe" -Recurse | Select-Object -First 1
        if (-not $exe) { throw "归档内无主程序" }
        $out = & $exe.FullName ci-marker
        if ($out -notmatch "BundlerArchiveIntegrationFixture:ci-marker") { throw "marker 输出异常：$out" }
    }
}
$tarGz = Get-ChildItem "$DIR/archive" -Filter *.tar.gz -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $tarGz) { Write-Host "[LEG-FAIL] archive-targz（产件目录无 .tar.gz）"; $failedLegs += "archive-targz" }
else {
    Leg "archive-targz" {
        $t = "$WORK/archive-targz"
        New-Item -ItemType Directory -Force $t | Out-Null
        & "$env:SystemRoot\System32\tar.exe" -xzf $tarGz.FullName -C $t
        if ($LASTEXITCODE -ne 0) { throw "tar rc=$LASTEXITCODE" }
        $exe = Get-ChildItem $t -Filter "BundlerArchiveIntegrationFixture*.exe" -Recurse | Select-Object -First 1
        if (-not $exe) { throw "归档内无主程序" }
        $out = & $exe.FullName ci-marker
        if ($out -notmatch "BundlerArchiveIntegrationFixture:ci-marker") { throw "marker 输出异常：$out" }
    }
}

}

# 零腿全绿防线：一条腿都没实际执行到零断言全绿——须判败。
if ($legsRan -eq 0) { Write-Error "无任何腿执行（零断言全绿）" }
if ($failedLegs.Count -gt 0) { Write-Error ("失败腿: " + ($failedLegs -join ", ")) }
Write-Host "=== consume-windows 全绿 ==="
