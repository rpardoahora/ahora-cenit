import { NavLink, Outlet } from "react-router-dom"
import { cn } from "@/lib/utils"

const NAV_ITEMS = [
  { to: "/admin/productos", label: "Productos" },
  { to: "/admin/usuarios", label: "Usuarios" },
]

export function AdminLayout() {
  return (
    <div className="flex flex-col gap-6 sm:flex-row sm:items-start sm:gap-8">
      <aside className="shrink-0 sm:sticky sm:top-20 sm:w-48">
        <nav className="flex gap-1 overflow-x-auto sm:flex-col sm:overflow-visible">
          {NAV_ITEMS.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              className={({ isActive }) =>
                cn(
                  "rounded-2xl px-3 py-2 text-sm font-medium whitespace-nowrap transition-colors",
                  isActive
                    ? "bg-muted text-foreground"
                    : "text-muted-foreground hover:bg-muted/50 hover:text-foreground"
                )
              }
            >
              {item.label}
            </NavLink>
          ))}
        </nav>
      </aside>
      <div className="min-w-0 flex-1">
        <Outlet />
      </div>
    </div>
  )
}
