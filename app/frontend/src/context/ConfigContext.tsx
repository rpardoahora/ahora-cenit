import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react"
import { configApi } from "@/lib/api"
import type { AppConfig } from "@/types"

interface ConfigContextValue {
  /** Mientras no se sepa lo contrario, el registro se considera abierto (no parpadea el botón). */
  registrationEnabled: boolean
  /** Días que pasa en la papelera lo que se borra (7 mientras no se sepa). */
  trashRetentionDays: number
  isLoading: boolean
  refresh: () => Promise<void>
}

const ConfigContext = createContext<ConfigContextValue | null>(null)

/** Configuración pública del portal (GET /api/config), compartida por toda la app. */
export function ConfigProvider({ children }: { children: ReactNode }) {
  const [config, setConfig] = useState<AppConfig | null>(null)
  const [isLoading, setIsLoading] = useState(true)

  const refresh = useCallback(async () => {
    try {
      setConfig(await configApi.get())
    } catch {
      // Sin respuesta: se mantiene el último valor conocido.
    } finally {
      setIsLoading(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const value = useMemo<ConfigContextValue>(
    () => ({
      registrationEnabled: config?.registrationEnabled ?? true,
      trashRetentionDays: config?.trashRetentionDays ?? 7,
      isLoading,
      refresh,
    }),
    [config, isLoading, refresh]
  )

  return <ConfigContext.Provider value={value}>{children}</ConfigContext.Provider>
}

export function useConfig() {
  const context = useContext(ConfigContext)
  if (!context) throw new Error("useConfig debe usarse dentro de <ConfigProvider>")
  return context
}
