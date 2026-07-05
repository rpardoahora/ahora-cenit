import { useEffect, useState, type FormEvent } from "react"
import { useNavigate, useParams } from "react-router-dom"
import { toast } from "sonner"
import { productsApi, applicationsApi, ApiError } from "@/lib/api"
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

export function DeployPage() {
  const { productId } = useParams<{ productId: string }>()
  const navigate = useNavigate()

  const [product, setProduct] = useState<Product | null>(null)
  const [loading, setLoading] = useState(true)
  const [subdomain, setSubdomain] = useState("")
  const [envVars, setEnvVars] = useState<Record<string, string>>({})
  const [isSubmitting, setIsSubmitting] = useState(false)

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

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    if (!productId) return
    setIsSubmitting(true)
    try {
      const application = await applicationsApi.create({
        productId,
        subdomain,
        envVars,
      })
      toast.success("Despliegue iniciado.")
      navigate(`/aplicaciones/${application.id}`)
    } catch (err) {
      if (!(err instanceof ApiError)) {
        toast.error("No se pudo iniciar el despliegue.")
      }
    } finally {
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
                  <code>{subdomain || "<subdominio>"}.ahoracenit.localhost</code>
                </FieldDescription>
              </Field>

              {product.envVarsSchema.length > 0 && (
                <Field>
                  <FieldLabel>Variables de entorno</FieldLabel>
                  <div className="flex flex-col gap-4">
                    {product.envVarsSchema.map((envVar) => (
                      <div key={envVar.key} className="flex flex-col gap-1.5">
                        <FieldLabel htmlFor={`env-${envVar.key}`}>
                          {envVar.label}
                        </FieldLabel>
                        <Input
                          id={`env-${envVar.key}`}
                          type={envVar.isSecret ? "password" : "text"}
                          value={envVars[envVar.key] ?? ""}
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
