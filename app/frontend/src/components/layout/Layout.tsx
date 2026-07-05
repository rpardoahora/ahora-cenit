import { Outlet } from "react-router-dom"
import { Navbar } from "@/components/layout/Navbar"

export function Layout() {
  return (
    <div className="flex min-h-svh flex-col bg-background text-foreground">
      <Navbar />
      <main className="mx-auto w-full max-w-6xl flex-1 px-4 py-8">
        <Outlet />
      </main>
      <footer className="border-t border-border py-6 text-center text-xs text-muted-foreground">
        ahora-cenit &mdash; marketplace de aplicaciones dockerizadas
      </footer>
    </div>
  )
}
