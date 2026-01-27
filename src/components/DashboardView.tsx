import { useEffect, useState } from 'react'
import { StatCard } from '@/components/StatCard'
import { JobCard } from '@/components/JobCard'
import { Button } from '@/components/ui/button'
import { Plus, ChartBar, Briefcase, Queue, Gear } from '@phosphor-icons/react'
import { mockAPI } from '@/lib/api'
import type { Job, SystemStats } from '@/types'

interface DashboardViewProps {
  onJobClick: (jobId: string) => void
  onCreateJob: () => void
  onUploadApplications: (jobId: string) => void
}

export function DashboardView({ onJobClick, onCreateJob, onUploadApplications }: DashboardViewProps) {
  const [jobs, setJobs] = useState<Job[]>([])
  const [systemStats, setSystemStats] = useState<SystemStats | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    loadData()
    const interval = setInterval(loadData, 10000)
    return () => clearInterval(interval)
  }, [])

  const loadData = async () => {
    try {
      const [jobsData, statsData] = await Promise.all([
        mockAPI.getJobs(),
        mockAPI.getSystemStats(),
      ])
      setJobs(jobsData)
      setSystemStats(statsData)
    } finally {
      setLoading(false)
    }
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center h-96">
        <Gear size={48} className="text-accent animate-spin" />
      </div>
    )
  }

  return (
    <div className="space-y-8">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Talent Matching Dashboard</h1>
          <p className="text-muted-foreground mt-1">
            Monitor job applications and AI-powered scoring across all positions
          </p>
        </div>
        <Button onClick={onCreateJob} size="lg">
          <Plus size={20} />
          Create Job
        </Button>
      </div>

      {systemStats && (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6">
          <StatCard
            label="Active Jobs"
            value={systemStats.activeJobs}
            icon={<Briefcase size={32} />}
            trend={{ value: 12, direction: 'up' }}
          />
          <StatCard
            label="Total Applications"
            value={systemStats.totalApplications.toLocaleString()}
            icon={<ChartBar size={32} />}
            trend={{ value: 8, direction: 'up' }}
          />
          <StatCard
            label="Processing Queue"
            value={systemStats.queuedApplications + systemStats.processingApplications}
            icon={<Queue size={32} />}
          />
          <StatCard
            label="Throughput/Hour"
            value={systemStats.averageThroughputPerHour}
            icon={<Gear size={32} />}
            trend={{ value: 5, direction: 'up' }}
          />
        </div>
      )}

      <div>
        <h2 className="text-2xl font-semibold mb-4">Jobs</h2>
        <div className="grid grid-cols-1 lg:grid-cols-2 xl:grid-cols-3 gap-6">
          {jobs.map((job) => (
            <JobCard
              key={job.jobId}
              job={job}
              onClick={() => onJobClick(job.jobId)}
              onUpload={() => onUploadApplications(job.jobId)}
            />
          ))}
        </div>
      </div>
    </div>
  )
}
