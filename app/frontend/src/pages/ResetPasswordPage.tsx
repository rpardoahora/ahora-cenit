import { useState, type FormEvent } from "react"
import { Link, useNavigate, useSearchParams } from "react-router-dom"
import { toast } from "sonner"
import { authApi, ApiError } from "@/lib/api"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Field, FieldGroup, FieldLabel, FieldError } from "@/components/ui/field"

export function ResetPasswordPage() {
  const [searchParams] = useSearchParams()
  const token = searchParams.get("token") ?? ""
  const navigate = useNavigate()

  const [newPassword, setNewPassword] = useState("")
  const [error, setError] = useState<string | null>(null)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [done, setDone] = useState(false)

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setIsSubmitting(true)
    try {
      await authApi.resetPassword({ token, newPassword })
      setDone(true)
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message)
      } else {
        toast.error("No se pudo restablecer la contraseña.")
      }
    } finally {
      setIsSubmitting(false)
    }
  }

  if (!token) {
    return (
      <div className="mx-auto max-w-sm">
        <Card>
          <CardHeader>
            <CardTitle>Enlace no válido</CardTitle>
            <CardDescription>
              Falta el token de restablecimiento. Solicita un nuevo enlace.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Link to="/olvide-password" className="text-sm text-primary underline-offset-4 hover:underline">
              Solicitar de nuevo
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
          <CardTitle>Restablecer contraseña</CardTitle>
          <CardDescription>Elige una nueva contraseña para tu cuenta.</CardDescription>
        </CardHeader>
        <CardContent>
          {done ? (
            <div className="flex flex-col gap-4">
              <p className="text-sm text-muted-foreground">
                Contraseña actualizada. Ya puedes iniciar sesión.
              </p>
              <Button onClick={() => navigate("/login")}>Ir a iniciar sesión</Button>
            </div>
          ) : (
            <form onSubmit={handleSubmit}>
              <FieldGroup>
                <Field data-invalid={!!error}>
                  <FieldLabel htmlFor="newPassword">Nueva contraseña</FieldLabel>
                  <Input
                    id="newPassword"
                    type="password"
                    autoComplete="new-password"
                    required
                    minLength={8}
                    value={newPassword}
                    onChange={(e) => setNewPassword(e.target.value)}
                  />
                  {error && <FieldError>{error}</FieldError>}
                </Field>
                <Button type="submit" disabled={isSubmitting}>
                  {isSubmitting ? "Guardando..." : "Restablecer contraseña"}
                </Button>
              </FieldGroup>
            </form>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
