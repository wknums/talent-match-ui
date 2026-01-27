import { CheckCircle, Circle, WarningCircle, Spinner } from '@phosphor-icons/react'
import { cn } from '@/lib/utils'

interface PipelineStage {
  name: string
  status: 'pending' | 'processing' | 'completed' | 'failed'
  count?: number
  total?: number
}

interface PipelineVisualizerProps {
  stages: PipelineStage[]
  className?: string
}

export function PipelineVisualizer({ stages, className }: PipelineVisualizerProps) {
  const getStageIcon = (status: PipelineStage['status']) => {
    switch (status) {
      case 'completed':
        return <CheckCircle weight="fill" className="text-success" size={24} />
      case 'processing':
        return <Spinner className="text-accent animate-spin" size={24} />
      case 'failed':
        return <WarningCircle weight="fill" className="text-destructive" size={24} />
      case 'pending':
        return <Circle className="text-muted-foreground" size={24} />
    }
  }

  const getStageColor = (status: PipelineStage['status']) => {
    switch (status) {
      case 'completed':
        return 'border-success'
      case 'processing':
        return 'border-accent'
      case 'failed':
        return 'border-destructive'
      case 'pending':
        return 'border-muted'
    }
  }

  return (
    <div className={cn('flex items-center gap-2', className)}>
      {stages.map((stage, index) => (
        <div key={stage.name} className="flex items-center gap-2">
          <div className="flex flex-col items-center gap-2">
            <div className={cn('rounded-full border-2 p-1', getStageColor(stage.status))}>
              {getStageIcon(stage.status)}
            </div>
            <div className="text-center">
              <p className="text-xs font-medium">{stage.name}</p>
              {stage.count !== undefined && stage.total !== undefined && (
                <p className="text-xs font-mono text-muted-foreground">
                  {stage.count}/{stage.total}
                </p>
              )}
            </div>
          </div>
          {index < stages.length - 1 && (
            <div
              className={cn(
                'h-0.5 w-8 transition-colors duration-300',
                stage.status === 'completed' ? 'bg-success' : 'bg-muted'
              )}
            />
          )}
        </div>
      ))}
    </div>
  )
}
