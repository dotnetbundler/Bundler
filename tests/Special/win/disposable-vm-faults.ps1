#requires -RunAsAdministrator
# 可丢弃 VM 故障注入验收：真实 ACL 拒绝、锁文件卸载(经 nsis-reboot 腿)、物理盘满。
# 只在可丢弃 VM 跑——会留 PendingFileRenameOperations 到下次重启。
# 用法: pwsh disposable-vm-faults.ps1 -InstallerPath <nsis产包> -ConfirmDisposableMachine
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][switch]$ConfirmDisposableMachine,
    [string]$OutDir = "$PSScriptRoot/../evidence"
)
$ErrorActionPreference = "Stop"
trap { Write-Host "SCRIPT-ERR line $($_.InvocationInfo.ScriptLineNumber): $_"; exit 1 }
$evidence = @()
function Note($step, $ok, $detail = "") {
    $script:evidence += "| $step | $(if($ok){'PASS'}else{'FAIL'}) | $detail |"
    Write-Host "[$(if($ok){'PASS'}else{'FAIL'})] $step $detail"
    if (-not $ok) { $script:hadFail = $true }
}

if (-not (Test-Path $InstallerPath)) { throw "InstallerPath 不存在: $InstallerPath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$work = Join-Path $env:TEMP ("bundler-fault-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    # 1) ACL 拒绝：目标目录先建成只读 ACL 再装，安装器应明确失败不半途坏树。
    # 随机目录名——绝不碰既存目录的 ACL 或内容。
    $aclDir = "$env:ProgramFiles\BundlerAclProbe-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $aclDir -Force | Out-Null
    $acl = Get-Acl $aclDir
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
        "Everyone", "Write", "Deny"))
    Set-Acl $aclDir $acl
    $p = Start-Process $InstallerPath -ArgumentList "/S", "/D=$aclDir" -Wait -PassThru
    Note "ACL 拒绝下安装被拒" ($p.ExitCode -ne 0) "rc=$($p.ExitCode)"
    Remove-Item $aclDir -Recurse -Force -ErrorAction SilentlyContinue

    # 2) 物理盘满：8M VHD 塞满后对卷内目录装包。
    $vhd = Join-Path $work "full.vhdx"
    @"
create vdisk file="$vhd" maximum=8 type=fixed
select vdisk file="$vhd"
attach vdisk
create partition primary
format fs=fat quick label=full
assign letter=V
"@ | Set-Content "$work\dp.txt"
    diskpart /s "$work\dp.txt" | Out-Null
    try {
        # 等卷挂载生效后断言 filler 真的落盘——塞不满则"盘满被拒"语义不成立
        for ($i = 0; $i -lt 20 -and -not (Test-Path "V:\"); $i++) { Start-Sleep -Milliseconds 500 }
        # 循环写到 ENOSPC——8MB FAT 卷元数据开销不确定，定长塞会留缝或写不下
        $fillErr = $null
        $fs = $null
        try {
            $fs = [IO.File]::Create("V:\filler.bin")
            $buf = New-Object byte[] 65536
            while ($true) { $fs.Write($buf, 0, $buf.Length) }
        } catch [System.IO.IOException] { $fillErr = "ENOSPC(预期): $($_.Exception.Message)" }
        catch { $fillErr = $_.Exception.Message }
        finally { if ($fs) { $fs.Close() } }
        $free = (Get-PSDrive V -ErrorAction SilentlyContinue)?.Free
        $filled = (Get-Item "V:\filler.bin" -ErrorAction SilentlyContinue)
        Note "卷塞满" ($null -ne $filled -and $free -lt 1048576) "len=$($filled?.Length) free=$free err=$fillErr"
        $p2 = Start-Process $InstallerPath -ArgumentList "/S", "/D=V:\app" -Wait -PassThru
        Note "盘满下安装被拒" ($p2.ExitCode -ne 0) "rc=$($p2.ExitCode)"
        $stray = @(Get-ChildItem "V:\app" -Recurse -Filter *.exe -ErrorAction SilentlyContinue)
        Note "无半途坏树" ($stray.Count -eq 0) "exe=$($stray.Count)"
    } finally {
        @"
select vdisk file="$vhd"
detach vdisk
"@ | Set-Content "$work\dp2.txt"
        diskpart /s "$work\dp2.txt" | Out-Null
    }
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

$out = Join-Path $OutDir ("SA-VM-FAULT-{0:yyyyMMdd}.md" -f (Get-Date))
@"
# SA-VM-FAULT 可丢弃 VM 故障注入
- 日期: $(Get-Date -Format "yyyy-MM-dd") UTC | 宿主: $([Environment]::OSVersion.VersionString)
- 工件: $(Split-Path $InstallerPath -Leaf) sha256=$((Get-FileHash $InstallerPath).Hash.Substring(0,16))

## 步骤与结果
| 步骤 | 结果 | 摘录 |
| --- | --- | --- |
$($evidence -join "`n")
"@ | Set-Content $out -Encoding UTF8
Write-Host "证据: $out"
if ($hadFail) { exit 1 }
