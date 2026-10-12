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
}

/// <summary>§3.6 <c>GET /quick-replies</c>: todo asesor de la caja (<c>messaging.conversation.read</c>). Las variables se resuelven
/// para quien llama (D-Q5): su nombre de asesor por <see cref="CallerMembership"/> + <see cref="IMessagingMemberNames"/> y el del
/// tenant por <see cref="IMessagingTenantNames"/>; sin nombre, <c>""</c>.</summary>
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
        var items = await repository.ListAsync(query.TenantId, cancellationToken);
        var asesor = string.Empty;
        if (await callerMembership.FindAsync(query.TenantId, cancellationToken) is { } memberId)
        {
            // FindDisplayNameAsync y no FindAsync: {asesor} llega al cliente y nunca lleva el correo (decisión del owner, Task 4).
            asesor = await memberNames.FindDisplayNameAsync(query.TenantId, memberId, cancellationToken) ?? string.Empty;
        }

        var empresa = await tenantNames.FindAsync(query.TenantId, cancellationToken) ?? string.Empty;
        return new QuickReplyListDto(items.Select(QuickReplySupport.ToDto).ToArray(), new QuickReplyVariablesDto(asesor, empresa));
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
