using BuildingBlocks.Domain;

namespace Modules.Pos.Domain;

/// <summary>Regla de negocio del punto de venta: ApiExceptionHandler la responde 422 con su código.</summary>
public sealed class PosDomainException(string code, string message) : DomainException(code, message);
