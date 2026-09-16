using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El interruptor <c>Quotations:PaymentProofs:PublicLinks</c> en el host real (spec 2026-09-15):
/// prendido sin bucket público, la API no arranca (P2), y según su valor el composition root
/// registra el publicador de comprobantes que copia o el que no hace nada (P3).
/// </summary>
public sealed class PaymentProofPublicLinksHostTests
{
    // P2: prendida sin bucket público, la opción no publicaría nada y nadie se enteraría. Los dos
    // valores de Storage se fijan vacíos para que no los traigan los user-secrets de quien corre la
    // prueba.
    [Fact]
    public async Task PublicLinksWithoutAPublicBucketStopsTheApiFromStarting()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "true");
            builder.UseSetting("Storage:R2:PublicBucket", string.Empty);
            builder.UseSetting("Storage:R2:PublicBaseUrl", string.Empty);
        });

        // ThrowsAny: ValidateOnStart lanza durante el arranque, y WebApplicationFactory puede
        // entregarla envuelta.
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(MessagesOf(exception), message => message.Contains(
            "Storage:R2:PublicBucket is required when Quotations:PaymentProofs:PublicLinks is true",
            StringComparison.Ordinal));
    }

    // P3: apagada —el default de las factorías—, el composition root registra el publicador que no
    // hace nada: sin URL aunque la clave exista.
    [Fact]
    public async Task WithPublicLinksOffThePublisherGivesNoUrl()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await using var scope = factory.Services.CreateAsyncScope();

        var publisher = scope.ServiceProvider.GetRequiredService<IPaymentProofPublisher>();

        Assert.Null(publisher.UrlFor("payment-proofs/abc.pdf"));
    }

    // P3: encendida, el publicador arma la URL con el bucket público.
    [Fact]
    public async Task WithPublicLinksOnThePublisherBuildsThePublicUrl()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        await using var scope = factory.Services.CreateAsyncScope();

        var publisher = scope.ServiceProvider.GetRequiredService<IPaymentProofPublisher>();

        Assert.Equal(
            $"{InMemoryPublicObjectStorage.BaseUrl}/payment-proofs/abc.pdf",
            publisher.UrlFor("payment-proofs/abc.pdf"));
    }

    private static List<string> MessagesOf(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return messages;
    }
}
