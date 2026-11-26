using Modules.Hr.Domain;

namespace Modules.Hr.Application;

public interface ILegalParameterSource
{
    /// <summary>La tabla es chica (dos claves por año): se lee entera. Dato de la plataforma, sin <c>tenantId</c>.</summary>
    Task<IReadOnlyList<LegalParameterValue>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Los tramos (fondo de solidaridad): también una tabla chica, leída entera.</summary>
    Task<IReadOnlyList<LegalParameterBracket>> ListBracketsAsync(CancellationToken cancellationToken);
}
