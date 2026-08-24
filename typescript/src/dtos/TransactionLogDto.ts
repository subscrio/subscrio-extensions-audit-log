import { z } from 'zod';

export type TransactionLogSource = 'api' | 'stripe' | 'system' | string;

export type TransactionLogAction =
  | 'create'
  | 'update'
  | 'delete'
  | 'archive'
  | 'unarchive'
  | 'feature_override'
  | 'clear_overrides'
  | 'stripe_event'
  | string;

export type TransactionLogEntityType = 'customer' | 'subscription' | 'stripe_event' | string;

export interface TransactionLogDto {
  id: number;
  createdAt: string;
  source: TransactionLogSource;
  action: TransactionLogAction;
  entityType: TransactionLogEntityType;
  entityKey?: string | null;
  customerKey?: string | null;
  subscriptionKey?: string | null;
  customerId?: number | null;
  subscriptionId?: number | null;
  actor?: string | null;
  summary: string;
  stripeEventId?: string | null;
  stripeEventType?: string | null;
  eventPayload?: Record<string, unknown> | null;
  preValue?: Record<string, unknown> | null;
  postValue?: Record<string, unknown> | null;
  metadata?: Record<string, unknown> | null;
}

export const TransactionLogFilterDtoSchema = z.object({
  customerKey: z.string().optional(),
  subscriptionKey: z.string().optional(),
  entityType: z.string().optional(),
  source: z.string().optional(),
  action: z.string().optional(),
  stripeEventId: z.string().optional(),
  stripeEventType: z.string().optional(),
  search: z.string().optional(),
  startDate: z.union([z.string(), z.date()]).optional(),
  endDate: z.union([z.string(), z.date()]).optional(),
  sortBy: z
    .enum(['createdAt', 'id', 'source', 'action', 'entityType', 'summary'])
    .optional()
    .default('createdAt'),
  sortOrder: z.enum(['asc', 'desc']).optional().default('desc'),
  limit: z.number().int().min(1).max(500).optional().default(50),
  offset: z.number().int().min(0).optional().default(0),
});

export type TransactionLogFilterDto = z.infer<typeof TransactionLogFilterDtoSchema>;

export interface TransactionLogListResult {
  data: TransactionLogDto[];
  total: number;
}
