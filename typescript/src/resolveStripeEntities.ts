import type { Pool } from 'pg';
import type { StripeEntityRefs } from './extractStripeEntityRefs.js';

export interface StripeEntityAssociation {
  customerId: number | null;
  customerKey: string | null;
  subscriptionId: number | null;
  subscriptionKey: string | null;
}

const EMPTY: StripeEntityAssociation = {
  customerId: null,
  customerKey: null,
  subscriptionId: null,
  subscriptionKey: null,
};

/**
 * Resolve Subscrio customer/subscription rows from Stripe IDs using the shared DB.
 * Uses core tables directly — public Subscrio APIs do not expose these lookups or numeric IDs.
 */
export async function resolveStripeEntities(
  pool: Pool,
  refs: StripeEntityRefs
): Promise<StripeEntityAssociation> {
  if (!refs.stripeCustomerId && !refs.stripeSubscriptionId) {
    return { ...EMPTY };
  }

  let customerId: number | null = null;
  let customerKey: string | null = null;
  let subscriptionId: number | null = null;
  let subscriptionKey: string | null = null;

  if (refs.stripeSubscriptionId) {
    const { rows } = await pool.query<{
      id: string;
      key: string;
      customer_id: string;
    }>(
      `SELECT id, key, customer_id
       FROM subscrio.subscriptions
       WHERE stripe_subscription_id = $1
       LIMIT 1`,
      [refs.stripeSubscriptionId]
    );
    if (rows[0]) {
      subscriptionId = Number(rows[0].id);
      subscriptionKey = rows[0].key;
      customerId = Number(rows[0].customer_id);
    }
  }

  if (refs.stripeCustomerId && customerId == null) {
    const { rows } = await pool.query<{ id: string; key: string }>(
      `SELECT id, key
       FROM subscrio.customers
       WHERE external_billing_id = $1
       LIMIT 1`,
      [refs.stripeCustomerId]
    );
    if (rows[0]) {
      customerId = Number(rows[0].id);
      customerKey = rows[0].key;
    }
  } else if (customerId != null && customerKey == null) {
    const { rows } = await pool.query<{ key: string }>(
      `SELECT key FROM subscrio.customers WHERE id = $1 LIMIT 1`,
      [customerId]
    );
    if (rows[0]) {
      customerKey = rows[0].key;
    }
  }

  return { customerId, customerKey, subscriptionId, subscriptionKey };
}
