# Cliente desde un contacto compartido (backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que cada teléfono con `waId` de una tarjeta de contacto compartido viaje en la página de mensajes con el cliente activo y no fusionado que tiene ese número (`customer`) y con el número partido en país y número nacional (`country`, `nationalNumber`).

**Architecture:** Customers gana dos puertos: una coincidencia por lote que filtra activos y no fusionados **antes** de elegir el más antiguo (`ICustomerPhoneDirectory.MatchActiveAsync`) y un partidor de E.164 sobre libphonenumber (`IPhoneNumberSplitter`). Messaging los ve por un método por lote **NUEVO** de `IMessagingCustomerDirectory` (adaptador en `src/Bootstrapper/MessagingCustomerDirectory.cs`). `ListMessagesHandler` junta los `waId` de las tarjetas de la página, llama una sola vez y le pasa a `MessageMapping.ToDto` un diccionario, como ya hace con miembros, chatbots y clientes.

**Tech Stack:** .NET 10, EF Core + Npgsql, libphonenumber-csharp 9.0.41, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-10-cliente-desde-contacto-compartido-backend-design.md` (manda el contrato; el frontend lo consume).

## Global Constraints

- Trabajo en el worktree `qep-backend-worktrees\cliente-desde-contacto`, rama `feature/cliente-desde-contacto`. Antes de cada commit: `git branch --show-current` **en ese momento**; si dice `main`, frenar.
- TDD obligatorio: RED antes que GREEN, con la salida literal de ambos pegada en el reporte de la tarea.
- Comandos para el developer en PowerShell: sin `&&` (`A; if ($?) { B }`), `curl.exe`, variables con `$env:VAR = "valor"` en línea aparte.
- Por tarea se corren **sólo las clases unitarias tocadas**, en primer plano: `dotnet test <proyecto> --filter "FullyQualifiedName~<Clase>"`. Nunca en background.
- Las pruebas de integración (Testcontainers, un Postgres por clase) se **escriben y compilan** en su tarea y se **corren una sola vez** en la Task 6, por proyecto, una a la vez, con `$env:Seed__Enabled = "false"` y `-- xUnit.MaxParallelThreads=4`, filtradas a las clases tocadas.
- `dotnet build` corre con `TreatWarningsAsErrors`: todo log nuevo usa `[LoggerMessage]` (CA1873 rompe el build con un `logger.LogX` con argumentos).
- Si `Api.exe` (o `dotnet Api.dll`) bloquea archivos (`MSB3021`), **no** se detiene el proceso: se para y se reporta.
- Capas: Messaging no referencia Customers; los puertos de Messaging viven en `Modules.Messaging.Application` y sus adaptadores en `src/Bootstrapper`. `Modules.Customers.Application` no referencia EF Core ni libphonenumber. Lo verifican `MessagingLayerTests`, `CustomersLayerTests` y `CompositionRootTests` (`ValidateOnBuild`).
- Contrato, literal del spec: `SharedContactPhoneDto(string? Phone, string? WaId, string? Type, CustomerRefDto? Customer, string? Country, string? NationalNumber)`. Los tres nuevos son nullable, van al final y se calculan **sólo desde `waId`**; sin `waId` viajan `null`.
- `customer` = entre los clientes con `is_active = true` y `merged_into_id IS NULL` cuyo `phone_e164` es `"+" + waId`, el más antiguo por `(created_at, id)`. Se filtra **antes** de elegir. Un fusionado **no** se cambia por su sobreviviente.
- `country` = región ISO alfa-2 y `nationalNumber` = número nacional significativo en dígitos, de `"+" + waId` con libphonenumber; si el número no es válido para ninguna región, los dos `null`.
- La coincidencia se calcula al leer; nada se guarda. Sin migración.
- Commits: conventional commits, **sin** `Co-Authored-By` ni atribución de IA (aunque el harness lo pida). Los planes no se commitean.
- `docs/estado.md` **no** se toca en esta rama.
- Comentarios de código y documentación en español colombiano tuteando; identificadores en inglés.

## Review Focus

1. **Un número que sólo tiene un cliente inactivo viejo y uno activo nuevo**: tiene que salir el activo. Si el filtro de activos se aplicara después de elegir el más antiguo, la tarjeta ofrecería «Agregar como contacto» para siempre y cada clic crearía otro duplicado. Lo amarra `AnOldInactiveAndANewActiveCarryTheActive` (Task 5) y `MatchActiveFiltersBeforePickingTheOldest` (Task 1).
2. **Un `waId` que no es un número real** (dígitos sin indicativo, `3001234567`, que libphonenumber lee como +30): la página no puede fallar; `country` y `nationalNumber` salen `null` y la burbuja sigue mostrando el número. Lo amarran `AnInvalidNumberIsNotSplit` (Task 2) y `ACardPhoneWithAnInvalidWaIdHasNoParts` (Task 4).
3. **Un número italiano o de otra región con cero nacional significativo** (`+390612345678`): el número nacional tiene que conservar el `0`, o el formulario del frontend arma otro E.164 y `StartConversation` le escribe a otro número. Lo amarra un caso de `SplitsAValidNumberIntoRegionAndNationalDigits` (Task 2).
4. **Una página con varias tarjetas y números repetidos**: una sola llamada al puerto, con los `waId` sin repetir, y ninguna llamada si la página no tiene tarjetas. Lo amarra `ThePageResolvesEveryCardInOneCall` (Task 5).
5. **El envío saliente y la búsqueda** (`OutboundGuards.ToDtoAsync`, `SearchMessages`) llaman `ToDto` sin el diccionario: los tres campos deben salir `null` sin fallar. Lo amarra `WithoutTheDictionaryTheNewFieldsAreNull` (Task 4).

---

## File Structure

| Archivo | Cambio | Responsabilidad |
| --- | --- | --- |
| `src/Modules/Customers/Modules.Customers.Application/ICustomerPhoneDirectory.cs` | Modify | Método por lote `MatchActiveAsync` |
| `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerPhoneDirectory.cs` | Modify | Su consulta (una sola) |
| `src/Modules/Customers/Modules.Customers.Application/IPhoneNumberSplitter.cs` | Create | Puerto `IPhoneNumberSplitter` + record `PhoneNumberParts` |
| `src/Modules/Customers/Modules.Customers.Infrastructure/Phones/LibPhoneNumberNormalizer.cs` | Modify | Implementa también `IPhoneNumberSplitter` |
| `src/Modules/Customers/Modules.Customers.Infrastructure/CustomersInfrastructureExtensions.cs` | Modify | Registro del partidor |
| `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs` | Modify | Record `MessagingSharedPhone` + método `ResolveSharedPhonesAsync` |
| `src/Bootstrapper/MessagingCustomerDirectory.cs` | Modify | Adaptador del método nuevo |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs` | Modify | Tres campos nuevos en `SharedContactPhoneDto` |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs` | Modify | `MessageMapping.SharedContactWaIdsOf` y el parámetro nuevo de `ToDto` |
| `src/Modules/Messaging/Modules.Messaging.Application/ListMessages.cs` | Modify | Una llamada por página |
| Pruebas | Modify/Create | Ver cada tarea |

---

### Task 1: Customers — coincidencia por lote de activos y no fusionados

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerPhoneDirectory.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerPhoneDirectory.cs`
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingCustomerDirectoryAudienceTests.cs` (sólo el doble `FakePhones`, para que compile)
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerPhoneDirectoryTests.cs`

**Interfaces:**
- Produces: en `ICustomerPhoneDirectory`,
  `Task<IReadOnlyDictionary<string, CustomerPhoneMatch>> MatchActiveAsync(Guid tenantId, IReadOnlyCollection<string> e164, CancellationToken cancellationToken);`
  Claves: el E.164 con «+» tal como se pidió; un número sin coincidencia no aparece. `CustomerPhoneMatch` es el record existente `(Guid Id, string Name, bool IsComplete)`.

- [ ] **Step 1: Escribir las pruebas de integración (fallan al compilar)**

En `CustomerPhoneDirectoryTests`, dos pruebas nuevas, con los helpers que ya tiene la clase (`CreateWithPhoneAsync`, `MarkMergedAsync`, `ExecuteAsync`, `CreateOtherTenantManager`) y un helper privado `MatchActiveAsync(QepApiFactory, IReadOnlyCollection<string>)` calcado de `MatchAsync` (línea 250):

```csharp
// Spec 2026-11-10: se filtra antes de elegir; un inactivo viejo no esconde al activo.
[Fact]
public async Task MatchActiveFiltersBeforePickingTheOldest()
{
    // oldInactive (300 123 4567), newActive (300 123 4567), onlyInactive (300 555 0000),
    // onlyMerged (300 777 0000, fusionado en survivor con 300 999 9999 y luego is_active = true otra vez,
    // para que lo descarte el filtro de fusionados y no el de activos).
    // UPDATE customers.customers SET is_active = false WHERE id = ANY(@ids)  -- oldInactive, onlyInactive
    // UPDATE customers.customers SET created_at = created_at - interval '1 day' WHERE id = @oldInactive
    var matches = await MatchActiveAsync(factory, ["+573001234567", "+573005550000", "+573007770000"]);

    Assert.Equal(new CustomerPhoneMatch(newActive.Id, "Nuevo Activo", true), Assert.Single(matches).Value);
    Assert.Equal("+573001234567", Assert.Single(matches).Key);
}

[Fact]
public async Task MatchActiveStaysWithinTheTenantAndAnswersNothingForNothing()
{
    // Un cliente activo con 300 123 4567 en OtherTenantId (CreateOtherTenantManager), ninguno en el mío.
    Assert.Empty(await MatchActiveAsync(factory, ["+573001234567"]));
    Assert.Empty(await MatchActiveAsync(factory, []));
}
```

- [ ] **Step 2: Verificar el RED**

Run: `dotnet build tests/Modules/Customers/Modules.Customers.IntegrationTests`
Expected: FAIL con `CS1061: 'ICustomerPhoneDirectory' does not contain a definition for 'MatchActiveAsync'`. (Estas pruebas se corren en la Task 6.)

- [ ] **Step 3: Implementar `MatchActiveAsync` en `CustomerPhoneDirectory`**

Una sola consulta: `Where(TenantId == tenantId && PhoneE164 != null && phones.Contains(PhoneE164) && IsActive && MergedIntoId == null)`, `OrderBy(CreatedAt).ThenBy(Id)`, proyección `{ PhoneE164, Id, Name, Completeness }` y `TryAdd` por número (el primero gana, como D-M7 en `MatchAsync`). Colección vacía → diccionario vacío sin consultar. Sin segunda consulta de sobrevivientes: a diferencia de `MatchAsync`, un fusionado **no** se cambia por el que quedó (spec, «Contrato»). La `<summary>` del método en el puerto dice las dos diferencias con `MatchAsync` y por qué (un fusionado puede tener otro teléfono; `StartConversation` escribe al teléfono del cliente).

En `MessagingCustomerDirectoryAudienceTests.FakePhones`, agregar el miembro con `throw new NotSupportedException()` (la Task 3 lo reemplaza).

- [ ] **Step 4: Verificar el GREEN de compilación**

Run: `dotnet build tests/Modules/Customers/Modules.Customers.IntegrationTests; if ($?) { dotnet build tests/Bootstrapper/Bootstrapper.UnitTests }`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` en los dos.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Customers/Modules.Customers.Application/ICustomerPhoneDirectory.cs src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerPhoneDirectory.cs tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerPhoneDirectoryTests.cs tests/Bootstrapper/Bootstrapper.UnitTests/MessagingCustomerDirectoryAudienceTests.cs
git commit -m "feat(customers): match active, unmerged customers by phone in batch"
```

---

### Task 2: Customers — partir un E.164 en región y número nacional

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Application/IPhoneNumberSplitter.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Phones/LibPhoneNumberNormalizer.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/CustomersInfrastructureExtensions.cs:54`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/LibPhoneNumberNormalizerTests.cs`

**Interfaces:**
- Produces (namespace `Modules.Customers.Application`):
  `public sealed record PhoneNumberParts(string Country, string NationalNumber);`
  `public interface IPhoneNumberSplitter { PhoneNumberParts? Split(string? e164); }`
  `LibPhoneNumberNormalizer : IPhoneNumberNormalizer, IPhoneNumberSplitter`; registrado con `services.AddSingleton<IPhoneNumberSplitter, LibPhoneNumberNormalizer>();` junto al normalizador.

Decisión del plan: el spec dice «el normalizador de Customers», pero `IPhoneNumberNormalizer` (Domain) tiene doce dobles de prueba que no necesitan partir números. Un puerto aparte en Application evita tocarlos y deja la implementación en la misma clase de libphonenumber.

- [ ] **Step 1: Escribir las pruebas que fallan**

```csharp
[Theory]
[InlineData("+573001234567", "CO", "3001234567")]
[InlineData("+14155552671", "US", "4155552671")]
[InlineData("+34612345678", "ES", "612345678")]
[InlineData("+390612345678", "IT", "0612345678")] // el cero italiano es parte del número nacional
public void SplitsAValidNumberIntoRegionAndNationalDigits(string e164, string country, string national) =>
    Assert.Equal(new PhoneNumberParts(country, national), _normalizer.Split(e164));

[Theory]
[InlineData("+3001234567")] // un wa_id sin indicativo: +30 (Grecia) no existe
[InlineData("+57123")]
[InlineData("+")]
[InlineData("+abc")]
[InlineData("")]
[InlineData(null)]
public void AnInvalidNumberIsNotSplit(string? e164) => Assert.Null(_normalizer.Split(e164));
```

- [ ] **Step 2: Verificar el RED**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~LibPhoneNumberNormalizerTests"`
Expected: FAIL de compilación, `CS1061: 'LibPhoneNumberNormalizer' does not contain a definition for 'Split'`.

- [ ] **Step 3: Implementar `PhoneNumberParts? Split(string? e164)` en `LibPhoneNumberNormalizer`**

`Util.Parse(e164.Trim(), UnknownRegion)`; si `!Util.IsValidNumber(parsed)` → `null`; región con `Util.GetRegionCodeForNumber(parsed)` (vacía o `ZZ` → `null`); número nacional con `Util.GetNationalSignificantNumber(parsed)`. `NumberParseException` → `null`, nunca lanza. Registro en `CustomersInfrastructureExtensions` al lado de `IPhoneNumberNormalizer`.

- [ ] **Step 4: Verificar el GREEN**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~LibPhoneNumberNormalizerTests"`
Expected: PASS, todas las de la clase (las viejas y las 10 nuevas).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Customers/Modules.Customers.Application/IPhoneNumberSplitter.cs src/Modules/Customers/Modules.Customers.Infrastructure/Phones/LibPhoneNumberNormalizer.cs src/Modules/Customers/Modules.Customers.Infrastructure/CustomersInfrastructureExtensions.cs tests/Modules/Customers/Modules.Customers.UnitTests/LibPhoneNumberNormalizerTests.cs
git commit -m "feat(customers): split an e164 number into region and national digits"
```

---

### Task 3: Messaging — puerto por lote y adaptador en Bootstrapper

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs`
- Modify: `src/Bootstrapper/MessagingCustomerDirectory.cs`
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateTestDoubles.cs` (`FakeCustomerDirectory`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingCustomerDirectoryAudienceTests.cs`

**Interfaces:**
- Consumes: `ICustomerPhoneDirectory.MatchActiveAsync` (Task 1), `IPhoneNumberSplitter.Split` (Task 2).
- Produces (namespace `Modules.Messaging.Application`):
  `public sealed record MessagingSharedPhone(CustomerRefDto? Customer, string? Country, string? NationalNumber);`
  en `IMessagingCustomerDirectory`:
  `Task<IReadOnlyDictionary<string, MessagingSharedPhone>> ResolveSharedPhonesAsync(Guid tenantId, IReadOnlyCollection<string> waIds, CancellationToken cancellationToken);`
  Claves: cada `waId` pedido (dígitos, sin «+»), sin repetir, **siempre presente** aunque los tres valores sean `null`. Una sola llamada a Customers por invocación; con la colección vacía no llama.
- `MessagingCustomerDirectory` gana el parámetro de constructor `IPhoneNumberSplitter splitter` (después de `phones`).

- [ ] **Step 1: Escribir las pruebas que fallan**

`FakePhones` gana `public Dictionary<string, CustomerPhoneMatch> Active { get; } = [];` y `public List<IReadOnlyCollection<string>> ActiveCalls { get; } = [];`; su `MatchActiveAsync` registra la llamada y devuelve las entradas de `Active` cuyas claves se pidieron. `AdapterOver` pasa `new LibPhoneNumberNormalizer()` como partidor (Bootstrapper.UnitTests ya ve los internos de Customers.Infrastructure, como `DialablePhoneNumbersTests`).

```csharp
[Fact]
public async Task ResolveSharedPhonesAsksCustomersOnceWithThePlusAndSplitsEveryNumber()
{
    var laura = new CustomerPhoneMatch(Guid.NewGuid(), "Laura Gómez", false);
    var phones = new FakePhones { Active = { ["+573001234567"] = laura } };

    var resolved = await AdapterOver(new FakeWhatsApp(), phones: phones)
        .ResolveSharedPhonesAsync(Tenant, ["573001234567", "14155552671", "573001234567"], TestContext.Current.CancellationToken);

    Assert.Equal(["+573001234567", "+14155552671"], Assert.Single(phones.ActiveCalls));
    Assert.Equal(new MessagingSharedPhone(new CustomerRefDto(laura.Id, "Laura Gómez", false), "CO", "3001234567"), resolved["573001234567"]);
    Assert.Equal(new MessagingSharedPhone(null, "US", "4155552671"), resolved["14155552671"]);
}

[Fact]
public async Task AnInvalidWaIdIsStillAnsweredWithoutParts()
{
    var resolved = await AdapterOver(new FakeWhatsApp(), phones: new FakePhones())
        .ResolveSharedPhonesAsync(Tenant, ["3001234567"], TestContext.Current.CancellationToken);

    Assert.Equal(new MessagingSharedPhone(null, null, null), resolved["3001234567"]);
}

[Fact]
public async Task NoWaIdsAskNothing()
{
    var phones = new FakePhones();
    Assert.Empty(await AdapterOver(new FakeWhatsApp(), phones: phones).ResolveSharedPhonesAsync(Tenant, [], TestContext.Current.CancellationToken));
    Assert.Empty(phones.ActiveCalls);
}
```

- [ ] **Step 2: Verificar el RED**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~MessagingCustomerDirectoryAudienceTests"`
Expected: FAIL de compilación, `CS1061: 'MessagingCustomerDirectory' does not contain a definition for 'ResolveSharedPhonesAsync'` (y el constructor con seis argumentos).

- [ ] **Step 3: Implementar**

- El record y el método en `IMessagingCustomerDirectory.cs`, con `<summary>` que remite al spec 2026-11-10 y dice que la regla (activos y no fusionados, filtrar antes de elegir, sin seguir la fusión) **no** es la de `MatchAsync`, que la bandeja sigue usando.
- En el adaptador: `Distinct` de los `waId`, una llamada a `phones.MatchActiveAsync` con `"+" + waId`, y por cada `waId` un `MessagingSharedPhone(customer, parts?.Country, parts?.NationalNumber)` con `parts = splitter.Split("+" + waId)`.
- `FakeCustomerDirectory` (Messaging.UnitTests): `ResolveSharedPhonesAsync` devuelve un diccionario vacío.

- [ ] **Step 4: Verificar el GREEN y las capas**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~MessagingCustomerDirectoryAudienceTests"`
Expected: PASS (todas, viejas y 3 nuevas).

Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~CustomersLayerTests|FullyQualifiedName~CompositionRootTests"`
Expected: PASS. Si `CompositionRootTests` falla con `Unable to resolve service for type 'IPhoneNumberSplitter'`, falta el registro de la Task 2.

Run: `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests`
Expected: `0 Warning(s) 0 Error(s)` (el doble compila).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Messaging/Modules.Messaging.Application/IMessagingCustomerDirectory.cs src/Bootstrapper/MessagingCustomerDirectory.cs tests/Bootstrapper/Bootstrapper.UnitTests/MessagingCustomerDirectoryAudienceTests.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/TemplateTestDoubles.cs
git commit -m "feat(messaging): resolve shared contact phones in one batch"
```

---

### Task 4: Messaging — el contrato y el mapeo de la tarjeta

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs:212-214`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs:207-256` (`ToDto`) y `:454-490` (`ContactsFrom`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageMappingContactsTests.cs`

**Interfaces:**
- Consumes: `MessagingSharedPhone` (Task 3).
- Produces:
  - `public sealed record SharedContactPhoneDto(string? Phone, string? WaId, string? Type, CustomerRefDto? Customer = null, string? Country = null, string? NationalNumber = null);` (los defaults mantienen compilando las construcciones de tres argumentos de las pruebas existentes).
  - `MessageMapping.ToDto(..., IReadOnlyDictionary<Guid, CustomerRefDto>? customerRefs = null, IReadOnlyDictionary<string, MessagingSharedPhone>? sharedPhones = null)` — parámetro opcional **al final**: `OutboundGuards.cs:223` y `SearchMessages.cs:104` no cambian.
  - `public static IReadOnlyCollection<string> SharedContactWaIdsOf(IEnumerable<MessageRow> rows)` en `MessageMapping`: los `wa_id` de los `phones` de toda fila `Kind == Contacts`, sin repetir; un `details` ilegible no aporta nada y no lanza.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `MessageMappingContactsTests` (la constante `MetaContacts` ya trae `573001234567` con `wa_id` y `+57 604 444 5566` sin él):

```csharp
private static readonly CustomerRefDto Laura = new(Guid.CreateVersion7(), "Laura Gómez", false);

[Fact]
public void APhoneThatMatchesCarriesTheCustomerAndItsParts()
{
    var shared = new Dictionary<string, MessagingSharedPhone> { ["573001234567"] = new(Laura, "CO", "3001234567") };
    var phones = MessageMapping.ToDto(Row(MessageKind.Contacts, MetaContacts), TenantId, Names, NoTargets, sharedPhones: shared).Contacts![0].Phones;

    Assert.Equal(new SharedContactPhoneDto("+57 300 123 4567", "573001234567", "CELL", Laura, "CO", "3001234567"), phones[0]);
    Assert.Equal(new SharedContactPhoneDto("+57 604 444 5566", null, "WORK"), phones[1]); // sin waId: los tres null
}

[Fact]
public void APhoneWithoutMatchCarriesOnlyItsParts() { /* entrada (null, "CO", "3001234567") → Customer null, Country "CO", NationalNumber "3001234567" */ }

[Fact]
public void ACardPhoneWithAnInvalidWaIdHasNoParts() { /* details con wa_id "3001234567" y entrada (null, null, null) → los tres null, Phone y WaId intactos */ }

[Fact]
public void WithoutTheDictionaryTheNewFieldsAreNull() { /* ToDto sin sharedPhones → phones[0] == new SharedContactPhoneDto("+57 300 123 4567", "573001234567", "CELL") */ }

[Fact]
public void SharedContactWaIdsOfCollectsEveryCardOfThePageOnce()
{
    var rows = new[]
    {
        Row(MessageKind.Contacts, MetaContacts),
        Row(MessageKind.Contacts, """[{ "phones": [{ "wa_id": "573001234567" }, { "wa_id": "14155552671" }] }]"""),
        Row(MessageKind.Text, MetaContacts), // no es Contacts: no cuenta
        Row(MessageKind.Contacts, "{not json"),
    };

    Assert.Equal(["573001234567", "14155552671"], MessageMapping.SharedContactWaIdsOf(rows));
}
```

- [ ] **Step 2: Verificar el RED**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessageMappingContactsTests"`
Expected: FAIL de compilación (`sharedPhones` no existe; `SharedContactPhoneDto` no tiene seis parámetros; `SharedContactWaIdsOf` no existe).

- [ ] **Step 3: Implementar**

- DTO con los tres campos y, en la `<summary>`, el motivo BFF del spec: el formulario de cliente pide país y número nacional por separado (`src/lib/phone.ts` del frontend sólo acepta dígitos); prellenar el `waId` entero con país `CO` deja `PhoneE164 = null` y `StartConversation` falla con `422 messaging.customer.unreachable`. Y que `customer` sale de la regla de `ResolveSharedPhonesAsync`.
- `ContactsFrom(detailsJson, sharedPhones)`: por cada teléfono con `waId`, `sharedPhones?.GetValueOrDefault(waId)` llena los tres; sin `waId` o sin entrada, `null`.
- `SharedContactWaIdsOf`: recorre con el mismo `JsonDocument` + `ObjectsOf` que `ContactsFrom`; `JsonException` → la fila no aporta.

- [ ] **Step 4: Verificar el GREEN**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessageMappingContactsTests|FullyQualifiedName~MessageMappingTests"`
Expected: PASS (las viejas de contactos siguen verdes sin cambio: el DTO de tres argumentos es igual al de seis con `null`).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/MessageMappingContactsTests.cs
git commit -m "feat(messaging): carry customer and phone parts on shared contact phones"
```

---

### Task 5: Messaging — la página de mensajes resuelve todas las tarjetas en una llamada

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ListMessages.cs:57-64`
- Modify: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MetaPayloads.cs` (helper nuevo)
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/SharedContactCustomerApiTests.cs`

**Interfaces:**
- Consumes: `MessageMapping.SharedContactWaIdsOf`, el parámetro `sharedPhones` de `ToDto` (Task 4), `IMessagingCustomerDirectory.ResolveSharedPhonesAsync` (Task 3).
- Produces: `MetaPayloads.InboundContacts(string phoneNumberId, string waId, string wamid, long timestamp, string contactsJson)` — un entrante `type = "contacts"` con `from_user_id = UserIdFor(waId)`, `from = waId` y `message["contacts"] = JsonNode.Parse(contactsJson)`; el resto como `Inbound` (perfil «Laura», metadata `"15550000000"`).

- [ ] **Step 1: Escribir las pruebas de integración (fallan al compilar)**

`SharedContactCustomerApiTests`, con `ArrangeAsync` calcado de `ReplyToApiTests` (tenant, `EnableMessagingAsync`, conexión `"111"`): el remitente es `573110000000` (su cliente incompleto lo crea la ingesta y no interfiere) y la tarjeta trae `{"name":{"formatted_name":"Laura Gómez"},"phones":[{"phone":"+57 300 123 4567","wa_id":"573001234567","type":"CELL"}]}`. Cada prueba postea con `PostWebhookAsync` + `DrainDeliveriesAsync`, lee `GET MessagesUrl(...)` con `ReadPermissions` y mira `items[last].contacts[0].phones[0]` con `JsonDocument`. Los clientes se crean con `CreateCustomerAsync`/`CreateCustomersAsync` del harness (teléfono `"3001234567"`, país `CO`); inactivo y fusionado se marcan por SQL con `ExecuteAsync`.

```csharp
[Fact] public async Task ACardWhoseNumberIsAnActiveCustomerCarriesTheCustomer()
// customer.id == el creado, customer.name == "Laura Gómez", country == "CO", nationalNumber == "3001234567"

[Fact] public async Task ANumberOnlyAnInactiveCustomerHasCarriesNoCustomer()
// UPDATE customers.customers SET is_active = false WHERE id = @id → customer es null; country y nationalNumber siguen llenos

[Fact] public async Task ANumberOnlyAMergedCustomerHasCarriesNoCustomer()
// dos clientes: el de 3001234567 y otro con 3009999999; UPDATE … SET merged_into_id = @otro, merged_at = now() WHERE id = @primero
// (is_active se deja en true: aísla el filtro de fusionados) → customer es null, no el sobreviviente

[Fact] public async Task AnOldInactiveAndANewActiveCarryTheActive()
// CreateCustomersAsync(("Viejo", "3001234567"), ("Nuevo", "3001234567")); el viejo inactivo y con created_at - 1 día → customer.id == Nuevo

[Fact] public async Task AfterCreatingTheCustomerTheNextReadCarriesIt()
// primera lectura: customer null; CreateCustomerAsync(..., "Laura Gómez", "3001234567"); segunda lectura: customer.id == el nuevo

[Fact] public async Task ThePageResolvesEveryCardInOneCall()
// host con IMessagingCustomerDirectory envuelto en un DispatchProxy contador (patrón de
// AutomationDataConsentsAdapterTests.FailingEnsureDirectory: Single(descriptor) + ActivatorUtilities.CreateInstance):
// cuenta las llamadas a ResolveSharedPhonesAsync y guarda los waIds.
// 1) sólo el texto del ArrangeAsync → leer → 0 llamadas.
// 2) dos tarjetas: [573001234567, 14155552671] y [573001234567, 34612345678] → leer → 1 llamada,
//    waIds == {573001234567, 14155552671, 34612345678} (sin repetir).
```

- [ ] **Step 2: Verificar el RED**

Run: `dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests`
Expected: FAIL, `CS0117: 'MetaPayloads' does not contain a definition for 'InboundContacts'`. Con el helper escrito, compila; las pruebas se corren en la Task 6 y, sin el cambio del handler, las de `customer` fallarían con `customer` en `null`.

- [ ] **Step 3: Implementar en `ListMessagesHandler.HandleAsync`**

Después de `customerRefs`: `var waIds = MessageMapping.SharedContactWaIdsOf(page);` y, sólo si `waIds.Count > 0`, `await customers.ResolveSharedPhonesAsync(query.TenantId, waIds, cancellationToken)`; se pasa como `sharedPhones:` a cada `ToDto`. Sin `try/catch`: una falla de base rompe la página igual que hoy `FindRefsAsync`; lo que el spec protege es el teléfono individual, y eso ya lo cubren el partidor (nunca lanza) y la entrada vacía. `OutboundGuards` y `SearchMessages` no cambian: un saliente de QEP nunca es `Contacts` (sólo lo escribe `WebhookPayloadParser`), y la búsqueda está fuera de alcance; los dos quedan con los campos en `null` (Task 4, `WithoutTheDictionaryTheNewFieldsAreNull`).

- [ ] **Step 4: Verificar el GREEN de compilación y las unitarias de Messaging tocadas**

Run: `dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests`
Expected: `0 Warning(s) 0 Error(s)`.

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessageMappingContactsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Messaging/Modules.Messaging.Application/ListMessages.cs tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MetaPayloads.cs tests/Modules/Messaging/Modules.Messaging.IntegrationTests/SharedContactCustomerApiTests.cs
git commit -m "feat(messaging): resolve shared contact customers on the message page"
```

---

### Task 6: Verificación final (integración una vez, por proyecto)

**Files:** ninguno nuevo; sólo se corrigen fallas que aparezcan, con su propio ciclo RED/GREEN.

- [ ] **Step 1: Build completo**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. Si falla por `MSB3021` (archivo bloqueado por `Api.exe`), parar y reportar; no detener el proceso.

- [ ] **Step 2: Integración de Customers**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerPhoneDirectoryTests" -- xUnit.MaxParallelThreads=4
```
Expected: PASS, incluidas `MatchActiveFiltersBeforePickingTheOldest` y `MatchActiveStaysWithinTheTenantAndAnswersNothingForNothing`.

- [ ] **Step 3: Integración de Messaging (después de que termine la anterior)**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~SharedContactCustomerApiTests|FullyQualifiedName~MessageSearchApiTests|FullyQualifiedName~ReplyToApiTests" -- xUnit.MaxParallelThreads=4
```
Expected: PASS. `MessageSearchApiTests` y `ReplyToApiTests` confirman que la búsqueda y el envío saliente (los otros dos llamadores de `ToDto`) siguen igual.

- [ ] **Step 4: Unitarias tocadas, juntas**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~LibPhoneNumberNormalizerTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~MessagingCustomerDirectoryAudienceTests|FullyQualifiedName~DialablePhoneNumbersTests"
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~MessageMapping"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~CustomersLayerTests|FullyQualifiedName~CompositionRootTests"
```
Expected: PASS en las cuatro.

- [ ] **Step 5: Revisar el árbol y la rama**

Run: `git branch --show-current; git status --short`
Expected: `feature/cliente-desde-contacto` y ningún cambio sin commitear salvo los planes (que no se commitean). `docs/estado.md` sin tocar.
