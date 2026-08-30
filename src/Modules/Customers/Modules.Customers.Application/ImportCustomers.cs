using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

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

    public ExcelCustomerRowRules()
    {
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
        RuleFor(row => row.IdentificationType)
            .NotEmpty()
            .WithErrorCode("customers.import.row.identification_type_required")
            .WithMessage("The identification type is required.");

        RuleFor(row => row.IdentificationType)
            .Must(IsSupportedIdentificationType)
            .WithErrorCode("customers.import.row.identification_type_invalid")
            .WithMessage(
                $"The identification type must be one of " +
                $"{Join(IdentificationTypeParser.SupportedWireValues)}.")
            .When(row => !string.IsNullOrWhiteSpace(row.IdentificationType));

        RuleFor(row => row.IdentificationNumber)
            .NotEmpty()
                .WithErrorCode("customers.import.row.identification_number_required")
                .WithMessage("The identification number is required.")
            .MaximumLength(CustomerIdentification.NumberMaxLength)
                .WithErrorCode("customers.import.row.identification_number_too_long")
                .WithMessage(
                    "The identification number cannot exceed " +
                    $"{CustomerIdentification.NumberMaxLength} characters.");

        RuleFor(row => row.Phone)
            .MaximumLength(CustomerContactInfo.PhoneMaxLength)
                .WithErrorCode("customers.import.row.phone_too_long")
                .WithMessage($"The phone cannot exceed {CustomerContactInfo.PhoneMaxLength} characters.");

        RuleFor(row => row.Address)
            .MaximumLength(CustomerContactInfo.AddressMaxLength)
                .WithErrorCode("customers.import.row.address_too_long")
                .WithMessage(
                    $"The address cannot exceed {CustomerContactInfo.AddressMaxLength} characters.");

        // Vacio es ausente para un campo opcional, mismo criterio que CustomerWriteRules: sin el
        // When(), una celda de correo vacia fallaria EmailAddress() y bloquearia una fila que
        // legitimamente no trae correo.
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
        RuleFor(row => row.Department)
            .NotEmpty()
                .WithErrorCode("customers.import.row.department_required")
                .WithMessage("The department is required.");

        RuleFor(row => row.City)
            .NotEmpty()
                .WithErrorCode("customers.import.row.city_required")
                .WithMessage("The city is required.");

        RuleFor(row => row.Classification)
            .NotEmpty()
                .WithErrorCode("customers.import.row.classification_required")
                .WithMessage("The classification is required.");

        RuleFor(row => row.WithRetention)
            .Must(BeSiOrNoOrEmpty)
                .WithErrorCode("customers.import.row.with_retention_invalid")
                .WithMessage("The 'Con Retencion' column must be 'Si', 'No' or empty.");
    }

    private static bool IsSupportedIdentificationType(string? value)
    {
        try
        {
            IdentificationTypeParser.Parse(value);
            return true;
        }
        catch (CustomersDomainException)
        {
            return false;
        }
    }

    private static bool BeSiOrNoOrEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) || SiNoValues.Contains(value.Trim().ToUpperInvariant());

    private static string Join(IReadOnlyCollection<string> values) =>
        string.Join(", ", values.Order(StringComparer.Ordinal));
}

public sealed class ImportCustomersHandler(
    ICustomerRepository customerRepository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICucGenerator cucGenerator,
    IExcelCustomerImporter excelImporter,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<ExcelCustomerRow> rowValidator)
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

        // Estructural, no de negocio: ¿el Excel tiene las diez columnas esperadas? Un archivo
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
        candidates = await RemoveExistingIdentificationsAsync(
            command.TenantId, candidates, errors, cancellationToken);

        var now = clock.UtcNow;
        var imported = await CreateCustomersAsync(command.TenantId, candidates, now, cancellationToken);

        // Un solo evento por el import completo, no uno por cliente: mil clientes importados de
        // una vez no deben dejar mil filas en el outbox. Los conteos van en el resourceId porque
        // ICustomersAuditPublisher.Publish no tiene un campo propio para ellos y el resto del
        // modulo ya usa ese parametro para identificar "sobre que fue esta accion".
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.imported",
            $"{fileName} (imported={imported.Count}, errors={errors.Count})",
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

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
            var type = IdentificationTypeParser.Parse(row.IdentificationType);
            var number = row.IdentificationNumber!.Trim();

            var city = await geographyLookup.FindCityByNameAsync(
                row.Department!.Trim(), row.City!.Trim(), cancellationToken);
            if (city is null)
            {
                errors.Add(new ImportRowError(
                    row.RowNumber,
                    "customers.import.row.city_not_found",
                    "The city was not found in the given department.",
                    null,
                    ToRowData(row)));
                continue;
            }

            var classification = await classificationRepository.FindByNameAsync(
                tenantId, row.Classification!.Trim(), cancellationToken);
            if (classification is null)
            {
                errors.Add(new ImportRowError(
                    row.RowNumber,
                    "customers.import.row.classification_not_found",
                    "The client classification was not found in this tenant.",
                    nameof(ExcelCustomerRow.Classification),
                    ToRowData(row)));
                continue;
            }

            candidates.Add(new CandidateRow(
                row.RowNumber,
                row.Name!.Trim(),
                type,
                number,
                row.Phone,
                row.Email,
                row.Address,
                city,
                classification,
                ParseWithRetention(row.WithRetention),
                row));
        }

        return (candidates, errors);
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

        var existing = await customerRepository.FindExistingIdentificationsAsync(
            tenantId,
            candidates.Select(candidate => (candidate.Type, candidate.Number)).ToArray(),
            cancellationToken);
        if (existing.Count == 0)
        {
            return candidates;
        }

        var stillValid = new List<CandidateRow>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (existing.Contains((candidate.Type, candidate.Number)))
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
    /// Con las filas que quedaron realmente validas: reserva el bloque de CUCs de una sola vez
    /// (<see cref="ICucGenerator.NextBatchAsync"/>) y los asigna en el mismo orden en que las filas
    /// aparecen en el archivo.
    /// </summary>
    private async Task<List<ImportedCustomerRow>> CreateCustomersAsync(
        Guid tenantId,
        List<CandidateRow> candidates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var imported = new List<ImportedCustomerRow>(candidates.Count);
        if (candidates.Count == 0)
        {
            return imported;
        }

        var firstSequence = await cucGenerator.NextBatchAsync(
            tenantId, candidates.Count, cancellationToken);

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var sequence = firstSequence + index;
            var cuc = CucFormatter.Build(
                candidate.Classification.Prefix, candidate.City.DepartmentDivipolaCode, sequence);

            var customer = Customer.Create(
                CustomerId.New(),
                tenantId,
                cuc,
                candidate.Name,
                candidate.City.CityId,
                new CustomerIdentification { Type = candidate.Type, Number = candidate.Number },
                new CustomerContactInfo
                {
                    Phone = candidate.Phone,
                    Email = candidate.Email,
                    Address = candidate.Address
                },
                new CustomerCommercialInfo
                {
                    ClassificationId = candidate.Classification.Id,
                    WithRetention = candidate.WithRetention
                },
                now);

            customerRepository.Add(customer);
            imported.Add(new ImportedCustomerRow(candidate.RowNumber, cuc, candidate.Name));
        }

        return imported;
    }

    private static bool ParseWithRetention(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && string.Equals(raw.Trim(), "Si", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Los campos crudos de la fila, texto tal cual vino del Excel — nulo se vuelve cadena vacia
    /// solo en las columnas que <see cref="CustomerImportRowData"/> declara no-nulas, para poder
    /// adjuntarla a un error de campo obligatorio (donde esa misma columna es justamente la que
    /// esta vacia). El resto de las columnas viaja tal cual, nulo incluido.
    /// </summary>
    private static CustomerImportRowData ToRowData(ExcelCustomerRow row) => new(
        row.Name ?? string.Empty,
        row.IdentificationType ?? string.Empty,
        row.IdentificationNumber ?? string.Empty,
        row.Phone,
        row.Email,
        row.Address,
        row.Department ?? string.Empty,
        row.City ?? string.Empty,
        row.Classification ?? string.Empty,
        row.WithRetention);

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
    private sealed record CandidateRow(
        int RowNumber,
        string Name,
        IdentificationType Type,
        string Number,
        string? Phone,
        string? Email,
        string? Address,
        CustomerCityRef City,
        ClientClassification Classification,
        bool WithRetention,
        ExcelCustomerRow SourceRow);
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
            var type = IdentificationTypeParser.Parse(row.IdentificationType);
            var number = row.IdentificationNumber!.Trim();

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
