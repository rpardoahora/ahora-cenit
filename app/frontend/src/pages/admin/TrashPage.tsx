import { useEffect, useState } from "react"
import { toast } from "sonner"
import { applicationsApi, trashApi, usersApi, ApiError } from "@/lib/api"
import type { Trash } from "@/types"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
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

const formatDate = (value: string) => new Date(value).toLocaleString()

/** Instancias y clientes borrados: se restauran o se borran del todo (solos, pasados los días de retención). */
export function TrashPage() {
  const [trash, setTrash] = useState<Trash | null>(null)

  async function load() {
    try {
      setTrash(await trashApi.list())
    } catch {
      toast.error("No se pudo cargar la papelera.")
    }
  }

  useEffect(() => {
    void load()
  }, [])

  async function run(action: () => Promise<unknown>, success: string, failure: string) {
    try {
      await action()
      toast.success(success)
      await load()
    } catch (err) {
      if (!(err instanceof ApiError)) toast.error(failure)
    }
  }

  if (!trash) {
    return <Skeleton className="h-60 w-full" />
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="font-heading text-2xl font-semibold">Papelera</h1>
        <p className="text-sm text-muted-foreground">
          Lo que se borra se para y se guarda aquí {trash.retentionDays} días; después se elimina del
          todo cada día de forma automática (contenedores, volúmenes, bases de datos, imágenes que nadie
          más use y, en los clientes, su login SQL). Mientras esté aquí, su nombre sigue ocupado.
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Instancias ({trash.applications.length})</CardTitle>
          <CardDescription>Restaurar la vuelve a arrancar tal como estaba.</CardDescription>
        </CardHeader>
        <CardContent>
          {trash.applications.length === 0 ? (
            <p className="text-sm text-muted-foreground">No hay instancias en la papelera.</p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Instancia</TableHead>
                  <TableHead>Cliente</TableHead>
                  <TableHead>Borrada</TableHead>
                  <TableHead>Se elimina</TableHead>
                  <TableHead className="text-right">Acciones</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {trash.applications.map((app) => (
                  <TableRow key={app.id}>
                    <TableCell>
                      <div className="font-medium">{app.subdomain}</div>
                      <div className="text-xs text-muted-foreground">{app.productName}</div>
                    </TableCell>
                    <TableCell>
                      <div>{app.clientName}</div>
                      <div className="text-xs text-muted-foreground">{app.clientSlug}</div>
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground">{formatDate(app.deletedAt)}</TableCell>
                    <TableCell className="text-xs">{formatDate(app.purgeAt)}</TableCell>
                    <TableCell className="text-right">
                      <div className="flex justify-end gap-1.5">
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() =>
                            void run(
                              () => applicationsApi.restore(app.id),
                              `${app.subdomain} restaurada.`,
                              `No se pudo restaurar ${app.subdomain}.`
                            )
                          }
                        >
                          Restaurar
                        </Button>
                        <ConfirmDialog
                          trigger={
                            <Button size="sm" variant="destructive">
                              Eliminar ya
                            </Button>
                          }
                          title="Eliminar del todo"
                          description={`Se borrará "${app.subdomain}" con todos sus datos (volúmenes y bases de datos). Esta acción no se puede deshacer.`}
                          confirmLabel="Eliminar del todo"
                          destructive
                          onConfirm={() =>
                            run(
                              () => applicationsApi.remove(app.id, { purge: true }),
                              `${app.subdomain} eliminada del todo.`,
                              `No se pudo eliminar ${app.subdomain}.`
                            )
                          }
                        />
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Clientes ({trash.users.length})</CardTitle>
          <CardDescription>
            Mientras estén aquí no pueden iniciar sesión. Al eliminarse del todo se borra también su login SQL.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {trash.users.length === 0 ? (
            <p className="text-sm text-muted-foreground">No hay clientes en la papelera.</p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Cliente</TableHead>
                  <TableHead>Email</TableHead>
                  <TableHead>Borrado</TableHead>
                  <TableHead>Se elimina</TableHead>
                  <TableHead className="text-right">Acciones</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {trash.users.map((user) => (
                  <TableRow key={user.id}>
                    <TableCell>
                      <div className="font-medium">{user.name}</div>
                      <div className="text-xs text-muted-foreground">{user.clientSlug}</div>
                    </TableCell>
                    <TableCell className="text-sm">{user.email}</TableCell>
                    <TableCell className="text-xs text-muted-foreground">{formatDate(user.deletedAt)}</TableCell>
                    <TableCell className="text-xs">{formatDate(user.purgeAt)}</TableCell>
                    <TableCell className="text-right">
                      <div className="flex justify-end gap-1.5">
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() =>
                            void run(
                              () => usersApi.restore(user.id),
                              `${user.name} restaurado.`,
                              `No se pudo restaurar ${user.name}.`
                            )
                          }
                        >
                          Restaurar
                        </Button>
                        <ConfirmDialog
                          trigger={
                            <Button size="sm" variant="destructive">
                              Eliminar ya
                            </Button>
                          }
                          title="Eliminar del todo"
                          description={`Se borrará "${user.name}" con su login SQL y las instancias que le queden en la papelera. Esta acción no se puede deshacer.`}
                          confirmLabel="Eliminar del todo"
                          destructive
                          onConfirm={() =>
                            run(
                              () => usersApi.remove(user.id, { purge: true }),
                              `${user.name} eliminado del todo.`,
                              `No se pudo eliminar ${user.name}.`
                            )
                          }
                        />
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
