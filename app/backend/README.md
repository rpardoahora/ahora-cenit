# AhoraCenit API

Backend del marketplace de aplicaciones dockerizadas AhoraCenit. ASP.NET Core Web API (minimal hosting) sobre .NET 10, EF Core + SQL Server, JWT propio, e integración con Portainer para desplegar/gestionar stacks docker compose.

## Estructura

```
app/backend/
  AhoraCenit.sln
  AhoraCenit.Api/
    Data/                 DbContext + entidades EF Core + Migrations/
    Contracts/            DTOs de request/response, por feature
    Features/
      Auth/                /api/auth/*
      Products/            /api/products/*
      Applications/        /api/applications/*
    Services/              PortainerClient, SlugGenerator, JwtTokenService, opciones
```

Este proyecto ya no tiene su propio `Dockerfile`: la imagen se construye
desde `app/Dockerfile` (contexto `app/`), que además compila el frontend y
lo copia como `wwwroot` para servirlo desde el mismo proceso/puerto que la
API (ver `../README.md`). En producción, `Program.cs` sirve el SPA con
`UseStaticFiles()` + `MapFallbackToFile("index.html")` para cualquier ruta
que no empiece por `/api`.

## Requisitos

- .NET SDK 10
- SQL Server accesible (local, Docker o remoto)
- Una instancia de Portainer CE accesible, con una API Key generada

## Ejecutar en local

```bash
cd app/backend/AhoraCenit.Api
dotnet run
```

Por defecto arranca en `http://localhost:5271` (perfil `http` de `Properties/launchSettings.json`) con `ASPNETCORE_ENVIRONMENT=Development`, lo que habilita Swagger en `/swagger`.

Al arrancar, el backend:

1. Aplica automáticamente las migraciones de EF Core pendientes (`db.Database.Migrate()`).
2. Si no existe ningún usuario con rol `Admin`, crea uno a partir de `AdminSeed:Email` / `AdminSeed:Password` (o los defaults `admin@ahoracenit.com` / `ChangeMe123!` si no se configuran).

## Variables de entorno

Todas se pueden pasar como variables de entorno (formato `Seccion__Clave`) o en `appsettings.{Environment}.json`.

| Variable | Descripción |
|---|---|
| `ConnectionStrings__Default` | Cadena de conexión a SQL Server |
| `Jwt__Secret` | Secreto para firmar los JWT (HMAC SHA-256) |
| `Jwt__Issuer` | Issuer/audience de los JWT (por defecto `AhoraCenit`) |
| `Jwt__ExpiresMinutes` | Minutos de validez del token (por defecto 1440) |
| `Portainer__Url` | URL base de la API de Portainer |
| `Portainer__ApiKey` | API Key de Portainer (`X-API-Key`) |
| `Portainer__EndpointId` | Id del entorno Docker gestionado por Portainer (normalmente `1` en local) |
| `BaseDomain` | Dominio base público (`ahoracenit.localhost` en dev, `ahoracenit.com` en prod) |
| `AdminSeed__Email` | Email del admin sembrado en el primer arranque |
| `AdminSeed__Password` | Password del admin sembrado en el primer arranque |

No hay configuración de CORS: en Docker el frontend y la API se sirven desde
el mismo origen (`/api`), así que no hace falta.

En desarrollo, si no se define `Jwt__Secret`, se usa un secreto de desarrollo inseguro embebido — **no usar en producción**, define siempre `Jwt__Secret` fuera de local.

## Migraciones de EF Core

La migración inicial (`InitialCreate`) ya está generada en `AhoraCenit.Api/Data/Migrations/` y se aplica automáticamente al arrancar la aplicación (no hace falta ejecutar `dotnet ef database update` manualmente en despliegues).

Para generarla desde cero o añadir nuevas migraciones tras cambiar el modelo (requiere la herramienta `dotnet-ef`: `dotnet tool install --global dotnet-ef`):

```bash
cd app/backend/AhoraCenit.Api
dotnet ef migrations add NombreDeLaMigracion --output-dir Data/Migrations
```

Para aplicarlas manualmente contra una base de datos concreta (normalmente no hace falta, ver arriba):

```bash
dotnet ef database update
```

## Docker

Ver `../README.md` — la imagen (frontend + backend) se construye desde
`app/Dockerfile` con `docker compose`, no build/run manual de esta carpeta
sola. El contenedor resultante escucha en el puerto `8080`, pensado para ir
detrás de Traefik en `<dominio>` (frontend en `/`, API en `/api`).
