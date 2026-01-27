import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Progress } from '@/components/ui/progress'
import { ApplicationsTable } from '@/components/ApplicationsTable'
import { PipelineVisualizer } from '@/components/PipelineVisualizer'
import { StatusBadge } from '@/components/StatusBadge'
import { ArrowLeft, UploadSimple, Funnel } from '@phosphor-icons/react'
import { mockAPI } from '@/lib/api'
import type { Job, Application } from '@/types'

interface JobDetailViewProps {
  jobId: string
  onBack: () => void
  onApplicationClick: (applicationId: string) => void
  onUploadApplications: () => void
}

export function JobDetailView({ jobId, onBack, onApplicationClick, onUploadApplications }: JobDetailViewProps) {
  const [job, setJob] = useState<Job | null>(null)
  const [applications, setApplications] = useState<Application[]>([])
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    loadData()
    const interval = setInterval(loadData, 10000)
    return () => clearInterval(interval)
  }, [jobId])

  const loadData = async () => {
    try {
      const [jobData, appsData] = await Promise.all([
        mockAPI.getJob(jobId),
        mockAPI.getApplications(jobId),
      ])
      if (jobData) setJob(jobData)
      setApplications(appsData)
    } finally {
      setLoading(false)
    }
  }

  if (loading || !job) {
    return <div className="py-12 text-center">Loading...</div>
  }

  const stats = job.stats!
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

  const pipelineStages = [
    {
      name: 'Queued',
      status: stats.queued > 0 ? ('processing' as const) : ('completed' as const),
      count: stats.queued,
      total: stats.totalApplications,
    },
    {
      name: 'Extracting',
      status: stats.extracting > 0 ? ('processing' as const) : stats.queued === 0 ? ('completed' as const) : ('pending' as const),
      count: stats.extracting,
      total: stats.totalApplications,
    },
    {
      name: 'Scoring',
      status: stats.scoring > 0 ? ('processing' as const) : stats.extracting === 0 && stats.queued === 0 ? ('completed' as const) : ('pending' as const),
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
          <p className="text-muted-foreground mt-1">{job.department} • {job.organization}</p>
          <p className="text-sm text-muted-foreground mt-1">
            Posted {new Date(job.postingDate).toLocaleDateString()} • {daysOpen} days open
          </p>
        </div>
        <Button onClick={onUploadApplications}>
          <UploadSimple size={20} />
          Upload Applications
        </Button>
      </div>

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
        <Card>
          <CardContent className="p-4">
            <p className="text-sm text-muted-foreground mb-1">Longlist</p>
            <p className="text-2xl font-mono font-bold text-accent">{stats.longlistCount}</p>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="p-4">
            <p className="text-sm text-muted-foreground mb-1">Shortlist</p>
            <p className="text-2xl font-mono font-bold text-success">{stats.shortlistCount}</p>
          </CardContent>
        </Card>
        <Card>
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
            <Button variant="outline" size="sm">
              <Funnel size={16} />
              Filters
            </Button>
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
              <ApplicationsTable applications={applications} onApplicationClick={onApplicationClick} />
            </TabsContent>
            <TabsContent value="longlist" className="mt-6">
              <ApplicationsTable applications={longlistApps} onApplicationClick={onApplicationClick} />
            </TabsContent>
            <TabsContent value="shortlist" className="mt-6">
              <ApplicationsTable applications={shortlistApps} onApplicationClick={onApplicationClick} />
            </TabsContent>
            <TabsContent value="excluded" className="mt-6">
              <ApplicationsTable applications={excludedApps} onApplicationClick={onApplicationClick} />
            </TabsContent>
            <TabsContent value="review" className="mt-6">
              <ApplicationsTable applications={manualReviewApps} onApplicationClick={onApplicationClick} />
            </TabsContent>
          </Tabs>
        </CardContent>
      </Card>
    </div>
  )
}
