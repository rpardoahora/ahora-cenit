import { useState } from "react"
import { Link } from "react-router-dom"
import { HugeiconsIcon } from "@hugeicons/react"
import { Settings01Icon } from "@hugeicons/core-free-icons"
import { useAuth } from "@/context/AuthContext"
import { useConfig } from "@/context/ConfigContext"
import { Button } from "@/components/ui/button"
import { Avatar, AvatarFallback } from "@/components/ui/avatar"
import { ProfileDialog } from "@/components/ProfileDialog"

function initials(name: string) {
  return name
    .split(" ")
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]!.toUpperCase())
    .join("")
}

export function Navbar() {
  const { user } = useAuth()
  const { registrationEnabled } = useConfig()
  const [profileOpen, setProfileOpen] = useState(false)

  return (
    <header className="sticky top-0 z-40 border-b border-border bg-background/80 backdrop-blur-sm">
      <div className="mx-auto flex h-14 max-w-6xl items-center justify-between gap-2 px-3 sm:px-4">
        <Link to="/" className="shrink-0 font-heading text-base font-semibold">
          ahora<span className="text-primary">cenit</span>
        </Link>

        <nav className="flex min-w-0 items-center gap-0.5 sm:gap-1">
          <Button variant="ghost" size="sm" className="hidden sm:inline-flex" render={<Link to="/" />}>
            Catálogo
          </Button>

          {user && (
            <Button variant="ghost" size="sm" render={<Link to="/aplicaciones" />}>
              <span className="hidden sm:inline">Mis aplicaciones</span>
              <span className="sm:hidden">Apps</span>
            </Button>
          )}

          {user?.role === "Admin" && (
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Administración"
              render={<Link to="/admin" />}
            >
              <HugeiconsIcon icon={Settings01Icon} strokeWidth={2} />
            </Button>
          )}

          {!user ? (
            <div className="ml-1 flex items-center gap-1 sm:ml-2 sm:gap-2">
              <Button
                variant="ghost"
                size="sm"
                className="px-2 sm:px-3"
                render={<Link to="/login" />}
              >
                <span className="hidden sm:inline">Iniciar sesión</span>
                <span className="sm:hidden">Entrar</span>
              </Button>
              {registrationEnabled && (
                <Button size="sm" className="px-2 sm:px-3" render={<Link to="/registro" />}>
                  Registrarse
                </Button>
              )}
            </div>
          ) : (
            <>
              <button
                className="ml-1 cursor-pointer rounded-full focus-visible:outline-2 focus-visible:outline-ring sm:ml-2"
                aria-label="Cuenta"
                onClick={() => setProfileOpen(true)}
              >
                <Avatar size="sm">
                  <AvatarFallback>{initials(user.name)}</AvatarFallback>
                </Avatar>
              </button>
              <ProfileDialog open={profileOpen} onOpenChange={setProfileOpen} />
            </>
          )}
        </nav>
      </div>
    </header>
  )
}
