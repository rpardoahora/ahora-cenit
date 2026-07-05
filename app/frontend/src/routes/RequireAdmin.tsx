import { Navigate, Outlet } from "react-router-dom"
import { useAuth } from "@/context/AuthContext"

export function RequireAdmin() {
  const { user, isLoading } = useAuth()

  if (isLoading) return null

  if (!user) {
    return <Navigate to="/login" replace />
  }

  if (user.role !== "Admin") {
    return <Navigate to="/" replace />
  }

  return <Outlet />
}
