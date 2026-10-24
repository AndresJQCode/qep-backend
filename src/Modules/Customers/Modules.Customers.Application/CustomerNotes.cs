using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

// Spec 2026-10-11 §4.2: las notas internas de un cliente. Las cuatro operaciones comparten el armado del DTO.

public sealed record ListCustomerNotesQuery(Guid TenantId, Guid CustomerId) : IQuery<IReadOnlyList<CustomerNoteDto>>;

public sealed record CreateCustomerNoteCommand(Guid TenantId, Guid CustomerId, string Body) : ICommand<CustomerNoteDto>;

public sealed record UpdateCustomerNoteCommand(
    Guid TenantId, Guid CustomerId, Guid NoteId, string Body, long ExpectedVersion) : ICommand<CustomerNoteDto>;

public sealed record DeleteCustomerNoteCommand(Guid TenantId, Guid CustomerId, Guid NoteId)
    : ICommand<CustomerNoteDeletedResult>;

// BuildingBlocks no tiene un ICommand sin resultado: el endpoint responde 204 y no lo mira.
public sealed record CustomerNoteDeletedResult(bool Deleted);

// El dominio valida lo mismo y daría un 422 con un solo código; el validador existe para que la respuesta lleve el
// mapa `errors` por campo, que es lo único que el formulario sabe leer.
public sealed class CreateCustomerNoteValidator : AbstractValidator<CreateCustomerNoteCommand>
{
    public CreateCustomerNoteValidator()
    {
        RuleFor(command => command.Body).NotEmpty().MaximumLength(CustomerNote.BodyMaxLength);
    }
}

public sealed class UpdateCustomerNoteValidator : AbstractValidator<UpdateCustomerNoteCommand>
{
    public UpdateCustomerNoteValidator()
    {
        RuleFor(command => command.Body).NotEmpty().MaximumLength(CustomerNote.BodyMaxLength);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

internal static class CustomerNoteMapping
{
    private const string DeletedUser = "Usuario eliminado";

    /// <summary>Un solo lookup de nombres para toda la lista. <c>CanEdit</c>: el autor con CustomerManage.
    /// <c>CanDelete</c>: ese mismo autor, o quien modera.</summary>
    public static async Task<IReadOnlyList<CustomerNoteDto>> ToDtosAsync(
        IReadOnlyCollection<CustomerNote> notes,
        ICustomerAuthorNames authorNames,
        IExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        if (notes.Count == 0)
        {
            return [];
        }

        var names = await authorNames.FindAsync(
            executionContext.TenantId.Value,
            notes.Select(note => note.AuthorUserId).Distinct().ToArray(),
            cancellationToken);
        var canManage = executionContext.HasPermission(CustomersPermissions.CustomerManage);
        var canModerate = executionContext.HasPermission(CustomersPermissions.NoteModerate);

        return notes
            .Select(note =>
            {
                var own = canManage && note.IsAuthoredBy(executionContext.SubjectId);
                return new CustomerNoteDto(
                    note.Id.Value,
                    note.Body,
                    new NoteAuthorDto(
                        note.AuthorUserId,
                        names.TryGetValue(note.AuthorUserId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : DeletedUser),
                    note.CreatedAt,
                    note.UpdatedAt,
                    own,
                    own || canModerate,
                    note.Version);
            })
            .ToArray();
    }
}

public sealed class ListCustomerNotesHandler(
    ICustomerRepository customers,
    ICustomerNoteRepository notes,
    ICustomerAuthorNames authorNames,
    IExecutionContext executionContext)
    : IQueryHandler<ListCustomerNotesQuery, IReadOnlyList<CustomerNoteDto>>
{
    public async Task<IReadOnlyList<CustomerNoteDto>> HandleAsync(
        ListCustomerNotesQuery query, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, query.TenantId, CustomersPermissions.CustomerRead);
        _ = await customers.FindAsync(query.TenantId, new CustomerId(query.CustomerId), cancellationToken)
            ?? throw CustomerNotFound.For(query.CustomerId);

        var list = await notes.ListByCustomerAsync(query.TenantId, new CustomerId(query.CustomerId), cancellationToken);
        return await CustomerNoteMapping.ToDtosAsync(list, authorNames, executionContext, cancellationToken);
    }
}

public sealed class CreateCustomerNoteHandler(
    ICustomerRepository customers,
    ICustomerNoteRepository notes,
    ICustomerAuthorNames authorNames,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateCustomerNoteCommand> validator)
    : ICommandHandler<CreateCustomerNoteCommand, CustomerNoteDto>
{
    public async Task<CustomerNoteDto> HandleAsync(CreateCustomerNoteCommand command, CancellationToken cancellationToken)
    {
        // Autorizar antes de validar: quien tiene el permiso en otro tenant no se lleva el mapa de errores.
        CustomersAuthorization.EnsureAuthorized(executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var customerId = new CustomerId(command.CustomerId);
        var customer = await customers.FindAsync(command.TenantId, customerId, cancellationToken)
            ?? throw CustomerNotFound.For(command.CustomerId);
        // La nota es otro agregado: no pasa por Customer, así que el rechazo del fusionado se pide acá.
        customer.EnsureNotMerged();

        var now = clock.UtcNow;
        var note = CustomerNote.Create(
            CustomerNoteId.New(), command.TenantId, customerId, command.Body, executionContext.SubjectId, now);
        notes.Add(note);
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, CustomerAuditActions.NoteCreated, note.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return (await CustomerNoteMapping.ToDtosAsync([note], authorNames, executionContext, cancellationToken))[0];
    }
}

public sealed class UpdateCustomerNoteHandler(
    ICustomerRepository customers,
    ICustomerNoteRepository notes,
    ICustomerAuthorNames authorNames,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateCustomerNoteCommand> validator)
    : ICommandHandler<UpdateCustomerNoteCommand, CustomerNoteDto>
{
    public async Task<CustomerNoteDto> HandleAsync(UpdateCustomerNoteCommand command, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var customerId = new CustomerId(command.CustomerId);
        var customer = await customers.FindAsync(command.TenantId, customerId, cancellationToken)
            ?? throw CustomerNotFound.For(command.CustomerId);
        customer.EnsureNotMerged();
        var note = await notes.FindAsync(command.TenantId, customerId, new CustomerNoteId(command.NoteId), cancellationToken)
            ?? throw NoteNotFound.For(command.NoteId);

        // Sólo el autor edita: ni siquiera quien modera, porque moderar es borrar, no reescribir lo que otro dijo.
        if (!note.IsAuthoredBy(executionContext.SubjectId))
        {
            throw new RequestForbiddenException(
                "customers.note.not_author", "Only the author of a note can edit it.");
        }

        if (note.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The note changed since it was loaded.");
        }

        var now = clock.UtcNow;
        note.Edit(command.Body, now);
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, CustomerAuditActions.NoteUpdated, note.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return (await CustomerNoteMapping.ToDtosAsync([note], authorNames, executionContext, cancellationToken))[0];
    }
}

public sealed class DeleteCustomerNoteHandler(
    ICustomerRepository customers,
    ICustomerNoteRepository notes,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeleteCustomerNoteCommand, CustomerNoteDeletedResult>
{
    public async Task<CustomerNoteDeletedResult> HandleAsync(DeleteCustomerNoteCommand command, CancellationToken cancellationToken)
    {
        // El permiso depende de la nota (autor o moderador), así que acá sólo se frena el tenant; el resto, abajo.
        CustomersAuthorization.EnsureTenant(executionContext, command.TenantId);
        var canManage = executionContext.HasPermission(CustomersPermissions.CustomerManage);
        var canModerate = executionContext.HasPermission(CustomersPermissions.NoteModerate);
        if (!canManage && !canModerate)
        {
            throw new RequestForbiddenException(
                "authorization.denied", "The subject cannot perform this customers operation for this tenant.");
        }

        var customerId = new CustomerId(command.CustomerId);
        var customer = await customers.FindAsync(command.TenantId, customerId, cancellationToken)
            ?? throw CustomerNotFound.For(command.CustomerId);
        customer.EnsureNotMerged();
        var note = await notes.FindAsync(command.TenantId, customerId, new CustomerNoteId(command.NoteId), cancellationToken)
            ?? throw NoteNotFound.For(command.NoteId);

        if (!canModerate && !note.IsAuthoredBy(executionContext.SubjectId))
        {
            throw new RequestForbiddenException(
                "customers.note.not_author", "Only the author of a note, or a moderator, can delete it.");
        }

        var now = clock.UtcNow;
        notes.Remove(note);
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, CustomerAuditActions.NoteDeleted, note.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new CustomerNoteDeletedResult(true);
    }
}
