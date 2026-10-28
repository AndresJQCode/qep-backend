# Zenvia por tenant — Implementation Plan (2 de 3, recortado a Zenvia)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** La cotización y el pedido se envían por WhatsApp con la conexión de Zenvia del propio tenant. Se retira la cuenta global `Quotations:WhatsApp:{ApiToken, FromNumber, TemplateId, OrderTemplateId}`.

**Architecture:** La conexión de Zenvia de Integrations gana dos campos, `templateId` (requerido) y `orderTemplateId` (opcional). Quotations declara un puerto, `IWhatsAppChannelDirectory`, que lista los canales de Zenvia que pueden enviar en ese momento. Su adaptador vive en Bootstrapper, sobre `IIntegrationConnections`, y Quotations nunca referencia `Modules.Integrations.Application`. Los handlers `SendQuotation` y `SendOrder` eligen el canal antes de cualquier efecto externo. Con cero canales responden 422 `*.whatsapp.no_channel` y con más de uno 422 `*.whatsapp.channel_required`. El `ZenviaWhatsAppSender` se vuelve un singleton sin credenciales, que recibe el canal en cada llamada. `LogWhatsAppSender` desaparece: las pruebas de integración registran dobles sólo en su host.

**Tech Stack:** .NET 10, EF Core + PostgreSQL, FluentValidation, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-21-envio-de-cotizaciones-por-canal-del-tenant-design.md`, decisiones D1, D11 y D18, §4.3 (sólo Zenvia) y §7. El recorte lo aprobó el owner el 2026-10-21. Queda **fuera**: Meta, la elección guardada (D6–D10), el diálogo de envío y la sección de Configuración (§4.4, §5.2). Eso es del plan 3.

**Worktree:** `../qep-backend-zenvia-por-tenant`, rama `feature/zenvia-por-tenant`, sacada de `develop` (`f98e3e6a`). Las rutas de este plan son relativas a ese worktree.

## Decisiones del recorte (owner, 2026-10-21)

- R1. **Más de una conexión de Zenvia disponible** ⇒ `422 quotation.whatsapp.channel_required` (en el pedido, `order.whatsapp.channel_required`). La elección llega con el plan 3.
- R2. **Ninguna disponible** ⇒ `422 quotation.whatsapp.no_channel` / `order.whatsapp.no_channel`. Un tenant que sólo tiene Meta pierde el envío hasta el plan 3.
- R3. **Origen Botánico** crea su conexión de Zenvia (token, número y `templateId` de la cuenta global) antes del despliegue. No hay migración que copie el `TemplateId` global: es la opción «a mano» del §7.2, decisión del autor del plan.
- R4. **El pedido también sale por la conexión:** `orderTemplateId` es campo opcional de la conexión. Si el canal elegido no lo tiene, la respuesta es `order.whatsapp.not_configured`, el código que ya existe.

## Global Constraints

- TDD obligatorio (CLAUDE.md): RED antes que GREEN, con la salida literal de ambos.
- Conventional commits en español, como el historial. **Sin atribución de IA** (CLAUDE.md): ni `Co-Authored-By` ni «Generated with». Revisar cada commit de un subagente.
- **No se corre la suite completa** (memoria del developer): sólo los proyectos de prueba del módulo tocado, con `--filter` cuando alcance.
- `Api.exe` / `Api` corriendo bloquea el build: detenerlo antes.
- Clave del proveedor: `zenvia` (`IntegrationProviders.Zenvia.Key`). Campos nuevos: `templateId` y `orderTemplateId`, con constantes en `ZenviaFieldKeys`.
- Patrón de los dos campos, que es el id de plantilla de Zenvia (un UUID, p. ej. `d0e79149-ed61-4784-9883-d86807f46ce5`): `^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z`, `maxLength: 36`, `FieldKind.Text`.
- Etiquetas y mensaje, con tuteo: `templateId` → «ID de la plantilla de cotización»; `orderTemplateId` → «ID de la plantilla de pedido»; `invalidMessage` de los dos → «Copia el ID de la plantilla tal como aparece en Zenvia (formato xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).»
- Códigos nuevos: `quotation.whatsapp.no_channel`, `quotation.whatsapp.channel_required`, `order.whatsapp.no_channel` y `order.whatsapp.channel_required`. Todos son `QuotationsDomainException` y salen como 422 por `ApiExceptionHandler`. No se arma ningún status a mano.
- La elección del canal va **fuera** del `try` que registra fallas, igual que `EnsureSendable`: no es un envío que falló, es uno que nunca empezó. No deja entrada `SendFailed` y la cotización no cambia de estado.
- `WhatsAppChannel.ToString()` no imprime `ApiToken`. Ni el token, ni el teléfono, ni la URL del PDF entran al log (regla actual del sender).
- Sobreviven `Quotations:WhatsApp:BaseUrl` y `Quotations:WhatsApp:DocumentUrlHours`, que no son de la cuenta.

## Review Focus

1. **Conexión vieja sin `templateId`:** se creó antes de este plan, sigue `Active` y no tiene plantilla. No cuenta como disponible: si es la única, el envío responde `no_channel` y no le manda a Zenvia un `templateId` vacío. La prueba va en la Task 3 (`SkipsConnectionWithoutTemplateId`).
2. **Conexión pausada, `NeedsAttention`, con Zenvia deshabilitado por el operador o con un secreto que no descifra:** `ResolveAsync` devuelve `null` y no cuenta. La prueba va en la Task 3 (`SkipsConnectionThatDoesNotResolve`).
3. **Reenvío de una cotización `Sent` en un tenant que se quedó sin canal:** 422 `quotation.whatsapp.no_channel`, sigue `Sent` y no se agrega historial `SendFailed`. La prueba va en la Task 4 (`ResendWithoutChannelKeepsSentAndRecordsNothing`).
4. **Pedido con un canal sin `orderTemplateId`:** responde `order.whatsapp.not_configured` **antes** de dibujar el PDF. La prueba va en la Task 2 (`OrderChannelWithoutOrderTemplateIsRejectedBeforeRendering`).
5. **El token no se filtra:** `WhatsAppChannel.ToString()` no lo incluye. La prueba va en la Task 2 (`ChannelToStringHidesTheToken`).

---

### Task 1: `templateId` y `orderTemplateId` en la conexión de Zenvia

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationProviders.cs`. Agregar `ZenviaFieldKeys.TemplateId = "templateId"` y `ZenviaFieldKeys.OrderTemplateId = "orderTemplateId"`, más las dos `FieldDefinition` después de `FromNumber`. Actualizar el `<summary>` de `Zenvia`: la plantilla **sí** es campo de la conexión (spec D11).
- Modify: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationCatalogTests.cs` y `CreateConnectionValidatorTests.cs`.
- Modify: `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/IntegrationsApiHarness.cs`. `ZenviaBody` lleva `templateId` con un default válido y un parámetro `templateId` para mandar otro. Agregar la constante `TemplateId = "d0e79149-ed61-4784-9883-d86807f46ce5"`.
- Barrido (gotcha de CLAUDE.md, «Endurecer una regla de dominio…»): `rg -n 'providerKey = "zenvia"|"providerKey":\s*"zenvia"|IntegrationProviders.Zenvia' tests`. Todo cuerpo crudo o `IntegrationConnection.Create(IntegrationProviders.Zenvia, …)` que deba quedar válido agrega `templateId`.

**Interfaces:**
- Produces: `ZenviaFieldKeys.TemplateId`, `ZenviaFieldKeys.OrderTemplateId` (los usa la Task 3).

- [ ] **Step 1: Escribir las pruebas que fallan**
  - `IntegrationCatalogTests.ZenviaDeclaresTemplateFields`: el proveedor `zenvia` tiene los campos, en orden, `apiToken`, `fromNumber`, `templateId` y `orderTemplateId`. `templateId` es `Required = true` y `Kind = Text`; `orderTemplateId` es `Required = false` y `Kind = Text`.
  - `CreateConnectionValidatorTests.ZenviaWithoutTemplateIdFails`: el cuerpo de Zenvia sin `templateId` falla con un error en la clave del campo que use hoy el validador para un requerido faltante. Mirar cómo prueba `fromNumber`.
  - `CreateConnectionValidatorTests.ZenviaTemplateIdMustBeAUuid`: `templateId = "plantilla-1"` falla con el `invalidMessage` de las Global Constraints. `orderTemplateId = "x"` falla igual. `orderTemplateId` ausente pasa.
- [ ] **Step 2: Correrlas y ver el RED.** `dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~IntegrationCatalogTests|FullyQualifiedName~CreateConnectionValidatorTests"`. Tienen que fallar por los campos que no existen.
- [ ] **Step 3: Agregar las constantes y las dos `FieldDefinition`** con los valores de las Global Constraints. Hacer el barrido de cuerpos crudos.
- [ ] **Step 4: GREEN.** Correr el mismo comando, y además `dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests` y `dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --filter "FullyQualifiedName~Connection"`. Todo verde.
- [ ] **Step 5: Commit.** `feat(integrations): la conexión de Zenvia lleva el id de sus plantillas`

### Task 2: Quotations elige el canal del tenant y lo pasa al sender

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppChannelDirectory.cs` (puerto y record).
- Create: `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppChannelSelection.cs`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppSender.cs`. Las dos firmas reciben el canal, y se corrigen los comentarios que citan `Quotations:WhatsApp:OrderTemplateId`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs` y `SendOrder.cs`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaWhatsAppSender.cs`. Usa `channel.FromNumber`, `channel.ApiToken`, `channel.QuotationTemplateId` y `channel.OrderTemplateId`. De `settings` sólo lee `BaseUrl`.
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs`. `AddWhatsAppSender` registra **siempre** `ZenviaWhatsAppSender` como singleton, sin condición y sin la rama de `LogWhatsAppSender`.
- Delete: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/LogWhatsAppSender.cs`.
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs`, `SendQuotationHandlerTests.cs`, `SendOrderHandlerTests.cs` y `ZenviaWhatsAppSenderTests.cs`.

**Interfaces:**
- Produces (namespace `Modules.Quotations.Application`, todo `public` salvo la selección):

```csharp
public sealed record WhatsAppChannel(
    Guid ConnectionId, string ApiToken, string FromNumber, string QuotationTemplateId, string? OrderTemplateId)
{
    public override string ToString(); // sin ApiToken
}

public interface IWhatsAppChannelDirectory
{
    /// Las conexiones de Zenvia del tenant que pueden enviar ahora: Active, visibles, con Zenvia
    /// habilitado y con token, número y templateId. Vacía si no hay ninguna.
    Task<IReadOnlyList<WhatsAppChannel>> ListAvailableAsync(Guid tenantId, CancellationToken cancellationToken);
}

internal static class WhatsAppChannelSelection
{
    public static WhatsAppChannel ForQuotation(IReadOnlyList<WhatsAppChannel> channels); // 0 → no_channel, >1 → channel_required
    public static WhatsAppChannel ForOrder(IReadOnlyList<WhatsAppChannel> channels);     // ídem con order.*, y sin OrderTemplateId → order.whatsapp.not_configured
}

public interface IWhatsAppSender
{
    Task SendQuotationAsync(WhatsAppChannel channel, WhatsAppQuotationMessage message, CancellationToken cancellationToken);
    Task SendOrderAsync(WhatsAppChannel channel, WhatsAppOrderMessage message, CancellationToken cancellationToken);
}
```

- Consumes: nada de otras tareas. El adaptador del puerto es la Task 3. Hasta entonces la API no resuelve el puerto, y las pruebas de integración de Quotations caen hasta la Task 4. **Por eso en esta tarea sólo se corren las unitarias.**

- [ ] **Step 1: Escribir las pruebas que fallan** (unitarias, con un `StubWhatsAppChannelDirectory(params WhatsAppChannel[])` nuevo en `QuotationsTestDoubles.cs`; `RecordingWhatsAppSender` guarda también el canal recibido)
  - `SendQuotationHandlerTests.SendsThroughTheOnlyAvailableChannel`: con un canal, el sender recibe ese canal (`ConnectionId` igual) y la cotización queda `Sent`.
  - `SendQuotationHandlerTests.NoChannelIsRejectedBeforeAnyExternalEffect`: con cero canales, `QuotationsDomainException` con `Code == "quotation.whatsapp.no_channel"`. El renderer de PDF no se llamó, el log de fallas está vacío y la cotización sigue `Draft`.
  - `SendQuotationHandlerTests.SeveralChannelsRequireAChoice`: con dos canales, `quotation.whatsapp.channel_required`, con las mismas tres aserciones.
  - `SendOrderHandlerTests.NoChannelIsRejected` / `SeveralChannelsRequireAChoice`: lo mismo con `order.whatsapp.no_channel` / `order.whatsapp.channel_required`.
  - `SendOrderHandlerTests.OrderChannelWithoutOrderTemplateIsRejectedBeforeRendering`: un canal con `OrderTemplateId = null` da `order.whatsapp.not_configured`, y el renderer del PDF del pedido no se llamó.
  - `SendOrderHandlerTests.SendsThroughTheOnlyAvailableChannel`.
  - `WhatsAppChannelTests.ChannelToStringHidesTheToken`: `new WhatsAppChannel(id, "tok-SENTINEL", "573001234567", "tpl", null).ToString()` no contiene `"tok-SENTINEL"` y sí contiene el id.
  - `ZenviaWhatsAppSenderTests`: adaptar las existentes para que las credenciales salgan del canal. El cuerpo enviado lleva `from = channel.FromNumber` y `templateId = channel.QuotationTemplateId` (pedido: `channel.OrderTemplateId`), y el header `X-API-TOKEN` es `channel.ApiToken`. Borrar o reescribir las pruebas que dependen de `Quotations:WhatsApp:OrderTemplateId` vacío: ese caso ahora lo cubre la selección.
- [ ] **Step 2: RED.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~SendQuotationHandlerTests|FullyQualifiedName~SendOrderHandlerTests|FullyQualifiedName~ZenviaWhatsAppSenderTests|FullyQualifiedName~WhatsAppChannelTests"`. Falla de compilación o de aserción.
- [ ] **Step 3: Implementar.** En `SendQuotation`, inyectar `IWhatsAppChannelDirectory`. Después de `quotation.EnsureSendable()` y **antes** del `try`: `var channel = WhatsAppChannelSelection.ForQuotation(await channels.ListAvailableAsync(command.TenantId, cancellationToken));`. En `SendOrder`, lo mismo después de `order.EnsureSendable()` y antes del `try`. El guard de `OrderTemplateId` del sender queda como defensa, con el mensaje «The WhatsApp channel has no order template: orders cannot be sent by WhatsApp yet.». Registrar el sender sin condición y borrar `LogWhatsAppSender`.
- [ ] **Step 4: GREEN.** Correr el mismo comando, y además `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests`. Todo verde, salvo `QuotationsOptionsValidatorTests`, que se ajusta en la Task 5. Si algo de ahí cae ahora, anotarlo y no tocarlo.
- [ ] **Step 5: Commit.** `feat(quotations): la cotización y el pedido salen por la conexión de Zenvia del tenant`

### Task 3: Adaptador en Bootstrapper sobre Integrations

**Files:**
- Create: `src/Bootstrapper/QuotationsWhatsAppChannelDirectory.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`. Agregar `services.AddScoped<IWhatsAppChannelDirectory, QuotationsWhatsAppChannelDirectory>();` junto a los otros adaptadores de Quotations.
- Create: `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationsWhatsAppChannelDirectoryTests.cs`. Lleva un doble de `IIntegrationConnections`, a mano y en el mismo archivo, o reutilizar uno si ya existe en `Bootstrapper.UnitTests`.

**Interfaces:**
- Consumes: `IIntegrationConnections.ListActiveAsync(tenantId, "zenvia", ct)` y `ResolveAsync(tenantId, id, ct)`, `ZenviaFieldKeys.*` (Task 1) e `IWhatsAppChannelDirectory` / `WhatsAppChannel` (Task 2).
- Produces: `internal sealed class QuotationsWhatsAppChannelDirectory(IIntegrationConnections connections) : IWhatsAppChannelDirectory`.

- [ ] **Step 1: Escribir las pruebas que fallan**
  - `MapsAResolvedZenviaConnection`: una activa que resuelve con `apiToken`, `fromNumber` y `templateId`, y sin `orderTemplateId`, da un `WhatsAppChannel` con esos valores y `OrderTemplateId == null`.
  - `MapsOrderTemplateIdWhenPresent`.
  - `SkipsConnectionWithoutTemplateId`: `templateId` ausente, o sólo con espacios, no aparece en la lista (Review Focus 1).
  - `SkipsConnectionThatDoesNotResolve`: `ResolveAsync` devuelve `null` y la lista queda vacía (Review Focus 2).
  - `ReturnsEveryAvailableConnection`: dos activas válidas dan dos canales, en el orden de `ListActiveAsync`.
  - `AsksIntegrationsForZenviaOnly`: el doble registra que `ListActiveAsync` se llamó con `"zenvia"`.
- [ ] **Step 2: RED.** `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~QuotationsWhatsAppChannelDirectoryTests"`
- [ ] **Step 3: Implementar.** `apiToken` sale de `Secrets`; `fromNumber`, `templateId` y `orderTemplateId` salen de `Fields`. Un `orderTemplateId` en blanco se mapea a `null`. El `<summary>` lo dice: es el único punto donde Quotations e Integrations se tocan, como `MessagingConnectionDirectory`.
- [ ] **Step 4: GREEN.** Correr el mismo comando, y además `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests` y `dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~Quotations"`.
- [ ] **Step 5: Commit.** `feat(bootstrapper): canales de WhatsApp de Quotations desde las conexiones de Zenvia`

### Task 4: Hosts de prueba con dobles y pruebas de integración del envío

**Files:**
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestDoubles.cs`, con `RecordingWhatsAppChannelDirectory` (lista mutable de canales, por defecto un canal válido con `OrderTemplateId`) y `RecordingWhatsAppSender` (guarda canal y mensaje).
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs`. En `ConfigureServices`: `RemoveAll<IWhatsAppChannelDirectory>()` + `AddSingleton` del doble, y lo mismo con `IWhatsAppSender`, expuestos como propiedades estáticas o por host, siguiendo el estilo de `ObjectStorage`. Borrar las tres líneas `UseSetting("Quotations:WhatsApp:…", string.Empty)` y su comentario, y reemplazarlo por uno que diga por qué hay dobles (D18: ninguna prueba llama a Zenvia).
- Modify: `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs`, igual. `CreateSentQuotationAsync` tiene que seguir enviando. Si Reporting no ve el proyecto de pruebas de Quotations, el doble se copia en su harness: dos dobles chicos son aceptables en pruebas.
- Modify: `PipelineApiHarness.cs`, `ShopifyApiHarness.cs`, `IntegrationsApiHarness.cs`, `PosApiHarness.cs` y `MessagingApiHarness.cs`. Borrar las tres líneas `Quotations:WhatsApp:*`, que quedan sin efecto. Si alguno de esos hosts envía una cotización o un pedido (`rg -n '/send"' tests/Modules/<Modulo>`), también registra los dobles.
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderSendApiTests.cs`. Su `<summary>` cita `LogWhatsAppSender`.
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppChannelApiTests.cs`.

**Interfaces:**
- Consumes: `IWhatsAppChannelDirectory`, `IWhatsAppSender` y `WhatsAppChannel` (Task 2).

- [ ] **Step 1: RED de la base.** `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationSendVoidApiTests|FullyQualifiedName~OrderSendApiTests"`. Hoy fallan: sin los dobles, el host usa el adaptador real y el tenant de prueba no tiene conexión, así que responde `no_channel`. Guardar la salida literal.
- [ ] **Step 2: Escribir las pruebas nuevas en `WhatsAppChannelApiTests`**
  - `SendWithoutChannelResponds422AndKeepsDraft`: el doble con cero canales. `POST …/quotations/{id}/send` da 422 con `code = "quotation.whatsapp.no_channel"`, el `GET` posterior muestra `Draft` y el historial no tiene `SendFailed`.
  - `SendWithSeveralChannelsResponds422`: dos canales, `quotation.whatsapp.channel_required`.
  - `ResendWithoutChannelKeepsSentAndRecordsNothing`: se envía con un canal, se vacía el doble y se reenvía. Da 422 `no_channel`, sigue `Sent` y el historial no tiene `SendFailed` (Review Focus 3).
  - `SendUsesTheTenantChannel`: el sender registró el `ConnectionId` del doble.
  - `OrderSendWithoutChannelResponds422`: `order.whatsapp.no_channel`.
- [ ] **Step 3: Implementar** los dobles y los cambios de harness descritos en Files.
- [ ] **Step 4: GREEN.** `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests` y `dotnet test tests/Modules/Reporting/Modules.Reporting.IntegrationTests`. Para los otros cinco harnesses alcanza con `dotnet build` de sus proyectos de prueba, salvo que alguno envíe: ahí se corre la prueba que envía.
- [ ] **Step 5: Commit.** `test: los hosts de prueba envían por dobles del canal de WhatsApp`

### Task 5: Retirar la cuenta global de la configuración y la documentación

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsOptions.cs`. `WhatsAppOptions` se queda sólo con `BaseUrl` y `DocumentUrlHours`; se borran `ApiToken`, `FromNumber`, `TemplateId` y `OrderTemplateId`, y se corrige el comentario de la clase.
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsOptionsValidator.cs`. Fuera la exigencia de las tres claves en `Production` y el comentario de `OrderTemplateId`. Lo demás queda.
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsOptionsValidatorTests.cs`.
- Modify: `src/Api/appsettings.json` (fuera `TemplateId` y `OrderTemplateId`; si `WhatsApp` queda vacío, fuera la sección) y `src/Api/appsettings.example.json` (la sección queda con `DocumentUrlHours` y `BaseUrl`).
- Modify: `k8s/prod-configMap.yaml` (fuera `Quotations__WhatsApp__TemplateId` y `…OrderTemplateId`) y `k8s/prod-secret.yaml` (fuera `Quotations__WhatsApp__ApiToken` y `…FromNumber`).
- Modify: `README.md`. Las secciones de configuración, secretos de usuario y «Plantilla de WhatsApp (Zenvia)» dicen ahora que el token, el número y los ids de plantilla son campos de la conexión de Zenvia del tenant, en Integraciones. Quitar los `dotnet user-secrets set "Quotations:WhatsApp:…"` y agregar el paso de despliegue de R3 (crear la conexión de Origen Botánico antes) más el retiro de `QUOTATIONS_WHATSAPP_*` del grupo `Backend-prod` después.
- Modify: `CLAUDE.md`, en dos entradas:
  - En «El texto de la plantilla no se edita: se crea otra», cambiar «apuntar `Quotations:WhatsApp:TemplateId` al `id` nuevo» por «editar el `templateId` de la conexión de Zenvia de cada tenant». La regla de commitear en el mismo commit pasa a ser: cambiar las variables obliga a actualizar el `templateId` de las conexiones **en el mismo despliegue**.
  - En la de `Notifications:EmailProvider`, sacar «Mismo par que `Quotations:WhatsApp:*` con Zenvia».
- Verificación transversal (criterio 6 del spec): `rg -n "Quotations:WhatsApp:(ApiToken|FromNumber|TemplateId|OrderTemplateId)|Quotations__WhatsApp__(ApiToken|FromNumber|TemplateId|OrderTemplateId)|QUOTATIONS_WHATSAPP_(API_TOKEN|FROM_NUMBER|TEMPLATE_ID)|LogWhatsAppSender" -g '!docs/superpowers/**'` no devuelve nada. Revisar también `azure-pipelines*.yml`, si los hay.

- [ ] **Step 1: Escribir las pruebas que fallan.** En `QuotationsOptionsValidatorTests`, `ProductionStartsWithoutWhatsAppCredentials`: en `Production`, sin ninguna clave `WhatsApp:*` salvo los defaults, el validador da `Succeeded`. Borrar las pruebas que exigían las tres claves.
- [ ] **Step 2: RED.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationsOptionsValidatorTests"`
- [ ] **Step 3: Implementar** los cambios de opciones, validador, appsettings, k8s y documentación.
- [ ] **Step 4: GREEN.** El mismo comando, más `dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests"`, más el `rg` de arriba vacío, más `dotnet build` de la solución sin warnings nuevos.
- [ ] **Step 5: Commit.** `chore(quotations): se retira la cuenta global de Zenvia de la configuración`

## Cierre

- Revisión de toda la rama, merge `--ff-only` a `develop` (nunca a `main`) y, en el mismo merge o justo después, el commit `docs:` de `docs/estado.md` en `develop`: la nota «Envío por canal, lo que sigue igual» pasa a decir que Zenvia ya sale por la conexión del tenant y que Meta y la elección siguen en el plan 3.
- Frontend: el formulario de conexión se arma desde `fields` del catálogo (`connection-form-dialog.tsx`), así que los dos campos aparecen solos. Antes de mergear, verificar a mano que un campo `Text` opcional se dibuja y se puede dejar vacío. Si `describe-integration-failure.ts` o la pantalla de cotización tienen copy para los códigos nuevos, eso es del frontend y queda anotado, no se hace en esta rama.
