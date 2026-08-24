namespace Subscrio.AuditLog.DTOs;

public sealed class TransactionLogFilters
{
    public string? CustomerKey { get; init; }
    public string? SubscriptionKey { get; init; }
    public string? EntityType { get; init; }
    public string? Source { get; init; }
    public string? Action { get; init; }
    public string? StripeEventId { get; init; }
    public string? StripeEventType { get; init; }
    public string? Search { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public string SortBy { get; init; } = "createdAt";
    public string SortOrder { get; init; } = "desc";
    public int Limit { get; init; } = 50;
    public int Offset { get; init; } = 0;
}
