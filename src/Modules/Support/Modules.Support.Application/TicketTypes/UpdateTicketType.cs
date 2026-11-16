using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record UpdateTicketTypeCommand(
    Guid TenantId,
    Guid TypeId,
    string Name,
    int? TermBusinessDays,
    bool? TermIsLegal,
    bool? RequiresFormalResponse,
    bool? IsPublished,
    bool? IsActive,
    string? FormalNextStep,
    long ExpectedVersion)
    : ICommand<SupportTicketTypeDto>
{
    public static UpdateTicketTypeCommand From(Guid tenantId, Guid typeId, TicketTypeRequest request, long expectedVersion) => new(
        tenantId,
        typeId,
        request.Name ?? string.Empty,
        request.TermBusinessDays,
        request.TermIsLegal,
        request.RequiresFormalResponse,
        request.IsPublished,
        request.IsActive,
        request.FormalNextStep,
        expectedVersion);
}

public sealed class UpdateTicketTypeValidator : AbstractValidator<UpdateTicketTypeCommand>
{
    public UpdateTicketTypeValidator()
    {
        RuleFor(command => command.Name).MustBeATypeName();
        RuleFor(command => command.TermBusinessDays).MustBeATerm();
        RuleFor(command => command.TermIsLegal).MustBeLegalOnlyWithTerm(command => command.TermBusinessDays);
        RuleFor(command => command.RequiresFormalResponse).NotNull();
        RuleFor(command => command.IsPublished).NotNull();
        RuleFor(command => command.IsActive).NotNull();
        RuleFor(command => command.FormalNextStep).MustBeAFormalNextStep();
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Edita el tipo entero (también desactivarlo: un tipo usado no se borra, se desactiva; <see cref="DeleteTicketTypeHandler"/>). No
/// mueve vencimientos ya calculados: el ticket guardó su foto del tipo (spec §7.2). Sin cambios, no sube la versión ni audita.
/// </summary>
public sealed class UpdateTicketTypeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ISupportProvisioner provisioner,
    ISupportTicketTypeRepository types,
    ISupportTicketRepository tickets,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    IValidator<UpdateTicketTypeCommand> validator)
    : ICommandHandler<UpdateTicketTypeCommand, SupportTicketTypeDto>
{
    public async Task<SupportTicketTypeDto> HandleAsync(UpdateTicketTypeCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);

        var type = await types.FindAsync(command.TenantId, new SupportTicketTypeId(command.TypeId), cancellationToken)
            ?? throw SupportNotFound.TicketType(command.TypeId);
        if (type.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The ticket type changed after it was loaded.");
        }

        var inUse = await tickets.AnyOfTypeAsync(command.TenantId, type.Id, cancellationToken);
        var before = SupportDtoMapping.ToDto(type, inUse);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        type.Update(
            command.Name,
            command.TermBusinessDays,
            command.TermIsLegal!.Value,
            command.RequiresFormalResponse!.Value,
            command.IsPublished!.Value,
            command.IsActive!.Value,
            command.FormalNextStep,
            now);
        if (type.Version != before.Version)
        {
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                SupportAuditActions.TicketTypeUpdated,
                SupportAuditActions.Resources.TicketType,
                type.Id.ToString(),
                "success",
                ChangedFields(before, SupportDtoMapping.ToDto(type, inUse)),
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return SupportDtoMapping.ToDto(type, inUse);
    }

    private static List<string> ChangedFields(SupportTicketTypeDto before, SupportTicketTypeDto after)
    {
        var fields = new List<string>();
        Add(fields, "name", before.Name != after.Name);
        Add(fields, "termBusinessDays", before.TermBusinessDays != after.TermBusinessDays);
        Add(fields, "termIsLegal", before.TermIsLegal != after.TermIsLegal);
        Add(fields, "requiresFormalResponse", before.RequiresFormalResponse != after.RequiresFormalResponse);
        Add(fields, "isPublished", before.IsPublished != after.IsPublished);
        Add(fields, "isActive", before.IsActive != after.IsActive);
        Add(fields, "formalNextStep", before.FormalNextStep != after.FormalNextStep);
        return fields;
    }

    private static void Add(List<string> fields, string name, bool changed)
    {
        if (changed)
        {
            fields.Add(name);
        }
    }
}
