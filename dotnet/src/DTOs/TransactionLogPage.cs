namespace Subscrio.AuditLog.DTOs;

public sealed class TransactionLogPage
{
    public required IReadOnlyList<TransactionLogDto> Data { get; init; }
    public required long Total { get; init; }
}
