# API de ahora-cenit (automatizaciones)

Todo lo que se hace desde el portal se puede hacer por API: dar de alta
productos, crear clientes y desplegar, consultar, parar, arrancar o borrar
sus aplicaciones. Pensado para CI/CD, scripts y otros servicios.

## Autenticación: el token de plataforma

El instalador genera un **token de plataforma** y lo guarda en `infra/.env`
como `CENIT_TOKEN`. Es el mismo token para todo:

| Para | Cómo |
|---|---|
| **API del portal** | Cabecera `Authorization: Bearer <CENIT_TOKEN>` (o `X-Api-Key: <CENIT_TOKEN>`) |
| **Docker Registry** | `docker login <registry> -u cenit-admin -p <CENIT_TOKEN>` |
| **NuGet** | Usuario `cenit-admin` y contraseña `<CENIT_TOKEN>` en el feed (ver la [guía](GUIA.md#11-repositorio-nuget-feeds-public-e-internal)) |

Con el token se actúa como **administrador** del portal (en nombre del
usuario administrador, y queda auditado como `via=api-token`).

> 🔒 Trátalo como una contraseña de administrador. Si se filtra, bórralo:
> quita la línea `CENIT_TOKEN` de `infra/.env` y ejecuta `--update`. El
> instalador genera uno nuevo y lo actualiza en el portal, en el registry y en
> Nexus (el usuario `cenit-admin`).

**URL base:** `https://cloud.<tu-dominio>/api` (en local:
`http://cloud.localhost/api`, con `:<puerto>` si no es el 80).

```bash
export CENIT=https://cloud.midominio.com/api
export TOKEN=<CENIT_TOKEN>
curl -H "Authorization: Bearer $TOKEN" $CENIT/auth/me
```

Errores: `401` sin token o token incorrecto, `403` sin permiso, `400`
datos no válidos (cuerpo `{"message": "..."}`), `404` no existe, `409`
conflicto. Los JSON usan `camelCase`; los enumerados van como texto.

---

## Productos

Un **producto** es una plantilla *docker-compose* que se puede desplegar
tantas veces como se quiera (cada despliegue es una **aplicación**).

### Crear: `POST /api/products`

```json
{
  "name": "Mi ERP",
  "description": "ERP Flexygo para pymes.",
  "imageUrl": "https://midominio.com/logos/mi-erp.svg",
  "websiteUrl": "https://midominio.com/mi-erp",
  "isActive": true,
  "composeTemplate": "services:\n  web:\n    image: registry.midominio.com/mi-erp:1.4.0\n    ...",
  "envVarsSchema": [
    { "key": "ADMIN_USER",     "label": "Usuario administrador", "defaultValue": "admin",  "mode": "Text" },
    { "key": "ADMIN_PASSWORD", "label": "Contraseña del admin",  "defaultValue": "",       "mode": "Secret" },
    { "key": "PLAN",           "label": "Plan contratado",       "defaultValue": "basico", "mode": "ReadOnly" },
    { "key": "LICENSE_KEY",    "label": "Licencia",              "defaultValue": "XXXX",   "mode": "Hidden" }
  ]
}
```

| Campo | Obligatorio | Descripción |
|---|---|---|
| `name` | **Sí** | Nombre en el catálogo. También forma el subdominio por defecto de cada despliegue. |
| `composeTemplate` | **Sí** | Fichero docker-compose (YAML) como texto. Ver [reglas](#reglas-del-composetemplate). |
| `description` | No | Texto del catálogo. |
| `imageUrl` | No | URL de un logo (SVG/PNG) para el catálogo. |
| `websiteUrl` | No | Web pública del producto. |
| `isActive` | No (`true`) | Si es `false` no aparece en el catálogo ni se puede desplegar. |
| `envVarsSchema` | No | Variables configurables al desplegar (ver abajo). |

Cada variable de `envVarsSchema`:

| Campo | Descripción |
|---|---|
| `key` | Nombre de la variable tal y como aparece en el compose como `${KEY}`. Letras, números y `_`, sin empezar por número; única. `APP_SUBDOMAIN` y `BASE_DOMAIN` están reservadas. |
| `label` | Texto que ve el usuario en el formulario de despliegue (si falta, se usa `key`). |
| `defaultValue` | Valor por defecto. |
| `mode` | `Text` (editable), `Secret` (editable, enmascarada como contraseña y oculta en la auditoría), `ReadOnly` (se ve pero no se puede cambiar: siempre `defaultValue`), `Hidden` (ni se ve ni se puede cambiar: siempre `defaultValue`; útil para claves internas). |

Respuesta `201` con el producto (incluye su `id`).

### Otras operaciones

| Método y ruta | Qué hace |
|---|---|
| `GET /api/products` | Lista los productos activos (`?includeInactive=true` para todos). |
| `GET /api/products/{id}` | Detalle. |
| `PUT /api/products/{id}` | Sustituye el producto: mismos campos que al crear salvo `isActive`. **No afecta** a las aplicaciones ya desplegadas. |
| `PATCH /api/products/{id}/active` | `{"isActive": false}` lo retira del catálogo. |
| `DELETE /api/products/{id}` | Borra el producto. `409` si tiene aplicaciones desplegadas. |
| `GET /api/products/export` / `POST /api/products/import` | Exportar/importar el catálogo completo (JSON). |

### Reglas del `composeTemplate`

Al desplegar, el portal crea un *stack* en Portainer con el compose y le
pasa como variables las de `envVarsSchema` más dos que inyecta siempre:

- `APP_SUBDOMAIN`: nombre único de la instancia (p.ej. `mi-erp-acme`).
- `BASE_DOMAIN`: el dominio de la plataforma (`midominio.com` o `localhost`).

La instancia se publica en `https://${APP_SUBDOMAIN}.${BASE_DOMAIN}`. Para
que funcione:

1. El servicio web debe estar en la red externa **`proxy`** y declararla al final.
2. Etiquetas de Traefik con `${APP_SUBDOMAIN}` como nombre de router y servicio:
   ```yaml
   labels:
     - traefik.enable=true
     - traefik.http.routers.${APP_SUBDOMAIN}.rule=Host(`${APP_SUBDOMAIN}.${BASE_DOMAIN}`)
     - traefik.http.services.${APP_SUBDOMAIN}.loadbalancer.server.port=80   # puerto interno del contenedor
   ```
3. **No** pongas `entrypoints` ni `tls.certresolver`: el HTTPS se aplica solo en producción.
4. **No** uses `container_name` ni publiques puertos (`ports:`): el mismo producto se despliega muchas veces en la misma máquina.
5. Si hay más servicios (p.ej. una base de datos), no los pongas en `proxy`: comparten una red interna del stack automáticamente.
6. Los volúmenes con nombre son propios de cada instancia y **se borran al borrar la aplicación**.
7. Imágenes: públicas (Docker Hub, ghcr.io…) o del registry interno (`registry.<dominio>/…`, en local `localhost:5000/…`), que Portainer ya tiene configurado.
8. Para un `$` literal en el compose escribe `$$`.

Ejemplo completo:

```yaml
services:
  web:
    image: registry.midominio.com/mi-erp:1.4.0
    restart: unless-stopped
    environment:
      ADMIN_USER: ${ADMIN_USER}
      ADMIN_PASSWORD: ${ADMIN_PASSWORD}
      PLAN: ${PLAN}
      LICENSE_KEY: ${LICENSE_KEY}
      DB_CONNECTION: Server=db;Database=erp;User Id=sa;Password=${ADMIN_PASSWORD}
      PUBLIC_URL: https://${APP_SUBDOMAIN}.${BASE_DOMAIN}
    depends_on:
      - db
    networks:
      - proxy
      - default
    labels:
      - traefik.enable=true
      - traefik.docker.network=proxy
      - traefik.http.routers.${APP_SUBDOMAIN}.rule=Host(`${APP_SUBDOMAIN}.${BASE_DOMAIN}`)
      - traefik.http.services.${APP_SUBDOMAIN}.loadbalancer.server.port=8080
  db:
    image: mcr.microsoft.com/mssql/server:2022-latest
    restart: unless-stopped
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_PID: Express
      MSSQL_SA_PASSWORD: ${ADMIN_PASSWORD}
    volumes:
      - db_data:/var/opt/mssql
volumes:
  db_data:
networks:
  proxy:
    external: true
```

(`traefik.docker.network=proxy` es necesario cuando el servicio está en más
de una red.)

---

## Clientes

Los clientes son usuarios del portal con rol `Cliente`: son los
propietarios de las aplicaciones y pueden entrar al portal a verlas.

| Método y ruta | Qué hace |
|---|---|
| `POST /api/users` | Crea un usuario: `{"email": "...", "name": "Acme S.L.", "password": "min. 8 caracteres", "role": "Cliente"}` (o `"Admin"`). `409` si el email ya existe. Devuelve su `id` y su `clientSlug`. |
| `GET /api/users` | Lista todos los usuarios (para buscar uno por email). |
| `GET /api/users/{id}` | Detalle. |
| `PUT /api/users/{id}` | `{"email", "name", "role"}`. |
| `POST /api/users/{id}/reset-password` | `{"newPassword": "..."}`. |
| `DELETE /api/users/{id}` | Borra el usuario. `409` si aún tiene aplicaciones (bórralas antes). |

---

## Aplicaciones (despliegues)

### Desplegar: `POST /api/applications`

```json
{
  "productId": "2d0d6848-1096-476f-9648-9a26b840970b",
  "userId": "96062d51-4879-4da9-aa31-a380244c19be",
  "subdomain": "mi-erp-acme",
  "envVars": { "ADMIN_USER": "acme", "ADMIN_PASSWORD": "S3gura!" }
}
```

| Campo | Descripción |
|---|---|
| `productId` | **Obligatorio.** Producto activo a desplegar. |
| `userId` | Cliente propietario. Si se omite, la aplicación es del administrador. |
| `subdomain` | Opcional. Se normaliza (minúsculas, sin acentos ni espacios) y, si ya existe, se le añade un sufijo: **usa el `subdomain` de la respuesta**, no el que enviaste. Por defecto `<producto>-<cliente>`. |
| `envVars` | Valores de las variables `Text` y `Secret`. Las que no envíes toman su `defaultValue`; las `ReadOnly` y `Hidden` ignoran lo que envíes. |

La llamada **espera a que el despliegue termine** (descarga de imágenes
incluida; puede tardar minutos: usa un timeout amplio, p.ej. 15 min).

- `201`: desplegada. `status` es `Running`, o `Provisioning` en producción mientras se emite el certificado HTTPS.
- `502`: Portainer no pudo desplegarla; el cuerpo trae `message`, `error` y la `application` con `status: "Error"`.

Respuesta (`ApplicationResponse`):

```json
{
  "id": "…", "productId": "…", "productName": "Mi ERP",
  "userId": "…", "ownerName": "Acme S.L.", "ownerClientSlug": "acme-s-l",
  "subdomain": "mi-erp-acme", "fullDomain": "mi-erp-acme.midominio.com",
  "envVarValues": { "ADMIN_USER": "acme", "ADMIN_PASSWORD": "S3gura!", "PLAN": "basico", "LICENSE_KEY": "XXXX" },
  "status": "Running", "portainerStackId": 12, "portainerEndpointId": 1,
  "createdAt": "…", "updatedAt": "…"
}
```

### Consultar y gestionar

| Método y ruta | Qué hace |
|---|---|
| `GET /api/applications` | Lista (`?userId=<id>` o `?clientSlug=<slug>` para filtrar por cliente). |
| `GET /api/applications/{id}` | Detalle con el último estado guardado. |
| `GET /api/applications/{id}/status` | **Consulta el estado real** en Docker/Portainer, lo guarda y lo devuelve. |
| `POST /api/applications/{id}/stop` | Para la aplicación (los datos se conservan). |
| `POST /api/applications/{id}/start` | La vuelve a arrancar. |
| `DELETE /api/applications/{id}` | La borra **con sus datos** (stack y volúmenes). `204`. |

Estados (`status`): `Deploying`, `Running`, `Provisioning` (arrancada,
esperando el certificado HTTPS), `Stopped`, `Error`, `Deleted` (sus
contenedores ya no existen: se borraron fuera del portal).

---

## Ajustes del portal

| Método y ruta | Qué hace |
|---|---|
| `GET /api/admin/settings` | Devuelve `{"registrationEnabled": true}`. |
| `PUT /api/admin/settings` | `{"registrationEnabled": false}`: cierra el registro abierto. |

Con `registrationEnabled: false`, `POST /api/auth/register` responde `403` y
el portal oculta el botón «Registrarse»; solo un administrador puede crear
usuarios (`POST /api/users`). Las cuentas existentes siguen funcionando.
Es el mismo interruptor que hay en el panel, en **Administración → Ajustes**.
El valor se guarda en la base de datos; el valor inicial (primer arranque) es
el de la variable `REGISTRATION_ENABLED` del `infra/.env` (por defecto `true`).
`GET /api/config` (público) incluye `registrationEnabled` para saber el estado.

---

## Ejemplo de principio a fin

```bash
CENIT=https://cloud.midominio.com/api
AUTH="Authorization: Bearer $TOKEN"

# 1. Producto
PRODUCT_ID=$(curl -s -H "$AUTH" -H "Content-Type: application/json" \
  -d @producto.json $CENIT/products | jq -r .id)

# 2. Cliente
USER_ID=$(curl -s -H "$AUTH" -H "Content-Type: application/json" \
  -d '{"email":"it@acme.com","name":"Acme S.L.","password":"Cambiar.12345","role":"Cliente"}' \
  $CENIT/users | jq -r .id)

# 3. Despliegue
APP=$(curl -s --max-time 900 -H "$AUTH" -H "Content-Type: application/json" \
  -d "{\"productId\":\"$PRODUCT_ID\",\"userId\":\"$USER_ID\",\"subdomain\":\"erp-acme\",\"envVars\":{\"ADMIN_PASSWORD\":\"S3gura!\"}}" \
  $CENIT/applications)
APP_ID=$(echo "$APP" | jq -r .id); echo "$APP" | jq -r .fullDomain

# 4. Estado, parar, arrancar, borrar
curl -s -H "$AUTH" $CENIT/applications/$APP_ID/status | jq -r .status
curl -s -X POST -H "$AUTH" $CENIT/applications/$APP_ID/stop  | jq -r .status
curl -s -X POST -H "$AUTH" $CENIT/applications/$APP_ID/start | jq -r .status
curl -s -X DELETE -H "$AUTH" $CENIT/applications/$APP_ID -o /dev/null -w "%{http_code}\n"
```
