namespace Modules.Payroll.Domain;

/// <summary>Los doce conceptos del sistema (R-11): las siete horas, cuatro ingresos y una deducción.</summary>
public enum SystemConceptKey
{
    ExtraDay,
    ExtraNight,
    NightSurcharge,
    SundaySurcharge,
    ExtraSundayDay,
    ExtraSundayNight,
    NightSundaySurcharge,
    Commissions,
    PrestationalBonus,
    SalaryAdvance,
    OtherIncome,
    OtherDeduction,
}
