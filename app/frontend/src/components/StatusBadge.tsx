import { Badge } from "@/components/ui/badge"
import type { ApplicationStatus } from "@/types"

const STATUS_LABEL: Record<ApplicationStatus, string> = {
  Running: "En ejecución",
  Stopped: "Parada",
  Deploying: "Desplegando",
  Error: "Error",
  Deleted: "Eliminada",
}

const STATUS_VARIANT: Record<
  ApplicationStatus,
  "default" | "secondary" | "destructive" | "outline"
> = {
  Running: "default",
  Stopped: "secondary",
  Deploying: "outline",
  Error: "destructive",
  Deleted: "outline",
}

export function StatusBadge({ status }: { status: ApplicationStatus }) {
  return <Badge variant={STATUS_VARIANT[status]}>{STATUS_LABEL[status]}</Badge>
}
