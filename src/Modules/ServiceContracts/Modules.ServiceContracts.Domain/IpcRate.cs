namespace Modules.ServiceContracts.Domain;

/// <summary>
/// El IPC de un año para un tenant (spec reajuste §6.7), en porcentaje con dos decimales: <c>5.20</c> es 5,20 %. Una fila por
/// tenant y año; <see cref="Version"/> es el token que impide pisar en silencio la tasa de otra persona.
/// </summary>
public sealed class IpcRate
{
    public const decimal MinRate = -10m;

    public const decimal MaxRate = 100m;

    public const int MinYear = 2000;

    private IpcRate()
    {
    }

    public Guid TenantId { get; private set; }

    public int Year { get; private set; }

    public decimal Rate { get; private set; }

    public Guid? UpdatedByMemberId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Version { get; private set; }

    /// <summary>El techo del año (el siguiente al de hoy) lo mira el validador del borde, que conoce la «hoy» del tenant.</summary>
    public static IpcRate Create(Guid tenantId, int year, decimal rate, Guid? memberId, DateTimeOffset now)
    {
        if (year < MinYear)
        {
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ServiceInvalid, $"The IPC year must be {MinYear} or later.");
        }

        return new IpcRate
        {
            TenantId = tenantId,
            Year = year,
            Rate = Validate(rate),
            UpdatedByMemberId = memberId,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>Falso, y sin tocar nada, si la tasa es la que ya está.</summary>
    public bool Change(decimal rate, Guid? memberId, DateTimeOffset now)
    {
        var clean = Validate(rate);
        if (clean == Rate)
        {
            return false;
        }

        Rate = clean;
        UpdatedByMemberId = memberId;
        UpdatedAt = now;
        Version++;
        return true;
    }

    private static decimal Validate(decimal rate)
    {
        if (rate is < MinRate or > MaxRate)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.ServiceInvalid, $"The IPC rate must be between {MinRate} and {MaxRate}.");
        }

        return ServiceContract.ToMoney(rate);
    }
}
