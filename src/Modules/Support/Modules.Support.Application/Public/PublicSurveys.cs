using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Fase 4b (H19): lo que la página <c>/calificar</c> muestra del token del correo. Es una lectura, pero viaja por <c>POST</c>: el
/// token va en el cuerpo, nunca en la ruta.</summary>
public sealed record LookupPublicSurveyQuery(string? TenantSlug, string? Token) : IQuery<SupportPublicSurveyDto>;

/// <summary>Fase 4b (H19): calificar por el enlace del correo.</summary>
public sealed record AnswerPublicSurveyCommand(string? TenantSlug, string? Token, string? Rating, string? Comment)
    : ICommand<SupportPublicSurveyDto>;

public sealed class AnswerPublicSurveyValidator : AbstractValidator<AnswerPublicSurveyCommand>
{
    public AnswerPublicSurveyValidator()
    {
        RuleFor(command => command.Rating).MustBeASurveyRating();
        RuleFor(command => command.Comment).MustBeASurveyComment();
    }
}

/// <summary>
/// Fase 4b (H19, owner 2026-11-16, Q-12): la encuesta del enlace del correo, sin Turnstile ni PIN: el token es la llave. Se busca por el
/// SHA-256 del token, filtrada por el tenant del slug (resuelto como la consulta: no exige formulario publicado). Un token mal formado
/// (no son 43 caracteres base64url), uno que no existe y uno de otro tenant responden lo mismo, <c>404 support.survey.not_found</c>. El
/// token nunca va a un log, a la auditoría ni a un evento.
/// </summary>
public sealed class SupportPublicSurveys(
    SupportPublicResolver resolver,
    ISupportTicketRepository tickets,
    ISupportFormRepository forms,
    ISupportTenantBranding branding)
{
    /// <summary>El tenant del slug y la encuesta del token, o el 404.</summary>
    public async Task<(SupportPublicConsultTenant Tenant, SupportTicketSurvey Survey)> FindAsync(
        string? tenantSlug, string? token, CancellationToken cancellationToken)
    {
        var tenant = await resolver.ResolveForConsultAsync(tenantSlug, cancellationToken);
        if (!SupportSurveyTokens.IsWellFormed(token)
            || await tickets.FindSurveyByTokenHashAsync(tenant.TenantId, SupportSurveyTokens.HashOf(token!), cancellationToken)
                is not { } survey)
        {
            throw SupportNotFound.Survey();
        }

        return (tenant, survey);
    }

    /// <summary>La página: el tenant, el ticket y el estado de la encuesta, leído de nuevo.</summary>
    public async Task<SupportPublicSurveyDto> ComposeAsync(
        SupportPublicConsultTenant tenant, Guid surveyId, SupportTicketId ticketId, CancellationToken cancellationToken)
    {
        var ticket = await tickets.FindAsync(tenant.TenantId, ticketId, cancellationToken) ?? throw SupportNotFound.Survey();
        var survey = (await tickets.ListSurveysAsync(tenant.TenantId, ticketId, cancellationToken)).FirstOrDefault(item => item.Id == surveyId)
            ?? throw SupportNotFound.Survey();
        var form = await forms.CurrentAsync(tenant.TenantId, cancellationToken);
        return new SupportPublicSurveyDto(
            tenant.Name,
            await branding.LogoUrlAsync(tenant.TenantId, cancellationToken),
            form?.AccentColor,
            ticket.DisplayNumber,
            ticket.TypeName,
            survey.IsAnswerable(ticket),
            survey.Rating?.ToString(),
            SupportPublicSurveyOptions.All);
    }
}

public sealed class LookupPublicSurveyHandler(SupportPublicSurveys surveys)
    : IQueryHandler<LookupPublicSurveyQuery, SupportPublicSurveyDto>
{
    public async Task<SupportPublicSurveyDto> HandleAsync(LookupPublicSurveyQuery query, CancellationToken cancellationToken)
    {
        var (tenant, survey) = await surveys.FindAsync(query.TenantSlug, query.Token, cancellationToken);
        return await surveys.ComposeAsync(tenant, survey.Id, survey.TicketId, cancellationToken);
    }
}

/// <summary>Fase 4b (H19, H21): la primera calificación gana (<c>support.survey.already_answered</c> después); una encuesta que ya no se
/// responde es <c>support.survey.closed</c>. <c>AnsweredVia = Email</c>.</summary>
public sealed class AnswerPublicSurveyHandler(
    IValidator<AnswerPublicSurveyCommand> validator,
    SupportPublicSurveys surveys,
    SupportSurveyAnswering answering)
    : ICommandHandler<AnswerPublicSurveyCommand, SupportPublicSurveyDto>
{
    public async Task<SupportPublicSurveyDto> HandleAsync(AnswerPublicSurveyCommand command, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var (tenant, survey) = await surveys.FindAsync(command.TenantSlug, command.Token, cancellationToken);
        await answering.AnswerAsync(
            tenant.TenantId, survey.Id, SupportSurveyAnswerRules.Parse(command.Rating), command.Comment, SupportSurveyChannel.Email,
            cancellationToken);
        return await surveys.ComposeAsync(tenant, survey.Id, survey.TicketId, cancellationToken);
    }
}

/// <summary>
/// Fase 4b (H19 a H21): guardar una calificación desde la página pública (el enlace o la consulta). Con la fila de la encuesta tomada
/// (<see cref="ISupportSurveyRepository.LockAsync"/>): la primera respuesta gana y la segunda ve la calificación ya guardada. Auditoría de
/// sistema (sólo el campo, nunca la calificación ni el comentario) y, después del commit, el aviso del ticket por el hub. No sube la
/// versión del ticket.
/// </summary>
public sealed class SupportSurveyAnswering(
    ISupportSurveyRepository surveys,
    ISupportTicketRepository tickets,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    IClock clock)
{
    private static readonly string[] SurveyFields = ["survey"];

    public async Task AnswerAsync(
        Guid tenantId,
        Guid surveyId,
        SupportSurveyRating rating,
        string? comment,
        SupportSurveyChannel via,
        CancellationToken cancellationToken)
    {
        SupportTicketId ticketId;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var survey = await surveys.LockAsync(surveyId, cancellationToken);
            if (survey is null || survey.TenantId != tenantId)
            {
                throw SupportNotFound.Survey();
            }

            var ticket = await tickets.FindAsync(tenantId, survey.TicketId, cancellationToken) ?? throw SupportNotFound.Survey();
            var now = clock.UtcNow;
            survey.Answer(ticket, rating, comment, via, null, null, now);
            ticketId = survey.TicketId;
            auditPublisher.PublishSystem(
                tenantId, SupportAuditActions.TicketSurveyAnswered, SupportAuditActions.Resources.Ticket, ticketId.ToString(),
                SurveyFields, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await realtime.TicketChangedAsync(tenantId, ticketId.Value, CancellationToken.None);
    }
}

/// <summary>Fase 4b (H19, H20): las reglas de la calificación y del comentario que llegan de la página pública.</summary>
internal static class SupportSurveyAnswerRules
{
    /// <summary>El nombre exacto de una de las tres; un número o el texto del botón no valen.</summary>
    public static IRuleBuilderOptions<T, string?> MustBeASurveyRating<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(rating => rating is not null && SupportPublicSurveyOptions.All.Contains(rating, StringComparer.Ordinal))
            .WithMessage("The rating is Good, Regular or Bad.");

    /// <summary>Opcional; hasta 1000 caracteres recortado, sin caracteres de control salvo el salto de línea.</summary>
    public static IRuleBuilderOptions<T, string?> MustBeASurveyComment<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(comment => comment is null
                || (comment.Trim().Length <= SupportTicketSurvey.CommentMaxLength
                    && !comment.Any(character => char.IsControl(character) && character is not ('\n' or '\r'))))
            .WithMessage($"The comment has at most {SupportTicketSurvey.CommentMaxLength} characters and no control characters other than line breaks.");

    /// <summary>Después del validador.</summary>
    public static SupportSurveyRating Parse(string? rating) =>
        Enum.Parse<SupportSurveyRating>(rating ?? throw new InvalidOperationException("The rating passed validation but is null."));
}
