using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record CreateCustomerCommand(
    Guid TenantId,
    string Name,
    string? BusinessName,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string Country,
    Guid? CityId,
    string? CityName,
    Guid? ClassificationId,
    bool VatSurplus,
    string? PersonType = null,
    // Spec impuestos por tipo §9.2: null no asigna ninguna. El WithRetention del cuerpo viejo ya no entra al comando.
    IReadOnlyList<Guid>? WithholdingTaxRateIds = null,
    // Spec ReteICA por empresa §9.3: null no registra ningún municipio.
    IReadOnlyList<WithholdingAgentCityRequest>? WithholdingAgentCities = null,
    // Spec 2026-11-15: null crea el cliente sin empresa, como siempre.
    Guid? CompanyId = null,
    // Spec 2026-11-23: la region (provincia) del domicilio de un pais con catalogo distinto de Colombia; null es «sin region».
    Guid? RegionId = null,
    // Spec 2026-11-27: los perfiles de facturación que nacen con el cliente, en el orden en que llegaron; null no crea ninguno.
    IReadOnlyList<NewCustomerBillingProfile>? BillingProfiles = null) : ICommand<CustomerDto>, ICustomerWriteCommand;

/// <summary>
/// Un perfil de facturación que viaja con el alta del cliente (spec 2026-11-27). Existe porque, en <c>/customers/new</c>,
/// la pantalla arma los perfiles como borradores y los manda junto con el cliente: crearlos uno por uno después deja un
/// cliente a medias cuando falla un request, y aquí nacen en la misma transacción. Tiene los campos de
/// <see cref="AddCustomerBillingProfileCommand"/> sin los ids de tenant y cliente, y pasa por las mismas reglas.
/// </summary>
public sealed record NewCustomerBillingProfile(
    string? Name,
    string? PersonType,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    string? Country,
    Guid? CityId,
    string? CityName,
    Guid? RegionId,
    IReadOnlyList<Guid>? WithholdingTaxRateIds,
    bool VatSurplus,
    bool IsPrincipal) : ICustomerBillingProfileWriteCommand;

// Las reglas viven en CustomerWriteRules y se incluyen, no se copian. Ver el hallazgo `D` alla.
public sealed class CreateCustomerValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerValidator()
    {
        Include(new CustomerWriteRules());

        // Las mismas reglas que el alta de un perfil suelto; la clave sale con índice (BillingProfiles[1].Name), que es
        // como el formulario marca el campo del borrador.
        RuleForEach(command => command.BillingProfiles).SetValidator(new CustomerBillingProfileWriteRules());
        RuleFor(command => command.BillingProfiles)
            .Must(profiles => profiles is null || profiles.Count(profile => profile.IsPrincipal) <= 1)
            .WithMessage("Sólo un perfil de facturación puede ser el principal.");
    }
}

public sealed class CreateCustomerHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerTagRepository tagRepository,
    ICustomerTaxRateLookup taxRateLookup,
    ICustomerGeographyLookup geographyLookup,
    ICustomerCompanyDirectory companyDirectory,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    ICucGenerator cucGenerator,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateCustomerCommand> validator,
    IPhoneNumberNormalizer phoneNormalizer,
    ITenantDirectory tenantDirectory)
    : ICommandHandler<CreateCustomerCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        CreateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar, y no al reves. La politica del endpoint ya frena a quien le
        // falta el permiso, pero no al que lo tiene para otro tenant: a ese lo rechaza esta
        // revalidacion. Validando primero, ese llamador ajeno se lleva el mapa de errores por
        // campo —la forma del contrato— antes de que nadie le diga que no. Lo encontro la revision
        // de riesgo de CAT-02.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        // El tope va como error de dominio y no como regla del validador: un error de FluentValidation sale siempre como
        // `validation.failed`, y el contrato pide el mismo código del alta de un perfil suelto. Antes del CUC, para no quemar un número.
        if (command.BillingProfiles is { } requested && requested.Count > CustomerBillingProfile.MaxPerCustomer)
        {
            throw new CustomersDomainException(
                CustomerBillingProfileErrors.LimitReached,
                $"A customer cannot have more than {CustomerBillingProfile.MaxPerCustomer} billing profiles.");
        }

        await CustomerWithholdingRules.ValidateAsync(
            taxRateLookup, command.TenantId, command.WithholdingTaxRateIds ?? [], cancellationToken);
        var agentCities = await CustomerWithholdingAgentRules.ValidateAsync(
            geographyLookup, command.WithholdingAgentCities ?? [], cancellationToken);
        await CustomerPersonTypeRequirement.EnsureSatisfiedAsync(
            tenantDirectory, command.TenantId, command.Country, command.PersonType, cancellationToken);
        _ = await CustomerRegionRules.ResolveRegionAsync(
            geographyLookup, command.Country, command.RegionId, CustomerRegionRules.CustomerRegionInvalid, cancellationToken);
        await CustomerFieldPolicyRequirement.EnsureSatisfiedAsync(
            tenantDirectory, command.TenantId, command, current: null, cancellationToken);

        // La clasificacion y la ciudad se resuelven **antes** de pedir el CUC: hacen falta su
        // prefijo (si trae clasificacion) y su codigo de departamento para armarlo, no son un pre-chequeo de integridad
        // redundante con la FK de base. Resolverlas antes tambien evita quemar un numero del
        // consecutivo cuando la referencia del cuerpo es invalida.
        var classification = await classificationRepository.ResolveForCustomerAsync(
            command.TenantId, command.ClassificationId, cancellationToken);
        // Solo un cliente colombiano tiene ciudad DIVIPOLA que resolver. Puede no venir: si hacia
        // falta, ya lo rechazo la politica del tenant (CustomerFieldPolicyRequirement, arriba); si
        // viene, aca se comprueba que exista.
        // Sin `?.`: el validador de arriba ya exigio el pais NotEmpty, asi que a esta altura no es
        // nulo — y tratarlo como si pudiera serlo contagia el nulo a Country mas abajo.
        var isColombian = string.Equals(
            command.Country.Trim(),
            Customer.ColombiaCountryCode,
            StringComparison.OrdinalIgnoreCase);
        var city = isColombian && command.CityId is { } cityId
            ? await geographyLookup.FindCityAsync(cityId, cancellationToken)
                ?? throw new CustomersDomainException(
                    "customers.customer.city_not_found",
                    "The city was not found.")
            : null;

        // Los perfiles se comprueban igual que en el alta de uno suelto (región, ciudad, tasas) y también antes del CUC: una
        // referencia mala en el segundo no debe quemar un número ni dejar al cliente creado.
        foreach (var profile in command.BillingProfiles ?? [])
        {
            await CustomerBillingProfileWorkflow.ValidateReferencesAsync(
                geographyLookup, taxRateLookup, command.TenantId, profile, cancellationToken);
        }

        // Spec 2026-11-15: con empresa, el alta corre en una transacción para bloquear la fila de la empresa, igual que
        // SetCustomerCompanyHandler: así no se vuelve contacto de otra entre esta lectura y el commit. Desactivarla no toma
        // este bloqueo (tampoco frente al vínculo de un cliente existente): sólo espera a que el alta confirme.
        // Se resuelve antes del CUC, como la clasificación y la ciudad, para no quemar un número con una empresa que no existe.
        // Sin empresa no se abre transacción (`await using` sobre null no hace nada): el alta sigue exactamente como antes.
        // Cada salida sin CommitAsync la deshace al liberarla.
        await using var transaction = command.CompanyId is null
            ? null
            : await unitOfWork.BeginTransactionAsync(cancellationToken);
        Customer? company = null;
        if (command.CompanyId is { } companyId)
        {
            await repository.LockForCompanyLinkAsync(command.TenantId, [companyId], cancellationToken);
            // Un id de otro tenant no aparece: es «no existe», igual que uno inexistente.
            company = await repository.FindAsync(command.TenantId, new CustomerId(companyId), cancellationToken)
                ?? throw new CustomersDomainException(
                    CustomersErrorCodes.CompanyNotFound, "The company does not exist.");
        }

        // El CUC se pide **despues** de resolver clasificacion y ciudad: cada llamada consume un
        // numero del consecutivo del tenant, y pedirlo antes quemaria uno por cada referencia mal
        // escrita. Un consecutivo con huecos no rompe nada, pero nadie tiene una buena respuesta
        // para el cliente que pregunta por que su primer codigo salto un numero.
        var sequence = await cucGenerator.NextAsync(command.TenantId, cancellationToken);
        // Un cliente de afuera no tiene departamento DIVIPOLA, y el CUC lleva dos digitos en ese
        // lugar: van los de CucFormatter.ForeignDepartmentCode. Ver alla por que no se omiten.
        var cuc = CucFormatter.Build(
            classification?.Prefix,
            city?.DepartmentDivipolaCode ?? CucFormatter.ForeignDepartmentCode,
            sequence);
        var now = clock.UtcNow;

        var customer = Customer.Create(
            CustomerId.New(),
            command.TenantId,
            cuc,
            command.Name,
            command.BusinessName,
            // La primera direccion de envio nace con el request de alta, con el mismo par que el
            // domicilio de abajo (spec 2026-09-18, decision 3): su ciudad es la que acaba de
            // emitir el CUC, y con ella la cotizacion tiene algo que preseleccionar. Desde aca son
            // dos datos distintos: editar la ficha no la toca.
            //
            // Sin ciudad DIVIPOLA (un cliente de afuera) o sin calle nace sin libreta.
            CustomerMapping.ToFirstAddress(command.Name, command.Address, city, command.Phone),
            CustomerMapping.ToIdentification(
                command.IdentificationType, command.IdentificationNumber),
            PersonTypeParser.ParseOptional(command.PersonType),
            new CustomerContactInfo
            {
                Phone = command.Phone,
                Email = command.Email,
                Address = command.Address,
                Country = command.Country,
                CityId = command.CityId,
                CityName = command.CityName,
                RegionId = command.RegionId
            },
            CustomerMapping.ToCommercialInfo(
                command.ClassificationId, command.VatSurplus),
            now,
            phoneNormalizer);

        if (command.WithholdingTaxRateIds is { Count: > 0 } taxRateIds)
        {
            customer.SetWithholdingTaxRates(taxRateIds, now);
        }

        if (agentCities.Count > 0)
        {
            customer.SetWithholdingAgentCities(agentCities, now);
        }

        // Las mismas reglas que el vínculo de un cliente existente (activa, no fusionada, no es contacto de otra). Un cliente
        // nuevo no tiene contactos ni puede ser la empresa elegida. Una sola auditoría: el cliente nació así, no cambió.
        if (company is not null)
        {
            customer.LinkToCompany(company, hasContacts: false, now);
        }

        // En orden y antes del único SaveChanges: el cliente y sus perfiles se guardan juntos o no se guarda nada. El primero
        // queda principal si ninguno lo pide. El alta de un perfil suelto no audita (paridad con direcciones), así que aquí
        // tampoco hay una entrada por perfil: la del cliente cubre el alta.
        foreach (var profile in command.BillingProfiles ?? [])
        {
            customer.EnsureBillingProfile(CustomerBillingProfileWorkflow.ToDetails(profile), profile.IsPrincipal, now);
        }

        repository.Add(customer);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.created",
            customer.Id.ToString(),
            "success",
            now);

        // La unicidad de la identificacion y del CUC, y la existencia de la ciudad y la
        // clasificacion en la carrera entre esta lectura y el commit, NO se comprueban con un
        // segundo SELECT: el unico arbitro real son los indices y las FK de base, y
        // CustomersUnitOfWork traduce sus violaciones. Cuando es la de la identificacion, el dueño se
        // relee **despues** del indice y nunca antes (spec clientes duplicados §3.1): un pre-chequeo
        // dejaria pasar la carrera, y el 422 lleva `existing` para que la pantalla ofrezca ir a el.
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (CustomersDomainException e) when (e.Code == CustomerIdentificationTaken.Code)
        {
            // Defensivo: EF Core guarda dentro de un savepoint y, al fallar, vuelve a él, así que la transacción sigue usable y
            // ResolveAsync podría releer al dueño dentro de ella. Deshacerla antes suelta el bloqueo de la empresa y deja esa
            // lectura en autocommit, sin depender de que los savepoints automáticos sigan prendidos.
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            throw await CustomerIdentificationTaken.ResolveAsync(
                repository, command.TenantId, customer, e, cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return await customer.ToDtoAsync(
            geographyLookup, classificationRepository, tagRepository, taxRateLookup, companyDirectory, cancellationToken);
    }
}
