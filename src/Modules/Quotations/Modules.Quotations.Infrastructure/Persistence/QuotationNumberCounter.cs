namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// El consecutivo del número de cotización de un tenant, por año — una fila por (tenant, año).
/// Mismo criterio que <c>CustomerCucCounter</c> en Customers: vive en Infrastructure y no en
/// Domain porque no es una regla de negocio, es el mecanismo con el que la base serializa la
/// emisión.
/// </summary>
internal sealed class QuotationNumberCounter
{
    public Guid TenantId { get; init; }

    public int Year { get; init; }

    /// <summary>El próximo número a emitir. Arranca en 1.</summary>
    public long NextValue { get; init; }
}
