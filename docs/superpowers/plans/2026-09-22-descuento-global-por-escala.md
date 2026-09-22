# Descuento global por escala — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el asesor elija un piso de escala (`FromUnit`) para toda la cotización, y que cada línea tome el tramo de su propio producto con ese piso cuando mejora lo que ya tenía.

**Architecture:** Campo `GlobalScaleFloor` en el agregado `Quotation` —que cotización y pedido comparten, porque el pedido no tiene líneas propias— aplicado como tercer candidato dentro de `QuotationScaleGroupPricing.Resolve`, el único punto por donde ya pasan los ocho handlers que recalculan. El origen del descuento se persiste en la línea y se expone por `QuotationResponseComposer`, que es el borde de presentación de todas las respuestas de cotización y de pedido.

**Tech Stack:** .NET 10, EF Core + PostgreSQL, FluentValidation, xUnit, Testcontainers para integración.

**Spec:** [`docs/superpowers/specs/2026-09-22-descuento-global-por-escala-design.md`](../specs/2026-09-22-descuento-global-por-escala-design.md)

## Global Constraints

- **TDD obligatorio.** RED antes que GREEN, con evidencia literal de las dos corridas.
- **No inventar.** Campos, estados, rutas, permisos y códigos de error tienen que existir en el código. Lo que falte se registra como decisión pendiente.
- **Comandos en PowerShell.** `A; if ($?) { B }`, nunca `&&`. `curl.exe`, nunca `curl`.
- **`Api.exe` corriendo bloquea todo.** `dotnet build`, `dotnet test` y `dotnet ef` fallan por archivo bloqueado. Si aparece `MSB3021`, buscar el proceso por `CommandLine` — puede estar corriendo como `dotnet Api.dll` y `tasklist` no ve `Api.exe`.
- **Commits: conventional commits, sin atribución de IA.** Ni `Co-Authored-By` ni menciones de asistentes.
- **Comentarios y textos nuevos en español colombiano, tuteando.** Nada de voseo. Identificadores, códigos de error y nombres de enum quedan en inglés: son contrato.
- **No se crea ningún permiso nuevo.** Se usan `QuotationsPermissions.QuotationManage` y `OrdersPermissions.OrderManage`, que ya existen con su política registrada.
- **`dotnet format` no sirve como verificación en este repo:** el baseline trae ~90k errores `ENDOFLINE` por CRLF. Si se corre, se mide con `git stash`, nunca contra cero.
- **Un permiso, un handler, un tenant:** todo handler nuevo llama `QuotationsAuthorization.EnsureAuthorized` antes de tocar el repositorio, y devuelve 403, nunca 404.

## Review Focus

Cinco cosas que el spec implica, que ninguna tarea probaría por su cuenta, y que muerden a una persona de verdad. Cada una tiene su prueba asignada a la tarea que la posee.

1. **Escala incompleta con el piso elegido.** Un producto copiado deja escalas con `Restriction: null`. Si el piso global coincide con el `FromUnit` de una de ésas, `Evaluate` la da por no satisfecha y la línea no descuenta — correcto, pero nadie lo probaría. → Tarea 1, paso 9.
2. **Cantidad decimal.** `QuotationItem.Quantity` es `decimal` con precisión (10,2). Una línea de 2.5 unidades contra un tramo de a 3 entra al `%` con decimales. → Tarea 1, paso 10.
3. **Dos escalas del mismo producto con el mismo `FromUnit`.** `FirstOrDefault` toma la primera que EF haya materializado, y el orden no está garantizado. Hay que elegir determinísticamente el de mayor descuento. → Tarea 1, paso 11.
4. **`floor` en cero o negativo en el `PUT`.** Sin validador entra al recálculo y no coincide con ninguna escala, así que sale `floor_not_available` en vez del error de forma. → validador en Tarea 5, paso 1; prueba en Tarea 6, paso 2, caso 5.
5. **La compuerta de compra mínima quita los descuentos.** Cuando quita todo, el origen de cada línea tiene que volver a `Own`, o la pantalla muestra "descuento global aplicado" sobre un 0%. → código en Tarea 2, paso 7; prueba en Tarea 6, paso 6.
6. **Agregar una línea deja el piso sin respaldo.** El spec decide a propósito que el piso **no** se limpia solo. Sin prueba, el primero que lo vea lo "arregla". → Tarea 6, paso 7.

---

### Task 1: El piso global dentro de la resolución de descuentos

Es el núcleo y es puro: sin base de datos, sin HTTP. Se puede probar entero con xUnit.

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationDiscountOrigin.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationScaleRestrictionRule.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationScaleGroupPricing.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationScaleGroupPricingTests.cs`

**Interfaces:**
- Consumes: `QuotationPriceScaleRef(int FromUnit, int ToUnit, decimal Discount, QuotationPriceScaleRestriction? Restriction, int? Multiple, int? PackagingUnit, bool AllowGrouping = false)`; `QuotationScaleRestrictionResult(bool IsSatisfied, string? Code, decimal EvaluatedQuantity, decimal Shortfall)`.
- Produces:
  - `enum QuotationDiscountOrigin { Own, Group, GlobalFloor }` en `Modules.Quotations.Domain`
  - `QuotationLinePricing(Guid ItemId, decimal DiscountPercentage, QuotationPriceScaleRef? Scale, QuotationScaleRestrictionResult? Restriction, QuotationDiscountOrigin Origin)`
  - `QuotationScaleGroupPricing.Resolve(IReadOnlyCollection<QuotationPricingLine> lines, IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> scalesByProduct, int? globalFloor = null)`
  - `QuotationScaleRestrictionRule.EvaluateFromZero(QuotationPriceScaleRef scale, decimal quantity)`

El default `= null` en `globalFloor` es deliberado: deja compilando a `QuotationPricingRecalculation`, que se actualiza en la Tarea 4.

- [ ] **Step 1: Crear el enum del origen**

Va en `Domain` y no en `Application` porque la Tarea 2 lo persiste en `QuotationItem`, que es Domain, y Domain no referencia Application.

Crear `src/Modules/Quotations/Modules.Quotations.Domain/QuotationDiscountOrigin.cs`:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>
/// De dónde salió el descuento de una línea. Reemplaza al bool <c>Grouped</c>, que sólo sabía
/// distinguir dos de los tres casos y que además nunca llegó a viajar a la respuesta aunque su
/// documentación dijera que sí.
///
/// Viaja a la pantalla porque "te lo ganaste sola", "te lo dieron entre todas" y "te lo dio el
/// descuento global que eligió el asesor" no son lo mismo para quien lee una cotización, y
/// desde afuera no hay manera de reconstruir cuál fue.
/// </summary>
public enum QuotationDiscountOrigin
{
    /// <summary>La cantidad de la línea cayó sola en un tramo del producto.</summary>
    Own,

    /// <summary>El tramo se lo dio la suma del grupo y no su propia cantidad.</summary>
    Group,

    /// <summary>El tramo se lo dio el piso global que el asesor eligió para la cotización.</summary>
    GlobalFloor
}
```

- [ ] **Step 2: Escribir la prueba que falla — el global le da descuento a una línea chica**

Agregar al final de `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationScaleGroupPricingTests.cs`, antes del cierre de la clase:

```csharp
    // El caso para el que existe el feature: tres unidades no llegan solas a ningún lado, y el
    // asesor eligió el tramo que arranca en 1000.
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
```

Y agregar el helper junto a los que ya existen arriba de la clase:

```csharp
    // El tramo "de mil": de a 1, así que ninguna cantidad falla el múltiplo salvo que la prueba
    // lo pida explícitamente.
    private static QuotationPriceScaleRef Thousand(
        int multiple = 1, decimal discount = 12m, int fromUnit = 1000, int toUnit = 5000) =>
        new(fromUnit, toUnit, discount, QuotationPriceScaleRestriction.Multiple, multiple, null);
```

El archivo necesita `using Modules.Quotations.Domain;` arriba, junto al `using Modules.Quotations.Application;` que ya tiene.

- [ ] **Step 3: Correr la prueba y verificar que falla**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationScaleGroupPricingTests"
```

Esperado: no compila. `QuotationScaleGroupPricing.Resolve` no acepta `globalFloor`, y `QuotationLinePricing` no tiene `Origin`.

- [ ] **Step 4: Agregar `EvaluateFromZero` a la regla de restricción**

En `src/Modules/Quotations/Modules.Quotations.Application/QuotationScaleRestrictionRule.cs`, justo después del método `Evaluate`:

```csharp
    /// <summary>
    /// La misma restricción, contada **desde cero** en vez de desde <c>FromUnit</c>.
    ///
    /// Existe por el piso global: una línea que no alcanza el tramo por su cuenta está por
    /// debajo de <c>FromUnit</c>, y ahí el offset da negativo y <see cref="EvaluateStep"/> la
    /// rechaza siempre. Sin esto, ninguna línea chica podría cobrar nunca el descuento global —
    /// que es exactamente para lo que el global existe.
    ///
    /// No es criterio nuevo: <c>QuotationScaleGroupPricing.Qualifies</c> ya cuenta crudo por el
    /// mismo motivo, y su comentario lo dice — por debajo del piso no hay contra qué anclar un
    /// offset.
    ///
    /// Quien llama decide cuándo usarla: sólo cuando la cantidad está por debajo del piso del
    /// tramo. Por encima manda <see cref="Evaluate"/>, o el global terminaría aflojando una
    /// restricción que la línea ya alcanzaba sola.
    ///
    /// Para <c>PackagingUnit</c> es idéntica a <see cref="Evaluate"/>: el empaque ya contaba
    /// crudo.
    /// </summary>
    public static QuotationScaleRestrictionResult EvaluateFromZero(
        QuotationPriceScaleRef scale, decimal quantity) =>
        scale.Restriction switch
        {
            QuotationPriceScaleRestriction.Multiple => EvaluateStep(
                scale.Multiple, quantity, 0, "quotation.item.quantity_not_multiple"),
            QuotationPriceScaleRestriction.PackagingUnit => EvaluateStep(
                scale.PackagingUnit, quantity, 0,
                "quotation.item.quantity_not_packaging_unit"),
            null => new QuotationScaleRestrictionResult(false, IncompleteScalesCode, quantity, 0m),
            _ => throw new ArgumentOutOfRangeException(
                nameof(scale), scale.Restriction, "Unknown price scale restriction.")
        };
```

- [ ] **Step 5: Cambiar `Grouped` por `Origin` en `QuotationLinePricing`**

En `src/Modules/Quotations/Modules.Quotations.Application/QuotationScaleGroupPricing.cs`, agregar el using y reemplazar el record:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;
```

```csharp
/// <param name="Origin">De dónde salió el descuento. Viaja a la respuesta: "te lo ganaste
/// sola", "te lo dieron entre todas" y "te lo dio el piso global" no son lo mismo en pantalla.
/// </param>
public sealed record QuotationLinePricing(
    Guid ItemId,
    decimal DiscountPercentage,
    QuotationPriceScaleRef? Scale,
    QuotationScaleRestrictionResult? Restriction,
    QuotationDiscountOrigin Origin);
```

Y actualizar las tres construcciones que ya existen en el archivo:

- en `OwnPricing`, la de la escala nula: `new QuotationLinePricing(line.ItemId, 0m, null, null, QuotationDiscountOrigin.Own)`
- en `OwnPricing`, la del final: último argumento `QuotationDiscountOrigin.Own`
- en `Upgrade`, la de `best`: último argumento `QuotationDiscountOrigin.Group`

- [ ] **Step 6: Agregar el piso global a `Resolve` y `ToPricing`**

En el mismo archivo:

```csharp
    public static IReadOnlyList<QuotationLinePricing> Resolve(
        IReadOnlyCollection<QuotationPricingLine> lines,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> scalesByProduct,
        int? globalFloor = null)
    {
        var groupTotals = GroupTotals(lines, scalesByProduct);

        return lines
            .Select(line => ToPricing(
                line, ScalesOf(scalesByProduct, line.ProductId), groupTotals, globalFloor))
            .ToArray();
    }
```

```csharp
    private static QuotationLinePricing ToPricing(
        QuotationPricingLine line,
        IReadOnlyCollection<QuotationPriceScaleRef> scales,
        Dictionary<(int, int, int), decimal> groupTotals,
        int? globalFloor)
    {
        var own = OwnPricing(line, QuotationDiscountResolver.Resolve(scales, line.Quantity));
        var best = Upgrade(line, scales, groupTotals, own) ?? own;

        return GlobalPricing(line, scales, globalFloor, best) ?? best;
    }
```

- [ ] **Step 7: Implementar `GlobalPricing`**

Agregar al final de la clase, antes de `IsGroupable`:

```csharp
    /// <summary>
    /// El tramo que el asesor eligió para toda la cotización, identificado por su piso
    /// (<c>FromUnit</c>). Las escalas son por producto, así que cada línea busca el tramo con
    /// ese piso **en su propio producto**: uno que no lo tenga simplemente no participa.
    ///
    /// Sólo reemplaza con un descuento **estrictamente** mayor, igual que <see cref="Upgrade"/>:
    /// el global es un piso y no un techo, y nadie pierde descuento por activarlo.
    ///
    /// La restricción se sigue exigiendo — el global decide qué tramo se usa, no afloja el
    /// múltiplo ni el empaque. Lo que sí cambia es desde dónde se cuenta: por debajo del piso
    /// no hay contra qué anclar un offset, así que ahí se cuenta crudo. Ver
    /// <see cref="QuotationScaleRestrictionRule.EvaluateFromZero"/>.
    ///
    /// Con dos tramos del mismo producto empatados en el piso gana el de mayor descuento, y no
    /// el primero que haya materializado EF: el orden de esa colección no está garantizado, y
    /// sin este criterio la misma cotización podría valorizarse distinto entre dos lecturas.
    /// </summary>
    private static QuotationLinePricing? GlobalPricing(
        QuotationPricingLine line,
        IReadOnlyCollection<QuotationPriceScaleRef> scales,
        int? globalFloor,
        QuotationLinePricing best)
    {
        if (globalFloor is not { } floor)
        {
            return null;
        }

        var scale = scales
            .Where(candidate => candidate.FromUnit == floor)
            .OrderByDescending(candidate => candidate.Discount)
            .FirstOrDefault();

        if (scale is null || scale.Discount <= best.DiscountPercentage)
        {
            return null;
        }

        var restriction = line.Quantity < scale.FromUnit
            ? QuotationScaleRestrictionRule.EvaluateFromZero(scale, line.Quantity)
            : QuotationScaleRestrictionRule.Evaluate(scale, line.Quantity);

        return restriction.IsSatisfied
            ? new QuotationLinePricing(
                line.ItemId,
                scale.Discount,
                scale,
                restriction,
                QuotationDiscountOrigin.GlobalFloor)
            : null;
    }
```

- [ ] **Step 8: Actualizar los 14 asserts de `Grouped` y correr la suite**

En `QuotationScaleGroupPricingTests.cs` hay 14 asserts sobre `Grouped` (líneas 55, 78, 105, 175, 193, 250, 255, 315, 344, 374, 380, 401, 418, 444). La traducción es mecánica:

- `Assert.False(x.Grouped)` → `Assert.NotEqual(QuotationDiscountOrigin.Group, x.Origin)`
- `Assert.True(x.Grouped)` → `Assert.Equal(QuotationDiscountOrigin.Group, x.Origin)`
- `Assert.All(result, line => Assert.False(line.Grouped))` → `Assert.All(result, line => Assert.NotEqual(QuotationDiscountOrigin.Group, line.Origin))`

`NotEqual` y no `Assert.Equal(Own, ...)`: esas pruebas afirman que el descuento no vino del grupo, no que vino de la línea sola. Ninguna de ellas pasa `globalFloor`, así que hoy dan `Own` — pero fijar `Own` las volvería frágiles frente a un caso futuro.

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationScaleGroupPricingTests"
```

Esperado: PASS, incluida `TheGlobalFloorGivesItsDiscountToALineThatCannotReachItAlone`.

- [ ] **Step 9: Pruebas del resto de los casos del spec**

Agregar a la misma clase:

```csharp
    // El global no le saca a nadie lo que ya tenía: la línea de 2000 cae sola en el tramo de
    // mil al 12%, y el piso 100 sólo ofrece 5%.
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

    // Empate: el global ofrece exactamente lo que la línea ya tenía. Gana lo propio, así que no
    // queda marcada con un origen que no le cambió nada.
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

    // El múltiplo se sigue exigiendo: 25 no es múltiplo de 10.
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
    // negativo y ninguna línea chica cobraría nunca el global.
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

    // Por encima del piso manda la cuenta desde FromUnit: (1002 - 1000) % 3 = 2, así que no
    // descuenta, aunque 1002 % 3 sí dé 0. El global no afloja lo que la línea ya alcanzaba.
    [Fact]
    public void AboveTheFloorTheMultipleIsStillCountedFromTheFloor()
    {
        var a = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [new QuotationPricingLine(a, ProductA, 1002m)],
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

    // Review Focus 1: una escala incompleta —la que deja la copia de escalas en Catalog, con
    // rango y descuento pero sin restricción— nunca se cumple, tampoco como piso global. Si
    // pasara, el global regalaría el descuento de un tramo que nadie terminó de configurar.
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

    // Sin piso global el resultado es idéntico al de siempre: la regresión que protege a las
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
```

`Catalog` arma un diccionario con **una** escala por producto, y dos de estas pruebas necesitan dos. Reemplazar el helper por uno que agrupe:

```csharp
    private static Dictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> Catalog(
        params (Guid ProductId, QuotationPriceScaleRef Scale)[] entries) =>
        entries
            .GroupBy(entry => entry.ProductId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<QuotationPriceScaleRef>)
                    group.Select(entry => entry.Scale).ToArray());
```

Las llamadas que ya existen no cambian: un solo par por producto sigue dando una colección de uno.

- [ ] **Step 10: Prueba de cantidad decimal (Review Focus 2)**

```csharp
    // Review Focus 2: Quantity es decimal(10,2). Una cantidad fraccionaria contra un tramo de a
    // 3 no es múltiplo y no descuenta — el resto decimal no se redondea a favor de nadie.
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
```

- [ ] **Step 11: Prueba del empate de pisos (Review Focus 3)**

```csharp
    // Review Focus 3: dos tramos del mismo producto arrancando en el mismo piso. Gana el de
    // mayor descuento y no el primero que EF haya materializado: el orden de esa colección no
    // está garantizado, y sin criterio la misma cotización se valorizaría distinto entre dos
    // lecturas.
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
```

- [ ] **Step 12: Correr toda la suite unitaria**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
```

Esperado: PASS. Si algo falla, comparar por **nombre de prueba** contra la corrida previa — no contra un número recordado.

- [ ] **Step 13: Commit**

```powershell
git add src/Modules/Quotations/Modules.Quotations.Domain/QuotationDiscountOrigin.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationScaleGroupPricing.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationScaleRestrictionRule.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationScaleGroupPricingTests.cs
git commit -m "feat(quotations): resolver el descuento por piso de escala global"
```

---

### Task 2: El campo en el agregado y el origen en la línea

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs:815-827` (`ApplyGroupDiscounts`), más el campo y los dos mutadores nuevos
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPricingRecalculation.cs` (para que compile con la firma nueva)
- Create: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationGlobalScaleFloorTests.cs`

**Interfaces:**
- Consumes: `QuotationDiscountOrigin` (Tarea 1), `MemberId`, `QuotationItemId`, `QuotationsDomainException`.
- Produces:
  - `record QuotationItemDiscount(decimal Percentage, QuotationDiscountOrigin Origin)` en Domain
  - `Quotation.GlobalScaleFloor` (`int?`)
  - `Quotation.SetGlobalScaleFloor(int? floor, MemberId updatedBy, DateTimeOffset occurredAt)`
  - `Quotation.SetGlobalScaleFloorAfterConversion(int? floor, MemberId updatedBy, DateTimeOffset occurredAt)`
  - `Quotation.ApplyGroupDiscounts(IReadOnlyDictionary<QuotationItemId, QuotationItemDiscount> discounts, DateTimeOffset occurredAt)`
  - `QuotationItem.DiscountOrigin` (`QuotationDiscountOrigin`)

- [ ] **Step 1: Escribir la prueba que falla**

Crear `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationGlobalScaleFloorTests.cs`. El armado del agregado tiene que copiar el que usan las pruebas de `Quotation` que ya existen en ese proyecto — abrir una de ellas y reusar su helper de construcción en vez de inventar uno.

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationGlobalScaleFloorTests
{
    [Fact]
    public void SettingTheGlobalScaleFloorIsAnEditAndBumpsTheVersion()
    {
        var quotation = ADraftQuotation();
        var versionBefore = quotation.Version;
        var editor = MemberId.New();
        var now = DateTimeOffset.UtcNow;

        quotation.SetGlobalScaleFloor(1000, editor, now);

        Assert.Equal(1000, quotation.GlobalScaleFloor);
        Assert.Equal(versionBefore + 1, quotation.Version);
        Assert.Equal(editor, quotation.UpdatedBy);
        Assert.Equal(now, quotation.UpdatedAt);
    }

    [Fact]
    public void ClearingTheGlobalScaleFloorLeavesItNull()
    {
        var quotation = ADraftQuotation();
        quotation.SetGlobalScaleFloor(1000, MemberId.New(), DateTimeOffset.UtcNow);

        quotation.SetGlobalScaleFloor(null, MemberId.New(), DateTimeOffset.UtcNow);

        Assert.Null(quotation.GlobalScaleFloor);
    }

    // Una cotización convertida no se edita por la puerta normal: para eso está el mutador
    // propio del pedido, igual que AddItemAfterConversion.
    [Fact]
    public void AConvertedQuotationRejectsTheOrdinarySetter()
    {
        var quotation = AConvertedQuotation();

        var error = Assert.Throws<QuotationsDomainException>(
            () => quotation.SetGlobalScaleFloor(1000, MemberId.New(), DateTimeOffset.UtcNow));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
    }

    [Fact]
    public void AConvertedQuotationAcceptsTheAfterConversionSetter()
    {
        var quotation = AConvertedQuotation();

        quotation.SetGlobalScaleFloorAfterConversion(
            1000, MemberId.New(), DateTimeOffset.UtcNow);

        Assert.Equal(1000, quotation.GlobalScaleFloor);
    }
}
```

`quotation.quotation.not_editable` es el código que tira `EnsureEditable`: **verificarlo** leyendo `Quotation.cs:851` y usar el literal real, no éste de memoria. `ADraftQuotation` y `AConvertedQuotation` se arman con los helpers que ya tienen las pruebas de `Quotation` en ese proyecto.

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationGlobalScaleFloorTests"
```

Esperado: no compila — `SetGlobalScaleFloor` no existe.

- [ ] **Step 3: Agregar el origen a la línea**

En `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs`, junto a `DiscountPercentage`:

```csharp
    /// <summary>
    /// De dónde salió <see cref="DiscountPercentage"/>. Se persiste porque la respuesta se arma
    /// leyendo la línea guardada y no el resultado del recálculo: sin columna, la pantalla no
    /// tiene cómo saber si el descuento se lo ganó la línea sola, se lo dio el grupo o se lo dio
    /// el piso global que eligió el asesor.
    /// </summary>
    public QuotationDiscountOrigin DiscountOrigin { get; private set; }
```

Cambiar `ApplyDiscount` para que reciba el origen:

```csharp
    /// <summary>Cambia sólo el descuento y rehace los importes de la línea, conservando cantidad,
    /// precio y tasa. Lo usa el recálculo global de la cotización
    /// (<see cref="Quotation.ApplyGroupDiscounts"/>): la agrupación de escalas, el piso global y
    /// la compuerta de compra mínima mueven el descuento sin que la línea haya cambiado en nada
    /// más.</summary>
    internal void ApplyDiscount(
        decimal discountPercentage, QuotationDiscountOrigin origin, DateTimeOffset occurredAt)
    {
        DiscountOrigin = origin;
        Apply(Quantity, UnitPrice, discountPercentage, TaxPercentage, occurredAt);
    }
```

`Create`, `UpdateQuantity` y `Reprice` resuelven el descuento contra la escala de la propia línea, así que dejan `DiscountOrigin` en `Own` — su valor por defecto. No hace falta tocarlos: el recálculo corre después de cada uno de esos caminos y fija el origen definitivo.

- [ ] **Step 4: Crear el record del descuento con origen**

Crear `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItemDiscount.cs`:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>
/// El descuento que el recálculo le baja a una línea, con su procedencia. Van juntos porque
/// aplicar uno sin el otro deja la línea diciendo que su 12% se lo dio el piso global cuando en
/// realidad la compuerta de compra mínima ya se lo quitó.
/// </summary>
public sealed record QuotationItemDiscount(decimal Percentage, QuotationDiscountOrigin Origin);
```

- [ ] **Step 5: Campo y mutadores en el agregado**

En `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs`, junto a las demás propiedades:

```csharp
    /// <summary>
    /// El piso de escala que el asesor eligió para toda la cotización, o <c>null</c> si no
    /// eligió ninguno. No es un porcentaje: es el <c>FromUnit</c> de un tramo, y cada línea
    /// resuelve contra el tramo de **su** producto que arranca ahí.
    ///
    /// Vive en la cotización y no en el pedido porque el pedido no tiene líneas propias: las
    /// suyas son éstas.
    /// </summary>
    public int? GlobalScaleFloor { get; private set; }
```

Y los dos mutadores, junto a `AddItemAfterConversion`:

```csharp
    /// <summary>
    /// Elige el piso de escala global, o lo quita con <c>null</c>. Es una edición del
    /// encabezado: sube la versión y deja rastro de quién la hizo.
    ///
    /// Que el piso exista en algún producto lo comprueba el caso de uso, que es quien puede
    /// mirar el catálogo. Acá sólo se exige que sea un piso posible.
    /// </summary>
    public void SetGlobalScaleFloor(int? floor, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        EnsureEditable();
        SetGlobalScaleFloorCore(floor, updatedBy, occurredAt);
    }

    /// <summary>
    /// El mismo cambio sobre la cotización de un pedido que sigue <c>OrderStatus.Pending</c>,
    /// sin exigir que la cotización sea editable. Misma excepción y mismo motivo que
    /// <see cref="AddItemAfterConversion"/>: el estado del pedido lo comprueba su caso de uso.
    /// </summary>
    public void SetGlobalScaleFloorAfterConversion(
        int? floor, MemberId updatedBy, DateTimeOffset occurredAt) =>
        SetGlobalScaleFloorCore(floor, updatedBy, occurredAt);

    private void SetGlobalScaleFloorCore(
        int? floor, MemberId updatedBy, DateTimeOffset occurredAt)
    {
        if (floor is { } value && value < 1)
        {
            throw new QuotationsDomainException(
                "quotation.global_scale.floor_invalid",
                "The global scale floor must be a positive unit count.");
        }

        GlobalScaleFloor = floor;
        Touch(updatedBy, occurredAt);
    }
```

`Touch` ya hace `RecalculateTotals()`, `UpdatedBy`, `UpdatedAt` y `Version++` — es el mismo helper que usan las otras ediciones.

- [ ] **Step 6: Cambiar `ApplyGroupDiscounts` para que lleve el origen**

Reemplazar el cuerpo actual (`Quotation.cs:815-827`):

```csharp
    public void ApplyGroupDiscounts(
        IReadOnlyDictionary<QuotationItemId, QuotationItemDiscount> discounts,
        DateTimeOffset occurredAt)
    {
        foreach (var item in _items)
        {
            if (discounts.TryGetValue(item.Id, out var discount))
            {
                item.ApplyDiscount(discount.Percentage, discount.Origin, occurredAt);
            }
        }

        RecalculateTotals();
    }
```

- [ ] **Step 7: Actualizar su único llamador para que compile**

En `src/Modules/Quotations/Modules.Quotations.Application/QuotationPricingRecalculation.cs`:

```csharp
        var discounts = resolved.ToDictionary(
            line => new QuotationItemId(line.ItemId),
            line => new QuotationItemDiscount(line.DiscountPercentage, line.Origin));
```

y el barrido de la compuerta:

```csharp
        quotation.ApplyGroupDiscounts(
            discounts.ToDictionary(
                discount => discount.Key,
                _ => new QuotationItemDiscount(0m, QuotationDiscountOrigin.Own)),
            occurredAt);
```

El archivo ya tiene `using Modules.Quotations.Domain;`.

- [ ] **Step 8: Correr y verificar que pasa**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
```

Esperado: PASS.

- [ ] **Step 9: Commit**

```powershell
git add src/Modules/Quotations/Modules.Quotations.Domain src/Modules/Quotations/Modules.Quotations.Application/QuotationPricingRecalculation.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationGlobalScaleFloorTests.cs
git commit -m "feat(quotations): guardar el piso de escala global y el origen del descuento"
```

---

### Task 3: Persistencia

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:52-...` (bloque `ConfigureQuotation`) y `:174-...` (bloque de `quotation_items`)
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddQuotationGlobalScaleFloor.cs` (la genera EF)

**Interfaces:**
- Consumes: `Quotation.GlobalScaleFloor`, `QuotationItem.DiscountOrigin` (Tarea 2).
- Produces: columnas `quotations.quotations.global_scale_floor` y `quotations.quotation_items.discount_origin`.

- [ ] **Step 1: Mapear las dos columnas**

En el bloque de la cotización, junto a las otras `Property`:

```csharp
        quotation.Property(value => value.GlobalScaleFloor).HasColumnName("global_scale_floor");
```

En el bloque de `quotation_items`, junto a `discount_percentage`:

```csharp
        // Texto y no entero, igual que QuotationHistoryEventType: sumar un valor al enum no va a
        // necesitar migración.
        item.Property(value => value.DiscountOrigin)
            .HasColumnName("discount_origin")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
```

- [ ] **Step 2: Detener `Api.exe` si está corriendo**

```powershell
Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like '*Api*' } | Select-Object ProcessId, CommandLine
```

Si aparece, `Stop-Process -Id <id>`. Sin esto la migración muere con `MSB3021`.

- [ ] **Step 3: Generar la migración**

```powershell
dotnet ef migrations add AddQuotationGlobalScaleFloor --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
```

Con el factory de diseño, **sin** `--startup-project`: `Api.csproj` no referencia `Microsoft.EntityFrameworkCore.Design`.

- [ ] **Step 4: Poner el default de las filas que ya existen**

EF genera `discount_origin` sin default y la tabla puede tener filas. Editar el `Up` de la migración recién creada:

```csharp
            migrationBuilder.AddColumn<string>(
                name: "discount_origin",
                schema: "quotations",
                table: "quotation_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Own");
```

`Own` es lo que esas filas efectivamente son: se valorizaron antes de que el global existiera, y la agrupación nunca marcó nada.

- [ ] **Step 5: Verificar que la migración compila y aplica**

```powershell
dotnet build src/Modules/Quotations/Modules.Quotations.Infrastructure
```

Esperado: build exitoso. La aplicación contra la base local la hace la suite de integración de la Tarea 6, que levanta su propio contenedor.

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Quotations/Modules.Quotations.Infrastructure
git commit -m "feat(quotations): migrar el piso de escala global y el origen del descuento"
```

---

### Task 4: El recálculo usa el piso, y la respuesta lo cuenta

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPricingRecalculation.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs` (`QuotationItemDto`, `QuotationDto`, `QuotationItemResponse`, `QuotationResponse`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationMapping.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationResponseComposer.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationGlobalScaleFloorTests.cs`

**Interfaces:**
- Consumes: `QuotationScaleGroupPricing.Resolve(..., int? globalFloor)` (Tarea 1), `Quotation.GlobalScaleFloor` (Tarea 2), `IQuotationProductLookup.FindManyAsync`.
- Produces:
  - `QuotationItemDto.DiscountOrigin` (`string`), último parámetro posicional
  - `QuotationDto.GlobalScaleFloor` (`int?`), último parámetro posicional
  - `QuotationItemResponse.DiscountOrigin` (`string`), último
  - `QuotationResponse.GlobalScaleFloor` (`int?`) y `QuotationResponse.AvailableGlobalScaleFloors` (`IReadOnlyCollection<int>`), últimos

Todos los campos van **al final** de sus records posicionales, que es como este repo agregó `AllowGrouping` y `AdvisorDisplayName`: las construcciones que ya existen no se tocan.

- [ ] **Step 1: El recálculo pasa el piso**

En `QuotationPricingRecalculation.ApplyAsync`:

```csharp
        var resolved = QuotationScaleGroupPricing.Resolve(
            quotation.Items
                .Select(item => new QuotationPricingLine(
                    item.Id.Value, item.ProductId, item.Quantity))
                .ToArray(),
            scalesByProduct,
            quotation.GlobalScaleFloor);
```

- [ ] **Step 2: Los DTO llevan el origen y el piso**

En `QuotationsDtos.cs`, al final de `QuotationItemDto`:

```csharp
    int Position,
    /// <summary>El nombre de <c>QuotationDiscountOrigin</c>: <c>Own</c>, <c>Group</c> o
    /// <c>GlobalFloor</c>. Los enums viajan con su nombre porque el diccionario lo tiene el
    /// frontend.</summary>
    string DiscountOrigin);
```

Al final de `QuotationDto`, después de `Version`:

```csharp
    /// <summary>El piso de escala global elegido, o null. Ver
    /// <c>Quotation.GlobalScaleFloor</c>.</summary>
    int? GlobalScaleFloor = null);
```

El default deja compilando a las construcciones que ya existen; sólo `QuotationMapping.ToDto` lo llena.

- [ ] **Step 3: El mapper los llena**

En `QuotationMapping.ToDto`, en la proyección de las líneas agregar `item.DiscountOrigin.ToString()` como último argumento, y `quotation.GlobalScaleFloor` como último argumento del `QuotationDto`.

- [ ] **Step 4: La respuesta los expone**

En `QuotationsDtos.cs`, al final de `QuotationItemResponse`:

```csharp
    int Position,
    string DiscountOrigin);
```

Al final de `QuotationResponse`, después de `Version`:

```csharp
    int Version,
    /// <summary>El piso de escala global elegido para esta cotización, o null.</summary>
    int? GlobalScaleFloor,
    /// <summary>Los pisos entre los que el asesor puede elegir: los <c>FromUnit</c> distintos de
    /// las escalas de los productos que esta cotización tiene cargados, ordenados ascendente.
    /// **Completo incluso vacío** — una colección que desaparece obliga a la pantalla a
    /// reconstruirla desde las escalas línea por línea.</summary>
    IReadOnlyCollection<int> AvailableGlobalScaleFloors);
```

En `QuotationResponseComposer.ComposeAsync`, agregar los tres argumentos al final del `new QuotationResponse(...)`:

```csharp
            quotation.Version,
            quotation.GlobalScaleFloor,
            AvailableFloors(quotation, products));
```

y el helper, junto a los otros privados:

```csharp
    /// <summary>
    /// Los pisos que el select del descuento global puede ofrecer. Sale de los productos que el
    /// composer ya cargó para poner nombre y escalas a cada línea, así que no cuesta una
    /// consulta más.
    ///
    /// No se ofrece un piso que ningún producto de esta cotización tiene: elegirlo no
    /// descontaría nada y no habría forma de explicar por qué.
    /// </summary>
    private static IReadOnlyCollection<int> AvailableFloors(
        QuotationDto quotation,
        IReadOnlyDictionary<Guid, QuotationProductRef> products) =>
        quotation.Items
            .Select(item => item.ProductId)
            .Distinct()
            .SelectMany(productId => products.TryGetValue(productId, out var product)
                ? product.Scales.Select(scale => scale.FromUnit)
                : Enumerable.Empty<int>())
            .Distinct()
            .OrderBy(floor => floor)
            .ToArray();
```

En `ToItemResponse`, agregar `item.DiscountOrigin` como último argumento.

- [ ] **Step 5: Compilar la solución entera**

```powershell
dotnet build
```

Esperado: build exitoso. Acá aparecen los llamadores de `QuotationItemResponse` y `QuotationResponse` que hayan quedado sin el argumento nuevo — el PDF y el export son candidatos. Arreglarlos pasando el valor real, nunca un literal.

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Quotations/Modules.Quotations.Application
git commit -m "feat(quotations): exponer el piso global y el origen del descuento en la respuesta"
```

---

### Task 5: Los dos `PUT`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/SetQuotationGlobalScale.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/SetOrderGlobalScale.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro de los dos handlers y sus validadores)

**Interfaces:**
- Consumes: `Quotation.SetGlobalScaleFloor` / `SetGlobalScaleFloorAfterConversion` (Tarea 2), `QuotationPricingRecalculation.ApplyAsync`, `IQuotationResponseComposer`.
- Produces: `SetQuotationGlobalScaleCommand(Guid TenantId, Guid QuotationId, int? Floor)`, `SetOrderGlobalScaleCommand(Guid TenantId, Guid OrderId, int? Floor)`, `SetGlobalScaleRequest(int? Floor)`.

- [ ] **Step 1: Escribir el handler de cotización**

Copiar la estructura de `UpdateQuotationItemHandler` —autorizar, validar, cargar, mutar, recalcular, auditar, guardar, componer— y no inventar una propia. El esqueleto:

```csharp
public sealed record SetQuotationGlobalScaleCommand(
    Guid TenantId, Guid QuotationId, int? Floor) : ICommand<QuotationDto>;

public sealed class SetQuotationGlobalScaleValidator
    : AbstractValidator<SetQuotationGlobalScaleCommand>
{
    public SetQuotationGlobalScaleValidator()
    {
        // Mismo piso que PriceScaleRequestRules.FromUnit en Catalog: una escala nunca arranca
        // por debajo de 1, así que un floor de 0 o negativo no puede coincidir con ninguna.
        RuleFor(command => command.Floor)
            .GreaterThanOrEqualTo(1)
            .When(command => command.Floor.HasValue);
    }
}
```

El handler, después de cargar la cotización y antes de mutar, comprueba que el piso exista:

```csharp
        // El piso llega de una lista que el propio backend acaba de dar en
        // AvailableGlobalScaleFloors, así que uno que no está es un bug del cliente y se dice
        // como tal. Código de dominio: sin mapa `errors`, porque no hay un campo del formulario
        // al que apuntar.
        if (command.Floor is { } floor)
        {
            var products = await pricingLookup.FindManyAsync(
                command.TenantId,
                quotation.Items.Select(item => item.ProductId).Distinct().ToArray(),
                cancellationToken);

            var exists = products.Values
                .SelectMany(product => product.Scales)
                .Any(scale => scale.FromUnit == floor);

            if (!exists)
            {
                throw new QuotationsDomainException(
                    "quotation.global_scale.floor_not_available",
                    "No product in this quotation has a price scale starting at that unit.");
            }
        }

        quotation.SetGlobalScaleFloor(command.Floor, updatedBy, now);

        await QuotationPricingRecalculation.ApplyAsync(
            pricingLookup, command.TenantId, quotation, now, cancellationToken);
```

Después del recálculo, antes de guardar:

```csharp
        quotationRepository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Edited,
            updatedBy,
            QuotationChangeSummary.GlobalScaleFloorChanged(command.Floor),
            now));

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.global_scale_changed",
            quotation.Id.ToString(),
            "success",
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
```

La firma exacta de `QuotationHistoryEntry.Create` y de `AddHistoryEntry` se copia de `ConvertQuotationToOrderHandler`, que las usa igual.

Agregar el resumen a `QuotationChangeSummary`, junto a los otros:

```csharp
    public static string GlobalScaleFloorChanged(int? floor) => floor is { } value
        ? $"Aplicó el descuento global de la escala desde {value} unidades."
        : "Quitó el descuento global de escala.";
```

Sin el valor anterior: los otros resúmenes que sí lo llevan —`ClientChanged`, `ItemQuantityChanged`— lo muestran porque el cambio es entre dos cosas que la persona reconoce. Un piso de escala anterior no le dice nada a quien lee el historial, y la entrada previa ya está ahí arriba.

- [ ] **Step 2: Escribir el handler de pedido**

Idéntico salvo tres cosas, copiadas de `AddOrderItemsHandler`:

- entra por `orderRepository.FindByIdAsync` y de ahí a la cotización
- exige `order.Status == OrderStatus.Pending`, o `order.order.not_pending`
- llama `SetGlobalScaleFloorAfterConversion` y autoriza con `OrdersPermissions.OrderManage`

- [ ] **Step 3: Registrar las rutas**

En `QuotationEndpoints.cs`, junto a las otras:

```csharp
        // El descuento global por escala: el asesor elige un piso y todas las líneas resuelven
        // contra el tramo de su producto que arranca ahí. PUT y no PATCH — Vercel no soporta
        // PATCH en el rewrite.
        group.MapPut("/{quotationId:guid}/global-scale", SetQuotationGlobalScaleAsync)
            .RequireAuthorization(QuotationsPermissions.QuotationManage)
            .Accepts<SetGlobalScaleRequest>("application/json")
            .Produces<QuotationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

```csharp
    private static async Task<IResult> SetQuotationGlobalScaleAsync(
        Guid tenantId,
        Guid quotationId,
        SetGlobalScaleRequest request,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken)
    {
        var quotation = await dispatcher.SendAsync(
            new SetQuotationGlobalScaleCommand(tenantId, quotationId, request.Floor),
            cancellationToken);

        return Results.Ok(await composer.ComposeAsync(tenantId, quotation, cancellationToken));
    }
```

En `OrderEndpoints.cs`, la hermana sobre `/{orderId:guid}/global-scale`, con `OrdersPermissions.OrderManage` y devolviendo lo mismo que devuelven `SaveOrderEditsAsync` y `AddOrderItemsAsync` — copiar de ahí la forma exacta de la respuesta, que compone la cotización con el mismo composer.

`SetGlobalScaleRequest` va donde viven los otros `*Request` de estos endpoints: buscarlo con `grep -n "record AddQuotationItemRequest" -r src/` y ponerlo al lado.

- [ ] **Step 4: Registrar handlers y validadores en el contenedor**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, junto a los registros de los otros handlers de Quotations. **No se agrega ninguna política de autorización**: las dos rutas usan permisos que ya tienen la suya.

- [ ] **Step 5: Compilar**

```powershell
dotnet build
```

Esperado: build exitoso.

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Quotations src/Bootstrapper/QepServiceCollectionExtensions.cs
git commit -m "feat(quotations): PUT del descuento global por escala en cotizacion y pedido"
```

---

### Task 6: Integración

**Files:**
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/GlobalScaleDiscountTests.cs`

**Interfaces:**
- Consumes: el harness de integración de Quotations que ya existe. **Siembra por la API**, igual que el de Reporting: lo que haga falta agregar se agrega ahí, o la prueba falla en capas que se tapan entre sí.

- [ ] **Step 1: Leer el harness antes de escribir nada**

Abrir el harness de `Modules.Quotations.IntegrationTests` y copiar su forma de crear tenant, producto con escalas, cliente y cotización. No inventar factorías nuevas.

Dos cosas que muerden y no se parecen a su causa:

- El stub de desarrollo concede **sólo los permisos de tenancy**. Estas pruebas tienen que pedir `quotations.quotation.manage` y `quotations.order.manage` por `X-Permissions`, o el 403 va a venir del permiso faltante.
- La factoría fija sus propias claves de configuración y nunca las hereda de user-secrets.

- [ ] **Step 2: Escribir las pruebas que fallan**

Ocho casos, todos por HTTP:

1. `PUT /global-scale` con `{ "floor": 1000 }` sobre una cotización con una línea de 3 unidades de un producto con tramo 1000-5000 al 12%: responde 200, la línea viene con `discountPercentage: 12` y `discountOrigin: "GlobalFloor"`.
2. La respuesta trae `globalScaleFloor: 1000` y `availableGlobalScaleFloors` con los pisos del producto, ordenados y sin repetir.
3. `{ "floor": null }` lo quita y la línea vuelve a `0` con `discountOrigin: "Own"`.
4. `{ "floor": 7777 }` → 422 `quotation.global_scale.floor_not_available`.
5. `{ "floor": 0 }` → 422 `validation.failed`, con el mapa `errors` (Review Focus 4).
6. El piso sobrevive la conversión a pedido: convertir y leer el pedido, `globalScaleFloor` sigue en 1000.
7. `PUT /orders/{id}/global-scale` con el pedido en `Pending` lo cambia; en cualquier otro estado da 422 `order.order.not_pending`.
8. `PUT /quotations/{id}/global-scale` sobre una cotización `Converted` da 422 con el código de `EnsureEditable`.

- [ ] **Step 3: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~GlobalScaleDiscountTests"
```

Esperado: FAIL. Anotar el nombre de cada falla — la comparación posterior se hace por nombre, no por conteo.

- [ ] **Step 4: Arreglar lo que las pruebas encuentren**

Las tareas 1 a 5 deberían dejarlas en verde sin tocar producción. Si no, el arreglo va acá y se explica en el commit.

- [ ] **Step 5: Los pisos disponibles**

Tres casos más, sobre la misma respuesta:

1. Una cotización con dos productos, uno con pisos `[100, 1000]` y otro con `[1000, 5000]`, responde `availableGlobalScaleFloors: [100, 1000, 5000]` — unión, ordenada, sin duplicados.
2. Una cotización sin líneas responde `availableGlobalScaleFloors: []`: no null y no ausente.
3. El mismo arreglo llega en la respuesta del `PUT` y del `GET`, no sólo del `GET`: el frontend cachea lo que devuelve cada mutación.

- [ ] **Step 6: La compuerta de compra mínima (Review Focus 5)**

```
Una cotización con el piso global puesto que no llega al mínimo de compra: todas las líneas
vuelven a `discountPercentage: 0` **y** a `discountOrigin: "Own"`. Sin esto la pantalla dice
"descuento global aplicado" sobre un cero.
```

- [ ] **Step 7: El piso no se limpia solo (Review Focus 6)**

```
Con el piso en 1000, agregar una línea de un producto que no tiene ese tramo:
  - la respuesta sigue trayendo `globalScaleFloor: 1000`
  - la línea nueva viene con `discountOrigin: "Own"` y el descuento que le toque por cantidad
  - las líneas viejas conservan su `"GlobalFloor"`

Es decisión del spec §5: limpiarlo sería borrar una decisión del asesor por un efecto
colateral de otra acción. Esta prueba existe para que nadie lo "arregle".
```

- [ ] **Step 8: Correr la suite completa de Quotations**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests
```

Esperado: PASS. Comparar cualquier falla contra la lista de nombres del paso 3.

- [ ] **Step 9: Correr la solución entera**

```powershell
dotnet test
```

Esperado: PASS. El cambio de firma de `ApplyGroupDiscounts` y el campo nuevo en `QuotationResponse` pueden tocar pruebas de otros módulos que armen cuerpos a mano — barrerlas, no sólo los harness.

- [ ] **Step 10: Commit**

```powershell
git add tests/Modules/Quotations/Modules.Quotations.IntegrationTests
git commit -m "test(quotations): cubrir el descuento global por escala de punta a punta"
```

---

## Qué queda para el frontend

`qep-frontend`, en su propio trabajo: leer `availableGlobalScaleFloors` de la respuesta que ya recibe, pintar el select, mandar el `PUT` y mostrar `discountOrigin` por línea. No hay endpoint nuevo que consultar para armar el select — los pisos vienen en la misma respuesta de siempre.
