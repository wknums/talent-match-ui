import { useState, useRef, useEffect } from 'react'
import {
  DraggableResizableDialog,
  DraggableDialogHeader,
  DraggableDialogBody,
  DraggableDialogFooter,
} from '@/components/DraggableResizableDialog'
import { DialogTitle, DialogDescription } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Card, CardContent } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Plus, X, UploadSimple, File, Sparkle, WarningCircle } from '@phosphor-icons/react'
import { api } from '@/lib/api'
import { toast } from 'sonner'
import type { RubricCategory, MustHave, DesiredCriteria, AggregationStrategy, RubricApprovalStatus, RubricSource } from '@/types'

interface CreateJobDialogProps {
  open: boolean
  onClose: () => void
  onSuccess?: () => void
  editingJob?: Job | null
}

import type { Job } from '@/types'

export function CreateJobDialog({ open, onClose, onSuccess, editingJob }: CreateJobDialogProps) {
  const [title, setTitle] = useState('')
  const [jobCode, setJobCode] = useState('')
  const [department, setDepartment] = useState('')
  const [organization, setOrganization] = useState('')
  const [postingDate, setPostingDate] = useState(new Date().toISOString().split('T')[0])
  const [rubricCategories, setRubricCategories] = useState<RubricCategory[]>([
    { id: '1', name: '', description: '', weight: 0 },
  ])
  const [mustHaves, setMustHaves] = useState<MustHave[]>([
    { id: '1', criterion: '', description: '' },
  ])
  const [desiredCriteria, setDesiredCriteria] = useState<DesiredCriteria[]>([])
  const [jobDescription, setJobDescription] = useState('')
  const [runsPerApplication, setRunsPerApplication] = useState('3')
  const [aggregationStrategy, setAggregationStrategy] = useState<AggregationStrategy>('median')
  const [longlistThreshold, setLonglistThreshold] = useState('60')
  const [shortlistThreshold, setShortlistThreshold] = useState('75')
  const [submitting, setSubmitting] = useState(false)
  const [uploadedFile, setUploadedFile] = useState<File | null>(null)
  const [uploadedSpecDocId, setUploadedSpecDocId] = useState<string | null>(null)
  const [processingFile, setProcessingFile] = useState(false)
  const [rubricUploadedFile, setRubricUploadedFile] = useState<File | null>(null)
  const [processingRubric, setProcessingRubric] = useState(false)
  const [rubricTitleMismatch, setRubricTitleMismatch] = useState<string | null>(null)
  const [rubricApprovalStatus, setRubricApprovalStatus] = useState<RubricApprovalStatus>('approved')
  const [rubricSource, setRubricSource] = useState<RubricSource>('manual')
  const [rawExtractionResponse, setRawExtractionResponse] = useState<string | undefined>(undefined)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const rubricFileInputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (editingJob) {
      setTitle(editingJob.title)
      setJobCode(editingJob.jobCode)
      setDepartment(editingJob.department)
      setOrganization(editingJob.organization)
      setPostingDate(editingJob.postingDate.split('T')[0])
      setRubricCategories(editingJob.currentVersion.rubric)
      setMustHaves(editingJob.currentVersion.mustHaves)
      setDesiredCriteria(editingJob.currentVersion.desiredCriteria || [])
      setJobDescription(editingJob.jobDescription || '')
      setRunsPerApplication(String(editingJob.currentVersion.runsPerApplication))
      setAggregationStrategy(editingJob.currentVersion.aggregationStrategy)
      setLonglistThreshold(String(editingJob.currentVersion.longlistThreshold))
      setShortlistThreshold(String(editingJob.currentVersion.shortlistThreshold))
      setRubricApprovalStatus(editingJob.currentVersion.rubricApprovalStatus || 'approved')
      setRubricSource(editingJob.currentVersion.rubricSource || 'manual')
      setRawExtractionResponse(editingJob.currentVersion.rawExtractionResponse)
    }
  }, [editingJob])

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
    setUploadedSpecDocId(`spec-doc-${Date.now()}-${file.name}`)
    setProcessingFile(true)

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

      const extracted = await api.extractJobSpec(file.name, base64Content, file.type)

      // Track rubric source and raw extraction response for audit
      setRawExtractionResponse(JSON.stringify(extracted))

      setTitle(extracted.title || '')
      setDepartment(extracted.department || '')
      setOrganization(extracted.organization && extracted.organization !== 'Not Specified' ? extracted.organization : '')
      setJobDescription(extracted.jobDescription || '')

      if (extracted.mustHaves && Array.isArray(extracted.mustHaves)) {
        setMustHaves(
          extracted.mustHaves.map((m: any, i: number) => ({
            id: String(i + 1),
            criterion: m.criterion || '',
            description: m.description || '',
          }))
        )
      }

      if (extracted.desiredCriteria && Array.isArray(extracted.desiredCriteria)) {
        setDesiredCriteria(
          extracted.desiredCriteria.map((d: any, i: number) => ({
            id: String(i + 1),
            qualification: d.qualification || '',
            description: d.description || '',
          }))
        )
      }

      if (extracted.rubric && Array.isArray(extracted.rubric) && extracted.rubric.length > 0) {
        setRubricCategories(
          extracted.rubric.map((r: any, i: number) => ({
            id: String(i + 1),
            name: r.name || '',
            description: r.description || '',
            weight: r.weight || 0,
          }))
        )
        setRubricSource('extracted')
        setRubricApprovalStatus('draft')
      } else if (!rubricUploadedFile) {
        // Auto-generate draft rubric: 60% weight to must-haves
        const mhItems = (extracted.mustHaves || []).filter((m: any) => m.criterion)
        const dcItems = (extracted.desiredCriteria || []).filter((d: any) => d.qualification)
        const draftCategories: RubricCategory[] = []
        const mustHaveWeight = 0.6
        const desiredWeight = 0.4

        if (mhItems.length > 0) {
          const perMh = mustHaveWeight / mhItems.length
          mhItems.forEach((m: any, i: number) => {
            draftCategories.push({
              id: `draft-mh-${i}`,
              name: m.criterion,
              description: m.description || 'Must-have requirement',
              weight: Math.round(perMh * 100) / 100,
            })
          })
        }

        if (dcItems.length > 0) {
          const perDc = (mhItems.length > 0 ? desiredWeight : 1.0) / dcItems.length
          dcItems.forEach((d: any, i: number) => {
            draftCategories.push({
              id: `draft-dc-${i}`,
              name: d.qualification,
              description: d.description || 'Desired qualification',
              weight: Math.round(perDc * 100) / 100,
            })
          })
        }

        // Normalize weights to sum to 1.0
        if (draftCategories.length > 0) {
          const total = draftCategories.reduce((s, c) => s + c.weight, 0)
          if (total > 0) {
            draftCategories.forEach(c => c.weight = Math.round((c.weight / total) * 100) / 100)
            // Fix rounding error on last item
            const sum = draftCategories.reduce((s, c) => s + c.weight, 0)
            draftCategories[draftCategories.length - 1].weight += Math.round((1.0 - sum) * 100) / 100
          }
          setRubricCategories(draftCategories)
          setRubricSource('generated')
          setRubricApprovalStatus('draft')
          toast.info('Draft rubric generated from requirements. Review and adjust weights.')
        }
      }

      toast.success('Job specification extracted successfully')
    } catch (error: any) {
      const errorMsg = error?.message || 'Unknown error'
      toast.error(`Extraction failed: ${errorMsg}. Please fill in manually.`)
      console.error('Error processing file:', error)
    } finally {
      setProcessingFile(false)
    }
  }

  const handleRubricFileSelect = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return

    const validExtensions = ['.pdf', '.jpg', '.jpeg', '.md', '.txt', '.docx']
    const fileExtension = file.name.toLowerCase().slice(file.name.lastIndexOf('.'))

    if (!validExtensions.includes(fileExtension)) {
      toast.error('Please upload a PDF, JPG, Markdown (.md), TXT, or DOCX file')
      return
    }

    setRubricUploadedFile(file)
    setProcessingRubric(true)
    setRubricTitleMismatch(null)

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

      // Track raw response for audit
      setRawExtractionResponse(JSON.stringify(extracted))

      // Check title mismatch
      if (extracted.title && title && extracted.title.toLowerCase() !== title.toLowerCase()) {
        setRubricTitleMismatch(
          `The rubric document title "${extracted.title}" does not match the job title "${title}". Please correct and upload the corrected rubric document. The existing rubric document will be discarded.`
        )
        setRubricUploadedFile(null)
        if (rubricFileInputRef.current) rubricFileInputRef.current.value = ''
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

        setRubricCategories(rubric)
        setRubricSource('extracted')
        setRubricApprovalStatus('draft')
        toast.success('Rubric extracted from document successfully')
      }
    } catch (error: any) {
      const errorMsg = error?.message || 'Unknown error'
      toast.error(`Rubric extraction failed: ${errorMsg}`)
      console.error('Error processing rubric file:', error)
    } finally {
      setProcessingRubric(false)
    }
  }

  const removeUploadedFile = () => {
    setUploadedFile(null)
    setUploadedSpecDocId(null)
    if (fileInputRef.current) {
      fileInputRef.current.value = ''
    }
  }

  const removeRubricFile = () => {
    setRubricUploadedFile(null)
    setRubricTitleMismatch(null)
    if (rubricFileInputRef.current) {
      rubricFileInputRef.current.value = ''
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

  const addDesiredCriterion = () => {
    setDesiredCriteria([
      ...desiredCriteria,
      { id: Date.now().toString(), qualification: '', description: '' },
    ])
  }

  const removeDesiredCriterion = (id: string) => {
    setDesiredCriteria(desiredCriteria.filter((d) => d.id !== id))
  }

  const updateDesiredCriterion = (id: string, field: keyof DesiredCriteria, value: string) => {
    setDesiredCriteria(desiredCriteria.map((d) => (d.id === id ? { ...d, [field]: value } : d)))
  }

  const handleSubmit = async () => {
    if (!title || !department || !organization || !jobCode) {
      toast.error('Please fill in job title, job code, department, and organization')
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
        await api.updateJob(editingJob.jobId, {
          title,
          jobCode,
          department,
          organization,
          postingDate,
          rubric: validCategories,
          mustHaves: mustHaves.filter((m) => m.criterion),
          desiredCriteria: desiredCriteria.filter((d) => d.qualification),
          jobDescription: jobDescription || undefined,
          runsPerApplication: parseInt(runsPerApplication),
          aggregationStrategy,
          longlistThreshold: parseFloat(longlistThreshold),
          shortlistThreshold: parseFloat(shortlistThreshold),
          specDocumentId: uploadedSpecDocId || undefined,
          rubricDocumentId: editingJob.rubricDocumentId,
          rubricSource,
          rawExtractionResponse,
          rubricApprovalStatus,
        })
        toast.success('Job updated successfully')
      } else {
        await api.createJob({
          title,
          jobCode,
          department,
          organization,
          postingDate,
          rubric: validCategories,
          mustHaves: mustHaves.filter((m) => m.criterion),
          desiredCriteria: desiredCriteria.filter((d) => d.qualification),
          jobDescription: jobDescription || undefined,
          runsPerApplication: parseInt(runsPerApplication),
          aggregationStrategy,
          longlistThreshold: parseFloat(longlistThreshold),
          shortlistThreshold: parseFloat(shortlistThreshold),
          specDocumentId: uploadedSpecDocId || undefined,
          rubricSource,
          rawExtractionResponse,
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
    setJobCode('')
    setDepartment('')
    setOrganization('')
    setJobDescription('')
    setPostingDate(new Date().toISOString().split('T')[0])
    setRubricCategories([{ id: '1', name: '', description: '', weight: 0 }])
    setMustHaves([{ id: '1', criterion: '', description: '' }])
    setDesiredCriteria([])
    setRunsPerApplication('3')
    setAggregationStrategy('median')
    setLonglistThreshold('60')
    setShortlistThreshold('75')
    setUploadedFile(null)
    setUploadedSpecDocId(null)
    setRubricUploadedFile(null)
    setRubricTitleMismatch(null)
    setRubricApprovalStatus('approved')
    setRubricSource('manual')
    setRawExtractionResponse(undefined)
    if (fileInputRef.current) {
      fileInputRef.current.value = ''
    }
    if (rubricFileInputRef.current) {
      rubricFileInputRef.current.value = ''
    }
  }

  return (
    <DraggableResizableDialog
      open={open}
      onOpenChange={onClose}
      defaultWidth={1200}
      defaultHeight={700}
      minWidth={800}
      minHeight={500}
    >
      <DraggableDialogHeader>
        <DialogTitle>{editingJob ? 'Edit Job' : 'Create New Job'}</DialogTitle>
        <DialogDescription>
          {editingJob 
            ? 'Update job details, scoring rubric, and requirements'
            : 'Upload a job specification document or manually define job details, scoring rubric, and requirements'
          }
        </DialogDescription>
      </DraggableDialogHeader>

      <DraggableDialogBody className="px-6">
        <Tabs defaultValue="manual" className="h-full flex flex-col">
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
                accept=".pdf,.jpg,.jpeg,.md,.txt,.docx"
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
                      Supported formats: PDF, JPG, Markdown (.md), TXT, DOCX
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

            <div className="border-2 border-dashed border-border rounded-lg p-6 text-center">
              <input
                ref={rubricFileInputRef}
                type="file"
                accept=".pdf,.jpg,.jpeg,.md,.txt,.docx"
                onChange={handleRubricFileSelect}
                className="hidden"
                id="rubric-upload"
              />
              {!rubricUploadedFile ? (
                <div className="space-y-3">
                  <div>
                    <p className="text-sm font-medium mb-1">Upload Rubric Document (Optional)</p>
                    <p className="text-xs text-muted-foreground">
                      Separately upload a scoring rubric if not included in the job specification
                    </p>
                  </div>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    onClick={() => rubricFileInputRef.current?.click()}
                    disabled={processingRubric}
                  >
                    <UploadSimple size={16} />
                    Choose Rubric File
                  </Button>
                </div>
              ) : (
                <div className="space-y-3">
                  <div className="flex items-center justify-center gap-3">
                    <File size={24} className="text-accent" />
                    <div className="text-left">
                      <p className="text-sm font-medium">{rubricUploadedFile.name}</p>
                      <p className="text-xs text-muted-foreground">
                        {(rubricUploadedFile.size / 1024).toFixed(1)} KB
                      </p>
                    </div>
                  </div>
                  {processingRubric && (
                    <div className="flex items-center justify-center gap-2 text-sm text-accent">
                      <Sparkle size={16} className="animate-pulse" />
                      Extracting rubric...
                    </div>
                  )}
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    onClick={removeRubricFile}
                    disabled={processingRubric}
                  >
                    <X size={16} />
                    Remove
                  </Button>
                </div>
              )}
            </div>

            {rubricTitleMismatch && (
              <div className="flex items-start gap-2 p-3 rounded-md bg-destructive/10 text-destructive text-sm">
                <WarningCircle size={20} className="shrink-0 mt-0.5" />
                <p>{rubricTitleMismatch}</p>
              </div>
            )}

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
                <Label htmlFor="jobCode">Job Code</Label>
                <Input
                  id="jobCode"
                  value={jobCode}
                  onChange={(e) => setJobCode(e.target.value.toUpperCase())}
                  placeholder="e.g., SSE-ENG-2024"
                  className="font-mono"
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
              <div className="space-y-2 col-span-2">
                <Label htmlFor="postingDate">Posting Date</Label>
                <Input
                  id="postingDate"
                  type="date"
                  value={postingDate}
                  onChange={(e) => setPostingDate(e.target.value)}
                />
              </div>
              <div className="space-y-2 col-span-2">
                <Label htmlFor="jobDescription">Job Description</Label>
                <Textarea
                  id="jobDescription"
                  value={jobDescription}
                  onChange={(e) => setJobDescription(e.target.value)}
                  placeholder="Job description (extracted from spec or enter manually)"
                  rows={3}
                />
              </div>
            </div>

            <div className="space-y-3">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <Label>Rubric Categories</Label>
                  {rubricApprovalStatus === 'draft' ? (
                    <span className="inline-flex items-center rounded-full bg-amber-100 px-2.5 py-0.5 text-xs font-medium text-amber-800">
                      Draft
                    </span>
                  ) : (
                    <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">
                      Approved
                    </span>
                  )}
                </div>
                <div className="flex items-center gap-2">
                  {rubricApprovalStatus === 'draft' && (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={async () => {
                        setRubricApprovalStatus('approved')
                        if (editingJob) {
                          try {
                            await api.updateRubricApproval(editingJob.jobId, 'approved')
                            toast.success('Rubric approved')
                          } catch {
                            toast.error('Failed to persist rubric approval')
                            setRubricApprovalStatus('draft')
                          }
                        }
                      }}
                      className="text-green-700 border-green-300 hover:bg-green-50"
                    >
                      Approve Rubric
                    </Button>
                  )}
                  <Button type="button" variant="outline" size="sm" onClick={addRubricCategory}>
                    <Plus size={16} />
                    Add Category
                  </Button>
                </div>
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

            <div className="space-y-3">
              <div className="flex items-center justify-between">
                <Label>Desired Criteria</Label>
                <Button type="button" variant="outline" size="sm" onClick={addDesiredCriterion}>
                  <Plus size={16} />
                  Add Criterion
                </Button>
              </div>
              {desiredCriteria.map((criterion) => (
                <Card key={criterion.id}>
                  <CardContent className="p-4">
                    <div className="flex gap-2">
                      <div className="flex-1 space-y-2">
                        <Label>Qualification</Label>
                        <Input
                          value={criterion.qualification}
                          onChange={(e) => updateDesiredCriterion(criterion.id, 'qualification', e.target.value)}
                          placeholder="e.g., Experience with cloud platforms"
                        />
                      </div>
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={() => removeDesiredCriterion(criterion.id)}
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
      </DraggableDialogBody>

      <DraggableDialogFooter>
        <Button variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button onClick={handleSubmit} disabled={submitting}>
          {submitting ? (editingJob ? 'Updating...' : 'Creating...') : (editingJob ? 'Update Job' : 'Create Job')}
        </Button>
      </DraggableDialogFooter>
    </DraggableResizableDialog>
  )
}
