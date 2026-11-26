using System.Text.Json;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// El antes y el después de cada acción del historial (<c>hr.employee_activity</c>). Puro: sólo compara y arma
/// <see cref="ActivityChange"/>. Campos en camelCase; sólo los que cambian; las referencias van por el <b>nombre</b> que tenían al
/// escribirse (<c>names</c>), no por el id; montos como número, fechas <c>yyyy-MM-dd</c>, enums por nombre. <c>Sensitive</c> en todo
/// lo de pago, salario, cargo, lugar y contrato: quien no tiene <c>hr.contract.read</c> no lo ve.
/// </summary>
internal static class ActivityChanges
{
    public static IReadOnlyList<ActivityChange> Diff(
        EmployeeProfile before, EmployeeProfile after, IReadOnlyDictionary<Guid, string>? names = null) => Collect([
        Change("firstName", before.FirstName, after.FirstName),
        Change("lastName", before.LastName, after.LastName),
        Change("documentType", before.DocumentType, after.DocumentType),
        Change("documentNumber", before.DocumentNumber, after.DocumentNumber),
        Change("country", before.Country, after.Country),
        Change("birthDate", before.BirthDate, after.BirthDate),
        Change("phone", before.Phone, after.Phone),
        Change("personalEmail", before.PersonalEmail, after.PersonalEmail),
        Change("workEmail", before.WorkEmail, after.WorkEmail),
        Change("address", before.Address, after.Address),
        Change("cityId", NameOf(names, before.CityId), NameOf(names, after.CityId)),
        Change("memberId", NameOf(names, before.MemberId), NameOf(names, after.MemberId)),
        Change("healthProvider", before.HealthProvider, after.HealthProvider),
        Change("pensionFund", before.PensionFund, after.PensionFund),
        Change("severanceFund", before.SeveranceFund, after.SeveranceFund),
        Change("occupationalRiskProvider", before.OccupationalRiskProvider, after.OccupationalRiskProvider),
        Change("contributorSubtype", before.ContributorSubtype, after.ContributorSubtype),
        Change("foreignPensionExempt", before.ForeignPensionExempt, after.ForeignPensionExempt),
        Change("restDays", string.Join(',', before.RestDays), string.Join(',', after.RestDays))]);

    public static IReadOnlyList<ActivityChange> Diff(PaymentDetails? before, PaymentDetails? after) => Collect([
        Change("paymentMethod", before?.Method, after?.Method, sensitive: true),
        Change("bankName", before?.BankName, after?.BankName, sensitive: true),
        Change("bankAccountType", before?.BankAccountType, after?.BankAccountType, sensitive: true),
        Change("bankAccountNumber", before?.BankAccountNumber, after?.BankAccountNumber, sensitive: true)]);

    /// <summary>El alta: lo personal visible, y el contrato, el salario, el cargo y el pago marcados como sensibles.</summary>
    public static IReadOnlyList<ActivityChange> EmployeeCreated(
        EmployeeProfile profile,
        PaymentDetails? payment,
        EmploymentContract contract,
        IReadOnlyDictionary<Guid, string> names)
    {
        var salary = contract.Salaries.OrderBy(record => record.EffectiveDate).First();
        var assignment = contract.Assignments.OrderBy(record => record.EffectiveDate).First();
        return Collect(
            [
                Change("firstName", null, profile.FirstName),
                Change("lastName", null, profile.LastName),
                Change("documentType", null, profile.DocumentType),
                Change("documentNumber", null, profile.DocumentNumber),
                Change("country", null, profile.Country),
                .. Diff(null, payment),
                .. ContractFields(null, contract, names),
                Change("amount", null, salary.Amount, sensitive: true),
                .. AssignmentFields(null, assignment, names),
            ]);
    }

    /// <param name="current">El registro vigente hoy: su monto es el «antes».</param>
    public static IReadOnlyList<ActivityChange> SalaryCreated(SalaryRecord? current, SalaryRecord next) => Collect([
        Change("amount", current?.Amount, next.Amount, sensitive: true),
        Change("effectiveDate", null, next.EffectiveDate, sensitive: true),
        Change("teleworker", current?.Teleworker, next.Teleworker, sensitive: true)]);

    /// <summary>El registro se reescribe en su lugar: quien llama guarda lo que tenía antes.</summary>
    public static IReadOnlyList<ActivityChange> SalaryRewritten(decimal beforeAmount, bool beforeTeleworker, SalaryRecord after) => Collect([
        Change("effectiveDate", after.EffectiveDate, after.EffectiveDate, sensitive: true, alwaysList: true),
        Change("amount", beforeAmount, after.Amount, sensitive: true),
        Change("teleworker", beforeTeleworker, after.Teleworker, sensitive: true)]);

    public static IReadOnlyList<ActivityChange> AssignmentCreated(
        AssignmentRecord? previous, AssignmentRecord next, IReadOnlyDictionary<Guid, string> names) => Collect(
        [Change("effectiveDate", null, next.EffectiveDate, sensitive: true), .. AssignmentFields(previous, next, names)]);

    public static IReadOnlyList<ActivityChange> ContractRenewed(DateOnly previousEnd, DateOnly newEnd) => Collect([
        Change("endDate", previousEnd, newEnd, sensitive: true)]);

    public static IReadOnlyList<ActivityChange> ConvertedToIndefinite(DateOnly previousEnd, DateOnly convertedOn) => Collect([
        Change("duration", ContractDuration.FixedTerm, ContractDuration.Indefinite, sensitive: true),
        Change("endDate", previousEnd, (DateOnly?)null, sensitive: true),
        Change("convertedToIndefiniteOn", null, convertedOn, sensitive: true)]);

    public static IReadOnlyList<ActivityChange> ContractTypeChanged(
        EmploymentContract previous, EmploymentContract next, IReadOnlyDictionary<Guid, string> names) => Collect(
        [.. ContractFields(previous, next, names), Change("amount", previous.Salaries.MaxBy(record => record.EffectiveDate)?.Amount, next.Salaries[0].Amount, sensitive: true)]);

    /// <summary>El estado es visible; el motivo y la fecha son del contrato (llamada del plan): no los ve quien no lee contratos.</summary>
    public static IReadOnlyList<ActivityChange> Terminated(TerminationReason reason, DateOnly date) => Collect([
        Change("status", EmployeeStatus.Active, EmployeeStatus.Retired),
        Change("reason", null, reason, sensitive: true),
        Change("date", null, date, sensitive: true)]);

    public static IReadOnlyList<ActivityChange> Deactivated(DateOnly date) => Collect([
        Change("status", EmployeeStatus.Active, EmployeeStatus.Deactivated),
        Change("date", null, date, sensitive: true)]);

    /// <param name="previousStatus">El estado con el que el empleado llegó: <c>Retired</c> si lo terminaron, <c>Deactivated</c> si lo desactivaron.</param>
    public static IReadOnlyList<ActivityChange> Reincorporated(
        EmployeeStatus previousStatus, EmploymentContract next, IReadOnlyDictionary<Guid, string> names) => Collect(
        [
            Change("status", previousStatus, EmployeeStatus.Active),
            .. ContractFields(null, next, names),
            Change("amount", null, next.Salaries[0].Amount, sensitive: true),
            .. AssignmentFields(null, next.Assignments[0], names),
        ]);

    // Spec 2026-11-23 §10: type, date and whether there is a PDF; never restrictions, recommendations or the provider. None of these
    // is marked sensitive: the whole Medical category is already hidden from whoever lacks hr.medical.read.
    public static IReadOnlyList<ActivityChange> MedicalExamRecorded(MedicalExamType type, DateOnly date, bool hasDocument) => Collect([
        Change("type", null, type),
        Change("examDate", null, date),
        Change("hasDocument", null, hasDocument)]);

    public static IReadOnlyList<ActivityChange> MedicalExamUpdated(
        (MedicalExamType Type, DateOnly Date, bool HasDocument) before, (MedicalExamType Type, DateOnly Date, bool HasDocument) after) => Collect([
        Change("type", before.Type, after.Type),
        Change("examDate", before.Date, after.Date),
        Change("hasDocument", before.HasDocument, after.HasDocument)]);

    public static IReadOnlyList<ActivityChange> MedicalExamDeleted(MedicalExamType type, DateOnly date, bool hasDocument) => Collect([
        Change("type", type, null),
        Change("examDate", date, null),
        Change("hasDocument", hasDocument, null)]);

    public static IReadOnlyList<ActivityChange> ExitExamNotAttended(DateOnly notifiedOn) => Collect([
        Change("status", ExitExamStatus.Pending, ExitExamStatus.NotAttended),
        Change("notifiedOn", null, notifiedOn)]);

    /// <param name="target">Qué se adjuntó: <c>contract</c>, <c>salary</c>, <c>assignment</c> o <c>renewal</c>.</param>
    public static IReadOnlyList<ActivityChange> DocumentAttached(string target, string? beforeFileName, string? afterFileName) => Collect([
        Change($"{target}Document", beforeFileName, afterFileName, sensitive: true)]);

    private static IEnumerable<ActivityChange?> ContractFields(
        EmploymentContract? before, EmploymentContract after, IReadOnlyDictionary<Guid, string> names) =>
    [
        Change("company", NameOf(names, before?.CompanyId), NameOf(names, after.CompanyId), sensitive: true),
        Change("contractType", before?.ContractType, after.ContractType, sensitive: true),
        Change("duration", before?.Duration, after.Duration, sensitive: true),
        Change("salaryType", before?.SalaryType, after.SalaryType, sensitive: true),
        Change("startDate", before?.StartDate, after.StartDate, sensitive: true),
        Change("endDate", before?.EndDate, after.EndDate, sensitive: true),
    ];

    private static IEnumerable<ActivityChange?> AssignmentFields(
        AssignmentRecord? before, AssignmentRecord after, IReadOnlyDictionary<Guid, string> names) =>
    [
        Change("position", NameOf(names, before?.PositionId.Value), NameOf(names, after.PositionId.Value), sensitive: true),
        Change("area", NameOf(names, before?.AreaId?.Value), NameOf(names, after.AreaId?.Value), sensitive: true),
        Change("site", NameOf(names, before?.SiteId.Value), NameOf(names, after.SiteId.Value), sensitive: true),
        Change("workplace", NameOf(names, before?.WorkplaceId.Value), NameOf(names, after.WorkplaceId.Value), sensitive: true),
    ];

    // Un id sin nombre conocido se escribe tal cual: el historial nunca pierde el dato, sólo pierde la legibilidad.
    private static string? NameOf(IReadOnlyDictionary<Guid, string>? names, Guid? id) =>
        id is not { } value ? null : names is not null && names.TryGetValue(value, out var name) ? name : value.ToString();

    private static List<ActivityChange> Collect(IEnumerable<ActivityChange?> changes) =>
        changes.OfType<ActivityChange>().ToList();

    private static ActivityChange? Change(
        string field, object? before, object? after, bool sensitive = false, bool alwaysList = false)
    {
        var beforeValue = Scalar(before);
        var afterValue = Scalar(after);
        if (!alwaysList && string.Equals(beforeValue?.GetRawText(), afterValue?.GetRawText(), StringComparison.Ordinal))
        {
            return null;
        }

        return new ActivityChange(field, beforeValue, afterValue, sensitive);
    }

    // Escalares: montos como número, fechas yyyy-MM-dd, enums por nombre, ids por su texto.
    private static JsonElement? Scalar(object? value) => value switch
    {
        null => null,
        string text => JsonSerializer.SerializeToElement(text),
        bool flag => JsonSerializer.SerializeToElement(flag),
        decimal amount => JsonSerializer.SerializeToElement(amount),
        DateOnly date => JsonSerializer.SerializeToElement(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
        Enum member => JsonSerializer.SerializeToElement(member.ToString()),
        Guid id => JsonSerializer.SerializeToElement(id.ToString()),
        _ => throw new InvalidOperationException($"'{value.GetType().Name}' is not an activity scalar."),
    };
}
