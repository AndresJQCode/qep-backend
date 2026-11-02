using Microsoft.AspNetCore.Http;
using Modules.ApiAccess.Domain;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// Lo que se va sabiendo de una llamada a la API externa mientras cruza la tubería: autenticación sabe qué clave es, el handler
/// qué mensaje resolvió y el manejador de excepciones con qué código terminó. Vive en <see cref="HttpContext.Items"/> porque
/// esos tres no se ven entre sí, y sólo guarda ids y códigos: nada de lo que manda el integrador (spec 2026-11-01 parte 1).
/// </summary>
public sealed class ExternalCallNote
{
    private static readonly object ItemKey = new();

    private (Guid Id, string Name, Guid TenantId)? _apiKey;
    private string? _errorCode;
    private Guid? _messageId;

    public static ExternalCallNote Attach(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var note = new ExternalCallNote();
        context.Items[ItemKey] = note;
        return note;
    }

    /// <summary>La nota de esta llamada, o <c>null</c> fuera de la API externa: quienes anotan corren también para el resto de la API.</summary>
    public static ExternalCallNote? Find(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var note) ? note as ExternalCallNote : null;
    }

    public void Identify(Guid apiKeyId, string apiKeyName, Guid tenantId) => _apiKey = (apiKeyId, apiKeyName, tenantId);

    public void NoteError(string code) => _errorCode = code;

    public void NoteMessage(Guid messageId) => _messageId = messageId;

    /// <summary>La fila de la llamada, o <c>null</c> si nadie la identificó: sin clave conocida no hay tenant al que atribuirla, y
    /// quien sólo sabe un prefijo no puede llenarle el log a nadie.</summary>
    public ApiKeyRequest? ToRequest(string method, string path, int statusCode, string? traceId, int durationMs, DateTimeOffset occurredAt) =>
        _apiKey is { } apiKey
            ? ApiKeyRequest.Record(apiKey.TenantId, apiKey.Id, apiKey.Name, method, path, statusCode, _errorCode, _messageId, traceId, durationMs, occurredAt)
            : null;
}
