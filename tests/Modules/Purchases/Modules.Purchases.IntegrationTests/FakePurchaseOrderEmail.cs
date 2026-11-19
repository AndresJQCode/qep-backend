using System.Collections.Concurrent;
using Modules.Purchases.Application;

namespace Modules.Purchases.IntegrationTests;

/// <summary>
/// El correo al proveedor de las pruebas. El host se comparte entre pruebas, así que cada una usa una dirección propia y filtra por
/// ella con <see cref="SentTo"/>. Una dirección que empieza por <see cref="FailPrefix"/> devuelve <c>Failed</c>.
/// </summary>
internal sealed class FakePurchaseOrderEmail : IPurchaseOrderEmail
{
    public const string FailPrefix = "fail-";

    private readonly ConcurrentQueue<PurchaseOrderEmailRequest> _requests = new();

    public static string NewAddress(bool failing = false) => $"{(failing ? FailPrefix : string.Empty)}{Guid.NewGuid():N}@proveedor.co";

    public IReadOnlyList<PurchaseOrderEmailRequest> SentTo(string address) =>
        _requests.Where(request => request.ToAddress == address).ToList();

    public Task<PurchaseOrderEmailResult> SendAsync(PurchaseOrderEmailRequest request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        return Task.FromResult(
            request.ToAddress.StartsWith(FailPrefix, StringComparison.Ordinal)
                ? PurchaseOrderEmailResult.Failed
                : PurchaseOrderEmailResult.Sent);
    }
}
