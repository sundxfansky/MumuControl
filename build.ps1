[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory = 'dist'
)

$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$projectFile = Join-Path $projectDirectory 'src\MumuControl\MumuControl.csproj'
$bundledDotnet = Join-Path $projectDirectory '.tools\dotnet\dotnet.exe'

if (Test-Path -LiteralPath $bundledDotnet) {
    $dotnetExecutable = $bundledDotnet
    $env:DOTNET_CLI_HOME = Join-Path $projectDirectory '.tools\dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $projectDirectory '.tools\nuget'
}
else {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCommand) {
        throw '需要 .NET 8 SDK。安装 SDK，或将独立 SDK 解压到 .tools\dotnet。'
    }
    $dotnetExecutable = $dotnetCommand.Source
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $publishDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    $publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory $OutputDirectory))
}

Write-Host '发布 MuMu Control（内嵌本地服务、Web 界面及 .NET 运行时）…'
& $dotnetExecutable publish $projectFile --configuration $Configuration --runtime $Runtime --self-contained true --output $publishDirectory -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) {
    throw "发布失败，退出码：$LASTEXITCODE"
}

$executable = Join-Path $publishDirectory 'MuMuControl.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "发布命令已结束，但未找到 $executable"
}

Write-Host "已生成：$executable"
Write-Host '桌面界面使用系统 Microsoft Edge WebView2 运行时；无桌面运行可使用 --headless。'
