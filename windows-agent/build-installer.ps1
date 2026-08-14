param(
  [string]$ApiBaseUrl,
  [string]$SerialNumber,
  [string]$SetupCode
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root "publish"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  Write-Error "需要先安装 .NET 8 SDK。请在 Windows PowerShell 执行：winget install Microsoft.DotNet.SDK.8"
}

if (-not $ApiBaseUrl -or -not $SerialNumber -or -not $SetupCode) {
  Write-Error "参数必填：-ApiBaseUrl -SerialNumber -SetupCode"
}

$settings = @{ RentDeviceAgent = @{ ApiBaseUrl = $ApiBaseUrl; SerialNumber = $SerialNumber; SetupCode = $SetupCode; HeartbeatIntervalSeconds = 60 } } | ConvertTo-Json
Set-Content (Join-Path $root "appsettings.json") $settings -Encoding UTF8

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root "RentDeviceAgent.csproj") -c Release -r win-x64 --self-contained true -o $publish

if (Get-Command iscc -ErrorAction SilentlyContinue) {
  iscc (Join-Path $root "installer.iss")
  Write-Host "安装包已生成到 $root\output"
} else {
  Write-Warning "未找到 Inno Setup。已完成 publish；安装包需要安装 Inno Setup 后重新运行此脚本。"
}
