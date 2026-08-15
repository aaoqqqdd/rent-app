param(
  [string]$InstallPath = "$env:ProgramFiles\RentDeviceAgent",
  [string]$SetupCode = ""
)

$ErrorActionPreference = "Stop"
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
  if ($SetupCode -notmatch '^\d{6}$') { throw "首次安装必须提供恰好 6 位数字访问码" }
  $settings.RentDeviceAgent.SetupCode = $SetupCode
  $settings.RentDeviceAgent.SerialNumber = ""
  $settings | ConvertTo-Json -Depth 5 | Set-Content $settingsPath -Encoding UTF8
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
  Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
  sc.exe delete $serviceName | Out-Null
  Start-Sleep -Seconds 1
}

sc.exe create $serviceName binPath= "`"$exe`" --service" start= auto DisplayName= "Rent Device Agent" | Out-Null
sc.exe description $serviceName "Connects this rental device to the Rent management website." | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
# The service owns the device state; this per-user UI is restarted on every Windows login.
# Closing the borderless overlay is already cancelled by the UI, while this startup entry
# restores it after a normal logout/login cycle without granting the renter admin rights.
$runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
New-ItemProperty -Path $runKey -Name "RentDeviceAgentUI" -Value "`"$exe`"" -PropertyType String -Force | Out-Null
Start-Service $serviceName
Write-Host "Installed and started $serviceName"
