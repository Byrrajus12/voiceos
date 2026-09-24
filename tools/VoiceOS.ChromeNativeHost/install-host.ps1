[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-p]{32}$')]
    [string]$ExtensionId,

    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$hostExecutable = Join-Path $projectDirectory "bin\$Configuration\net9.0\VoiceOS.ChromeNativeHost.exe"

if (-not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) {
    throw "Native host executable not found at '$hostExecutable'. Build it first with dotnet build."
}

$registrationDirectory = Join-Path $env:LOCALAPPDATA "VoiceOS\NativeMessaging"
New-Item -ItemType Directory -Path $registrationDirectory -Force | Out-Null
$manifestPath = Join-Path $registrationDirectory "com.voiceos.chrome_companion.json"
$manifest = [ordered]@{
    name = "com.voiceos.chrome_companion"
    description = "VoiceOS Chrome Companion native bridge"
    path = (Resolve-Path -LiteralPath $hostExecutable).Path
    type = "stdio"
    allowed_origins = @("chrome-extension://$ExtensionId/")
}
$manifestJson = $manifest | ConvertTo-Json -Depth 4
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8WithoutBom)

$registryPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.voiceos.chrome_companion"
New-Item -Path $registryPath -Force | Out-Null
Set-Item -Path $registryPath -Value $manifestPath

Write-Host "Registered com.voiceos.chrome_companion for extension $ExtensionId"
Write-Host "Manifest: $manifestPath"
Write-Host "Executable: $hostExecutable"
