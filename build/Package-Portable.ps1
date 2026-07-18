param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactsRoot = Join-Path $repoRoot "artifacts"
$publishDir = Join-Path $artifactsRoot "portable\UCU Mod Manager"
$projectPath = Join-Path $repoRoot "src\UcuModManager.App\UcuModManager.App.csproj"
$versionPropsPath = Join-Path $repoRoot "Directory.Build.props"

[xml]$versionProps = Get-Content -LiteralPath $versionPropsPath -Raw
$canonicalVersion = @($versionProps.Project.PropertyGroup) |
    ForEach-Object { $_.Version } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($canonicalVersion)) {
    throw "Directory.Build.props does not define the canonical Version property."
}

if (-not [string]::IsNullOrWhiteSpace($Version) -and $Version -ne $canonicalVersion) {
    throw "Requested version '$Version' does not match canonical version '$canonicalVersion'."
}

$Version = $canonicalVersion
$zipPath = Join-Path $artifactsRoot "UCU-ModManager-$Version-win-x64-portable.zip"
$releaseManifestPath = Join-Path $publishDir "release-manifest.json"

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
Assert-InRepoPath $releaseManifestPath

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

$channel = if ($Version.Contains("-")) {
    $Version.Split("-", 2)[1].Split("+", 2)[0]
}
else {
    "stable"
}

$publishRootWithSeparator = $publishDir.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) `
    + [System.IO.Path]::DirectorySeparatorChar
$payloadFiles = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Where-Object { $_.FullName -ne $releaseManifestPath } |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            Path = $_.FullName.Substring($publishRootWithSeparator.Length).Replace("\", "/")
            Size = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

$releaseManifest = [ordered]@{
    SchemaVersion = 1
    Product = "UCU Mod Manager"
    Version = $Version
    Channel = $channel
    GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    Distribution = [ordered]@{
        Type = "portable"
        Platform = "windows"
        Architecture = "x64"
        RuntimeIdentifier = "win-x64"
        Framework = "net8.0-windows"
        EntryPoint = "UCU Mod Manager.exe"
        RequiresAdministrator = $true
        DataLocation = "application-directory"
    }
    Game = [ordered]@{
        Name = "Casualties Unknown Demo"
        SteamAppId = "4576510"
        NexusDomain = "scavprototype"
    }
    Integrations = [ordered]@{
        BepInEx = [ordered]@{
            Version = "5.4.23.5"
            Bundled = $false
        }
        NexusMods = [ordered]@{
            Authentication = "OAuth 2.0 with PKCE"
            ClientId = "ucu_mod_manager"
            RedirectUri = "http://127.0.0.1:17142/ucu-modmanager/oauth/callback"
            ClientSecretBundled = $false
        }
    }
    Payload = [ordered]@{
        HashAlgorithm = "SHA-256"
        ManifestExcludedFromFileList = $true
        Files = @($payloadFiles)
    }
}

$releaseManifest |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $releaseManifestPath -Encoding utf8

Compress-Archive -Path $publishDir -DestinationPath $zipPath -CompressionLevel Optimal

$exePath = Join-Path $publishDir "UCU Mod Manager.exe"
$productVersion = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
if ($productVersion -ne $Version) {
    throw "Published ProductVersion '$productVersion' does not match canonical version '$Version'."
}

$exeSizeMb = [math]::Round((Get-Item -LiteralPath $exePath).Length / 1MB, 2)
$zipSizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)

[pscustomobject]@{
    Version = $Version
    Tag = "v$Version"
    Exe = $exePath
    ExeSizeMB = $exeSizeMb
    Manifest = $releaseManifestPath
    Zip = $zipPath
    ZipSizeMB = $zipSizeMb
}
