<#
  Rebuild-NarakaCN.ps1 —— 「免下载切客户端」实现（永劫无间 国服 重建）

  原理：
    1. 读网易官方清单 D:\Naraka\patch\md5list\data.xml（14984 个文件的 MD5 + 大小）
    2. 在已有的另一份客户端（Epic/国际服）里找同名同大小的文件
    3. 逐个用 MD5 校验，确认内容一致的 → 硬链接（同卷，零额外空间、零下载）
    4. 只有校验不过/缺失的文件（约 34 个、~200 MB）才从网易 CDN 下载
       CDN: https://d90.gdl.netease.com/publish/<TargetVersion>/<path>

  安全设计：
    - 默认 dry-run，只出报告不动文件；确认后加 -Execute
    - 只链接 MD5 与官方清单完全一致的文件，网易启动器后续「修复」不会有东西可改，
      因此也不会原地改写硬链接而伤到源客户端
    - 生成 rollback.ps1，一键撤销本次创建的所有链接与下载

  用法：
    pwsh -File .\Rebuild-NarakaCN.ps1                     # 干跑 + 全量 MD5 校验
    pwsh -File .\Rebuild-NarakaCN.ps1 -Execute             # 真正执行（先干跑过再执行）
    pwsh -File .\Rebuild-NarakaCN.ps1 -SkipHash            # 跳过 MD5（快，但只按大小匹配）
#>
[CmdletBinding()]
param(
  [string]$Manifest = 'D:\Naraka\patch\md5list\data.xml',
  [string]$Source   = 'D:\EpicGames\NARAKABLADEPOINT',
  [string]$Target   = 'D:\Naraka',
  [string]$ReportDir = (Join-Path $PSScriptRoot 'reports'),
  [switch]$Execute,
  [switch]$SkipHash,
  [switch]$ProbeCdn,
  [string]$UsePlan = '',
  [int]$Limit = 0,
  [int]$ProgressEvery = 200
)

$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $ReportDir $stamp
New-Item -ItemType Directory -Path $runDir -Force | Out-Null

function Write-Log([string]$msg) {
  $line = '{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -LiteralPath (Join-Path $runDir 'run.log') -Value $line -Encoding UTF8
}

function Get-MD5Fast([string]$path) {
  $md5 = [System.Security.Cryptography.MD5]::Create()
  try {
    $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
      $buf = New-Object byte[] (4 * 1024 * 1024)
      while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) { [void]$md5.TransformBlock($buf, 0, $n, $null, 0) }
      [void]$md5.TransformFinalBlock([byte[]]::new(0), 0, 0)
      return ([System.BitConverter]::ToString($md5.Hash) -replace '-', '').ToLowerInvariant()
    } finally { $fs.Dispose() }
  } finally { $md5.Dispose() }
}

# ---------- 1. 读清单 ----------
Write-Log "清单: $Manifest"
if (-not (Test-Path -LiteralPath $Manifest)) { throw "找不到清单文件: $Manifest" }
[xml]$xml = Get-Content -LiteralPath $Manifest -Raw
$ver = $xml.Root.config.TargetVersion.value
$cdn = $xml.Root.config.CDNDomain.value.TrimEnd('/')
$items = $xml.Root.file.item
Write-Log ("国服清单: {0} 个文件, TargetVersion={1}, 声明总量={2:N2} GB" -f $items.Count, $ver, ([double]$xml.Root.config.DownloadSizeKB.value / 1MB))
Write-Log "CDN: $cdn/$ver/"

if (-not (Test-Path -LiteralPath $Source)) { throw "找不到源客户端: $Source" }
if (-not (Test-Path -LiteralPath $Target)) { throw "找不到目标安装根: $Target" }

# ---------- 2. 分类 ----------
$plan = New-Object System.Collections.Generic.List[object]
$links = New-Object System.Collections.Generic.List[object]
$downloads = New-Object System.Collections.Generic.List[object]

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$idx = 0

if ($UsePlan) {
  # 复用上一次【已全量 MD5 校验】的 plan.csv，跳过重复哈希
  $rows = Import-Csv -LiteralPath $UsePlan
  $md5map = @{}
  foreach ($it in $items) { $md5map[$it.path] = $it.md5 }
  foreach ($r in $rows) {
    $rec = [pscustomobject]@{
      path = $r.path; action = $r.action; reason = $r.reason
      wantKB = [int64]$r.wantKB; srcBytes = [int64]$r.srcBytes
      target = $r.target; source = $r.source; md5 = $md5map[$r.path]
    }
    $plan.Add($rec)
    if ($rec.action -eq 'link') { $links.Add($rec) } else { $downloads.Add($rec) }
  }
  Write-Log ("从已验证报告装载: {0} 条 (链接 {1} / 下载 {2})  <- {3}" -f $plan.Count, $links.Count, $downloads.Count, $UsePlan)
}
else {
foreach ($it in $items) {
  $idx++
  if ($Limit -gt 0 -and $idx -gt $Limit) { break }

  $rel = $it.path                      # 形如 program/xxx 或 LauncherGame.exe
  $wantKb = [int64]$it.size
  $wantMd5 = $it.md5
  $tFull = Join-Path $Target ($rel -replace '/', '\')

  # 源目录 = 目标 program 目录的内容
  $sFull = $null
  if ($rel -like 'program/*') { $sFull = Join-Path $Source (($rel.Substring(8)) -replace '/', '\') }

  $action = 'download'
  $reason = 'no-source'
  $sLen = -1

  if ($sFull -and (Test-Path -LiteralPath $sFull)) {
    $sLen = (Get-Item -LiteralPath $sFull -Force).Length
    if ([math]::Floor($sLen / 1024) -ne $wantKb) {
      $reason = "size-mismatch(local=$sLen wantKB=$wantKb)"
    } elseif ($SkipHash) {
      $action = 'link'; $reason = 'size-match'
    } else {
      $h = Get-MD5Fast $sFull
      if ($h -eq $wantMd5) { $action = 'link'; $reason = 'md5-match' }
      else { $reason = "md5-mismatch(local=$h want=$wantMd5)" }
    }
  }

  $rec = [pscustomobject]@{
    path = $rel; action = $action; reason = $reason
    wantKB = $wantKb; srcBytes = $sLen; target = $tFull; source = $sFull; md5 = $wantMd5
  }
  $plan.Add($rec)
  if ($action -eq 'link') { $links.Add($rec) } else { $downloads.Add($rec) }

  if ($idx % $ProgressEvery -eq 0) {
    $mbps = if ($sw.Elapsed.TotalSeconds -gt 0) { ($plan | Measure-Object -Property srcBytes -Sum).Sum / 1MB / $sw.Elapsed.TotalSeconds } else { 0 }
    Write-Log ("  进度 {0}/{1}  可链接={2}  待下载={3}  ({4:N0} MB/s)" -f $idx, $items.Count, $links.Count, $downloads.Count, $mbps)
  }
}
}
$sw.Stop()

$linkBytes = ($links | ForEach-Object { [int64]$_.wantKB * 1024 } | Measure-Object -Sum).Sum
$dlBytes   = ($downloads | ForEach-Object { [int64]$_.wantKB * 1024 } | Measure-Object -Sum).Sum

Write-Log ''
Write-Log '================ 结论 ================'
Write-Log ("可硬链接复用 : {0,6} 个文件  {1,9:N2} GB   (零下载)" -f $links.Count, ($linkBytes / 1GB))
Write-Log ("需要下载     : {0,6} 个文件  {1,9:N2} MB  ({2:N2} GB)" -f $downloads.Count, ($dlBytes / 1MB), ($dlBytes / 1GB))
if (-not $SkipHash) { Write-Log ("MD5 校验耗时 : {0:N1} 分钟" -f $sw.Elapsed.TotalMinutes) }
Write-Log '======================================'

# ---------- 3. 报告 ----------
$plan | Export-Csv -LiteralPath (Join-Path $runDir 'plan.csv') -NoTypeInformation -Encoding UTF8
$downloads | Select-Object path, wantKB, reason, @{n='url';e={ "$cdn/$ver/$($_.path)" }} |
  Export-Csv -LiteralPath (Join-Path $runDir 'download.csv') -NoTypeInformation -Encoding UTF8

if ($ProbeCdn -and $downloads.Count -gt 0) {
  Write-Log "探测 CDN 可用性（$($downloads.Count) 个文件）..."
  foreach ($d in $downloads) {
    $u = "$cdn/$ver/$($d.path)"
    try {
      $r = Invoke-WebRequest -Uri $u -Method Head -TimeoutSec 20 -UseBasicParsing
      Write-Log ("  OK  {0,8}  {1}" -f $r.Headers['Content-Length'], $d.path)
    } catch {
      Write-Log ("  ERR {0}  {1}" -f $_.Exception.Message, $d.path)
    }
  }
}

# ---------- 4. 执行 ----------
if (-not $Execute) {
  Write-Log ''
  Write-Log "这是【干跑】。报告目录: $runDir"
  Write-Log "确认无误后执行:  pwsh -File `"$PSCommandPath`" -Execute"
  return
}

Write-Log ''
Write-Log "开始执行：$($links.Count) 个硬链接 + $($downloads.Count) 个下载"
$created = New-Object System.Collections.Generic.List[string]
$failed  = New-Object System.Collections.Generic.List[string]

foreach ($l in $links) {
  try {
    if (-not (Test-Path -LiteralPath $l.source)) { $failed.Add("SRC-MISSING $($l.path)"); continue }
    $curLen = (Get-Item -LiteralPath $l.source -Force).Length
    if ([math]::Floor($curLen / 1024) -ne $l.wantKB) { $failed.Add("SRC-CHANGED $($l.path) len=$curLen wantKB=$($l.wantKB)"); continue }
    $dir = Split-Path -Parent $l.target
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    if (Test-Path -LiteralPath $l.target) { Remove-Item -LiteralPath $l.target -Force }
    New-Item -ItemType HardLink -Path $l.target -Target $l.source -Force | Out-Null
    $created.Add($l.target)
  } catch { $failed.Add("LINK $($l.path) :: $($_.Exception.Message)") }
  if ($created.Count % 2000 -eq 0 -and $created.Count -gt 0) { Write-Log "  已建链接 $($created.Count)/$($links.Count)" }
}
Write-Log ("硬链接完成: 成功 {0} / 失败 {1}" -f $created.Count, $failed.Count)

$wc = New-Object System.Net.WebClient
$wc.Headers.Add('User-Agent', 'Mozilla/5.0')
$dlOk = 0
foreach ($d in $downloads) {
  $u = "$cdn/$ver/$($d.path)"
  $tmp = "$($d.target).dltmp"
  try {
    $dir = Split-Path -Parent $d.target
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $wc.DownloadFile($u, $tmp)
    $h = Get-MD5Fast $tmp
    if ($h -ne $d.md5 -and $d.wantKB -gt 0) {
      $failed.Add("MD5 $($d.path) :: got $h want $($d.md5)")
      Remove-Item -LiteralPath $tmp -Force
      continue
    }
    if (Test-Path -LiteralPath $d.target) { Remove-Item -LiteralPath $d.target -Force }
    Move-Item -LiteralPath $tmp -Destination $d.target -Force
    $created.Add($d.target)
    $dlOk++
    Write-Log ("  下载 {0}/{1}  {2}" -f $dlOk, $downloads.Count, $d.path)
  } catch {
    $failed.Add("DL $($d.path) :: $($_.Exception.Message)")
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
  }
}
$wc.Dispose()
Write-Log ("下载完成: 成功 {0} / 失败 {1}" -f $dlOk, $failed.Count)

# 回滚脚本
$rb = Join-Path $runDir 'rollback.ps1'
$lines = @('# 撤销本次 Rebuild-NarakaCN 创建的文件', '$ErrorActionPreference = ''SilentlyContinue''')
foreach ($p in $created) { $lines += "Remove-Item -LiteralPath '$p' -Force" }
$lines += '# 清理空目录'
$lines += "Get-ChildItem -LiteralPath '$Target' -Recurse -Directory | Sort-Object FullName -Descending | Where-Object { -not (Get-ChildItem -LiteralPath \$_.FullName -Force) } | Remove-Item -Force"
$lines -join "`r`n" | Set-Content -LiteralPath $rb -Encoding UTF8
Write-Log "回滚脚本: $rb"

if ($failed.Count -gt 0) {
  $failed | Set-Content -LiteralPath (Join-Path $runDir 'failures.txt') -Encoding UTF8
  Write-Log "有 $($failed.Count) 项失败，详见 $runDir\failures.txt"
  $failed | Select-Object -First 20 | ForEach-Object { Write-Log "  $_" }
}

$summary = [pscustomobject]@{
  manifest = $Manifest; source = $Source; target = $Target; targetVersion = $ver
  links = $links.Count; linkGB = [math]::Round($linkBytes / 1GB, 2)
  downloads = $downloads.Count; downloadMB = [math]::Round($dlBytes / 1MB, 1)
  created = $created.Count; failed = $failed.Count
  executedAt = (Get-Date).ToString('s')
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDir 'summary.json') -Encoding UTF8
Write-Log '完成。'
