# ahora-cenit

**ahora-cenit** es una plataforma de código abierto para desplegar proyectos
**Flexygo** (y cualquier aplicación en Docker) con un clic: un portal web
donde los clientes eligen un producto del catálogo, pulsan *Instalar* y
obtienen su propia instancia en `https://<nombre>.tudominio.com`.

Este repositorio incluye **toda la infraestructura**. Con un solo comando
sobre una máquina recién instalada (Linux o Windows) queda montado y
configurado:

| Dirección | Qué es |
|---|---|
| `cloud.tudominio.com` | El **portal** ahora-cenit (catálogo, despliegues, usuarios). |
| `portainer.tudominio.com` | **Portainer**: gestión de los contenedores Docker. El portal lo usa por debajo para desplegar. |
| `registry.tudominio.com` | **Docker Registry** privado, para tus propias imágenes. |
| `traefik.tudominio.com` | **Traefik**: el proxy que reparte el tráfico y pone los certificados HTTPS. |
| `telemetry.tudominio.com` | **OpenObserve**: logs, métricas y auditoría del portal. |
| `nuget.tudominio.com` | **Repositorio NuGet** (Forgejo): paquetes públicos (sin login) e internos (con token). |
| `<nombre>.tudominio.com` | Cada aplicación desplegada desde el portal. |
| `tudominio.com` | Redirige al portal. |

Todo queda enlazado: Portainer ya conoce el registry interno, el portal ya
tiene la clave de acceso a Portainer, la telemetría ya recibe datos y los
certificados HTTPS se piden solos.

---

## Índice

1. [Antes de empezar: ¿local o producción?](#1-antes-de-empezar-local-o-producción)
2. [Requisitos](#2-requisitos)
3. [Solo para producción: configurar el DNS](#3-solo-para-producción-configurar-el-dns)
4. [Instalar en Linux](#4-instalar-en-linux)
5. [Instalar en Windows 10 / 11](#5-instalar-en-windows-10--11)
6. [Instalar en Windows Server](#6-instalar-en-windows-server)
7. [Qué te va a preguntar el instalador](#7-qué-te-va-a-preguntar-el-instalador)
8. [Ya está instalado: primeros pasos](#8-ya-está-instalado-primeros-pasos)
9. [Actualizar, ver el estado y copias de seguridad](#9-actualizar-ver-el-estado-y-copias-de-seguridad)
10. [Subir tus propias imágenes al registry](#10-subir-tus-propias-imágenes-al-registry)
11. [Repositorio NuGet: paquetes públicos e internos](#11-repositorio-nuget-paquetes-públicos-e-internos)
12. [Crear plantillas de producto](#12-crear-plantillas-de-producto)
13. [Cambiar la configuración](#13-cambiar-la-configuración)
14. [Problemas frecuentes](#14-problemas-frecuentes)
15. [Para desarrolladores](#15-para-desarrolladores)
16. [Licencia](#licencia)

---

## 1. Antes de empezar: ¿local o producción?

El instalador te pregunta entre dos modos:

- **Local**: para probar en tu propio ordenador. No necesitas dominio ni
  nada más. Todo funciona en `http://cloud.localhost`,
  `http://portainer.localhost`, etc. (los navegadores ya saben que
  `*.localhost` es tu propio ordenador).
- **Producción**: para un servidor de verdad, accesible desde internet, con
  tu dominio y HTTPS. Necesitas un dominio y haber configurado su DNS (ver
  [paso 3](#3-solo-para-producción-configurar-el-dns)).

## 2. Requisitos

| | Mínimo | Recomendado |
|---|---|---|
| Procesador | 2 núcleos **x86-64 / AMD64** | 4 núcleos |
| Memoria | 4 GB (8 GB en Windows 10/11) | 8 GB o más |
| Disco | 30 GB libres | 100 GB (las aplicaciones desplegadas ocupan) |
| Sistema | Ubuntu 22.04+, Debian 12+, RHEL/Rocky/Alma 9, Fedora… · Windows 10/11 · Windows Server 2022/2025 | Ubuntu Server 24.04 LTS |

> ⚠️ Los procesadores **ARM** (Raspberry Pi, Mac M1-M4, servidores Graviton)
> **no** sirven: SQL Server solo funciona en x86-64.

Solo para **producción**, además:

- Un **dominio** (por ejemplo `midominio.com`).
- Los **puertos 80 y 443** del servidor abiertos a internet (en el firewall
  del proveedor cloud: *security group*, *firewall de red*, etc.).
- Que no haya otro servidor web (Apache, Nginx, IIS…) ocupando esos puertos.

No necesitas instalar Docker: el instalador lo hace por ti.

## 3. Solo para producción: configurar el DNS

Hazlo **antes** de instalar. En el panel de tu proveedor de dominio (GoDaddy,
OVH, Cloudflare, IONOS, Arsys…) crea **dos registros de tipo A** apuntando a
la IP pública de tu servidor:

| Tipo | Nombre | Valor |
|---|---|---|
| A | `@` (o `midominio.com`) | `IP del servidor` |
| A | `*` (o `*.midominio.com`) | `IP del servidor` |

El segundo (el asterisco, *wildcard*) hace que **cualquier** subdominio
(`cloud.`, `portainer.`, y las apps que se desplieguen) llegue a tu servidor.

> 💡 Si usas **Cloudflare**, deja los registros en modo **"Solo DNS"** (nube
> gris), no en modo proxy (nube naranja), o Let's Encrypt no podrá emitir los
> certificados.

Los cambios de DNS pueden tardar unos minutos en propagarse. El instalador
comprueba el DNS y te avisa si algo no cuadra.

## 4. Instalar en Linux

Conéctate al servidor (por SSH) y ejecuta, línea a línea:

```bash
# 1. Instalar git (si no lo tienes)
sudo apt-get update && sudo apt-get install -y git      # Ubuntu / Debian
# sudo dnf install -y git                               # RHEL / Rocky / Alma / Fedora

# 2. Descargar ahora-cenit
sudo git clone https://github.com/rpardoahora/ahora-cenit.git /opt/ahora-cenit
cd /opt/ahora-cenit

# 3. Instalar
sudo ./install.sh
```

Responde a las preguntas ([qué significan](#7-qué-te-va-a-preguntar-el-instalador))
y espera. La primera vez tarda **entre 5 y 15 minutos** (descarga unos 2 GB).
Al final verás algo así:

```
== Todo listo ==
  Portal ahora-cenit : https://cloud.midominio.com        (usuario: tu@email.com)
  Portainer          : https://portainer.midominio.com    (usuario: admin)
  Traefik            : https://traefik.midominio.com      (usuario: admin)
  OpenObserve        : https://telemetry.midominio.com    (usuario: tu@email.com)
  Docker Registry    : registry.midominio.com  (docker login registry.midominio.com -u admin)
```

## 5. Instalar en Windows 10 / 11

1. Instala **git** si no lo tienes: abre *PowerShell* y ejecuta
   `winget install --id Git.Git -e`. Cierra y vuelve a abrir PowerShell.
2. Descarga ahora-cenit:
   ```powershell
   git clone https://github.com/rpardoahora/ahora-cenit.git C:\ahora-cenit
   cd C:\ahora-cenit
   ```
3. Lanza el instalador:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   Si hace falta instalar algo, Windows te pedirá permiso de administrador: acepta.

Si Docker Desktop ya está instalado y arrancado no hace falta ser
administrador. Si tienes **IIS** en el puerto 80, en modo local el instalador
usará otro puerto (`http://cloud.localhost:8880`).

Si el equipo no tiene Docker, el instalador:

1. Activa **WSL2** y te pide **reiniciar**. Tras reiniciar e iniciar sesión,
   el instalador **continúa solo** (acepta el aviso de permisos otra vez).
2. Descarga e instala **Docker Desktop** (~600 MB) y lo arranca.
3. Sigue con la instalación normal.

> ℹ️ Docker Desktop solo funciona con tu sesión de Windows iniciada. Para un
> servidor que esté encendido 24x7 usa Linux o Windows Server.
>
> ℹ️ Docker Desktop es gratuito para uso personal, educación, proyectos open
> source y empresas pequeñas. Las empresas grandes necesitan licencia de
> Docker (consulta sus condiciones). En Windows Server no se usa Docker
> Desktop.

## 6. Instalar en Windows Server

Requiere **Windows Server 2022 o 2025** (2019 no soporta WSL2). Los pasos son
los mismos que en [Windows 10/11](#5-instalar-en-windows-10--11), pero el
instalador hace algo distinto por debajo, porque Docker Desktop no funciona
en Windows Server:

1. Activa **WSL2** (puede pedir reiniciar; luego continúa solo).
2. Crea una máquina Linux ligera dedicada (*distro WSL* llamada
   `AhoraCenit`, con Ubuntu 24.04).
3. Copia el repositorio dentro de ella (`/opt/ahora-cenit`) y ejecuta allí el
   instalador de Linux, con las mismas preguntas.
4. Publica los puertos 80 y 443 del servidor hacia esa máquina Linux y abre
   el firewall de Windows.
5. Crea una **tarea programada** (`AhoraCenit-WSL`) para que todo arranque
   solo cuando se reinicie el servidor, aunque nadie inicie sesión. Para eso
   te pide **la contraseña de tu usuario de Windows** (si la cambias, edita
   la tarea en el *Programador de tareas*).

> ⚠️ Si el servidor tiene **IIS** instalado ocupará el puerto 80. Páralo antes:
> `Stop-Service W3SVC; Set-Service W3SVC -StartupType Disabled`.

Los comandos de mantenimiento (`--update`, `--status`, `--backup`) se lanzan
igual, con `install.ps1`; se ejecutan automáticamente dentro de WSL. Las
copias de seguridad quedan dentro de WSL, en `/opt/ahora-cenit/backups`
(desde el Explorador de Windows: `\\wsl$\AhoraCenit\opt\ahora-cenit\backups`).

## 7. Qué te va a preguntar el instalador

| Pregunta | Qué contestar |
|---|---|
| **¿Dónde se instala?** | `1` para probar en tu ordenador, `2` para un servidor con dominio. |
| **Dominio** (solo producción) | Solo el dominio, sin `http://` ni `www`: `midominio.com`. |
| **Email del administrador** | Tu email. Es el usuario para entrar al portal y a OpenObserve. |
| **Contraseña** | La contraseña de administrador de **todo** (portal, Portainer, Traefik, registry, OpenObserve). Mínimo 12 caracteres, con mayúscula, minúscula, número y símbolo, sin espacios ni comillas. **Si la dejas vacía se genera una segura** y se muestra al final: apúntala. |
| **Email para Let's Encrypt** (solo producción) | Un email donde avisarte si un certificado HTTPS va a caducar. Normalmente el mismo. |
| **¿Configurar SMTP?** | Si quieres que el portal envíe emails (confirmar registro, recuperar contraseña) contesta `s` y pon los datos de tu servidor de correo (Office 365, Gmail, SendGrid…). Si contestas `n`, los emails solo se escriben en el log y no se exige confirmar el email. Puedes configurarlo más tarde. |

Las demás contraseñas internas (base de datos, firma de sesiones, clave de
Portainer) se generan solas. Todo se guarda en `infra/.env`.

Para instalar sin preguntas (con valores por defecto: modo local y contraseña
generada): `sudo ./install.sh --yes`.

## 8. Ya está instalado: primeros pasos

1. Abre el **portal** (`cloud.tudominio.com` o `http://cloud.localhost`) y
   entra con tu email y contraseña de administrador.
2. En **Productos** da de alta el catálogo (vienen un par de ejemplos):
   cada producto es un *docker-compose* (ver
   [plantillas](#12-crear-plantillas-de-producto)).
3. Los clientes se registran en el portal, eligen un producto y pulsan
   **Instalar**: se despliega en `https://<nombre>.tudominio.com`.

Usuarios de cada panel:

| Panel | Usuario | Contraseña |
|---|---|---|
| Portal | tu email | la de administrador |
| Portainer | `admin` | la de administrador |
| Traefik | `admin` | la de administrador |
| Registry (`docker login`) | `admin` | la de administrador |
| OpenObserve | tu email | la de administrador |
| Repositorio NuGet (Forgejo) | `cenit-admin` | la de administrador |

> 🔒 En producción, la primera vez que abras cada subdominio puede tardar
> unos segundos en tener el candado HTTPS: Let's Encrypt emite el
> certificado en ese momento.

## 9. Actualizar, ver el estado y copias de seguridad

En Linux usa `sudo ./install.sh …`; en Windows
`powershell -ExecutionPolicy Bypass -File .\install.ps1 …` (en los ejemplos
abreviado como `.\install.ps1`).

| Comando | Qué hace |
|---|---|
| `--update` | Descarga la última versión del código (`git pull`), hace una **copia de seguridad**, añade al `.env` las variables nuevas sin tocar las tuyas, descarga las imágenes nuevas y reinicia lo que haya cambiado. **Los datos se conservan.** |
| `--status` | Muestra si cada servicio está en marcha y las direcciones de acceso. |
| `--backup` | Copia todos los datos (base de datos, Portainer, registry, paquetes NuGet, telemetría, certificados) y la configuración a `backups/AAAAMMDD-HHMMSS/`. Para los servicios ~1 minuto mientras copia. |
| `--restore <carpeta>` | Restaura una copia: `sudo ./install.sh --restore backups/20261002-101500`. **Sustituye los datos actuales.** |

Opciones extra: `--yes` (no preguntar nada, útil para tareas programadas) y
`--no-backup` (actualizar sin copia previa).

Ejecutar el instalador otra vez sin opciones sobre una instalación existente
te deja elegir entre **actualizar** o **reconfigurar** (volver a contestar
las preguntas, p.ej. para pasar de local a producción) sin perder datos.

> 💡 Guarda las copias de seguridad **fuera** del servidor (otro disco, un
> almacenamiento S3, tu PC…). Contienen contraseñas: trátalas con cuidado.

Ejemplo de actualización automática cada domingo a las 4:00 (Linux, `sudo crontab -e`):

```
0 4 * * 0 /opt/ahora-cenit/install.sh --update --yes >> /var/log/ahora-cenit-update.log 2>&1
```

## 10. Subir tus propias imágenes al registry

El registry privado sirve para que tus productos usen imágenes que no son
públicas. Desde cualquier máquina con Docker:

```bash
docker login registry.midominio.com -u admin          # pide la contraseña de administrador
docker tag mi-app:1.0 registry.midominio.com/mi-app:1.0
docker push registry.midominio.com/mi-app:1.0
```

En **modo local** el registry está en `localhost:5000`:

```bash
docker login localhost:5000 -u admin
docker tag mi-app:1.0 localhost:5000/mi-app:1.0
docker push localhost:5000/mi-app:1.0
```

Portainer ya tiene el registry configurado, así que los productos pueden
usar esas imágenes directamente.

## 11. Repositorio NuGet: paquetes públicos e internos

El instalador monta **Forgejo** como repositorio NuGet y deja creadas dos
**organizaciones**, que son las que deciden quién puede descargar cada paquete:

| Organización | Quién puede descargar | Feed |
|---|---|---|
| `publico` | **Cualquiera**, sin usuario ni contraseña | `https://nuget.midominio.com/api/packages/publico/nuget/index.json` |
| `interno` | Solo quien tenga **usuario + token** | `https://nuget.midominio.com/api/packages/interno/nuget/index.json` |

Es decir: **un paquete es público o interno según en qué feed lo publiques.**
Desde fuera no se puede ni buscar ni ver los paquetes internos.

En **modo local** la dirección es `http://localhost:5100/` en lugar de
`https://nuget.midominio.com/` (por ejemplo
`http://localhost:5100/api/packages/publico/nuget/index.json`).

### Publicar un paquete

El instalador genera un token para publicar y lo guarda en `infra/.env`
(variable `NUGET_TOKEN`):

```bash
dotnet pack -c Release
# Público:
dotnet nuget push bin/Release/MiLibreria.1.0.0.nupkg -s https://nuget.midominio.com/api/packages/publico/nuget/index.json -k <NUGET_TOKEN>
# Interno:
dotnet nuget push bin/Release/MiLibreria.1.0.0.nupkg -s https://nuget.midominio.com/api/packages/interno/nuget/index.json -k <NUGET_TOKEN>
```

### Usar los paquetes en un proyecto

Feed público (no necesita nada más):

```bash
dotnet nuget add source https://nuget.midominio.com/api/packages/publico/nuget/index.json -n cenit-publico
```

Feed interno (con tu usuario de Forgejo y un token):

```bash
dotnet nuget add source https://nuget.midominio.com/api/packages/interno/nuget/index.json -n cenit-interno -u <usuario> -p <token> --store-password-in-clear-text
```

O en un `nuget.config` junto a la solución:

```xml
<configuration>
  <packageSources>
    <add key="cenit-publico" value="https://nuget.midominio.com/api/packages/publico/nuget/index.json" />
    <add key="cenit-interno" value="https://nuget.midominio.com/api/packages/interno/nuget/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <cenit-interno>
      <add key="Username" value="tu-usuario" />
      <add key="ClearTextPassword" value="tu-token" />
    </cenit-interno>
  </packageSourceCredentials>
</configuration>
```

> 💡 En **modo local** el feed va por HTTP y `dotnet` lo bloquea por defecto:
> añade `allowInsecureConnections="true"` a la línea `<add key=... />` del
> feed, o `--allow-insecure-connections` en `dotnet nuget push`.

### Dar acceso al feed interno a un compañero

1. Entra en `https://nuget.midominio.com` con el usuario **`cenit-admin`** y la
   contraseña de administrador.
2. *Administración del sitio* → *Cuentas de usuario* → *Crear cuenta*.
3. Ve a la organización **interno** → *Equipos* → *Owners* (o crea un equipo
   con permiso de lectura) y añade al usuario.
4. El compañero entra con su usuario, va a *Configuración* → *Aplicaciones* →
   *Generar token* (marcando el permiso **package: lectura**, o
   **lectura y escritura** si también va a publicar) y usa ese token como
   contraseña del feed.

Para un servidor de integración continua (CI), crea un usuario propio
(p.ej. `ci`) con su token, en lugar de usar el de `cenit-admin`.

> ℹ️ Puedes crear más organizaciones (por cliente, por equipo…): cada una
> tiene su propio feed `…/api/packages/<organización>/nuget/index.json` y su
> visibilidad (*pública*, *limitada* o *privada*).

## 12. Crear plantillas de producto

Cada producto del catálogo es un fichero *docker-compose*. Al desplegar, el
portal le pasa dos variables:

- `APP_SUBDOMAIN`: el nombre de la instancia (p.ej. `crm-acme`).
- `BASE_DOMAIN`: tu dominio (`midominio.com`, o `localhost` en local).

Plantilla mínima:

```yaml
services:
  web:
    image: registry.midominio.com/mi-app:1.0
    restart: unless-stopped
    networks: [proxy]
    labels:
      - traefik.enable=true
      - traefik.http.routers.${APP_SUBDOMAIN}.rule=Host(`${APP_SUBDOMAIN}.${BASE_DOMAIN}`)
      - traefik.http.services.${APP_SUBDOMAIN}.loadbalancer.server.port=80

networks:
  proxy:
    external: true
```

Reglas:

- El servicio expuesto debe estar en la red `proxy`.
- **No** añadas `entrypoints` ni `tls.certresolver`: el HTTPS se aplica solo
  en producción y así la misma plantilla funciona en local.
- No uses como nombre de instancia `cloud`, `portainer`, `registry`,
  `traefik`, `telemetry` ni `nuget`: son de la plataforma.

Más detalle técnico en [infra/README.md](infra/README.md).

## 13. Cambiar la configuración

Toda la configuración está en **`infra/.env`** (está comentado). Puedes
editarlo y aplicar los cambios con `--update`. Ejemplos:

- **Configurar el SMTP más tarde**: vuelve a ejecutar el instalador sin
  opciones y elige *Reconfigurar*, o rellena `SMTP_*` en el `.env`.
- **Fijar una versión concreta del portal**: `APP_VERSION='v1.2.0'` (o
  `sha-<commit>`). Por defecto `latest`.
- **Pasar de local a producción** (o cambiar de dominio): ejecuta el
  instalador, elige *Reconfigurar* y después *Producción*. Las aplicaciones
  que ya estuvieran desplegadas siguen con el dominio antiguo: bórralas y
  vuelve a desplegarlas desde el portal.

> ⚠️ **Cambiar la contraseña de administrador**: Portainer y OpenObserve
> guardan su propia copia. Si la cambias con *Reconfigurar*, cámbiala también
> desde el panel de Portainer y de OpenObserve.
>
> ⚠️ No cambies `DB_SA_PASSWORD` a mano: la base de datos ya existe con la
> contraseña original.

- **Edición de SQL Server**: `MSSQL_PID='Express'` por defecto (gratuita y
  apta para producción, hasta 10 GB por base de datos). Si tienes licencia
  de SQL Server pon `Standard`, `Enterprise` o tu clave de producto. No uses
  `Developer` en producción: su licencia solo permite desarrollo y pruebas.

## 14. Problemas frecuentes

**"El puerto 80 ya está en uso"**
En **modo local** el instalador lo detecta y propone otro puerto (8880): las
direcciones pasan a ser `http://cloud.localhost:8880`, etc. Es lo normal en
equipos de desarrollo con **IIS**, que no hace falta parar.
En **producción** el 80 es obligatorio (Let's Encrypt lo usa): otro programa
(Apache, Nginx, IIS…) lo está ocupando y hay que pararlo. En Linux
mira cuál con `sudo ss -ltnp | grep ':80 '` y páralo
(`sudo systemctl disable --now apache2` o `nginx`).

**El navegador dice que el certificado no es seguro (producción)**
Let's Encrypt aún no lo ha emitido. Casi siempre es el DNS: comprueba con
`nslookup cloud.midominio.com` que apunta a la IP del servidor y que los
puertos 80/443 están abiertos en el firewall del proveedor. Los errores
aparecen en `docker compose logs traefik` (desde `infra/`). Let's Encrypt
tiene límites de intentos: si has fallado muchas veces, espera una hora.

**El portal no carga o da error 502/504**
Mira los logs: `cd infra && sudo docker compose logs --tail=100 app`.
La primera vez SQL Server tarda en arrancar; espera un par de minutos.

**"No se pudo descargar la imagen del portal"**
La imagen se descarga de `ghcr.io`. Comprueba la conexión a internet. Si
usas un *fork* con la imagen privada, haz antes `docker login ghcr.io`.

**No puedo desplegar aplicaciones desde el portal**
Comprueba en Portainer (`portainer.tudominio.com`) que existe el entorno
`local` y en *Registries* el registry `ahora-cenit`. Si no, ejecuta
`--update`: vuelve a configurarlo todo.

**He olvidado la contraseña de administrador**
Está en `infra/.env` (`ADMIN_PASSWORD`). Solo root/administradores pueden
leer ese fichero.

**Windows: "la ejecución de scripts está deshabilitada"**
Lanza el instalador con `powershell -ExecutionPolicy Bypass -File .\install.ps1`.

**Windows: Docker Desktop no arranca**
Abre Docker Desktop a mano, espera a que ponga *Engine running* y vuelve a
ejecutar el instalador. Comprueba que la virtualización está activa en la
BIOS (*Intel VT-x* / *AMD-V*).

**Ver los logs de cualquier servicio**
Desde la carpeta `infra/`: `sudo docker compose logs -f <servicio>`, donde
servicio es `app`, `traefik`, `portainer`, `registry`, `sqlserver` u
`openobserve`.

## 15. Para desarrolladores

```
install.sh / install.ps1   instaladores (Linux / Windows)
infra/                     stack de producción: compose + configuración generada  -> infra/README.md
app/                       código del portal: React + API .NET 10 en un contenedor -> app/README.md
.github/workflows/         build y publicación de la imagen en ghcr.io
```

- Cada push a `main` (o tag `v*`) publica la imagen
  `ghcr.io/rpardoahora/ahora-cenit` (`latest`, `sha-<commit>`, `v*`), que es la
  que usan los instaladores.
- Para desarrollar el portal compilando desde el código, usa
  `app/docker-compose.dev.yml` (ver [app/README.md](app/README.md)).

### Roles del portal

- **Sin sesión**: ve el catálogo; al pulsar *Instalar* se le lleva al registro.
- **Cliente**: despliega, ve, para y borra **sus** aplicaciones.
- **Admin**: además gestiona productos y usuarios y ve las aplicaciones de todos.

## Licencia

Copyright © 2026 **CEESI ASESORES S.L.**

ahora-cenit se distribuye bajo la licencia [Apache-2.0](LICENSE): puedes
usarlo, modificarlo y redistribuirlo, también con fines comerciales,
manteniendo los avisos de copyright y el fichero [NOTICE](NOTICE).

"Ahora", "ahora-cenit" y "Flexygo" son marcas de CEESI ASESORES S.L.; la
licencia no da permiso para usarlas (salvo para indicar el origen del
código).

El instalador despliega software de terceros (Traefik, Portainer, Forgejo,
OpenObserve, SQL Server…) que se rige por sus propias licencias: ver
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). En particular, al instalar
se acepta el [EULA de Microsoft SQL Server](https://go.microsoft.com/fwlink/?linkid=857698)
(edición Express por defecto).
