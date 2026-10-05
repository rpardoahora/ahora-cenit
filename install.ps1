<#
=============================================================================
 ahora-cenit - instalador para Windows

 Windows 10/11:  instala (si falta) WSL2 + Docker Desktop y levanta el stack
                 completo (Traefik + Portainer + Registry + portal + SQL Server
                 + OpenObserve), todo configurado y enlazado.
 Windows Server: Docker Desktop no está soportado, así que crea una distro
                 Linux dedicada en WSL2 ("AhoraCenit"), ejecuta dentro el
                 instalador de Linux (install.sh) y publica los puertos 80/443
                 del servidor hacia ella. Arranca sola con el servidor.

 Uso (PowerShell como administrador, desde la carpeta del repo):
   powershell -ExecutionPolicy Bypass -File .\install.ps1              instalar
   powershell -ExecutionPolicy Bypass -File .\install.ps1 --update     actualizar
   powershell -ExecutionPolicy Bypass -File .\install.ps1 --status     estado
   powershell -ExecutionPolicy Bypass -File .\install.ps1 --backup     copia de seguridad
   powershell -ExecutionPolicy Bypass -File .\install.ps1 --restore <carpeta>
=============================================================================
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest es muchísimo más lento con la barra de progreso
$env:WSL_UTF8 = '1'                        # que wsl.exe escriba UTF-8 y no UTF-16

$ScriptRoot = $PSScriptRoot
$InfraDir   = Join-Path $ScriptRoot 'infra'
$EnvFile    = Join-Path $InfraDir '.env'
$AuthDir    = Join-Path $InfraDir 'auth'
$BackupRoot = Join-Path $ScriptRoot 'backups'

$HtpasswdImage  = 'httpd:2.4-alpine'
$HelperImage    = 'alpine:3.20'
$AdminUser      = 'admin'
$PlatformUser   = 'cenit-admin'   # usuario de automatizacion (token de plataforma): registry y Nexus
$Volumes        = @('cenit_sqlserver_data', 'cenit_portainer_data', 'cenit_registry_data', 'cenit_openobserve_data', 'cenit_nexus_data', 'cenit_letsencrypt')
$LegacyContainers = @('traefik', 'portainer', 'app', 'sqlserver', 'openobserve')
$DockerDesktopUrl = 'https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe'

# Windows Server (WSL2)
$WslDistro    = 'AhoraCenit'
$WslHome      = Join-Path $env:ProgramData 'AhoraCenit'
$WslRootfsUrl = 'https://cloud-images.ubuntu.com/wsl/releases/24.04/current/ubuntu-noble-wsl-amd64-24.04lts.rootfs.tar.gz'
$WslRepoPath  = '/opt/ahora-cenit'
$BootTaskName = 'AhoraCenit-WSL'

$EnvKeys = @('CENIT_MODE', 'DOMAIN', 'PUBLIC_SCHEME', 'HTTP_PORT', 'PUBLIC_PORT_SUFFIX', 'COMPOSE_FILE', 'COMPOSE_PATH_SEPARATOR', 'ACME_EMAIL',
    'ADMIN_EMAIL', 'ADMIN_PASSWORD', 'DB_SA_PASSWORD', 'MSSQL_PID', 'APPS_SQL_MODE', 'JWT_SECRET', 'APP_IMAGE', 'APP_VERSION',
    'PORTAINER_API_KEY', 'PORTAINER_ENDPOINT_ID', 'PORTAINER_LOCAL_PORT', 'REGISTRY_HOST', 'REGISTRY_LOCAL_PORT',
    'NUGET_PUBLIC_URL', 'NUGET_LOCAL_PORT', 'CENIT_TOKEN',
    'REGISTRATION_ENABLED', 'REQUIRE_EMAIL_CONFIRMATION', 'SMTP_HOST', 'SMTP_PORT', 'SMTP_USER', 'SMTP_PASSWORD', 'SMTP_FROM',
    'OTEL_EXPORTER_OTLP_ENDPOINT')

$script:Cfg = [ordered]@{}
$script:GeneratedPassword = $false
$script:PtHeaders = @{}

# ----------------------------------------------------------------------------
# Salida
# ----------------------------------------------------------------------------
function Write-Section([string]$Text) { Write-Host ""; Write-Host "== $Text ==" -ForegroundColor Cyan }
function Write-Info([string]$Text)    { Write-Host "--> $Text" }
function Write-Ok([string]$Text)      { Write-Host " OK " -ForegroundColor Green -NoNewline; Write-Host $Text }
function Write-Warn([string]$Text)    { Write-Host "AVISO: $Text" -ForegroundColor Yellow }
function Stop-WithError([string]$Text) { throw $Text }

function Show-Usage {
    @'
ahora-cenit - instalador

  .\install.ps1                      Instala (o reconfigura) el sistema
  .\install.ps1 --update             Actualiza: git pull, imagenes nuevas, variables
                                     nuevas en el .env (con copia de seguridad previa)
  .\install.ps1 --status             Estado de los contenedores y URLs
  .\install.ps1 --backup             Copia de seguridad en .\backups\
  .\install.ps1 --restore <carpeta>  Restaura una copia de seguridad

Opciones:
  --yes, -y        No preguntar: usar las respuestas por defecto
  --no-backup      En --update, no hacer la copia de seguridad previa
  --http-port <n>  Solo modo local: puerto HTTP en el que publicar (por defecto 80)
  --help, -h       Esta ayuda
'@ | Write-Host
}

# ----------------------------------------------------------------------------
# Argumentos (mismo formato que install.sh)
# ----------------------------------------------------------------------------
$Command = 'install'; $AssumeYes = $false; $NoBackup = $false; $SkipGitPull = $false
$RestoreDir = ''; $Elevated = $false; $HttpPortArg = ''
$PassThroughArgs = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $args.Count; $i++) {
    $a = [string]$args[$i]
    switch -Regex ($a) {
        '^(--update|-u|update|-update)$'   { $Command = 'update'; $PassThroughArgs.Add('--update') }
        '^(--status|-s|status|-status)$'   { $Command = 'status'; $PassThroughArgs.Add('--status') }
        '^(--backup|backup|-backup)$'      { $Command = 'backup'; $PassThroughArgs.Add('--backup') }
        '^(--restore|restore|-restore)$'   {
            $Command = 'restore'; $PassThroughArgs.Add('--restore')
            if ($i + 1 -lt $args.Count) { $i++; $RestoreDir = [string]$args[$i]; $PassThroughArgs.Add($RestoreDir) }
        }
        '^(--yes|-y|-yes)$'                { $AssumeYes = $true; $PassThroughArgs.Add('--yes') }
        '^(--no-backup|-nobackup)$'        { $NoBackup = $true; $PassThroughArgs.Add('--no-backup') }
        '^--skip-git-pull$'                { $SkipGitPull = $true }
        '^--http-port$'                    {
            if ($i + 1 -lt $args.Count) { $i++; $HttpPortArg = [string]$args[$i]; $PassThroughArgs.Add('--http-port'); $PassThroughArgs.Add($HttpPortArg) }
        }
        '^--elevated$'                     { $Elevated = $true }
        '^(--help|-h|help|-help|/\?)$'     { Show-Usage; exit 0 }
        default                            { Write-Host "Opcion desconocida: $a (usa --help)" -ForegroundColor Red; exit 1 }
    }
}

if ($HttpPortArg -and (-not ($HttpPortArg -match '^\d+$') -or [int]$HttpPortArg -lt 1 -or [int]$HttpPortArg -gt 65535)) {
    Write-Host "--http-port debe ser un puerto valido (1-65535)." -ForegroundColor Red; exit 1
}

function Get-HostExe { (Get-Process -Id $PID).Path }

function Get-ScriptArgs([string[]]$Extra) {
    $list = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    $list += @($PassThroughArgs)
    if ($Extra) { $list += $Extra }
    return $list
}

function Test-IsAdmin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ----------------------------------------------------------------------------
# Preguntas
# ----------------------------------------------------------------------------
function Read-Value([string]$Prompt, [string]$Default = '') {
    if ($AssumeYes) { return $Default }
    if ($Default) { $answer = Read-Host "  $Prompt [$Default]" } else { $answer = Read-Host "  $Prompt" }
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim()
}

function Read-Secret([string]$Prompt) {
    $secure = Read-Host "  $Prompt" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Read-YesNo([string]$Prompt, [bool]$DefaultYes = $false) {
    if ($AssumeYes) { return $DefaultYes }
    $hint = 's/N'; if ($DefaultYes) { $hint = 'S/n' }
    $raw = Read-Host "  $Prompt ($hint)"
    if ([string]::IsNullOrWhiteSpace($raw)) { return $DefaultYes }
    return ($raw -match '^[sSyY]')
}

function Read-Choice([string]$Prompt, [string[]]$Options, [int]$Default = 1) {
    for ($i = 0; $i -lt $Options.Length; $i++) { Write-Host ("    {0}) {1}" -f ($i + 1), $Options[$i]) }
    if ($AssumeYes) { return $Default }
    while ($true) {
        $raw = Read-Host "  $Prompt [$Default]"
        if ([string]::IsNullOrWhiteSpace($raw)) { return $Default }
        $n = 0
        if ([int]::TryParse($raw, [ref]$n) -and $n -ge 1 -and $n -le $Options.Length) { return $n }
        Write-Host "  Opcion no valida."
    }
}

function Test-ForbiddenChars([string]$Value) { return ($Value -match "[\s'`"\\``]") }

# Devuelve el primer motivo por el que la contrasena no es segura ('' si vale).
function Get-AdminPasswordProblem([string]$p) {
    if ($p.Length -lt 12) { return 'tiene menos de 12 caracteres' }
    if ($p -cnotmatch '[A-Z]') { return 'le falta una mayuscula' }
    if ($p -cnotmatch '[a-z]') { return 'le falta una minuscula' }
    if ($p -notmatch '[0-9]') { return 'le falta un numero' }
    if ($p -notmatch '[^A-Za-z0-9]') { return 'le falta un simbolo' }
    if (Test-ForbiddenChars $p) { return 'contiene espacios, comillas, \ o `' }
    if ($p -cmatch '(.)\1\1\1') { return 'repite el mismo caracter 4 veces seguidas' }
    if ([System.Collections.Generic.HashSet[char]]::new($p.ToCharArray()).Count -lt 8) { return 'tiene menos de 8 caracteres distintos' }
    # Palabras y secuencias tipicas, mas el usuario del email y las partes del
    # dominio (salvo la extension: com, es...).
    $words = [System.Collections.Generic.List[string]]@('password', 'passw0rd', 'contrase', 'qwerty', 'asdf', 'zxcv', '1234', '4321', 'abcd',
        'admin', 'cenit', 'ahora', 'letmein', 'welcome', 'bienvenid', 'changeme', 'iloveyou')
    $emailUser = (Get-Cfg 'ADMIN_EMAIL').Split('@')[0]
    if ($emailUser.Length -ge 3) { $words.Add($emailUser.ToLowerInvariant()) }
    $labels = (Get-Cfg 'DOMAIN').Split('.')
    foreach ($label in $labels[0..([Math]::Max(0, $labels.Count - 2))]) { if ($label.Length -ge 4) { $words.Add($label.ToLowerInvariant()) } }
    $lower = $p.ToLowerInvariant()
    foreach ($w in $words) { if ($lower.Contains($w)) { return "contiene algo facil de adivinar (`"$w`")" } }
    return ''
}

function Test-Email([string]$v)  { return ($v -match '^[^@\s]+@[^@\s]+\.[^@\s]+$') -and -not (Test-ForbiddenChars $v) }
function Test-Domain([string]$v) { return $v -match '^([a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}$' }

function New-RandomString([string]$Chars, [int]$Length) {
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] $Length
    $rng.GetBytes($bytes)
    $sb = New-Object System.Text.StringBuilder
    foreach ($b in $bytes) { [void]$sb.Append($Chars[$b % $Chars.Length]) }
    return $sb.ToString()
}

# Siempre con mayúscula, minúscula, dígito y símbolo (lo exigen SQL Server y OpenObserve).
function New-Secret([int]$Length = 32) {
    $alnum = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    return (New-RandomString $alnum ($Length - 4)) + (New-RandomString 'ABCDEFGHIJKLMNOPQRSTUVWXYZ' 1) +
        (New-RandomString 'abcdefghijklmnopqrstuvwxyz' 1) + (New-RandomString '0123456789' 1) + (New-RandomString '_.@-' 1)
}

# ----------------------------------------------------------------------------
# Comandos nativos (docker, git, wsl): los avisos por stderr no deben abortar
# ----------------------------------------------------------------------------
function Invoke-Native([scriptblock]$Block, [string]$ErrorMessage = '') {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Block } finally { $ErrorActionPreference = $previous }
    if ($ErrorMessage -and $LASTEXITCODE -ne 0) { Stop-WithError "$ErrorMessage (codigo $LASTEXITCODE)" }
}

function Invoke-Compose {
    Push-Location $InfraDir
    try {
        $composeArgs = @('compose') + $args
        Invoke-Native { & docker @composeArgs } "docker $($composeArgs -join ' ') ha fallado"
    } finally { Pop-Location }
}

function Test-DockerReady {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return $false }
    Invoke-Native { docker info *> $null }
    return ($LASTEXITCODE -eq 0)
}

# ----------------------------------------------------------------------------
# Fichero .env (mismo formato que install.sh: KEY='valor')
# ----------------------------------------------------------------------------
function Import-EnvFile {
    $script:Cfg = [ordered]@{}
    if (-not (Test-Path $EnvFile)) { return }
    foreach ($line in [IO.File]::ReadAllLines($EnvFile)) {
        if ($line -match '^\s*#') { continue }
        if ($line -notmatch '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { continue }
        $key = $Matches[1]; $value = $Matches[2]
        if ($value -match "^'(.*)'$" -or $value -match '^"(.*)"$') { $value = $Matches[1] }
        $script:Cfg[$key] = $value
    }
}

function Get-Cfg([string]$Key) { if ($script:Cfg.Contains($Key)) { return [string]$script:Cfg[$Key] } return '' }
function Set-CfgDefault([string]$Key, [string]$Value) { if (-not $script:Cfg.Contains($Key) -or (Get-Cfg $Key) -eq '') { $script:Cfg[$Key] = $Value } }

function Set-Defaults {
    Set-CfgDefault 'CENIT_MODE' 'local'
    Set-CfgDefault 'DOMAIN' 'localhost'
    Set-CfgDefault 'REGISTRY_LOCAL_PORT' '5000'
    if ((Get-Cfg 'CENIT_MODE') -eq 'production') {
        $script:Cfg['PUBLIC_SCHEME'] = 'https'
        $script:Cfg['COMPOSE_FILE'] = 'docker-compose.yml:docker-compose.prod.yml'
        $script:Cfg['REGISTRY_HOST'] = "registry." + (Get-Cfg 'DOMAIN')
        $script:Cfg['HTTP_PORT'] = '80'   # Let's Encrypt (HTTP-01) necesita el 80
    } else {
        $script:Cfg['PUBLIC_SCHEME'] = 'http'
        $script:Cfg['COMPOSE_FILE'] = 'docker-compose.yml'
        $script:Cfg['REGISTRY_HOST'] = "localhost:" + (Get-Cfg 'REGISTRY_LOCAL_PORT')
        Set-CfgDefault 'HTTP_PORT' '80'
    }
    if ((Get-Cfg 'CENIT_MODE') -ne 'production' -and $HttpPortArg) { $script:Cfg['HTTP_PORT'] = $HttpPortArg }
    if ((Get-Cfg 'HTTP_PORT') -eq '80') { $script:Cfg['PUBLIC_PORT_SUFFIX'] = '' } else { $script:Cfg['PUBLIC_PORT_SUFFIX'] = ':' + (Get-Cfg 'HTTP_PORT') }
    Set-CfgDefault 'NUGET_LOCAL_PORT' '5100'
    # Versiones anteriores lo llamaban NUGET_TOKEN.
    if (-not (Get-Cfg 'CENIT_TOKEN') -and (Get-Cfg 'NUGET_TOKEN')) { $script:Cfg['CENIT_TOKEN'] = Get-Cfg 'NUGET_TOKEN' }
    if (-not (Get-Cfg 'CENIT_TOKEN')) {
        $script:Cfg['CENIT_TOKEN'] = 'cenit_' + (New-RandomString 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789' 40)
        # Instalacion existente sin token (o token borrado para rotarlo): hay que darlo de alta en el registry.
        if (Test-Path (Join-Path $AuthDir 'htpasswd')) { $script:TokenChanged = $true }
    }
    if ($script:Cfg.Contains('NUGET_TOKEN')) { $script:Cfg.Remove('NUGET_TOKEN') }
    if ((Get-Cfg 'CENIT_MODE') -eq 'production') { $script:Cfg['NUGET_PUBLIC_URL'] = "https://nuget.$(Get-Cfg 'DOMAIN')/" }
    else { $script:Cfg['NUGET_PUBLIC_URL'] = "http://localhost:$(Get-Cfg 'NUGET_LOCAL_PORT')/" }
    $script:Cfg['COMPOSE_PATH_SEPARATOR'] = ':'
    Set-CfgDefault 'ACME_EMAIL' (Get-Cfg 'ADMIN_EMAIL')
    Set-CfgDefault 'DB_SA_PASSWORD' (New-Secret 32)
    Set-CfgDefault 'MSSQL_PID' 'Express'
    Set-CfgDefault 'APPS_SQL_MODE' 'dedicated'
    Set-CfgDefault 'JWT_SECRET' (New-Secret 64)
    Set-CfgDefault 'APP_IMAGE' 'ghcr.io/rpardoahora/ahora-cenit'
    Set-CfgDefault 'APP_VERSION' 'latest'
    Set-CfgDefault 'PORTAINER_LOCAL_PORT' '9000'
    Set-CfgDefault 'REGISTRATION_ENABLED' 'false'
    Set-CfgDefault 'REQUIRE_EMAIL_CONFIRMATION' 'false'
    Set-CfgDefault 'SMTP_PORT' '587'
    if (-not $script:Cfg.Contains('OTEL_EXPORTER_OTLP_ENDPOINT')) { $script:Cfg['OTEL_EXPORTER_OTLP_ENDPOINT'] = 'http://openobserve:5080/api/default' }
    foreach ($k in $EnvKeys) { if (-not $script:Cfg.Contains($k)) { $script:Cfg[$k] = '' } }
}

function Export-EnvFile {
    $comments = @{
        'CENIT_MODE'                  = @('', '# --- Modo y dominio (local = localhost por HTTP, production = HTTPS) ---')
        'HTTP_PORT'                   = @('# Puerto HTTP del host (solo local puede ser distinto de 80; el sufijo lo calcula el instalador).')
        'COMPOSE_FILE'                = @("# Ficheros compose que usa 'docker compose' en esta carpeta (no tocar).")
        'ACME_EMAIL'                  = @("# Email para Let's Encrypt (solo produccion).")
        'ADMIN_EMAIL'                 = @('', '# --- Administrador: portal, Portainer, Traefik, registry y OpenObserve ---')
        'DB_SA_PASSWORD'              = @('', '# --- Secretos internos (generados automaticamente) ---')
        'MSSQL_PID'                   = @('# Edicion de SQL Server: Express (gratis, apta para produccion), Developer (solo pruebas) o Standard/Enterprise/clave con licencia.')
        'APPS_SQL_MODE'               = @('# SQL Server de las aplicaciones: dedicated (cada una con su imagen SQL) o shared (todas en el de la plataforma, un login por cliente). Solo afecta a los despliegues nuevos.')
        'APP_IMAGE'                   = @('', '# --- Imagen del portal (APP_VERSION: latest o sha-<commit> / v<version>) ---')
        'PORTAINER_API_KEY'           = @('', '# --- Portainer / registry (rellenado por el instalador) ---')
        'NUGET_PUBLIC_URL'            = @('', "# --- NuGet (Nexus) y token de plataforma. CENIT_TOKEN es la contrasena del usuario ${PlatformUser}: vale para la API del portal, docker login y NuGet ---")
        'REGISTRATION_ENABLED'        = @('', '# --- Registro de usuarios externos (valor inicial: despues manda Administracion > Ajustes del portal) ---')
        'REQUIRE_EMAIL_CONFIRMATION'  = @('# --- Emails (SMTP vacio = los emails solo se escriben en el log) ---')
        'OTEL_EXPORTER_OTLP_ENDPOINT' = @('', '# --- Telemetria (vacio = no exportar a OpenObserve) ---')
    }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# ============================================================================')
    $lines.Add('#  ahora-cenit - configuracion de la instalacion')
    $lines.Add('#  Generado por install.sh / install.ps1. Puedes editarlo a mano; aplica los')
    $lines.Add('#  cambios con: .\install.ps1 --update  (o docker compose up -d en infra\).')
    $lines.Add('#  CONTIENE CONTRASENAS: no lo subas a git ni lo compartas.')
    $lines.Add('# ============================================================================')
    foreach ($k in $EnvKeys) {
        if ($comments.ContainsKey($k)) { foreach ($c in $comments[$k]) { $lines.Add($c) } }
        $lines.Add("$k='$(Get-Cfg $k)'")
    }
    $extra = @($script:Cfg.Keys | Where-Object { $EnvKeys -notcontains $_ })
    if ($extra.Count -gt 0) {
        $lines.Add(''); $lines.Add('# --- Variables personalizadas ---')
        foreach ($k in $extra) { $lines.Add("$k='$(Get-Cfg $k)'") }
    }
    New-Item -ItemType Directory -Force -Path $InfraDir | Out-Null
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($EnvFile, (($lines -join "`n") + "`n"), $utf8NoBom)
    # Solo el usuario que instala, los administradores y SYSTEM pueden leerlo.
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Invoke-Native { icacls $EnvFile /inheritance:r /grant:r "*${me}:(F)" "*S-1-5-32-544:(F)" "*S-1-5-18:(F)" | Out-Null }
}

# ----------------------------------------------------------------------------
# Docker Desktop (Windows 10/11)
# ----------------------------------------------------------------------------
function Test-WslInstalled {
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { return $false }
    Invoke-Native { wsl.exe --status *> $null }
    return ($LASTEXITCODE -eq 0)
}

function Register-ResumeAfterReboot {
    $cmd = "`"$(Get-HostExe)`" " + ((Get-ScriptArgs @('--elevated')) -join ' ')
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce' -Name 'AhoraCenitInstall' -Value $cmd
}

function Request-Reboot([string]$Reason) {
    Register-ResumeAfterReboot
    Write-Warn $Reason
    Write-Host "  Tras reiniciar e iniciar sesion, el instalador continuara automaticamente." -ForegroundColor Yellow
    if (Read-YesNo "Reiniciar ahora?" $true) { Restart-Computer -Force }
    exit 0
}

function Install-Wsl {
    Write-Info "Instalando WSL2 (necesario para Docker)..."
    Invoke-Native { wsl.exe --install --no-distribution }
    if ($LASTEXITCODE -ne 0) {
        Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux -NoRestart -All | Out-Null
        Enable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -NoRestart -All | Out-Null
    }
    Request-Reboot "WSL2 instalado: hay que reiniciar Windows."
}

function Add-DockerToPath {
    $bin = Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin'
    if ((Test-Path $bin) -and ($env:Path -notlike "*$bin*")) { $env:Path = "$bin;$env:Path" }
}

function Start-DockerDesktop {
    $exe = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if (-not (Test-Path $exe)) { Stop-WithError "No se encuentra Docker Desktop en $exe" }
    Write-Info "Arrancando Docker Desktop (puede tardar un par de minutos)..."
    Start-Process -FilePath $exe
    for ($i = 0; $i -lt 90; $i++) {
        Start-Sleep -Seconds 4
        if (Test-DockerReady) { return }
    }
    Stop-WithError "Docker Desktop no ha arrancado. Abrelo a mano, espera a que ponga 'Engine running' y vuelve a ejecutar el instalador."
}

function Confirm-DockerDesktop {
    Write-Section "Docker"
    Add-DockerToPath
    if (-not (Test-DockerReady)) {
        $exe = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
        if (-not (Test-Path $exe)) {
            if (-not (Test-WslInstalled)) { Install-Wsl }
            $installer = Join-Path $env:TEMP 'DockerDesktopInstaller.exe'
            Write-Info "Descargando Docker Desktop (~600 MB)..."
            Invoke-WebRequest -Uri $DockerDesktopUrl -OutFile $installer -UseBasicParsing
            Write-Info "Instalando Docker Desktop..."
            $p = Start-Process -FilePath $installer -ArgumentList 'install', '--quiet', '--accept-license', '--backend=wsl-2' -Wait -PassThru
            if ($p.ExitCode -ne 0) { Stop-WithError "La instalacion de Docker Desktop ha fallado (codigo $($p.ExitCode))." }
            Remove-Item $installer -Force -ErrorAction SilentlyContinue
            try { Add-LocalGroupMember -Group 'docker-users' -Member "$env:USERDOMAIN\$env:USERNAME" -ErrorAction Stop } catch { }
            Add-DockerToPath
        }
        Start-DockerDesktop
    }
    $osType = (Invoke-Native { docker info --format '{{.OSType}}' }) | Select-Object -Last 1
    if ($osType -ne 'linux') {
        Stop-WithError "Docker Desktop esta en modo 'Windows containers'. Cambia a Linux containers (icono de Docker > Switch to Linux containers) y vuelve a ejecutar."
    }
    Write-Ok ((Invoke-Native { docker --version }) | Select-Object -Last 1)
    Write-Ok ((Invoke-Native { docker compose version }) | Select-Object -Last 1)
}

function Confirm-System {
    Write-Section "Comprobando el sistema"
    if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
        Stop-WithError "Se necesita Windows x64: SQL Server no funciona en ARM."
    }
    $memGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
    if ($memGb -lt 8) {
        Write-Warn "El equipo tiene $memGb GB de RAM. Con Docker Desktop, SQL Server y Nexus se recomiendan al menos 8 GB."
        if (-not (Read-YesNo "Continuar de todos modos?" $false)) { exit 1 }
    } else { Write-Ok "Memoria: $memGb GB" }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Warn "git no esta instalado: --update no podra descargar la ultima version del codigo (winget install Git.Git)."
    }
}

# ----------------------------------------------------------------------------
# Docker: red, htpasswd, puertos, contenedores antiguos
# ----------------------------------------------------------------------------
function Confirm-Network {
    Invoke-Native { docker network inspect proxy *> $null }
    if ($LASTEXITCODE -ne 0) { Write-Info "Creando red Docker 'proxy'"; Invoke-Native { docker network create proxy | Out-Null } "No se pudo crear la red proxy" }
}

# Usuarios del registry y del dashboard de Traefik: admin (contrasena de
# administrador) y, si ya existe, cenit-admin con el token de plataforma.
function New-HtpasswdLine([string]$User, [string]$Password) {
    $out = Invoke-Native { docker run --rm --entrypoint htpasswd $HtpasswdImage -Bbn $User $Password } "No se pudo generar el fichero htpasswd"
    return @($out | Where-Object { "$_" -match "^$([regex]::Escape($User)):" })[0]
}

function Write-Htpasswd {
    New-Item -ItemType Directory -Force -Path $AuthDir | Out-Null
    $content = (New-HtpasswdLine $AdminUser (Get-Cfg 'ADMIN_PASSWORD')) + "`n"
    if (Get-Cfg 'CENIT_TOKEN') { $content += (New-HtpasswdLine $PlatformUser (Get-Cfg 'CENIT_TOKEN')) + "`n" }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $AuthDir 'htpasswd'), $content, $utf8NoBom)
}

# Tras crear/renovar el token de plataforma, darlo de alta en el registry.
$script:TokenChanged = $false
function Sync-PlatformToken {
    if (-not (Get-Cfg 'CENIT_TOKEN')) { return }
    $file = Join-Path $AuthDir 'htpasswd'
    $hasUser = (Test-Path $file) -and (Select-String -Path $file -Pattern "^$([regex]::Escape($PlatformUser)):" -Quiet)
    if ($script:TokenChanged -or -not $hasUser) {
        Write-Htpasswd
        Invoke-Compose restart registry traefik
        Write-Ok "Token de plataforma dado de alta en el registry (usuario $PlatformUser)"
    }
}

function Test-CenitTraefikRunning {
    $id = Invoke-Native { docker ps -q --filter label=com.docker.compose.project=cenit --filter label=com.docker.compose.service=traefik }
    return -not [string]::IsNullOrWhiteSpace(($id -join ''))
}

function Get-PortListener([int]$Port) {
    return (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1)
}

function Confirm-Ports {
    if (Test-CenitTraefikRunning) { return }
    $ports = @([int](Get-Cfg 'HTTP_PORT')); if ((Get-Cfg 'CENIT_MODE') -eq 'production') { $ports += 443 }
    foreach ($p in $ports) {
        $conn = Get-PortListener $p
        if ($conn) {
            $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
            $name = if ($conn.OwningProcess -eq 4) { 'System (IIS / http.sys)' } elseif ($proc) { $proc.ProcessName } else { "PID $($conn.OwningProcess)" }
            Stop-WithError "El puerto $p ya esta en uso por '$name'. Liberalo (p.ej. para IIS: Stop-Service W3SVC) y vuelve a ejecutar."
        }
    }
}

function Open-Firewall {
    if ((Get-Cfg 'CENIT_MODE') -ne 'production') { return }
    if (-not (Test-IsAdmin)) {
        Write-Warn "Sin permisos de administrador no puedo abrir el firewall: abre los puertos 80 y 443 a mano o ejecuta el instalador como administrador."
        return
    }
    if (-not (Get-NetFirewallRule -Name 'AhoraCenit-HTTP' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -Name 'AhoraCenit-HTTP' -DisplayName 'ahora-cenit HTTP/HTTPS' -Direction Inbound -Protocol TCP -LocalPort 80, 443 -Action Allow | Out-Null
        Write-Ok "Firewall de Windows: abiertos 80 y 443"
    }
}

function Remove-LegacyContainers {
    $found = @()
    foreach ($c in $LegacyContainers) {
        Invoke-Native { docker container inspect $c *> $null }
        if ($LASTEXITCODE -eq 0) {
            $project = Invoke-Native { docker container inspect -f '{{ index .Config.Labels "com.docker.compose.project" }}' $c }
            if ("$project".Trim() -ne 'cenit') { $found += $c }
        }
    }
    if ($found.Count -eq 0) { return }
    Write-Warn "Hay contenedores de una instalacion anterior: $($found -join ', ')"
    if (-not (Read-YesNo "Eliminarlos? (los datos en volumenes se conservan)" $true)) { Stop-WithError "No se puede continuar con esos contenedores ocupando los puertos." }
    Invoke-Native { docker rm -f @found | Out-Null } "No se pudieron eliminar los contenedores antiguos"
    Write-Ok "Contenedores antiguos eliminados"
}

function Get-ExistingVolumes {
    $existing = @()
    foreach ($v in $Volumes) {
        Invoke-Native { docker volume inspect $v *> $null }
        if ($LASTEXITCODE -eq 0) { $existing += $v }
    }
    return $existing
}

function Resolve-ExistingData {
    $vols = @(Get-ExistingVolumes)
    if ($vols.Count -eq 0) { return }
    Write-Warn "Hay datos de una instalacion anterior: $($vols -join ', ')"
    $choice = Read-Choice "Elige" @(
        'Borrarlos y empezar de cero (se pierden apps, usuarios y configuracion)',
        'Conservarlos (necesitaras la contrasena SA de SQL Server de entonces)') 2
    if ($choice -eq 1) {
        if (-not (Read-YesNo "Se BORRARAN todos los datos. Seguro?" $false)) { exit 1 }
        $ids = Invoke-Native { docker ps -aq --filter label=com.docker.compose.project=cenit }
        if ($ids) { Invoke-Native { docker rm -f @ids | Out-Null } }
        Invoke-Native { docker volume rm -f @vols | Out-Null } "No se pudieron borrar los volumenes"
        Write-Ok "Datos anteriores borrados"
    } elseif ($vols -contains 'cenit_sqlserver_data') {
        $old = ''
        foreach ($f in @((Join-Path $InfraDir '.env.cenit'), (Join-Path $ScriptRoot 'app\.env.prod'), (Join-Path $ScriptRoot 'app\.env.dev'))) {
            if (Test-Path $f) {
                $m = Select-String -Path $f -Pattern '^DB_SA_PASSWORD=(.*)$' | Select-Object -First 1
                if ($m) { $old = $m.Matches[0].Groups[1].Value.Trim("'", '"'); if ($old) { break } }
            }
        }
        $script:Cfg['DB_SA_PASSWORD'] = Read-Value "Contrasena SA de SQL Server existente" $old
        Write-Warn "OpenObserve conservara el usuario root con el que se creo; si era otro, ajusta la telemetria desde su panel."
    }
}

# ----------------------------------------------------------------------------
# Configuración interactiva
# ----------------------------------------------------------------------------
function Test-Dns([string]$Domain) {
    $publicIp = ''
    try { $publicIp = (Invoke-WebRequest -Uri 'https://api.ipify.org' -UseBasicParsing -TimeoutSec 5).Content.Trim() } catch { }
    Write-Info "IP publica de esta maquina: $(if ($publicIp) { $publicIp } else { 'desconocida' })"
    $bad = $false
    foreach ($h in @("cloud.$Domain", "registry.$Domain", "portainer.$Domain", "prueba-dns.$Domain")) {
        $ip = ''
        try { $ip = (Resolve-DnsName -Name $h -Type A -DnsOnly -ErrorAction Stop | Where-Object { $_.Type -eq 'A' } | Select-Object -First 1).IPAddress } catch { }
        if (-not $ip) { Write-Warn "$h no resuelve a ninguna IP"; $bad = $true }
        elseif ($publicIp -and $ip -ne $publicIp) { Write-Warn "$h apunta a $ip (esta maquina es $publicIp)"; $bad = $true }
        else { Write-Ok "$h -> $ip" }
    }
    if ($bad) {
        Write-Warn "Sin DNS correcto Let's Encrypt no podra emitir los certificados HTTPS."
        Write-Warn "Crea un registro A comodin en tu proveedor DNS:  *.$Domain -> IP"
        if (-not (Read-YesNo "Continuar igualmente? (los certificados se emitiran solos cuando el DNS este bien)" $true)) { exit 1 }
    }
}

function Read-AdminPassword {
    if ($AssumeYes) { $script:Cfg['ADMIN_PASSWORD'] = New-Secret 20; $script:GeneratedPassword = $true; return }
    Write-Host "  Contrasena: minimo 12 caracteres con mayuscula, minuscula, numero y simbolo,"
    Write-Host "  sin palabras ni secuencias faciles de adivinar (password, 1234, admin, tu email...)"
    Write-Host "  y sin espacios, comillas ni \. Dejala vacia para generar una segura."
    while ($true) {
        $p1 = Read-Secret "Contrasena"
        if ([string]::IsNullOrEmpty($p1)) { $script:Cfg['ADMIN_PASSWORD'] = New-Secret 20; $script:GeneratedPassword = $true; return }
        $problem = Get-AdminPasswordProblem $p1
        if ($problem) { Write-Host "  No es segura: $problem." -ForegroundColor Red; continue }
        $p2 = Read-Secret "Repite la contrasena"
        if ($p1 -ceq $p2) { $script:Cfg['ADMIN_PASSWORD'] = $p1; return }
        Write-Host "  No coinciden."
    }
}

function Read-Configuration {
    Write-Section "Configuracion"
    $modeDefault = 1; if ((Get-Cfg 'CENIT_MODE') -eq 'production') { $modeDefault = 2 }
    Write-Host "  Donde se instala?"
    $mode = Read-Choice "Elige" @(
        'Local: este ordenador, se accede por http://cloud.localhost',
        "Produccion: servidor con dominio propio y HTTPS automatico (Let's Encrypt)") $modeDefault

    if ($mode -eq 2) {
        $script:Cfg['CENIT_MODE'] = 'production'
        $domainDefault = Get-Cfg 'DOMAIN'; if ($domainDefault -eq 'localhost') { $domainDefault = '' }
        while ($true) {
            $d = (Read-Value "Dominio (ej: midominio.com, sin http://)" $domainDefault).ToLowerInvariant()
            if (Test-Domain $d) { $script:Cfg['DOMAIN'] = $d; break }
            if ($AssumeYes) { Stop-WithError "Dominio no valido: '$d'" }
            Write-Host "  Dominio no valido."
        }
        $script:Cfg['REGISTRY_HOST'] = "registry.$(Get-Cfg 'DOMAIN')"
        Test-Dns (Get-Cfg 'DOMAIN')
    } else {
        $script:Cfg['CENIT_MODE'] = 'local'
        $script:Cfg['DOMAIN'] = 'localhost'
        $port = Get-Cfg 'REGISTRY_LOCAL_PORT'; if (-not $port) { $port = '5000' }
        $script:Cfg['REGISTRY_HOST'] = "localhost:$port"
        $portDefault = Get-Cfg 'HTTP_PORT'; if (-not $portDefault) { $portDefault = '80' }
        if ($portDefault -eq '80' -and (Get-PortListener 80) -and -not (Test-CenitTraefikRunning)) {
            Write-Warn "El puerto 80 esta ocupado por otro programa (p.ej. IIS): se usara otro puerto (las direcciones seran http://cloud.localhost:<puerto>)."
            $portDefault = '8880'
        }
        while ($true) {
            $hp = Read-Value "Puerto HTTP" $portDefault
            $n = 0
            if ([int]::TryParse($hp, [ref]$n) -and $n -ge 1 -and $n -le 65535) { $script:Cfg['HTTP_PORT'] = [string]$n; break }
            if ($AssumeYes) { Stop-WithError "Puerto no valido" }
            Write-Host "  Puerto no valido."
        }
    }

    Write-Host ""
    Write-Host "  Cuenta de administrador (la misma para el portal, Portainer, Traefik,"
    Write-Host "  el registry y OpenObserve; usuario 'admin' donde se pide usuario)."
    $emailDefault = Get-Cfg 'ADMIN_EMAIL'
    if (-not $emailDefault -and (Get-Cfg 'CENIT_MODE') -eq 'local') { $emailDefault = 'admin@cenit.local' }
    while ($true) {
        $e = Read-Value "Email del administrador" $emailDefault
        if (Test-Email $e) { $script:Cfg['ADMIN_EMAIL'] = $e; break }
        if ($AssumeYes) { Stop-WithError "Email no valido" }
        Write-Host "  Email no valido."
    }

    $currentPw = Get-Cfg 'ADMIN_PASSWORD'
    $weak = if ($currentPw) { Get-AdminPasswordProblem $currentPw } else { '' }
    if ($weak) {
        Write-Warn "La contrasena de administrador actual no es segura: $weak."
        if ($AssumeYes) { Write-Warn "Cambiala reconfigurando sin --yes." }
    }
    if ($currentPw -and ($AssumeYes -or (-not $weak -and (Read-YesNo "Mantener la contrasena de administrador actual?" $true)))) {
        # se mantiene
    } else {
        if ($currentPw) { Write-Warn "Portainer y OpenObserve guardan su propia contrasena: cambiala tambien desde sus paneles." }
        Read-AdminPassword
    }

    if ((Get-Cfg 'CENIT_MODE') -eq 'production') {
        $acme = Get-Cfg 'ACME_EMAIL'; if (-not $acme) { $acme = Get-Cfg 'ADMIN_EMAIL' }
        $script:Cfg['ACME_EMAIL'] = Read-Value "Email para avisos de Let's Encrypt" $acme
    }

    Write-Host ""
    Write-Info "SQL Server de las aplicaciones que se desplieguen:"
    $sqlDefault = if ((Get-Cfg 'APPS_SQL_MODE') -eq 'shared') { 2 } else { 1 }
    $sqlChoice = Read-Choice "Un SQL Server por aplicacion o un motor comun?" @(
        'Uno por aplicacion: cada app usa la imagen SQL que traiga su compose',
        'Motor comun: todas usan el SQL Server de la plataforma, con un login por cliente') $sqlDefault
    if ($sqlChoice -eq 2) {
        $script:Cfg['APPS_SQL_MODE'] = 'shared'
        Write-Info "Se desactivaran las imagenes SQL de los compose y sus cadenas de conexion apuntaran al SQL comun."
        Write-Info "Las aplicaciones tienen que crear sus bases al arrancar (p. ej. Flexygo despliega su dacpac)."
        if ((Get-Cfg 'MSSQL_PID') -eq 'Express') {
            Write-Warn "El SQL de la plataforma es Express: todas las aplicaciones compartiran sus limites"
            Write-Warn "(~1,4 GB de cache, 4 nucleos, 10 GB por base). Para muchas apps usa Standard (MSSQL_PID en infra\.env)."
        }
    } else { $script:Cfg['APPS_SQL_MODE'] = 'dedicated' }

    Write-Host ""
    Write-Warn "Si habilitas el registro de usuarios externos, cualquier persona podra crearse una cuenta"
    Write-Warn "en el portal y desplegar aplicaciones en este servidor bajo su propia organizacion."
    Write-Info "Es el valor inicial: si ya lo has cambiado en Administracion > Ajustes del portal, manda ese."
    if (Read-YesNo "Habilitar el registro de usuarios externos?" ((Get-Cfg 'REGISTRATION_ENABLED') -eq 'true')) {
        $script:Cfg['REGISTRATION_ENABLED'] = 'true'
    } else { $script:Cfg['REGISTRATION_ENABLED'] = 'false' }

    Write-Host ""
    $smtpDefault = [bool](Get-Cfg 'SMTP_HOST')
    if (Read-YesNo "Configurar SMTP para enviar emails (confirmacion de registro, recuperar contrasena)?" $smtpDefault) {
        $script:Cfg['SMTP_HOST'] = Read-Value "Servidor SMTP" (Get-Cfg 'SMTP_HOST')
        $smtpPort = Get-Cfg 'SMTP_PORT'; if (-not $smtpPort) { $smtpPort = '587' }
        $script:Cfg['SMTP_PORT'] = Read-Value "Puerto" $smtpPort
        $script:Cfg['SMTP_USER'] = Read-Value "Usuario" (Get-Cfg 'SMTP_USER')
        $sp = Read-Secret "Contrasena SMTP (vacio = mantener la actual)"
        if ($sp) { $script:Cfg['SMTP_PASSWORD'] = $sp }
        if (Test-ForbiddenChars (Get-Cfg 'SMTP_PASSWORD')) { Stop-WithError "La contrasena SMTP no puede contener espacios, comillas ni \." }
        $from = Get-Cfg 'SMTP_FROM'; if (-not $from) { $from = "no-reply@$(Get-Cfg 'DOMAIN')" }
        $script:Cfg['SMTP_FROM'] = Read-Value "Remitente (From)" $from
        if ((Get-Cfg 'REGISTRATION_ENABLED') -eq 'true' -and (Read-YesNo "Exigir confirmacion de email a los usuarios que se registren?" $true)) {
            $script:Cfg['REQUIRE_EMAIL_CONFIRMATION'] = 'true'
        } else { $script:Cfg['REQUIRE_EMAIL_CONFIRMATION'] = 'false' }
    } else {
        foreach ($k in @('SMTP_HOST', 'SMTP_USER', 'SMTP_PASSWORD', 'SMTP_FROM')) { $script:Cfg[$k] = '' }
        $script:Cfg['REQUIRE_EMAIL_CONFIRMATION'] = 'false'
    }
}

# ----------------------------------------------------------------------------
# Portainer: admin, API key, endpoint y registry, todo por API
# ----------------------------------------------------------------------------
function Invoke-Portainer([string]$Method, [string]$Path, $Body = $null, [string]$FormBody = '') {
    return (Invoke-JsonApi "http://127.0.0.1:$(Get-Cfg 'PORTAINER_LOCAL_PORT')$Path" $Method $script:PtHeaders $Body $FormBody)
}

# Devuelve { Status, Json, Raw } sin lanzar excepción en errores HTTP (igual en PS 5.1 y 7).
function Invoke-JsonApi([string]$Uri, [string]$Method, [hashtable]$Headers, $Body = $null, [string]$FormBody = '', [string]$RawBody = '', [string]$RawContentType = 'text/plain') {
    $params = @{
        Uri             = $Uri
        Method          = $Method
        Headers         = $Headers.Clone()
        UseBasicParsing = $true
        TimeoutSec      = 60
    }
    if ($null -ne $Body) {
        $params.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 5 -Compress))
        $params.ContentType = 'application/json; charset=utf-8'
    } elseif ($FormBody) {
        $params.Body = $FormBody
        $params.ContentType = 'application/x-www-form-urlencoded'
    }
    elseif ($RawBody) {
        $params.Body = [Text.Encoding]::UTF8.GetBytes($RawBody)
        $params.ContentType = $RawContentType
    }
    $status = 0; $raw = ''
    try {
        $r = Invoke-WebRequest @params
        $status = [int]$r.StatusCode
        # Windows PowerShell 5.1 decodifica como ISO-8859-1 las respuestas JSON sin charset: forzamos UTF-8
        # (si no, los textos con acentos o comillas tipograficas, como el EULA de Nexus, llegan alterados).
        $raw = [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray())
    } catch {
        $resp = $_.Exception.Response
        if ($resp) { $status = [int]$resp.StatusCode }
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $raw = $_.ErrorDetails.Message }
        elseif ($resp -and ($resp | Get-Member -Name GetResponseStream)) {
            try { $raw = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } catch { }
        }
    }
    $json = $null
    if ($raw) { try { $json = $raw | ConvertFrom-Json } catch { } }
    return [pscustomobject]@{ Status = $status; Json = $json; Raw = $raw }
}

function Wait-Portainer {
    for ($i = 0; $i -lt 60; $i++) {
        if ((Invoke-Portainer GET '/api/system/status').Status -eq 200) { return }
        Start-Sleep -Seconds 2
    }
    Stop-WithError "Portainer no responde en 127.0.0.1:$(Get-Cfg 'PORTAINER_LOCAL_PORT'). Mira: cd infra; docker compose logs portainer"
}

function Get-PortainerSetupToken {
    Push-Location $InfraDir
    try { $logs = Invoke-Native { docker compose logs --no-log-prefix portainer 2>&1 } } finally { Pop-Location }
    $m = [regex]::Matches(($logs -join "`n"), 'setup_token=([0-9a-f]+)')
    if ($m.Count -gt 0) { return $m[$m.Count - 1].Groups[1].Value }
    return ''
}

function Initialize-PortainerAdmin([string]$Password) {
    $script:PtHeaders = @{ 'X-Setup-Token' = (Get-PortainerSetupToken) }
    return (Invoke-Portainer POST '/api/users/admin/init' @{ Username = $AdminUser; Password = $Password })
}

function Set-PortainerConfiguration {
    Write-Section "Configurando Portainer"
    Wait-Portainer
    $script:PtHeaders = @{}
    $authenticated = $false

    # 1) ¿La API key guardada sigue siendo válida?
    if (Get-Cfg 'PORTAINER_API_KEY') {
        $script:PtHeaders = @{ 'X-API-Key' = (Get-Cfg 'PORTAINER_API_KEY') }
        if ((Invoke-Portainer GET '/api/endpoints').Status -eq 200) { $authenticated = $true; Write-Ok "API key existente valida" }
        else { $script:PtHeaders = @{} }
    }

    if (-not $authenticated) {
        $password = Get-Cfg 'ADMIN_PASSWORD'
        # 2) Crear el admin si Portainer está recién instalado (exige el setup token de su log).
        if ((Invoke-Portainer GET '/api/users/admin/check').Status -ne 204) {
            $r = Initialize-PortainerAdmin $password
            if ($r.Status -ne 200) {
                # Portainer se bloquea si no se inicializa en 5 minutos: reiniciar y reintentar.
                Write-Info "Reiniciando Portainer para inicializarlo..."
                Invoke-Compose restart portainer
                Wait-Portainer
                $r = Initialize-PortainerAdmin $password
                if ($r.Status -ne 200) { Stop-WithError "No se pudo crear el admin de Portainer (HTTP $($r.Status)): $($r.Raw)" }
            }
            $script:PtHeaders = @{}
            Write-Ok "Usuario admin de Portainer creado"
        }

        # 3) Login (si el admin ya existía con otra contraseña, se pregunta).
        $jwt = ''
        for ($attempt = 0; $attempt -lt 4; $attempt++) {
            $r = Invoke-Portainer POST '/api/auth' @{ Username = $AdminUser; Password = $password }
            if ($r.Status -eq 200) { $jwt = $r.Json.jwt; break }
            if ($AssumeYes) { break }
            Write-Warn "La contrasena no es valida para el usuario '$AdminUser' de Portainer (se cambio desde su panel?)."
            $password = Read-Secret "Contrasena actual del admin de Portainer"
        }
        if (-not $jwt) { Stop-WithError "No se pudo iniciar sesion en Portainer." }
        $script:PtHeaders = @{ Authorization = "Bearer $jwt" }

        # 4) API key para el portal.
        $users = (Invoke-Portainer GET '/api/users').Json
        $uid = 1
        $me = @($users | Where-Object { $_.Username -eq $AdminUser })
        if ($me.Count -gt 0) { $uid = $me[0].Id }
        $r = Invoke-Portainer POST "/api/users/$uid/tokens" @{ password = $password; description = "ahora-cenit $(Get-Date -Format yyyy-MM-dd)" }
        if ($r.Status -lt 200 -or $r.Status -ge 300) { Stop-WithError "No se pudo crear la API key de Portainer (HTTP $($r.Status)): $($r.Raw)" }
        $script:Cfg['PORTAINER_API_KEY'] = $r.Json.rawAPIKey
        $script:PtHeaders = @{ 'X-API-Key' = $r.Json.rawAPIKey }
        Write-Ok "API key de Portainer generada"
    }

    # 5) Entorno Docker local.
    $endpoints = @((Invoke-Portainer GET '/api/endpoints').Json | Where-Object { $_.Type -eq 1 })
    if ($endpoints.Count -gt 0) {
        $eid = $endpoints[0].Id
    } else {
        $r = Invoke-Portainer POST '/api/endpoints' -FormBody 'Name=local&EndpointCreationType=1'
        if (-not $r.Json -or -not $r.Json.Id) { Stop-WithError "No se pudo crear el entorno local en Portainer: $($r.Raw)" }
        $eid = $r.Json.Id
    }
    $script:Cfg['PORTAINER_ENDPOINT_ID'] = [string]$eid
    Write-Ok "Entorno Docker local (EndpointId=$eid)"

    # 6) Registry interno.
    $regUrl = Get-Cfg 'REGISTRY_HOST'
    $body = @{ Name = 'ahora-cenit'; Type = 3; URL = $regUrl; BaseURL = ''; Authentication = $true; Username = $AdminUser; Password = (Get-Cfg 'ADMIN_PASSWORD') }
    $existing = @((Invoke-Portainer GET '/api/registries').Json | Where-Object { $_.Name -eq 'ahora-cenit' })
    if ($existing.Count -gt 0) { $r = Invoke-Portainer PUT "/api/registries/$($existing[0].Id)" $body }
    else { $r = Invoke-Portainer POST '/api/registries' $body }
    if ($r.Status -lt 200 -or $r.Status -ge 300) { Stop-WithError "No se pudo registrar el registry en Portainer (HTTP $($r.Status)): $($r.Raw)" }
    Write-Ok "Registry $regUrl dado de alta en Portainer"

    Export-EnvFile
}

# ----------------------------------------------------------------------------
# Nexus Repository CE (NuGet): admin, EULA, feeds public/internal y usuario de automatizacion
# ----------------------------------------------------------------------------
$script:NxAuth = @{}
$NexusEulaUrl = 'https://links.sonatype.com/products/nxrm/ce-eula'

function Set-NexusCredentials([string]$User, [string]$Password) {
    $basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${User}:$Password"))
    $script:NxAuth = @{ Authorization = "Basic $basic" }
}

function Invoke-Nexus([string]$Method, [string]$Path, $Body = $null, [string]$Raw = '') {
    return (Invoke-JsonApi "http://127.0.0.1:$(Get-Cfg 'NUGET_LOCAL_PORT')/service/rest/v1$Path" $Method $script:NxAuth $Body '' $Raw 'text/plain')
}

function Set-NexusConfiguration {
    Write-Section "Configurando el repositorio NuGet (Nexus)"
    Write-Info "Esperando a que Nexus arranque (la primera vez tarda 1-3 minutos)..."
    $ready = $false
    for ($i = 0; $i -lt 120; $i++) {
        $code = Invoke-Native { curl.exe -s -o NUL -w '%{http_code}' --max-time 5 "http://127.0.0.1:$(Get-Cfg 'NUGET_LOCAL_PORT')/service/rest/v1/status/writable" }
        if ("$code".Trim() -eq '200') { $ready = $true; break }
        Start-Sleep -Seconds 3
    }
    if (-not $ready) { Stop-WithError "Nexus no arranca. Mira: cd infra; docker compose logs nexus" }

    # Contrasena de admin: la misma que el resto. Nexus arranca con una aleatoria en /nexus-data/admin.password.
    Set-NexusCredentials 'admin' (Get-Cfg 'ADMIN_PASSWORD')
    if ((Invoke-Nexus GET '/security/anonymous').Status -ne 200) {
        Push-Location $InfraDir
        try { $init = (Invoke-Native { docker compose exec -T nexus cat /nexus-data/admin.password 2>$null }) -join '' } finally { Pop-Location }
        $init = "$init".Trim()
        if (-not $init) {
            Write-Warn "No se pudo entrar en Nexus como admin (se cambio su contrasena?). Configuralo desde su panel."
            return
        }
        Set-NexusCredentials 'admin' $init
        $r = Invoke-Nexus PUT '/security/users/admin/change-password' $null (Get-Cfg 'ADMIN_PASSWORD')
        if ($r.Status -lt 200 -or $r.Status -ge 300) { Stop-WithError "No se pudo fijar la contrasena de admin de Nexus (HTTP $($r.Status)): $($r.Raw)" }
        Set-NexusCredentials 'admin' (Get-Cfg 'ADMIN_PASSWORD')
        Write-Ok "Contrasena de administrador de Nexus fijada"
    }

    # Sin aceptar el EULA de la edicion Community, Nexus no deja subir ni bajar paquetes.
    $eula = Invoke-Nexus GET '/system/eula'
    if ($eula.Json -and -not $eula.Json.accepted) {
        Write-Host "  Nexus Repository Community Edition se rige por el EULA de Sonatype: $NexusEulaUrl"
        Write-Host "  (edicion gratuita con limites: 40.000 componentes y 100.000 peticiones al dia)."
        if (Read-YesNo "Aceptas ese EULA?" $true) {
            $eula.Json.accepted = $true
            $r = Invoke-Nexus POST '/system/eula' $eula.Json
            if ($r.Status -lt 200 -or $r.Status -ge 300) { Stop-WithError "No se pudo aceptar el EULA de Nexus (HTTP $($r.Status)): $($r.Raw)" }
            Write-Ok "EULA de Nexus Community Edition aceptado"
        } else {
            Write-Warn "Sin aceptar el EULA, Nexus no permitira subir ni descargar paquetes hasta que lo aceptes desde su panel."
        }
    }

    # Feeds: internal (lectura y escritura con login) y public (lectura anonima, escritura con login).
    foreach ($repo in 'internal', 'public') {
        if ((Invoke-Nexus GET "/repositories/$repo").Status -ne 200) {
            $r = Invoke-Nexus POST '/repositories/nuget/hosted' @{
                name = $repo; online = $true
                storage = @{ blobStoreName = 'default'; strictContentTypeValidation = $true; writePolicy = 'allow' }
            }
            if ($r.Status -lt 200 -or $r.Status -ge 300) { Stop-WithError "No se pudo crear el feed '$repo' en Nexus (HTTP $($r.Status)): $($r.Raw)" }
        }
        Write-Ok "Feed NuGet '$repo'"
    }

    # Acceso anonimo limitado a LEER el feed public. Solo la primera vez (marca: existe el rol public-read).
    if ((Invoke-Nexus GET '/security/roles/public-read').Status -ne 200) {
        $r = Invoke-Nexus POST '/security/roles' @{
            id = 'public-read'; name = 'public-read'; description = 'Lectura anonima del feed public'
            privileges = @('nx-repository-view-nuget-public-read', 'nx-repository-view-nuget-public-browse'); roles = @()
        }
        if ($r.Status -lt 200 -or $r.Status -ge 300) { Stop-WithError "No se pudo crear el rol public-read en Nexus (HTTP $($r.Status)): $($r.Raw)" }
        Invoke-Nexus PUT '/security/users/anonymous' @{
            userId = 'anonymous'; firstName = 'Anonymous'; lastName = 'User'; emailAddress = 'anonymous@example.org'
            source = 'default'; status = 'active'; roles = @('public-read')
        } | Out-Null
        Invoke-Nexus PUT '/security/anonymous' @{ enabled = $true; userId = 'anonymous'; realmName = 'NexusAuthorizingRealm' } | Out-Null
        # Repositorios de ejemplo (maven, nuget) que trae Nexus: no hacen falta.
        foreach ($d in 'maven-releases', 'maven-snapshots', 'maven-central', 'maven-public', 'nuget-hosted', 'nuget.org-proxy', 'nuget-group') {
            Invoke-Nexus DELETE "/repositories/$d" | Out-Null
        }
        Write-Ok "Acceso anonimo: solo lectura del feed public"
    }

    # Usuario de automatizacion: cenit-admin, con el token de plataforma como contrasena.
    $found = Invoke-Nexus GET "/security/users?userId=$PlatformUser"
    if (@($found.Json).Count -gt 0) {
        Invoke-Nexus PUT "/security/users/$PlatformUser/change-password" $null (Get-Cfg 'CENIT_TOKEN') | Out-Null
    } else {
        $r = Invoke-Nexus POST '/security/users' @{
            userId = $PlatformUser; firstName = 'cenit'; lastName = 'admin'; emailAddress = (Get-Cfg 'ADMIN_EMAIL')
            password = (Get-Cfg 'CENIT_TOKEN'); status = 'active'; roles = @('nx-admin')
        }
        if ($r.Status -lt 200 -or $r.Status -ge 300) { Stop-WithError "No se pudo crear el usuario $PlatformUser en Nexus (HTTP $($r.Status)): $($r.Raw)" }
    }
    Write-Ok "Usuario $PlatformUser (token de plataforma) en Nexus"
}

# ----------------------------------------------------------------------------
# Arranque y comprobaciones
# ----------------------------------------------------------------------------
function Start-Stack {
    Write-Section "Descargando imagenes"
    # Si no hay conexión pero las imágenes ya están en local, seguimos con ellas.
    Push-Location $InfraDir
    try { Invoke-Native { docker compose pull --ignore-pull-failures } } finally { Pop-Location }
    # Igual que docker compose: las variables de entorno mandan sobre el .env.
    $img = if ($env:APP_IMAGE) { $env:APP_IMAGE } else { Get-Cfg 'APP_IMAGE' }
    $ver = if ($env:APP_VERSION) { $env:APP_VERSION } else { Get-Cfg 'APP_VERSION' }
    $appImage = "${img}:$ver"
    Invoke-Native { docker image inspect $appImage *> $null }
    if ($LASTEXITCODE -ne 0) { Stop-WithError "No se pudo descargar la imagen del portal ($appImage). Si es privada, ejecuta antes: docker login ghcr.io" }
    Write-Section "Arrancando servicios"
    Invoke-Compose up -d --remove-orphans
}

function Wait-App {
    Write-Info "Esperando a que el portal responda (la primera vez puede tardar 1-2 minutos)..."
    $domain = Get-Cfg 'DOMAIN'
    for ($i = 0; $i -lt 90; $i++) {
        # Mientras Let's Encrypt emite el certificado Traefik sirve uno propio: -k.
        if ((Get-Cfg 'CENIT_MODE') -eq 'production') {
            $code = Invoke-Native { curl.exe -sk -o NUL -w '%{http_code}' --max-time 5 --resolve "cloud.${domain}:443:127.0.0.1" "https://cloud.$domain/api/config" }
        } else {
            $code = Invoke-Native { curl.exe -s -o NUL -w '%{http_code}' --max-time 5 -H "Host: cloud.$domain" "http://127.0.0.1:$(Get-Cfg 'HTTP_PORT')/api/config" }
        }
        if ("$code".Trim() -eq '200') { Write-Ok "Portal funcionando"; return }
        Start-Sleep -Seconds 3
    }
    Write-Warn "El portal todavia no responde. Revisa: cd infra; docker compose logs app"
}

# Tabla con bordes y una columna de color cada una. Cada fila es un array de 4
# celdas; la fila '-' pinta un separador.
function Write-Table([object[]]$Rows) {
    $colors = @('White', 'Cyan', 'Green', 'Yellow')
    $w = @(0, 0, 0, 0)
    foreach ($r in $Rows) {
        if ($r -is [string]) { continue }
        for ($i = 0; $i -lt 4; $i++) { $w[$i] = [Math]::Max($w[$i], "$($r[$i])".Length) }
    }
    $line = {
        param($left, $mid, $right)
        $parts = for ($i = 0; $i -lt 4; $i++) { [string]::new([char]0x2500, $w[$i] + 2) }
        Write-Host ("  $left" + ($parts -join $mid) + $right) -ForegroundColor DarkCyan
    }
    & $line ([char]0x250C) ([char]0x252C) ([char]0x2510)
    $header = $true
    foreach ($r in $Rows) {
        if ($r -is [string]) { & $line ([char]0x251C) ([char]0x253C) ([char]0x2524); continue }
        Write-Host "  $([char]0x2502)" -ForegroundColor DarkCyan -NoNewline
        for ($i = 0; $i -lt 4; $i++) {
            $color = if ($header) { 'White' } else { $colors[$i] }
            Write-Host (" " + "$($r[$i])".PadRight($w[$i]) + " ") -ForegroundColor $color -NoNewline
            Write-Host ([char]0x2502) -ForegroundColor DarkCyan -NoNewline
        }
        Write-Host ""
        if ($header) { & $line ([char]0x255E) ([char]0x256A) ([char]0x2561); $header = $false }
    }
    & $line ([char]0x2514) ([char]0x2534) ([char]0x2518)
}

function Show-Summary {
    $s = Get-Cfg 'PUBLIC_SCHEME'; $d = Get-Cfg 'DOMAIN'; $p = Get-Cfg 'PUBLIC_PORT_SUFFIX'
    $email = Get-Cfg 'ADMIN_EMAIL'; $pass = Get-Cfg 'ADMIN_PASSWORD'; $token = Get-Cfg 'CENIT_TOKEN'
    $reg = Get-Cfg 'REGISTRY_HOST'; $nu = Get-Cfg 'NUGET_PUBLIC_URL'
    Write-Section "Todo listo"
    Write-Host ""
    Write-Table @(
        , @('Servicio', 'URL', 'Usuario', 'Clave')
        , @('Portal ahora-cenit', "${s}://cloud.$d$p", $email, $pass); '-'
        , @('Portainer', "${s}://portainer.$d$p", $AdminUser, $pass); '-'
        , @('Traefik', "${s}://traefik.$d$p", $AdminUser, $pass); '-'
        , @('OpenObserve', "${s}://telemetry.$d$p", $email, $pass); '-'
        , @('Registry Docker', $reg, $AdminUser, $pass)
        , @('', '', $PlatformUser, $token); '-'
        , @('Repositorio NuGet', $nu, $AdminUser, $pass)
        , @('', '', $PlatformUser, $token); '-'
        , @('API del portal', "${s}://cloud.$d$p/api", 'Bearer / X-Api-Key', $token); '-'
        , @('SQL Server', 'sqlserver:1433 (red interna)', 'sa', (Get-Cfg 'DB_SA_PASSWORD')); '-'
        , @('Apps desplegadas', "${s}://<nombre>.$d$p", '', '')
    )
    Write-Host ""
    Write-Host "  Feeds NuGet" -ForegroundColor White
    Write-Host "     public   : ${nu}repository/public/index.json    (leer: sin login; escribir: con login)"
    Write-Host "     internal : ${nu}repository/internal/index.json  (leer y escribir: con login)"
    Write-Host ""
    Write-Host "  Token de plataforma (CENIT_TOKEN, usuario $PlatformUser)" -ForegroundColor White
    Write-Host "     API    : curl -H `"Authorization: Bearer $token`" ${s}://cloud.$d$p/api/products"
    Write-Host "     Docker : docker login $reg -u $PlatformUser -p $token"
    Write-Host ""
    if ($script:GeneratedPassword) {
        Write-Host "  La contrasena de administrador se ha generado automaticamente: apuntala." -ForegroundColor Yellow
    }
    if ((Get-Cfg 'APPS_SQL_MODE') -eq 'shared') {
        Write-Host "  SQL Server de las aplicaciones: motor comun (el de la plataforma, un login por cliente)."
    } else {
        Write-Host "  SQL Server de las aplicaciones: uno por aplicacion (la imagen SQL de cada compose)."
    }
    if ((Get-Cfg 'REGISTRATION_ENABLED') -eq 'true') {
        Write-Host "  Registro de usuarios externos (valor inicial): HABILITADO (cualquiera puede crearse una cuenta y desplegar)" -ForegroundColor Yellow
    } else {
        Write-Host "  Registro de usuarios externos (valor inicial): deshabilitado (solo el administrador da de alta usuarios)."
    }
    Write-Host "  Todas las credenciales estan guardadas en infra\.env."
    if ((Get-Cfg 'CENIT_MODE') -eq 'production') {
        Write-Host "  Los certificados HTTPS se emiten solos en el primer acceso a cada subdominio."
        Write-Warn "Docker Desktop solo funciona con la sesion de Windows iniciada. Para un servidor 24x7 usa Linux o Windows Server."
    }
    Write-Host ""
    Write-Host "  Actualizar: .\install.ps1 --update     Estado: .\install.ps1 --status"
}

# ----------------------------------------------------------------------------
# Comandos (Windows 10/11 con Docker Desktop)
# ----------------------------------------------------------------------------
function Confirm-Installed {
    if (-not (Test-Path $EnvFile)) { Stop-WithError "No hay ninguna instalacion (falta infra\.env). Ejecuta primero: .\install.ps1" }
    Import-EnvFile
}

function Invoke-Install {
    Write-Section "ahora-cenit - instalacion"
    if (Test-Path $EnvFile) {
        Import-EnvFile
        Write-Warn "Ya hay una instalacion en esta carpeta (modo $(Get-Cfg 'CENIT_MODE'), dominio $(Get-Cfg 'DOMAIN'))."
        $c = Read-Choice "Que quieres hacer?" @(
            'Actualizar (equivale a --update)',
            'Reconfigurar (volver a responder las preguntas; se conservan los datos)',
            'Salir') 1
        if ($c -eq 1) { Invoke-Update; return }
        if ($c -eq 3) { return }
    }

    Confirm-System
    Confirm-DockerDesktop
    Remove-LegacyContainers
    Read-Configuration
    if (-not (Test-Path $EnvFile)) { Resolve-ExistingData }
    Set-Defaults
    Export-EnvFile
    Write-Ok "Configuracion guardada en infra\.env"

    Write-Htpasswd
    Confirm-Ports
    Open-Firewall
    Confirm-Network
    Start-Stack
    Set-PortainerConfiguration
    Set-NexusConfiguration
    Sync-PlatformToken
    Invoke-Compose up -d --remove-orphans   # recrea el portal con la API key de Portainer
    Wait-App
    Show-Summary
}

function Invoke-Update {
    Confirm-Installed
    if (-not $SkipGitPull -and (Test-Path (Join-Path $ScriptRoot '.git')) -and (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Section "Actualizando el codigo (git pull)"
        Invoke-Native { git -C $ScriptRoot pull --ff-only }
        if ($LASTEXITCODE -eq 0) {
            # El propio instalador puede haber cambiado: continuar con la versión nueva.
            $next = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '--update', '--skip-git-pull')
            if ($AssumeYes) { $next += '--yes' }
            if ($NoBackup) { $next += '--no-backup' }
            if ($HttpPortArg) { $next += '--http-port'; $next += $HttpPortArg }
            & (Get-HostExe) @next
            exit $LASTEXITCODE
        }
        Write-Warn "git pull ha fallado (cambios locales?). Continuo con el codigo actual."
    }

    Write-Section "Actualizando ahora-cenit"
    Confirm-DockerDesktop
    if (-not $NoBackup -and (Read-YesNo "Hacer copia de seguridad antes de actualizar?" $true)) { Invoke-Backup }
    Set-Defaults
    Export-EnvFile
    if (-not (Test-Path (Join-Path $AuthDir 'htpasswd'))) { Write-Htpasswd }
    Remove-LegacyContainers
    Confirm-Network
    Start-Stack
    Set-PortainerConfiguration
    Set-NexusConfiguration
    Sync-PlatformToken
    Invoke-Compose up -d --remove-orphans
    Write-Info "Limpiando imagenes antiguas..."
    Invoke-Native { docker image prune -f | Out-Null }
    Wait-App
    Show-Summary
}

function Invoke-Status {
    Confirm-Installed
    Write-Section "Servicios"
    Invoke-Compose ps
    Show-Summary
}

function Invoke-Backup {
    Confirm-Installed
    $dest = Join-Path $BackupRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Write-Section "Copia de seguridad en $dest"
    Write-Info "Parando servicios para copiar los datos de forma consistente..."
    Invoke-Compose stop
    try {
        foreach ($v in $Volumes) {
            Invoke-Native { docker volume inspect $v *> $null }
            if ($LASTEXITCODE -ne 0) { continue }
            Write-Info "Copiando $v"
            Invoke-Native { docker run --rm -v "${v}:/data:ro" -v "${dest}:/backup" $HelperImage tar czf "/backup/$v.tar.gz" -C /data . } "No se pudo copiar $v"
        }
        Copy-Item $EnvFile (Join-Path $dest 'env')
        if (Test-Path $AuthDir) { Copy-Item $AuthDir (Join-Path $dest 'auth') -Recurse }
    } finally {
        Write-Info "Arrancando servicios de nuevo..."
        Invoke-Compose up -d
    }
    Write-Ok "Copia completada: $dest"
}

function Invoke-Restore {
    if (-not $RestoreDir) { Stop-WithError "Indica la carpeta: .\install.ps1 --restore backups\AAAAMMDD-HHMMSS" }
    $dir = (Resolve-Path $RestoreDir -ErrorAction SilentlyContinue)
    if (-not $dir -or -not (Test-Path (Join-Path $dir 'env'))) { Stop-WithError "$RestoreDir no parece una copia de ahora-cenit (falta el fichero env)." }
    $dir = $dir.Path
    Write-Section "Restaurar desde $dir"
    Write-Warn "Se sustituiran TODOS los datos actuales por los de la copia."
    if (-not $AssumeYes -and -not (Read-YesNo "Continuar?" $false)) { exit 1 }
    Confirm-DockerDesktop
    if (Test-Path $EnvFile) { Push-Location $InfraDir; try { Invoke-Native { docker compose down *> $null } } finally { Pop-Location } }
    Copy-Item (Join-Path $dir 'env') $EnvFile -Force
    if (Test-Path (Join-Path $dir 'auth')) {
        Remove-Item $AuthDir -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item (Join-Path $dir 'auth') $AuthDir -Recurse
    }
    foreach ($f in Get-ChildItem -Path $dir -Filter '*.tar.gz') {
        $v = $f.Name -replace '\.tar\.gz$', ''
        Write-Info "Restaurando $v"
        Invoke-Native { docker volume rm -f $v *> $null }
        Invoke-Native { docker volume create $v | Out-Null } "No se pudo crear el volumen $v"
        Invoke-Native { docker run --rm -v "${v}:/data" -v "${dir}:/backup:ro" $HelperImage tar xzf "/backup/$($f.Name)" -C /data } "No se pudo restaurar $v"
    }
    Import-EnvFile
    Confirm-Network
    Invoke-Compose up -d --remove-orphans
    Wait-App
    Show-Summary
}

# ----------------------------------------------------------------------------
# Windows Server: distro WSL2 dedicada + install.sh dentro
# ----------------------------------------------------------------------------
function Invoke-Wsl([string]$Script, [string]$ErrorMessage = '') {
    Invoke-Native { wsl.exe -d $WslDistro -u root -- bash -c $Script } $ErrorMessage
}

function Test-WslDistro {
    $list = Invoke-Native { wsl.exe -l -q }
    return (@($list | ForEach-Object { "$_".Trim([char]0, ' ') }) -contains $WslDistro)
}

function Install-ServerWsl {
    Write-Section "WSL2 para Windows Server"
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 20348) { Stop-WithError "Se necesita Windows Server 2022 o posterior (WSL2). Este servidor es build $build." }
    if (-not (Get-ScheduledTask -TaskName $BootTaskName -ErrorAction SilentlyContinue)) {
        foreach ($p in 80, 443) {
            $conn = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($conn) {
                $name = if ($conn.OwningProcess -eq 4) { 'System (IIS / http.sys)' } else { (Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue).ProcessName }
                Stop-WithError "El puerto $p ya esta en uso por '$name'. Liberalo (p.ej. para IIS: Stop-Service W3SVC; Set-Service W3SVC -StartupType Disabled) y vuelve a ejecutar."
            }
        }
    }
    if (-not (Test-WslInstalled)) { Install-Wsl }
    Invoke-Native { wsl.exe --update *> $null }
    Invoke-Native { wsl.exe --set-default-version 2 *> $null }

    if (-not (Test-WslDistro)) {
        New-Item -ItemType Directory -Force -Path $WslHome | Out-Null
        $rootfs = Join-Path $WslHome 'ubuntu-rootfs.tar.gz'
        Write-Info "Descargando Ubuntu 24.04 para WSL..."
        Invoke-WebRequest -Uri $WslRootfsUrl -OutFile $rootfs -UseBasicParsing
        Write-Info "Creando la distro '$WslDistro'..."
        Invoke-Native { wsl.exe --import $WslDistro (Join-Path $WslHome 'disk') $rootfs --version 2 } "No se pudo importar la distro WSL"
        Remove-Item $rootfs -Force -ErrorAction SilentlyContinue
    }
    Write-Ok "Distro WSL '$WslDistro' lista"

    # systemd (para que Docker arranque solo) y root por defecto.
    Invoke-Wsl "printf '[boot]\nsystemd=true\n\n[user]\ndefault=root\n' > /etc/wsl.conf" "No se pudo configurar /etc/wsl.conf"

    # Sin localhostForwarding: el portproxy de Windows publica 80/443 hacia la IP de WSL.
    $wslConfig = Join-Path $env:USERPROFILE '.wslconfig'
    $content = ''
    if (Test-Path $wslConfig) { $content = Get-Content $wslConfig -Raw }
    if ($content -notmatch 'localhostForwarding') {
        if ($content -notmatch '\[wsl2\]') { $content = "[wsl2]`r`n" + $content }
        $content = $content -replace '\[wsl2\]', "[wsl2]`r`nlocalhostForwarding=false"
        Set-Content -Path $wslConfig -Value $content -Encoding ASCII
        Invoke-Native { wsl.exe --shutdown }
    } else {
        Invoke-Native { wsl.exe --terminate $WslDistro *> $null }
    }

    Register-BootTask
    Start-ScheduledTask -TaskName $BootTaskName
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        Invoke-Native { wsl.exe -d $WslDistro -u root -- true *> $null }
        if ($LASTEXITCODE -eq 0) { break }
    }
}

# Tarea programada al arrancar el servidor: mantiene viva la distro (y con
# ella Docker) y actualiza el portproxy 80/443 hacia la IP actual de WSL.
function Register-BootTask {
    New-Item -ItemType Directory -Force -Path $WslHome | Out-Null
    $bootScript = Join-Path $WslHome 'wsl-boot.ps1'
    @"
`$env:WSL_UTF8 = '1'
`$distro = '$WslDistro'
for (`$i = 0; `$i -lt 30; `$i++) {
    `$ip = ((wsl.exe -d `$distro -u root -- hostname -I) -join ' ').Trim().Split(' ')[0]
    if (`$ip) { break }
    Start-Sleep -Seconds 2
}
foreach (`$port in 80, 443) {
    netsh interface portproxy delete v4tov4 listenport=`$port listenaddress=0.0.0.0 | Out-Null
    netsh interface portproxy add v4tov4 listenport=`$port listenaddress=0.0.0.0 connectport=`$port connectaddress=`$ip | Out-Null
}
wsl.exe -d `$distro -u root -- sleep infinity
"@ | Set-Content -Path $bootScript -Encoding ASCII

    if (Get-ScheduledTask -TaskName $BootTaskName -ErrorAction SilentlyContinue) { return }
    Write-Host ""
    Write-Host "  Para que ahora-cenit arranque solo al reiniciar el servidor (sin iniciar sesion)"
    Write-Host "  se crea una tarea programada con tu usuario, $env:USERDOMAIN\$env:USERNAME."
    $pw = Read-Secret "Contrasena de Windows de $env:USERNAME"
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$bootScript`""
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName $BootTaskName -Action $action -Trigger $trigger -Settings $settings `
        -User "$env:USERDOMAIN\$env:USERNAME" -Password $pw -RunLevel Highest | Out-Null
    Write-Ok "Tarea programada '$BootTaskName' creada"
}

function Invoke-ServerFlow {
    if ($Command -eq 'install') {
        Install-ServerWsl
        # Copia del repo dentro de Linux (finales de línea LF y permisos reales),
        # con el mismo origin para que --update haga git pull desde allí.
        Invoke-Wsl "command -v git >/dev/null || (apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq git curl ca-certificates)" "No se pudo instalar git en WSL"
        $origin = ''
        if (Get-Command git -ErrorAction SilentlyContinue) { $origin = ((Invoke-Native { git -C $ScriptRoot remote get-url origin }) | Select-Object -Last 1) }
        $branch = ((Invoke-Native { git -C $ScriptRoot rev-parse --abbrev-ref HEAD }) | Select-Object -Last 1)
        if (-not $branch) { $branch = 'main' }
        $winPath = ((Invoke-Native { wsl.exe -d $WslDistro -u root -- wslpath -a ($ScriptRoot -replace '\\', '/') }) | Select-Object -Last 1).Trim()
        Invoke-Wsl "git config --global --add safe.directory '*'; if [ ! -d $WslRepoPath/.git ]; then git clone -b '$branch' '$winPath' $WslRepoPath; fi" "No se pudo copiar el repositorio dentro de WSL"
        if ($origin) { Invoke-Wsl "git -C $WslRepoPath remote set-url origin '$origin'" }
    } elseif (-not (Test-WslDistro)) {
        Stop-WithError "No hay ninguna instalacion en WSL. Ejecuta primero: .\install.ps1"
    } else {
        Start-ScheduledTask -TaskName $BootTaskName -ErrorAction SilentlyContinue
    }

    Write-Section "Ejecutando el instalador de Linux dentro de WSL"
    $linuxArgs = @($PassThroughArgs | ForEach-Object { "'$_'" }) -join ' '
    Invoke-Native { wsl.exe -d $WslDistro -u root -- bash -c "$WslRepoPath/install.sh $linuxArgs" }
    $exit = $LASTEXITCODE
    if ($Command -eq 'install' -and $exit -eq 0) {
        # La IP de WSL puede haber cambiado tras reiniciar la distro: refrescar portproxy.
        Stop-ScheduledTask -TaskName $BootTaskName -ErrorAction SilentlyContinue
        Start-ScheduledTask -TaskName $BootTaskName
        $installedMode = (Invoke-Wsl "grep -E '^CENIT_MODE=' $WslRepoPath/infra/.env") -join ''
        if ($installedMode -match 'production') {
            if (-not (Get-NetFirewallRule -Name 'AhoraCenit-HTTP' -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -Name 'AhoraCenit-HTTP' -DisplayName 'ahora-cenit HTTP/HTTPS' -Direction Inbound -Protocol TCP -LocalPort 80, 443 -Action Allow | Out-Null
                Write-Ok "Firewall de Windows: abiertos 80 y 443"
            }
        }
        Write-Host ""
        Write-Host "  Los datos y la configuracion viven dentro de WSL, en $WslRepoPath (distro '$WslDistro')." -ForegroundColor Cyan
        Write-Host "  Para entrar: wsl -d $WslDistro" -ForegroundColor Cyan
    }
    exit $exit
}

# ----------------------------------------------------------------------------
# Principal
# ----------------------------------------------------------------------------
$exitCode = 0
$isServer = (Get-CimInstance Win32_OperatingSystem).ProductType -ne 1

# Solo se necesita ser administrador en Windows Server o para instalar
# WSL/Docker Desktop; con Docker Desktop ya funcionando basta un usuario normal.
if (-not (Test-IsAdmin)) {
    Add-DockerToPath
    if ($isServer -or -not (Test-DockerReady)) {
        Write-Host "Se necesitan permisos de administrador. Relanzando..." -ForegroundColor Yellow
        Start-Process -FilePath (Get-HostExe) -Verb RunAs -ArgumentList (Get-ScriptArgs @('--elevated'))
        exit 0
    }
}

try {
    if ($isServer) {
        Invoke-ServerFlow
    } else {
        switch ($Command) {
            'install' { Invoke-Install }
            'update'  { Invoke-Update }
            'status'  { Invoke-Status }
            'backup'  { Invoke-Backup }
            'restore' { Invoke-Restore }
        }
    }
} catch {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($Elevated) { Read-Host "Pulsa Enter para cerrar" | Out-Null }
}
exit $exitCode
