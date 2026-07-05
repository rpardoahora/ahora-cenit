import { useEffect, useState } from "react"
import { Link, useNavigate, useSearchParams } from "react-router-dom"
import { authApi, ApiError } from "@/lib/api"
import { useAuth } from "@/context/AuthContext"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Button } from "@/components/ui/button"

export function ConfirmEmailPage() {
  const [searchParams] = useSearchParams()
  const token = searchParams.get("token") ?? ""
  const { login: _login } = useAuth()
  const navigate = useNavigate()

  const [status, setStatus] = useState<"loading" | "success" | "error">("loading")
  const [message, setMessage] = useState("")

  useEffect(() => {
    if (!token) {
      setStatus("error")
      setMessage("Falta el token de confirmación. Solicita un nuevo enlace.")
      return
    }

    authApi
      .confirmEmail({ token })
      .then(() => {
        setStatus("success")
      })
      .catch((err) => {
        setStatus("error")
        setMessage(
          err instanceof ApiError ? err.message : "El enlace de confirmación no es válido o ha expirado."
        )
      })
  }, [token])

  if (status === "loading") {
    return (
      <div className="mx-auto max-w-sm">
        <Card>
          <CardHeader>
            <CardTitle>Confirmando email...</CardTitle>
            <CardDescription>Por favor espera.</CardDescription>
          </CardHeader>
        </Card>
      </div>
    )
  }

  if (status === "error") {
    return (
      <div className="mx-auto max-w-sm">
        <Card>
          <CardHeader>
            <CardTitle>Error de confirmación</CardTitle>
            <CardDescription>{message}</CardDescription>
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
          <CardTitle>Email confirmado</CardTitle>
          <CardDescription>Tu cuenta ha sido activada. Ya puedes iniciar sesión.</CardDescription>
        </CardHeader>
        <CardContent>
          <Button onClick={() => navigate("/login")}>Iniciar sesión</Button>
        </CardContent>
      </Card>
    </div>
  )
}
