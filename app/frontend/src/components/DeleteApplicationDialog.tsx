import type { ReactNode } from "react"
import { ConfirmDialog } from "@/components/ConfirmDialog"
import { useConfig } from "@/context/ConfigContext"

interface DeleteApplicationDialogProps {
  trigger: ReactNode
  subdomain: string
  onConfirm: () => void | Promise<void>
}

/** Confirmación para eliminar una instancia: va a la papelera (parada) y se borra del todo pasados unos días. */
export function DeleteApplicationDialog({ trigger, subdomain, onConfirm }: DeleteApplicationDialogProps) {
  const { trashRetentionDays } = useConfig()

  return (
    <ConfirmDialog
      trigger={trigger}
      title="Eliminar aplicación"
      description={
        `"${subdomain}" se parará y pasará a la papelera. Dentro de ${trashRetentionDays} días se borrará ` +
        "del todo, con sus datos (volúmenes y bases de datos). Hasta entonces un administrador puede restaurarla, " +
        "y su nombre no se puede reutilizar."
      }
      confirmLabel="Mover a la papelera"
      destructive
      onConfirm={onConfirm}
    />
  )
}
