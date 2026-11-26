using BuildingBlocks.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>
/// El dominio lanza todo como <see cref="PayrollDomainException"/> (422). Cinco de sus códigos son conflictos con el estado de lo
/// que ya existe (<see cref="PayrollErrorCodes.Conflicts"/>) y se responden 409: acá se convierten, y el resto sigue 422.
/// </summary>
internal static class PayrollConflicts
{
    public static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (PayrollDomainException exception) when (PayrollErrorCodes.Conflicts.Contains(exception.Code))
        {
            throw new ResourceConflictException(exception.Code, exception.Message, exception);
        }
    }
}
