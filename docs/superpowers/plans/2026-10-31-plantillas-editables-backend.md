# Plantillas editables: backend. Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que las plantillas de cotización y de cobro se creen con un texto editado por el tenant, y que su envío y la bandeja salgan de la plantilla aprobada de cada conexión.

**Architecture:** `components` de `messaging.message_templates` ya guarda el texto, así que no hay migración. El envío deja de usar constantes: lee variables y texto de la fila aprobada con `TemplateCatalog.FromStored`. La lista cerrada de variables vive en `SystemTemplateVariables`, y las definiciones sólo conservan nombre, texto base y el orden de las filas viejas.

**Tech Stack:** .NET 10, FluentValidation, EF Core, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-31-plantillas-base-editables-design.md`

## Global Constraints

- Worktree: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\plantillas-editables`, rama `feature/plantillas-editables`. Rutas absolutas o `Set-Location` a ese worktree en cada llamada: el cwd se resetea al checkout principal, que no se toca.
- TDD: RED y GREEN con evidencia literal en cada tarea.
- Por tarea sólo las clases tocadas (`--filter`). La suite completa, una vez, en la Task 8.
- Conventional commits, sin `Co-Authored-By` ni atribución de IA, aunque el harness lo sugiera.
- Comentarios y textos para personas en español colombiano, tuteando. Contratos en inglés sin traducir.
- Archivos sin BOM. Nunca `Set-Content` para reescribir un `.cs`: rompe las tildes.
- Regla del owner: variables siempre con nombre. Toda plantilla nueva sale `NAMED`; `POSITIONAL` sólo se lee.
- Las plantillas nuevas de cotización no llevan pie. Una fila existente conserva el suyo.
- Pruebas de integración: `$env:Seed__Enabled = "false"` en línea aparte.
- `Api.exe` o `dotnet Api.dll` corriendo bloquea el build: detenlo primero.
- No tocar `docs/estado.md` en esta rama.

## Review Focus

1. Meta no devuelve `parameter_format` en el listado: el refresco deja `POSITIONAL` en una fila con claves con nombre, y el envío tiene que seguir nombrando las variables (hoy está fijo en `Named: true`). Prueba en Task 2.
2. El nombre visible del asesor tiene un salto de línea o está vacío: el mensaje sale con el nombre de la empresa, no falla. Prueba en Task 2.
3. Una plantilla sin variables: sale el PDF en el encabezado y ningún parámetro de cuerpo. Prueba en Task 3.
4. El mismo `{{total}}` dos veces en el cuerpo: un solo ejemplo a Meta y un solo parámetro al enviar. Prueba en Task 5.
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
  - `public static class SystemTemplateVariables` con `Quotation`, `BillingReminder`, `BillingOverdue` (`IReadOnlyList<SystemTemplateVariable>`), constantes `Advisor = "advisor"`, `Company = "company"` y `IReadOnlySet<string> KeysOf(IReadOnlyList<SystemTemplateVariable> variables)`.
  - `internal static class TemplateBodyRules` con `string? Check(string? body, IReadOnlySet<string>? allowedKeys = null)`: el mensaje de error o `null`.
  - `TemplateMessages.BodyVariableNotAllowed = "Usa sólo las variables de la lista de esta plantilla."`

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact]
public void EachKindHasItsClosedListInOrder()
{
    Assert.Equal(["fullname", "order_number", "total", "valid_until", "advisor", "company"], SystemTemplateVariables.Quotation.Select(v => v.Key));
    Assert.Equal(["fullname", "service", "amount", "due_date", "advisor", "company"], SystemTemplateVariables.BillingReminder.Select(v => v.Key));
    Assert.Equal(["fullname", "overdue_count", "overdue_total", "advisor", "company"], SystemTemplateVariables.BillingOverdue.Select(v => v.Key));
    Assert.Equal(["Nombre del cliente", "Número de cotización", "Valor total", "Vigencia", "Asesor", "Empresa"], SystemTemplateVariables.Quotation.Select(v => v.Label));
}

[Theory]
[InlineData("Hola {{fullname}}, gracias.", null)]
[InlineData("Hola {{otra}}, gracias.", TemplateMessages.BodyVariableNotAllowed)]
[InlineData("Hola {{ fullname }}, gracias.", TemplateMessages.BodyVariable)]
[InlineData("{{fullname}}", TemplateMessages.Body)]
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
| `fullname` | Nombre del cliente | Juan Perez |
| `order_number` | Número de cotización | COT-000123 |
| `total` | Valor total | 2.450.000 COP |
| `valid_until` | Vigencia | 30 de septiembre de 2026 |
| `service` | Servicio | Hosting plan pro |
| `amount` | Valor | 150.000 COP |
| `due_date` | Vencimiento | 10/03/2026 |
| `overdue_count` | Cantidad de cobros | 2 |
| `overdue_total` | Total adeudado | 450.000 COP |
| `advisor` | Asesor | Ana Gomez |
| `company` | Empresa | Comercial Andina |

Sin `$`, `#` ni `%` en los ejemplos: Meta los rechaza. Mueve `LooseBrace` y `StrictVariable` de `CreateTemplateCommandValidator` a `TemplateBodyRules`, y deja que el `Custom` de `Body` llame a `Check(body)` y agregue su mensaje con la clave `body`.

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
- Consumes: `SystemTemplateVariables` (Task 1), `TemplateCatalog.FromStored`, `TemplateCatalog.BuildSend`, `TemplateParameterRules.IsValid`.
- Produces:
  - `public sealed record ApprovedTemplate(string Name, string Language, string Category, string ParameterFormat, string ComponentsJson);` Reemplaza a `ApprovedQuotationTemplate` y `ApprovedBillingTemplate`; `FindApprovedAsync` de los dos puertos la devuelve.
  - `public static class SystemTemplateSend` con `IReadOnlyList<string> VariablesOf(ApprovedTemplate template)` y `BuiltTemplate? Build(ApprovedTemplate template, IReadOnlyDictionary<string, string> values, IReadOnlyList<string>? legacyPositionalKeys = null)`.
  - `public sealed class TemplateSenderNames(IMessagingMemberNames memberNames, IMessagingTenantNames tenantNames)` con `Task AddAsync(Guid tenantId, Guid memberId, IReadOnlyCollection<string> usedKeys, IDictionary<string, string> values, CancellationToken cancellationToken)`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact]
public void SendsOnlyTheVariablesTheRowUsesInItsOrder()
{
    var built = SystemTemplateSend.Build(Row("NAMED", "Hola {{fullname}}, de {{company}}, tu total: {{total}} hoy."), AllValues);
    Assert.True(built!.Send.Named);
    Assert.Equal(["fullname", "company", "total"], built.Send.Parameters.Select(parameter => parameter.Key));
    Assert.Equal("Hola Laura, de Andina, tu total: 10 hoy.", built.Text);
    Assert.DoesNotContain("valid_until", built.DetailsJson);
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
    var built = SystemTemplateSend.Build(Row("POSITIONAL", "Hola {{1}}, debes {{2}} hoy."), AllValues, ["fullname", "total"]);
    Assert.False(built!.Send.Named);
    Assert.Equal([("1", "Laura"), ("2", "10")], built.Send.Parameters.Select(parameter => (parameter.Key, parameter.Value)));
}

// Review Focus 1: si Meta no devuelve parameter_format, el refresco guarda POSITIONAL.
[Fact]
public void NamedKeysAreNamedWhateverTheStoredFormatSays() =>
    Assert.True(SystemTemplateSend.Build(Row("POSITIONAL", "Hola {{fullname}}, ya salió."), AllValues)!.Send.Named);

[Fact]
public void AMissingOrInvalidValueBuildsNothing()
{
    Assert.Null(SystemTemplateSend.Build(Row("NAMED", "Hola {{service}}, ya salió."), AllValues));
    Assert.Null(SystemTemplateSend.Build(Row("NAMED", "Hola {{fullname}}, ya salió."), new Dictionary<string, string> { ["fullname"] = "a\nb" }));
}

[Fact]
public async Task TheAdvisorIsTheSendersDisplayName()
{
    var values = await AddAsync(displayName: "Ana Gomez", tenantName: "Andina", ["advisor", "company"]);
    Assert.Equal(("Ana Gomez", "Andina"), (values["advisor"], values["company"]));
}

// Review Focus 2.
[Theory]
[InlineData(null)]
[InlineData("   ")]
[InlineData("Ana\nGomez")]
public async Task AnAdvisorWithoutAUsableNameFallsBackToTheCompany(string? displayName) =>
    Assert.Equal("Andina", (await AddAsync(displayName, "Andina", ["advisor"]))["advisor"]);

[Fact]
public async Task NamesTheRowDoesNotUseAreNotLookedUp() => Assert.Empty(await AddAsync("Ana", "Andina", ["fullname"], failIfCalled: true));
```

`Row` arma un `ApprovedTemplate` con `Components(new TemplateDraft(...))` o JSON literal; `AllValues` trae `fullname = "Laura"`, `company = "Andina"`, `total = "10"`.

- [ ] **Step 2: Córrelas y comprueba que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~SystemTemplateSendTests" --nologo`
Expected: error de compilación, `SystemTemplateSend` no existe.

- [ ] **Step 3: Implementa `SystemTemplateSend` y `TemplateSenderNames`**

- `Build`: `FromStored(...)`, y `named = Parameters.Any(key => !key.All(char.IsAsciiDigit))`. Si es con nombre, cada clave lee `values[key]`; si no, `legacyPositionalKeys[int.Parse(key) - 1]`. Un valor ausente, una posición fuera de rango o un valor que no cumpla `TemplateParameterRules.IsValid` devuelve `null`. Llama a `BuildSend(described with { ParameterFormat = named ? TemplateCatalog.Named : TemplateCatalog.Positional }, resolved)` con el diccionario de las usadas, indexado por la clave de la fila, para que `details` sólo lleve esas.
- Comenta en el `named` por qué no lee la columna: el refresco guarda `POSITIONAL` si Meta no manda el campo.
- `TemplateSenderNames.AddAsync`: `company` sale de `FindAsync`, y `advisor` de `FindDisplayNameAsync`. Si el nombre es nulo, en blanco o no cumple `IsValid`, usa la empresa. Sólo busca lo que esté en `usedKeys`. Sin empresa no escribe la clave, y `Build` devuelve `null`.
- Registra `services.AddScoped<TemplateSenderNames>();` junto a `IQuotationTemplateOutbound` en `MessagingInfrastructureExtensions.cs`.
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
- Consumes: `ApprovedTemplate`, `SystemTemplateSend`, `TemplateSenderNames` (Task 2).
- Produces:
  - `QuotationTemplateSend(Guid TenantId, Guid ConnectionId, Guid SentByMemberId, string WaId, Guid? CustomerId, string RecipientName, ApprovedTemplate Template, string FullName, string OrderNumber, string Total, string ValidUntil, ReadOnlyMemory<byte> Document, string DocumentFileName)`
  - `QuotationsMetaWhatsAppSender(IQuotationTemplateOutbound outbound, IObjectStorage storage, IQuotationTemplateReadiness readiness)`

- [ ] **Step 1: Ajusta `Send` en las pruebas y escribe las nuevas**

`Send` pasa a recibir `string body` y `string? footer`, con `new ApprovedTemplate("qep_cotizacion_v2", "es", "UTILITY", "NAMED", components)`. El texto esperado de `SendsTheTemplateWithItsDocumentIntoANewOpenUnassignedConversation` se escribe literal.

```csharp
[Fact]
public async Task AnEditedTemplateSendsOnlyItsVariablesWithTheSendersNames()
{
    // Cuerpo: "Hola {{fullname}}, soy {{advisor}} de {{company}}. Te comparto {{order_number}}."
    // Meta recibe, en orden, fullname, advisor, company y order_number con su parameter_name, y no recibe total ni valid_until.
    // messaging.messages.text queda con el cuerpo sustituido.
}

[Fact]
public async Task ALegacyApprovedTemplateStillSendsItsFourVariablesAndShowsItsFooter()
{
    // Cuerpo viejo y pie «Este mensaje fue generado automáticamente.»: los cuatro parámetros, y el texto termina en el pie.
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
    // Total con un salto de línea y cuerpo sin {{total}}: Sent.
}

[Fact]
public async Task AValueTheRowUsesThatMetaWouldRejectFailsBeforeAnyCall()
{
    // FullName con un salto de línea y cuerpo con {{fullname}}: Failed(MessagingErrorCodes.MessageRejected) y ninguna llamada a Graph.
}
```

En `QuotationsMetaWhatsAppSenderTests`: `SendsThePrivatePdfBytesTheTemplateAndTheFormattedValues` lee `send.Template` de una `StubReadiness`, y una prueba nueva `ATemplateNoLongerApprovedIsSendFailed`: `FindApprovedAsync` devuelve `null` y el sender lanza `QuotationsDomainException` con código `quotation.whatsapp.send_failed`, sin llamar al outbound.

Borra de `QuotationTemplateDefinitionTests` lo que pruebe `Render` y `ParametersFor`, incluida `RenderFillsTheFourVariablesAndAppendsTheFooter`.

- [ ] **Step 2: Córrelas y comprueba que fallan**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~QuotationsMetaWhatsAppSenderTests" --nologo`
Expected: error de compilación, `QuotationTemplateSend` no tiene `Template`.

- [ ] **Step 3: Cambia el outbound y el sender**

- Agrega `TemplateSenderNames senderNames` al constructor de `QuotationTemplateOutbound`.
- En el paso 1, después de validar `WaId`: arma el diccionario con `fullname`, `order_number`, `total` y `valid_until`, llama a `senderNames.AddAsync(send.TenantId, send.SentByMemberId, SystemTemplateSend.VariablesOf(send.Template), values, cancellationToken)` y luego a `Build`. `null` devuelve `Failed(MessageRejected)`.
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
- Consumes: `ApprovedTemplate`, `SystemTemplateSend`, `TemplateSenderNames` (Task 2).
- Produces:
  - `BillingTemplateSend(Guid TenantId, Guid ConnectionId, Guid SentByMemberId, string WaId, Guid? CustomerId, string RecipientName, MessageTemplateKind Kind, ApprovedTemplate Template, IReadOnlyDictionary<string, string> Values)`
  - `BillingTemplateDefinition.LegacyPositionalKeys`: `["fullname", "service", "amount", "due_date"]` en `Reminder`, `["fullname", "overdue_count", "overdue_total"]` en `Overdue`.

- [ ] **Step 1: Ajusta y escribe las pruebas**

`ServiceContractsMetaWhatsAppSenderTests`:

```csharp
Assert.Equal("Ana Pérez", send.Values["fullname"]);
Assert.Equal("Hosting plan pro", send.Values["service"]);
Assert.Equal("$ 150.000", send.Values["amount"]);
Assert.Equal("10/03/2026", send.Values["due_date"]);
// vencido
Assert.Equal(("2", "$ 450.000"), (send.Values["overdue_count"], send.Values["overdue_total"]));
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
    // "Hola {{fullname}}, {{advisor}} te recuerda {{service}} hoy.": tres parámetros con nombre; advisor es el nombre de SentByMemberId.
}
```

- [ ] **Step 2: Córrelas y comprueba que fallan**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ServiceContractsMetaWhatsAppSenderTests" --nologo`
Expected: error de compilación, `BillingTemplateSend` no tiene `Values`.

- [ ] **Step 3: Cambia el outbound y el sender**

- `Parameters(message)` pasa a `Values(message)`, un `Dictionary<string, string>` con las claves de arriba. `CustomerName`, `Joined` y `Fit` siguen igual.
- El sender pasa `template` entero en `Template`.
- `BillingTemplateOutbound` recibe `TemplateSenderNames`, llama a `AddAsync` y a `Build(send.Template, values, BillingTemplateDefinitions.For(send.Kind).LegacyPositionalKeys)`, y usa `built.Text`, `built.DetailsJson` y `built.Send`. `null` devuelve `Failed(MessageRejected)`. La regla del `WaId` sigue en `IsAcceptable`.

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
        "¡Hola, {{fullname}}! Te compartimos en este mensaje la cotización *{{order_number}}*, por un valor total de *{{total}}*. Esta cotización estará vigente hasta el *{{valid_until}}*. Si tienes alguna pregunta, responde este mensaje y con gusto te ayudamos.",
        QuotationTemplateDefinition.BaseBody);
    Assert.Null(QuotationTemplateDefinition.Draft("qep_cotizacion", QuotationTemplateDefinition.BaseBody).Footer);
    var components = QuotationTemplateDefinition.Payload("handle", "qep_cotizacion", "Hola {{fullname}}, gracias.")["components"]!.AsArray();
    Assert.DoesNotContain(components, component => component!["type"]!.GetValue<string>() == "FOOTER");
}

// El cuerpo y el pie viejos siguen siendo adoptables: el texto ya no decide.
[Theory]
[InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{fullname}}, gracias."},{"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]""", true)]
[InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{fullname}}, gracias."}]""", true)]
[InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Hola {{fullname}}, gracias."}]""", false)]
[InlineData("""[{"type":"BODY","text":"Hola {{fullname}}, gracias."}]""", false)]
[InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{otra}}, gracias."}]""", false)]
[InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{fullname}}, gracias."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Sí"}]}]""", false)]
public void AdoptionLooksAtTheShapeNotTheText(string components, bool expected) =>
    Assert.Equal(expected, QuotationTemplateDefinition.IsAdoptable(components));
```

Handler (`ProvisionAsync` recibe `string? body = null`):

```csharp
[Fact]
public async Task TheEditedBodyGoesToMetaAndToTheRowTrimmed()
{
    var rig = new Rig();
    var dto = await rig.ProvisionAsync("  Hola {{fullname}}, soy {{advisor}}.  ");
    Assert.Equal("Hola {{fullname}}, soy {{advisor}}.", dto.Body);
    Assert.Null(dto.Footer);
    Assert.Equal("NAMED", rig.Repository.Rows.Single().ParameterFormat);
}

// Review Focus 4.
[Fact]
public async Task ARepeatedVariableTravelsWithOneExample()
{
    // "Total {{total}}, repito: {{total}} y gracias.": body_text_named_params tiene un solo elemento, con el ejemplo de total.
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
- El handler valida después de `EnsureAsync` y calcula `var body = (command.Body ?? QuotationTemplateDefinition.BaseBody).Trim();`.
- Adopción cuando `current is null` y hay `External` con el nombre base: si `IsAdoptable` y (`command.Body is null` o el cuerpo guardado es igual a `body`), `AdoptAsync`; si no, `quotation_conflict`.
- `IsDocumentHeader` se conserva. `IsAdoptable` exige `described.Header is null`, `UnsupportedReason == MediaHeader`, sin botones, cuerpo no vacío y variables dentro de la lista.
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
    Assert.Equal("Hola {{fullname}}, te recordamos que tu servicio {{service}} por {{amount}} vence el {{due_date}}. Si ya realizaste el pago, ignora este mensaje.", BillingTemplateDefinitions.Reminder.BaseBody);
    Assert.Equal("Hola {{fullname}}, tienes {{overdue_count}} cobro(s) vencido(s) por un total de {{overdue_total}}. Escríbenos para ponerte al día. Si ya pagaste, ignora este mensaje.", BillingTemplateDefinitions.Overdue.BaseBody);
}

[Fact]
public void ThePayloadIsNamedWithoutHeaderOrFooter()
{
    var payload = BillingTemplateDefinitions.Reminder.Payload("qep_cobro_recordatorio", "Hola {{fullname}}, gracias.");
    Assert.Equal("NAMED", payload["parameter_format"]!.GetValue<string>());
    var body = Assert.Single(payload["components"]!.AsArray());
    Assert.Equal("fullname", body!["example"]!["body_text_named_params"]![0]!["param_name"]!.GetValue<string>());
}

[Fact]
public async Task EachBodyGoesToItsTemplate() { }

[Fact]
public async Task AVariableOfTheOtherNoticeIsRejectedWithItsKey()
{
    // reminderBody con {{overdue_count}}: ValidationException con clave reminderBody y ninguna llamada a Meta.
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
    Assert.Equal(6, draft.Variables.Count);
}

[Fact]
public async Task ARejectedRowStartsFromItsBodyAndCarriesTheReason() { }

[Fact]
public async Task ACompatibleExternalStartsFromItsBody() { }

[Fact]
public async Task AnIncompatibleExternalStartsFromTheBaseText() { }

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

### Task 8: Documentación y suite completa

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
  `POSITIONAL` si Meta no manda el campo.
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
```
Expected: 0 errores y 0 pruebas rojas nuevas. Compara por nombre contra la base y deja el resultado literal en el reporte. `docs/estado.md` registra 17 fallas previas. Si faltan puertos, corre por proyecto y repite por nombre.

- [ ] **Step 4: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs(messaging): document editable system templates"
```
