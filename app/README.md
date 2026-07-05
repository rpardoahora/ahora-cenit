# app — ahora-cenit (marketplace)

Stack de Docker Compose con un único servicio `app` (frontend React + shadcn
y API .NET 10 en el mismo contenedor, mismo puerto: la API sirve los
estáticos del build de Vite desde `wwwroot` y expone sus endpoints bajo
`/api`, así que todo va por el mismo origen, sin CORS) y `sqlserver` (SQL
Server). Se despliega sobre la infra compartida de `../infra` (Traefik +
Portainer) y se conecta a su red externa `proxy`.

La imagen se construye con `Dockerfile` en esta misma carpeta (contexto
`app/`, no `app/backend` ni `app/frontend`): un stage compila el frontend
(Vite, con `VITE_API_URL=/api` fijo), otro publica el backend, y el stage
final copia ambos (el backend sirve `wwwroot` + `/api`).

Requiere que `../infra` esté levantada primero (ver `../infra/README.md`) y
que exista la red `proxy` (`docker network create proxy`).

## Dev

```bash
docker compose --env-file .env.dev -f docker-compose.dev.yml up -d --build
```

- Marketplace + API: http://ahoracenit.localhost (API bajo `http://ahoracenit.localhost/api`, también expuesta directa en `localhost:8081`, ya que `8080` lo usa el dashboard de Traefik)
- SQL Server accesible en `localhost:1433` (sa / valor de `DB_SA_PASSWORD`)

Antes de desplegar aplicaciones de verdad, entra en Portainer
(http://portainer.ahoracenit.localhost), genera una API Key de usuario y
ponla en `PORTAINER_API_KEY` dentro de `.env.dev`.

## Prod

Copia `.env.prod.example` a `.env.prod`, rellena todos los secretos
(`DB_SA_PASSWORD`, `JWT_SECRET`, `PORTAINER_API_KEY`, `ADMIN_PASSWORD`) y:

```bash
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --build
```

## Variables de entorno

| Variable | Descripción |
|---|---|
| `DOMAIN` | dominio base (`ahoracenit.localhost` / `ahoracenit.com`), usado por Traefik y para componer subdominios de instancias |
| `ConnectionStrings__Default` | cadena de conexión SQL Server (montada por compose) |
| `Jwt__Secret` / `Jwt__Issuer` / `Jwt__ExpiresMinutes` | firma y expiración de los JWT propios |
| `Portainer__Url` / `Portainer__ApiKey` / `Portainer__EndpointId` | acceso a la API de Portainer para crear/parar/borrar stacks |
| `AdminSeed__Email` / `AdminSeed__Password` | credenciales del admin creado automáticamente si no existe ninguno |

`VITE_API_URL` ya no depende del entorno: siempre es `/api` (mismo origen),
fijado dentro del `Dockerfile` — no hace falta configurarlo por `.env`.

Detalle de cada proyecto en `backend/README.md` y `frontend/README.md`
(sus propios `Dockerfile` ya no se usan; el único build real es el de
`app/Dockerfile`).
