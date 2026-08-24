import type { TransactionLogDto } from '../dtos/TransactionLogDto.js';

/** Raw row from subscrio.transaction_logs */
export interface TransactionLogRecord {
  id: string | number;
  created_at: Date | string;
  source: string;
  action: string;
  entity_type: string;
  entity_key: string | null;
  customer_key: string | null;
  subscription_key: string | null;
  customer_id: string | number | null;
  subscription_id: string | number | null;
  actor: string | null;
  summary: string;
  stripe_event_id: string | null;
  stripe_event_type: string | null;
  event_payload: Record<string, unknown> | null;
  pre_value: Record<string, unknown> | null;
  post_value: Record<string, unknown> | null;
  metadata: Record<string, unknown> | null;
}

function toNumber(value: string | number | null | undefined): number | null {
  if (value === null || value === undefined) return null;
  return typeof value === 'number' ? value : Number(value);
}

function toIso(value: Date | string): string {
  if (value instanceof Date) return value.toISOString();
  return new Date(value).toISOString();
}

export class TransactionLogMapper {
  static toDto(record: TransactionLogRecord): TransactionLogDto {
    return {
      id: toNumber(record.id)!,
      createdAt: toIso(record.created_at),
      source: record.source,
      action: record.action,
      entityType: record.entity_type,
      entityKey: record.entity_key,
      customerKey: record.customer_key,
      subscriptionKey: record.subscription_key,
      customerId: toNumber(record.customer_id),
      subscriptionId: toNumber(record.subscription_id),
      actor: record.actor,
      summary: record.summary,
      stripeEventId: record.stripe_event_id,
      stripeEventType: record.stripe_event_type,
      eventPayload: record.event_payload,
      preValue: record.pre_value,
      postValue: record.post_value,
      metadata: record.metadata,
    };
  }
}
