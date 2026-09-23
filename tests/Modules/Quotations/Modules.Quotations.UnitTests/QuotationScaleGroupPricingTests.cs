using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationScaleGroupPricingTests
{
    private static readonly Guid ProductA = Guid.NewGuid();
    private static readonly Guid ProductB = Guid.NewGuid();
    private static readonly Guid ProductC = Guid.NewGuid();

    private static QuotationPriceScaleRef Scale(
        bool allowGrouping, int multiple = 3, decimal discount = 5m, int fromUnit = 5, int toUnit = 48) =>
        new(fromUnit, toUnit, discount, QuotationPriceScaleRestriction.Multiple, multiple, null,
            allowGrouping);

    private static QuotationPriceScaleRef Packages(int packagingUnit = 12) =>
        new(1, 999, 5m, QuotationPriceScaleRestriction.PackagingUnit, null, packagingUnit);

    // El tramo "de mil": de a 1 por defecto, asi que ninguna cantidad falla el multiplo salvo
    // que la prueba lo pida explicitamente.
    private static QuotationPriceScaleRef Thousand(
        int multiple = 1, decimal discount = 12m, int fromUnit = 1000, int toUnit = 5000) =>
        new(fromUnit, toUnit, discount, QuotationPriceScaleRestriction.Multiple, multiple, null);

    // Agrupa por producto: el piso global necesita productos con mas de una escala, y la version
    // anterior de este helper tiraba con la clave repetida.
    private static Dictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> Catalog(
        params (Guid ProductId, QuotationPriceScaleRef Scale)[] entries) =>
        entries
            .GroupBy(entry => entry.ProductId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<QuotationPriceScaleRef>)
                    group.Select(entry => entry.Scale).ToArray());

    private static QuotationLinePricing For(IReadOnlyList<QuotationLinePricing> result, Guid itemId) =>
        result.Single(line => line.ItemId == itemId);

    // El multiplo es POR LINEA y se cuenta desde el piso del tramo. En 5-48 de a 3 la unica de
    // las tres que cumple sola es 8 (8 - 5 = 3); 10 y 12 no, aunque 12 sea multiplo crudo de 3 y
    // hasta el 2026-09-21 se llevara el descuento.
    //
    // La que cumple lo recibe por su cuenta y sin quedar marcada como agrupada.
    [Fact]
    public void OnlyTheLineThatMeetsTheMultipleOnItsOwnGetsTheDiscount()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 10m),
                new QuotationPricingLine(b, ProductB, 8m),
                new QuotationPricingLine(c, ProductC, 12m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true)),
                (ProductB, Scale(allowGrouping: true)),
                (ProductC, Scale(allowGrouping: true))));

        Assert.Equal(0m, For(result, a).DiscountPercentage);
        Assert.Equal(0m, For(result, c).DiscountPercentage);

        Assert.Equal(5m, For(result, b).DiscountPercentage);
        Assert.NotEqual(QuotationDiscountOrigin.Group, For(result, b).Origin);
        Assert.Equal(8m, For(result, b).Restriction!.EvaluatedQuantity);
    }

    // 10 % 3 y 13 % 3 fallan las dos, asi que ninguna califica y no hay grupo que armar. Cada
    // una reporta su propia cantidad y su propio faltante: antes las dos decian 23, el total de
    // un grupo que con el multiplo por linea ya no existe.
    [Fact]
    public void LinesThatMissTheMultipleNeverFormAGroup()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 10m),
                new QuotationPricingLine(b, ProductB, 13m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true)),
                (ProductB, Scale(allowGrouping: true))));

        Assert.All(result, line => Assert.Equal(0m, line.DiscountPercentage));
        Assert.All(result, line => Assert.NotEqual(QuotationDiscountOrigin.Group, line.Origin));
        Assert.All(
            result,
            line => Assert.Equal("quotation.item.quantity_not_multiple", line.Restriction!.Code));

        Assert.Equal(10m, For(result, a).Restriction!.EvaluatedQuantity);
        Assert.Equal(13m, For(result, b).Restriction!.EvaluatedQuantity);
    }

    // Sin el switch, cada linea valida su multiplo sola: en 5-48 de a 3, (10 - 5) y (12 - 5)
    // fallan las dos.
    [Fact]
    public void UngroupedLinesValidateOnTheirOwn()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 10m),
                new QuotationPricingLine(b, ProductB, 12m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: false)),
                (ProductB, Scale(allowGrouping: false))));

        Assert.All(result, line => Assert.Equal(0m, line.DiscountPercentage));
        Assert.All(result, line => Assert.NotEqual(QuotationDiscountOrigin.Group, line.Origin));
        Assert.Equal(10m, For(result, a).Restriction!.EvaluatedQuantity);
        Assert.Equal(12m, For(result, b).Restriction!.EvaluatedQuantity);
    }

    // El flag es condicion de pertenencia: dos escalas identicas en Desde/Hasta/Multiplo no
    // agrupan si solo una lo tiene. La que lo tiene queda sola con su propia cantidad.
    [Fact]
    public void ALineWithoutTheFlagNeverJoinsTheGroup()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 8m),
                new QuotationPricingLine(b, ProductB, 9m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true)),
                (ProductB, Scale(allowGrouping: false))));

        Assert.Equal(8m, For(result, a).Restriction!.EvaluatedQuantity);
        Assert.Equal(5m, For(result, a).DiscountPercentage);
        Assert.Equal(9m, For(result, b).Restriction!.EvaluatedQuantity);
        Assert.Equal(0m, For(result, b).DiscountPercentage);
    }

    // Escalas con distinto paso son grupos distintos: nunca hay ambiguedad sobre contra que
    // numero se compara el total.
    [Fact]
    public void ScalesWithADifferentStepFormSeparateGroups()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 9m),
                new QuotationPricingLine(b, ProductB, 10m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true, multiple: 3)),
                (ProductB, Scale(allowGrouping: true, multiple: 4))));

        Assert.Equal(9m, For(result, a).Restriction!.EvaluatedQuantity);
        Assert.Equal(10m, For(result, b).Restriction!.EvaluatedQuantity);
    }

    // El descuento queda fuera de la clave del grupo: dos productos con el mismo Desde/Hasta/
    // Multiplo agrupan aunque descuenten distinto, y cada linea se lleva el suyo.
    //
    // 3 + 3 = 6 entra al tramo 5-48 al que ninguna llega sola, que es donde se ve que agruparon.
    [Fact]
    public void GroupingIgnoresTheDiscountAndEachLineKeepsItsOwn()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 3m),
                new QuotationPricingLine(b, ProductB, 3m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true, discount: 10m)),
                (ProductB, Scale(allowGrouping: true, discount: 15m))));

        Assert.Equal(10m, For(result, a).DiscountPercentage);
        Assert.Equal(15m, For(result, b).DiscountPercentage);
        Assert.All(result, line => Assert.Equal(QuotationDiscountOrigin.Group, line.Origin));
    }

    // La unidad de empaque nunca agrupa y nunca lanza desde aca: 6 no es empaque entero de 12,
    // asi que la linea pierde la escala sin tumbar la operacion.
    [Fact]
    public void PackagingUnitIsEvaluatedPerLineAndNeverThrows()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 6m),
                new QuotationPricingLine(b, ProductB, 6m)
            ],
            Catalog((ProductA, Packages()), (ProductB, Packages())));

        Assert.All(result, line => Assert.NotEqual(QuotationDiscountOrigin.Group, line.Origin));
        Assert.All(result, line => Assert.Equal(0m, line.DiscountPercentage));
        Assert.All(result, line => Assert.Equal(6m, line.Restriction!.EvaluatedQuantity));
    }

    // Una cantidad que no cae en ninguna escala sigue sin descuento y sin restriccion que
    // reportar: no hay nada que la pantalla deba explicar.
    [Fact]
    public void ALineOutsideEveryScaleHasNoRestriction()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 2m)],
            Catalog((ProductA, Scale(allowGrouping: true))));

        Assert.Equal(0m, For(result, a).DiscountPercentage);
        Assert.Null(For(result, a).Restriction);
        Assert.Null(For(result, a).Scale);
    }

    // Un producto que ya no existe en el catalogo no tumba el recalculo de las demas lineas.
    [Fact]
    public void AMissingProductLeavesItsLineWithoutDiscount()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 9m)],
            Catalog((ProductB, Scale(allowGrouping: true))));

        Assert.Equal(0m, For(result, a).DiscountPercentage);
        Assert.Null(For(result, a).Restriction);
    }

    // Una linea que no cumple el multiplo no le hace nada a la que si: A=8 cumple desde el piso
    // (8 - 5 = 3) y se lleva su descuento por su cuenta; B=10 no cumple, se queda en cero, y
    // reporta SU propia cantidad.
    //
    // Antes B decia 16 -- el total de un grupo del que ahora ni siquiera es parte -- y quedaba
    // marcada como agrupada. Con el multiplo por linea, una linea que no califica no entra en
    // ninguna suma, asi que no tiene ningun total que reportar.
    [Fact]
    public void ALineThatMissesTheMultipleDoesNotAffectTheOneThatMeetsIt()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 8m),
                new QuotationPricingLine(b, ProductB, 10m)
            ],
            Catalog((ProductA, Scale(allowGrouping: true)), (ProductB, Scale(allowGrouping: true))));

        var lineA = For(result, a);
        Assert.Equal(5m, lineA.DiscountPercentage);
        Assert.NotEqual(QuotationDiscountOrigin.Group, lineA.Origin);
        Assert.Equal(8m, lineA.Restriction!.EvaluatedQuantity);

        var lineB = For(result, b);
        Assert.Equal(0m, lineB.DiscountPercentage);
        Assert.NotEqual(QuotationDiscountOrigin.Group, lineB.Origin);
        Assert.Equal("quotation.item.quantity_not_multiple", lineB.Restriction!.Code);
        Assert.Equal(10m, lineB.Restriction.EvaluatedQuantity);
    }

    // Recalcular nunca lanza, ni siquiera ante una escala incompleta: la línea ya estaba en la
    // cotización. Pero tampoco se lleva un descuento que nadie terminó de configurar.
    [Fact]
    public void AnIncompleteScaleNeverAppliesItsDiscount()
    {
        var item = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(item, ProductA, 6m)],
            Catalog((ProductA, new QuotationPriceScaleRef(5, 48, 5m, null, null, null))));

        var line = For(result, item);
        Assert.Equal(0m, line.DiscountPercentage);
        Assert.False(line.Restriction!.IsSatisfied);
    }

    // ---- Agrupación para ALCANZAR el tramo (2026-09-21) ----
    //
    // Hasta acá la suma sólo servía para cumplir el múltiplo de un tramo que cada línea ya había
    // alcanzado sola. Ahora también decide en qué tramo cae la línea: 3 + 3 = 6 entra al tramo
    // 6-48 aunque ninguna de las dos llegue sola.

    private static Dictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> MultiScaleCatalog(
        params (Guid ProductId, QuotationPriceScaleRef[] Scales)[] entries) =>
        entries.ToDictionary(
            entry => entry.ProductId,
            entry => (IReadOnlyCollection<QuotationPriceScaleRef>)entry.Scales);

    // Los tres tramos del producto tipo: el primero sin descuento, el segundo agrupable, el
    // tercero para volumen y sin agrupar.
    private static QuotationPriceScaleRef[] Tiers() =>
    [
        new(1, 5, 0m, QuotationPriceScaleRestriction.Multiple, 1, null, false),
        new(6, 48, 5m, QuotationPriceScaleRestriction.Multiple, 3, null, true),
        new(49, 200, 10m, QuotationPriceScaleRestriction.Multiple, 1, null, false)
    ];

    // El caso que pidió el owner: dos productos con 3 unidades cada uno, los dos con el mismo
    // tramo agrupable. Solas caen en 1-5 y no descuentan; sumadas llegan a 6 y las dos toman el
    // tramo 6-48.
    [Fact]
    public void GroupingReachesTheTierThatNoLineReachesAlone()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 3m),
                new QuotationPricingLine(b, ProductB, 3m)
            ],
            MultiScaleCatalog((ProductA, Tiers()), (ProductB, Tiers())));

        Assert.Equal(5m, For(result, a).DiscountPercentage);
        Assert.Equal(5m, For(result, b).DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.Group, For(result, a).Origin);
        Assert.Equal(6m, For(result, a).Restriction!.EvaluatedQuantity);
    }

    // Una línea que ya pasó el techo del tramo no arrastra al grupo: 100 no se suma al total del
    // tramo 6-48 porque no cabe en él. Si se sumara, 3 + 3 + 100 = 106 se saldría del rango y las
    // dos líneas chicas perderían el descuento que el grupo les consiguió.
    //
    // La de 100 conserva el tramo que alcanzó sola.
    [Fact]
    public void ALineAboveTheTierCeilingDoesNotDragTheGroup()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 3m),
                new QuotationPricingLine(b, ProductB, 3m),
                new QuotationPricingLine(c, ProductC, 100m)
            ],
            MultiScaleCatalog((ProductA, Tiers()), (ProductB, Tiers()), (ProductC, Tiers())));

        Assert.Equal(5m, For(result, a).DiscountPercentage);
        Assert.Equal(5m, For(result, b).DiscountPercentage);
        Assert.Equal(6m, For(result, a).Restriction!.EvaluatedQuantity);

        Assert.Equal(10m, For(result, c).DiscountPercentage);
        Assert.NotEqual(QuotationDiscountOrigin.Group, For(result, c).Origin);
    }

    // El caso reportado por el owner el 2026-09-21, con la escala real del catalogo sembrado:
    // 6-48, 15%, multiplo 3, agrupable. Cinco lineas de 3 y una de 5.
    //
    // Las cinco de 3 cumplen el multiplo solas y suman 15, que cae en 6-48: se llevan el 15%
    // aunque ninguna llegue sola a 6. La de 5 no cumple el multiplo, asi que no recibe nada y --
    // esto es lo que estaba roto -- tampoco entra en la suma: antes daba 20, que no es multiplo
    // de 3, y dejaba a las seis lineas en cero.
    [Fact]
    public void ALineThatMissesTheMultipleNoLongerDragsTheGroupDown()
    {
        var products = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var quantities = new decimal[] { 3m, 3m, 3m, 3m, 3m, 5m };
        var itemIds = quantities.Select(_ => Guid.NewGuid()).ToArray();

        var seeded = new QuotationPriceScaleRef(
            6, 48, 15m, QuotationPriceScaleRestriction.Multiple, 3, null, true);

        var result = QuotationScaleGroupPricing.Resolve(
            itemIds.Select((id, i) => new QuotationPricingLine(id, products[i], quantities[i]))
                .ToArray(),
            products.ToDictionary(
                id => id,
                _ => (IReadOnlyCollection<QuotationPriceScaleRef>)[seeded]));

        foreach (var itemId in itemIds.Take(5))
        {
            Assert.Equal(15m, For(result, itemId).DiscountPercentage);
            Assert.Equal(QuotationDiscountOrigin.Group, For(result, itemId).Origin);
            Assert.Equal(15m, For(result, itemId).Restriction!.EvaluatedQuantity);
        }

        var odd = For(result, itemIds[5]);
        Assert.Equal(0m, odd.DiscountPercentage);
        Assert.NotEqual(QuotationDiscountOrigin.Group, odd.Origin);
    }

    // ---- El piso de pertenencia al grupo (2026-09-21) ----

    // Una linea que el tramo ya cubre no entra al grupo, ni siquiera cuando cumple el multiplo
    // crudo. Sin este piso, 54 en una escala 50-98 de a 6 armaba un "grupo" de una sola linea:
    // su total de 54 caia en el rango y se llevaba el descuento que el multiplo desde el piso le
    // niega, marcada ademas como agrupada sin nadie con quien agrupar. Por esa puerta el criterio
    // crudo volvia a gobernar toda escala agrupable.
    [Fact]
    public void ALineTheTierAlreadyCoversNeverJoinsTheGroup()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 54m)],
            Catalog((ProductA, Scale(allowGrouping: true, multiple: 6, fromUnit: 50, toUnit: 98))));

        var line = For(result, a);
        Assert.Equal(0m, line.DiscountPercentage);
        Assert.NotEqual(QuotationDiscountOrigin.Group, line.Origin);
        Assert.Equal("quotation.item.quantity_not_multiple", line.Restriction!.Code);
    }

    // La contracara: la misma escala con 56 unidades, que si cumple desde el piso. Se lo gana
    // sola y no queda marcada como agrupada.
    [Fact]
    public void ALineThatMeetsTheFloorMultipleEarnsItsTierAlone()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 56m)],
            Catalog((ProductA, Scale(allowGrouping: true, multiple: 6, fromUnit: 50, toUnit: 98))));

        var line = For(result, a);
        Assert.Equal(5m, line.DiscountPercentage);
        Assert.NotEqual(QuotationDiscountOrigin.Group, line.Origin);
        Assert.Equal(56m, line.Restriction!.EvaluatedQuantity);
    }

    // Y el grupo sigue haciendo su trabajo con las que NO llegan al piso: ahi el multiplo se
    // cuenta crudo -- por debajo del piso no hay contra que anclar un offset -- y la cantidad
    // minima del tramo se exige sobre la suma. 24 + 30 = 54 entra en 50-98 y las dos cobran.
    //
    // Que el total sea justamente el 54 que una linea sola no puede cobrar es deliberado: el piso
    // se valida sobre la suma, el multiplo por miembro.
    [Fact]
    public void LinesBelowTheFloorStillGroupWithTheRawMultiple()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 24m),
                new QuotationPricingLine(b, ProductB, 30m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true, multiple: 6, fromUnit: 50, toUnit: 98)),
                (ProductB, Scale(allowGrouping: true, multiple: 6, fromUnit: 50, toUnit: 98))));

        Assert.All(result, line => Assert.Equal(5m, line.DiscountPercentage));
        Assert.All(result, line => Assert.Equal(QuotationDiscountOrigin.Group, line.Origin));
        Assert.All(result, line => Assert.Equal(54m, line.Restriction!.EvaluatedQuantity));
    }

    // El caso para el que existe el feature: tres unidades no llegan solas a ningun lado, y el
    // asesor eligio el tramo que arranca en 1000.
    [Fact]
    public void TheGlobalFloorGivesItsDiscountToALineThatCannotReachItAlone()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 3m)],
            Catalog((ProductA, Thousand())),
            globalFloor: 1000);

        Assert.Equal(12m, For(result, a).DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.GlobalFloor, For(result, a).Origin);
    }

    // El global no le saca a nadie lo que ya tenia: la linea de 2000 cae sola en el tramo de mil
    // al 12%, y el piso 100 solo ofrece 5%.
    [Fact]
    public void TheGlobalFloorNeverLowersADiscountTheLineAlreadyEarned()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 2000m)],
            Catalog(
                (ProductA, Thousand()),
                (ProductA, Thousand(fromUnit: 100, toUnit: 999, discount: 5m))),
            globalFloor: 100);

        Assert.Equal(12m, For(result, a).DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.Own, For(result, a).Origin);
    }

    // Empate: el global ofrece exactamente lo que la linea ya tenia. Gana lo propio, asi que no
    // queda marcada con un origen que no le cambio nada.
    [Fact]
    public void ATieKeepsTheOriginTheLineAlreadyHad()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 2000m)],
            Catalog(
                (ProductA, Thousand()),
                (ProductA, Thousand(fromUnit: 100, toUnit: 999))),
            globalFloor: 100);

        Assert.Equal(12m, For(result, a).DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.Own, For(result, a).Origin);
    }

    // Un producto sin ese piso no participa y resuelve como siempre.
    [Fact]
    public void AProductWithoutThatFloorIsUntouchedByTheGlobalDiscount()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 8m)],
            Catalog((ProductA, Scale(allowGrouping: false))),
            globalFloor: 1000);

        Assert.Equal(5m, For(result, a).DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.Own, For(result, a).Origin);
    }

    // El multiplo se sigue exigiendo: 25 no es multiplo de 10.
    [Fact]
    public void TheGlobalFloorStillRequiresTheMultiple()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 25m)],
            Catalog((ProductA, Thousand(multiple: 10))),
            globalFloor: 1000);

        Assert.Equal(0m, For(result, a).DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.Own, For(result, a).Origin);
    }

    // ...y se cuenta crudo por debajo del piso. Con la cuenta desde FromUnit, 30 - 1000 da
    // negativo y ninguna linea chica cobraria nunca el global.
    [Fact]
    public void BelowTheFloorTheMultipleIsCountedFromZero()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 30m)],
            Catalog((ProductA, Thousand(multiple: 10))),
            globalFloor: 1000);

        Assert.Equal(12m, For(result, a).DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.GlobalFloor, For(result, a).Origin);
    }

    // Con el piso global elegido, la unica condicion es la restriccion del tramo: ser multiplo, o
    // empaque entero. Nada mas. 1002 % 3 = 0, asi que descuenta -- y no importa que contado desde
    // FromUnit diera 2.
    //
    // Es decision del owner (2026-09-23) y cambia el criterio con el que nacio: hasta hoy, por
    // encima del piso se contaba desde FromUnit, y eso hacia que 999 unidades cobraran el 12% y
    // 1002 no. Un vendedor que sube la cantidad y pierde el descuento reporta eso como defecto,
    // con razon.
    [Fact]
    public void TheGlobalFloorOnlyChecksTheMultipleCountedRaw()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 1002m)],
            Catalog((ProductA, Thousand(multiple: 3))),
            globalFloor: 1000);

        Assert.Equal(12m, For(result, a).DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.GlobalFloor, For(result, a).Origin);
    }

    // Y sigue exigiendola: 50 % 3 = 2, asi que no descuenta. "Solo valida el multiplo" no es
    // "no valida nada".
    //
    // La cantidad va por DEBAJO del piso a proposito: una de 1003 cae dentro del tramo 1000-5000
    // y se gana el 12% por su cuenta, sin que el global tenga nada que ver.
    [Fact]
    public void TheGlobalFloorStillRejectsAQuantityThatIsNotAMultiple()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 50m)],
            Catalog((ProductA, Thousand(multiple: 3))),
            globalFloor: 1000);

        Assert.Equal(0m, For(result, a).DiscountPercentage);
    }

    // PackagingUnit bloquea igual: 25 no son paquetes enteros de 12.
    [Fact]
    public void TheGlobalFloorStillRequiresThePackagingUnit()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 25m)],
            Catalog((ProductA, new QuotationPriceScaleRef(
                1000, 5000, 12m, QuotationPriceScaleRestriction.PackagingUnit, null, 12))),
            globalFloor: 1000);

        Assert.Equal(0m, For(result, a).DiscountPercentage);
    }

    // Una escala incompleta --la que deja la copia de escalas en Catalog, con rango y descuento
    // pero sin restriccion-- nunca se cumple, tampoco como piso global. Si pasara, el global
    // regalaria el descuento de un tramo que nadie termino de configurar.
    [Fact]
    public void AnIncompleteScaleNeverBecomesTheGlobalDiscount()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 3m)],
            Catalog((ProductA, new QuotationPriceScaleRef(
                1000, 5000, 12m, Restriction: null, Multiple: null, PackagingUnit: null))),
            globalFloor: 1000);

        Assert.Equal(0m, For(result, a).DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.Own, For(result, a).Origin);
    }

    // Quantity es decimal(10,2). Una cantidad fraccionaria contra un tramo de a 3 no es multiplo
    // y no descuenta: el resto decimal no se redondea a favor de nadie.
    [Fact]
    public void AFractionalQuantityDoesNotSatisfyTheMultiple()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 2.5m)],
            Catalog((ProductA, Thousand(multiple: 3))),
            globalFloor: 1000);

        Assert.Equal(0m, For(result, a).DiscountPercentage);
    }

    // Dos tramos del mismo producto arrancando en el mismo piso. Gana el de mayor descuento y no
    // el primero que EF haya materializado: el orden de esa coleccion no esta garantizado, y sin
    // criterio la misma cotizacion se valorizaria distinto entre dos lecturas.
    [Fact]
    public void WithTwoScalesOnTheSameFloorTheBestDiscountWins()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 3m)],
            Catalog(
                (ProductA, Thousand(discount: 8m)),
                (ProductA, Thousand(discount: 15m))),
            globalFloor: 1000);

        Assert.Equal(15m, For(result, a).DiscountPercentage);
    }

    // Sin piso global el resultado es identico al de siempre: la regresion que protege a las
    // cotizaciones que ya existen.
    [Fact]
    public void WithoutAGlobalFloorNothingChanges()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 3m)],
            Catalog((ProductA, Thousand())));

        Assert.Equal(0m, For(result, a).DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.Own, For(result, a).Origin);
    }
}
