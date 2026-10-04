# Software de terceros

El código de este repositorio se distribuye bajo la licencia
[Apache-2.0](LICENSE). Para funcionar, ahora-cenit usa software de terceros
que **no forma parte de este repositorio** y que se rige por sus propias
licencias. Este documento lo resume a título informativo; en caso de duda,
manda la licencia original de cada proyecto.

## Servicios que despliega el instalador

`install.sh` / `install.ps1` descargan las imágenes oficiales de estos
programas desde sus registros públicos y los ejecutan como **contenedores
independientes**, sin modificarlos ni enlazarlos con el código de
ahora-cenit (se comunican por red).

| Software | Licencia | Notas |
|---|---|---|
| [Traefik](https://github.com/traefik/traefik) | MIT | Proxy inverso y certificados HTTPS. |
| [Portainer CE](https://github.com/portainer/portainer) | zlib | Gestión de contenedores. |
| [Distribution (Docker Registry)](https://github.com/distribution/distribution) | Apache-2.0 | Registry privado de imágenes. |
| [Sonatype Nexus Repository Community Edition](https://www.sonatype.com/products/nexus-repository-community-edition/learn) | Licencia propietaria de Sonatype ([EULA](https://links.sonatype.com/products/nxrm/ce-eula)) | Repositorio NuGet. Gratuita con límites (40.000 componentes y 100.000 peticiones/día). El instalador pide aceptar su EULA. |
| [OpenObserve](https://github.com/openobserve/openobserve) | AGPL-3.0 | Telemetría. Se usa sin modificar. Si modificas OpenObserve y lo ofreces por red, la AGPL te obliga a publicar esas modificaciones. |
| [Microsoft SQL Server 2022](https://www.microsoft.com/sql-server) | Licencia propietaria de Microsoft ([EULA](https://go.microsoft.com/fwlink/?linkid=857698)) | Por defecto se instala la edición **Express** (gratuita, apta para producción, máx. 10 GB por base de datos). Al instalar se acepta su EULA (`ACCEPT_EULA=Y`). Ver más abajo. |
| [Docker Engine](https://github.com/moby/moby) | Apache-2.0 | En Linux y Windows Server. |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | Licencia propietaria de Docker Inc. | Solo en Windows 10/11. Gratuito para uso personal, educación, proyectos open source y empresas pequeñas; las empresas grandes necesitan una suscripción de pago. |
| [Ubuntu](https://ubuntu.com/) (imagen WSL) | Varias (mayoritariamente GPL) | Solo en Windows Server, como máquina Linux de Docker. |
| Imágenes auxiliares: [httpd](https://hub.docker.com/_/httpd), [Alpine](https://hub.docker.com/_/alpine), [jq](https://github.com/jqlang/jq) | Apache-2.0 / MIT | Generar htpasswd, copias de seguridad y leer JSON. |

### SQL Server: elige la edición correcta

La edición se controla con `MSSQL_PID` en `infra/.env`:

- `Express` (**por defecto**): gratuita y permitida en producción. Límite de 10 GB por
  base de datos, de sobra para el portal.
- `Developer`: gratuita, con todas las funciones, pero **solo para desarrollo
  y pruebas**: no está permitido usarla en producción.
- `Standard` / `Enterprise` o una clave de producto: requiere una licencia
  comprada a Microsoft.

## Dependencias incluidas en la imagen del portal

La imagen `ghcr.io/rpardoahora/ahora-cenit` incluye el runtime de
[.NET](https://github.com/dotnet/runtime) (MIT) y las dependencias de
`app/backend` (NuGet) y `app/frontend` (npm), todas con licencias permisivas:

- **NuGet**: Entity Framework Core, ASP.NET Core (MIT), BCrypt.Net-Next (MIT),
  OpenTelemetry (Apache-2.0), Swashbuckle (MIT).
- **npm**: mayoritariamente MIT, ISC, BSD y Apache-2.0. Incluye la fuente
  JetBrains Mono (SIL OFL-1.1). Algunas herramientas usadas solo al compilar
  (`lightningcss`, MPL-2.0; `caniuse-lite`, CC-BY-4.0) no forman parte del
  resultado distribuido.

La lista exacta y sus versiones están en
`app/backend/AhoraCenit.Api/AhoraCenit.Api.csproj` y
`app/frontend/package-lock.json`.
