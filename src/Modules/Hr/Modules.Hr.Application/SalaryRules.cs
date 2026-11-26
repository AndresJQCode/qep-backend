using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>Resuelve los pisos legales a una fecha. Los registros de salario y la importación evalúan contra la fecha del registro, no contra hoy.</summary>
public sealed class SalaryRules(ILegalParameterSource source)
{
    public async Task<SalaryFloor> FloorAtAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var values = await source.ListAsync(cancellationToken);
        var smmlv = LegalParameters.Resolve(values, LegalParameterKey.Smmlv, date);
        var integralFloor = LegalParameters.Resolve(values, LegalParameterKey.IntegralSalaryFloor, date);
        if (smmlv is null || integralFloor is null)
        {
            throw new HrDomainException(
                HrErrorCodes.LegalParameterMissing,
                $"There is no SMMLV loaded for {date:yyyy-MM-dd}.");
        }

        return new SalaryFloor(date, smmlv.Value, integralFloor.Value);
    }
}
