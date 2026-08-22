[CmdletBinding()]
param(
    [string]$Version = "0.0.0-local",
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\artifacts\release"),
    [string]$SigningCertificatePath,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $projectRoot "FH6OpenAssist.csproj"
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
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
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

function Find-SignTool {
    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $windowsKitsBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $candidate = Get-ChildItem -LiteralPath $windowsKitsBin -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ($null -ne $candidate) {
        return $candidate
    }

    throw "SignTool não foi encontrado no Windows SDK."
}

function Invoke-CodeSign {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath
    )

    if ([string]::IsNullOrWhiteSpace($SigningCertificatePath)) {
        return
    }

    if (-not (Test-Path -LiteralPath $SigningCertificatePath -PathType Leaf)) {
        throw "Certificado de assinatura não encontrado: $SigningCertificatePath"
    }

    if ([string]::IsNullOrWhiteSpace($env:FH6_CODE_SIGNING_PASSWORD)) {
        throw "Defina FH6_CODE_SIGNING_PASSWORD para usar o certificado de assinatura informado."
    }

    $signTool = Find-SignTool
    Invoke-CheckedCommand $signTool @(
        "sign",
        "/fd", "SHA256",
        "/tr", $TimestampUrl,
        "/td", "SHA256",
        "/f", $SigningCertificatePath,
        "/p", $env:FH6_CODE_SIGNING_PASSWORD,
        $FilePath
    )
    Invoke-CheckedCommand $signTool @("verify", "/pa", "/all", $FilePath)
}

function Test-PublishedApplication {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath
    )

    $existing = @(Get-Process -Name "FH6OpenAssist" -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        throw "Feche todas as instâncias do FH6 Open Assist antes de gerar o release."
    }

    $process = Start-Process `
        -FilePath $ExecutablePath `
        -WorkingDirectory (Split-Path -Parent $ExecutablePath) `
        -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($process.HasExited) {
                throw "O aplicativo publicado encerrou durante a inicialização com código $($process.ExitCode)."
            }

            $process.Refresh()
            if ($process.MainWindowHandle -ne 0 -and
                $process.MainWindowTitle -eq "FH6 Open Assist") {
                Write-Host "Inicialização do staging confirmada por uma janela WinUI real."
                return
            }

            Start-Sleep -Milliseconds 250
        }

        throw "O aplicativo publicado permaneceu ativo, mas não exibiu a janela FH6 Open Assist em 15 segundos."
    }
    finally {
        if (-not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(10000)) {
                Stop-Process -Id $process.Id -Force
                $process.WaitForExit()
            }
        }

        $process.Dispose()
    }
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

    foreach ($resourceName in @("App.xbf", "MainWindow.xbf", "FH6OpenAssist.pri")) {
        $resourcePath = Join-Path $stagingDirectory $resourceName
        if (-not (Test-Path -LiteralPath $resourcePath -PathType Leaf)) {
            throw "O publish WinUI está incompleto: recurso obrigatório ausente no staging: $resourceName"
        }
    }

    if (Test-Path -LiteralPath (Join-Path $stagingDirectory "portable.marker")) {
        throw "O staging não pode conter portable.marker."
    }

    Invoke-CodeSign -FilePath $executablePath
    Test-PublishedApplication -ExecutablePath $executablePath

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

    Invoke-CodeSign -FilePath $setupArtifact

    foreach ($artifact in @($portableArtifact, $setupArtifact)) {
        if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
            throw "Artefato esperado não foi gerado: $artifact"
        }
    }

    Write-Host "Artefatos gerados a partir de um único staging:"
    Write-Host "- $portableArtifact"
    Write-Host "- $setupArtifact"
    if ([string]::IsNullOrWhiteSpace($SigningCertificatePath)) {
        Write-Warning "Artefatos gerados sem assinatura Authenticode: nenhum certificado público foi informado."
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
