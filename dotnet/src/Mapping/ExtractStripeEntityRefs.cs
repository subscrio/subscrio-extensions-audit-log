namespace Subscrio.AuditLog.Mapping;

/// <summary>
/// Stripe customer / subscription IDs pulled from a verified Stripe event payload.
/// </summary>
public sealed class StripeEntityRefs
{
    public string? StripeCustomerId { get; init; }
    public string? StripeSubscriptionId { get; init; }
}

/// <summary>
/// Best-effort extraction of Stripe entity IDs from Event.Data.Object.
/// </summary>
public static class ExtractStripeEntityRefs
{
    public static StripeEntityRefs FromEvent(Stripe.Event? stripeEvent)
    {
        if (stripeEvent?.Data?.Object == null)
            return new StripeEntityRefs();

        var obj = stripeEvent.Data.Object;
        var type = stripeEvent.Type ?? string.Empty;

        // customer.* (not customer.subscription.*)
        if (type.StartsWith("customer.", StringComparison.Ordinal)
            && !type.StartsWith("customer.subscription.", StringComparison.Ordinal))
        {
            return new StripeEntityRefs
            {
                StripeCustomerId = obj is Stripe.Customer c ? AsId(c.Id) : null
            };
        }

        // customer.subscription.*
        if (type.StartsWith("customer.subscription.", StringComparison.Ordinal)
            && obj is Stripe.Subscription subFromType)
        {
            return FromSubscription(subFromType);
        }

        return obj switch
        {
            Stripe.Invoice invoice => new StripeEntityRefs
            {
                StripeCustomerId = AsId(invoice.CustomerId) ?? AsId(invoice.Customer?.Id),
                StripeSubscriptionId = AsId(invoice.Parent?.SubscriptionDetails?.SubscriptionId)
                    ?? AsId(invoice.Parent?.SubscriptionDetails?.Subscription?.Id)
            },
            Stripe.Checkout.Session session => new StripeEntityRefs
            {
                StripeCustomerId = AsId(session.CustomerId) ?? AsId(session.Customer?.Id),
                StripeSubscriptionId = AsId(session.SubscriptionId) ?? AsId(session.Subscription?.Id)
            },
            Stripe.Customer customer => new StripeEntityRefs { StripeCustomerId = AsId(customer.Id) },
            Stripe.Subscription subscription => FromSubscription(subscription),
            _ => new StripeEntityRefs()
        };
    }

    private static StripeEntityRefs FromSubscription(Stripe.Subscription subscription) =>
        new()
        {
            StripeSubscriptionId = AsId(subscription.Id),
            StripeCustomerId = AsId(subscription.CustomerId) ?? AsId(subscription.Customer?.Id)
        };

    private static string? AsId(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
