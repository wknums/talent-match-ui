import { useState, useCallback } from 'react'
import {
  DraggableResizableDialog,
  DraggableDialogHeader,
  DraggableDialogBody,
  DraggableDialogFooter,
} from '@/components/DraggableResizableDialog'
import { DialogTitle, DialogDescription } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Progress } from '@/components/ui/progress'
import { UploadSimple, File, CheckCircle, X } from '@phosphor-icons/react'
import { api } from '@/lib/api'
import { toast } from 'sonner'
import { cn } from '@/lib/utils'

interface UploadApplicationsDialogProps {
  open: boolean
  jobId: string | null
  jobTitle?: string
  onClose: () => void
  onSuccess?: () => void
}

export function UploadApplicationsDialog({
  open,
  jobId,
  jobTitle,
  onClose,
  onSuccess,
}: UploadApplicationsDialogProps) {
  const ALLOWED_EXTENSIONS = ['.pdf', '.md', '.docx', '.txt', '.jpg', '.png']
  const MAX_FILE_SIZE = 15 * 1024 * 1024

  const [files, setFiles] = useState<File[]>([])
  const [uploading, setUploading] = useState(false)
  const [uploadProgress, setUploadProgress] = useState(0)
  const [dragActive, setDragActive] = useState(false)

  const validateFile = useCallback(
    (file: File): string | null => {
      const extension = `.${file.name.split('.').pop()?.toLowerCase() ?? ''}`
      if (!ALLOWED_EXTENSIONS.includes(extension)) {
        return `Unsupported file type for ${file.name}`
      }
      if (file.size > MAX_FILE_SIZE) {
        return `${file.name} exceeds the 15 MB limit`
      }
      return null
    },
    [ALLOWED_EXTENSIONS]
  )

  const addFiles = useCallback(
    (incomingFiles: File[]) => {
      const accepted: File[] = []
      const rejected: string[] = []

      incomingFiles.forEach((file) => {
        const validationError = validateFile(file)
        if (validationError) {
          rejected.push(validationError)
        } else {
          accepted.push(file)
        }
      })

      if (accepted.length > 0) {
        setFiles((prev) => [...prev, ...accepted])
      }

      if (rejected.length > 0) {
        toast.error(rejected.join('; '))
      }
    },
    [validateFile]
  )

  const handleDrag = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    e.stopPropagation()
    if (e.type === 'dragenter' || e.type === 'dragover') {
      setDragActive(true)
    } else if (e.type === 'dragleave') {
      setDragActive(false)
    }
  }, [])

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    e.stopPropagation()
    setDragActive(false)

    const droppedFiles = Array.from(e.dataTransfer.files)
    addFiles(droppedFiles)
  }, [addFiles])

  const handleFileInput = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files) {
      const selectedFiles = Array.from(e.target.files)
      addFiles(selectedFiles)
    }
  }

  const removeFile = (index: number) => {
    setFiles(files.filter((_, i) => i !== index))
  }

  const handleUpload = async () => {
    if (!jobId || files.length === 0) return

    setUploading(true)
    setUploadProgress(0)

    try {
      const progressInterval = setInterval(() => {
        setUploadProgress((prev) => Math.min(prev + 10, 90))
      }, 200)

      await api.uploadApplications(jobId, files)

      clearInterval(progressInterval)
      setUploadProgress(100)

      toast.success(`Successfully uploaded ${files.length} application(s)`)
      setTimeout(() => {
        onSuccess?.()
        onClose()
        resetDialog()
      }, 500)
    } catch (error) {
      toast.error('Failed to upload applications')
      setUploadProgress(0)
    } finally {
      setUploading(false)
    }
  }

  const resetDialog = () => {
    setFiles([])
    setUploadProgress(0)
    setDragActive(false)
  }

  const handleClose = () => {
    if (!uploading) {
      onClose()
      resetDialog()
    }
  }

  const formatFileSize = (bytes: number) => {
    if (bytes < 1024) return bytes + ' B'
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB'
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB'
  }

  return (
    <DraggableResizableDialog
      open={open}
      onOpenChange={handleClose}
      defaultWidth={700}
      defaultHeight={600}
      minWidth={500}
      minHeight={400}
    >
      <DraggableDialogHeader>
        <DialogTitle>Upload Applications</DialogTitle>
        <DialogDescription>
          {jobTitle ? `Upload application documents for ${jobTitle}` : 'Upload application documents'}
        </DialogDescription>
      </DraggableDialogHeader>

      <DraggableDialogBody className="px-6">
        <div className="space-y-4">
          <Card
            className={cn(
              'border-2 border-dashed transition-colors cursor-pointer',
              dragActive && 'border-accent bg-accent/10',
              !dragActive && 'border-muted hover:border-accent/50'
            )}
            onDragEnter={handleDrag}
            onDragLeave={handleDrag}
            onDragOver={handleDrag}
            onDrop={handleDrop}
          >
            <CardContent className="p-12 text-center">
              <input
                type="file"
                id="file-upload"
                multiple
                accept=".pdf,.md,.docx,.txt,.jpg,.png"
                onChange={handleFileInput}
                className="hidden"
              />
              <label htmlFor="file-upload" className="cursor-pointer">
                <div className="flex flex-col items-center gap-4">
                  <UploadSimple size={48} className="text-accent" />
                  <div>
                    <p className="font-medium mb-1">Drop files here or click to browse</p>
                    <p className="text-sm text-muted-foreground">
                      Supports PDF, MD, DOCX, TXT, JPG, and PNG files (max 15 MB each)
                    </p>
                  </div>
                </div>
              </label>
            </CardContent>
          </Card>

          {files.length > 0 && (
            <div className="space-y-2">
              <p className="text-sm font-medium">{files.length} file(s) selected</p>
              <div className="max-h-64 overflow-y-auto space-y-2">
                {files.map((file, index) => (
                  <Card key={index}>
                    <CardContent className="p-3">
                      <div className="flex items-center gap-3">
                        <File size={24} className="text-accent" />
                        <div className="flex-1 min-w-0">
                          <p className="text-sm font-medium truncate">{file.name}</p>
                          <p className="text-xs text-muted-foreground">
                            {formatFileSize(file.size)}
                          </p>
                        </div>
                        {!uploading && (
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => removeFile(index)}
                          >
                            <X size={16} />
                          </Button>
                        )}
                        {uploading && uploadProgress === 100 && (
                          <CheckCircle size={24} className="text-success" weight="fill" />
                        )}
                      </div>
                    </CardContent>
                  </Card>
                ))}
              </div>
            </div>
          )}

          {uploading && (
            <div className="space-y-2">
              <div className="flex items-center justify-between text-sm">
                <span className="text-muted-foreground">Uploading...</span>
                <span className="font-mono font-medium">{uploadProgress}%</span>
              </div>
              <Progress value={uploadProgress} className="h-2" />
            </div>
          )}
        </div>
      </DraggableDialogBody>

      <DraggableDialogFooter>
        <Button variant="outline" onClick={handleClose} disabled={uploading}>
          Cancel
        </Button>
        <Button onClick={handleUpload} disabled={files.length === 0 || uploading}>
          {uploading ? 'Uploading...' : `Upload ${files.length} file(s)`}
        </Button>
      </DraggableDialogFooter>
    </DraggableResizableDialog>
  )
}
