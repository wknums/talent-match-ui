import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Progress } from '@/components/ui/progress'
import { Sheet, SheetContent, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import {
  DraggableResizableDialog,
  DraggableDialogHeader,
  DraggableDialogBody,
} from '@/components/DraggableResizableDialog'
import { ApplicationsTable } from '@/components/ApplicationsTable'
import { DeleteJobDialog } from '@/components/DeleteJobDialog'
import { PipelineVisualizer } from '@/components/PipelineVisualizer'
import { StatusBadge } from '@/components/StatusBadge'
import { UploadRubricDialog } from '@/components/UploadRubricDialog'
import { PromptManagement } from '@/components/PromptManagement'
import { Tooltip, TooltipTrigger, TooltipContent } from '@/components/ui/tooltip'
import { ArrowLeft, UploadSimple, Funnel, PencilSimple, FileText, Lightning, Play, SpinnerGap, ArrowClockwise, Trash } from '@phosphor-icons/react'
import { api } from '@/lib/api'
import { toast } from 'sonner'
import type { Job, Application, ScoringPrompt, User } from '@/types'

interface JobDetailViewProps {
  jobId: string
  onBack: () => void
  onApplicationClick: (applicationId: string) => void
  onUploadApplications: () => void
  onEditJob: (job: Job) => void
  onStartManualReview?: (applicationId: string, jobId: string) => void
  currentUser?: User
}

type DrilldownType = 'longlist' | 'shortlist' | 'manual-review' | null

export function JobDetailView({ jobId, onBack, onApplicationClick, onUploadApplications, onEditJob, onStartManualReview, currentUser }: JobDetailViewProps) {
  const [job, setJob] = useState<Job | null>(null)
  const [applications, setApplications] = useState<Application[]>([])
  const [loading, setLoading] = useState(true)
  const [drilldownOpen, setDrilldownOpen] = useState(false)
  const [drilldownType, setDrilldownType] = useState<DrilldownType>(null)
  const [drilldownSnapshot, setDrilldownSnapshot] = useState<Application[]>([])
  const [uploadRubricOpen, setUploadRubricOpen] = useState(false)
  const [viewingDocument, setViewingDocument] = useState<'spec' | 'rubric' | null>(null)
  const [promptManagementOpen, setPromptManagementOpen] = useState(false)
  const [productionApprovedPrompt, setProductionApprovedPrompt] = useState<ScoringPrompt | null>(null)
  const [processing, setProcessing] = useState(false)
  const [retryingFailed, setRetryingFailed] = useState(false)
  const [reaggregating, setReaggregating] = useState(false)
  const [reaggregateMessage, setReaggregateMessage] = useState<string | null>(null)
  const [deleteDialogOpen, setDeleteDialogOpen] = useState(false)
  const [deletingJob, setDeletingJob] = useState(false)
  const [applicantSearch, setApplicantSearch] = useState('')

  useEffect(() => {
    loadData()
    const interval = setInterval(loadData, 10000)
    return () => clearInterval(interval)
  }, [jobId, applicantSearch])

  const loadData = async () => {
    try {
      const [jobData, appsData] = await Promise.all([
        api.getJob(jobId),
        api.getApplications(jobId, applicantSearch.trim().length > 0 ? { applicantName: applicantSearch } : undefined),
      ])
      if (jobData) setJob(jobData)
      setApplications(appsData)

      // Load production-approved prompt
      try {
        const prompts = await api.getPrompts(jobId)
        const approved = prompts.find((p) => p.status === 'production-approved') ?? null
        setProductionApprovedPrompt(approved)
      } catch {
        // Non-critical — leave as null
      }
    } finally {
      setLoading(false)
    }
  }

  if (loading || !job) {
    return <div className="py-12 text-center">Loading...</div>
  }

  const stats = job.stats!
  const createdByName = job.createdByName || 'Unknown User'
  const createdDate = new Date(job.createdAt).toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  })
  const completionPercentage = stats.totalApplications > 0
    ? Math.round((stats.completed / stats.totalApplications) * 100)
    : 0
    
  const daysOpen = Math.floor(
    (Date.now() - new Date(job.postingDate).getTime()) / (1000 * 60 * 60 * 24)
  )

  const longlistApps = applications.filter(
    (a) => a.finalDecision === 'Eligible' && a.finalScore && a.finalScore >= job.currentVersion.longlistThreshold
  )
  const shortlistApps = applications.filter(
    (a) => a.finalDecision === 'Eligible' && a.finalScore && a.finalScore >= job.currentVersion.shortlistThreshold
  )
  const excludedApps = applications.filter((a) => a.finalDecision === 'Excluded')
  const manualReviewApps = applications.filter(
    (a) => a.finalDecision === 'NeedsManualReview' || a.status === 'NeedsManualReview'
  )

  const openDrilldown = (type: DrilldownType) => {
    setDrilldownType(type)
    const snapshot = getDrilldownApplicationsForType(type)
    setDrilldownSnapshot(snapshot)
    setDrilldownOpen(true)
  }

  const getDrilldownApplicationsForType = (type: DrilldownType) => {
    switch (type) {
      case 'longlist':
        return longlistApps
      case 'shortlist':
        return shortlistApps
      case 'manual-review':
        return manualReviewApps
      default:
        return []
    }
  }

  const getDrilldownApplications = () => {
    return drilldownSnapshot
  }

  const getDrilldownTitle = () => {
    switch (drilldownType) {
      case 'longlist':
        return 'Longlist Applications'
      case 'shortlist':
        return 'Shortlist Applications'
      case 'manual-review':
        return 'Applications Needing Manual Review'
      default:
        return 'Applications'
    }
  }

  const pipelineStages = [
    {
      name: 'Queued',
      status: stats.queued > 0 ? ('processing' as const) : ('completed' as const),
      count: stats.queued,
      total: stats.totalApplications,
    },
    {
      name: 'Scoring',
      status: stats.scoring > 0 ? ('processing' as const) : stats.queued === 0 ? ('completed' as const) : ('pending' as const),
      count: stats.scoring,
      total: stats.totalApplications,
    },
    {
      name: 'Completed',
      status: stats.completed === stats.totalApplications ? ('completed' as const) : stats.scoring > 0 ? ('processing' as const) : ('pending' as const),
      count: stats.completed,
      total: stats.totalApplications,
    },
  ]

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-4">
        <Button variant="ghost" onClick={onBack}>
          <ArrowLeft size={20} />
        </Button>
        <div className="flex-1">
          <div className="flex items-center gap-3">
            <h1 className="text-3xl font-bold">{job.title}</h1>
            <StatusBadge status={job.status} />
          </div>
          <p className="text-sm font-mono text-accent font-semibold mt-1">{job.jobCode}</p>
          <p className="text-muted-foreground mt-1">{job.department} • {job.organization}</p>
          <p className="text-sm text-muted-foreground mt-1">
            Posted {new Date(job.postingDate).toLocaleDateString()} • {daysOpen} days open
          </p>
          <p className="text-sm text-muted-foreground mt-1">
            Created {createdDate} by {createdByName}
          </p>
        </div>
        <div className="flex items-center gap-3">
          <Button variant="outline" onClick={() => onEditJob(job)}>
            <PencilSimple size={20} />
            Edit Job
          </Button>
          {job.specDocumentId && (
            <Button variant="outline" onClick={() => setViewingDocument('spec')}>
              <FileText size={20} />
              View Job Spec
            </Button>
          )}
          {job.rubricDocumentId && (
            <Button variant="outline" onClick={() => setViewingDocument('rubric')}>
              <FileText size={20} />
              View Rubric
            </Button>
          )}
          <Button variant="outline" onClick={() => setUploadRubricOpen(true)}>
            <UploadSimple size={20} />
            Upload Rubric
          </Button>
          <Button variant="outline" onClick={() => setPromptManagementOpen(true)}>
            <Lightning size={20} />
            Prompt Management
          </Button>
          {currentUser?.role === 'admin' && (
            <Button
              variant="destructive"
              onClick={() => setDeleteDialogOpen(true)}
              disabled={deletingJob}
            >
              <Trash size={20} />
              Delete Job
            </Button>
          )}
          {productionApprovedPrompt ? (
            <>
              <Button onClick={onUploadApplications}>
                <UploadSimple size={20} />
                Upload Applications
              </Button>
              {stats.queued > 0 && (
                <Button
                  variant="outline"
                  disabled={processing}
                  onClick={async () => {
                    setProcessing(true)
                    try {
                      await api.processJob(jobId)
                      toast.success(`Processing started for ${stats.queued} queued application(s)`)
                      await loadData()
                    } catch {
                      toast.error('Failed to start processing')
                    } finally {
                      setProcessing(false)
                    }
                  }}
                >
                  {processing ? <SpinnerGap size={20} className="animate-spin" /> : <Play size={20} />}
                  Process Queued ({stats.queued})
                </Button>
              )}
              {stats.failed > 0 && (
                <Button
                  variant="outline"
                  disabled={retryingFailed}
                  onClick={async () => {
                    setRetryingFailed(true)
                    try {
                      await api.retryFailedApplications(jobId)
                      toast.success(`Retrying ${stats.failed} failed application(s)`)
                      await loadData()
                    } catch {
                      toast.error('Failed to retry applications')
                    } finally {
                      setRetryingFailed(false)
                    }
                  }}
                >
                  {retryingFailed ? <SpinnerGap size={20} className="animate-spin" /> : <ArrowClockwise size={20} />}
                  Retry Failed ({stats.failed})
                </Button>
              )}
            </>
          ) : (
            <Tooltip>
              <TooltipTrigger asChild>
                <span tabIndex={0}>
                  <Button disabled>
                    <UploadSimple size={20} />
                    Upload Applications
                  </Button>
                </span>
              </TooltipTrigger>
              <TooltipContent>
                A production-approved prompt is required before processing applications (FR-032)
              </TooltipContent>
            </Tooltip>
          )}
          <Button
            variant="outline"
            disabled={reaggregating}
            onClick={async () => {
              setReaggregating(true)
              setReaggregateMessage(null)
              try {
                const result = await api.reaggregateJob(jobId)
                setReaggregateMessage(`Updated ${result.updated} of ${result.total} applications.`)
                toast.success('Re-aggregation completed')
                await loadData()
              } catch {
                setReaggregateMessage('Failed to re-aggregate applications.')
                toast.error('Failed to re-aggregate applications')
              } finally {
                setReaggregating(false)
              }
            }}
          >
            {reaggregating ? <SpinnerGap size={20} className="animate-spin" /> : <ArrowClockwise size={20} />}
            Re-aggregate
          </Button>
        </div>
      </div>

      {reaggregateMessage && (
        <Card className="border-yellow-300 bg-yellow-50">
          <CardContent className="py-3 text-sm text-yellow-900">
            {reaggregateMessage}
          </CardContent>
        </Card>
      )}

      <Card>
        <CardHeader>
          <CardTitle>Processing Pipeline</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="mb-6">
            <div className="flex items-center justify-between text-sm mb-2">
              <span className="text-muted-foreground">Overall Progress</span>
              <span className="font-mono font-medium">{completionPercentage}%</span>
            </div>
            <Progress value={completionPercentage} className="h-3" />
          </div>
          <PipelineVisualizer stages={pipelineStages} className="justify-center" />
        </CardContent>
      </Card>

      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        <Card>
          <CardContent className="p-4">
            <p className="text-sm text-muted-foreground mb-1">Total Applications</p>
            <p className="text-2xl font-mono font-bold">{stats.totalApplications}</p>
          </CardContent>
        </Card>
        <Card 
          className="cursor-pointer transition-all duration-200 hover:shadow-lg hover:border-accent"
          onClick={() => openDrilldown('longlist')}
        >
          <CardContent className="p-4">
            <p className="text-sm text-muted-foreground mb-1">Longlist</p>
            <p className="text-2xl font-mono font-bold text-accent">{stats.longlistCount}</p>
          </CardContent>
        </Card>
        <Card 
          className="cursor-pointer transition-all duration-200 hover:shadow-lg hover:border-success"
          onClick={() => openDrilldown('shortlist')}
        >
          <CardContent className="p-4">
            <p className="text-sm text-muted-foreground mb-1">Shortlist</p>
            <p className="text-2xl font-mono font-bold text-success">{stats.shortlistCount}</p>
          </CardContent>
        </Card>
        <Card 
          className="cursor-pointer transition-all duration-200 hover:shadow-lg hover:border-destructive"
          onClick={() => openDrilldown('manual-review')}
        >
          <CardContent className="p-4">
            <p className="text-sm text-muted-foreground mb-1">Manual Review</p>
            <p className="text-2xl font-mono font-bold text-destructive">{stats.needsManualReview}</p>
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <CardTitle>Applications</CardTitle>
            <div className="flex items-center gap-2">
              <Input
                value={applicantSearch}
                onChange={(event) => setApplicantSearch(event.target.value)}
                placeholder="Search applicant name"
                className="w-64"
                aria-label="Search by applicant name"
              />
              {applicantSearch.trim().length > 0 && (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setApplicantSearch('')}
                >
                  Clear
                </Button>
              )}
              <Button variant="outline" size="sm">
                <Funnel size={16} />
                Filters
              </Button>
            </div>
          </div>
        </CardHeader>
        <CardContent>
          <Tabs defaultValue="all">
            <TabsList className="grid w-full grid-cols-5">
              <TabsTrigger value="all">
                All ({applications.length})
              </TabsTrigger>
              <TabsTrigger value="longlist">
                Longlist ({longlistApps.length})
              </TabsTrigger>
              <TabsTrigger value="shortlist">
                Shortlist ({shortlistApps.length})
              </TabsTrigger>
              <TabsTrigger value="excluded">
                Excluded ({excludedApps.length})
              </TabsTrigger>
              <TabsTrigger value="review">
                Manual Review ({manualReviewApps.length})
              </TabsTrigger>
            </TabsList>
            <TabsContent value="all" className="mt-6">
              <ApplicationsTable applications={applications} onApplicationClick={onApplicationClick} onStartManualReview={onStartManualReview} />
            </TabsContent>
            <TabsContent value="longlist" className="mt-6">
              <ApplicationsTable applications={longlistApps} onApplicationClick={onApplicationClick} onStartManualReview={onStartManualReview} />
            </TabsContent>
            <TabsContent value="shortlist" className="mt-6">
              <ApplicationsTable applications={shortlistApps} onApplicationClick={onApplicationClick} onStartManualReview={onStartManualReview} />
            </TabsContent>
            <TabsContent value="excluded" className="mt-6">
              <ApplicationsTable applications={excludedApps} onApplicationClick={onApplicationClick} onStartManualReview={onStartManualReview} />
            </TabsContent>
            <TabsContent value="review" className="mt-6">
              <ApplicationsTable applications={manualReviewApps} onApplicationClick={onApplicationClick} onStartManualReview={onStartManualReview} />
            </TabsContent>
          </Tabs>
        </CardContent>
      </Card>

      <Sheet open={drilldownOpen} onOpenChange={setDrilldownOpen}>
        <SheetContent side="right" className="w-full sm:max-w-4xl overflow-y-auto">
          <SheetHeader>
            <SheetTitle>{getDrilldownTitle()}</SheetTitle>
          </SheetHeader>
          <div className="mt-6">
            <ApplicationsTable 
              applications={getDrilldownApplications()} 
              onApplicationClick={(appId) => {
                setDrilldownOpen(false)
                onApplicationClick(appId)
              }}
              onStartManualReview={onStartManualReview ? (appId, jobId) => {
                onStartManualReview(appId, jobId)
                setDrilldownOpen(false)
              } : undefined}
            />
          </div>
        </SheetContent>
      </Sheet>

      <UploadRubricDialog
        open={uploadRubricOpen}
        jobId={jobId}
        onClose={() => setUploadRubricOpen(false)}
        onSuccess={async (rubric) => {
          const rubricDocId = `rubric-doc-${Date.now()}`
          await api.updateJobRubric(jobId, rubricDocId)
          toast.success(`Uploaded rubric with ${rubric.length} categories`)
          setUploadRubricOpen(false)
          loadData()
        }}
      />

      <Dialog open={viewingDocument !== null} onOpenChange={() => setViewingDocument(null)}>
        <DialogContent className="max-w-4xl max-h-[90vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle>
              {viewingDocument === 'spec' ? 'Job Specification Document' : 'Scoring Rubric Document'}
            </DialogTitle>
          </DialogHeader>
          <div className="mt-4 p-6 bg-muted rounded-lg">
            <p className="text-sm text-muted-foreground mb-4">
              Document ID: {viewingDocument === 'spec' ? job.specDocumentId : job.rubricDocumentId}
            </p>
            <div className="prose prose-sm max-w-none">
              <p className="text-muted-foreground italic">
                In a production environment, this would display the actual document content 
                (PDF viewer, Markdown renderer, or DOCX preview). The document would be fetched 
                from storage using the document ID.
              </p>
              <div className="mt-4 p-4 bg-background border border-border rounded">
                <h3 className="font-semibold mb-2">Document Preview Placeholder</h3>
                <p className="text-sm">
                  The original {viewingDocument === 'spec' ? 'job specification' : 'scoring rubric'} document 
                  would be rendered here in its original format.
                </p>
              </div>
            </div>
          </div>
        </DialogContent>
      </Dialog>

      <DraggableResizableDialog
        open={promptManagementOpen}
        onOpenChange={setPromptManagementOpen}
        defaultWidth={1200}
        defaultHeight={820}
        minWidth={900}
        minHeight={600}
      >
        <DraggableDialogHeader>
          <DialogTitle>Scoring Prompt Management</DialogTitle>
        </DraggableDialogHeader>
        <DraggableDialogBody className="px-6 pb-6">
          <PromptManagement
            jobId={jobId}
            hasApprovedRubric={job.currentVersion.rubricApprovalStatus === 'approved'}
            onPromptStatusChange={() => loadData()}
            onStartManualReview={onStartManualReview}
          />
        </DraggableDialogBody>
      </DraggableResizableDialog>

      <DeleteJobDialog
        open={deleteDialogOpen}
        jobTitle={job.title}
        applicationCount={stats.totalApplications}
        isDeleting={deletingJob}
        onOpenChange={setDeleteDialogOpen}
        onConfirm={async () => {
          setDeletingJob(true)
          try {
            await api.deleteJob(jobId)
            toast.success(`Deleted ${job.title}`)
            onBack()
          } catch (error) {
            toast.error(error instanceof Error ? error.message : 'Failed to delete job')
          } finally {
            setDeletingJob(false)
          }
        }}
      />
    </div>
  )
}
