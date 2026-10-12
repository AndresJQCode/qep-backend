using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

// Spec 2026-10-11 §6.2: las campañas. Todo handler revalida tenant, permiso (messaging.campaign.manage) y módulo antes de tocar nada
// (403 con código, nunca 404 ante otro tenant), y valida después, así el 422 por campo no le cuenta a un extraño qué existe.

/// <summary>Una variable de la plantilla como llega en el cuerpo: <c>{ "source": "text", "value": "…" }</c> o
/// <c>{ "source": "customerName" }</c>. Todo opcional: el validador marca la clave exacta.</summary>
public sealed record CampaignParameterInput(string? Source, string? Value);

/// <summary>El <c>CampaignInput</c> de crear y editar, que comparten su validador y su manera de armar el agregado.</summary>
public interface ICampaignInput
{
    string? Name { get; }

    Guid? ConnectionId { get; }

    string? TemplateName { get; }

    string? TemplateLanguage { get; }

    IReadOnlyDictionary<string, CampaignParameterInput?>? Parameters { get; }

    IReadOnlyList<Guid>? TagIds { get; }

    IReadOnlyList<Guid>? CustomerIds { get; }

    DateTimeOffset? ScheduledAt { get; }
}

public sealed record CreateCampaignCommand(
    Guid TenantId,
    string? Name,
    Guid? ConnectionId,
    string? TemplateName,
    string? TemplateLanguage,
    IReadOnlyDictionary<string, CampaignParameterInput?>? Parameters,
    IReadOnlyList<Guid>? TagIds,
    IReadOnlyList<Guid>? CustomerIds,
    DateTimeOffset? ScheduledAt) : ICommand<CampaignDetailDto>, ICampaignInput;

public sealed record UpdateCampaignCommand(
    Guid TenantId,
    Guid CampaignId,
    long? ExpectedVersion,
    string? Name,
    Guid? ConnectionId,
    string? TemplateName,
    string? TemplateLanguage,
    IReadOnlyDictionary<string, CampaignParameterInput?>? Parameters,
    IReadOnlyList<Guid>? TagIds,
    IReadOnlyList<Guid>? CustomerIds,
    DateTimeOffset? ScheduledAt) : ICommand<CampaignDetailDto>, ICampaignInput;

public sealed record DeleteCampaignCommand(Guid TenantId, Guid CampaignId) : ICommand<bool>;

public sealed record LaunchCampaignCommand(Guid TenantId, Guid CampaignId, long? ExpectedVersion) : ICommand<CampaignDetailDto>;

public sealed record CancelCampaignCommand(Guid TenantId, Guid CampaignId, long? ExpectedVersion) : ICommand<CampaignDetailDto>;

public sealed record ListCampaignsQuery(Guid TenantId, int? Page, int? PageSize) : IQuery<CampaignPageDto<CampaignSummaryDto>>;

public sealed record GetCampaignQuery(Guid TenantId, Guid CampaignId) : IQuery<CampaignDetailDto>;

public sealed record ListCampaignRecipientsQuery(Guid TenantId, Guid CampaignId, string? Status, int? Page, int? PageSize)
    : IQuery<CampaignPageDto<CampaignRecipientDto>>;

public sealed record PreviewAudienceQuery(Guid TenantId, IReadOnlyList<Guid>? TagIds, IReadOnlyList<Guid>? CustomerIds, string? TemplateCategory)
    : IQuery<AudiencePreviewDto>;

/// <summary>
/// Las reglas de <c>CampaignInput</c> (§6.2) con la clave del cuerpo JSON como clave de <c>errors</c> (camelCase, la anidada con punto;
/// <c>OverridePropertyName</c> como los demás validadores de Messaging): <c>name</c>, <c>connectionId</c>, <c>template.name</c>,
/// <c>template.language</c>, <c>parameters</c> y <c>parameters.&lt;clave&gt;</c>, <c>audience</c>, <c>audience.tagIds</c>,
/// <c>audience.customerIds</c> y <c>scheduledAt</c>. Que las claves de <c>parameters</c> sean las de la plantilla se comprueba después,
/// contra el catálogo.
/// </summary>
public abstract class CampaignInputValidator<T> : AbstractValidator<T>
    where T : ICampaignInput
{
    protected CampaignInputValidator(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        RuleFor(input => input.Name).NotEmpty().MaximumLength(Campaign.NameMaxLength)
            .OverridePropertyName("name").WithMessage($"Escribe un nombre de 1 a {Campaign.NameMaxLength} caracteres.");
        RuleFor(input => input.ConnectionId).NotNull().NotEqual(Guid.Empty)
            .OverridePropertyName("connectionId").WithMessage("Elige la conexión de WhatsApp.");
        RuleFor(input => input.TemplateName).NotEmpty().MaximumLength(SendTemplateMessageValidator.NameMaxLength)
            .OverridePropertyName("template.name").WithMessage("Elige una plantilla.");
        RuleFor(input => input.TemplateLanguage).NotEmpty().MaximumLength(SendTemplateMessageValidator.LanguageMaxLength)
            .OverridePropertyName("template.language").WithMessage("Elige una plantilla.");
        RuleFor(input => input.Parameters).Custom((parameters, context) =>
        {
            foreach (var failure in ParameterFailures(parameters))
            {
                context.AddFailure(failure);
            }
        });
        RuleFor(input => input).Custom((input, context) =>
        {
            foreach (var failure in AudienceFailures(input.TagIds, input.CustomerIds))
            {
                context.AddFailure(failure);
            }
        });
        RuleFor(input => input.ScheduledAt)
            .Must(at => at is null || (at.Value > clock.UtcNow && at.Value <= clock.UtcNow.Add(Campaign.MaxScheduleAhead)))
            .OverridePropertyName("scheduledAt").WithMessage("La fecha de envío tiene que estar en el futuro y a no más de 30 días.");
    }

    internal static IEnumerable<ValidationFailure> ParameterFailures(IReadOnlyDictionary<string, CampaignParameterInput?>? parameters)
    {
        if (parameters is null)
        {
            yield return new ValidationFailure("parameters", "Completa las variables de la plantilla.");
            yield break;
        }

        foreach (var (key, input) in parameters)
        {
            if (input is null || !CampaignParameter.TryParseSource(input.Source, out var source))
            {
                yield return new ValidationFailure(TemplateParameterRules.KeyPrefix + key, $"Elige de dónde sale «{key}»: un texto fijo o el nombre del cliente.");
            }
            else if (source == CampaignParameterSource.Text && !TemplateParameterRules.IsValid(input.Value))
            {
                yield return new ValidationFailure(
                    TemplateParameterRules.KeyPrefix + key,
                    $"El valor de «{key}» debe tener de 1 a {TemplateParameterRules.MaxValueLength} caracteres, sin saltos de línea ni tabulaciones ni más de {TemplateParameterRules.MaxConsecutiveSpaces} espacios seguidos.");
            }
        }
    }

    internal static IEnumerable<ValidationFailure> AudienceFailures(IReadOnlyList<Guid>? tagIds, IReadOnlyList<Guid>? customerIds)
    {
        var tags = tagIds ?? [];
        var customers = customerIds ?? [];
        if (tags.Contains(Guid.Empty))
        {
            yield return new ValidationFailure("audience.tagIds", "Hay una etiqueta que no es válida.");
        }

        if (customers.Contains(Guid.Empty))
        {
            yield return new ValidationFailure("audience.customerIds", "Hay un cliente que no es válido.");
        }

        var distinctCustomers = customers.Distinct().Count();
        if (distinctCustomers > Campaign.MaxLooseCustomers)
        {
            yield return new ValidationFailure("audience.customerIds", $"Elige hasta {Campaign.MaxLooseCustomers} clientes sueltos; para más, usa etiquetas.");
        }

        if (tags.Count == 0 && distinctCustomers == 0)
        {
            yield return new ValidationFailure("audience", "Elige al menos una etiqueta o un cliente.");
        }
    }
}

public sealed class CreateCampaignValidator(IClock clock) : CampaignInputValidator<CreateCampaignCommand>(clock);

public sealed class UpdateCampaignValidator : CampaignInputValidator<UpdateCampaignCommand>
{
    public UpdateCampaignValidator(IClock clock)
        : base(clock)
    {
        RuleFor(command => command.ExpectedVersion).Must(version => version is > 0).OverridePropertyName("expectedVersion").WithMessage("Falta la versión que estás editando.");
    }
}

public sealed class LaunchCampaignValidator : AbstractValidator<LaunchCampaignCommand>
{
    public LaunchCampaignValidator() =>
        RuleFor(command => command.ExpectedVersion).Must(version => version is > 0).OverridePropertyName("expectedVersion").WithMessage("Falta la versión que estás viendo.");
}

public sealed class CancelCampaignValidator : AbstractValidator<CancelCampaignCommand>
{
    public CancelCampaignValidator() =>
        RuleFor(command => command.ExpectedVersion).Must(version => version is > 0).OverridePropertyName("expectedVersion").WithMessage("Falta la versión que estás viendo.");
}

/// <summary>page ≥ 1, pageSize 1–100.</summary>
public sealed class ListCampaignsValidator : AbstractValidator<ListCampaignsQuery>
{
    public const int MaxPageSize = 100;

    public ListCampaignsValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1).When(query => query.Page is not null).OverridePropertyName("page");
        RuleFor(query => query.PageSize).InclusiveBetween(1, MaxPageSize).When(query => query.PageSize is not null).OverridePropertyName("pageSize");
    }
}

/// <summary><c>status</c> es el nombre de un estado de destinatario; page ≥ 1, pageSize 1–100.</summary>
public sealed class ListCampaignRecipientsValidator : AbstractValidator<ListCampaignRecipientsQuery>
{
    public ListCampaignRecipientsValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => status is null || Enum.TryParse<CampaignRecipientStatus>(status, ignoreCase: false, out _))
            .OverridePropertyName("status").WithMessage("status must be Pending, Sending, Sent, Failed or Skipped.");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1).When(query => query.Page is not null).OverridePropertyName("page");
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListCampaignsValidator.MaxPageSize).When(query => query.PageSize is not null).OverridePropertyName("pageSize");
    }
}

/// <summary>El cuerpo del preview es plano (<c>tagIds</c>, <c>customerIds</c>, <c>templateCategory</c>), así que sus claves también.</summary>
public sealed class PreviewAudienceValidator : AbstractValidator<PreviewAudienceQuery>
{
    public PreviewAudienceValidator()
    {
        RuleFor(query => query.TagIds).Must(ids => ids is null || !ids.Contains(Guid.Empty)).OverridePropertyName("tagIds");
        RuleFor(query => query.CustomerIds)
            .Must(ids => ids is null || (!ids.Contains(Guid.Empty) && ids.Distinct().Count() <= Campaign.MaxLooseCustomers))
            .OverridePropertyName("customerIds");
        RuleFor(query => query.TemplateCategory).NotEmpty().MaximumLength(32).OverridePropertyName("templateCategory");
    }
}

/// <summary>Lo que comparten crear, editar y lanzar: la plantilla contra el catálogo (§5.2) y la manera de armar las variables.</summary>
internal static class CampaignSupport
{
    /// <summary>Aprobada, enviable y con las claves exactas: <c>422 messaging.template.not_available</c> o
    /// <c>messaging.template.parameters_mismatch</c>. Una conexión que no es del tenant o no está activa lanza
    /// <c>messaging.connection_unavailable</c> desde el catálogo.</summary>
    public static async Task<MessageTemplateDto> ResolveTemplateAsync(
        IMessageTemplates templates, Guid tenantId, Guid connectionId, string name, string language, IEnumerable<string> parameterKeys, CancellationToken cancellationToken)
    {
        var catalog = await templates.ListAsync(tenantId, connectionId, refresh: false, cancellationToken);
        return TemplateCatalog.RequireSendable(catalog, name, language, parameterKeys);
    }

    public static Dictionary<string, CampaignParameter> ToParameters(IReadOnlyDictionary<string, CampaignParameterInput?> input)
    {
        var result = new Dictionary<string, CampaignParameter>(StringComparer.Ordinal);
        foreach (var (key, value) in input)
        {
            // El validador ya garantizó que la fuente existe y que un texto trae valor.
            _ = CampaignParameter.TryParseSource(value!.Source, out var source);
            result[key] = source == CampaignParameterSource.Text ? CampaignParameter.Text(value.Value!) : CampaignParameter.CustomerName;
        }

        return result;
    }

    public static RequestConcurrencyException Stale() =>
        new("concurrency.conflict", "The campaign changed since it was loaded.");

    public static ResourceNotFoundException NotFound(Guid campaignId) =>
        new(MessagingErrorCodes.CampaignNotFound, $"Campaign '{campaignId}' was not found.");

    public static ResourceConflictException NotEditable() =>
        new(MessagingErrorCodes.CampaignNotEditable, "Only a draft campaign can be edited, deleted or launched.");

    public static ResourceConflictException NotCancellable() =>
        new(MessagingErrorCodes.CampaignNotCancellable, "Only a scheduled or sending campaign can be cancelled.");

    public static async Task<CampaignDetailDto> DetailAsync(
        ICampaignQueries queries, CampaignMapper mapper, Guid tenantId, Guid campaignId, CancellationToken cancellationToken)
    {
        var row = await queries.FindAsync(tenantId, campaignId, cancellationToken) ?? throw NotFound(campaignId);
        return await mapper.DetailAsync(tenantId, row, cancellationToken);
    }
}

/// <summary>§6.2 <c>POST /</c>: un borrador a nombre de quien llama (su membresía activa es el <c>created_by_member_id</c> con el que el
/// worker enviará).</summary>
public sealed class CreateCampaignHandler(
    ICampaignRepository repository,
    ICampaignQueries queries,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    IMessageTemplates templates,
    CampaignMapper mapper,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateCampaignCommand> validator)
    : ICommandHandler<CreateCampaignCommand, CampaignDetailDto>
{
    public async Task<CampaignDetailDto> HandleAsync(CreateCampaignCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.CampaignManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var member = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");

        var parameters = CampaignSupport.ToParameters(command.Parameters!);
        var template = await CampaignSupport.ResolveTemplateAsync(
            templates, command.TenantId, command.ConnectionId!.Value, command.TemplateName!, command.TemplateLanguage!, parameters.Keys, cancellationToken);
        var now = clock.UtcNow;
        var campaign = Campaign.Create(
            Guid.CreateVersion7(), command.TenantId, command.ConnectionId.Value, command.Name!, template.Name, template.Language, template.Category,
            parameters, command.TagIds ?? [], command.CustomerIds ?? [], command.ScheduledAt, member, now);
        repository.Add(campaign);
        audit.RecordCampaign(command.TenantId, executionContext.SubjectId, bySystem: false, MessagingAuditActions.CampaignCreated, campaign.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await CampaignSupport.DetailAsync(queries, mapper, command.TenantId, campaign.Id, cancellationToken);
    }
}

/// <summary>§6.2 <c>PUT /{id}</c>: sólo en <c>Draft</c> (409) y con la versión que se está viendo (412).</summary>
public sealed class UpdateCampaignHandler(
    ICampaignRepository repository,
    ICampaignQueries queries,
    IMessagingUnitOfWork unitOfWork,
    IMessageTemplates templates,
    CampaignMapper mapper,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateCampaignCommand> validator)
    : ICommandHandler<UpdateCampaignCommand, CampaignDetailDto>
{
    public async Task<CampaignDetailDto> HandleAsync(UpdateCampaignCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.CampaignManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var campaign = await repository.FindAsync(command.TenantId, command.CampaignId, cancellationToken) ?? throw CampaignSupport.NotFound(command.CampaignId);
        if (!campaign.IsEditable)
        {
            throw CampaignSupport.NotEditable();
        }

        if (campaign.Version != command.ExpectedVersion)
        {
            throw CampaignSupport.Stale();
        }

        var parameters = CampaignSupport.ToParameters(command.Parameters!);
        var template = await CampaignSupport.ResolveTemplateAsync(
            templates, command.TenantId, command.ConnectionId!.Value, command.TemplateName!, command.TemplateLanguage!, parameters.Keys, cancellationToken);
        campaign.Update(
            command.Name!, command.ConnectionId.Value, template.Name, template.Language, template.Category, parameters,
            command.TagIds ?? [], command.CustomerIds ?? [], command.ScheduledAt, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await CampaignSupport.DetailAsync(queries, mapper, command.TenantId, campaign.Id, cancellationToken);
    }
}

/// <summary>§6.2 <c>DELETE /{id}</c>: sólo un borrador (409). Sin versión: borrar lo que ya no está es lo mismo que haberlo borrado.</summary>
public sealed class DeleteCampaignHandler(
    ICampaignRepository repository,
    IMessagingUnitOfWork unitOfWork,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : ICommandHandler<DeleteCampaignCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteCampaignCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.CampaignManage, cancellationToken);
        var campaign = await repository.FindAsync(command.TenantId, command.CampaignId, cancellationToken) ?? throw CampaignSupport.NotFound(command.CampaignId);
        if (!campaign.IsEditable)
        {
            throw CampaignSupport.NotEditable();
        }

        repository.Remove(campaign);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>§6.2 <c>POST /{id}/launch</c>: con <c>scheduledAt</c> futuro queda <c>Scheduled</c>; sin él, <c>Sending</c>. La plantilla se
/// vuelve a validar contra el catálogo —pudo pausarse desde que se guardó el borrador—, y una fecha que ya pasó se corrige en el
/// borrador (422 en <c>scheduledAt</c>): no se manda de golpe lo que alguien había agendado.</summary>
public sealed class LaunchCampaignHandler(
    ICampaignRepository repository,
    ICampaignQueries queries,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    IMessageTemplates templates,
    CampaignMapper mapper,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<LaunchCampaignCommand> validator)
    : ICommandHandler<LaunchCampaignCommand, CampaignDetailDto>
{
    public async Task<CampaignDetailDto> HandleAsync(LaunchCampaignCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.CampaignManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var campaign = await repository.FindAsync(command.TenantId, command.CampaignId, cancellationToken) ?? throw CampaignSupport.NotFound(command.CampaignId);
        if (!campaign.IsEditable)
        {
            throw CampaignSupport.NotEditable();
        }

        if (campaign.Version != command.ExpectedVersion)
        {
            throw CampaignSupport.Stale();
        }

        var now = clock.UtcNow;
        if (campaign.ScheduledAt is { } scheduledAt && scheduledAt <= now)
        {
            throw new ValidationException([new ValidationFailure("scheduledAt", "La fecha de envío ya pasó. Edita el borrador y elige otra.")]);
        }

        var template = await CampaignSupport.ResolveTemplateAsync(
            templates, command.TenantId, campaign.ConnectionId, campaign.TemplateName, campaign.TemplateLanguage, campaign.Parameters.Keys, cancellationToken);
        campaign.UseLiveTemplateCategory(template.Category);
        campaign.Launch(now);
        audit.RecordCampaign(command.TenantId, executionContext.SubjectId, bySystem: false, MessagingAuditActions.CampaignLaunched, campaign.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await CampaignSupport.DetailAsync(queries, mapper, command.TenantId, campaign.Id, cancellationToken);
    }
}

/// <summary>§6.2 <c>POST /{id}/cancel</c>: <c>Scheduled</c> o <c>Sending</c> (409 si no). Los pendientes pasan a <c>Skipped</c> en la
/// misma transacción; lo que ya está en vuelo termina.</summary>
public sealed class CancelCampaignHandler(
    ICampaignRepository repository,
    ICampaignQueries queries,
    IMessagingAuditRecorder audit,
    CampaignMapper mapper,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CancelCampaignCommand> validator)
    : ICommandHandler<CancelCampaignCommand, CampaignDetailDto>
{
    public async Task<CampaignDetailDto> HandleAsync(CancelCampaignCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.CampaignManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var campaign = await repository.FindAsync(command.TenantId, command.CampaignId, cancellationToken) ?? throw CampaignSupport.NotFound(command.CampaignId);
        if (!campaign.IsCancellable)
        {
            throw CampaignSupport.NotCancellable();
        }

        if (campaign.Version != command.ExpectedVersion)
        {
            throw CampaignSupport.Stale();
        }

        var now = clock.UtcNow;
        campaign.Cancel(now);
        audit.RecordCampaign(command.TenantId, executionContext.SubjectId, bySystem: false, MessagingAuditActions.CampaignCancelled, campaign.Id, now);
        await repository.SaveCancelledAsync(campaign, cancellationToken);
        return await CampaignSupport.DetailAsync(queries, mapper, command.TenantId, campaign.Id, cancellationToken);
    }
}

/// <summary>§6.2 <c>GET /?page=&amp;pageSize=</c>: de la más nueva a la más vieja.</summary>
public sealed class ListCampaignsHandler(
    ICampaignQueries queries,
    CampaignMapper mapper,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<ListCampaignsQuery> validator)
    : IQueryHandler<ListCampaignsQuery, CampaignPageDto<CampaignSummaryDto>>
{
    public const int DefaultPageSize = 25;

    public async Task<CampaignPageDto<CampaignSummaryDto>> HandleAsync(ListCampaignsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.CampaignManage, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;
        var (rows, total) = await queries.ListAsync(query.TenantId, page, pageSize, cancellationToken);
        return new CampaignPageDto<CampaignSummaryDto>(await mapper.SummariesAsync(query.TenantId, rows, cancellationToken), total, page, pageSize);
    }
}

/// <summary>§6.2 <c>GET /{id}</c>.</summary>
public sealed class GetCampaignHandler(
    ICampaignQueries queries,
    CampaignMapper mapper,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetCampaignQuery, CampaignDetailDto>
{
    public async Task<CampaignDetailDto> HandleAsync(GetCampaignQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.CampaignManage, cancellationToken);
        return await CampaignSupport.DetailAsync(queries, mapper, query.TenantId, query.CampaignId, cancellationToken);
    }
}

/// <summary>§6.2 <c>GET /{id}/recipients?status=&amp;page=&amp;pageSize=</c>.</summary>
public sealed class ListCampaignRecipientsHandler(
    ICampaignQueries queries,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<ListCampaignRecipientsQuery> validator)
    : IQueryHandler<ListCampaignRecipientsQuery, CampaignPageDto<CampaignRecipientDto>>
{
    public const int DefaultPageSize = 50;

    public async Task<CampaignPageDto<CampaignRecipientDto>> HandleAsync(ListCampaignRecipientsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.CampaignManage, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        _ = await queries.FindAsync(query.TenantId, query.CampaignId, cancellationToken) ?? throw CampaignSupport.NotFound(query.CampaignId);
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;
        CampaignRecipientStatus? status = query.Status is null ? null : Enum.Parse<CampaignRecipientStatus>(query.Status);
        var (rows, total) = await queries.ListRecipientsAsync(query.TenantId, query.CampaignId, status, page, pageSize, cancellationToken);
        return new CampaignPageDto<CampaignRecipientDto>([.. rows.Select(CampaignMapper.Recipient)], total, page, pageSize);
    }
}

/// <summary>§6.2 <c>POST /audience-preview</c>: a cuántos les llegaría hoy. Cubos excluyentes de Customers (inactivo, luego sin WhatsApp,
/// luego opt-out): <c>reachable</c> resta el opt-out sólo si la plantilla es <c>MARKETING</c> (decisión 10).</summary>
public sealed class PreviewAudienceHandler(
    IMessagingCustomerDirectory customers,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<PreviewAudienceQuery> validator)
    : IQueryHandler<PreviewAudienceQuery, AudiencePreviewDto>
{
    public const string MarketingCategory = "MARKETING";

    public async Task<AudiencePreviewDto> HandleAsync(PreviewAudienceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.CampaignManage, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        IReadOnlyList<Guid> tags = [.. (query.TagIds ?? []).Distinct()];
        IReadOnlyList<Guid> loose = [.. (query.CustomerIds ?? []).Distinct()];
        if (tags.Count == 0 && loose.Count == 0)
        {
            return new AudiencePreviewDto(0, 0, 0, 0, 0);
        }

        var count = await customers.CountAudienceAsync(query.TenantId, tags, loose, cancellationToken);
        var subtractsOptOut = string.Equals(query.TemplateCategory, MarketingCategory, StringComparison.OrdinalIgnoreCase);
        var reachable = Math.Max(0, count.Total - count.Inactive - count.WithoutWhatsApp - (subtractsOptOut ? count.OptedOut : 0));
        return new AudiencePreviewDto(count.Total, reachable, count.OptedOut, count.Inactive, count.WithoutWhatsApp);
    }
}
