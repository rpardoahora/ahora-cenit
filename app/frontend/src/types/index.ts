export type Role = "Admin" | "Cliente"

export interface User {
  id: string
  name: string
  email: string
  role: Role
  clientSlug: string
  emailConfirmed: boolean
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

export interface EnvVarSchema {
  key: string
  label: string
  defaultValue: string
  isSecret: boolean
}

export interface Product {
  id: string
  name: string
  description: string
  imageUrl: string
  composeTemplate: string
  envVarsSchema: EnvVarSchema[]
  isActive: boolean
}

export type ProductInput = Omit<Product, "id">

export type ApplicationStatus = "Deploying" | "Running" | "Stopped" | "Error" | "Deleted"

export interface Application {
  id: string
  productId: string
  productName: string
  userId: string
  ownerName: string
  ownerClientSlug: string
  subdomain: string
  fullDomain: string
  envVarValues: Record<string, string>
  status: ApplicationStatus
  portainerStackId: number | null
  portainerEndpointId: number
  createdAt: string
  updatedAt: string
}

export interface DeploySuggestion {
  suggestedSubdomain: string
}

export interface CreateApplicationInput {
  productId: string
  subdomain: string
  envVars: Record<string, string>
}
