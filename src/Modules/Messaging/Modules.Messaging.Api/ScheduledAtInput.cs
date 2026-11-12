using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modules.Messaging.Api;

/// <summary>Spec 2026-11-09 «Contrato HTTP»: <c>scheduledAt</c> es un instante. El SPA lo manda con <c>Z</c>; sin zona horaria se lee como UTC,
/// nunca como la hora local del servidor (que en un equipo de desarrollo no es UTC). Con zona, se normaliza a UTC.</summary>
internal static class ScheduledAtInput
{
    /// <summary>El campo del multipart de la nota de voz. Ausente o ilegible: <c>null</c>, y el validador lo marca en
    /// <c>errors.scheduledAt</c> en vez de adivinar una hora.</summary>
    public static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
}

/// <summary>El mismo criterio que <see cref="ScheduledAtInput"/> para las rutas JSON: System.Text.Json lee un valor sin zona como hora local.
/// El formato sigue siendo el ISO 8601 estricto de System.Text.Json.</summary>
internal sealed class ScheduledAtJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Sin zona, TryGetDateTime devuelve Kind Unspecified; con Z o con offset, Utc o Local.
        if (reader.TryGetDateTime(out var value) && value.Kind == DateTimeKind.Unspecified)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }

        return reader.GetDateTimeOffset().ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value);
    }
}
