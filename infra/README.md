# infra — stack completo de ahora-cenit

Esta carpeta contiene todo lo que se despliega en un servidor. **No hace
falta tocar nada a mano**: `install.sh` / `install.ps1` (en la raíz del repo)
generan la configuración y lo levantan todo. Ver el [README principal](../README.md).

| Fichero | Para qué sirve |
|---|---|
| `docker-compose.yml` | Stack completo (proyecto compose `cenit`). Modo local, HTTP. |
| `docker-compose.prod.yml` | Override de producción: puerto 443 y HTTPS con Let's Encrypt (HTTP-01). |
| `.env` | **Generado por el instalador.** Dominio, modo, contraseñas, API key de Portainer… No va a git. |
| `auth/htpasswd` | **Generado por el instalador.** Usuario `admin` del dashboard de Traefik y del registry. |
| `traefik/dynamic/` | Configuración dinámica extra de Traefik (file provider, recarga en caliente). |

`.env` define `COMPOSE_FILE`, así que desde esta carpeta funcionan los
comandos normales de compose sin indicar ficheros:

```bash
docker compose ps
docker compose logs -f app
docker compose restart portainer
docker compose up -d          # aplica cambios hechos a mano en .env
```

## Servicios

| Servicio | Imagen | Host público | Notas |
|---|---|---|---|
| `traefik` | `traefik:v3.6` | `traefik.DOMAIN` | Proxy inverso. Dashboard protegido con `auth/htpasswd`. |
| `portainer` | `portainer/portainer-ce:lts` | `portainer.DOMAIN` | También en `127.0.0.1:9000` (solo loopback) para que el instalador lo configure por API. |
| `registry` | `registry:3` | `registry.DOMAIN` | Registry privado con auth htpasswd. También en `127.0.0.1:5000` (en local Docker hace pull/push contra `localhost:5000`). |
| `app` | `ghcr.io/rpardoahora/ahora-cenit` | `cloud.DOMAIN` | Portal (UI + `/api`). |
| `sqlserver` | `mssql/server:2022` | — | Solo en la red interna `cenit_internal`. |
| `openobserve` | `openobserve` | `telemetry.DOMAIN` | Trazas, métricas y logs de auditoría del portal (OTLP). |
| `nuget` | `bagetter/bagetter:1.6.5` | `nuget.DOMAIN` | Repositorio NuGet privado (BaGetter). También en `127.0.0.1:5100`: en local es su URL pública (`dotnet` no resuelve `*.localhost`). |

Volúmenes (todos con prefijo `cenit_`): `cenit_sqlserver_data`,
`cenit_portainer_data`, `cenit_registry_data`, `cenit_openobserve_data`,
`cenit_nuget_data`, `cenit_letsencrypt`.

### Explorador de BBDD (CloudBeaver bajo demanda)

No está en el compose: el portal crea el contenedor `cenit-sqladmin` (imagen
`dbeaver/cloudbeaver`, volumen `cenit_sqladmin_data`, redes `proxy`,
`cenit_sql` y `cenit_sqladmin`) la primera vez que un administrador pulsa
**Explorar BBDD**, escribe en su espacio de trabajo una única conexión global
(la base elegida) y lo arranca. CloudBeaver funciona en modo anónimo; lo que
protege `sqladmin.DOMAIN` es el `forwardAuth` de Traefik contra el portal
(`/api/sqladmin/auth`, alias `cenit-portal`), que exige la cookie que deja el
pase de un solo uso (`/__cenit/enter`, enrutado al portal). Sin el contenedor,
un router de prioridad 1 en el portal redirige a Cenit. El portal lo para tras
`SQLADMIN_IDLE_MINUTES` minutos sin peticiones. Los SQL Server propios de las
instancias se unen a `cenit_sqladmin` (alias `<instancia>-sql`) al explorarlos.

### NuGet (BaGetter)

Se configura entero por variables de entorno en el compose (el instalador solo
espera a que responda `/health`):

- **Feed privado**: `Authentication__Credentials__*` con `cenit-admin`
  (`CENIT_TOKEN`), `cenit-apps` (`NUGET_READ_TOKEN`, solo lectura, para las
  aplicaciones) y `admin` (`ADMIN_PASSWORD`). Sin credenciales, `/v3` responde 401.
- **Subir y borrar**: `ApiKey` = `CENIT_TOKEN`. `dotnet nuget push/delete` lee
  antes `/v3/index.json`, así que el origen tiene que llevar también usuario y
  contraseña: con la URL y la API key sola responde 401.
- **Borrado definitivo**: `PackageDeletionBehavior=HardDelete`; no se admite
  sobrescribir una versión (`AllowPackageOverwrites=false`).
- **Retención**: `Retention__Max{Major,Minor,Patch,Prerelease}Versions` desde
  `NUGET_RETENTION_*` (3/5/5/10 por defecto). BaGetter solo la aplica si están
  las cuatro.
- **Web**: Traefik la protege con `cenit-auth` (htpasswd); las rutas del feed
  (`/v3`, `/api`, `/health`) van por otro router sin ese middleware porque las
  autentica BaGetter (con sus propios usuarios y la API key, no con el htpasswd).

Datos (SQLite y paquetes) en el volumen `cenit_nuget_data` (`/data`).
`BAGETTER_VERSION` en `.env` fija otra versión.

## Cómo queda todo enlazado

- **Red `proxy`** (externa): la comparten Traefik, Portainer, el registry, el
  portal, OpenObserve y **todas las instancias de producto** que despliega el
  portal. Traefik solo enruta contenedores con `traefik.enable=true`.
- **Portainer**: el instalador crea el usuario `admin` (usando el setup token
  que Portainer escribe en su log), da de alta el entorno Docker local, genera
  una API key y la guarda en `.env` (`PORTAINER_API_KEY`,
  `PORTAINER_ENDPOINT_ID`), que es lo que usa el portal para crear stacks.
- **Registry en Portainer**: queda registrado como registry `ahora-cenit`
  (`REGISTRY_HOST` = `registry.DOMAIN` en producción, `localhost:5000` en
  local) con sus credenciales, así que los stacks pueden usar imágenes privadas.
- **TLS**: en producción los routers no declaran `entrypoints` ni
  `certresolver`; lo heredan del entrypoint `websecure`. Por eso las mismas
  etiquetas sirven en local (HTTP) y en producción (HTTPS).

## Plantillas de producto

El portal inyecta en cada stack `APP_SUBDOMAIN` (slug de la instancia) y
`BASE_DOMAIN` (= `DOMAIN`). Una plantilla mínima:

```yaml
services:
  web:
    image: registry.midominio.com/mi-app:1.0   # en local: localhost:5000/mi-app:1.0
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

No pongas `entrypoints=websecure` ni `tls.certresolver=le` en las
plantillas: en local no existe el entrypoint `websecure` y en producción ya
se aplica solo.
