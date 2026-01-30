import { useEffect, useRef, useState, ReactNode } from 'react'
import { Dialog, DialogContent, DialogPortal } from '@/components/ui/dialog'
import { cn } from '@/lib/utils'
import { ArrowsOutCardinal } from '@phosphor-icons/react'

interface DraggableResizableDialogProps {
  open: boolean
  onOpenChange?: (open: boolean) => void
  children: ReactNode
  className?: string
  minWidth?: number
  minHeight?: number
  defaultWidth?: number
  defaultHeight?: number
}

export function DraggableResizableDialog({
  open,
  onOpenChange,
  children,
  className,
  minWidth = 400,
  minHeight = 300,
  defaultWidth = 600,
  defaultHeight = 500,
}: DraggableResizableDialogProps) {
  const contentRef = useRef<HTMLDivElement>(null)
  const [isDragging, setIsDragging] = useState(false)
  const [isResizing, setIsResizing] = useState(false)
  const [position, setPosition] = useState({ x: 0, y: 0 })
  const [size, setSize] = useState({ width: defaultWidth, height: defaultHeight })
  const [dragStart, setDragStart] = useState({ x: 0, y: 0 })
  const [resizeStart, setResizeStart] = useState({ x: 0, y: 0, width: 0, height: 0 })
  const [isInitialized, setIsInitialized] = useState(false)

  useEffect(() => {
    if (open && !isInitialized) {
      const centerX = (window.innerWidth - defaultWidth) / 2
      const centerY = (window.innerHeight - defaultHeight) / 2
      setPosition({ x: centerX, y: centerY })
      setSize({ width: defaultWidth, height: defaultHeight })
      setIsInitialized(true)
    }
  }, [open, isInitialized, defaultWidth, defaultHeight])

  useEffect(() => {
    if (!isDragging) return

    const handleMouseMove = (e: MouseEvent) => {
      const deltaX = e.clientX - dragStart.x
      const deltaY = e.clientY - dragStart.y
      
      setPosition(prev => ({
        x: Math.max(0, Math.min(window.innerWidth - size.width, prev.x + deltaX)),
        y: Math.max(0, Math.min(window.innerHeight - size.height, prev.y + deltaY)),
      }))
      
      setDragStart({ x: e.clientX, y: e.clientY })
    }

    const handleMouseUp = () => {
      setIsDragging(false)
    }

    document.addEventListener('mousemove', handleMouseMove)
    document.addEventListener('mouseup', handleMouseUp)

    return () => {
      document.removeEventListener('mousemove', handleMouseMove)
      document.removeEventListener('mouseup', handleMouseUp)
    }
  }, [isDragging, dragStart, size])

  useEffect(() => {
    if (!isResizing) return

    const handleMouseMove = (e: MouseEvent) => {
      const deltaX = e.clientX - resizeStart.x
      const deltaY = e.clientY - resizeStart.y
      
      const newWidth = Math.max(minWidth, resizeStart.width + deltaX)
      const newHeight = Math.max(minHeight, resizeStart.height + deltaY)
      
      setSize({
        width: Math.min(newWidth, window.innerWidth - position.x),
        height: Math.min(newHeight, window.innerHeight - position.y),
      })
    }

    const handleMouseUp = () => {
      setIsResizing(false)
    }

    document.addEventListener('mousemove', handleMouseMove)
    document.addEventListener('mouseup', handleMouseUp)

    return () => {
      document.removeEventListener('mousemove', handleMouseMove)
      document.removeEventListener('mouseup', handleMouseUp)
    }
  }, [isResizing, resizeStart, minWidth, minHeight, position])

  const handleHeaderMouseDown = (e: React.MouseEvent) => {
    if ((e.target as HTMLElement).closest('[data-resize-handle]')) return
    if ((e.target as HTMLElement).closest('button')) return
    
    setIsDragging(true)
    setDragStart({ x: e.clientX, y: e.clientY })
  }

  const handleResizeMouseDown = (e: React.MouseEvent) => {
    e.stopPropagation()
    setIsResizing(true)
    setResizeStart({
      x: e.clientX,
      y: e.clientY,
      width: size.width,
      height: size.height,
    })
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogPortal>
        <div className="fixed inset-0 z-50 bg-black/80" />
        <div
          ref={contentRef}
          className={cn(
            'fixed z-50 bg-background border shadow-lg rounded-lg overflow-hidden',
            className
          )}
          style={{
            left: `${position.x}px`,
            top: `${position.y}px`,
            width: `${size.width}px`,
            height: `${size.height}px`,
            cursor: isDragging ? 'grabbing' : isResizing ? 'nwse-resize' : 'default',
          }}
        >
          <div
            className="absolute inset-x-0 top-0 h-12 cursor-grab active:cursor-grabbing flex items-center px-6"
            onMouseDown={handleHeaderMouseDown}
          >
            <div className="absolute left-4 top-1/2 -translate-y-1/2 text-muted-foreground/40">
              <ArrowsOutCardinal size={16} />
            </div>
          </div>
          
          <div className="h-full overflow-hidden flex flex-col">
            {children}
          </div>

          <div
            data-resize-handle
            className="absolute bottom-0 right-0 w-4 h-4 cursor-nwse-resize group"
            onMouseDown={handleResizeMouseDown}
          >
            <div className="absolute bottom-0.5 right-0.5 w-3 h-3 border-r-2 border-b-2 border-muted-foreground/30 group-hover:border-muted-foreground/60 transition-colors" />
          </div>
        </div>
      </DialogPortal>
    </Dialog>
  )
}

interface DraggableDialogHeaderProps {
  children: ReactNode
  className?: string
}

export function DraggableDialogHeader({ children, className }: DraggableDialogHeaderProps) {
  return (
    <div className={cn('px-6 pt-6 pb-4 border-b', className)}>
      {children}
    </div>
  )
}

interface DraggableDialogBodyProps {
  children: ReactNode
  className?: string
}

export function DraggableDialogBody({ children, className }: DraggableDialogBodyProps) {
  return (
    <div className={cn('flex-1 overflow-auto', className)}>
      {children}
    </div>
  )
}

interface DraggableDialogFooterProps {
  children: ReactNode
  className?: string
}

export function DraggableDialogFooter({ children, className }: DraggableDialogFooterProps) {
  return (
    <div className={cn('px-6 py-4 border-t flex justify-end gap-2', className)}>
      {children}
    </div>
  )
}
