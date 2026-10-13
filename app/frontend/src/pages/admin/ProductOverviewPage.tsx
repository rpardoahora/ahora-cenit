import { useEffect, useState } from "react"
import { Link, useParams } from "react-router-dom"
import { toast } from "sonner"
import { productsApi } from "@/lib/api"
import type { ProductOverview } from "@/types"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
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
import { SqlAdminLaunchButton } from "@/components/SqlAdminLaunchButton"

/** Panel de un producto: versiones publicadas en sus feeds y clientes que lo tienen instalado. */
export function ProductOverviewPage() {
  const { id } = useParams<{ id: string }>()
  const [overview, setOverview] = useState<ProductOverview | null>(null)
  const [loading, setLoading] = useState(true)

  async function load() {
    setLoading(true)
    try {
      setOverview(await productsApi.overview(id!))
    } catch {
      toast.error("No se pudo cargar el panel del producto.")
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  if (loading && !overview) {
    return (
      <div className="flex flex-col gap-4">
        <Skeleton className="h-8 w-1/3" />
        <Skeleton className="h-40 w-full" />
        <Skeleton className="h-40 w-full" />
      </div>
    )
  }

  if (!overview) {
    return <p className="text-sm text-muted-foreground">Producto no encontrado.</p>
  }

  const { images, nuget, installations } = overview
  const outdated = installations.filter((i) => i.outdated).length

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="font-heading text-2xl font-semibold">{overview.productName}</h1>
          <p className="text-sm text-muted-foreground">
            Versiones publicadas en sus repositorios y clientes que lo tienen instalado.
          </p>
        </div>
        <div className="flex gap-2">
          <Button variant="outline" onClick={() => void load()} disabled={loading}>
            {loading ? "Actualizando..." : "Actualizar"}
          </Button>
          <Button variant="outline" render={<Link to={`/admin/productos/${overview.productId}/editar`} />}>
            Editar producto
          </Button>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Versiones disponibles</CardTitle>
          <CardDescription>
            Última versión de cada imagen en su registry y del paquete NuGet en el feed configurado.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Repositorio</TableHead>
                <TableHead>Artefacto</TableHead>
                <TableHead>En el producto</TableHead>
                <TableHead>Última publicada</TableHead>
                <TableHead className="text-right"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {images.map((image) => (
                <TableRow key={`${image.service}-${image.repository}`}>
                  <TableCell>
                    <Badge variant="outline">Docker</Badge>
                  </TableCell>
                  <TableCell>
                    <div className="font-mono text-xs break-all">{image.repository}</div>
                    <div className="text-xs text-muted-foreground">servicio {image.service}</div>
                    {image.error && <div className="text-xs text-destructive">{image.error}</div>}
                  </TableCell>
                  <TableCell className="font-mono text-xs">{image.configuredTag ?? "—"}</TableCell>
                  <TableCell>
                    <VersionCell latest={image.latestTag} recent={image.recentTags} updateAvailable={image.updateAvailable} />
                  </TableCell>
                  <TableCell className="text-right">
                    <RepoButton url={image.repositoryUrl} />
                  </TableCell>
                </TableRow>
              ))}
              {nuget && (
                <TableRow>
                  <TableCell>
                    <Badge variant="outline">NuGet</Badge>
                  </TableCell>
                  <TableCell>
                    <div className="font-mono text-xs break-all">{nuget.packageId}</div>
                    <div className="text-xs text-muted-foreground">
                      {nuget.feedUrl ?? "Repositorio NuGet de la plataforma"}
                      {nuget.packageIdInferred && " · paquete supuesto (el compose no indica DatabaseNuget__PackageId)"}
                    </div>
                    {nuget.error && <div className="text-xs text-destructive">{nuget.error}</div>}
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground">—</TableCell>
                  <TableCell>
                    <VersionCell latest={nuget.latestVersion} recent={nuget.recentVersions} updateAvailable={false} />
                  </TableCell>
                  <TableCell className="text-right">
                    <RepoButton url={nuget.repositoryUrl} />
                  </TableCell>
                </TableRow>
              )}
              {images.length === 0 && !nuget && (
                <TableRow>
                  <TableCell colSpan={5} className="text-sm text-muted-foreground">
                    El compose no tiene imágenes que consultar.
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Clientes con el producto instalado ({installations.length})</CardTitle>
          <CardDescription>
            Versión con la que corre cada instancia, según Portainer.
            {outdated > 0 && ` ${outdated} con una versión anterior a la última publicada.`}
          </CardDescription>
        </CardHeader>
        <CardContent>
          {installations.length === 0 ? (
            <p className="text-sm text-muted-foreground">Ningún cliente tiene instalado este producto.</p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Cliente</TableHead>
                  <TableHead>Instancia</TableHead>
                  <TableHead>Estado</TableHead>
                  <TableHead>Versión instalada</TableHead>
                  <TableHead>Instalada</TableHead>
                  <TableHead className="text-right"></TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {installations.map((installation) => (
                  <TableRow key={installation.applicationId}>
                    <TableCell>
                      <div className="font-medium">{installation.clientName}</div>
                      <div className="text-xs text-muted-foreground">{installation.clientSlug}</div>
                    </TableCell>
                    <TableCell>
                      <Link to={`/aplicaciones/${installation.applicationId}`} className="hover:underline">
                        {installation.subdomain}
                      </Link>
                      <div>
                        <a
                          href={installation.url}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="font-mono text-xs text-muted-foreground hover:text-foreground hover:underline"
                        >
                          {installation.url}
                        </a>
                      </div>
                    </TableCell>
                    <TableCell>
                      <Badge variant={installation.status === "Running" ? "default" : "secondary"}>
                        {installation.status}
                      </Badge>
                    </TableCell>
                    <TableCell>
                      {installation.images.length === 0 ? (
                        <span className="text-xs text-muted-foreground">Sin datos de Portainer</span>
                      ) : (
                        <div className="flex flex-col gap-0.5">
                          {installation.images.map((image) => (
                            <span key={image.service} className="font-mono text-xs">
                              {image.service}: {image.tag ?? image.image}
                            </span>
                          ))}
                          {installation.outdated && (
                            <Badge variant="destructive" className="mt-1 w-fit">
                              Hay una versión más nueva
                            </Badge>
                          )}
                        </div>
                      )}
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      {new Date(installation.createdAt).toLocaleDateString()}
                    </TableCell>
                    <TableCell className="text-right">
                      <SqlAdminLaunchButton applicationId={installation.applicationId} />
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

function VersionCell({ latest, recent, updateAvailable }: { latest: string | null; recent: string[]; updateAvailable: boolean }) {
  if (!latest) {
    return <span className="text-xs text-muted-foreground">—</span>
  }

  return (
    <div className="flex flex-col gap-1">
      <span className="font-mono text-xs font-medium">{latest}</span>
      {updateAvailable && (
        <Badge variant="destructive" className="w-fit">
          Más nueva que la del producto
        </Badge>
      )}
      {recent.length > 1 && (
        <span className="text-xs text-muted-foreground" title={recent.join(", ")}>
          Anteriores: {recent.slice(1).join(", ")}
        </span>
      )}
    </div>
  )
}

function RepoButton({ url }: { url: string | null }) {
  if (!url) {
    return null
  }

  return (
    <Button size="sm" variant="outline" render={<a href={url} target="_blank" rel="noopener noreferrer" />}>
      Ir al repositorio
    </Button>
  )
}
