using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Subscrio.AuditLog.DTOs;

namespace Subscrio.AuditLog.Repository;

public sealed class InsertTransactionLogInput
{
    public required string Source { get; init; }
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public string? EntityKey { get; init; }
    public string? CustomerKey { get; init; }
    public string? SubscriptionKey { get; init; }
    public long? CustomerId { get; init; }
    public long? SubscriptionId { get; init; }
    public string? Actor { get; init; }
    public required string Summary { get; init; }
    public string? StripeEventId { get; init; }
    public string? StripeEventType { get; init; }
    public Dictionary<string, object?>? EventPayload { get; init; }
    public Dictionary<string, object?>? PreValue { get; init; }
    public Dictionary<string, object?>? PostValue { get; init; }
    public Dictionary<string, object?>? Metadata { get; init; }
}

public sealed class PostgresAuditLogRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.Ordinal)
    {
        ["createdAt"] = "created_at",
        ["id"] = "id",
        ["source"] = "source",
        ["action"] = "action",
        ["entityType"] = "entity_type",
        ["summary"] = "summary",
    };

    private readonly NpgsqlDataSource _dataSource;

    public PostgresAuditLogRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task InsertAsync(InsertTransactionLogInput input, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO subscrio.transaction_logs (
              source, action, entity_type, entity_key, customer_key, subscription_key,
              customer_id, subscription_id, actor, summary,
              stripe_event_id, stripe_event_type, event_payload, pre_value, post_value, metadata
            ) VALUES (
              @source, @action, @entityType, @entityKey, @customerKey, @subscriptionKey,
              @customerId, @subscriptionId, @actor, @summary,
              @stripeEventId, @stripeEventType, @eventPayload, @preValue, @postValue, @metadata
            )
            """, connection);

        cmd.Parameters.AddWithValue("source", input.Source);
        cmd.Parameters.AddWithValue("action", input.Action);
        cmd.Parameters.AddWithValue("entityType", input.EntityType);
        cmd.Parameters.AddWithValue("entityKey", (object?)input.EntityKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("customerKey", (object?)input.CustomerKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("subscriptionKey", (object?)input.SubscriptionKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("customerId", (object?)input.CustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("subscriptionId", (object?)input.SubscriptionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actor", (object?)input.Actor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("summary", input.Summary);
        cmd.Parameters.AddWithValue("stripeEventId", (object?)input.StripeEventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("stripeEventType", (object?)input.StripeEventType ?? DBNull.Value);
        AddJsonb(cmd, "eventPayload", input.EventPayload);
        AddJsonb(cmd, "preValue", input.PreValue);
        AddJsonb(cmd, "postValue", input.PostValue);
        AddJsonb(cmd, "metadata", input.Metadata);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TransactionLogDto?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT * FROM subscrio.transaction_logs WHERE id = @id",
            connection);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return MapRow(reader);
    }

    public async Task<TransactionLogPage> ListAsync(
        TransactionLogFilters? filters = null,
        CancellationToken cancellationToken = default)
    {
        var f = NormalizeFilters(filters);

        var where = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        void Add(string clause, string name, object value)
        {
            where.Add(clause);
            parameters.Add(new NpgsqlParameter(name, value));
        }

        if (!string.IsNullOrEmpty(f.CustomerKey))
            Add("customer_key = @customerKey", "customerKey", f.CustomerKey);
        if (!string.IsNullOrEmpty(f.SubscriptionKey))
            Add("subscription_key = @subscriptionKey", "subscriptionKey", f.SubscriptionKey);
        if (!string.IsNullOrEmpty(f.EntityType))
            Add("entity_type = @entityType", "entityType", f.EntityType);
        if (!string.IsNullOrEmpty(f.Source))
            Add("source = @source", "source", f.Source);
        if (!string.IsNullOrEmpty(f.Action))
            Add("action = @action", "action", f.Action);
        if (!string.IsNullOrEmpty(f.StripeEventId))
            Add("stripe_event_id = @stripeEventId", "stripeEventId", f.StripeEventId);
        if (!string.IsNullOrEmpty(f.StripeEventType))
            Add("stripe_event_type = @stripeEventType", "stripeEventType", f.StripeEventType);
        if (f.StartDate.HasValue)
            Add("created_at >= @startDate", "startDate", f.StartDate.Value.ToUniversalTime());
        if (f.EndDate.HasValue)
            Add("created_at <= @endDate", "endDate", f.EndDate.Value.ToUniversalTime());
        if (!string.IsNullOrEmpty(f.Search))
        {
            where.Add(
                "(summary ILIKE @search OR entity_key ILIKE @search OR customer_key ILIKE @search OR subscription_key ILIKE @search OR actor ILIKE @search)");
            parameters.Add(new NpgsqlParameter("search", $"%{f.Search}%"));
        }

        var whereSql = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";
        var sortCol = SortColumns.GetValueOrDefault(f.SortBy, "created_at");
        var sortOrder = string.Equals(f.SortOrder, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        long total;
        await using (var countCmd = new NpgsqlCommand(
            $"SELECT COUNT(*)::bigint FROM subscrio.transaction_logs {whereSql}",
            connection))
        {
            foreach (var p in parameters)
                countCmd.Parameters.Add(CloneParameter(p));
            total = (long)(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }

        var data = new List<TransactionLogDto>();
        await using (var dataCmd = new NpgsqlCommand($"""
            SELECT * FROM subscrio.transaction_logs
            {whereSql}
            ORDER BY {sortCol} {sortOrder}, id {sortOrder}
            LIMIT @limit OFFSET @offset
            """, connection))
        {
            foreach (var p in parameters)
                dataCmd.Parameters.Add(CloneParameter(p));
            dataCmd.Parameters.AddWithValue("limit", f.Limit);
            dataCmd.Parameters.AddWithValue("offset", f.Offset);

            await using var reader = await dataCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                data.Add(MapRow(reader));
        }

        return new TransactionLogPage { Data = data, Total = total };
    }

    private static TransactionLogFilters NormalizeFilters(TransactionLogFilters? filters)
    {
        filters ??= new TransactionLogFilters();

        var sortBy = string.IsNullOrWhiteSpace(filters.SortBy) ? "createdAt" : filters.SortBy;
        if (!SortColumns.ContainsKey(sortBy))
            throw new ArgumentException($"Invalid sortBy: {sortBy}");

        var sortOrder = string.IsNullOrWhiteSpace(filters.SortOrder) ? "desc" : filters.SortOrder.ToLowerInvariant();
        if (sortOrder is not ("asc" or "desc"))
            throw new ArgumentException($"Invalid sortOrder: {filters.SortOrder}");

        var limit = filters.Limit <= 0 ? 50 : filters.Limit;
        if (limit is < 1 or > 500)
            throw new ArgumentException("limit must be between 1 and 500");

        var offset = filters.Offset < 0
            ? throw new ArgumentException("offset must be >= 0")
            : filters.Offset;

        return new TransactionLogFilters
        {
            CustomerKey = filters.CustomerKey,
            SubscriptionKey = filters.SubscriptionKey,
            EntityType = filters.EntityType,
            Source = filters.Source,
            Action = filters.Action,
            StripeEventId = filters.StripeEventId,
            StripeEventType = filters.StripeEventType,
            Search = filters.Search,
            StartDate = filters.StartDate,
            EndDate = filters.EndDate,
            SortBy = sortBy,
            SortOrder = sortOrder,
            Limit = limit,
            Offset = offset
        };
    }

    private static void AddJsonb(NpgsqlCommand cmd, string name, Dictionary<string, object?>? value)
    {
        var param = cmd.Parameters.Add(name, NpgsqlDbType.Jsonb);
        param.Value = value == null
            ? DBNull.Value
            : JsonSerializer.Serialize(value, JsonOptions);
    }

    private static NpgsqlParameter CloneParameter(NpgsqlParameter source) =>
        new(source.ParameterName, source.Value ?? DBNull.Value);

    private static TransactionLogDto MapRow(NpgsqlDataReader reader)
    {
        return new TransactionLogDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            CreatedAt = ReadTimestamp(reader, "created_at").ToUniversalTime().ToString("O"),
            Source = reader.GetString(reader.GetOrdinal("source")),
            Action = reader.GetString(reader.GetOrdinal("action")),
            EntityType = reader.GetString(reader.GetOrdinal("entity_type")),
            EntityKey = ReadNullableString(reader, "entity_key"),
            CustomerKey = ReadNullableString(reader, "customer_key"),
            SubscriptionKey = ReadNullableString(reader, "subscription_key"),
            CustomerId = ReadNullableLong(reader, "customer_id"),
            SubscriptionId = ReadNullableLong(reader, "subscription_id"),
            Actor = ReadNullableString(reader, "actor"),
            Summary = reader.GetString(reader.GetOrdinal("summary")),
            StripeEventId = ReadNullableString(reader, "stripe_event_id"),
            StripeEventType = ReadNullableString(reader, "stripe_event_type"),
            EventPayload = ReadJsonObject(reader, "event_payload"),
            PreValue = ReadJsonObject(reader, "pre_value"),
            PostValue = ReadJsonObject(reader, "post_value"),
            Metadata = ReadJsonObject(reader, "metadata"),
        };
    }

    private static DateTime ReadTimestamp(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.GetFieldValue<DateTime>(ordinal);
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long? ReadNullableLong(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static Dictionary<string, object?>? ReadJsonObject(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
            return null;

        var raw = reader.GetValue(ordinal);
        string json = raw switch
        {
            string s => s,
            JsonDocument doc => doc.RootElement.GetRawText(),
            JsonElement el => el.GetRawText(),
            _ => raw.ToString() ?? "null"
        };

        if (string.IsNullOrWhiteSpace(json) || json == "null")
            return null;

        using var parsed = JsonDocument.Parse(json);
        return JsonElementToDictionary(parsed.RootElement);
    }

    private static Dictionary<string, object?>? JsonElementToDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
            dict[prop.Name] = JsonElementToObject(prop.Value);
        return dict;
    }

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonElementToDictionary(element),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };
}
