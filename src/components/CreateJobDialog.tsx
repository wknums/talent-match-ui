import { useState } from 'react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Card, CardContent } from '@/components/ui/card'
import { Plus, X } from '@phosphor-icons/react'
import { mockAPI } from '@/lib/api'
import { toast } from 'sonner'
import type { RubricCategory, MustHave, AggregationStrategy } from '@/types'

interface CreateJobDialogProps {
  open: boolean
  onClose: () => void
  onSuccess?: () => void
}

export function CreateJobDialog({ open, onClose, onSuccess }: CreateJobDialogProps) {
  const [title, setTitle] = useState('')
  const [department, setDepartment] = useState('')
  const [organization, setOrganization] = useState('')
  const [postingDate, setPostingDate] = useState(new Date().toISOString().split('T')[0])
  const [rubricCategories, setRubricCategories] = useState<RubricCategory[]>([
    { id: '1', name: '', description: '', weight: 0 },
  ])
  const [mustHaves, setMustHaves] = useState<MustHave[]>([
    { id: '1', criterion: '', description: '' },
  ])
  const [runsPerApplication, setRunsPerApplication] = useState('3')
  const [aggregationStrategy, setAggregationStrategy] = useState<AggregationStrategy>('median')
  const [longlistThreshold, setLonglistThreshold] = useState('60')
  const [shortlistThreshold, setShortlistThreshold] = useState('75')
  const [submitting, setSubmitting] = useState(false)

  const addRubricCategory = () => {
    setRubricCategories([
      ...rubricCategories,
      { id: Date.now().toString(), name: '', description: '', weight: 0 },
    ])
  }

  const removeRubricCategory = (id: string) => {
    setRubricCategories(rubricCategories.filter((c) => c.id !== id))
  }

  const updateRubricCategory = (id: string, field: keyof RubricCategory, value: string | number) => {
    setRubricCategories(
      rubricCategories.map((c) => (c.id === id ? { ...c, [field]: value } : c))
    )
  }

  const addMustHave = () => {
    setMustHaves([
      ...mustHaves,
      { id: Date.now().toString(), criterion: '', description: '' },
    ])
  }

  const removeMustHave = (id: string) => {
    setMustHaves(mustHaves.filter((m) => m.id !== id))
  }

  const updateMustHave = (id: string, field: keyof MustHave, value: string) => {
    setMustHaves(mustHaves.map((m) => (m.id === id ? { ...m, [field]: value } : m)))
  }

  const handleSubmit = async () => {
    if (!title || !department || !organization) {
      toast.error('Please fill in job title, department, and organization')
      return
    }

    const validCategories = rubricCategories.filter((c) => c.name && c.weight > 0)
    if (validCategories.length === 0) {
      toast.error('Please add at least one rubric category')
      return
    }

    const totalWeight = validCategories.reduce((sum, c) => sum + c.weight, 0)
    if (Math.abs(totalWeight - 1.0) > 0.01) {
      toast.error('Rubric weights must sum to 1.0')
      return
    }

    setSubmitting(true)
    try {
      await mockAPI.createJob({
        title,
        department,
        organization,
        postingDate,
        rubric: validCategories,
        mustHaves: mustHaves.filter((m) => m.criterion),
        runsPerApplication: parseInt(runsPerApplication),
        aggregationStrategy,
        longlistThreshold: parseFloat(longlistThreshold),
        shortlistThreshold: parseFloat(shortlistThreshold),
      })

      toast.success('Job created successfully')
      onSuccess?.()
      onClose()
      resetForm()
    } catch (error) {
      toast.error('Failed to create job')
    } finally {
      setSubmitting(false)
    }
  }

  const resetForm = () => {
    setTitle('')
    setDepartment('')
    setOrganization('')
    setPostingDate(new Date().toISOString().split('T')[0])
    setRubricCategories([{ id: '1', name: '', description: '', weight: 0 }])
    setMustHaves([{ id: '1', criterion: '', description: '' }])
    setRunsPerApplication('3')
    setAggregationStrategy('median')
    setLonglistThreshold('60')
    setShortlistThreshold('75')
  }

  return (
    <Dialog open={open} onOpenChange={onClose}>
      <DialogContent className="max-w-4xl max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>Create New Job</DialogTitle>
          <DialogDescription>
            Define job details, scoring rubric, and must-have requirements
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-6 mt-4">
          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="title">Job Title</Label>
              <Input
                id="title"
                value={title}
                onChange={(e) => setTitle(e.target.value)}
                placeholder="e.g., Senior Software Engineer"
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="department">Department</Label>
              <Input
                id="department"
                value={department}
                onChange={(e) => setDepartment(e.target.value)}
                placeholder="e.g., Engineering"
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="organization">Organization</Label>
              <Input
                id="organization"
                value={organization}
                onChange={(e) => setOrganization(e.target.value)}
                placeholder="e.g., TechCorp Solutions"
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="postingDate">Posting Date</Label>
              <Input
                id="postingDate"
                type="date"
                value={postingDate}
                onChange={(e) => setPostingDate(e.target.value)}
              />
            </div>
          </div>

          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <Label>Rubric Categories</Label>
              <Button type="button" variant="outline" size="sm" onClick={addRubricCategory}>
                <Plus size={16} />
                Add Category
              </Button>
            </div>
            {rubricCategories.map((category) => (
              <Card key={category.id}>
                <CardContent className="p-4">
                  <div className="space-y-3">
                    <div className="flex gap-2">
                      <div className="flex-1 space-y-2">
                        <Label>Name</Label>
                        <Input
                          value={category.name}
                          onChange={(e) => updateRubricCategory(category.id, 'name', e.target.value)}
                          placeholder="e.g., Technical Skills"
                        />
                      </div>
                      <div className="w-32 space-y-2">
                        <Label>Weight</Label>
                        <Input
                          type="number"
                          step="0.05"
                          min="0"
                          max="1"
                          value={category.weight}
                          onChange={(e) => updateRubricCategory(category.id, 'weight', parseFloat(e.target.value) || 0)}
                          placeholder="0.30"
                        />
                      </div>
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={() => removeRubricCategory(category.id)}
                        disabled={rubricCategories.length === 1}
                        className="mt-7"
                      >
                        <X size={16} />
                      </Button>
                    </div>
                    <div className="space-y-2">
                      <Label>Description</Label>
                      <Textarea
                        value={category.description}
                        onChange={(e) => updateRubricCategory(category.id, 'description', e.target.value)}
                        placeholder="What to evaluate in this category"
                        rows={2}
                      />
                    </div>
                  </div>
                </CardContent>
              </Card>
            ))}
          </div>

          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <Label>Must-Have Requirements</Label>
              <Button type="button" variant="outline" size="sm" onClick={addMustHave}>
                <Plus size={16} />
                Add Requirement
              </Button>
            </div>
            {mustHaves.map((mustHave) => (
              <Card key={mustHave.id}>
                <CardContent className="p-4">
                  <div className="flex gap-2">
                    <div className="flex-1 space-y-2">
                      <Label>Criterion</Label>
                      <Input
                        value={mustHave.criterion}
                        onChange={(e) => updateMustHave(mustHave.id, 'criterion', e.target.value)}
                        placeholder="e.g., Bachelor's degree in Computer Science"
                      />
                    </div>
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      onClick={() => removeMustHave(mustHave.id)}
                      disabled={mustHaves.length === 1}
                      className="mt-7"
                    >
                      <X size={16} />
                    </Button>
                  </div>
                </CardContent>
              </Card>
            ))}
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="runs">Runs per Application</Label>
              <Select value={runsPerApplication} onValueChange={setRunsPerApplication}>
                <SelectTrigger id="runs">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="1">1 run</SelectItem>
                  <SelectItem value="3">3 runs</SelectItem>
                  <SelectItem value="5">5 runs</SelectItem>
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-2">
              <Label htmlFor="aggregation">Aggregation Strategy</Label>
              <Select value={aggregationStrategy} onValueChange={(v) => setAggregationStrategy(v as AggregationStrategy)}>
                <SelectTrigger id="aggregation">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="median">Median</SelectItem>
                  <SelectItem value="mean">Mean</SelectItem>
                  <SelectItem value="weighted">Weighted</SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="longlist">Longlist Threshold</Label>
              <Input
                id="longlist"
                type="number"
                value={longlistThreshold}
                onChange={(e) => setLonglistThreshold(e.target.value)}
                placeholder="60"
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="shortlist">Shortlist Threshold</Label>
              <Input
                id="shortlist"
                type="number"
                value={shortlistThreshold}
                onChange={(e) => setShortlistThreshold(e.target.value)}
                placeholder="75"
              />
            </div>
          </div>
        </div>

        <div className="flex justify-end gap-2 mt-6">
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={handleSubmit} disabled={submitting}>
            {submitting ? 'Creating...' : 'Create Job'}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
