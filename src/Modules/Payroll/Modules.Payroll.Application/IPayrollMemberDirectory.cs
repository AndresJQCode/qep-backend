namespace Modules.Payroll.Application;

/// <summary>Puerto hacia las membresías del tenant (Tenancy): el nombre de quien actúa en el historial del período.</summary>
public interface IPayrollMemberDirectory
{
    /// <summary>El nombre de la membresía del tenant de un usuario; nulo si el usuario no tiene membresía en el tenant.</summary>
    Task<string?> FindDisplayNameAsync(Guid tenantId, Guid subjectId, CancellationToken cancellationToken);
}
