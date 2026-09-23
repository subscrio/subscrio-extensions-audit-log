import { Pool } from 'pg';
import type { Subscrio } from 'subscrio';
import {
  HookEvents,
  type CustomerMutationHookEvent,
  type AccountingMutationHookEvent,
  type StripeReceivedHookEvent,
  type SubscriptionMutationHookEvent,
} from 'subscrio';
import type {
  TransactionLogDto,
  TransactionLogFilterDto,
  TransactionLogListResult,
} from './dtos/TransactionLogDto.js';
import { extractStripeEntityRefs } from './extractStripeEntityRefs.js';
import {
  mapCustomerAfterEvent,
  mapStripeReceivedAfterEvent,
  mapSubscriptionAfterEvent,
} from './mapHookEvent.js';
import { PostgresAuditLogRepository } from './repository/PostgresAuditLogRepository.js';
import { resolveStripeEntities } from './resolveStripeEntities.js';
import { AuditLogSchemaInstaller } from './schema/installer.js';

export interface AuditLogDatabaseConfig {
  connectionString: string;
}

export interface AuditLogOptions {
  database: AuditLogDatabaseConfig;
}

export interface AuditLog {
  installSchema(): Promise<void>;
  verifySchema(): Promise<string | null>;
  migrate(): Promise<number>;
  list(filters?: Partial<TransactionLogFilterDto>): Promise<TransactionLogListResult>;
  get(id: number): Promise<TransactionLogDto | null>;
  dispose(): Promise<void>;
}

/**
 * Create an audit-log extension bound to a Subscrio instance.
 *
 * Registers handlers for all `*.after` HookEvents. Rows are written after the
 * mutation has already been committed, so:
 * - If the audit insert throws, the API call still fails (hook error propagates),
 *   but the underlying customer/subscription change remains in the database.
 * - Call `dispose()` to unsubscribe all handlers and close the connection pool.
 */
export function createAuditLog(subscrio: Subscrio, options: AuditLogOptions): AuditLog {
  const pool = new Pool({ connectionString: options.database.connectionString });
  const installer = new AuditLogSchemaInstaller(pool);
  const repository = new PostgresAuditLogRepository(pool);
  const unsubscribers: Array<() => void> = [];

  const writeCustomer = async (event: CustomerMutationHookEvent) => {
    await repository.insert(mapCustomerAfterEvent(event));
  };
  const writeSubscription = async (event: SubscriptionMutationHookEvent) => {
    await repository.insert(mapSubscriptionAfterEvent(event));
  };
  const writeStripe = async (event: StripeReceivedHookEvent) => {
    const refs = extractStripeEntityRefs(event.data);
    const association = await resolveStripeEntities(pool, refs);
    await repository.insert(
      mapStripeReceivedAfterEvent(event, {
        ...association,
        stripeCustomerId: refs.stripeCustomerId ?? null,
        stripeSubscriptionId: refs.stripeSubscriptionId ?? null,
      })
    );
  };

  const writeAccounting = async (event: AccountingMutationHookEvent) => {
    const subscriptionKey = typeof event.input.subscriptionKey === 'string' ? event.input.subscriptionKey : null;
    const subscription = subscriptionKey ? await subscrio.subscriptions.getSubscription(subscriptionKey) : null;
    const customerKey = typeof event.input.customerKey === 'string' ? event.input.customerKey : subscription?.customerKey ?? null;
    await repository.insert({
      source: event.source,
      action: event.type.split('.')[1],
      entityType: event.type.split('.')[0],
      entityKey: typeof event.input.idempotencyKey === 'string' ? event.input.idempotencyKey : subscriptionKey,
      customerKey,
      subscriptionKey,
      summary: event.type,
      preValue: null,
      postValue: event.result,
      metadata: { hookType: event.type, input: event.input },
    });
  };
  unsubscribers.push(
    ...[
      HookEvents.SubscriptionAddonAttachedAfter,
      HookEvents.SubscriptionAddonDetachedAfter,
      HookEvents.UsageReportedAfter,
      HookEvents.CreditConsumedAfter,
      HookEvents.CreditGrantedAfter,
      HookEvents.CreditAdjustedAfter,
    ].map(name => subscrio.hooks.on(name, writeAccounting)),
    subscrio.hooks.on(HookEvents.CustomerCreatedAfter, writeCustomer),
    subscrio.hooks.on(HookEvents.CustomerUpdatedAfter, writeCustomer),
    subscrio.hooks.on(HookEvents.CustomerArchivedAfter, writeCustomer),
    subscrio.hooks.on(HookEvents.CustomerUnarchivedAfter, writeCustomer),
    subscrio.hooks.on(HookEvents.CustomerDeletedAfter, writeCustomer),
    subscrio.hooks.on(HookEvents.SubscriptionCreatedAfter, writeSubscription),
    subscrio.hooks.on(HookEvents.SubscriptionUpdatedAfter, writeSubscription),
    subscrio.hooks.on(HookEvents.SubscriptionArchivedAfter, writeSubscription),
    subscrio.hooks.on(HookEvents.SubscriptionUnarchivedAfter, writeSubscription),
    subscrio.hooks.on(HookEvents.SubscriptionDeletedAfter, writeSubscription),
    subscrio.hooks.on(HookEvents.SubscriptionFeatureOverrideAddedAfter, writeSubscription),
    subscrio.hooks.on(HookEvents.SubscriptionFeatureOverrideRemovedAfter, writeSubscription),
    subscrio.hooks.on(HookEvents.SubscriptionTemporaryOverridesClearedAfter, writeSubscription),
    subscrio.hooks.on(HookEvents.StripeReceivedAfter, writeStripe)
  );

  let disposed = false;

  return {
    async installSchema() {
      await installer.install();
    },
    async verifySchema() {
      return installer.verify();
    },
    async migrate() {
      return installer.migrate();
    },
    async list(filters) {
      return repository.list(filters);
    },
    async get(id) {
      return repository.get(id);
    },
    async dispose() {
      if (disposed) return;
      disposed = true;
      for (const off of unsubscribers) {
        off();
      }
      unsubscribers.length = 0;
      await pool.end();
    },
  };
}
