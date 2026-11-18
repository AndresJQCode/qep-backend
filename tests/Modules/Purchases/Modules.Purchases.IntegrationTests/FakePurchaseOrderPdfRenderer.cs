using System.Text;
using Modules.Purchases.Application;

namespace Modules.Purchases.IntegrationTests;

/// <summary>El renderer de las pruebas: devuelve <c>"%PDF-1.7 fake"</c> y guarda el último documento que recibió.</summary>
internal sealed class FakePurchaseOrderPdfRenderer : IPurchaseOrderPdfRenderer
{
    public static readonly byte[] Content = Encoding.ASCII.GetBytes("%PDF-1.7 fake");

    public PurchaseOrderPdfDocument? LastDocument { get; private set; }

    public Task<byte[]> RenderAsync(PurchaseOrderPdfDocument document, CancellationToken cancellationToken)
    {
        LastDocument = document;
        return Task.FromResult(Content);
    }
}
