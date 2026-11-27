using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Settings;

/// <summary>Un id guardado que la cuenta nueva no tiene. Sólo se guardan ids, así que no hay nombre que mostrar. <see cref="Kind"/> es
/// <see cref="DocumentType"/> o <see cref="PaymentType"/>.</summary>
public sealed record SiigoAccountMismatch(string Kind, string Id)
{
    public const string DocumentType = "documentType";
    public const string PaymentType = "paymentType";
}

/// <summary>409 <c>siigo.connection.account_changed</c>: las credenciales nuevas son de otra cuenta de Siigo y la edición no trae la
/// confirmación. <c>mismatches</c> lleva <c>kind</c> e <c>id</c> de cada id guardado que la cuenta nueva no tiene; nunca credenciales.</summary>
public sealed class SiigoAccountChangedException(IReadOnlyList<SiigoAccountMismatch> mismatches)
    : ResourceConflictException(
        SiigoErrorCodes.ConnectionAccountChanged,
        "The new credentials belong to another Siigo account; confirm to delete the saved configuration."),
      IHasProblemExtensions
{
    public IReadOnlyList<SiigoAccountMismatch> Mismatches { get; } = mismatches;

    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["mismatches"] = mismatches,
    };
}

/// <summary>
/// Lo que la edición de una conexión de Siigo necesita saber de la configuración guardada (spec de la tarjeta 0pQhlDt5). El adaptador de
/// Bootstrapper lo llama desde Integrations, que no ve Siigo.
/// </summary>
public interface ISiigoAccountChange
{
    /// <summary>
    /// Compara los ids guardados (comprobante y formas de pago) con las listas que Siigo da <b>con las credenciales nuevas</b>. Sin fila ni
    /// ids guardados no hay nada que comparar y no se lee ninguna lista. Si todo cuadra (la misma cuenta con otra clave) devuelve
    /// <c>false</c>. Si algún id no está: sin <paramref name="confirmed"/> lanza <see cref="SiigoAccountChangedException"/>; con ella
    /// devuelve <c>true</c> (hay que llamar a <see cref="ResetAsync"/>). Una falla de Siigo al leer las listas sube tal cual: no se decide
    /// con listas que no se pudieron leer.
    /// </summary>
    Task<bool> MustResetAsync(
        Guid tenantId, Guid connectionId, SiigoCredentials credentials, bool confirmed, CancellationToken cancellationToken);

    /// <summary>Borra la configuración de la cuenta anterior, la audita y guarda, <b>en la transacción que el llamador ya abrió</b> sobre
    /// la base del módulo.</summary>
    Task ResetAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

public sealed class SiigoAccountChange(
    ISiigoApi api,
    ISiigoSettingsRepository settingsRepository,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IExecutionContext executionContext,
    IClock clock) : ISiigoAccountChange
{
    public async Task<bool> MustResetAsync(
        Guid tenantId, Guid connectionId, SiigoCredentials credentials, bool confirmed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var settings = await settingsRepository.FindAsync(tenantId, connectionId, withVariantLinks: false, cancellationToken);
        if (settings is null)
        {
            return false;
        }

        // Cada lista se lee sólo si hay algo guardado de ese tipo; con nada guardado no hay con qué comparar.
        var mismatches = new List<SiigoAccountMismatch>();
        if (settings.DocumentTypeId is { } documentTypeId)
        {
            var documentTypes = await api.ListDocumentTypesAsync(credentials, cancellationToken);
            if (documentTypes.All(option => !string.Equals(option.Id, documentTypeId, StringComparison.Ordinal)))
            {
                mismatches.Add(new SiigoAccountMismatch(SiigoAccountMismatch.DocumentType, documentTypeId));
            }
        }

        var savedPaymentTypes = settings.SavedPaymentTypeIds();
        if (savedPaymentTypes.Count > 0)
        {
            // La lista sólo trae las formas de pago activas: una que se desactivó en la misma cuenta también cuenta como distinta.
            var paymentTypes = await api.ListPaymentTypesAsync(credentials, cancellationToken);
            mismatches.AddRange(savedPaymentTypes
                .Where(id => paymentTypes.All(option => !string.Equals(option.Id, id, StringComparison.Ordinal)))
                .Select(id => new SiigoAccountMismatch(SiigoAccountMismatch.PaymentType, id)));
        }

        return mismatches.Count == 0
            ? false
            : confirmed
                ? true
                : throw new SiigoAccountChangedException(mismatches);
    }

    public async Task ResetAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.FindAsync(tenantId, connectionId, withVariantLinks: true, cancellationToken);
        if (settings is null)
        {
            return;
        }

        settings.ResetForAnotherAccount();
        // En la misma unidad de trabajo que el borrado: si el guardado de la conexión pierde, tampoco queda la auditoría.
        SiigoSettingsSupport.Audit(
            auditRecorder, executionContext, settings, SiigoSettingsSteps.AccountReset, clock.UtcNow, SiigoAuditActions.SettingsAccountReset);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
