#Requires -Version 5.1
<#
Dragon Butler · 发布说明抽取脚本

RELEASE_NOTES.md 是发布说明的唯一来源：每个版本一节，形如

    # v1.5.1 清理与文档
    > 已发布：<https://…/releases/tag/v1.5.1>
    ## 变更
    …

本脚本把某一节抠出来，交给 gh release create 使用。自动发版时发布地址还不存在，
所以会丢掉「> 已发布：」那一行（本地手动发版时它由人补回）。

用法
    .\scripts\release-notes.ps1 -Version v1.5.1                 # 打到标准输出
    .\scripts\release-notes.ps1 -Version 1.5.1 -Out notes.md    # 写入文件（UTF-8 无 BOM）
    .\scripts\release-notes.ps1 -Version v1.5.1 -TitleOnly      # 只取标题（去掉 # 前缀）
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [string] $Path,

    [string] $Out,

    [switch] $TitleOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Version.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) { $Version = "v$Version" }
# 默认路径要在这里算：param 默认值求值时 $PSScriptRoot 还是空的（PS 5.1 用 -File 调用时）
if ([string]::IsNullOrWhiteSpace($Path)) {
    $Path = Join-Path (Split-Path -Parent $PSScriptRoot) 'RELEASE_NOTES.md'
}
if (-not (Test-Path -LiteralPath $Path)) { throw "找不到发布说明：$Path" }

$lines = Get-Content -LiteralPath $Path -Encoding UTF8

# 找到这一节的标题行：以 # 开头且含版本号，且版本号后面不是数字（v1.5 不能匹配到 v1.5.1）
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -notmatch '^#\s+') { continue }
    if ($lines[$i] -match ('(?<![\d.])' + [regex]::Escape($Version) + '(?![\d.])')) { $start = $i; break }
}
if ($start -lt 0) { throw "RELEASE_NOTES.md 里找不到 $Version 的标题" }

if ($TitleOnly) {
    Write-Output ($lines[$start] -replace '^#\s*', '').Trim()
    return
}

# 到下一个版本标题为止
$end = $lines.Count - 1
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^#\s+v\d') { $end = $i - 1; break }
}

$body = [System.Collections.Generic.List[string]]::new()
for ($i = $start; $i -le $end; $i++) {
    $line = $lines[$i]
    if ($line -match '^>\s*已发布') { continue }
    # 抽掉「已发布」那行后会留下连续空行，压成一个
    if ([string]::IsNullOrWhiteSpace($line) -and $body.Count -gt 0 -and [string]::IsNullOrWhiteSpace($body[$body.Count - 1])) { continue }
    $body.Add($line)
}
while ($body.Count -gt 0 -and [string]::IsNullOrWhiteSpace($body[$body.Count - 1])) {
    $body.RemoveAt($body.Count - 1)
}
if ($body.Count -eq 0) { throw "$Version 这一节是空的" }

$text = ($body -join "`r`n")
if ($Out) {
    [System.IO.File]::WriteAllText($Out, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "已写出 $Out（$($text.Length) 字符）"
}
else {
    Write-Output $text
}
