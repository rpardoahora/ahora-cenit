import { useState, type FormEvent } from "react"
import { Link, useNavigate } from "react-router-dom"
import { toast } from "sonner"
import { useAuth } from "@/context/AuthContext"
import { useConfig } from "@/context/ConfigContext"
import { ApiError } from "@/lib/api"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Field, FieldGroup, FieldLabel, FieldError } from "@/components/ui/field"

export function RegisterPage() {
  const { register } = useAuth()
  const { registrationEnabled, isLoading: configLoading } = useConfig()
  const navigate = useNavigate()
  const [name, setName] = useState("")
  const [email, setEmail] = useState("")
  const [password, setPassword] = useState("")
  const [error, setError] = useState<string | null>(null)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [pendingMessage, setPendingMessage] = useState<string | null>(null)

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setIsSubmitting(true)
    try {
      const result = await register({ name, email, password })
      if ("requiresEmailConfirmation" in result) {
        setPendingMessage(result.message)
      } else {
        navigate("/", { replace: true })
      }
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message)
      } else {
        toast.error("No se pudo completar el registro.")
      }
    } finally {
      setIsSubmitting(false)
    }
  }

  if (!configLoading && !registrationEnabled) {
    return (
      <div className="mx-auto max-w-sm">
        <Card>
          <CardHeader>
            <CardTitle>Registro deshabilitado</CardTitle>
            <CardDescription>
              Ahora mismo no se admiten altas nuevas desde esta página. Si necesitas una
              cuenta, contacta con el administrador.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Link to="/login" className="text-sm text-primary underline-offset-4 hover:underline">
              Ya tengo cuenta: iniciar sesión
            </Link>
          </CardContent>
        </Card>
      </div>
    )
  }

  if (pendingMessage) {
    return (
      <div className="mx-auto max-w-sm">
        <Card>
          <CardHeader>
            <CardTitle>Revisa tu email</CardTitle>
            <CardDescription>{pendingMessage}</CardDescription>
          </CardHeader>
          <CardContent>
            <Link to="/login" className="text-sm text-primary underline-offset-4 hover:underline">
              Volver a iniciar sesión
            </Link>
          </CardContent>
        </Card>
      </div>
    )
  }

  return (
    <div className="mx-auto max-w-sm">
      <Card>
        <CardHeader>
          <CardTitle>Crear cuenta</CardTitle>
          <CardDescription>
            Regístrate para desplegar aplicaciones del catálogo.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit}>
            <FieldGroup>
              <Field>
                <FieldLabel htmlFor="name">Nombre</FieldLabel>
                <Input
                  id="name"
                  autoComplete="name"
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
                  autoComplete="email"
                  required
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                />
              </Field>
              <Field data-invalid={!!error}>
                <FieldLabel htmlFor="password">Contraseña</FieldLabel>
                <Input
                  id="password"
                  type="password"
                  autoComplete="new-password"
                  required
                  minLength={8}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
                {error && <FieldError>{error}</FieldError>}
              </Field>
              <Button type="submit" disabled={isSubmitting}>
                {isSubmitting ? "Creando cuenta..." : "Crear cuenta"}
              </Button>
              <p className="text-center text-sm text-muted-foreground">
                ¿Ya tienes cuenta?{" "}
                <Link to="/login" className="text-primary underline-offset-4 hover:underline">
                  Inicia sesión
                </Link>
              </p>
            </FieldGroup>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
