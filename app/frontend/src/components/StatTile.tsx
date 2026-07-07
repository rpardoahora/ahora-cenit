import { Card } from "@/components/ui/card"

interface StatTileProps {
  label: string
  value: string
  hint?: string
}

export function StatTile({ label, value, hint }: StatTileProps) {
  return (
    <Card className="gap-1 px-6">
      <p className="text-sm text-muted-foreground">{label}</p>
      <p className="font-heading text-3xl font-semibold tabular-nums">{value}</p>
      {hint && <p className="text-xs text-muted-foreground">{hint}</p>}
    </Card>
  )
}
