# Infra: Traefik + Portainer

Stack base compartido por todos los proyectos (incluido `../app`, ahora-cenit).
Expone el proxy inverso (Traefik) y la gestión de contenedores (Portainer),
que la API de ahora-cenit usa para desplegar/parar/borrar instancias de producto.

## Red compartida

Todos los stacks (esta infra y cualquier app, incluidas las instancias
desplegadas dinámicamente por ahora-cenit) se conectan a la red externa
`proxy`. Créala una sola vez por host:

```bash
docker network create proxy
```

## Dev (HTTP, dominio `*.ahoracenit.localhost`)

```bash
docker compose --env-file .env.dev -f docker-compose.dev.yml up -d
```

- Portainer: http://portainer.ahoracenit.localhost
- Dashboard Traefik: http://localhost:8080 (sin auth, solo dev)

`*.ahoracenit.localhost` resuelve a 127.0.0.1 sin tocar `/etc/hosts` (dominio
público delegado a localhost).

## Prod (HTTPS con Let's Encrypt, dominio `*.ahoracenit.com`)

1. Copia `.env.prod.example` a `.env.prod` y rellena `ACME_EMAIL` y
   `TRAEFIK_DASHBOARD_AUTH` (hash con `htpasswd`, escapando `$` como `$$`).
2. Asegúrate de que los DNS de `ahoracenit.com`, `*.ahoracenit.com`,
   `portainer.ahoracenit.com` y `traefik.ahoracenit.com` apuntan al host.

```bash
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d
```

- Portainer: https://portainer.ahoracenit.com
- Dashboard Traefik (con auth): https://traefik.ahoracenit.com

## Variables reservadas para stacks de producto

Cuando ahora-cenit despliega el compose de un producto vía la API de
Portainer, siempre inyecta estas dos variables; las plantillas de producto
deben usarlas en su router de Traefik:

- `APP_SUBDOMAIN`: slug único de la instancia (`<producto>-<cliente>`)
- `BASE_DOMAIN`: `ahoracenit.localhost` en dev / `ahoracenit.com` en prod

```yaml
labels:
  - traefik.enable=true
  - traefik.docker.network=proxy
  - traefik.http.routers.${APP_SUBDOMAIN}.rule=Host(`${APP_SUBDOMAIN}.${BASE_DOMAIN}`)
```
