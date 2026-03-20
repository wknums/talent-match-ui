import { useState, useEffect, useCallback } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Badge } from '@/components/ui/badge'
import { Textarea } from '@/components/ui/textarea'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from '@/components/ui/dialog'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import {
  Star,
  UploadSimple,
  FilePlus,
  Lightning,
  Check,
  X,
  File,
  Warning,
  SpinnerGap,
  CheckCircle,
  Play,
  ShieldCheck,
} from '@phosphor-icons/react'
import { api } from '@/lib/api'
import { toast } from 'sonner'
import { cn } from '@/lib/utils'
import type { ScoringPrompt, PromptStatus, PromptTestRun, PromptTestRunDetail, Application } from '@/types'

interface PromptManagementProps {
  jobId: string
  hasApprovedRubric: boolean
  onPromptStatusChange?: () => void
}

const statusColors: Record<PromptStatus, string> = {
  draft: 'bg-muted text-muted-foreground',
  active: 'bg-accent text-accent-foreground',
  inactive: 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-200',
  'production-approved': 'bg-success text-success-foreground',
}

const statusLabels: Record<PromptStatus, string> = {
  draft: 'Draft',
  active: 'Active',
  inactive: 'Inactive',
  'production-approved': 'Production Approved',
}

// Star rating component
function StarRating({
  value,
  onChange,
  readonly = false,
}: {
  value: number
  onChange?: (rating: number) => void
  readonly?: boolean
}) {
  const [hovered, setHovered] = useState(0)

  return (
    <div className="flex items-center gap-0.5">
      {[1, 2, 3, 4, 5].map((star) => {
        const filled = star <= (hovered || value)
        return (
          <button
            key={star}
            type="button"
            disabled={readonly}
            className={cn(
              'transition-colors',
              readonly ? 'cursor-default' : 'cursor-pointer hover:scale-110',
              filled ? 'text-yellow-500' : 'text-muted-foreground/40'
            )}
            onClick={() => onChange?.(star === value ? 0 : star)}
            onMouseEnter={() => !readonly && setHovered(star)}
            onMouseLeave={() => !readonly && setHovered(0)}
          >
            <Star size={20} weight={filled ? 'fill' : 'regular'} />
          </button>
        )
      })}
    </div>
  )
}

// Test workflow sub-component
function PromptTestWorkflow({
  jobId,
  prompt,
  onProductionApproved,
}: {
  jobId: string
  prompt: ScoringPrompt
  onProductionApproved: () => void
}) {
  const [testRuns, setTestRuns] = useState<PromptTestRun[]>([])
  const [loading, setLoading] = useState(false)
  const [files, setFiles] = useState<File[]>([])
  const [dragActive, setDragActive] = useState(false)
  const [creating, setCreating] = useState(false)
  const [approving, setApproving] = useState(false)
  const [approvingProduction, setApprovingProduction] = useState(false)
  const [selectedTestRun, setSelectedTestRun] = useState<PromptTestRunDetail | null>(null)

  useEffect(() => {
    loadTestRuns()
  }, [jobId, prompt.promptId])

  const loadTestRuns = async () => {
    setLoading(true)
    try {
      const runs = await api.getTestRuns(jobId, prompt.promptId)
      setTestRuns(runs)
    } catch {
      toast.error('Failed to load test runs')
    } finally {
      setLoading(false)
    }
  }

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
    const droppedFiles = Array.from(e.dataTransfer.files).filter(
      (file) => file.type === 'application/pdf' || file.type.startsWith('image/')
    )
    setFiles((prev) => [...prev, ...droppedFiles])
  }, [])

  const handleFileInput = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files) {
      setFiles((prev) => [...prev, ...Array.from(e.target.files!)])
    }
  }

  const createTestRun = async () => {
    if (files.length === 0) return
    setCreating(true)
    try {
      const fileData = await Promise.all(
        files.map(async (file) => {
          const content = await file.text()
          return {
            fileName: file.name,
            content: btoa(content),
            mimeType: file.type,
            sizeBytes: file.size,
          }
        })
      )
      const run = await api.createTestRun(jobId, prompt.promptId, fileData)
      setTestRuns((prev) => [run, ...prev])
      setFiles([])
      toast.success('Test run created successfully')
    } catch {
      toast.error('Failed to create test run')
    } finally {
      setCreating(false)
    }
  }

  const handleApproveTestRun = async (testRun: PromptTestRun) => {
    setApproving(true)
    try {
      const updated = await api.approveTestRun(jobId, prompt.promptId, testRun.testRunId)
      setTestRuns((prev) => prev.map((r) => (r.testRunId === testRun.testRunId ? updated : r)))
      toast.success('Test run approved')
    } catch {
      toast.error('Failed to approve test run')
    } finally {
      setApproving(false)
    }
  }

  const handleApproveForProduction = async (testRun: PromptTestRun) => {
    setApprovingProduction(true)
    try {
      await api.approveTestRun(jobId, prompt.promptId, testRun.testRunId)
      await api.approvePromptForProduction(jobId, prompt.promptId)
      toast.success('Prompt approved for production')
      onProductionApproved()
    } catch {
      toast.error('Failed to approve prompt for production')
    } finally {
      setApprovingProduction(false)
    }
  }

  const viewTestRun = async (testRun: PromptTestRun) => {
    try {
      const detail = await api.getTestRun(jobId, prompt.promptId, testRun.testRunId)
      setSelectedTestRun(detail)
    } catch {
      toast.error('Failed to load test run details')
    }
  }

  const isActive = prompt.status === 'active'

  return (
    <div className="space-y-4">
      <h4 className="font-semibold text-sm">Test Workflow</h4>

      {!isActive && (
        <div className="flex items-center gap-2 p-3 rounded-md bg-muted text-sm text-muted-foreground">
          <Warning size={16} />
          Prompt must be active before running tests
        </div>
      )}

      {isActive && (
        <div className="space-y-3">
          {/* File upload area */}
          <Card
            className={cn(
              'border-2 border-dashed transition-colors',
              dragActive && 'border-accent bg-accent/10',
              !dragActive && 'border-muted hover:border-accent/50'
            )}
            onDragEnter={handleDrag}
            onDragLeave={handleDrag}
            onDragOver={handleDrag}
            onDrop={handleDrop}
          >
            <CardContent className="p-6 text-center">
              <input
                type="file"
                id={`test-file-upload-${prompt.promptId}`}
                multiple
                accept=".pdf,image/*"
                onChange={handleFileInput}
                className="hidden"
              />
              <label htmlFor={`test-file-upload-${prompt.promptId}`} className="cursor-pointer">
                <div className="flex flex-col items-center gap-2">
                  <UploadSimple size={32} className="text-accent" />
                  <div>
                    <p className="text-sm font-medium">Drop test application files or click to browse</p>
                    <p className="text-xs text-muted-foreground">PDF and image files</p>
                  </div>
                </div>
              </label>
            </CardContent>
          </Card>

          {files.length > 0 && (
            <div className="space-y-2">
              <p className="text-sm font-medium">{files.length} file(s) selected</p>
              <div className="max-h-32 overflow-y-auto space-y-1">
                {files.map((file, index) => (
                  <div key={index} className="flex items-center gap-2 text-sm p-2 bg-muted rounded">
                    <File size={16} className="text-accent" />
                    <span className="flex-1 truncate">{file.name}</span>
                    <Button variant="ghost" size="sm" onClick={() => setFiles((f) => f.filter((_, i) => i !== index))}>
                      <X size={14} />
                    </Button>
                  </div>
                ))}
              </div>
              <Button onClick={createTestRun} disabled={creating} size="sm">
                {creating ? (
                  <>
                    <SpinnerGap size={16} className="animate-spin" />
                    Creating...
                  </>
                ) : (
                  <>
                    <Play size={16} />
                    Run Test
                  </>
                )}
              </Button>
            </div>
          )}
        </div>
      )}

      {/* Test runs list */}
      {loading ? (
        <div className="flex items-center gap-2 text-sm text-muted-foreground py-2">
          <SpinnerGap size={16} className="animate-spin" />
          Loading test runs...
        </div>
      ) : testRuns.length > 0 ? (
        <div className="space-y-2">
          <p className="text-sm font-medium">Test Runs</p>
          {testRuns.map((run) => (
            <Card key={run.testRunId} className="p-3">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <Badge
                    className={cn(
                      run.status === 'approved'
                        ? 'bg-success text-success-foreground'
                        : run.status === 'rejected'
                          ? 'bg-destructive text-destructive-foreground'
                          : 'bg-accent text-accent-foreground'
                    )}
                  >
                    {run.status.replace('_', ' ')}
                  </Badge>
                  <span className="text-xs text-muted-foreground">
                    {new Date(run.createdAt).toLocaleDateString()} • {run.applicationIds.length} apps
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <Button variant="ghost" size="sm" onClick={() => viewTestRun(run)}>
                    View
                  </Button>
                  {run.status === 'pending_review' && (
                    <Button size="sm" variant="outline" onClick={() => handleApproveTestRun(run)} disabled={approving}>
                      {approving ? <SpinnerGap size={14} className="animate-spin" /> : <Check size={14} />}
                      Approve
                    </Button>
                  )}
                  {run.status === 'approved' && prompt.status !== 'production-approved' && (
                    <Button size="sm" onClick={() => handleApproveForProduction(run)} disabled={approvingProduction}>
                      {approvingProduction ? (
                        <SpinnerGap size={14} className="animate-spin" />
                      ) : (
                        <ShieldCheck size={14} />
                      )}
                      Approve for Production
                    </Button>
                  )}
                </div>
              </div>
            </Card>
          ))}
        </div>
      ) : (
        <p className="text-xs text-muted-foreground">No test runs yet</p>
      )}

      {/* Test run detail dialog */}
      <Dialog open={selectedTestRun !== null} onOpenChange={() => setSelectedTestRun(null)}>
        <DialogContent className="max-w-2xl max-h-[80vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Test Run Details</DialogTitle>
          </DialogHeader>
          {selectedTestRun && (
            <div className="space-y-4">
              <div className="grid grid-cols-2 gap-2 text-sm">
                <div>
                  <span className="text-muted-foreground">Status:</span>{' '}
                  <Badge
                    className={cn(
                      selectedTestRun.status === 'approved'
                        ? 'bg-success text-success-foreground'
                        : selectedTestRun.status === 'rejected'
                          ? 'bg-destructive text-destructive-foreground'
                          : 'bg-accent text-accent-foreground'
                    )}
                  >
                    {selectedTestRun.status.replace('_', ' ')}
                  </Badge>
                </div>
                <div>
                  <span className="text-muted-foreground">Created:</span>{' '}
                  {new Date(selectedTestRun.createdAt).toLocaleString()}
                </div>
              </div>

              {/* Scoring results per application */}
              {selectedTestRun.applications && selectedTestRun.applications.length > 0 ? (
                <div className="space-y-3">
                  <p className="text-sm font-medium">
                    Applications ({selectedTestRun.applications.length})
                  </p>
                  {selectedTestRun.applications.map(({ application, scoringRuns }) => (
                    <Card key={application.applicationId} className="p-3">
                      <div className="space-y-2">
                        <div className="flex items-center justify-between">
                          <span className="text-sm font-medium">
                            {application.candidateName || application.candidateRef || application.applicationId.slice(0, 8)}
                          </span>
                          <Badge
                            className={cn(
                              application.status === 'Completed'
                                ? 'bg-success text-success-foreground'
                                : application.status === 'ScoringFailed' || application.status === 'ExtractionFailed'
                                  ? 'bg-destructive text-destructive-foreground'
                                  : 'bg-accent text-accent-foreground'
                            )}
                          >
                            {application.status}
                          </Badge>
                        </div>

                        {scoringRuns.length > 0 ? (
                          <div className="space-y-2">
                            {scoringRuns.map((run) => (
                              <div key={run.runId} className="border rounded p-2 text-xs space-y-1.5 bg-muted/50">
                                <div className="flex items-center justify-between">
                                  <span className="font-medium">Run #{run.runIndex}</span>
                                  <span className="font-semibold text-sm">
                                    Score: {run.overallScore.toFixed(1)}
                                  </span>
                                </div>

                                {/* Sub-scores */}
                                {Object.keys(run.subScores).length > 0 && (
                                  <div className="grid grid-cols-2 gap-x-4 gap-y-0.5">
                                    {Object.entries(run.subScores).map(([category, score]) => (
                                      <div key={category} className="flex justify-between">
                                        <span className="text-muted-foreground truncate mr-2">{category}:</span>
                                        <span className="font-medium">{(score as number).toFixed(1)}</span>
                                      </div>
                                    ))}
                                  </div>
                                )}

                                {/* Must-have gate */}
                                {run.mustHaveResult && (
                                  <div className="flex items-center gap-1">
                                    {run.mustHaveResult.passed ? (
                                      <CheckCircle size={14} className="text-green-600" />
                                    ) : (
                                      <Warning size={14} className="text-red-600" />
                                    )}
                                    <span>
                                      Eligibility: {run.mustHaveResult.passed ? 'Passed' : `Failed (${run.mustHaveResult.missingCriteria.join(', ')})`}
                                    </span>
                                  </div>
                                )}

                                {/* Improvement tips */}
                                {run.improvementRecommendations && run.improvementRecommendations.length > 0 && (
                                  <div>
                                    <span className="text-muted-foreground">Tips:</span>
                                    <ul className="list-disc list-inside ml-1">
                                      {run.improvementRecommendations.map((tip, i) => (
                                        <li key={i}>{tip}</li>
                                      ))}
                                    </ul>
                                  </div>
                                )}
                              </div>
                            ))}
                          </div>
                        ) : (
                          <p className="text-xs text-muted-foreground">No scoring runs yet</p>
                        )}
                      </div>
                    </Card>
                  ))}
                </div>
              ) : selectedTestRun.applicationIds.length > 0 ? (
                <div>
                  <p className="text-sm font-medium mb-1">Applications ({selectedTestRun.applicationIds.length})</p>
                  <p className="text-xs text-muted-foreground">Scoring in progress...</p>
                </div>
              ) : null}

              {selectedTestRun.reviewNotes && (
                <div>
                  <p className="text-sm font-medium mb-1">Review Notes</p>
                  <p className="text-sm text-muted-foreground">{selectedTestRun.reviewNotes}</p>
                </div>
              )}
            </div>
          )}
        </DialogContent>
      </Dialog>
    </div>
  )
}

export function PromptManagement({ jobId, hasApprovedRubric, onPromptStatusChange }: PromptManagementProps) {
  const [prompts, setPrompts] = useState<ScoringPrompt[]>([])
  const [loading, setLoading] = useState(true)
  const [selectedPrompt, setSelectedPrompt] = useState<ScoringPrompt | null>(null)

  // Editor state
  const [editorOpen, setEditorOpen] = useState(false)
  const [editorText, setEditorText] = useState('')
  const [editorSource, setEditorSource] = useState<'manual' | 'imported' | 'generated'>('manual')
  const [editorMetadata, setEditorMetadata] = useState<Record<string, any> | undefined>()
  const [editingPrompt, setEditingPrompt] = useState<ScoringPrompt | null>(null)
  const [saving, setSaving] = useState(false)

  // Generation state
  const [generating, setGenerating] = useState(false)

  // Activation confirm state
  const [activateConfirmPrompt, setActivateConfirmPrompt] = useState<ScoringPrompt | null>(null)
  const [activating, setActivating] = useState(false)

  // Rating state
  const [ratingPrompt, setRatingPrompt] = useState<ScoringPrompt | null>(null)
  const [ratingValue, setRatingValue] = useState(0)
  const [ratingComments, setRatingComments] = useState('')
  const [savingRating, setSavingRating] = useState(false)

  useEffect(() => {
    loadPrompts()
  }, [jobId])

  const loadPrompts = async () => {
    setLoading(true)
    try {
      const data = await api.getPrompts(jobId)
      setPrompts(data.sort((a, b) => b.versionNumber - a.versionNumber))
    } catch {
      toast.error('Failed to load prompts')
    } finally {
      setLoading(false)
    }
  }

  // --- Creation methods ---

  const handleWriteManually = () => {
    setEditingPrompt(null)
    setEditorText('')
    setEditorSource('manual')
    setEditorMetadata(undefined)
    setEditorOpen(true)
  }

  const handleImportFile = () => {
    const input = document.createElement('input')
    input.type = 'file'
    input.accept = '.txt,.md,.text'
    input.onchange = async (e) => {
      const file = (e.target as HTMLInputElement).files?.[0]
      if (!file) return
      try {
        const text = await file.text()
        setEditingPrompt(null)
        setEditorText(text)
        setEditorSource('imported')
        setEditorMetadata(undefined)
        setEditorOpen(true)
      } catch {
        toast.error('Failed to read file')
      }
    }
    input.click()
  }

  const handleGenerate = async () => {
    setGenerating(true)
    try {
      const result = await api.generatePrompt(jobId)
      setEditingPrompt(null)
      setEditorText(result.promptText)
      setEditorSource('generated')
      setEditorMetadata(result.generationMetadata)
      setEditorOpen(true)
      toast.success('Prompt generated — review and save when ready')
    } catch {
      toast.error('Failed to generate prompt')
    } finally {
      setGenerating(false)
    }
  }

  // --- Editor save ---

  const handleSave = async () => {
    if (!editorText.trim()) return
    setSaving(true)
    try {
      if (editingPrompt) {
        await api.editPrompt(jobId, editingPrompt.promptId, { promptText: editorText })
      } else {
        await api.createPrompt(jobId, {
          promptText: editorText,
          source: editorSource,
          generationMetadata: editorMetadata,
        })
      }
      toast.success(editingPrompt ? 'Prompt updated' : 'New prompt revision created')
      setEditorOpen(false)
      await loadPrompts()
      onPromptStatusChange?.()
    } catch {
      toast.error('Failed to save prompt')
    } finally {
      setSaving(false)
    }
  }

  // --- Activation ---

  const handleActivate = async () => {
    if (!activateConfirmPrompt) return
    setActivating(true)
    try {
      await api.activatePrompt(jobId, activateConfirmPrompt.promptId)
      toast.success(`Prompt v${activateConfirmPrompt.versionNumber} activated`)
      setActivateConfirmPrompt(null)
      await loadPrompts()
      onPromptStatusChange?.()
    } catch {
      toast.error('Failed to activate prompt')
    } finally {
      setActivating(false)
    }
  }

  // --- Rating ---

  const openRating = (prompt: ScoringPrompt) => {
    setRatingPrompt(prompt)
    setRatingValue(prompt.rating ?? 0)
    setRatingComments(prompt.comments ?? '')
  }

  const handleSaveRating = async () => {
    if (!ratingPrompt) return
    setSavingRating(true)
    try {
      await api.ratePrompt(jobId, ratingPrompt.promptId, {
        rating: ratingValue,
        comments: ratingComments || undefined,
      })
      toast.success('Rating saved')
      setRatingPrompt(null)
      await loadPrompts()
    } catch {
      toast.error('Failed to save rating')
    } finally {
      setSavingRating(false)
    }
  }

  // --- Disabled state ---

  if (!hasApprovedRubric) {
    return (
      <div className="flex flex-col items-center justify-center py-12 text-center space-y-3">
        <Warning size={48} className="text-muted-foreground" />
        <p className="text-muted-foreground font-medium">
          Job must have an approved rubric before managing prompts
        </p>
        <p className="text-xs text-muted-foreground">(FR-033)</p>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      {/* Creation actions */}
      <div className="flex items-center gap-3">
        <Button variant="outline" onClick={handleWriteManually}>
          <FilePlus size={16} />
          Write Manually
        </Button>
        <Button variant="outline" onClick={handleImportFile}>
          <UploadSimple size={16} />
          Import File
        </Button>
        <Button variant="outline" onClick={handleGenerate} disabled={generating}>
          {generating ? (
            <>
              <SpinnerGap size={16} className="animate-spin" />
              Generating...
            </>
          ) : (
            <>
              <Lightning size={16} />
              Generate from Rubric
            </>
          )}
        </Button>
      </div>

      {/* Prompt list */}
      {loading ? (
        <div className="flex items-center justify-center gap-2 py-8 text-muted-foreground">
          <SpinnerGap size={20} className="animate-spin" />
          Loading prompts...
        </div>
      ) : prompts.length === 0 ? (
        <Card>
          <CardContent className="py-8 text-center">
            <p className="text-muted-foreground">No prompt revisions yet. Create one to get started.</p>
          </CardContent>
        </Card>
      ) : (
        <Tabs
          defaultValue={prompts[0]?.promptId}
          onValueChange={(val) => {
            const p = prompts.find((p) => p.promptId === val)
            setSelectedPrompt(p ?? null)
          }}
        >
          <TabsList className="flex flex-wrap h-auto gap-1">
            {prompts.map((p) => (
              <TabsTrigger key={p.promptId} value={p.promptId} className="text-xs">
                v{p.versionNumber}
                {p.status === 'production-approved' && (
                  <ShieldCheck size={12} className="ml-1 text-success" weight="fill" />
                )}
              </TabsTrigger>
            ))}
          </TabsList>

          {prompts.map((p) => (
            <TabsContent key={p.promptId} value={p.promptId} className="mt-4 space-y-4">
              {/* Header info */}
              <div className="flex items-start justify-between">
                <div className="space-y-1">
                  <div className="flex items-center gap-2">
                    <h3 className="font-semibold">Version {p.versionNumber}</h3>
                    <Badge className={cn(statusColors[p.status])}>{statusLabels[p.status]}</Badge>
                    {p.source && (
                      <Badge variant="outline" className="text-xs">
                        {p.source}
                      </Badge>
                    )}
                  </div>
                  <p className="text-xs text-muted-foreground">
                    Created {new Date(p.createdAt).toLocaleDateString()} by {p.author}
                    {p.lastModifiedAt !== p.createdAt && (
                      <> • Modified {new Date(p.lastModifiedAt).toLocaleDateString()}</>
                    )}
                  </p>
                  <div className="flex items-center gap-2 mt-1">
                    <StarRating value={p.rating ?? 0} readonly />
                    {p.rating !== undefined && <span className="text-xs text-muted-foreground">({p.rating}/5)</span>}
                  </div>
                </div>
                <div className="flex items-center gap-2">
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => openRating(p)}
                  >
                    <Star size={14} />
                    Rate
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => {
                      setEditingPrompt(p)
                      setEditorText(p.promptText)
                      setEditorSource(p.source)
                      setEditorMetadata(p.generationMetadata)
                      setEditorOpen(true)
                    }}
                  >
                    Edit
                  </Button>
                  {p.status === 'draft' && (
                    <Button size="sm" variant="outline" onClick={() => setActivateConfirmPrompt(p)}>
                      <Check size={14} />
                      Activate
                    </Button>
                  )}
                </div>
              </div>

              {/* Production approved banner */}
              {p.status === 'production-approved' && (
                <div className="flex items-center gap-2 p-3 rounded-md bg-success/10 text-success text-sm font-medium">
                  <ShieldCheck size={20} weight="fill" />
                  This prompt is approved for production use
                </div>
              )}

              {/* Prompt text preview */}
              <Card>
                <CardContent className="p-4">
                  <pre className="text-sm whitespace-pre-wrap font-mono bg-muted p-3 rounded max-h-64 overflow-y-auto">
                    {p.promptText}
                  </pre>
                </CardContent>
              </Card>

              {/* Comments */}
              {p.comments && (
                <div className="text-sm">
                  <span className="font-medium">Comments: </span>
                  <span className="text-muted-foreground">{p.comments}</span>
                </div>
              )}

              {/* Test workflow */}
              <PromptTestWorkflow
                jobId={jobId}
                prompt={p}
                onProductionApproved={() => {
                  loadPrompts()
                  onPromptStatusChange?.()
                }}
              />
            </TabsContent>
          ))}
        </Tabs>
      )}

      {/* Prompt editor dialog */}
      <Dialog open={editorOpen} onOpenChange={setEditorOpen}>
        <DialogContent className="max-w-3xl max-h-[85vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle>{editingPrompt ? `Edit Prompt v${editingPrompt.versionNumber}` : 'New Prompt'}</DialogTitle>
            <DialogDescription>
              {editingPrompt
                ? 'Edit the prompt text. Saving will create a new revision.'
                : `Create a new prompt revision (${editorSource})`}
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            <Textarea
              value={editorText}
              onChange={(e) => setEditorText(e.target.value)}
              placeholder="Enter scoring prompt text..."
              className="min-h-[300px] font-mono text-sm"
            />
            <div className="flex justify-end gap-2">
              <Button variant="outline" onClick={() => setEditorOpen(false)} disabled={saving}>
                Cancel
              </Button>
              <Button onClick={handleSave} disabled={!editorText.trim() || saving}>
                {saving ? (
                  <>
                    <SpinnerGap size={16} className="animate-spin" />
                    Saving...
                  </>
                ) : (
                  <>
                    <Check size={16} />
                    Save
                  </>
                )}
              </Button>
            </div>
          </div>
        </DialogContent>
      </Dialog>

      {/* Activate confirmation dialog */}
      <Dialog open={activateConfirmPrompt !== null} onOpenChange={() => setActivateConfirmPrompt(null)}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>Activate Prompt</DialogTitle>
            <DialogDescription>
              Are you sure you want to activate version {activateConfirmPrompt?.versionNumber}? This will deactivate any
              currently active prompt.
            </DialogDescription>
          </DialogHeader>
          <div className="flex justify-end gap-2 mt-4">
            <Button variant="outline" onClick={() => setActivateConfirmPrompt(null)} disabled={activating}>
              Cancel
            </Button>
            <Button onClick={handleActivate} disabled={activating}>
              {activating ? (
                <>
                  <SpinnerGap size={16} className="animate-spin" />
                  Activating...
                </>
              ) : (
                <>
                  <Check size={16} />
                  Activate
                </>
              )}
            </Button>
          </div>
        </DialogContent>
      </Dialog>

      {/* Rating dialog */}
      <Dialog open={ratingPrompt !== null} onOpenChange={() => setRatingPrompt(null)}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>Rate Prompt v{ratingPrompt?.versionNumber}</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            <div>
              <label className="text-sm font-medium mb-2 block">Rating</label>
              <StarRating value={ratingValue} onChange={setRatingValue} />
            </div>
            <div>
              <label className="text-sm font-medium mb-2 block">Comments</label>
              <Textarea
                value={ratingComments}
                onChange={(e) => setRatingComments(e.target.value)}
                placeholder="Optional comments..."
                className="min-h-[80px]"
              />
            </div>
            <div className="flex justify-end gap-2">
              <Button variant="outline" onClick={() => setRatingPrompt(null)} disabled={savingRating}>
                Cancel
              </Button>
              <Button onClick={handleSaveRating} disabled={savingRating}>
                {savingRating ? (
                  <>
                    <SpinnerGap size={16} className="animate-spin" />
                    Saving...
                  </>
                ) : (
                  'Save Rating'
                )}
              </Button>
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}
