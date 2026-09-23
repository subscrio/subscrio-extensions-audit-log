using System.Text.Json;
using Subscrio.AuditLog.Repository;
using Subscrio.Core.Application.DTOs;
using Subscrio.Core.Application.Hooks;

namespace Subscrio.AuditLog.Mapping;

public static class MapHookEvent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static InsertTransactionLogInput MapCustomerAfterEvent(CustomerMutationHookEvent evt)
    {
        var dto = evt.New ?? evt.Old;
        var key = DtoKey(dto);
        var action = ActionFromCustomerEvent(evt.Type);
        var label = StripAfter(evt.Type);

        return new InsertTransactionLogInput
        {
            Source = evt.Source,
            Action = action,
            EntityType = "customer",
            EntityKey = key,
            CustomerKey = key,
            SubscriptionKey = null,
            CustomerId = evt.EntityId,
            SubscriptionId = null,
            Actor = null,
            Summary = key != null ? $"{label}: {key}" : label,
            PreValue = AsJson(evt.Old),
            PostValue = AsJson(evt.New),
            Metadata = new Dictionary<string, object?> { ["hookType"] = evt.Type }
        };
    }

    public static InsertTransactionLogInput MapSubscriptionAfterEvent(SubscriptionMutationHookEvent evt)
    {
        var dto = evt.New ?? evt.Old;
        var key = DtoKey(dto);
        var custKey = CustomerKeyFrom(dto) ?? CustomerKeyFrom(evt.Old) ?? CustomerKeyFrom(evt.New);
        var action = ActionFromSubscriptionEvent(evt.Type);
        var label = StripAfter(evt.Type);

        var metadata = new Dictionary<string, object?> { ["hookType"] = evt.Type };
        if (evt.FeatureKey != null) metadata["featureKey"] = evt.FeatureKey;
        if (evt.Value != null) metadata["value"] = evt.Value;
        if (evt.OverrideType != null) metadata["overrideType"] = evt.OverrideType;
        if (evt.ExpiresAt != null) metadata["expiresAt"] = evt.ExpiresAt;

        return new InsertTransactionLogInput
        {
            Source = evt.Source,
            Action = action,
            EntityType = "subscription",
            EntityKey = key,
            CustomerKey = custKey,
            SubscriptionKey = key,
            CustomerId = evt.CustomerId,
            SubscriptionId = evt.EntityId,
            Actor = null,
            Summary = key != null ? $"{label}: {key}" : label,
            PreValue = AsJson(evt.Old),
            PostValue = AsJson(evt.New),
            Metadata = metadata
        };
    }

    public static InsertTransactionLogInput MapStripeReceivedAfterEvent(
        StripeReceivedHookEvent evt,
        StripeEntityAssociation? association = null,
        StripeEntityRefs? refs = null)
    {
        var stripeEvent = evt.Data;
        var eventId = stripeEvent?.Id;
        var eventType = stripeEvent?.Type;

        var metadata = new Dictionary<string, object?> { ["hookType"] = evt.Type };
        if (!string.IsNullOrEmpty(refs?.StripeCustomerId))
            metadata["stripeCustomerId"] = refs.StripeCustomerId;
        if (!string.IsNullOrEmpty(refs?.StripeSubscriptionId))
            metadata["stripeSubscriptionId"] = refs.StripeSubscriptionId;

        return new InsertTransactionLogInput
        {
            Source = "stripe",
            Action = "stripe_event",
            EntityType = "stripe_event",
            EntityKey = eventId,
            CustomerKey = association?.CustomerKey,
            SubscriptionKey = association?.SubscriptionKey,
            CustomerId = association?.CustomerId,
            SubscriptionId = association?.SubscriptionId,
            Actor = null,
            Summary = eventType != null ? $"stripe.received: {eventType}" : "stripe.received",
            StripeEventId = eventId,
            StripeEventType = eventType,
            EventPayload = AsJson(stripeEvent),
            PreValue = null,
            PostValue = null,
            Metadata = metadata
        };
    }

    private static string? DtoKey(CustomerDto? dto) => dto?.Key;
    private static string? DtoKey(SubscriptionDto? dto) => dto?.Key;

    private static string? CustomerKeyFrom(CustomerDto? dto) => dto?.Key;

    private static string? CustomerKeyFrom(SubscriptionDto? dto)
    {
        if (dto == null) return null;
        if (!string.IsNullOrEmpty(dto.CustomerKey)) return dto.CustomerKey;
        return dto.Key;
    }

    private static string StripAfter(string type) =>
        type.EndsWith(".after", StringComparison.Ordinal)
            ? type[..^".after".Length]
            : type;

    private static string ActionFromCustomerEvent(string type)
    {
        if (type.Contains(".created.", StringComparison.Ordinal)) return "create";
        if (type.Contains(".updated.", StringComparison.Ordinal)) return "update";
        if (type.Contains(".archived.", StringComparison.Ordinal)) return "archive";
        if (type.Contains(".unarchived.", StringComparison.Ordinal)) return "unarchive";
        if (type.Contains(".deleted.", StringComparison.Ordinal)) return "delete";
        return "update";
    }

    private static string ActionFromSubscriptionEvent(string type)
    {
        if (type.Contains("featureOverride", StringComparison.Ordinal)) return "feature_override";
        if (type.Contains("temporaryOverridesCleared", StringComparison.Ordinal)) return "clear_overrides";
        if (type.Contains(".created.", StringComparison.Ordinal)) return "create";
        if (type.Contains(".updated.", StringComparison.Ordinal)) return "update";
        if (type.Contains(".archived.", StringComparison.Ordinal)) return "archive";
        if (type.Contains(".unarchived.", StringComparison.Ordinal)) return "unarchive";
        if (type.Contains(".deleted.", StringComparison.Ordinal)) return "delete";
        return "update";
    }

    private static Dictionary<string, object?>? AsJson(object? value)
    {
        if (value == null) return null;

        // Stripe.net types use Newtonsoft JsonProperty attributes for wire names.
        string json;
        if (value is Stripe.Event)
        {
            json = Newtonsoft.Json.JsonConvert.SerializeObject(value);
        }
        else
        {
            json = JsonSerializer.Serialize(value, JsonOptions);
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        return ElementToDictionary(doc.RootElement);
    }

    private static Dictionary<string, object?> ElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
            dict[prop.Name] = ElementToObject(prop.Value);
        return dict;
    }

    private static object? ElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ElementToDictionary(element),
        JsonValueKind.Array => element.EnumerateArray().Select(ElementToObject).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };
}
