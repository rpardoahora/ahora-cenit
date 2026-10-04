import { useState, type FormEvent } from "react"
import { Link, useLocation, useNavigate } from "react-router-dom"
import { toast } from "sonner"
import { useAuth } from "@/context/AuthContext"
import { useConfig } from "@/context/ConfigContext"
import { ApiError } from "@/lib/api"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Field, FieldGroup, FieldLabel, FieldError } from "@/components/ui/field"

export function LoginPage() {
  const { login } = useAuth()
  const { registrationEnabled } = useConfig()
  const navigate = useNavigate()
  const location = useLocation()
  const [identifier, setIdentifier] = useState("")
  const [password, setPassword] = useState("")
  const [error, setError] = useState<string | null>(null)
  const [isSubmitting, setIsSubmitting] = useState(false)

  const from =
    (location.state as { from?: { pathname?: string } } | null)?.from?.pathname ?? "/"

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setIsSubmitting(true)
    try {
      await login({ identifier, password })
      navigate(from, { replace: true })
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message)
      } else {
        toast.error("No se pudo iniciar sesión.")
      }
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <div className="mx-auto max-w-sm">
      <Card>
        <CardHeader>
          <CardTitle>Iniciar sesión</CardTitle>
          <CardDescription>Accede a tu cuenta de ahora-cenit.</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit}>
            <FieldGroup>
              <Field data-invalid={!!error}>
                <FieldLabel htmlFor="identifier">Email o usuario</FieldLabel>
                <Input
                  id="identifier"
                  autoComplete="username"
                  required
                  value={identifier}
                  onChange={(e) => setIdentifier(e.target.value)}
                />
              </Field>
              <Field data-invalid={!!error}>
                <FieldLabel htmlFor="password">Contraseña</FieldLabel>
                <Input
                  id="password"
                  type="password"
                  autoComplete="current-password"
                  required
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
                {error && <FieldError>{error}</FieldError>}
              </Field>
              <div className="flex justify-end">
                <Link
                  to="/olvide-password"
                  className="text-sm text-muted-foreground underline-offset-4 hover:underline"
                >
                  ¿Olvidaste tu contraseña?
                </Link>
              </div>
              <Button type="submit" disabled={isSubmitting}>
                {isSubmitting ? "Accediendo..." : "Entrar"}
              </Button>
              {registrationEnabled && (
                <p className="text-center text-sm text-muted-foreground">
                  ¿No tienes cuenta?{" "}
                  <Link to="/registro" className="text-primary underline-offset-4 hover:underline">
                    Regístrate
                  </Link>
                </p>
              )}
            </FieldGroup>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
