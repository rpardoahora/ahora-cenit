import { BrowserRouter, Route, Routes } from "react-router-dom"
import { AuthProvider } from "@/context/AuthContext"
import { Toaster } from "@/components/ui/sonner"
import { Layout } from "@/components/layout/Layout"
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
import { ForgotPasswordPage } from "@/pages/ForgotPasswordPage"
import { ResetPasswordPage } from "@/pages/ResetPasswordPage"
import { ConfirmEmailPage } from "@/pages/ConfirmEmailPage"

function App() {
  return (
    <BrowserRouter>
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
              <Route path="admin/productos" element={<ProductsListPage />} />
              <Route path="admin/productos/nuevo" element={<ProductFormPage />} />
              <Route path="admin/productos/:id/editar" element={<ProductFormPage />} />
            </Route>
          </Route>
        </Routes>
        <Toaster />
      </AuthProvider>
    </BrowserRouter>
  )
}

export default App
