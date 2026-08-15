$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot\RentDeviceAgent.csproj" -c Release -r win-x64 --self-contained false -p:SelfContained=false -p:PublishSingleFile=true -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=true -o "$PSScriptRoot\dist"
Write-Host "Published to $PSScriptRoot\dist"
