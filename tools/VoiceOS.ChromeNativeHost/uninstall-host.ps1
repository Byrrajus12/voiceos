$ErrorActionPreference = "Stop"
$registryPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.voiceos.chrome_companion"
if (Test-Path -LiteralPath $registryPath) {
    Remove-Item -LiteralPath $registryPath -Recurse
    Write-Host "Removed the VoiceOS Chrome Native Messaging registration."
} else {
    Write-Host "The VoiceOS Chrome Native Messaging host was not registered."
}
