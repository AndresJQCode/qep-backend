using Modules.Quotations.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-21 §5.7: Coordinadora nunca referencia tipos de Quotations; el adaptador de Bootstrapper copia la foto del pedido campo por
/// campo. Este proyecto ve los dos módulos y exige que nada se pierda en la copia: un campo nuevo de la foto que el adaptador no pase
/// llegaría al armador como vacío y saldría como un faltante que no es.
/// </summary>
public sealed class CoordinadoraOrderSourceTests
{
    [Fact]
    public void TheAdapterCopiesEveryFieldOfTheOrderAndTheRecipient()
    {
        var orderId = Guid.CreateVersion7();
        var cityId = Guid.CreateVersion7();
        var order = new ExternalShippingOrder(
            orderId,
            "PED-2026-0042",
            new DateOnly(2026, 11, 21),
            IsCancelled: false,
            IsStorePickup: false,
            RequiresShipping: true,
            "Coordinadora",
            CashOnDelivery: true,
            89900m,
            85000m,
            "Entregar en portería",
            7,
            [new ExternalShippingLine("Aceite esencial", "AE-1", 3)],
            new ExternalShippingRecipient("Pedro Pérez", "3189023450", "Calle 40 20-40", cityId, null, null, "900123456"));

        var input = CoordinadoraOrderSource.Translate(order);

        Assert.Equal((orderId, "PED-2026-0042", new DateOnly(2026, 11, 21), 7L), (input.OrderId, input.OrderNumber, input.Today, input.Version));
        Assert.Equal((false, false, true, "Coordinadora", true), (input.IsCancelled, input.IsStorePickup, input.RequiresShipping, input.CarrierName, input.CashOnDelivery));
        Assert.Equal((89900m, 85000m, "Entregar en portería"), (input.ProductsTotal, input.NetTotal, input.Notes));
        var line = Assert.Single(input.Lines);
        Assert.Equal(("Aceite esencial", "AE-1", 3m), (line.Name, line.Code, line.Quantity));
        Assert.Equal(("Pedro Pérez", "3189023450", "Calle 40 20-40", cityId, "900123456"), (
            input.Recipient.Name, input.Recipient.Phone, input.Recipient.Address, input.Recipient.CityId, input.Recipient.Identification));
        Assert.Null(input.Recipient.Country);
    }

    // Los dos records tienen los mismos campos: uno nuevo en la foto de Quotations tiene que llegar al puerto de Coordinadora.
    [Fact]
    public void TheTwoSnapshotsDeclareTheSameFields()
    {
        static IEnumerable<string> Names(Type type) => type.GetProperties().Select(property => property.Name).Order();

        Assert.Equal(Names(typeof(ExternalShippingOrder)), Names(typeof(Modules.Coordinadora.Application.CoordinadoraShipmentInput)));
        Assert.Equal(Names(typeof(ExternalShippingRecipient)), Names(typeof(Modules.Coordinadora.Application.CoordinadoraRecipientInput)));
        Assert.Equal(Names(typeof(ExternalShippingLine)), Names(typeof(Modules.Coordinadora.Application.CoordinadoraShipmentLine)));
    }
}
