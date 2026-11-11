import type { RubricItem } from '@/types'

interface RubricItemCardProps {
  item: RubricItem
}

export function RubricItemCard({ item }: RubricItemCardProps) {
  return (
    <div className="rounded-md border bg-card p-3">
      <div className="flex items-start justify-between gap-3">
        <div className="font-medium">{item.text}</div>
        <div className="flex items-center gap-2 text-xs text-muted-foreground">
          {item.reviewStatus === 'needs_review' && (
            <span className="rounded-full bg-amber-100 px-2 py-0.5 font-medium text-amber-800">
              Needs review
            </span>
          )}
          <span>{item.requirementType}</span>
        </div>
      </div>
      {item.sourceText && (
        <div className="mt-2 text-xs text-muted-foreground">
          <strong>Source:</strong> {item.sourceText}
        </div>
      )}
      {item.sourceLocation && (
        <div className="mt-1 text-xs text-muted-foreground">{item.sourceLocation}</div>
      )}
    </div>
  )
}
