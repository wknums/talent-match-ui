import { createRoot } from 'react-dom/client'
import { ErrorBoundary } from "react-error-boundary";
import { MsalProvider } from '@azure/msal-react'

import App from './App.tsx'
import { ErrorFallback } from './ErrorFallback.tsx'
import { AuthProvider } from './hooks/useAuth.ts'
import { appAuthMode, createMsalInstance, initializeMsal } from './lib/msal-config.ts'

import "./main.css"
import "./styles/theme.css"
import "./index.css"

type BuildStamp = {
  version?: string
  createdAtUtc?: string
  source?: string
}

async function logBuildStamp() {
  try {
    const response = await fetch('/build-info.json', { cache: 'no-store' })
    if (!response.ok) {
      console.warn(`[TalentMatch Build] unable to load build-info.json (status=${response.status})`)
      return
    }

    const stamp = (await response.json()) as BuildStamp
    if (!stamp.version || !stamp.createdAtUtc) {
      console.warn('[TalentMatch Build] build-info.json is missing required fields.')
      return
    }

    console.info(
      `[TalentMatch Build] version=${stamp.version} createdAtUtc=${stamp.createdAtUtc}`,
    )
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    console.warn(`[TalentMatch Build] unable to read build-info.json: ${message}`)
  }
}

void logBuildStamp()

async function renderApp() {
  const app = (
    <AuthProvider>
      <App />
    </AuthProvider>
  )
  const content = appAuthMode === 'entra'
    ? await (async () => {
        const msal = createMsalInstance()
        await initializeMsal(msal)
        return <MsalProvider instance={msal}>{app}</MsalProvider>
      })()
    : app

  createRoot(document.getElementById('root')!).render(
    <ErrorBoundary FallbackComponent={ErrorFallback}>
      {content}
    </ErrorBoundary>,
  )
}

void renderApp()
