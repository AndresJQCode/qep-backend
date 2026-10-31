namespace Modules.ServiceContracts.Application;

/// <summary>El email diario al equipo (§3.3, paso 5); adaptador en Bootstrapper sobre <c>IEmailChannel</c>.</summary>
public interface IServiceContractsStaffEmail
{
    Task<ServiceContractsSendResult> SendDigestAsync(ServiceContractsDigestEmail digest, CancellationToken cancellationToken);
}
