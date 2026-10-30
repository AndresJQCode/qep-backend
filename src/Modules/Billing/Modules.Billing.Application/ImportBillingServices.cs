using System.Globalization;
using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

/// <summary>El Excel subido a <c>POST /import</c>: el mismo archivo de la vista previa (no hay staging, §3.2b).</summary>
public sealed record ImportBillingServicesCommand(Guid TenantId, string FileName, long SizeInBytes, Stream Content)
    : ICommand<ImportResultDto>;

/// <summary>
/// La importación (§3.2b): revalida el archivo entero con <see cref="BillingImportAnalyzer"/> y escribe las filas válidas, una
/// transacción por fila. Primero el cliente por <see cref="IBillingCustomerProvisioning"/> (unidad de trabajo de Customers),
/// después perfil + servicio + cobros en la de Billing. Ninguna fila válida se pierde por otra: lo que falla al escribir vuelve
/// como <c>billing.import.row.failed</c>. Si falla el segundo paso, el cliente recién creado queda (incompleto, inofensivo): la
/// misma deuda que <c>SDD-CT-07</c>, documentada y sin compensar.
/// </summary>
public sealed class ImportBillingServicesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    BillingImportAnalyzer analyzer,
    IBillingCustomerProvisioning provisioning,
    ICustomerBillingProfileRepository profiles,
    IBillingServiceRepository services,
    IChargeRepository charges,
    IBillingUnitOfWork unitOfWork,
    IBillingAuditPublisher auditPublisher,
    IBillingImportFailureLog failureLog,
    IClock clock)
    : ICommandHandler<ImportBillingServicesCommand, ImportResultDto>
{
    private static readonly string[] CompletedFields = ["rows", "imported", "customersCreated", "errors"];

    public async Task<ImportResultDto> HandleAsync(ImportBillingServicesCommand command, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, BillingPermissions.Manage, cancellationToken);

        var receivedAt = clock.UtcNow;
        var analysis = await analyzer.AnalyzeAsync(
            command.TenantId, command.FileName, command.SizeInBytes, command.Content, cancellationToken);

        var errors = new List<ImportRowErrorDto>(analysis.Errors);
        var imported = new List<ImportedRowDto>(analysis.Plan.Rows.Count);
        // El cliente nuevo que dio de alta una fila anterior: dos servicios del mismo cliente nuevo lo crean una sola vez.
        var createdByKey = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var customersCreated = 0;

        foreach (var planned in analysis.Plan.Rows)
        {
            var customer = await ResolveCustomerAsync(command.TenantId, planned, createdByKey, cancellationToken);
            if (customer.Error is { } customerError)
            {
                errors.Add(customerError);
                continue;
            }

            if (customer.Created)
            {
                customersCreated++;
            }

            var row = await WriteServiceAsync(command.TenantId, planned, customer.Id, customer.Created, analysis.Today, cancellationToken);
            if (row.Error is { } writeError)
            {
                errors.Add(writeError);
            }
            else
            {
                imported.Add(row.Imported!);
            }
        }

        var orderedErrors = errors.OrderBy(error => error.RowNumber).ToArray();
        var errorRows = orderedErrors.Select(error => error.RowNumber).Distinct().Count();
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            BillingAuditActions.ImportCompleted,
            BillingAuditActions.Resources.Import,
            string.Create(
                CultureInfo.InvariantCulture,
                $"rows={analysis.TotalRows};imported={imported.Count};customersCreated={customersCreated};errors={errorRows}"),
            "success",
            CompletedFields,
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ImportResultDto(
            analysis.FileName,
            receivedAt,
            analysis.TotalRows,
            imported.Count,
            errorRows,
            customersCreated,
            imported,
            orderedErrors);
    }

    private async Task<(Guid Id, bool Created, ImportRowErrorDto? Error)> ResolveCustomerAsync(
        Guid tenantId, PlannedBillingRow planned, Dictionary<string, Guid> createdByKey, CancellationToken cancellationToken)
    {
        if (planned.MatchedCustomerId is { } matched)
        {
            return (matched, false, null);
        }

        var key = planned.NewCustomerKey!;
        if (createdByKey.TryGetValue(key, out var existing))
        {
            return (existing, false, null);
        }

        var row = planned.Row;
        try
        {
            var provisioned = await provisioning.CreateAsync(
                tenantId,
                executionContext.SubjectId,
                new BillingNewCustomer(
                    row.CustomerName, row.IdentificationType, row.IdentificationNumber, row.PhoneE164, row.Email, row.Country),
                cancellationToken);
            createdByKey[key] = provisioned.Id;
            return (provisioned.Id, provisioned.Created, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // El adaptador de Customers ya limpió su contexto; el de Billing no tiene nada de esta fila, pero se limpia igual
            // para que ninguna fila herede lo que otra dejó a medias.
            unitOfWork.Clear();
            return (Guid.Empty, false, Failed(tenantId, row, "customer", "The customer could not be created", exception));
        }
    }

    private async Task<(ImportedRowDto? Imported, ImportRowErrorDto? Error)> WriteServiceAsync(
        Guid tenantId, PlannedBillingRow planned, Guid customerId, bool customerCreated, DateOnly today, CancellationToken cancellationToken)
    {
        var row = planned.Row;
        try
        {
            var now = clock.UtcNow;
            // Se vuelve a mirar: otra fila de este archivo pudo haber creado el perfil del mismo cliente, o haber fallado al
            // crearlo. Por eso toda fila de un cliente sin perfil trae el canal y el encargado, no sólo la que lo planeó.
            if (planned is { Channel: { } channel, AssigneeMemberId: { } assignee }
                && await profiles.FindAsync(tenantId, customerId, cancellationToken) is null)
            {
                profiles.Add(CustomerBillingProfile.Create(tenantId, customerId, channel, assignee, now));
            }

            var service = BillingService.Create(
                BillingServiceId.New(), tenantId, customerId, row.ServiceName, row.Price, planned.Currency, row.Cycle, row.FirstDueDate, now);
            services.Add(service);
            var generated = ChargeGenerator.Generate(service, today, now);
            charges.AddRange(generated);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return (new ImportedRowDto(
                row.RowNumber,
                service.Id.Value,
                customerId,
                customerCreated,
                generated.Count,
                generated.Count(charge => charge.IsOverdue(today))), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, Failed(tenantId, row, "service", "The service could not be saved", exception));
        }
        finally
        {
            // Una transacción por fila: lo que quedó en el contexto (lo guardado o lo que falló) no viaja a la siguiente.
            unitOfWork.Clear();
        }
    }

    // El código de una regla de dominio dice qué pasó y viaja en el mensaje de la fila. Cualquier otra excepción es un error del
    // servidor: su mensaje puede traer detalles de la base, así que no viaja, y sin el log nadie sabría qué falló.
    private ImportRowErrorDto Failed(Guid tenantId, BillingImportRow row, string stage, string what, Exception exception)
    {
        if (exception is DomainException domain)
        {
            return new(row.RowNumber, BillingErrorCodes.ImportRowFailed, $"{what}: {domain.Code}.", null);
        }

        failureLog.RowFailed(tenantId, row.RowNumber, stage, exception);
        return new(row.RowNumber, BillingErrorCodes.ImportRowFailed, $"{what}.", null);
    }
}
