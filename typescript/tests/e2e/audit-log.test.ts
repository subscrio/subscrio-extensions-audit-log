import { afterAll, beforeAll, describe, expect, test } from 'vitest';
import { OverrideType, type Subscrio } from 'subscrio';
import {
  createAuditLog,
  AUDIT_LOG_SCHEMA_VERSION,
  type AuditLog,
} from '../../src/index.js';
import { setupTestDatabase, teardownTestDatabase } from '../setup/database.js';

function unique(prefix: string): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

describe('Audit Log E2E', () => {
  let subscrio: Subscrio;
  let audit: AuditLog;
  let dbName: string;
  let connectionString: string;

  beforeAll(async () => {
    const ctx = await setupTestDatabase();
    subscrio = ctx.subscrio;
    dbName = ctx.dbName;
    connectionString = ctx.connectionString;
    audit = createAuditLog(subscrio, { database: { connectionString } });
  });

  afterAll(async () => {
    if (audit) await audit.dispose();
    await teardownTestDatabase(dbName, subscrio);
  });

  async function createSubscriptionFixture() {
    const product = await subscrio.products.createProduct({
      key: unique('prod'),
      displayName: 'Audit Product',
    });
    const plan = await subscrio.plans.createPlan({
      productKey: product.key,
      key: unique('plan'),
      displayName: 'Audit Plan',
    });
    const cycle = await subscrio.billingCycles.createBillingCycle({
      planKey: plan.key,
      key: unique('cycle'),
      displayName: 'Monthly',
      durationValue: 1,
      durationUnit: 'months',
    });
    const customer = await subscrio.customers.createCustomer({
      key: unique('cust'),
      displayName: 'Audit Customer',
    });
    return { product, plan, cycle, customer };
  }

  test('installSchema / verifySchema / idempotent install', async () => {
    expect(await audit.verifySchema()).toBeNull();

    await audit.installSchema();
    expect(await audit.verifySchema()).toBe(AUDIT_LOG_SCHEMA_VERSION);

    await audit.installSchema();
    expect(await audit.verifySchema()).toBe(AUDIT_LOG_SCHEMA_VERSION);

    const migrated = await audit.migrate();
    expect(migrated).toBe(0);
  });

  test('customer create and update write audit rows', async () => {
    await audit.installSchema();

    const key = unique('acme');
    await subscrio.customers.createCustomer({
      key,
      displayName: 'Acme',
    });
    await subscrio.customers.updateCustomer(key, { displayName: 'Acme Corp' });

    const { data, total } = await audit.list({ customerKey: key, sortOrder: 'asc' });
    expect(total).toBeGreaterThanOrEqual(2);

    const createRow = data.find((r) => r.action === 'create' && r.entityType === 'customer');
    const updateRow = data.find((r) => r.action === 'update' && r.entityType === 'customer');

    expect(createRow).toBeDefined();
    expect(createRow!.entityKey).toBe(key);
    expect(createRow!.customerKey).toBe(key);
    expect(createRow!.customerId).toBeTypeOf('number');
    expect(createRow!.source).toBe('api');
    expect(createRow!.summary).toBe(`customer.created: ${key}`);
    expect(createRow!.postValue).toMatchObject({ key, displayName: 'Acme' });

    expect(updateRow).toBeDefined();
    expect(updateRow!.summary).toBe(`customer.updated: ${key}`);
    expect(updateRow!.preValue).toMatchObject({ displayName: 'Acme' });
    expect(updateRow!.postValue).toMatchObject({ displayName: 'Acme Corp' });

    const fetched = await audit.get(createRow!.id);
    expect(fetched?.id).toBe(createRow!.id);
    expect(fetched?.customerKey).toBe(key);
  });

  test('subscription create and feature override write rows', async () => {
    await audit.installSchema();
    const { product, cycle, customer } = await createSubscriptionFixture();

    const feature = await subscrio.features.createFeature({
      key: unique('feat'),
      displayName: 'Feat',
      valueType: 'toggle',
      defaultValue: 'false',
    });
    await subscrio.products.associateFeature(product.key, feature.key);

    const subKey = unique('sub');
    await subscrio.subscriptions.createSubscription({
      key: subKey,
      customerKey: customer.key,
      billingCycleKey: cycle.key,
    });

    await subscrio.subscriptions.addFeatureOverride(
      subKey,
      feature.key,
      'true',
      OverrideType.Permanent
    );

    const { data } = await audit.list({ subscriptionKey: subKey });
    const createRow = data.find((r) => r.action === 'create' && r.entityType === 'subscription');
    const overrideRow = data.find((r) => r.action === 'feature_override');

    expect(createRow).toBeDefined();
    expect(createRow!.customerKey).toBe(customer.key);
    expect(createRow!.subscriptionId).toBeTypeOf('number');
    expect(createRow!.customerId).toBeTypeOf('number');
    expect(createRow!.summary).toBe(`subscription.created: ${subKey}`);

    expect(overrideRow).toBeDefined();
    expect(overrideRow!.metadata).toMatchObject({
      featureKey: feature.key,
      value: 'true',
    });
  });

  test('stripe.received.after writes stripe_event row', async () => {
    await audit.installSchema();

    const product = await subscrio.products.createProduct({
      key: unique('sprod'),
      displayName: 'Stripe Product',
    });
    const plan = await subscrio.plans.createPlan({
      productKey: product.key,
      key: unique('splan'),
      displayName: 'Stripe Plan',
    });
    const priceId = unique('price');
    await subscrio.billingCycles.createBillingCycle({
      planKey: plan.key,
      key: unique('scycle'),
      displayName: 'Monthly',
      durationValue: 1,
      durationUnit: 'months',
      externalProductId: priceId,
    });
    const stripeCustomerId = unique('cus');
    const customer = await subscrio.customers.createCustomer({
      key: unique('scust'),
      displayName: 'Stripe Cust',
      externalBillingId: stripeCustomerId,
    });

    const eventId = unique('evt');
    const stripeSubId = unique('sub');
    const event = {
      id: eventId,
      object: 'event',
      api_version: '2026-07-29.dahlia',
      created: Math.floor(Date.now() / 1000),
      data: {
        object: {
          id: stripeSubId,
          object: 'subscription',
          customer: stripeCustomerId,
          status: 'active',
          created: Math.floor(Date.now() / 1000),
          cancel_at_period_end: false,
          items: {
            data: [{
              price: { id: priceId },
              current_period_start: Math.floor(Date.now() / 1000),
              current_period_end: Math.floor(Date.now() / 1000) + 86400 * 30,
            }],
          },
          metadata: {},
        },
      },
      livemode: false,
      pending_webhooks: 0,
      request: null,
      type: 'customer.subscription.created',
    };

    await subscrio.stripe.processStripeEvent(event as any);

    const { data, total } = await audit.list({
      stripeEventId: eventId,
      entityType: 'stripe_event',
    });
    expect(total).toBeGreaterThanOrEqual(1);
    const row = data[0];
    expect(row.action).toBe('stripe_event');
    expect(row.entityType).toBe('stripe_event');
    expect(row.stripeEventId).toBe(eventId);
    expect(row.stripeEventType).toBe('customer.subscription.created');
    expect(row.customerKey).toBe(customer.key);
    expect(row.customerId).not.toBeNull();
    expect(row.subscriptionId).not.toBeNull();
    expect(row.subscriptionKey).toBeTruthy();
    expect(row.metadata).toMatchObject({
      stripeCustomerId,
      stripeSubscriptionId: stripeSubId,
    });
    expect(row.eventPayload).toMatchObject({ id: eventId, type: 'customer.subscription.created' });
  });

  test('list filters return data and total', async () => {
    await audit.installSchema();
    const keyA = unique('filter-a');
    const keyB = unique('filter-b');
    await subscrio.customers.createCustomer({ key: keyA, displayName: 'A' });
    await subscrio.customers.createCustomer({ key: keyB, displayName: 'B' });

    const byCustomer = await audit.list({ customerKey: keyA });
    expect(byCustomer.data.every((r) => r.customerKey === keyA)).toBe(true);
    expect(byCustomer.total).toBe(byCustomer.data.length);

    const byAction = await audit.list({ action: 'create', entityType: 'customer', limit: 10 });
    expect(byAction.data.every((r) => r.action === 'create')).toBe(true);
    expect(byAction.total).toBeGreaterThanOrEqual(2);

    const bySearch = await audit.list({ search: keyB });
    expect(bySearch.data.some((r) => r.customerKey === keyB)).toBe(true);

    const bySource = await audit.list({ source: 'api', limit: 5 });
    expect(bySource.data.every((r) => r.source === 'api')).toBe(true);
  });

  test('after-hook audit write failure fails API; dispose unsubscribes', async () => {
    await audit.installSchema();

    // Dispose unsubscribes — further mutations must not write audit rows
    await audit.dispose();

    const key = unique('disposed');
    await subscrio.customers.createCustomer({
      key,
      displayName: 'No Audit',
    });

    // Re-open a query-only pool via a fresh instance without hooks for listing,
    // or create temporary audit that only queries — easiest: new createAuditLog
    // will register hooks again, so use a second connection via list on new instance
    // after disposing the first. Instead query with a new AuditLog then dispose immediately
    // without leaving hooks — wait, createAuditLog always registers hooks.
    // Use raw pg via another createAuditLog then dispose after list.
    const reader = createAuditLog(subscrio, { database: { connectionString } });
    await reader.installSchema();
    // Immediately dispose hooks from reader... but that would leave the create we just
    // registered. Better approach: list before dispose of reader after counting prior rows.
    const before = await reader.list({ customerKey: key });
    // The create above happened while audit was disposed, so no row for this key
    // except if reader already wrote — reader registered after create, so no write.
    expect(before.total).toBe(0);
    await reader.dispose();

    // Recreate audit for remaining suite isolation — already disposed in afterAll path
    audit = createAuditLog(subscrio, { database: { connectionString } });
    await audit.installSchema();

    // Simulate sink failure: dispose pool mid-flight by closing then mutating
    // Drop table to force insert failure while hooks still registered
    const { Client } = await import('pg');
    const client = new Client({ connectionString });
    await client.connect();
    await client.query('DROP TABLE IF EXISTS subscrio.transaction_logs CASCADE');
    await client.end();

    const failKey = unique('fail');
    await expect(
      subscrio.customers.createCustomer({
        key: failKey,
        displayName: 'Should Fail Audit',
      })
    ).rejects.toThrow();

    // Mutation already committed (after-hook runs after persist)
    const persisted = await subscrio.customers.getCustomer(failKey);
    expect(persisted).not.toBeNull();
    expect(persisted?.displayName).toBe('Should Fail Audit');

    // Restore table so afterAll dispose / other cleanup is clean
    await audit.installSchema();
  });
});
