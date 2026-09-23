using FluentAssertions;
using Npgsql;
using Stripe;
using Subscrio.AuditLog.DTOs;
using Subscrio.AuditLog.Mapping;
using Subscrio.AuditLog.Schema;
using Subscrio.AuditLog.Tests.Setup;
using Subscrio.Core.Application.DTOs;
using Subscrio.Core.Application.Hooks;
using Subscrio.Core.Domain.ValueObjects;
using Xunit;

namespace Subscrio.AuditLog.Tests.E2E;

public class AuditLogTests : IAsyncLifetime
{
    private AuditLogTestContext _ctx = null!;
    private AuditLog _audit = null!;

    public async Task InitializeAsync()
    {
        _ctx = await TestDatabase.SetupAsync();
        _audit = _ctx.Subscrio.UseAuditLog(new AuditLogOptions
        {
            ConnectionString = _ctx.ConnectionString
        });
    }

    public async Task DisposeAsync()
    {
        if (_audit != null)
            await _audit.DisposeAsync();
        if (_ctx != null)
            await TestDatabase.TeardownAsync(_ctx.DbName, _ctx.Subscrio);
    }

    private static string Unique(string prefix) =>
        $"{prefix}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{Guid.NewGuid().ToString("N")[..6]}";

    private async Task<(ProductDto Product, PlanDto Plan, BillingCycleDto Cycle, CustomerDto Customer)> CreateSubscriptionFixtureAsync()
    {
        var product = await _ctx.Subscrio.Products.CreateProductAsync(new CreateProductDto(
            Key: Unique("prod"),
            DisplayName: "Audit Product"));
        var plan = await _ctx.Subscrio.Plans.CreatePlanAsync(new CreatePlanDto(
            ProductKey: product.Key,
            Key: Unique("plan"),
            DisplayName: "Audit Plan"));
        var cycle = await _ctx.Subscrio.BillingCycles.CreateBillingCycleAsync(new CreateBillingCycleDto(
            PlanKey: plan.Key,
            Key: Unique("cycle"),
            DisplayName: "Monthly",
            DurationUnit: "months",
            DurationValue: 1));
        var customer = await _ctx.Subscrio.Customers.CreateCustomerAsync(new CreateCustomerDto(
            Key: Unique("cust"),
            DisplayName: "Audit Customer"));
        return (product, plan, cycle, customer);
    }

    [Fact]
    public async Task InstallSchema_VerifySchema_IdempotentInstall()
    {
        (await _audit.VerifySchemaAsync()).Should().BeNull();

        await _audit.InstallSchemaAsync();
        (await _audit.VerifySchemaAsync()).Should().Be(Migrations.AuditLogSchemaVersion);

        await _audit.InstallSchemaAsync();
        (await _audit.VerifySchemaAsync()).Should().Be(Migrations.AuditLogSchemaVersion);

        var migrated = await _audit.MigrateAsync();
        migrated.Should().Be(0);
    }

    [Fact]
    public async Task CustomerCreateAndUpdate_WriteAuditRows()
    {
        await _audit.InstallSchemaAsync();

        var key = Unique("acme");
        await _ctx.Subscrio.Customers.CreateCustomerAsync(new CreateCustomerDto(
            Key: key,
            DisplayName: "Acme"));
        await _ctx.Subscrio.Customers.UpdateCustomerAsync(key, new UpdateCustomerDto(DisplayName: "Acme Corp"));

        var page = await _audit.ListAsync(new TransactionLogFilters
        {
            CustomerKey = key,
            SortOrder = "asc"
        });
        page.Total.Should().BeGreaterThanOrEqualTo(2);

        var createRow = page.Data.FirstOrDefault(r => r.Action == "create" && r.EntityType == "customer");
        var updateRow = page.Data.FirstOrDefault(r => r.Action == "update" && r.EntityType == "customer");

        createRow.Should().NotBeNull();
        createRow!.EntityKey.Should().Be(key);
        createRow.CustomerKey.Should().Be(key);
        createRow.CustomerId.Should().NotBeNull();
        createRow.Source.Should().Be("api");
        createRow.Summary.Should().Be($"customer.created: {key}");
        createRow.PostValue.Should().NotBeNull();
        createRow.PostValue!["key"]!.ToString().Should().Be(key);
        createRow.PostValue["displayName"]!.ToString().Should().Be("Acme");

        updateRow.Should().NotBeNull();
        updateRow!.Summary.Should().Be($"customer.updated: {key}");
        updateRow.PreValue!["displayName"]!.ToString().Should().Be("Acme");
        updateRow.PostValue!["displayName"]!.ToString().Should().Be("Acme Corp");

        var fetched = await _audit.GetAsync(createRow.Id);
        fetched!.Id.Should().Be(createRow.Id);
        fetched.CustomerKey.Should().Be(key);
    }

    [Fact]
    public async Task AddonAndUsageHooks_AuditCommittedOperationsWithAssociations()
    {
        await _audit.InstallSchemaAsync();
        var app = _ctx.Subscrio;
        var (product, plan, cycle, customer) = await CreateSubscriptionFixtureAsync();
        var featureKey = Unique("requests");
        await app.Features.CreateFeatureAsync(new(featureKey, "Requests", "metered", "20",
            MeteredConfig: new("monthly", "hard", "sum", "subscription")));
        await app.Products.AssociateFeatureAsync(product.Key, featureKey, new(AddonRule: "additive"));
        await app.Plans.SetFeatureValueAsync(plan.Key, featureKey, "20");
        var subscription = await app.Subscriptions.CreateSubscriptionAsync(new(Unique("sub"), customer.Key,
            cycle.Key, ActivationDate: DateTime.UtcNow));
        var addon = await app.Addons.CreateAddonAsync(new(Unique("extra"), product.Key, "Extra requests",
            FeatureValues: new() { [featureKey] = "5" }));
        await app.Subscriptions.AttachAddonAsync(subscription.Key, addon.Key, 2);
        await app.Subscriptions.DetachAddonAsync(subscription.Key, addon.Key);
        var options = new UsageReportOptions("usage-1", subscription.Key);
        await app.Metering.ReportUsageAsync(customer.Key, product.Key, featureKey, 2, options);
        await app.Metering.ReportUsageAsync(customer.Key, product.Key, featureKey, 2, options);
        var off = app.Hooks.OnUsageReportedBefore((_, _) => throw new InvalidOperationException("Veto usage"));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => app.Metering.ReportUsageAsync(
                customer.Key, product.Key, featureKey, 1, new("rejected", subscription.Key)));
        }
        finally { off(); }
        Assert.Equal(2, (await app.Metering.GetUsageAsync(customer.Key, product.Key, featureKey,
            new(SubscriptionKey: subscription.Key))).Consumed);
        var rows = (await _audit.ListAsync(new() { CustomerKey = customer.Key })).Data;
        foreach (var hook in new[] { HookEvents.SubscriptionAddonAttachedAfter, HookEvents.SubscriptionAddonDetachedAfter, HookEvents.UsageReportedAfter })
        {
            var row = Assert.Single(rows, r => r.Summary == hook);
            Assert.Equal(subscription.Key, row.SubscriptionKey);
            Assert.Equal("api", row.Source);
            Assert.NotNull(row.PostValue);
        }
        var attached = rows.Single(r => r.Summary == HookEvents.SubscriptionAddonAttachedAfter);
        var input = System.Text.Json.JsonSerializer.SerializeToElement(attached.Metadata!["input"]);
        Assert.Equal(addon.Key, input.GetProperty("addonKey").GetString());
        Assert.Equal(2, input.GetProperty("quantity").GetInt32());
        var expiresAt = DateTime.UtcNow.AddHours(1);
        await app.Subscriptions.AddFeatureOverrideAsync(subscription.Key, featureKey, "30", OverrideType.Timed, expiresAt);
        var overrideRow = Assert.Single((await _audit.ListAsync(new() { CustomerKey = customer.Key })).Data,
            r => r.Action == "feature_override");
        Assert.Equal(expiresAt, DateTime.Parse(overrideRow.Metadata!["expiresAt"]!.ToString()!).ToUniversalTime());
    }

    [Fact]
    public async Task CreditAdjustment_AuditsOnceWithReasonAndBalance()
    {
        await _audit.InstallSchemaAsync();
        var app = _ctx.Subscrio;
        var customerKey = Unique("adjust-customer");
        var currencyKey = Unique("adjust-currency");
        await app.Customers.CreateCustomerAsync(new(customerKey));
        await app.Credits.CreateCurrencyAsync(new(currencyKey, "Credits"));
        var input = new CreditAdjustInput(customerKey, currencyKey, 10, "Support credit", "adjust-1");
        await app.Credits.AdjustAsync(input);
        await app.Credits.AdjustAsync(input);
        var row = Assert.Single((await _audit.ListAsync(new() { CustomerKey = customerKey })).Data,
            r => r.Summary == "credit.adjusted.after");
        var metadata = System.Text.Json.JsonSerializer.SerializeToElement(row.Metadata!["input"]);
        Assert.Equal("Support credit", metadata.GetProperty("reason").GetString());
        var result = System.Text.Json.JsonSerializer.SerializeToElement(row.PostValue);
        Assert.Equal(currencyKey, result.GetProperty("currencyKey").GetString());
        Assert.Equal(10, result.GetProperty("available").GetInt64());
    }

    [Fact]
    public async Task SubscriptionCreateAndFeatureOverride_WriteRows()
    {
        await _audit.InstallSchemaAsync();
        var (product, _, cycle, customer) = await CreateSubscriptionFixtureAsync();

        var feature = await _ctx.Subscrio.Features.CreateFeatureAsync(new CreateFeatureDto(
            Key: Unique("feat"),
            DisplayName: "Feat",
            ValueType: "toggle",
            DefaultValue: "false"));
        await _ctx.Subscrio.Products.AssociateFeatureAsync(product.Key, feature.Key);

        var subKey = Unique("sub");
        await _ctx.Subscrio.Subscriptions.CreateSubscriptionAsync(new CreateSubscriptionDto(
            Key: subKey,
            CustomerKey: customer.Key,
            BillingCycleKey: cycle.Key));

        await _ctx.Subscrio.Subscriptions.AddFeatureOverrideAsync(
            subKey,
            feature.Key,
            "true",
            OverrideType.Permanent);

        var page = await _audit.ListAsync(new TransactionLogFilters { SubscriptionKey = subKey });
        var createRow = page.Data.FirstOrDefault(r => r.Action == "create" && r.EntityType == "subscription");
        var overrideRow = page.Data.FirstOrDefault(r => r.Action == "feature_override");

        createRow.Should().NotBeNull();
        createRow!.CustomerKey.Should().Be(customer.Key);
        createRow.SubscriptionId.Should().NotBeNull();
        createRow.CustomerId.Should().NotBeNull();
        createRow.Summary.Should().Be($"subscription.created: {subKey}");

        overrideRow.Should().NotBeNull();
        overrideRow!.Metadata.Should().NotBeNull();
        overrideRow.Metadata!["featureKey"]!.ToString().Should().Be(feature.Key);
        overrideRow.Metadata["value"]!.ToString().Should().Be("true");
    }

    [Fact]
    public async Task StripeReceivedAfter_WritesStripeEventRow()
    {
        await _audit.InstallSchemaAsync();

        var product = await _ctx.Subscrio.Products.CreateProductAsync(new CreateProductDto(
            Key: Unique("sprod"),
            DisplayName: "Stripe Product"));
        var plan = await _ctx.Subscrio.Plans.CreatePlanAsync(new CreatePlanDto(
            ProductKey: product.Key,
            Key: Unique("splan"),
            DisplayName: "Stripe Plan"));
        var priceId = Unique("price");
        await _ctx.Subscrio.BillingCycles.CreateBillingCycleAsync(new CreateBillingCycleDto(
            PlanKey: plan.Key,
            Key: Unique("scycle"),
            DisplayName: "Monthly",
            DurationUnit: "months",
            DurationValue: 1,
            ExternalProductId: priceId));
        var stripeCustomerId = Unique("cus");
        var customer = await _ctx.Subscrio.Customers.CreateCustomerAsync(new CreateCustomerDto(
            Key: Unique("scust"),
            DisplayName: "Stripe Cust",
            ExternalBillingId: stripeCustomerId));

        var periodStart = DateTime.UtcNow;
        var periodEnd = periodStart.AddMonths(1);
        var eventId = Unique("evt");
        var stripeSubId = Unique("sub");
        var stripeSub = new Subscription
        {
            Id = stripeSubId,
            CustomerId = stripeCustomerId,
            Status = "active",
            Created = periodStart,
            CancelAtPeriodEnd = false,
            Metadata = new Dictionary<string, string>
            {
                ["subscrioCustomerKey"] = customer.Key
            },
            Items = new StripeList<SubscriptionItem>
            {
                Data =
                [
                    new SubscriptionItem
                    {
                        Id = Unique("si"),
                        Price = new Price { Id = priceId },
                        CurrentPeriodStart = periodStart,
                        CurrentPeriodEnd = periodEnd
                    }
                ]
            }
        };

        var stripeEvent = new Event
        {
            Id = eventId,
            Type = EventTypes.CustomerSubscriptionCreated,
            Data = new EventData { Object = stripeSub }
        };

        var stripeRefs = ExtractStripeEntityRefs.FromEvent(stripeEvent);
        stripeRefs.StripeCustomerId.Should().Be(stripeCustomerId);
        stripeRefs.StripeSubscriptionId.Should().Be(stripeSubId);

        await _ctx.Subscrio.Stripe.ProcessStripeEventAsync(stripeEvent);

        var page = await _audit.ListAsync(new TransactionLogFilters
        {
            StripeEventId = eventId,
            EntityType = "stripe_event"
        });
        page.Total.Should().BeGreaterThanOrEqualTo(1);
        var row = page.Data[0];
        row.Action.Should().Be("stripe_event");
        row.EntityType.Should().Be("stripe_event");
        row.StripeEventId.Should().Be(eventId);
        row.StripeEventType.Should().Be("customer.subscription.created");
        row.CustomerKey.Should().Be(customer.Key);
        row.CustomerId.Should().NotBeNull();
        row.SubscriptionId.Should().NotBeNull();
        row.SubscriptionKey.Should().NotBeNullOrEmpty();
        row.Metadata.Should().NotBeNull();
        row.Metadata!["stripeCustomerId"]!.ToString().Should().Be(stripeCustomerId);
        row.Metadata["stripeSubscriptionId"]!.ToString().Should().Be(stripeSubId);
        row.EventPayload.Should().NotBeNull();
        row.EventPayload!["id"]!.ToString().Should().Be(eventId);
        row.EventPayload["type"]!.ToString().Should().Be("customer.subscription.created");
    }

    [Fact]
    public async Task ListFilters_ReturnDataAndTotal()
    {
        await _audit.InstallSchemaAsync();
        var keyA = Unique("filter-a");
        var keyB = Unique("filter-b");
        await _ctx.Subscrio.Customers.CreateCustomerAsync(new CreateCustomerDto(Key: keyA, DisplayName: "A"));
        await _ctx.Subscrio.Customers.CreateCustomerAsync(new CreateCustomerDto(Key: keyB, DisplayName: "B"));

        var byCustomer = await _audit.ListAsync(new TransactionLogFilters { CustomerKey = keyA });
        byCustomer.Data.Should().OnlyContain(r => r.CustomerKey == keyA);
        byCustomer.Total.Should().Be(byCustomer.Data.Count);

        var byAction = await _audit.ListAsync(new TransactionLogFilters
        {
            Action = "create",
            EntityType = "customer",
            Limit = 10
        });
        byAction.Data.Should().OnlyContain(r => r.Action == "create");
        byAction.Total.Should().BeGreaterThanOrEqualTo(2);

        var bySearch = await _audit.ListAsync(new TransactionLogFilters { Search = keyB });
        bySearch.Data.Should().Contain(r => r.CustomerKey == keyB);

        var bySource = await _audit.ListAsync(new TransactionLogFilters { Source = "api", Limit = 5 });
        bySource.Data.Should().OnlyContain(r => r.Source == "api");
    }

    [Fact]
    public async Task Dispose_Unsubscribes_And_AfterHookFailureLeavesMutation()
    {
        await _audit.InstallSchemaAsync();

        await _audit.DisposeAsync();

        var key = Unique("disposed");
        await _ctx.Subscrio.Customers.CreateCustomerAsync(new CreateCustomerDto(
            Key: key,
            DisplayName: "No Audit"));

        await using var reader = _ctx.Subscrio.UseAuditLog(new AuditLogOptions
        {
            ConnectionString = _ctx.ConnectionString
        });
        await reader.InstallSchemaAsync();
        var before = await reader.ListAsync(new TransactionLogFilters { CustomerKey = key });
        before.Total.Should().Be(0);
        await reader.DisposeAsync();

        _audit = _ctx.Subscrio.UseAuditLog(new AuditLogOptions
        {
            ConnectionString = _ctx.ConnectionString
        });
        await _audit.InstallSchemaAsync();

        await using (var client = new NpgsqlConnection(_ctx.ConnectionString))
        {
            await client.OpenAsync();
            await using var drop = new NpgsqlCommand(
                "DROP TABLE IF EXISTS subscrio.transaction_logs CASCADE", client);
            await drop.ExecuteNonQueryAsync();
        }

        var failKey = Unique("fail");
        var act = async () => await _ctx.Subscrio.Customers.CreateCustomerAsync(new CreateCustomerDto(
            Key: failKey,
            DisplayName: "Should Fail Audit"));

        await act.Should().ThrowAsync<Exception>();

        var persisted = await _ctx.Subscrio.Customers.GetCustomerAsync(failKey);
        persisted.Should().NotBeNull();
        persisted!.DisplayName.Should().Be("Should Fail Audit");

        await _audit.InstallSchemaAsync();
    }

    [Fact] public async Task CreditAfterEventsAuditOnceAcrossRetry(){await _audit.InstallSchemaAsync();var c=Unique("credits-customer");var cu=Unique("currency");var f=Unique("action");var app=_ctx.Subscrio;await app.Customers.CreateCustomerAsync(new(c));await app.Features.CreateFeatureAsync(new(f,f,"toggle","true"));await app.Credits.CreateCurrencyAsync(new(cu,cu));await app.Credits.SetConsumptionRuleAsync(f,cu,2);await app.Credits.GrantAsync(new(c,cu,10,"prepaid","grant"));var input=new CreditConsumeInput(c,f,2,"consume");await app.Credits.ConsumeAsync(input);await app.Credits.ConsumeAsync(input);var rows=(await _audit.ListAsync(new(){CustomerKey=c})).Data;Assert.Single(rows,r=>r.Summary=="credit.consumed.after");Assert.Single(rows,r=>r.Summary=="credit.granted.after");}
}
