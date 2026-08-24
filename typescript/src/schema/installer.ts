import type { Pool, PoolClient } from 'pg';
import {
  AUDIT_LOG_MIGRATIONS,
  AUDIT_LOG_SCHEMA_VERSION,
  AUDIT_LOG_SCHEMA_VERSION_KEY,
  compareVersions,
} from './migrations.js';

export class AuditLogSchemaInstaller {
  constructor(private readonly pool: Pool) {}

  async install(): Promise<void> {
    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');
      await client.query(`CREATE SCHEMA IF NOT EXISTS subscrio`);

      await client.query(`
        CREATE TABLE IF NOT EXISTS subscrio.transaction_logs (
          id BIGSERIAL PRIMARY KEY,
          created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
          source TEXT NOT NULL,
          action TEXT NOT NULL,
          entity_type TEXT NOT NULL,
          entity_key TEXT,
          customer_key TEXT,
          subscription_key TEXT,
          customer_id BIGINT NULL REFERENCES subscrio.customers(id) ON DELETE SET NULL,
          subscription_id BIGINT NULL REFERENCES subscrio.subscriptions(id) ON DELETE SET NULL,
          actor TEXT,
          summary TEXT NOT NULL,
          stripe_event_id TEXT,
          stripe_event_type TEXT,
          event_payload JSONB,
          pre_value JSONB,
          post_value JSONB,
          metadata JSONB
        )
      `);

      await client.query(`
        CREATE INDEX IF NOT EXISTS idx_transaction_logs_created_at
          ON subscrio.transaction_logs (created_at DESC)
      `);
      await client.query(`
        CREATE INDEX IF NOT EXISTS idx_transaction_logs_customer_key_created_at
          ON subscrio.transaction_logs (customer_key, created_at DESC)
      `);
      await client.query(`
        CREATE INDEX IF NOT EXISTS idx_transaction_logs_subscription_key_created_at
          ON subscrio.transaction_logs (subscription_key, created_at DESC)
      `);
      await client.query(`
        CREATE INDEX IF NOT EXISTS idx_transaction_logs_customer_id
          ON subscrio.transaction_logs (customer_id)
      `);
      await client.query(`
        CREATE INDEX IF NOT EXISTS idx_transaction_logs_subscription_id
          ON subscrio.transaction_logs (subscription_id)
      `);
      await client.query(`
        CREATE INDEX IF NOT EXISTS idx_transaction_logs_entity_type_action
          ON subscrio.transaction_logs (entity_type, action)
      `);
      await client.query(`
        CREATE INDEX IF NOT EXISTS idx_transaction_logs_source
          ON subscrio.transaction_logs (source)
      `);
      await client.query(`
        CREATE INDEX IF NOT EXISTS idx_transaction_logs_stripe_event_id
          ON subscrio.transaction_logs (stripe_event_id)
          WHERE stripe_event_id IS NOT NULL
      `);

      await this.upsertVersion(client, AUDIT_LOG_SCHEMA_VERSION);
      await client.query('COMMIT');
    } catch (err) {
      await client.query('ROLLBACK');
      throw err;
    } finally {
      client.release();
    }
  }

  async verify(): Promise<string | null> {
    try {
      const result = await this.pool.query<{ config_value: string }>(
        `SELECT config_value FROM subscrio.system_config WHERE config_key = $1 LIMIT 1`,
        [AUDIT_LOG_SCHEMA_VERSION_KEY]
      );
      return result.rows[0]?.config_value ?? null;
    } catch {
      return null;
    }
  }

  async migrate(): Promise<number> {
    let current = await this.verify();
    if (!current) {
      // Not installed — install creates 1.0.0
      await this.install();
      return 1;
    }

    let applied = 0;
    for (const migration of AUDIT_LOG_MIGRATIONS) {
      if (compareVersions(current, migration.version) < 0) {
        const client = await this.pool.connect();
        try {
          await client.query('BEGIN');
          await migration.up(client);
          await this.upsertVersion(client, migration.version);
          await client.query('COMMIT');
          current = migration.version;
          applied++;
        } catch (err) {
          await client.query('ROLLBACK');
          throw err;
        } finally {
          client.release();
        }
      }
    }
    return applied;
  }

  private async upsertVersion(client: PoolClient, version: string): Promise<void> {
    const existing = await client.query(
      `SELECT id FROM subscrio.system_config WHERE config_key = $1 LIMIT 1`,
      [AUDIT_LOG_SCHEMA_VERSION_KEY]
    );

    if (existing.rows.length > 0) {
      await client.query(
        `UPDATE subscrio.system_config
         SET config_value = $1, updated_at = NOW()
         WHERE config_key = $2`,
        [version, AUDIT_LOG_SCHEMA_VERSION_KEY]
      );
    } else {
      await client.query(
        `INSERT INTO subscrio.system_config (config_key, config_value, encrypted, created_at, updated_at)
         VALUES ($1, $2, FALSE, NOW(), NOW())`,
        [AUDIT_LOG_SCHEMA_VERSION_KEY, version]
      );
    }
  }
}
