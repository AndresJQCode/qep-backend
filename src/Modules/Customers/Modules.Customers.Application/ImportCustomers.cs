using BuildingBlocks.Application;
using BuildingBlocks.Domain.Identification;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// La carga de un Excel de clientes. Lleva el nombre, el tamano y el contenido: a diferencia del
/// slice original (`CLI-01`, que dejaba el procesamiento fuera de alcance a proposito), esta es la
/// Fase 5 y el contenido si se lee — <see cref="Content"/> es el stream que abre el endpoint sobre
/// el <c>IFormFile</c> subido, y el handler lo consume por completo antes de que el request
/// termine.
/// </summary>
public sealed record ImportCustomersCommand(
    Guid TenantId,
    string FileName,
    long SizeInBytes,
    Stream Content) : ICommand<ImportCustomersResponse>;

/// <summary>
/// Las reglas de una fila del Excel de importacion. Texto crudo entrando, a diferencia de
/// <c>CustomerWriteRules</c> (que valida un comando ya tipado con <c>CityId</c>/
/// <c>ClassificationId</c> como <see cref="Guid"/>): una fila de Excel trae "Antioquia"/"Medellin"/
/// "Mayorista" como texto, y esas cadenas no son campos que este validador pueda resolver — solo
/// puede comprobar que no esten vacias. Resolverlas contra la base (¿existe el departamento? ¿la
/// ciudad, dentro de ese departamento? ¿la clasificacion, en este tenant?) es trabajo de
/// <c>ImportCustomersHandler</c>, no de este validador.
///
/// Publico y no <c>internal</c> como <c>CustomerWriteRules</c>: a diferencia de esa, esta SI se
/// resuelve directo desde el contenedor de DI como <c>IValidator&lt;ExcelCustomerRow&gt;</c> en el
/// handler, y no solo se incluye desde otro validador publico.
/// </summary>
public sealed class ExcelCustomerRowRules : AbstractValidator<ExcelCustomerRow>
{
    private static readonly string[] SiNoValues = ["SI", "NO"];

    /// <summary>
    /// La importación es colombiana: ofrece y acepta sólo los códigos de <c>CO</c>, aunque Customers ya conozca
    /// los siete. Un NIF o un NIE se da de alta a mano, donde se ve el país.
    /// </summary>
    public static IReadOnlyList<string> IdentificationTypes => DocumentTypesByCountry.AllowedFor("CO");

    /// <summary>Sin politica del tenant rige la de siempre: documento y ciudad obligatorios.</summary>
    public ExcelCustomerRowRules() : this(CustomerFieldPolicy.Default)
    {
    }

    public ExcelCustomerRowRules(CustomerFieldPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var documentRequired = policy.Document.OnCustomerSave;
        var cityRequired = policy.City.OnCustomerSave;

        // Opcional a proposito: vacio significa "esta fila crea un cliente"; con un valor,
        // "esta fila actualiza el cliente con ese CUC" (ImportCustomersHandler resuelve cual).
        // El resto de las reglas de esta clase NO se condicionan por Cuc — una fila de
        // actualizacion tiene que traer el registro completo igual que el PUT de un cliente,
        // porque Customer.Update reemplaza todo lo que no viene.
        RuleFor(row => row.Cuc)
            .Must(value => value!.Trim().Length >= Customer.CucSuffixLength)
                .WithErrorCode("customers.import.row.cuc_invalid")
                .WithMessage(
                    $"The Cuc must be at least {Customer.CucSuffixLength} characters.")
            .When(row => !string.IsNullOrWhiteSpace(row.Cuc));

        RuleFor(row => row.Name)
            .NotEmpty()
                .WithErrorCode("customers.import.row.name_required")
                .WithMessage("The customer name is required.")
            .MaximumLength(Customer.NameMaxLength)
                .WithErrorCode("customers.import.row.name_too_long")
                .WithMessage($"The customer name cannot exceed {Customer.NameMaxLength} characters.");

        // Dos RuleFor separados y no uno encadenado (NotEmpty().Must().When(...)): el When() de
        // FluentValidation, sin ApplyConditionTo.AllValidators explicito, puede terminar
        // gobernando el chequeo completo de la propiedad segun como se encadene, no solo el
        // validador al que sigue textualmente — el defecto que encontro
        // ExcelCustomerRowRulesTests.AMissingIdentificationTypeFailsWithTheRequiredCodeAndNotTheFormatCode
        // (RED: con el tipo vacio, el resultado daba valido). Dos reglas independientes, cada una
        // con su propio When() cuando lo necesita, no dejan ambiguedad de alcance.
        // El documento es un par (tipo, numero). Ambos vacios solo se rechaza si la politica del tenant lo pide al
        // guardar; la mitad de un par nunca pasa. Tipo sin numero conserva identification_number_required (Ruling P2);
        // numero sin tipo es identification_incomplete, reportado sobre el tipo, que es la celda que falta.
        RuleFor(row => row.IdentificationType)
            .Must(_ => false)
            .WithErrorCode("customers.import.row.identification_type_required")
            .WithMessage("The identification type is required.")
            .When(row => !IsUpdate(row) && documentRequired && IsBlank(row.IdentificationType) && IsBlank(row.IdentificationNumber));

        RuleFor(row => row.IdentificationType)
            .Must(_ => false)
            .WithErrorCode("customers.import.row.identification_incomplete")
            .WithMessage("The identification needs both its type and its number.")
            .When(row => IsBlank(row.IdentificationType) && !IsBlank(row.IdentificationNumber));

        RuleFor(row => row.IdentificationType)
            .Must(IsSupportedIdentificationType)
            .WithErrorCode("customers.import.row.identification_type_invalid")
            .WithMessage(
                $"The identification type must be one of " +
                $"{Join(IdentificationTypes)}.")
            .When(row => !IsBlank(row.IdentificationType));

        RuleFor(row => row.IdentificationNumber)
            .Must(_ => false)
                .WithErrorCode("customers.import.row.identification_number_required")
                .WithMessage("The identification number is required.")
            .When(row => IsBlank(row.IdentificationNumber) &&
                (!IsBlank(row.IdentificationType) || (!IsUpdate(row) && documentRequired)));

        RuleFor(row => row.IdentificationNumber)
            .MaximumLength(CustomerIdentification.NumberMaxLength)
                .WithErrorCode("customers.import.row.identification_number_too_long")
                .WithMessage(
                    "The identification number cannot exceed " +
                    $"{CustomerIdentification.NumberMaxLength} characters.");

        // El telefono es obligatorio (CustomerContactInfo). Se rechaza como fila, con su codigo,
        // por la misma razon que la direccion: si llegara vacio al dominio, la
        // CustomersDomainException reventaria a mitad del archivo y se llevaria el lote entero.
        RuleFor(row => row.Phone)
            .NotEmpty()
                .WithErrorCode("customers.import.row.phone_required")
                .WithMessage("The phone is required.")
            .MaximumLength(CustomerContactInfo.PhoneMaxLength)
                .WithErrorCode("customers.import.row.phone_too_long")
                .WithMessage($"The phone cannot exceed {CustomerContactInfo.PhoneMaxLength} characters.");

        // La celda es el domicilio del cliente (Customer.Address, CustomerContactInfo.AddressMaxLength);
        // cuando la fila crea un cliente, ademas siembra la primera fila de la libreta de
        // direcciones con la misma calle y ciudad. Se rechaza como fila, con su codigo, y no
        // dejando que reviente el dominio a mitad del archivo -- este metodo no tiene try/catch
        // por fila, y eso se llevaria puesto el resto del lote.
        RuleFor(row => row.Address)
            .NotEmpty()
                .WithErrorCode("customers.import.row.address_required")
                .WithMessage("The address is required.")
            .MaximumLength(CustomerContactInfo.AddressMaxLength)
                .WithErrorCode("customers.import.row.address_too_long")
                .WithMessage(
                    $"The address cannot exceed {CustomerContactInfo.AddressMaxLength} characters.");

        // El correo es opcional, igual que en el formulario (CustomerContactInfo): una celda vacia
        // no rechaza la fila; si viene, tiene que ser una direccion.
        RuleFor(row => row.Email)
            .MaximumLength(CustomerContactInfo.EmailMaxLength)
                .WithErrorCode("customers.import.row.email_too_long")
                .WithMessage($"The email cannot exceed {CustomerContactInfo.EmailMaxLength} characters.")
            .EmailAddress()
                .WithErrorCode("customers.import.row.email_invalid")
                .WithMessage("The email is not a valid address.")
            .When(row => !string.IsNullOrWhiteSpace(row.Email));

        // Solo "no vacio" aca: si el departamento y la ciudad existen, y si la ciudad pertenece a
        // ese departamento, lo resuelve el handler contra Geography — este validador no tiene
        // acceso a la base ni la necesita para esto.
        // En una fila de actualizacion (Cuc lleno) las celdas vacias significan "no se envia" y conservan lo guardado,
        // asi que "vacio los dos" nunca se exige ahi; las mitades de un par si siguen fallando.
        // Departamento y ciudad viajan como par (G9): los dos vacios solo se rechazan si la politica pide la ciudad al
        // guardar; uno sin el otro siempre conserva su codigo.
        RuleFor(row => row.Department)
            .Must(_ => false)
                .WithErrorCode("customers.import.row.department_required")
                .WithMessage("The department is required.")
            .When(row => IsBlank(row.Department) && (!IsBlank(row.City) || (!IsUpdate(row) && cityRequired)));

        RuleFor(row => row.City)
            .Must(_ => false)
                .WithErrorCode("customers.import.row.city_required")
                .WithMessage("The city is required.")
            .When(row => IsBlank(row.City) && (!IsBlank(row.Department) || (!IsUpdate(row) && cityRequired)));

        RuleFor(row => row.VatSurplus)
            .Must(BeSiOrNoOrEmpty)
                .WithErrorCode("customers.import.row.vat_surplus_invalid")
                .WithMessage("The 'Excedente Iva' column must be 'Si', 'No' or empty.");
    }

    private static bool IsUpdate(ExcelCustomerRow row) => !string.IsNullOrWhiteSpace(row.Cuc);

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static bool IsSupportedIdentificationType(string? value) =>
        value is not null && IdentificationTypes.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static bool BeSiOrNoOrEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) || SiNoValues.Contains(value.Trim().ToUpperInvariant());

    private static string Join(IReadOnlyCollection<string> values) =>
        string.Join(", ", values.Order(StringComparer.Ordinal));
}

public sealed class ImportCustomersHandler(
    ICustomerRepository customerRepository,
    ICustomerCompanyDirectory companyDirectory,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomerTaxRateLookup taxRateLookup,
    ICucGenerator cucGenerator,
    IExcelCustomerImporter excelImporter,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    ITenantDirectory tenantDirectory,
    IPhoneNumberNormalizer phoneNormalizer)
    : ICommandHandler<ImportCustomersCommand, ImportCustomersResponse>
{
    /// <summary>
    /// Las extensiones que el modal de importacion ya ofrece
    /// (<c>ACCEPTED_EXTENSIONS</c> en <c>import-customers-modal.tsx</c>).
    /// </summary>
    private static readonly string[] AcceptedExtensions = [".xlsx", ".xls"];

    /// <summary>
    /// 10 MB, el mismo tope que el frontend ya aplica antes de subir. Duplicarlo aca no es
    /// redundancia: la validacion del navegador la elige el llamador, y un cliente que no sea ese
    /// formulario puede mandar lo que quiera.
    /// </summary>
    private const long MaxSizeInBytes = 10 * 1024 * 1024;

    public async Task<ImportCustomersResponse> HandleAsync(
        ImportCustomersCommand command,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerImport);

        var fileName = EnsureAcceptableFile(command.FileName, command.SizeInBytes);

        // Estructural, no de negocio: ¿el Excel tiene las once columnas esperadas? Un archivo
        // corrupto, protegido con contrasena, o que no es realmente un .xlsx tambien cae aca
        // (ClosedXmlCustomerImporter lo homogeneiza a "columnas no encontradas") — desde afuera es
        // indistinguible de una cabecera equivocada, y los dos son "el archivo no sirve".
        var parsed = excelImporter.Parse(command.Content, cancellationToken);
        if (!parsed.HasExpectedColumns)
        {
            throw new CustomersDomainException(
                "customers.import.file_invalid",
                "The file does not have the expected columns. Download the template and try again.");
        }

        if (parsed.Rows.Count == 0)
        {
            throw new CustomersDomainException(
                "customers.import.file_empty_data",
                "The file has the expected columns but no data rows.");
        }

        var (candidates, errors) = await ValidateRowsAsync(command.TenantId, parsed.Rows, cancellationToken);
        candidates = await ResolveCucTargetsAsync(
            command.TenantId, candidates, errors, cancellationToken);
        candidates = await RemoveExistingIdentificationsAsync(
            command.TenantId, candidates, errors, cancellationToken);

        var now = clock.UtcNow;

        // Spec 2026-11-10 §4.8. Sin celdas de «Empresa» el camino es el de siempre: ni transacción ni bloqueos. Con ellas, todo lo
        // que sigue vive en una transacción que sólo se confirma al final: los bloqueos del vínculo duran lo que ella, y si algo
        // revienta a mitad no queda ninguna fila a medias.
        ICustomersTransaction? transaction = null;
        List<ImportedCustomerRow> imported;
        try
        {
            var companyNumbers = candidates
                .Select(candidate => candidate.CompanyNumber)
                .OfType<string>()
                .Distinct()
                .ToArray();
            IReadOnlyDictionary<string, IReadOnlyList<CustomerId>> ownersByNumber =
                new Dictionary<string, IReadOnlyList<CustomerId>>();
            if (companyNumbers.Length > 0)
            {
                // Una sola consulta de ids para todo el archivo, antes de bloquear: dice a quién bloquear, no con qué se decide.
                ownersByNumber = await customerRepository.FindIdsByIdentificationNumberAsync(
                    command.TenantId, companyNumbers, cancellationToken);
                transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
                // Los mismos bloqueos que la acción de vincular (`SetCustomerCompanyHandler`), en un solo llamado y en orden de id:
                // los contactos y las empresas que este archivo toca. Todo lo que se lee de aquí en adelante ya los tiene.
                await customerRepository.LockForCompanyLinkAsync(
                    command.TenantId, LockIdsFor(candidates, ownersByNumber), cancellationToken);
            }

            // Orden: (1) cargar los clientes que se actualizan y descartar los que ya no pueden escribirse, (2) decidir qué
            // vínculos valen, mirando todas las filas a la vez, (3) escribir todos los clientes, (4) recién entonces vincular. Una
            // fila sólo se escribe si todo lo suyo —incluida su empresa— ya está decidido, así ninguna queda a medias.
            var (eligible, loaded) = await LoadUpdateTargetsAsync(
                command.TenantId, candidates, errors, cancellationToken);
            var (writable, links) = await PlanCompanyLinksAsync(
                command.TenantId, eligible, loaded, ownersByNumber, errors, cancellationToken);
            var written = await CreateOrUpdateCustomersAsync(
                command.TenantId, writable, loaded, parsed.WithholdingColumns ?? CustomerImportColumns.WithholdingTypes, now,
                cancellationToken);
            imported = written.Imported;
            ApplyCompanyLinks(links, written.CustomersByRow, now);

            // Un solo evento por el import completo, no uno por cliente: mil clientes importados de
            // una vez no deben dejar mil filas en el outbox. Los conteos van en el resourceId porque
            // ICustomersAuditPublisher.Publish no tiene un campo propio para ellos y el resto del
            // modulo ya usa ese parametro para identificar "sobre que fue esta accion". Los vinculos de empresa no
            // llevan evento propio por lo mismo: son parte de este.
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "customers.customer.imported",
                $"{fileName} (imported={imported.Count}, errors={errors.Count})",
                "success",
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        return new ImportCustomersResponse(
            fileName,
            now,
            StatusOf(imported.Count, errors.Count),
            parsed.Rows.Count,
            imported.Count,
            errors.Count,
            imported,
            errors.OrderBy(error => error.RowNumber).ToArray());
    }

    private static string StatusOf(int importedCount, int errorCount) => (importedCount, errorCount) switch
    {
        (_, 0) => "completed",
        (0, _) => "failed",
        _ => "completed_with_errors"
    };

    /// <summary>
    /// El primer paso de la fila: campos obligatorios/longitudes/formato con
    /// <see cref="ExcelCustomerRowRules"/>, resolver Departamento+Ciudad y Clasificacion contra la
    /// base, y el duplicado **dentro del archivo** — todo sin abortar en la primera fila mala.
    ///
    /// El chequeo de duplicados contra la base NO va aca: ese es un solo query batch sobre las
    /// filas que ya pasaron este paso (<see cref="RemoveExistingIdentificationsAsync"/>), no una
    /// consulta por fila.
    /// </summary>
    private async Task<(List<CandidateRow> Candidates, List<ImportRowError> Errors)> ValidateRowsAsync(
        Guid tenantId,
        IReadOnlyList<ExcelCustomerRow> rows,
        CancellationToken cancellationToken)
    {
        var errors = new List<ImportRowError>();
        var candidates = new List<CandidateRow>();

        // Primera pasada: solo campos, sin tocar la base. Separa las filas con formato valido de
        // las que no, sin abortar el archivo por una fila mala.
        // La politica del tenant se lee una sola vez por archivo, no por fila.
        var settings = await tenantDirectory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken);
        var rowValidator = new ExcelCustomerRowRules(settings?.EffectiveCustomerFieldPolicy ?? CustomerFieldPolicy.Default);

        // Las tasas de retencion activas del tenant, una sola vez por archivo y solo si alguna fila trae el nombre de una.
        IReadOnlyList<CustomerTaxRateRef>? withholdingRates = null;

        var fieldValidRows = new List<ExcelCustomerRow>();
        foreach (var row in rows)
        {
            var validation = rowValidator.Validate(row);
            if (validation.IsValid)
            {
                fieldValidRows.Add(row);
            }
            else
            {
                errors.AddRange(validation.Errors.Select(failure =>
                    new ImportRowError(
                        row.RowNumber,
                        failure.ErrorCode,
                        failure.ErrorMessage,
                        failure.PropertyName,
                        ToRowData(row))));
            }
        }

        // El duplicado **dentro del archivo** es una decision que no necesita la base: dos filas
        // con la misma identificacion se distinguen solo mirando el archivo. Partition es pura
        // (ver ExcelCustomerRowDeduplicator) y se prueba unitariamente sin infraestructura.
        var (firstOccurrences, duplicates) = ExcelCustomerRowDeduplicator.Partition(fieldValidRows);
        errors.AddRange(duplicates.Select(row => new ImportRowError(
            row.RowNumber,
            "customers.import.row.duplicate_in_file",
            "Another row earlier in this file already uses this identification.",
            nameof(ExcelCustomerRow.IdentificationNumber),
            ToRowData(row))));

        // Segunda pasada, solo sobre las filas que ya pasaron campo y deduplicacion: resolver
        // Departamento+Ciudad y Clasificacion contra la base. Esta si necesita infraestructura, y
        // por eso queda separada de la particion pura de arriba.
        foreach (var row in firstOccurrences)
        {
            // Sin documento (politica relajada) la fila no lleva par; las reglas ya descartaron las mitades.
            var hasDocument = !string.IsNullOrWhiteSpace(row.IdentificationType)
                && !string.IsNullOrWhiteSpace(row.IdentificationNumber);
            IdentificationType? type = hasDocument ? IdentificationTypeParser.Parse(row.IdentificationType) : null;
            var number = hasDocument ? row.IdentificationNumber!.Trim() : null;

            // Departamento y ciudad vacios son una fila sin ciudad (las reglas ya exigieron el par completo).
            var hasCity = !string.IsNullOrWhiteSpace(row.Department) && !string.IsNullOrWhiteSpace(row.City);
            var city = hasCity
                ? await geographyLookup.FindCityByNameAsync(
                    row.Department!.Trim(), row.City!.Trim(), cancellationToken)
                : null;
            if (hasCity && city is null)
            {
                errors.Add(new ImportRowError(
                    row.RowNumber,
                    "customers.import.row.city_not_found",
                    "The city was not found in the given department.",
                    null,
                    ToRowData(row)));
                continue;
            }

            // Opcional desde la decision del 2026-10-19 (Customer.cs): vacia es "sin clasificacion",
            // pero un nombre dado que no existe sigue siendo un error de la fila.
            ClientClassification? classification = null;
            if (!string.IsNullOrWhiteSpace(row.Classification))
            {
                classification = await classificationRepository.FindByNameAsync(
                    tenantId, row.Classification.Trim(), cancellationToken);
            }

            if (classification is null && !string.IsNullOrWhiteSpace(row.Classification))
            {
                errors.Add(new ImportRowError(
                    row.RowNumber,
                    "customers.import.row.classification_not_found",
                    "The client classification was not found in this tenant.",
                    nameof(ExcelCustomerRow.Classification),
                    ToRowData(row)));
                continue;
            }

            var withholdingTaxRateIds = Array.Empty<Guid>();
            if (!string.IsNullOrWhiteSpace(row.Retefuente) || !string.IsNullOrWhiteSpace(row.ReteIca))
            {
                withholdingRates ??= await taxRateLookup.ListWithholdingAsync(tenantId, cancellationToken);
                var (resolvedIds, withholdingError) = ResolveWithholdings(row, withholdingRates);
                if (withholdingError is not null)
                {
                    errors.Add(withholdingError);
                    continue;
                }

                withholdingTaxRateIds = resolvedIds;
            }

            candidates.Add(new CandidateRow(
                row.RowNumber,
                string.IsNullOrWhiteSpace(row.Cuc) ? null : row.Cuc.Trim(),
                row.Name!.Trim(),
                // Vacia es "no es una empresa", no una cadena vacia -- lo mismo que hace el
                // formulario. El agregado lo vuelve a normalizar igual.
                string.IsNullOrWhiteSpace(row.BusinessName) ? null : row.BusinessName.Trim(),
                type,
                number,
                row.Phone,
                row.Email,
                row.Address,
                city,
                classification,
                ParseSiNo(row.VatSurplus),
                withholdingTaxRateIds,
                null,
                row));
        }

        return (candidates, errors);
    }

    /// <summary>
    /// Las tasas del cliente que una actualizacion no toca: las de un tipo cuya columna no vino en el archivo. Una tasa que
    /// el catalogo ya no tiene (borrada) no tiene tipo que comparar y tambien se conserva: la importacion no borra lo que
    /// no sabe leer. Con las dos columnas presentes no consulta el catalogo.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> KeptWithholdingsAsync(
        Guid tenantId,
        Customer customer,
        IReadOnlyCollection<string> withholdingColumns,
        CancellationToken cancellationToken)
    {
        if (customer.WithholdingTaxRateIds.Count == 0
            || CustomerImportColumns.WithholdingTypes.All(withholdingColumns.Contains))
        {
            return [];
        }

        var rates = await taxRateLookup.FindManyAsync(tenantId, customer.WithholdingTaxRateIds, cancellationToken);
        return
        [
            .. customer.WithholdingTaxRateIds.Where(id =>
                !rates.TryGetValue(id, out var rate) || !withholdingColumns.Contains(rate.Type))
        ];
    }

    /// <summary>
    /// Resuelve las columnas por tipo (Retefuente, ReteICA) al id de la tasa del catalogo, por nombre y sin importar
    /// mayusculas. Una celda vacia no asigna tasa. Un nombre que no es una tasa activa de ese tipo es un error de la fila,
    /// con la columna como campo; el resultado pasa por las mismas reglas que el alta por API
    /// (<see cref="CustomerWithholdingRules"/>), asi el Excel no se salta ningun invariante.
    /// </summary>
    private static (Guid[] Ids, ImportRowError? Error) ResolveWithholdings(
        ExcelCustomerRow row, IReadOnlyList<CustomerTaxRateRef> activeWithholdingRates)
    {
        (string Type, string? Cell, string Field)[] columns =
        [
            (CustomerImportColumns.Retefuente, row.Retefuente, nameof(ExcelCustomerRow.Retefuente)),
            (CustomerImportColumns.ReteIca, row.ReteIca, nameof(ExcelCustomerRow.ReteIca))
        ];

        var chosen = new Dictionary<Guid, CustomerTaxRateRef>();
        foreach (var (type, cell, field) in columns)
        {
            if (string.IsNullOrWhiteSpace(cell))
            {
                continue;
            }

            // Dos paises pueden llamar igual a sus tasas; la importacion es colombiana, asi que gana la de CO.
            var matches = activeWithholdingRates
                .Where(rate => rate.Type == type && string.Equals(rate.Name, cell.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length > 1)
            {
                matches = [.. matches.Where(rate => rate.Country == Customer.ColombiaCountryCode)];
            }

            if (matches.Length != 1)
            {
                return ([], new ImportRowError(
                    row.RowNumber,
                    "customers.customer.withholding_tax_rate_invalid",
                    $"The {type} column must hold the name of an active {type} rate, or be empty.",
                    field,
                    ToRowData(row)));
            }

            chosen[matches[0].Id] = matches[0];
        }

        try
        {
            CustomerWithholdingRules.Validate(chosen, [.. chosen.Keys]);
        }
        catch (CustomersDomainException exception)
        {
            return ([], new ImportRowError(row.RowNumber, exception.Code, exception.Message, null, ToRowData(row)));
        }

        return ([.. chosen.Keys], null);
    }

    /// <summary>
    /// Las candidatas con Cuc son actualizaciones: resuelve cada una contra un cliente existente
    /// del tenant, en una sola consulta batch por sufijo estable (ver
    /// <see cref="Customer.StableSuffixOf"/>), no una por fila. Una candidata cuyo Cuc no matchea
    /// ningun cliente pasa a error <c>cuc_not_found</c> — el pedido es explicito: traer Cuc es
    /// pedir una actualizacion, y si no hay a quien actualizar la fila falla, no cae a crear.
    /// </summary>
    private async Task<List<CandidateRow>> ResolveCucTargetsAsync(
        Guid tenantId,
        List<CandidateRow> candidates,
        List<ImportRowError> errors,
        CancellationToken cancellationToken)
    {
        var suffixes = candidates
            .Where(candidate => candidate.Cuc is not null)
            .Select(candidate => Customer.StableSuffixOf(candidate.Cuc!))
            .Distinct()
            .ToArray();
        if (suffixes.Length == 0)
        {
            return candidates;
        }

        var targetIdsBySuffix = await customerRepository.FindIdsByCucSuffixAsync(
            tenantId, suffixes, cancellationToken);
        var survivorCucs = await SurvivorCucsAsync(tenantId, targetIdsBySuffix.Values, cancellationToken);

        var resolved = new List<CandidateRow>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (candidate.Cuc is null)
            {
                resolved.Add(candidate);
                continue;
            }

            if (targetIdsBySuffix.TryGetValue(Customer.StableSuffixOf(candidate.Cuc), out var targetId))
            {
                // Spec 2026-10-24 §3.3 (D-F6): el CUC impreso de un fusionado no lo actualiza. Se decide acá, antes de
                // RemoveExistingIdentificationsAsync, para que la fila diga «fusionado» y traiga el CUC del que quedó aunque
                // su documento sea el del que quedó (ese sería un identification_taken que manda a corregir el campo que
                // está bien). También va antes del chequeo de inactivo: un fusionado siempre lo está.
                if (survivorCucs.TryGetValue(targetId, out var survivorCuc))
                {
                    errors.Add(new ImportRowError(
                        candidate.RowNumber,
                        "customers.import.row.customer_merged",
                        $"The customer with that Cuc was merged into {survivorCuc}.",
                        nameof(ExcelCustomerRow.Cuc),
                        ToRowData(candidate.SourceRow),
                        survivorCuc));
                    continue;
                }

                resolved.Add(candidate.WithTargetId(targetId));
            }
            else
            {
                errors.Add(new ImportRowError(
                    candidate.RowNumber,
                    "customers.import.row.cuc_not_found",
                    "No customer with that Cuc was found in this tenant.",
                    nameof(ExcelCustomerRow.Cuc),
                    ToRowData(candidate.SourceRow)));
            }
        }

        return resolved;
    }

    /// <summary>De los clientes que resolvieron un CUC, los fusionados con el CUC del que quedó (dos consultas batch, no una
    /// por fila). El que quedó siempre es completo (<c>survivor_incomplete</c>), así que trae CUC; si faltara, el campo va
    /// en null y la fila igual falla como fusionada.</summary>
    private async Task<IReadOnlyDictionary<CustomerId, string?>> SurvivorCucsAsync(
        Guid tenantId, IEnumerable<CustomerId> targetIds, CancellationToken cancellationToken)
    {
        var targets = await customerRepository.FindManyAsync(tenantId, targetIds.Distinct().ToArray(), cancellationToken);
        var survivorOf = targets.Values
            .Where(customer => customer.MergedIntoId.HasValue)
            .ToDictionary(customer => customer.Id, customer => customer.MergedIntoId.GetValueOrDefault());
        if (survivorOf.Count == 0)
        {
            return new Dictionary<CustomerId, string?>();
        }

        var survivors = await customerRepository.FindManyAsync(tenantId, survivorOf.Values.Distinct().ToArray(), cancellationToken);
        return survivorOf.ToDictionary(
            pair => pair.Key,
            pair => survivors.TryGetValue(pair.Value, out var survivor) ? survivor.Cuc : null);
    }

    /// <summary>
    /// El chequeo de duplicados **contra la base**, en una sola consulta batch sobre todas las
    /// filas que pasaron <see cref="ValidateRowsAsync"/> — no una consulta por fila. Las que ya
    /// existen se sacan de la lista de candidatas y pasan a errores.
    /// </summary>
    private async Task<List<CandidateRow>> RemoveExistingIdentificationsAsync(
        Guid tenantId,
        List<CandidateRow> candidates,
        List<ImportRowError> errors,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        // Las filas sin documento no chocan con nadie: no se consultan.
        var withDocument = candidates
            .Where(candidate => candidate.Type is not null && candidate.Number is not null)
            .Select(candidate => (candidate.Type!.Value, candidate.Number!))
            .ToArray();
        if (withDocument.Length == 0)
        {
            return candidates;
        }

        var existing = await customerRepository.FindExistingIdentificationsAsync(
            tenantId, withDocument, cancellationToken);
        if (existing.Count == 0)
        {
            return candidates;
        }

        var stillValid = new List<CandidateRow>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // Una fila de actualizacion que conserva su propia identificacion no esta "tomada":
            // el dueno existente y el objetivo de esta fila (TargetId) son el mismo cliente. Para
            // una fila de creacion, TargetId siempre es null, asi que cualquier dueno encontrado
            // cuenta como "otro" — mismo comportamiento que antes.
            if (candidate.Type is { } type && candidate.Number is { } number &&
                existing.TryGetValue((type, number), out var ownerId) &&
                ownerId != candidate.TargetId)
            {
                errors.Add(new ImportRowError(
                    candidate.RowNumber,
                    "customers.import.row.identification_taken",
                    "Another customer in this tenant already uses that identification.",
                    nameof(ExcelCustomerRow.IdentificationNumber),
                    ToRowData(candidate.SourceRow)));
            }
            else
            {
                stillValid.Add(candidate);
            }
        }

        return stillValid;
    }

    /// <summary>
    /// Carga (ya con los bloqueos tomados, si los hay) el cliente que cada fila con Cuc actualiza, y saca de la lista las filas
    /// que no se pueden escribir: un inactivo, o una jurídica que la fila querría pasar a otro documento que no es NIT. Va antes
    /// de decidir las empresas porque un cliente que no se escribe no puede ser contacto ni empresa de nadie.
    /// </summary>
    private async Task<(List<CandidateRow> Eligible, Dictionary<CustomerId, Customer> Loaded)> LoadUpdateTargetsAsync(
        Guid tenantId,
        List<CandidateRow> candidates,
        List<ImportRowError> errors,
        CancellationToken cancellationToken)
    {
        var eligible = new List<CandidateRow>(candidates.Count);
        var loaded = new Dictionary<CustomerId, Customer>();
        foreach (var candidate in candidates)
        {
            if (candidate.TargetId is not { } targetId)
            {
                eligible.Add(candidate);
                continue;
            }

            var customer = await customerRepository.FindAsync(tenantId, targetId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Customer '{targetId}' resolved by Cuc suffix was not found when applying the update.");

            // Un fusionado ya salió como customer_merged en ResolveCucTargetsAsync (spec 2026-10-24 §3.3).
            // Mismo chequeo que UpdateCustomerHandler hace via Customer.Update, pero
            // adelantado: Update() lanza CustomersDomainException si el cliente esta
            // inactivo, y esta funcion no tiene un try/catch por fila — dejarlo pasar
            // abortaria el import entero por una sola fila en vez de reportarla como error.
            if (!customer.IsActive)
            {
                errors.Add(new ImportRowError(
                    candidate.RowNumber,
                    "customers.import.row.customer_inactive",
                    "The customer with that Cuc is inactive and cannot be edited.",
                    nameof(ExcelCustomerRow.Cuc),
                    ToRowData(candidate.SourceRow)));
                continue;
            }

            // Una jurídica se identifica con NIT: Update lanzaría la excepción de dominio y, como arriba,
            // no hay try/catch por fila — se adelanta para reportar sólo esa fila (spec 2026-10-26).
            if (customer.PersonType == PersonType.Juridica && (candidate.Type ?? customer.IdentificationType) is { } rowType && rowType != IdentificationType.Nit)
            {
                errors.Add(new ImportRowError(
                    candidate.RowNumber,
                    "customers.import.row.person_type_requires_nit",
                    "The customer is a legal entity and is identified by its NIT.",
                    nameof(ExcelCustomerRow.IdentificationType),
                    ToRowData(candidate.SourceRow)));
                continue;
            }

            loaded[targetId] = customer;
            eligible.Add(candidate);
        }

        return (eligible, loaded);
    }

    /// <summary>
    /// Con las filas que quedaron realmente validas: las que no tienen <c>TargetId</c> crean
    /// (reservan su bloque de CUCs de una sola vez con
    /// <see cref="ICucGenerator.NextBatchAsync"/>, asignados en el mismo orden en que las filas
    /// aparecen en el archivo) y las que si tienen actualizan ese cliente
    /// (<see cref="Customer.Update"/>, mismo reemplazo total que usa el PUT de un cliente). Un
    /// solo recorrido en orden de archivo para que <c>imported</c> salga en ese mismo orden sin
    /// importar la mezcla de creaciones y actualizaciones. Devuelve también el cliente de cada fila, para vincular después.
    /// </summary>
    private async Task<(List<ImportedCustomerRow> Imported, Dictionary<int, Customer> CustomersByRow)> CreateOrUpdateCustomersAsync(
        Guid tenantId,
        List<CandidateRow> candidates,
        Dictionary<CustomerId, Customer> loaded,
        IReadOnlyCollection<string> withholdingColumns,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var imported = new List<ImportedCustomerRow>(candidates.Count);
        var customersByRow = new Dictionary<int, Customer>(candidates.Count);
        if (candidates.Count == 0)
        {
            return (imported, customersByRow);
        }

        var createCount = candidates.Count(candidate => candidate.TargetId is null);
        var nextSequence = createCount > 0
            ? await cucGenerator.NextBatchAsync(tenantId, createCount, cancellationToken)
            : 0L;

        foreach (var candidate in candidates)
        {
            if (candidate.TargetId is { } targetId)
            {
                var customer = loaded[targetId];

                // Una celda de clasificacion vacia en una actualizacion conserva la que el cliente ya tiene: la columna es
                // opcional desde el 2026-10-19 y reimportar un archivo sin ella no puede borrarla ni quitarle el prefijo al
                // CUC. El prefijo se relee porque Update lo exige siempre que haya clasificacion, aunque no cambie el CUC.
                var classification = candidate.Classification;
                if (classification is null && customer.ClassificationId is { } currentClassificationId)
                {
                    classification = await classificationRepository.FindAsync(
                        tenantId, currentClassificationId, cancellationToken);
                }

                // La fila actualiza el domicilio del cliente (address, ciudad) y el resto de la
                // ficha; la libreta de envio no se toca, mismo criterio que UpdateCustomerHandler
                // (spec 2026-09-18).
                customer.Update(
                    candidate.Name,
                    candidate.BusinessName,
                    // Documento vacio en una actualizacion = no enviado: conserva el guardado (ni lo borra ni lo exige).
                    ToIdentification(candidate)
                        ?? (customer is { IdentificationType: { } storedType, IdentificationNumber: { } storedNumber }
                            ? new CustomerIdentification { Type = storedType, Number = storedNumber }
                            : null),
                    // La planilla no trae tipo de persona: reimportar no borra el que ya tenía (spec 2026-10-26).
                    customer.PersonType,
                    // La importacion masiva es **solo colombiana**: sus columnas son Departamento y
                    // Ciudad, resueltas contra DIVIPOLA por FindCityByNameAsync, y la plantilla
                    // valida la ciudad con una lista cerrada que depende del departamento elegido.
                    // Un cliente de afuera se da de alta por el formulario, no por Excel.
                    new CustomerContactInfo
                    {
                        Phone = candidate.Phone,
                        Email = candidate.Email,
                        Address = candidate.Address ?? string.Empty,
                        // Sin departamento ni ciudad en la fila se conserva el carril de ciudad guardado (pais, CityId, CityName).
                        Country = candidate.City is null ? customer.Country ?? Customer.ColombiaCountryCode : Customer.ColombiaCountryCode,
                        CityId = candidate.City is null ? customer.CityId : candidate.City.CityId,
                        CityName = candidate.City is null ? customer.CityName : null
                    },
                    new CustomerCommercialInfo
                    {
                        ClassificationId = classification?.Id,
                        VatSurplus = candidate.VatSurplus
                    },
                    classification?.Prefix,
                    now,
                    phoneNormalizer);

                // Celda vacia = sin tasa de ese tipo. Una columna que el archivo no trae (armado antes de que existiera, o
                // con solo una de las dos) conserva las tasas de ese tipo que el cliente ya tiene.
                customer.SetWithholdingTaxRates(
                    [
                        .. candidate.WithholdingTaxRateIds,
                        .. await KeptWithholdingsAsync(tenantId, customer, withholdingColumns, cancellationToken)
                    ],
                    now);

                imported.Add(new ImportedCustomerRow(
                    // Update exige una ficha completa (EnsureComplete), y una completa siempre tiene CUC.
                    candidate.RowNumber, customer.Cuc!, candidate.Name, "updated"));
                customersByRow[candidate.RowNumber] = customer;
                continue;
            }

            var sequence = nextSequence;
            nextSequence++;
            var cuc = CucFormatter.Build(
                candidate.Classification?.Prefix,
                candidate.City?.DepartmentDivipolaCode ?? CucFormatter.ForeignDepartmentCode,
                sequence);

            var newCustomer = Customer.Create(
                CustomerId.New(),
                tenantId,
                cuc,
                candidate.Name,
                candidate.BusinessName,
                // Sin ciudad no hay libreta: nace sin primera direccion, como en el formulario.
                CustomerMapping.ToFirstAddress(candidate.Name, candidate.Address, candidate.City, candidate.Phone),
                ToIdentification(candidate),
                // La planilla no trae tipo de persona (spec 2026-10-26).
                null,
                // Solo colombiana, por lo mismo que la rama de actualizacion de arriba.
                new CustomerContactInfo
                {
                    Phone = candidate.Phone,
                    Email = candidate.Email,
                    Address = candidate.Address ?? string.Empty,
                    Country = Customer.ColombiaCountryCode,
                    CityId = candidate.City?.CityId
                },
                new CustomerCommercialInfo
                {
                    ClassificationId = candidate.Classification?.Id,
                    VatSurplus = candidate.VatSurplus
                },
                now,
                phoneNormalizer);

            if (candidate.WithholdingTaxRateIds.Count > 0)
            {
                newCustomer.SetWithholdingTaxRates(candidate.WithholdingTaxRateIds, now);
            }

            customerRepository.Add(newCustomer);
            imported.Add(new ImportedCustomerRow(candidate.RowNumber, cuc, candidate.Name, "created"));
            customersByRow[candidate.RowNumber] = newCustomer;
        }

        return (imported, customersByRow);
    }

    /// <summary>El campo de los errores de la columna «Empresa» (G6): el que el modal de errores marca y el frontend traduce.</summary>
    private const string CompanyField = "companyIdentificationNumber";

    /// <summary>
    /// Los ids que el vínculo bloquea (<see cref="ICustomerRepository.LockForCompanyLinkAsync"/>): los dueños de cada número
    /// nombrado —las posibles empresas— y todos los clientes existentes que el archivo actualiza. Todo en una sola llamada, que ordena por id.
    /// </summary>
    private static Guid[] LockIdsFor(
        List<CandidateRow> candidates, IReadOnlyDictionary<string, IReadOnlyList<CustomerId>> ownersByNumber)
    {
        var ids = new HashSet<Guid>(ownersByNumber.Values.SelectMany(owners => owners).Select(id => id.Value));
        foreach (var candidate in candidates)
        {
            // Todo cliente que el archivo actualiza, no sólo los que se vuelven contactos: una operación concurrente que
            // bloquee estas filas en otro orden (otra importación, una fusión) se serializa con ésta en vez de abrazarse.
            if (candidate.TargetId is { } targetId)
            {
                ids.Add(targetId.Value);
            }
        }

        return [.. ids];
    }

    /// <summary>Una fila con su empresa decidida: la empresa es otra fila de este archivo, o un cliente que ya existía.</summary>
    private sealed record CompanyLink(CandidateRow Contact, CandidateRow? CompanyRow, Customer? CompanyEntity);

    /// <summary>
    /// Spec 2026-11-10 §4.8. Decide, mirando **todas** las filas a la vez y sin escribir nada, qué filas con «Empresa» quedan
    /// vinculadas. Las reglas son las de <see cref="Customer.LinkToCompany"/>, en su mismo orden (ella las vuelve a hacer al
    /// aplicar el vínculo): una fila se rechaza por su empresa con un error de fila, y una fila rechazada no se escribe.
    ///
    /// Dos pasadas, para que el resultado no dependa del orden de las filas:
    /// (A) cada fila resuelve su empresa por el número, primero entre las filas del archivo y luego entre los clientes del
    /// tenant sin fusionar, y comprueba las reglas contra el estado ya bloqueado;
    /// (B) las que pasaron la A son «contactos en potencia»: quien nombre a uno de ellos como empresa rompe «un solo nivel» y se
    /// rechaza (A→B y B→A se rechazan las dos, y en C→X, Z→C sólo Z). Quien nombre a una fila que la A rechazó, que no se va a
    /// escribir, tampoco puede quedar vinculada a ella.
    /// </summary>
    private async Task<(List<CandidateRow> Writable, List<CompanyLink> Links)> PlanCompanyLinksAsync(
        Guid tenantId,
        List<CandidateRow> eligible,
        Dictionary<CustomerId, Customer> loaded,
        IReadOnlyDictionary<string, IReadOnlyList<CustomerId>> ownersByNumber,
        List<ImportRowError> errors,
        CancellationToken cancellationToken)
    {
        var withCell = eligible.Where(candidate => candidate.CompanyNumber is not null).ToArray();
        if (withCell.Length == 0)
        {
            return (eligible, []);
        }

        // El documento con el que cada fila va a quedar: el de la fila o, en una actualización sin documento, el guardado.
        string? NumberOf(CandidateRow candidate) =>
            candidate.Number
            ?? (candidate.TargetId is { } id && loaded.TryGetValue(id, out var stored) ? stored.IdentificationNumber : null);

        var rowsByNumber = eligible
            .Select(candidate => (Number: NumberOf(candidate), Row: candidate))
            .Where(pair => pair.Number is not null)
            .GroupBy(pair => pair.Number!)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Row).ToList());

        // Un cliente que el archivo actualiza se ve por el documento que va a tener, no por el que tiene en la base (el archivo
        // puede cambiárselo): por eso el dueño guardado de un número no cuenta como «otro cliente» si su fila está en el archivo.
        var inTheFile = eligible.Where(candidate => candidate.TargetId.HasValue).Select(candidate => candidate.TargetId!.Value).ToHashSet();

        List<CustomerId> OutsideTheFile(string number) =>
            ownersByNumber.TryGetValue(number, out var owners) ? [.. owners.Where(id => !inTheFile.Contains(id))] : [];

        var outsideIds = withCell.SelectMany(candidate => OutsideTheFile(candidate.CompanyNumber!)).Distinct().ToArray();
        var outside = outsideIds.Length == 0
            ? new Dictionary<CustomerId, Customer>()
            : new Dictionary<CustomerId, Customer>(await customerRepository.FindManyAsync(tenantId, outsideIds, cancellationToken));

        // Una consulta para todos los clientes existentes que la fila quiere volver contactos.
        var contactTargets = withCell
            .Where(candidate => candidate.TargetId.HasValue)
            .Select(candidate => candidate.TargetId!.Value.Value)
            .Distinct()
            .ToArray();
        var contactCounts = contactTargets.Length == 0
            ? new Dictionary<Guid, int>()
            : new Dictionary<Guid, int>(await companyDirectory.CountContactsAsync(tenantId, contactTargets, cancellationToken));

        var rejected = new HashSet<int>();
        void Reject(CandidateRow row, string suffix, string message)
        {
            rejected.Add(row.RowNumber);
            errors.Add(new ImportRowError(
                row.RowNumber, "customers.import.row." + suffix, message, CompanyField, ToRowData(row.SourceRow)));
        }

        var tentative = new List<CompanyLink>();
        foreach (var row in withCell)
        {
            var key = row.CompanyNumber!;
            if (NumberOf(row) == key)
            {
                Reject(row, "company_is_self", "A customer cannot be its own company.");
                continue;
            }

            var sameFile = rowsByNumber.GetValueOrDefault(key) ?? [];
            var elsewhere = OutsideTheFile(key);
            if (sameFile.Count + elsewhere.Count == 0)
            {
                Reject(row, "company_not_found", "No customer in this tenant or in this file has that document number.");
                continue;
            }

            if (sameFile.Count + elsewhere.Count > 1)
            {
                Reject(row, "company_ambiguous", "More than one customer has that document number, so the company is ambiguous.");
                continue;
            }

            var current = row.TargetId is { } targetId ? loaded[targetId] : null;
            var hasContacts = row.TargetId is { } contactId && contactCounts.ContainsKey(contactId.Value);

            if (sameFile.Count == 1)
            {
                var companyRow = sameFile[0];
                var companyNow = companyRow.TargetId is { } companyTarget ? loaded[companyTarget] : null;
                if (current?.CompanyId is { } linkedTo && linkedTo == companyRow.TargetId)
                {
                    tentative.Add(new CompanyLink(row, companyRow, null));
                }
                else if (companyNow?.CompanyId is not null)
                {
                    // Un cliente que ya es contacto y cuya celda vacía no lo desvincula: sigue siendo contacto.
                    Reject(row, "company_is_contact", "A contact cannot be the company of another customer.");
                }
                else if (hasContacts)
                {
                    Reject(row, "has_contacts", "A customer that has contacts cannot become a contact.");
                }
                else
                {
                    tentative.Add(new CompanyLink(row, companyRow, null));
                }

                continue;
            }

            var companyId = elsewhere[0];
            if (!outside.TryGetValue(companyId, out var company))
            {
                Reject(row, "company_not_found", "No customer in this tenant or in this file has that document number.");
            }
            else if (company.IsMerged)
            {
                // Como Customer.LinkToCompany: una empresa fusionada ya no existe, ni siquiera para el no-op de abajo.
                Reject(row, "company_not_found", "No customer in this tenant or in this file has that document number.");
            }
            else if (current?.CompanyId == companyId)
            {
                tentative.Add(new CompanyLink(row, null, company));
            }
            else if (!company.IsActive)
            {
                Reject(row, "company_inactive", "The company is inactive.");
            }
            else if (company.CompanyId is not null)
            {
                Reject(row, "company_is_contact", "A contact cannot be the company of another customer.");
            }
            else if (hasContacts)
            {
                Reject(row, "has_contacts", "A customer that has contacts cannot become a contact.");
            }
            else
            {
                tentative.Add(new CompanyLink(row, null, company));
            }
        }

        var rejectedByRule = rejected.ToHashSet();
        var contactsToBe = tentative.Select(link => link.Contact.RowNumber).ToHashSet();
        var links = new List<CompanyLink>(tentative.Count);
        foreach (var link in tentative)
        {
            if (link.CompanyRow is { } companyRow)
            {
                if (rejectedByRule.Contains(companyRow.RowNumber))
                {
                    Reject(link.Contact, "company_not_found", "The row of that company in this file was rejected, so it will not exist.");
                    continue;
                }

                if (contactsToBe.Contains(companyRow.RowNumber))
                {
                    Reject(link.Contact, "company_is_contact", "A contact cannot be the company of another customer.");
                    continue;
                }
            }

            links.Add(link);
        }

        return ([.. eligible.Where(candidate => !rejected.Contains(candidate.RowNumber))], links);
    }

    /// <summary>
    /// Los vínculos, después de que todos los clientes del archivo existen (los nuevos ya están en el contexto y los que se
    /// actualizan ya llevan sus cambios). Todo lo que podía fallar lo decidió <see cref="PlanCompanyLinksAsync"/> con los
    /// bloqueos puestos, así que aquí <see cref="Customer.LinkToCompany"/> no debería lanzar; si lo hiciera, la excepción
    /// sube, no se confirma la transacción y no queda nada escrito.
    /// </summary>
    private static void ApplyCompanyLinks(
        List<CompanyLink> links, Dictionary<int, Customer> customersByRow, DateTimeOffset now)
    {
        foreach (var link in links)
        {
            var contact = customersByRow[link.Contact.RowNumber];
            var company = link.CompanyEntity ?? customersByRow[link.CompanyRow!.RowNumber];
            // Un cliente con contactos ya se rechazó en la planificación (has_contacts): aquí nunca los tiene.
            contact.LinkToCompany(company, false, now);
        }
    }

    private static CustomerIdentification? ToIdentification(CandidateRow candidate) =>
        candidate.Type is { } type && candidate.Number is { } number
            ? new CustomerIdentification { Type = type, Number = number }
            : null;

    // "Si" es si; cualquier otra cosa —incluida la celda vacia— es no. Lo mismo para las dos
    // columnas booleanas: el validador ya rechazo todo lo que no sea Si/No/vacio, asi que aca no
    // queda ningun valor ambiguo por interpretar.
    private static bool ParseSiNo(string? raw) =>
        !string.IsNullOrWhiteSpace(raw)
        && string.Equals(raw.Trim(), "Si", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Los campos crudos de la fila, texto tal cual vino del Excel — nulo se vuelve cadena vacia
    /// solo en las columnas que <see cref="CustomerImportRowData"/> declara no-nulas, para poder
    /// adjuntarla a un error de campo obligatorio (donde esa misma columna es justamente la que
    /// esta vacia). El resto de las columnas viaja tal cual, nulo incluido.
    /// </summary>
    private static CustomerImportRowData ToRowData(ExcelCustomerRow row) => new(
        row.Cuc,
        row.Name ?? string.Empty,
        row.BusinessName,
        row.IdentificationType ?? string.Empty,
        row.IdentificationNumber ?? string.Empty,
        row.Phone,
        row.Email,
        row.Address,
        row.Department ?? string.Empty,
        row.City ?? string.Empty,
        row.Classification ?? string.Empty,
        row.Retefuente,
        row.ReteIca,
        row.VatSurplus,
        row.CompanyIdentificationNumber);

    private static string EnsureAcceptableFile(string fileName, long sizeInBytes)
    {
        var trimmed = fileName?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new CustomersDomainException(
                "customers.import.file_required",
                "An Excel file is required.");
        }

        if (!AcceptedExtensions.Any(extension =>
                trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CustomersDomainException(
                "customers.import.file_type_invalid",
                "Only .xlsx and .xls files are accepted.");
        }

        if (sizeInBytes <= 0)
        {
            throw new CustomersDomainException(
                "customers.import.file_empty",
                "The uploaded file is empty.");
        }

        return sizeInBytes > MaxSizeInBytes
            ? throw new CustomersDomainException(
                "customers.import.file_too_large",
                "The uploaded file cannot exceed 10 MB.")
            : trimmed;
    }

    /// <summary>
    /// Una fila ya resuelta: campos validos, ciudad y clasificacion encontradas, sin duplicado
    /// dentro del archivo. Todavia le falta el chequeo de duplicado contra la base y el CUC — por
    /// eso no es todavia un <see cref="Customer"/>.
    /// </summary>
    // `SourceRow`: la fila cruda de origen, guardada solo para poder adjuntar
    // `CustomerImportRowData` a un `identification_taken` — el unico error que se detecta
    // despues de que la fila ya paso a candidata.
    // `Cuc`: el valor crudo de la columna Cuc, recortado — null si la fila crea. `TargetId` lo
    // llena `ResolveCucTargetsAsync` una vez que matchea contra un cliente existente; hasta
    // entonces viaja null aunque `Cuc` no lo sea.
    private sealed record CandidateRow(
        int RowNumber,
        string? Cuc,
        string Name,
        string? BusinessName,
        IdentificationType? Type,
        string? Number,
        string? Phone,
        string? Email,
        string? Address,
        CustomerCityRef? City,
        ClientClassification? Classification,
        bool VatSurplus,
        IReadOnlyList<Guid> WithholdingTaxRateIds,
        CustomerId? TargetId,
        ExcelCustomerRow SourceRow)
    {
        public CandidateRow WithTargetId(CustomerId targetId) => this with { TargetId = targetId };

        /// <summary>El número de la columna «Empresa», recortado; null si la celda venía vacía o el archivo no trae la columna.</summary>
        public string? CompanyNumber =>
            string.IsNullOrWhiteSpace(SourceRow.CompanyIdentificationNumber) ? null : SourceRow.CompanyIdentificationNumber.Trim();
    }
}

/// <summary>
/// Detecta duplicados de identificacion **dentro del archivo**: entre dos o mas filas con el mismo
/// tipo y numero de identificacion (normalizado: recortado, como hace
/// <see cref="CustomerIdentification"/>), solo la primera que aparece en el archivo es candidata
/// valida — las siguientes son el duplicado.
///
/// Pura, sin infraestructura ni acceso a la base: es la pieza reutilizable y testeable sin
/// levantar el modulo entero, mismo criterio que <see cref="CucFormatter"/>. Opera solo sobre
/// filas que **ya pasaron** <see cref="ExcelCustomerRowRules"/> — con <c>IdentificationType</c> e
/// <c>IdentificationNumber</c> garantizados no vacios, puede parsear el tipo sin volver a validar.
/// </summary>
public static class ExcelCustomerRowDeduplicator
{
    public static (
        IReadOnlyList<ExcelCustomerRow> FirstOccurrences, IReadOnlyList<ExcelCustomerRow> Duplicates)
        Partition(IReadOnlyList<ExcelCustomerRow> fieldValidRows)
    {
        var seen = new HashSet<(IdentificationType Type, string Number)>();
        var firstOccurrences = new List<ExcelCustomerRow>();
        var duplicates = new List<ExcelCustomerRow>();

        foreach (var row in fieldValidRows)
        {
            // Sin documento no hay nada que duplicar: dos filas sin documento conviven.
            if (string.IsNullOrWhiteSpace(row.IdentificationType)
                || string.IsNullOrWhiteSpace(row.IdentificationNumber))
            {
                firstOccurrences.Add(row);
                continue;
            }

            var type = IdentificationTypeParser.Parse(row.IdentificationType);
            var number = row.IdentificationNumber.Trim();

            if (seen.Add((type, number)))
            {
                firstOccurrences.Add(row);
            }
            else
            {
                duplicates.Add(row);
            }
        }

        return (firstOccurrences, duplicates);
    }
}
