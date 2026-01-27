import { useEffect, useState } from 'react'
import { StatCard } from '@/components/StatCard'
import { JobCard } from '@/components/JobCard'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Plus, ChartBar, Briefcase, Queue, Gear, Funnel, X } from '@phosphor-icons/react'
import { mockAPI } from '@/lib/api'
import type { Job, SystemStats } from '@/types'

interface DashboardViewProps {
  onJobClick: (jobId: string) => void
  onCreateJob: () => void
  onUploadApplications: (jobId: string) => void
}

export function DashboardView({ onJobClick, onCreateJob, onUploadApplications }: DashboardViewProps) {
  const [jobs, setJobs] = useState<Job[]>([])
  const [filteredJobs, setFilteredJobs] = useState<Job[]>([])
  const [systemStats, setSystemStats] = useState<SystemStats | null>(null)
  const [loading, setLoading] = useState(true)
  
  const [filterDepartment, setFilterDepartment] = useState<string>('all')
  const [filterOrganization, setFilterOrganization] = useState<string>('all')
  const [filterTitle, setFilterTitle] = useState<string>('')
  const [showFilters, setShowFilters] = useState(false)

  useEffect(() => {
    loadData()
    const interval = setInterval(loadData, 10000)
    return () => clearInterval(interval)
  }, [])
  
  useEffect(() => {
    applyFilters()
  }, [jobs, filterDepartment, filterOrganization, filterTitle])

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
  
  const applyFilters = () => {
    let filtered = [...jobs]
    
    if (filterDepartment !== 'all') {
      filtered = filtered.filter(job => job.department === filterDepartment)
    }
    
    if (filterOrganization !== 'all') {
      filtered = filtered.filter(job => job.organization === filterOrganization)
    }
    
    if (filterTitle) {
      filtered = filtered.filter(job => 
        job.title.toLowerCase().includes(filterTitle.toLowerCase())
      )
    }
    
    setFilteredJobs(filtered)
  }
  
  const clearFilters = () => {
    setFilterDepartment('all')
    setFilterOrganization('all')
    setFilterTitle('')
  }
  
  const hasActiveFilters = filterDepartment !== 'all' || filterOrganization !== 'all' || filterTitle !== ''
  
  const departments = Array.from(new Set(jobs.map(j => j.department)))
  const organizations = Array.from(new Set(jobs.map(j => j.organization)))

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

      <div className="flex items-center justify-between mb-4">
        <h2 className="text-2xl font-semibold">Jobs</h2>
        <div className="flex gap-2">
          {hasActiveFilters && (
            <Button variant="outline" size="sm" onClick={clearFilters}>
              <X size={16} />
              Clear Filters
            </Button>
          )}
          <Button 
            variant={showFilters ? 'default' : 'outline'} 
            size="sm" 
            onClick={() => setShowFilters(!showFilters)}
          >
            <Funnel size={16} />
            {showFilters ? 'Hide' : 'Show'} Filters
          </Button>
        </div>
      </div>
      
      {showFilters && (
        <div className="bg-card border border-border rounded-lg p-4 mb-6">
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
            <div className="space-y-2">
              <label className="text-sm font-medium">Job Title</label>
              <Input
                placeholder="Search by title..."
                value={filterTitle}
                onChange={(e) => setFilterTitle(e.target.value)}
              />
            </div>
            <div className="space-y-2">
              <label className="text-sm font-medium">Department</label>
              <Select value={filterDepartment} onValueChange={setFilterDepartment}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="all">All Departments</SelectItem>
                  {departments.map(dept => (
                    <SelectItem key={dept} value={dept}>{dept}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-2">
              <label className="text-sm font-medium">Organization</label>
              <Select value={filterOrganization} onValueChange={setFilterOrganization}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="all">All Organizations</SelectItem>
                  {organizations.map(org => (
                    <SelectItem key={org} value={org}>{org}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          </div>
        </div>
      )}

      <div>
        <div className="grid grid-cols-1 lg:grid-cols-2 xl:grid-cols-3 gap-6">
          {filteredJobs.map((job) => (
            <JobCard
              key={job.jobId}
              job={job}
              onClick={() => onJobClick(job.jobId)}
              onUpload={() => onUploadApplications(job.jobId)}
            />
          ))}
          {filteredJobs.length === 0 && (
            <div className="col-span-full text-center py-12 text-muted-foreground">
              No jobs found matching your filters
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
