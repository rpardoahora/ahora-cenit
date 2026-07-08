import { useEffect, useRef, useState, type FormEvent } from "react"
import { useNavigate, useParams } from "react-router-dom"
import { toast } from "sonner"
import { productsApi, applicationsApi, configApi, ApiError } from "@/lib/api"
import type { Product } from "@/types"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Field, FieldDescription, FieldGroup, FieldLabel } from "@/components/ui/field"
import { Skeleton } from "@/components/ui/skeleton"
import { Progress } from "@/components/ui/progress"

/** Estimación a usar mientras no haya despliegues previos de este producto. */
const DEFAULT_ESTIMATE_SECONDS = 20

export function DeployPage() {
  const { productId } = useParams<{ productId: string }>()
  const navigate = useNavigate()

  const [product, setProduct] = useState<Product | null>(null)
  const [loading, setLoading] = useState(true)
  const [subdomain, setSubdomain] = useState("")
  const [envVars, setEnvVars] = useState<Record<string, string>>({})
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [estimateSeconds, setEstimateSeconds] = useState(DEFAULT_ESTIMATE_SECONDS)
  const [baseDomain, setBaseDomain] = useState(window.location.hostname)
  const [progress, setProgress] = useState(0)
  const [isOvertime, setIsOvertime] = useState(false)
  const progressInterval = useRef<ReturnType<typeof setInterval> | null>(null)

  useEffect(() => {
    if (!productId) return
    let cancelled = false

    async function load() {
      try {
        const loadedProduct = await productsApi.get(productId!)
        if (cancelled) return
        setProduct(loadedProduct)

        const defaults: Record<string, string> = {}
        for (const envVar of loadedProduct.envVarsSchema) {
          defaults[envVar.key] = envVar.defaultValue
        }
        setEnvVars(defaults)

        try {
          const suggestion = await productsApi.suggestSubdomain(productId!)
          if (!cancelled) setSubdomain(suggestion.suggestedSubdomain)
        } catch {
          // backend suggestion unavailable, leave subdomain empty for manual entry
        }

        try {
          const stats = await productsApi.deployStats(productId!)
          if (!cancelled && stats.averageDeploySeconds) {
            setEstimateSeconds(stats.averageDeploySeconds)
          }
        } catch {
          // sin estadísticas todavía, se usa la estimación por defecto
        }

        try {
          const config = await configApi.get()
          if (!cancelled) setBaseDomain(config.baseDomain)
        } catch {
          // si falla, se mantiene window.location.hostname como aproximación
        }
      } catch {
        if (!cancelled) toast.error("No se pudo cargar el producto.")
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    load()
    return () => {
      cancelled = true
    }
  }, [productId])

  useEffect(() => {
    return () => {
      if (progressInterval.current) clearInterval(progressInterval.current)
    }
  }, [])

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    if (!productId) return
    setIsSubmitting(true)
    setIsOvertime(false)
    setProgress(0)

    const startedAt = Date.now()
    progressInterval.current = setInterval(() => {
      const elapsedSeconds = (Date.now() - startedAt) / 1000
      if (elapsedSeconds <= estimateSeconds) {
        // Avanza hasta el 95% en el tiempo estimado; el último tramo espera a la respuesta real.
        setProgress(Math.min(95, (elapsedSeconds / estimateSeconds) * 95))
        return
      }

      // Se ha superado la estimación: el despliegue puede seguir en curso perfectamente
      // (p.ej. una imagen grande tardando en descargarse), así que no lo tratamos como un
      // fallo. Seguimos avanzando muy despacio sin llegar nunca al 100% por nuestra cuenta;
      // solo la respuesta real del servidor completa la barra o la corta con un error.
      setIsOvertime(true)
      setProgress((prev) => Math.min(99, prev + 0.3))
    }, 200)

    try {
      const application = await applicationsApi.create({
        productId,
        subdomain,
        envVars,
      })
      setProgress(100)
      toast.success("Despliegue iniciado.")
      navigate(`/aplicaciones/${application.id}`)
    } catch (err) {
      if (!(err instanceof ApiError)) {
        toast.error("No se pudo iniciar el despliegue.")
      }
    } finally {
      if (progressInterval.current) clearInterval(progressInterval.current)
      setIsSubmitting(false)
    }
  }

  if (loading) {
    return (
      <div className="mx-auto max-w-lg space-y-4">
        <Skeleton className="h-8 w-2/3" />
        <Skeleton className="h-64 w-full" />
      </div>
    )
  }

  if (!product) {
    return <p className="text-sm text-muted-foreground">Producto no encontrado.</p>
  }

  const visibleEnvVars = product.envVarsSchema.filter((envVar) => envVar.mode !== "Hidden")

  return (
    <div className="mx-auto max-w-lg">
      <Card>
        <CardHeader>
          <CardTitle>Desplegar {product.name}</CardTitle>
          <CardDescription>{product.description}</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit}>
            <FieldGroup>
              <Field>
                <FieldLabel htmlFor="subdomain">Subdominio</FieldLabel>
                <Input
                  id="subdomain"
                  required
                  value={subdomain}
                  onChange={(e) => setSubdomain(e.target.value)}
                  placeholder="mi-app-mi-cliente"
                />
                <FieldDescription>
                  Tu aplicación estará disponible en{" "}
                  <code>
                    {subdomain || "<subdominio>"}.{baseDomain}
                  </code>
                </FieldDescription>
              </Field>

              {visibleEnvVars.length > 0 && (
                <Field>
                  <FieldLabel>Variables de entorno</FieldLabel>
                  <div className="flex flex-col gap-4">
                    {visibleEnvVars.map((envVar) => (
                      <div key={envVar.key} className="flex flex-col gap-1.5">
                        <FieldLabel htmlFor={`env-${envVar.key}`}>
                          {envVar.label}
                        </FieldLabel>
                        <Input
                          id={`env-${envVar.key}`}
                          type={envVar.mode === "Secret" ? "password" : "text"}
                          value={envVars[envVar.key] ?? ""}
                          readOnly={envVar.mode === "ReadOnly"}
                          disabled={envVar.mode === "ReadOnly"}
                          onChange={(e) =>
                            setEnvVars((prev) => ({
                              ...prev,
                              [envVar.key]: e.target.value,
                            }))
                          }
                        />
                      </div>
                    ))}
                  </div>
                </Field>
              )}

              {isSubmitting && (
                <Field>
                  <Progress value={progress} />
                  <FieldDescription>
                    {isOvertime
                      ? "El despliegue está tardando más de lo habitual, pero seguimos trabajando en ello. No cierres esta página."
                      : `Desplegando... normalmente tarda unos ${Math.round(estimateSeconds)}s.`}
                  </FieldDescription>
                </Field>
              )}

              <Button type="submit" disabled={isSubmitting}>
                {isSubmitting ? "Desplegando..." : "Desplegar"}
              </Button>
            </FieldGroup>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
