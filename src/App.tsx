import { useState, useEffect } from 'react'
import { Toaster } from '@/components/ui/sonner'
import { DashboardView } from '@/components/DashboardView'
import { JobDetailView } from '@/components/JobDetailView'
import { ApplicationDetail } from '@/components/ApplicationDetail'
import { CreateJobDialog } from '@/components/CreateJobDialog'
import { UploadApplicationsDialog } from '@/components/UploadApplicationsDialog'
import { ManualReviewView } from '@/components/ManualReviewView'
import { LoginForm } from '@/components/LoginForm'
import { UserMenu } from '@/components/UserMenu'
import { ChangePasswordDialog } from '@/components/ChangePasswordDialog'
import { UserManagementDialog } from '@/components/UserManagementDialog'
import { AnalyticsView } from '@/components/AnalyticsView'
import type { Job, User } from '@/types'
import { initializeAuth, login, logout, getCurrentUser, requestPasswordReset } from '@/lib/auth'
import { toast } from 'sonner'

type View = 'dashboard' | 'job-detail' | 'manual-review' | 'analytics'

function App() {
  const [currentUser, setCurrentUser] = useState<User | null>(null)
  const [isAuthInitialized, setIsAuthInitialized] = useState(false)
  const [currentView, setCurrentView] = useState<View>('dashboard')
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null)
  const [selectedApplicationId, setSelectedApplicationId] = useState<string | null>(null)
  const [reviewApplicationId, setReviewApplicationId] = useState<string | null>(null)
  const [reviewJobId, setReviewJobId] = useState<string | null>(null)
  const [createJobDialogOpen, setCreateJobDialogOpen] = useState(false)
  const [editingJob, setEditingJob] = useState<Job | null>(null)
  const [uploadDialogOpen, setUploadDialogOpen] = useState(false)
  const [uploadJobId, setUploadJobId] = useState<string | null>(null)
  const [refreshKey, setRefreshKey] = useState(0)
  const [changePasswordOpen, setChangePasswordOpen] = useState(false)
  const [userManagementOpen, setUserManagementOpen] = useState(false)

  useEffect(() => {
    async function init() {
      await initializeAuth()
      const user = await getCurrentUser()
      setCurrentUser(user)
      setIsAuthInitialized(true)
    }
    init()
  }, [])

  const handleLogin = async (username: string, password: string): Promise<boolean> => {
    const user = await login(username, password)
    if (user) {
      setCurrentUser(user)
      toast.success(`Welcome back, ${user.fullName}!`)
      return true
    }
    return false
  }

  const handleLogout = async () => {
    await logout()
    setCurrentUser(null)
    setCurrentView('dashboard')
    toast.success('Signed out successfully')
  }

  const handleRequestPasswordReset = async () => {
    if (!currentUser) return
    
    try {
      await requestPasswordReset(currentUser.userId)
      toast.success('Password reset request submitted. An admin will review it shortly.')
    } catch (error) {
      toast.error('Failed to submit password reset request')
    }
  }

  if (!isAuthInitialized) {
    return <div className="min-h-screen bg-background flex items-center justify-center">
      <div className="text-muted-foreground">Loading...</div>
    </div>
  }

  if (!currentUser) {
    return <LoginForm onLogin={handleLogin} />
  }

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
    if (reviewJobId) {
      setSelectedJobId(reviewJobId)
    }
    setCurrentView('job-detail')
    setReviewApplicationId(null)
    setReviewJobId(null)
  }

  const handleApplicationClick = (applicationId: string, forceManualReview?: boolean) => {
    if (forceManualReview) {
      setSelectedApplicationId(applicationId)
    } else {
      setSelectedApplicationId(applicationId)
    }
  }

  const handleStartManualReview = (applicationId: string, jobId: string) => {
    setReviewApplicationId(applicationId)
    setReviewJobId(jobId)
    if (!selectedJobId) {
      setSelectedJobId(jobId)
    }
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

  const handleEditJob = (job: Job) => {
    setEditingJob(job)
    setCreateJobDialogOpen(true)
  }

  const handleCloseCreateJobDialog = () => {
    setCreateJobDialogOpen(false)
    setEditingJob(null)
  }

  return (
    <div className="min-h-screen bg-background">
      {currentView === 'dashboard' && (
        <div className="container mx-auto px-8 py-6">
          <div className="flex justify-between items-center mb-6">
            <div>
              <h1 className="text-3xl font-bold">Talent Matching Platform</h1>
              <p className="text-muted-foreground mt-1">
                {currentUser.role === 'admin' 
                  ? 'Administrator View' 
                  : currentUser.role === 'business_panel'
                  ? 'Business Panel Member - Candidate Approval'
                  : `${currentUser.department || 'Recruiter'} Dashboard`}
              </p>
            </div>
            <div className="flex items-center gap-2">
              {(currentUser.role === 'admin' || currentUser.role === 'recruiter') && (
                <button
                  onClick={() => setCurrentView('analytics')}
                  className="px-4 py-2 text-sm font-medium text-primary hover:text-primary/80 transition-colors"
                >
                  View Analytics
                </button>
              )}
              <UserMenu
                user={currentUser}
                onChangePassword={() => setChangePasswordOpen(true)}
                onRequestPasswordReset={handleRequestPasswordReset}
                onManageUsers={currentUser.role === 'admin' ? () => setUserManagementOpen(true) : undefined}
                onLogout={handleLogout}
              />
            </div>
          </div>
          <DashboardView
            key={refreshKey}
            onJobClick={handleJobClick}
            onCreateJob={() => setCreateJobDialogOpen(true)}
            onUploadApplications={handleUploadApplications}
            currentUser={currentUser}
          />
        </div>
      )}

      {currentView === 'analytics' && (
        <div className="container mx-auto px-8 py-6">
          <div className="flex justify-between items-center mb-6">
            <button
              onClick={() => setCurrentView('dashboard')}
              className="px-4 py-2 text-sm font-medium text-primary hover:text-primary/80 transition-colors"
            >
              ← Back to Dashboard
            </button>
            <UserMenu
              user={currentUser}
              onChangePassword={() => setChangePasswordOpen(true)}
              onRequestPasswordReset={handleRequestPasswordReset}
              onManageUsers={currentUser.role === 'admin' ? () => setUserManagementOpen(true) : undefined}
              onLogout={handleLogout}
            />
          </div>
          <AnalyticsView />
        </div>
      )}

      {currentView === 'job-detail' && selectedJobId && (
        <div className="container mx-auto px-8 py-6">
          <div className="flex justify-end mb-4">
            <UserMenu
              user={currentUser}
              onChangePassword={() => setChangePasswordOpen(true)}
              onRequestPasswordReset={handleRequestPasswordReset}
              onManageUsers={currentUser.role === 'admin' ? () => setUserManagementOpen(true) : undefined}
              onLogout={handleLogout}
            />
          </div>
          <JobDetailView
            key={`${selectedJobId}-${refreshKey}`}
            jobId={selectedJobId}
            onBack={handleBackToDashboard}
            onApplicationClick={handleApplicationClick}
            onUploadApplications={() => handleUploadApplications(selectedJobId)}
            onEditJob={handleEditJob}
            onStartManualReview={handleStartManualReview}
          />
        </div>
      )}

      {currentView === 'manual-review' && reviewApplicationId && reviewJobId && (
        <>
          <div className="absolute top-4 right-8 z-10">
            <UserMenu
              user={currentUser}
              onChangePassword={() => setChangePasswordOpen(true)}
              onRequestPasswordReset={handleRequestPasswordReset}
              onManageUsers={currentUser.role === 'admin' ? () => setUserManagementOpen(true) : undefined}
              onLogout={handleLogout}
            />
          </div>
          <ManualReviewView
            applicationId={reviewApplicationId}
            jobId={reviewJobId}
            onBack={handleBackToJobDetail}
          />
        </>
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
        onClose={handleCloseCreateJobDialog}
        onSuccess={handleCreateJobSuccess}
        editingJob={editingJob}
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

      <ChangePasswordDialog
        open={changePasswordOpen}
        onClose={() => setChangePasswordOpen(false)}
        onSuccess={() => setRefreshKey(prev => prev + 1)}
        userId={currentUser.userId}
      />

      {currentUser.role === 'admin' && (
        <UserManagementDialog
          open={userManagementOpen}
          onClose={() => setUserManagementOpen(false)}
          currentUserId={currentUser.userId}
        />
      )}

      <Toaster />
    </div>
  )
}

export default App