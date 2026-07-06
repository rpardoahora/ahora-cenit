import { useEffect, useState, type FormEvent } from "react"
import { useNavigate, useParams } from "react-router-dom"
import { toast } from "sonner"
import { usersApi, ApiError } from "@/lib/api"
import type { Role } from "@/types"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Switch } from "@/components/ui/switch"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Field, FieldGroup, FieldLabel, FieldDescription, FieldError } from "@/components/ui/field"
import { Skeleton } from "@/components/ui/skeleton"

export function UserFormPage() {
  const { id } = useParams<{ id: string }>()
  const isEditing = !!id
  const navigate = useNavigate()

  const [loading, setLoading] = useState(isEditing)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [name, setName] = useState("")
  const [email, setEmail] = useState("")
  const [password, setPassword] = useState("")
  const [isAdmin, setIsAdmin] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!id) return
    usersApi
      .get(id)
      .then((user) => {
        setName(user.name)
        setEmail(user.email)
        setIsAdmin(user.role === "Admin")
      })
      .catch(() => toast.error("No se pudo cargar el usuario."))
      .finally(() => setLoading(false))
  }, [id])

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setIsSubmitting(true)
    try {
      const role: Role = isAdmin ? "Admin" : "Cliente"

      if (isEditing && id) {
        await usersApi.update(id, { name, email, role })
        if (password) {
          await usersApi.resetPassword(id, password)
        }
        toast.success("Usuario actualizado.")
      } else {
        await usersApi.create({ name, email, password, role })
        toast.success("Usuario creado.")
      }
      navigate("/admin/usuarios")
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message)
      } else {
        toast.error("No se pudo guardar el usuario.")
      }
    } finally {
      setIsSubmitting(false)
    }
  }

  if (loading) {
    return (
      <div className="mx-auto max-w-lg space-y-4">
        <Skeleton className="h-8 w-1/2" />
        <Skeleton className="h-64 w-full" />
      </div>
    )
  }

  return (
    <div className="mx-auto max-w-lg">
      <Card>
        <CardHeader>
          <CardTitle>{isEditing ? "Editar usuario" : "Nuevo usuario"}</CardTitle>
          <CardDescription>
            {isEditing
              ? "Modifica los datos del usuario. El slug de cliente no cambia."
              : "Crea manualmente una cuenta ya confirmada."}
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
                <FieldLabel htmlFor="email">Email</FieldLabel>
                <Input
                  id="email"
                  type="email"
                  required
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                />
              </Field>

              <Field data-invalid={!!error}>
                <FieldLabel htmlFor="password">
                  {isEditing ? "Nueva contraseña" : "Contraseña"}
                </FieldLabel>
                <Input
                  id="password"
                  type="password"
                  autoComplete="new-password"
                  required={!isEditing}
                  minLength={8}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
                {isEditing && (
                  <FieldDescription>Déjala en blanco para no cambiarla.</FieldDescription>
                )}
                {error && <FieldError>{error}</FieldError>}
              </Field>

              <Field orientation="horizontal">
                <FieldLabel htmlFor="isAdmin">Administrador</FieldLabel>
                <Switch id="isAdmin" checked={isAdmin} onCheckedChange={setIsAdmin} />
              </Field>

              <Button type="submit" disabled={isSubmitting}>
                {isSubmitting ? "Guardando..." : isEditing ? "Guardar cambios" : "Crear usuario"}
              </Button>
            </FieldGroup>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
