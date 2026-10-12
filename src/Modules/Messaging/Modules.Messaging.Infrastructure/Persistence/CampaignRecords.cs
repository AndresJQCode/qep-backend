using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>
/// La fila de <c>messaging.campaign_recipients</c> (spec 2026-10-11 §6.1). Se escribe por SQL —el worker la congela, la reclama y la
/// liquida con <c>FOR UPDATE SKIP LOCKED</c>— y se lee por SQL con los contadores; el modelo de EF existe para el esquema y la
/// migración, no para cargarla.
/// </summary>
internal sealed class CampaignRecipientRecord
{
    public Guid Id { get; set; }

    public Guid CampaignId { get; set; }

    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    public string CustomerName { get; set; } = string.Empty;

    public string? WaId { get; set; }

    public string? UserId { get; set; }

    public CampaignRecipientStatus Status { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureReason { get; set; }

    public Guid? ConversationId { get; set; }

    public Guid? MessageId { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>
/// <c>parameters jsonb</c>: <c>{ "1": { "source": "customerName" }, "promo": { "source": "text", "value": "20%" } }</c>. A mano con
/// <see cref="JsonNode"/> y no con un serializador sobre el record, así los nombres de las fuentes (<c>text</c>, <c>customerName</c>) son
/// los del contrato HTTP y no los del enum.
/// </summary>
internal static class CampaignParametersJson
{
    public static string Serialize(IReadOnlyDictionary<string, CampaignParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var root = new JsonObject();
        foreach (var (key, parameter) in parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var node = new JsonObject { ["source"] = parameter.SourceWire };
            if (parameter.Value is not null)
            {
                node["value"] = parameter.Value;
            }

            root[key] = node;
        }

        return root.ToJsonString();
    }

    public static IReadOnlyDictionary<string, CampaignParameter> Parse(string? json)
    {
        var result = new Dictionary<string, CampaignParameter>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(json))
        {
            return result;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var source = property.Value.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() : null;
            if (!CampaignParameter.TryParseSource(source, out var parsed))
            {
                continue;
            }

            var value = property.Value.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String ? valueElement.GetString() : null;
            result[property.Name] = new CampaignParameter(parsed, parsed == CampaignParameterSource.Text ? value : null);
        }

        return result;
    }

    /// <summary>Compara por el JSON: sin esto, EF vería un diccionario nuevo con los mismos datos como un cambio.</summary>
    public static ValueConverter<IReadOnlyDictionary<string, CampaignParameter>, string> Converter { get; } =
        new(parameters => Serialize(parameters), json => Parse(json));

    public static ValueComparer<IReadOnlyDictionary<string, CampaignParameter>> Comparer { get; } = new(
        (left, right) => Serialize(left!) == Serialize(right!),
        parameters => Serialize(parameters).GetHashCode(StringComparison.Ordinal),
        parameters => Parse(Serialize(parameters)));
}
