import { useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { api } from '@/lib/api'
import { toast } from 'sonner'
import type {
  ExtractionInstructionVersion,
  ExtractionInstructionVersionDetail,
  PromptGenerationInstruction,
  ReasoningEffort,
  ReasoningModelsResponse,
} from '@/types'
import { ReasoningProfileFields } from '@/components/ReasoningProfileFields'

interface ExtractionInstructionAdminProps {
  open: boolean
  onClose: () => void
}

export function ExtractionInstructionAdmin({ open, onClose }: ExtractionInstructionAdminProps) {
  const [versions, setVersions] = useState<ExtractionInstructionVersion[]>([])
  const [selected, setSelected] = useState<ExtractionInstructionVersionDetail | null>(null)
  const [instructionText, setInstructionText] = useState('Extract every independently assessable requirement as its own rubric item.')
  const [changeNote, setChangeNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [scoringVersions, setScoringVersions] = useState<PromptGenerationInstruction[]>([])
  const [scoringInstructionText, setScoringInstructionText] = useState('')
  const [scoringChangeNote, setScoringChangeNote] = useState('')
  const [catalog, setCatalog] = useState<ReasoningModelsResponse | null>(null)
  const [extractionModelId, setExtractionModelId] = useState('')
  const [extractionReasoningLevel, setExtractionReasoningLevel] = useState<ReasoningEffort>('high')
  const [scoringModelId, setScoringModelId] = useState('')
  const [scoringReasoningLevel, setScoringReasoningLevel] = useState<ReasoningEffort>('high')
  const fileInputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (open) void refresh().catch((error: unknown) => {
      toast.error(error instanceof Error ? error.message : 'Failed to load instruction versions')
    })
  }, [open])

  const refresh = async () => {
    const [list, scoringList] = await Promise.all([
      api.listExtractionInstructions(),
      api.getSystemPromptGenerationInstructions(),
    ])
    setVersions(list)
    setScoringVersions(scoringList)
    try {
      const reasoningCatalog = await api.getReasoningModels()
      setCatalog(reasoningCatalog)
      setExtractionModelId(current => current || reasoningCatalog.defaultModel)
      setExtractionReasoningLevel(current =>
        reasoningCatalog.supportedReasoningEfforts.includes(current)
          ? current
          : reasoningCatalog.defaultReasoningEffort)
      setScoringModelId(current => current || reasoningCatalog.defaultModel)
      setScoringReasoningLevel(current =>
        reasoningCatalog.supportedReasoningEfforts.includes(current)
          ? current
          : reasoningCatalog.defaultReasoningEffort)
    } catch (error) {
      setCatalog(null)
      toast.error(error instanceof Error
        ? `Supported models could not be loaded: ${error.message}`
        : 'Supported models could not be loaded')
    }
    if (list[0]) {
      setSelected(await api.getExtractionInstruction(list[0].id))
    }
  }

  const createScoringDraft = async () => {
    setBusy(true)
    try {
      await api.createSystemPromptGenerationInstruction(
        scoringInstructionText,
        scoringChangeNote || undefined,
        scoringModelId,
        scoringReasoningLevel,
      )
      toast.success('System scoring generation draft created')
      setScoringInstructionText('')
      setScoringChangeNote('')
      await refresh()
    } catch (error: any) {
      toast.error(error?.message || 'Failed to create scoring generation draft')
    } finally {
      setBusy(false)
    }
  }

  const activateScoringVersion = async (instructionId: string) => {
    setBusy(true)
    try {
      await api.activateSystemPromptGenerationInstruction(instructionId)
      toast.success('System scoring generation instruction activated')
      await refresh()
    } catch (error: any) {
      toast.error(error?.message || 'Failed to activate scoring generation instruction')
    } finally {
      setBusy(false)
    }
  }

  const createDraft = async () => {
    setBusy(true)
    try {
      await api.createExtractionInstructionDraft(
        instructionText,
        changeNote || undefined,
        extractionModelId,
        extractionReasoningLevel,
      )
      toast.success('Draft created')
      setChangeNote('')
      await refresh()
    } catch (error: any) {
      toast.error(error?.message || 'Failed to create draft')
    } finally {
      setBusy(false)
    }
  }

  const validateVersion = async (versionId: string, file: File) => {
    setBusy(true)
    try {
      const base64 = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader()
        reader.onload = () => {
          const result = reader.result as string
          resolve(result.split(',')[1] || result)
        }
        reader.onerror = reject
        reader.readAsDataURL(file)
      })
      await api.validateExtractionInstruction(versionId, file.name, base64, file.type)
      toast.success('Validation completed')
      await refresh()
      setSelected(await api.getExtractionInstruction(versionId))
    } catch (error: any) {
      toast.error(error?.message || 'Validation failed')
    } finally {
      setBusy(false)
    }
  }

  const activateVersion = async (version: ExtractionInstructionVersion) => {
    setBusy(true)
    try {
      await api.activateExtractionInstruction(version.id, version.concurrencyVersion)
      toast.success('Instruction version activated')
      await refresh()
    } catch (error: any) {
      toast.error(error?.message || 'Activation failed')
    } finally {
      setBusy(false)
    }
  }

  if (!open) return null

  return (
    <div className="fixed inset-0 z-50 bg-black/50 flex items-center justify-center p-6" onClick={onClose}>
      <div className="bg-background rounded-lg shadow-xl w-full max-w-6xl max-h-[90vh] overflow-auto p-6 space-y-4" onClick={(event) => event.stopPropagation()}>
        <div className="flex items-center justify-between">
          <div>
            <h2 className="text-2xl font-semibold">Extraction Instruction Versions</h2>
            <p className="text-sm text-muted-foreground">Create, validate, activate, and review the protected extraction contract lifecycle.</p>
          </div>
          <Button variant="outline" onClick={onClose}>Close</Button>
        </div>

        <div className="grid gap-4 lg:grid-cols-[1.2fr_0.8fr]">
          <Card>
            <CardHeader>
              <CardTitle>Create draft</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3">
              <Textarea value={instructionText} onChange={(event) => setInstructionText(event.target.value)} rows={8} />
              <Input value={changeNote} onChange={(event) => setChangeNote(event.target.value)} placeholder="Change note (optional)" />
              <ReasoningProfileFields
                idPrefix="extraction-instruction"
                catalog={catalog}
                modelId={extractionModelId}
                reasoningLevel={extractionReasoningLevel}
                onModelChange={setExtractionModelId}
                onReasoningLevelChange={setExtractionReasoningLevel}
                disabled={busy}
              />
              <Button onClick={createDraft} disabled={busy || !catalog || !extractionModelId}>Create draft</Button>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Protected contract</CardTitle>
            </CardHeader>
            <CardContent>
              <pre className="text-xs whitespace-pre-wrap">{JSON.stringify(selected?.protectedContract || {}, null, 2)}</pre>
            </CardContent>
          </Card>
        </div>

        <div className="grid gap-3">
          {versions.map((version) => (
            <Card key={version.id}>
              <CardContent className="p-4 space-y-3">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <div className="font-medium">v{version.versionNumber} · {version.status} · {version.validationStatus}</div>
                    <div className="text-xs text-muted-foreground">{version.createdBy} · {new Date(version.createdAt).toLocaleString()}</div>
                    <div className="text-xs text-muted-foreground">
                      {version.modelId} · reasoning effort {version.reasoningLevel}
                    </div>
                  </div>
                  <div className="flex items-center gap-2">
                    <Button variant="outline" size="sm" onClick={async () => setSelected(await api.getExtractionInstruction(version.id))} disabled={busy}>View</Button>
                    <div>
                      <Label htmlFor={`validate-${version.id}`} className="sr-only">Validation file</Label>
                      <input
                        id={`validate-${version.id}`}
                        ref={fileInputRef}
                        type="file"
                        accept=".pdf,.jpg,.jpeg,.md,.txt,.docx"
                        onChange={(event) => {
                          const file = event.target.files?.[0]
                          if (file) void validateVersion(version.id, file)
                        }}
                      />
                    </div>
                    <Button size="sm" onClick={() => void activateVersion(version)} disabled={busy || version.validationStatus !== 'valid'}>Activate</Button>
                  </div>
                </div>
                <pre className="text-sm whitespace-pre-wrap">{version.instructionText}</pre>
                {version.validationFindings.length > 0 && (
                  <ul className="text-xs text-muted-foreground space-y-1">
                    {version.validationFindings.map((finding) => (
                      <li key={`${version.id}-${finding.code}-${finding.path}`}>{finding.code}: {finding.message}</li>
                    ))}
                  </ul>
                )}
              </CardContent>
            </Card>
          ))}
        </div>
        <div className="border-t pt-6 space-y-4">
          <div>
            <h2 className="text-2xl font-semibold">System Scoring Prompt Generation</h2>
            <p className="text-sm text-muted-foreground">
              Manage the versioned system instruction used when a job has no active job-specific override.
            </p>
          </div>
          <Card>
            <CardHeader>
              <CardTitle>Create scoring generation draft</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3">
              <Textarea
                aria-label="System scoring prompt generation instruction"
                value={scoringInstructionText}
                onChange={(event) => setScoringInstructionText(event.target.value)}
                rows={8}
              />
              <Input
                aria-label="Scoring generation change note"
                value={scoringChangeNote}
                onChange={(event) => setScoringChangeNote(event.target.value)}
                placeholder="Change note (optional)"
              />
              <ReasoningProfileFields
                idPrefix="system-scoring-generation"
                catalog={catalog}
                modelId={scoringModelId}
                reasoningLevel={scoringReasoningLevel}
                onModelChange={setScoringModelId}
                onReasoningLevelChange={setScoringReasoningLevel}
                disabled={busy}
              />
              <Button onClick={createScoringDraft} disabled={busy || !catalog || !scoringInstructionText.trim()}>
                Create scoring draft
              </Button>
            </CardContent>
          </Card>
          <div className="grid gap-3">
            {scoringVersions.map((version) => (
              <Card key={version.id}>
                <CardContent className="p-4 space-y-3">
                  <div className="flex items-start justify-between gap-4">
                    <div>
                      <div className="font-medium">v{version.versionNumber} · {version.status}</div>
                      <div className="text-xs text-muted-foreground">
                        {version.createdBy} · {new Date(version.createdAt).toLocaleString()}
                      </div>
                      <div className="text-xs text-muted-foreground">
                        {version.modelId} · reasoning effort {version.reasoningLevel}
                      </div>
                    </div>
                    {version.status !== 'active' && (
                      <Button size="sm" onClick={() => void activateScoringVersion(version.id)} disabled={busy}>
                        Activate
                      </Button>
                    )}
                  </div>
                  <pre className="text-sm whitespace-pre-wrap">{version.instructionText}</pre>
                  {version.changeNote && <p className="text-xs text-muted-foreground">{version.changeNote}</p>}
                </CardContent>
              </Card>
            ))}
          </div>
        </div>
      </div>
    </div>
  )
}
