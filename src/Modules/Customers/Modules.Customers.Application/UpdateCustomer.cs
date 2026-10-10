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
    Guid ClassificationId,
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

        // Se resuelven aca, no solo por la FK de base: la respuesta del PUT lleva la ciudad, el
        // departamento y la clasificacion resueltos, igual que el detalle. Ademas, si la
        // clasificacion cambia, su prefijo es lo que Customer.Update usa para reescribir el CUC —
        // ver la regla de negocio documentada alla.
        var classification = await classificationRepository.FindAsync(
            command.TenantId, new ClientClassificationId(command.ClassificationId), cancellationToken)
            ?? throw new CustomersDomainException(
                "customers.customer.classification_not_found",
                "The client classification was not found in this tenant.");
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
                classification.Prefix,
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
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return await customer.ToDtoAsync(
                geographyLookup, classificationRepository, cancellationToken);
        }

        // Los opcionales se mandan siempre, incluidos los null: el PUT reemplaza el recurso
        // entero, asi que un campo ausente se limpia. El CUC no esta en la firma porque no viaja
        // en el request — lo emite el backend al crear. Update si recibe el prefijo de la
        // clasificacion resuelta: lo usa para reescribir el CUC solo si la clasificacion cambio.
        // `address`/`cityId` del request son el **domicilio del cliente** (spec 2026-09-18) y
        // nada mas: la libreta de envio tiene su propio recurso (`/customers/{id}/addresses`) y
        // no se toca desde aca. Espejar el domicilio en la principal era justo el acoplamiento
        // que hacia perder direcciones al marcar otra como principal y volver a guardar.
        customer.Update(
            command.Name,
            command.BusinessName,
            CustomerMapping.ToIdentification(
                command.IdentificationType, command.IdentificationNumber),
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
            classification.Prefix,
            now,
            phoneNormalizer);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.updated",
            customer.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, cancellationToken);
    }
}
