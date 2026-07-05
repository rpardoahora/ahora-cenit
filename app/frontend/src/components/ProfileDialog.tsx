import { useState, type FormEvent } from "react"
import { useNavigate } from "react-router-dom"
import { toast } from "sonner"
import { useAuth } from "@/context/AuthContext"
import { authApi, ApiError } from "@/lib/api"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Separator } from "@/components/ui/separator"
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { Field, FieldGroup, FieldLabel, FieldError } from "@/components/ui/field"

interface ProfileDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
}

export function ProfileDialog({ open, onOpenChange }: ProfileDialogProps) {
  const { user, logout, refreshUser } = useAuth()
  const navigate = useNavigate()

  const [name, setName] = useState(user?.name ?? "")
  const [nameError, setNameError] = useState<string | null>(null)
  const [isSavingName, setIsSavingName] = useState(false)

  const [currentPassword, setCurrentPassword] = useState("")
  const [newPassword, setNewPassword] = useState("")
  const [passwordError, setPasswordError] = useState<string | null>(null)
  const [isSavingPassword, setIsSavingPassword] = useState(false)

  async function handleSaveName(e: FormEvent) {
    e.preventDefault()
    setNameError(null)
    setIsSavingName(true)
    try {
      await authApi.updateProfile({ name })
      await refreshUser()
      toast.success("Nombre actualizado.")
    } catch (err) {
      if (err instanceof ApiError) setNameError(err.message)
      else toast.error("No se pudo actualizar el nombre.")
    } finally {
      setIsSavingName(false)
    }
  }

  async function handleChangePassword(e: FormEvent) {
    e.preventDefault()
    setPasswordError(null)
    setIsSavingPassword(true)
    try {
      await authApi.changePassword({ currentPassword, newPassword })
      setCurrentPassword("")
      setNewPassword("")
      toast.success("Contraseña actualizada.")
    } catch (err) {
      if (err instanceof ApiError) setPasswordError(err.message)
      else toast.error("No se pudo cambiar la contraseña.")
    } finally {
      setIsSavingPassword(false)
    }
  }

  function handleLogout() {
    logout()
    onOpenChange(false)
    navigate("/")
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Mi cuenta</DialogTitle>
        </DialogHeader>

        <form onSubmit={handleSaveName}>
          <FieldGroup>
            <Field data-invalid={!!nameError}>
              <FieldLabel htmlFor="profile-name">Nombre</FieldLabel>
              <Input
                id="profile-name"
                required
                value={name}
                onChange={(e) => setName(e.target.value)}
              />
              {nameError && <FieldError>{nameError}</FieldError>}
            </Field>
            <Button type="submit" size="sm" disabled={isSavingName}>
              {isSavingName ? "Guardando..." : "Guardar nombre"}
            </Button>
          </FieldGroup>
        </form>

        <Separator />

        <form onSubmit={handleChangePassword}>
          <FieldGroup>
            <Field data-invalid={!!passwordError}>
              <FieldLabel htmlFor="current-password">Contraseña actual</FieldLabel>
              <Input
                id="current-password"
                type="password"
                autoComplete="current-password"
                required
                value={currentPassword}
                onChange={(e) => setCurrentPassword(e.target.value)}
              />
            </Field>
            <Field data-invalid={!!passwordError}>
              <FieldLabel htmlFor="new-password">Nueva contraseña</FieldLabel>
              <Input
                id="new-password"
                type="password"
                autoComplete="new-password"
                required
                minLength={8}
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
              />
              {passwordError && <FieldError>{passwordError}</FieldError>}
            </Field>
            <Button type="submit" size="sm" disabled={isSavingPassword}>
              {isSavingPassword ? "Guardando..." : "Cambiar contraseña"}
            </Button>
          </FieldGroup>
        </form>

        <Separator />

        <Button variant="destructive" onClick={handleLogout}>
          Cerrar sesión
        </Button>
      </DialogContent>
    </Dialog>
  )
}
