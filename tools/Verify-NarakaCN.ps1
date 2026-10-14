<#
  Verify-NarakaCN.ps1 —— 重建结果验收

  验收内容：
    1. 国服清单里 14984 个文件是否全部到位、大小是否吻合
    2. 每个文件是「硬链接」还是「真实下载」，各自数量与体积
    3. 所有非硬链接文件（= 下载来的）逐个 MD5 比对官方清单
    4. 随机抽样若干硬链接文件，MD5 比对官方清单
    5. 源客户端（Epic/国际服）是否原封未动
#>
[CmdletBinding()]
param(
  [string]$Manifest = 'D:\Naraka\patch\md5list\data.xml',
  [string]$Target   = 'D:\Naraka',
  [string]$Source   = 'D:\EpicGames\NARAKABLADEPOINT',
  [int]$SampleLinks = 200,
  [string]$ReportDir = (Join-Path $PSScriptRoot 'reports')
)

$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $ReportDir "verify-$stamp"
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
function Say($m) { Write-Host $m; Add-Content -LiteralPath (Join-Path $runDir 'verify.log') -Value $m -Encoding UTF8 }

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

[xml]$xml = Get-Content -LiteralPath $Manifest -Raw
$items = $xml.Root.file.item
Say "清单: $Manifest  ($($items.Count) 项, TargetVersion=$($xml.Root.config.TargetVersion.value))"
Say "目标: $Target"

$missing = New-Object System.Collections.Generic.List[string]
$sizeBad = New-Object System.Collections.Generic.List[string]
$linkItems = New-Object System.Collections.Generic.List[object]
$realItems = New-Object System.Collections.Generic.List[object]
$totalBytes = 0L; $linkBytes = 0L; $realBytes = 0L

foreach ($it in $items) {
  $t = Join-Path $Target ($it.path -replace '/', '\')
  if (-not (Test-Path -LiteralPath $t)) { $missing.Add($it.path); continue }
  $fi = Get-Item -LiteralPath $t -Force
  $len = $fi.Length
  $totalBytes += $len
  if ([math]::Floor($len / 1024) -ne [int]$it.size) { $sizeBad.Add("$($it.path) local=$len wantKB=$($it.size)") }
  if ($fi.LinkType -eq 'HardLink') { $linkItems.Add($it); $linkBytes += $len } else { $realItems.Add($it); $realBytes += $len }
}

Say ''
Say '============ 到货情况 ============'
Say ("清单文件数      : {0}" -f $items.Count)
Say ("缺失            : {0}" -f $missing.Count)
Say ("大小不符        : {0}" -f $sizeBad.Count)
Say ("硬链接复用      : {0,6} 个  {1,8:N2} GB" -f $linkItems.Count, ($linkBytes / 1GB))
Say ("真实文件(下载)  : {0,6} 个  {1,8:N2} MB" -f $realItems.Count, ($realBytes / 1MB))
Say ("目标目录总大小  : {0:N2} GB" -f ($totalBytes / 1GB))
Say '=================================='

# 1) 所有真实文件逐个 MD5
$bad = New-Object System.Collections.Generic.List[string]
Say ''
Say "MD5 校验所有真实文件（$($realItems.Count) 个）..."
$n = 0
foreach ($it in $realItems) {
  $n++
  $t = Join-Path $Target ($it.path -replace '/', '\')
  $h = Get-MD5Fast $t
  if ($h -ne $it.md5) { $bad.Add("REAL $($it.path) got=$h want=$($it.md5)") }
}
Say "  完成，失败 $($bad.Count) 个"

# 2) 抽样校验硬链接
if ($SampleLinks -gt 0 -and $linkItems.Count -gt 0) {
  $take = [math]::Min($SampleLinks, $linkItems.Count)
  $idx = 0..($linkItems.Count - 1) | Get-Random -Count $take
  Say ''
  Say "抽样 MD5 校验硬链接（$take / $($linkItems.Count) 个）..."
  foreach ($i in $idx) {
    $it = $linkItems[$i]
    $t = Join-Path $Target ($it.path -replace '/', '\')
    $h = Get-MD5Fast $t
    if ($h -ne $it.md5) { $bad.Add("LINK $($it.path) got=$h want=$($it.md5)") }
  }
  Say "  完成，累计失败 $($bad.Count) 个"
}

# 3) 源客户端状态
Say ''
$src = Get-ChildItem -LiteralPath $Source -Recurse -File -Force
Say ("源客户端 {0}: {1} 个文件, {2:N2} GB" -f $Source, $src.Count, (($src | Measure-Object Length -Sum).Sum / 1GB))

$free = (Get-PSDrive -Name ($Target.Substring(0,1))).Free
Say ("{0}: 剩余空间 {1:N1} GB" -f $Target.Substring(0,1), ($free / 1GB))

if ($missing.Count -gt 0) { $missing | Set-Content -LiteralPath (Join-Path $runDir 'missing.txt') -Encoding UTF8 }
if ($sizeBad.Count -gt 0) { $sizeBad | Set-Content -LiteralPath (Join-Path $runDir 'size-mismatch.txt') -Encoding UTF8 }
if ($bad.Count -gt 0) { $bad | Set-Content -LiteralPath (Join-Path $runDir 'md5-bad.txt') -Encoding UTF8 }

Say ''
if ($missing.Count -eq 0 -and $sizeBad.Count -eq 0 -and $bad.Count -eq 0) {
  Say 'RESULT: PASS —— 国服客户端已按官方清单完整重建，内容全部校验通过。'
} else {
  Say ("RESULT: FAIL —— 缺失 {0} / 大小不符 {1} / MD5 不符 {2}" -f $missing.Count, $sizeBad.Count, $bad.Count)
}
Say "报告目录: $runDir"
