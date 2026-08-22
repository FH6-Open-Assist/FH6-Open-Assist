[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PortablePath,

    [Parameter(Mandatory)]
    [string]$SetupPath,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$ApiKey = $env:VT_API_KEY,

    [string]$ApiBaseUrl = "https://www.virustotal.com/api/v3",

    [ValidateRange(5, 300)]
    [int]$PollIntervalSeconds = 30,

    [ValidateRange(1, 60)]
    [int]$TimeoutMinutes = 15,

    [ValidateRange(0, 60)]
    [int]$MinimumRequestIntervalSeconds = 16,

    [ValidateRange(1, 1099511627776)]
    [long]$LargeFileThresholdBytes = 32MB,

    [string]$FixturePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
$script:lastVirusTotalRequestUtc = $null

function Resolve-RequiredFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description não encontrado: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Assert-SafeApiUri {
    param(
        [Parameter(Mandatory)]
        [Uri]$Uri
    )

    if (-not $Uri.IsAbsoluteUri) {
        throw "A URL do VirusTotal deve ser absoluta: $Uri"
    }

    $isLocalTestUri = $Uri.Host -in @("localhost", "127.0.0.1", "::1")
    if ($Uri.Scheme -ne "https" -and -not $isLocalTestUri) {
        throw "A comunicação com o VirusTotal exige HTTPS: $Uri"
    }
}

function Resolve-SafeUploadUri {
    param(
        [Parameter(Mandatory)]
        [string]$Url
    )

    $uri = [Uri]$Url
    if (-not $uri.IsAbsoluteUri) {
        throw "A URL de upload do VirusTotal deve ser absoluta: $Url"
    }

    $isVirusTotalHost =
        $uri.Host.Equals(
            "virustotal.com",
            [StringComparison]::OrdinalIgnoreCase) -or
        $uri.Host.EndsWith(
            ".virustotal.com",
            [StringComparison]::OrdinalIgnoreCase)

    if ($uri.Scheme -eq "http" -and $isVirusTotalHost) {
        $builder = New-Object System.UriBuilder($uri)
        $builder.Scheme = "https"
        $builder.Port = -1
        $uri = $builder.Uri
    }

    Assert-SafeApiUri -Uri $uri
    return $uri
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, $utf8WithoutBom)
}

function Get-HttpStatusCode {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    $response = $ErrorRecord.Exception.Response
    if ($null -eq $response) {
        return $null
    }

    try {
        return [int]$response.StatusCode
    }
    catch {
        return $null
    }
}

function Wait-VirusTotalRateLimit {
    if ($MinimumRequestIntervalSeconds -le 0) {
        $script:lastVirusTotalRequestUtc = [DateTimeOffset]::UtcNow
        return
    }

    if ($null -ne $script:lastVirusTotalRequestUtc) {
        $elapsed = [DateTimeOffset]::UtcNow - $script:lastVirusTotalRequestUtc
        $remainingMilliseconds = [Math]::Ceiling(
            ($MinimumRequestIntervalSeconds - $elapsed.TotalSeconds) * 1000)
        if ($remainingMilliseconds -gt 0) {
            Start-Sleep -Milliseconds ([int]$remainingMilliseconds)
        }
    }

    $script:lastVirusTotalRequestUtc = [DateTimeOffset]::UtcNow
}

function Invoke-VirusTotalJsonRequest {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Get", "Post")]
        [string]$Method,

        [Parameter(Mandatory)]
        [Uri]$Uri
    )

    Assert-SafeApiUri -Uri $Uri
    $headers = @{
        "Accept" = "application/json"
        "x-apikey" = $ApiKey
    }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        Wait-VirusTotalRateLimit

        try {
            return Invoke-RestMethod `
                -Method $Method `
                -Uri $Uri.AbsoluteUri `
                -Headers $headers `
                -TimeoutSec 120
        }
        catch {
            $statusCode = Get-HttpStatusCode -ErrorRecord $_
            $isTransient = $statusCode -eq 429 -or $statusCode -in 500, 502, 503, 504
            if (-not $isTransient -or $attempt -eq 5) {
                throw
            }

            $retryAfterSeconds = [Math]::Max(30, $attempt * 15)
            try {
                $retryAfterHeader = $_.Exception.Response.Headers["Retry-After"]
                if (-not [string]::IsNullOrWhiteSpace($retryAfterHeader)) {
                    $retryAfterSeconds = [Math]::Max(
                        $retryAfterSeconds,
                        [int]$retryAfterHeader)
                }
            }
            catch {
                # Usa o atraso conservador calculado acima.
            }

            Write-Warning "VirusTotal retornou HTTP $statusCode; nova tentativa em $retryAfterSeconds s."
            Start-Sleep -Seconds $retryAfterSeconds
        }
    }
}

function Get-VirusTotalFile {
    param(
        [Parameter(Mandatory)]
        [string]$Sha256
    )

    $uri = [Uri]("{0}/files/{1}" -f $ApiBaseUrl.TrimEnd("/"), $Sha256)
    try {
        return Invoke-VirusTotalJsonRequest -Method Get -Uri $uri
    }
    catch {
        if ((Get-HttpStatusCode -ErrorRecord $_) -eq 404) {
            return $null
        }

        throw
    }
}

function Send-VirusTotalFile {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$File
    )

    Add-Type -AssemblyName System.Net.Http

    $baseUrl = $ApiBaseUrl.TrimEnd("/")
    if ($File.Length -gt $LargeFileThresholdBytes) {
        $uploadUrlResponse = Invoke-VirusTotalJsonRequest `
            -Method Get `
            -Uri ([Uri]("$baseUrl/files/upload_url"))
        $uploadUrl = [string]$uploadUrlResponse.data
        if ([string]::IsNullOrWhiteSpace($uploadUrl)) {
            throw "O VirusTotal não retornou uma URL para upload do arquivo grande."
        }
    }
    else {
        $uploadUrl = "$baseUrl/files"
    }

    $uploadUri = Resolve-SafeUploadUri -Url $uploadUrl
    Wait-VirusTotalRateLimit

    $handler = New-Object System.Net.Http.HttpClientHandler
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(12)
    $client.DefaultRequestHeaders.Add("Accept", "application/json")
    $client.DefaultRequestHeaders.Add("x-apikey", $ApiKey)
    # O endpoint de upload para arquivos grandes rejeita o multipart gerado pelo
    # .NET Framework quando o boundary vem entre aspas e o nome inclui filename*.
    # Escreva os cabeçalhos simples esperados pela API do VirusTotal.
    $boundary = "--------------------------$([Guid]::NewGuid().ToString('N'))"
    $form = New-Object System.Net.Http.MultipartFormDataContent -ArgumentList $boundary
    $form.Headers.ContentType.Parameters.Clear()
    $form.Headers.ContentType.Parameters.Add(
        (New-Object System.Net.Http.Headers.NameValueHeaderValue `
            -ArgumentList "boundary", $boundary))

    try {
        $stream = [System.IO.File]::OpenRead($File.FullName)
        $fileContent = New-Object System.Net.Http.StreamContent($stream)
        $fileContent.Headers.ContentType = `
            [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("application/octet-stream")
        $contentDisposition = New-Object `
            System.Net.Http.Headers.ContentDispositionHeaderValue `
            -ArgumentList "form-data"
        $contentDisposition.Name = '"file"'
        $contentDisposition.FileName = '"' + $File.Name + '"'
        $fileContent.Headers.ContentDisposition = $contentDisposition
        $form.Add($fileContent)

        $response = $client.PostAsync($uploadUri, $form).GetAwaiter().GetResult()
        $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "O upload para o VirusTotal retornou HTTP $([int]$response.StatusCode): $responseBody"
        }

        return $responseBody | ConvertFrom-Json
    }
    finally {
        $form.Dispose()
        $client.Dispose()
        $handler.Dispose()
    }
}

function Wait-VirusTotalAnalysis {
    param(
        [Parameter(Mandatory)]
        [string]$AnalysisId
    )

    $deadline = [DateTimeOffset]::UtcNow.AddMinutes($TimeoutMinutes)
    $analysisUri = [Uri](
        "{0}/analyses/{1}" -f $ApiBaseUrl.TrimEnd("/"),
        [Uri]::EscapeDataString($AnalysisId))

    do {
        $analysis = Invoke-VirusTotalJsonRequest -Method Get -Uri $analysisUri
        $status = [string]$analysis.data.attributes.status
        if ($status -eq "completed") {
            return $analysis
        }

        if ($status -notin @("queued", "in-progress")) {
            throw "O VirusTotal retornou um status de análise inesperado: $status"
        }

        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            break
        }

        Start-Sleep -Seconds $PollIntervalSeconds
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "A análise $AnalysisId não terminou em até $TimeoutMinutes minuto(s)."
}

function Get-StatValue {
    param(
        [Parameter(Mandatory)]
        [object]$Stats,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $property = $Stats.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return 0
    }

    return [int]$property.Value
}

function Convert-AnalysisDate {
    param(
        [Parameter(Mandatory)]
        [object]$Value
    )

    if ($Value -is [byte] -or
        $Value -is [int16] -or
        $Value -is [int32] -or
        $Value -is [int64]) {
        return [DateTimeOffset]::FromUnixTimeSeconds([long]$Value).ToUniversalTime()
    }

    return [DateTimeOffset]::Parse(
        [string]$Value,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
}

function New-ArtifactResult {
    param(
        [Parameter(Mandatory)]
        [string]$Key,

        [Parameter(Mandatory)]
        [string]$DisplayName,

        [Parameter(Mandatory)]
        [System.IO.FileInfo]$File,

        [Parameter(Mandatory)]
        [string]$Sha256,

        [Parameter(Mandatory)]
        [object]$Stats,

        [Parameter(Mandatory)]
        [DateTimeOffset]$AnalysisDateUtc,

        [Parameter(Mandatory)]
        [string]$Source
    )

    $normalizedStats = [ordered]@{
        malicious = Get-StatValue -Stats $Stats -Name "malicious"
        suspicious = Get-StatValue -Stats $Stats -Name "suspicious"
        harmless = Get-StatValue -Stats $Stats -Name "harmless"
        undetected = Get-StatValue -Stats $Stats -Name "undetected"
        timeout = Get-StatValue -Stats $Stats -Name "timeout"
        confirmedTimeout = Get-StatValue -Stats $Stats -Name "confirmed-timeout"
        failure = Get-StatValue -Stats $Stats -Name "failure"
        typeUnsupported = Get-StatValue -Stats $Stats -Name "type-unsupported"
    }
    $alertCount = $normalizedStats.malicious + $normalizedStats.suspicious
    $engineCount = 0
    foreach ($value in $normalizedStats.Values) {
        $engineCount += [int]$value
    }

    if ($engineCount -le 0) {
        throw "O VirusTotal não retornou resultados de mecanismos para $DisplayName."
    }

    $color = if ($normalizedStats.malicious -gt 0) {
        "red"
    }
    elseif ($normalizedStats.suspicious -gt 0) {
        "orange"
    }
    else {
        "brightgreen"
    }

    return [pscustomobject][ordered]@{
        key = $Key
        displayName = $DisplayName
        fileName = $File.Name
        bytes = $File.Length
        sha256 = $Sha256
        reportUrl = "https://www.virustotal.com/gui/file/$Sha256"
        analysisDateUtc = $AnalysisDateUtc.ToString("o")
        source = $Source
        alertCount = $alertCount
        engineCount = $engineCount
        badgeColor = $color
        stats = $normalizedStats
    }
}

function Get-ArtifactResult {
    param(
        [Parameter(Mandatory)]
        [string]$Key,

        [Parameter(Mandatory)]
        [string]$DisplayName,

        [Parameter(Mandatory)]
        [string]$Path,

        [object]$Fixture
    )

    $resolvedPath = Resolve-RequiredFile -Path $Path -Description $DisplayName
    $file = Get-Item -LiteralPath $resolvedPath
    $sha256 = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($null -ne $Fixture) {
        $fixtureProperty = $Fixture.PSObject.Properties[$Key]
        if ($null -eq $fixtureProperty) {
            throw "A fixture não contém o resultado sintético '$Key'."
        }

        $fixtureResult = $fixtureProperty.Value
        return New-ArtifactResult `
            -Key $Key `
            -DisplayName $DisplayName `
            -File $file `
            -Sha256 $sha256 `
            -Stats $fixtureResult.stats `
            -AnalysisDateUtc (Convert-AnalysisDate -Value $fixtureResult.analysisDateUtc) `
            -Source "fixture"
    }

    Write-Host "Consultando $DisplayName no VirusTotal pelo SHA-256 $sha256..."
    $existingFile = Get-VirusTotalFile -Sha256 $sha256
    if ($null -ne $existingFile) {
        $attributes = $existingFile.data.attributes
        return New-ArtifactResult `
            -Key $Key `
            -DisplayName $DisplayName `
            -File $file `
            -Sha256 $sha256 `
            -Stats $attributes.last_analysis_stats `
            -AnalysisDateUtc (Convert-AnalysisDate -Value $attributes.last_analysis_date) `
            -Source "existing"
    }

    Write-Host "$DisplayName ainda não existe no VirusTotal; iniciando upload público..."
    $upload = Send-VirusTotalFile -File $file
    $analysisId = [string]$upload.data.id
    if ([string]::IsNullOrWhiteSpace($analysisId)) {
        throw "O VirusTotal não retornou o identificador da análise de $DisplayName."
    }

    $analysis = Wait-VirusTotalAnalysis -AnalysisId $analysisId
    $analysisAttributes = $analysis.data.attributes
    return New-ArtifactResult `
        -Key $Key `
        -DisplayName $DisplayName `
        -File $file `
        -Sha256 $sha256 `
        -Stats $analysisAttributes.stats `
        -AnalysisDateUtc (Convert-AnalysisDate -Value $analysisAttributes.date) `
        -Source "uploaded"
}

$portablePathResolved = Resolve-RequiredFile `
    -Path $PortablePath `
    -Description "Artefato portátil"
$setupPathResolved = Resolve-RequiredFile `
    -Path $SetupPath `
    -Description "Instalador"
$outputDirectoryResolved = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputDirectoryResolved -Force | Out-Null

$fixture = $null
if (-not [string]::IsNullOrWhiteSpace($FixturePath)) {
    $fixturePathResolved = Resolve-RequiredFile `
        -Path $FixturePath `
        -Description "Fixture do VirusTotal"
    $fixture = Get-Content -Raw -LiteralPath $fixturePathResolved | ConvertFrom-Json
}
else {
    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        throw "Configure o secret VT_API_KEY antes de publicar uma release."
    }

    $apiBaseUri = [Uri]$ApiBaseUrl
    Assert-SafeApiUri -Uri $apiBaseUri
}

$results = @(
    Get-ArtifactResult `
        -Key "Portable" `
        -DisplayName "Portable" `
        -Path $portablePathResolved `
        -Fixture $fixture
    Get-ArtifactResult `
        -Key "Setup" `
        -DisplayName "Setup" `
        -Path $setupPathResolved `
        -Fixture $fixture
)

foreach ($result in $results) {
    $badge = [ordered]@{
        schemaVersion = 1
        label = "VirusTotal · $($result.displayName)"
        message = "$($result.alertCount)/$($result.engineCount) alertas"
        color = $result.badgeColor
        namedLogo = "virustotal"
        isError = $false
    }
    $badgePath = Join-Path `
        $outputDirectoryResolved `
        ("virustotal-{0}.json" -f $result.key.ToLowerInvariant())
    Write-Utf8File `
        -Path $badgePath `
        -Content ($badge | ConvertTo-Json -Depth 4 -Compress)

    if ($result.alertCount -gt 0) {
        $warningMessage = (
            "VirusTotal registrou {0} alerta(s) em {1} resultado(s) para {2}. " +
            "A release não foi bloqueada automaticamente; revise o relatório: {3}") -f
            $result.alertCount,
            $result.engineCount,
            $result.displayName,
            $result.reportUrl
        Write-Warning $warningMessage
    }
}

$resultsDocument = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    artifacts = $results
}
Write-Utf8File `
    -Path (Join-Path $outputDirectoryResolved "virustotal-results.json") `
    -Content ($resultsDocument | ConvertTo-Json -Depth 8)

$notes = New-Object System.Collections.Generic.List[string]
$notes.Add("<!-- virustotal:start -->")
$notes.Add("## Verificação no VirusTotal")
$notes.Add("")
$notes.Add("| Artefato | Resultado | Relatório |")
$notes.Add("|---|---:|---|")
foreach ($result in $results) {
    $notes.Add(
        "| $($result.displayName) | $($result.alertCount)/$($result.engineCount) alertas | " +
        "[SHA-256 $($result.sha256.Substring(0, 12))…]($($result.reportUrl)) |")
}
$notes.Add("")
$notes.Add(
    "> Os números representam um retrato da análise no momento da release. " +
    "Zero alertas não garante ausência de ameaças; resultados isolados podem ser falsos positivos.")
$notes.Add("<!-- virustotal:end -->")
Write-Utf8File `
    -Path (Join-Path $outputDirectoryResolved "virustotal-release-notes.md") `
    -Content ($notes -join [Environment]::NewLine)

Write-Host "Metadados do VirusTotal gerados em ${outputDirectoryResolved}:"
foreach ($result in $results) {
    Write-Host (
        "- {0}: {1}/{2} alerta(s), SHA-256 {3}" -f
        $result.displayName,
        $result.alertCount,
        $result.engineCount,
        $result.sha256)
}
