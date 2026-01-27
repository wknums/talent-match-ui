import { useState } from 'react'
import { Toaster } from '@/components/ui/sonner'
import { DashboardView } from '@/components/DashboardView'
import { JobDetailView } from '@/components/JobDetailView'
import { ApplicationDetail } from '@/components/ApplicationDetail'
import { CreateJobDialog } from '@/components/CreateJobDialog'
import { UploadApplicationsDialog } from '@/components/UploadApplicationsDialog'

type View = 'dashboard' | 'job-detail'

function App() {
  const [currentView, setCurrentView] = useState<View>('dashboard')
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null)
  const [selectedApplicationId, setSelectedApplicationId] = useState<string | null>(null)
  const [createJobDialogOpen, setCreateJobDialogOpen] = useState(false)
  const [uploadDialogOpen, setUploadDialogOpen] = useState(false)
  const [uploadJobId, setUploadJobId] = useState<string | null>(null)
  const [refreshKey, setRefreshKey] = useState(0)

  const handleJobClick = (jobId: string) => {
    setSelectedJobId(jobId)
    setCurrentView('job-detail')
  }

  const handleBackToDashboard = () => {
    setCurrentView('dashboard')
    setSelectedJobId(null)
    setRefreshKey((prev) => prev + 1)
  }

  const handleApplicationClick = (applicationId: string) => {
    setSelectedApplicationId(applicationId)
  }

  const handleUploadApplications = (jobId: string) => {
    setUploadJobId(jobId)
    setUploadDialogOpen(true)
  }

  const handleUploadSuccess = () => {
    setRefreshKey((prev) => prev + 1)
  }

  const handleCreateJobSuccess = () => {
    setRefreshKey((prev) => prev + 1)
  }

  return (
    <div className="min-h-screen bg-background">
      <div className="container mx-auto px-8 py-6">
        {currentView === 'dashboard' && (
          <DashboardView
            key={refreshKey}
            onJobClick={handleJobClick}
            onCreateJob={() => setCreateJobDialogOpen(true)}
            onUploadApplications={handleUploadApplications}
          />
        )}

        {currentView === 'job-detail' && selectedJobId && (
          <JobDetailView
            key={`${selectedJobId}-${refreshKey}`}
            jobId={selectedJobId}
            onBack={handleBackToDashboard}
            onApplicationClick={handleApplicationClick}
            onUploadApplications={() => handleUploadApplications(selectedJobId)}
          />
        )}
      </div>

      <ApplicationDetail
        applicationId={selectedApplicationId}
        open={selectedApplicationId !== null}
        onClose={() => setSelectedApplicationId(null)}
      />

      <CreateJobDialog
        open={createJobDialogOpen}
        onClose={() => setCreateJobDialogOpen(false)}
        onSuccess={handleCreateJobSuccess}
      />

      <UploadApplicationsDialog
        open={uploadDialogOpen}
        jobId={uploadJobId}
        onClose={() => {
          setUploadDialogOpen(false)
          setUploadJobId(null)
        }}
        onSuccess={handleUploadSuccess}
      />

      <Toaster />
    </div>
  )
}

export default App