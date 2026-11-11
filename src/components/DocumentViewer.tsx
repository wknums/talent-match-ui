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
  const [binaryContentUrl, setBinaryContentUrl] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const kind = resolveDocumentKind(doc.mimeType, doc.fileName)

  useEffect(() => {
    let cancelled = false
    let objectUrl: string | null = null

    const loadContent = async () => {
      setLoading(true)
      setError(null)
      setHtmlContent(null)
      setTextContent(null)
      setBinaryContentUrl(null)

      try {
        const buffer = await api.getDocumentContent(applicationId, doc.documentId)
        if (cancelled) return

        if (kind === 'pdf' || kind === 'image') {
          objectUrl = URL.createObjectURL(new Blob([buffer], {
            type: doc.mimeType || 'application/octet-stream',
          }))
          setBinaryContentUrl(objectUrl)
        } else if (kind === 'docx') {
          const result = await mammoth.convertToHtml({ arrayBuffer: buffer })
          if (!cancelled) setHtmlContent(result.value)
        } else if (kind === 'markdown') {
          const text = new TextDecoder().decode(buffer)
          const html = await marked(text)
          if (!cancelled) setHtmlContent(html)
        } else if (kind === 'text') {
          setTextContent(new TextDecoder().decode(buffer))
        } else {
          const text = new TextDecoder().decode(buffer)
          // Tab, LF and CR are legitimate printable-text markers here.
          // eslint-disable-next-line no-control-regex
          if (text && /[\x09\x0A\x0D\x20-\x7E]/.test(text)) {
            setTextContent(text)
          } else {
            objectUrl = URL.createObjectURL(new Blob([buffer], {
              type: doc.mimeType || 'application/octet-stream',
            }))
            setBinaryContentUrl(objectUrl)
          }
        }
      } catch (err) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : 'Failed to load document')
        }
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    void loadContent()

    return () => {
      cancelled = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [applicationId, doc.documentId, doc.mimeType, doc.fileName, kind])

  if (loading) {
    return <div className={`flex items-center justify-center p-8 ${className}`}>Loading document...</div>
  }

  if (error) {
    return <div className={`text-destructive p-4 ${className}`}>{error}</div>
  }

  if (kind === 'pdf' && binaryContentUrl) {
    return (
      <iframe
        src={binaryContentUrl}
        className={`w-full h-full border-0 ${className}`}
        title={doc.fileName}
      />
    )
  }

  if (kind === 'image' && binaryContentUrl) {
    return (
      <div className={`flex items-center justify-center p-4 overflow-auto ${className}`}>
        <img src={binaryContentUrl} alt={doc.fileName} className="max-w-full" />
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

  if (binaryContentUrl) {
    return (
      <div className={`text-muted-foreground p-4 ${className}`}>
        Preview not available for {doc.fileName || doc.mimeType || 'this document'}.{' '}
        <a href={binaryContentUrl} download={doc.fileName} className="underline">
          Download
        </a>
      </div>
    )
  }

  return (
    <div className={`text-muted-foreground p-4 ${className}`}>
      Preview not available for {doc.fileName || doc.mimeType || 'this document'}.
    </div>
  )
}
