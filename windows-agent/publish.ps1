$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot\RentDeviceAgent.csproj" -c Release -r win-x64 --self-contained true -p:SelfContained=true -p:PublishSingleFile=true -p:PublishTrimmed=false -o "$PSScriptRoot\dist"
Write-Host "Published to $PSScriptRoot\dist"
