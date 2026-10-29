using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

// Spec 2026-10-12 §3.3: las respuestas rápidas. Todo handler revalida tenant, permiso y módulo antes de tocar nada (403 con código,
// nunca 404 ante otro tenant) y valida después, así el 422 por campo no le cuenta a un extraño qué existe.

/// <summary>Lo que comparten crear y editar: el cuerpo de <c>SaveQuickReplyRequest</c> sin la versión.</summary>
public interface IQuickReplyInput
{
    string? Shortcut { get; }

    string? Body { get; }
}

/// <summary>Los textos de <c>errors</c>, tuteando. Fuera de <see cref="QuickReplyInputValidator{T}"/> porque un tipo genérico no
/// lleva miembros estáticos (CA1000).</summary>
public static class QuickReplyMessages
{
    public const string Shortcut = "El atajo lleva de 2 a 32 letras minúsculas, números o guiones, sin «/» ni espacios.";

    public const string Body = "Escribe una respuesta de 1 a 4096 caracteres.";

    public const string MissingVersion = "Falta la versión de la respuesta que estás viendo.";

    public const string MissingOrder = "Falta la lista de respuestas en el orden que elegiste.";

    public const string OrderTooLong = "El orden lleva a lo sumo 500 respuestas.";

    public static string UnknownVariable(string variable) => $"«{variable}» no es una variable. Usa {{nombre}}, {{asesor}} o {{empresa}}.";
}

/// <summary>
/// §3.3: las reglas de formato de <see cref="QuickReply"/> con la clave del cuerpo JSON (<c>shortcut</c>, <c>body</c>). Usa los mismos
/// métodos del agregado, así que no pueden divergir. Una variable desconocida también se marca acá: sin eso el 422 saldría con
/// <c>messaging.quick_reply.unknown_variable</c> y sin <c>errors</c>, y el formulario no sabría qué campo pintar.
/// </summary>
public abstract class QuickReplyInputValidator<T> : AbstractValidator<T>
    where T : IQuickReplyInput
{
    protected QuickReplyInputValidator()
    {
        RuleFor(input => input.Shortcut)
            .Must(shortcut => QuickReply.IsValidShortcut(QuickReply.NormalizeShortcut(shortcut)))
            .OverridePropertyName("shortcut").WithMessage(QuickReplyMessages.Shortcut);
        // Custom con ValidationFailure armado a mano: el mensaje lleva {nombre} literal, y WithMessage lo pasaría por el formateador
        // de FluentValidation, que trata {…} como placeholder.
        RuleFor(input => input.Body).Custom((body, context) =>
        {
            var trimmed = QuickReply.TrimBody(body);
            if (!QuickReply.IsValidBodyLength(trimmed))
            {
                context.AddFailure(new ValidationFailure("body", QuickReplyMessages.Body));
            }
            else if (QuickReply.FindUnknownVariable(trimmed) is { } unknown)
            {
                context.AddFailure(new ValidationFailure("body", QuickReplyMessages.UnknownVariable(unknown)));
            }
        });
    }
}

public sealed record ListQuickRepliesQuery(Guid TenantId) : IQuery<QuickReplyListDto>;

public sealed record CreateQuickReplyCommand(Guid TenantId, string? Shortcut, string? Body) : ICommand<QuickReplyDto>, IQuickReplyInput;

public sealed class CreateQuickReplyValidator : QuickReplyInputValidator<CreateQuickReplyCommand>;

internal static class QuickReplySupport
{
    public static QuickReplyDto ToDto(QuickReply quickReply) =>
        new(quickReply.Id, quickReply.Shortcut, quickReply.Body, quickReply.UpdatedAt, quickReply.Version);

    public static ResourceNotFoundException NotFound(Guid quickReplyId) =>
        new(MessagingErrorCodes.QuickReplyNotFound, $"Quick reply '{quickReplyId}' was not found.");

    public static RequestConcurrencyException Stale() =>
        new("concurrency.conflict", "The quick reply changed since it was loaded.");

    /// <summary>Spec 2026-10-29: la lista de <c>GET /quick-replies</c> para quien llama, en su orden personal (o por atajo si no tiene
    /// uno, o si no tiene membresía).</summary>
    public static async Task<QuickReplyListDto> ListForAsync(
        IQuickReplyRepository repository,
        IMessagingMemberNames memberNames,
        IMessagingTenantNames tenantNames,
        Guid tenantId,
        Guid? memberId,
        CancellationToken cancellationToken)
    {
        var items = await repository.ListAsync(tenantId, cancellationToken);
        var order = memberId is { } member ? await repository.FindOrderAsync(tenantId, member, cancellationToken) : [];
        return await ComposeAsync(memberNames, tenantNames, tenantId, memberId, QuickReplyOrder.Arrange(items, order), cancellationToken);
    }

    /// <summary>Las variables se resuelven para quien llama (D-Q5): su nombre de asesor por <see cref="IMessagingMemberNames"/> y el del
    /// tenant por <see cref="IMessagingTenantNames"/>; sin nombre, <c>""</c>.</summary>
    public static async Task<QuickReplyListDto> ComposeAsync(
        IMessagingMemberNames memberNames,
        IMessagingTenantNames tenantNames,
        Guid tenantId,
        Guid? memberId,
        IReadOnlyList<QuickReply> items,
        CancellationToken cancellationToken)
    {
        var asesor = string.Empty;
        if (memberId is { } member)
        {
            // FindDisplayNameAsync y no FindAsync: {asesor} llega al cliente y nunca lleva el correo (decisión del owner, Task 4).
            asesor = await memberNames.FindDisplayNameAsync(tenantId, member, cancellationToken) ?? string.Empty;
        }

        var empresa = await tenantNames.FindAsync(tenantId, cancellationToken) ?? string.Empty;
        return new QuickReplyListDto(items.Select(ToDto).ToArray(), new QuickReplyVariablesDto(asesor, empresa));
    }
}

/// <summary>§3.6 <c>GET /quick-replies</c>: todo asesor de la caja (<c>messaging.conversation.read</c>). Las variables se resuelven
/// para quien llama (D-Q5): su nombre de asesor por <see cref="CallerMembership"/> + <see cref="IMessagingMemberNames"/> y el del
/// tenant por <see cref="IMessagingTenantNames"/>; sin nombre, <c>""</c>. Spec 2026-10-29: los ítems van en el orden personal de quien
/// llama (<see cref="QuickReplyOrder.Arrange"/>); sin orden guardado o sin membresía, por atajo.</summary>
public sealed class ListQuickRepliesHandler(
    IQuickReplyRepository repository,
    CallerMembership callerMembership,
    IMessagingMemberNames memberNames,
    IMessagingTenantNames tenantNames,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<ListQuickRepliesQuery, QuickReplyListDto>
{
    public async Task<QuickReplyListDto> HandleAsync(ListQuickRepliesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        var memberId = await callerMembership.FindAsync(query.TenantId, cancellationToken);
        return await QuickReplySupport.ListForAsync(repository, memberNames, tenantNames, query.TenantId, memberId, cancellationToken);
    }
}

/// <summary>Spec 2026-10-29: el orden personal. <c>null</c> llega como 422 en <c>quickReplyIds</c>; la lista vacía es válida (vuelve al
/// orden por atajo).</summary>
public sealed record SaveQuickReplyOrderCommand(Guid TenantId, IReadOnlyList<Guid>? QuickReplyIds) : ICommand<QuickReplyListDto>;

public sealed class SaveQuickReplyOrderValidator : AbstractValidator<SaveQuickReplyOrderCommand>
{
    public SaveQuickReplyOrderValidator() =>
        RuleFor(command => command.QuickReplyIds)
            .NotNull().WithMessage(QuickReplyMessages.MissingOrder)
            .Must(ids => ids is null || ids.Count <= QuickReplyOrder.MaxItems).WithMessage(QuickReplyMessages.OrderTooLong)
            .OverridePropertyName("quickReplyIds");
}

/// <summary>Spec 2026-10-29 <c>PUT /quick-replies/order</c>: con el mismo permiso que leerlas (<c>messaging.conversation.read</c>), no el
/// de gestionarlas: cada asesor ordena sólo lo que él ve. Sin <c>If-Match</c> ni versión: es personal y gana la última escritura. Sin
/// membresía activa no hay de quién guardar el orden: 403, como el envío. Responde la lista ya ordenada, igual que el <c>GET</c>.</summary>
public sealed class SaveQuickReplyOrderHandler(
    IQuickReplyRepository repository,
    CallerMembership callerMembership,
    IMessagingMemberNames memberNames,
    IMessagingTenantNames tenantNames,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SaveQuickReplyOrderCommand> validator)
    : ICommandHandler<SaveQuickReplyOrderCommand, QuickReplyListDto>
{
    public async Task<QuickReplyListDto> HandleAsync(SaveQuickReplyOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        var memberId = await callerMembership.FindAsync(command.TenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var existing = await repository.ListAsync(command.TenantId, cancellationToken);
        var order = QuickReplyOrder.Normalize(command.QuickReplyIds!, existing.Select(reply => reply.Id));
        var now = clock.UtcNow;
        audit.RecordQuickReplyOrder(command.TenantId, executionContext.SubjectId, memberId, now);
        await repository.SaveOrderAsync(command.TenantId, memberId, order, now, cancellationToken);
        return await QuickReplySupport.ComposeAsync(
            memberNames, tenantNames, command.TenantId, memberId, QuickReplyOrder.Arrange(existing, order), cancellationToken);
    }
}

/// <summary>§3.6 <c>POST /quick-replies</c>: con <c>messaging.quick_reply.manage</c>. El atajo repetido lo traduce la unidad de trabajo
/// (422 <c>shortcut_taken</c>); la auditoría viaja en el mismo <c>SaveChanges</c>.</summary>
public sealed class CreateQuickReplyHandler(
    IQuickReplyRepository repository,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateQuickReplyCommand> validator)
    : ICommandHandler<CreateQuickReplyCommand, QuickReplyDto>
{
    public async Task<QuickReplyDto> HandleAsync(CreateQuickReplyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.QuickReplyManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var now = clock.UtcNow;
        var quickReply = QuickReply.Create(command.TenantId, command.Shortcut, command.Body, now);
        repository.Add(quickReply);
        audit.RecordQuickReply(command.TenantId, executionContext.SubjectId, MessagingAuditActions.QuickReplyCreated, quickReply.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return QuickReplySupport.ToDto(quickReply);
    }
}

public sealed record UpdateQuickReplyCommand(Guid TenantId, Guid QuickReplyId, string? Shortcut, string? Body, long? Version)
    : ICommand<QuickReplyDto>, IQuickReplyInput;

public sealed record DeleteQuickReplyCommand(Guid TenantId, Guid QuickReplyId, long? Version) : ICommand<bool>;

public sealed class UpdateQuickReplyValidator : QuickReplyInputValidator<UpdateQuickReplyCommand>
{
    public UpdateQuickReplyValidator() =>
        RuleFor(command => command.Version).Must(version => version is > 0)
            .OverridePropertyName("version").WithMessage(QuickReplyMessages.MissingVersion);
}

/// <summary>§3.6: la versión viaja en el query (<c>?version=</c>), un <c>DELETE</c> no lleva cuerpo.</summary>
public sealed class DeleteQuickReplyValidator : AbstractValidator<DeleteQuickReplyCommand>
{
    public DeleteQuickReplyValidator() =>
        RuleFor(command => command.Version).Must(version => version is > 0)
            .OverridePropertyName("version").WithMessage(QuickReplyMessages.MissingVersion);
}

/// <summary>§3.6 <c>PUT /quick-replies/{id}</c>: reemplaza atajo y cuerpo con la versión que se está viendo (412 si no). Un id que no
/// es del tenant es 404, igual que uno inexistente.</summary>
public sealed class UpdateQuickReplyHandler(
    IQuickReplyRepository repository,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateQuickReplyCommand> validator)
    : ICommandHandler<UpdateQuickReplyCommand, QuickReplyDto>
{
    public async Task<QuickReplyDto> HandleAsync(UpdateQuickReplyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.QuickReplyManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var quickReply = await repository.FindAsync(command.TenantId, command.QuickReplyId, cancellationToken)
            ?? throw QuickReplySupport.NotFound(command.QuickReplyId);
        if (quickReply.Version != command.Version)
        {
            throw QuickReplySupport.Stale();
        }

        var now = clock.UtcNow;
        quickReply.Update(command.Shortcut, command.Body, now);
        audit.RecordQuickReply(command.TenantId, executionContext.SubjectId, MessagingAuditActions.QuickReplyUpdated, quickReply.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return QuickReplySupport.ToDto(quickReply);
    }
}

/// <summary>§3.6 <c>DELETE /quick-replies/{id}?version=</c>: con la versión que se está viendo (412 si no; una carrera entre la
/// lectura y el commit también es 412, por el token de concurrencia).</summary>
public sealed class DeleteQuickReplyHandler(
    IQuickReplyRepository repository,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<DeleteQuickReplyCommand> validator)
    : ICommandHandler<DeleteQuickReplyCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteQuickReplyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.QuickReplyManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var quickReply = await repository.FindAsync(command.TenantId, command.QuickReplyId, cancellationToken)
            ?? throw QuickReplySupport.NotFound(command.QuickReplyId);
        if (quickReply.Version != command.Version)
        {
            throw QuickReplySupport.Stale();
        }

        repository.Remove(quickReply);
        audit.RecordQuickReply(command.TenantId, executionContext.SubjectId, MessagingAuditActions.QuickReplyDeleted, quickReply.Id, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
