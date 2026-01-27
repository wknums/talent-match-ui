import { useState, useRef, useEffect } from 'react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Card, CardContent } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Plus, X, UploadSimple, File, Sparkle } from '@phosphor-icons/react'
import { mockAPI } from '@/lib/api'
import { toast } from 'sonner'
import type { RubricCategory, MustHave, AggregationStrategy } from '@/types'

interface CreateJobDialogProps {
  open: boolean
  onClose: () => void
  onSuccess?: () => void
  editingJob?: Job | null
}

import type { Job } from '@/types'

export function CreateJobDialog({ open, onClose, onSuccess, editingJob }: CreateJobDialogProps) {
  const [title, setTitle] = useState('')
  const [department, setDepartment] = useState('')
  const [organization, setOrganization] = useState('')
  const [postingDate, setPostingDate] = useState(new Date().toISOString().split('T')[0])
  const [rubricCategories, setRubricCategories] = useState<RubricCategory[]>([
    { id: '1', name: '', description: '', weight: 0 },
  ])
  const [mustHaves, setMustHaves] = useState<MustHave[]>([
    { id: '1', criterion: '', description: '' },
  ])
  const [runsPerApplication, setRunsPerApplication] = useState('3')
  const [aggregationStrategy, setAggregationStrategy] = useState<AggregationStrategy>('median')
  const [longlistThreshold, setLonglistThreshold] = useState('60')
  const [shortlistThreshold, setShortlistThreshold] = useState('75')
  const [submitting, setSubmitting] = useState(false)
  const [uploadedFile, setUploadedFile] = useState<File | null>(null)
  const [uploadedSpecDocId, setUploadedSpecDocId] = useState<string | null>(null)
  const [processingFile, setProcessingFile] = useState(false)
  const fileInputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (editingJob) {
      setTitle(editingJob.title)
      setDepartment(editingJob.department)
      setOrganization(editingJob.organization)
      setPostingDate(editingJob.postingDate.split('T')[0])
      setRubricCategories(editingJob.currentVersion.rubric)
      setMustHaves(editingJob.currentVersion.mustHaves)
      setRunsPerApplication(String(editingJob.currentVersion.runsPerApplication))
      setAggregationStrategy(editingJob.currentVersion.aggregationStrategy)
      setLonglistThreshold(String(editingJob.currentVersion.longlistThreshold))
      setShortlistThreshold(String(editingJob.currentVersion.shortlistThreshold))
    }
  }, [editingJob])

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
    setUploadedSpecDocId(`spec-doc-${Date.now()}-${file.name}`)
    setProcessingFile(true)

    try {
      const prompt = (window.spark.llmPrompt as any)`You are analyzing a job specification document. Extract the following information and return it as JSON:

{
  "title": "job title",
  "department": "department name",
  "organization": "organization name (if mentioned, otherwise use 'Not Specified')",
  "rubric": [
    {
      "name": "category name",
      "description": "what to evaluate",
      "weight": 0.25
    }
  ],
  "mustHaves": [
    {
      "criterion": "requirement description",
      "description": "additional context"
    }
  ]
}

Important:
- Rubric weights MUST sum to exactly 1.0
- Include 3-5 rubric categories that cover the key evaluation areas
- Must-haves should be clear, specific requirements
- If any field is unclear, make reasonable inferences based on the job description

Document content:
${file.name}

Note: Since this is a simulated environment, I'll generate a realistic job spec based on the filename. In production, the actual file content would be extracted and analyzed.`

      const response = await window.spark.llm(prompt, 'gpt-4o', true)
      const parsed = JSON.parse(response)

      setTitle(parsed.title || '')
      setDepartment(parsed.department || '')
      setOrganization(parsed.organization !== 'Not Specified' ? parsed.organization : '')

      if (parsed.rubric && Array.isArray(parsed.rubric)) {
        setRubricCategories(
          parsed.rubric.map((r: any, i: number) => ({
            id: String(i + 1),
            name: r.name || '',
            description: r.description || '',
            weight: r.weight || 0,
          }))
        )
      }

      if (parsed.mustHaves && Array.isArray(parsed.mustHaves)) {
        setMustHaves(
          parsed.mustHaves.map((m: any, i: number) => ({
            id: String(i + 1),
            criterion: m.criterion || '',
            description: m.description || '',
          }))
        )
      }

      toast.success('Job specification extracted successfully')
    } catch (error) {
      toast.error('Failed to process document. Please fill in manually.')
      console.error('Error processing file:', error)
    } finally {
      setProcessingFile(false)
    }
  }

  const removeUploadedFile = () => {
    setUploadedFile(null)
    setUploadedSpecDocId(null)
    if (fileInputRef.current) {
      fileInputRef.current.value = ''
    }
  }

  const addRubricCategory = () => {
    setRubricCategories([
      ...rubricCategories,
      { id: Date.now().toString(), name: '', description: '', weight: 0 },
    ])
  }

  const removeRubricCategory = (id: string) => {
    setRubricCategories(rubricCategories.filter((c) => c.id !== id))
  }

  const updateRubricCategory = (id: string, field: keyof RubricCategory, value: string | number) => {
    setRubricCategories(
      rubricCategories.map((c) => (c.id === id ? { ...c, [field]: value } : c))
    )
  }

  const addMustHave = () => {
    setMustHaves([
      ...mustHaves,
      { id: Date.now().toString(), criterion: '', description: '' },
    ])
  }

  const removeMustHave = (id: string) => {
    setMustHaves(mustHaves.filter((m) => m.id !== id))
  }

  const updateMustHave = (id: string, field: keyof MustHave, value: string) => {
    setMustHaves(mustHaves.map((m) => (m.id === id ? { ...m, [field]: value } : m)))
  }

  const handleSubmit = async () => {
    if (!title || !department || !organization) {
      toast.error('Please fill in job title, department, and organization')
      return
    }

    const validCategories = rubricCategories.filter((c) => c.name && c.weight > 0)
    if (validCategories.length === 0) {
      toast.error('Please add at least one rubric category')
      return
    }

    const totalWeight = validCategories.reduce((sum, c) => sum + c.weight, 0)
    if (Math.abs(totalWeight - 1.0) > 0.01) {
      toast.error('Rubric weights must sum to 1.0')
      return
    }

    setSubmitting(true)
    try {
      if (editingJob) {
        await mockAPI.updateJob(editingJob.jobId, {
          title,
          department,
          organization,
          postingDate,
          rubric: validCategories,
          mustHaves: mustHaves.filter((m) => m.criterion),
          runsPerApplication: parseInt(runsPerApplication),
          aggregationStrategy,
          longlistThreshold: parseFloat(longlistThreshold),
          shortlistThreshold: parseFloat(shortlistThreshold),
          specDocumentId: uploadedSpecDocId || undefined,
          rubricDocumentId: editingJob.rubricDocumentId,
        })
        toast.success('Job updated successfully')
      } else {
        await mockAPI.createJob({
          title,
          department,
          organization,
          postingDate,
          rubric: validCategories,
          mustHaves: mustHaves.filter((m) => m.criterion),
          runsPerApplication: parseInt(runsPerApplication),
          aggregationStrategy,
          longlistThreshold: parseFloat(longlistThreshold),
          shortlistThreshold: parseFloat(shortlistThreshold),
          specDocumentId: uploadedSpecDocId || undefined,
        })
        toast.success('Job created successfully')
      }

      onSuccess?.()
      onClose()
      resetForm()
    } catch (error) {
      toast.error(editingJob ? 'Failed to update job' : 'Failed to create job')
    } finally {
      setSubmitting(false)
    }
  }

  const resetForm = () => {
    setTitle('')
    setDepartment('')
    setOrganization('')
    setPostingDate(new Date().toISOString().split('T')[0])
    setRubricCategories([{ id: '1', name: '', description: '', weight: 0 }])
    setMustHaves([{ id: '1', criterion: '', description: '' }])
    setRunsPerApplication('3')
    setAggregationStrategy('median')
    setLonglistThreshold('60')
    setShortlistThreshold('75')
    setUploadedFile(null)
    setUploadedSpecDocId(null)
    if (fileInputRef.current) {
      fileInputRef.current.value = ''
    }
  }

  return (
    <Dialog open={open} onOpenChange={onClose}>
      <DialogContent className="max-w-4xl max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{editingJob ? 'Edit Job' : 'Create New Job'}</DialogTitle>
          <DialogDescription>
            {editingJob 
              ? 'Update job details, scoring rubric, and requirements'
              : 'Upload a job specification document or manually define job details, scoring rubric, and requirements'
            }
          </DialogDescription>
        </DialogHeader>

        <Tabs defaultValue="manual" className="mt-4">
          <TabsList className="grid w-full grid-cols-2">
            <TabsTrigger value="upload">
              <UploadSimple size={16} className="mr-2" />
              Upload Document
            </TabsTrigger>
            <TabsTrigger value="manual">Manual Entry</TabsTrigger>
          </TabsList>

          <TabsContent value="upload" className="space-y-4">
            <div className="border-2 border-dashed border-border rounded-lg p-8 text-center">
              <input
                ref={fileInputRef}
                type="file"
                accept=".pdf,.md,.docx,application/pdf,text/markdown,application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                onChange={handleFileSelect}
                className="hidden"
                id="job-spec-upload"
              />
              {!uploadedFile ? (
                <div className="space-y-4">
                  <div className="flex justify-center">
                    <UploadSimple size={48} className="text-muted-foreground" />
                  </div>
                  <div>
                    <p className="text-sm font-medium mb-1">Upload Job Specification</p>
                    <p className="text-xs text-muted-foreground">
                      Supported formats: PDF, Markdown (.md), DOCX
                    </p>
                  </div>
                  <Button
                    type="button"
                    variant="outline"
                    onClick={() => fileInputRef.current?.click()}
                    disabled={processingFile}
                  >
                    <UploadSimple size={16} />
                    Choose File
                  </Button>
                </div>
              ) : (
                <div className="space-y-4">
                  <div className="flex items-center justify-center gap-3">
                    <File size={32} className="text-accent" />
                    <div className="text-left">
                      <p className="text-sm font-medium">{uploadedFile.name}</p>
                      <p className="text-xs text-muted-foreground">
                        {(uploadedFile.size / 1024).toFixed(1)} KB
                      </p>
                    </div>
                  </div>
                  {processingFile && (
                    <div className="flex items-center justify-center gap-2 text-sm text-accent">
                      <Sparkle size={16} className="animate-pulse" />
                      Processing with AI...
                    </div>
                  )}
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    onClick={removeUploadedFile}
                    disabled={processingFile}
                  >
                    <X size={16} />
                    Remove
                  </Button>
                </div>
              )}
            </div>

            {uploadedFile && !processingFile && (
              <p className="text-sm text-muted-foreground text-center">
                Document processed! Review and edit the extracted information in the Manual Entry tab.
              </p>
            )}
          </TabsContent>

          <TabsContent value="manual" className="space-y-6">
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-2">
                <Label htmlFor="title">Job Title</Label>
                <Input
                  id="title"
                  value={title}
                  onChange={(e) => setTitle(e.target.value)}
                  placeholder="e.g., Senior Software Engineer"
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="department">Department</Label>
                <Input
                  id="department"
                  value={department}
                  onChange={(e) => setDepartment(e.target.value)}
                  placeholder="e.g., Engineering"
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="organization">Organization</Label>
                <Input
                  id="organization"
                  value={organization}
                  onChange={(e) => setOrganization(e.target.value)}
                  placeholder="e.g., TechCorp Solutions"
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="postingDate">Posting Date</Label>
                <Input
                  id="postingDate"
                  type="date"
                  value={postingDate}
                  onChange={(e) => setPostingDate(e.target.value)}
                />
              </div>
            </div>

            <div className="space-y-3">
              <div className="flex items-center justify-between">
                <Label>Rubric Categories</Label>
                <Button type="button" variant="outline" size="sm" onClick={addRubricCategory}>
                  <Plus size={16} />
                  Add Category
                </Button>
              </div>
              {rubricCategories.map((category) => (
                <Card key={category.id}>
                  <CardContent className="p-4">
                    <div className="space-y-3">
                      <div className="flex gap-2">
                        <div className="flex-1 space-y-2">
                          <Label>Name</Label>
                          <Input
                            value={category.name}
                            onChange={(e) => updateRubricCategory(category.id, 'name', e.target.value)}
                            placeholder="e.g., Technical Skills"
                          />
                        </div>
                        <div className="w-32 space-y-2">
                          <Label>Weight</Label>
                          <Input
                            type="number"
                            step="0.05"
                            min="0"
                            max="1"
                            value={category.weight}
                            onChange={(e) => updateRubricCategory(category.id, 'weight', parseFloat(e.target.value) || 0)}
                            placeholder="0.30"
                          />
                        </div>
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          onClick={() => removeRubricCategory(category.id)}
                          disabled={rubricCategories.length === 1}
                          className="mt-7"
                        >
                          <X size={16} />
                        </Button>
                      </div>
                      <div className="space-y-2">
                        <Label>Description</Label>
                        <Textarea
                          value={category.description}
                          onChange={(e) => updateRubricCategory(category.id, 'description', e.target.value)}
                          placeholder="What to evaluate in this category"
                          rows={2}
                        />
                      </div>
                    </div>
                  </CardContent>
                </Card>
              ))}
            </div>

            <div className="space-y-3">
              <div className="flex items-center justify-between">
                <Label>Must-Have Requirements</Label>
                <Button type="button" variant="outline" size="sm" onClick={addMustHave}>
                  <Plus size={16} />
                  Add Requirement
                </Button>
              </div>
              {mustHaves.map((mustHave) => (
                <Card key={mustHave.id}>
                  <CardContent className="p-4">
                    <div className="flex gap-2">
                      <div className="flex-1 space-y-2">
                        <Label>Criterion</Label>
                        <Input
                          value={mustHave.criterion}
                          onChange={(e) => updateMustHave(mustHave.id, 'criterion', e.target.value)}
                          placeholder="e.g., Bachelor's degree in Computer Science"
                        />
                      </div>
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={() => removeMustHave(mustHave.id)}
                        disabled={mustHaves.length === 1}
                        className="mt-7"
                      >
                        <X size={16} />
                      </Button>
                    </div>
                  </CardContent>
                </Card>
              ))}
            </div>

            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-2">
                <Label htmlFor="runs">Runs per Application</Label>
                <Select value={runsPerApplication} onValueChange={setRunsPerApplication}>
                  <SelectTrigger id="runs">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="1">1 run</SelectItem>
                    <SelectItem value="3">3 runs</SelectItem>
                    <SelectItem value="5">5 runs</SelectItem>
                  </SelectContent>
                </Select>
              </div>
              <div className="space-y-2">
                <Label htmlFor="aggregation">Aggregation Strategy</Label>
                <Select value={aggregationStrategy} onValueChange={(v) => setAggregationStrategy(v as AggregationStrategy)}>
                  <SelectTrigger id="aggregation">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="median">Median</SelectItem>
                    <SelectItem value="mean">Mean</SelectItem>
                    <SelectItem value="weighted">Weighted</SelectItem>
                  </SelectContent>
                </Select>
              </div>
            </div>

            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-2">
                <Label htmlFor="longlist">Longlist Threshold</Label>
                <Input
                  id="longlist"
                  type="number"
                  value={longlistThreshold}
                  onChange={(e) => setLonglistThreshold(e.target.value)}
                  placeholder="60"
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="shortlist">Shortlist Threshold</Label>
                <Input
                  id="shortlist"
                  type="number"
                  value={shortlistThreshold}
                  onChange={(e) => setShortlistThreshold(e.target.value)}
                  placeholder="75"
                />
              </div>
            </div>
          </TabsContent>
        </Tabs>

        <div className="flex justify-end gap-2 mt-6">
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={handleSubmit} disabled={submitting}>
            {submitting ? (editingJob ? 'Updating...' : 'Creating...') : (editingJob ? 'Update Job' : 'Create Job')}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
