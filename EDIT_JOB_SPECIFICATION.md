# EDIT JOB FEATURE SPECIFICATION
## Stack A (React/TypeScript) vs Stack B (.NET Blazor) - Feature Comparison & Implementation Guide

## EXECUTIVE SUMMARY
The React/TypeScript frontend (Stack A) has a fully functional "Edit Job" feature allowing users to modify job configurations. The .NET Blazor backend (Stack B) has the API infrastructure but lacks the Blazor UI component. This specification outlines how to build the Blazor Edit Job UI.

---

## 1. STACK A EDIT JOB UI FEATURE (React/TypeScript)

### 1.1 Entry Point: Edit Button in Job Detail
**File:** src/components/JobDetailView.tsx (Lines 176-179)
**Pattern:** Pencil icon button in job header

The Edit button passes the full job object to onEditJob callback:
```
<Button variant="outline" onClick={() => onEditJob(job)}>
  <PencilSimple size={20} />
  Edit Job
</Button>
```

### 1.2 Dialog Component: CreateJobDialog.tsx (Dual-Purpose)
**File:** src/components/CreateJobDialog.tsx
**Key Feature:** Single component handles both CREATE and EDIT via editingJob prop

#### Form Fields:
- title, jobCode, department, organization, postingDate
- jobDescription (textarea)
- rubricCategories (array with: id, name, description, weight)
- mustHaves (array with: id, criterion, description)
- desiredCriteria (array with: id, qualification, description)
- runsPerApplication (number, default 3)
- aggregationStrategy (median, mean, weighted)
- longlistThreshold (0-100)
- shortlistThreshold (0-100)

### 1.3 Pre-Population Logic (Lines 58-74)
When editingJob prop is set, a useEffect hook populates all form fields:

```tsx
useEffect(() => {
  if (editingJob) {
    setTitle(editingJob.title)
    setJobCode(editingJob.jobCode)
    setDepartment(editingJob.department)
    setOrganization(editingJob.organization)
    setPostingDate(editingJob.postingDate.split('T')[0])
    setRubricCategories(editingJob.currentVersion.rubric)
    setMustHaves(editingJob.currentVersion.mustHaves)
    setDesiredCriteria(editingJob.currentVersion.desiredCriteria || [])
    setJobDescription(editingJob.jobDescription || '')
    setRunsPerApplication(String(editingJob.currentVersion.runsPerApplication))
    setAggregationStrategy(editingJob.currentVersion.aggregationStrategy)
    setLonglistThreshold(String(editingJob.currentVersion.longlistThreshold))
    setShortlistThreshold(String(editingJob.currentVersion.shortlistThreshold))
  }
}, [editingJob])
```

### 1.4 Submit Handler (Lines 342-359)
Conditional logic checks if editingJob exists:

**CREATE Path:**
```tsx
await api.createJob({
  title, jobCode, department, organization, postingDate,
  rubric: validCategories,
  mustHaves: mustHaves.filter(m => m.criterion),
  desiredCriteria: desiredCriteria.filter(d => d.qualification),
  jobDescription, runsPerApplication, aggregationStrategy,
  longlistThreshold, shortlistThreshold, specDocumentId
})
```

**EDIT Path:**
```tsx
await api.updateJob(editingJob.jobId, {
  title, jobCode, department, organization, postingDate,
  rubric: validCategories, mustHaves, desiredCriteria,
  jobDescription, runsPerApplication, aggregationStrategy,
  longlistThreshold, shortlistThreshold,
  specDocumentId: uploadedSpecDocId || undefined,
  rubricDocumentId: editingJob.rubricDocumentId  // preserves original
})
toast.success('Job updated successfully')
```

### 1.5 API Call: updateJob (src/lib/api-real.ts)
**URL:** PUT /api/jobs/{jobId}/config
**Method:** Calls UpdateJobConfigCommand on backend

The updateJob method sends data to PUT endpoint, then fetches updated job:
```typescript
async updateJob(jobId: string, data: {
  title, department, organization, postingDate,
  rubric: RubricCategory[],
  mustHaves: MustHave[],
  desiredCriteria?: DesiredCriteria[],
  jobDescription?: string,
  runsPerApplication: number,
  aggregationStrategy: AggregationStrategy,
  longlistThreshold: number,
  shortlistThreshold: number,
  specDocumentId?: string,
  rubricDocumentId?: string,
  jobCode?: string
}): Promise<Job> {
  // PUT request to config endpoint
  await fetchJSON(/api/jobs//config, {
    method: 'PUT',
    body: JSON.stringify(data),
  })
  // Fetch and return updated job
  const job = await this.getJob(jobId)
  return job!
}
```

---

## 2. STACK B BACKEND - EDIT JOB INFRASTRUCTURE

### 2.1 PUT Endpoint (JobsEndpoints.cs, Lines 176-183)
**Endpoint:** PUT /api/jobs/{jobId}/config
**Request Type:** UpdateJobConfigRequest
**Response:** JobConfigVersion (the new version created)

```csharp
group.MapPut("/{jobId}/config", async (string jobId, UpdateJobConfigRequest request, ISender mediator) =>
{
    var config = await mediator.Send(new UpdateJobConfigCommand(
        jobId, request.RubricJson, request.MustHaveCriteriaJson, request.DesiredCriteriaJson,
        request.ScoringRunCount, request.AggregationStrategy, request.LonglistThreshold,
        request.ShortlistThreshold, request.VarianceThreshold));
    return Results.Ok(config);
});
```

**UpdateJobConfigRequest (Lines 211-214):**
```csharp
public record UpdateJobConfigRequest(
    string? RubricJson, string? MustHaveCriteriaJson, string? DesiredCriteriaJson,
    int ScoringRunCount, string AggregationStrategy, double LonglistThreshold,
    double ShortlistThreshold, double VarianceThreshold);
```

**IMPORTANT:** Does NOT include title, department, organization, or postingDate

### 2.2 UpdateJobConfigCommand Handler
**File:** dotnet/src/Application/Jobs/Commands/UpdateJobConfigCommand.cs

**Design Pattern:** Creates a NEW JobConfigVersion instead of modifying existing
- Increments VersionNumber
- Sets Job.CurrentConfigVersionId to new version ID
- Updates Job.UpdatedAt timestamp
- Preserves full edit history

```csharp
public async Task<JobConfigVersion> Handle(UpdateJobConfigCommand request, CancellationToken cancellationToken)
{
    var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken)
        ?? throw new InvalidOperationException($"Job '{request.JobId}' not found.");

    var versions = await _jobRepository.GetConfigVersionsAsync(request.JobId, cancellationToken);
    var nextVersion = versions.Count + 1;

    var configVersion = new JobConfigVersion
    {
        JobId = request.JobId,
        VersionNumber = nextVersion,
        RubricJson = request.RubricJson ?? "[]",
        MustHaveCriteriaJson = request.MustHaveCriteriaJson ?? "[]",
        DesiredCriteriaJson = request.DesiredCriteriaJson ?? "[]",
        ScoringRunCount = request.ScoringRunCount,
        AggregationStrategy = request.AggregationStrategy,
        LonglistThreshold = request.LonglistThreshold,
        ShortlistThreshold = request.ShortlistThreshold,
        VarianceThreshold = request.VarianceThreshold
    };

    job.CurrentConfigVersionId = configVersion.Id;
    job.UpdatedAt = DateTime.UtcNow;

    await _jobRepository.AddConfigVersionAsync(configVersion, cancellationToken);
    await _jobRepository.UpdateAsync(job, cancellationToken);

    return configVersion;
}
```

### 2.3 ApiClient.UpdateJobConfigAsync
**File:** dotnet/src/Web.Client/Services/ApiClient.cs (Lines 125-129)

```csharp
public async Task<bool> UpdateJobConfigAsync(string jobId, UpdateConfigDto config)
{
    var response = await _http.PutAsJsonAsync($"/api/jobs/{jobId}/config", config);
    return response.IsSuccessStatusCode;
}
```

**UpdateConfigDto (Line 291):**
```csharp
public record UpdateConfigDto(
    string? RubricJson, string? MustHaveCriteriaJson, string? DesiredCriteriaJson,
    int ScoringRunCount, string AggregationStrategy, double LonglistThreshold,
    double ShortlistThreshold, double VarianceThreshold);
```

### 2.4 Data Models
**Job Entity:** dotnet/src/Domain/Entities/Job.cs
- Id, JobCode, Title, Department, Organisation, PostingDate, Status
- CurrentConfigVersionId (pointer to active JobConfigVersion)
- CreatedBy, JobDescription, CreatedAt, UpdatedAt
- Navigation: ConfigVersions (ICollection<JobConfigVersion>)

**JobConfigVersion Entity:** dotnet/src/Domain/Entities/JobConfigVersion.cs
- Id, JobId, VersionNumber
- RubricJson (JSON array)
- MustHaveCriteriaJson (JSON array)
- DesiredCriteriaJson (JSON array)
- ScoringRunCount (default 3)
- AggregationStrategy (median, mean, weighted)
- LonglistThreshold (0-100)
- ShortlistThreshold (0-100)
- VarianceThreshold (0-100)
- CreatedAt

---

## 3. STACK B CURRENT UI - WHAT'S MISSING

### 3.1 JobDetail.razor Status
**File:** dotnet/src/Web.Client/Pages/JobDetail.razor
- Shows job title, department, organization, status
- Has buttons: Upload Applications, Manage Prompts, Process Applications, Delete Job (admin)
- **MISSING:** Edit Job button
- **MISSING:** EditJobDialog component

### 3.2 Data Loading (Lines 142-150)
```csharp
protected override async Task OnInitializedAsync()
{
    var currentUser = await Api.GetCurrentUserAsync();
    isAdmin = currentUser?.Role == "admin";
    
    job = await Api.GetJobAsync(JobId);
    allApps = await Api.GetApplicationsAsync(JobId);
    await LoadPromptStatus();
}
```

**Issue:** GetJobAsync returns minimal JobDto without rubric/criteria details

**Current GET Response (Lines 164-174 JobsEndpoints.cs):**
```csharp
return Results.Ok(new {
    job.Id, job.JobCode, job.Title, job.Department,
    job.Organisation, job.PostingDate, job.Status,
    job.CurrentConfigVersionId, job.JobDescription, job.CreatedBy, job.CreatedAt
});
```

Lacks RubricJson, MustHaveCriteriaJson, DesiredCriteriaJson!

---

## 4. IMPLEMENTATION PLAN FOR STACK B

### Step 1: Add Edit Button to JobDetail.razor
Add this to the button group (around line 14):
```razor
<button @onclick="OpenEditJobDialog" style="padding: 0.5rem 1rem; background: #3b82f6; color: white; border: none; border-radius: 4px;">
    ✎ Edit Job
</button>
```

### Step 2: Add Dialog State Variables
In JobDetail.razor @code section:
```csharp
private bool showEditDialog = false;

private void OpenEditJobDialog()
{
    showEditDialog = true;
}

private void CloseEditJobDialog()
{
    showEditDialog = false;
}

private async Task HandleEditJobSaved()
{
    showEditDialog = false;
    job = await Api.GetJobAsync(JobId);
    StateHasChanged();
}
```

### Step 3: Add Dialog Markup to JobDetail.razor
After existing content:
```razor
@if (showEditDialog && job != null)
{
    <EditJobDialog Job="@job"
                   Visible="true"
                   OnCancel="CloseEditJobDialog"
                   OnSave="HandleEditJobSaved" />
}
```

### Step 4: Create EditJobDialog.razor Component
**File:** dotnet/src/Web.Client/Components/EditJobDialog.razor

Key features needed:
- Draggable dialog window (like CreateJobDialog)
- Tabs for: Basic Info, Scoring Config, Rubric, Must-Haves, Desired Criteria
- Load current config from jobConfigVersionId
- Pre-populate all fields
- Validate form (weights sum to 1.0)
- Call Api.UpdateJobConfigAsync on save
- Handle errors gracefully

### Step 5: Enhance GET Job Endpoint (Optional but Recommended)
Add a new endpoint to return full config details:
```csharp
group.MapGet("/{jobId}/detail", async (string jobId, ISender mediator) =>
{
    var job = await mediator.Send(new GetJobDetailQuery(jobId));
    if (job is null) return Results.NotFound();
    
    var config = await mediator.Send(new GetJobConfigQuery(jobId));
    
    return Results.Ok(new {
        job.Id, job.JobCode, job.Title, job.Department,
        job.Organisation, job.PostingDate, job.Status,
        job.CurrentConfigVersionId, job.JobDescription, job.CreatedBy, job.CreatedAt,
        Config = config  // Include full config
    });
});
```

Or make GetJobConfigAsync call in ApiClient:
```csharp
public async Task<JobConfigDto?> GetJobConfigAsync(string jobId)
{
    try { return await _http.GetFromJsonAsync<JobConfigDto>($"/api/jobs/{jobId}/config"); }
    catch { return null; }
}
```

---

## 5. IMPLEMENTATION CHECKLIST

### Backend
- [x] PUT /api/jobs/{jobId}/config endpoint exists
- [x] UpdateJobConfigCommand handler exists
- [x] Api.UpdateJobConfigAsync exists
- [ ] (Optional) Create /api/jobs/{jobId}/detail endpoint with full config
- [ ] (Optional) Add GetJobConfigQuery

### Frontend - JobDetail.razor
- [ ] Add Edit button to button group
- [ ] Add openEditDialog state variable
- [ ] Add OpenEditJobDialog method
- [ ] Add CloseEditJobDialog method
- [ ] Add HandleEditJobSaved method
- [ ] Add EditJobDialog markup with @if condition
- [ ] Import EditJobDialog component

### New Component - EditJobDialog.razor
- [ ] Create file in dotnet/src/Web.Client/Components/
- [ ] Add @parameters (Job, Visible, OnCancel, OnSave)
- [ ] Create form with 5 tabs
- [ ] Implement field binding with @bind
- [ ] Load config from Job.CurrentConfigVersionId
- [ ] Implement ValidateForm method
- [ ] Implement HandleSave method calling UpdateJobConfigAsync
- [ ] Implement HandleCancel method
- [ ] Style with draggable dialog pattern (matching CreateJobDialog)

### Models
- [ ] Create RubricCategoryBlazor, MustHaveBlazor, DesiredCriteriaBlazor classes for JSON deserialization

---

## 6. FORM FIELD MAPPING

**Dialog State:**
- title (text) - from job.Title
- department (text) - from job.Department
- organisation (text) - from job.Organisation
- postingDate (date) - from job.PostingDate
- jobDescription (textarea) - from job.JobDescription
- rubricCategories (array) - parse from config.RubricJson
- mustHaves (array) - parse from config.MustHaveCriteriaJson
- desiredCriteria (array) - parse from config.DesiredCriteriaJson
- scoringRunCount (number) - from config.ScoringRunCount
- aggregationStrategy (select) - from config.AggregationStrategy
- longlistThreshold (number) - from config.LonglistThreshold
- shortlistThreshold (number) - from config.ShortlistThreshold
- varianceThreshold (number) - from config.VarianceThreshold

**Fields to Send in UpdateConfigDto:**
- RubricJson (JSON.stringify)
- MustHaveCriteriaJson (JSON.stringify)
- DesiredCriteriaJson (JSON.stringify)
- ScoringRunCount
- AggregationStrategy
- LonglistThreshold
- ShortlistThreshold
- VarianceThreshold

Note: Title, Department, Organisation, PostingDate are NOT sent in UpdateJobConfigRequest

---

## 7. KEY INSIGHTS & RECOMMENDATIONS

1. **Config Versioning:** Every edit creates a new JobConfigVersion. This provides full audit trail.
2. **Title/Department/Org Update:** If these should be editable, need a separate endpoint (would modify Job entity, not JobConfigVersion)
3. **Rubric Weights:** Must validate that weights sum to 1.0 (within 0.01 tolerance)
4. **JSON Serialization:** Blazor will need to serialize arrays to JSON strings before sending
5. **Pre-Population Source:** Use Job.CurrentConfigVersionId to fetch the active JobConfigVersion
6. **Error Handling:** Show user-friendly messages if validation fails or API call fails
7. **Consistency:** Match the draggable dialog pattern and styling from CreateJobDialog.razor

