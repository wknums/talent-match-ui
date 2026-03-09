import { useState, useRef } from 'react'
import {
  DraggableResizableDialog,
  DraggableDialogHeader,
  DraggableDialogBody,
  DraggableDialogFooter,
} from '@/components/DraggableResizableDialog'
import { DialogTitle, DialogDescription } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { UploadSimple, File, X, Sparkle, WarningCircle } from '@phosphor-icons/react'
import { api } from '@/lib/api'
import { toast } from 'sonner'
import type { RubricCategory } from '@/types'

interface UploadRubricDialogProps {
  open: boolean
  jobId: string | null
  jobTitle?: string
  onClose: () => void
  onSuccess?: (rubric: RubricCategory[]) => void
}

export function UploadRubricDialog({ open, jobId, jobTitle, onClose, onSuccess }: UploadRubricDialogProps) {
  const [uploadedFile, setUploadedFile] = useState<File | null>(null)
  const [processing, setProcessing] = useState(false)
  const [extractedRubric, setExtractedRubric] = useState<RubricCategory[] | null>(null)
  const [titleMismatch, setTitleMismatch] = useState<string | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)

  const handleFileSelect = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return

    const validExtensions = ['.pdf', '.jpg', '.jpeg', '.md', '.txt', '.docx']
    const fileExtension = file.name.toLowerCase().slice(file.name.lastIndexOf('.'))

    if (!validExtensions.includes(fileExtension)) {
      toast.error('Please upload a PDF, JPG, Markdown (.md), TXT, or DOCX file')
      return
    }

    setUploadedFile(file)
    setProcessing(true)
    setTitleMismatch(null)

    try {
      const reader = new FileReader()
      const base64Content = await new Promise<string>((resolve, reject) => {
        reader.onload = () => {
          const result = reader.result as string
          resolve(result.split(',')[1] || result)
        }
        reader.onerror = reject
        reader.readAsDataURL(file)
      })

      const extracted = await api.extractRubric(file.name, base64Content, file.type)

      // Check title mismatch
      if (extracted.title && jobTitle && extracted.title.toLowerCase() !== jobTitle.toLowerCase()) {
        setTitleMismatch(
          `The rubric document title "${extracted.title}" does not match the job title "${jobTitle}". Please correct and re-upload the rubric document. The existing rubric will be discarded.`
        )
        setUploadedFile(null)
        setExtractedRubric(null)
        if (fileInputRef.current) fileInputRef.current.value = ''
        return
      }

      if (extracted.categories && Array.isArray(extracted.categories)) {
        const rubric: RubricCategory[] = extracted.categories.map((c: any, i: number) => ({
          id: `rubric-${Date.now()}-${i}`,
          name: c.name || '',
          description: c.description || '',
          weight: c.weight || 0,
        }))

        const totalWeight = rubric.reduce((sum, c) => sum + c.weight, 0)
        if (Math.abs(totalWeight - 1.0) > 0.01) {
          rubric.forEach(c => c.weight = c.weight / totalWeight)
        }

        setExtractedRubric(rubric)
        toast.success('Rubric extracted successfully')
      } else {
        throw new Error('Invalid rubric format')
      }
    } catch (error) {
      toast.error('Failed to process rubric document')
      console.error('Error processing file:', error)
    } finally {
      setProcessing(false)
    }
  }

  const removeFile = () => {
    setUploadedFile(null)
    setExtractedRubric(null)
    setTitleMismatch(null)
    if (fileInputRef.current) {
      fileInputRef.current.value = ''
    }
  }

  const handleSubmit = () => {
    if (extractedRubric) {
      onSuccess?.(extractedRubric)
      toast.success('Rubric uploaded successfully')
      handleClose()
    }
  }

  const handleClose = () => {
    removeFile()
    onClose()
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
        <DialogTitle>Upload Scoring Rubric</DialogTitle>
        <DialogDescription>
          Upload a document containing the scoring rubric for this job position
        </DialogDescription>
      </DraggableDialogHeader>

      <DraggableDialogBody className="px-6">
        <div className="space-y-4">
          <div className="border-2 border-dashed border-border rounded-lg p-8">
            <input
              ref={fileInputRef}
              type="file"
              accept=".pdf,.jpg,.jpeg,.md,.txt,.docx"
              onChange={handleFileSelect}
              className="hidden"
              id="rubric-upload"
            />
            {!uploadedFile ? (
              <div className="space-y-4 text-center">
                <div className="flex justify-center">
                  <UploadSimple size={48} className="text-muted-foreground" />
                </div>
                <div>
                  <p className="text-sm font-medium mb-1">Upload Rubric Document</p>
                  <p className="text-xs text-muted-foreground">
                    Supported formats: PDF, JPG, Markdown (.md), TXT, DOCX
                  </p>
                </div>
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => fileInputRef.current?.click()}
                >
                  <UploadSimple size={16} />
                  Choose File
                </Button>
              </div>
            ) : (
              <div className="space-y-4">
                <div className="flex items-center justify-between">
                  <div className="flex items-center gap-3">
                    <File size={32} className="text-accent" />
                    <div>
                      <p className="text-sm font-medium">{uploadedFile.name}</p>
                      <p className="text-xs text-muted-foreground">
                        {(uploadedFile.size / 1024).toFixed(1)} KB
                      </p>
                    </div>
                  </div>
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={removeFile}
                    disabled={processing}
                  >
                    <X size={16} />
                  </Button>
                </div>
                {processing && (
                  <div className="flex items-center justify-center gap-2 text-sm text-accent">
                    <Sparkle size={16} className="animate-pulse" />
                    Extracting rubric categories...
                  </div>
                )}
              </div>
            )}
          </div>

          {titleMismatch && (
            <div className="flex items-start gap-2 p-3 rounded-md bg-destructive/10 text-destructive text-sm">
              <WarningCircle size={20} className="shrink-0 mt-0.5" />
              <p>{titleMismatch}</p>
            </div>
          )}

          {extractedRubric && (
            <Card className="p-4 bg-muted/50">
              <h4 className="font-medium mb-3">Extracted Rubric Categories</h4>
              <div className="space-y-2">
                {extractedRubric.map((category) => (
                  <div key={category.id} className="flex items-start gap-3 text-sm">
                    <div className="font-mono font-medium text-accent min-w-[60px]">
                      {(category.weight * 100).toFixed(0)}%
                    </div>
                    <div className="flex-1">
                      <p className="font-medium">{category.name}</p>
                      <p className="text-xs text-muted-foreground">{category.description}</p>
                    </div>
                  </div>
                ))}
              </div>
            </Card>
          )}
        </div>
      </DraggableDialogBody>

      <DraggableDialogFooter>
        <Button variant="outline" onClick={handleClose}>
          Cancel
        </Button>
        <Button onClick={handleSubmit} disabled={!extractedRubric}>
          Upload Rubric
        </Button>
      </DraggableDialogFooter>
    </DraggableResizableDialog>
  )
}
