namespace Modules.Payroll.Domain;

/// <summary>Un concepto del sistema: no es una fila, su id es fijo y es igual en todos los tenants (R-11).</summary>
public sealed record SystemConcept(Guid Id, SystemConceptKey Key, ConceptKind Kind, string Name, bool? DefaultSalaryConstitutive);

/// <summary>La tabla de conceptos del sistema del contrato HTTP, en su orden.</summary>
public static class SystemConcepts
{
    public static IReadOnlyList<SystemConcept> All { get; } =
    [
        Overtime("00000000-0000-0000-0001-000000000001", SystemConceptKey.ExtraDay, "Hora extra diurna"),
        Overtime("00000000-0000-0000-0001-000000000002", SystemConceptKey.ExtraNight, "Hora extra nocturna"),
        Overtime("00000000-0000-0000-0001-000000000003", SystemConceptKey.NightSurcharge, "Recargo nocturno"),
        Overtime("00000000-0000-0000-0001-000000000004", SystemConceptKey.SundaySurcharge, "Recargo dominical y festivo"),
        Overtime("00000000-0000-0000-0001-000000000005", SystemConceptKey.ExtraSundayDay, "Hora extra dominical diurna"),
        Overtime("00000000-0000-0000-0001-000000000006", SystemConceptKey.ExtraSundayNight, "Hora extra dominical nocturna"),
        Overtime("00000000-0000-0000-0001-000000000007", SystemConceptKey.NightSundaySurcharge, "Recargo nocturno dominical"),
        Income("00000000-0000-0000-0002-000000000001", SystemConceptKey.Commissions, "Comisiones", true),
        Income("00000000-0000-0000-0002-000000000002", SystemConceptKey.PrestationalBonus, "Bonificación prestacional", true),
        Income("00000000-0000-0000-0002-000000000003", SystemConceptKey.SalaryAdvance, "Anticipo de salario", null),
        Income("00000000-0000-0000-0002-000000000004", SystemConceptKey.OtherIncome, "Otros ingresos", null),
        new SystemConcept(
            Guid.Parse("00000000-0000-0000-0003-000000000001"),
            SystemConceptKey.OtherDeduction,
            ConceptKind.Deduction,
            "Otra deducción",
            null),
    ];

    public static SystemConcept? Find(Guid id) => All.FirstOrDefault(concept => concept.Id == id);

    private static SystemConcept Overtime(string id, SystemConceptKey key, string name) =>
        new(Guid.Parse(id), key, ConceptKind.Overtime, name, null);

    private static SystemConcept Income(string id, SystemConceptKey key, string name, bool? defaultSalaryConstitutive) =>
        new(Guid.Parse(id), key, ConceptKind.Income, name, defaultSalaryConstitutive);
}
