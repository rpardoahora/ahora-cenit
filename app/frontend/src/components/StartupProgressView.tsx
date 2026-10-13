import type { StartupProgress } from "@/types"
import { Progress } from "@/components/ui/progress"

/** Por dónde va el arranque de una aplicación que aún no responde. compact: una línea, para la lista. */
export function StartupProgressView({ progress, compact = false }: { progress: StartupProgress; compact?: boolean }) {
  const hasSteps = progress.step != null && progress.stepCount != null
  const tone = progress.isError ? "text-destructive" : "text-muted-foreground"

  if (compact) {
    return (
      <p className={`mt-1 max-w-72 truncate text-xs ${tone}`} title={progress.detail ?? progress.title}>
        {hasSteps && `${progress.step}/${progress.stepCount} · `}
        {progress.title}
      </p>
    )
  }

  return (
    <div className={`flex flex-col gap-2 rounded-lg border px-4 py-3 ${progress.isError ? "border-destructive/40" : ""}`}>
      <div className="flex items-baseline justify-between gap-3">
        <p className={`text-sm font-medium ${progress.isError ? "text-destructive" : ""}`}>{progress.title}</p>
        {hasSteps && (
          <span className="shrink-0 text-xs tabular-nums text-muted-foreground">
            Paso {progress.step} de {progress.stepCount}
          </span>
        )}
      </div>
      {hasSteps ? (
        <Progress value={(100 * progress.step!) / progress.stepCount!} />
      ) : (
        !progress.isError && (
          <div className="h-2 w-full overflow-hidden rounded-full bg-muted">
            <div className="h-full w-1/3 animate-pulse rounded-full bg-primary/60" />
          </div>
        )
      )}
      {progress.detail && <p className={`break-all font-mono text-xs ${tone}`}>{progress.detail}</p>}
    </div>
  )
}
