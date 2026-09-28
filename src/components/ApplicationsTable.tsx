import { useState } from 'react'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/StatusBadge'
import { Badge } from '@/components/ui/badge'
import { ArrowRight, DotsThree, Warning, Pencil } from '@phosphor-icons/react'
import type { Application } from '@/types'
import { cn } from '@/lib/utils'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import {
  downloadApplicationExport,
  type ApplicationExportFormat,
} from '@/lib/application-export'
import { toast } from 'sonner'

interface ApplicationExportContext {
  jobTitle: string
  viewName: 'Longlist' | 'Shortlist' | 'Excluded'
}

interface ApplicationsTableProps {
  applications: Application[]
  onApplicationClick: (applicationId: string) => void
  onStartManualReview?: (applicationId: string, jobId: string) => void
  exportContext?: ApplicationExportContext
  className?: string
}

export function ApplicationsTable({
  applications,
  onApplicationClick,
  onStartManualReview,
  exportContext,
  className,
}: ApplicationsTableProps) {
  const [sortField, setSortField] = useState<'finalScore' | 'createdAt'>('createdAt')
  const [sortDirection, setSortDirection] = useState<'asc' | 'desc'>('desc')
  const [exporting, setExporting] = useState<ApplicationExportFormat | null>(null)

  const sortedApplications = [...applications].sort((a, b) => {
    if (sortField === 'finalScore') {
      const aScore = a.finalScore ?? -1
      const bScore = b.finalScore ?? -1
      return sortDirection === 'asc' ? aScore - bScore : bScore - aScore
    } else {
      const aTime = new Date(a.createdAt).getTime()
      const bTime = new Date(b.createdAt).getTime()
      return sortDirection === 'asc' ? aTime - bTime : bTime - aTime
    }
  })

  const toggleSort = (field: 'finalScore' | 'createdAt') => {
    if (sortField === field) {
      setSortDirection(sortDirection === 'asc' ? 'desc' : 'asc')
    } else {
      setSortField(field)
      setSortDirection('desc')
    }
  }

  const formatDate = (dateStr: string) => {
    const date = new Date(dateStr)
    return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  }

  const getScoreColor = (score: number) => {
    if (score >= 80) return 'text-success'
    if (score >= 60) return 'text-accent'
    return 'text-muted-foreground'
  }

  const exportApplications = async (format: ApplicationExportFormat) => {
    if (!exportContext) return

    setExporting(format)
    try {
      await downloadApplicationExport(
        sortedApplications,
        exportContext.jobTitle,
        exportContext.viewName,
        format,
      )
      toast.success(`${exportContext.viewName} exported as ${format.toUpperCase()}`)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown export error'
      toast.error(`Unable to export ${exportContext.viewName.toLowerCase()}: ${message}`)
    } finally {
      setExporting(null)
    }
  }

  return (
    <div className={cn('border rounded-lg', className)}>
      {exportContext && (
        <div className="flex justify-end border-b px-2 py-1">
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button
                variant="ghost"
                size="sm"
                aria-label={`Export ${exportContext.viewName.toLowerCase()} applications`}
                disabled={applications.length === 0 || exporting !== null}
              >
                <DotsThree size={20} weight="bold" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuLabel>Export to Excel</DropdownMenuLabel>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={() => void exportApplications('csv')}>
                Export as CSV
              </DropdownMenuItem>
              <DropdownMenuItem onSelect={() => void exportApplications('xlsx')}>
                Export as XLSX
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      )}
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Candidate</TableHead>
            <TableHead>Status</TableHead>
            <TableHead
              className="cursor-pointer hover:bg-muted/50"
              onClick={() => toggleSort('finalScore')}
            >
              Score {sortField === 'finalScore' && (sortDirection === 'asc' ? '↑' : '↓')}
            </TableHead>
            <TableHead>Variance</TableHead>
            <TableHead>Decision</TableHead>
            <TableHead
              className="cursor-pointer hover:bg-muted/50"
              onClick={() => toggleSort('createdAt')}
            >
              Submitted {sortField === 'createdAt' && (sortDirection === 'asc' ? '↑' : '↓')}
            </TableHead>
            <TableHead className="w-[180px]"></TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {sortedApplications.map((app) => (
            <TableRow
              key={app.applicationId}
              className={cn(
                'cursor-pointer transition-colors',
                app.flagged && 'border-l-4 border-l-destructive'
              )}
              onClick={() => onApplicationClick(app.applicationId)}
            >
              <TableCell>
                <div className="flex items-center gap-2">
                  {app.flagged && <Warning className="text-destructive" size={16} />}
                  <div>
                    <p className="font-medium">{app.candidateName || app.candidateRef}</p>
                    {app.candidateEmail && (
                      <p className="text-xs text-muted-foreground">{app.candidateEmail}</p>
                    )}
                  </div>
                </div>
              </TableCell>
              <TableCell>
                <StatusBadge status={app.status} />
              </TableCell>
              <TableCell>
                {app.finalScore !== undefined ? (
                  <span className={cn('font-mono font-semibold text-lg', getScoreColor(app.finalScore))}>
                    {app.finalScore.toFixed(3)}
                  </span>
                ) : (
                  <span className="text-muted-foreground">—</span>
                )}
              </TableCell>
              <TableCell>
                {app.variance !== undefined ? (
                  <span className={cn('font-mono text-sm', app.variance > 15 && 'text-destructive font-semibold')}>
                    ±{app.variance.toFixed(3)}
                  </span>
                ) : (
                  <span className="text-muted-foreground">—</span>
                )}
              </TableCell>
              <TableCell>
                {app.finalDecision && (
                  <Badge
                    variant={
                      app.finalDecision === 'Eligible'
                        ? 'default'
                        : app.finalDecision === 'Excluded'
                        ? 'secondary'
                        : 'destructive'
                    }
                  >
                    {app.finalDecision}
                  </Badge>
                )}
              </TableCell>
              <TableCell>
                <span className="text-sm text-muted-foreground font-mono">
                  {formatDate(app.createdAt)}
                </span>
              </TableCell>
              <TableCell>
                <div className="flex items-center gap-2">
                  {(app.finalDecision === 'NeedsManualReview' || app.finalDecision === 'Excluded' || app.status === 'NeedsManualReview') && onStartManualReview && (
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={(e) => {
                        e.stopPropagation()
                        onStartManualReview(app.applicationId, app.jobId)
                      }}
                      className="text-destructive border-destructive hover:bg-destructive hover:text-destructive-foreground"
                    >
                      <Pencil size={16} />
                      Review
                    </Button>
                  )}
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={(e) => {
                      e.stopPropagation()
                      onApplicationClick(app.applicationId)
                    }}
                  >
                    <ArrowRight size={16} />
                  </Button>
                </div>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {sortedApplications.length === 0 && (
        <div className="py-12 text-center text-muted-foreground">
          No applications found
        </div>
      )}
    </div>
  )
}
