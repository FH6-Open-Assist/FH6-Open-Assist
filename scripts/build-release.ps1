[CmdletBinding()]
param(
    [string]$Version = "0.0.0-local",
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\artifacts\release"),
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $projectRoot "ForzaFarm.csproj"
$installerScript = Join-Path $projectRoot "installer\FH6OpenAssist.iss"
$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$releaseVersion = $Version -replace '^v(?=\d)', ''

if ([string]::IsNullOrWhiteSpace($releaseVersion)) {
    throw "Informe uma versão válida para o release."
}

if ($releaseVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw "A versão '$Version' deve seguir o formato SemVer, opcionalmente precedido por v."
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "O comando '$Command' terminou com o código $LASTEXITCODE."
    }
}

function Find-InnoCompiler {
    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "Inno Setup 6 não foi encontrado. Instale-o para gerar FH6-Open-Assist-Setup.exe."
}

$innoCompiler = Find-InnoCompiler
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("fh6-open-assist-release-" + [Guid]::NewGuid().ToString("N"))
$stagingDirectory = Join-Path $temporaryRoot "staging"
$portableDirectory = Join-Path $temporaryRoot "portable"
$portableArtifact = Join-Path $resolvedOutputDirectory "FH6-Open-Assist-Portable.zip"
$setupArtifact = Join-Path $resolvedOutputDirectory "FH6-Open-Assist-Setup.exe"

New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
Remove-Item -LiteralPath $portableArtifact, $setupArtifact -Force -ErrorAction SilentlyContinue

try {
    if (-not $NoRestore) {
        Invoke-CheckedCommand "dotnet" @("restore", $projectPath, "-r", "win-x64")
    }

    Invoke-CheckedCommand "dotnet" @(
        "publish",
        $projectPath,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "--no-restore",
        "-o", $stagingDirectory,
        "-p:Version=$releaseVersion",
        "-p:PublishSingleFile=false",
        "-p:ContinuousIntegrationBuild=true"
    )

    $executablePath = Join-Path $stagingDirectory "FH6OpenAssist.exe"
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "O publish não gerou FH6OpenAssist.exe no staging."
    }

    if (Test-Path -LiteralPath (Join-Path $stagingDirectory "portable.marker")) {
        throw "O staging não pode conter portable.marker."
    }

    New-Item -ItemType Directory -Path $portableDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $stagingDirectory "*") -Destination $portableDirectory -Recurse -Force
    [System.IO.File]::WriteAllText((Join-Path $portableDirectory "portable.marker"), "")
    Compress-Archive -Path (Join-Path $portableDirectory "*") -DestinationPath $portableArtifact -CompressionLevel Optimal

    Invoke-CheckedCommand $innoCompiler @(
        "/DStagingDir=$stagingDirectory",
        "/DOutputDir=$resolvedOutputDirectory",
        "/DAppVersion=$releaseVersion",
        $installerScript
    )

    foreach ($artifact in @($portableArtifact, $setupArtifact)) {
        if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
            throw "Artefato esperado não foi gerado: $artifact"
        }
    }

    Write-Host "Artefatos gerados a partir de um único staging:"
    Write-Host "- $portableArtifact"
    Write-Host "- $setupArtifact"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
