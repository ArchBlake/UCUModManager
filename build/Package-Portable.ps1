param(
    [string]$Version = "0.1.3-alpha-public",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactsRoot = Join-Path $repoRoot "artifacts"
$publishDir = Join-Path $artifactsRoot "portable\UCU Mod Manager"
$zipPath = Join-Path $artifactsRoot "UCU-ModManager-$Version-portable.zip"
$projectPath = Join-Path $repoRoot "src\UcuModManager.App\UcuModManager.App.csproj"

function Assert-InRepoPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRepoRoot = [System.IO.Path]::GetFullPath($repoRoot)
    if (-not $fullPath.StartsWith($fullRepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to touch path outside repository: $fullPath"
    }
}

Assert-InRepoPath $artifactsRoot
Assert-InRepoPath $publishDir
Assert-InRepoPath $zipPath

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

dotnet publish $projectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -o $publishDir `
    -p:PublishSingleFile=true `
    -p:UseAppHost=true `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-Item `
    -LiteralPath (Join-Path $repoRoot "THIRD-PARTY-NOTICES.txt") `
    -Destination (Join-Path $publishDir "THIRD-PARTY-NOTICES.txt") `
    -Force

Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Where-Object { $_.Extension -in ".pdb", ".xml" } |
    Remove-Item -Force

Compress-Archive -Path $publishDir -DestinationPath $zipPath -CompressionLevel Optimal

$exePath = Join-Path $publishDir "UCU Mod Manager.exe"
$exeSizeMb = [math]::Round((Get-Item -LiteralPath $exePath).Length / 1MB, 2)
$zipSizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)

[pscustomobject]@{
    Exe = $exePath
    ExeSizeMB = $exeSizeMb
    Zip = $zipPath
    ZipSizeMB = $zipSizeMb
}
