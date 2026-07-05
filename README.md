# ahora-cenit

Marketplace de aplicaciones desplegables bajo demanda vía Docker/Portainer.

## Estructura del repo

```
infra/    stack base: Traefik (proxy inverso) + Portainer      -> ver infra/README.md
app/      marketplace ahora-cenit (frontend+backend en un único contenedor, SQL Server) -> ver app/README.md
```

Ambos son stacks de Docker Compose independientes que comparten la red externa
`proxy` creada por `infra`. `app` es un cliente más de esa infra: expone
sus routers de Traefik igual que cualquier stack de producto desplegado
dinámicamente.

El frontend (React) y la API (.NET) se sirven desde el mismo contenedor y el
mismo puerto: la API (.NET) sirve los estáticos del build de Vite y expone
sus endpoints bajo `/api`, así que todo vive en el mismo origen (sin CORS).

## Dominios

| Entorno | Marketplace (UI + `/api`)   | Portainer                          | Instancias de producto                  |
|---------|-------------------------------|-------------------------------------|-------------------------------------------|
| Dev     | http://ahoracenit.localhost   | http://portainer.ahoracenit.localhost | http://\<producto>-\<cliente>.ahoracenit.localhost |
| Prod    | https://ahoracenit.com        | https://portainer.ahoracenit.com     | https://\<producto>-\<cliente>.ahoracenit.com       |

## Roles

- **Sin sesión**: ve el catálogo de productos activos; al pulsar "Instalar" se le redirige a registro.
- **Cliente**: ve y gestiona (desplegar/ver estado/parar/borrar) únicamente sus propias aplicaciones.
- **Admin**: además de lo anterior, da de alta/edita/borra/desactiva productos y ve las aplicaciones de cualquier cliente.

## Arranque

### Opción rápida: `arrancar.ps1`

Desde la raíz del repo (PowerShell):

```powershell
.\arrancar.ps1
```

Pregunta interactivamente: entorno (dev/prod), qué levantar (todo / solo
infra / solo marketplace), los parámetros de los `.env` correspondientes
(con valores por defecto y generación automática de secretos si detecta
placeholders sin rellenar), y si detecta una instalación previa en marcha
ofrece borrarla (containers + volúmenes) antes de arrancar.

### Manual (orden)

```bash
# 1) una sola vez por máquina
docker network create proxy

# 2) infra
cd infra
docker compose --env-file .env.dev -f docker-compose.dev.yml up -d

# 3) app (ver app/README.md para variables de entorno)
cd ../app
docker compose --env-file .env.dev -f docker-compose.dev.yml up -d --build
```

## Convención reservada para plantillas de producto

Cuando el backend despliega el compose de un producto vía la API de
Portainer, siempre inyecta:

- `APP_SUBDOMAIN`: slug de la instancia (`<slug-producto>-<slug-cliente>`, o el elegido por el usuario)
- `BASE_DOMAIN`: `ahoracenit.localhost` (dev) / `ahoracenit.com` (prod)

Toda plantilla de producto debe usar estas dos variables en su router de
Traefik y conectarse a la red externa `proxy`. Detalle en `infra/README.md`.
