using System.Text.Json;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-29 §1: el historial de la app no trae los archivos, sólo avisa que hubo uno (<c>media_placeholder</c>, guardado como
/// <c>Unsupported</c> con <c>{"historyMedia":"placeholder"}</c>). BFF: la burbuja dibuja «descargando» o «no disponible» según este
/// estado, porque el frontend no sabe que Meta sólo conserva los archivos de los últimos 14 días.
/// </summary>
public static class HistoryMediaState
{
    public const string Pending = "Pending";
    public const string Unavailable = "Unavailable";

    /// <summary>Spec §1: sólo los últimos 14 días traen un id de medio.</summary>
    public static readonly TimeSpan MetaMediaHistory = TimeSpan.FromDays(14);

    public static bool IsPlaceholder(string? detailsJson)
    {
        if (string.IsNullOrEmpty(detailsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("historyMedia", out var marker)
                && marker.ValueKind == JsonValueKind.String
                && marker.GetString() == "placeholder";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <returns><c>null</c> si no es un placeholder o ya tiene medio; <see cref="Unavailable"/> si el mensaje es anterior a la
    /// importación por más de 14 días; si no, <see cref="Pending"/>.</returns>
    public static string? For(string? detailsJson, bool hasMedia, DateTimeOffset occurredAt, DateTimeOffset createdAt)
    {
        if (hasMedia || !IsPlaceholder(detailsJson))
        {
            return null;
        }

        // Resta de instantes y no createdAt - 14 días: no se sale del rango de DateTimeOffset.
        return createdAt - occurredAt > MetaMediaHistory ? Unavailable : Pending;
    }
}
