using Npgsql;
using static Subscrio.AuditLog.Schema.Migrations;

namespace Subscrio.AuditLog.Schema;

public sealed class SchemaInstaller
{
    private readonly NpgsqlDataSource _dataSource;

    public SchemaInstaller(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await ExecuteAsync(connection, transaction, "CREATE SCHEMA IF NOT EXISTS subscrio", cancellationToken);

            await ExecuteAsync(connection, transaction, """
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
                """, cancellationToken);

            await ExecuteAsync(connection, transaction, """
                CREATE INDEX IF NOT EXISTS idx_transaction_logs_created_at
                  ON subscrio.transaction_logs (created_at DESC)
                """, cancellationToken);

            await ExecuteAsync(connection, transaction, """
                CREATE INDEX IF NOT EXISTS idx_transaction_logs_customer_key_created_at
                  ON subscrio.transaction_logs (customer_key, created_at DESC)
                """, cancellationToken);

            await ExecuteAsync(connection, transaction, """
                CREATE INDEX IF NOT EXISTS idx_transaction_logs_subscription_key_created_at
                  ON subscrio.transaction_logs (subscription_key, created_at DESC)
                """, cancellationToken);

            await ExecuteAsync(connection, transaction, """
                CREATE INDEX IF NOT EXISTS idx_transaction_logs_customer_id
                  ON subscrio.transaction_logs (customer_id)
                """, cancellationToken);

            await ExecuteAsync(connection, transaction, """
                CREATE INDEX IF NOT EXISTS idx_transaction_logs_subscription_id
                  ON subscrio.transaction_logs (subscription_id)
                """, cancellationToken);

            await ExecuteAsync(connection, transaction, """
                CREATE INDEX IF NOT EXISTS idx_transaction_logs_entity_type_action
                  ON subscrio.transaction_logs (entity_type, action)
                """, cancellationToken);

            await ExecuteAsync(connection, transaction, """
                CREATE INDEX IF NOT EXISTS idx_transaction_logs_source
                  ON subscrio.transaction_logs (source)
                """, cancellationToken);

            await ExecuteAsync(connection, transaction, """
                CREATE INDEX IF NOT EXISTS idx_transaction_logs_stripe_event_id
                  ON subscrio.transaction_logs (stripe_event_id)
                  WHERE stripe_event_id IS NOT NULL
                """, cancellationToken);

            await UpsertVersionAsync(connection, transaction, AuditLogSchemaVersion, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<string?> VerifyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand(
                "SELECT config_value FROM subscrio.system_config WHERE config_key = @key LIMIT 1",
                connection);
            cmd.Parameters.AddWithValue("key", AuditLogSchemaVersionKey);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result as string;
        }
        catch
        {
            return null;
        }
    }

    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var current = await VerifyAsync(cancellationToken);
        if (current == null)
        {
            await InstallAsync(cancellationToken);
            return 1;
        }

        var applied = 0;
        foreach (var migration in AuditLogMigrations)
        {
            if (CompareVersions(current, migration.Version) >= 0)
                continue;

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await migration.Up(connection, transaction);
                await UpsertVersionAsync(connection, transaction, migration.Version, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                current = migration.Version;
                applied++;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        return applied;
    }

    private static async Task UpsertVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string version,
        CancellationToken cancellationToken)
    {
        await using (var select = new NpgsqlCommand(
            "SELECT id FROM subscrio.system_config WHERE config_key = @key LIMIT 1",
            connection, transaction))
        {
            select.Parameters.AddWithValue("key", AuditLogSchemaVersionKey);
            var existing = await select.ExecuteScalarAsync(cancellationToken);

            if (existing != null)
            {
                await using var update = new NpgsqlCommand("""
                    UPDATE subscrio.system_config
                    SET config_value = @value, updated_at = NOW()
                    WHERE config_key = @key
                    """, connection, transaction);
                update.Parameters.AddWithValue("value", version);
                update.Parameters.AddWithValue("key", AuditLogSchemaVersionKey);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO subscrio.system_config (config_key, config_value, encrypted, created_at, updated_at)
                    VALUES (@key, @value, FALSE, NOW(), NOW())
                    """, connection, transaction);
                insert.Parameters.AddWithValue("key", AuditLogSchemaVersionKey);
                insert.Parameters.AddWithValue("value", version);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
