using System.Globalization;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Api;

/// <summary>
/// La única regla de idempotencia de WMS (S2), en dos modos. <b>Header</b> (rutas IK): la clave es <c>Idempotency-Key</c>. <b>Server</b>
/// (rutas SK, acciones sobre un documento versionado): la clave la deriva el servidor y el header se ignora. El orden es parte del
/// contrato: primero el tenant, después el recibo, para que otro tenant no pueda sondear claves ajenas.
/// </summary>
public sealed class IdempotencyKeyFilter(IdempotencyKeySource source, string? document, string? action) : IEndpointFilter
{
    private const string HeaderName = "Idempotency-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;

        // Un cuerpo ausente o ilegible ya fijó su 400/415 al enlazar los argumentos: no hay clave que derivar ni recibo que buscar.
        if (http.Response.StatusCode >= StatusCodes.Status400BadRequest)
        {
            return await next(context);
        }

        var services = http.RequestServices;
        var executionContext = services.GetRequiredService<IExecutionContext>();

        // 1. Tenant antes de tocar operation_receipts: un 403 igual exista o no la clave. Una ruta sin tenantId es un error de cableado
        // que se trata como denegado y no como 500, para no distinguirse de una ruta ajena.
        var routeTenant = Convert.ToString(http.GetRouteValue("tenantId"), CultureInfo.InvariantCulture);
        if (!Guid.TryParse(routeTenant, out var tenantId) || executionContext.TenantId.Value != tenantId)
        {
            throw WmsAuthorization.Denied();
        }

        var receipts = services.GetRequiredService<IOperationReceipts>();
        var scope = services.GetRequiredService<IdempotencyScope>();
        var method = http.Request.Method;
        var path = http.Request.Path.Value ?? string.Empty;

        // 2 y 3. Clave y hash.
        string key;
        string hash;
        if (source == IdempotencyKeySource.Header)
        {
            key = http.Request.Headers[HeaderName].ToString();
            if (string.IsNullOrWhiteSpace(key) || key.Length > OperationReceipt.KeyMaxLength)
            {
                throw new PreconditionRequiredException(
                    WmsErrorCodes.IdempotencyKeyRequired, "Esta operación necesita el header Idempotency-Key.");
            }

            // El spec fija `Idempotency-Key: <uuid>`. Una clave libre compartiría espacio con las que deriva el servidor
            // (`adjustment:<id>:post:3`): un cliente podría fabricarla y bloquear esa acción durante los 45 días del recibo.
            // No hay un código propio para «clave inválida»; la respuesta es la misma 428 que la clave ausente.
            if (!Guid.TryParse(key, out _))
            {
                throw new PreconditionRequiredException(
                    WmsErrorCodes.IdempotencyKeyRequired, "El header Idempotency-Key debe ser un UUID.");
            }

            hash = RequestHasher.Hash(method, path, context.Arguments.OfType<IIdempotentRequest>().FirstOrDefault());
        }
        else
        {
            var versioned = context.Arguments.OfType<IVersionedRequest>().FirstOrDefault()
                ?? throw new InvalidOperationException("A server-keyed route needs a body that implements IVersionedRequest.");
            var routeId = Convert.ToString(http.GetRouteValue("id"), CultureInfo.InvariantCulture);
            if (!Guid.TryParse(routeId, out var id))
            {
                throw new InvalidOperationException("A server-keyed route needs a Guid 'id' route value.");
            }

            key = ServerIdempotencyKeys.For(
                document ?? throw new InvalidOperationException("A server-keyed filter needs a document name."),
                id,
                action ?? throw new InvalidOperationException("A server-keyed filter needs an action name."),
                versioned.Version);
            // La versión ya viaja en la clave: hashear sin cuerpo hace que repetir la acción nunca dé 409.
            hash = RequestHasher.Hash(method, path, null);
        }

        // 4. Recibo existente.
        var existing = await receipts.FindAsync(tenantId, key, http.RequestAborted);
        if (existing is not null)
        {
            return Replay(existing, hash);
        }

        scope.TenantId = tenantId;
        scope.Key = key;
        scope.RequestHash = hash;
        scope.Source = source;

        try
        {
            return await next(context);
        }
        // 5. Dos peticiones iguales a la vez: la perdedora puede fallar por la PK del recibo (OperationReplayedException) o antes, por una
        // precondición que la ganadora ya consumió (412 de versión, 422 de ya reversado, saldo insuficiente). Sea cual sea el error, su
        // transacción ya se deshizo (el await using del handler) y el recibo de la ganadora es visible: reintentar tiene que ser
        // indistinguible de haber acertado a la primera (§6.4). Sin recibo no hay qué repetir y el error original sigue. La
        // cancelación no cuenta: el cliente se fue.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var winner = await receipts.FindAsync(tenantId, key, CancellationToken.None);
            if (winner is null)
            {
                throw;
            }

            return Replay(winner, hash);
        }
    }

    private static IResult Replay(OperationReceipt receipt, string hash) =>
        receipt.RequestHash == hash
            ? Results.Content(receipt.ResponseJson, "application/json", null, StatusCodes.Status200OK)
            : throw new ResourceConflictException(WmsErrorCodes.IdempotencyKeyReused, "Ya usaste esta Idempotency-Key con otro cuerpo.");
}

public static class IdempotencyEndpointExtensions
{
    /// <summary>Ruta IK: exige el header <c>Idempotency-Key</c> (428 si falta).</summary>
    public static RouteHandlerBuilder RequireIdempotencyKey(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(new IdempotencyKeyFilter(IdempotencyKeySource.Header, null, null));
    }

    /// <summary>Ruta SK: la clave sale de <c>{document}:{id}:{action}:{version}</c>; el cuerpo tiene que ser un
    /// <see cref="IVersionedRequest"/> y la ruta llevar <c>{id:guid}</c>.</summary>
    public static RouteHandlerBuilder UseServerIdempotencyKey(this RouteHandlerBuilder builder, string document, string action)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(new IdempotencyKeyFilter(IdempotencyKeySource.Server, document, action));
    }
}
