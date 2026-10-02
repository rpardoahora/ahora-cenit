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
| `traefik` | `traefik:v3.6` | `traefik.DOMAIN` | Proxy inverso. Dashboard protegido con `auth/htpasswd`. El dominio raíz redirige a `cloud.DOMAIN`. |
| `portainer` | `portainer/portainer-ce:lts` | `portainer.DOMAIN` | También en `127.0.0.1:9000` (solo loopback) para que el instalador lo configure por API. |
| `registry` | `registry:3` | `registry.DOMAIN` | Registry privado con auth htpasswd. También en `127.0.0.1:5000` (en local Docker hace pull/push contra `localhost:5000`). |
| `app` | `ghcr.io/rpardoahora/ahora-cenit` | `cloud.DOMAIN` | Portal (UI + `/api`). |
| `sqlserver` | `mssql/server:2022` | — | Solo en la red interna `cenit_internal`. |
| `openobserve` | `openobserve` | `telemetry.DOMAIN` | Trazas, métricas y logs de auditoría del portal (OTLP). |
| `forgejo` | `codeberg.org/forgejo/forgejo:15` (LTS) | `nuget.DOMAIN` | Repositorio NuGet. SQLite, sin registro abierto ni SSH. También en `127.0.0.1:5100`: en local es su URL pública (`dotnet` no resuelve `*.localhost`). |

Volúmenes (todos con prefijo `cenit_`): `cenit_sqlserver_data`,
`cenit_portainer_data`, `cenit_registry_data`, `cenit_openobserve_data`,
`cenit_forgejo_data`, `cenit_letsencrypt`.

### NuGet (Forgejo)

El instalador crea el admin `cenit-admin` (CLI `forgejo admin user create`),
las organizaciones `publico` (visibilidad *public*) e `interno` (*private*) y
un token `cenit-nuget` (scopes `write:package`, `read:user`,
`read:organization`) que guarda en `NUGET_TOKEN`. En Forgejo los permisos de
paquetes van por propietario: los paquetes de una organización pública se
leen de forma anónima; los de una privada solo sus miembros (con token).
`FORGEJO_VERSION` en `.env` permite fijar otra versión.

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
