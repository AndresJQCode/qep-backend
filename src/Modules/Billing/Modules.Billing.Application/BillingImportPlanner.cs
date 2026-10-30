using System.Globalization;
using Modules.Billing.Domain;

namespace Modules.Billing.Application;

/// <summary>
/// Lo que el plan necesita saber de la base, ya resuelto: los clientes que encontró el emparejamiento por fila, quién ya tiene
/// perfil, los servicios <c>Active</c>/<c>Paused</c> de cada cliente (por nombre) y los encargados posibles por correo en
/// minúsculas (sólo miembros activos con <c>billing.service.manage</c>). <c>NewCustomerKeys</c> es el cliente nuevo de cada fila
/// sin emparejar, ya enlazado con el de una fila anterior si es el mismo; sin él, la clave de la propia fila.
/// </summary>
public sealed record BillingImportContext(
    DateOnly Today,
    DateTimeOffset Now,
    string DefaultCurrency,
    IReadOnlyDictionary<int, IReadOnlyList<BillingCustomerRef>> MatchesByRow,
    IReadOnlyCollection<Guid> CustomersWithProfile,
    IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> OpenServiceNamesByCustomer,
    IReadOnlyDictionary<string, BillingMemberRef> ManagersByEmail,
    IReadOnlyDictionary<int, string>? NewCustomerKeys = null);

/// <summary>
/// Una fila que se va a importar. <c>MatchedCustomerId</c> null es «se crea»; <c>NewCustomerKey</c> agrupa las filas del mismo
/// cliente nuevo para darlo de alta una sola vez. <c>Channel</c> y <c>AssigneeMemberId</c> sólo vienen si la fila crea el perfil.
/// </summary>
public sealed record PlannedBillingRow(
    BillingImportRow Row,
    Guid? MatchedCustomerId,
    string CustomerName,
    string? NewCustomerKey,
    bool CreatesProfile,
    BillingChannel? Channel,
    Guid? AssigneeMemberId,
    string Currency,
    int ChargesToGenerate,
    int OverdueCharges)
{
    public ImportRowPreviewDto ToPreview() => new(
        Row.RowNumber,
        new ImportCustomerPreviewDto(CustomerName, MatchedCustomerId, MatchedCustomerId is null ? "create" : "match"),
        CreatesProfile ? "create" : "kept",
        new ImportServicePreviewDto(Row.ServiceName, Row.Price, Currency, Row.Cycle.ToString(), Row.FirstDueDate),
        ChargesToGenerate,
        OverdueCharges);
}

public sealed record BillingImportPlan(
    IReadOnlyList<PlannedBillingRow> Rows,
    IReadOnlyList<ImportRowErrorDto> Errors,
    ImportPreviewSummaryDto Summary);

/// <summary>
/// El plan de la importación (§3.2b), puro: qué cliente usa cada fila, si crea el perfil, con qué canal y encargado, y cuántos
/// cobros nacen (con <see cref="ChargeGenerator"/> en seco, la misma regla del alta). Una fila con un problema sale como error y
/// no frena a las demás. La vista previa y la importación corren el mismo plan, así que no pueden divergir.
/// </summary>
public static class BillingImportPlanner
{
    public static BillingImportPlan Plan(IReadOnlyList<BillingImportRow> rows, BillingImportContext context)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(context);
        var planned = new List<PlannedBillingRow>(rows.Count);
        var errors = new List<ImportRowErrorDto>();
        // Clientes que ya van a tener perfil por una fila anterior de este archivo: la segunda fila lo conserva.
        var profilesPlanned = new HashSet<string>(StringComparer.Ordinal);
        // Clientes nuevos que ya da de alta una fila anterior planeada: la que se suma no necesita traer teléfono propio.
        var customersPlanned = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var outcome = PlanRow(row, context, profilesPlanned, customersPlanned);
            if (outcome.Error is { } error)
            {
                errors.Add(error);
            }
            else
            {
                planned.Add(outcome.Row!);
            }
        }

        var summary = new ImportPreviewSummaryDto(
            planned.Where(row => row.NewCustomerKey is not null).Select(row => row.NewCustomerKey).Distinct().Count(),
            planned.Where(row => row.MatchedCustomerId is not null).Select(row => row.MatchedCustomerId).Distinct().Count(),
            planned.Count,
            planned.Sum(row => row.ChargesToGenerate),
            planned.Sum(row => row.OverdueCharges));
        return new BillingImportPlan(planned, errors, summary);
    }

    private static (PlannedBillingRow? Row, ImportRowErrorDto? Error) PlanRow(
        BillingImportRow row, BillingImportContext context, HashSet<string> profilesPlanned, HashSet<string> customersPlanned)
    {
        var matches = context.MatchesByRow.TryGetValue(row.RowNumber, out var found) ? found : [];
        if (matches.Count > 1)
        {
            return Fail(row, BillingErrorCodes.ImportRowCustomerAmbiguous, "More than one customer matches this row.", null);
        }

        var matched = matches.Count == 1 ? matches[0] : null;
        if (matched is { IsActive: false })
        {
            return Fail(row, BillingErrorCodes.ImportRowCustomerInactive, "The matched customer is inactive.", null);
        }

        // Crear exige teléfono, así que una fila nueva que crea siempre tiene clave; el número de fila es sólo una red.
        var customerKey = matched is not null
            ? matched.Id.ToString()
            : context.NewCustomerKeys?.GetValueOrDefault(row.RowNumber)
                ?? row.CustomerKey
                ?? $"row:{row.RowNumber.ToString(CultureInfo.InvariantCulture)}";

        if (matched is null && row.PhoneE164 is null && !customersPlanned.Contains(customerKey))
        {
            return Fail(row, BillingErrorCodes.ImportRowPhoneRequired, "A new customer needs a phone.", nameof(ExcelBillingRow.Phone));
        }

        if (matched is not null
            && context.OpenServiceNamesByCustomer.TryGetValue(matched.Id, out var openNames)
            && openNames.Any(name => BillingImportValues.NormalizeServiceName(name) == row.ServiceKey))
        {
            return Fail(
                row,
                BillingErrorCodes.ImportRowServiceExists,
                "The customer already has an active or paused service with that name.",
                nameof(ExcelBillingRow.ServiceName));
        }

        var hasProfile = (matched is not null && context.CustomersWithProfile.Contains(matched.Id))
            || profilesPlanned.Contains(customerKey);

        BillingChannel? channel = null;
        Guid? assignee = null;
        if (!hasProfile)
        {
            // Lo que el perfil valida al guardarse (§3.1), contra el contacto que el cliente va a tener: el de la fila si se
            // crea, el suyo si ya existe (la importación no le cambia los datos a un cliente emparejado).
            var phone = matched is null ? row.PhoneE164 : matched.PhoneE164;
            var email = matched is null ? row.Email : matched.Email;
            var resolved = row.Channel ?? (string.IsNullOrWhiteSpace(phone) ? BillingChannel.Email : BillingChannel.WhatsApp);
            if (resolved == BillingChannel.WhatsApp && string.IsNullOrWhiteSpace(phone))
            {
                return Fail(row, BillingErrorCodes.ImportRowPhoneRequired, "The WhatsApp channel needs a phone.", nameof(ExcelBillingRow.Phone));
            }

            if (resolved == BillingChannel.Email && string.IsNullOrWhiteSpace(email))
            {
                return Fail(row, BillingErrorCodes.ImportRowEmailRequired, "The Email channel needs an email.", nameof(ExcelBillingRow.Email));
            }

            if (!context.ManagersByEmail.TryGetValue(row.AssigneeEmail, out var member) || member is not { IsActive: true, CanManage: true })
            {
                return Fail(
                    row,
                    BillingErrorCodes.ImportRowAssigneeInvalid,
                    "No active member who can manage billing has that email.",
                    nameof(ExcelBillingRow.AssigneeEmail));
            }

            channel = resolved;
            assignee = member.MemberId;
            profilesPlanned.Add(customerKey);
        }

        var currency = row.Currency ?? context.DefaultCurrency;
        // Nombre, precio y cliente no cambian cuántos cobros nacen; el servicio transitorio nunca se guarda.
        var transient = BillingService.Create(
            BillingServiceId.New(), Guid.Empty, Guid.Empty, "preview", 1m, currency, row.Cycle, row.FirstDueDate, context.Now);
        var charges = ChargeGenerator.Generate(transient, context.Today, context.Now);
        if (matched is null)
        {
            customersPlanned.Add(customerKey);
        }

        return (new PlannedBillingRow(
            row,
            matched?.Id,
            matched?.Name ?? row.CustomerName,
            matched is null ? customerKey : null,
            !hasProfile,
            channel,
            assignee,
            currency,
            charges.Count,
            charges.Count(charge => charge.IsOverdue(context.Today))), null);
    }

    private static (PlannedBillingRow?, ImportRowErrorDto?) Fail(BillingImportRow row, string code, string message, string? field) =>
        (null, new ImportRowErrorDto(row.RowNumber, code, message, field));
}
