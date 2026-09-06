# Agrupación de escalas de precio — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que la restricción `Multiple` de una escala de precio se pueda cumplir sumando las cantidades de varias líneas de la cotización, y que incumplirla deje de bloquear: la escala simplemente no aplica.

**Architecture:** Catalog gana un flag `AllowGrouping` por escala. En Quotations, `QuotationScaleRestrictionRule` deja de lanzar para `Multiple` y devuelve un resultado; un servicio nuevo, `QuotationScaleGroupPricing`, recibe **todas** las líneas resultantes de cada mutación, agrupa las que comparten escala agrupable, y devuelve el descuento efectivo de cada una. El agregado lo aplica con `ApplyItemDiscounts`. `PackagingUnit` no cambia.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit v3, FluentValidation, Testcontainers para integración.

**Spec:** `docs/superpowers/specs/2026-09-06-agrupacion-escalas-precio-design.md` — leerla antes de la primera tarea. Este plan argumenta desde ella.

## Checkpoint — 2026-09-06

**Rama:** `feature/agrupacion-escala-precio`. Comprobar con `git branch --show-current` antes de
commitear: el snapshot del arranque de sesión miente, dijo `main` estando acá.

**Hecho — Tasks 1 a 4.** Sus pasos quedan marcados abajo. Commits, del más viejo al más nuevo:

| Commit | Qué |
| --- | --- |
| `3ff40c3` | La spec |
| `0deabaa` | Este plan |
| `bfe5832` + `77c5b5d` | Task 1 — `AllowGrouping` en `PriceScale`, con su rechazo en `PackagingUnit` |
| `b4b9062` + `b2641cd` | Task 2 — columna, migración `20260906194946_AddPriceScaleAllowGrouping` y contrato HTTP |
| `d99c0c1` | Task 3 — `Multiple` cuenta crudo, `Evaluate` reemplaza al `EnsureSatisfied` que lanzaba |
| `9057637` | Task 4 — `QuotationScaleGroupPricing`, el agrupador |
| `608d28b` | Merge de `origin`, que trajo `develop`: moneda, cuenta de cobro, aprobación de venta e historial |

**Task 4 quedó ajustada por D8**, agregada a la spec después de implementarla: una línea que
cumple el múltiplo sola conserva su escala aunque el total del grupo falle. Cambió
`ToPricing` y se reescribieron dos de sus pruebas (`GroupedLinesSatisfyTheMultipleTogether`
afirma ahora línea por línea; `GroupedLinesThatMissTheMultipleLoseTheScale` pasó a 10 + 13 = 23,
donde ninguna de las dos cumple sola).

**Siguiente paso: Task 5** — `Quotation.ApplyItemDiscounts`, el método del agregado que aplica
los descuentos que devuelve el agrupador y recalcula los totales. Después van Task 6 (cablear
agregar, editar y quitar) y Task 7 (el estado de la restricción en la respuesta).

**Ojo al empezar Task 5:** el merge de `develop` cambió `Quotation` a fondo —moneda
(`QuotationCurrency`), cuenta de cobro (`QuotationBillingAccount`), historial con
`QuotationChangeSummary`— y `QuotationProductPricingResolver.ResolveAsync` ahora recibe la
moneda y devuelve un objeto con `.Pricing` y `.Name`. El plan se escribió contra el código
anterior: hay que releer el agregado antes de seguirlo al pie de la letra.

**Verificación al cerrar el checkpoint.** Compila sin errores. Unitarias verdes después del
merge y de D8: `Modules.Quotations.UnitTests` 102/102 y `Modules.Catalog.UnitTests` 94/94.
**Las de integración no se corrieron** — piden Docker por Testcontainers. Task 3 tocó
`QuotationItemApiTests` y el merge de `develop` tocó varios harness más, así que toda la
integración de Quotations está sin verificar y hay que correrla antes de seguir.

**Ojo con el entorno:** este tramo se trabajó en macOS con zsh, no en el Windows del developer.
Los comandos que queden en el plan siguen siendo PowerShell, que es lo que el `CLAUDE.md` exige
para todo lo que se le entregue a él.

**Las ambigüedades del requisito ya están resueltas en la spec**, no se vuelven a discutir: D1
(incumplir no bloquea, la escala simplemente no aplica), D2 (agrupan escalas idénticas aunque
sean de productos distintos), D4 (múltiplo sobre la cantidad cruda), D5 (`PackagingUnit`
intacto, incluido su 422) y D8 (la agrupación rescata a las que no cumplen, nunca hunde a las
que sí).

---

## Global Constraints

- **TDD obligatorio.** RED antes que GREEN, con evidencia literal de la salida de ambos. Sin excepción.
- **Commits:** conventional commits, en español, **sin atribución de IA** (regla dura del `CLAUDE.md`).
- **Rama:** `feature/agrupacion-escala-precio`, ya creada desde `develop`. Comprobar con `git branch --show-current` antes de cada commit — nunca commitear en `main`.
- **Entorno del developer: Windows + PowerShell.** Todo comando que se le entregue va en PowerShell: `A; if ($?) { B }`, nunca `&&`.
- **`Api.exe` corriendo bloquea `dotnet build`, `dotnet test` y los comandos `ef`.** Detener el proceso antes.
- **Capas:** `Modules.Quotations.Application` **no** puede referenciar Catalog. `ArchitectureTests` lo verifica. Todo lo que cruce va por el puerto y su adaptador en `Bootstrapper`.
- **Migraciones con el factory de diseño**, nunca con `--startup-project`:
  `dotnet ef migrations add <Nombre> --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext -o Persistence/Migrations`
- **No tocar `Directory.Packages.props`.** Este plan no agrega paquetes; si algo lo pidiera, hay que regenerar los 74 lock files con `dotnet restore --force-evaluate` y commitearlos junto.
- **Código de error nuevo, único:** `catalog.product.price_scale.grouping_not_allowed`.
- **Códigos que se retiran del camino de 422:** `quotation.item.quantity_not_multiple` deja de ser un status HTTP y pasa a ser un motivo informativo en la respuesta. `quotation.item.quantity_not_packaging_unit` **sigue siendo 422**, sin cambios.

---

### Task 1: Catalog — `AllowGrouping` en el dominio de la escala

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Domain/ProductPricing.cs:23-31` (`PriceScaleInput`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Domain/PriceScale.cs`
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/ProductTests.cs`

**Interfaces:**
- Consumes: nada (primera tarea).
- Produces:
  - `PriceScale.AllowGrouping` → `bool` (get; private set;)
  - `PriceScaleInput(int FromUnit, int ToUnit, decimal Discount, PriceScaleRestriction? Restriction, int? Multiple, int? PackagingUnit, decimal? FinalUsd, decimal? FinalCop, bool AllowGrouping = false)` — el parámetro nuevo va **último y con default**, para que las decenas de construcciones posicionales existentes sigan compilando.
  - Código de error `catalog.product.price_scale.grouping_not_allowed`.

- [x] **Step 1: Escribir las dos pruebas que fallan**

En `ProductTests.cs`, junto a `CreateRejectsAMultipleRestrictionWithAPackagingUnit`:

```csharp
    // La agrupación es exclusiva de la restricción Multiple: un empaque no se parte entre
    // productos distintos, así que sumar cajas de A con cajas de B no significa nada.
    [Fact]
    public void CreateRejectsGroupingOnAPackagingUnitRestriction()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    BaseUsd = 10m,
                    Scales =
                    [
                        new PriceScaleInput(
                            1, 9, 0m, PriceScaleRestriction.PackagingUnit, null, 12, 10m, null,
                            AllowGrouping: true)
                    ]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.grouping_not_allowed", error.Code);
    }

    [Fact]
    public void CreateKeepsGroupingOnAMultipleRestriction()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing
            {
                BaseUsd = 10m,
                Scales =
                [
                    new PriceScaleInput(
                        5, 48, 0m, PriceScaleRestriction.Multiple, 3, null, 10m, null,
                        AllowGrouping: true)
                ]
            },
            Now);

        Assert.True(Assert.Single(product.PriceScales).AllowGrouping);
    }

    // Sin el flag explícito, una escala no agrupa: es el comportamiento de todas las que ya
    // están guardadas.
    [Fact]
    public void CreateDefaultsGroupingToDisabled()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing
            {
                BaseUsd = 10m,
                Scales = [new PriceScaleInput(5, 48, 0m, PriceScaleRestriction.Multiple, 3, null, 10m, null)]
            },
            Now);

        Assert.False(Assert.Single(product.PriceScales).AllowGrouping);
    }
```

- [x] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~ProductTests"
```

Esperado: no compila — `PriceScaleInput` no tiene `AllowGrouping` y `PriceScale` no tiene la propiedad. **Un fallo de compilación es un RED válido acá**: es la ausencia del miembro, que es exactamente lo que la prueba afirma. Pegar la salida.

- [x] **Step 3: Agregar el campo al input**

En `ProductPricing.cs`, `PriceScaleInput` pasa a:

```csharp
/// <param name="AllowGrouping">Si las cantidades de varias líneas de una cotización que caen en
/// esta misma escala se suman para validar el múltiplo. Exclusivo de
/// <see cref="PriceScaleRestriction.Multiple"/>. Último y con default a propósito: las escalas
/// que ya existen no agrupan, y las construcciones posicionales existentes no se tocan.</param>
public sealed record PriceScaleInput(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    PriceScaleRestriction? Restriction,
    int? Multiple,
    int? PackagingUnit,
    decimal? FinalUsd,
    decimal? FinalCop,
    bool AllowGrouping = false);
```

- [x] **Step 4: Agregar la propiedad y la validación en `PriceScale`**

Agregar el parámetro `bool allowGrouping` al constructor privado (último), asignarlo, y declarar:

```csharp
    /// <summary>Si esta escala permite que las cantidades de varias líneas de una cotización
    /// se sumen para validar el múltiplo. Siempre <c>false</c> cuando
    /// <see cref="Restriction"/> es <c>PackagingUnit</c> — lo hace cumplir
    /// <see cref="Create"/>.</summary>
    public bool AllowGrouping { get; private set; }
```

En `Create`, dentro de la rama `Multiple`, nada que agregar. En la rama `else` (la de `PackagingUnit`), antes de asignar `packagingUnit`:

```csharp
            if (input.AllowGrouping)
            {
                throw new CatalogDomainException(
                    "catalog.product.price_scale.grouping_not_allowed",
                    "Grouping is only available when the restriction is 'multiple'.");
            }
```

Y pasar `input.AllowGrouping` como último argumento del `new PriceScale(...)` final.

- [x] **Step 5: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~ProductTests"
```

Esperado: PASS, incluidas las pruebas de escalas que ya existían. Pegar la salida.

- [x] **Step 6: Commit**

```powershell
git branch --show-current
git add src/Modules/Catalog/Modules.Catalog.Domain/PriceScale.cs src/Modules/Catalog/Modules.Catalog.Domain/ProductPricing.cs tests/Modules/Catalog/Modules.Catalog.UnitTests/ProductTests.cs
git commit -m "feat(catalog): permitir marcar una escala de multiplo como agrupable"
```

---

### Task 2: Catalog — persistencia y contrato HTTP

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/CatalogDbContext.cs:140-175` (`ConfigurePriceScale`)
- Create: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/Migrations/<timestamp>_AddPriceScaleAllowGrouping.cs` (generada)
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/CatalogDtos.cs:52-70` (`PriceScaleRequest`, `PriceScaleResponse`)
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/ProductPricingMapping.cs:21-28`
- Modify: `src/Modules/Catalog/Modules.Catalog.Application/ProductMapping.cs:30-39`
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductPricingApiTests.cs`

**Interfaces:**
- Consumes: `PriceScale.AllowGrouping`, `PriceScaleInput.AllowGrouping` (Task 1).
- Produces:
  - Columna `catalog.product_price_scales.allow_grouping`, `boolean NOT NULL DEFAULT false`.
  - `PriceScaleRequest(..., decimal? FinalCop, bool? AllowGrouping)` — nullable y último: los cuerpos crudos de las pruebas que ya existen no lo mandan.
  - `PriceScaleResponse(..., decimal? FinalCop, bool AllowGrouping)`.

**Nota:** `Seed/Data/catalog-products.json` **no se toca**. Verificado: `CatalogSeeder.cs:66` construye `ProductPricing` sólo con `BaseUsd`/`BaseCop` y no crea escalas.

- [x] **Step 1: Escribir la prueba de integración que falla**

En `ProductPricingApiTests.cs`:

```csharp
    // El flag viaja de ida y de vuelta: sin el de vuelta, la pantalla de producto no puede
    // dibujar el switch en el estado en que quedó guardado.
    [Fact]
    public async Task CreateProductRoundTripsTheGroupingFlagOnAMultipleScale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name = "Vela de soja",
                code = "VS-AGR-001",
                pricing = new
                {
                    baseCop = 100_000m,
                    scales = new[]
                    {
                        new
                        {
                            fromUnit = 5, toUnit = 48, discount = 5m,
                            restriction = "multiple", multiple = 3,
                            finalCop = 95_000m, allowGrouping = true
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProductResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.True(Assert.Single(body.PriceScales).AllowGrouping);
    }

    [Fact]
    public async Task CreateProductRejectsGroupingOnAPackagingUnitScale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name = "Vela de soja",
                code = "VS-AGR-002",
                pricing = new
                {
                    baseCop = 100_000m,
                    scales = new[]
                    {
                        new
                        {
                            fromUnit = 1, toUnit = 999, discount = 0m,
                            restriction = "packaging_unit", packagingUnit = 12,
                            finalCop = 100_000m, allowGrouping = true
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
```

Si el `ProductResponseDto` local del archivo de pruebas no expone `PriceScales` con `AllowGrouping`, agregarle el campo a ese record de prueba — es un DTO de lectura propio del archivo, no el de producción.

- [x] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductPricingApiTests"
```

Esperado: FAIL. La primera por `allowGrouping` ausente en la respuesta; la segunda con `Created` en vez de `UnprocessableEntity`. Pegar la salida.

- [x] **Step 3: Contrato HTTP y mapeos**

`CatalogDtos.cs` — agregar a `PriceScaleRequest` como último parámetro:

```csharp
    /// <summary>Nullable y último: los cuerpos que no lo mandan mantienen el comportamiento de
    /// siempre, que es no agrupar.</summary>
    bool? AllowGrouping);
```

Y a `PriceScaleResponse` como último parámetro: `bool AllowGrouping);`

`ProductPricingMapping.cs`, en `ToDomain(PriceScaleRequest)`, agregar como último argumento:

```csharp
        request.AllowGrouping ?? false);
```

`ProductMapping.cs`, en `ToResponse(PriceScale)`, agregar como último argumento:

```csharp
        scale.AllowGrouping);
```

- [x] **Step 4: Persistencia**

En `CatalogDbContext.ConfigurePriceScale`, junto a `PackagingUnit`:

```csharp
        scale.Property(value => value.AllowGrouping).HasColumnName("allow_grouping");
```

- [x] **Step 5: Generar la migración**

Detener `Api.exe` si está corriendo, y:

```powershell
dotnet ef migrations add AddPriceScaleAllowGrouping --project src/Modules/Catalog/Modules.Catalog.Infrastructure --context CatalogDbContext -o Persistence/Migrations
```

Abrir el archivo generado y **verificar que la columna lleva default**: EF genera `nullable: false` con `defaultValue: false`, que es lo que hace falta para las filas existentes. Si no lo puso, agregarlo a mano en el `AddColumn<bool>`.

- [x] **Step 6: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductPricingApiTests"
```

Esperado: PASS, y **todas** las demás pruebas del archivo también — es el barrido de cuerpos crudos que exige el `CLAUDE.md`. Pegar la salida completa del archivo, no sólo la de las dos nuevas.

- [x] **Step 7: Commit**

```powershell
git branch --show-current
git add src/Modules/Catalog tests/Modules/Catalog
git commit -m "feat(catalog): persistir y exponer el flag de agrupacion de la escala"
```

---

### Task 3: Quotations — `Multiple` cuenta crudo y deja de lanzar

Esta tarea trae el flag al puerto y cambia la semántica de la regla para **una** línea. La agrupación es la tarea siguiente.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationProductPricingLookup.cs` (`QuotationPriceScaleRef`)
- Modify: `src/Bootstrapper/QuotationPriceScaleMapping.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationScaleRestrictionRule.cs` (reescritura)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationProductPricingResolver.cs:47-54`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationScaleRestrictionRuleTests.cs` (reescritura de aserciones)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationItemApiTests.cs:236-345`

**Interfaces:**
- Consumes: `PriceScale.AllowGrouping` (Task 1), vía el adaptador.
- Produces:
  - `QuotationPriceScaleRef(int FromUnit, int ToUnit, decimal Discount, QuotationPriceScaleRestriction Restriction, int? Multiple, int? PackagingUnit, bool AllowGrouping = false)` — el parámetro nuevo va último y con default, para no tocar `QuotationDiscountResolverTests`.
  - `public sealed record QuotationScaleRestrictionResult(bool IsSatisfied, string? Code, decimal EvaluatedQuantity, decimal Shortfall)`
  - `QuotationScaleRestrictionRule.Evaluate(QuotationPriceScaleRef scale, decimal quantity)` → `QuotationScaleRestrictionResult`. **Nunca lanza.**
  - `QuotationScaleRestrictionRule.EnsurePackagingUnit(QuotationPriceScaleRef scale, decimal quantity)` → `void`. Lanza `QuotationsDomainException` con `quotation.item.quantity_not_packaging_unit`.

- [x] **Step 1: Reescribir las pruebas unitarias de la regla**

Reemplazar el cuerpo de `QuotationScaleRestrictionRuleTests.cs` por:

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationScaleRestrictionRuleTests
{
    private static QuotationPriceScaleRef MultipleOf(int multiple, int fromUnit = 5) =>
        new(fromUnit, 48, 5m, QuotationPriceScaleRestriction.Multiple, multiple, null);

    private static QuotationPriceScaleRef PackagesOf(int packagingUnit) =>
        new(1, 999, 5m, QuotationPriceScaleRestriction.PackagingUnit, null, packagingUnit);

    // El multiplo se cuenta sobre la cantidad cruda, no desde FromUnit. Revierte el criterio de
    // 5a76b07: en una escala 5-48 de a 3, 8 unidades ya no cumple (8 - 5 = 3 daba valido).
    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(48)]
    public void MultipleAcceptsRawMultiples(decimal quantity)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(MultipleOf(3), quantity);

        Assert.True(result.IsSatisfied);
        Assert.Null(result.Code);
        Assert.Equal(0m, result.Shortfall);
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(7, 2)]
    [InlineData(8, 1)]
    public void MultipleReportsHowManyUnitsAreMissing(decimal quantity, decimal shortfall)
    {
        var result = QuotationScaleRestrictionRule.Evaluate(MultipleOf(3), quantity);

        Assert.False(result.IsSatisfied);
        Assert.Equal("quotation.item.quantity_not_multiple", result.Code);
        Assert.Equal(quantity, result.EvaluatedQuantity);
        Assert.Equal(shortfall, result.Shortfall);
    }

    // Evaluate nunca lanza: incumplir el multiplo deja la linea sin descuento, no la bloquea.
    [Fact]
    public void MultipleNeverThrows()
    {
        var result = QuotationScaleRestrictionRule.Evaluate(MultipleOf(3), 7m);

        Assert.False(result.IsSatisfied);
    }

    // Un multiplo que desmiente la invariante de Catalog no puede bloquear una linea con un
    // dato que nadie corrige desde la cotizacion, ni dividir por cero.
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void MultipleIgnoresANonPositiveStep(int multiple)
    {
        Assert.True(QuotationScaleRestrictionRule.Evaluate(MultipleOf(multiple), 7m).IsSatisfied);
    }

    // La unidad de empaque se cuenta sobre la cantidad cruda, igual que antes: sin cambios.
    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(120)]
    public void PackagingUnitAcceptsWholePackages(decimal quantity)
    {
        Assert.True(QuotationScaleRestrictionRule.Evaluate(PackagesOf(12), quantity).IsSatisfied);
        QuotationScaleRestrictionRule.EnsurePackagingUnit(PackagesOf(12), quantity);
    }

    // Y sigue siendo un 422: su comportamiento no lo toca esta funcionalidad.
    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    public void PackagingUnitStillThrows(decimal quantity)
    {
        var exception = Assert.Throws<QuotationsDomainException>(
            () => QuotationScaleRestrictionRule.EnsurePackagingUnit(PackagesOf(12), quantity));

        Assert.Equal("quotation.item.quantity_not_packaging_unit", exception.Code);
    }
}
```

- [x] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationScaleRestrictionRuleTests"
```

Esperado: no compila — no existen `Evaluate` ni `QuotationScaleRestrictionResult`. Pegar la salida.

- [x] **Step 3: Llevar el flag al puerto y al adaptador**

`IQuotationProductPricingLookup.cs` — `QuotationPriceScaleRef` pasa a:

```csharp
/// <param name="AllowGrouping">Si las cantidades de varias líneas que caen en esta misma escala
/// se suman para validar el múltiplo. Siempre <c>false</c> con <c>PackagingUnit</c>: lo hace
/// cumplir Catalog. Último y con default para no tocar las construcciones que ya existen.</param>
public sealed record QuotationPriceScaleRef(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    QuotationPriceScaleRestriction Restriction,
    int? Multiple,
    int? PackagingUnit,
    bool AllowGrouping = false);
```

`QuotationPriceScaleMapping.cs` — agregar `scale.AllowGrouping` como último argumento del `new(...)`.

- [x] **Step 4: Reescribir la regla**

Reemplazar `QuotationScaleRestrictionRule.cs` por:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <param name="EvaluatedQuantity">La cantidad contra la que se evaluó: la de la línea, o la
/// suma del grupo cuando la escala agrupa. Viaja a la respuesta porque un total que la pantalla
/// no puede reconstruir sola es lo único que explica un precio sin descuento.</param>
/// <param name="Shortfall">Cuántas unidades faltan para el siguiente múltiplo. 0 cuando cumple.</param>
public sealed record QuotationScaleRestrictionResult(
    bool IsSatisfied,
    string? Code,
    decimal EvaluatedQuantity,
    decimal Shortfall)
{
    public static QuotationScaleRestrictionResult Satisfied(decimal quantity) =>
        new(true, null, quantity, 0m);
}

/// <summary>
/// Decide si la escala que cubre una cantidad aplica sobre ella (CAT-09 + US-4).
///
/// **Dos modelos de falla, a propósito.** <c>Multiple</c> no bloquea: si no se cumple, la escala
/// no aplica y la línea va con descuento 0 y precio base — lo mismo que ya le pasa a una
/// cantidad que no cae en ninguna escala. Es lo único que hace construible un grupo de a poco:
/// con 422 por línea, un total válido como 10+8+12 no tiene ningún camino de estados
/// intermedios que lo alcance. <c>PackagingUnit</c>, en cambio, conserva intacto su 422 — el
/// requisito exige compatibilidad total con su comportamiento actual.
///
/// **El múltiplo se cuenta sobre la cantidad cruda**, no desde <c>FromUnit</c>. Revierte el
/// criterio de <c>5a76b07</c>, que lo heredaba del CRM: en una escala 5-48 de a 3, 8 unidades
/// era válida (8 − 5 = 3) y ya no lo es. Fue decisión explícita del developer el 2026-09-06.
/// </summary>
internal static class QuotationScaleRestrictionRule
{
    public static QuotationScaleRestrictionResult Evaluate(
        QuotationPriceScaleRef scale, decimal quantity) =>
        scale.Restriction switch
        {
            QuotationPriceScaleRestriction.Multiple => EvaluateStep(
                scale.Multiple, quantity, "quotation.item.quantity_not_multiple"),
            QuotationPriceScaleRestriction.PackagingUnit => EvaluateStep(
                scale.PackagingUnit, quantity, "quotation.item.quantity_not_packaging_unit"),
            _ => QuotationScaleRestrictionResult.Satisfied(quantity)
        };

    /// <summary>
    /// El 422 de la unidad de empaque, sobre la línea que el comando toca. No lo llama el
    /// recalculador: si una línea vieja incumpliera el empaque —sólo posible si la escala cambió
    /// en el catálogo después de agregarla—, lanzar desde ahí haría que quitar una línea sana
    /// fallara con el error de otra, y ese error no lo puede corregir nadie desde la cotización.
    /// </summary>
    public static void EnsurePackagingUnit(QuotationPriceScaleRef scale, decimal quantity)
    {
        if (scale.Restriction != QuotationPriceScaleRestriction.PackagingUnit)
        {
            return;
        }

        var result = Evaluate(scale, quantity);
        if (result.IsSatisfied)
        {
            return;
        }

        throw new QuotationsDomainException(
            result.Code!,
            $"The quantity must be a whole number of packages of {scale.PackagingUnit} units " +
            $"while it falls in the {scale.FromUnit}-{scale.ToUnit} price scale.");
    }

    // Catalog exige un paso > 0 al crear la escala. Si una fila lo desmiente, la línea no se
    // castiga con un dato que nadie puede corregir desde la cotización — y sobre todo no se
    // divide por cero.
    private static QuotationScaleRestrictionResult EvaluateStep(
        int? step, decimal quantity, string code)
    {
        if (step is not { } value || value <= 0)
        {
            return QuotationScaleRestrictionResult.Satisfied(quantity);
        }

        var remainder = quantity % value;
        return remainder == 0
            ? QuotationScaleRestrictionResult.Satisfied(quantity)
            : new QuotationScaleRestrictionResult(false, code, quantity, value - remainder);
    }
}
```

- [x] **Step 5: Ajustar el resolver por línea**

En `QuotationProductPricingResolver.cs`, reemplazar el bloque que hoy llama a `EnsureSatisfied` y el `return`:

```csharp
        var scale = QuotationDiscountResolver.Resolve(product.Scales, quantity);

        // PackagingUnit conserva su 422, y sólo sobre la línea que el comando toca: es el
        // comportamiento que ya existía y que esta funcionalidad no debe alterar.
        if (scale is not null)
        {
            QuotationScaleRestrictionRule.EnsurePackagingUnit(scale, quantity);
        }

        // Multiple ya no bloquea: si no cumple, la escala no aplica. Todavía sin agrupar — eso
        // lo agrega QuotationScaleGroupPricing, que recalcula todas las líneas juntas.
        var discount = scale is not null
            && QuotationScaleRestrictionRule.Evaluate(scale, quantity).IsSatisfied
                ? scale.Discount
                : 0m;

        return (unitPrice, discount, product.TaxPercentage ?? 0);
```

- [x] **Step 6: Correr las unitarias y verificar que pasan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
```

Esperado: PASS, incluidas `QuotationDiscountResolverTests` sin tocarlas. Pegar la salida.

- [x] **Step 7: Reescribir las pruebas de integración que afirman el 422 de múltiplo**

En `QuotationItemApiTests.cs`:

- `AddItemWithAQuantityOffTheScaleMultipleIsUnprocessable` → renombrar a `AddItemOffTheScaleMultipleIsAcceptedWithoutDiscount`, cambiar el cuerpo para esperar `HttpStatusCode.Created` y afirmar `Assert.Equal(0m, Assert.Single(created.Items).DiscountPercentage);`. La cantidad 7 se mantiene.
- `AddItemOnTheScaleMultipleIsAccepted` → cambiar la cantidad de `8m` a `9m` (con conteo crudo, 8 ya no cumple) y agregar `Assert.Equal(5m, Assert.Single(created.Items).DiscountPercentage);`.
- La prueba de `UpdateQuotationItem` en la línea ~320 que espera 422 al pasar de 8 a 7 → esperar `HttpStatusCode.OK` y descuento 0. Su `AddQuotationItemRequest(productId, 8m)` inicial pasa a `9m`.
- La prueba de empaque de la línea ~358 (`quantity_not_packaging_unit`) → **no se toca**. Es la prueba que garantiza que el empaque sigue bloqueando.

- [x] **Step 8: Correr las de integración y verificar que pasan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationItemApiTests"
```

Esperado: PASS el archivo entero. Pegar la salida.

- [x] **Step 9: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations src/Bootstrapper tests/Modules/Quotations
git commit -m "feat(quotations): contar el multiplo sobre la cantidad cruda y dejar de bloquear"
```

---

### Task 4: `QuotationScaleGroupPricing` — el agrupador

Servicio puro, sin dependencias ni E/S. No se cablea todavía: se prueba solo.

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/QuotationScaleGroupPricing.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationScaleGroupPricingTests.cs`

**Interfaces:**
- Consumes: `QuotationPriceScaleRef.AllowGrouping`, `QuotationScaleRestrictionRule.Evaluate`, `QuotationScaleRestrictionResult` (Task 3); `QuotationDiscountResolver.Resolve` (ya existe).
- Produces:
  - `public sealed record QuotationPricingLine(Guid ItemId, Guid ProductId, decimal Quantity)`
  - `public sealed record QuotationLinePricing(Guid ItemId, decimal DiscountPercentage, QuotationPriceScaleRef? Scale, QuotationScaleRestrictionResult? Restriction, bool Grouped)`
  - `QuotationScaleGroupPricing.Resolve(IReadOnlyCollection<QuotationPricingLine> lines, IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> scalesByProduct)` → `IReadOnlyList<QuotationLinePricing>`

- [x] **Step 1: Escribir las pruebas que fallan**

Crear `QuotationScaleGroupPricingTests.cs`:

```csharp
using Modules.Quotations.Application;

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

    private static IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> Catalog(
        params (Guid ProductId, QuotationPriceScaleRef Scale)[] entries) =>
        entries.ToDictionary(
            entry => entry.ProductId,
            entry => (IReadOnlyCollection<QuotationPriceScaleRef>)[entry.Scale]);

    private static QuotationLinePricing For(IReadOnlyList<QuotationLinePricing> result, Guid itemId) =>
        result.Single(line => line.ItemId == itemId);

    // El caso del requisito: 10 + 8 + 12 = 30, multiplo de 3. Ninguna de las tres lo cumple
    // sola, y las tres reciben su descuento.
    [Fact]
    public void GroupedLinesSatisfyTheMultipleTogether()
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

        Assert.All(result, line => Assert.Equal(5m, line.DiscountPercentage));
        Assert.All(result, line => Assert.True(line.Grouped));
        Assert.Equal(30m, For(result, a).Restriction!.EvaluatedQuantity);
    }

    // 10 + 12 = 22: le faltan 2 unidades para 24. Ninguna de las dos recibe descuento, y las
    // dos reportan el mismo total y el mismo faltante.
    [Fact]
    public void GroupedLinesThatMissTheMultipleLoseTheScale()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 10m),
                new QuotationPricingLine(b, ProductB, 12m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true)),
                (ProductB, Scale(allowGrouping: true))));

        Assert.All(result, line => Assert.Equal(0m, line.DiscountPercentage));
        Assert.All(result, line => Assert.Equal(22m, line.Restriction!.EvaluatedQuantity));
        Assert.All(result, line => Assert.Equal(2m, line.Restriction!.Shortfall));
        Assert.All(
            result,
            line => Assert.Equal("quotation.item.quantity_not_multiple", line.Restriction!.Code));
    }

    // Sin el switch, cada linea valida su multiplo sola: 10 % 3 y 8 % 3 fallan las dos.
    [Fact]
    public void UngroupedLinesValidateOnTheirOwn()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 10m),
                new QuotationPricingLine(b, ProductB, 8m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: false)),
                (ProductB, Scale(allowGrouping: false))));

        Assert.All(result, line => Assert.Equal(0m, line.DiscountPercentage));
        Assert.All(result, line => Assert.False(line.Grouped));
        Assert.Equal(10m, For(result, a).Restriction!.EvaluatedQuantity);
        Assert.Equal(8m, For(result, b).Restriction!.EvaluatedQuantity);
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
                new QuotationPricingLine(a, ProductA, 9m),
                new QuotationPricingLine(b, ProductB, 8m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true)),
                (ProductB, Scale(allowGrouping: false))));

        Assert.Equal(9m, For(result, a).Restriction!.EvaluatedQuantity);
        Assert.Equal(5m, For(result, a).DiscountPercentage);
        Assert.Equal(8m, For(result, b).Restriction!.EvaluatedQuantity);
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

    // El descuento queda fuera de la clave del grupo: agrupan igual, y cada linea conserva el
    // de su propia escala.
    [Fact]
    public void GroupingIgnoresTheDiscountAndEachLineKeepsItsOwn()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = QuotationScaleGroupPricing.Resolve(
            [
                new QuotationPricingLine(a, ProductA, 10m),
                new QuotationPricingLine(b, ProductB, 8m)
            ],
            Catalog(
                (ProductA, Scale(allowGrouping: true, discount: 10m)),
                (ProductB, Scale(allowGrouping: true, discount: 15m))));

        Assert.Equal(10m, For(result, a).DiscountPercentage);
        Assert.Equal(15m, For(result, b).DiscountPercentage);
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

        Assert.All(result, line => Assert.False(line.Grouped));
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
}
```

- [x] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationScaleGroupPricingTests"
```

Esperado: no compila — `QuotationScaleGroupPricing` no existe. Pegar la salida.

- [x] **Step 3: Implementar el servicio**

Crear `QuotationScaleGroupPricing.cs`:

```csharp
namespace Modules.Quotations.Application;

/// <summary>Una línea tal como queda después de la mutación, con la cantidad que el recálculo
/// debe considerar.</summary>
public sealed record QuotationPricingLine(Guid ItemId, Guid ProductId, decimal Quantity);

/// <param name="Grouped">Si la cantidad evaluada fue la del grupo y no la de la línea. Viaja a
/// la respuesta: "te faltan 2 unidades" significa cosas distintas según de quién sean.</param>
public sealed record QuotationLinePricing(
    Guid ItemId,
    decimal DiscountPercentage,
    QuotationPriceScaleRef? Scale,
    QuotationScaleRestrictionResult? Restriction,
    bool Grouped);

/// <summary>
/// Resuelve el descuento de **todas** las líneas de una cotización a la vez, porque desde que
/// existe la agrupación el descuento de una línea depende de las otras.
///
/// El orden importa y es el del requisito: primero cada línea elige su escala por su **propia**
/// cantidad —la suma nunca decide en qué escala cae una línea, ni se compara contra
/// <c>ToUnit</c>—, y recién después las que comparten una escala agrupable suman sus cantidades
/// para validar el múltiplo.
///
/// La clave del grupo es <c>FromUnit</c> + <c>ToUnit</c> + <c>Multiple</c>. El descuento queda
/// **fuera**: es parámetro de cada línea, así que dos productos con la misma escala agrupan
/// aunque descuenten distinto, y cada uno conserva el suyo. La agrupación decide **si** la
/// escala aplica, nunca **cuál**.
///
/// Nunca lanza. El 422 de <c>PackagingUnit</c> vive en <c>QuotationProductPricingResolver</c>,
/// sobre la línea que el comando toca — ver <c>QuotationScaleRestrictionRule</c>.
/// </summary>
internal static class QuotationScaleGroupPricing
{
    public static IReadOnlyList<QuotationLinePricing> Resolve(
        IReadOnlyCollection<QuotationPricingLine> lines,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> scalesByProduct)
    {
        var resolved = lines
            .Select(line => (
                Line: line,
                Scale: QuotationDiscountResolver.Resolve(
                    scalesByProduct.TryGetValue(line.ProductId, out var scales) ? scales : [],
                    line.Quantity)))
            .ToArray();

        var groupTotals = resolved
            .Where(entry => IsGroupable(entry.Scale))
            .GroupBy(entry => GroupKey(entry.Scale!))
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Line.Quantity));

        return resolved.Select(entry => ToPricing(entry.Line, entry.Scale, groupTotals)).ToArray();
    }

    private static QuotationLinePricing ToPricing(
        QuotationPricingLine line,
        QuotationPriceScaleRef? scale,
        IReadOnlyDictionary<(int, int, int), decimal> groupTotals)
    {
        if (scale is null)
        {
            return new QuotationLinePricing(line.ItemId, 0m, null, null, false);
        }

        var grouped = IsGroupable(scale);
        var quantity = grouped ? groupTotals[GroupKey(scale)] : line.Quantity;
        var restriction = QuotationScaleRestrictionRule.Evaluate(scale, quantity);

        return new QuotationLinePricing(
            line.ItemId,
            restriction.IsSatisfied ? scale.Discount : 0m,
            scale,
            restriction,
            grouped);
    }

    // El paso > 0 es invariante de Catalog; exigirlo acá evita que una fila que la desmienta
    // arme un grupo que después nadie sabe contra qué comparar.
    private static bool IsGroupable(QuotationPriceScaleRef? scale) =>
        scale is
        {
            Restriction: QuotationPriceScaleRestriction.Multiple,
            AllowGrouping: true,
            Multiple: > 0
        };

    private static (int, int, int) GroupKey(QuotationPriceScaleRef scale) =>
        (scale.FromUnit, scale.ToUnit, scale.Multiple!.Value);
}
```

- [x] **Step 4: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
```

Esperado: PASS, las diez nuevas y todas las anteriores. Pegar la salida.

- [x] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations/Modules.Quotations.Application/QuotationScaleGroupPricing.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationScaleGroupPricingTests.cs
git commit -m "feat(quotations): agrupar cantidades de lineas que comparten una escala de multiplo"
```

---

### Task 5: El agregado aplica los descuentos recalculados

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationItem.cs:110-113` (junto a `UpdateQuantity`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs` (después de `RemoveItem`, ~línea 262)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTests.cs`

**Interfaces:**
- Consumes: nada de las tareas anteriores — el dominio no sabe de escalas.
- Produces:
  - `QuotationItem.UpdateDiscount(decimal discountPercentage, DateTimeOffset occurredAt)` → `void`, `internal`.
  - `Quotation.ApplyItemDiscounts(IReadOnlyDictionary<QuotationItemId, decimal> discountsByItem, MemberId updatedBy, DateTimeOffset occurredAt)` → `void`, `public`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `QuotationTests.cs`, siguiendo el estilo de las que ya arman una cotización con líneas:

```csharp
    // El descuento de una linea pasa a depender de las otras, asi que el agregado necesita una
    // forma de recibirlos todos juntos y recalcular totales una sola vez.
    [Fact]
    public void ApplyItemDiscountsRewritesTheDiscountsAndTheTotals()
    {
        var quotation = QuotationWithTwoItems(out var first, out var second);
        var before = quotation.Total;

        quotation.ApplyItemDiscounts(
            new Dictionary<QuotationItemId, decimal> { [first] = 10m, [second] = 0m },
            AdvisorId,
            Now);

        Assert.Equal(10m, quotation.Items.Single(item => item.Id == first).DiscountPercentage);
        Assert.Equal(0m, quotation.Items.Single(item => item.Id == second).DiscountPercentage);
        Assert.Equal(
            Math.Round(quotation.Items.Sum(item => item.DiscountAmount), 2),
            quotation.DiscountAmount);
        Assert.NotEqual(before, quotation.Total);
    }

    // La cantidad y el precio de la linea no los toca: solo el descuento.
    [Fact]
    public void ApplyItemDiscountsKeepsQuantityAndUnitPrice()
    {
        var quotation = QuotationWithTwoItems(out var first, out _);
        var item = quotation.Items.Single(entry => entry.Id == first);
        var quantity = item.Quantity;
        var unitPrice = item.UnitPrice;

        quotation.ApplyItemDiscounts(
            new Dictionary<QuotationItemId, decimal> { [first] = 25m }, AdvisorId, Now);

        Assert.Equal(quantity, item.Quantity);
        Assert.Equal(unitPrice, item.UnitPrice);
    }

    // Una linea que no viene en el mapa se queda como estaba: el recalculador manda todas, pero
    // el agregado no asume nada.
    [Fact]
    public void ApplyItemDiscountsLeavesUnlistedItemsAlone()
    {
        var quotation = QuotationWithTwoItems(out var first, out var second);
        var untouched = quotation.Items.Single(item => item.Id == second).DiscountPercentage;

        quotation.ApplyItemDiscounts(
            new Dictionary<QuotationItemId, decimal> { [first] = 10m }, AdvisorId, Now);

        Assert.Equal(untouched, quotation.Items.Single(item => item.Id == second).DiscountPercentage);
    }

    // US-10: una cotizacion convertida, anulada o vencida ya no se recalcula.
    [Fact]
    public void ApplyItemDiscountsRejectsANonEditableQuotation()
    {
        var quotation = QuotationWithTwoItems(out var first, out _);
        quotation.Void(AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(() => quotation.ApplyItemDiscounts(
            new Dictionary<QuotationItemId, decimal> { [first] = 10m }, AdvisorId, Now));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
    }
```

Y el helper, junto a `NewQuotation` (reusa sus constantes `AdvisorId` y `Now`, que ya existen en el archivo):

```csharp
    private static Quotation QuotationWithTwoItems(
        out QuotationItemId first, out QuotationItemId second)
    {
        var quotation = NewQuotation();
        first = QuotationItemId.New();
        second = QuotationItemId.New();

        quotation.AddItem(
            first, Guid.CreateVersion7(), quantity: 10, unitPrice: 119_000m,
            discountPercentage: 5m, taxPercentage: 19, AdvisorId, Now);
        quotation.AddItem(
            second, Guid.CreateVersion7(), quantity: 8, unitPrice: 119_000m,
            discountPercentage: 5m, taxPercentage: 19, AdvisorId, Now);

        return quotation;
    }
```

`quotation.quotation.not_editable` es el literal exacto de `EnsureEditable` (`Quotation.cs:403-410`) — verificado, no supuesto.

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationTests"
```

Esperado: no compila — `ApplyItemDiscounts` no existe. Pegar la salida.

- [ ] **Step 3: Implementar en `QuotationItem`**

Junto a `UpdateQuantity`:

```csharp
    /// <summary>Cambia sólo el descuento, conservando cantidad, precio e impuesto. Lo usa
    /// <see cref="Quotation.ApplyItemDiscounts"/> cuando el recálculo de un grupo mueve el
    /// descuento de una línea que nadie tocó.</summary>
    internal void UpdateDiscount(decimal discountPercentage, DateTimeOffset occurredAt) =>
        Apply(Quantity, UnitPrice, discountPercentage, TaxPercentage, occurredAt);
```

- [ ] **Step 4: Implementar en `Quotation`**

Después de `RemoveItem`:

```csharp
    /// <summary>
    /// Reescribe los descuentos de las líneas y recalcula los totales una sola vez. Existe
    /// porque desde la agrupación por escala el descuento de una línea depende de las otras: la
    /// aplicación los resuelve todos juntos (<c>QuotationScaleGroupPricing</c>) y el agregado
    /// los aplica. Una línea ausente del mapa se queda como está.
    /// </summary>
    public void ApplyItemDiscounts(
        IReadOnlyDictionary<QuotationItemId, decimal> discountsByItem,
        MemberId updatedBy,
        DateTimeOffset occurredAt)
    {
        EnsureEditable();

        foreach (var item in _items)
        {
            if (discountsByItem.TryGetValue(item.Id, out var discountPercentage))
            {
                item.UpdateDiscount(discountPercentage, occurredAt);
            }
        }

        Touch(updatedBy, occurredAt);
    }
```

- [ ] **Step 5: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
```

Esperado: PASS. Pegar la salida.

- [ ] **Step 6: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations/Modules.Quotations.Domain tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTests.cs
git commit -m "feat(quotations): aplicar descuentos recalculados sobre todas las lineas"
```

---

### Task 6: Cablear el recálculo en agregar, editar y quitar

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPricingRecalculation.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationProductPricingResolver.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/AddQuotationItem.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/UpdateQuotationItem.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/RemoveQuotationItem.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationItemApiTests.cs`

**Interfaces:**
- Consumes: `QuotationScaleGroupPricing.Resolve`, `QuotationPricingLine`, `QuotationLinePricing` (Task 4); `Quotation.ApplyItemDiscounts` (Task 5); `IQuotationProductLookup.FindManyAsync` (ya existe).
- Produces:
  - `QuotationPricingRecalculation.ApplyAsync(IQuotationProductLookup productLookup, Guid tenantId, Quotation quotation, MemberId updatedBy, DateTimeOffset occurredAt, CancellationToken cancellationToken)` → `Task`
  - `QuotationProductPricingResolver.ResolveAsync(...)` cambia su retorno a `Task<(decimal UnitPrice, int TaxPercentage)>` — **deja de devolver el descuento**.

**Verificado:** `CreateQuotation` no agrega líneas, así que no entra al alcance. Los tres handlers de línea son los únicos que mutan `Items`.

- [ ] **Step 1: Escribir las pruebas de integración que fallan**

En `QuotationItemApiTests.cs`, y agregar primero el helper de escala agrupable junto a `MultipleOfThreeFromFive`:

```csharp
    /// <summary>Una sola escala 5-48 de a 3, con la agrupacion activada.</summary>
    private static object[] GroupableMultipleOfThree(decimal baseCop) =>
    [
        new
        {
            fromUnit = 5, toUnit = 48, discount = 5m,
            restriction = "multiple", multiple = 3, finalCop = baseCop * 0.95m,
            allowGrouping = true
        }
    ];
```

Y las pruebas:

```csharp
    // El caso del requisito: 10 + 8 + 12 = 30. Ninguna linea cumple el multiplo sola y las tres
    // reciben su descuento.
    [Fact]
    public async Task ItemsOnAGroupableScaleShareTheirQuantitiesToSatisfyTheMultiple()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        QuotationResponse? latest = null;
        foreach (var quantity in new[] { 10m, 8m, 12m })
        {
            var productId = await CreateProductWithScalesAsync(
                client, tenantId, baseCop: 100_000m, scales: GroupableMultipleOfThree(100_000m));
            latest = await ReadQuotationAsync(await client.PostAsJsonAsync(
                $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
                new AddQuotationItemRequest(productId, quantity),
                TestContext.Current.CancellationToken));
        }

        Assert.NotNull(latest);
        Assert.Equal(3, latest.Items.Count);
        Assert.All(latest.Items, item => Assert.Equal(5m, item.DiscountPercentage));
    }

    // La primera linea del grupo se guarda sin descuento en vez de dar 422: con bloqueo, el
    // total 10+8+12 no tendria ningun camino de estados intermedios validos que lo alcance.
    [Fact]
    public async Task TheFirstItemOfAGroupIsStoredWithoutDiscount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 100_000m, scales: GroupableMultipleOfThree(100_000m));
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 10m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal(0m, Assert.Single(created.Items).DiscountPercentage);
    }

    // Quitar una linea cambia el total del grupo: 30 - 8 = 22, que no es multiplo de 3. Sin
    // recalcular al quitar, la cotizacion quedaria guardada con descuentos que ya no
    // corresponden.
    [Fact]
    public async Task RemovingAnItemRecalculatesTheDiscountsOfTheRest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        Guid removableItemId = Guid.Empty;
        foreach (var quantity in new[] { 10m, 8m, 12m })
        {
            var productId = await CreateProductWithScalesAsync(
                client, tenantId, baseCop: 100_000m, scales: GroupableMultipleOfThree(100_000m));
            var current = await ReadQuotationAsync(await client.PostAsJsonAsync(
                $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
                new AddQuotationItemRequest(productId, quantity),
                TestContext.Current.CancellationToken));
            if (quantity == 8m)
            {
                removableItemId = current.Items.Single(item => item.Quantity == 8m).Id;
            }
        }

        var response = await client.DeleteAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{removableItemId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await ReadQuotationAsync(await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(2, after.Items.Count);
        Assert.All(after.Items, item => Assert.Equal(0m, item.DiscountPercentage));
    }

    // Editar la cantidad de una linea mueve el descuento de las otras del grupo: 10 + 8 = 18
    // cumple, y subir la primera a 11 deja 19, que no.
    [Fact]
    public async Task UpdatingOneItemMovesTheDiscountOfTheOthersInItsGroup()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var firstProductId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 100_000m, scales: GroupableMultipleOfThree(100_000m));
        var secondProductId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 100_000m, scales: GroupableMultipleOfThree(100_000m));

        await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(firstProductId, 10m),
            TestContext.Current.CancellationToken);
        var withBoth = await ReadQuotationAsync(await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(secondProductId, 8m),
            TestContext.Current.CancellationToken));
        Assert.All(withBoth.Items, item => Assert.Equal(5m, item.DiscountPercentage));

        var firstItemId = withBoth.Items.Single(item => item.ProductId == firstProductId).Id;
        var updated = await ReadQuotationAsync(await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{firstItemId}",
            new UpdateQuotationItemRequest(11m),
            TestContext.Current.CancellationToken));

        Assert.All(updated.Items, item => Assert.Equal(0m, item.DiscountPercentage));
    }
```

`MapDelete` devuelve `Results.Ok(...)` con la cotización compuesta (`QuotationEndpoints.cs:67` y su handler), así que `ReadQuotationAsync` sirve igual sobre la respuesta del `DELETE`; la prueba de arriba usa el `GET` posterior sólo para comprobar que lo guardado coincide con lo devuelto.

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationItemApiTests"
```

Esperado: FAIL. Las de grupo, porque cada línea se evalúa sola y ninguna alcanza el múltiplo; la de borrado, porque nadie recalcula al quitar. Pegar la salida.

- [ ] **Step 3: Crear el recalculador**

Crear `QuotationPricingRecalculation.cs`:

```csharp
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// El paso que cierra toda mutación de líneas: vuelve a resolver el descuento de **todas** y se
/// lo entrega al agregado. Existe desde que la agrupación por escala hizo que el descuento de
/// una línea dependa de las otras — recalcular sólo la línea tocada deja a las demás con un
/// descuento que ya no corresponde, y quitar una línea no toca ninguna.
///
/// Lee las escalas con <see cref="IQuotationProductLookup.FindManyAsync"/>, el mismo puerto en
/// lote que ya usa <c>QuotationResponseComposer</c> para pintar la respuesta: una consulta por
/// mutación, no una por línea.
/// </summary>
internal static class QuotationPricingRecalculation
{
    public static async Task ApplyAsync(
        IQuotationProductLookup productLookup,
        Guid tenantId,
        Quotation quotation,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var lines = quotation.Items
            .Select(item => new QuotationPricingLine(item.Id.Value, item.ProductId, item.Quantity))
            .ToArray();

        if (lines.Length == 0)
        {
            return;
        }

        var products = await productLookup.FindManyAsync(
            tenantId,
            lines.Select(line => line.ProductId).Distinct().ToArray(),
            cancellationToken);

        var pricing = QuotationScaleGroupPricing.Resolve(
            lines,
            products.ToDictionary(entry => entry.Key, entry => entry.Value.Scales));

        quotation.ApplyItemDiscounts(
            pricing.ToDictionary(
                line => new QuotationItemId(line.ItemId),
                line => line.DiscountPercentage),
            updatedBy,
            occurredAt);
    }
}
```

- [ ] **Step 4: Sacarle el descuento al resolver por línea**

En `QuotationProductPricingResolver.cs`, cambiar la firma a
`Task<(decimal UnitPrice, int TaxPercentage)>`, borrar el cálculo de `discount` que dejó la Task 3 y devolver `(unitPrice, product.TaxPercentage ?? 0)`. **Conservar** la llamada a `EnsurePackagingUnit`: es el 422 de la línea que el comando toca. Actualizar el comentario para decir que el descuento ahora lo resuelve `QuotationScaleGroupPricing` sobre todas las líneas juntas.

- [ ] **Step 5: Cablear los tres handlers**

En los tres, inyectar `IQuotationProductLookup productLookup` en el constructor primario.

`AddQuotationItem`: el destructuring pasa a `var (unitPrice, taxPercentage) = ...`; `quotation.AddItem(...)` recibe `0m` como `discountPercentage` —lo fija el recálculo un renglón después—; e inmediatamente después de `AddItem`:

```csharp
        // El descuento de esta línea y el de las demás de su grupo salen de acá, no de AddItem:
        // agregarla puede completar el múltiplo que le faltaba al grupo entero.
        await QuotationPricingRecalculation.ApplyAsync(
            productLookup, command.TenantId, quotation, updatedBy, now, cancellationToken);
```

`UpdateQuotationItem`: `var (_, taxPercentage) = ...`; `UpdateItemQuantity(item.Id, command.Quantity, 0m, taxPercentage, updatedBy, now)`; y la misma llamada a `ApplyAsync` después.

`RemoveQuotationItem`: la misma llamada después de `quotation.RemoveItem(...)`.

En los tres va **antes** de `AddHistoryEntry`, `auditPublisher.Publish` y `SaveChangesAsync`.

- [ ] **Step 6: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationItemApiTests"
```

Esperado: PASS el archivo entero, incluida la de empaque que sigue esperando 422. Pegar la salida.

- [ ] **Step 7: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations tests/Modules/Quotations
git commit -m "feat(quotations): recalcular los descuentos de todas las lineas en cada mutacion"
```

---

### Task 7: El estado de la restricción en la respuesta

Sin esto la pantalla muestra un precio sin descuento y nadie sabe por qué: como `Multiple` ya no da 422, no queda ningún otro canal.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs:136-146` y `:206-227`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationResponseComposer.cs:30-58` y `:112-140`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationItemApiTests.cs`

**Interfaces:**
- Consumes: `QuotationScaleGroupPricing.Resolve`, `QuotationLinePricing` (Task 4).
- Produces:
  - `QuotationItemPriceScaleResponse(int FromUnit, int ToUnit, decimal Discount, string Restriction, int? Multiple, int? PackagingUnit, bool AllowGrouping)`
  - `public sealed record QuotationItemScaleStatusResponse(bool Applied, bool Grouped, string? Code, decimal EvaluatedQuantity, decimal Shortfall)`
  - `QuotationItemResponse(..., int Position, QuotationItemScaleStatusResponse? ScaleStatus)` — **último**, porque el composer es su único constructor y así ninguna otra construcción posicional se rompe.

- [ ] **Step 1: Escribir la prueba que falla**

En `QuotationItemApiTests.cs`:

```csharp
    // El 422 de multiplo ya no existe, asi que el motivo tiene que viajar en la respuesta: sin
    // el total del grupo y el faltante, la pantalla no puede explicar el precio sin descuento.
    [Fact]
    public async Task AnUnsatisfiedGroupReportsItsTotalAndItsShortfall()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        QuotationResponse? latest = null;
        foreach (var quantity in new[] { 10m, 12m })
        {
            var productId = await CreateProductWithScalesAsync(
                client, tenantId, baseCop: 100_000m, scales: GroupableMultipleOfThree(100_000m));
            latest = await ReadQuotationAsync(await client.PostAsJsonAsync(
                $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
                new AddQuotationItemRequest(productId, quantity),
                TestContext.Current.CancellationToken));
        }

        Assert.NotNull(latest);
        Assert.All(latest.Items, item =>
        {
            Assert.NotNull(item.ScaleStatus);
            Assert.False(item.ScaleStatus.Applied);
            Assert.True(item.ScaleStatus.Grouped);
            Assert.Equal("quotation.item.quantity_not_multiple", item.ScaleStatus.Code);
            Assert.Equal(22m, item.ScaleStatus.EvaluatedQuantity);
            Assert.Equal(2m, item.ScaleStatus.Shortfall);
        });
        Assert.All(latest.Items, item => Assert.True(Assert.Single(item.PriceScales).AllowGrouping));
    }
```

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~AnUnsatisfiedGroupReportsItsTotalAndItsShortfall"
```

Esperado: no compila — `ScaleStatus` y `AllowGrouping` no existen en los DTO de respuesta. Pegar la salida.

- [ ] **Step 3: Los DTO**

En `QuotationsDtos.cs`, agregar `bool AllowGrouping` como último parámetro de `QuotationItemPriceScaleResponse` y actualizar su comentario: la escala ya no viaja para "evitar el 422" —que para `Multiple` no existe más— sino para que el formulario anticipe qué cantidades reciben descuento.

Agregar el record nuevo:

```csharp
/// <summary>
/// Por qué la escala que cubre la cantidad se aplicó o no. Existe porque la restricción
/// <c>Multiple</c> dejó de dar 422: incumplirla ya no bloquea, deja la línea sin descuento — y
/// sin este bloque la pantalla muestra un precio sin explicación.
/// </summary>
/// <param name="Grouped">Si <paramref name="EvaluatedQuantity"/> es la suma del grupo y no la
/// cantidad de esta línea. "Faltan 2 unidades" significa cosas distintas según de quién sean.</param>
/// <param name="EvaluatedQuantity">La cantidad contra la que se evaluó el múltiplo. La pantalla
/// no puede reconstruirla sola: depende de qué otras líneas comparten esta escala.</param>
public sealed record QuotationItemScaleStatusResponse(
    bool Applied,
    bool Grouped,
    string? Code,
    decimal EvaluatedQuantity,
    decimal Shortfall);
```

Y a `QuotationItemResponse`, como último parámetro:

```csharp
    /// <summary>Null cuando la cantidad no cae en ninguna escala: no hay nada que explicar.</summary>
    QuotationItemScaleStatusResponse? ScaleStatus);
```

- [ ] **Step 4: El composer**

En `ComposeAsync`, después de resolver `products` y antes de construir la respuesta:

```csharp
        // El mismo agrupador que usan las mutaciones, sobre los mismos productos que esta
        // respuesta ya trajo: ninguna consulta nueva.
        var pricing = QuotationScaleGroupPricing
            .Resolve(
                quotation.Items
                    .Select(item => new QuotationPricingLine(item.Id, item.ProductId, item.Quantity))
                    .ToArray(),
                products.ToDictionary(entry => entry.Key, entry => entry.Value.Scales))
            .ToDictionary(line => line.ItemId);
```

Pasar `pricing` a `ToItemResponse`, agregar `scale.AllowGrouping` al `new QuotationItemPriceScaleResponse(...)`, y como último argumento del `new QuotationItemResponse(...)`:

```csharp
            ToScaleStatus(pricing.GetValueOrDefault(item.Id)));
```

Con:

```csharp
    private static QuotationItemScaleStatusResponse? ToScaleStatus(QuotationLinePricing? pricing) =>
        pricing?.Restriction is not { } restriction
            ? null
            : new QuotationItemScaleStatusResponse(
                restriction.IsSatisfied,
                pricing.Grouped,
                restriction.Code,
                restriction.EvaluatedQuantity,
                restriction.Shortfall);
```

`QuotationLinePricing` es `internal` y el composer vive en el mismo assembly, así que el helper va `private static` sin problema — pero `ToItemResponse` deja de poder ser `static` público si hiciera falta exponerlo; no hace falta, se queda `private static`.

- [ ] **Step 5: Correr y verificar que pasa**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests
```

Esperado: PASS el proyecto entero de integración de Quotations. Pegar la salida.

- [ ] **Step 6: Verificación completa del repositorio**

```powershell
dotnet format --verify-no-changes
if ($?) { dotnet build }
if ($?) { dotnet test }
```

Esperado: los tres en verde, incluidos `ArchitectureTests` (`Modules.Quotations.Application` sigue sin referenciar Catalog) y los módulos que no se tocaron. Pegar la salida de los tres. **No declarar la funcionalidad terminada sin esta evidencia.**

- [ ] **Step 7: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations tests/Modules/Quotations
git commit -m "feat(quotations): devolver por linea si la escala aplico y cuanto falta al grupo"
```

---

## Fuera de alcance

Anotado para que no se cuele sin decisión:

- **`ConvertQuotationToSale` no revalida los grupos.** Se convierte desde `Sent`, y US-10 permite editar en `Sent`, así que una cotización editada después de enviada llega a la venta con lo que el último recálculo dejó. Es lo mismo que ya pasa hoy con el resto del precio.
- **No se migran datos.** Las cotizaciones creadas entre `5a76b07` y este cambio conservan sus descuentos hasta que alguien edite una línea.
- **`Seed/Data/catalog-products.json` no cambia.** `CatalogSeeder.cs:66` no crea escalas.
