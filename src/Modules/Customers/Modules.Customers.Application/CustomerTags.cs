using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

// Spec 2026-10-11 §4.2: el catálogo de etiquetas del tenant y el conjunto de etiquetas de cada cliente.

public sealed record ListTagsQuery(Guid TenantId) : IQuery<IReadOnlyList<TagDto>>;

public sealed record CreateTagCommand(Guid TenantId, string Name, string Color) : ICommand<TagDto>;

public sealed record UpdateTagCommand(Guid TenantId, Guid TagId, string Name, string Color, long ExpectedVersion) : ICommand<TagDto>;

public sealed record DeleteTagCommand(Guid TenantId, Guid TagId) : ICommand<TagDeletedResult>;

public sealed record TagDeletedResult(bool Deleted);

public sealed record ListCustomerTagsQuery(Guid TenantId, Guid CustomerId) : IQuery<IReadOnlyList<TagDto>>;

public sealed record SetCustomerTagsCommand(Guid TenantId, Guid CustomerId, IReadOnlyList<Guid> TagIds)
    : ICommand<IReadOnlyList<TagDto>>;

// El color llega como texto: uno desconocido tiene que ser un 422 con el campo marcado y no un 400 del binding.
public sealed class CreateTagValidator : AbstractValidator<CreateTagCommand>
{
    public CreateTagValidator()
    {
        RuleFor(command => command.Name).NotEmpty().MaximumLength(Tag.NameMaxLength);
        RuleFor(command => command.Color).Must(color => TagColorParser.TryParse(color, out _))
            .WithMessage("The color must be one of: gray, red, orange, yellow, green, teal, blue, purple.");
    }
}

public sealed class UpdateTagValidator : AbstractValidator<UpdateTagCommand>
{
    public UpdateTagValidator()
    {
        RuleFor(command => command.Name).NotEmpty().MaximumLength(Tag.NameMaxLength);
        RuleFor(command => command.Color).Must(color => TagColorParser.TryParse(color, out _))
            .WithMessage("The color must be one of: gray, red, orange, yellow, green, teal, blue, purple.");
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

public sealed class SetCustomerTagsValidator : AbstractValidator<SetCustomerTagsCommand>
{
    public SetCustomerTagsValidator()
    {
        RuleFor(command => command.TagIds).NotNull();
        RuleFor(command => command.TagIds)
            .Must(ids => ids is null || ids.Count <= CustomerTagAssignment.MaxPerCustomer)
            .WithMessage($"A customer cannot have more than {CustomerTagAssignment.MaxPerCustomer} tags.");
        RuleFor(command => command.TagIds)
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("The tags cannot repeat.");
    }
}

public sealed class ListTagsHandler(ICustomerTagRepository tags, IExecutionContext executionContext)
    : IQueryHandler<ListTagsQuery, IReadOnlyList<TagDto>>
{
    public async Task<IReadOnlyList<TagDto>> HandleAsync(ListTagsQuery query, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, query.TenantId, CustomersPermissions.CustomerRead);

        return (await tags.ListAsync(query.TenantId, cancellationToken)).Select(item => item.ToDto()).ToArray();
    }
}

public sealed class CreateTagHandler(
    ICustomerTagRepository tags,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateTagCommand> validator)
    : ICommandHandler<CreateTagCommand, TagDto>
{
    public async Task<TagDto> HandleAsync(CreateTagCommand command, CancellationToken cancellationToken)
    {
        // Crear en línea es de quien gestiona clientes (decisión 3); renombrar y borrar, no.
        CustomersAuthorization.EnsureAuthorized(executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        Tag.EnsureTenantCapacity(await tags.CountAsync(command.TenantId, cancellationToken));

        var now = clock.UtcNow;
        var tag = Tag.Create(TagId.New(), command.TenantId, command.Name, TagColorParser.Parse(command.Color), now);
        tags.Add(tag);
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, CustomerAuditActions.TagCreated, tag.Id.ToString(), "success", now);
        // El nombre repetido lo arbitra IX_tags_tenant_name, y CustomersUnitOfWork lo vuelve 409 customers.tag.name_taken.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return tag.ToDto(0);
    }
}

public sealed class UpdateTagHandler(
    ICustomerTagRepository tags,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateTagCommand> validator)
    : ICommandHandler<UpdateTagCommand, TagDto>
{
    public async Task<TagDto> HandleAsync(UpdateTagCommand command, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, command.TenantId, CustomersPermissions.TagManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var tag = await tags.FindAsync(command.TenantId, new TagId(command.TagId), cancellationToken)
            ?? throw TagNotFound.For(command.TagId);

        if (tag.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The tag changed since it was loaded.");
        }

        var now = clock.UtcNow;
        tag.Update(command.Name, TagColorParser.Parse(command.Color), now);
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, CustomerAuditActions.TagUpdated, tag.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return tag.ToDto(await tags.CountCustomersAsync(command.TenantId, tag.Id, cancellationToken));
    }
}

public sealed class DeleteTagHandler(
    ICustomerTagRepository tags,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeleteTagCommand, TagDeletedResult>
{
    public async Task<TagDeletedResult> HandleAsync(DeleteTagCommand command, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, command.TenantId, CustomersPermissions.TagManage);
        var tag = await tags.FindAsync(command.TenantId, new TagId(command.TagId), cancellationToken)
            ?? throw TagNotFound.For(command.TagId);

        var now = clock.UtcNow;
        tags.Remove(tag);
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, CustomerAuditActions.TagDeleted, tag.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new TagDeletedResult(true);
    }
}

public sealed class ListCustomerTagsHandler(
    ICustomerRepository customers, ICustomerTagRepository tags, IExecutionContext executionContext)
    : IQueryHandler<ListCustomerTagsQuery, IReadOnlyList<TagDto>>
{
    public async Task<IReadOnlyList<TagDto>> HandleAsync(ListCustomerTagsQuery query, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, query.TenantId, CustomersPermissions.CustomerRead);
        var customerId = new CustomerId(query.CustomerId);
        _ = await customers.FindAsync(query.TenantId, customerId, cancellationToken)
            ?? throw CustomerNotFound.For(query.CustomerId);

        return (await tags.ListForCustomerAsync(query.TenantId, customerId, cancellationToken)).Select(item => item.ToDto()).ToArray();
    }
}

public sealed class SetCustomerTagsHandler(
    ICustomerRepository customers,
    ICustomerTagRepository tags,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SetCustomerTagsCommand> validator)
    : ICommandHandler<SetCustomerTagsCommand, IReadOnlyList<TagDto>>
{
    public async Task<IReadOnlyList<TagDto>> HandleAsync(SetCustomerTagsCommand command, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var customerId = new CustomerId(command.CustomerId);
        var customer = await customers.FindAsync(command.TenantId, customerId, cancellationToken)
            ?? throw CustomerNotFound.For(command.CustomerId);
        // Las asignaciones son otro agregado: no pasan por Customer, así que el rechazo del fusionado se pide acá.
        customer.EnsureNotMerged();

        var requested = command.TagIds.Select(id => new TagId(id)).ToArray();
        CustomerTagAssignment.EnsureValidSelection(requested);

        // Todas tienen que ser de este tenant: una de otro tenant no aparece en la consulta y sale como 404.
        var existing = (await tags.ListByIdsAsync(command.TenantId, requested, cancellationToken)).Select(tag => tag.Id).ToHashSet();
        var missing = requested.FirstOrDefault(id => !existing.Contains(id));
        if (missing != default)
        {
            throw TagNotFound.For(missing.Value);
        }

        var current = await tags.ListAssignmentsAsync(command.TenantId, customerId, cancellationToken);
        var requestedSet = requested.ToHashSet();
        var currentSet = current.Select(assignment => assignment.TagId).ToHashSet();
        var toRemove = current.Where(assignment => !requestedSet.Contains(assignment.TagId)).ToArray();
        var toAdd = requested.Where(id => !currentSet.Contains(id)).ToArray();

        if (toRemove.Length > 0 || toAdd.Length > 0)
        {
            var now = clock.UtcNow;
            foreach (var assignment in toRemove)
            {
                tags.RemoveAssignment(assignment);
            }

            foreach (var tagId in toAdd)
            {
                tags.AddAssignment(CustomerTagAssignment.Create(customerId, tagId, command.TenantId, executionContext.SubjectId, now));
            }

            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, CustomerAuditActions.TagsChanged, customerId.ToString(), "success", now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // La colección entera y en orden, que es lo que la pantalla repinta (BFF): se relee después de guardar.
        return (await tags.ListForCustomerAsync(command.TenantId, customerId, cancellationToken)).Select(item => item.ToDto()).ToArray();
    }
}
