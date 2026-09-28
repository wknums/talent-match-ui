import { useEffect, useState } from 'react'
import {
  DraggableResizableDialog,
  DraggableDialogHeader,
  DraggableDialogBody,
  DraggableDialogFooter,
} from '@/components/DraggableResizableDialog'
import { DialogDescription, DialogTitle } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { api, TalentMatchApiError } from '@/lib/api'
import type { UploadSettings } from '@/types'

const MIB = 1024 * 1024

interface OptionalUploadSettingsProps {
  open: boolean
  onClose: () => void
}

interface SettingsForm {
  fileConcurrency: number
  maxIndividualFileMiB: number
  maxInFlightMiB: number
}

function toForm(settings: UploadSettings): SettingsForm {
  return {
    fileConcurrency: settings.fileConcurrency,
    maxIndividualFileMiB: settings.maxIndividualFileBytes / MIB,
    maxInFlightMiB: settings.maxInFlightBytes / MIB,
  }
}

export function OptionalUploadSettings({
  open,
  onClose,
}: OptionalUploadSettingsProps) {
  const [settings, setSettings] = useState<UploadSettings | null>(null)
  const [form, setForm] = useState<SettingsForm | null>(null)
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (!open) return
    let cancelled = false
    setLoading(true)
    setErrors({})
    void api.getUploadSettings()
      .then(result => {
        if (cancelled) return
        setSettings(result)
        setForm(toForm(result))
      })
      .catch(error => {
        if (cancelled) return
        setErrors({
          form: [error instanceof Error ? error.message : 'Unable to load upload settings.'],
        })
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => { cancelled = true }
  }, [open])

  const save = async () => {
    if (!settings || !form) return
    setSaving(true)
    setErrors({})
    try {
      const saved = await api.updateUploadSettings({
        fileConcurrency: form.fileConcurrency,
        maxIndividualFileBytes: Math.round(form.maxIndividualFileMiB * MIB),
        maxInFlightBytes: Math.round(form.maxInFlightMiB * MIB),
        expectedConcurrencyVersion: settings.concurrencyVersion,
      })
      setSettings(saved)
      setForm(toForm(saved))
    } catch (error) {
      setForm(toForm(settings))
      if (error instanceof TalentMatchApiError && error.validationErrors) {
        setErrors(error.validationErrors)
      } else {
        const candidate = error as { validationErrors?: Record<string, string[]> }
        setErrors(candidate.validationErrors ?? {
          form: [error instanceof Error ? error.message : 'Unable to save upload settings.'],
        })
      }
    } finally {
      setSaving(false)
    }
  }

  const fieldError = (name: string) => errors[name]?.[0]

  return (
    <DraggableResizableDialog
      open={open}
      onOpenChange={nextOpen => { if (!nextOpen && !saving) onClose() }}
      defaultWidth={560}
      defaultHeight={480}
      minWidth={460}
      minHeight={400}
    >
      <DraggableDialogHeader>
        <DialogTitle>Optional Upload Settings</DialogTitle>
        <DialogDescription>
          Configure upload-only limits for newly created optional upload sessions.
        </DialogDescription>
      </DraggableDialogHeader>
      <DraggableDialogBody className="space-y-5 px-6">
        {loading && <p className="text-sm text-muted-foreground">Loading settings...</p>}
        {form && (
          <>
            <div className="space-y-2">
              <Label htmlFor="upload-file-concurrency">File concurrency</Label>
              <Input
                id="upload-file-concurrency"
                type="number"
                min={1}
                step={1}
                value={form.fileConcurrency}
                onChange={event => setForm(current => current && ({
                  ...current,
                  fileConcurrency: Number(event.target.value),
                }))}
                aria-invalid={Boolean(fieldError('fileConcurrency'))}
              />
              {fieldError('fileConcurrency') && (
                <p className="text-sm text-destructive">{fieldError('fileConcurrency')}</p>
              )}
            </div>
            <div className="space-y-2">
              <Label htmlFor="upload-max-individual">
                Maximum individual file size (MiB)
              </Label>
              <Input
                id="upload-max-individual"
                type="number"
                min={1}
                step={1}
                value={form.maxIndividualFileMiB}
                onChange={event => setForm(current => current && ({
                  ...current,
                  maxIndividualFileMiB: Number(event.target.value),
                }))}
                aria-invalid={Boolean(fieldError('maxIndividualFileBytes'))}
              />
              {fieldError('maxIndividualFileBytes') && (
                <p className="text-sm text-destructive">{fieldError('maxIndividualFileBytes')}</p>
              )}
            </div>
            <div className="space-y-2">
              <Label htmlFor="upload-max-in-flight">Total in-flight raw bytes (MiB)</Label>
              <Input
                id="upload-max-in-flight"
                type="number"
                min={1}
                step={1}
                value={form.maxInFlightMiB}
                onChange={event => setForm(current => current && ({
                  ...current,
                  maxInFlightMiB: Number(event.target.value),
                }))}
                aria-invalid={Boolean(fieldError('maxInFlightBytes'))}
              />
              {fieldError('maxInFlightBytes') && (
                <p className="text-sm text-destructive">{fieldError('maxInFlightBytes')}</p>
              )}
            </div>
            {errors.form?.[0] && (
              <p className="text-sm text-destructive">{errors.form[0]}</p>
            )}
            <p className="text-xs text-muted-foreground">
              Active sessions keep their existing snapshot. These limits do not change scoring capacity.
            </p>
          </>
        )}
      </DraggableDialogBody>
      <DraggableDialogFooter>
        <Button variant="outline" onClick={onClose} disabled={saving}>Close</Button>
        <Button onClick={save} disabled={!form || loading || saving}>
          {saving ? 'Saving...' : 'Save settings'}
        </Button>
      </DraggableDialogFooter>
    </DraggableResizableDialog>
  )
}
