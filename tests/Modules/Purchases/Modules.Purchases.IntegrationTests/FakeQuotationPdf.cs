using System.Collections.Concurrent;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Purchases.IntegrationTests;

/// <summary>
/// El PDF del pedido del mayorista nunca sale a <c>qcode-pdf</c> ni a R2 en las pruebas: el falso guarda cada documento que
/// recibe, por número de pedido, para que una prueba vea exactamente lo que se le mandaría al servicio de PDF.
/// </summary>
internal sealed class FakeQuotationPdfRenderer : IQuotationPdfRenderer
{
    private readonly ConcurrentDictionary<string, QuotationPdfDocument> _documents = new();

    public QuotationPdfDocument? DocumentOf(string number) => _documents.GetValueOrDefault(number);

    public Task<byte[]> RenderAsync(QuotationPdfDocument document, CancellationToken cancellationToken)
    {
        _documents[document.QuotationNumber] = document;
        return Task.FromResult<byte[]>([0x25, 0x50, 0x44, 0x46]);
    }
}

internal sealed class FakeQuotationPdfStorage : IQuotationPdfStorage
{
    public Task<string> PublishInvoiceAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string> SaveAsync(Guid tenantId, QuotationId quotationId, byte[] content, CancellationToken cancellationToken) =>
        Task.FromResult($"quotations/tenants/{tenantId:N}/{Guid.CreateVersion7():N}.pdf");

    public Task<string> SaveOrderAsync(Guid tenantId, OrderId orderId, byte[] content, CancellationToken cancellationToken) =>
        Task.FromResult($"quotations/tenants/{tenantId:N}/orders/{Guid.CreateVersion7():N}.pdf");

    public Task<string> PublishAsync(string storageKey, CancellationToken cancellationToken) =>
        Task.FromResult($"https://assets.example.co/{storageKey}");

    public Task<string> CreateDownloadUrlAsync(string storageKey, string downloadFileName, CancellationToken cancellationToken) =>
        Task.FromResult($"https://r2.example.com/{storageKey}?X-Amz-Signature=stub");
}
