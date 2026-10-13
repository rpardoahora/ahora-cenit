export type Role = "Admin" | "Cliente"

export interface AppConfig {
  baseDomain: string
  /** Si es false, nadie puede crearse una cuenta desde el portal (solo un admin). */
  registrationEnabled: boolean
  /** Modo SQL por defecto al desplegar (el de los ajustes del portal). */
  appsSqlMode: AppsSqlMode
  /** appsSqlMode === "Shared": las aplicaciones se instalan solas y piden la contraseña de su administrador. */
  sharedSql: boolean
  /** Días que pasa en la papelera lo que se borra. */
  trashRetentionDays: number
}

/** GET /api/admin/trash */
export interface Trash {
  retentionDays: number
  applications: {
    id: string
    subdomain: string
    productName: string
    userId: string
    clientName: string
    clientSlug: string
    deletedAt: string
    purgeAt: string
  }[]
  users: { id: string; name: string; email: string; clientSlug: string; deletedAt: string; purgeAt: string }[]
}

/** GET /api/products/{id}/overview */
export interface ProductOverview {
  productId: string
  productName: string
  images: ProductImageInfo[]
  nuget: ProductNugetInfo | null
  installations: ProductInstallation[]
}

export interface ProductImageInfo {
  service: string
  repository: string
  configuredTag: string | null
  latestTag: string | null
  updateAvailable: boolean
  recentTags: string[]
  repositoryUrl: string | null
  error: string | null
}

export interface ProductNugetInfo {
  packageId: string
  packageIdInferred: boolean
  feedUrl: string | null
  latestVersion: string | null
  recentVersions: string[]
  repositoryUrl: string | null
  error: string | null
}

export interface ProductInstallation {
  applicationId: string
  subdomain: string
  url: string
  userId: string
  clientName: string
  clientSlug: string
  status: ApplicationStatus
  createdAt: string
  images: { service: string; image: string; tag: string | null; state: string }[]
  outdated: boolean
}

/** Dedicated: cada aplicación con la imagen SQL de su compose. Shared: todas en el SQL Server de la plataforma. */
export type AppsSqlMode = "Dedicated" | "Shared"

export interface PortalSettings {
  registrationEnabled: boolean
  appsSqlMode: AppsSqlMode
}

export interface AdminTool {
  key: string
  name: string
  description: string
  url: string
  /** Solo el repositorio NuGet: el feed para dotnet/NuGet. */
  feedUrl?: string | null
}

export interface User {
  id: string
  name: string
  email: string
  role: Role
  clientSlug: string
  emailConfirmed: boolean
}

export interface AdminUser extends User {
  createdAt: string
  applicationsCount: number
}

export interface CreateUserInput {
  email: string
  password: string
  name: string
  role: Role
}

export interface UpdateUserInput {
  email: string
  name: string
  role: Role
}

export interface AuthResponse {
  token: string
  user: User
}

export interface RegistrationPendingResponse {
  requiresEmailConfirmation: true
  message: string
}

export type RegisterResult = AuthResponse | RegistrationPendingResponse

export interface MessageResponse {
  message: string
}

export type EnvVarInputMode = "Text" | "Secret" | "ReadOnly" | "Hidden"

export interface EnvVarSchema {
  key: string
  label: string
  defaultValue: string
  mode: EnvVarInputMode
}

export interface Product {
  id: string
  name: string
  description: string
  imageUrl: string
  websiteUrl: string
  composeTemplate: string
  envVarsSchema: EnvVarSchema[]
  isActive: boolean
  /** Variables que no se piden en modo BBDD compartida (las usa solo el SQL del compose). */
  sharedSqlHiddenEnvVars?: string[]
}

export type ProductInput = Omit<Product, "id" | "sharedSqlHiddenEnvVars">

export interface ProductExportItem {
  name: string
  description: string
  imageUrl: string
  websiteUrl: string
  composeTemplate: string
  envVarsSchema: EnvVarSchema[]
  isActive: boolean
}

export interface ProductExportEnvelope {
  version: number
  exportedAt: string
  products: ProductExportItem[]
}

export interface ImportedProductInfo {
  name: string
  id: string
}

export interface ImportProductError {
  name: string
  reason: string
}

export interface ImportProductsResult {
  created: ImportedProductInfo[]
  updated: ImportedProductInfo[]
  errors: ImportProductError[]
}

export type ApplicationStatus =
  | "Deploying"
  | "Provisioning"
  | "Starting"
  | "Running"
  | "Stopped"
  | "Error"
  | "Deleted"

export interface Application {
  id: string
  productId: string
  productName: string
  userId: string
  ownerName: string
  ownerClientSlug: string
  subdomain: string
  fullDomain: string
  url: string
  envVarValues: Record<string, string>
  status: ApplicationStatus
  portainerStackId: number | null
  portainerEndpointId: number
  createdAt: string
  updatedAt: string
  /** Mientras arranca: por dónde va (solo en la respuesta del refresco de estado). */
  startup?: StartupProgress | null
}

/** Paso del arranque; step/stepCount, si los hay, son para una barra "paso 3 de 6". */
export interface StartupProgress {
  title: string
  detail: string | null
  step: number | null
  stepCount: number | null
  isError: boolean
}

export interface DeploySuggestion {
  suggestedSubdomain: string
}

export interface DeployStats {
  averageDeploySeconds: number | null
  sampleCount: number
}

export interface CreateApplicationInput {
  productId: string
  subdomain: string
  envVars: Record<string, string>
  adminPassword?: string
  /** Solo administradores: cliente al que se instala (por defecto, el propio administrador). */
  userId?: string
  /** Solo administradores. */
  sqlMode?: AppsSqlMode
}

/** La máquina Docker: uso leído de su kernel (null si no se pudo leer) y datos de Docker. */
export interface DockerHost {
  usage: {
    cpuPercent: number | null
    loadAverage: number[] | null
    memoryTotalBytes: number | null
    memoryUsedBytes: number | null
    diskTotalBytes: number | null
    diskUsedBytes: number | null
    uptimeSeconds: number | null
  }
  docker: {
    cpus: number
    memoryBytes: number
    operatingSystem: string
    kernelVersion: string
    dockerVersion: string
    containers: number
    containersRunning: number
    images: number
    imagesBytes: number
    volumesBytes: number
    containersBytes: number
    buildCacheBytes: number
  } | null
}

export interface ApplicationUsage {
  applicationId: string
  subdomain: string
  productName: string
  userId: string
  ownerName: string
  ownerClientSlug: string
  cpuPercent: number
  memoryUsageBytes: number
  diskUsageBytes: number
  containerCount: number
}
