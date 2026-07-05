import { useEffect, useState } from "react"
import { Link } from "react-router-dom"
import { toast } from "sonner"
import { productsApi, ApiError } from "@/lib/api"
import type { Product } from "@/types"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import { Switch } from "@/components/ui/switch"
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

export function ProductsListPage() {
  const [products, setProducts] = useState<Product[] | null>(null)

  async function load() {
    try {
      setProducts(await productsApi.list())
    } catch {
      setProducts([])
    }
  }

  useEffect(() => {
    load()
  }, [])

  async function handleToggleActive(product: Product, active: boolean) {
    try {
      const updated = await productsApi.setActive(product.id, active)
      setProducts((prev) =>
        prev ? prev.map((p) => (p.id === product.id ? updated : p)) : prev
      )
    } catch (err) {
      if (!(err instanceof ApiError)) toast.error("No se pudo actualizar el producto.")
    }
  }

  async function handleDelete(product: Product) {
    try {
      await productsApi.remove(product.id)
      setProducts((prev) => (prev ? prev.filter((p) => p.id !== product.id) : prev))
      toast.success("Producto eliminado.")
    } catch (err) {
      if (!(err instanceof ApiError)) toast.error("No se pudo eliminar el producto.")
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="font-heading text-2xl font-semibold">Productos</h1>
          <p className="text-sm text-muted-foreground">
            Gestiona el catálogo de aplicaciones dockerizadas.
          </p>
        </div>
        <Button render={<Link to="/admin/productos/nuevo" />}>Nuevo producto</Button>
      </div>

      {products === null && <Skeleton className="h-48 w-full" />}

      {products?.length === 0 && (
        <p className="text-sm text-muted-foreground">Todavía no hay productos.</p>
      )}

      {products && products.length > 0 && (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Nombre</TableHead>
              <TableHead>Estado</TableHead>
              <TableHead>Activo</TableHead>
              <TableHead className="text-right">Acciones</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {products.map((product) => (
              <TableRow key={product.id}>
                <TableCell className="font-medium">{product.name}</TableCell>
                <TableCell>
                  <Badge variant={product.isActive ? "default" : "secondary"}>
                    {product.isActive ? "Activo" : "Inactivo"}
                  </Badge>
                </TableCell>
                <TableCell>
                  <Switch
                    checked={product.isActive}
                    onCheckedChange={(checked) => handleToggleActive(product, checked)}
                  />
                </TableCell>
                <TableCell className="text-right">
                  <div className="flex justify-end gap-1.5">
                    <Button
                      size="sm"
                      variant="outline"
                      render={<Link to={`/admin/productos/${product.id}/editar`} />}
                    >
                      Editar
                    </Button>
                    <ConfirmDialog
                      trigger={
                        <Button size="sm" variant="destructive">
                          Borrar
                        </Button>
                      }
                      title="Eliminar producto"
                      description={`Se eliminará "${product.name}" del catálogo de forma permanente.`}
                      confirmLabel="Eliminar"
                      destructive
                      onConfirm={() => handleDelete(product)}
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
