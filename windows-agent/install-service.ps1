param(
  [string]$InstallPath = "$env:ProgramFiles\RentDeviceAgent",
  [string]$SetupCode = ""
)

$ErrorActionPreference = "Stop"
$logDirectory = Join-Path $env:ProgramData "RentDeviceAgent"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory "install-service.log"
Start-Transcript -Path $logPath -Force | Out-Null

function Invoke-Sc {
  param([Parameter(Mandatory = $true)][string[]]$Arguments)
  & "$env:SystemRoot\System32\sc.exe" @Arguments
  if ($LASTEXITCODE -ne 0) { throw "sc.exe $($Arguments -join ' ') 失败，退出码 $LASTEXITCODE" }
}

try {
$serviceName = "RentDeviceAgent"
$exe = Join-Path $InstallPath "RentDeviceAgent.exe"
if (-not (Test-Path $exe)) { throw "找不到客户端程序: $exe" }
$settingsPath = Join-Path $InstallPath "appsettings.json"
$statePath = Join-Path $env:ProgramData "RentDeviceAgent\state.json"
$settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{ RentDeviceAgent = [pscustomobject]@{} } }
if ([string]::IsNullOrWhiteSpace($settings.RentDeviceAgent.ApiBaseUrl)) { $settings.RentDeviceAgent.ApiBaseUrl = "https://rent.ydnw6zt6vj.workers.dev" }
if (Test-Path $statePath) {
  Write-Host "检测到已有设备绑定，保留现有访问令牌和配置。"
} else {
  if ($SetupCode -and $SetupCode -notmatch '^\d{6}$') { throw "访问码必须为空，或填写恰好 6 位数字" }
  $settings.RentDeviceAgent.SetupCode = $SetupCode
  $settings.RentDeviceAgent.SerialNumber = ""
  $settings | ConvertTo-Json -Depth 5 | Set-Content $settingsPath -Encoding UTF8
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
  Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
  Invoke-Sc @('delete', $serviceName)
  Start-Sleep -Seconds 1
}

$binPath = '"' + $exe + '" --service'
Invoke-Sc @('create', $serviceName, 'binPath=', $binPath, 'start=', 'auto', 'DisplayName=', 'Rent Device Agent')
Invoke-Sc @('description', $serviceName, 'Connects this rental device to the Rent management website.')
Invoke-Sc @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000')
# The service owns the device state; this per-user UI is restarted on every Windows login.
# Closing the borderless overlay is already cancelled by the UI, while this startup entry
# restores it after a normal logout/login cycle without granting the renter admin rights.
$runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
New-ItemProperty -Path $runKey -Name "RentDeviceAgentUI" -Value "`"$exe`"" -PropertyType String -Force | Out-Null
Start-Service $serviceName
if ((Get-Service -Name $serviceName).Status -ne 'Running') { throw "服务已注册但未能进入 Running 状态" }
Write-Host "Installed and started $serviceName"
} catch {
  Write-Error $_
  exit 1
} finally {
  Stop-Transcript | Out-Null
}
