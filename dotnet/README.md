# Subscrio.AuditLog

First-party audit log extension for [Subscrio.Core](https://github.com/subscrio/subscrio-dotnet). Registers after-hooks and stores rows in Postgres for reporting.

## Install

```bash
dotnet add package Subscrio.AuditLog
```

Depends on `Subscrio.Core` and PostgreSQL (Npgsql).

## Usage

```csharp
using Subscrio.Core;
using Subscrio.Core.Config;
using Subscrio.AuditLog;
using Subscrio.AuditLog.DTOs;

var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")!;
var subscrio = new Subscrio(new SubscrioConfig
{
    Database = new DatabaseConfig { ConnectionString = connectionString }
});
await subscrio.InstallSchemaAsync();

await using var audit = subscrio.UseAuditLog(new AuditLogOptions
{
    ConnectionString = connectionString
});
await audit.InstallSchemaAsync();

var page = await audit.ListAsync(new TransactionLogFilters
{
    CustomerKey = "acme",
    Limit = 50
});
var row = page.Data.Count > 0 ? await audit.GetAsync(page.Data[0].Id) : null;

// Dispose unsubscribes all hooks and closes the Npgsql data source
```

## Schema

Owned by this extension (not core). Created only when you call `InstallSchemaAsync()`.

- Table: `subscrio.transaction_logs`
- Version key in `subscrio.system_config`: `audit_log_schema_version` (current: `1.0.0`)
- `VerifySchemaAsync()` → version string or `null`
- `MigrateAsync()` applies future extension-only migrations

Nullable FKs (`customer_id`, `subscription_id`) use `ON DELETE SET NULL` so history survives entity deletes. Business keys (`customer_key`, `subscription_key`, `entity_key`) are always stored for reporting.

## Hooks

Registers all `*.after` hooks via `Subscrio.Hooks.On*After`:

| Event family | entity_type | action |
| --- | --- | --- |
| `customer.*.after` | `customer` | create / update / archive / unarchive / delete |
| `subscription.*.after` (lifecycle) | `subscription` | create / update / archive / unarchive / delete |
| feature override / clear | `subscription` | `feature_override` / `clear_overrides` |
| `stripe.received.after` | `stripe_event` | `stripe_event` |

For Stripe events, the extension extracts Stripe customer/subscription IDs from the payload and looks up matching Subscrio rows (`customers.external_billing_id`, `subscriptions.stripe_subscription_id`) so `customer_id` / `subscription_id` / keys are filled when resolvable. Unresolved refs stay null.

Summary format: `customer.updated: acme`.

## After-hook failure semantics

Audit writes run on **after** hooks, so the Subscrio mutation is already committed when the insert runs.

- If the audit insert throws, the error **propagates** and the public API call fails.
- The underlying customer/subscription change **remains** in the database.
- Call `DisposeAsync()` to unsubscribe; further mutations will not write audit rows.

## Query

```csharp
await audit.ListAsync(new TransactionLogFilters
{
    CustomerKey = "acme",
    SubscriptionKey = "sub-1",
    EntityType = "customer",
    Source = "api",
    Action = "update",
    StripeEventId = "evt_...",
    StripeEventType = "customer.subscription.created",
    Search = "acme",
    StartDate = DateTime.Parse("2026-01-01T00:00:00.000Z").ToUniversalTime(),
    EndDate = DateTime.Parse("2026-12-31T23:59:59.999Z").ToUniversalTime(),
    SortBy = "createdAt",
    SortOrder = "desc",
    Limit = 50,
    Offset = 0,
});
// → TransactionLogPage { Data, Total }
```

## Development

From this directory (`dotnet/`):

```bash
dotnet build
dotnet test
```

Local builds resolve `Subscrio.Core` from the hub checkout at `core/dotnet`. Check out the [hub workspace layout](https://github.com/subscrio/subscrio/blob/main/repos.md) first. Core tests in [subscrio-dotnet](https://github.com/subscrio/subscrio-dotnet) do not run this suite.

Tests create a fresh Postgres database. Set `TEST_DATABASE_URL`, copy `tests/appsettings.example.json` to `tests/appsettings.json`, or use default localhost credentials (`postgres` / `postgres`).
