import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Button } from '@/components/ui/button'

interface DeleteJobDialogProps {
  open: boolean
  jobTitle: string
  applicationCount: number
  isDeleting?: boolean
  onOpenChange: (open: boolean) => void
  onConfirm: () => void
}

export function DeleteJobDialog({
  open,
  jobTitle,
  applicationCount,
  isDeleting = false,
  onOpenChange,
  onConfirm,
}: DeleteJobDialogProps) {
  return (
    <AlertDialog open={open} onOpenChange={(nextOpen) => {
      if (!isDeleting) {
        onOpenChange(nextOpen)
      }
    }}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Delete Job</AlertDialogTitle>
          <AlertDialogDescription>
            Are you sure you want to permanently delete <strong>{jobTitle}</strong>?
          </AlertDialogDescription>
        </AlertDialogHeader>

        {applicationCount > 0 && (
          <div className="rounded-md border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
            This will also permanently delete {applicationCount} application{applicationCount === 1 ? '' : 's'} and all associated scoring data.
          </div>
        )}

        <p className="text-sm text-muted-foreground">This action cannot be undone.</p>

        <AlertDialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={isDeleting}>
            Cancel
          </Button>
          <Button variant="destructive" onClick={onConfirm} disabled={isDeleting}>
            {isDeleting ? 'Deleting...' : 'Delete Job'}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}