using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>La consulta es un comando y no una lectura: un intento fallido se anota en la clave del radicado.</summary>
public sealed record LookupPublicTicketCommand(
    string? TenantSlug, string VisitorIp, string? Number, string? Pin, string? TurnstileToken)
    : ICommand<SupportPublicTicketDto>;

/// <summary>
/// Consultar un ticket con radicado y PIN (spec §9.4, plan F7 a F9 y F13). Orden: Turnstile → tenant → radicado → clave. Un radicado
/// inexistente o mal escrito, un PIN errado y un radicado bloqueado responden lo mismo, <c>422 support.public.lookup_failed</c>, con el
/// mismo cuerpo, y los tres pasan por una verificación PBKDF2 (contra el hash del radicado o contra el señuelo): ni el código ni el
/// tiempo dicen si el radicado existe. Un fallo cuenta en la clave del radicado (cinco bloquean 15 minutos, también para el PIN correcto);
/// un bloqueado no cuenta ni verifica contra su hash; un acierto pone el contador en cero. La clave vive aparte del ticket, que se lee sin
/// tracking: consultar nunca le sube la versión. Mirar el bloqueo y anotar el intento ocurren con la fila de la clave tomada
/// (<see cref="ISupportTicketAccessRepository.LockByNumberAsync"/>). No lee el contexto de ejecución ni la sesión.
/// </summary>
public sealed partial class LookupPublicTicketHandler(
    SupportPublicResolver resolver,
    ISupportCaptcha captcha,
    ISupportTicketAccessRepository accesses,
    ISupportReplyRepository replies,
    ISupportPinHasher pinHasher,
    ISupportUnitOfWork unitOfWork,
    SupportTicketDtoComposer composer)
    : ICommandHandler<LookupPublicTicketCommand, SupportPublicTicketDto>
{
    public async Task<SupportPublicTicketDto> HandleAsync(LookupPublicTicketCommand command, CancellationToken cancellationToken)
    {
        if (!await captcha.VerifyAsync(command.TurnstileToken?.Trim() ?? string.Empty, command.VisitorIp, cancellationToken))
        {
            throw new SupportDomainException(SupportErrorCodes.PublicCaptchaInvalid, "The anti-bot challenge was not passed.");
        }

        var tenant = await resolver.ResolveForConsultAsync(command.TenantSlug, cancellationToken);
        var pin = command.Pin?.Trim() ?? string.Empty;
        SupportTicket ticket;
        SupportReadContext context;
        // Revisión del Bloque B (Important 1): la clave se lee con FOR UPDATE y el intento se anota en la misma transacción. Dos
        // intentos sobre el mismo radicado van en fila: cada uno ve el contador y el bloqueo que dejó el anterior, así N fallos a la vez
        // cuentan N y el quinto bloquea. Sólo se toma la fila de ticket_access: el ticket sigue sin tracking y sin versión nueva.
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var found = SupportTicketNumber.TryParse(command.Number, out var number) && PinFormat().IsMatch(pin)
                ? await accesses.LockByNumberAsync(tenant.TenantId, number, cancellationToken)
                : null;
            if (found is not { } match)
            {
                // El señuelo iguala el tiempo de un radicado que no existe con el de uno con el PIN errado.
                pinHasher.Verify(null, pin);
                throw LookupFailed();
            }

            (ticket, var access) = match;

            // Un solo reloj para el bloqueo y para el plazo que se muestra.
            context = await composer.ReadContextAsync(tenant.TenantId, cancellationToken);
            if (access.IsLocked(context.Now))
            {
                // Bloqueado: no se verifica contra su hash ni cuenta otro fallo, pero se paga el mismo PBKDF2 con el señuelo.
                pinHasher.Verify(null, pin);
                throw LookupFailed();
            }

            if (!pinHasher.Verify(access.PinHash, pin))
            {
                access.RecordFailure(context.Now);
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
        }

        var (dueState, _) = SupportDueStates.Evaluate(
            ticket.Status, ticket.DueOn, context.Today, context.Clock.Calendar, context.DueSoonBusinessDays);
        var sent = (await replies.ListAsync(tenant.TenantId, ticket.Id, cancellationToken))
            .Where(reply => reply.Status is SupportReplyStatus.Sent)
            .OrderBy(reply => reply.UpdatedAt)
            .Select(reply => new SupportPublicReplyDto(reply.Text, reply.UpdatedAt, reply.Channel.ToString()))
            .ToList();

        return new SupportPublicTicketDto(
            ticket.DisplayNumber,
            ticket.TypeName,
            ticket.Status.ToString(),
            ticket.ReceivedAt,
            ticket.DueOn,
            dueState.ToString(),
            ticket.TermIsLegal,
            sent);
    }

    // Un solo mensaje para todas las causas: el cuerpo es idéntico salvo el traceId.
    private static SupportDomainException LookupFailed() =>
        new(SupportErrorCodes.PublicLookupFailed, "The ticket number or the PIN is not valid.");

    [GeneratedRegex("^[0-9]{6}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PinFormat();
}
