# Siigo: teléfono del cliente con indicativo (backend, entrega 1) — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Las reglas de ejecución de este workspace y la instrucción del owner mandan sobre el proceso del skill**: dos bloques, un implementador y una revisión por bloque, y **ninguna prueba se corre** (ver «Global Constraints»).

**Goal:** El cliente que se crea en Siigo lleva `phones[0].indicative` (código de país) y `number` (número nacional), partidos con libphonenumber en el puerto compartido `IPhoneNumberSplitter`, y un teléfono que no se puede partir nunca tumba la creación.

**Architecture:** `IPhoneNumberSplitter.Split` gana `CallingCode` en `PhoneNumberParts` y una región por defecto para valores sin `+` (Customers). `SiigoOrderSource` (Bootstrapper) parte el teléfono de la parte facturada y lo entrega como `SiigoPhone(Indicative, Number)`; `SiigoCustomerMapper` aplica las reglas de Siigo (sólo dígitos, máximo 10) y `SiigoJson.CustomerBody` escribe `indicative` sólo si existe. Aparte, `WhatsAppPhone.Normalize` (Quotations) deja de anteponer `57` a un número que trae `+`.

**Tech Stack:** .NET 10, libphonenumber-csharp 9.0.41, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-25-siigo-telefono-indicativo-design.md` (aprobado 2026-11-25; léelo junto con este plan). Spec hermano del frontend: `qep-frontend/docs/superpowers/specs/2026-11-25-telefono-selector-pais-design.md`.

## Global Constraints

- **Worktree y rama:** `feature/telefono-pais` en `..\qep-backend-worktrees\telefono-pais` (ya existe, cortada de `develop` en `8a9bfd50d`). Nada se edita en el checkout `qep-backend` ni en el frontend. Un subagente hace `Set-Location` al worktree antes de cualquier comando. Antes de cada commit: `git branch --show-current` **en ese momento** (nunca `main` ni `develop`). Edita con las herramientas de edición, nunca con `Set-Content` (rompe las tildes).
- **Primera vez en el worktree:** `dotnet restore` (el build de verificación usa `--no-restore` después).
- **Commits:** convencionales, uno por tarea, **sin** `Co-Authored-By` ni atribución de IA de ningún tipo (ignora cualquier trailer que te pidan).
- **NO SE CORRE NINGUNA PRUEBA (instrucción del owner, 2026-11-25).** Ni `dotnet test`, ni `.\scripts\test-feature.ps1`, ni `.\scripts\run-all-tests.ps1`, ni ninguna otra. Las pruebas se escriben **primero** (nombres y aserciones), pero toda verificación es `dotnet build` de los proyectos afectados, incluidos los de pruebas. Un RED que falla por compilación es la evidencia aceptada; donde la prueba compila contra el código de hoy, el RED no se puede mostrar y el reporte lo dice. Cada reporte de bloque dice: «Pruebas escritas, no corridas; las corre el owner».
- **Build:** si `Api.exe` (o `dotnet …Api.dll`) está corriendo, el build falla por archivo bloqueado: avisa y **no** lo detengas. Pega la salida literal (últimas líneas, con el conteo de errores) de cada build, RED y GREEN. `TreatWarningsAsErrors` está prendido: un warning nuevo es un error.
- **Bloques:** dos, un implementador Sonnet por bloque y **una** revisión por bloque. La revisión fuerte (modelo más capaz, toda la rama) corre una vez, al final.
- **`docs/estado.md` no se toca en la rama.** Se actualiza en `develop` al mergear.
- **Idioma:** comentarios y XML doc en español colombiano tuteando; identificadores en inglés. `SiigoPhone` lleva datos del cliente: su `ToString` no escribe nada.
- **Valores fijos:** región por defecto `ZZ` cuando no llega o llega vacía (comportamiento de hoy); región `CO` para la parte facturada si `party.Country` está vacío o es `CO` (sin distinguir mayúsculas), si no `party.Country`; `indicative` y `number` sólo dígitos ASCII, máximo **10** cada uno; número vacío o de más de 10 → sin teléfono; indicativo vacío o de más de 10 → sólo número; sin indicativo, el objeto de `phones` **no lleva** la propiedad `indicative` (no `null`).
- **Despliegue:** este backend va **antes** que la entrega 2 del frontend. Sin migraciones ni configuración nueva.

## Review Focus

1. **Teléfono con prefijo doble o de otro país sin `+`** (`+57573001234567`, `584121234567` con parte `CO`): no se parte, sus dígitos pasan de 10 y el cliente se crea **sin** `phones`, nunca con un número basura ni con una excepción. Pruebas en las Tareas 3 (mapper) y 1 (splitter).
2. **País de la parte en minúsculas o con espacios** (`"co"`, `" ve "`): la región se normaliza (trim + mayúsculas) antes de llamar a `Split`, y un país que la librería no conoce da `null`, no una excepción. Prueba en la Tarea 4 (`ve` → región `VE`) y en la Tarea 1 (región desconocida → `null`).
3. **Consumidor final con teléfono**: hoy `ResolvePartyAsync` lo manda `null`, pero el mapper aplica las mismas reglas en esa rama por si cambia. Prueba en la Tarea 3.
4. **E.164 de 10 dígitos en total para WhatsApp** (Belice `+5016123456`): sale `5016123456`, no `575016123456`. Prueba en la Tarea 2.
5. **El curl de `siigo.call_failures`** guarda el cuerpo nuevo sin cambios de forma fuera de `phones`: la prueba de `CustomerBody` sigue verificando el resto del cuerpo. Tarea 3.

---

## Bloque 1 — Puerto compartido y WhatsApp (Tareas 1 y 2, un implementador)

### Task 1: `IPhoneNumberSplitter` devuelve el indicativo y acepta región por defecto

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/IPhoneNumberSplitter.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Phones/LibPhoneNumberNormalizer.cs` (método `Split`, líneas 40-64)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/LibPhoneNumberNormalizerTests.cs`

**Interfaces:**
- Produces: `public sealed record PhoneNumberParts(string Country, string NationalNumber, string CallingCode);` y `PhoneNumberParts? IPhoneNumberSplitter.Split(string? phone, string? defaultRegion = null);`. `CallingCode` son los dígitos de `parsed.CountryCode` (`"57"`). `MessagingCustomerDirectory` (Bootstrapper) llama `Split(e164)` y no cambia.

- [ ] **Step 1: Escribe las pruebas**

En `LibPhoneNumberNormalizerTests`, cambia la teoría existente `SplitsAValidNumberIntoRegionAndNationalDigits` para que espere tres partes y agrega:

```csharp
[Theory]
[InlineData("+573001234567", "CO", "3001234567", "57")]
[InlineData("+14155552671", "US", "4155552671", "1")]
[InlineData("+34612345678", "ES", "612345678", "34")]
[InlineData("+390612345678", "IT", "0612345678", "39")]
public void SplitsAValidNumberIntoRegionNationalDigitsAndCallingCode(string e164, string country, string national, string callingCode) =>
    Assert.Equal(new PhoneNumberParts(country, national, callingCode), _normalizer.Split(e164));

[Theory]
[InlineData("573001234567", "CO")]
[InlineData("3001234567", "CO")]
[InlineData("300 123 4567", "CO")]
[InlineData("+57 300 123 4567", null)]
[InlineData("+57 300 123 4567", "ES")] // con «+» la región se ignora
public void ANumberWithoutPlusIsReadAsNationalOfTheDefaultRegion(string phone, string? region) =>
    Assert.Equal(new PhoneNumberParts("CO", "3001234567", "57"), _normalizer.Split(phone, region));

[Fact]
public void AVenezuelanMobileWithTheVenezuelanRegionGetsItsCallingCode() =>
    Assert.Equal(new PhoneNumberParts("VE", "4121234567", "58"), _normalizer.Split("4121234567", "VE"));

[Theory]
[InlineData("3001234567", null)]   // sin «+» y sin región: hoy
[InlineData("3001234567", "")]
[InlineData("573001234567", null)]
[InlineData("584121234567", "CO")] // VE sin «+» leído como colombiano: inválido
[InlineData("+57573001234567", "CO")] // prefijo doble
[InlineData("3001234567", "XX")]   // región que la librería no conoce
public void WhatCannotBeSplitIsNull(string phone, string? region) => Assert.Null(_normalizer.Split(phone, region));
```

Borra la teoría vieja de dos partes (la reemplaza la primera de arriba). `SplitPartsRoundTripToTheSameE164` y `AnInvalidNumberIsNotSplit` quedan igual.

- [ ] **Step 2: RED por compilación**

Run: `dotnet build tests/Modules/Customers/Modules.Customers.UnitTests/Modules.Customers.UnitTests.csproj --no-restore`
Expected: falla con `CS1729` (`PhoneNumberParts` no tiene constructor de 3 argumentos) y `CS1501` (`Split` no acepta 2 argumentos).

- [ ] **Step 3: Implementa**

En `IPhoneNumberSplitter.cs`: el record con `CallingCode` y la firma `Split(string? phone, string? defaultRegion = null)`; actualiza los XML doc (con «+» la región se ignora; sin «+» es nacional de `defaultRegion`, ISO alfa-2; sin región o vacía, `ZZ`; `null` si no parsea, no es válido o no tiene región; nunca lanza). En `LibPhoneNumberNormalizer.Split`: región = `string.IsNullOrWhiteSpace(defaultRegion) ? UnknownRegion : defaultRegion.Trim().ToUpperInvariant()`; `Util.Parse(phone.Trim(), region)`; el resultado lleva `parsed.CountryCode.ToString(CultureInfo.InvariantCulture)`. El `catch (NumberParseException)` ya cubre la región desconocida.

- [ ] **Step 4: GREEN por compilación**

Run: `dotnet build tests/Modules/Customers/Modules.Customers.UnitTests/Modules.Customers.UnitTests.csproj --no-restore; if ($?) { dotnet build src/Bootstrapper/Bootstrapper.csproj --no-restore }`
Expected: `0 Error(s)` en los dos (Bootstrapper compila `MessagingCustomerDirectory`, que llama `Split(e164)`). Pruebas escritas, no corridas.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Customers/Modules.Customers.Application/IPhoneNumberSplitter.cs src/Modules/Customers/Modules.Customers.Infrastructure/Phones/LibPhoneNumberNormalizer.cs tests/Modules/Customers/Modules.Customers.UnitTests/LibPhoneNumberNormalizerTests.cs
git commit -m "feat(customers): return the calling code and accept a default region when splitting a phone"
```

### Task 2: `WhatsAppPhone.Normalize` respeta el `+`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppPhone.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/WhatsAppPhoneTests.cs`

**Interfaces:** firma sin cambio: `public static string? Normalize(string? phone)`.

- [ ] **Step 1: Escribe la prueba**

```csharp
[Theory]
[InlineData("+5016123456", "5016123456")]   // Belice: 10 dígitos en total con su indicativo
[InlineData("+573001234567", "573001234567")]
[InlineData(" +592 612 3456", "5926123456")]
public void ANumberWithPlusKeepsItsDigits(string phone, string expected) =>
    Assert.Equal(expected, WhatsAppPhone.Normalize(phone));
```

Las teorías existentes (`TenDigitsGetTheColombianPrefix` con `3001234567` → `573001234567`) no cambian.

- [ ] **Step 2: RED**

Esta prueba compila contra el código de hoy: el RED (`575016123456` en vez de `5016123456`) no se puede mostrar sin correrla. Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --no-restore` → `0 Error(s)`, y el reporte dice que el RED no es verificable por la instrucción del owner.

- [ ] **Step 3: Implementa**

Con `phone.TrimStart().StartsWith('+')` devuelve los dígitos tal cual; si no, la regla de hoy. Actualiza el comentario: el `+` dice que el número ya trae su indicativo (E.164 que guardan los formularios con selector de país).

- [ ] **Step 4: GREEN por compilación**

Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --no-restore`
Expected: `0 Error(s)`. Pruebas escritas, no corridas.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Quotations/Modules.Quotations.Application/WhatsAppPhone.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/WhatsAppPhoneTests.cs
git commit -m "fix(quotations): keep a plus-prefixed phone as is for WhatsApp"
```

**Revisión del bloque 1:** un revisor (Sonnet) sobre los dos commits: firmas del puerto, que ningún otro implementador de `IPhoneNumberSplitter` exista, y que nada corrió pruebas.

---

## Bloque 2 — Siigo recibe indicativo y número (Tareas 3 y 4, un implementador)

### Task 3: `SiigoPhone`, mapper y cuerpo del cliente

**Files:**
- Create: `src/Modules/Siigo/Modules.Siigo.Application/SiigoPhone.cs` (namespace `Modules.Siigo.Application`, el mismo de `SiigoCustomerRequest`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/ISiigoApi.cs:47` (`SiigoCustomerRequest.Phone`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceInput.cs:60` (`SiigoBilledPartyInput.Phone`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoCustomerMapper.cs` (líneas 30 y 56)
- Modify: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoJson.cs:184-187`
- Modify (puente, ver Step 3): `src/Bootstrapper/SiigoOrderSource.cs:62`
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoCustomerMapperTests.cs`, `SiigoApiClientTests.cs` (prueba de `CreateCustomerAsync`, línea ~789-810, y la de línea ~821), `InvoiceTestData.cs:58-59` (`Company()`)

**Interfaces:**
- Produces: `public sealed record SiigoPhone(string? Indicative, string Number) { public override string ToString() => "SiigoPhone { }"; }`; `SiigoBilledPartyInput(..., SiigoPhone? Phone)`; `SiigoCustomerRequest(..., SiigoPhone? Phone, ...)` (misma posición, sólo cambia el tipo).

- [ ] **Step 1: Escribe las pruebas**

Ajusta los constructores existentes al tipo nuevo: `Person()` en el mapper test y `Company()` en `InvoiceTestData` pasan `new SiigoPhone("57", "3109998877")` / `new SiigoPhone("57", "3001234567")`; las `SiigoCustomerRequest` de prueba pasan `new SiigoPhone("57", "3001234567")` (CreateCustomer), `null` (la del error) y `new SiigoPhone(null, "300")` (`TheRequestToStringIsRedactedEvenWithoutTheMapper`). En `TheAddressCarriesTheDaneCodes`, `Assert.Equal(new SiigoPhone("57", "3109998877"), request.Phone)`.

Nuevas en `SiigoCustomerMapperTests`:

```csharp
[Theory]
[InlineData("57", "300 123-4567", "57", "3001234567")]
[InlineData("+57", "3001234567", "57", "3001234567")]
[InlineData(null, "12345", null, "12345")]
[InlineData("12345678901", "3001234567", null, "3001234567")] // indicativo de más de 10: viaja el número solo
[InlineData("", "3001234567", null, "3001234567")]
public void ThePhoneIsCleanedToDigits(string? indicative, string number, string? expectedIndicative, string expectedNumber) =>
    Assert.Equal(new SiigoPhone(expectedIndicative, expectedNumber), SiigoCustomerMapper.Map(Person() with { Phone = new SiigoPhone(indicative, number) })!.Phone);

[Theory]
[InlineData("+57573001234567")] // no partido: 14 dígitos
[InlineData("584121234567")]
[InlineData("sin número")]
[InlineData("  ")]
public void ANumberThatIsEmptyOrLongerThanTenDigitsSendsNoPhone(string number) =>
    Assert.Null(SiigoCustomerMapper.Map(Person() with { Phone = new SiigoPhone(null, number) })!.Phone);

[Fact]
public void AFinalConsumerFollowsTheSamePhoneRules()
{
    var party = new SiigoBilledPartyInput(true, null, null, "", "", null, null, null, null, new SiigoPhone("57", "+57 300 123 4567"));
    Assert.Null(SiigoCustomerMapper.Map(party)!.Phone); // «57300…» son 12 dígitos en el número
    Assert.Null(SiigoCustomerMapper.Map(party with { Phone = null })!.Phone);
}

[Fact]
public void ThePhoneNeverPrints() =>
    Assert.DoesNotContain("3001234567", new SiigoPhone("57", "3001234567").ToString(), StringComparison.Ordinal);
```

En `SiigoApiClientTests`, la prueba de `CreateCustomerAsync` reemplaza la aserción de `number` por:

```csharp
var phone = root.GetProperty("phones")[0];
Assert.Equal("57", phone.GetProperty("indicative").GetString());
Assert.Equal("3001234567", phone.GetProperty("number").GetString());
```

y agrega dos pruebas sobre `SiigoJson.CustomerBody` (`internal`, visible por `InternalsVisibleTo`, como ya la usa `SiigoServiceErrorTests`); `Body(request)` es un helper local que parsea `SiigoJson.CustomerBody(request)` con `JsonDocument` y devuelve `RootElement.Clone()`:

```csharp
[Fact]
public void APhoneWithoutIndicativeHasNoIndicativeProperty()
{
    // request con new SiigoPhone(null, "12345")
    var phone = Body(request).GetProperty("phones")[0];
    Assert.False(phone.TryGetProperty("indicative", out _));
    Assert.Equal("12345", phone.GetProperty("number").GetString());
}

[Fact]
public void NoPhoneMeansNoPhonesProperty() => Assert.False(Body(requestWithoutPhone).TryGetProperty("phones", out _));
```

- [ ] **Step 2: RED por compilación**

Run: `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests/Modules.Siigo.UnitTests.csproj --no-restore`
Expected: falla con `CS0246` (`SiigoPhone` no existe).

- [ ] **Step 3: Implementa**

- `SiigoPhone.cs` con XML doc: el teléfono como lo pide Siigo al crear un cliente (spec 2026-11-25); lleva datos del cliente.
- Cambia el tipo de `Phone` en los dos records.
- `SiigoCustomerMapper`: las dos ramas usan un `private static SiigoPhone? Phone(SiigoPhone? phone)` que limpia `Number` e `Indicative` a dígitos ASCII (`char.IsAsciiDigit`) y aplica los topes de «Valores fijos». Constante `private const int PhonePartMaxLength = 10;` con el porqué (documentación de Siigo, crear cliente).
- `SiigoJson.CustomerBody`: con `request.Phone` no nulo, `phones` es un arreglo de un `Dictionary<string, string>` con `indicative` (sólo si no es nulo) y `number`.
- **Puente en `SiigoOrderSource`** para que este commit compile: `string.IsNullOrWhiteSpace(party.Phone) ? null : new SiigoPhone(null, party.Phone)`. Es el comportamiento de hoy con las reglas nuevas (dígitos ≤ 10); la Tarea 4 lo reemplaza.

- [ ] **Step 4: GREEN por compilación**

Run: `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests/Modules.Siigo.UnitTests.csproj --no-restore; if ($?) { dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests/Modules.Siigo.IntegrationTests.csproj --no-restore }; if ($?) { dotnet build tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj --no-restore }`
Expected: `0 Error(s)` en los tres. Pruebas escritas, no corridas.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Siigo tests/Modules/Siigo src/Bootstrapper/SiigoOrderSource.cs
git commit -m "feat(siigo): send the customer phone as indicative and number"
```

### Task 4: `SiigoOrderSource` parte el teléfono

**Files:**
- Modify: `src/Bootstrapper/SiigoOrderSource.cs` (constructor, línea 14; armado del teléfono, línea 62; reusa `IsColombian`, líneas 86-88)
- Modify: `.claude/rules/siigo.md` (un gotcha: el teléfono del cliente nuevo se parte en `SiigoOrderSource` con `IPhoneNumberSplitter`, región `CO` por defecto; el mapper omite `phones` si el número no cabe en 10 dígitos; no probado contra Siigo real)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoOrderSourceTests.cs`

**Interfaces:**
- Consumes: `IPhoneNumberSplitter.Split(string? phone, string? defaultRegion)` y `PhoneNumberParts.CallingCode` (Tarea 1); `SiigoPhone` (Tarea 3).
- Produces: `internal sealed class SiigoOrderSource(IExternalOrderInvoicing invoicing, ISiigoGeographyLookup geography, IPhoneNumberSplitter phones)`. El registro en `QepServiceCollectionExtensions.cs:2153` no cambia (el contenedor resuelve el singleton).

- [ ] **Step 1: Escribe las pruebas**

Agrega `using Modules.Customers.Application;` y un doble que registra la región:

```csharp
private sealed class FakeSplitter(PhoneNumberParts? parts) : IPhoneNumberSplitter
{
    public List<(string? Phone, string? Region)> Calls { get; } = [];

    public PhoneNumberParts? Split(string? phone, string? defaultRegion = null)
    {
        Calls.Add((phone, defaultRegion));
        return parts;
    }
}
```

Todas las construcciones existentes pasan un tercer argumento `new FakeSplitter(null)`. Pruebas nuevas (con un helper `Party(string? country, string? phone)` sobre `Order().BilledParty with { Country = …, Phone = … }`):

```csharp
[Fact]
public async Task ASplitPhoneTravelsAsIndicativeAndNumber()
{
    // splitter devuelve new PhoneNumberParts("CO", "3001234567", "57"); phone "+573001234567"
    Assert.Equal(new SiigoPhone("57", "3001234567"), input!.BilledParty.Phone);
}

[Theory]
[InlineData(null, "CO")]
[InlineData("", "CO")]
[InlineData("co", "CO")]
[InlineData("CO", "CO")]
[InlineData("VE", "VE")]
[InlineData(" ve ", "VE")]
public async Task TheRegionIsColombiaForAnEmptyCountryAndOtherwiseThePartyCountry(string? country, string expectedRegion)
{
    // phone "4121234567"
    Assert.Equal(("4121234567", expectedRegion), Assert.Single(splitter.Calls));
}

[Fact]
public async Task AnUnsplitPhoneTravelsRawForTheMapperToDecide()
{
    // splitter devuelve null; phone "12345"
    Assert.Equal(new SiigoPhone(null, "12345"), input!.BilledParty.Phone);
}

[Theory]
[InlineData(null)]
[InlineData("  ")]
public async Task NoPhoneIsNullAndTheSplitterIsNotCalled(string? phone)
{
    Assert.Null(input!.BilledParty.Phone);
    Assert.Empty(splitter.Calls);
}
```

- [ ] **Step 2: RED por compilación**

Run: `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj --no-restore`
Expected: falla con `CS1729` (`SiigoOrderSource` no tiene constructor de 3 argumentos).

- [ ] **Step 3: Implementa**

Constructor con `IPhoneNumberSplitter phones`; un `private SiigoPhone? PhoneOf(ExternalInvoicingParty party)`: vacío → `null`; región = `IsColombian(party.Country) ? "CO" : party.Country!.Trim().ToUpperInvariant()`; partido → `new SiigoPhone(parts.CallingCode, parts.NationalNumber)`; si no → `new SiigoPhone(null, party.Phone)`. Reemplaza el puente de la Tarea 3. Ajusta `IsColombian` para tolerar espacios (`country.Trim()`), sin cambiar su resultado para los valores de hoy. Actualiza el XML doc de la clase (el teléfono se parte acá porque Siigo no referencia Customers). Agrega el gotcha a `.claude/rules/siigo.md`.

- [ ] **Step 4: GREEN por compilación**

Run: `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests/Bootstrapper.UnitTests.csproj --no-restore; if ($?) { dotnet build Backend.slnx --no-restore }`
Expected: `0 Error(s)` en los dos (la solución entera confirma que nada más construía `SiigoOrderSource` ni los records). Pruebas escritas, no corridas.

- [ ] **Step 5: Commit**

```powershell
git add src/Bootstrapper/SiigoOrderSource.cs tests/Bootstrapper/Bootstrapper.UnitTests/SiigoOrderSourceTests.cs .claude/rules/siigo.md
git commit -m "feat(siigo): split the billed party phone into calling code and national number"
```

**Revisión del bloque 2:** un revisor (Sonnet): reglas de 10 dígitos, ausencia de `indicative` (no `null`), `ToString` sin datos, región normalizada.

**Revisión final (fuerte, toda la rama):** una sola, modelo más capaz, contra el spec. Luego el handoff al owner: «Pruebas escritas, no corridas: corre `.\scripts\test-feature.ps1` antes de mergear a `develop`. El backend se despliega antes de la entrega 2 del frontend. `docs/estado.md` se actualiza en `develop` al mergear. Seguimiento: confirmar con el tenant que su ERP acepta `+57…` en el Excel de pedidos (decisión del owner 1)».
