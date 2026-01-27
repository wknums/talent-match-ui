import { Badge } from '@/components/ui/badge'
import type { ApplicationStatus, JobStatus } from '@/types'
import { cn } from '@/lib/utils'

interface StatusBadgeProps {
  status: ApplicationStatus | JobStatus
  className?: string
}

export function StatusBadge({ status, className }: StatusBadgeProps) {
  const getStatusColor = (status: ApplicationStatus | JobStatus) => {
    switch (status) {
      case 'Completed':
        return 'bg-success text-success-foreground'
      case 'Processing':
      case 'Extracting':
      case 'Scoring':
      case 'Aggregating':
        return 'bg-accent text-accent-foreground'
      case 'Active':
        return 'bg-accent text-accent-foreground'
      case 'Queued':
      case 'Draft':
        return 'bg-muted text-muted-foreground'
      case 'ExtractionFailed':
      case 'ScoringFailed':
        return 'bg-destructive text-destructive-foreground'
      case 'NeedsManualReview':
        return 'bg-destructive text-destructive-foreground border-destructive'
      case 'Archived':
        return 'bg-muted text-muted-foreground opacity-60'
      default:
        return 'bg-muted text-muted-foreground'
    }
  }

  const formatStatus = (status: string) => {
    return status.replace(/([A-Z])/g, ' $1').trim()
  }

  return (
    <Badge
      className={cn(
        'transition-colors duration-150',
        getStatusColor(status),
        className
      )}
    >
      {formatStatus(status)}
    </Badge>
  )
}
