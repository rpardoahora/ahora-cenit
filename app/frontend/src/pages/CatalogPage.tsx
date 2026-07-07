import { useEffect, useState } from "react"
import { useNavigate } from "react-router-dom"
import { productsApi } from "@/lib/api"
import { useAuth } from "@/context/AuthContext"
import type { Product } from "@/types"
import {
  Card,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Button } from "@/components/ui/button"
import { Skeleton } from "@/components/ui/skeleton"

function openWebsitePopup(url: string) {
  window.open(url, "_blank", "noopener,noreferrer,width=1100,height=800")
}

export function CatalogPage() {
  const { user } = useAuth()
  const navigate = useNavigate()
  const [products, setProducts] = useState<Product[] | null>(null)

  useEffect(() => {
    productsApi
      .list()
      .then(setProducts)
      .catch(() => setProducts([]))
  }, [])

  function handleInstall(productId: string) {
    if (!user) {
      navigate("/registro")
      return
    }
    navigate(`/desplegar/${productId}`)
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="font-heading text-2xl font-semibold">Catálogo de aplicaciones</h1>
        <p className="text-sm text-muted-foreground">
          Elige una aplicación y despliégala en tu propio subdominio en segundos.
        </p>
      </div>

      {products === null && (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 6 }).map((_, i) => (
            <Skeleton key={i} className="h-64 w-full" />
          ))}
        </div>
      )}

      {products?.length === 0 && (
        <p className="text-sm text-muted-foreground">
          No hay aplicaciones disponibles en este momento.
        </p>
      )}

      {products && products.length > 0 && (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {products.map((product) => (
            <Card key={product.id} className="h-full overflow-hidden">
              <div className="flex h-40 w-full items-center justify-center bg-muted/30 p-6">
                <img
                  src={product.imageUrl}
                  alt={product.name}
                  className="max-h-full max-w-full object-contain"
                />
              </div>
              <CardHeader className="flex-1">
                <CardTitle>{product.name}</CardTitle>
                <CardDescription className="line-clamp-3">
                  {product.description}
                </CardDescription>
              </CardHeader>
              <CardFooter className="gap-2">
                {product.websiteUrl && (
                  <Button
                    variant="outline"
                    className="flex-1"
                    onClick={() => openWebsitePopup(product.websiteUrl)}
                  >
                    Más info
                  </Button>
                )}
                <Button className="flex-1" onClick={() => handleInstall(product.id)}>
                  Instalar
                </Button>
              </CardFooter>
            </Card>
          ))}
        </div>
      )}
    </div>
  )
}
