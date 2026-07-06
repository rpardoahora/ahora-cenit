import { useState } from "react"
import { Link } from "react-router-dom"
import { HugeiconsIcon } from "@hugeicons/react"
import { Settings01Icon } from "@hugeicons/core-free-icons"
import { useAuth } from "@/context/AuthContext"
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
  const [profileOpen, setProfileOpen] = useState(false)

  return (
    <header className="sticky top-0 z-40 border-b border-border bg-background/80 backdrop-blur-sm">
      <div className="mx-auto flex h-14 max-w-6xl items-center justify-between px-4">
        <Link to="/" className="font-heading text-base font-semibold">
          ahora<span className="text-primary">cenit</span>
        </Link>

        <nav className="flex items-center gap-1">
          <Button variant="ghost" size="sm" render={<Link to="/" />}>
            Catálogo
          </Button>

          {user && (
            <Button variant="ghost" size="sm" render={<Link to="/aplicaciones" />}>
              Mis aplicaciones
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
            <div className="ml-2 flex items-center gap-2">
              <Button variant="ghost" size="sm" render={<Link to="/login" />}>
                Iniciar sesión
              </Button>
              <Button size="sm" render={<Link to="/registro" />}>
                Registrarse
              </Button>
            </div>
          ) : (
            <>
              <button
                className="ml-2 cursor-pointer rounded-full focus-visible:outline-2 focus-visible:outline-ring"
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
