import {
  HookEvents,
  type CustomerMutationHookEvent,
  type StripeReceivedHookEvent,
  type SubscriptionMutationHookEvent,
} from 'subscrio';
import type { InsertTransactionLogInput } from './repository/PostgresAuditLogRepository.js';

type EntityDto = { key?: string; customerKey?: string } | null;

function dtoKey(dto: EntityDto): string | null {
  return dto?.key ?? null;
}

function customerKeyFrom(dto: EntityDto): string | null {
  if (!dto) return null;
  if ('customerKey' in dto && dto.customerKey) return dto.customerKey;
  return dto.key ?? null;
}

function stripAfter(type: string): string {
  return type.endsWith('.after') ? type.slice(0, -'.after'.length) : type;
}

function actionFromCustomerEvent(type: string): string {
  if (type.includes('.created.')) return 'create';
  if (type.includes('.updated.')) return 'update';
  if (type.includes('.archived.')) return 'archive';
  if (type.includes('.unarchived.')) return 'unarchive';
  if (type.includes('.deleted.')) return 'delete';
  return 'update';
}

function actionFromSubscriptionEvent(type: string): string {
  if (type.includes('featureOverride')) return 'feature_override';
  if (type.includes('temporaryOverridesCleared')) return 'clear_overrides';
  if (type.includes('.created.')) return 'create';
  if (type.includes('.updated.')) return 'update';
  if (type.includes('.archived.')) return 'archive';
  if (type.includes('.unarchived.')) return 'unarchive';
  if (type.includes('.deleted.')) return 'delete';
  return 'update';
}

function asJson(value: unknown): Record<string, unknown> | null {
  if (value === null || value === undefined) return null;
  return value as Record<string, unknown>;
}

export function mapCustomerAfterEvent(event: CustomerMutationHookEvent): InsertTransactionLogInput {
  const dto = event.new ?? event.old;
  const key = dtoKey(dto);
  const action = actionFromCustomerEvent(event.type);
  const label = stripAfter(event.type);

  return {
    source: event.source,
    action,
    entityType: 'customer',
    entityKey: key,
    customerKey: key,
    subscriptionKey: null,
    customerId: event.entityId,
    subscriptionId: null,
    actor: null,
    summary: key ? `${label}: ${key}` : label,
    preValue: asJson(event.old),
    postValue: asJson(event.new),
    metadata: { hookType: event.type },
  };
}

export function mapSubscriptionAfterEvent(
  event: SubscriptionMutationHookEvent
): InsertTransactionLogInput {
  const dto = event.new ?? event.old;
  const key = dtoKey(dto);
  const custKey = customerKeyFrom(dto) ?? customerKeyFrom(event.old) ?? customerKeyFrom(event.new);
  const action = actionFromSubscriptionEvent(event.type);
  const label = stripAfter(event.type);

  const metadata: Record<string, unknown> = { hookType: event.type };
  if (event.featureKey !== undefined) metadata.featureKey = event.featureKey;
  if (event.value !== undefined) metadata.value = event.value;
  if (event.overrideType !== undefined) metadata.overrideType = event.overrideType;

  return {
    source: event.source,
    action,
    entityType: 'subscription',
    entityKey: key,
    customerKey: custKey,
    subscriptionKey: key,
    customerId: event.customerId ?? null,
    subscriptionId: event.entityId,
    actor: null,
    summary: key ? `${label}: ${key}` : label,
    preValue: asJson(event.old),
    postValue: asJson(event.new),
    metadata,
  };
}

export interface StripeEntityAssociationInput {
  customerId?: number | null;
  customerKey?: string | null;
  subscriptionId?: number | null;
  subscriptionKey?: string | null;
  stripeCustomerId?: string | null;
  stripeSubscriptionId?: string | null;
}

export function mapStripeReceivedAfterEvent(
  event: StripeReceivedHookEvent,
  association: StripeEntityAssociationInput = {}
): InsertTransactionLogInput {
  const stripeEvent = event.data;
  const eventId = stripeEvent?.id ?? null;
  const eventType = stripeEvent?.type ?? null;

  const metadata: Record<string, unknown> = { hookType: event.type };
  if (association.stripeCustomerId) {
    metadata.stripeCustomerId = association.stripeCustomerId;
  }
  if (association.stripeSubscriptionId) {
    metadata.stripeSubscriptionId = association.stripeSubscriptionId;
  }

  return {
    source: 'stripe',
    action: 'stripe_event',
    entityType: 'stripe_event',
    entityKey: eventId,
    customerKey: association.customerKey ?? null,
    subscriptionKey: association.subscriptionKey ?? null,
    customerId: association.customerId ?? null,
    subscriptionId: association.subscriptionId ?? null,
    actor: null,
    summary: eventType ? `stripe.received: ${eventType}` : 'stripe.received',
    stripeEventId: eventId,
    stripeEventType: eventType,
    eventPayload: asJson(stripeEvent),
    preValue: null,
    postValue: null,
    metadata,
  };
}

/** All after-hook event names the audit log registers */
export const AUDIT_AFTER_EVENTS = [
  HookEvents.CustomerCreatedAfter,
  HookEvents.CustomerUpdatedAfter,
  HookEvents.CustomerArchivedAfter,
  HookEvents.CustomerUnarchivedAfter,
  HookEvents.CustomerDeletedAfter,
  HookEvents.SubscriptionCreatedAfter,
  HookEvents.SubscriptionUpdatedAfter,
  HookEvents.SubscriptionArchivedAfter,
  HookEvents.SubscriptionUnarchivedAfter,
  HookEvents.SubscriptionDeletedAfter,
  HookEvents.SubscriptionFeatureOverrideAddedAfter,
  HookEvents.SubscriptionFeatureOverrideRemovedAfter,
  HookEvents.SubscriptionTemporaryOverridesClearedAfter,
  HookEvents.StripeReceivedAfter,
] as const;
