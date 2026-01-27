import { Card, CardContent } from '@/components/ui/card'
import { ArrowUp, ArrowDown, Minus } from '@phosphor-icons/react'
import { cn } from '@/lib/utils'

interface StatCardProps {
  label: string
  value: string | number
  trend?: {
    value: number
    direction: 'up' | 'down' | 'neutral'
  }
  icon?: React.ReactNode
  className?: string
}

export function StatCard({ label, value, trend, icon, className }: StatCardProps) {
  const getTrendIcon = () => {
    if (!trend) return null
    switch (trend.direction) {
      case 'up':
        return <ArrowUp className="text-success" size={16} />
      case 'down':
        return <ArrowDown className="text-destructive" size={16} />
      case 'neutral':
        return <Minus className="text-muted-foreground" size={16} />
    }
  }

  const getTrendColor = () => {
    if (!trend) return ''
    switch (trend.direction) {
      case 'up':
        return 'text-success'
      case 'down':
        return 'text-destructive'
      case 'neutral':
        return 'text-muted-foreground'
    }
  }

  return (
    <Card className={cn('transition-all duration-200 hover:shadow-md', className)}>
      <CardContent className="p-6">
        <div className="flex items-start justify-between gap-4">
          <div className="flex-1">
            <p className="text-sm text-muted-foreground mb-2">{label}</p>
            <p className="text-3xl font-mono font-semibold tracking-tight">{value}</p>
            {trend && (
              <div className="flex items-center gap-1 mt-2">
                {getTrendIcon()}
                <span className={cn('text-sm font-mono', getTrendColor())}>
                  {Math.abs(trend.value)}%
                </span>
              </div>
            )}
          </div>
          {icon && (
            <div className="text-accent opacity-60">
              {icon}
            </div>
          )}
        </div>
      </CardContent>
    </Card>
  )
}
