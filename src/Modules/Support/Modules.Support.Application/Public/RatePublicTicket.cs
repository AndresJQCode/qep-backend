using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Fase 4b (H20): calificar por la página de consulta. Es un comando: un PIN errado se anota en la clave del radicado.</summary>
public sealed record RatePublicTicketCommand(
    string? TenantSlug,
    string VisitorIp,
    string? Number,
    string? Pin,
    string? TurnstileToken,
    string? Rating,
    string? Comment)
    : ICommand<SupportPublicTicketDto>;

public sealed class RatePublicTicketValidator : AbstractValidator<RatePublicTicketCommand>
{
    public RatePublicTicketValidator()
    {
        RuleFor(command => command.Rating).MustBeASurveyRating();
        RuleFor(command => command.Comment).MustBeASurveyComment();
    }
}

/// <summary>
/// Fase 4b (H20, H21): calificar la encuesta de la última resolución desde la página de consulta. La verificación es exactamente la de la
/// consulta (<see cref="SupportPublicAccess"/>): las mismas fallas, el mismo cuerpo y el mismo conteo de intentos. Con el PIN correcto,
/// una encuesta que no se responde (no hay, sin procesar, apagada, reabierto) es <c>422 support.survey.closed</c> y una ya calificada,
/// <c>support.survey.already_answered</c>. Un ticket anónimo sólo califica por acá. Responde lo mismo que la consulta, ya con la
/// calificación.
/// </summary>
public sealed class RatePublicTicketHandler(
    IValidator<RatePublicTicketCommand> validator,
    SupportPublicAccess access,
    SupportPublicTicketView view,
    ISupportTicketRepository tickets,
    SupportSurveyAnswering answering)
    : ICommandHandler<RatePublicTicketCommand, SupportPublicTicketDto>
{
    public async Task<SupportPublicTicketDto> HandleAsync(RatePublicTicketCommand command, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var granted = await access.VerifyAsync(
            command.TenantSlug, command.VisitorIp, command.Number, command.Pin, command.TurnstileToken, cancellationToken);
        var tenantId = granted.Tenant.TenantId;
        var survey = await tickets.FindLatestSurveyAsync(tenantId, granted.Ticket.Id, cancellationToken)
            ?? throw new SupportDomainException(SupportErrorCodes.SurveyClosed, "The ticket has no survey to answer.");
        await answering.AnswerAsync(
            tenantId, survey.Id, SupportSurveyAnswerRules.Parse(command.Rating), command.Comment, SupportSurveyChannel.Portal,
            cancellationToken);
        return await view.ComposeAsync(tenantId, granted.Ticket, granted.Context, cancellationToken);
    }
}
