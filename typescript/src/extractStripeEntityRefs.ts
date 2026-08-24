/**
 * Pull Stripe customer / subscription IDs from a verified Stripe.Event payload.
 * Best-effort: unknown shapes leave fields undefined.
 */
export interface StripeEntityRefs {
  stripeCustomerId?: string;
  stripeSubscriptionId?: string;
}

function asId(value: unknown): string | undefined {
  if (typeof value === 'string' && value.length > 0) return value;
  if (
    value !== null &&
    typeof value === 'object' &&
    'id' in value &&
    typeof (value as { id: unknown }).id === 'string'
  ) {
    const id = (value as { id: string }).id;
    return id.length > 0 ? id : undefined;
  }
  return undefined;
}

export function extractStripeEntityRefs(stripeEvent: {
  type?: string | null;
  data?: { object?: unknown } | null;
} | null | undefined): StripeEntityRefs {
  if (!stripeEvent?.data?.object || typeof stripeEvent.data.object !== 'object') {
    return {};
  }

  const obj = stripeEvent.data.object as Record<string, unknown>;
  const type = stripeEvent.type ?? '';

  // customer.* (not customer.subscription.*)
  if (type.startsWith('customer.') && !type.startsWith('customer.subscription.')) {
    return { stripeCustomerId: asId(obj.id) };
  }

  // customer.subscription.*
  if (type.startsWith('customer.subscription.')) {
    return {
      stripeSubscriptionId: asId(obj.id),
      stripeCustomerId: asId(obj.customer),
    };
  }

  // invoice.*, checkout.session.*, charge.*, etc.
  const stripeCustomerId = asId(obj.customer);
  const parent = obj.parent as Record<string, unknown> | undefined;
  const subscriptionDetails = parent?.subscription_details as Record<string, unknown> | undefined;
  const stripeSubscriptionId = asId(obj.subscription) ??
    asId(subscriptionDetails?.subscription) ??
    (obj.object === 'subscription' ? asId(obj.id) : undefined);

  if (stripeCustomerId || stripeSubscriptionId) {
    return { stripeCustomerId, stripeSubscriptionId };
  }

  if (obj.object === 'customer') {
    return { stripeCustomerId: asId(obj.id) };
  }

  return {};
}
