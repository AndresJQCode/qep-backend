using System.Text.Json;
using Modules.Coordinadora.Application;
using Modules.Coordinadora.Application.Shipments;
using Modules.Coordinadora.Domain;

namespace Modules.Coordinadora.UnitTests;

/// <summary>Spec 2026-11-21 §5.3: el armador junta todos los faltantes y, sin ninguno, arma el <c>p</c> de la guía y el de la cotización con los
/// nombres de campo del contrato (§3). Puro: sin reloj ni repositorios.</summary>
public sealed class CoordinadoraGuideBuilderTests
{
    private static readonly Guid RecipientCityId = Guid.Parse("0193c0de-0002-7000-8000-000000000002");
    private static readonly Guid SenderCityId = Guid.Parse("0193c0de-0001-7000-8000-000000000001");
    private static readonly CoordinadoraCityRef Bogota = new(RecipientCityId, "BOGOTÁ", "11001", "BOGOTA (CUN)");
    private static readonly CoordinadoraCityRef Medellin = new(SenderCityId, "MEDELLÍN", "05001", "MEDELLIN (ANT)");
    private static readonly CoordinadoraCityRef Unserved = new(RecipientCityId, "LA GUADALUPE", "94885", null);

    internal static CoordinadoraShipmentInput Order(
        bool cashOnDelivery = false,
        string? carrierName = null,
        bool storePickup = false,
        bool requiresShipping = true,
        bool cancelled = false,
        CoordinadoraRecipientInput? recipient = null) =>
        new(
            Guid.Parse("0193c0de-0003-7000-8000-000000000003"),
            "PED-2026-0042",
            new DateOnly(2026, 11, 21),
            cancelled,
            storePickup,
            requiresShipping,
            carrierName,
            cashOnDelivery,
            89900.49m,
            85000m,
            "Entregar en portería",
            7,
            [new CoordinadoraShipmentLine("Aceite esencial", "AE-1", 3), new CoordinadoraShipmentLine("Vela", null, 1.5m)],
            recipient ?? Recipient());

    internal static CoordinadoraRecipientInput Recipient(string? name = "Pedro Pérez", string? phone = "3189023450", string? address = "Calle 40 20-40") =>
        new(name, phone, address, RecipientCityId, null, null, "900123456-7");

    private static CoordinadoraSettings Settings(bool collect = false, decimal minimum = 0m)
    {
        var settings = CoordinadoraSettings.Create(Guid.CreateVersion7(), Guid.CreateVersion7());
        settings.Update(CoordinadoraSettingsTests.CompleteInput() with { SenderCityId = SenderCityId, CollectOnDelivery = collect, DeclaredValueMinimum = minimum });
        return settings;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void AReadyOrderBuildsTheGuideWithTheContractFields()
    {
        var result = CoordinadoraGuideBuilder.Build(Order(), Settings(), Bogota, Medellin, "800123456", null);

        var ready = Assert.IsType<CoordinadoraBuildResult.Ready>(result);
        var guide = Parse(ready.GuideJson);
        Assert.Equal("", guide.GetProperty("codigo_remision").GetString());
        Assert.Equal("2026-11-21", guide.GetProperty("fecha").GetString());
        Assert.Equal("800123456", guide.GetProperty("nit_remitente").GetString());
        Assert.Equal("Bodega Ritual", guide.GetProperty("nombre_remitente").GetString());
        Assert.Equal("05001000", guide.GetProperty("ciudad_remitente").GetString());
        Assert.Equal("900123456", guide.GetProperty("nit_destinatario").GetString());
        Assert.Equal("Pedro Pérez", guide.GetProperty("nombre_destinatario").GetString());
        Assert.Equal("Calle 40 20-40", guide.GetProperty("direccion_destinatario").GetString());
        Assert.Equal("11001000", guide.GetProperty("ciudad_destinatario").GetString());
        Assert.Equal("3189023450", guide.GetProperty("telefono_destinatario").GetString());
        // El total de las líneas con IVA, redondeado a pesos.
        Assert.Equal("89900", guide.GetProperty("valor_declarado").GetString());
        Assert.Equal(("2", "0", "1"), (guide.GetProperty("codigo_cuenta").GetString(), guide.GetProperty("codigo_producto").GetString(), guide.GetProperty("nivel_servicio").GetString()));
        Assert.Equal("3 x Aceite esencial, 1.5 x Vela", guide.GetProperty("contenido").GetString());
        Assert.Equal("PED-2026-0042", guide.GetProperty("referencia").GetString());
        Assert.Equal("Entregar en portería", guide.GetProperty("observaciones").GetString());
        Assert.Equal("IMPRESO", guide.GetProperty("estado").GetString());
        Assert.Equal("", guide.GetProperty("recaudos").GetString());
        var item = Assert.Single(guide.GetProperty("detalle").EnumerateArray());
        Assert.Equal(("0", "10", "20", "30", "1.5", "1"), (
            item.GetProperty("ubl").GetString(), item.GetProperty("alto").GetString(), item.GetProperty("ancho").GetString(),
            item.GetProperty("largo").GetString(), item.GetProperty("peso").GetString(), item.GetProperty("unidades").GetString()));
        Assert.Equal("PED-2026-0042", item.GetProperty("referencia").GetString());
        Assert.Equal(JsonValueKind.Object, guide.GetProperty("atributos_retorno").ValueKind);
        Assert.Equal(89900m, ready.DeclaredValue);
        Assert.Equal(new CoordinadoraPackage(10, 20, 30, 1.5m, 1), ready.Package);
        // Las credenciales no están en lo que se guarda.
        Assert.DoesNotContain("apikey", ready.GuideJson, StringComparison.Ordinal);
        Assert.DoesNotContain("clave", ready.GuideJson, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQuoteUsesTheSameCitiesPackageAndDeclaredValue()
    {
        var ready = Assert.IsType<CoordinadoraBuildResult.Ready>(CoordinadoraGuideBuilder.Build(Order(), Settings(), Bogota, Medellin, "800123456", null));

        var quote = Parse(ready.QuoteJson);
        Assert.Equal(("05001000", "11001000", "89900"), (quote.GetProperty("origen").GetString(), quote.GetProperty("destino").GetString(), quote.GetProperty("valoracion").GetString()));
        Assert.Equal(("2", "0", ""), (quote.GetProperty("cuenta").GetString(), quote.GetProperty("producto").GetString(), quote.GetProperty("div").GetString()));
        Assert.Equal(1, Assert.Single(quote.GetProperty("nivel_servicio").EnumerateArray()).GetInt32());
        var item = Assert.Single(quote.GetProperty("detalle").GetProperty("item").EnumerateArray());
        Assert.Equal("1.5", item.GetProperty("peso").GetString());
        Assert.False(item.TryGetProperty("referencia", out _));
    }

    [Fact]
    public void CashOnDeliveryAsksForCollectionOnlyWhenTheConnectionAllowsIt()
    {
        var off = Assert.IsType<CoordinadoraBuildResult.Ready>(CoordinadoraGuideBuilder.Build(Order(cashOnDelivery: true), Settings(), Bogota, Medellin, null, null));
        Assert.Equal("", Parse(off.GuideJson).GetProperty("recaudos").GetString());

        var on = Assert.IsType<CoordinadoraBuildResult.Ready>(CoordinadoraGuideBuilder.Build(Order(cashOnDelivery: true), Settings(collect: true), Bogota, Medellin, null, null));
        Assert.Equal("85000", Parse(on.GuideJson).GetProperty("recaudos").GetString());

        var notCod = Assert.IsType<CoordinadoraBuildResult.Ready>(CoordinadoraGuideBuilder.Build(Order(), Settings(collect: true), Bogota, Medellin, null, null));
        Assert.Equal("", Parse(notCod.GuideJson).GetProperty("recaudos").GetString());
    }

    [Fact]
    public void TheOverridesReplaceThePackageAndTheDeclaredValueNeverBelowTheMinimum()
    {
        var overrides = new CoordinadoraShipmentOverrides(CoordinadoraPackage.Create(5, 5, 5, 0.25m, 2), 10000m);

        var ready = Assert.IsType<CoordinadoraBuildResult.Ready>(CoordinadoraGuideBuilder.Build(Order(), Settings(minimum: 20000m), Bogota, Medellin, null, overrides));

        Assert.Equal(20000m, ready.DeclaredValue);
        Assert.Equal("20000", Parse(ready.GuideJson).GetProperty("valor_declarado").GetString());
        var item = Assert.Single(Parse(ready.GuideJson).GetProperty("detalle").EnumerateArray());
        Assert.Equal(("5", "0.25", "2"), (item.GetProperty("alto").GetString(), item.GetProperty("peso").GetString(), item.GetProperty("unidades").GetString()));
    }

    [Fact]
    public void ACarrierThatIsCoordinadoraOrNoneIsFineAndAnotherIsAMismatch()
    {
        Assert.IsType<CoordinadoraBuildResult.Ready>(CoordinadoraGuideBuilder.Build(Order(carrierName: "Coordinadora Mercantil"), Settings(), Bogota, Medellin, null, null));

        var notReady = Assert.IsType<CoordinadoraBuildResult.NotReady>(CoordinadoraGuideBuilder.Build(Order(carrierName: "Servientrega"), Settings(), Bogota, Medellin, null, null));
        var missing = Assert.Single(notReady.Missing);
        Assert.Equal((CoordinadoraMissing.CarrierMismatch, "Servientrega"), (missing.Code, missing.Label));
    }

    [Fact]
    public void EveryMissingPieceIsReportedTogetherAndNothingIsBuilt()
    {
        var order = Order(
            cashOnDelivery: false,
            storePickup: true,
            requiresShipping: false,
            cancelled: true,
            recipient: new CoordinadoraRecipientInput(null, " ", null, null, null, null, null));
        var settings = CoordinadoraSettings.Create(Guid.CreateVersion7(), Guid.CreateVersion7());

        var notReady = Assert.IsType<CoordinadoraBuildResult.NotReady>(CoordinadoraGuideBuilder.Build(order, settings, null, null, null, null));

        Assert.Equal(
            [
                CoordinadoraMissing.OrderCancelled, CoordinadoraMissing.StorePickup, CoordinadoraMissing.NothingToShip,
                CoordinadoraMissing.RecipientName, CoordinadoraMissing.RecipientPhone, CoordinadoraMissing.RecipientAddress,
                CoordinadoraMissing.RecipientCity, CoordinadoraMissing.SenderIncomplete, CoordinadoraMissing.PackageDefaultsMissing,
            ],
            notReady.Missing.Select(value => value.Code));
    }

    [Fact]
    public void WithoutAConnectionTheOnlySettingsMissingIsTheConnection()
    {
        var notReady = Assert.IsType<CoordinadoraBuildResult.NotReady>(CoordinadoraGuideBuilder.Build(Order(), null, Bogota, null, null, null));

        Assert.Equal([CoordinadoraMissing.ConnectionMissing], notReady.Missing.Select(value => value.Code));
    }

    [Fact]
    public void ACityThatCoordinadoraDoesNotServeIsReportedWithItsName()
    {
        var notReady = Assert.IsType<CoordinadoraBuildResult.NotReady>(CoordinadoraGuideBuilder.Build(Order(), Settings(), Unserved, Medellin, null, null));
        var missing = Assert.Single(notReady.Missing);
        Assert.Equal((CoordinadoraMissing.RecipientCityNotServed, RecipientCityId.ToString(), "LA GUADALUPE"), (missing.Code, missing.Reference, missing.Label));

        var sender = Assert.IsType<CoordinadoraBuildResult.NotReady>(CoordinadoraGuideBuilder.Build(Order(), Settings(), Bogota, Unserved with { CityId = SenderCityId }, null, null));
        Assert.Equal(CoordinadoraMissing.SenderCityNotServed, Assert.Single(sender.Missing).Code);
    }

    [Fact]
    public void ARecipientOutsideColombiaHasNoCity()
    {
        var abroad = Recipient() with { Country = "MX", CityName = "Monterrey" };

        var notReady = Assert.IsType<CoordinadoraBuildResult.NotReady>(CoordinadoraGuideBuilder.Build(Order(recipient: abroad), Settings(), Bogota, Medellin, null, null));

        Assert.Equal([CoordinadoraMissing.RecipientCity], notReady.Missing.Select(value => value.Code));
    }

    [Fact]
    public void TheContentIsCappedAndNeverEmpty()
    {
        var lines = Enumerable.Range(1, 40).Select(index => new CoordinadoraShipmentLine($"Producto con nombre largo {index}", null, index)).ToList();

        var content = CoordinadoraGuideBuilder.ContentOf(lines);

        Assert.Equal(CoordinadoraGuideBuilder.ContentMaxLength, content.Length);
        Assert.Equal("Mercancía", CoordinadoraGuideBuilder.ContentOf([]));
    }

    [Fact]
    public void AMissingIdentificationTravelsAsZero()
    {
        var ready = Assert.IsType<CoordinadoraBuildResult.Ready>(CoordinadoraGuideBuilder.Build(
            Order(recipient: Recipient() with { Identification = null }), Settings(), Bogota, Medellin, null, null));

        Assert.Equal("0", Parse(ready.GuideJson).GetProperty("nit_destinatario").GetString());
    }
}
