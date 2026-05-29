# Talent Matching Platform - Frontend

A production-grade frontend for an Azure-hosted AI-powered job application scoring system. This application provides recruiters, hiring managers, and administrators with comprehensive tools to manage jobs, upload applications, monitor AI-powered scoring pipelines, and review candidate assessments.

> **Scoring backends.** The client supports two scoring modes selected by env vars:
> - **Sequential mode** (`AWR_SEQ_API_ENDPOINT`) — synchronous `POST /assess/passthrough`. Used for test scoring and small jobs.
> - **Platform mode** (`AWR_PLATFORM_API_ENDPOINT`) — asynchronous batched submission + polling against a Service Bus + Durable Functions backend. Orchestration (queueing, fan-out per CV, retries) lives on the platform. The client only submits, reconciles, and reports.
>
> The full client ↔ platform ↔ engine contract is documented in
> [specs/008-platform-mode-shift/platform-contract.md](specs/008-platform-mode-shift/platform-contract.md). Platform and engine teams build against that document. Any change to wire format, headers, or storage layout must be made there first.

## Features

- **Dashboard Overview**: Real-time system statistics and job monitoring
- **Job Management**: Create jobs with custom rubrics, must-have requirements, and scoring configurations
- **Bulk Upload**: Upload thousands of applications with drag-and-drop interface
- **AI Scoring Pipeline**: Monitor multi-stage processing (extraction → scoring → aggregation)
- **Smart Lists**: Dynamic longlist, shortlist, and exclusion lists with filtering
- **Detailed Assessments**: Drill-down into individual applications with evidence-based scoring
- **Variance Analysis**: Identify applications requiring manual review based on score variance
- **Audit Trail**: Complete traceability of all decisions and processing events

## Technology Stack

- **React 19** with TypeScript
- **Tailwind CSS** with custom design system
- **Shadcn UI** components (v4)
- **Framer Motion** for animations
- **Phosphor Icons** for iconography
- **Space Grotesk & JetBrains Mono** fonts

## Architecture

### Simulated API Layer

The application currently uses a **simulated API layer** (`src/lib/api.ts`) that mimics backend responses. This allows for rapid frontend development and testing without requiring a live backend.

All API calls are centralized in `src/lib/api.ts` and return properly typed data matching the backend schema.

### Replacing Simulated APIs with Real Endpoints

To connect to your Azure backend, modify `src/lib/api.ts`:

#### 1. Create API Client Configuration

```typescript
// src/lib/api-client.ts
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || 'https://your-api.azure.com/api'

export async function apiRequest<T>(
  endpoint: string,
  options?: RequestInit
): Promise<T> {
  const response = await fetch(`${API_BASE_URL}${endpoint}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${getAuthToken()}`,
      ...options?.headers,
    },
  })

  if (!response.ok) {
    throw new Error(`API Error: ${response.statusText}`)
  }

  return response.json()
}

function getAuthToken(): string {
  // Implement Azure AD token retrieval
  return localStorage.getItem('auth_token') || ''
}
```

#### 2. Update API Methods

Replace the mock implementations in `src/lib/api.ts`:

```typescript
// Before (Simulated)
export const mockAPI = {
  async getJobs(): Promise<Job[]> {
    await delay(400)
    return generateMockJobs()
  },
  // ... other methods
}

// After (Real API)
export const api = {
  async getJobs(): Promise<Job[]> {
    return apiRequest<Job[]>('/jobs')
  },
  
  async getJob(jobId: string): Promise<Job | null> {
    return apiRequest<Job>(`/jobs/${jobId}`)
  },
  
  async createJob(data: CreateJobRequest): Promise<Job> {
    return apiRequest<Job>('/jobs', {
      method: 'POST',
      body: JSON.stringify(data),
    })
  },
  
  async getApplications(jobId: string, filters?: ApplicationFilters): Promise<Application[]> {
    const params = new URLSearchParams(filters as any)
    return apiRequest<Application[]>(`/jobs/${jobId}/applications?${params}`)
  },
  
  async uploadApplications(jobId: string, files: File[]): Promise<{ applicationIds: string[] }> {
    const formData = new FormData()
    files.forEach(file => formData.append('files', file))
    
    return apiRequest<{ applicationIds: string[] }>(`/jobs/${jobId}/applications/upload`, {
      method: 'POST',
      body: formData,
      headers: {}, // Let browser set multipart/form-data boundary
    })
  },
  
  // ... implement remaining methods
}
```

#### 3. Update Component Imports

Update all components to use the new API:

```typescript
// Before
import { mockAPI } from '@/lib/api'

// After
import { api } from '@/lib/api'
```

### API Endpoints Mapping

| Frontend Method | HTTP Method | Backend Endpoint | Description |
|----------------|-------------|------------------|-------------|
| `getSystemStats()` | GET | `/stats` | System-wide statistics |
| `getJobs()` | GET | `/jobs` | List all jobs |
| `getJob(jobId)` | GET | `/jobs/:jobId` | Get job details |
| `createJob(data)` | POST | `/jobs` | Create new job |
| `getApplications(jobId, filters)` | GET | `/jobs/:jobId/applications` | Get applications with filters |
| `getApplication(applicationId)` | GET | `/applications/:applicationId` | Get application details |
| `getScoringRuns(applicationId)` | GET | `/applications/:applicationId/runs` | Get all scoring runs |
| `getAggregatedResult(applicationId)` | GET | `/applications/:applicationId/result` | Get final aggregated result |
| `getExtractionArtifact(applicationId)` | GET | `/applications/:applicationId/extraction` | Get extracted markdown |
| `uploadApplications(jobId, files)` | POST | `/jobs/:jobId/applications/upload` | Upload application documents |
| `getDLQItems()` | GET | `/dlq` | Get dead letter queue items |
| `retryDLQItem(itemId)` | POST | `/dlq/:itemId/retry` | Retry failed item |
| `getAuditEvents(filters)` | GET | `/audit` | Get audit trail events |

### Environment Variables

Create a `.env` file in the project root:

```bash
VITE_API_BASE_URL=https://your-api.azure.com/api
VITE_ENABLE_AUTH=true
VITE_AZURE_AD_CLIENT_ID=your-client-id
VITE_AZURE_AD_TENANT_ID=your-tenant-id
```

### Authentication Integration

For Azure AD authentication, install and configure MSAL:

```bash
npm install @azure/msal-browser @azure/msal-react
```

Create an auth wrapper:

```typescript
// src/lib/auth.ts
import { PublicClientApplication } from '@azure/msal-browser'

const msalConfig = {
  auth: {
    clientId: import.meta.env.VITE_AZURE_AD_CLIENT_ID,
    authority: `https://login.microsoftonline.com/${import.meta.env.VITE_AZURE_AD_TENANT_ID}`,
    redirectUri: window.location.origin,
  },
}

export const msalInstance = new PublicClientApplication(msalConfig)
```

## Project Structure

```
src/
├── components/
│   ├── ui/                      # Shadcn components (pre-installed)
│   ├── ApplicationDetail.tsx    # Application drill-down sheet
│   ├── ApplicationsTable.tsx    # Sortable applications table
│   ├── CreateJobDialog.tsx      # Job creation form
│   ├── DashboardView.tsx        # Main dashboard
│   ├── JobCard.tsx              # Job summary card
│   ├── JobDetailView.tsx        # Job detail with tabs
│   ├── PipelineVisualizer.tsx   # Multi-stage pipeline status
│   ├── StatCard.tsx             # Metric display card
│   ├── StatusBadge.tsx          # Status indicator
│   └── UploadApplicationsDialog.tsx # Bulk file upload
├── lib/
│   ├── api.ts                   # API layer (currently simulated)
│   └── utils.ts                 # Utility functions
├── types/
│   └── index.ts                 # TypeScript type definitions
├── App.tsx                      # Main application component
└── index.css                    # Theme and global styles
```

## Type System

All data types are defined in `src/types/index.ts` and align with the backend schema:

- `Job`, `JobConfigVersion`, `JobStats`
- `Application`, `ApplicationDocument`, `ApplicationStatus`
- `ScoringRun`, `AggregatedResult`, `ExtractionArtifact`
- `MustHave`, `RubricCategory`, `EvidenceCitation`
- `ProcessingEvent`, `DLQItem`, `SystemStats`

These types ensure type safety across the application and make API integration seamless.

## Running the Application

```bash
# Install dependencies
npm install

# Start development server
npm run dev

# Build for production
npm run build

# Preview production build
npm run preview
```

## Design System

### Colors

- **Primary (Deep Navy)**: `oklch(0.25 0.05 250)` - Authority and trust
- **Accent (Electric Blue)**: `oklch(0.60 0.18 240)` - Active states and CTAs
- **Success (Emerald)**: `oklch(0.65 0.15 160)` - Completed processing
- **Destructive (Warm Amber)**: `oklch(0.70 0.15 60)` - Warnings and failures

### Typography

- **Primary**: Space Grotesk (UI, labels, content)
- **Data**: JetBrains Mono (scores, metrics, timestamps)

### Motion

- State transitions: 150ms ease-out
- Card animations: 200ms with stagger
- Progress indicators: 500ms smooth transitions
- Modals/sheets: 250ms slide animations

## Real-Time Updates

The dashboard and job detail views automatically refresh every 10 seconds to show live processing status. When connecting to real APIs, consider:

1. **WebSocket Integration**: For true real-time updates
2. **Server-Sent Events (SSE)**: For one-way server-to-client updates
3. **Polling Optimization**: Adjust refresh intervals based on processing activity

## Error Handling

The application uses:

- **Toast notifications** (via Sonner) for user feedback
- **Try-catch blocks** around all async operations
- **Loading states** for async UI components
- **Fallback UI** for missing data

When integrating real APIs, enhance error handling:

```typescript
try {
  const data = await api.getSomething()
  // Success handling
} catch (error) {
  if (error.status === 401) {
    // Redirect to login
  } else if (error.status === 403) {
    toast.error('You do not have permission to perform this action')
  } else {
    toast.error('An unexpected error occurred')
    // Log to monitoring service
  }
}
```

## Security Considerations

When deploying:

1. **Never expose secrets** in environment variables prefixed with `VITE_` (they're bundled in the client)
2. **Implement CSRF protection** for state-changing operations
3. **Validate file uploads** on both client and server
4. **Use HTTPS** for all API communication
5. **Implement proper CORS** policies on the backend

## Next Steps

1. **Authentication**: Integrate Azure AD with MSAL
2. **API Integration**: Replace simulated API with real endpoints
3. **WebSocket/SSE**: Add real-time processing updates
4. **Export Features**: Add CSV/Excel export for candidate lists
5. **Advanced Filtering**: Implement complex filter UI with saved filters
6. **Audit Viewer**: Create dedicated audit trail browser for compliance
7. **Admin Panel**: Add configuration UI for system-wide settings

## Support

For questions or issues, refer to the PRD.md for detailed feature specifications and requirements.
