using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record UpdateCustomerCommand(
    Guid TenantId,
    Guid CustomerId,
    string Name,
    string? BusinessName,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string Country,
    Guid? CityId,
    string? CityName,
    Guid? ClassificationId,
    bool WithRetention,
    bool VatSurplus) : ICommand<CustomerDto>, ICustomerWriteCommand;

// Mismas reglas que el POST, por inclusion y no por copia. Ver CustomerWriteRules.
public sealed class UpdateCustomerValidator : AbstractValidator<UpdateCustomerCommand>
{
    public UpdateCustomerValidator() => Include(new CustomerWriteRules());
}

public sealed class UpdateCustomerHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerTagRepository tagRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    ICucGenerator cucGenerator,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateCustomerCommand> validator,
    IPhoneNumberNormalizer phoneNormalizer)
    : ICommandHandler<UpdateCustomerCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        UpdateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razon en CreateCustomerHandler.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var customer = await repository.FindAsync(
            command.TenantId,
            new CustomerId(command.CustomerId),
            cancellationToken)
            ?? throw CustomerNotFound.For(command.CustomerId);

        // Antes de pedir el CUC: Customer.Complete también rechaza, pero para entonces el consecutivo ya se quemó.
        customer.EnsureNotMerged();

        // Se resuelven aca, no solo por la FK de base: la respuesta del PUT lleva la ciudad, el
        // departamento y la clasificacion resueltos, igual que el detalle. Ademas, si la
        // clasificacion cambia, su prefijo es lo que Customer.Update usa para reescribir el CUC —
        // ver la regla de negocio documentada alla.
        var classification = await classificationRepository.ResolveForCustomerAsync(
            command.TenantId, command.ClassificationId, cancellationToken);
        // Solo un cliente colombiano tiene ciudad DIVIPOLA que resolver; uno de afuera escribe la
        // suya. El validador ya exigio la que corresponde al pais. Sobre una ficha completa es puro
        // chequeo de existencia: el CUC no se reconstruye en un Update (el departamento de su
        // codigo es el del alta, no el vigente). Sobre una incompleta, la ciudad da el departamento
        // del CUC que se emite al completarla, como en el alta.
        // Sin `?.`: el validador ya exigio el pais NotEmpty. Ver la misma nota en CreateCustomer.
        var isColombian = string.Equals(
            command.Country.Trim(),
            Customer.ColombiaCountryCode,
            StringComparison.OrdinalIgnoreCase);
        var city = isColombian
            ? await geographyLookup.FindCityAsync(command.CityId!.Value, cancellationToken)
                ?? throw new CustomersDomainException(
                    "customers.customer.city_not_found",
                    "The city was not found.")
            : null;

        var now = clock.UtcNow;

        if (!customer.IsComplete)
        {
            // Spec 2026-10-10 §6.2: completar emite el CUC exactamente como el alta (CreateCustomer.cs) y
            // siembra la libreta como Create. Mismas reglas de validacion: el validador ya corrio arriba. El
            // normalizador va siempre: sin el, PhoneE164 quedaria en null y la bandeja dejaria de encontrar
            // al cliente por su numero.
            var sequence = await cucGenerator.NextAsync(command.TenantId, cancellationToken);
            var cuc = CucFormatter.Build(
                classification?.Prefix,
                city?.DepartmentDivipolaCode ?? CucFormatter.ForeignDepartmentCode,
                sequence);
            customer.Complete(
                cuc,
                command.Name,
                command.BusinessName,
                city is null
                    ? null
                    : new CustomerAddressDetails
                    {
                        Name = command.Name,
                        Address = command.Address ?? string.Empty,
                        CityId = city.CityId,
                        Phone = command.Phone
                    },
                CustomerMapping.ToIdentification(
                    command.IdentificationType, command.IdentificationNumber),
                // Task 2 lo conecta al request (spec 2026-10-26).
                null,
                new CustomerContactInfo
                {
                    Phone = command.Phone,
                    Email = command.Email,
                    Address = command.Address ?? string.Empty,
                    Country = command.Country,
                    CityId = command.CityId,
                    CityName = command.CityName
                },
                CustomerMapping.ToCommercialInfo(
                    command.ClassificationId, command.WithRetention, command.VatSurplus),
                now,
                phoneNormalizer);
            // D-A9: completar se audita aparte de una edicion.
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                CustomerAuditActions.Completed,
                customer.Id.ToString(),
                "success",
                now);
            await SaveAsync(customer, command.TenantId, cancellationToken);

            return await customer.ToDtoAsync(
                geographyLookup, classificationRepository, tagRepository, cancellationToken);
        }

        // Los opcionales se mandan siempre, incluidos los null: el PUT reemplaza el recurso
        // entero, asi que un campo ausente se limpia. El CUC no esta en la firma porque no viaja
        // en el request — lo emite el backend al crear. Update si recibe el prefijo de la
        // clasificacion resuelta: lo usa para reescribir el CUC solo si la clasificacion cambio.
        // Sin clasificacion el prefijo llega nulo y el CUC se queda con sus ocho caracteres
        // estables.
        // `address`/`cityId` del request son el **domicilio del cliente** (spec 2026-09-18) y
        // nada mas: la libreta de envio tiene su propio recurso (`/customers/{id}/addresses`) y
        // no se toca desde aca. Espejar el domicilio en la principal era justo el acoplamiento
        // que hacia perder direcciones al marcar otra como principal y volver a guardar.
        customer.Update(
            command.Name,
            command.BusinessName,
            CustomerMapping.ToIdentification(
                command.IdentificationType, command.IdentificationNumber),
            // Task 2 lo conecta al request (spec 2026-10-26).
            null,
            new CustomerContactInfo
            {
                Phone = command.Phone,
                Email = command.Email,
                Address = command.Address ?? string.Empty,
                Country = command.Country,
                CityId = command.CityId,
                CityName = command.CityName
            },
            CustomerMapping.ToCommercialInfo(
                command.ClassificationId, command.WithRetention, command.VatSurplus),
            classification?.Prefix,
            now,
            phoneNormalizer);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.updated",
            customer.Id.ToString(),
            "success",
            now);
        await SaveAsync(customer, command.TenantId, cancellationToken);

        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, tagRepository, cancellationToken);
    }

    // Completar y editar guardan por separado y los dos pueden chocar con el indice de la identificacion: en ambos el
    // dueño se relee despues del indice (spec clientes duplicados §3.1) y el 422 lo lleva en `existing`.
    private async Task SaveAsync(Customer customer, Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (CustomersDomainException e) when (e.Code == CustomerIdentificationTaken.Code)
        {
            throw await CustomerIdentificationTaken.ResolveAsync(repository, tenantId, customer, e, cancellationToken);
        }
    }
}
