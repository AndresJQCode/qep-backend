using BuildingBlocks.Application;
using FluentValidation;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

/// <summary>
/// El cuerpo de <c>POST /services</c> tal como llega por el cable, con todo anulable para que su ausencia sea un
/// <c>validation.failed</c> con el campo y no un 400 del binding (mismo criterio que <see cref="ProfileInput"/>).
/// </summary>
public sealed record CreateServiceRequest(
    Guid? CustomerId,
    string? Name,
    decimal? Price,
    string? Currency,
    string? Cycle,
    DateOnly? FirstDueDate,
    ProfileInput? Profile);

/// <summary>
/// <c>Currency</c> null o en blanco es la moneda del tenant. <c>Profile</c> sólo se usa si el cliente aún no tiene perfil (spec §4).
/// </summary>
public sealed record CreateServiceCommand(
    Guid TenantId,
    Guid CustomerId,
    string Name,
    decimal Price,
    string? Currency,
    string Cycle,
    DateOnly FirstDueDate,
    ProfileInput? Profile)
    : ICommand<ServiceDetailDto>
{
    public static CreateServiceCommand From(Guid tenantId, CreateServiceRequest request) => new(
        tenantId,
        request.CustomerId ?? Guid.Empty,
        request.Name ?? string.Empty,
        request.Price ?? 0m,
        request.Currency,
        request.Cycle ?? string.Empty,
        request.FirstDueDate ?? default,
        request.Profile);
}

public sealed class CreateServiceValidator : AbstractValidator<CreateServiceCommand>
{
    public CreateServiceValidator(IClock clock)
    {
        RuleFor(command => command.CustomerId).NotEqual(Guid.Empty);
        RuleFor(command => command.Name).MustBeAServiceName();
        RuleFor(command => command.Price).MustBeAServicePrice();
        RuleFor(command => command.Cycle).MustBeABillingCycle();
        RuleFor(command => command.FirstDueDate).MustBeAReasonableFirstDueDate(clock);
    }
}

/// <summary>
/// Crea el servicio, su perfil si el cliente no tenía y los cobros que la ventana pide hoy (spec §3.2), todo en una sola
/// transacción con su auditoría. El orden de las validaciones es el del contrato: cliente → perfil → moneda.
/// </summary>
public sealed class CreateServiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ITenantDefaultCurrency tenantDefaultCurrency,
    IBillingCustomerLookup customers,
    IBillingMemberDirectory members,
    ICustomerBillingProfileRepository profiles,
    IBillingServiceRepository services,
    IChargeRepository charges,
    IBillingUnitOfWork unitOfWork,
    IBillingAuditPublisher auditPublisher,
    IClock clock,
    ServiceDetailComposer composer,
    IValidator<CreateServiceCommand> validator,
    IValidator<ProfileInput> profileValidator)
    : ICommandHandler<CreateServiceCommand, ServiceDetailDto>
{
    private static readonly string[] CreatedFields = ["customerId", "name", "price", "currency", "cycle", "firstDueDate"];

    private static readonly string[] ProfileFields = ["channel", "assigneeMemberId"];

    public async Task<ServiceDetailDto> HandleAsync(CreateServiceCommand command, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, BillingPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        if (!ServiceRules.TryParseCycle(command.Cycle, out var cycle))
        {
            throw new InvalidOperationException($"Cycle '{command.Cycle}' passed validation but is not a billing cycle.");
        }

        // La búsqueda va acotada al tenant de la ruta: un cliente de otro tenant llega como «no existe».
        var customer = await customers.FindAsync(command.TenantId, command.CustomerId, cancellationToken)
            ?? throw new BillingDomainException(
                BillingErrorCodes.CustomerNotFound,
                $"Customer '{command.CustomerId}' was not found.");
        if (!customer.IsActive)
        {
            throw new BillingDomainException(
                BillingErrorCodes.CustomerInactive,
                "An inactive customer cannot get a new recurring service.");
        }

        var now = clock.UtcNow;
        var newProfile = await BuildProfileIfMissingAsync(command, customer, now, cancellationToken);
        var currency = string.IsNullOrWhiteSpace(command.Currency)
            ? await tenantDefaultCurrency.GetAsync(command.TenantId, cancellationToken)
            : ServiceRules.NormalizeCurrency(command.Currency);

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var service = BillingService.Create(
            BillingServiceId.New(),
            command.TenantId,
            command.CustomerId,
            command.Name,
            command.Price,
            currency,
            cycle,
            command.FirstDueDate,
            now);
        services.Add(service);
        // Crear siempre genera el primer cobro (el primero aunque caiga fuera de la ventana): por eso la reactivación nunca
        // se topa con un servicio que no generó nada.
        charges.AddRange(ChargeGenerator.Generate(service, calendar.Today, now));

        if (newProfile is not null)
        {
            profiles.Add(newProfile);
            Audit(command, BillingAuditActions.ProfileUpdated, BillingAuditActions.Resources.Profile, command.CustomerId, ProfileFields, now);
        }

        Audit(command, BillingAuditActions.ServiceCreated, BillingAuditActions.Resources.Service, service.Id.Value, CreatedFields, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(service, cancellationToken);
    }

    /// <summary>
    /// El perfil nuevo, o null si el cliente ya tiene uno (entonces el del cuerpo se ignora, spec §4). Sin perfil y sin cuerpo es
    /// <c>billing.profile.missing</c>; con cuerpo, las mismas reglas que el PUT del perfil.
    /// </summary>
    private async Task<CustomerBillingProfile?> BuildProfileIfMissingAsync(
        CreateServiceCommand command, BillingCustomerRef customer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await profiles.FindAsync(command.TenantId, command.CustomerId, cancellationToken) is not null)
        {
            return null;
        }

        var input = command.Profile
            ?? throw new BillingDomainException(
                BillingErrorCodes.ProfileMissing,
                "The customer has no billing profile; send one with the service.");
        await profileValidator.ValidateAndThrowAsync(input, cancellationToken);
        if (!ProfileRules.TryParseChannel(input.Channel, out var channel))
        {
            throw new InvalidOperationException($"Channel '{input.Channel}' passed validation but is not a billing channel.");
        }

        ProfileRules.EnsureChannelIsReachable(channel, customer);
        var assigneeId = input.AssigneeMemberId!.Value;
        var found = await members.FindAsync(command.TenantId, [assigneeId], cancellationToken);
        ProfileRules.EnsureAssigneeCanManage(found.Count > 0 ? found[0] : null);

        return CustomerBillingProfile.Create(command.TenantId, command.CustomerId, channel, assigneeId, now);
    }

    private void Audit(
        CreateServiceCommand command,
        string action,
        string resourceType,
        Guid resourceId,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset now) =>
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            action,
            resourceType,
            resourceId.ToString(),
            "success",
            changedFields,
            now);
}
