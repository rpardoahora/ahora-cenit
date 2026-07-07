interface UsageBarChartItem {
  label: string
  value: number
  formattedValue: string
}

interface UsageBarChartProps {
  items: UsageBarChartItem[]
  color: string
  maxItems?: number
  emptyMessage?: string
}

/**
 * Gráfico de barras horizontales de una sola serie: color fijo (la identidad
 * la da la etiqueta, no el color), valor mostrado siempre al final de la
 * barra y tooltip al pasar el ratón/foco con el mismo dato.
 */
export function UsageBarChart({
  items,
  color,
  maxItems = 8,
  emptyMessage = "Sin datos.",
}: UsageBarChartProps) {
  const sorted = [...items].sort((a, b) => b.value - a.value)

  const visible = sorted.slice(0, maxItems)
  const rest = sorted.slice(maxItems)
  if (rest.length > 0) {
    const restValue = rest.reduce((sum, item) => sum + item.value, 0)
    visible.push({
      label: `Otros (${rest.length})`,
      value: restValue,
      formattedValue: "",
    })
  }

  if (visible.length === 0) {
    return <p className="text-sm text-muted-foreground">{emptyMessage}</p>
  }

  const max = Math.max(...visible.map((item) => item.value), 1)

  return (
    <div className="flex flex-col gap-3">
      {visible.map((item) => {
        const widthPercent = Math.max((item.value / max) * 100, item.value > 0 ? 2 : 0)
        return (
          <div
            key={item.label}
            tabIndex={0}
            className="group relative flex items-center gap-3 rounded-lg outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
          >
            <span className="w-28 shrink-0 truncate text-sm text-muted-foreground sm:w-40">
              {item.label}
            </span>
            <div className="h-5 min-w-0 flex-1 rounded-r-[4px] bg-muted">
              <div
                className="h-5 rounded-r-[4px] transition-[width] duration-300 ease-out group-hover:brightness-110"
                style={{ width: `${widthPercent}%`, backgroundColor: color }}
              />
            </div>
            <span className="w-20 shrink-0 text-right text-sm font-medium tabular-nums">
              {item.formattedValue}
            </span>

            <div
              role="tooltip"
              className="pointer-events-none absolute -top-8 left-28 z-10 rounded-md bg-foreground px-2 py-1 text-xs text-background opacity-0 shadow-md transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100 sm:left-40"
            >
              {item.label}: {item.formattedValue}
            </div>
          </div>
        )
      })}
    </div>
  )
}
