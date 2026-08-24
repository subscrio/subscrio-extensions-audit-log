namespace Subscrio.AuditLog.DTOs;

public sealed class TransactionLogDto
{
    public long Id { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string? EntityKey { get; init; }
    public string? CustomerKey { get; init; }
    public string? SubscriptionKey { get; init; }
    public long? CustomerId { get; init; }
    public long? SubscriptionId { get; init; }
    public string? Actor { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? StripeEventId { get; init; }
    public string? StripeEventType { get; init; }
    public Dictionary<string, object?>? EventPayload { get; init; }
    public Dictionary<string, object?>? PreValue { get; init; }
    public Dictionary<string, object?>? PostValue { get; init; }
    public Dictionary<string, object?>? Metadata { get; init; }
}
