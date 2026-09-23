using Npgsql;
using Subscrio.AuditLog.DTOs;
using Subscrio.AuditLog.Mapping;
using Subscrio.AuditLog.Repository;
using Subscrio.AuditLog.Schema;
using Subscrio.Core;
using Subscrio.Core.Application.Hooks;

namespace Subscrio.AuditLog;

/// <summary>
/// Audit-log extension bound to a Subscrio instance.
/// Registers handlers for all *.after HookEvents.
/// </summary>
public sealed class AuditLog : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaInstaller _installer;
    private readonly PostgresAuditLogRepository _repository;
    private readonly List<Action> _unsubscribers = new();
    private bool _disposed;

    internal AuditLog(Subscrio.Core.Subscrio subscrio, AuditLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(subscrio);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("ConnectionString is required", nameof(options));

        _dataSource = NpgsqlDataSource.Create(options.ConnectionString);
        _installer = new SchemaInstaller(_dataSource);
        _repository = new PostgresAuditLogRepository(_dataSource);

        Task WriteCustomer(CustomerMutationHookEvent evt, CancellationToken ct) =>
            _repository.InsertAsync(MapHookEvent.MapCustomerAfterEvent(evt), ct);

        Task WriteSubscription(SubscriptionMutationHookEvent evt, CancellationToken ct) =>
            _repository.InsertAsync(MapHookEvent.MapSubscriptionAfterEvent(evt), ct);

        async Task WriteStripe(StripeReceivedHookEvent evt, CancellationToken ct)
        {
            var refs = ExtractStripeEntityRefs.FromEvent(evt.Data);
            refs = new StripeEntityRefs
            {
                StripeCustomerId = evt.StripeCustomerId ?? refs.StripeCustomerId,
                StripeSubscriptionId = evt.StripeSubscriptionId ?? refs.StripeSubscriptionId
            };
            var association = await ResolveStripeEntities.ResolveAsync(_dataSource, refs, ct);
            await _repository.InsertAsync(
                MapHookEvent.MapStripeReceivedAfterEvent(evt, association, refs),
                ct);
        }

        async Task WriteAccounting(AccountingMutationHookEvent evt, CancellationToken ct)
        {
            var subscriptionKey = evt.Input["subscriptionKey"]?.GetValue<string>();
            var subscription = subscriptionKey == null ? null : await subscrio.Subscriptions.GetSubscriptionAsync(subscriptionKey);
            var customerKey = evt.Input["customerKey"]?.GetValue<string>() ?? subscription?.CustomerKey;
            await _repository.InsertAsync(new()
            {
                Source = evt.Source,
                Action = evt.Type.Split('.')[1],
                EntityType = evt.Type.Split('.')[0],
                EntityKey = evt.Input["idempotencyKey"]?.GetValue<string>() ?? subscriptionKey,
                CustomerKey = customerKey,
                SubscriptionKey = subscriptionKey,
                Summary = evt.Type,
                PostValue = evt.Result.HasValue
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(evt.Result.Value.GetRawText())
                    : null,
                Metadata = new() { ["hookType"] = evt.Type, ["input"] = evt.Input }
            }, ct);
        }
        _unsubscribers.Add(subscrio.Hooks.OnSubscriptionAddonAttachedAfter(WriteAccounting));
        _unsubscribers.Add(subscrio.Hooks.OnSubscriptionAddonDetachedAfter(WriteAccounting));
        _unsubscribers.Add(subscrio.Hooks.OnUsageReportedAfter(WriteAccounting));
        _unsubscribers.Add(subscrio.Hooks.OnCreditConsumedAfter(WriteAccounting));
        _unsubscribers.Add(subscrio.Hooks.OnCreditGrantedAfter(WriteAccounting));
        _unsubscribers.Add(subscrio.Hooks.OnCreditAdjustedAfter(WriteAccounting));
        _unsubscribers.Add(subscrio.Hooks.OnCustomerCreatedAfter(WriteCustomer));
        _unsubscribers.Add(subscrio.Hooks.OnCustomerUpdatedAfter(WriteCustomer));
        _unsubscribers.Add(subscrio.Hooks.OnCustomerArchivedAfter(WriteCustomer));
        _unsubscribers.Add(subscrio.Hooks.OnCustomerUnarchivedAfter(WriteCustomer));
        _unsubscribers.Add(subscrio.Hooks.OnCustomerDeletedAfter(WriteCustomer));
        _unsubscribers.Add(subscrio.Hooks.OnSubscriptionCreatedAfter(WriteSubscription));
        _unsubscribers.Add(subscrio.Hooks.OnSubscriptionUpdatedAfter(WriteSubscription));
        _unsubscribers.Add(subscrio.Hooks.OnSubscriptionArchivedAfter(WriteSubscription));
        _unsubscribers.Add(subscrio.Hooks.OnSubscriptionUnarchivedAfter(WriteSubscription));
        _unsubscribers.Add(subscrio.Hooks.OnSubscriptionDeletedAfter(WriteSubscription));
        _unsubscribers.Add(subscrio.Hooks.OnSubscriptionFeatureOverrideAddedAfter(WriteSubscription));
        _unsubscribers.Add(subscrio.Hooks.OnSubscriptionFeatureOverrideRemovedAfter(WriteSubscription));
        _unsubscribers.Add(subscrio.Hooks.OnSubscriptionTemporaryOverridesClearedAfter(WriteSubscription));
        _unsubscribers.Add(subscrio.Hooks.OnStripeReceivedAfter(WriteStripe));
    }

    public Task InstallSchemaAsync(CancellationToken cancellationToken = default) =>
        _installer.InstallAsync(cancellationToken);

    public Task<string?> VerifySchemaAsync(CancellationToken cancellationToken = default) =>
        _installer.VerifyAsync(cancellationToken);

    public Task<int> MigrateAsync(CancellationToken cancellationToken = default) =>
        _installer.MigrateAsync(cancellationToken);

    public Task<TransactionLogPage> ListAsync(
        TransactionLogFilters? filters = null,
        CancellationToken cancellationToken = default) =>
        _repository.ListAsync(filters, cancellationToken);

    public Task<TransactionLogDto?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        _repository.GetAsync(id, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        foreach (var off in _unsubscribers)
            off();
        _unsubscribers.Clear();
        _dataSource.Dispose();
        return ValueTask.CompletedTask;
    }
}
