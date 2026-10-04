# ahora-cenit

Plataforma para desplegar proyectos **Flexygo** (y cualquier aplicación
Docker) con un clic: un portal donde eliges un producto, pulsas *Instalar* y
tienes tu instancia en `https://<nombre>.tudominio.com`.

Un solo comando deja montado y configurado todo: el portal, Portainer,
Traefik con HTTPS automático, un registry Docker privado, un repositorio
NuGet y la telemetría.

## Requisitos

- Una máquina **x86-64** con 8 GB de RAM o más.
- Linux (Ubuntu, Debian, RHEL…), Windows 10/11 o Windows Server 2022/2025.
- Para producción: un dominio con dos registros DNS de tipo **A** apuntando
  al servidor (`tudominio.com` y `*.tudominio.com`) y los puertos 80 y 443 abiertos.

Docker no hace falta: lo instala el instalador.

## Instalar

**Linux**

```bash
sudo apt-get install -y git        # o: sudo dnf install -y git
sudo git clone https://github.com/rpardoahora/ahora-cenit.git /opt/ahora-cenit
cd /opt/ahora-cenit
sudo ./install.sh
```

**Windows** (PowerShell)

```powershell
git clone https://github.com/rpardoahora/ahora-cenit.git C:\ahora-cenit
cd C:\ahora-cenit
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

El instalador te pregunta si es para **probar en local** o para
**producción** (con tu dominio), un email y una contraseña de
administrador. Al terminar te muestra las direcciones:

| | Dirección |
|---|---|
| Portal | `cloud.tudominio.com` |
| Portainer | `portainer.tudominio.com` |
| Traefik | `traefik.tudominio.com` |
| Registry Docker | `registry.tudominio.com` |
| NuGet | `nuget.tudominio.com` |
| Telemetría | `telemetry.tudominio.com` |

En local las direcciones son `http://cloud.localhost`, etc.

## Mantenimiento

| Comando | Qué hace |
|---|---|
| `sudo ./install.sh --update` | Actualiza a la última versión, conservando los datos. |
| `sudo ./install.sh --status` | Muestra si todo está funcionando. |
| `sudo ./install.sh --backup` | Hace una copia de seguridad en `backups/`. |
| `sudo ./install.sh --restore <carpeta>` | Restaura una copia. |

En Windows, lo mismo con `.\install.ps1 --update`, etc.

Toda la configuración (y las contraseñas) queda en `infra/.env`.

## Más información

- [Guía completa](docs/GUIA.md): DNS, Windows Server, registry, NuGet, plantillas de producto y solución de problemas.
- [API](docs/API.md): automatizar productos, clientes y despliegues con un token.

## Licencia

[Apache-2.0](LICENSE) © 2026 CEESI ASESORES S.L. Ver también [NOTICE](NOTICE)
y [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
