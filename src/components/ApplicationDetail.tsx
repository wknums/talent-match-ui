import { useEffect, useState } from 'react'
import {
  DraggableResizableDialog,
  DraggableDialogHeader,
  DraggableDialogBody,
} from '@/components/DraggableResizableDialog'
import { DialogTitle } from '@/components/ui/dialog'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Badge } from '@/components/ui/badge'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { StatusBadge } from '@/components/StatusBadge'
import { Quotes, File, CheckCircle, XCircle, ShieldCheck, Pencil, Warning, ArrowClockwise, SpinnerGap } from '@phosphor-icons/react'
import { api } from '@/lib/api'
import { deriveCandidateNameFromScoringRuns, matchCategoryToRubric, parseCategoryScores, parseGate, parseGateEntries } from '@/lib/stackb-scoring'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { DocumentViewer } from '@/components/DocumentViewer'
import type { Application, ScoringRun, AggregatedResult, DLQItem, Job } from '@/types'
import { cn } from '@/lib/utils'

interface ApplicationDetailProps {
  applicationId: string | null
  open: boolean
  onClose: () => void
  onStartManualReview?: (applicationId: string, jobId: string) => void
}

export function ApplicationDetail({ applicationId, open, onClose, onStartManualReview }: ApplicationDetailProps) {
  const [application, setApplication] = useState<Application | null>(null)
  const [job, setJob] = useState<Job | null>(null)
  const [scoringRuns, setScoringRuns] = useState<ScoringRun[]>([])
  const [aggregatedResult, setAggregatedResult] = useState<AggregatedResult | null>(null)
  const [dlqItem, setDlqItem] = useState<DLQItem | null>(null)
  const [retrying, setRetrying] = useState(false)
  const [rescoring, setRescoring] = useState(false)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (applicationId && open) {
      loadApplicationData()
    }
  }, [applicationId, open])

  const loadApplicationData = async () => {
    if (!applicationId) return

    setLoading(true)
    try {
      const appData = await api.getApplication(applicationId)
      if (!appData) {
        setApplication(null)
        setJob(null)
        setScoringRuns([])
        setAggregatedResult(null)
        setDlqItem(null)
        return
      }

      const [runsData, resultData, jobData] = await Promise.all([
        api.getScoringRuns(applicationId),
        (appData.status === 'Completed' || appData.status === 'NeedsManualReview')
          ? api.getAggregatedResult(applicationId)
          : Promise.resolve(null),
        api.getJob(appData.jobId),
      ])
      setApplication(appData)
      setJob(jobData)
      setScoringRuns(runsData)
      setAggregatedResult(resultData)

      // Check DLQ for this application if it failed
      if (appData && (appData.status === 'ScoringFailed' || appData.status === 'ExtractionFailed')) {
        try {
          const dlqItems: DLQItem[] = await api.getDLQItems()
          const match = dlqItems.find(d => d.applicationId === applicationId)
          setDlqItem(match ?? null)
        } catch {
          setDlqItem(null)
        }
      } else {
        setDlqItem(null)
      }
    } finally {
      setLoading(false)
    }
  }

  const handleRetry = async () => {
    if (!dlqItem) return
    setRetrying(true)
    try {
      await api.retryDLQItem(dlqItem.itemId)
      toast.success('Re-queued for scoring')
      setDlqItem(null)
      // Reload after a short delay to let the pipeline start
      setTimeout(() => loadApplicationData(), 2000)
    } catch {
      toast.error('Failed to retry scoring')
    } finally {
      setRetrying(false)
    }
  }

  if (!applicationId || !application) return null

  const getScoreColor = (score: number) => {
    if (score >= 80) return 'text-success'
    if (score >= 60) return 'text-accent'
    return 'text-muted-foreground'
  }

  const formatMissingCriteria = (missingCriteria: unknown) => {
    if (!Array.isArray(missingCriteria) || missingCriteria.length === 0) {
      return 'No criteria details provided'
    }

    return missingCriteria.join(', ')
  }

  const rubricNames = job?.currentVersion.rubric.map(category => category.name) ?? []

  const matchCategory = (llmName: string): string => matchCategoryToRubric(llmName, rubricNames) ?? llmName

  const categoryDetails = (() => {
    const evidence = new Map<string, string[]>()
    const scores = new Map<string, number[]>()

    for (const run of scoringRuns) {
      for (const [rawCategory, score] of Object.entries(parseCategoryScores(run))) {
        const category = matchCategory(rawCategory)
        if (!scores.has(category)) scores.set(category, [])
        if (!scores.get(category)!.includes(Number(score ?? 0))) {
          scores.get(category)!.push(Number(score ?? 0))
        }
      }

      for (const citation of run.evidenceCitations ?? []) {
        const category = matchCategory(citation.category)
        if (!evidence.has(category)) evidence.set(category, [])
        const snippets = evidence.get(category)!
        const snippet = citation.snippet?.trim()
        if (snippet && !snippets.includes(snippet)) snippets.push(snippet)
      }
    }

    return { evidence, scores }
  })()

  const displayRationale = aggregatedResult?.rationaleText?.trim()
    || scoringRuns.map(run => run.rationale?.trim()).find(Boolean)
    || Array.from(categoryDetails.evidence.entries())
      .filter(([, snippets]) => snippets.length > 0)
      .slice(0, 3)
      .map(([category, snippets]) => `${category}: ${snippets[0]}`)
      .join(' ')
    || 'No rationale available'

  const displayRecommendations = aggregatedResult?.recommendationsText?.trim()
    || Array.from(
      new Set(
        scoringRuns.flatMap(run => run.improvementRecommendations ?? []).map(tip => tip.trim()).filter(Boolean),
      ),
    ).join('; ')
    || 'No recommendations available'

  const derivedCategoryScores = (() => {
    // Prioritize aggregatedResult.finalSubScores if available
    if (aggregatedResult?.finalSubScores && Object.keys(aggregatedResult.finalSubScores).length > 0) {
      return aggregatedResult.finalSubScores
    }

    // Fallback: recalculate from individual scoring runs
    const groupedScores = new Map<string, number[]>()
    for (const [category, scores] of categoryDetails.scores.entries()) {
      if (!groupedScores.has(category)) groupedScores.set(category, [])
      groupedScores.get(category)!.push(...scores)
    }

    return Object.fromEntries(
      Array.from(groupedScores.entries()).map(([category, scores]) => [
        category,
        scores.reduce((sum, score) => sum + score, 0) / scores.length,
      ]),
    )
  })()

  const rubricOverviewEntries = (() => {
    if (!job?.currentVersion.rubric?.length) {
      return Object.entries(derivedCategoryScores).map(([category, score]) => ({
        category,
        description: '',
        weightLabel: '',
        score: Number(score ?? 0),
        scoreSource: 'category' as const,
        evidence: categoryDetails.evidence.get(category) ?? [],
        tips: [] as string[],
      }))
    }

    return job.currentVersion.rubric.map((rubricCategory) => {
      const scoreCandidates = categoryDetails.scores.get(rubricCategory.name) ?? []
      const derivedScore = derivedCategoryScores[rubricCategory.name]
      const explicitScore = typeof derivedScore === 'number'
        ? Number(derivedScore)
        : (scoreCandidates.length > 0
            ? scoreCandidates.reduce((sum, value) => sum + value, 0) / scoreCandidates.length
            : undefined)

      return {
        category: rubricCategory.name,
        description: rubricCategory.description,
        weightLabel: `${(rubricCategory.weight * 100).toFixed(0)}%`,
        score: explicitScore,
        scoreSource: explicitScore != null ? 'category' as const : 'none' as const,
        evidence: categoryDetails.evidence.get(rubricCategory.name) ?? [],
        tips: [] as string[],
      }
    })
  })()

  const firstRun = [...scoringRuns].sort((left, right) => left.runIndex - right.runIndex)[0]
  const mustHaveDetails = firstRun ? parseGate(firstRun) : null
  const hasOverviewData = Boolean(aggregatedResult || scoringRuns.length > 0)
  const resolvedCandidateName = application.candidateName
    || deriveCandidateNameFromScoringRuns(scoringRuns)
    || application.candidateRef
  const mustHaveEntries = (() => {
    if (!firstRun) return [] as Array<{ criterion: string; passed: boolean; evidence?: string }>

    const parsedEntries = parseGateEntries(firstRun)
    if (parsedEntries?.length) {
      return parsedEntries.map(entry => ({
        criterion: entry.criterion ?? 'Unnamed criterion',
        passed: entry.passed,
        evidence: entry.evidence,
      }))
    }

    if (!mustHaveDetails) return [] as Array<{ criterion: string; passed: boolean; evidence?: string }>

    if (Array.isArray(mustHaveDetails.missingCriteria) && mustHaveDetails.missingCriteria.length > 0) {
      return mustHaveDetails.missingCriteria.map((criterion) => ({ criterion, passed: false }))
    }

    const effectiveDecision = aggregatedResult?.finalDecision ?? application.finalDecision
    const inferredPass = effectiveDecision === 'Eligible' || effectiveDecision === 'NeedsManualReview'

    return [{
      criterion: inferredPass ? 'Eligibility gate passed' : 'Eligibility gate failed',
      passed: inferredPass ? true : mustHaveDetails.passed,
    }]
  })()

  return (
    <DraggableResizableDialog
      open={open}
      onOpenChange={onClose}
      defaultWidth={Math.round(window.innerWidth * 0.8)}
      defaultHeight={Math.round(window.innerHeight * 0.8)}
      minWidth={600}
      minHeight={500}
    >
      <DraggableDialogHeader>
        <div className="flex items-start justify-between gap-4">
          <div className="flex-1">
            <DialogTitle className="text-2xl">{resolvedCandidateName}</DialogTitle>
            {application.candidateEmail && (
              <p className="text-sm text-muted-foreground mt-1">{application.candidateEmail}</p>
            )}
          </div>
          <div className="flex items-center gap-2">
            {application.status === 'Completed' && (
              <Button
                variant="outline"
                size="sm"
                disabled={rescoring}
                onClick={async () => {
                  setRescoring(true)
                  try {
                    await api.rescoreApplication(application.jobId, application.applicationId)
                    toast.success('Application queued for re-scoring')
                    setTimeout(() => loadApplicationData(), 3000)
                  } catch {
                    toast.error('Failed to re-score application')
                  } finally {
                    setRescoring(false)
                  }
                }}
              >
                {rescoring ? <SpinnerGap size={16} className="animate-spin" /> : <ArrowClockwise size={16} />}
                Re-score
              </Button>
            )}
            {onStartManualReview && (
              <Button 
                variant="outline" 
                size="sm"
                onClick={() => onStartManualReview(application.applicationId, application.jobId)}
              >
                <Pencil size={16} />
                Manual Review
              </Button>
            )}
            <StatusBadge status={application.status} />
          </div>
        </div>
        {application.finalScore !== undefined && (
          <div className="flex items-center gap-6 mt-4">
            <div>
              <p className="text-sm text-muted-foreground mb-1">Final Score</p>
              <p className={cn('text-4xl font-mono font-bold', getScoreColor(application.finalScore))}>
                {application.finalScore.toFixed(1)}
              </p>
            </div>
            {application.variance !== undefined && (
              <div>
                <p className="text-sm text-muted-foreground mb-1">Variance</p>
                <p className={cn('text-2xl font-mono', application.variance > 15 && 'text-destructive font-bold')}>
                  ±{application.variance.toFixed(1)}
                </p>
              </div>
            )}
            {application.finalDecision && (
              <div>
                <p className="text-sm text-muted-foreground mb-1">Decision</p>
                <Badge
                  variant={
                    application.finalDecision === 'Eligible'
                      ? 'default'
                      : application.finalDecision === 'Excluded'
                      ? 'secondary'
                      : 'destructive'
                  }
                  className="text-base px-3 py-1"
                >
                  {application.finalDecision}
                </Badge>
              </div>
            )}
          </div>
        )}
      </DraggableDialogHeader>

      <DraggableDialogBody className="px-6">
        <Tabs defaultValue="overview">
          <TabsList className="grid w-full grid-cols-3">
            <TabsTrigger value="overview">Overview</TabsTrigger>
            <TabsTrigger value="runs">Runs ({scoringRuns.length})</TabsTrigger>
            <TabsTrigger value="documents">Documents</TabsTrigger>
          </TabsList>

          <TabsContent value="overview" className="space-y-4 mt-6">
                  {/* Failed scoring banner */}
                  {(application.status === 'ScoringFailed' || application.status === 'ExtractionFailed') && (
                    <Card className="border-destructive">
                      <CardContent className="p-4 space-y-3">
                        <div className="flex items-center gap-2 text-destructive">
                          <Warning size={20} weight="bold" />
                          <span className="font-semibold">Scoring Failed</span>
                        </div>
                        {dlqItem && (
                          <>
                            <p className="text-sm text-muted-foreground">{dlqItem.failureReason}</p>
                            <div className="flex items-center gap-4 text-xs text-muted-foreground">
                              <span>Attempts: {dlqItem.attemptCount}</span>
                              <span>Last attempt: {new Date(dlqItem.lastAttemptedAt).toLocaleString()}</span>
                            </div>
                            {dlqItem.canRetry && (
                              <Button size="sm" variant="outline" onClick={handleRetry} disabled={retrying}>
                                {retrying ? <SpinnerGap size={14} className="animate-spin" /> : <ArrowClockwise size={14} />}
                                Retry Scoring
                              </Button>
                            )}
                          </>
                        )}
                      </CardContent>
                    </Card>
                  )}

                  {hasOverviewData && (
                    <>
                      <Card>
                        <CardHeader>
                          <CardTitle className="text-lg flex items-center gap-2">
                            <ShieldCheck size={20} />
                            Final Assessment
                          </CardTitle>
                        </CardHeader>
                        <CardContent className="space-y-4">
                          <div>
                            <p className="text-sm font-medium mb-2">Rationale</p>
                            <p className="text-sm text-muted-foreground leading-relaxed">
                              {displayRationale}
                            </p>
                          </div>
                          <div>
                            <p className="text-sm font-medium mb-2">Recommendations</p>
                            <p className="text-sm text-muted-foreground leading-relaxed">
                              {displayRecommendations}
                            </p>
                          </div>
                        </CardContent>
                      </Card>

                      <Card>
                        <CardHeader>
                          <CardTitle className="text-lg">Category Scores</CardTitle>
                        </CardHeader>
                        <CardContent>
                          <div className="space-y-5">
                            {rubricOverviewEntries.map(({ category, description, weightLabel, score, scoreSource, evidence, tips }) => {
                              return (
                                <div key={category}>
                                  <div className="flex items-center justify-between mb-1">
                                    <div>
                                      <span className="text-sm font-medium">{category}</span>
                                      {weightLabel && (
                                        <span className="text-xs text-muted-foreground ml-2">Weight: {weightLabel}</span>
                                      )}
                                    </div>
                                    {typeof score === 'number' ? (
                                      <span className={cn('font-mono font-semibold', getScoreColor(Number(score ?? 0)))}>
                                        {Number(score ?? 0).toFixed(1)}
                                      </span>
                                    ) : (
                                      <span className="text-xs text-muted-foreground">No AI score</span>
                                    )}
                                  </div>
                                  {description && (
                                    <p className="text-xs text-muted-foreground mb-2">{description}</p>
                                  )}
                                  {typeof score === 'number' && (
                                    <p className="text-xs font-medium text-muted-foreground mb-2">
                                      {scoreSource === 'overall' ? 'AI Score (overall fallback): ' : 'AI Score: '}
                                      <span className={cn('font-mono', getScoreColor(Number(score ?? 0)))}>{Number(score ?? 0).toFixed(1)} / 100</span>
                                    </p>
                                  )}
                                  {typeof score === 'number' && (
                                    <div className="h-2 bg-muted rounded-full overflow-hidden mb-2">
                                      <div
                                        className={cn(
                                          'h-full transition-all',
                                          Number(score ?? 0) >= 80 ? 'bg-success' : Number(score ?? 0) >= 60 ? 'bg-accent' : 'bg-muted-foreground'
                                        )}
                                        style={{ width: `${Math.max(0, Math.min(100, Number(score ?? 0)))}%` }}
                                      />
                                    </div>
                                  )}
                                  {evidence.length > 0 && (
                                    <div className="ml-1 mb-2">
                                      <p className="text-xs font-semibold text-muted-foreground underline mb-1">Evidence</p>
                                      <ul className="text-xs text-muted-foreground space-y-0.5 list-disc list-inside">
                                        {evidence.map((e, i) => <li key={i}>{e}</li>)}
                                      </ul>
                                    </div>
                                  )}
                                  {tips.length > 0 && (
                                    <div className="ml-1">
                                      <p className="text-xs font-semibold text-muted-foreground underline mb-1">Gaps / Improvements</p>
                                      <ul className="text-xs text-muted-foreground space-y-0.5 list-disc list-inside">
                                        {tips.map((t, i) => <li key={i}>{t}</li>)}
                                      </ul>
                                    </div>
                                  )}
                                </div>
                              )
                            })}
                            {rubricOverviewEntries.length === 0 && (
                              <p className="text-sm text-muted-foreground">No category scores available</p>
                            )}
                          </div>
                        </CardContent>
                      </Card>

                      <Card>
                        <CardHeader>
                          <CardTitle className="text-lg">Must-Have Requirements</CardTitle>
                        </CardHeader>
                        <CardContent>
                          <div className="space-y-3">
                            {mustHaveEntries.map((entry, i) => (
                              <div key={`${entry.criterion}-${i}`} className="flex items-start gap-2">
                                {entry.passed ? (
                                  <CheckCircle className="text-success mt-0.5 shrink-0" size={18} weight="fill" />
                                ) : (
                                  <XCircle className="text-destructive mt-0.5 shrink-0" size={18} weight="fill" />
                                )}
                                <div>
                                  <span className="text-sm">{entry.criterion}</span>
                                  {entry.evidence && (
                                    <p className="text-xs text-muted-foreground mt-0.5 italic">{entry.evidence}</p>
                                  )}
                                </div>
                              </div>
                            ))}
                            {mustHaveEntries.length === 0 && (
                              <p className="text-sm text-muted-foreground">No must-have requirement details available</p>
                            )}
                          </div>
                        </CardContent>
                      </Card>
                    </>
                  )}
                </TabsContent>

                <TabsContent value="runs" className="space-y-4 mt-6">
                  {scoringRuns.map((run) => {
                    const runCategoryScores = parseCategoryScores(run)
                    const runGate = parseGate(run)
                    const runGateEntries = parseGateEntries(run)

                    return (
                      <Card key={run.runId}>
                        <CardHeader>
                          <div className="flex items-center justify-between">
                            <div className="flex items-center gap-2">
                              <CardTitle className="text-lg">Run {run.runIndex}</CardTitle>
                              <Badge variant="outline">{run.status}</Badge>
                            </div>
                            <Badge variant="outline" className="font-mono">
                              {run.overallScore.toFixed(1)}
                            </Badge>
                          </div>
                          <div className="flex gap-4 text-xs text-muted-foreground mt-2">
                            <span>Model: {run.modelDeploymentId}</span>
                            <span>Duration: {(run.durationMs / 1000).toFixed(1)}s</span>
                            {run.tokenUsage && <span>Tokens: {run.tokenUsage.totalTokens}</span>}
                            {run.parserConfidence != null && (
                              <span>Parser confidence: {Math.round(run.parserConfidence * 100)}%</span>
                            )}
                          </div>
                        </CardHeader>
                        <CardContent className="space-y-4">
                          {run.parserWarnings && run.parserWarnings.length > 0 && (
                            <div className="rounded border border-amber-300 bg-amber-50 p-3 text-amber-900 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200 text-sm">
                              <p className="font-medium mb-1">Parser Warnings</p>
                              <ul className="list-disc list-inside space-y-0.5">
                                {run.parserWarnings.map((warning, index) => (
                                  <li key={index}>{warning}</li>
                                ))}
                              </ul>
                            </div>
                          )}

                          {Object.keys(runCategoryScores).length > 0 && (
                            <div>
                              <p className="text-sm font-medium mb-2">Category Scores</p>
                              <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-sm">
                                {Object.entries(runCategoryScores).map(([category, score]) => (
                                  <div key={category} className="flex justify-between">
                                    <span>{category}</span>
                                    <span className={cn('font-mono font-medium', getScoreColor(Number(score ?? 0)))}>
                                      {Number(score ?? 0).toFixed(1)}
                                    </span>
                                  </div>
                                ))}
                              </div>
                            </div>
                          )}

                          <div>
                            <p className="text-sm font-medium mb-2">Must-Have Requirements</p>
                            <span className="text-sm">
                              {runGate?.passed ? 'Passed' : `Failed (${formatMissingCriteria(runGate?.missingCriteria)})`}
                            </span>
                            {runGateEntries && runGateEntries.length > 0 && (
                              <div className="mt-2 space-y-2">
                                {runGateEntries.map((entry, index) => (
                                  <div key={`${entry.criterion}-${index}`} className="rounded bg-muted p-2 text-xs">
                                    <div className="font-medium">{entry.criterion}</div>
                                    <div className="text-muted-foreground">{entry.passed ? 'Passed' : 'Failed'}</div>
                                    {entry.evidence && <div className="mt-1 italic">{entry.evidence}</div>}
                                  </div>
                                ))}
                              </div>
                            )}
                          </div>

                          <div>
                            <p className="text-sm font-medium mb-2">Rationale</p>
                            <pre className="whitespace-pre-wrap break-words text-sm text-muted-foreground rounded bg-muted p-3">{run.rationale}</pre>
                          </div>

                          {run.evidenceCitations.length > 0 && (
                            <div>
                              <p className="text-sm font-medium mb-2 flex items-center gap-2">
                                <Quotes size={16} />
                                Evidence
                              </p>
                              <div className="space-y-2">
                                {run.evidenceCitations.map((citation, i) => (
                                  <div key={i} className="bg-muted p-3 rounded-md">
                                    <div className="flex items-center justify-between mb-1">
                                      <Badge variant="outline" className="text-xs">
                                        {citation.category}
                                      </Badge>
                                      <span className="text-xs text-muted-foreground">
                                        {(citation.confidence * 100).toFixed(0)}% confidence
                                      </span>
                                    </div>
                                    <p className="text-sm italic">&ldquo;{citation.snippet}&rdquo;</p>
                                  </div>
                                ))}
                              </div>
                            </div>
                          )}

                          {run.improvementRecommendations && run.improvementRecommendations.length > 0 && (
                            <div>
                              <p className="text-sm font-medium mb-2">Improvement Recommendations</p>
                              <ul className="list-disc list-inside text-sm text-muted-foreground space-y-1">
                                {run.improvementRecommendations.map((tip, i) => (
                                  <li key={i}>{tip}</li>
                                ))}
                              </ul>
                            </div>
                          )}

                          {run.rawResponseText && (
                            <details className="rounded border bg-muted p-3 text-xs">
                              <summary className="cursor-pointer font-medium text-sm">Raw LLM Response</summary>
                              <pre className="mt-2 whitespace-pre-wrap break-words">{run.rawResponseText}</pre>
                            </details>
                          )}
                        </CardContent>
                      </Card>
                    )
                  })}
                  {scoringRuns.length === 0 && (
                    <p className="text-sm text-muted-foreground text-center py-4">No scoring runs available</p>
                  )}
                </TabsContent>

                <TabsContent value="documents" className="space-y-4 mt-6">
                  {application.documents.map((doc) => (
                    <Card key={doc.documentId}>
                      <CardHeader>
                        <div className="flex items-center gap-3">
                          <File size={20} className="text-accent" />
                          <div className="flex-1 min-w-0">
                            <CardTitle className="text-lg truncate">{doc.fileName}</CardTitle>
                            <p className="text-xs text-muted-foreground">
                              {(doc.sizeBytes / 1024).toFixed(1)} KB • {doc.mimeType}
                            </p>
                          </div>
                        </div>
                      </CardHeader>
                      <CardContent>
                        <div className="border rounded-md overflow-hidden" style={{ height: '400px' }}>
                          <DocumentViewer
                            applicationId={application.applicationId}
                            document={doc}
                            className="h-full"
                          />
                        </div>
                      </CardContent>
                    </Card>
                  ))}
                </TabsContent>
              </Tabs>
          </DraggableDialogBody>
        </DraggableResizableDialog>
      )
    }
