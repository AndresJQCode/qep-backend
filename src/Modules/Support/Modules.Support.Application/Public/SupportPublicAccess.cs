using System.Text.RegularExpressions;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Lo que deja pasar la verificación de radicado y PIN: el tenant del slug, el ticket (sin tracking) y el contexto de lectura con
/// el reloj que se usó para el bloqueo.</summary>
public sealed record SupportPublicAccessGranted(SupportPublicConsultTenant Tenant, SupportTicket Ticket, SupportReadContext Context);

/// <summary>
/// La verificación de la consulta pública (spec §9.4, plan F7 a F9 y F13), extraída de <see cref="LookupPublicTicketHandler"/> en la fase
/// 4b (Tarea 13) para que calificar por la consulta (H20) verifique exactamente igual. Orden: Turnstile → tenant → radicado → clave. Un
/// radicado inexistente o mal escrito, un PIN errado y un radicado bloqueado responden lo mismo, <c>422 support.public.lookup_failed</c>,
/// con el mismo cuerpo, y los tres pasan por una verificación PBKDF2 (contra el hash del radicado o contra el señuelo): ni el código ni el
/// tiempo dicen si el radicado existe. Un fallo cuenta en la clave del radicado (cinco bloquean 15 minutos, también para el PIN correcto);
/// un bloqueado no cuenta ni verifica contra su hash; un acierto pone el contador en cero. La clave vive aparte del ticket, que se lee sin
/// tracking: verificar nunca le sube la versión. Mirar el bloqueo y anotar el intento ocurren con la fila de la clave tomada
/// (<see cref="ISupportTicketAccessRepository.LockByNumberAsync"/>). No lee el contexto de ejecución ni la sesión.
/// </summary>
public sealed partial class SupportPublicAccess(
    SupportPublicResolver resolver,
    ISupportCaptcha captcha,
    ISupportTicketAccessRepository accesses,
    ISupportPinHasher pinHasher,
    ISupportUnitOfWork unitOfWork,
    SupportTicketDtoComposer composer)
{
    public async Task<SupportPublicAccessGranted> VerifyAsync(
        string? tenantSlug, string visitorIp, string? number, string? pin, string? turnstileToken, CancellationToken cancellationToken)
    {
        if (!await captcha.VerifyAsync(turnstileToken?.Trim() ?? string.Empty, visitorIp, cancellationToken))
        {
            throw new SupportDomainException(SupportErrorCodes.PublicCaptchaInvalid, "The anti-bot challenge was not passed.");
        }

        var tenant = await resolver.ResolveForConsultAsync(tenantSlug, cancellationToken);
        var typedPin = pin?.Trim() ?? string.Empty;
        // Revisión del Bloque B (Important 1): la clave se lee con FOR UPDATE y el intento se anota en la misma transacción. Dos
        // intentos sobre el mismo radicado van en fila: cada uno ve el contador y el bloqueo que dejó el anterior, así N fallos a la vez
        // cuentan N y el quinto bloquea. Sólo se toma la fila de ticket_access: el ticket sigue sin tracking y sin versión nueva.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // Triage #4 del Bloque F: las mismas lecturas en los tres caminos, para que el tiempo tampoco diga si el radicado existe. El
        // contexto (un solo reloj para el bloqueo y para el plazo que se muestra) se lee siempre, y la clave se busca siempre: un
        // radicado mal escrito busca el 0, que nunca existe (los radicados empiezan en 1). Un PIN mal formado sobre un radicado que
        // existe no cuenta como intento, como antes. Un tenant que nunca abrió Soporte no tiene fila de ajustes ni tickets: no se
        // aprovisiona desde acá, y su contexto es null.
        var readContext = await composer.TryReadContextAsync(tenant.TenantId, cancellationToken);
        var parsed = SupportTicketNumber.TryParse(number, out var ticketNumber);
        var found = await accesses.LockByNumberAsync(tenant.TenantId, parsed ? ticketNumber : 0, cancellationToken);
        if (!parsed || !PinFormat().IsMatch(typedPin) || found is not { } match || readContext is null)
        {
            // El señuelo iguala el tiempo de un radicado que no existe con el de uno con el PIN errado.
            pinHasher.Verify(null, typedPin);
            throw LookupFailed();
        }

        var (ticket, access) = match;
        if (access.IsLocked(readContext.Now))
        {
            // Bloqueado: no se verifica contra su hash ni cuenta otro fallo, pero se paga el mismo PBKDF2 con el señuelo.
            pinHasher.Verify(null, typedPin);
            throw LookupFailed();
        }

        if (!pinHasher.Verify(access.PinHash, typedPin))
        {
            access.RecordFailure(readContext.Now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw LookupFailed();
        }

        if (access.FailedAttempts > 0)
        {
            access.RecordSuccess();
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new SupportPublicAccessGranted(tenant, ticket, readContext);
    }

    // Un solo mensaje para todas las causas: el cuerpo es idéntico salvo el traceId.
    private static SupportDomainException LookupFailed() =>
        new(SupportErrorCodes.PublicLookupFailed, "The ticket number or the PIN is not valid.");

    [GeneratedRegex("^[0-9]{6}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PinFormat();
}
