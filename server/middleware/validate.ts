import { z } from 'zod';
import type { Request, Response, NextFunction } from 'express';

interface ValidationSchemas {
  body?: z.ZodSchema;
  params?: z.ZodSchema;
  query?: z.ZodSchema;
}

export function validate(schemas: ValidationSchemas) {
  return (req: Request, res: Response, next: NextFunction) => {
    const errors: z.ZodIssue[] = [];

    if (schemas.body) {
      const result = schemas.body.safeParse(req.body);
      if (!result.success) {
        errors.push(...result.error.issues);
      }
    }

    if (schemas.params) {
      const result = schemas.params.safeParse(req.params);
      if (!result.success) {
        errors.push(...result.error.issues);
      }
    }

    if (schemas.query) {
      const result = schemas.query.safeParse(req.query);
      if (!result.success) {
        errors.push(...result.error.issues);
      }
    }

    if (errors.length > 0) {
      return res.status(400).json({
        error: 'Validation Error',
        message: errors.map((e) => e.message).join(', '),
        details: errors,
      });
    }

    next();
  };
}