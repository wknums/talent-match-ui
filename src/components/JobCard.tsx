import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Progress } from '@/components/ui/progress'
import { StatusBadge } from '@/components/StatusBadge'
import { Briefcase, ArrowRight, UploadSimple } from '@phosphor-icons/react'
import type { Job } from '@/types'
import { cn } from '@/lib/utils'

interface JobCardProps {
  job: Job
  onClick?: () => void
  onUpload?: () => void
  className?: string
}

export function JobCard({ job, onClick, onUpload, className }: JobCardProps) {
  const stats = job.stats

  const completionPercentage = stats
    ? Math.round((stats.completed / stats.totalApplications) * 100) || 0
    : 0

  const isProcessing = job.status === 'Processing'
  
  const daysOpen = Math.floor(
    (Date.now() - new Date(job.postingDate).getTime()) / (1000 * 60 * 60 * 24)
  )

  return (
    <Card
      className={cn(
        'transition-all duration-200 hover:shadow-lg cursor-pointer',
        'border-l-4',
        isProcessing && 'border-l-accent',
        job.status === 'Completed' && 'border-l-success',
        job.status === 'Active' && 'border-l-primary',
        job.status === 'Draft' && 'border-l-muted',
        className
      )}
      onClick={onClick}
    >
      <CardHeader>
        <div className="flex items-start justify-between gap-4">
          <div className="flex items-start gap-3 flex-1">
            <Briefcase size={24} className="text-primary mt-1" />
            <div className="flex-1 min-w-0">
              <CardTitle className="text-xl mb-1 truncate">{job.title}</CardTitle>
              <p className="text-sm text-muted-foreground">{job.department} • {job.organization}</p>
              <p className="text-xs text-muted-foreground mt-1">{daysOpen} days open</p>
            </div>
          </div>
          <StatusBadge status={job.status} />
        </div>
      </CardHeader>
      <CardContent>
        {stats && stats.totalApplications > 0 && (
          <>
            <div className="mb-4">
              <div className="flex items-center justify-between text-sm mb-2">
                <span className="text-muted-foreground">Progress</span>
                <span className="font-mono font-medium">{completionPercentage}%</span>
              </div>
              <Progress value={completionPercentage} className="h-2" />
            </div>

            <div className="grid grid-cols-2 gap-3 mb-4">
              <div className="flex flex-col gap-1">
                <span className="text-xs text-muted-foreground">Total Applications</span>
                <span className="text-lg font-mono font-semibold">{stats.totalApplications}</span>
              </div>
              <div className="flex flex-col gap-1">
                <span className="text-xs text-muted-foreground">Completed</span>
                <span className="text-lg font-mono font-semibold text-success">{stats.completed}</span>
              </div>
              <div className="flex flex-col gap-1">
                <span className="text-xs text-muted-foreground">Shortlist</span>
                <span className="text-lg font-mono font-semibold text-accent">{stats.shortlistCount}</span>
              </div>
              <div className="flex flex-col gap-1">
                <span className="text-xs text-muted-foreground">Failed</span>
                <span className="text-lg font-mono font-semibold text-destructive">{stats.failed}</span>
              </div>
            </div>
          </>
        )}

        {(!stats || stats.totalApplications === 0) && (
          <div className="py-4 text-center text-sm text-muted-foreground">
            No applications uploaded yet
          </div>
        )}

        <div className="flex gap-2">
          <Button
            variant="outline"
            size="sm"
            className="flex-1"
            onClick={(e) => {
              e.stopPropagation()
              onUpload?.()
            }}
          >
            <UploadSimple size={16} />
            Upload Apps
          </Button>
          <Button
            size="sm"
            className="flex-1"
            onClick={(e) => {
              e.stopPropagation()
              onClick?.()
            }}
          >
            View Details
            <ArrowRight size={16} />
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}
