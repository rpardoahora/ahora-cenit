import { toast } from "sonner"
import type {
  AdminTool,
  AdminUser,
  AppConfig,
  Application,
  ApplicationStatus,
  ApplicationUsage,
  AuthResponse,
  CreateApplicationInput,
  CreateUserInput,
  DeploySuggestion,
  DeployStats,
  ImportProductsResult,
  MessageResponse,
  PortalSettings,
  Product,
  ProductExportEnvelope,
  ProductInput,
  RegisterResult,
  UpdateUserInput,
  User,
} from "@/types"

const BASE_URL = (import.meta.env.VITE_API_URL ?? "").replace(/\/+$/, "")

const TOKEN_KEY = "ahora-cenit:token"

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY)
}

export function setToken(token: string | null) {
  if (token) {
    localStorage.setItem(TOKEN_KEY, token)
  } else {
    localStorage.removeItem(TOKEN_KEY)
  }
}

/** Called when the API responds 401. Wired up by AuthProvider at startup. */
let onUnauthorized: (() => void) | null = null
export function setUnauthorizedHandler(handler: (() => void) | null) {
  onUnauthorized = handler
}

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
    this.name = "ApiError"
  }
}

interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE"
  body?: unknown
  /** Suppress the automatic error toast (caller handles its own error UI). */
  silent?: boolean
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = "GET", body, silent } = options
  const token = getToken()

  const headers: Record<string, string> = {}
  if (body !== undefined) headers["Content-Type"] = "application/json"
  if (token) headers["Authorization"] = `Bearer ${token}`

  let res: Response
  try {
    res = await fetch(`${BASE_URL}${path}`, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    })
  } catch {
    const message = "No se pudo conectar con el servidor."
    if (!silent) toast.error(message)
    throw new ApiError(0, message)
  }

  // Solo es una sesión caducada si se envió token; sin él (p. ej. el login)
  // el 401 trae su propio mensaje y se trata como cualquier otro error.
  if (res.status === 401 && token) {
    setToken(null)
    onUnauthorized?.()
    const message = "Tu sesión ha expirado. Inicia sesión de nuevo."
    if (!silent) toast.error(message)
    throw new ApiError(401, message)
  }

  if (!res.ok) {
    let message = `Error ${res.status}`
    try {
      const data = await res.json()
      message = data?.message ?? data?.title ?? message
    } catch {
      // response had no JSON body
    }
    if (!silent) toast.error(message)
    throw new ApiError(res.status, message)
  }

  if (res.status === 204) return undefined as T

  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

// ---------- Config ----------

export const configApi = {
  get: () => request<AppConfig>("/config", { silent: true }),
}

// ---------- Admin settings ----------

export const adminSettingsApi = {
  get: () => request<PortalSettings>("/admin/settings"),
  update: (settings: PortalSettings) =>
    request<PortalSettings>("/admin/settings", { method: "PUT", body: settings }),
}

// ---------- Admin tools ----------

export const adminToolsApi = {
  list: () => request<AdminTool[]>("/admin/tools"),
}

// ---------- Auth ----------

export const authApi = {
  register: (input: { name: string; email: string; password: string }) =>
    request<RegisterResult>("/auth/register", { method: "POST", body: input }),
  login: (input: { identifier: string; password: string }) =>
    request<AuthResponse>("/auth/login", { method: "POST", body: input }),
  me: () => request<User>("/auth/me", { silent: true }),
  updateProfile: (input: { name: string }) =>
    request<User>("/auth/me", { method: "PATCH", body: input }),
  changePassword: (input: { currentPassword: string; newPassword: string }) =>
    request<MessageResponse>("/auth/change-password", { method: "POST", body: input }),
  forgotPassword: (input: { identifier: string }) =>
    request<MessageResponse>("/auth/forgot-password", { method: "POST", body: input }),
  resetPassword: (input: { token: string; newPassword: string }) =>
    request<MessageResponse>("/auth/reset-password", { method: "POST", body: input }),
  confirmEmail: (input: { token: string }) =>
    request<AuthResponse>("/auth/confirm-email", { method: "POST", body: input }),
}

// ---------- Products ----------

export const productsApi = {
  list: (params?: { includeInactive?: boolean }) => {
    const query = params?.includeInactive ? "?includeInactive=true" : ""
    return request<Product[]>(`/products${query}`)
  },
  get: (id: string) => request<Product>(`/products/${id}`),
  create: (input: ProductInput) =>
    request<Product>("/products", { method: "POST", body: input }),
  update: (id: string, input: ProductInput) =>
    request<Product>(`/products/${id}`, { method: "PUT", body: input }),
  setActive: (id: string, isActive: boolean) =>
    request<Product>(`/products/${id}/active`, {
      method: "PATCH",
      body: { isActive },
    }),
  remove: (id: string) => request<void>(`/products/${id}`, { method: "DELETE" }),
  suggestSubdomain: (id: string) =>
    request<DeploySuggestion>(`/products/${id}/suggest-subdomain`, {
      silent: true,
    }),
  deployStats: (id: string) =>
    request<DeployStats>(`/products/${id}/deploy-stats`, { silent: true }),
  exportAll: () => request<ProductExportEnvelope>("/products/export"),
  importAll: (payload: ProductExportEnvelope) =>
    request<ImportProductsResult>("/products/import", { method: "POST", body: payload }),
}

// ---------- Users (admin) ----------

export const usersApi = {
  list: () => request<AdminUser[]>("/users"),
  get: (id: string) => request<AdminUser>(`/users/${id}`),
  create: (input: CreateUserInput) =>
    request<AdminUser>("/users", { method: "POST", body: input }),
  update: (id: string, input: UpdateUserInput) =>
    request<AdminUser>(`/users/${id}`, { method: "PUT", body: input }),
  resetPassword: (id: string, newPassword: string) =>
    request<MessageResponse>(`/users/${id}/reset-password`, {
      method: "POST",
      body: { newPassword },
    }),
  remove: (id: string) => request<void>(`/users/${id}`, { method: "DELETE" }),
}

// ---------- Applications ----------

export const applicationsApi = {
  list: (params?: { clientSlug?: string; userId?: string }) => {
    const search = new URLSearchParams()
    if (params?.clientSlug) search.set("clientSlug", params.clientSlug)
    if (params?.userId) search.set("userId", params.userId)
    const query = search.toString()
    return request<Application[]>(`/applications${query ? `?${query}` : ""}`)
  },
  get: (id: string) => request<Application>(`/applications/${id}`),
  create: (input: CreateApplicationInput) =>
    request<Application>("/applications", { method: "POST", body: input }),
  status: (id: string) =>
    request<{ status: ApplicationStatus }>(`/applications/${id}/status`, {
      silent: true,
    }),
  start: (id: string) =>
    request<Application>(`/applications/${id}/start`, { method: "POST" }),
  stop: (id: string) =>
    request<Application>(`/applications/${id}/stop`, { method: "POST" }),
  remove: (id: string) =>
    request<void>(`/applications/${id}`, { method: "DELETE" }),
  usage: () => request<ApplicationUsage[]>("/applications/usage"),
}
