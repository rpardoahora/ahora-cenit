#!/usr/bin/env bash
# =============================================================================
#  ahora-cenit - instalador para Linux
#
#  Instala (si falta) Docker y deja funcionando el stack completo:
#  Traefik + Portainer + Docker Registry + portal ahora-cenit + SQL Server +
#  OpenObserve, todo enlazado entre sí (Portainer configurado por API, con el
#  registry interno dado de alta y su API key inyectada en el portal).
#
#  Uso:
#    sudo ./install.sh              instalación interactiva (o reconfiguración)
#    sudo ./install.sh --update     actualiza código, imágenes y configuración
#    sudo ./install.sh --status     estado de los servicios y URLs
#    sudo ./install.sh --backup     copia de seguridad de datos y configuración
#    sudo ./install.sh --restore <carpeta>   restaura una copia de seguridad
#    ./install.sh --help
# =============================================================================
set -Eeuo pipefail

ORIG_ARGS=("$@")
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INFRA_DIR="$SCRIPT_DIR/infra"
ENV_FILE="$INFRA_DIR/.env"
AUTH_DIR="$INFRA_DIR/auth"
BACKUP_ROOT="$SCRIPT_DIR/backups"

JQ_IMAGE="ghcr.io/jqlang/jq:1.7.1"
HTPASSWD_IMAGE="httpd:2.4-alpine"
HELPER_IMAGE="alpine:3.20"
ADMIN_USER="admin"
PLATFORM_USER="cenit-admin"   # usuario de automatización (token de plataforma): registry y Nexus
VOLUMES=(cenit_sqlserver_data cenit_portainer_data cenit_registry_data cenit_openobserve_data cenit_nexus_data cenit_letsencrypt)
LEGACY_CONTAINERS=(traefik portainer app sqlserver openobserve)

# Orden y comentarios con los que se escribe infra/.env.
ENV_KEYS=(CENIT_MODE DOMAIN PUBLIC_SCHEME HTTP_PORT PUBLIC_PORT_SUFFIX COMPOSE_FILE COMPOSE_PATH_SEPARATOR ACME_EMAIL
  ADMIN_EMAIL ADMIN_PASSWORD DB_SA_PASSWORD MSSQL_PID JWT_SECRET APP_IMAGE APP_VERSION
  PORTAINER_API_KEY PORTAINER_ENDPOINT_ID PORTAINER_LOCAL_PORT REGISTRY_HOST REGISTRY_LOCAL_PORT
  NUGET_PUBLIC_URL NUGET_LOCAL_PORT CENIT_TOKEN
  REQUIRE_EMAIL_CONFIRMATION SMTP_HOST SMTP_PORT SMTP_USER SMTP_PASSWORD SMTP_FROM
  OTEL_EXPORTER_OTLP_ENDPOINT)

declare -A CFG=()
GENERATED_PASSWORD=0

# ----------------------------------------------------------------------------
# Utilidades de salida
# ----------------------------------------------------------------------------
if [ -t 1 ]; then
  C_RESET=$'\033[0m'; C_BOLD=$'\033[1m'; C_CYAN=$'\033[36m'; C_GREEN=$'\033[32m'
  C_YELLOW=$'\033[33m'; C_RED=$'\033[31m'
else
  C_RESET=''; C_BOLD=''; C_CYAN=''; C_GREEN=''; C_YELLOW=''; C_RED=''
fi

section() { printf '\n%s== %s ==%s\n' "$C_BOLD$C_CYAN" "$*" "$C_RESET"; }
info()    { printf '%s-->%s %s\n' "$C_CYAN" "$C_RESET" "$*"; }
ok()      { printf '%s OK%s %s\n' "$C_GREEN" "$C_RESET" "$*"; }
warn()    { printf '%sAVISO:%s %s\n' "$C_YELLOW" "$C_RESET" "$*" >&2; }
die()     { printf '%sERROR:%s %s\n' "$C_RED" "$C_RESET" "$*" >&2; exit 1; }

# Solo en el shell principal: dentro de subshells el fallo ya se propaga al padre.
trap 'if [ "$BASH_SUBSHELL" -eq 0 ]; then die "Fallo inesperado en la línea $LINENO (comando: $BASH_COMMAND)."; fi' ERR

usage() {
  cat <<'EOF'
ahora-cenit - instalador

  sudo ./install.sh                     Instala (o reconfigura) el sistema
  sudo ./install.sh --update            Actualiza: git pull, imágenes nuevas,
                                        variables nuevas en el .env (con copia
                                        de seguridad previa)
  sudo ./install.sh --status            Estado de los contenedores y URLs
  sudo ./install.sh --backup            Copia de seguridad en ./backups/
  sudo ./install.sh --restore <carpeta> Restaura una copia de seguridad

Opciones:
  --yes, -y          No preguntar: usar las respuestas por defecto
  --no-backup        En --update, no hacer la copia de seguridad previa
  --http-port <n>    Solo modo local: puerto HTTP en el que publicar (por defecto 80)
  --help, -h         Esta ayuda
EOF
}

# ----------------------------------------------------------------------------
# Argumentos
# ----------------------------------------------------------------------------
CMD="install"; ASSUME_YES=0; NO_BACKUP=0; SKIP_GIT_PULL=0; RESTORE_DIR=""; HTTP_PORT_ARG=""
while [ $# -gt 0 ]; do
  case "$1" in
    --update|-u|update)   CMD="update" ;;
    --status|-s|status)   CMD="status" ;;
    --backup|backup)      CMD="backup" ;;
    --restore|restore)    CMD="restore"; RESTORE_DIR="${2:-}"; [ $# -gt 1 ] && shift ;;
    --yes|-y)             ASSUME_YES=1 ;;
    --no-backup)          NO_BACKUP=1 ;;
    --skip-git-pull)      SKIP_GIT_PULL=1 ;;
    --http-port)          HTTP_PORT_ARG="${2:-}"; [ $# -gt 1 ] && shift ;;
    --help|-h|help)       usage; exit 0 ;;
    *) die "Opción desconocida: $1 (usa --help)" ;;
  esac
  shift
done

if [ -n "$HTTP_PORT_ARG" ] && { ! [[ "$HTTP_PORT_ARG" =~ ^[0-9]+$ ]] || [ "$HTTP_PORT_ARG" -lt 1 ] || [ "$HTTP_PORT_ARG" -gt 65535 ]; }; then
  die "--http-port debe ser un puerto válido (1-65535)."
fi

if [ "$(id -u)" -ne 0 ]; then
  command -v sudo >/dev/null 2>&1 || die "Ejecuta este script como root."
  info "Se necesitan permisos de administrador, relanzando con sudo..."
  exec sudo -E bash "$0" "${ORIG_ARGS[@]}"
fi

# Usuario "real" que ha lanzado sudo (para git y para darle acceso a Docker).
REAL_USER="${SUDO_USER:-}"
[ "$REAL_USER" = "root" ] && REAL_USER=""

# ----------------------------------------------------------------------------
# Preguntas
# ----------------------------------------------------------------------------
ask() { # ask "Pregunta" "valor por defecto" -> imprime la respuesta
  local prompt="$1" default="${2:-}" answer=""
  if [ "$ASSUME_YES" -eq 1 ]; then printf '%s' "$default"; return; fi
  if [ -n "$default" ]; then
    read -r -p "  $prompt [$default]: " answer </dev/tty
  else
    read -r -p "  $prompt: " answer </dev/tty
  fi
  printf '%s' "${answer:-$default}"
}

ask_secret() { # ask_secret "Pregunta" -> imprime la respuesta (sin eco)
  local answer=""
  read -r -s -p "  $1: " answer </dev/tty
  printf '\n' >&2
  printf '%s' "$answer"
}

ask_yes_no() { # ask_yes_no "Pregunta" s|n -> exit 0 si sí
  local prompt="$1" default="${2:-n}" answer="" hint="s/N"
  [ "$default" = "s" ] && hint="S/n"
  if [ "$ASSUME_YES" -eq 1 ]; then [ "$default" = "s" ]; return; fi
  read -r -p "  $prompt ($hint): " answer </dev/tty
  answer="${answer:-$default}"
  [[ "$answer" =~ ^[sSyY] ]]
}

ask_choice() { # ask_choice "Pregunta" defecto opcion1 opcion2... -> imprime el número elegido
  local prompt="$1" default="$2" answer="" i=1
  shift 2
  for opt in "$@"; do printf '    %d) %s\n' "$i" "$opt" >&2; i=$((i + 1)); done
  if [ "$ASSUME_YES" -eq 1 ]; then printf '%s' "$default"; return; fi
  while :; do
    read -r -p "  $prompt [$default]: " answer </dev/tty
    answer="${answer:-$default}"
    if [[ "$answer" =~ ^[0-9]+$ ]] && [ "$answer" -ge 1 ] && [ "$answer" -le $# ]; then
      printf '%s' "$answer"; return
    fi
    printf '  Opción no válida.\n' >&2
  done
}

has_forbidden_chars() { # comillas, barra invertida, acento grave o espacios rompen el .env
  case "$1" in *[[:space:]]*|*\'*|*\"*|*\\*|*\`*) return 0 ;; esac
  return 1
}

# Escribe el primer motivo por el que la contraseña no es segura (nada si vale).
admin_password_problem() {
  local p="$1" lower="${1,,}" w
  [ "${#p}" -ge 12 ]         || { echo "tiene menos de 12 caracteres"; return; }
  [[ "$p" =~ [A-Z] ]]        || { echo "le falta una mayúscula"; return; }
  [[ "$p" =~ [a-z] ]]        || { echo "le falta una minúscula"; return; }
  [[ "$p" =~ [0-9] ]]        || { echo "le falta un número"; return; }
  [[ "$p" =~ [^A-Za-z0-9] ]] || { echo "le falta un símbolo"; return; }
  if has_forbidden_chars "$p"; then echo 'contiene espacios, comillas, \ o `'; return; fi
  if printf '%s' "$p" | grep -Eq '(.)\1\1\1'; then echo "repite el mismo carácter 4 veces seguidas"; return; fi
  if [ "$(printf '%s' "$p" | fold -w1 | LC_ALL=C sort -u | wc -l)" -lt 8 ]; then
    echo "tiene menos de 8 caracteres distintos"; return
  fi
  # Palabras y secuencias típicas, más el usuario del email y las partes del
  # dominio (salvo la extensión: com, es...).
  local -a words=(password passw0rd contrase qwerty asdf zxcv 1234 4321 abcd admin cenit ahora letmein welcome bienvenid changeme iloveyou)
  local email_user="${CFG[ADMIN_EMAIL]:-}" dom="${CFG[DOMAIN]:-}" label
  email_user="${email_user%%@*}"
  [ "${#email_user}" -ge 3 ] && words+=("${email_user,,}")
  for label in $(printf '%s' "${dom%.*}" | tr '.' ' '); do
    [ "${#label}" -ge 4 ] && words+=("${label,,}")
  done
  for w in "${words[@]}"; do
    if [[ "$lower" == *"$w"* ]]; then echo "contiene algo fácil de adivinar (\"$w\")"; return; fi
  done
}

valid_email()  { [[ "$1" =~ ^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$ ]] && ! has_forbidden_chars "$1"; }
valid_domain() { [[ "$1" =~ ^([a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}$ ]]; }

random_from() { # random_from "conjunto" longitud
  local set="$1" len="$2" raw
  raw="$(head -c 4096 /dev/urandom | LC_ALL=C tr -dc "$set")"
  printf '%s' "${raw:0:$len}"
}

gen_secret() { # Siempre con mayúscula, minúscula, dígito y símbolo (lo exigen SQL Server y OpenObserve)
  local len="${1:-32}"
  printf '%s%s%s%s%s' "$(random_from 'A-Za-z0-9' $((len - 4)))" "$(random_from 'A-Z' 1)" \
    "$(random_from 'a-z' 1)" "$(random_from '0-9' 1)" "$(random_from '_.@-' 1)"
}

# ----------------------------------------------------------------------------
# Fichero .env
# ----------------------------------------------------------------------------
load_env() {
  CFG=()
  [ -f "$ENV_FILE" ] || return 0
  local line key value
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line%$'\r'}"
    [[ "$line" =~ ^[[:space:]]*# ]] && continue
    [[ "$line" =~ ^([A-Za-z_][A-Za-z0-9_]*)=(.*)$ ]] || continue
    key="${BASH_REMATCH[1]}"; value="${BASH_REMATCH[2]}"
    if [[ "$value" =~ ^\'(.*)\'$ ]] || [[ "$value" =~ ^\"(.*)\"$ ]]; then value="${BASH_REMATCH[1]}"; fi
    CFG[$key]="$value"
  done <"$ENV_FILE"
}

cfg() { printf '%s' "${CFG[$1]:-}"; }

# Valores por defecto y secretos internos que falten (instalación nueva o
# .env de una versión anterior). Nunca pisa valores existentes.
apply_defaults() {
  local mode="${CFG[CENIT_MODE]:-local}"
  : "${CFG[CENIT_MODE]:=$mode}"
  if [ "$mode" = "production" ]; then
    CFG[PUBLIC_SCHEME]="https"
    CFG[COMPOSE_FILE]="docker-compose.yml:docker-compose.prod.yml"
    CFG[REGISTRY_HOST]="registry.${CFG[DOMAIN]}"
    CFG[HTTP_PORT]="80"   # Let's Encrypt (HTTP-01) necesita el 80
  else
    CFG[PUBLIC_SCHEME]="http"
    CFG[COMPOSE_FILE]="docker-compose.yml"
    CFG[REGISTRY_HOST]="localhost:${CFG[REGISTRY_LOCAL_PORT]:-5000}"
    : "${CFG[HTTP_PORT]:=80}"
  fi
  if [ "$mode" != "production" ] && [ -n "$HTTP_PORT_ARG" ]; then CFG[HTTP_PORT]="$HTTP_PORT_ARG"; fi
  if [ "${CFG[HTTP_PORT]}" = "80" ]; then CFG[PUBLIC_PORT_SUFFIX]=""; else CFG[PUBLIC_PORT_SUFFIX]=":${CFG[HTTP_PORT]}"; fi
  : "${CFG[NUGET_LOCAL_PORT]:=5100}"
  # Versiones anteriores lo llamaban NUGET_TOKEN.
  if [ -z "${CFG[CENIT_TOKEN]:-}" ] && [ -n "${CFG[NUGET_TOKEN]:-}" ]; then CFG[CENIT_TOKEN]="${CFG[NUGET_TOKEN]}"; fi
  unset 'CFG[NUGET_TOKEN]'
  if [ -z "${CFG[CENIT_TOKEN]:-}" ]; then
    CFG[CENIT_TOKEN]="cenit_$(random_from 'A-Za-z0-9' 40)"
    # Instalación existente sin token (o token borrado para rotarlo): hay que darlo de alta en el registry.
    [ -f "$AUTH_DIR/htpasswd" ] && TOKEN_CHANGED=1
  fi
  if [ "$mode" = "production" ]; then
    CFG[NUGET_PUBLIC_URL]="https://nuget.${CFG[DOMAIN]}/"
  else
    CFG[NUGET_PUBLIC_URL]="http://localhost:${CFG[NUGET_LOCAL_PORT]}/"
  fi
  CFG[COMPOSE_PATH_SEPARATOR]=":"
  : "${CFG[DOMAIN]:=localhost}"
  : "${CFG[ACME_EMAIL]:=${CFG[ADMIN_EMAIL]:-}}"
  : "${CFG[DB_SA_PASSWORD]:=$(gen_secret 32)}"
  : "${CFG[MSSQL_PID]:=Express}"
  : "${CFG[JWT_SECRET]:=$(gen_secret 64)}"
  : "${CFG[APP_IMAGE]:=ghcr.io/rpardoahora/ahora-cenit}"
  : "${CFG[APP_VERSION]:=latest}"
  : "${CFG[PORTAINER_LOCAL_PORT]:=9000}"
  : "${CFG[REGISTRY_LOCAL_PORT]:=5000}"
  : "${CFG[PORTAINER_ENDPOINT_ID]:=}"
  : "${CFG[PORTAINER_API_KEY]:=}"
  : "${CFG[REQUIRE_EMAIL_CONFIRMATION]:=false}"
  : "${CFG[SMTP_PORT]:=587}"
  [ -n "${CFG[OTEL_EXPORTER_OTLP_ENDPOINT]+x}" ] || CFG[OTEL_EXPORTER_OTLP_ENDPOINT]="http://openobserve:5080/api/default"
  local k
  for k in SMTP_HOST SMTP_USER SMTP_PASSWORD SMTP_FROM; do : "${CFG[$k]:=}"; done
}

write_env() {
  mkdir -p "$INFRA_DIR"
  local tmp="$ENV_FILE.tmp" k known extra=()
  {
    echo "# ============================================================================"
    echo "#  ahora-cenit - configuración de la instalación"
    echo "#  Generado por install.sh / install.ps1. Puedes editarlo a mano; aplica los"
    echo "#  cambios con: ./install.sh --update  (o docker compose up -d en infra/)."
    echo "#  CONTIENE CONTRASEÑAS: no lo subas a git ni lo compartas."
    echo "# ============================================================================"
    for k in "${ENV_KEYS[@]}"; do
      case "$k" in
        CENIT_MODE)        echo; echo "# --- Modo y dominio (local = localhost por HTTP, production = HTTPS) ---" ;;
        HTTP_PORT)         echo "# Puerto HTTP del host (solo local puede ser distinto de 80; el sufijo lo calcula el instalador)." ;;
        COMPOSE_FILE)      echo "# Ficheros compose que usa 'docker compose' en esta carpeta (no tocar)." ;;
        ACME_EMAIL)        echo "# Email para Let's Encrypt (solo producción)." ;;
        ADMIN_EMAIL)       echo; echo "# --- Administrador: portal, Portainer, Traefik, registry y OpenObserve ---" ;;
        DB_SA_PASSWORD)    echo; echo "# --- Secretos internos (generados automáticamente) ---" ;;
        MSSQL_PID)         echo "# Edición de SQL Server: Express (gratis, apta para producción), Developer (solo pruebas) o Standard/Enterprise/clave con licencia." ;;
        APP_IMAGE)         echo; echo "# --- Imagen del portal (APP_VERSION: latest o sha-<commit> / v<versión>) ---" ;;
        PORTAINER_API_KEY) echo; echo "# --- Portainer / registry (rellenado por el instalador) ---" ;;
        NUGET_PUBLIC_URL)  echo; echo "# --- NuGet (Nexus) y token de plataforma. CENIT_TOKEN es la contraseña del usuario $PLATFORM_USER: vale para la API del portal, docker login y NuGet ---" ;;
        REQUIRE_EMAIL_CONFIRMATION) echo; echo "# --- Emails (SMTP vacío = los emails solo se escriben en el log) ---" ;;
        OTEL_EXPORTER_OTLP_ENDPOINT) echo; echo "# --- Telemetría (vacío = no exportar a OpenObserve) ---" ;;
      esac
      printf "%s='%s'\n" "$k" "${CFG[$k]:-}"
    done
    for k in "${!CFG[@]}"; do
      known=0
      for kk in "${ENV_KEYS[@]}"; do [ "$kk" = "$k" ] && known=1 && break; done
      if [ "$known" -eq 0 ]; then extra+=("$k"); fi
    done
    if [ "${#extra[@]}" -gt 0 ]; then
      echo; echo "# --- Variables personalizadas ---"
      for k in "${extra[@]}"; do printf "%s='%s'\n" "$k" "${CFG[$k]}"; done
    fi
  } >"$tmp"
  chmod 600 "$tmp"
  mv "$tmp" "$ENV_FILE"
  [ -n "$REAL_USER" ] && chown "$REAL_USER" "$ENV_FILE" || true
}

# ----------------------------------------------------------------------------
# Sistema y Docker
# ----------------------------------------------------------------------------
pkg_install() {
  if command -v apt-get >/dev/null 2>&1; then
    DEBIAN_FRONTEND=noninteractive apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "$@"
  elif command -v dnf >/dev/null 2>&1; then dnf install -y -q "$@"
  elif command -v yum >/dev/null 2>&1; then yum install -y -q "$@"
  elif command -v zypper >/dev/null 2>&1; then zypper --non-interactive install "$@"
  elif command -v apk >/dev/null 2>&1; then apk add --no-cache "$@"
  elif command -v pacman >/dev/null 2>&1; then pacman -Sy --noconfirm "$@"
  else die "No sé instalar paquetes en esta distribución. Instala a mano: $*"
  fi
}

check_system() {
  section "Comprobando el sistema"
  local arch; arch="$(uname -m)"
  case "$arch" in
    x86_64|amd64) ok "Arquitectura $arch" ;;
    *) die "Arquitectura $arch no soportada: SQL Server solo funciona en x86_64 (amd64)." ;;
  esac
  local mem_kb; mem_kb="$(awk '/MemTotal/ {print $2}' /proc/meminfo 2>/dev/null || echo 0)"
  if [ "$mem_kb" -lt 5500000 ]; then
    warn "La máquina tiene $((mem_kb / 1024)) MB de RAM. Se recomiendan al menos 8 GB (SQL Server y Nexus necesitan unos 2 GB cada uno)."
    ask_yes_no "¿Continuar de todos modos?" n || exit 1
  else
    ok "Memoria: $((mem_kb / 1024)) MB"
  fi
  local missing=()
  command -v curl >/dev/null 2>&1 || missing+=(curl)
  command -v git  >/dev/null 2>&1 || missing+=(git)
  if [ "${#missing[@]}" -gt 0 ]; then
    info "Instalando herramientas básicas: ${missing[*]}"
    pkg_install "${missing[@]}" ca-certificates
  fi
  ok "Herramientas básicas (curl, git)"
}

start_docker_service() {
  if [ -d /run/systemd/system ]; then
    systemctl enable --now docker >/dev/null 2>&1 || systemctl start docker
  else
    service docker start >/dev/null 2>&1 || true
  fi
  local i
  for i in $(seq 1 30); do docker info >/dev/null 2>&1 && return 0; sleep 2; done
  die "Docker está instalado pero no arranca. Revisa: systemctl status docker"
}

ensure_docker() {
  section "Docker"
  if ! command -v docker >/dev/null 2>&1; then
    info "Docker no está instalado. Instalándolo con el script oficial (get.docker.com)..."
    curl -fsSL https://get.docker.com -o /tmp/get-docker.sh
    sh /tmp/get-docker.sh
    rm -f /tmp/get-docker.sh
  fi
  docker info >/dev/null 2>&1 || start_docker_service
  if ! docker compose version >/dev/null 2>&1; then
    info "Falta el plugin 'docker compose'. Instalándolo..."
    pkg_install docker-compose-plugin || die "No se pudo instalar docker compose. Reinstala Docker desde https://docs.docker.com/engine/install/"
  fi
  ok "$(docker --version)"
  ok "$(docker compose version)"
  if [ -n "$REAL_USER" ] && ! id -nG "$REAL_USER" | grep -qw docker; then
    if usermod -aG docker "$REAL_USER" 2>/dev/null; then
      info "Usuario '$REAL_USER' añadido al grupo docker (cierra sesión y vuelve a entrar para usar docker sin sudo)."
    fi
  fi
}

dc() { (cd "$INFRA_DIR" && docker compose "$@"); }

jq_run() {
  if command -v jq >/dev/null 2>&1; then jq "$@"; else docker run --rm -i "$JQ_IMAGE" "$@"; fi
}

ensure_network() {
  docker network inspect proxy >/dev/null 2>&1 || { info "Creando red Docker 'proxy'"; docker network create proxy >/dev/null; }
}

# Usuarios del registry y del dashboard de Traefik: admin (contraseña de
# administrador) y, si ya existe, cenit-admin con el token de plataforma.
write_htpasswd() {
  mkdir -p "$AUTH_DIR"
  local tmp="$AUTH_DIR/htpasswd.tmp"
  docker run --rm --entrypoint htpasswd "$HTPASSWD_IMAGE" -Bbn "$ADMIN_USER" "$(cfg ADMIN_PASSWORD)" >"$tmp"
  if [ -n "$(cfg CENIT_TOKEN)" ]; then
    docker run --rm --entrypoint htpasswd "$HTPASSWD_IMAGE" -Bbn "$PLATFORM_USER" "$(cfg CENIT_TOKEN)" >>"$tmp"
  fi
  mv "$tmp" "$AUTH_DIR/htpasswd"
  chmod 644 "$AUTH_DIR/htpasswd"
}

# Tras crear/renovar el token de plataforma, darlo de alta en el registry.
TOKEN_CHANGED=0
sync_platform_token() {
  [ -n "$(cfg CENIT_TOKEN)" ] || return 0
  if [ "$TOKEN_CHANGED" -eq 1 ] || ! grep -q "^$PLATFORM_USER:" "$AUTH_DIR/htpasswd" 2>/dev/null; then
    write_htpasswd
    dc restart registry traefik >/dev/null
    ok "Token de plataforma dado de alta en el registry (usuario $PLATFORM_USER)"
  fi
}

port_busy() { command -v ss >/dev/null 2>&1 && ss -ltnH 2>/dev/null | awk '{print $4}' | grep -Eq "[:.]$1\$"; }

check_ports() {
  if [ -n "$(docker ps -q --filter label=com.docker.compose.project=cenit --filter label=com.docker.compose.service=traefik)" ]; then
    return 0
  fi
  local ports=("$(cfg HTTP_PORT)") p
  [ "$(cfg CENIT_MODE)" = "production" ] && ports+=(443)
  for p in "${ports[@]}"; do
    if port_busy "$p"; then
      die "El puerto $p ya está en uso por otro programa (¿Apache, Nginx, otro Traefik?). Páralo y vuelve a ejecutar el instalador."
    fi
  done
}

open_firewall() {
  [ "$(cfg CENIT_MODE)" = "production" ] || return 0
  if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    if ufw allow 80/tcp >/dev/null && ufw allow 443/tcp >/dev/null; then ok "Firewall (ufw): abiertos 80 y 443"; fi
  elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    if firewall-cmd --permanent --add-service=http --add-service=https >/dev/null && firewall-cmd --reload >/dev/null; then
      ok "Firewall (firewalld): abiertos 80 y 443"
    fi
  fi
}

# Contenedores de la versión anterior (arrancar.ps1 / compose sueltos) con
# nombres fijos que chocarían con los puertos. Los volúmenes se conservan.
cleanup_legacy() {
  local found=() c project
  for c in "${LEGACY_CONTAINERS[@]}"; do
    if docker container inspect "$c" >/dev/null 2>&1; then
      project="$(docker container inspect -f '{{ index .Config.Labels "com.docker.compose.project" }}' "$c" 2>/dev/null || true)"
      [ "$project" != "cenit" ] && found+=("$c")
    fi
  done
  [ "${#found[@]}" -eq 0 ] && return 0
  warn "Hay contenedores de una instalación anterior: ${found[*]}"
  ask_yes_no "¿Eliminarlos? (los datos en volúmenes se conservan)" s || die "No se puede continuar con esos contenedores ocupando los puertos."
  docker rm -f "${found[@]}" >/dev/null
  ok "Contenedores antiguos eliminados"
}

existing_volumes() {
  local v out=()
  for v in "${VOLUMES[@]}"; do docker volume inspect "$v" >/dev/null 2>&1 && out+=("$v"); done
  printf '%s\n' "${out[@]}"
}

# ----------------------------------------------------------------------------
# Configuración interactiva
# ----------------------------------------------------------------------------
check_dns() {
  local public_ip host resolved bad=0
  public_ip="$(curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || true)"
  info "IP pública de esta máquina: ${public_ip:-desconocida}"
  for host in "cloud.$1" "registry.$1" "portainer.$1" "prueba-dns.$1"; do
    resolved="$(getent ahostsv4 "$host" 2>/dev/null | awk 'NR==1 {print $1}' || true)"
    if [ -z "$resolved" ]; then
      warn "$host no resuelve a ninguna IP"; bad=1
    elif [ -n "$public_ip" ] && [ "$resolved" != "$public_ip" ]; then
      warn "$host apunta a $resolved (esta máquina es $public_ip)"; bad=1
    else
      ok "$host -> $resolved"
    fi
  done
  if [ "$bad" -eq 1 ]; then
    warn "Sin DNS correcto Let's Encrypt no podrá emitir los certificados HTTPS."
    warn "Crea un registro A comodín en tu proveedor DNS:  *.$1 -> IP"
    ask_yes_no "¿Continuar igualmente? (los certificados se emitirán solos cuando el DNS esté bien)" s || exit 1
  fi
}

read_admin_password() {
  if [ "$ASSUME_YES" -eq 1 ]; then
    CFG[ADMIN_PASSWORD]="$(gen_secret 20)"; GENERATED_PASSWORD=1; return
  fi
  echo "  Contraseña: mínimo 12 caracteres con mayúscula, minúscula, número y símbolo,"
  echo "  sin palabras ni secuencias fáciles de adivinar (password, 1234, admin, tu email...)"
  echo "  y sin espacios, comillas ni \\. Déjala vacía para generar una segura."
  local p1 p2 problem
  while :; do
    p1="$(ask_secret "Contraseña")"
    if [ -z "$p1" ]; then CFG[ADMIN_PASSWORD]="$(gen_secret 20)"; GENERATED_PASSWORD=1; return; fi
    problem="$(admin_password_problem "$p1")"
    if [ -n "$problem" ]; then echo "  ${C_RED}No es segura: $problem.${C_RESET}"; continue; fi
    p2="$(ask_secret "Repite la contraseña")"
    if [ "$p1" = "$p2" ]; then CFG[ADMIN_PASSWORD]="$p1"; return; fi
    echo "  No coinciden."
  done
}

collect_config() {
  section "Configuración"
  local mode_default=1
  [ "$(cfg CENIT_MODE)" = "production" ] && mode_default=2
  echo "  ¿Dónde se instala?"
  local mode
  mode="$(ask_choice "Elige" "$mode_default" \
    "Local: este ordenador, se accede por http://cloud.localhost" \
    "Producción: servidor con dominio propio y HTTPS automático (Let's Encrypt)")"

  if [ "$mode" = "2" ]; then
    CFG[CENIT_MODE]="production"
    local domain_default; domain_default="$(cfg DOMAIN)"; [ "$domain_default" = "localhost" ] && domain_default=""
    while :; do
      CFG[DOMAIN]="$(ask "Dominio (ej: midominio.com, sin http://)" "$domain_default")"
      CFG[DOMAIN]="${CFG[DOMAIN],,}"
      valid_domain "${CFG[DOMAIN]}" && break
      [ "$ASSUME_YES" -eq 1 ] && die "Dominio no válido: '${CFG[DOMAIN]}'"
      echo "  Dominio no válido."
    done
    CFG[REGISTRY_HOST]="registry.${CFG[DOMAIN]}"
    check_dns "${CFG[DOMAIN]}"
  else
    CFG[CENIT_MODE]="local"
    CFG[DOMAIN]="localhost"
    CFG[REGISTRY_HOST]="localhost:${CFG[REGISTRY_LOCAL_PORT]:-5000}"
    local port_default="${CFG[HTTP_PORT]:-80}"
    if [ "$port_default" = "80" ] && port_busy 80 && [ -z "$(docker ps -q --filter label=com.docker.compose.project=cenit --filter label=com.docker.compose.service=traefik)" ]; then
      warn "El puerto 80 está ocupado por otro programa: se usará otro puerto (las direcciones serán http://cloud.localhost:<puerto>)."
      port_default=8880
    fi
    while :; do
      CFG[HTTP_PORT]="$(ask "Puerto HTTP" "$port_default")"
      [[ "${CFG[HTTP_PORT]}" =~ ^[0-9]+$ ]] && [ "${CFG[HTTP_PORT]}" -ge 1 ] && [ "${CFG[HTTP_PORT]}" -le 65535 ] && break
      [ "$ASSUME_YES" -eq 1 ] && die "Puerto no válido"
      echo "  Puerto no válido."
    done
  fi

  echo
  echo "  Cuenta de administrador (la misma para el portal, Portainer, Traefik,"
  echo "  el registry y OpenObserve; usuario 'admin' donde se pide usuario)."
  local email_default; email_default="$(cfg ADMIN_EMAIL)"
  [ -z "$email_default" ] && [ "${CFG[CENIT_MODE]}" = "local" ] && email_default="admin@cenit.local"
  while :; do
    CFG[ADMIN_EMAIL]="$(ask "Email del administrador" "$email_default")"
    valid_email "${CFG[ADMIN_EMAIL]}" && break
    [ "$ASSUME_YES" -eq 1 ] && die "Email no válido"
    echo "  Email no válido."
  done

  local current_pw weak=""; current_pw="$(cfg ADMIN_PASSWORD)"
  [ -n "$current_pw" ] && weak="$(admin_password_problem "$current_pw")"
  if [ -n "$weak" ]; then
    warn "La contraseña de administrador actual no es segura: $weak."
    [ "$ASSUME_YES" -eq 1 ] && warn "Cámbiala reconfigurando sin --yes."
  fi
  if [ -n "$current_pw" ] && { [ "$ASSUME_YES" -eq 1 ] || { [ -z "$weak" ] && ask_yes_no "¿Mantener la contraseña de administrador actual?" s; }; }; then
    :
  else
    if [ -n "$current_pw" ]; then
      warn "Portainer y OpenObserve guardan su propia contraseña: cámbiala también desde sus paneles."
    fi
    read_admin_password
  fi

  if [ "${CFG[CENIT_MODE]}" = "production" ]; then
    CFG[ACME_EMAIL]="$(ask "Email para avisos de Let's Encrypt" "${CFG[ACME_EMAIL]:-${CFG[ADMIN_EMAIL]}}")"
  fi

  echo
  local smtp_default=n; [ -n "$(cfg SMTP_HOST)" ] && smtp_default=s
  if ask_yes_no "¿Configurar SMTP para enviar emails (confirmación de registro, recuperar contraseña)?" "$smtp_default"; then
    CFG[SMTP_HOST]="$(ask "Servidor SMTP" "$(cfg SMTP_HOST)")"
    CFG[SMTP_PORT]="$(ask "Puerto" "${CFG[SMTP_PORT]:-587}")"
    CFG[SMTP_USER]="$(ask "Usuario" "$(cfg SMTP_USER)")"
    local sp; sp="$(ask_secret "Contraseña SMTP (vacío = mantener la actual)")"
    [ -n "$sp" ] && CFG[SMTP_PASSWORD]="$sp"
    has_forbidden_chars "${CFG[SMTP_PASSWORD]:-}" && die "La contraseña SMTP no puede contener espacios, comillas ni \\."
    CFG[SMTP_FROM]="$(ask "Remitente (From)" "${CFG[SMTP_FROM]:-no-reply@${CFG[DOMAIN]}}")"
    if ask_yes_no "¿Exigir confirmación de email a los usuarios que se registren?" s; then
      CFG[REQUIRE_EMAIL_CONFIRMATION]=true
    else
      CFG[REQUIRE_EMAIL_CONFIRMATION]=false
    fi
  else
    CFG[SMTP_HOST]=""; CFG[SMTP_USER]=""; CFG[SMTP_PASSWORD]=""; CFG[SMTP_FROM]=""
    CFG[REQUIRE_EMAIL_CONFIRMATION]=false
  fi
}

# Volúmenes de una instalación anterior sin .env: o se borran o hay que
# conocer las contraseñas con las que se crearon.
handle_existing_data() {
  local vols; vols="$(existing_volumes)"
  [ -z "$vols" ] && return 0
  warn "Hay datos de una instalación anterior: $(echo "$vols" | tr '\n' ' ')"
  echo "    1) Borrarlos y empezar de cero (se pierden apps, usuarios y configuración)"
  echo "    2) Conservarlos (necesitarás la contraseña SA de SQL Server de entonces)"
  local choice; choice="$(ask_choice "Elige" 2)"
  if [ "$choice" = "1" ]; then
    ask_yes_no "Se BORRARÁN todos los datos. ¿Seguro?" n || exit 1
    docker ps -aq --filter label=com.docker.compose.project=cenit | xargs -r docker rm -f >/dev/null
    echo "$vols" | xargs docker volume rm -f >/dev/null
    ok "Datos anteriores borrados"
  elif echo "$vols" | grep -q cenit_sqlserver_data; then
    local old="" f
    for f in "$SCRIPT_DIR/infra/.env.cenit" "$SCRIPT_DIR/app/.env.prod" "$SCRIPT_DIR/app/.env.dev"; do
      [ -f "$f" ] && old="$(grep -E '^DB_SA_PASSWORD=' "$f" | head -1 | cut -d= -f2- | tr -d "'\"\r")" && [ -n "$old" ] && break
    done
    CFG[DB_SA_PASSWORD]="$(ask "Contraseña SA de SQL Server existente" "$old")"
    warn "OpenObserve conservará el usuario root con el que se creó; si era otro, ajusta la telemetría desde su panel."
  fi
}

# ----------------------------------------------------------------------------
# Portainer: admin, API key, endpoint y registry, todo por API
# ----------------------------------------------------------------------------
PT_BODY="$(mktemp)"
trap 'rm -f "$PT_BODY"' EXIT

pt() { # pt METODO RUTA [JSON] -> imprime el código HTTP; cuerpo en $PT_BODY
  local args=(-sS -o "$PT_BODY" -w '%{http_code}' -X "$1" --max-time 60)
  [ -n "${PT_AUTH:-}" ] && args+=(-H "$PT_AUTH")
  [ -n "${PT_SETUP_TOKEN:-}" ] && args+=(-H "X-Setup-Token: $PT_SETUP_TOKEN")
  [ -n "${3:-}" ] && args+=(-H 'Content-Type: application/json' --data "$3")
  curl "${args[@]}" "http://127.0.0.1:$(cfg PORTAINER_LOCAL_PORT)$2" 2>/dev/null || true
}

json_str() { local s="${1//\\/\\\\}"; s="${s//\"/\\\"}"; printf '"%s"' "$s"; }

portainer_setup_token() {
  dc logs --no-log-prefix portainer 2>&1 | grep -o 'setup_token=[0-9a-f]*' | tail -1 | cut -d= -f2 || true
}

wait_portainer() {
  local i
  for i in $(seq 1 60); do
    [ "$(pt GET /api/system/status)" = "200" ] && return 0
    sleep 2
  done
  die "Portainer no responde en 127.0.0.1:$(cfg PORTAINER_LOCAL_PORT). Mira: cd infra && docker compose logs portainer"
}

configure_portainer() {
  section "Configurando Portainer"
  wait_portainer
  PT_AUTH=""

  # 1) ¿La API key guardada sigue siendo válida?
  if [ -n "$(cfg PORTAINER_API_KEY)" ]; then
    PT_AUTH="X-API-Key: $(cfg PORTAINER_API_KEY)"
    if [ "$(pt GET /api/endpoints)" = "200" ]; then
      ok "API key existente válida"
    else
      PT_AUTH=""
    fi
  fi

  if [ -z "$PT_AUTH" ]; then
    local password; password="$(cfg ADMIN_PASSWORD)"
    # 2) Crear el admin si Portainer está recién instalado. Portainer exige el
    #    setup token que imprime en su log al arrancar (cabecera X-Setup-Token).
    if [ "$(pt GET /api/users/admin/check)" != "204" ]; then
      local code
      PT_SETUP_TOKEN="$(portainer_setup_token)"
      code="$(pt POST /api/users/admin/init "{\"Username\":\"$ADMIN_USER\",\"Password\":$(json_str "$password")}")"
      if [ "$code" != "200" ]; then
        # Portainer se bloquea si no se inicializa en 5 minutos: reiniciar y reintentar.
        info "Reiniciando Portainer para inicializarlo..."
        dc restart portainer >/dev/null; wait_portainer
        PT_SETUP_TOKEN="$(portainer_setup_token)"
        code="$(pt POST /api/users/admin/init "{\"Username\":\"$ADMIN_USER\",\"Password\":$(json_str "$password")}")"
        [ "$code" = "200" ] || die "No se pudo crear el admin de Portainer (HTTP $code): $(cat "$PT_BODY")"
      fi
      PT_SETUP_TOKEN=""
      ok "Usuario admin de Portainer creado"
    fi

    # 3) Login (si el admin ya existía con otra contraseña, se pregunta).
    local jwt="" attempt
    for attempt in 1 2 3 4; do
      if [ "$(pt POST /api/auth "{\"Username\":\"$ADMIN_USER\",\"Password\":$(json_str "$password")}")" = "200" ]; then
        jwt="$(jq_run -r '.jwt' <"$PT_BODY")"; break
      fi
      [ "$ASSUME_YES" -eq 1 ] && break
      warn "La contraseña no es válida para el usuario '$ADMIN_USER' de Portainer (¿se cambió desde su panel?)."
      password="$(ask_secret "Contraseña actual del admin de Portainer")"
    done
    [ -n "$jwt" ] || die "No se pudo iniciar sesión en Portainer."
    PT_AUTH="Authorization: Bearer $jwt"

    # 4) API key para el portal.
    local uid
    pt GET /api/users >/dev/null
    uid="$(jq_run -r --arg u "$ADMIN_USER" '[.[] | select(.Username == $u)][0].Id // 1' <"$PT_BODY")"
    local code
    code="$(pt POST "/api/users/$uid/tokens" "{\"password\":$(json_str "$password"),\"description\":\"ahora-cenit $(date +%Y-%m-%d)\"}")"
    [[ "$code" =~ ^20 ]] || die "No se pudo crear la API key de Portainer (HTTP $code): $(cat "$PT_BODY")"
    CFG[PORTAINER_API_KEY]="$(jq_run -r '.rawAPIKey' <"$PT_BODY")"
    PT_AUTH="X-API-Key: ${CFG[PORTAINER_API_KEY]}"
    ok "API key de Portainer generada"
  fi

  # 5) Entorno Docker local.
  pt GET /api/endpoints >/dev/null
  local eid; eid="$(jq_run -r '[.[] | select(.Type == 1)][0].Id // empty' <"$PT_BODY")"
  if [ -z "$eid" ]; then
    curl -sS -o "$PT_BODY" -X POST -H "$PT_AUTH" --data "Name=local&EndpointCreationType=1" \
      "http://127.0.0.1:$(cfg PORTAINER_LOCAL_PORT)/api/endpoints" >/dev/null
    eid="$(jq_run -r '.Id // empty' <"$PT_BODY")"
    [ -n "$eid" ] || die "No se pudo crear el entorno local en Portainer: $(cat "$PT_BODY")"
  fi
  CFG[PORTAINER_ENDPOINT_ID]="$eid"
  ok "Entorno Docker local (EndpointId=$eid)"

  # 6) Registry interno.
  local reg_url; reg_url="$(cfg REGISTRY_HOST)"
  local body="{\"Name\":\"ahora-cenit\",\"Type\":3,\"URL\":$(json_str "$reg_url"),\"BaseURL\":\"\",\"Authentication\":true,\"Username\":\"$ADMIN_USER\",\"Password\":$(json_str "$(cfg ADMIN_PASSWORD)")}"
  pt GET /api/registries >/dev/null
  local rid; rid="$(jq_run -r --arg n "ahora-cenit" '[.[] | select(.Name == $n)][0].Id // empty' <"$PT_BODY")"
  local code
  if [ -n "$rid" ]; then code="$(pt PUT "/api/registries/$rid" "$body")"; else code="$(pt POST /api/registries "$body")"; fi
  [[ "$code" =~ ^20 ]] || die "No se pudo registrar el registry en Portainer (HTTP $code): $(cat "$PT_BODY")"
  ok "Registry $reg_url dado de alta en Portainer"

  write_env
}

# ----------------------------------------------------------------------------
# Nexus Repository CE (NuGet): admin, EULA, feeds public/internal y usuario de automatización
# ----------------------------------------------------------------------------
NX_USER="admin"; NX_PASS=""
NEXUS_EULA_URL="https://links.sonatype.com/products/nxrm/ce-eula"

nx() { # nx METODO RUTA [cuerpo] [content-type] -> imprime el código HTTP; cuerpo de la respuesta en $PT_BODY
  local method="$1" path="$2" data="${3:-}" ctype="${4:-application/json}"
  local args=(-sS -o "$PT_BODY" -w '%{http_code}' -X "$method" --max-time 60 -u "$NX_USER:$NX_PASS")
  [ -n "$data" ] && args+=(-H "Content-Type: $ctype" --data "$data")
  curl "${args[@]}" "http://127.0.0.1:$(cfg NUGET_LOCAL_PORT)/service/rest/v1$path" 2>/dev/null || true
}

nx_exists() { [ "$(nx GET "$1")" = "200" ]; }

configure_nexus() {
  section "Configurando el repositorio NuGet (Nexus)"
  local port code i; port="$(cfg NUGET_LOCAL_PORT)"
  info "Esperando a que Nexus arranque (la primera vez tarda 1-3 minutos)..."
  for i in $(seq 1 120); do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$port/service/rest/v1/status/writable" || true)"
    [ "$code" = "200" ] && break
    sleep 3
  done
  [ "$code" = "200" ] || die "Nexus no arranca. Mira: cd infra && docker compose logs nexus"

  # Contraseña de admin: la misma que el resto. Nexus arranca con una aleatoria en /nexus-data/admin.password.
  NX_USER="admin"; NX_PASS="$(cfg ADMIN_PASSWORD)"
  if ! nx_exists /security/anonymous; then
    local init; init="$(dc exec -T nexus cat /nexus-data/admin.password 2>/dev/null | tr -d '\r\n' || true)"
    if [ -z "$init" ]; then
      warn "No se pudo entrar en Nexus como admin (¿se cambió su contraseña?). Configúralo desde su panel."
      return 0
    fi
    NX_PASS="$init"
    code="$(nx PUT /security/users/admin/change-password "$(cfg ADMIN_PASSWORD)" text/plain)"
    [[ "$code" =~ ^20 ]] || die "No se pudo fijar la contraseña de admin de Nexus (HTTP $code): $(cat "$PT_BODY")"
    NX_PASS="$(cfg ADMIN_PASSWORD)"
    ok "Contraseña de administrador de Nexus fijada"
  fi

  # Sin aceptar el EULA de la edición Community, Nexus no deja subir ni bajar paquetes.
  nx GET /system/eula >/dev/null
  if [ "$(jq_run -r '.accepted' <"$PT_BODY")" != "true" ]; then
    echo "  Nexus Repository Community Edition se rige por el EULA de Sonatype: $NEXUS_EULA_URL"
    echo "  (edición gratuita con límites: 40.000 componentes y 100.000 peticiones al día)."
    if ask_yes_no "¿Aceptas ese EULA?" s; then
      local body; body="$(jq_run -c '.accepted=true' <"$PT_BODY")"
      code="$(nx POST /system/eula "$body")"
      [[ "$code" =~ ^20 ]] || die "No se pudo aceptar el EULA de Nexus (HTTP $code): $(cat "$PT_BODY")"
      ok "EULA de Nexus Community Edition aceptado"
    else
      warn "Sin aceptar el EULA, Nexus no permitirá subir ni descargar paquetes hasta que lo aceptes desde su panel."
    fi
  fi

  # Feeds: internal (lectura y escritura con login) y public (lectura anónima, escritura con login).
  local r
  for r in internal public; do
    if ! nx_exists "/repositories/$r"; then
      code="$(nx POST /repositories/nuget/hosted "{\"name\":\"$r\",\"online\":true,\"storage\":{\"blobStoreName\":\"default\",\"strictContentTypeValidation\":true,\"writePolicy\":\"allow\"}}")"
      [[ "$code" =~ ^20 ]] || die "No se pudo crear el feed '$r' en Nexus (HTTP $code): $(cat "$PT_BODY")"
    fi
    ok "Feed NuGet '$r'"
  done

  # Acceso anónimo limitado a LEER el feed public. Solo la primera vez (marca: existe el rol public-read).
  if ! nx_exists /security/roles/public-read; then
    code="$(nx POST /security/roles '{"id":"public-read","name":"public-read","description":"Lectura anonima del feed public","privileges":["nx-repository-view-nuget-public-read","nx-repository-view-nuget-public-browse"],"roles":[]}')"
    [[ "$code" =~ ^20 ]] || die "No se pudo crear el rol public-read en Nexus (HTTP $code): $(cat "$PT_BODY")"
    nx PUT /security/users/anonymous '{"userId":"anonymous","firstName":"Anonymous","lastName":"User","emailAddress":"anonymous@example.org","source":"default","status":"active","roles":["public-read"]}' >/dev/null
    nx PUT /security/anonymous '{"enabled":true,"userId":"anonymous","realmName":"NexusAuthorizingRealm"}' >/dev/null
    # Repositorios de ejemplo (maven, nuget) que trae Nexus: no hacen falta.
    for r in maven-releases maven-snapshots maven-central maven-public nuget-hosted nuget.org-proxy nuget-group; do
      nx DELETE "/repositories/$r" >/dev/null
    done
    ok "Acceso anónimo: solo lectura del feed public"
  fi

  # Usuario de automatización: cenit-admin, con el token de plataforma como contraseña.
  nx GET "/security/users?userId=$PLATFORM_USER" >/dev/null
  if [ "$(jq_run 'length' <"$PT_BODY")" != "0" ]; then
    nx PUT "/security/users/$PLATFORM_USER/change-password" "$(cfg CENIT_TOKEN)" text/plain >/dev/null
  else
    code="$(nx POST /security/users "{\"userId\":\"$PLATFORM_USER\",\"firstName\":\"cenit\",\"lastName\":\"admin\",\"emailAddress\":\"$(cfg ADMIN_EMAIL)\",\"password\":\"$(cfg CENIT_TOKEN)\",\"status\":\"active\",\"roles\":[\"nx-admin\"]}")"
    [[ "$code" =~ ^20 ]] || die "No se pudo crear el usuario $PLATFORM_USER en Nexus (HTTP $code): $(cat "$PT_BODY")"
  fi
  ok "Usuario $PLATFORM_USER (token de plataforma) en Nexus"
}

# ----------------------------------------------------------------------------
# Arranque y comprobaciones
# ----------------------------------------------------------------------------
start_stack() {
  section "Descargando imágenes"
  docker pull -q "$JQ_IMAGE" >/dev/null 2>&1 || true
  # Si no hay conexión pero las imágenes ya están en local, seguimos con ellas.
  dc pull --ignore-pull-failures || true
  # Igual que docker compose: las variables de entorno mandan sobre el .env.
  local app_image; app_image="${APP_IMAGE:-$(cfg APP_IMAGE)}:${APP_VERSION:-$(cfg APP_VERSION)}"
  if ! docker image inspect "$app_image" >/dev/null 2>&1; then
    die "No se pudo descargar la imagen del portal ($app_image). Si es privada, ejecuta antes: docker login ghcr.io"
  fi
  section "Arrancando servicios"
  dc up -d --remove-orphans
}

wait_app() {
  info "Esperando a que el portal responda (la primera vez puede tardar 1-2 minutos)..."
  local domain; domain="$(cfg DOMAIN)"
  local i code
  for i in $(seq 1 90); do
    if [ "$(cfg CENIT_MODE)" = "production" ]; then
      code="$(curl -sk -o /dev/null -w '%{http_code}' --max-time 5 --resolve "cloud.$domain:443:127.0.0.1" "https://cloud.$domain/api/config" || true)"
    else
      code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 -H "Host: cloud.$domain" "http://127.0.0.1:$(cfg HTTP_PORT)/api/config" || true)"
    fi
    [ "$code" = "200" ] && { ok "Portal funcionando"; return 0; }
    sleep 3
  done
  warn "El portal todavía no responde. Revisa: cd infra && docker compose logs app"
}

# Tabla con bordes y una columna de color cada una. Cada fila son 4 campos
# separados por ; la fila "-" pinta un separador. Las celdas van sin tildes
# para que el ancho cuadre aunque sudo arranque con LANG=C.
print_table() {
  local -a w=(0 0 0 0) cells colors=("$C_BOLD" "$C_CYAN" "$C_GREEN" "$C_BOLD$C_YELLOW")
  local r i text
  for r in "$@"; do
    [ "$r" = "-" ] && continue
    IFS=$'' read -r -a cells <<<"$r"
    for i in 0 1 2 3; do
      text="${cells[i]:-}"
      [ "${#text}" -gt "${w[i]}" ] && w[i]=${#text}
    done
  done
  hline() { # izquierda cruce derecha
    local out="  $C_CYAN$1" k
    for i in 0 1 2 3; do
      for ((k = 0; k < w[i] + 2; k++)); do out+="─"; done
      [ "$i" -lt 3 ] && out+="$2"
    done
    printf '%s%s%s
' "$out" "$3" "$C_RESET"
  }
  local header=1
  hline "┌" "┬" "┐"
  for r in "$@"; do
    if [ "$r" = "-" ]; then hline "├" "┼" "┤"; continue; fi
    IFS=$'' read -r -a cells <<<"$r"
    local line="  $C_CYAN│$C_RESET" color
    for i in 0 1 2 3; do
      text="${cells[i]:-}"
      color="${colors[i]}"; [ "$header" -eq 1 ] && color="$C_BOLD"
      line+=" $color$text$C_RESET$(printf '%*s' $((w[i] - ${#text})) '') $C_CYAN│$C_RESET"
    done
    printf '%s
' "$line"
    if [ "$header" -eq 1 ]; then hline "╞" "╪" "╡"; header=0; fi
  done
  hline "└" "┴" "┘"
}

print_summary() {
  local s d p; s="$(cfg PUBLIC_SCHEME)"; d="$(cfg DOMAIN)"; p="$(cfg PUBLIC_PORT_SUFFIX)"
  local email pass token reg nu; email="$(cfg ADMIN_EMAIL)"; pass="$(cfg ADMIN_PASSWORD)"
  token="$(cfg CENIT_TOKEN)"; reg="$(cfg REGISTRY_HOST)"; nu="$(cfg NUGET_PUBLIC_URL)"
  local F=$''
  section "Todo listo"
  echo
  print_table     "Servicio${F}URL${F}Usuario${F}Clave"     "Portal ahora-cenit${F}$s://cloud.$d$p${F}$email${F}$pass" "-"     "Portainer${F}$s://portainer.$d$p${F}$ADMIN_USER${F}$pass" "-"     "Traefik${F}$s://traefik.$d$p${F}$ADMIN_USER${F}$pass" "-"     "OpenObserve${F}$s://telemetry.$d$p${F}$email${F}$pass" "-"     "Registry Docker${F}$reg${F}$ADMIN_USER${F}$pass"     "${F}${F}$PLATFORM_USER${F}$token" "-"     "Repositorio NuGet${F}$nu${F}$ADMIN_USER${F}$pass"     "${F}${F}$PLATFORM_USER${F}$token" "-"     "API del portal${F}$s://cloud.$d$p/api${F}Bearer / X-Api-Key${F}$token" "-"     "SQL Server${F}sqlserver:1433 (red interna)${F}sa${F}$(cfg DB_SA_PASSWORD)" "-"     "Apps desplegadas${F}$s://<nombre>.$d$p${F}${F}"
  cat <<EOF

  ${C_BOLD}Feeds NuGet${C_RESET}
     public   : ${C_CYAN}${nu}repository/public/index.json${C_RESET}    (leer: sin login; escribir: con login)
     internal : ${C_CYAN}${nu}repository/internal/index.json${C_RESET}  (leer y escribir: con login)

  ${C_BOLD}Token de plataforma${C_RESET} (CENIT_TOKEN, usuario $PLATFORM_USER)
     API    : curl -H "Authorization: Bearer $token" $s://cloud.$d$p/api/products
     Docker : docker login $reg -u $PLATFORM_USER -p $token
EOF
  echo
  if [ "$GENERATED_PASSWORD" -eq 1 ]; then
    printf '  %sLa contraseña de administrador se ha generado automáticamente: apúntala.%s
' "$C_BOLD$C_YELLOW" "$C_RESET"
  fi
  echo "  Todas las credenciales están guardadas en infra/.env."
  if [ "$(cfg CENIT_MODE)" = "production" ]; then
    echo "  Los certificados HTTPS se emiten solos en el primer acceso a cada subdominio."
  fi
  echo
  echo "  Actualizar:  sudo ./install.sh --update     Estado: sudo ./install.sh --status"
}

# ----------------------------------------------------------------------------
# Comandos
# ----------------------------------------------------------------------------
require_installed() {
  [ -f "$ENV_FILE" ] || die "No hay ninguna instalación (falta infra/.env). Ejecuta primero: sudo ./install.sh"
  load_env
}

do_install() {
  section "ahora-cenit - instalación"
  if [ -f "$ENV_FILE" ]; then
    load_env
    warn "Ya hay una instalación en esta carpeta (modo $(cfg CENIT_MODE), dominio $(cfg DOMAIN))."
    local c; c="$(ask_choice "¿Qué quieres hacer?" 1 \
      "Actualizar (equivale a --update)" \
      "Reconfigurar (volver a responder las preguntas; se conservan los datos)" \
      "Salir")"
    case "$c" in 1) do_update; return ;; 3) exit 0 ;; esac
  fi

  check_system
  ensure_docker
  cleanup_legacy
  collect_config
  [ -f "$ENV_FILE" ] || handle_existing_data
  apply_defaults
  write_env
  ok "Configuración guardada en infra/.env"

  write_htpasswd
  check_ports
  open_firewall
  ensure_network
  start_stack
  configure_portainer
  configure_nexus
  sync_platform_token
  dc up -d --remove-orphans   # recrea el portal con la API key de Portainer
  wait_app
  print_summary
}

do_update() {
  require_installed
  if [ "$SKIP_GIT_PULL" -eq 0 ] && [ -d "$SCRIPT_DIR/.git" ]; then
    section "Actualizando el código (git pull)"
    local git_cmd=(git -C "$SCRIPT_DIR" pull --ff-only)
    [ -n "$REAL_USER" ] && git_cmd=(sudo -u "$REAL_USER" "${git_cmd[@]}")
    if "${git_cmd[@]}"; then
      # El propio instalador puede haber cambiado: continuar con la versión nueva.
      local next=(--update --skip-git-pull)
      [ "$ASSUME_YES" -eq 1 ] && next+=(--yes)
      [ "$NO_BACKUP" -eq 1 ] && next+=(--no-backup)
      [ -n "$HTTP_PORT_ARG" ] && next+=(--http-port "$HTTP_PORT_ARG")
      rm -f "$PT_BODY"
      exec bash "$SCRIPT_DIR/install.sh" "${next[@]}"
    fi
    warn "git pull ha fallado (¿cambios locales?). Continúo con el código actual."
  fi

  section "Actualizando ahora-cenit"
  ensure_docker
  if [ "$NO_BACKUP" -eq 0 ] && ask_yes_no "¿Hacer copia de seguridad antes de actualizar?" s; then
    do_backup
  fi
  apply_defaults
  write_env
  [ -f "$AUTH_DIR/htpasswd" ] || write_htpasswd
  cleanup_legacy
  ensure_network
  start_stack
  configure_portainer
  configure_nexus
  sync_platform_token
  dc up -d --remove-orphans
  info "Limpiando imágenes antiguas..."
  docker image prune -f >/dev/null
  wait_app
  print_summary
}

do_status() {
  require_installed
  section "Servicios"
  dc ps
  print_summary
}

do_backup() {
  require_installed
  local dest; dest="$BACKUP_ROOT/$(date +%Y%m%d-%H%M%S)"
  mkdir -p "$dest"
  section "Copia de seguridad en $dest"
  info "Parando servicios para copiar los datos de forma consistente..."
  dc stop >/dev/null
  local v
  for v in "${VOLUMES[@]}"; do
    docker volume inspect "$v" >/dev/null 2>&1 || continue
    info "Copiando $v"
    docker run --rm -v "$v:/data:ro" -v "$dest:/backup" "$HELPER_IMAGE" tar czf "/backup/$v.tar.gz" -C /data .
  done
  cp "$ENV_FILE" "$dest/env"
  [ -d "$AUTH_DIR" ] && cp -r "$AUTH_DIR" "$dest/auth"
  chmod -R go-rwx "$dest"
  info "Arrancando servicios de nuevo..."
  dc up -d >/dev/null
  ok "Copia completada: $dest"
}

do_restore() {
  [ -n "$RESTORE_DIR" ] || die "Indica la carpeta: sudo ./install.sh --restore backups/AAAAMMDD-HHMMSS"
  RESTORE_DIR="$(cd "$RESTORE_DIR" 2>/dev/null && pwd)" || die "No existe la carpeta indicada."
  [ -f "$RESTORE_DIR/env" ] || die "$RESTORE_DIR no parece una copia de ahora-cenit (falta el fichero env)."
  section "Restaurar desde $RESTORE_DIR"
  warn "Se sustituirán TODOS los datos actuales por los de la copia."
  if [ "$ASSUME_YES" -eq 0 ]; then ask_yes_no "¿Continuar?" n || exit 1; fi
  ensure_docker
  [ -f "$ENV_FILE" ] && dc down >/dev/null 2>&1 || true
  cp "$RESTORE_DIR/env" "$ENV_FILE"; chmod 600 "$ENV_FILE"
  [ -d "$RESTORE_DIR/auth" ] && { rm -rf "$AUTH_DIR"; cp -r "$RESTORE_DIR/auth" "$AUTH_DIR"; chmod 644 "$AUTH_DIR"/*; }
  local f v
  for f in "$RESTORE_DIR"/*.tar.gz; do
    [ -e "$f" ] || continue
    v="$(basename "$f" .tar.gz)"
    info "Restaurando $v"
    docker volume rm -f "$v" >/dev/null 2>&1 || true
    docker volume create "$v" >/dev/null
    docker run --rm -v "$v:/data" -v "$RESTORE_DIR:/backup:ro" "$HELPER_IMAGE" tar xzf "/backup/$v.tar.gz" -C /data
  done
  load_env
  ensure_network
  dc up -d --remove-orphans
  wait_app
  print_summary
}

case "$CMD" in
  install) do_install ;;
  update)  do_update ;;
  status)  do_status ;;
  backup)  do_backup ;;
  restore) do_restore ;;
esac
