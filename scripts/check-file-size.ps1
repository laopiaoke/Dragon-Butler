# 文件大小闸门：一个文件超过 20 KB（约 5000 token）就拦住，请按功能拆开
#
# 为什么要有这个：agent 每次读代码都要把这些文件整份读进上下文，越大越贵、越容易读漏。
# 规矩只写在这一处，本地提交钩子（.git/hooks/pre-commit）和 CI 都调它——
# 本地钩子能被 --no-verify 绕过，CI 不能。
#
# 用法：
#   ./scripts/check-file-size.ps1                 # 检查暂存区（提交前钩子用）
#   ./scripts/check-file-size.ps1 -All            # 检查工作区全部被跟踪文件（CI 用）
#   ./scripts/check-file-size.ps1 -Staged         # 显式指定暂存区
param(
    [switch]$All,
    [switch]$Staged
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

# 5000 token ≈ 20 KB（字节数 ÷ 4）
$LimitBytes = 20480

# 已经超标的存量文件：白名单，不拦。想拆的时候从这张表里划掉一个。
# 这张表只减不增——新文件超标请拆，不要往这里加。
$Whitelist = @(
    'Dragon Butler/MainWindow.xaml.cs'
    'Dragon Butler/MainWindow.xaml'
    'Dragon Butler/QueueService.cs'
    'Dragon Butler/FirstRunWindow.xaml.cs'
    'Dragon Butler/TimePickerControl.xaml.cs'
    'Dragon Butler/FirstRunWindow.xaml'
    'Dragon Butler/AppState.cs'
    'Dragon Butler/NotifyService.cs'
    'Dragon Butler/SentinelService.cs'
    'Dragon Butler/IncidentBundle.cs'
    'Dragon Butler/ToolCatalog.cs'
    'Dragon Butler/CustomToolWindow.xaml'
    'Dragon Butler/CustomThemeWindow.xaml'
    'tests/CoordinatorProbe/Program.cs'
    'tests/CoordinatorProbe/RenderProbe.cs'
    'tests/CoordinatorProbe/MemoryProbe.cs'
    # 给用户看的说明书，是散文不是代码，没有「按功能拆」这回事
    'docs/使用说明.txt'
)

# 这些文件 agent 不会读，不查：
# lockfile、构建产物、图片（走 LFS，本来就只有指针大小）、二进制、日志
$SkipNamePatterns = @(
    'package-lock.json', 'pnpm-lock.yaml', 'yarn.lock', '*.lock'
    'LICENSE', 'LICENSE.txt'
)
$SkipExtPatterns = @(
    '*.png', '*.jpg', '*.jpeg', '*.gif', '*.webp', '*.ico'
    '*.zip', '*.exe', '*.dll', '*.pdb', '*.baml', '*.cache'
    '*.md'   # 文档不算代码，报告本来就长（RELEASE_NOTES.md 十几万字）
)
$SkipPathPatterns = @(
    '*/obj/*', '*/bin/*', 'obj/*', 'bin/*'
    '发布/*', 'logs/*'
    'docs/proto/*'
)

function Test-Skip([string]$path) {
    $name = Split-Path -Leaf $path
    foreach ($p in $SkipNamePatterns) { if ($name -like $p) { return $true } }
    foreach ($p in $SkipExtPatterns) { if ($name -like $p) { return $true } }
    foreach ($p in $SkipPathPatterns) { if ($path -like $p) { return $true } }
    return $false
}

# 取待查清单：暂存区（默认）或工作区全部被跟踪文件
$files = @()
if ($All) {
    $files = @(git ls-files)
} else {
    # --diff-filter=ACM：新增、复制、修改；删除的不用查
    $files = @(git diff --cached --name-only --diff-filter=ACM)
}

if ($files.Count -eq 0) {
    Write-Host '文件大小：没有要检查的文件'
    exit 0
}

$bad = @()
foreach ($f in $files) {
    if ([string]::IsNullOrWhiteSpace($f)) { continue }
    $norm = $f -replace '\\', '/'
    if (Test-Skip $norm) { continue }
    if ($Whitelist -contains $norm) { continue }

    # 暂存区里取大小（提交进去的实际字节），工作区模式直接读磁盘
    $size = 0
    if ($All) {
        if (-not (Test-Path -LiteralPath $f)) { continue }
        $size = (Get-Item -LiteralPath $f).Length
    } else {
        $s = git cat-file -s ":$f" 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($s)) { continue }
        $size = [int]$s
    }

    if ($size -gt $LimitBytes) {
        $bad += [pscustomobject]@{ Path = $norm; KB = [math]::Round($size / 1KB, 1) }
    }
}

if ($bad.Count -eq 0) {
    Write-Host "文件大小：OK（上限 $([math]::Round($LimitBytes/1KB)) KB，白名单 $($Whitelist.Count) 个存量文件）"
    exit 0
}

Write-Host ''
Write-Host "文件大小：有 $($bad.Count) 个文件超过 $([math]::Round($LimitBytes/1KB)) KB（约 5000 token）" -ForegroundColor Red
foreach ($b in $bad | Sort-Object KB -Descending) {
    Write-Host ("  {0,-8} {1}" -f ("$($b.KB) KB"), $b.Path) -ForegroundColor Red
}
Write-Host ''
Write-Host '按功能把它们拆成几个文件再提交。' -ForegroundColor Yellow
Write-Host '确实拆不动的（比如已经是单一职责的大 XAML），把路径加进 scripts/check-file-size.ps1 的 $Whitelist，但那张表只减不增。' -ForegroundColor Yellow
Write-Host ''
exit 1
