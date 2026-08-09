[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.0.0',

    [Parameter()]
    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$packageName = "3DModelExplorer-$Version-$Runtime"
$packageDirectory = [IO.Path]::GetFullPath((Join-Path $artifactsRoot $packageName))
$archivePath = [IO.Path]::GetFullPath((Join-Path $artifactsRoot "$packageName.zip"))
$projectPath = Join-Path $repositoryRoot 'src\ModelExplorer.App\ModelExplorer.App.csproj'

$artifactsPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $packageDirectory.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to package outside the artifacts directory: $packageDirectory"
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

dotnet publish $projectPath `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --property:PublishProfile=win-x64 `
    --property:Version=$Version `
    --output $packageDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Project-reference PDBs can still be copied even when the application profile
# disables its own symbols. Public release archives do not ship debug symbols.
Get-ChildItem -LiteralPath $packageDirectory -Filter '*.pdb' -File -Recurse |
    Remove-Item -Force

$documents = @(
    'README.md',
    'CHANGELOG.md',
    'LICENSE',
    'AI_ASSISTED_DEVELOPMENT.md',
    'THIRD-PARTY-NOTICES.md'
)

foreach ($document in $documents) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $document) -Destination $packageDirectory
}

Compress-Archive -LiteralPath $packageDirectory -DestinationPath $archivePath -CompressionLevel Optimal

$archive = Get-Item -LiteralPath $archivePath
Write-Output "Created $($archive.FullName) ($([Math]::Round($archive.Length / 1MB, 1)) MB)"
