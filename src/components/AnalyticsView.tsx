import { useState, useEffect } from 'react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { api } from '@/lib/api'
import type { RecruiterAnalytics, DepartmentAnalytics } from '@/types'
import { Users, Clipboard, CheckCircle, Briefcase, TrendUp, Clock, WarningCircle } from '@phosphor-icons/react'

export function AnalyticsView() {
  const [loading, setLoading] = useState(true)
  const [recruiterData, setRecruiterData] = useState<RecruiterAnalytics[]>([])
  const [departmentData, setDepartmentData] = useState<DepartmentAnalytics[]>([])
  const [selectedDepartment, setSelectedDepartment] = useState<string>('all')
  const [loadError, setLoadError] = useState<string>()

  useEffect(() => {
    async function loadAnalytics() {
      setLoading(true)
      setLoadError(undefined)
      try {
        const [recruiters, departments] = await Promise.all([
          api.getRecruiterAnalytics(),
          api.getDepartmentAnalytics(),
        ])
        setRecruiterData(recruiters)
        setDepartmentData(departments)
      } catch (error) {
        console.error('Failed to load analytics:', error)
        setLoadError(error instanceof Error ? error.message : 'Recruiter analytics could not be loaded.')
      } finally {
        setLoading(false)
      }
    }
    loadAnalytics()
  }, [])

  const filteredRecruiters = selectedDepartment === 'all'
    ? recruiterData
    : recruiterData.filter(r => r.department === selectedDepartment)

  const totalStats = {
    applicationsInQueue: recruiterData.reduce((sum, r) => sum + r.applicationsInQueue, 0),
    manualReviewsPerformed: recruiterData.reduce((sum, r) => sum + r.manualReviewsPerformed, 0),
    shortlistRecommendations: recruiterData.reduce((sum, r) => sum + r.shortlistRecommendations, 0),
    activeJobs: recruiterData.reduce((sum, r) => sum + r.activeJobs, 0),
  }

  if (loading) {
    return (
      <div className="space-y-6">
        <Skeleton className="h-10 w-64" />
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
          {[...Array(4)].map((_, i) => (
            <Skeleton key={i} className="h-32" />
          ))}
        </div>
        <Skeleton className="h-96" />
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-3xl font-bold">Recruiter Analytics</h2>
        <p className="text-muted-foreground mt-1">
          Track performance metrics across departments and recruiters
        </p>
      </div>

      {loadError && (
        <Alert variant="destructive">
          <WarningCircle aria-hidden="true" />
          <AlertDescription>{loadError}</AlertDescription>
        </Alert>
      )}

      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Applications in Queue</CardTitle>
            <Clipboard className="h-5 w-5 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{totalStats.applicationsInQueue}</div>
            <p className="text-xs text-muted-foreground mt-1">
              Across all recruiters
            </p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Manual Reviews</CardTitle>
            <CheckCircle className="h-5 w-5 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{totalStats.manualReviewsPerformed}</div>
            <p className="text-xs text-muted-foreground mt-1">
              Total reviews completed
            </p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Shortlist Recommendations</CardTitle>
            <TrendUp className="h-5 w-5 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{totalStats.shortlistRecommendations}</div>
            <p className="text-xs text-muted-foreground mt-1">
              Candidates recommended
            </p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">Active Jobs</CardTitle>
            <Briefcase className="h-5 w-5 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{totalStats.activeJobs}</div>
            <p className="text-xs text-muted-foreground mt-1">
              Currently being managed
            </p>
          </CardContent>
        </Card>
      </div>

      <Tabs defaultValue="by-recruiter" className="space-y-4">
        <TabsList>
          <TabsTrigger value="by-recruiter">By Recruiter</TabsTrigger>
          <TabsTrigger value="by-department">By Department</TabsTrigger>
        </TabsList>

        <TabsContent value="by-recruiter" className="space-y-4">
          <Card>
            <CardHeader>
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle>Recruiter Performance</CardTitle>
                  <CardDescription>Individual metrics for each recruiter</CardDescription>
                </div>
                <Select value={selectedDepartment} onValueChange={setSelectedDepartment}>
                  <SelectTrigger className="w-48">
                    <SelectValue placeholder="Filter by department" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="all">All Departments</SelectItem>
                    {departmentData.map(dept => (
                      <SelectItem key={dept.department} value={dept.department}>
                        {dept.department}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            </CardHeader>
            <CardContent>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Recruiter</TableHead>
                    <TableHead>Department</TableHead>
                    <TableHead className="text-right">Applications in Queue</TableHead>
                    <TableHead className="text-right">Manual Reviews</TableHead>
                    <TableHead className="text-right">Shortlist Recs</TableHead>
                    <TableHead className="text-right">Active Jobs</TableHead>
                    <TableHead className="text-right">Avg. Time (hrs)</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {filteredRecruiters.length === 0 ? (
                    <TableRow>
                      <TableCell colSpan={7} className="text-center text-muted-foreground">
                        No recruiters found
                      </TableCell>
                    </TableRow>
                  ) : (
                    filteredRecruiters.map((recruiter) => (
                      <TableRow key={recruiter.recruiterId}>
                        <TableCell className="font-medium">
                          <div className="flex items-center gap-2">
                            <Users className="h-4 w-4 text-muted-foreground" />
                            {recruiter.recruiterName}
                          </div>
                        </TableCell>
                        <TableCell>
                          <Badge variant="secondary">{recruiter.department}</Badge>
                        </TableCell>
                        <TableCell className="text-right">
                          {recruiter.applicationsInQueue}
                        </TableCell>
                        <TableCell className="text-right">
                          {recruiter.manualReviewsPerformed}
                        </TableCell>
                        <TableCell className="text-right">
                          {recruiter.shortlistRecommendations}
                        </TableCell>
                        <TableCell className="text-right">
                          {recruiter.activeJobs}
                        </TableCell>
                        <TableCell className="text-right">
                          {recruiter.averageProcessingTime ? (
                            <div className="flex items-center justify-end gap-1">
                              <Clock className="h-3 w-3 text-muted-foreground" />
                              {recruiter.averageProcessingTime.toFixed(1)}
                            </div>
                          ) : (
                            <span className="text-muted-foreground">—</span>
                          )}
                        </TableCell>
                      </TableRow>
                    ))
                  )}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="by-department" className="space-y-4">
          <div className="grid gap-4">
            {departmentData.map((dept) => (
              <Card key={dept.department}>
                <CardHeader>
                  <div className="flex items-center justify-between">
                    <div>
                      <CardTitle>{dept.department}</CardTitle>
                      <CardDescription>
                        {dept.totalRecruiters} {dept.totalRecruiters === 1 ? 'recruiter' : 'recruiters'}
                      </CardDescription>
                    </div>
                    <div className="flex gap-6 text-sm">
                      <div className="text-center">
                        <div className="text-2xl font-bold text-primary">{dept.applicationsInQueue}</div>
                        <div className="text-xs text-muted-foreground">In Queue</div>
                      </div>
                      <div className="text-center">
                        <div className="text-2xl font-bold text-accent">{dept.manualReviewsPerformed}</div>
                        <div className="text-xs text-muted-foreground">Reviews</div>
                      </div>
                      <div className="text-center">
                        <div className="text-2xl font-bold text-success">{dept.shortlistRecommendations}</div>
                        <div className="text-xs text-muted-foreground">Shortlisted</div>
                      </div>
                      <div className="text-center">
                        <div className="text-2xl font-bold text-secondary">{dept.activeJobs}</div>
                        <div className="text-xs text-muted-foreground">Active Jobs</div>
                      </div>
                    </div>
                  </div>
                </CardHeader>
                <CardContent>
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>Recruiter</TableHead>
                        <TableHead className="text-right">Applications in Queue</TableHead>
                        <TableHead className="text-right">Manual Reviews</TableHead>
                        <TableHead className="text-right">Shortlist Recs</TableHead>
                        <TableHead className="text-right">Active Jobs</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {dept.recruiters.map((recruiter) => (
                        <TableRow key={recruiter.recruiterId}>
                          <TableCell className="font-medium">
                            {recruiter.recruiterName}
                          </TableCell>
                          <TableCell className="text-right">
                            {recruiter.applicationsInQueue}
                          </TableCell>
                          <TableCell className="text-right">
                            {recruiter.manualReviewsPerformed}
                          </TableCell>
                          <TableCell className="text-right">
                            {recruiter.shortlistRecommendations}
                          </TableCell>
                          <TableCell className="text-right">
                            {recruiter.activeJobs}
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </CardContent>
              </Card>
            ))}
          </div>
        </TabsContent>
      </Tabs>
    </div>
  )
}
