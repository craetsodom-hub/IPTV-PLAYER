[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts\playback-test')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repository $OutputDirectory }
dotnet publish (Join-Path $repository 'src\IptvPlayer.App\IptvPlayer.App.csproj') -c Release -r win-x64 --self-contained true -p:PlaybackDiagnosticBuild=true -p:VlcWindowsX86Enabled=false -p:VlcWindowsArm64Enabled=false -m:1 -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic build failed.' }

# Only the separate published copy receives diagnostic settings.
$settingsPath = Join-Path $output 'appsettings.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$settings.PlaybackDiagnostics = [ordered]@{
    Enabled = $true
    ProbeEnabled = $true
    NativeVerbosity = 0
    StatsIntervalMs = 1000
    LogSamples = $true
    SummaryOnly = $false
}
$settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Playback-Test-README.md') -Destination (Join-Path $output 'READ-ME.md')
Write-Output "Test app ready: $output\IptvPlayer.App.exe"
