import { useEffect, useRef, useState, type FormEvent } from "react"
import { useNavigate, useParams } from "react-router-dom"
import { toast } from "sonner"
import { productsApi, ApiError } from "@/lib/api"
import type { EnvVarSchema } from "@/types"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Textarea } from "@/components/ui/textarea"
import { Switch } from "@/components/ui/switch"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field"
import { Skeleton } from "@/components/ui/skeleton"

interface EnvVarRow extends EnvVarSchema {
  rowId: number
}

export function ProductFormPage() {
  const { id } = useParams<{ id: string }>()
  const isEditing = !!id
  const navigate = useNavigate()
  const nextRowId = useRef(0)

  const [loading, setLoading] = useState(isEditing)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [name, setName] = useState("")
  const [description, setDescription] = useState("")
  const [imageUrl, setImageUrl] = useState("")
  const [websiteUrl, setWebsiteUrl] = useState("")
  const [composeTemplate, setComposeTemplate] = useState("")
  const [isActive, setIsActive] = useState(true)
  const [envVars, setEnvVars] = useState<EnvVarRow[]>([])

  useEffect(() => {
    if (!id) return
    productsApi
      .get(id)
      .then((product) => {
        setName(product.name)
        setDescription(product.description)
        setImageUrl(product.imageUrl)
        setWebsiteUrl(product.websiteUrl)
        setComposeTemplate(product.composeTemplate)
        setIsActive(product.isActive)
        setEnvVars(
          product.envVarsSchema.map((envVar) => ({
            ...envVar,
            rowId: nextRowId.current++,
          }))
        )
      })
      .catch(() => toast.error("No se pudo cargar el producto."))
      .finally(() => setLoading(false))
  }, [id])

  function addEnvVar() {
    setEnvVars((prev) => [
      ...prev,
      { rowId: nextRowId.current++, key: "", label: "", defaultValue: "", isSecret: false },
    ])
  }

  function updateEnvVar(rowId: number, patch: Partial<EnvVarSchema>) {
    setEnvVars((prev) =>
      prev.map((row) => (row.rowId === rowId ? { ...row, ...patch } : row))
    )
  }

  function removeEnvVar(rowId: number) {
    setEnvVars((prev) => prev.filter((row) => row.rowId !== rowId))
  }

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setIsSubmitting(true)
    try {
      const input = {
        name,
        description,
        imageUrl,
        websiteUrl,
        composeTemplate,
        isActive,
        envVarsSchema: envVars.map(({ rowId: _rowId, ...envVar }) => envVar),
      }
      if (isEditing && id) {
        await productsApi.update(id, input)
        toast.success("Producto actualizado.")
      } else {
        await productsApi.create(input)
        toast.success("Producto creado.")
      }
      navigate("/admin/productos")
    } catch (err) {
      if (!(err instanceof ApiError)) toast.error("No se pudo guardar el producto.")
    } finally {
      setIsSubmitting(false)
    }
  }

  if (loading) {
    return (
      <div className="mx-auto max-w-2xl space-y-4">
        <Skeleton className="h-8 w-1/2" />
        <Skeleton className="h-96 w-full" />
      </div>
    )
  }

  return (
    <div className="mx-auto max-w-2xl">
      <Card>
        <CardHeader>
          <CardTitle>{isEditing ? "Editar producto" : "Nuevo producto"}</CardTitle>
          <CardDescription>
            Define la aplicación, su compose de despliegue y las variables de entorno
            configurables por el cliente.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit}>
            <FieldGroup>
              <Field>
                <FieldLabel htmlFor="name">Nombre</FieldLabel>
                <Input
                  id="name"
                  required
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                />
              </Field>

              <Field>
                <FieldLabel htmlFor="description">Descripción</FieldLabel>
                <Textarea
                  id="description"
                  required
                  rows={3}
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                />
              </Field>

              <Field>
                <FieldLabel htmlFor="imageUrl">URL de imagen</FieldLabel>
                <Input
                  id="imageUrl"
                  type="url"
                  required
                  value={imageUrl}
                  onChange={(e) => setImageUrl(e.target.value)}
                  placeholder="https://..."
                />
              </Field>

              <Field>
                <FieldLabel htmlFor="websiteUrl">URL del sitio web</FieldLabel>
                <Input
                  id="websiteUrl"
                  type="url"
                  value={websiteUrl}
                  onChange={(e) => setWebsiteUrl(e.target.value)}
                  placeholder="https://..."
                />
              </Field>

              <Field>
                <FieldLabel htmlFor="composeTemplate">Compose de despliegue (YAML)</FieldLabel>
                <Textarea
                  id="composeTemplate"
                  required
                  rows={12}
                  className="font-mono text-xs"
                  value={composeTemplate}
                  onChange={(e) => setComposeTemplate(e.target.value)}
                  spellCheck={false}
                />
              </Field>

              <Field orientation="horizontal">
                <FieldLabel htmlFor="isActive">Producto activo</FieldLabel>
                <Switch id="isActive" checked={isActive} onCheckedChange={setIsActive} />
              </Field>

              <Field>
                <FieldLabel>Variables de entorno configurables</FieldLabel>
                <div className="flex flex-col gap-3">
                  {envVars.map((envVar) => (
                    <div
                      key={envVar.rowId}
                      className="grid grid-cols-1 gap-2 rounded-2xl border border-border p-3 sm:grid-cols-2"
                    >
                      <div className="flex flex-col gap-1">
                        <FieldLabel htmlFor={`key-${envVar.rowId}`} className="text-xs">
                          Clave (env var)
                        </FieldLabel>
                        <Input
                          id={`key-${envVar.rowId}`}
                          required
                          value={envVar.key}
                          onChange={(e) =>
                            updateEnvVar(envVar.rowId, { key: e.target.value })
                          }
                          placeholder="DB_PASSWORD"
                        />
                      </div>
                      <div className="flex flex-col gap-1">
                        <FieldLabel htmlFor={`label-${envVar.rowId}`} className="text-xs">
                          Etiqueta
                        </FieldLabel>
                        <Input
                          id={`label-${envVar.rowId}`}
                          required
                          value={envVar.label}
                          onChange={(e) =>
                            updateEnvVar(envVar.rowId, { label: e.target.value })
                          }
                          placeholder="Contraseña de la base de datos"
                        />
                      </div>
                      <div className="flex flex-col gap-1">
                        <FieldLabel htmlFor={`default-${envVar.rowId}`} className="text-xs">
                          Valor por defecto
                        </FieldLabel>
                        <Input
                          id={`default-${envVar.rowId}`}
                          value={envVar.defaultValue}
                          onChange={(e) =>
                            updateEnvVar(envVar.rowId, { defaultValue: e.target.value })
                          }
                        />
                      </div>
                      <div className="flex items-end justify-between gap-2">
                        <div className="flex items-center gap-2">
                          <Switch
                            id={`secret-${envVar.rowId}`}
                            size="sm"
                            checked={envVar.isSecret}
                            onCheckedChange={(checked) =>
                              updateEnvVar(envVar.rowId, { isSecret: checked })
                            }
                          />
                          <FieldLabel htmlFor={`secret-${envVar.rowId}`} className="text-xs">
                            Es secreto
                          </FieldLabel>
                        </div>
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          onClick={() => removeEnvVar(envVar.rowId)}
                        >
                          Quitar
                        </Button>
                      </div>
                    </div>
                  ))}
                  <Button type="button" variant="outline" size="sm" onClick={addEnvVar}>
                    Añadir variable de entorno
                  </Button>
                </div>
              </Field>

              <Button type="submit" disabled={isSubmitting}>
                {isSubmitting ? "Guardando..." : isEditing ? "Guardar cambios" : "Crear producto"}
              </Button>
            </FieldGroup>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
