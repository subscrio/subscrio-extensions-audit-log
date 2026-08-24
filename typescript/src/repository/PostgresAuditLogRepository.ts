import type { Pool } from 'pg';
import {
  TransactionLogFilterDtoSchema,
  type TransactionLogDto,
  type TransactionLogFilterDto,
  type TransactionLogListResult,
} from '../dtos/TransactionLogDto.js';
import { TransactionLogMapper, type TransactionLogRecord } from '../mappers/TransactionLogMapper.js';

export interface InsertTransactionLogInput {
  source: string;
  action: string;
  entityType: string;
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
  preValue?: unknown;
  postValue?: unknown;
  metadata?: Record<string, unknown> | null;
}

const SORT_COLUMNS: Record<string, string> = {
  createdAt: 'created_at',
  id: 'id',
  source: 'source',
  action: 'action',
  entityType: 'entity_type',
  summary: 'summary',
};

export class PostgresAuditLogRepository {
  constructor(private readonly pool: Pool) {}

  async insert(input: InsertTransactionLogInput): Promise<void> {
    await this.pool.query(
      `INSERT INTO subscrio.transaction_logs (
        source, action, entity_type, entity_key, customer_key, subscription_key,
        customer_id, subscription_id, actor, summary,
        stripe_event_id, stripe_event_type, event_payload, pre_value, post_value, metadata
      ) VALUES (
        $1, $2, $3, $4, $5, $6,
        $7, $8, $9, $10,
        $11, $12, $13, $14, $15, $16
      )`,
      [
        input.source,
        input.action,
        input.entityType,
        input.entityKey ?? null,
        input.customerKey ?? null,
        input.subscriptionKey ?? null,
        input.customerId ?? null,
        input.subscriptionId ?? null,
        input.actor ?? null,
        input.summary,
        input.stripeEventId ?? null,
        input.stripeEventType ?? null,
        input.eventPayload != null ? JSON.stringify(input.eventPayload) : null,
        input.preValue != null ? JSON.stringify(input.preValue) : null,
        input.postValue != null ? JSON.stringify(input.postValue) : null,
        input.metadata != null ? JSON.stringify(input.metadata) : null,
      ]
    );
  }

  async get(id: number): Promise<TransactionLogDto | null> {
    const result = await this.pool.query<TransactionLogRecord>(
      `SELECT * FROM subscrio.transaction_logs WHERE id = $1`,
      [id]
    );
    const row = result.rows[0];
    return row ? TransactionLogMapper.toDto(row) : null;
  }

  async list(filters?: Partial<TransactionLogFilterDto>): Promise<TransactionLogListResult> {
    const parsed = TransactionLogFilterDtoSchema.safeParse(filters ?? {});
    if (!parsed.success) {
      throw new Error(`Invalid transaction log filters: ${parsed.error.message}`);
    }
    const f = parsed.data;

    const where: string[] = [];
    const params: unknown[] = [];
    let i = 1;

    const add = (clause: string, value: unknown) => {
      where.push(clause.replace('?', `$${i++}`));
      params.push(value);
    };

    if (f.customerKey) add('customer_key = ?', f.customerKey);
    if (f.subscriptionKey) add('subscription_key = ?', f.subscriptionKey);
    if (f.entityType) add('entity_type = ?', f.entityType);
    if (f.source) add('source = ?', f.source);
    if (f.action) add('action = ?', f.action);
    if (f.stripeEventId) add('stripe_event_id = ?', f.stripeEventId);
    if (f.stripeEventType) add('stripe_event_type = ?', f.stripeEventType);
    if (f.startDate) add('created_at >= ?', f.startDate instanceof Date ? f.startDate : new Date(f.startDate));
    if (f.endDate) add('created_at <= ?', f.endDate instanceof Date ? f.endDate : new Date(f.endDate));
    if (f.search) {
      where.push(
        `(summary ILIKE $${i} OR entity_key ILIKE $${i} OR customer_key ILIKE $${i} OR subscription_key ILIKE $${i} OR actor ILIKE $${i})`
      );
      params.push(`%${f.search}%`);
      i++;
    }

    const whereSql = where.length > 0 ? `WHERE ${where.join(' AND ')}` : '';
    const sortCol = SORT_COLUMNS[f.sortBy] ?? 'created_at';
    const sortOrder = f.sortOrder === 'asc' ? 'ASC' : 'DESC';

    const countResult = await this.pool.query<{ count: string }>(
      `SELECT COUNT(*)::text AS count FROM subscrio.transaction_logs ${whereSql}`,
      params
    );
    const total = Number(countResult.rows[0]?.count ?? 0);

    const dataParams = [...params, f.limit, f.offset];
    const dataResult = await this.pool.query<TransactionLogRecord>(
      `SELECT * FROM subscrio.transaction_logs
       ${whereSql}
       ORDER BY ${sortCol} ${sortOrder}, id ${sortOrder}
       LIMIT $${i++} OFFSET $${i}`,
      dataParams
    );

    return {
      data: dataResult.rows.map(TransactionLogMapper.toDto),
      total,
    };
  }
}
