import { useEffect, useState } from "react"
import { Link } from "react-router-dom"
import { toast } from "sonner"
import { usersApi, ApiError } from "@/lib/api"
import type { AdminUser } from "@/types"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { Skeleton } from "@/components/ui/skeleton"
import { ConfirmDialog } from "@/components/ConfirmDialog"

export function UsersListPage() {
  const [users, setUsers] = useState<AdminUser[] | null>(null)

  async function load() {
    try {
      setUsers(await usersApi.list())
    } catch {
      setUsers([])
    }
  }

  useEffect(() => {
    load()
  }, [])

  async function handleDelete(user: AdminUser) {
    try {
      await usersApi.remove(user.id)
      setUsers((prev) => (prev ? prev.filter((u) => u.id !== user.id) : prev))
      toast.success("Usuario eliminado.")
    } catch (err) {
      if (!(err instanceof ApiError)) toast.error("No se pudo eliminar el usuario.")
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="font-heading text-2xl font-semibold">Usuarios</h1>
          <p className="text-sm text-muted-foreground">
            Gestiona las cuentas de clientes y administradores.
          </p>
        </div>
        <Button render={<Link to="/admin/usuarios/nuevo" />}>Nuevo usuario</Button>
      </div>

      {users === null && <Skeleton className="h-48 w-full" />}

      {users?.length === 0 && (
        <p className="text-sm text-muted-foreground">Todavía no hay usuarios.</p>
      )}

      {users && users.length > 0 && (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Nombre</TableHead>
              <TableHead>Email</TableHead>
              <TableHead>Rol</TableHead>
              <TableHead>Aplicaciones</TableHead>
              <TableHead className="text-right">Acciones</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {users.map((user) => (
              <TableRow key={user.id}>
                <TableCell className="font-medium">{user.name}</TableCell>
                <TableCell>{user.email}</TableCell>
                <TableCell>
                  <Badge variant={user.role === "Admin" ? "default" : "secondary"}>
                    {user.role === "Admin" ? "Administrador" : "Cliente"}
                  </Badge>
                </TableCell>
                <TableCell>
                  <Button
                    size="sm"
                    variant="ghost"
                    render={<Link to={`/aplicaciones?clientSlug=${user.clientSlug}`} />}
                  >
                    {user.applicationsCount} {user.applicationsCount === 1 ? "app" : "apps"}
                  </Button>
                </TableCell>
                <TableCell className="text-right">
                  <div className="flex justify-end gap-1.5">
                    <Button
                      size="sm"
                      variant="outline"
                      render={<Link to={`/admin/usuarios/${user.id}/editar`} />}
                    >
                      Editar
                    </Button>
                    <ConfirmDialog
                      trigger={
                        <Button size="sm" variant="destructive">
                          Borrar
                        </Button>
                      }
                      title="Eliminar usuario"
                      description={`Se eliminará "${user.name}" de forma permanente. Debe no tener aplicaciones desplegadas.`}
                      confirmLabel="Eliminar"
                      destructive
                      onConfirm={() => handleDelete(user)}
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
