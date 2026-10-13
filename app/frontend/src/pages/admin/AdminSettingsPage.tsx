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
  const [sharedSql, setSharedSql] = useState<boolean | null>(null)
  const [isSaving, setIsSaving] = useState(false)

  useEffect(() => {
    adminSettingsApi
      .get()
      .then((settings) => {
        setRegistrationEnabled(settings.registrationEnabled)
        setSharedSql(settings.appsSqlMode === "Shared")
      })
      .catch(() => toast.error("No se pudieron cargar los ajustes."))
  }, [])

  async function handleSqlToggle(shared: boolean) {
    const previous = sharedSql
    setSharedSql(shared)
    setIsSaving(true)
    try {
      const saved = await adminSettingsApi.update({ appsSqlMode: shared ? "Shared" : "Dedicated" })
      setSharedSql(saved.appsSqlMode === "Shared")
      await refresh()
      toast.success(
        saved.appsSqlMode === "Shared"
          ? "Las nuevas aplicaciones usarán la BBDD compartida."
          : "Las nuevas aplicaciones usarán su propia BBDD."
      )
    } catch {
      setSharedSql(previous)
    } finally {
      setIsSaving(false)
    }
  }

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

      <Card>
        <CardHeader>
          <CardTitle>Base de datos de las aplicaciones</CardTitle>
          <CardDescription>
            Con la BBDD independiente, cada aplicación usa el SQL Server que trae su compose. Con la
            BBDD compartida, todas usan el SQL Server de la plataforma: se desactivan las imágenes SQL
            del compose y cada cliente tiene su propio login, que no ve las bases de los demás. Los
            clientes despliegan siempre con este modo; un administrador puede elegir otro al
            desplegar. Solo afecta a los despliegues nuevos.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {sharedSql === null ? (
            <Skeleton className="h-6 w-48" />
          ) : (
            <label className="flex items-center gap-3 text-sm font-medium">
              <Switch checked={sharedSql} disabled={isSaving} onCheckedChange={handleSqlToggle} />
              {sharedSql ? "BBDD compartida" : "BBDD independiente"}
            </label>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
