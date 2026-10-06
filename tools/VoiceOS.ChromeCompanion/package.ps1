[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$extensionDirectory = $PSScriptRoot
$repositoryDirectory = Split-Path -Parent (Split-Path -Parent $extensionDirectory)
$outputDirectory = Join-Path $repositoryDirectory "artifacts"
$outputPath = Join-Path $outputDirectory "vos-companion.zip"
# Explicit runtime allow-list: never package tests, documentation, or local files.
$files = @(
    "background.js",
    "content.js",
    "icons/icon16.png",
    "icons/icon48.png",
    "icons/icon128.png",
    "manifest.json"
)

Get-Content -LiteralPath (Join-Path $extensionDirectory "manifest.json") -Raw |
    ConvertFrom-Json | Out-Null
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $extensionDirectory $file) -PathType Leaf)) {
        throw "Required extension file is missing: $file"
    }
}

Add-Type -AssemblyName System.IO.Compression
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$stream = [System.IO.File]::Open($outputPath, [System.IO.FileMode]::Create)
try {
    $archive = [System.IO.Compression.ZipArchive]::new(
        $stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in $files) {
            $entry = $archive.CreateEntry($file, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $entryStream = $entry.Open()
            try {
                $bytes = [System.IO.File]::ReadAllBytes((Join-Path $extensionDirectory $file))
                $entryStream.Write($bytes, 0, $bytes.Length)
            } finally { $entryStream.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

Write-Host "Created $outputPath"
