using FluentValidation;
using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

// Spec 2026-10-15: el motivo de resolución. La elección (D-R4) la usa el resolver; el catálogo (§3.4) lo agrega la Task 6.

/// <summary>Los textos de <c>errors</c>, tuteando.</summary>
public static class ResolutionReasonMessages
{
    public const string Name = "Escribe un motivo de 1 a 60 caracteres.";

    public const string NameTooLong = "El motivo puede tener hasta 60 caracteres.";

    public const string Both = "Elige un motivo de la lista o escribe uno nuevo, no los dos.";
}

internal static class ResolutionReasonSupport
{
    public static ResourceNotFoundException NotFound(Guid reasonId) =>
        new(MessagingErrorCodes.ResolutionReasonNotFound, $"Resolution reason '{reasonId}' was not found.");
}

/// <summary>El motivo que llevará la resolución. <see cref="IsNew"/>: todavía no existe y el resolver lo crea con este id.</summary>
internal sealed record ChosenReason(Guid Id, string Name, bool IsNew);

internal static class ResolutionReasonChoice
{
    /// <summary>D-R4. <c>reasonId</c> tiene que estar vigente en el tenant (404 si no). <c>reasonName</c> en blanco es «sin motivo»; si
    /// coincide con uno vigente (lower de Postgres, recortado) lo reutiliza; si no, es uno nuevo. No cuenta el tope: eso lo hace el
    /// handler sólo si de verdad va a crear, porque reutilizar nunca choca con el tope (D-R9).</summary>
    public static async Task<ChosenReason?> ChooseAsync(
        IResolutionReasonRepository reasons, Guid tenantId, Guid? reasonId, string? reasonName, CancellationToken cancellationToken)
    {
        if (reasonId is { } id)
        {
            var existing = await reasons.FindActiveAsync(tenantId, id, cancellationToken) ?? throw ResolutionReasonSupport.NotFound(id);
            return new ChosenReason(existing.Id, existing.Name, IsNew: false);
        }

        var trimmed = ResolutionReason.TrimName(reasonName);
        if (trimmed.Length == 0)
        {
            return null;
        }

        var byName = await reasons.FindActiveByNameAsync(tenantId, trimmed, cancellationToken);
        return byName is null ? new ChosenReason(Guid.CreateVersion7(), trimmed, IsNew: true) : new ChosenReason(byName.Id, byName.Name, IsNew: false);
    }
}

/// <summary>Spec 2026-10-15 §3.4: un motivo del catálogo. La versión viaja para el <c>If-Match</c> del renombrar.</summary>
public sealed record ResolutionReasonDto(Guid Id, string Name, long Version);

public sealed record ListResolutionReasonsQuery(Guid TenantId) : IQuery<IReadOnlyList<ResolutionReasonDto>>;

public sealed record RenameResolutionReasonCommand(Guid TenantId, Guid ReasonId, string? Name, long ExpectedVersion) : ICommand<ResolutionReasonDto>;

public sealed record ArchiveResolutionReasonCommand(Guid TenantId, Guid ReasonId) : ICommand<bool>;

/// <summary>Texto libre: el validador da <c>errors.name</c>, que el dominio (<c>name_required</c>, <c>name_too_long</c>) no puede dar.</summary>
public sealed class RenameResolutionReasonValidator : AbstractValidator<RenameResolutionReasonCommand>
{
    public RenameResolutionReasonValidator() =>
        RuleFor(command => command.Name)
            .Must(name => ResolutionReason.IsValidName(ResolutionReason.TrimName(name)))
            .OverridePropertyName("name").WithMessage(ResolutionReasonMessages.Name);
}

/// <summary>§3.4 <c>GET /resolution-reasons</c>: todo asesor de la caja. D-R10: completa, sin paginar, por nombre.</summary>
public sealed class ListResolutionReasonsHandler(
    IResolutionReasonRepository reasons, ITenantModules tenantModules, IExecutionContext executionContext)
    : IQueryHandler<ListResolutionReasonsQuery, IReadOnlyList<ResolutionReasonDto>>
{
    public async Task<IReadOnlyList<ResolutionReasonDto>> HandleAsync(ListResolutionReasonsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        return (await reasons.ListActiveAsync(query.TenantId, cancellationToken))
            .Select(reason => new ResolutionReasonDto(reason.Id, reason.Name, reason.Version)).ToArray();
    }
}

/// <summary>§3.4 <c>PATCH /resolution-reasons/{id}</c> con <c>If-Match</c>: archivado o ajeno es 404; versión vieja, 412; nombre de otro
/// vigente, 409 (la unidad de trabajo, por nombre de índice). D-R6: el historial pasado muestra el nombre nuevo.</summary>
public sealed class RenameResolutionReasonHandler(
    IResolutionReasonRepository reasons,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<RenameResolutionReasonCommand> validator)
    : ICommandHandler<RenameResolutionReasonCommand, ResolutionReasonDto>
{
    public async Task<ResolutionReasonDto> HandleAsync(RenameResolutionReasonCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ResolutionReasonManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var reason = await reasons.FindActiveAsync(command.TenantId, command.ReasonId, cancellationToken)
            ?? throw ResolutionReasonSupport.NotFound(command.ReasonId);
        if (reason.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The resolution reason changed since it was loaded.");
        }

        var now = clock.UtcNow;
        reason.Rename(command.Name, now);
        audit.RecordResolutionReason(command.TenantId, executionContext.SubjectId, MessagingAuditActions.ResolutionReasonRenamed, reason.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ResolutionReasonDto(reason.Id, reason.Name, reason.Version);
    }
}

/// <summary>§3.4 <c>DELETE /resolution-reasons/{id}</c>: archiva (D-R5). Ya archivado o ajeno es 404. Sin <c>If-Match</c>: archivar dos
/// veces o archivar un nombre recién corregido deja el mismo resultado, y la pantalla confirma antes.</summary>
public sealed class ArchiveResolutionReasonHandler(
    IResolutionReasonRepository reasons,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ArchiveResolutionReasonCommand, bool>
{
    public async Task<bool> HandleAsync(ArchiveResolutionReasonCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ResolutionReasonManage, cancellationToken);
        var reason = await reasons.FindActiveAsync(command.TenantId, command.ReasonId, cancellationToken)
            ?? throw ResolutionReasonSupport.NotFound(command.ReasonId);

        var now = clock.UtcNow;
        reason.Archive(now);
        audit.RecordResolutionReason(command.TenantId, executionContext.SubjectId, MessagingAuditActions.ResolutionReasonArchived, reason.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
