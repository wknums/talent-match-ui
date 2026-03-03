import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { ScrollArea } from '@/components/ui/scroll-area'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import { Label } from '@/components/ui/label'
import { ArrowLeft, FloppyDisk } from '@phosphor-icons/react'
import { mockAPI } from '@/lib/api'
import { kv } from '@/lib/spark-client'
import { getCurrentUser } from '@/lib/auth'
import { toast } from 'sonner'
import { cn } from '@/lib/utils'
import type { Application, Job, ExtractionArtifact, ManualReviewData, ManualReviewAuditEntry } from '@/types'

interface ManualReviewViewProps {
  applicationId: string
  jobId: string
  onBack: () => void
}

export function ManualReviewView({ applicationId, jobId, onBack }: ManualReviewViewProps) {
  const [application, setApplication] = useState<Application | null>(null)
  const [job, setJob] = useState<Job | null>(null)
  const [extractionArtifact, setExtractionArtifact] = useState<ExtractionArtifact | null>(null)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [reviewData, setReviewData] = useState<ManualReviewData>({
    applicationId,
    jobId,
    rubricScores: {},
    overallComment: '',
    auditTrail: [],
    lastModifiedAt: new Date().toISOString(),
    lastModifiedBy: 'current-user',
  })

  const [currentUser, setCurrentUser] = useState<{ login: string; name?: string } | null>(null)

  useEffect(() => {
    loadData()
    loadUser()
    loadSavedReview()
  }, [applicationId, jobId])

  const loadUser = async () => {
    try {
      const user = await getCurrentUser()
      if (user) {
        setCurrentUser({ login: user.username, name: user.fullName })
      }
    } catch (error) {
      setCurrentUser({ login: 'reviewer', name: 'Reviewer' })
    }
  }

  const loadSavedReview = async () => {
    try {
      const saved = await kv.get<ManualReviewData>(`manual-review-${applicationId}`)
      if (saved) {
        setReviewData(saved)
      }
    } catch (error) {
      console.error('Error loading saved review:', error)
    }
  }

  const loadData = async () => {
    setLoading(true)
    setError(null)
    try {
      const [appData, jobData, artifactData] = await Promise.all([
        mockAPI.getApplication(applicationId),
        mockAPI.getJob(jobId),
        mockAPI.getExtractionArtifact(applicationId),
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
      setExtractionArtifact(artifactData)

      if (jobData) {
        setReviewData((current) => {
          const existingScores = current.rubricScores
          const newScores: Record<string, { points: number; maxPoints: number; comment: string }> = {}

          jobData.currentVersion.rubric.forEach((category) => {
            if (existingScores[category.id]) {
              newScores[category.id] = existingScores[category.id]
            } else {
              newScores[category.id] = {
                points: 0,
                maxPoints: Math.round(category.weight * 100),
                comment: '',
              }
            }
          })

          return {
            ...current,
            rubricScores: newScores,
          }
        })
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load data')
    } finally {
      setLoading(false)
    }
  }

  const addAuditEntry = (
    changeType: ManualReviewAuditEntry['changeType'],
    categoryId?: string,
    categoryName?: string,
    previousValue?: number | string,
    newValue?: number | string,
    comment?: string
  ) => {
    const entry: ManualReviewAuditEntry = {
      entryId: `audit-${Date.now()}`,
      applicationId,
      reviewerId: currentUser?.login || 'unknown',
      reviewerName: currentUser?.name || 'Unknown Reviewer',
      timestamp: new Date().toISOString(),
      changeType,
      categoryId,
      categoryName,
      previousValue,
      newValue,
      comment,
    }

    setReviewData((current) => ({
      ...current,
      auditTrail: [...current.auditTrail, entry],
      lastModifiedAt: new Date().toISOString(),
      lastModifiedBy: currentUser?.login || 'unknown',
    }))
  }

  const updatePoints = (categoryId: string, categoryName: string, newPoints: number) => {
    const previousPoints = reviewData.rubricScores[categoryId]?.points

    setReviewData((current) => ({
      ...current,
      rubricScores: {
        ...current.rubricScores,
        [categoryId]: {
          ...current.rubricScores[categoryId],
          points: newPoints,
        },
      },
    }))

    addAuditEntry('points_allocated', categoryId, categoryName, previousPoints, newPoints)
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
    }))

    addAuditEntry('comment_added', categoryId, categoryName, undefined, undefined, newComment)
  }

  const updateOverallComment = (newComment: string) => {
    setReviewData((current) => ({
      ...current,
      overallComment: newComment,
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
    setSaving(true)
    try {
      const finalScore = calculateTotalScore()
      const dataToSave = {
        ...reviewData,
        adjustedFinalScore: finalScore,
      }
      
      await kv.set(`manual-review-${applicationId}`, dataToSave)
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
          <div className="container mx-auto px-6 py-4">
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
        <div className="container mx-auto px-6 py-12 text-center">
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
        <div className="container mx-auto px-6 py-4">
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

      <div className="container mx-auto px-6 py-6">
        <div className="grid grid-cols-3 gap-6" style={{ height: 'calc(100vh - 180px)' }}>
          <Card className="flex flex-col h-full">
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

          <Card className="flex flex-col h-full">
            <CardHeader className="shrink-0">
              <CardTitle className="text-lg">Scoring Rubric</CardTitle>
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
                          <p className="text-xs text-muted-foreground mb-3">
                            {category.description}
                          </p>

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

          <Card className="flex flex-col h-full">
            <CardHeader className="shrink-0">
              <CardTitle className="text-lg">Application</CardTitle>
            </CardHeader>
            <CardContent className="flex-1 overflow-hidden p-0">
              <ScrollArea className="h-full w-full">
                <div className="space-y-4 px-6 pb-6">
                  <div>
                    <h3 className="font-semibold mb-1">
                      {application.candidateName || application.candidateRef}
                    </h3>
                    {application.candidateEmail && (
                      <p className="text-sm text-muted-foreground">
                        {application.candidateEmail}
                      </p>
                    )}
                  </div>

                  <Separator />

                  {application.finalScore !== undefined && (
                    <>
                      <div className="grid grid-cols-2 gap-4">
                        <div>
                          <p className="text-xs text-muted-foreground mb-1">AI Score</p>
                          <p className="text-xl font-mono font-bold text-accent">
                            {application.finalScore.toFixed(1)}
                          </p>
                        </div>
                        {application.variance !== undefined && (
                          <div>
                            <p className="text-xs text-muted-foreground mb-1">Variance</p>
                            <p className="text-lg font-mono">±{application.variance.toFixed(1)}</p>
                          </div>
                        )}
                      </div>
                      <Separator />
                    </>
                  )}

                  {extractionArtifact && (
                    <div>
                      <h4 className="font-medium text-sm mb-2 text-muted-foreground">
                        EXTRACTED CONTENT
                      </h4>
                      <div className="prose prose-sm max-w-none text-sm">
                        <pre className="whitespace-pre-wrap font-sans text-foreground">
                          {extractionArtifact.markdown}
                        </pre>
                      </div>
                    </div>
                  )}

                  {application.documents && application.documents.length > 0 && (
                    <>
                      <Separator />
                      <div>
                        <h4 className="font-medium text-sm mb-2 text-muted-foreground">
                          ATTACHED DOCUMENTS
                        </h4>
                        <div className="space-y-2">
                          {application.documents.map((doc) => (
                            <div
                              key={doc.documentId}
                              className="flex items-center gap-2 text-sm p-2 rounded bg-muted/50"
                            >
                              <span className="text-xs text-muted-foreground">📄</span>
                              <span className="flex-1 truncate">{doc.fileName}</span>
                              <span className="text-xs text-muted-foreground">
                                {(doc.sizeBytes / 1024).toFixed(1)} KB
                              </span>
                            </div>
                          ))}
                        </div>
                      </div>
                    </>
                  )}
                </div>
              </ScrollArea>
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  )
}
