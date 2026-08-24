# subscrio-audit-log

First-party audit log extension for [subscrio](https://github.com/subscrio/subscrio-typescript). Registers after-hooks and stores rows in Postgres for reporting.

## Install

```bash
npm install subscrio-audit-log
```

Peer dependency: `subscrio`.

## Usage

```typescript
import { Subscrio } from 'subscrio';
import { createAuditLog } from 'subscrio-audit-log';

const connectionString = process.env.DATABASE_URL!;
const subscrio = new Subscrio({ database: { connectionString } });
await subscrio.installSchema();

const audit = createAuditLog(subscrio, { database: { connectionString } });
await audit.installSchema();

const { data, total } = await audit.list({ customerKey: 'acme', limit: 50 });
const row = await audit.get(data[0]?.id);

await audit.dispose(); // unsubscribes all hooks and closes the pool
```

## Schema

Owned by this extension (not core). Created only when you call `installSchema()`.

- Table: `subscrio.transaction_logs`
- Version key in `subscrio.system_config`: `audit_log_schema_version` (current: `1.0.0`)
- `verifySchema()` → version string or `null`
- `migrate()` applies future extension-only migrations

Nullable FKs (`customer_id`, `subscription_id`) use `ON DELETE SET NULL` so history survives entity deletes. Business keys (`customer_key`, `subscription_key`, `entity_key`) are always stored for reporting.

## Hooks

Registers all `*.after` `HookEvents` from Subscrio:

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
- Call `dispose()` to unsubscribe; further mutations will not write audit rows.

## Query

```typescript
await audit.list({
  customerKey: 'acme',
  subscriptionKey: 'sub-1',
  entityType: 'customer',
  source: 'api',
  action: 'update',
  stripeEventId: 'evt_...',
  stripeEventType: 'customer.subscription.created',
  search: 'acme',
  startDate: '2026-01-01T00:00:00.000Z',
  endDate: '2026-12-31T23:59:59.999Z',
  sortBy: 'createdAt',
  sortOrder: 'desc',
  limit: 50,
  offset: 0,
});
// → { data: TransactionLogDto[], total: number }
```

## Development

From this directory (`typescript/`):

```bash
npm install
npm run build
npm test
```

`npm install` resolves the `subscrio` peer from the hub checkout at `core/typescript`. Check out the [hub workspace layout](https://github.com/subscrio/subscrio/blob/main/repos.md) first. Core tests in [subscrio-typescript](https://github.com/subscrio/subscrio-typescript) do not run this suite.

Tests create a fresh Postgres database. Set `TEST_DATABASE_URL`, add a `typescript/.env`, or use default localhost credentials (`postgresql://postgres:postgres@localhost:5432/postgres`). In the hub workspace, `core/typescript/.env` is also loaded when those are unset.
