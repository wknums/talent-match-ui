import { Label } from '@/components/ui/label'
import type {
  ReasoningEffort,
  ReasoningModelsResponse,
} from '@/types'

interface ReasoningProfileFieldsProps {
  catalog: ReasoningModelsResponse | null
  modelId: string
  reasoningLevel: ReasoningEffort
  onModelChange: (value: string) => void
  onReasoningLevelChange: (value: ReasoningEffort) => void
  disabled?: boolean
  idPrefix: string
}

export function ReasoningProfileFields({
  catalog,
  modelId,
  reasoningLevel,
  onModelChange,
  onReasoningLevelChange,
  disabled = false,
  idPrefix,
}: ReasoningProfileFieldsProps) {
  return (
    <div className="grid gap-3 sm:grid-cols-2">
      <div className="space-y-1">
        <Label htmlFor={`${idPrefix}-model`}>Model</Label>
        <select
          id={`${idPrefix}-model`}
          className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm"
          value={modelId}
          onChange={(event) => onModelChange(event.target.value)}
          disabled={disabled || !catalog}
        >
          {catalog?.models.map((model) => (
            <option key={model.slot} value={model.deployment}>
              {model.deployment}{model.isDefault ? ' (default)' : ''}
            </option>
          ))}
        </select>
      </div>
      <div className="space-y-1">
        <Label htmlFor={`${idPrefix}-reasoning-effort`}>Reasoning effort</Label>
        <select
          id={`${idPrefix}-reasoning-effort`}
          className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm"
          value={reasoningLevel}
          onChange={(event) => onReasoningLevelChange(event.target.value as ReasoningEffort)}
          disabled={disabled || !catalog}
        >
          {catalog?.supportedReasoningEfforts.map((effort) => (
            <option key={effort} value={effort}>{effort}</option>
          ))}
        </select>
      </div>
    </div>
  )
}
