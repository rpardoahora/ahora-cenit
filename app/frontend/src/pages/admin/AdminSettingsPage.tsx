import { useEffect, useState } from "react"
import { toast } from "sonner"
import { adminSettingsApi } from "@/lib/api"
import { useConfig } from "@/context/ConfigContext"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { Switch } from "@/components/ui/switch"

export function AdminSettingsPage() {
  const { refresh } = useConfig()
  const [registrationEnabled, setRegistrationEnabled] = useState<boolean | null>(null)
  const [isSaving, setIsSaving] = useState(false)

  useEffect(() => {
    adminSettingsApi
      .get()
      .then((settings) => setRegistrationEnabled(settings.registrationEnabled))
      .catch(() => toast.error("No se pudieron cargar los ajustes."))
  }, [])

  async function handleToggle(enabled: boolean) {
    const previous = registrationEnabled
    setRegistrationEnabled(enabled)
    setIsSaving(true)
    try {
      const saved = await adminSettingsApi.update({ registrationEnabled: enabled })
      setRegistrationEnabled(saved.registrationEnabled)
      await refresh()
      toast.success(
        saved.registrationEnabled
          ? "Registro abierto: cualquiera puede crearse una cuenta."
          : "Registro cerrado: solo un administrador puede dar de alta usuarios."
      )
    } catch {
      setRegistrationEnabled(previous)
    } finally {
      setIsSaving(false)
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="font-heading text-2xl font-semibold">Ajustes</h1>
        <p className="text-sm text-muted-foreground">Configuración general del portal.</p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Registro de usuarios</CardTitle>
          <CardDescription>
            Si está activado, cualquier persona puede crearse una cuenta desde el portal. Si lo
            desactivas, desaparece el botón «Registrarse» y solo un administrador puede dar de alta
            usuarios (en Usuarios o por API). Las cuentas existentes no se ven afectadas.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {registrationEnabled === null ? (
            <Skeleton className="h-6 w-48" />
          ) : (
            <label className="flex items-center gap-3 text-sm font-medium">
              <Switch
                checked={registrationEnabled}
                disabled={isSaving}
                onCheckedChange={handleToggle}
              />
              {registrationEnabled ? "Registro abierto" : "Registro cerrado"}
            </label>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
