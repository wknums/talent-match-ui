import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { ScrollArea } from '@/components/ui/scroll-area'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import { Label } from '@/components/ui/label'
import { ArrowLeft, FloppyDisk, Robot } from '@phosphor-icons/react'
import { api } from '@/lib/api'
import { getCurrentUser as authGetCurrentUser } from '@/lib/auth'
import { buildStackBManualReviewPrepopulation, normalizeManualReviewForRubric } from '@/lib/stackb-scoring'
import { toast } from 'sonner'
import { cn } from '@/lib/utils'
import { ResizablePanelGroup, ResizablePanel, ResizableHandle } from '@/components/ui/resizable'
import { DocumentViewer } from '@/components/DocumentViewer'
import type { Application, Job, AggregatedResult, ManualReviewData, ManualReviewAuditEntry, ScoringRun } from '@/types'

interface ManualReviewViewProps {
  applicationId: string
  jobId: string
  onBack: () => void
}

export function ManualReviewView({ applicationId, jobId, onBack }: ManualReviewViewProps) {
  const createEmptyReviewData = (): ManualReviewData => ({
    applicationId,
    jobId,
    rubricScores: {},
    overallComment: '',
    auditTrail: [],
    lastModifiedAt: new Date().toISOString(),
    lastModifiedBy: 'current-user',
  })

  const [application, setApplication] = useState<Application | null>(null)
  const [job, setJob] = useState<Job | null>(null)
  const [selectedDocIndex, setSelectedDocIndex] = useState(0)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [aiPrePopulated, setAiPrePopulated] = useState(false)
  const [aiScoringMismatch, setAiScoringMismatch] = useState(false)
  const [aggregatedResult, setAggregatedResult] = useState<AggregatedResult | null>(null)
  const [reviewData, setReviewData] = useState<ManualReviewData>(createEmptyReviewData)

  const [currentUser, setCurrentUser] = useState<{ login: string; name?: string } | null>(null)

  useEffect(() => {
    loadData()
    loadUser()
  }, [applicationId, jobId])

  const loadUser = async () => {
    try {
      const user = await authGetCurrentUser()
      if (user) {
        setCurrentUser({ login: user.username, name: user.fullName })
      }
    } catch (error) {
      setCurrentUser({ login: 'reviewer', name: 'Reviewer' })
    }
  }

  const loadData = async () => {
    setLoading(true)
    setError(null)
    try {
      const [appData, jobData] = await Promise.all([
        api.getApplication(applicationId),
        api.getJob(jobId),
      ])
      
      if (!appData) {
        setError(`Application ${applicationId} not found`)
        return
      }
      
      if (!jobData) {
        setError(`Job ${jobId} not found`)
        return
      }
      
      setApplication(appData)
      setJob(jobData)

      // Fetch saved review, aggregated result, and scoring runs in parallel.
      const [savedReview, aggResult, scoringRuns] = await Promise.all([
        api.getManualReview(applicationId).catch(() => null),
        api.getAggregatedResult(applicationId).catch(() => null),
        api.getScoringRuns(applicationId).catch(() => [] as ScoringRun[]),
      ])

      if (aggResult) {
        setAggregatedResult(aggResult)
      }

      if (jobData) {
        const rubricDefinition = jobData.currentVersion.rubric.map(category => ({
          id: category.id,
          name: category.name,
          weight: category.weight,
        }))
        const normalizedSavedReview = normalizeManualReviewForRubric({
          review: savedReview,
          rubric: rubricDefinition,
        })
        const base = normalizedSavedReview || createEmptyReviewData()
        const prepopulated = buildStackBManualReviewPrepopulation({
          scoringRuns,
          aggregatedResult: aggResult,
          rubric: rubricDefinition,
          existingReview: base,
        })

        const rubricScores: Record<string, { points: number; maxPoints: number; comment: string }> = {}
        for (const category of jobData.currentVersion.rubric) {
          rubricScores[category.id] = prepopulated.rubricScores[category.id] ?? {
            points: 0,
            maxPoints: Math.round(category.weight * 100),
            comment: '',
          }
        }

        setAiPrePopulated(prepopulated.aiPrePopulated)
        setAiScoringMismatch(prepopulated.aiScoringMismatch)

        const nextReviewData = {
          ...base,
          applicationId,
          jobId,
          rubricScores,
          overallComment: prepopulated.overallComment ?? base.overallComment,
        }

        setReviewData(nextReviewData)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load data')
    } finally {
      setLoading(false)
    }
  }

  const updatePoints = (categoryId: string, categoryName: string, newPoints: number) => {
    setReviewData((current) => ({
      ...current,
      rubricScores: {
        ...current.rubricScores,
        [categoryId]: {
          ...current.rubricScores[categoryId],
          points: newPoints,
        },
      },
      lastModifiedAt: new Date().toISOString(),
      lastModifiedBy: currentUser?.login || 'unknown',
    }))
  }

  const updateComment = (categoryId: string, categoryName: string, newComment: string) => {
    setReviewData((current) => ({
      ...current,
      rubricScores: {
        ...current.rubricScores,
        [categoryId]: {
          ...current.rubricScores[categoryId],
          comment: newComment,
        },
      },
      lastModifiedAt: new Date().toISOString(),
      lastModifiedBy: currentUser?.login || 'unknown',
    }))
  }

  const updateOverallComment = (newComment: string) => {
    setReviewData((current) => ({
      ...current,
      overallComment: newComment,
      lastModifiedAt: new Date().toISOString(),
      lastModifiedBy: currentUser?.login || 'unknown',
    }))
  }

  const calculateTotalScore = () => {
    if (!job) return 0
    let total = 0
    job.currentVersion.rubric.forEach((category) => {
      const score = reviewData.rubricScores[category.id]
      if (score) {
        total += (score.points / score.maxPoints) * (category.weight * 100)
      }
    })
    return total
  }

  const handleSave = async () => {
    if (!job) {
      return
    }

    setSaving(true)
    try {
      const finalScore = calculateTotalScore()
      const dataToSave = {
        ...reviewData,
        adjustedFinalScore: finalScore,
      }
      
      const savedReview = await api.saveManualReview(applicationId, dataToSave)
      const normalizedSavedReview = normalizeManualReviewForRubric({
        review: savedReview,
        rubric: job.currentVersion.rubric.map(category => ({
          id: category.id,
          name: category.name,
          weight: category.weight,
        })),
      })
      setReviewData(normalizedSavedReview ?? savedReview)
      toast.success('Manual review saved successfully')
    } catch (error) {
      toast.error('Failed to save review')
    } finally {
      setSaving(false)
    }
  }

  if (loading) {
    return <div className="py-12 text-center">Loading...</div>
  }

  if (error) {
    return (
      <div className="min-h-screen bg-background">
        <div className="border-b bg-card sticky top-0 z-10">
          <div className="px-6 py-4">
            <div className="flex items-center gap-4">
              <Button variant="ghost" size="sm" onClick={onBack}>
                <ArrowLeft size={20} />
              </Button>
              <div>
                <h1 className="text-xl font-bold">Manual Review</h1>
              </div>
            </div>
          </div>
        </div>
        <div className="px-6 py-12 text-center">
          <p className="text-destructive text-lg">{error}</p>
          <Button onClick={onBack} className="mt-4">
            Go Back
          </Button>
        </div>
      </div>
    )
  }

  if (!application || !job) {
    return <div className="py-12 text-center">Data not available</div>
  }

  const totalScore = calculateTotalScore()

  return (
    <div className="min-h-screen bg-background">
      <div className="border-b bg-card sticky top-0 z-10">
        <div className="px-6 py-4">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-4">
              <Button variant="ghost" size="sm" onClick={onBack}>
                <ArrowLeft size={20} />
              </Button>
              <div>
                <h1 className="text-xl font-bold">Manual Review</h1>
                <p className="text-sm text-muted-foreground">
                  {application.candidateName || application.candidateRef} • {job.title}
                </p>
              </div>
            </div>
            <div className="flex items-center gap-4">
              <div className="text-right">
                <p className="text-sm text-muted-foreground">Total Score</p>
                <p className="text-2xl font-mono font-bold text-accent">
                  {totalScore.toFixed(1)}
                </p>
              </div>
              <Button onClick={handleSave} disabled={saving}>
                <FloppyDisk size={16} />
                {saving ? 'Saving...' : 'Save Review'}
              </Button>
            </div>
          </div>
        </div>
      </div>

      <div className="px-4 py-4" style={{ height: 'calc(100vh - 80px)' }}>
        <ResizablePanelGroup direction="horizontal" className="h-full rounded-lg">
          {/* Left pane: Original Document (FR-058) */}
          <ResizablePanel defaultSize={33} minSize={15}>
            <Card className="flex flex-col h-full rounded-none border-0">
              <CardHeader className="shrink-0">
                <CardTitle className="text-lg">Original Document</CardTitle>
                {application.documents.length > 1 && (
                  <div className="flex gap-1 mt-2">
                    {application.documents.map((doc, idx) => (
                      <Button
                        key={doc.documentId}
                        variant={idx === selectedDocIndex ? 'default' : 'outline'}
                        size="sm"
                        className="text-xs"
                        onClick={() => setSelectedDocIndex(idx)}
                      >
                        {doc.fileName}
                      </Button>
                    ))}
                  </div>
                )}
              </CardHeader>
              <CardContent className="flex-1 overflow-hidden p-0">
                {application.documents[selectedDocIndex] && (
                  <DocumentViewer
                    applicationId={application.applicationId}
                    document={application.documents[selectedDocIndex]}
                    className="h-full"
                  />
                )}
              </CardContent>
            </Card>
          </ResizablePanel>

          <ResizableHandle withHandle />

          {/* Centre pane: Scoring Rubric */}
          <ResizablePanel defaultSize={34} minSize={15}>
            <Card className="flex flex-col h-full rounded-none border-0">
              <CardHeader className="shrink-0">
                <CardTitle className="text-lg">Scoring Rubric</CardTitle>
              {aiPrePopulated && (
                <div className="flex items-center gap-2 mt-2 px-3 py-2 rounded-md bg-blue-50 dark:bg-blue-950 border border-blue-200 dark:border-blue-800 text-sm text-blue-700 dark:text-blue-300">
                  <Robot size={16} />
                  <span>
                    Pre-populated from AI scoring
                    {aggregatedResult && (
                      <> (score: {aggregatedResult.finalScore.toFixed(1)}, variance: {aggregatedResult.variance.toFixed(2)})</>
                    )}
                    . Please verify and adjust.
                  </span>
                </div>
              )}
              {aiScoringMismatch && !aiPrePopulated && (
                <div className="mt-2 px-3 py-2 rounded-md border border-amber-200 bg-amber-50 text-sm text-amber-800 dark:border-amber-800 dark:bg-amber-950 dark:text-amber-300">
                  AI scoring exists for this application, but Stack B-compatible category scores were not available to pre-populate the rubric.
                </div>
              )}
            </CardHeader>
            <CardContent className="flex-1 overflow-hidden p-0">
              <ScrollArea className="h-full w-full">
                <div className="space-y-4 px-6 pb-6">
                  {job.currentVersion.rubric.map((category) => {
                    const score = reviewData.rubricScores[category.id] || {
                      points: 0,
                      maxPoints: Math.round(category.weight * 100),
                      comment: '',
                    }

                    return (
                      <div key={category.id} className="space-y-3">
                        <div>
                          <div className="flex items-center justify-between mb-2">
                            <h4 className="font-medium text-sm">{category.name}</h4>
                            <Badge variant="secondary" className="font-mono text-xs">
                              Weight: {(category.weight * 100).toFixed(0)}%
                            </Badge>
                          </div>

                          <div className="space-y-2">
                            <div className="flex items-center gap-2">
                              <Input
                                type="number"
                                min={0}
                                max={score.maxPoints}
                                value={score.points}
                                onChange={(e) =>
                                  updatePoints(
                                    category.id,
                                    category.name,
                                    parseFloat(e.target.value) || 0
                                  )
                                }
                                className="w-24 font-mono"
                              />
                              <span className="text-sm text-muted-foreground">
                                / {score.maxPoints} points
                              </span>
                            </div>

                            <Textarea
                              placeholder="Add evaluation comments..."
                              value={score.comment}
                              onChange={(e) =>
                                updateComment(category.id, category.name, e.target.value)
                              }
                              className="min-h-[80px] text-sm"
                            />
                          </div>
                        </div>
                        <Separator />
                      </div>
                    )
                  })}

                  <div className="pt-2">
                    <Label className="text-sm font-medium mb-2 block">
                      Overall Review Comments
                    </Label>
                    <Textarea
                      placeholder="Add overall comments about this candidate..."
                      value={reviewData.overallComment}
                      onChange={(e) => updateOverallComment(e.target.value)}
                      className="min-h-[120px]"
                    />
                  </div>
                </div>
              </ScrollArea>
            </CardContent>
          </Card>
          </ResizablePanel>

          <ResizableHandle withHandle />

          {/* Right pane: Job Specification */}
          <ResizablePanel defaultSize={33} minSize={15}>
            <Card className="flex flex-col h-full rounded-none border-0">
              <CardHeader className="shrink-0">
                <CardTitle className="text-lg">Job Specification</CardTitle>
              </CardHeader>
            <CardContent className="flex-1 overflow-hidden p-0">
              <ScrollArea className="h-full w-full">
                <div className="space-y-4 px-6 pb-6">
                  <div>
                    <h3 className="font-semibold mb-2">{job.title}</h3>
                    <p className="text-sm text-muted-foreground">
                      {job.department} • {job.organization}
                    </p>
                  </div>

                  <Separator />

                  <div>
                    <h4 className="font-medium text-sm mb-2 text-muted-foreground">
                      MUST-HAVE REQUIREMENTS
                    </h4>
                    <ul className="space-y-2">
                      {job.currentVersion.mustHaves.map((mustHave) => (
                        <li key={mustHave.id} className="text-sm flex gap-2">
                          <span className="text-accent">•</span>
                          <div>
                            <p className="font-medium">{mustHave.criterion}</p>
                            {mustHave.description && (
                              <p className="text-xs text-muted-foreground">
                                {mustHave.description}
                              </p>
                            )}
                          </div>
                        </li>
                      ))}
                    </ul>
                  </div>

                  <Separator />

                  <div>
                    <h4 className="font-medium text-sm mb-2 text-muted-foreground">
                      SCORING CRITERIA
                    </h4>
                    <div className="space-y-3">
                      {job.currentVersion.rubric.map((category) => (
                        <div key={category.id} className="text-sm">
                          <div className="flex items-center gap-2 mb-1">
                            <Badge variant="outline" className="font-mono text-xs">
                              {(category.weight * 100).toFixed(0)}%
                            </Badge>
                            <span className="font-medium">{category.name}</span>
                          </div>
                          <p className="text-xs text-muted-foreground pl-14">
                            {category.description}
                          </p>
                        </div>
                      ))}
                    </div>
                  </div>

                  <Separator />

                  <div>
                    <h4 className="font-medium text-sm mb-2 text-muted-foreground">
                      THRESHOLDS
                    </h4>
                    <div className="grid grid-cols-2 gap-2 text-sm">
                      <div>
                        <span className="text-muted-foreground">Longlist:</span>
                        <span className="font-mono ml-2">
                          {job.currentVersion.longlistThreshold}+
                        </span>
                      </div>
                      <div>
                        <span className="text-muted-foreground">Shortlist:</span>
                        <span className="font-mono ml-2">
                          {job.currentVersion.shortlistThreshold}+
                        </span>
                      </div>
                    </div>
                  </div>
                </div>
              </ScrollArea>
            </CardContent>
          </Card>
          </ResizablePanel>
        </ResizablePanelGroup>

        {/* Audit Trail */}
        {reviewData.auditTrail.length > 0 && (
          <Card className="mt-4">
            <CardHeader>
              <CardTitle className="text-lg">Audit Trail</CardTitle>
            </CardHeader>
            <CardContent>
              <div className="space-y-2 max-h-60 overflow-y-auto">
                {[...reviewData.auditTrail].reverse().map((entry) => (
                  <div key={entry.entryId} className="flex items-start gap-3 text-sm p-2 rounded bg-muted/30">
                    <div className="text-xs text-muted-foreground whitespace-nowrap">
                      {new Date(entry.timestamp).toLocaleString()}
                    </div>
                    <div className="flex-1">
                      <span className="font-medium">{entry.reviewerName}</span>
                      {entry.changeType === 'points_allocated' && (
                        <span> adjusted <strong>{entry.categoryName}</strong> from {entry.previousValue ?? 0} → {entry.newValue}</span>
                      )}
                      {entry.changeType === 'comment_added' && (
                        <span> added comment to <strong>{entry.categoryName || 'overall'}</strong></span>
                      )}
                      {entry.changeType === 'score_adjustment' && (
                        <span> changed score for <strong>{entry.categoryName}</strong>: {entry.previousValue} → {entry.newValue}</span>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </CardContent>
          </Card>
        )}
      </div>
    </div>
  )
}
