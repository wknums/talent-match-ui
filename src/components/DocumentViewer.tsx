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

type DocumentKind = 'pdf' | 'image' | 'docx' | 'markdown' | 'text' | 'unknown'

function resolveDocumentKind(mimeType: string | undefined, fileName: string | undefined): DocumentKind {
  const mime = (mimeType ?? '').toLowerCase().split(';')[0].trim()
  const ext = (fileName ?? '').toLowerCase().split('.').pop() ?? ''

  if (mime === 'application/pdf' || mime === 'application/x-pdf' || ext === 'pdf') return 'pdf'
  if (mime.startsWith('image/') || ['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg'].includes(ext)) return 'image'
  if (
    mime === 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' ||
    mime === 'application/msword' ||
    ext === 'docx' ||
    ext === 'doc'
  ) return 'docx'
  if (mime === 'text/markdown' || mime === 'text/x-markdown' || ext === 'md' || ext === 'markdown') return 'markdown'
  if (mime.startsWith('text/') || ext === 'txt') return 'text'
  return 'unknown'
}

export function DocumentViewer({ applicationId, document: doc, className = '' }: DocumentViewerProps) {
  const [htmlContent, setHtmlContent] = useState<string | null>(null)
  const [textContent, setTextContent] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  // Build URL directly — api proxy wraps all methods in async, which would return a Promise instead of a string
  const contentUrl = `/api/applications/${applicationId}/documents/${doc.documentId}/content`
  const kind = resolveDocumentKind(doc.mimeType, doc.fileName)

  useEffect(() => {
    loadContent()
  }, [applicationId, doc.documentId, doc.mimeType, doc.fileName])

  const loadContent = async () => {
    setLoading(true)
    setError(null)
    setHtmlContent(null)
    setTextContent(null)

    try {
      if (kind === 'pdf' || kind === 'image') {
        // PDF and images handled inline via URL — no fetch needed
        setLoading(false)
        return
      }

      const buffer = await api.getDocumentContent(applicationId, doc.documentId)

      if (kind === 'docx') {
        const result = await mammoth.convertToHtml({ arrayBuffer: buffer })
        setHtmlContent(result.value)
      } else if (kind === 'markdown') {
        const text = new TextDecoder().decode(buffer)
        const html = await marked(text)
        setHtmlContent(html)
      } else if (kind === 'text') {
        setTextContent(new TextDecoder().decode(buffer))
      } else {
        // Last-resort: attempt to decode as text so users can still see something useful.
        const text = new TextDecoder().decode(buffer)
        if (text && /[\x09\x0A\x0D\x20-\x7E]/.test(text)) {
          setTextContent(text)
        }
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

  if (kind === 'pdf') {
    return (
      <iframe
        src={contentUrl}
        className={`w-full h-full border-0 ${className}`}
        title={doc.fileName}
      />
    )
  }

  if (kind === 'image') {
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

  return (
    <div className={`text-muted-foreground p-4 ${className}`}>
      Preview not available for {doc.fileName || doc.mimeType || 'this document'}.{' '}
      <a href={contentUrl} target="_blank" rel="noreferrer" className="underline">
        Download
      </a>
    </div>
  )
}
