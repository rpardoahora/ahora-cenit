import { useEffect, useState } from "react"
import { Link, useSearchParams } from "react-router-dom"
import { toast } from "sonner"
import { useAuth } from "@/context/AuthContext"
import { applicationsApi, ApiError } from "@/lib/api"
import type { Application } from "@/types"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { Skeleton } from "@/components/ui/skeleton"
import { StatusBadge } from "@/components/StatusBadge"
import { ConfirmDialog } from "@/components/ConfirmDialog"

export function ApplicationsListPage() {
  const { user } = useAuth()
  const isAdmin = user?.role === "Admin"

  const [searchParams] = useSearchParams()
  const [applications, setApplications] = useState<Application[] | null>(null)
  const [clientFilter, setClientFilter] = useState(searchParams.get("clientSlug") ?? "")

  async function load() {
    try {
      const data = await applicationsApi.list(
        isAdmin && clientFilter ? { clientSlug: clientFilter } : undefined
      )
      setApplications(data)
      refreshStatuses(data)
    } catch {
      setApplications([])
    }
  }

  /** Contrasta el estado guardado en BBDD con el real en Portainer/Docker. */
  function refreshStatuses(apps: Application[]) {
    for (const app of apps) {
      if (app.portainerStackId === null) continue
      applicationsApi
        .status(app.id)
        .then(({ status }) => {
          setApplications((prev) =>
            prev ? prev.map((a) => (a.id === app.id ? { ...a, status } : a)) : prev
          )
        })
        .catch(() => {
          // Si Portainer no responde, se mantiene el último estado conocido.
        })
    }
  }

  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clientFilter])

  // Mientras alguna app esté desplegándose, obteniendo el certificado, o marcada como
  // error (que puede deberse a un timeout aunque el despliegue terminara bien en
  // Portainer), sigue refrescando su estado sin que el usuario tenga que recargar la
  // página ni pulsar nada.
  const pollableStatuses = ["Deploying", "Provisioning", "Error"]
  useEffect(() => {
    const interval = setInterval(() => {
      setApplications((prev) => {
        const pending = prev?.filter((a) => pollableStatuses.includes(a.status)) ?? []
        if (pending.length > 0) refreshStatuses(pending)
        return prev
      })
    }, 4000)
    return () => clearInterval(interval)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function handleStart(app: Application) {
    try {
      const updated = await applicationsApi.start(app.id)
      setApplications((prev) =>
        prev ? prev.map((a) => (a.id === app.id ? updated : a)) : prev
      )
      toast.success("Aplicación iniciada.")
    } catch (err) {
      if (!(err instanceof ApiError)) toast.error("No se pudo iniciar la aplicación.")
    }
  }

  async function handleStop(app: Application) {
    try {
      const updated = await applicationsApi.stop(app.id)
      setApplications((prev) =>
        prev ? prev.map((a) => (a.id === app.id ? updated : a)) : prev
      )
      toast.success("Aplicación parada.")
    } catch (err) {
      if (!(err instanceof ApiError)) toast.error("No se pudo parar la aplicación.")
    }
  }

  async function handleDelete(app: Application) {
    try {
      await applicationsApi.remove(app.id)
      setApplications((prev) => (prev ? prev.filter((a) => a.id !== app.id) : prev))
      toast.success("Aplicación eliminada.")
    } catch (err) {
      if (!(err instanceof ApiError)) toast.error("No se pudo eliminar la aplicación.")
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="font-heading text-2xl font-semibold">
            {isAdmin ? "Todas las aplicaciones" : "Mis aplicaciones"}
          </h1>
          <p className="text-sm text-muted-foreground">
            {isAdmin
              ? "Gestiona las aplicaciones desplegadas por cualquier cliente."
              : "Gestiona tus aplicaciones desplegadas."}
          </p>
        </div>
        {isAdmin && (
          <Input
            placeholder="Filtrar por cliente (slug)"
            value={clientFilter}
            onChange={(e) => setClientFilter(e.target.value)}
            className="max-w-64"
          />
        )}
      </div>

      {applications === null && <Skeleton className="h-48 w-full" />}

      {applications?.length === 0 && (
        <p className="text-sm text-muted-foreground">
          No hay aplicaciones desplegadas todavía.
        </p>
      )}

      {applications && applications.length > 0 && (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Producto</TableHead>
              <TableHead>Subdominio</TableHead>
              {isAdmin && <TableHead>Cliente</TableHead>}
              <TableHead>Estado</TableHead>
              <TableHead className="text-right">Acciones</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {applications.map((app) => (
              <TableRow key={app.id}>
                <TableCell className="font-medium">{app.productName}</TableCell>
                <TableCell>{app.subdomain}</TableCell>
                {isAdmin && <TableCell>{app.ownerName}</TableCell>}
                <TableCell>
                  <StatusBadge status={app.status} />
                </TableCell>
                <TableCell className="text-right">
                  <div className="flex justify-end gap-1.5">
                    <Button size="sm" variant="outline" render={<Link to={`/aplicaciones/${app.id}`} />}>
                      Ver
                    </Button>
                    {app.status === "Running" || app.status === "Provisioning" ? (
                      <Button size="sm" variant="outline" onClick={() => handleStop(app)}>
                        Parar
                      </Button>
                    ) : (
                      <Button size="sm" variant="outline" onClick={() => handleStart(app)}>
                        Iniciar
                      </Button>
                    )}
                    <ConfirmDialog
                      trigger={
                        <Button size="sm" variant="destructive">
                          Borrar
                        </Button>
                      }
                      title="Eliminar aplicación"
                      description={`Se eliminará "${app.subdomain}" de forma permanente. Esta acción no se puede deshacer.`}
                      confirmLabel="Eliminar"
                      destructive
                      onConfirm={() => handleDelete(app)}
                    />
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  )
}
