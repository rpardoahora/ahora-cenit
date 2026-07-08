import { useEffect, useState } from "react"
import { useNavigate, useParams } from "react-router-dom"
import { toast } from "sonner"
import { applicationsApi, ApiError } from "@/lib/api"
import type { Application } from "@/types"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { StatusBadge } from "@/components/StatusBadge"
import { ConfirmDialog } from "@/components/ConfirmDialog"

export function ApplicationDetailPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const [application, setApplication] = useState<Application | null>(null)
  const [loading, setLoading] = useState(true)
  const [isRefreshing, setIsRefreshing] = useState(false)

  async function load() {
    if (!id) return
    try {
      const data = await applicationsApi.get(id)
      setApplication(data)
      if (data.portainerStackId !== null) {
        applicationsApi
          .status(id)
          .then(({ status }) => setApplication((prev) => (prev ? { ...prev, status } : prev)))
          .catch(() => {
            // Si Portainer no responde, se mantiene el último estado conocido.
          })
      }
    } catch {
      toast.error("No se pudo cargar la aplicación.")
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  // Mientras el estado no sea definitivo (desplegando, obteniendo certificado, o marcada
  // como error) seguimos refrescando solos, sin que el usuario tenga que pulsar "Refrescar":
  // un despliegue lento puede haberse marcado como error por timeout aunque en Portainer
  // haya terminado bien, y el próximo refresco automático lo recupera solo.
  useEffect(() => {
    const pollableStatuses = ["Deploying", "Provisioning", "Error"]
    if (!id || !application || !pollableStatuses.includes(application.status)) {
      return
    }

    const interval = setInterval(async () => {
      try {
        const { status } = await applicationsApi.status(id)
        setApplication((prev) => (prev ? { ...prev, status } : prev))
      } catch {
        // se reintenta en el próximo tick
      }
    }, 4000)

    return () => clearInterval(interval)
  }, [id, application?.status])

  async function handleRefresh() {
    if (!id) return
    setIsRefreshing(true)
    try {
      const { status } = await applicationsApi.status(id)
      setApplication((prev) => (prev ? { ...prev, status } : prev))
    } catch {
      toast.error("No se pudo refrescar el estado.")
    } finally {
      setIsRefreshing(false)
    }
  }

  async function handleStart() {
    if (!application) return
    try {
      const updated = await applicationsApi.start(application.id)
      setApplication(updated)
      toast.success("Aplicación iniciada.")
    } catch (err) {
      if (!(err instanceof ApiError)) toast.error("No se pudo iniciar la aplicación.")
    }
  }

  async function handleStop() {
    if (!application) return
    try {
      const updated = await applicationsApi.stop(application.id)
      setApplication(updated)
      toast.success("Aplicación parada.")
    } catch (err) {
      if (!(err instanceof ApiError)) toast.error("No se pudo parar la aplicación.")
    }
  }

  async function handleDelete() {
    if (!application) return
    try {
      await applicationsApi.remove(application.id)
      toast.success("Aplicación eliminada.")
      navigate("/aplicaciones")
    } catch (err) {
      if (!(err instanceof ApiError)) toast.error("No se pudo eliminar la aplicación.")
    }
  }

  if (loading) {
    return (
      <div className="mx-auto max-w-xl space-y-4">
        <Skeleton className="h-8 w-1/2" />
        <Skeleton className="h-64 w-full" />
      </div>
    )
  }

  if (!application) {
    return <p className="text-sm text-muted-foreground">Aplicación no encontrada.</p>
  }

  return (
    <div className="mx-auto max-w-xl">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            {application.productName}
            <StatusBadge status={application.status} />
          </CardTitle>
          <CardDescription>{application.subdomain}</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
            <dt className="text-muted-foreground">Cliente</dt>
            <dd>{application.ownerName}</dd>
            <dt className="text-muted-foreground">URL</dt>
            <dd>
              <a
                href={`${window.location.protocol}//${application.fullDomain}`}
                target="_blank"
                rel="noreferrer"
                className="text-primary underline-offset-4 hover:underline"
              >
                {application.fullDomain}
              </a>
            </dd>
            <dt className="text-muted-foreground">Creada</dt>
            <dd>{new Date(application.createdAt).toLocaleString()}</dd>
          </dl>

          {application.status === "Provisioning" && (
            <p className="text-sm text-muted-foreground">
              El contenedor ya está en marcha; Traefik está emitiendo el certificado HTTPS
              del dominio. Puede tardar hasta un minuto.
            </p>
          )}

          {application.status === "Deploying" && (
            <p className="text-sm text-muted-foreground">
              El despliegue sigue en curso. Esta página se actualiza sola en cuanto cambie
              el estado.
            </p>
          )}

          {application.status === "Error" && (
            <p className="text-sm text-muted-foreground">
              Seguimos comprobando el estado real en Portainer: si el despliegue tardó más
              de lo previsto pero terminó bien, el estado se corregirá solo en unos segundos.
            </p>
          )}

          {Object.keys(application.envVarValues).length > 0 && (
            <div>
              <h3 className="mb-1.5 text-sm font-medium">Variables de entorno</h3>
              <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
                {Object.entries(application.envVarValues).map(([key, value]) => (
                  <div key={key} className="contents">
                    <dt className="text-muted-foreground">{key}</dt>
                    <dd className="break-all">{value}</dd>
                  </div>
                ))}
              </dl>
            </div>
          )}

          <div className="flex flex-wrap gap-2 pt-2">
            <Button variant="outline" onClick={handleRefresh} disabled={isRefreshing}>
              {isRefreshing ? "Actualizando..." : "Refrescar estado"}
            </Button>
            {application.status === "Running" || application.status === "Provisioning" ? (
              <Button variant="outline" onClick={handleStop}>
                Parar
              </Button>
            ) : (
              <Button variant="outline" onClick={handleStart}>
                Iniciar
              </Button>
            )}
            <ConfirmDialog
              trigger={<Button variant="destructive">Borrar</Button>}
              title="Eliminar aplicación"
              description="Se eliminará esta aplicación de forma permanente. Esta acción no se puede deshacer."
              confirmLabel="Eliminar"
              destructive
              onConfirm={handleDelete}
            />
          </div>
        </CardContent>
      </Card>
    </div>
  )
}
