namespace Subscrio.AuditLog;

/// <summary>
/// Configuration for the audit-log extension.
/// </summary>
public sealed class AuditLogOptions
{
    /// <summary>
    /// PostgreSQL connection string (typically the same database as Subscrio.Core).
    /// </summary>
    public required string ConnectionString { get; init; }
}
