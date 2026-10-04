import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom"
import { AuthProvider } from "@/context/AuthContext"
import { Toaster } from "@/components/ui/sonner"
import { Layout } from "@/components/layout/Layout"
import { AdminLayout } from "@/components/layout/AdminLayout"
import { RequireAuth } from "@/routes/RequireAuth"
import { RequireAdmin } from "@/routes/RequireAdmin"
import { CatalogPage } from "@/pages/CatalogPage"
import { LoginPage } from "@/pages/LoginPage"
import { RegisterPage } from "@/pages/RegisterPage"
import { DeployPage } from "@/pages/DeployPage"
import { ApplicationsListPage } from "@/pages/ApplicationsListPage"
import { ApplicationDetailPage } from "@/pages/ApplicationDetailPage"
import { ProductsListPage } from "@/pages/admin/ProductsListPage"
import { ProductFormPage } from "@/pages/admin/ProductFormPage"
import { UsersListPage } from "@/pages/admin/UsersListPage"
import { UserFormPage } from "@/pages/admin/UserFormPage"
import { UsageDashboardPage } from "@/pages/admin/UsageDashboardPage"
import { AdminToolsPage } from "@/pages/admin/AdminToolsPage"
import { AdminSettingsPage } from "@/pages/admin/AdminSettingsPage"
import { ConfigProvider } from "@/context/ConfigContext"
import { ForgotPasswordPage } from "@/pages/ForgotPasswordPage"
import { ResetPasswordPage } from "@/pages/ResetPasswordPage"
import { ConfirmEmailPage } from "@/pages/ConfirmEmailPage"

function App() {
  return (
    <BrowserRouter>
      <ConfigProvider>
      <AuthProvider>
        <Routes>
          <Route element={<Layout />}>
            <Route index element={<CatalogPage />} />
            <Route path="login" element={<LoginPage />} />
            <Route path="registro" element={<RegisterPage />} />
            <Route path="olvide-password" element={<ForgotPasswordPage />} />
            <Route path="restablecer-password" element={<ResetPasswordPage />} />
            <Route path="confirmar-email" element={<ConfirmEmailPage />} />

            <Route element={<RequireAuth />}>
              <Route path="desplegar/:productId" element={<DeployPage />} />
              <Route path="aplicaciones" element={<ApplicationsListPage />} />
              <Route path="aplicaciones/:id" element={<ApplicationDetailPage />} />
            </Route>

            <Route element={<RequireAdmin />}>
              <Route path="admin" element={<AdminLayout />}>
                <Route index element={<Navigate to="productos" replace />} />
                <Route path="productos" element={<ProductsListPage />} />
                <Route path="productos/nuevo" element={<ProductFormPage />} />
                <Route path="productos/:id/editar" element={<ProductFormPage />} />
                <Route path="usuarios" element={<UsersListPage />} />
                <Route path="usuarios/nuevo" element={<UserFormPage />} />
                <Route path="usuarios/:id/editar" element={<UserFormPage />} />
                <Route path="consumo" element={<UsageDashboardPage />} />
                <Route path="herramientas" element={<AdminToolsPage />} />
                <Route path="ajustes" element={<AdminSettingsPage />} />
              </Route>
            </Route>
          </Route>
        </Routes>
        <Toaster />
      </AuthProvider>
      </ConfigProvider>
    </BrowserRouter>
  )
}

export default App
