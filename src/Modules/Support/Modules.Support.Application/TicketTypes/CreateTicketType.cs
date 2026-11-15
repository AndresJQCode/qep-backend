using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>El cuerpo de alta y edición de un tipo, todo anulable para que su ausencia sea un <c>validation.failed</c> con el campo y
/// no un 400 del binding. El alta ignora <c>IsActive</c>: un tipo nace activo.</summary>
public sealed record TicketTypeRequest(
    string? Name,
    int? TermBusinessDays,
    bool? TermIsLegal,
    bool? RequiresFormalResponse,
    bool? IsPublished,
    bool? IsActive);

public sealed record CreateTicketTypeCommand(
    Guid TenantId,
    string Name,
    int? TermBusinessDays,
    bool? TermIsLegal,
    bool? RequiresFormalResponse,
    bool? IsPublished)
    : ICommand<SupportTicketTypeDto>
{
    public static CreateTicketTypeCommand From(Guid tenantId, TicketTypeRequest request) => new(
        tenantId,
        request.Name ?? string.Empty,
        request.TermBusinessDays,
        request.TermIsLegal,
        request.RequiresFormalResponse,
        request.IsPublished);
}

public sealed class CreateTicketTypeValidator : AbstractValidator<CreateTicketTypeCommand>
{
    public CreateTicketTypeValidator()
    {
        RuleFor(command => command.Name).MustBeATypeName();
        RuleFor(command => command.TermBusinessDays).MustBeATerm();
        RuleFor(command => command.TermIsLegal).MustBeLegalOnlyWithTerm(command => command.TermBusinessDays);
        RuleFor(command => command.RequiresFormalResponse).NotNull();
        RuleFor(command => command.IsPublished).NotNull();
    }
}

/// <summary>El tipo nuevo va al final de la lista (<c>Position</c> = la mayor + 1) y activo. El nombre repetido lo frena el índice
/// <c>IX_ticket_types_tenant_name</c> (409 <c>support.ticket_type.name_taken</c>), no una consulta previa.</summary>
public sealed class CreateTicketTypeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IClock clock,
    ISupportProvisioner provisioner,
    ISupportTicketTypeRepository types,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    IValidator<CreateTicketTypeCommand> validator)
    : ICommandHandler<CreateTicketTypeCommand, SupportTicketTypeDto>
{
    private static readonly string[] CreatedFields =
        ["name", "termBusinessDays", "termIsLegal", "requiresFormalResponse", "isPublished"];

    public async Task<SupportTicketTypeDto> HandleAsync(CreateTicketTypeCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);

        var existing = await types.ListAsync(command.TenantId, cancellationToken);
        var now = clock.UtcNow;
        var type = SupportTicketType.Create(
            SupportTicketTypeId.New(),
            command.TenantId,
            command.Name,
            command.TermBusinessDays,
            command.TermIsLegal!.Value,
            command.RequiresFormalResponse!.Value,
            command.IsPublished!.Value,
            existing.Count == 0 ? 1 : existing.Max(item => item.Position) + 1,
            now);
        types.Add(type);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupportAuditActions.TicketTypeCreated,
            SupportAuditActions.Resources.TicketType,
            type.Id.ToString(),
            "success",
            CreatedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SupportDtoMapping.ToDto(type);
    }
}
