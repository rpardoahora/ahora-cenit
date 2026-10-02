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

**Despliegue (local o producción):** no se hace desde aquí. Usa el
instalador de la raíz del repo (`install.sh` / `install.ps1`), que levanta
la imagen publicada en `ghcr.io` junto con el resto de la infraestructura.
Ver el [README principal](../README.md).

## Desarrollo (compilando desde el código)

`docker-compose.dev.yml` construye el portal desde este código y lo engancha
a una instalación **local** existente (hecha con el instalador): usa su red
`proxy`, su Traefik y su Portainer. Tiene su propia base de datos y
OpenObserve (contenedores `cenit-dev-*`, volúmenes `cenit_dev_*`), así que
no toca los datos de la instalación.

```bash
cp .env.dev.example .env.dev      # y rellena PORTAINER_API_KEY (ver abajo)
docker compose --env-file .env.dev -f docker-compose.dev.yml up -d --build
```

- Portal + API: http://ahoracenit.localhost (con el puerto de Traefik si no es el 80, p.ej. `:8880`); también directo en `localhost:8081`
- SQL Server accesible en `localhost:1433` (sa / valor de `DB_SA_PASSWORD`)
- OpenObserve: http://localhost:5080 o http://telemetry.ahoracenit.localhost

Para desplegar aplicaciones desde el portal de desarrollo, pon en
`PORTAINER_API_KEY` la API key de Portainer: puedes reutilizar la que el
instalador guardó en `../infra/.env`, o generar otra en Portainer
(*My account* → *Access tokens*).

## Variables de entorno

| Variable | Descripción |
|---|---|
| `DOMAIN` | dominio base (`ahoracenit.localhost` / `ahoracenit.com`), usado por Traefik y para componer subdominios de instancias |
| `ConnectionStrings__Default` | cadena de conexión SQL Server (montada por compose) |
| `Jwt__Secret` / `Jwt__Issuer` / `Jwt__ExpiresMinutes` | firma y expiración de los JWT propios |
| `Portainer__Url` / `Portainer__ApiKey` / `Portainer__EndpointId` | acceso a la API de Portainer para crear/parar/borrar stacks |
| `AdminSeed__Email` / `AdminSeed__Password` | credenciales del admin creado automáticamente si no existe ninguno |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | opcional; si se rellena (p.ej. `http://openobserve:5080/api/default`), la auditoría de altas/bajas/cambios de Usuario, Producto y Aplicación (y métricas/logs generales) se exporta vía OpenTelemetry. Vacía por defecto: no se envía nada, solo queda el log estructurado de la API |
| `OPENOBSERVE_ROOT_EMAIL` / `OPENOBSERVE_ROOT_PASSWORD` | credenciales del usuario root de OpenObserve: login del panel y autenticación (Basic Auth) de la ingesta OTLP |

`VITE_API_URL` ya no depende del entorno: siempre es `/api` (mismo origen),
fijado dentro del `Dockerfile` — no hace falta configurarlo por `.env`.

## Auditoría

Cada alta/modificación/baja de Usuario, Producto o Aplicación (incluye
desplegar, arrancar, parar y borrar) queda registrada con fecha, duración,
usuario que la ejecuta, parámetros y resultado (los valores marcados como
secretos en el schema de variables de entorno se redactan como `***`, igual
que las contraseñas). Esto siempre se escribe en el log estructurado de la
API (`docker compose logs app`); si además se configura
`OTEL_EXPORTER_OTLP_ENDPOINT`, cada evento se exporta también a OpenObserve
(incluido en este mismo compose) como:
- **traza** (duración exacta, actor, parámetros/resultado como atributos),
- **métrica** (`ahora_cenit.audit.actions` contador y `ahora_cenit.audit.action.duration`
  histograma, ambas con etiquetas `entity_type`/`action`/`success`, para ver
  ritmo de altas/bajas y percentiles de duración por tipo de acción),
- **log** (la misma línea que ya se escribe en consola).

También se exportan métricas estándar de ASP.NET Core, HttpClient (llamadas
a Portainer) y del runtime de .NET.

Detalle de cada proyecto en `backend/README.md` y `frontend/README.md`
(sus propios `Dockerfile` ya no se usan; el único build real es el de
`app/Dockerfile`).
