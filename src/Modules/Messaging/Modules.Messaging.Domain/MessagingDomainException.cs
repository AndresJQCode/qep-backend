using BuildingBlocks.Domain;

namespace Modules.Messaging.Domain;

/// <summary>Regla de negocio de Messaging: <c>ApiExceptionHandler</c> la responde 422 con su código.</summary>
public sealed class MessagingDomainException(string code, string message) : DomainException(code, message);
