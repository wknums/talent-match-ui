import { useState } from 'react'
import { Toaster } from '@/components/ui/sonner'
import { DashboardView } from '@/components/DashboardView'
import { JobDetailView } from '@/components/JobDetailView'
import { ApplicationDetail } from '@/components/ApplicationDetail'
import { CreateJobDialog } from '@/components/CreateJobDialog'
import { UploadApplicationsDialog } from '@/components/UploadApplicationsDialog'
import { ManualReviewView } from '@/components/ManualReviewView'

type View = 'dashboard' | 'job-detail' | 'manual-review'

function App() {
  const [currentView, setCurrentView] = useState<View>('dashboard')
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null)
  const [selectedApplicationId, setSelectedApplicationId] = useState<string | null>(null)
  const [reviewApplicationId, setReviewApplicationId] = useState<string | null>(null)
  const [reviewJobId, setReviewJobId] = useState<string | null>(null)
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

  const handleBackToJobDetail = () => {
    setCurrentView('job-detail')
    setReviewApplicationId(null)
    setReviewJobId(null)
  }

  const handleApplicationClick = (applicationId: string) => {
    setSelectedApplicationId(applicationId)
  }

  const handleStartManualReview = (applicationId: string, jobId: string) => {
    setReviewApplicationId(applicationId)
    setReviewJobId(jobId)
    setCurrentView('manual-review')
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
      {currentView === 'dashboard' && (
        <div className="container mx-auto px-8 py-6">
          <DashboardView
            key={refreshKey}
            onJobClick={handleJobClick}
            onCreateJob={() => setCreateJobDialogOpen(true)}
            onUploadApplications={handleUploadApplications}
          />
        </div>
      )}

      {currentView === 'job-detail' && selectedJobId && (
        <div className="container mx-auto px-8 py-6">
          <JobDetailView
            key={`${selectedJobId}-${refreshKey}`}
            jobId={selectedJobId}
            onBack={handleBackToDashboard}
            onApplicationClick={handleApplicationClick}
            onUploadApplications={() => handleUploadApplications(selectedJobId)}
          />
        </div>
      )}

      {currentView === 'manual-review' && reviewApplicationId && reviewJobId && (
        <ManualReviewView
          applicationId={reviewApplicationId}
          jobId={reviewJobId}
          onBack={handleBackToJobDetail}
        />
      )}

      <ApplicationDetail
        applicationId={selectedApplicationId}
        open={selectedApplicationId !== null}
        onClose={() => setSelectedApplicationId(null)}
        onStartManualReview={(appId, jobId) => {
          setSelectedApplicationId(null)
          handleStartManualReview(appId, jobId)
        }}
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