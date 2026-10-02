# Configuración dinámica de Traefik

Traefik vigila esta carpeta (file provider). Cualquier `.yml` que dejes aquí
(routers, middlewares o servicios extra que no vivan en Docker) se aplica en
caliente, sin reiniciar nada.
