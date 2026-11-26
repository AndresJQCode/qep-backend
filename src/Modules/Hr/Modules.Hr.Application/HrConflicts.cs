using BuildingBlocks.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// El dominio lanza todo como <see cref="HrDomainException"/> (422). Seis de sus códigos son conflictos con el estado de lo que
/// ya existe (<see cref="HrErrorCodes.Conflicts"/>) y se responden 409: acá se convierten, y el resto sigue 422.
/// </summary>
internal static class HrConflicts
{
    public static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (HrDomainException exception) when (HrErrorCodes.Conflicts.Contains(exception.Code))
        {
            throw new ResourceConflictException(exception.Code, exception.Message, exception);
        }
    }

    public static T Run<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (HrDomainException exception) when (HrErrorCodes.Conflicts.Contains(exception.Code))
        {
            throw new ResourceConflictException(exception.Code, exception.Message, exception);
        }
    }
}
