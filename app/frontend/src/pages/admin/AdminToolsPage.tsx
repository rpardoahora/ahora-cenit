import { useEffect, useState } from "react"
import { toast } from "sonner"
import { adminToolsApi } from "@/lib/api"
import type { AdminTool } from "@/types"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"

export function AdminToolsPage() {
  const [tools, setTools] = useState<AdminTool[] | null>(null)

  useEffect(() => {
    adminToolsApi
      .list()
      .then(setTools)
      .catch(() => {
        toast.error("No se pudieron cargar las herramientas de administración.")
        setTools([])
      })
  }, [])

  async function copyUrl(url: string) {
    try {
      await navigator.clipboard.writeText(url)
      toast.success("URL copiada al portapapeles.")
    } catch {
      toast.error("No se pudo copiar la URL.")
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="font-heading text-2xl font-semibold">Herramientas</h1>
        <p className="text-sm text-muted-foreground">
          Accesos a las herramientas de infraestructura de la plataforma.
        </p>
      </div>

      {tools === null && (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
          {Array.from({ length: 4 }).map((_, i) => (
            <Skeleton key={i} className="h-44 w-full" />
          ))}
        </div>
      )}

      {tools?.length === 0 && (
        <p className="text-sm text-muted-foreground">
          No hay herramientas configuradas.
        </p>
      )}

      {tools && tools.length > 0 && (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
          {tools.map((tool) => (
            <Card key={tool.key}>
              <CardHeader>
                <CardTitle>{tool.name}</CardTitle>
                <CardDescription>{tool.description}</CardDescription>
              </CardHeader>
              <CardContent className="flex-1">
                <a
                  href={tool.url}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="font-mono text-xs break-all text-muted-foreground hover:text-foreground hover:underline"
                >
                  {tool.url}
                </a>
              </CardContent>
              <CardFooter className="gap-2">
                <Button
                  size="sm"
                  render={<a href={tool.url} target="_blank" rel="noopener noreferrer" />}
                >
                  Abrir
                </Button>
                <Button size="sm" variant="outline" onClick={() => copyUrl(tool.url)}>
                  Copiar URL
                </Button>
              </CardFooter>
            </Card>
          ))}
        </div>
      )}
    </div>
  )
}
