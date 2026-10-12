using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Meta;

/// <summary>
/// Spec 2026-10-11 §5.1: el catálogo de plantillas aprobadas de una conexión, normalizado por <see cref="TemplateCatalog"/> y guardado
/// 5 minutos en <c>IMemoryCache</c> por <c>(tenant, conexión)</c> (decisión 6: se crean y aprueban en WhatsApp Manager, acá sólo se
/// leen). <c>refresh</c> se salta la caché y la reemplaza. La conexión se resuelve siempre, también con la caché llena: una conexión
/// pausada, borrada o de otro tenant no entrega plantillas de la caché de otro momento.
/// <para>
/// Una lista que Meta no contesta no se guarda como catálogo, pero sí se recuerda 30 s como <b>falla</b>, y la lectura es de a una por
/// <c>(tenant, conexión)</c> (single-flight, R4): sin eso, con Meta caída cada destinatario de un lote de cientos esperaría su propio
/// timeout contra la misma lista. Las lecturas que esperan el turno reciben el resultado del primero (catálogo o falla). Un
/// <c>refresh</c> no mira la falla recordada: es la persona pidiendo que se intente de nuevo.
/// </para>
/// </summary>
internal sealed class MessageTemplateCatalog(IMessagingConnectionDirectory connections, IWhatsAppCloudClient meta, IMemoryCache cache, TimeProvider? time = null) : IMessageTemplates
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>R4: cuánto se recuerda que Meta no contestó la lista.</summary>
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(30);

    // Un semáforo por (tenant, conexión), compartido entre los scopes: el catálogo es scoped, la caché y estos turnos no.
    private static readonly ConcurrentDictionary<(Guid TenantId, Guid ConnectionId), SemaphoreSlim> Flights = new();

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<IReadOnlyList<MessageTemplateDto>> ListAsync(Guid tenantId, Guid connectionId, bool refresh, CancellationToken cancellationToken)
    {
        var source = await connections.ResolveTemplateSourceAsync(tenantId, connectionId, cancellationToken)
            ?? throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");
        var key = (tenantId, connectionId);
        if (!refresh && Cached(key) is { } cached)
        {
            return cached;
        }

        var flight = Flights.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await flight.WaitAsync(cancellationToken);
        try
        {
            // Quien esperaba el turno encuentra lo que dejó el primero. Un refresh sí vuelve a preguntar.
            if (!refresh)
            {
                if (Cached(key) is { } filled)
                {
                    return filled;
                }

                if (cache.TryGetValue(FailureKey(key), out long failedAt) && _time.GetElapsedTime(failedAt) < FailureLifetime)
                {
                    throw Unavailable();
                }
            }

            var result = await meta.ListTemplatesAsync(source, cancellationToken);
            if (!result.Succeeded || result.Value is null)
            {
                cache.Set(FailureKey(key), _time.GetTimestamp(), FailureLifetime + FailureLifetime);
                throw Unavailable();
            }

            var catalog = TemplateCatalog.Normalize(result.Value);
            cache.Remove(FailureKey(key));
            cache.Set(key, catalog, Lifetime);
            return catalog;
        }
        finally
        {
            flight.Release();
        }
    }

    private IReadOnlyList<MessageTemplateDto>? Cached((Guid TenantId, Guid ConnectionId) key) =>
        cache.TryGetValue(key, out IReadOnlyList<MessageTemplateDto>? cached) ? cached : null;

    private static (string, Guid, Guid) FailureKey((Guid TenantId, Guid ConnectionId) key) => ("templates-failure", key.TenantId, key.ConnectionId);

    private static MessagingDomainException Unavailable() =>
        new(MessagingErrorCodes.TemplatesUnavailable, "WhatsApp did not answer the list of templates.");
}
