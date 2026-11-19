using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.UnitTests;

/// <summary>El canal de correo de las pruebas: guarda lo que recibe y devuelve el resultado que se le diga, o lanza.</summary>
internal sealed class FakePurchaseOrderEmail : IPurchaseOrderEmail
{
    public List<PurchaseOrderEmailRequest> Requests { get; } = [];

    public PurchaseOrderEmailResult Result { get; set; } = PurchaseOrderEmailResult.Sent;

    public bool Throw { get; set; }

    /// <summary>El timeout de un HttpClient: <c>TaskCanceledException</c> sin que nadie haya cancelado el token de la petición.</summary>
    public bool TimeOut { get; set; }

    public Task<PurchaseOrderEmailResult> SendAsync(PurchaseOrderEmailRequest request, CancellationToken cancellationToken)
    {
        if (TimeOut)
        {
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        }

        if (Throw)
        {
            throw new InvalidOperationException("channel down");
        }

        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

/// <summary>El armado del documento y su PDF de las pruebas: el PDF son bytes fijos y el nombre sigue al número de la orden.</summary>
internal sealed class FakePurchaseOrderDocumentBuilder : IPurchaseOrderDocumentBuilder
{
    public static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46];

    public bool Throw { get; set; }

    public int Calls { get; private set; }

    public Task<PurchaseOrderDocumentBundle> BuildAsync(
        PurchaseOrder order, PurchaseOrderDetailDto detail, PurchasesSupplierRef supplier, CancellationToken cancellationToken)
    {
        Calls++;
        if (Throw)
        {
            throw new InvalidOperationException("pdf renderer down");
        }

        var document = new PurchaseOrderPdfDocument(
            "Verde SAS", order.Number, "Enviada", "17/11/2026", supplier.Name, "NIT 900123456", order.Currency, order.Notes,
            [], "$ 0", "$ 0", "$ 0", ShowsConfirmedValues: false);
        return Task.FromResult(new PurchaseOrderDocumentBundle(document, Pdf, $"{order.Number}.pdf"));
    }
}

/// <summary>Un guardado que falla desde el segundo: el primero es el del envío, el segundo es el del resultado del correo.</summary>
internal sealed class FailingSecondSaveUnitOfWork : IPurchasesUnitOfWork
{
    public int Saves { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Saves >= 2 ? throw new InvalidOperationException("db down") : Task.CompletedTask;
    }

    public Task<IPurchasesTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
