import type { DockerHost } from "@/types"
import { formatBytes, formatPercent } from "@/lib/format"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Progress } from "@/components/ui/progress"

function formatUptime(seconds: number): string {
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  if (days > 0) return `${days} d ${hours} h`
  const minutes = Math.floor((seconds % 3600) / 60)
  return hours > 0 ? `${hours} h ${minutes} min` : `${minutes} min`
}

interface GaugeProps {
  label: string
  percent: number | null
  value: string
  hint?: string
}

function Gauge({ label, percent, value, hint }: GaugeProps) {
  return (
    <Card className="gap-2 px-6">
      <p className="text-sm text-muted-foreground">{label}</p>
      <p className="font-heading text-3xl font-semibold tabular-nums">{value}</p>
      {percent !== null && <Progress value={percent} />}
      {hint && <p className="text-xs text-muted-foreground">{hint}</p>}
    </Card>
  )
}

/** La máquina Docker vista desde fuera: uso total, versión y espacio que ocupa Docker. */
export function DockerHostPanel({ host, appsMemoryBytes }: { host: DockerHost; appsMemoryBytes: number | null }) {
  const { usage, docker } = host

  const memoryTotal = usage.memoryTotalBytes ?? docker?.memoryBytes ?? null
  const memoryPercent =
    memoryTotal && usage.memoryUsedBytes !== null ? (100 * usage.memoryUsedBytes) / memoryTotal : null
  const diskPercent =
    usage.diskTotalBytes && usage.diskUsedBytes !== null ? (100 * usage.diskUsedBytes) / usage.diskTotalBytes : null

  const cpuHint = [
    docker ? `${docker.cpus} núcleo${docker.cpus === 1 ? "" : "s"}` : null,
    usage.loadAverage ? `carga ${usage.loadAverage.map((l) => l.toFixed(2)).join(" / ")} (1, 5 y 15 min)` : null,
  ]
    .filter(Boolean)
    .join(" · ")

  const dockerSpace = docker
    ? [
        { label: "Imágenes", bytes: docker.imagesBytes },
        { label: "Volúmenes", bytes: docker.volumesBytes },
        { label: "Contenedores (capa escribible)", bytes: docker.containersBytes },
        { label: "Caché de build", bytes: docker.buildCacheBytes },
      ]
    : []
  const dockerTotal = dockerSpace.reduce((sum, item) => sum + item.bytes, 0)
  // Docker Desktop (Windows/macOS) corre en una máquina virtual: lo que se mide es esa máquina, no el equipo.
  const isVirtualMachine =
    /docker desktop/i.test(docker?.operatingSystem ?? "") || /microsoft|wsl/i.test(docker?.kernelVersion ?? "")

  return (
    <div className="flex flex-col gap-4">
      <div>
        <h2 className="font-heading text-lg font-semibold">Máquina Docker</h2>
        <p className="text-sm text-muted-foreground">
          {[
            docker?.operatingSystem,
            docker?.kernelVersion && `kernel ${docker.kernelVersion}`,
            docker?.dockerVersion && `Docker ${docker.dockerVersion}`,
            usage.uptimeSeconds !== null && `encendida hace ${formatUptime(usage.uptimeSeconds)}`,
          ]
            .filter(Boolean)
            .join(" · ") || "Uso total de la máquina en la que corre Docker."}
        </p>
      </div>

      {isVirtualMachine && (
        <p className="rounded-lg border bg-muted/40 px-4 py-3 text-sm text-muted-foreground">
          Docker Desktop corre dentro de una máquina virtual (WSL2 en Windows): estas cifras son las de esa máquina,
          no las del equipo. Por defecto tiene la mitad de la RAM del equipo, y su disco es un disco virtual que crece
          ocupando espacio real del disco del sistema (en Windows, C:) aunque aquí aparezca como libre. El
          Administrador de tareas de Windows mide el equipo entero.
        </p>
      )}

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <Gauge
          label={isVirtualMachine ? "CPU de la máquina virtual" : "CPU"}
          percent={usage.cpuPercent}
          value={usage.cpuPercent !== null ? formatPercent(usage.cpuPercent) : "—"}
          hint={cpuHint || undefined}
        />
        <Gauge
          label={isVirtualMachine ? "RAM de la máquina virtual" : "RAM"}
          percent={memoryPercent}
          value={usage.memoryUsedBytes !== null ? formatBytes(usage.memoryUsedBytes) : "—"}
          hint={[
            memoryTotal ? `de ${formatBytes(memoryTotal)}` : null,
            appsMemoryBytes !== null ? `las aplicaciones usan ${formatBytes(appsMemoryBytes)}` : null,
          ]
            .filter(Boolean)
            .join(" · ")}
        />
        <Gauge
          label={isVirtualMachine ? "Disco virtual de Docker" : "Disco de Docker"}
          percent={diskPercent}
          value={usage.diskUsedBytes !== null ? formatBytes(usage.diskUsedBytes) : "—"}
          hint={
            usage.diskTotalBytes && usage.diskUsedBytes !== null
              ? `de ${formatBytes(usage.diskTotalBytes)} · libres ${formatBytes(usage.diskTotalBytes - usage.diskUsedBytes)}`
              : undefined
          }
        />
      </div>

      {docker && (
        <Card>
          <CardHeader>
            <CardTitle>Espacio que ocupa Docker</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-3">
            <p className="text-sm text-muted-foreground">
              {docker.containersRunning} de {docker.containers} contenedores en marcha · {docker.images} imágenes
            </p>
            {dockerSpace.map((item) => (
              <div key={item.label} className="flex flex-col gap-1">
                <div className="flex items-baseline justify-between gap-3 text-sm">
                  <span>{item.label}</span>
                  <span className="tabular-nums text-muted-foreground">{formatBytes(item.bytes)}</span>
                </div>
                <Progress value={dockerTotal > 0 ? (100 * item.bytes) / dockerTotal : 0} />
              </div>
            ))}
            <div className="flex items-baseline justify-between gap-3 border-t pt-3 text-sm font-medium">
              <span>Total</span>
              <span className="tabular-nums">{formatBytes(dockerTotal)}</span>
            </div>
          </CardContent>
        </Card>
      )}
    </div>
  )
}
