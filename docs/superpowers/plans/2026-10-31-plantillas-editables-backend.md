# Plantillas editables: backend. Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que las plantillas de cotización y de cobro se creen con un texto editado por el tenant, y que su envío y la bandeja salgan de la plantilla aprobada de cada conexión.

**Architecture:** `components` de `messaging.message_templates` ya guarda el texto, así que no hay migración. El envío deja de usar constantes: lee variables y texto de la fila aprobada con `TemplateCatalog.FromStored`. La lista cerrada de variables vive en `SystemTemplateVariables`, y las definiciones sólo conservan nombre, texto base y el orden de las filas viejas.

**Tech Stack:** .NET 10, FluentValidation, EF Core, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-31-plantillas-base-editables-design.md`

## Global Constraints

- Worktree: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\plantillas-editables`, rama `feature/plantillas-editables`. Rutas absolutas o `Set-Location` a ese worktree en cada llamada: el cwd se resetea al checkout principal, que no se toca.
- TDD: RED y GREEN con evidencia literal en cada tarea.
- Por tarea sólo las clases tocadas (`--filter`). La suite completa, una vez, en la Task 9.
- Conventional commits, sin `Co-Authored-By` ni atribución de IA, aunque el harness lo sugiera.
- Comentarios y textos para personas en español colombiano, tuteando. Contratos en inglés sin traducir.
- Archivos sin BOM. Nunca `Set-Content` para reescribir un `.cs`: rompe las tildes.
- Regla del owner: variables siempre con nombre. Toda plantilla nueva sale `NAMED`; `POSITIONAL` sólo se lee.
- Las plantillas nuevas de cotización no llevan pie. Una fila existente conserva el suyo.
- Regla del owner: un solo nombre por variable, en español. Las fijas son `nombre_contacto`, `asesor`, `empresa` y `saludo`, los de `FIXED_VARIABLE_NAMES` del frontend. `fullname`, `order_number`, `total` y `valid_until` no se ofrecen ni pasan el validador, pero las filas viejas los llevan y el envío los sigue llenando.
- Automatizaciones renombra `{nombre}` a `{nombre_contacto}` sin alias ni migración (owner, 2026-10-31: los flujos no están en producción). Antes de desplegar se confirma que ningún flujo publicado en producción use `{nombre}`: una versión publicada no se revalida y saldría vacío (Task 9, Step 4).
- Pruebas de integración: `$env:Seed__Enabled = "false"` en línea aparte.
- `Api.exe` o `dotnet Api.dll` corriendo bloquea el build: detenlo primero.
- No tocar `docs/estado.md` en esta rama.

## Review Focus

1. Meta no devuelve `parameter_format` en el listado: el refresco deja `POSITIONAL` en una fila con claves con nombre, y el envío tiene que seguir nombrando las variables (hoy está fijo en `Named: true`). Prueba en Task 2.
2. El nombre visible del asesor tiene un salto de línea o está vacío: el mensaje sale con el nombre de la empresa, no falla. Prueba en Task 2.
3. Una plantilla sin variables: sale el PDF en el encabezado y ningún parámetro de cuerpo. Prueba en Task 3.
4. Una cotización ya aprobada lleva `fullname`, `order_number`, `total` y `valid_until`, que salieron de la lista: el envío los llena con el valor de su clave en español, y una `External` con ese texto se sigue adoptando. Pruebas en Tasks 2 y 5.
5. Meta rechaza el vencido después de aceptar el recordatorio y el admin reintenta: el recordatorio no se vuelve a crear ni queda huérfano. Prueba en Task 6.

---

### Task 1: Lista cerrada de variables y regla del cuerpo

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/SystemTemplateVariables.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/TemplateBodyRules.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagement.cs` (`CreateTemplateCommandValidator`, `TemplateMessages`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/SystemTemplateVariablesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record SystemTemplateVariable(string Key, string Label, string Example);`
  - `public static class SystemTemplateVariables` con `Quotation`, `BillingReminder`, `BillingOverdue` (`IReadOnlyList<SystemTemplateVariable>`), constantes `ContactName = "nombre_contacto"`, `Advisor = "asesor"`, `Company = "empresa"`, `Greeting = "saludo"`, `IReadOnlyDictionary<string, string> LegacyKeys` (`fullname` → `nombre_contacto`, `order_number` → `numero_cotizacion`, `total` → `valor_total`, `valid_until` → `vigencia`) y `IReadOnlySet<string> KeysOf(IReadOnlyList<SystemTemplateVariable> variables)`.
  - `internal static class TemplateBodyRules` con `string? Check(string? body, IReadOnlySet<string>? allowedKeys = null)`: el mensaje de error o `null`.
  - `TemplateMessages.BodyVariableNotAllowed = "Usa sólo las variables de la lista de esta plantilla."`

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact]
public void EachKindHasItsClosedListInOrder()
{
    string[] fixedKeys = ["nombre_contacto", "asesor", "empresa", "saludo"];
    Assert.Equal([.. fixedKeys, "numero_cotizacion", "valor_total", "vigencia"], SystemTemplateVariables.Quotation.Select(v => v.Key));
    Assert.Equal([.. fixedKeys, "servicio", "valor", "vencimiento"], SystemTemplateVariables.BillingReminder.Select(v => v.Key));
    Assert.Equal([.. fixedKeys, "cantidad_cobros", "total_vencido"], SystemTemplateVariables.BillingOverdue.Select(v => v.Key));
    Assert.Equal(
        ["Nombre del contacto", "Nombre del asesor", "Nombre de la empresa", "Saludo según la hora", "Número de cotización", "Valor total", "Vigencia"],
        SystemTemplateVariables.Quotation.Select(v => v.Label));
    Assert.Empty(SystemTemplateVariables.KeysOf(SystemTemplateVariables.Quotation).Intersect(SystemTemplateVariables.LegacyKeys.Keys));
    Assert.All(SystemTemplateVariables.LegacyKeys.Values, key => Assert.Contains(key, SystemTemplateVariables.KeysOf(SystemTemplateVariables.Quotation)));
}

[Theory]
[InlineData("Hola {{nombre_contacto}}, gracias.", null)]
[InlineData("Hola {{otra}}, gracias.", TemplateMessages.BodyVariableNotAllowed)]
[InlineData("Hola {{ nombre_contacto }}, gracias.", TemplateMessages.BodyVariable)]
[InlineData("{{nombre_contacto}}", TemplateMessages.Body)]
[InlineData("   ", TemplateMessages.Body)]
public void TheBodyRuleChecksShapeThenTheList(string body, string? expected) =>
    Assert.Equal(expected, TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(SystemTemplateVariables.Quotation)));

[Fact]
public void WithoutAListAnyWellFormedVariablePasses() => Assert.Null(TemplateBodyRules.Check("Hola {{otra}}, gracias."));

[Fact]
public void ABodyOverTheLimitIsRejected() =>
    Assert.Equal(TemplateMessages.Body, TemplateBodyRules.Check(new string('a', 1025)));
```

- [ ] **Step 2: Córrelas y comprueba que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SystemTemplateVariablesTests" --nologo`
Expected: error de compilación, `SystemTemplateVariables` no existe.

- [ ] **Step 3: Implementa las listas y la regla**

Etiquetas y ejemplos, en el orden de la prueba:

| Clave | Etiqueta | Ejemplo |
| --- | --- | --- |
| `nombre_contacto` | Nombre del contacto | María González |
| `asesor` | Nombre del asesor | Carlos Ramírez |
| `empresa` | Nombre de la empresa | Mi Empresa |
| `saludo` | Saludo según la hora | Buenos días |
| `numero_cotizacion` | Número de cotización | COT-000123 |
| `valor_total` | Valor total | 2.450.000 COP |
| `vigencia` | Vigencia | 30 de septiembre de 2026 |
| `servicio` | Servicio | Hosting plan pro |
| `valor` | Valor | 150.000 COP |
| `vencimiento` | Vencimiento | 10/03/2026 |
| `cantidad_cobros` | Cantidad de cobros | 2 |
| `total_vencido` | Total vencido | 450.000 COP |
Las cuatro fijas van primero en las tres listas, y su etiqueta y ejemplo son los de `FIXED_VARIABLE_DETAILS` del frontend (`src/components/whatsapp-templates/message-template.ts`). Comenta en `SystemTemplateVariables` que esa lista es la autoridad y que aquí viven copiadas porque son dos repos. Sin `$`, `#` ni `%` en los ejemplos: Meta los rechaza. Mueve `LooseBrace` y `StrictVariable` de `CreateTemplateCommandValidator` a `TemplateBodyRules`, y deja que el `Custom` de `Body` llame a `Check(body)` y agregue su mensaje con la clave `body`.

- [ ] **Step 4: Comprueba GREEN, incluida la validación de la creación libre**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SystemTemplateVariablesTests|FullyQualifiedName~CreateTemplate" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Messaging/Modules.Messaging.Application tests/Modules/Messaging/Modules.Messaging.UnitTests/SystemTemplateVariablesTests.cs
git commit -m "feat(messaging): add the closed variable lists of system templates"
```

### Task 2: Armar un envío desde la fila aprobada

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/SystemTemplateSend.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagementPorts.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/QuotationTemplateReadiness.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/MessagingInfrastructureExtensions.cs`
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationsWhatsAppChannelDirectoryTests.cs` y el doble de `IBillingTemplateReadiness` de `ServiceContractsMetaWhatsAppSenderTests.cs`: pasan a `ApprovedTemplate`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/SystemTemplateSendTests.cs`

**Interfaces:**
- Consumes: `SystemTemplateVariables` (Task 1), `TemplateCatalog.FromStored`, `TemplateCatalog.BuildSend`, `TemplateParameterRules.IsValid`, `ITenantClock.GetAsync`, `TimeOfDayGreeting.For`.
- Produces:
  - `public sealed record ApprovedTemplate(string Name, string Language, string Category, string ParameterFormat, string ComponentsJson);` Reemplaza a `ApprovedQuotationTemplate` y `ApprovedBillingTemplate`; `FindApprovedAsync` de los dos puertos la devuelve.
  - `public static class SystemTemplateSend` con `IReadOnlyList<string> VariablesOf(ApprovedTemplate template)` y `BuiltTemplate? Build(ApprovedTemplate template, IReadOnlyDictionary<string, string> values, IReadOnlyList<string>? legacyPositionalKeys = null)`.
  - `public sealed class TemplateSenderValues(IMessagingMemberNames memberNames, IMessagingTenantNames tenantNames, ITenantClock tenantClock)` con `Task AddAsync(Guid tenantId, Guid memberId, IReadOnlyCollection<string> usedKeys, IDictionary<string, string> values, CancellationToken cancellationToken)`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact]
public void SendsOnlyTheVariablesTheRowUsesInItsOrder()
{
    var built = SystemTemplateSend.Build(Row("NAMED", "Hola {{nombre_contacto}}, de {{empresa}}, tu total: {{valor_total}} hoy."), AllValues);
    Assert.True(built!.Send.Named);
    Assert.Equal(["nombre_contacto", "empresa", "valor_total"], built.Send.Parameters.Select(parameter => parameter.Key));
    Assert.Equal("Hola Laura, de Andina, tu total: 10 hoy.", built.Text);
    Assert.DoesNotContain("vigencia", built.DetailsJson);
}

[Fact]
public void ALegacyRowKeepsItsFooterInTheText()
{
    var built = SystemTemplateSend.Build(Row("NAMED", "Hola {{fullname}}, ya salió.", footer: "Este mensaje fue generado automáticamente."), AllValues);
    Assert.Equal("Hola Laura, ya salió.\n\nEste mensaje fue generado automáticamente.", built!.Text);
}

[Fact]
public void APositionalRowTakesItsValuesInTheLegacyOrder()
{
    var built = SystemTemplateSend.Build(Row("POSITIONAL", "Hola {{1}}, debes {{2}} hoy."), AllValues, ["nombre_contacto", "valor_total"]);
    Assert.False(built!.Send.Named);
    Assert.Equal([("1", "Laura"), ("2", "10")], built.Send.Parameters.Select(parameter => (parameter.Key, parameter.Value)));
}

// Review Focus 1: si Meta no devuelve parameter_format, el refresco guarda POSITIONAL.
[Fact]
public void NamedKeysAreNamedWhateverTheStoredFormatSays() =>
    Assert.True(SystemTemplateSend.Build(Row("POSITIONAL", "Hola {{nombre_contacto}}, ya salió."), AllValues)!.Send.Named);

[Fact]
public void AMissingOrInvalidValueBuildsNothing()
{
    Assert.Null(SystemTemplateSend.Build(Row("NAMED", "Hola {{servicio}}, ya salió."), AllValues));
    Assert.Null(SystemTemplateSend.Build(Row("NAMED", "Hola {{nombre_contacto}}, ya salió."), new Dictionary<string, string> { ["nombre_contacto"] = "a\nb" }));
}

[Fact]
public async Task TheAdvisorIsTheSendersDisplayName()
{
    var values = await AddAsync(displayName: "Ana Gomez", tenantName: "Andina", ["asesor", "empresa"]);
    Assert.Equal(("Ana Gomez", "Andina"), (values["asesor"], values["empresa"]));
}

// Review Focus 2.
[Theory]
[InlineData(null)]
[InlineData("   ")]
[InlineData("Ana\nGomez")]
public async Task AnAdvisorWithoutAUsableNameFallsBackToTheCompany(string? displayName) =>
    Assert.Equal("Andina", (await AddAsync(displayName, "Andina", ["asesor"]))["asesor"]);

[Fact]
public async Task NamesTheRowDoesNotUseAreNotLookedUp() => Assert.Empty(await AddAsync("Ana", "Andina", ["nombre_contacto"], failIfCalled: true));

// Review Focus 4.
[Fact]
public void AnOldRowKeepsItsFourKeysWithTheValuesOfTheSpanishOnes()
{
    var row = Row("NAMED", "¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*.");
    var built = SystemTemplateSend.Build(row, new Dictionary<string, string>
    {
        ["nombre_contacto"] = "Laura", ["numero_cotizacion"] = "COT-1", ["valor_total"] = "10", ["vigencia"] = "1 de enero",
    });
    Assert.Equal(
        [("fullname", "Laura"), ("order_number", "COT-1"), ("total", "10"), ("valid_until", "1 de enero")],
        built!.Send.Parameters.Select(parameter => (parameter.Key, parameter.Value)));
    Assert.Equal("¡Hola, Laura! Adjuntamos la cotización *COT-1* por *10*, vigente hasta el *1 de enero*.", built.Text);
}

[Theory]
[InlineData(9, "Buenos días")]
[InlineData(15, "Buenas tardes")]
[InlineData(20, "Buenas noches")]
public async Task TheGreetingFollowsTheTenantsLocalHour(int localHour, string expected) =>
    Assert.Equal(expected, (await AddAsync("Ana", "Andina", ["saludo"], localHour: localHour))["saludo"]);
```

`Row` arma un `ApprovedTemplate` con `Components(new TemplateDraft(...))` o JSON literal; `AllValues` trae `nombre_contacto = "Laura"`, `empresa = "Andina"`, `valor_total = "10"`. `AddAsync` usa un `ITenantClock` doble con esa hora local, en una zona distinta de UTC.

- [ ] **Step 2: Córrelas y comprueba que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SystemTemplateSendTests" --nologo`
Expected: error de compilación, `SystemTemplateSend` no existe.

- [ ] **Step 3: Implementa `SystemTemplateSend` y `TemplateSenderValues`**

- `Build`: `FromStored(...)`, y `named = Parameters.Any(key => !key.All(char.IsAsciiDigit))`. Si es con nombre, cada clave lee `values[LegacyKeys.GetValueOrDefault(key, key)]`: las claves viejas sólo sobreviven ahí, y un comentario dice por qué. `details` guarda la clave de la fila, no la española. Si no, `legacyPositionalKeys[int.Parse(key) - 1]`. Un valor ausente, una posición fuera de rango o un valor que no cumpla `TemplateParameterRules.IsValid` devuelve `null`. Llama a `BuildSend(described with { ParameterFormat = named ? TemplateCatalog.Named : TemplateCatalog.Positional }, resolved)` con el diccionario de las usadas, indexado por la clave de la fila, para que `details` sólo lleve esas.
- Comenta en el `named` por qué no lee la columna: el refresco guarda `POSITIONAL` si Meta no manda el campo.
- `TemplateSenderValues.AddAsync`: `empresa` sale de `FindAsync`, y `asesor` de `FindDisplayNameAsync`. Si el nombre es nulo, en blanco o no cumple `IsValid`, usa la empresa. `saludo` sale de `TimeOfDayGreeting.For`, con el patrón de `CampaignDispatcher.GreetingAsync`: `var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);` y `TimeOnly.FromDateTime(calendar.ToLocal(calendar.UtcNow).DateTime)`. Sólo busca lo que esté en `usedKeys`. Sin empresa no escribe la clave, y `Build` devuelve `null`.
- Registra `services.AddScoped<TemplateSenderValues>();` junto a `IQuotationTemplateOutbound` en `MessagingInfrastructureExtensions.cs`.
- Las dos lecturas de `FindApprovedAsync` proyectan los cinco campos.

- [ ] **Step 4: Comprueba GREEN**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SystemTemplateSendTests" --nologo`, y `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~QuotationsWhatsAppChannelDirectoryTests" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(messaging): build system template sends from the approved row"
```

### Task 3: Enviar la cotización desde su fila

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/QuotationTemplateOutbound.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/QuotationTemplateProvisioning.cs` (quita `ParametersFor` y `Render`)
- Modify: `src/Bootstrapper/QuotationsMetaWhatsAppSender.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuotationTemplateOutboundTests.cs`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationsMetaWhatsAppSenderTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/QuotationTemplateDefinitionTests.cs`

**Interfaces:**
- Consumes: `ApprovedTemplate`, `SystemTemplateSend`, `TemplateSenderValues` (Task 2).
- Produces:
  - `QuotationTemplateSend(Guid TenantId, Guid ConnectionId, Guid SentByMemberId, string WaId, Guid? CustomerId, string RecipientName, ApprovedTemplate Template, string FullName, string OrderNumber, string Total, string ValidUntil, ReadOnlyMemory<byte> Document, string DocumentFileName)`
  - `QuotationsMetaWhatsAppSender(IQuotationTemplateOutbound outbound, IObjectStorage storage, IQuotationTemplateReadiness readiness)`

- [ ] **Step 1: Ajusta `Send` en las pruebas y escribe las nuevas**

`Send` pasa a recibir `string body` y `string? footer`, con `new ApprovedTemplate("qep_cotizacion_v2", "es", "UTILITY", "NAMED", components)`. El texto esperado de `SendsTheTemplateWithItsDocumentIntoANewOpenUnassignedConversation` se escribe literal.

```csharp
[Fact]
public async Task AnEditedTemplateSendsOnlyItsVariablesWithTheSendersNames()
{
    // Cuerpo: "{{saludo}}, {{nombre_contacto}}, soy {{asesor}} de {{empresa}}. Te comparto {{numero_cotizacion}}."
    // Meta recibe, en orden, saludo, nombre_contacto, asesor, empresa y numero_cotizacion con su parameter_name, y no recibe valor_total ni vigencia.
    // messaging.messages.text queda con el cuerpo sustituido.
}

[Fact]
public async Task ALegacyApprovedTemplateStillSendsItsFourVariablesAndShowsItsFooter()
{
    // Cuerpo viejo, con {{fullname}}, {{order_number}}, {{total}} y {{valid_until}}, y pie «Este mensaje fue generado automáticamente.»: los cuatro parámetros con sus nombres de siempre, y el texto termina en el pie.
}

// Review Focus 3.
[Fact]
public async Task ATemplateWithoutVariablesSendsThePdfAndNoBodyParameters()
{
    // template.components tiene un solo elemento, de tipo header.
}

[Fact]
public async Task AReclassifiedTemplateStillSendsAndKeepsItsCategory()
{
    // ApprovedTemplate con category "MARKETING": Sent, y details->'template'->>'category' = 'MARKETING'.
}

[Fact]
public async Task AValueTheRowDoesNotUseCannotBlockTheSend()
{
    // Total con un salto de línea y cuerpo sin {{valor_total}}: Sent.
}

[Fact]
public async Task AValueTheRowUsesThatMetaWouldRejectFailsBeforeAnyCall()
{
    // FullName con un salto de línea y cuerpo con {{nombre_contacto}}: Failed(MessagingErrorCodes.MessageRejected) y ninguna llamada a Graph.
}
```

En `QuotationsMetaWhatsAppSenderTests`: `SendsThePrivatePdfBytesTheTemplateAndTheFormattedValues` lee `send.Template` de una `StubReadiness`, y una prueba nueva `ATemplateNoLongerApprovedIsSendFailed`: `FindApprovedAsync` devuelve `null` y el sender lanza `QuotationsDomainException` con código `quotation.whatsapp.send_failed`, sin llamar al outbound.

Borra de `QuotationTemplateDefinitionTests` lo que pruebe `Render` y `ParametersFor`, incluida `RenderFillsTheFourVariablesAndAppendsTheFooter`.

- [ ] **Step 2: Córrelas y comprueba que fallan**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~QuotationsMetaWhatsAppSenderTests" --nologo`
Expected: error de compilación, `QuotationTemplateSend` no tiene `Template`.

- [ ] **Step 3: Cambia el outbound y el sender**

- Agrega `TemplateSenderValues senderValues` al constructor de `QuotationTemplateOutbound`.
- En el paso 1, después de validar `WaId`: arma el diccionario con `nombre_contacto`, `numero_cotizacion`, `valor_total` y `vigencia`, llama a `senderValues.AddAsync(send.TenantId, send.SentByMemberId, SystemTemplateSend.VariablesOf(send.Template), values, cancellationToken)` y luego a `Build`. `null` devuelve `Failed(MessageRejected)`.
- Deja `IsAcceptable` sólo con la regla del `WaId`.
- El `OutboundDraft` usa `built.Text` y `built.DetailsJson`. `SendTemplateAsync` recibe `built.Send with { HeaderDocument = new TemplateHeaderDocument(mediaId, send.DocumentFileName) }`.
- El sender lee `readiness.FindApprovedAsync(tenantId, channel.ConnectionId, cancellationToken)`; con `null` lanza `quotation.whatsapp.send_failed` con motivo `template_not_approved`.

- [ ] **Step 4: Comprueba GREEN**

Run:
```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~QuotationTemplateOutboundTests" --nologo
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~QuotationsMetaWhatsAppSenderTests" --nologo
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~QuotationTemplateDefinitionTests" --nologo
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(messaging): send the quotation template from its approved row"
```

### Task 4: Enviar los cobros desde su fila

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/BillingTemplateOutbound.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/BillingTemplateProvisioning.cs` (`LegacyPositionalKeys`; quita `ParametersFor` y `Render`)
- Modify: `src/Bootstrapper/ServiceContractsMetaWhatsAppSender.cs`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/ServiceContractsMetaWhatsAppSenderTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/BillingTemplateOutboundTests.cs`

**Interfaces:**
- Consumes: `ApprovedTemplate`, `SystemTemplateSend`, `TemplateSenderValues` (Task 2).
- Produces:
  - `BillingTemplateSend(Guid TenantId, Guid ConnectionId, Guid SentByMemberId, string WaId, Guid? CustomerId, string RecipientName, MessageTemplateKind Kind, ApprovedTemplate Template, IReadOnlyDictionary<string, string> Values)`
  - `BillingTemplateDefinition.LegacyPositionalKeys`: `["nombre_contacto", "servicio", "valor", "vencimiento"]` en `Reminder`, `["nombre_contacto", "cantidad_cobros", "total_vencido"]` en `Overdue`.

- [ ] **Step 1: Ajusta y escribe las pruebas**

`ServiceContractsMetaWhatsAppSenderTests`:

```csharp
Assert.Equal("Ana Pérez", send.Values["nombre_contacto"]);
Assert.Equal("Hosting plan pro", send.Values["servicio"]);
Assert.Equal("$ 150.000", send.Values["valor"]);
Assert.Equal("10/03/2026", send.Values["vencimiento"]);
// vencido
Assert.Equal(("2", "$ 450.000"), (send.Values["cantidad_cobros"], send.Values["total_vencido"]));
```

`BillingTemplateOutboundTests`:

```csharp
[Fact]
public async Task ALegacyPositionalReminderSendsItsFourValuesInOrder()
{
    // Fila POSITIONAL con "{{1}} … {{4}}": Meta recibe cuatro parámetros sin parameter_name y el texto queda sustituido.
}

[Fact]
public async Task ANamedReminderUsesTheEncargadoAsAdvisorAndSendsOnlyItsVariables()
{
    // "Hola {{nombre_contacto}}, {{asesor}} te recuerda {{servicio}} hoy.": tres parámetros con nombre; asesor es el nombre de SentByMemberId.
}
```

- [ ] **Step 2: Córrelas y comprueba que fallan**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ServiceContractsMetaWhatsAppSenderTests" --nologo`
Expected: error de compilación, `BillingTemplateSend` no tiene `Values`.

- [ ] **Step 3: Cambia el outbound y el sender**

- `Parameters(message)` pasa a `Values(message)`, un `Dictionary<string, string>` con las claves de arriba. `CustomerName`, `Joined` y `Fit` siguen igual.
- El sender pasa `template` entero en `Template`.
- `BillingTemplateOutbound` recibe `TemplateSenderValues`, llama a `AddAsync` y a `Build(send.Template, values, BillingTemplateDefinitions.For(send.Kind).LegacyPositionalKeys)`, y usa `built.Text`, `built.DetailsJson` y `built.Send`. `null` devuelve `Failed(MessageRejected)`. La regla del `WaId` sigue en `IsAcceptable`.

- [ ] **Step 4: Comprueba GREEN**

Run:
```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~BillingTemplateOutboundTests" --nologo
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ServiceContractsMetaWhatsAppSenderTests" --nologo
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(messaging): send billing templates from their approved row"
```

### Task 5: Crear la cotización con cuerpo editado

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/QuotationTemplateProvisioning.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/QuotationTemplateDefinitionTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ProvisionQuotationTemplateHandlerTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuotationTemplateProvisioningApiTests.cs`

**Interfaces:**
- Consumes: `SystemTemplateVariables`, `TemplateBodyRules` (Task 1).
- Produces:
  - `ProvisionQuotationTemplateCommand(Guid TenantId, Guid ConnectionId, string? Body = null)`
  - `ProvisionQuotationTemplateCommandValidator : AbstractValidator<ProvisionQuotationTemplateCommand>`, último parámetro del handler como `IValidator<ProvisionQuotationTemplateCommand>`.
  - `QuotationTemplateDefinition.BaseBody` (reemplaza a `Body`), `Draft(string name, string body)`, `Payload(string handle, string name, string body)`, `IsAdoptable(string componentsJson)` (reemplaza a `Matches`). Desaparecen `Footer` y `Variables`; `Examples` sale de `SystemTemplateVariables.Quotation`.
  - `ProvisionQuotationTemplateRequest(string? Body)` en Api.

- [ ] **Step 1: Escribe las pruebas que fallan**

Definición:

```csharp
[Fact]
public void TheBaseTextIsTheSpecsAndNewTemplatesHaveNoFooter()
{
    Assert.Equal(
        "¡Hola, {{nombre_contacto}}! Te compartimos en este mensaje la cotización *{{numero_cotizacion}}*, por un valor total de *{{valor_total}}*. Esta cotización estará vigente hasta el *{{vigencia}}*. Si tienes alguna pregunta, responde este mensaje y con gusto te ayudamos.",
        QuotationTemplateDefinition.BaseBody);
    Assert.Null(QuotationTemplateDefinition.Draft("qep_cotizacion", QuotationTemplateDefinition.BaseBody).Footer);
    var components = QuotationTemplateDefinition.Payload("handle", "qep_cotizacion", "Hola {{nombre_contacto}}, gracias.")["components"]!.AsArray();
    Assert.DoesNotContain(components, component => component!["type"]!.GetValue<string>() == "FOOTER");
}

// El cuerpo y el pie viejos siguen siendo adoptables: el texto ya no decide.
[Theory]
[InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{fullname}}, gracias."},{"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]""", true)]
[InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."}]""", true)]
[InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."}]""", false)]
[InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."}]""", false)]
[InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{otra}}, gracias."}]""", false)]
[InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Sí"}]}]""", false)]
public void AdoptionLooksAtTheShapeNotTheText(string components, bool expected) =>
    Assert.Equal(expected, QuotationTemplateDefinition.IsAdoptable(components));
```

Handler (`ProvisionAsync` recibe `string? body = null`):

```csharp
[Fact]
public async Task TheEditedBodyGoesToMetaAndToTheRowTrimmed()
{
    var rig = new Rig();
    var dto = await rig.ProvisionAsync("  Hola {{nombre_contacto}}, soy {{asesor}}.  ");
    Assert.Equal("Hola {{nombre_contacto}}, soy {{asesor}}.", dto.Body);
    Assert.Null(dto.Footer);
    Assert.Equal("NAMED", rig.Repository.Rows.Single().ParameterFormat);
}

[Fact]
public async Task ARepeatedVariableTravelsWithOneExample()
{
    // "Total {{valor_total}}, repito: {{valor_total}} y gracias.": body_text_named_params tiene un solo elemento, con el ejemplo de valor_total.
}

[Fact]
public async Task EveryVersionOfARetryCarriesTheEditedBody()
{
    // name_taken en la primera creación: la segunda lleva el mismo cuerpo editado.
}

[Fact]
public async Task AVariableOutsideTheListIsAValidationErrorWithoutTouchingMeta()
{
    var error = await Assert.ThrowsAsync<ValidationException>(() => new Rig().ProvisionAsync("Hola {{otra}}, gracias."));
    Assert.Equal("body", Assert.Single(error.Errors).PropertyName);
}

[Fact]
public async Task AnExternalWithTheSubmittedBodyIsAdopted() { }

[Fact]
public async Task AnExternalWithAnotherBodyIsAConflict() { }

// Review Focus 4: el borrador trae sus claves viejas y el POST las devuelve igual; el validador no puede frenarlo.
[Fact]
public async Task AnExternalWithTheOldKeysIsAdoptedWithItsOwnBody() { }

// Compatibilidad: backend desplegado antes que el frontend.
[Fact]
public async Task WithoutBodyACompatibleExternalIsAdoptedAndWithoutExternalTheBaseTextIsCreated() { }
```

API:

```csharp
[Fact]
public async Task ThePostAcceptsABodyAndWorksWithoutOne() { }

[Fact]
public async Task AVariableOutsideTheListIs422WithTheBodyKey() { }
```

- [ ] **Step 2: Córrelas y comprueba que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~QuotationTemplateDefinitionTests|FullyQualifiedName~ProvisionQuotationTemplateHandlerTests" --nologo`
Expected: error de compilación, `BaseBody` no existe.

- [ ] **Step 3: Implementa**

- El validador omite `null` y aplica `TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(SystemTemplateVariables.Quotation))` con `ValidationFailure("body", …)`.
- El handler calcula `var body = (command.Body ?? QuotationTemplateDefinition.BaseBody).Trim();` después de `EnsureAsync`, y valida **después** de la adopción y justo antes de subir el PDF: una `External` adoptable puede llevar claves viejas, que el validador rechaza.
- Adopción cuando `current is null` y hay `External` con el nombre base: si `IsAdoptable` y (`command.Body is null` o el cuerpo guardado es igual a `body`), `AdoptAsync`; si no, `quotation_conflict`.
- `IsDocumentHeader` se conserva. `IsAdoptable` exige `described.Header is null`, `UnsupportedReason == MediaHeader`, sin botones, cuerpo no vacío y variables dentro de la lista más `LegacyKeys.Keys`.
- Actualiza el resumen de la clase, el de `QuotationTemplateDefinition` y el comentario sobre Zenvia: el texto ya no es único ni igual al de Zenvia.
- Endpoint: `ProvisionQuotationTemplateRequest? request` con `.Accepts<ProvisionQuotationTemplateRequest>("application/json")`. Cuerpo opcional, como `SaveQuickReplyOrderRequest?`. El comentario «Sin cuerpo» se reescribe.
- Ajusta `Rig.Handler()` y `External` para pasar el validador y unos `components` válidos cuando la prueba lo pida.

- [ ] **Step 4: Comprueba GREEN**

Run:
```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~QuotationTemplateDefinitionTests|FullyQualifiedName~ProvisionQuotationTemplateHandlerTests" --nologo
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~QuotationTemplateProvisioningApiTests" --nologo
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(messaging): create the quotation template from an edited body"
```

### Task 6: Crear los cobros con cuerpo editado, una plantilla a la vez

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/BillingTemplateProvisioning.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/BillingTemplateProvisioningTests.cs`

**Interfaces:**
- Consumes: `SystemTemplateVariables`, `TemplateBodyRules` (Task 1), `TemplateComponentsBuilder.CreatePayload`.
- Produces:
  - `ProvisionBillingTemplatesCommand(Guid TenantId, Guid ConnectionId, string? ReminderBody = null, string? OverdueBody = null)`
  - `ProvisionBillingTemplatesCommandValidator`, con claves `reminderBody` y `overdueBody`.
  - `BillingTemplateDefinition(MessageTemplateKind Kind, string Name, string Label, string BaseBody, IReadOnlyList<string> LegacyPositionalKeys)` con `IReadOnlyList<SystemTemplateVariable> Variables`, `Draft(string name, string body)` y `Payload(string name, string body)`. `Label` es `Recordatorio` o `Vencido`.
  - `ProvisionBillingTemplatesRequest(string? ReminderBody, string? OverdueBody)` en Api.

- [ ] **Step 1: Reemplaza y escribe las pruebas**

```csharp
[Fact]
public void TheBaseTextsAreTheOwnersWordsWithNamedVariables()
{
    Assert.Equal("Hola {{nombre_contacto}}, te recordamos que tu servicio {{servicio}} por {{valor}} vence el {{vencimiento}}. Si ya realizaste el pago, ignora este mensaje.", BillingTemplateDefinitions.Reminder.BaseBody);
    Assert.Equal("Hola {{nombre_contacto}}, tienes {{cantidad_cobros}} cobro(s) vencido(s) por un total de {{total_vencido}}. Escríbenos para ponerte al día. Si ya pagaste, ignora este mensaje.", BillingTemplateDefinitions.Overdue.BaseBody);
}

[Fact]
public void ThePayloadIsNamedWithoutHeaderOrFooter()
{
    var payload = BillingTemplateDefinitions.Reminder.Payload("qep_cobro_recordatorio", "Hola {{nombre_contacto}}, gracias.");
    Assert.Equal("NAMED", payload["parameter_format"]!.GetValue<string>());
    var body = Assert.Single(payload["components"]!.AsArray());
    Assert.Equal("nombre_contacto", body!["example"]!["body_text_named_params"]![0]!["param_name"]!.GetValue<string>());
}

[Fact]
public async Task EachBodyGoesToItsTemplate() { }

[Fact]
public async Task AVariableOfTheOtherNoticeIsRejectedWithItsKey()
{
    // reminderBody con {{cantidad_cobros}}: ValidationException con clave reminderBody y ninguna llamada a Meta.
}

// Review Focus 5.
[Fact]
public async Task WhenMetaRejectsTheSecondTheFirstStaysSavedAndARetryCreatesOnlyTheSecond()
{
    var rig = new Rig();
    // Meta acepta el recordatorio y rechaza el vencido con title "Invalid Format", msg "El cuerpo no sirve".
    var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ProvisionAsync());
    Assert.Equal(MessagingErrorCodes.TemplateRejectedByMeta, error.Code);
    Assert.StartsWith("Vencido: ", error.Message, StringComparison.Ordinal);
    Assert.Equal(MessageTemplateKind.BillingReminder, Assert.Single(rig.Repository.Rows).Kind);
    Assert.Equal(1, rig.UnitOfWork.Saves);
    // Meta ya acepta: el reintento hace una sola creación más y deja las dos filas.
}

[Fact]
public async Task AnotherErrorKeepsItsCodeWithoutThePrefix()
{
    // limit_reached en el segundo: mismo código y mismo mensaje, y el primero queda guardado.
}

[Fact]
public async Task ALiveTemplateIgnoresItsBodyAndIsNotRecreated() { }
```

`TheTwoDefinitionsCarryTheOwnersTextVerbatim` y `ThePayloadIsPositionalWithOneExamplePerVariableAndNoHeaderOrFooter` se reemplazan por las de arriba. `ProvisioningCreatesBothTemplatesPendingWithTheirKindAndAuditsEach` pasa a esperar `Saves == 2`: un guardado por plantilla.

- [ ] **Step 2: Córrelas y comprueba que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~BillingTemplateProvisioningTests" --nologo`
Expected: error de compilación, `BaseBody` no existe.

- [ ] **Step 3: Implementa**

- `Payload` usa `TemplateComponentsBuilder.CreatePayload(Draft(name, body))`, con `Examples` armado desde `Variables`. Quita `Components()`, `Examples` y `ParameterFormat`.
- `MessageTemplate.Pending` guarda `TemplateComponentsBuilder.Named` y `TemplateComponentsBuilder.Components(draft).ToJsonString()`.
- El bucle del handler guarda dentro de cada vuelta: después de `repository.Add` y `audit.RecordTemplate`, `await unitOfWork.SaveChangesAsync(cancellationToken)`. Quita `created`.
- Envuelve `CreateAsync` y `DeletePreviousAsync` en un `try` por definición. `catch (MessagingDomainException error) when (error.Code == MessagingErrorCodes.TemplateRejectedByMeta)` relanza `new MessagingDomainException(error.Code, $"{definition.Label}: {error.Message}")`.
- Cuerpo por definición: `(body ?? definition.BaseBody).Trim()`. Una viva no lo mira.
- Valida después de `EnsureAsync` y del guard del módulo.
- Reescribe el resumen de la clase, que hoy describe la transacción única y la huérfana.
- Endpoint: `ProvisionBillingTemplatesRequest? request`, como en la Task 5.

- [ ] **Step 4: Comprueba GREEN**

Run:
```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~BillingTemplateProvisioningTests" --nologo
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~BillingTemplate" --nologo
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(messaging): create billing templates from edited bodies one at a time"
```

### Task 7: Los borradores para el editor

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/SystemTemplateDrafts.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/TemplateManagementDtos.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:910`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/SystemTemplateDraftsTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/QuotationTemplateProvisioningApiTests.cs`

**Interfaces:**
- Consumes: `QuotationTemplateDefinition`, `BillingTemplateDefinitions`, `SystemTemplateVariables`.
- Produces:
  - `public sealed record SystemTemplateVariableDto(string Key, string Label, string Example);`
  - `public sealed record SystemTemplateDraftDto(string Body, bool Locked, string? RejectionReason, IReadOnlyList<SystemTemplateVariableDto> Variables);`
  - `public sealed record BillingTemplatesDraftDto(SystemTemplateDraftDto Reminder, SystemTemplateDraftDto Overdue);`
  - `GetQuotationTemplateDraftQuery(Guid TenantId, Guid ConnectionId)` y `GetBillingTemplatesDraftQuery(Guid TenantId, Guid ConnectionId)` con sus handlers.
  - Rutas `GET …/template-management/quotation/draft` y `GET …/template-management/billing/draft`, con `MessagingPermissions.TemplateManage`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact]
public async Task WithoutRowsTheDraftIsTheBaseTextWithItsList()
{
    var draft = await rig.QuotationAsync();
    Assert.Equal(QuotationTemplateDefinition.BaseBody, draft.Body);
    Assert.False(draft.Locked);
    Assert.Null(draft.RejectionReason);
    Assert.Equal(7, draft.Variables.Count);
}

[Fact]
public async Task ARejectedRowStartsFromItsBodyAndCarriesTheReason() { }

[Fact]
public async Task ACompatibleExternalStartsFromItsBody() { }

[Fact]
public async Task AnIncompatibleExternalStartsFromTheBaseText() { }

[Fact]
public async Task ARejectedQuotationWithTheOldKeysStartsFromTheBaseText()
{
    // Fila Quotation REJECTED con {{fullname}}: Body = BaseBody, y conserva RejectionReason. Su texto no pasaría el validador.
}

[Fact]
public async Task ARejectedPositionalBillingRowStartsFromTheBaseText()
{
    // Fila BillingOverdue REJECTED con "{{1}}": Body = BaseBody, Locked = false, y conserva RejectionReason.
}

[Fact]
public async Task ALiveBillingRowIsLockedWithItsBody()
{
    // Recordatorio APPROVED con {{1}}: Locked = true y Body con {{1}}; el vencido sin fila, base y sin bloquear.
}

[Fact]
public async Task BillingDraftNeedsTheModule() { }
```

API: `TheDraftRouteAnswersTheBaseTextAndABillingDraftWithoutModuleIs403`, y un `403` sin `messaging.template.manage`.

- [ ] **Step 2: Córrelas y comprueba que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SystemTemplateDraftsTests" --nologo`
Expected: error de compilación, `GetQuotationTemplateDraftQuery` no existe.

- [ ] **Step 3: Implementa**

- `internal static class SystemTemplateDrafts`, con `SystemTemplateDraftDto For(MessageTemplate? current, MessageTemplate? external, string baseBody, IReadOnlyList<SystemTemplateVariable> variables)`.
- Orden: `current` viva da `Locked = true` con su cuerpo. `current` no viva, si su cuerpo no está vacío y todas sus variables están en la lista, da su cuerpo; si no, el base, conservando `StatusReason`. Sin `current`, una `external` con `IsAdoptable` da su cuerpo. Si no, el base.
- Los handlers siguen a `GetTemplateManagementHandler`: `EnsureAsync`, `ResolveTemplateSourceAsync` y, en cobros, `TenantModuleGuard.EnsureEnabledAsync`.
- Escribe en el DTO por qué `Locked` y el texto inicial viajan ya resueltos: la pantalla no conoce el formato de Meta ni las reglas de adopción.
- Registra los dos `IQueryHandler` junto a las líneas 910-911. Sin ese registro, la ruta responde 500.

- [ ] **Step 4: Comprueba GREEN**

Run:
```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SystemTemplateDraftsTests" --nologo
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~QuotationTemplateProvisioningApiTests" --nologo
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(messaging): expose the editable drafts of system templates"
```

### Task 8: Automatizaciones habla de `nombre_contacto`

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowDefinition.cs:140`
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/FixedVariables.cs:13`
- Modify: `src/Modules/Automations/Modules.Automations.Domain/Flow/FlowValidator.cs:590` (`CheckName`)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowValidatorTests.cs`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/FlowEngineWaitingTests.cs`
- Test: las del barrido del Step 5

**Interfaces:**
- Consumes: nada de las tareas anteriores. Es independiente del resto del plan.
- Produces: `FlowFixedVariables.Names == ["nombre_contacto", "telefono", "empresa", "conexion"]`, que el editor recibe en `variables`.

- [ ] **Step 1: Escribe las pruebas nuevas**

En `FlowValidatorTests.cs`:

```csharp
[Fact]
public void AConditionOnTheContactNameIsValid()
{
    var condition = new ConditionNode(
        "n1", "¿Tiene nombre?", Out(("yes", "end"), ("no", "end")), "all", [new ConditionRule("nombre_contacto", OPERADOR, VALOR)]);

    Assert.DoesNotContain(FlowValidator.Validate(Flow(condition, End()), Ctx()), i => i.Code == "variable.name_invalid");
}

[Fact]
public void AnOwnVariableStillCannotUseAnUnderscore()
{
    var question = new OpenQuestionNode(
        "n1", "Dato", Out(("answered", "end"), ("invalid", null)), "¿Dato?", "otro_dato", new QuestionValidation("none", null, null), null);

    Assert.Contains(FlowValidator.Validate(Flow(question, End()), Ctx()), i => i.Code == "variable.name_invalid");
}

[Fact]
public void TheOldNameIsNoLongerAFixedVariable()
{
    var issue = Single(FlowValidator.Validate(Flow(Message("n1", "Hola {nombre}"), End()), Ctx()), "variable.never_set");

    Assert.Equal(FlowIssueSeverity.Warning, issue.Severity);
}

[Fact]
public void TheContactNameIsOfferedAndNeedsNoBlockToSetIt()
{
    var flow = Flow(Message("n1", "Hola {nombre_contacto}"), End());

    Assert.Equal("nombre_contacto", FlowValidator.KnownVariables(flow)[0]);
    Assert.DoesNotContain(FlowValidator.Validate(flow, Ctx()), i => i.Code == "variable.never_set");
}
```

`OPERADOR` y `VALOR` son los de cualquier `ConditionRule` ya usada en ese archivo: la prueba mira el nombre, no el operador. En `FlowEngineWaitingTests.cs`:

```csharp
[Fact]
public void ARestartKeepsTheContactNameAndNotTheOldKey()
{
    var kept = RunVariables.Parse("""{"nombre_contacto":"Ana","nombre":"Vieja","motivo":"Falla"}""").FixedOnly();

    Assert.Equal(["nombre_contacto"], kept.Snapshot().Keys);
}
```

- [ ] **Step 2: Corre y mira fallar**

Run: `dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~FlowValidatorTests|FullyQualifiedName~FlowEngineWaitingTests" --nologo`
Expected: FAIL. `AConditionOnTheContactNameIsValid`, `TheOldNameIsNoLongerAFixedVariable`, `TheContactNameIsOfferedAndNeedsNoBlockToSetIt` y `ARestartKeepsTheContactNameAndNotTheOldKey` en rojo. `AnOwnVariableStillCannotUseAnUnderscore` ya pasa y protege la regla.

- [ ] **Step 3: Renombra en el dominio y el motor**

`FlowFixedVariables.Names` y `FixedVariables.Build` escriben `nombre_contacto`. Sin alias: `nombre` deja de escribirse.

- [ ] **Step 4: Exime a las fijas en `CheckName`**

`else if (!FixedVariables.Contains(name) && !VariableName().IsMatch(name))`. No toques `VariableName()` ni la comprobación de variables declaradas de la línea 74: el admin sigue sin poder crear nombres con guion bajo. Un `saveTo` sobre una fija sigue valiendo, como pasaba con `nombre`.

- [ ] **Step 5: Barrido de pruebas**

Cambia `{nombre}` por `{nombre_contacto}` y la clave `"nombre"` de la variable fija por `"nombre_contacto"` en:

- Unitarias: `FlowValidatorTests.cs:33,35,395,397,558`, `FlowEngineWaitingTests.cs:182,198,199,369,382`, `FlowEngineTests.cs:65,67,243,366`, `FlowEngineDataConsentTests.cs:10,29,140`, `FlowJsonTests.cs:224,237,257,341`, `RunProcessorTests.cs:74`, `SimulationRunnerTests.cs:121` y `Fixtures/annex-a.json`.
- Integración: `ChatbotsApiTests.cs:110,332,486`, `EngineEndToEndTests.cs:36,149,513`, `EngineEndToEndTests.DataConsent.cs:12,21,155`, `SimulationsApiTests.cs:146`, `TemplatesApiTests.cs:77`.

**No las toques:** ahí `nombre` es otro dato. `FlowValidatorTests.cs:70,537`, `HttpBlockRequestTests.cs:17,91` y `FlowEngineTests.cs:115` son campos de un objeto (`cliente.nombre`); `Fixtures/clasic.json` usa `nombres`, que es variable propia; `ConversationRunTests.cs` y `RunsPersistenceTests.cs` guardan una clave cualquiera en un JSON.

En `AVariableNeverSetWarns` la prueba sigue esperando un solo aviso, por `{cliente.nombre}`.

Comprueba con: `rg -n '\{nombre\}' tests/Modules/Automations src/Modules/Automations --glob '!**/Migrations/**'`
Expected: sólo `Chatbots.cs:740` (el nombre de un chatbot, no una variable) y `TheOldNameIsNoLongerAFixedVariable`.

- [ ] **Step 6: Corre y mira pasar**

Run:
```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --nologo
dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --nologo
```
Expected: PASS. Compara por nombre contra la base si hay rojas previas.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Automations tests/Modules/Automations
git commit -m "feat(automations)!: rename the contact variable to nombre_contacto"
```

### Task 9: Documentación, suite completa y comprobación de producción

**Files:**
- Modify: `README.md:3181` y § «La plantilla de cotización» (línea 3195). `README.md:2543` es Zenvia: no se toca.
- Modify: `CLAUDE.md`, sección de gotchas.

- [ ] **Step 1: Actualiza el README**

Agrega `GET /quotation/draft`, `GET /billing/draft` y `POST /billing` a la tabla, y explica que el `POST` recibe el cuerpo opcional. En la sección de la plantilla de cotización deja dicho que el texto lo elige el tenant y que sin pie sólo salen las nuevas.

- [ ] **Step 2: Agrega el gotcha a `CLAUDE.md`**

```markdown
- **Una plantilla del sistema se envía desde su fila, no desde una definición** (spec 2026-10-31). `QuotationTemplateDefinition.BaseBody` y
  `BillingTemplateDefinition.BaseBody` son sólo el texto inicial: cada tenant lo cambia. Un dato nuevo disponible para el texto entra en
  `SystemTemplateVariables` y en los valores de `QuotationTemplateOutbound` o `ServiceContractsMetaWhatsAppSender`, con su etiqueta; si falta en
  el segundo, `SystemTemplateSend.Build` devuelve `null` y el envío falla con `message_rejected`. `POSITIONAL` sólo existe en filas viejas, y
  `LegacyPositionalKeys` da su orden: no lo cambies. Nombrada o no se decide por las claves, no por la columna, porque el refresco guarda
  `POSITIONAL` si Meta no manda el campo. Las cuatro primeras variables son las fijas de `FIXED_VARIABLE_NAMES` del frontend: si esa lista
  crece, `SystemTemplateVariables` también, y hay que darle valor en `TemplateSenderValues`. `fullname`, `order_number`, `total` y `valid_until`
  salieron de la lista pero las cotizaciones aprobadas los llevan: `LegacyKeys` les da su valor en `SystemTemplateSend.Build` y `IsAdoptable` los acepta;
  no los limpies.
```

- [ ] **Step 3: Corre la suite completa una vez**

Run:
```powershell
$env:Seed__Enabled = "false"
dotnet build --nologo
dotnet test tests/ArchitectureTests --nologo
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --nologo
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --nologo
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --nologo
dotnet test tests/Modules/ServiceContracts --nologo
dotnet test tests/Modules/Automations --nologo
```
Expected: 0 errores y 0 pruebas rojas nuevas. Compara por nombre contra la base y deja el resultado literal en el reporte. `docs/estado.md` registra 17 fallas previas. Si faltan puertos, corre por proyecto y repite por nombre.

- [ ] **Step 4: Deja la comprobación de producción en el reporte**

El agente no tiene permiso para leer la base de producción: no lo intentes. Entrega esta consulta para que el owner la corra antes de desplegar, y anótala en el reporte final como pendiente suyo:

```sql
SELECT 'version' AS origen, id::text FROM automations.chatbot_versions
WHERE flow::text ~ '\{nombre[.\[}]' OR flow::text ~ '"variable": *"nombre[.\["]'
UNION ALL
SELECT 'borrador', id::text FROM automations.chatbots
WHERE draft_flow::text ~ '\{nombre[.\[}]' OR draft_flow::text ~ '"variable": *"nombre[.\["]';
```

Expected: 0 filas. Si devuelve alguna, ese flujo se corrige antes de desplegar.

- [ ] **Step 5: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs(messaging): document editable system templates"
```
