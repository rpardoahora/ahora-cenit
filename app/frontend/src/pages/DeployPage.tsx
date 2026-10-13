import { useEffect, useRef, useState, type FormEvent } from "react"
import { useNavigate, useParams } from "react-router-dom"
import { toast } from "sonner"
import { productsApi, applicationsApi, configApi, usersApi, ApiError } from "@/lib/api"
import { useAuth } from "@/context/AuthContext"
import { Switch } from "@/components/ui/switch"
import type { AdminUser, Product } from "@/types"
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
  const { user } = useAuth()
  const isAdmin = user?.role === "Admin"
  // Modo SQL de esta instancia: el de los ajustes; solo un administrador puede cambiarlo aquí.
  const [sharedSql, setSharedSql] = useState(false)
  const [adminPassword, setAdminPassword] = useState("")
  // Solo administradores: cliente al que se instala ("" = el propio administrador).
  const [clients, setClients] = useState<AdminUser[]>([])
  const [targetUserId, setTargetUserId] = useState("")
  const [progress, setProgress] = useState(0)
  const [isOvertime, setIsOvertime] = useState(false)
  const progressInterval = useRef<ReturnType<typeof setInterval> | null>(null)

  useEffect(() => {
    if (!isAdmin) return
    usersApi
      .list()
      .then((users) => setClients(users.filter((u) => u.role === "Cliente")))
      .catch(() => setClients([]))
  }, [isAdmin])

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
          if (!cancelled) {
            setBaseDomain(config.baseDomain)
            setSharedSql(config.sharedSql)
          }
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
        adminPassword: sharedSql ? adminPassword : undefined,
        sqlMode: isAdmin ? (sharedSql ? "Shared" : "Dedicated") : undefined,
        userId: isAdmin && targetUserId ? targetUserId : undefined,
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

  // Con BBDD compartida no se piden las variables del SQL del compose (p. ej. la contraseña de sa).
  const hiddenBySharedSql = new Set(sharedSql ? (product.sharedSqlHiddenEnvVars ?? []) : [])
  const visibleEnvVars = product.envVarsSchema.filter(
    (envVar) => envVar.mode !== "Hidden" && !hiddenBySharedSql.has(envVar.key)
  )

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

              {isAdmin && (
                <Field>
                  <FieldLabel htmlFor="target-user">Cliente</FieldLabel>
                  <select
                    id="target-user"
                    value={targetUserId}
                    onChange={(e) => setTargetUserId(e.target.value)}
                    className="h-9 w-full rounded-md border border-input bg-transparent px-3 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 dark:bg-input/30"
                  >
                    <option value="">Yo ({user?.name ?? "administrador"})</option>
                    {clients.map((client) => (
                      <option key={client.id} value={client.id}>
                        {client.name} ({client.clientSlug})
                      </option>
                    ))}
                  </select>
                  <FieldDescription>
                    La aplicación será de este cliente: la verá en su panel y, con BBDD compartida, usará su
                    login SQL.
                  </FieldDescription>
                </Field>
              )}

              {isAdmin && (
                <Field>
                  <FieldLabel>Base de datos</FieldLabel>
                  <label className="flex items-center gap-3 text-sm font-medium">
                    <Switch checked={sharedSql} onCheckedChange={setSharedSql} />
                    {sharedSql ? "BBDD compartida" : "BBDD independiente"}
                  </label>
                  <FieldDescription>
                    {sharedSql
                      ? "La aplicación usará el SQL Server de la plataforma, con el login de su cliente."
                      : "La aplicación usará el SQL Server que trae su compose."}{" "}
                    Por defecto, el modo de los ajustes del portal.
                  </FieldDescription>
                </Field>
              )}

              {sharedSql && (
                <Field>
                  <FieldLabel htmlFor="admin-password">
                    Contraseña del administrador de la aplicación
                  </FieldLabel>
                  <Input
                    id="admin-password"
                    type="password"
                    autoComplete="new-password"
                    value={adminPassword}
                    onChange={(e) => setAdminPassword(e.target.value)}
                  />
                  <FieldDescription>
                    La aplicación se instala sola al arrancar y crea su usuario
                    administrador con esta contraseña.
                  </FieldDescription>
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
