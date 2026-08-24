using Subscrio.Core;

namespace Subscrio.AuditLog;

public static class AuditLogExtensions
{
    /// <summary>
    /// Create an audit-log extension bound to a Subscrio instance.
    /// Registers handlers for all *.after HookEvents. Dispose to unsubscribe.
    /// </summary>
    public static AuditLog UseAuditLog(this Subscrio.Core.Subscrio subscrio, AuditLogOptions options) =>
        new(subscrio, options);
}
