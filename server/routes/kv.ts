import { Router, json } from 'express'
import type { StorageProvider } from '../storage/types.js'

export function createKVRouter(storage: StorageProvider): Router {
  const router = Router()
  router.use(json({ limit: '10mb' }))

  // GET /api/kv/keys - list all keys
  router.get('/keys', async (_req, res) => {
    try {
      const allKeys = await storage.keys()
      res.json(allKeys)
    } catch (err) {
      console.error('KV keys error:', err)
      res.status(500).json({ error: 'Failed to list keys' })
    }
  })

  // GET /api/kv/:key - get value
  router.get('/:key', async (req, res) => {
    try {
      const value = await storage.get(req.params.key)
      if (value === undefined) {
        res.status(404).json({ error: 'Key not found' })
      } else {
        res.json(value)
      }
    } catch (err) {
      console.error('KV get error:', err)
      res.status(500).json({ error: 'Failed to get value' })
    }
  })

  // PUT /api/kv/:key - set value
  router.put('/:key', async (req, res) => {
    try {
      await storage.set(req.params.key, req.body)
      res.json({ ok: true })
    } catch (err) {
      console.error('KV set error:', err)
      res.status(500).json({ error: 'Failed to set value' })
    }
  })

  // DELETE /api/kv/:key - delete key
  router.delete('/:key', async (req, res) => {
    try {
      await storage.delete(req.params.key)
      res.json({ ok: true })
    } catch (err) {
      console.error('KV delete error:', err)
      res.status(500).json({ error: 'Failed to delete key' })
    }
  })

  return router
}
