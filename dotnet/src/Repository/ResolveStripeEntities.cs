using Npgsql;
using Subscrio.AuditLog.Mapping;

namespace Subscrio.AuditLog.Repository;

public sealed class StripeEntityAssociation
{
    public long? CustomerId { get; init; }
    public string? CustomerKey { get; init; }
    public long? SubscriptionId { get; init; }
    public string? SubscriptionKey { get; init; }
}

/// <summary>
/// Resolve Subscrio customer/subscription rows from Stripe IDs using the shared DB.
/// Uses core tables directly — public Subscrio APIs do not expose these lookups or numeric IDs.
/// </summary>
public static class ResolveStripeEntities
{
    public static async Task<StripeEntityAssociation> ResolveAsync(
        NpgsqlDataSource dataSource,
        StripeEntityRefs refs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(refs.StripeCustomerId)
            && string.IsNullOrEmpty(refs.StripeSubscriptionId))
        {
            return new StripeEntityAssociation();
        }

        long? customerId = null;
        string? customerKey = null;
        long? subscriptionId = null;
        string? subscriptionKey = null;

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        if (!string.IsNullOrEmpty(refs.StripeSubscriptionId))
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT s.id, s.key, c.id, c.key
                FROM subscrio.subscriptions s
                JOIN subscrio.customers c ON c.id = s.customer_id
                WHERE s.stripe_subscription_id = @stripeSubId
                LIMIT 1
                """,
                conn);
            cmd.Parameters.AddWithValue("stripeSubId", refs.StripeSubscriptionId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                subscriptionId = reader.GetInt64(0);
                subscriptionKey = reader.GetString(1);
                customerId = reader.GetInt64(2);
                customerKey = reader.GetString(3);
            }
        }

        if (!string.IsNullOrEmpty(refs.StripeCustomerId) && customerId == null)
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT id, key
                FROM subscrio.customers
                WHERE external_billing_id = @billingId
                LIMIT 1
                """,
                conn);
            cmd.Parameters.AddWithValue("billingId", refs.StripeCustomerId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                customerId = reader.GetInt64(0);
                customerKey = reader.GetString(1);
            }
        }
        else if (customerId != null && customerKey == null)
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT key FROM subscrio.customers WHERE id = @id LIMIT 1
                """,
                conn);
            cmd.Parameters.AddWithValue("id", customerId.Value);
            var key = await cmd.ExecuteScalarAsync(cancellationToken);
            if (key is string s)
                customerKey = s;
        }

        return new StripeEntityAssociation
        {
            CustomerId = customerId,
            CustomerKey = customerKey,
            SubscriptionId = subscriptionId,
            SubscriptionKey = subscriptionKey
        };
    }
}
