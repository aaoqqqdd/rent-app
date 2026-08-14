param(
  [string]$InstallPath = "$env:ProgramFiles\RentDeviceAgent"
)

$ErrorActionPreference = "Stop"
$serviceName = "RentDeviceAgent"
$exe = Join-Path $InstallPath "RentDeviceAgent.exe"
if (-not (Test-Path $exe)) { throw "找不到客户端程序: $exe" }
$settingsPath = Join-Path $InstallPath "appsettings.json"
$settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{ RentDeviceAgent = [pscustomobject]@{} } }
if ([string]::IsNullOrWhiteSpace($settings.RentDeviceAgent.ApiBaseUrl)) { $settings.RentDeviceAgent.ApiBaseUrl = "https://rent.ydnw6zt6vj.workers.dev" }
if ([string]::IsNullOrWhiteSpace($settings.RentDeviceAgent.SetupCode)) {
  do { $setupCode = Read-Host "请输入管理员生成的 6 位设备注册码" } while ($setupCode -notmatch '^\d{6}$')
  $settings.RentDeviceAgent.SetupCode = $setupCode
  $settings.RentDeviceAgent.SerialNumber = ""
  $settings | ConvertTo-Json -Depth 5 | Set-Content $settingsPath -Encoding UTF8
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
  Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
  sc.exe delete $serviceName | Out-Null
  Start-Sleep -Seconds 1
}

sc.exe create $serviceName binPath= "`"$exe`"" start= auto DisplayName= "Rent Device Agent" | Out-Null
sc.exe description $serviceName "Connects this rental device to the Rent management website." | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
Start-Service $serviceName
Write-Host "Installed and started $serviceName"
