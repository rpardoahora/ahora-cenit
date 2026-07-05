<#
.SYNOPSIS
    Arranca el stack de ahora-cenit (infra y/o app) en dev o prod,
    pidiendo interactivamente los parametros necesarios.
#>

$ErrorActionPreference = 'Stop'

$Root     = $PSScriptRoot
$InfraDir = Join-Path $Root 'infra'
$AppDir   = Join-Path $Root 'app'

function Write-Section {
    param([string]$Text)
    Write-Host ""
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

function Read-Choice {
    param(
        [string]$Prompt,
        [string[]]$Options,
        [int]$DefaultIndex = 0
    )
    for ($i = 0; $i -lt $Options.Length; $i++) {
        Write-Host ("  {0}) {1}" -f ($i + 1), $Options[$i])
    }
    $raw = Read-Host "$Prompt [$($DefaultIndex + 1)]"
    if ([string]::IsNullOrWhiteSpace($raw)) { return $DefaultIndex }
    $n = 0
    if ([int]::TryParse($raw, [ref]$n) -and $n -ge 1 -and $n -le $Options.Length) {
        return $n - 1
    }
    Write-Host "Opcion no valida, se usa la opcion por defecto." -ForegroundColor Yellow
    return $DefaultIndex
}

function Read-YesNo {
    param(
        [string]$Prompt,
        [bool]$DefaultYes = $false
    )
    $suffix = "s/N"
    if ($DefaultYes) { $suffix = "S/n" }
    $raw = Read-Host "$Prompt ($suffix)"
    if ([string]::IsNullOrWhiteSpace($raw)) { return $DefaultYes }
    return ($raw -match '^[sSyY]')
}

function New-RandomSecret {
    param([int]$Length = 32)
    $chars = (48..57) + (65..90) + (97..122)
    -join (1..$Length | ForEach-Object { [char]($chars | Get-Random) })
}

function Set-EnvFileContent {
    param([string]$Path, [string[]]$Lines)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllLines($Path, $Lines, $utf8NoBom)
}

# Recorre un fichero .env pidiendo confirmacion/valor de cada variable.
# $Overrides permite fijar valores sin preguntar (p.ej. DOMAIN compartido).
function Edit-EnvFile {
    param(
        [string]$Path,
        [string]$TemplatePath,
        [hashtable]$Overrides = @{}
    )

    if (-not (Test-Path $Path)) {
        if (-not (Test-Path $TemplatePath)) {
            throw "No existe '$Path' ni la plantilla '$TemplatePath'."
        }
        Copy-Item $TemplatePath $Path
    }

    $lines = Get-Content -Path $Path
    $result = New-Object System.Collections.Generic.List[string]

    foreach ($line in $lines) {
        $trimmed = $line.Trim()

        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) {
            $result.Add($line)
            continue
        }

        if ($trimmed -notmatch '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            $result.Add($line)
            continue
        }

        $key = $Matches[1]
        $currentValue = $Matches[2]

        if ($Overrides.ContainsKey($key)) {
            $result.Add("$key=$($Overrides[$key])")
            continue
        }

        $looksLikePlaceholder = $currentValue -match '(?i)replace|changeme|dev-only'
        $isSecretLike = $key -match '(?i)secret|password|apikey'

        if ($looksLikePlaceholder -and $isSecretLike) {
            if (Read-YesNo "  $key aun tiene un valor de ejemplo. Generar uno aleatorio seguro?" $true) {
                $generated = New-RandomSecret
                Write-Host "    -> $key generado automaticamente."
                $result.Add("$key=$generated")
                continue
            }
        }

        $input = Read-Host "  $key [$currentValue]"
        if ([string]::IsNullOrWhiteSpace($input)) {
            $result.Add("$key=$currentValue")
        } else {
            $result.Add("$key=$input")
        }
    }

    Set-EnvFileContent -Path $Path -Lines $result
}

function Test-DockerNetwork {
    param([string]$Name)
    docker network inspect $Name *> $null
    return ($LASTEXITCODE -eq 0)
}

function Confirm-DockerNetwork {
    param([string]$Name)
    if (-not (Test-DockerNetwork -Name $Name)) {
        Write-Host "Creando red externa '$Name'..."
        docker network create $Name | Out-Null
    }
}

function Test-StackRunning {
    param([string]$WorkDir, [string]$ComposeFile, [string]$EnvFile)
    Push-Location $WorkDir
    try {
        $ids = docker compose --env-file $EnvFile -f $ComposeFile ps -q 2>$null
        return -not [string]::IsNullOrWhiteSpace(($ids -join ''))
    } finally {
        Pop-Location
    }
}

function Remove-Stack {
    param([string]$WorkDir, [string]$ComposeFile, [string]$EnvFile, [string]$Label)
    Write-Host "Borrando instalacion previa de $Label (containers + volumenes)..." -ForegroundColor Yellow
    Push-Location $WorkDir
    try {
        docker compose --env-file $EnvFile -f $ComposeFile down -v
    } finally {
        Pop-Location
    }
}

function Start-Stack {
    param([string]$WorkDir, [string]$ComposeFile, [string]$EnvFile, [string[]]$ExtraArgs = @())
    Push-Location $WorkDir
    try {
        docker compose --env-file $EnvFile -f $ComposeFile up -d @ExtraArgs
    } finally {
        Pop-Location
    }
}

# --------------------------------------------------------------------------

Write-Section "ahora-cenit - arranque"

$envIndex = Read-Choice -Prompt "Entorno a arrancar" -Options @("dev", "prod") -DefaultIndex 0
$envName = @("dev", "prod")[$envIndex]

$scopeIndex = Read-Choice -Prompt "Que quieres arrancar" -Options @(
    "Todo (infra + marketplace)",
    "Solo infra (Traefik + Portainer)",
    "Solo marketplace (app)"
) -DefaultIndex 0
$scope = @("all", "infra", "app")[$scopeIndex]

$doInfra = ($scope -eq "all" -or $scope -eq "infra")
$doApp   = ($scope -eq "all" -or $scope -eq "app")

$infraComposeFile = "docker-compose.$envName.yml"
$appComposeFile   = "docker-compose.$envName.yml"
$infraEnvPath     = Join-Path $InfraDir ".env.$envName"
$appEnvPath       = Join-Path $AppDir ".env.$envName"
$infraEnvTemplate = if ($envName -eq "prod") { Join-Path $InfraDir ".env.prod.example" } else { $infraEnvPath }
$appEnvTemplate   = if ($envName -eq "prod") { Join-Path $AppDir ".env.prod.example" } else { $appEnvPath }

# --------------------------------------------------------------------------
# Detectar instalacion previa y ofrecer borrarla
# --------------------------------------------------------------------------

Write-Section "Comprobando instalaciones previas"

if ($doInfra -and (Test-Path $infraEnvPath) -and (Test-StackRunning -WorkDir $InfraDir -ComposeFile $infraComposeFile -EnvFile $infraEnvPath)) {
    Write-Host "Se ha detectado infra ($envName) ya en marcha."
    if (Read-YesNo "Borrar containers y volumenes de infra antes de continuar? (se pierde Portainer data / certificados)" $false) {
        Remove-Stack -WorkDir $InfraDir -ComposeFile $infraComposeFile -EnvFile $infraEnvPath -Label "infra"
    }
}

if ($doApp -and (Test-Path $appEnvPath) -and (Test-StackRunning -WorkDir $AppDir -ComposeFile $appComposeFile -EnvFile $appEnvPath)) {
    Write-Host "Se ha detectado el marketplace ($envName) ya en marcha."
    if (Read-YesNo "Borrar containers y volumenes de app antes de continuar? (se pierde la base de datos)" $false) {
        Remove-Stack -WorkDir $AppDir -ComposeFile $appComposeFile -EnvFile $appEnvPath -Label "app"
    }
}

# --------------------------------------------------------------------------
# Parametros / variables de entorno
# --------------------------------------------------------------------------

Write-Section "Parametros de $envName"

$sharedOverrides = @{}
if ($doInfra -and $doApp) {
    $defaultDomain = if ($envName -eq "prod") { "ahoracenit.com" } else { "ahoracenit.localhost" }
    $domain = Read-Host "Dominio base (compartido por infra y app) [$defaultDomain]"
    if ([string]::IsNullOrWhiteSpace($domain)) { $domain = $defaultDomain }
    $sharedOverrides["DOMAIN"] = $domain
}

if ($doInfra) {
    Write-Host ""
    Write-Host "-- infra/.env.$envName --"
    Edit-EnvFile -Path $infraEnvPath -TemplatePath $infraEnvTemplate -Overrides $sharedOverrides
}

if ($doApp) {
    Write-Host ""
    Write-Host "-- app/.env.$envName --"
    Edit-EnvFile -Path $appEnvPath -TemplatePath $appEnvTemplate -Overrides $sharedOverrides
}

# --------------------------------------------------------------------------
# Arranque
# --------------------------------------------------------------------------

Write-Section "Arrancando"

if ($doInfra) {
    Confirm-DockerNetwork -Name "proxy"
    Write-Host "Levantando infra ($envName)..."
    Start-Stack -WorkDir $InfraDir -ComposeFile $infraComposeFile -EnvFile $infraEnvPath
}

if ($doApp) {
    if (-not (Test-DockerNetwork -Name "proxy")) {
        Write-Host "La red 'proxy' no existe todavia. La infra debe estar levantada antes del marketplace." -ForegroundColor Yellow
        if (Read-YesNo "Crear la red 'proxy' ahora (sin levantar infra)?" $false) {
            Confirm-DockerNetwork -Name "proxy"
        } else {
            Write-Host "Cancelado: levanta infra primero (opcion 'Solo infra' o 'Todo')." -ForegroundColor Red
            exit 1
        }
    }
    Write-Host "Construyendo y levantando app ($envName)..."
    Start-Stack -WorkDir $AppDir -ComposeFile $appComposeFile -EnvFile $appEnvPath -ExtraArgs @("--build")
}

Write-Section "Listo"

function Get-EnvValue {
    param([string]$Path, [string]$Key, [string]$FallbackValue)
    if (Test-Path $Path) {
        foreach ($line in Get-Content -Path $Path) {
            if ($line.Trim() -match "^$Key=(.*)$") { return $Matches[1] }
        }
    }
    return $FallbackValue
}

$defaultDomainDisplay = if ($envName -eq "prod") { "ahoracenit.com" } else { "ahoracenit.localhost" }
$displayDomain = $defaultDomainDisplay
if ($sharedOverrides.ContainsKey("DOMAIN")) {
    $displayDomain = $sharedOverrides["DOMAIN"]
} elseif ($doInfra) {
    $displayDomain = Get-EnvValue -Path $infraEnvPath -Key "DOMAIN" -FallbackValue $defaultDomainDisplay
} elseif ($doApp) {
    $displayDomain = Get-EnvValue -Path $appEnvPath -Key "DOMAIN" -FallbackValue $defaultDomainDisplay
}
$displayScheme = if ($envName -eq "prod") { "https" } else { "http" }

if ($doInfra) {
    Write-Host "Portainer:         $displayScheme`://portainer.$displayDomain"
    if ($envName -eq "dev") {
        Write-Host "Dashboard Traefik: http://localhost:8080"
    } else {
        Write-Host "Dashboard Traefik: https://traefik.$displayDomain"
    }
}

if ($doApp) {
    Write-Host "Marketplace:       $displayScheme`://$displayDomain"
    Write-Host "API:               $displayScheme`://$displayDomain/api"
}
