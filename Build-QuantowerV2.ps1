[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$QuantowerPluginDirectory = 'D:\Programy\Trading\Quantower\Settings\Scripts\plug-ins\ThetaQuantowerBridgeV2'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'QuantowerThetaBridgeV2.csproj'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$programFiles = if ($env:ProgramFiles) { $env:ProgramFiles } else { 'C:\Program Files' }
$dotnetPath = if ($dotnet) { if ($dotnet.Source) { $dotnet.Source } else { $dotnet.Path } } else { Join-Path $programFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path $dotnetPath)) { throw 'dotnet SDK was not found. Install the .NET SDK matching the Quantower plugin template, then rerun this script.' }

$assets = Join-Path $PSScriptRoot 'obj\project.assets.json'
if (Test-Path $assets) {
    & $dotnetPath build $project -c $Configuration --nologo --no-restore
} else {
    & $dotnetPath build $project -c $Configuration --nologo
}
if ($LASTEXITCODE -ne 0) { throw "Quantower V2 build failed with exit code $LASTEXITCODE." }

$assembly = Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows\ThetaQuantowerBridgeV2.dll"
if (-not (Test-Path $assembly)) { throw "Build completed but the plugin assembly was not found: $assembly" }

New-Item -ItemType Directory -Force -Path $QuantowerPluginDirectory | Out-Null
Copy-Item -Force $assembly (Join-Path $QuantowerPluginDirectory 'ThetaQuantowerBridgeV2.dll')
Write-Host "Installed $assembly to $QuantowerPluginDirectory"
Write-Host 'Restart Quantower before testing the plugin.'
