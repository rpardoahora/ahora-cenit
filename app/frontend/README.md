# ahora-cenit — frontend

Frontend del marketplace de aplicaciones dockerizadas. Vite + React + TypeScript,
UI con Tailwind CSS v4 y [shadcn/ui](https://ui.shadcn.com).

## Dev

```bash
npm install
npm run dev
```

En Docker, el frontend y la API se sirven desde el mismo contenedor y el
mismo origen (la API va bajo `/api`), así que allí `VITE_API_URL` está fijado
a `/api` en el `Dockerfile` y no depende del entorno. Para desarrollo local
con `npm run dev` (servidor de Vite aparte del backend), copia `.env.example`
a `.env` y pon en `VITE_API_URL` la URL completa donde tengas el backend
corriendo (ver `../README.md` para el resto del stack).

## Variables de entorno

| Variable        | Descripción                                                                |
|-----------------|-----------------------------------------------------------------------------|
| `VITE_API_URL`  | Base de las llamadas a la API. Vite la embebe en el bundle en **build time**. En Docker es siempre `/api` (mismo origen); en `npm run dev` suelta, la URL completa del backend. |

## Scripts

- `npm run dev` — servidor de desarrollo con HMR.
- `npm run build` — type-check (`tsc -b`) + build de producción a `dist/`.
- `npm run preview` — sirve `dist/` localmente para verificar el build.
- `npm run lint` — lint con oxlint.

## Estructura

```
src/
  components/ui/     componentes shadcn/ui generados (button, card, table, ...)
  components/         Navbar, Layout, StatusBadge, ConfirmDialog
  context/            AuthContext (usuario, login/register/logout, JWT en localStorage)
  routes/             RequireAuth, RequireAdmin (guards de rutas)
  lib/api.ts           cliente fetch con Authorization header, manejo de 401 y toasts
  pages/               páginas públicas, de cliente y admin/ (gestión de productos)
```

## Docker

Este frontend **no tiene su propio `Dockerfile`**. Se construye como parte de
la imagen única de `app/` (ver `../Dockerfile` y `../README.md`): un stage de
`node` compila este proyecto con `VITE_API_URL=/api` fijo, y el resultado
(`dist/`) se copia como `wwwroot` dentro de la imagen del backend .NET, que
lo sirve con fallback SPA (`MapFallbackToFile("index.html")` en
`Program.cs`) para que las rutas de React Router (`/aplicaciones/123`,
`/admin/productos`, ...) funcionen al recargar o entrar directamente por URL.

## Sobre el preset de shadcn/ui

El proyecto se inicializó con:

```bash
npx shadcn@latest init --preset b1temXdsR
```

El flag `--preset` existía en la versión de la CLI instalada (`shadcn@4.13.0`)
y el preset `b1temXdsR` se aplicó correctamente (estilo `base-luma`, iconos
`hugeicons`, tipografía JetBrains Mono, Tailwind v4). **No hizo falta ningún
fallback.** Si en el futuro necesitas reinicializar el tema desde cero:

```bash
npx shadcn@latest init --preset b1temXdsR -y --template vite
```

Requisitos previos para que el comando anterior valide correctamente en un
proyecto Vite nuevo (si no vinieran ya configurados):

1. Tailwind v4 instalado con el plugin de Vite (`@tailwindcss/vite`) y
   `@import "tailwindcss";` en `src/index.css`.
2. Alias de import `@/*` apuntando a `./src/*` en `tsconfig.json` /
   `tsconfig.app.json` (`compilerOptions.paths`) y en `vite.config.ts`
   (`resolve.alias`).

## Nota sobre el componente "form"

La lista de componentes shadcn solicitada incluía `form`. En la versión de
CLI/registro usada (`shadcn@4.13.0`, estilo `base-luma` sobre `@base-ui/react`),
el item `form` del registro es un placeholder vacío heredado de versiones
anteriores basadas en `react-hook-form` + Radix. El registro actual sustituye
esa pieza por el primitivo **`field`** (`npx shadcn@latest add field`), que sí
se instaló (`src/components/ui/field.tsx`, con `Field`, `FieldLabel`,
`FieldGroup`, `FieldError`, etc.) y es lo que usan todos los formularios de
esta app (login, registro, despliegue, alta/edición de productos).
