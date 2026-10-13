import { useState } from "react"
import { toast } from "sonner"
import { sqlAdminApi, ApiError } from "@/lib/api"
import { Button } from "@/components/ui/button"

interface SqlAdminLaunchButtonProps {
  /** Instancia cuya base de datos se explora; sin ella, el SQL Server de la plataforma. */
  applicationId?: string
  size?: "default" | "sm"
  variant?: "default" | "outline"
  label?: string
}

const POLL_MS = 1500

/** Mensaje de carga en la pestaña que se abre (se abre ya al pulsar para que el navegador no la bloquee). */
function writeLoading(target: Window, message: string) {
  target.document.title = "Explorador de BBDD — Cenit"
  target.document.body.innerHTML = `
    <div style="font-family:system-ui,sans-serif;display:flex;flex-direction:column;align-items:center;justify-content:center;height:90vh;gap:16px;color:#333">
      <div style="width:36px;height:36px;border:4px solid #ddd;border-top-color:#1d4ed8;border-radius:50%;animation:s 1s linear infinite"></div>
      <div style="font-size:16px">${message.replace(/</g, "&lt;")}</div>
      <style>@keyframes s{to{transform:rotate(360deg)}}</style>
    </div>`
}

/**
 * "Explorar BBDD": arranca el explorador (CloudBeaver) conectado a la base elegida y lo abre en otra pestaña sin
 * pedir credenciales. Mientras se prepara (la primera vez descarga la imagen) muestra el progreso.
 */
export function SqlAdminLaunchButton({ applicationId, size = "sm", variant = "outline", label = "Explorar BBDD" }: SqlAdminLaunchButtonProps) {
  const [busy, setBusy] = useState(false)

  async function handleClick() {
    const target = window.open("", "_blank")
    if (target) writeLoading(target, "Preparando el explorador de bases de datos...")
    setBusy(true)
    const progress = toast.loading("Preparando el explorador de bases de datos...")
    try {
      const { id } = await sqlAdminApi.launch(applicationId)
      for (;;) {
        await new Promise((resolve) => setTimeout(resolve, POLL_MS))
        const launch = await sqlAdminApi.status(id)
        if (launch.state === "ready" && launch.launchUrl) {
          toast.success(launch.message, { id: progress })
          if (target && !target.closed) {
            target.location.href = launch.launchUrl
          } else {
            window.open(launch.launchUrl, "_blank")
          }
          break
        }
        if (launch.state === "error") {
          throw new Error(launch.message)
        }
        toast.loading(launch.message, { id: progress })
        if (target && !target.closed) writeLoading(target, launch.message)
      }
    } catch (err) {
      target?.close()
      const message = err instanceof ApiError || err instanceof Error ? err.message : "No se pudo abrir el explorador de bases de datos."
      toast.error(message, { id: progress })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Button size={size} variant={variant} onClick={() => void handleClick()} disabled={busy}>
      {busy ? "Abriendo..." : label}
    </Button>
  )
}
