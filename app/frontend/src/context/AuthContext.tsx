import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react"
import { authApi, getToken, setToken, setUnauthorizedHandler } from "@/lib/api"
import type { RegisterResult, User } from "@/types"

interface RegisterInput {
  name: string
  email: string
  password: string
}

interface LoginInput {
  identifier: string
  password: string
}

interface AuthContextValue {
  user: User | null
  isLoading: boolean
  login: (input: LoginInput) => Promise<void>
  /** Devuelve el resultado tal cual: si requiere confirmación de email, no hay sesión iniciada. */
  register: (input: RegisterInput) => Promise<RegisterResult>
  logout: () => void
  refreshUser: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null)
  const [isLoading, setIsLoading] = useState(true)

  const logout = useCallback(() => {
    setToken(null)
    setUser(null)
  }, [])

  useEffect(() => {
    setUnauthorizedHandler(logout)
    return () => setUnauthorizedHandler(null)
  }, [logout])

  useEffect(() => {
    const token = getToken()
    if (!token) {
      setIsLoading(false)
      return
    }
    authApi
      .me()
      .then(setUser)
      .catch(() => setToken(null))
      .finally(() => setIsLoading(false))
  }, [])

  const login = useCallback(async (input: LoginInput) => {
    const response = await authApi.login(input)
    setToken(response.token)
    setUser(response.user)
  }, [])

  const register = useCallback(async (input: RegisterInput) => {
    const response = await authApi.register(input)
    if ("token" in response) {
      setToken(response.token)
      setUser(response.user)
    }
    return response
  }, [])

  const refreshUser = useCallback(async () => {
    if (!getToken()) return
    const me = await authApi.me()
    setUser(me)
  }, [])

  const value = useMemo(
    () => ({ user, isLoading, login, register, logout, refreshUser }),
    [user, isLoading, login, register, logout, refreshUser]
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error("useAuth must be used within an AuthProvider")
  return ctx
}
