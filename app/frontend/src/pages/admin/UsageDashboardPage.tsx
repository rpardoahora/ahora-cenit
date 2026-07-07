import { useEffect, useState } from "react"
import { toast } from "sonner"
import { applicationsApi } from "@/lib/api"
import { formatBytes, formatPercent } from "@/lib/format"
import type { ApplicationUsage } from "@/types"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { Skeleton } from "@/components/ui/skeleton"
import { StatTile } from "@/components/StatTile"
import { UsageBarChart } from "@/components/UsageBarChart"

interface ClientAggregate {
  clientSlug: string
  ownerName: string
  cpuPercent: number
  memoryUsageBytes: number
  diskUsageBytes: number
  stackCount: number
}

function aggregateByClient(usage: ApplicationUsage[]): ClientAggregate[] {
  const byClient = new Map<string, ClientAggregate>()

  for (const app of usage) {
    const existing = byClient.get(app.ownerClientSlug)
    if (existing) {
      existing.cpuPercent += app.cpuPercent
      existing.memoryUsageBytes += app.memoryUsageBytes
      existing.diskUsageBytes += app.diskUsageBytes
      existing.stackCount += 1
    } else {
      byClient.set(app.ownerClientSlug, {
        clientSlug: app.ownerClientSlug,
        ownerName: app.ownerName,
        cpuPercent: app.cpuPercent,
        memoryUsageBytes: app.memoryUsageBytes,
        diskUsageBytes: app.diskUsageBytes,
        stackCount: 1,
      })
    }
  }

  return [...byClient.values()]
}

export function UsageDashboardPage() {
  const [usage, setUsage] = useState<ApplicationUsage[] | null>(null)
  const [loading, setLoading] = useState(true)

  async function load() {
    setLoading(true)
    try {
      const data = await applicationsApi.usage()
      setUsage(data)
    } catch {
      toast.error("No se pudo cargar el consumo de recursos.")
      setUsage([])
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    load()
  }, [])

  const totalCpu = usage?.reduce((sum, a) => sum + a.cpuPercent, 0) ?? 0
  const totalMemory = usage?.reduce((sum, a) => sum + a.memoryUsageBytes, 0) ?? 0
  const totalDisk = usage?.reduce((sum, a) => sum + a.diskUsageBytes, 0) ?? 0
  const clients = usage ? aggregateByClient(usage) : []

  const byClientItems = clients.map((c) => ({
    label: c.ownerName,
    value: c.memoryUsageBytes,
    formattedValue: formatBytes(c.memoryUsageBytes),
  }))

  const byStackItems = (usage ?? []).map((a) => ({
    label: a.subdomain,
    value: a.memoryUsageBytes,
    formattedValue: formatBytes(a.memoryUsageBytes),
  }))

  const stacksSortedByMemory = [...(usage ?? [])].sort(
    (a, b) => b.memoryUsageBytes - a.memoryUsageBytes
  )

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="font-heading text-2xl font-semibold">Consumo de recursos</h1>
          <p className="text-sm text-muted-foreground">
            CPU, RAM y disco de cada aplicación desplegada, en tiempo real desde Docker.
          </p>
        </div>
        <Button variant="outline" onClick={load} disabled={loading}>
          {loading ? "Actualizando..." : "Actualizar"}
        </Button>
      </div>

      {loading && usage === null && (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
          {Array.from({ length: 3 }).map((_, i) => (
            <Skeleton key={i} className="h-24 w-full" />
          ))}
        </div>
      )}

      {usage?.length === 0 && (
        <p className="text-sm text-muted-foreground">
          No hay aplicaciones desplegadas todavía.
        </p>
      )}

      {usage && usage.length > 0 && (
        <>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
            <StatTile
              label="CPU total"
              value={formatPercent(totalCpu)}
              hint={`suma de ${usage.length} stack${usage.length === 1 ? "" : "s"}`}
            />
            <StatTile label="RAM total" value={formatBytes(totalMemory)} />
            <StatTile label="Disco total" value={formatBytes(totalDisk)} />
          </div>

          <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
            <Card>
              <CardHeader>
                <CardTitle>RAM por cliente</CardTitle>
              </CardHeader>
              <CardContent>
                <UsageBarChart items={byClientItems} color="var(--chart-2)" />
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <CardTitle>RAM por stack</CardTitle>
              </CardHeader>
              <CardContent>
                <UsageBarChart items={byStackItems} color="var(--chart-3)" />
              </CardContent>
            </Card>
          </div>

          <Card>
            <CardHeader>
              <CardTitle>Detalle por stack</CardTitle>
            </CardHeader>
            <CardContent>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Producto</TableHead>
                    <TableHead>Subdominio</TableHead>
                    <TableHead>Cliente</TableHead>
                    <TableHead className="text-right">CPU</TableHead>
                    <TableHead className="text-right">RAM</TableHead>
                    <TableHead className="text-right">Disco</TableHead>
                    <TableHead className="text-right">Contenedores</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {stacksSortedByMemory.map((app) => (
                    <TableRow key={app.applicationId}>
                      <TableCell className="font-medium">{app.productName}</TableCell>
                      <TableCell>{app.subdomain}</TableCell>
                      <TableCell>{app.ownerName}</TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatPercent(app.cpuPercent)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatBytes(app.memoryUsageBytes)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatBytes(app.diskUsageBytes)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {app.containerCount}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        </>
      )}
    </div>
  )
}
