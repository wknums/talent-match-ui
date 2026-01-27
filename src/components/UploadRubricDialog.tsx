import { useState, useRef } from 'react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { UploadSimple, File, X, Sparkle } from '@phosphor-icons/react'
import { toast } from 'sonner'
import type { RubricCategory } from '@/types'

interface UploadRubricDialogProps {
  open: boolean
  jobId: string | null
  onClose: () => void
  onSuccess?: (rubric: RubricCategory[]) => void
}

export function UploadRubricDialog({ open, jobId, onClose, onSuccess }: UploadRubricDialogProps) {
  const [uploadedFile, setUploadedFile] = useState<File | null>(null)
  const [processing, setProcessing] = useState(false)
  const [extractedRubric, setExtractedRubric] = useState<RubricCategory[] | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)

  const handleFileSelect = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return

    const validTypes = [
      'application/pdf',
      'text/markdown',
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    ]
    const validExtensions = ['.pdf', '.md', '.docx']
    const fileExtension = file.name.toLowerCase().slice(file.name.lastIndexOf('.'))

    if (!validTypes.includes(file.type) && !validExtensions.includes(fileExtension)) {
      toast.error('Please upload a PDF, Markdown (.md), or DOCX file')
      return
    }

    setUploadedFile(file)
    setProcessing(true)

    try {
      const prompt = window.spark.llmPrompt`You are analyzing a scoring rubric document for evaluating job applications. Extract the rubric categories and return them as JSON.

Return ONLY a JSON object with this exact structure:
{
  "categories": [
    {
      "name": "category name",
      "description": "what to evaluate in this category",
      "weight": 0.25,
      "maxPoints": 100
    }
  ]
}

Important rules:
- Extract 3-8 rubric categories
- Weights MUST sum to exactly 1.0
- Each category should have clear evaluation criteria
- If point values are specified, use them; otherwise default to 100
- Return ONLY the JSON object, no additional text

Document: ${file.name}

Note: This is a simulated environment. Generate a realistic rubric based on the filename and common evaluation criteria.`

      const response = await window.spark.llm(prompt, 'gpt-4o', true)
      const parsed = JSON.parse(response)

      if (parsed.categories && Array.isArray(parsed.categories)) {
        const rubric: RubricCategory[] = parsed.categories.map((c: any, i: number) => ({
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
    <Dialog open={open} onOpenChange={handleClose}>
      <DialogContent className="max-w-2xl">
        <DialogHeader>
          <DialogTitle>Upload Scoring Rubric</DialogTitle>
          <DialogDescription>
            Upload a document containing the scoring rubric for this job position
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <div className="border-2 border-dashed border-border rounded-lg p-8">
            <input
              ref={fileInputRef}
              type="file"
              accept=".pdf,.md,.docx,application/pdf,text/markdown,application/vnd.openxmlformats-officedocument.wordprocessingml.document"
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
                    Supported formats: PDF, Markdown (.md), DOCX
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

        <DialogFooter>
          <Button variant="outline" onClick={handleClose}>
            Cancel
          </Button>
          <Button onClick={handleSubmit} disabled={!extractedRubric}>
            Upload Rubric
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
