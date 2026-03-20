import { useEffect, useState } from 'react'
import { api } from '@/lib/api'
import mammoth from 'mammoth'
import { marked } from 'marked'
import type { ApplicationDocument } from '@/types'

interface DocumentViewerProps {
  applicationId: string
  document: ApplicationDocument
  className?: string
}

export function DocumentViewer({ applicationId, document: doc, className = '' }: DocumentViewerProps) {
  const [htmlContent, setHtmlContent] = useState<string | null>(null)
  const [textContent, setTextContent] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  // Build URL directly — api proxy wraps all methods in async, which would return a Promise instead of a string
  const contentUrl = `/api/applications/${applicationId}/documents/${doc.documentId}/content`

  useEffect(() => {
    loadContent()
  }, [applicationId, doc.documentId])

  const loadContent = async () => {
    setLoading(true)
    setError(null)

    try {
      if (doc.mimeType === 'application/pdf' || doc.mimeType.startsWith('image/')) {
        // PDF and images handled inline via URL — no fetch needed
        setLoading(false)
        return
      }

      const buffer = await api.getDocumentContent(applicationId, doc.documentId)

      if (doc.mimeType === 'application/vnd.openxmlformats-officedocument.wordprocessingml.document') {
        const result = await mammoth.convertToHtml({ arrayBuffer: buffer })
        setHtmlContent(result.value)
      } else if (doc.mimeType === 'text/markdown') {
        const text = new TextDecoder().decode(buffer)
        const html = await marked(text)
        setHtmlContent(html)
      } else if (doc.mimeType === 'text/plain') {
        setTextContent(new TextDecoder().decode(buffer))
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load document')
    } finally {
      setLoading(false)
    }
  }

  if (loading) {
    return <div className={`flex items-center justify-center p-8 ${className}`}>Loading document...</div>
  }

  if (error) {
    return <div className={`text-destructive p-4 ${className}`}>{error}</div>
  }

  if (doc.mimeType === 'application/pdf') {
    return (
      <iframe
        src={contentUrl}
        className={`w-full h-full border-0 ${className}`}
        title={doc.fileName}
      />
    )
  }

  if (doc.mimeType.startsWith('image/')) {
    return (
      <div className={`flex items-center justify-center p-4 overflow-auto ${className}`}>
        <img src={contentUrl} alt={doc.fileName} className="max-w-full" />
      </div>
    )
  }

  if (htmlContent) {
    return (
      <div
        className={`prose prose-sm max-w-none p-4 overflow-auto ${className}`}
        dangerouslySetInnerHTML={{ __html: htmlContent }}
      />
    )
  }

  if (textContent) {
    return (
      <pre className={`whitespace-pre-wrap font-mono text-sm p-4 overflow-auto ${className}`}>
        {textContent}
      </pre>
    )
  }

  return <div className={`text-muted-foreground p-4 ${className}`}>Unsupported file type: {doc.mimeType}</div>
}
