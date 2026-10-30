namespace Modules.ServiceContracts.Application;

/// <summary>El aviso por correo al cliente (§3.3); adaptador en Bootstrapper sobre <c>IEmailChannel</c> y las plantillas de Notifications.</summary>
public interface IServiceContractsCustomerEmail
{
    Task<ServiceContractsSendResult> SendAsync(ServiceContractsCustomerMessage message, CancellationToken cancellationToken);
}
