import { useEffect, useState } from 'react'
import { Sheet, SheetContent, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Badge } from '@/components/ui/badge'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ScrollArea } from '@/components/ui/scroll-area'
import { StatusBadge } from '@/components/StatusBadge'
import { Quotes, File, CheckCircle, XCircle, ShieldCheck } from '@phosphor-icons/react'
import { mockAPI } from '@/lib/api'
import type { Application, ScoringRun, ExtractionArtifact, AggregatedResult } from '@/types'
import { cn } from '@/lib/utils'

interface ApplicationDetailProps {
  applicationId: string | null
  open: boolean
  onClose: () => void
}

export function ApplicationDetail({ applicationId, open, onClose }: ApplicationDetailProps) {
  const [application, setApplication] = useState<Application | null>(null)
  const [scoringRuns, setScoringRuns] = useState<ScoringRun[]>([])
  const [extractionArtifact, setExtractionArtifact] = useState<ExtractionArtifact | null>(null)
  const [aggregatedResult, setAggregatedResult] = useState<AggregatedResult | null>(null)
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
      const [appData, runsData, artifactData, resultData] = await Promise.all([
        mockAPI.getApplication(applicationId),
        mockAPI.getScoringRuns(applicationId),
        mockAPI.getExtractionArtifact(applicationId),
        mockAPI.getAggregatedResult(applicationId),
      ])
      setApplication(appData)
      setScoringRuns(runsData)
      setExtractionArtifact(artifactData)
      setAggregatedResult(resultData)
    } finally {
      setLoading(false)
    }
  }

  if (!applicationId || !application) return null

  const getScoreColor = (score: number) => {
    if (score >= 80) return 'text-success'
    if (score >= 60) return 'text-accent'
    return 'text-muted-foreground'
  }

  return (
    <Sheet open={open} onOpenChange={onClose}>
      <SheetContent side="right" className="w-full sm:max-w-3xl p-0">
        <div className="h-full flex flex-col">
          <SheetHeader className="p-6 border-b">
            <div className="flex items-start justify-between gap-4">
              <div className="flex-1">
                <SheetTitle className="text-2xl">{application.candidateName || application.candidateRef}</SheetTitle>
                {application.candidateEmail && (
                  <p className="text-sm text-muted-foreground mt-1">{application.candidateEmail}</p>
                )}
              </div>
              <StatusBadge status={application.status} />
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
          </SheetHeader>

          <ScrollArea className="flex-1">
            <div className="p-6 space-y-6">
              <Tabs defaultValue="overview">
                <TabsList className="grid w-full grid-cols-4">
                  <TabsTrigger value="overview">Overview</TabsTrigger>
                  <TabsTrigger value="runs">Runs ({scoringRuns.length})</TabsTrigger>
                  <TabsTrigger value="documents">Documents</TabsTrigger>
                  <TabsTrigger value="extraction">Extracted</TabsTrigger>
                </TabsList>

                <TabsContent value="overview" className="space-y-4 mt-6">
                  {aggregatedResult && (
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
                              {aggregatedResult.rationaleText}
                            </p>
                          </div>
                          <div>
                            <p className="text-sm font-medium mb-2">Recommendations</p>
                            <p className="text-sm text-muted-foreground leading-relaxed">
                              {aggregatedResult.recommendationsText}
                            </p>
                          </div>
                        </CardContent>
                      </Card>

                      <Card>
                        <CardHeader>
                          <CardTitle className="text-lg">Category Scores</CardTitle>
                        </CardHeader>
                        <CardContent>
                          <div className="space-y-3">
                            {Object.entries(aggregatedResult.finalSubScores).map(([category, score]) => (
                              <div key={category}>
                                <div className="flex items-center justify-between mb-1">
                                  <span className="text-sm">{category}</span>
                                  <span className={cn('font-mono font-semibold', getScoreColor(score))}>
                                    {score.toFixed(1)}
                                  </span>
                                </div>
                                <div className="h-2 bg-muted rounded-full overflow-hidden">
                                  <div
                                    className={cn(
                                      'h-full transition-all',
                                      score >= 80 ? 'bg-success' : score >= 60 ? 'bg-accent' : 'bg-muted-foreground'
                                    )}
                                    style={{ width: `${score}%` }}
                                  />
                                </div>
                              </div>
                            ))}
                          </div>
                        </CardContent>
                      </Card>

                      <Card>
                        <CardHeader>
                          <CardTitle className="text-lg">Must-Have Requirements</CardTitle>
                        </CardHeader>
                        <CardContent>
                          {aggregatedResult.allRuns[0]?.mustHaveResult && (
                            <div className="space-y-2">
                              {Object.entries(aggregatedResult.allRuns[0].mustHaveResult.details).map(([criterion, met]) => (
                                <div key={criterion} className="flex items-start gap-2">
                                  {met ? (
                                    <CheckCircle className="text-success mt-0.5" size={18} weight="fill" />
                                  ) : (
                                    <XCircle className="text-destructive mt-0.5" size={18} weight="fill" />
                                  )}
                                  <span className="text-sm">{criterion}</span>
                                </div>
                              ))}
                            </div>
                          )}
                        </CardContent>
                      </Card>
                    </>
                  )}
                </TabsContent>

                <TabsContent value="runs" className="space-y-4 mt-6">
                  {scoringRuns.map((run, index) => (
                    <Card key={run.runId}>
                      <CardHeader>
                        <div className="flex items-center justify-between">
                          <CardTitle className="text-lg">Run {run.runIndex}</CardTitle>
                          <Badge variant="outline" className="font-mono">
                            {run.overallScore.toFixed(1)}
                          </Badge>
                        </div>
                        <div className="flex gap-4 text-xs text-muted-foreground mt-2">
                          <span>Model: {run.modelDeploymentId}</span>
                          <span>Duration: {(run.durationMs / 1000).toFixed(1)}s</span>
                          {run.tokenUsage && <span>Tokens: {run.tokenUsage.totalTokens}</span>}
                        </div>
                      </CardHeader>
                      <CardContent className="space-y-4">
                        <div>
                          <p className="text-sm font-medium mb-2">Rationale</p>
                          <p className="text-sm text-muted-foreground">{run.rationale}</p>
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
                      </CardContent>
                    </Card>
                  ))}
                </TabsContent>

                <TabsContent value="documents" className="space-y-4 mt-6">
                  {application.documents.map((doc) => (
                    <Card key={doc.documentId}>
                      <CardContent className="p-4">
                        <div className="flex items-center gap-3">
                          <File size={32} className="text-accent" />
                          <div className="flex-1 min-w-0">
                            <p className="font-medium truncate">{doc.fileName}</p>
                            <p className="text-xs text-muted-foreground">
                              {(doc.sizeBytes / 1024).toFixed(1)} KB • {doc.mimeType}
                            </p>
                          </div>
                        </div>
                      </CardContent>
                    </Card>
                  ))}
                </TabsContent>

                <TabsContent value="extraction" className="mt-6">
                  {extractionArtifact && (
                    <Card>
                      <CardHeader>
                        <CardTitle className="text-lg">Extracted Content</CardTitle>
                        <p className="text-xs text-muted-foreground">
                          Confidence: {(extractionArtifact.extractionMetadata.confidence * 100).toFixed(0)}% • 
                          Tool: {extractionArtifact.extractionMetadata.toolVersion}
                        </p>
                      </CardHeader>
                      <CardContent>
                        <div className="bg-muted p-4 rounded-md font-mono text-sm whitespace-pre-wrap">
                          {extractionArtifact.markdown}
                        </div>
                      </CardContent>
                    </Card>
                  )}
                </TabsContent>
              </Tabs>
            </div>
          </ScrollArea>
        </div>
      </SheetContent>
    </Sheet>
  )
}
