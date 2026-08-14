$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot\RentDeviceAgent.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -o "$PSScriptRoot\dist"
Write-Host "Published to $PSScriptRoot\dist"
