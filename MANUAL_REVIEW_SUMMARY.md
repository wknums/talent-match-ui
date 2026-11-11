# Manual Review & Rubric Upload - Implementation Summary

## Overview

This iteration adds comprehensive manual review functionality and rubric upload capabilities to the Talent Matching Platform, enabling recruiters to:

1. Upload scoring rubrics from documents (PDF, Markdown, DOCX)
2. Perform detailed manual reviews with a three-pane interface
3. Track all manual review changes in an audit trail

## New Features

### 1. Rubric Upload Dialog (`UploadRubricDialog.tsx`)

**Purpose**: Allow recruiters to upload a scoring rubric document instead of manually entering rubric categories.

**Functionality**:
- Accepts PDF, Markdown (.md), and DOCX files
- Uses LLM to extract rubric categories with weights and descriptions
- Validates that weights sum to 1.0
- Displays extracted categories for review before applying
- Integrates seamlessly into job creation and job detail views

**Usage**:
- In Job Detail view: Click "Upload Rubric" button
- File is processed using `window.spark.llm` to extract structured rubric data
- Extracted categories are displayed for confirmation
- On success, rubric is saved and associated with the job

### 2. Manual Review Interface (`ManualReviewView.tsx`)

**Purpose**: Provide recruiters with a comprehensive three-pane interface for manual candidate evaluation.

**Three-Pane Layout**:

#### Pane A: Job Specification (Left)
- Job title, department, and organization
- Must-have requirements list
- Scoring criteria with category weights
- Longlist and shortlist thresholds
- **Fully scrollable** to accommodate lengthy job specs

#### Pane B: Scoring Rubric (Center)
- Each rubric category with:
  - Editable point allocation (0 to max points)
  - Weight percentage display
  - Category description
  - Comment text area for evaluation notes
- Overall review comments section
- Real-time score calculation display
- **Fully scrollable** for multiple categories

#### Pane C: Application Content (Right)
- Candidate name and contact information
- AI-generated score and variance (for comparison)
- Extracted application content (Markdown)
- Attached documents list
- **Fully scrollable** to review entire application

**Key Features**:
- **Live Score Calculation**: Total score updates in real-time as points are allocated
- **Persistent State**: Review data saved to `spark.kv` for resume capability
- **Audit Tracking**: Every change generates an audit trail entry
- **Save Functionality**: Explicit save button to persist all changes

### 3. Audit Trail Implementation

**Purpose**: Track all manual review changes for compliance and transparency.

**Tracked Events**:
- `points_allocated`: When recruiter updates points for a category
  - Records: category name, previous value, new value
- `comment_added`: When recruiter adds/updates evaluation comments
  - Records: category name, comment text
- `score_adjustment`: When overall score is manually adjusted
  - Records: previous score, new score

**Audit Entry Structure**:
```typescript
{
  entryId: string           // Unique identifier
  applicationId: string     // Link to application
  reviewerId: string        // GitHub login of reviewer
  reviewerName: string      // Display name
  timestamp: string         // ISO timestamp
  changeType: string        // Type of change
  categoryId?: string       // Rubric category (if applicable)
  categoryName?: string     // Human-readable category name
  previousValue?: any       // Value before change
  newValue?: any            // Value after change
  comment?: string          // Additional context
}
```

**Audit Trail Display**:
- Shown below the three-pane interface
- Scrollable list of all changes
- Reverse chronological order (newest first)
- Formatted timestamps and human-readable descriptions
- Expandable to show full comment text

### 4. Integration Points

**Application Detail Sheet**:
- Added "Manual Review" button with pencil icon
- Clicking button navigates to full-screen manual review interface
- Button only shown when `onStartManualReview` callback is provided

**Job Detail View**:
- Added "Upload Rubric" button next to "Upload Applications"
- Opens rubric upload dialog
- Success toast shows number of categories extracted

**App.tsx Navigation**:
- New `manual-review` view type
- Manages state for:
  - Current review application ID
  - Current review job ID  
  - Navigation between views
- Provides back navigation to job detail view

## Data Persistence

### Local Storage (spark.kv)

**Manual Review Data** (`manual-review-${applicationId}`):
```typescript
{
  applicationId: string
  jobId: string
  rubricScores: Record<string, {
    points: number
    maxPoints: number
    comment: string
  }>
  overallComment: string
  adjustedFinalScore?: number
  auditTrail: ManualReviewAuditEntry[]
  lastModifiedAt: string
  lastModifiedBy: string
}
```

**Benefits**:
- Reviews persist across page refreshes
- Recruiters can resume incomplete reviews
- Changes tracked even if not explicitly saved
- Per-application storage prevents conflicts

## Type Definitions

**Added to `types/index.ts`**:

1. `ManualReviewAuditEntry` - Structure for audit trail entries
2. `ManualReviewData` - Complete manual review state
3. `Job.rubricDocumentId` - Optional reference to uploaded rubric document

## User Workflow

### Uploading a Rubric

1. Recruiter navigates to job detail page
2. Clicks "Upload Rubric" button
3. Selects PDF/MD/DOCX file containing rubric
4. System extracts categories using LLM
5. Recruiter reviews extracted categories
6. Confirms and applies to job

### Performing Manual Review

1. Recruiter views application in Application Detail sheet
2. Clicks "Manual Review" button
3. Three-pane interface loads with:
   - Job spec on left
   - Empty rubric form in center
   - Application content on right
4. Recruiter allocates points for each category
5. Adds comments explaining evaluation
6. Writes overall assessment
7. Clicks "Save Review"
8. Review data persisted with audit trail

### Viewing Audit Trail

1. Scroll below the three-pane interface
2. See chronological list of all changes
3. Each entry shows:
   - Timestamp
   - Reviewer name
   - Action taken
   - Values changed
4. Full transparency of review process

## Technical Implementation Notes

### LLM Integration

- Uses `window.spark.llmPrompt` for safe prompt construction
- Model: `gpt-4o` with JSON mode
- Extracts structured rubric data from unstructured documents
- Falls back gracefully if extraction fails

### State Management

- Manual review uses `useState` for local state
- `window.spark.kv` API for persistence
- Functional updates ensure correct state transitions
- Audit trail built incrementally with each change

### Scroll Behavior

- Each pane uses `<ScrollArea>` from shadcn
- Independent scroll for each column
- Flex layout prevents overflow issues
- Full height calculation: `h-[calc(100vh-180px)]`

### Error Handling

- File upload validates type and size
- LLM extraction wrapped in try-catch
- Toast notifications for user feedback
- Graceful degradation if features unavailable

## Future Enhancements

### Possible Additions

1. **Rubric Templates**: Save frequently-used rubrics for reuse
2. **Collaborative Review**: Multi-recruiter review with consensus scoring
3. **Comparison View**: Side-by-side comparison of multiple candidates
4. **Export Reviews**: PDF/Excel export of manual reviews
5. **Review Analytics**: Track reviewer patterns and inter-rater reliability
6. **Approval Workflows**: Multi-stage review with approvals
7. **Inline Document Annotation**: Highlight specific passages in applications

### Backend API Placeholders

When integrating with real backend:

1. **POST /api/jobs/{jobId}/rubric** - Upload and process rubric document
2. **GET /api/applications/{appId}/manual-review** - Fetch saved review
3. **POST /api/applications/{appId}/manual-review** - Save review with audit
4. **GET /api/applications/{appId}/audit-trail** - Retrieve full audit history
5. **PUT /api/applications/{appId}/final-score** - Update with manual score

## Files Modified/Created

### Created
- `src/components/UploadRubricDialog.tsx` - Rubric upload interface
- `src/components/ManualReviewView.tsx` - Three-pane manual review
- `MANUAL_REVIEW_SUMMARY.md` - This documentation

### Modified
- `src/types/index.ts` - Added manual review types
- `src/App.tsx` - Added manual review navigation
- `src/components/ApplicationDetail.tsx` - Added manual review button
- `src/components/JobDetailView.tsx` - Added rubric upload button

## Testing Checklist

- [ ] Upload rubric PDF and verify extraction
- [ ] Upload rubric Markdown and verify extraction
- [ ] Upload rubric DOCX and verify extraction
- [ ] Allocate points in manual review
- [ ] Verify score calculation is correct
- [ ] Add comments to categories
- [ ] Save review and verify persistence
- [ ] Reload page and verify review data restored
- [ ] Check audit trail shows all changes
- [ ] Verify timestamps in audit trail
- [ ] Test scrolling in all three panes
- [ ] Navigate from application detail to manual review
- [ ] Navigate back to job detail from manual review
- [ ] Test with job having many rubric categories (10+)
- [ ] Test with long job specifications
- [ ] Test with lengthy application content

## Accessibility Considerations

- All form inputs have proper labels
- Keyboard navigation supported throughout
- Focus states visible on all interactive elements
- Adequate color contrast maintained
- Screen reader friendly with semantic HTML
- Toast notifications for important status updates

## Performance Considerations

- Lazy loading of application content
- Virtualized scrolling for long content
- Debounced state updates
- Efficient re-render prevention
- Minimal unnecessary API calls
- Local state for responsive UI

## Security Considerations

- User identity from `window.spark.user()`
- All changes attributed to specific user
- Immutable audit trail design
- No sensitive data in client-side logs
- Secure file upload validation
- XSS prevention in comment fields
