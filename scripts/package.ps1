#Requires -Version 5.1
<#
Dragon Butler · 发布打包脚本 —— 压缩包命名的唯一来源

命名规则
    <Product>-v<Version>-<Runtime>-<Variant>.zip

    Dragon-Butler-v1.2-win-x64-runtime-dependent.zip
    Dragon-Butler-v1.2-win-x64-portable.zip

    Product   固定 Dragon-Butler
    vVersion  取自 csproj 的 <Version>，去掉补位的 .0（1.2.0 -> v1.2）
    Runtime   .NET RID，默认 win-x64（将来可传 win-arm64 / linux-x64 / osx-arm64）
    Variant   runtime-dependent = 框架依赖，需已装 .NET 8 桌面运行时
              portable          = 自包含单文件，自带运行时，免安装

包内顶层目录固定为 <Product>-<Runtime>-<Variant>（不带版本），
便于新版本解压后直接覆盖旧目录升级，config\ 里的设置不会丢。

用法
    .\scripts\package.ps1                        # 构建并打包全部变体
    .\scripts\package.ps1 -Variant portable      # 只打包便携版
    .\scripts\package.ps1 -ZipOnly               # 跳过 publish，只重新打 zip
    .\scripts\package.ps1 -Runtime win-arm64     # 打其他平台

注意
    - 发布前先退出正在运行的 Dragon Butler（否则 exe 被占用，清理旧目录会失败）
    - 产物目录会被整个删除重建，config\ 等运行期数据不要放在里面
#>

[CmdletBinding()]
param(
    [ValidateSet('all', 'runtime-dependent', 'portable')]
    [string] $Variant = 'all',

    [string] $Runtime = 'win-x64',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $ZipOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Product  = 'Dragon-Butler'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project  = Join-Path $RepoRoot 'Dragon Butler/Dragon Butler.csproj'
$OutRoot  = Join-Path $RepoRoot '发布'
$Guide    = Join-Path $RepoRoot 'docs/使用说明.txt'

if (-not (Test-Path -LiteralPath $Project)) { throw "找不到工程文件：$Project" }
if (-not (Test-Path -LiteralPath $Guide))   { throw "找不到使用说明：$Guide" }

# ---- 版本号：读 csproj 的 <Version>，去掉补位的 .0 ----
[xml] $xml  = Get-Content -LiteralPath $Project -Raw
$node = $xml.SelectSingleNode('//*[local-name()="Version"]')
if ($null -eq $node) { throw 'csproj 里缺少 <Version>' }
$parts = $node.InnerText.Trim().Split('.')
if ($parts.Count -ge 3 -and $parts[-1] -eq '0') { $parts = $parts[0..($parts.Count - 2)] }
$DisplayVersion = 'v' + ($parts -join '.')

# ---- 变体定义：新增包类型只需在这里加一行 ----
$Variants = @(
    @{ Key = 'runtime-dependent'; SelfContained = $false; NativeExtract = $false; Desc = '框架依赖版，需已装 .NET 8 桌面运行时' }
    @{ Key = 'portable';          SelfContained = $true;  NativeExtract = $true;  Desc = '自包含单文件版，自带运行时，免安装' }
)
if ($Variant -ne 'all') { $Variants = @($Variants | Where-Object { $_.Key -eq $Variant }) }

Write-Host ''
Write-Host "Dragon Butler 打包 · $Product $DisplayVersion · $Runtime" -ForegroundColor Cyan

foreach ($v in $Variants) {
    $dirName = "$Product-$Runtime-$($v.Key)"
    $zipName = "$Product-$DisplayVersion-$Runtime-$($v.Key).zip"
    $outDir  = Join-Path $OutRoot $dirName
    $zipPath = Join-Path $OutRoot $zipName

    Write-Host ''
    Write-Host "== $($v.Key) ==" -ForegroundColor Yellow
    Write-Host "   $($v.Desc)"

    if (-not $ZipOnly) {
        if (Test-Path -LiteralPath $outDir) {
            Write-Host "   清理旧产物目录：$dirName"
            Remove-Item -LiteralPath $outDir -Recurse -Force
        }
        Write-Host "   dotnet publish -r $Runtime --self-contained $($v.SelfContained)"
        & dotnet publish $Project -c $Configuration -r $Runtime `
            --self-contained $($v.SelfContained.ToString().ToLower()) `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=$($v.NativeExtract.ToString().ToLower()) `
            -p:DebugType=None `
            -o $outDir
        if ($LASTEXITCODE -ne 0) { throw "publish 失败：$($v.Key)" }
    }

    if (-not (Test-Path -LiteralPath $outDir)) { throw "产物目录不存在：$outDir" }

    # 去掉调试符号，放入使用说明
    Get-ChildItem -LiteralPath $outDir -Filter '*.pdb' -Recurse -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $Guide -Destination (Join-Path $outDir '使用说明.txt') -Force

    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -LiteralPath $outDir -DestinationPath $zipPath -CompressionLevel Optimal

    $sizeMB = '{0:N2}' -f ((Get-Item -LiteralPath $zipPath).Length / 1MB)
    Write-Host "   -> $zipName  ($sizeMB MB)" -ForegroundColor Green
}

Write-Host ''
Write-Host "完成，产物在：$OutRoot" -ForegroundColor Cyan
