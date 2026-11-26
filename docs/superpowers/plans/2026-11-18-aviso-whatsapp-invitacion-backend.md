# Aviso por WhatsApp de la invitación de distribuidor — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Si la invitación de distribuidor trae `phone`, además del correo de hoy sale por outbox un aviso por WhatsApp (sin link
ni token) con una plantilla de sistema nueva, `WholesaleInvitationNotice`, y el detalle muestra si salió o falló.

**Architecture:** Mensajería gana el tipo de plantilla, su aprovisionamiento por conexión (calco de `SupportReply`, que es el
calco sin encabezado de `OrderTemplateProvisioning`), su señal de aprobación y un `IWholesaleInvitationNoticeOutbound` (el núcleo
de `BillingTemplateOutbound`, con `CustomerId: null` y `OpenByPhoneAsync`). Mayorista guarda teléfono, conexión, `generation` y
estado del aviso; el commit de invitar/reenviar escribe `wholesale.distributor-invitation-notice.v1` junto al evento del correo.
Un procesador con inbox propio en `Modules.Wholesale.Infrastructure` consume el evento y manda por un puerto
`IWholesaleInvitationNoticeSender`, cuyo adaptador vive en `src/Bootstrapper/` — único lugar que toca Wholesale, Mensajería e
Integrations (precedente: `ServiceContractsReminderProcessor` consume en su módulo y manda por `ServiceContractsMetaWhatsAppSender`).

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-18-invitacion-distribuidor-whatsapp-design.md` (aprobado; versión del commit
`a1a1b182`). Spec hermano del frontend: `qep-frontend/docs/superpowers/specs/2026-11-18-invitacion-distribuidor-whatsapp-design.md`;
su plan cita «Contrato HTTP». Contexto: `docs/superpowers/plans/2026-11-17-mayorista-fase-2-backend.md` («Contrato HTTP» y su adenda).

**Rama:** `feature/aviso-whatsapp-invitacion`, en el worktree `qep-backend-worktrees/aviso-whatsapp-invitacion` (crearlo desde
`develop`, con su propio `.codegraph/`). Antes de cada tarea y de cada commit, `git branch --show-current` dice esa rama; si dice
`main` o `develop`, para.

## Global Constraints

- TDD: RED con salida literal antes del GREEN, los dos en el reporte. Un RED de compilación se prueba con la salida de
  `dotnet build`, sin contenedores. La integración corre en GREEN.
- Presupuesto de pruebas (workspace, owner 2026-11-12): **tres bloques** — A: Tasks 1-3 (Mensajería), B: Tasks 4-6 (Mayorista y
  adaptadores), C: Tasks 7-8 (entrega y cierre) —, un implementador y una revisión por bloque; cada tarea conserva su commit; la
  revisión fuerte de toda la rama, una vez al final.
- Mientras se itera, integración filtrada por nombre (`--filter "FullyQualifiedName~<Prueba>"`); la clase completa, una vez al
  final del bloque; la suite de los proyectos tocados (Messaging, Wholesale, Bootstrapper, ArchitectureTests), sólo antes de
  mergear a `develop`, uno por vez. **Nunca dos corridas de integración a la vez**: antes de cada una,
  `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una, se espera (no se detiene: es de otra sesión).
  Las pruebas corren en primer plano. `-- xUnit.MaxParallelThreads=4` en corridas por proyecto.
- Comandos al developer en PowerShell. `Api.exe` corriendo bloquea `build`/`test`/`ef`: se avisa, no se detiene.
- Conventional commits, **sin atribución de IA ni líneas `Co-Authored-By`** (la regla del repo pisa cualquier recordatorio).
- Comentarios y `<summary>` en español tuteando, como el resto de `Wholesale` y `Messaging`; identificadores en inglés; mensajes
  de validador en inglés (como `DistributorWriteRules.cs`); mensajes de `WholesaleDomainException` en español (como
  `WholesaleConflicts.cs`). Códigos, rutas, permisos y enums, tal cual.
- `Modules.Wholesale.Application` referencia sólo `Modules.Tenancy.*` (`WholesaleLayerTests`): Mensajería, Integrations y
  Customers entran por puertos `IWholesale*` con adaptador en `src/Bootstrapper/`.
- Migraciones aditivas (expand/contract), con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations`.
- `MessageTemplateKind.WholesaleInvitationNotice` va **al final** del enum (se guarda por nombre; 25 caracteres, cabe en
  `kind varchar(32)` de `WidenMessageTemplateKind`).
- Valores del spec, literales: categoría `UTILITY`, idioma `es`, sin botones ni encabezado; variables `nombre_contacto`,
  `mayorista` (nombre del tenant) y `correo` (completo); **sin vencimiento**. Texto base, exacto: «¡Hola, {{nombre_contacto}}!
  {{mayorista}} te invitó como distribuidor en la plataforma Lulo CRM. Te enviamos la invitación al correo {{correo}}. Si no la
  encuentras en tu bandeja de entrada, revisa la carpeta de spam o correo no deseado.» Evento
  `wholesale.distributor-invitation-notice.v1`. Códigos 422 `wholesale.distributor.whatsapp_connection_required` y
  `wholesale.distributor.whatsapp_unavailable`; claves del 422 `Phone` y `WhatsAppConnectionId`. Códigos de falla del aviso:
  `template_not_approved`, `connection_unavailable:<motivo>`, `send_failed:<código>` y, en el reenvío, `whatsapp_unavailable`.
- El token y el link nunca viajan en el evento del aviso ni llegan a Mensajería. El teléfono no se loguea (logs sólo con ids).
- Un fallo del aviso nunca frena, retrasa ni cambia el correo o la invitación.
- `docs/estado.md` no se toca en la rama; se actualiza en `develop` al mergear.

## Decisiones de este plan (no del spec)

- **D-1. El consumidor vive en `Modules.Wholesale.Infrastructure`, el envío en `src/Bootstrapper/`.** El spec dice «un worker en
  `Bootstrapper`», pero Bootstrapper no tiene `DbContext` ni consumidor de outbox: cada consumidor reclama con su inbox propio
  (`Purchases/…/Messaging/ClaimedOutboxConsumer.cs`, `PurchasesInbox.cs`; sexta copia, mismo criterio que el ruling X5). El
  precedente que cita el spec, `ServiceContractsMetaWhatsAppSender`, es un adaptador llamado desde el procesador de su módulo
  (`ServiceContractsReminderProcessor.cs:28`). Se cumple el motivo del spec: sólo Bootstrapper toca los tres módulos.
- **D-2. `RecordInvitationNotice` es interno de Mayorista** (`DistributorInvitationNotices.RecordAsync`) y recibe además
  `tenantId`: todo método de repositorio lo recibe (CLAUDE.md, «Aislamiento de tenant»).
- **D-3. El resultado del aviso no sube `Version` ni `UpdatedAt`** del distribuidor: si lo hiciera, el formulario abierto daría
  412 (precedente CLAUDE.md, «Un vínculo `Linked` del worker no sube la versión del ticket»).
- **D-4. Un evento de `generation` vieja no se manda** (además de no pisar el estado): el procesador relee el distribuidor y, si
  no existe, no está `Invited` o su `generation` cambió, marca el mensaje procesado sin enviar.
- **D-5. El `clientId` del mensaje es el id del mensaje del outbox**: un reintento tras un commit fallido de Mayorista no manda
  dos avisos (el reclamo de `IOutboundMessages` es idempotente por `(conversation_id, client_id)`; si la fila ya salió, se
  devuelve `Sent` sin llamar a Meta).
- **D-6. Autor del mensaje:** la membresía activa de quien invita o reenvía
  (`IMembershipDirectory.FindActiveMembershipIdAsync(SubjectId, tenantId)`), y si no tiene (stub de desarrollo), el asesor
  (`AdvisorMemberId`, ya validado como membresía activa). La fila de Mensajería exige autor (`BillingTemplateSend.SentByMemberId`).
- **D-7. `label` de una conexión** = su `DisplayPhoneNumber` (el mock del frontend muestra «+57 300 …») y, sin número, su nombre.
  `connectionLabel` de un aviso cuya conexión ya no está activa = `"Sin nombre"` (`DistributorComposer.UnknownName`).
- **D-8. Auditoría:** `audit.entries` no tiene columna de datos; el flag va como cadena `clave=valor` en `changed_fields`
  (precedente `ICustomersAuditPublisher.PublishWithChangedFields`, A-8): `"whatsAppNotice=true"` si se escribió el evento del
  aviso, `"whatsAppNotice=false"` si no.
- **D-9. `PUT` sin cambio no resuelve conexión:** con el mismo teléfono normalizado y `whatsAppConnectionId` nulo o igual al
  guardado, no se consulta Integrations; editar la lista no falla porque la conexión dejó de estar lista. El plan del frontend
  cuenta con esto: en un `PUT` sin cambio manda siempre el teléfono guardado con `whatsAppConnectionId: null`.
- **D-10. Nombre de la plantilla:** `qep_mayorista_invitacion` y versiones `qep_mayorista_invitacion_v{n}`, prefijo reservado
  `qep_mayorista_invitacion_`. Las variables son las cuatro fijas más `mayorista` y `correo`
  (`SystemTemplateVariables`: «Las cuatro fijas van primero en todas»).

## Pendiente del owner

- **P-2. `PUT` fuera de `Invited`.** El spec dice «sólo `Invited`» y `PUT` edita en cualquier estado. Se implementa: fuera de
  `Invited`, `phone` y `whatsAppConnectionId` se ignoran (sin 422), porque el formulario reenvía todos los campos.
- **P-3. `PUT` que cambia teléfono o conexión** sube `generation` y deja `whatsAppNotice: null` hasta el próximo «Reenviar» (no
  hay aviso para el número nuevo). `phone` ausente o `null` en un `PUT` de un invitado **borra** teléfono y conexión.

## Review Focus

1. Un `PUT` de un invitado con el mismo teléfono cuya conexión ya no está lista responde 200 y no toca `generation` (D-9) —
   prueba en Task 5.
2. Un resultado o un evento de `generation` vieja (tras `PUT` o «Reenviar») ni pisa el estado ni manda (D-4) — pruebas en Tasks 4 y 7.
3. Registrar el resultado no sube `Version`: el `If-Match` leído antes sigue valiendo (D-3) — pruebas en Tasks 4 y 7.
4. El evento del aviso no contiene el token y la auditoría no contiene el teléfono — prueba en Task 7.
5. Un reintento después de un envío que sí salió no manda un segundo aviso (D-5) — prueba en Task 3.

## Mapa de archivos

| Capa | Archivos |
| --- | --- |
| Messaging.Domain | `MessageTemplate.cs` (enum, `MessageTemplateName`, `AdoptAsWholesaleInvitationNotice`), `MessagingErrorCodes.cs` |
| Messaging.Application | Create `WholesaleInvitationNoticeTemplateProvisioning.cs`, `WholesaleInvitationNoticeOutbound.cs`, `PlainTemplateOutbound.cs`; Modify `SystemTemplateVariables.cs`, `SystemTemplateDrafts.cs`, `TemplateManagement.cs`, `TemplateManagementDtos.cs`, `TemplateManagementPorts.cs`, `IMessagingAuditRecorder.cs`, `BillingTemplateOutbound.cs` |
| Messaging.Infrastructure | Create `Persistence/WholesaleInvitationNoticeTemplateReadiness.cs`, migración `AddWholesaleInvitationNoticeTemplateKind`; Modify `MessagingDbContext.cs`, `MessagingSaveChanges.cs`, `MessageTemplateRepository.cs`, `MessagingInfrastructureExtensions.cs` |
| Messaging.Api | `TemplateManagementEndpoints.cs` |
| Wholesale.Domain | Create `InvitationNoticeStatus.cs`; Modify `Distributor.cs`, `DistributorInvitation.cs`, `WholesaleErrorCodes.cs` |
| Wholesale.Application | Create `WholesaleWhatsAppPorts.cs`, `DistributorWhatsAppNotice.cs`, `DistributorInvitationNotices.cs`; Modify `InviteDistributor.cs`, `UpdateDistributor.cs`, `TransitionDistributor.cs`, `DistributorWriteRules.cs`, `Ports.cs`, `DistributorDtos.cs`, `DistributorComposer.cs`, `GetDistributorOptions.cs`, `WholesaleConflicts.cs` |
| Wholesale.Infrastructure | Create `Messaging/ClaimedOutboxConsumer.cs`, `Messaging/InvitationNoticeProcessor.cs`, `Messaging/InvitationNoticeWorker.cs`, `Persistence/WholesaleInbox.cs`, migraciones `AddInvitationWhatsAppNotice`, `AddWholesaleInbox`; Modify `WholesaleDbContext.cs`, `WholesaleEventPublisher.cs`, `WholesaleInfrastructureExtensions.cs` |
| Wholesale.Api | `WholesaleEndpoints.cs` (cuerpos) |
| Bootstrapper | Create `WholesalePhoneNormalizer.cs`, `WholesaleWhatsAppConnections.cs`, `WholesaleInvitationNoticeSender.cs`; Modify `QepServiceCollectionExtensions.cs` |
| Pruebas | Messaging Unit/Integration, Wholesale Unit/Integration, `Bootstrapper.UnitTests`, `ArchitectureTests/MultiReplicaInventory.cs` |

## Contrato HTTP

Lo que el plan del frontend puede dar por hecho. JSON en camelCase como el resto (`System.Text.Json` web); enums por nombre.

### Distribuidores — `/api/v1/tenants/{tenantId}/wholesale/distributors` (cambios sobre fase 2)

| Método y ruta | Cambio |
| --- | --- |
| `POST /` | Cuerpo suma `phone: string \| null`, `whatsAppConnectionId: Guid \| null` |
| `PUT /{id}` | Cuerpo suma los mismos dos; sólo cuentan con `status = Invited` (P-2); `phone` ausente o `null` borra (P-3) |
| `POST /{id}/resend-invitation` | Sin cuerpo; con teléfono, aviso nuevo (o `Failed` `whatsapp_unavailable`) |
| `GET /options` | Suma `whatsAppConnections` |
| `GET /{id}`, `GET /` | Suman `phone`; el detalle suma `invitation.whatsAppNotice` |

```
DistributorOptions  { priceLists, advisors, companies,
                      whatsAppConnections: [{ id: Guid, label: string }] }   // whatsapp-cloud activas con la plantilla aprobada; [] ⇒ sin aviso
DistributorSummary  { …fase 2…, phone: string | null }                       // E.164 con «+», p. ej. "+573001234567"
DistributorDetail   { …fase 2…, phone: string | null,
                      invitation: { sentAt, expiresAt, expired, acceptedAt,
                                    whatsAppNotice: { status: 'Pending'|'Sent'|'Failed',
                                                      failureCode: string | null,
                                                      connectionLabel: string } | null } }
```

- `phone` se recorta, ≤ 32 caracteres antes de normalizar, y se guarda en E.164 con el país del tenant
  (`TenantRegionalSettings.Country`, `CO` sin fila). Vacío o sólo espacios = `null`. Uno que no normaliza: 422 `validation.failed`
  con `errors.Phone`. `whatsAppConnectionId = 00000000-…`: `errors.WhatsAppConnectionId`. Sin `phone`, `whatsAppConnectionId` se ignora.
- Conexión (sólo con `phone`): sin `whatsAppConnectionId`, una lista ⇒ esa; varias ⇒ 422
  `wholesale.distributor.whatsapp_connection_required`; ninguna ⇒ 422 `wholesale.distributor.whatsapp_unavailable`. Un id que no
  está en `whatsAppConnections` ⇒ `whatsapp_unavailable`. En un `PUT`, el mismo teléfono con `whatsAppConnectionId` nulo o igual
  al guardado no resuelve nada (D-9). Orden en `POST`: 403 → 422 validación → 404 lista → 422 asesor → 422 empresa → 409
  documento → 422 `Phone` → 422 conexión.
- `whatsAppNotice`: `null` sin teléfono y tras un `PUT` que cambió teléfono o conexión (P-3). `Pending` desde el commit hasta el
  resultado; `failureCode` sólo con `Failed`: `template_not_approved`, `connection_unavailable:<motivo>` (los de
  `ConnectionUnavailableReason`, p. ej. `paused`), `send_failed:<código>` (p. ej. `send_failed:messaging.message.rejected`) y
  `whatsapp_unavailable` (reenvío con la conexión guardada ya no lista). Un código desconocido se muestra genérico.
- El resultado del aviso **no** sube `version`.

### Plantilla de aviso — `/api/v1/tenants/{tenantId}/messaging/connections/{connectionId}/template-management`

Permiso `messaging.template.manage` (el mismo de `/order`), sin gate de módulo en el backend.

| Método y ruta | Cuerpo | Respuesta |
| --- | --- | --- |
| `POST /wholesale-invitation-notice` | opcional `{ body: string \| null }` | 201 `ManagedTemplateDto` |
| `GET /wholesale-invitation-notice/draft` | — | `SystemTemplateDraftDto { body, locked, rejectionReason, variables: [{ key, label, example }] }` |
| `GET /` | — | `TemplateManagementDto` suma `wholesaleInvitationNoticeTemplate: { templateId, status, statusReason } \| null` |

- Errores: 422 `messaging.template.wholesale_invitation_notice_exists` (ya viva, o dos aprovisionamientos a la vez),
  `messaging.template.wholesale_invitation_notice_conflict` (una `qep_mayorista_invitacion` externa incompatible), más los de
  aprovisionamiento que ya existen (`connection_unavailable`, `name_taken`, `validation.failed` con `errors.body`).
- `variables`: `nombre_contacto`, `asesor`, `empresa`, `saludo`, `mayorista`, `correo`, en ese orden.

---

## Bloque A — Mensajería (Tasks 1-3, un implementador)

### Task 1: Tipo, definición y esquema de la plantilla

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessageTemplate.cs:6-30` (enum), `:93-135` (`MessageTemplateName`),
  junto a `AdoptAsSupportReply` (`:323`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs:185-209`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/SystemTemplateVariables.cs:43-74`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/WholesaleInvitationNoticeTemplateProvisioning.cs` (sólo la definición en esta tarea)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs:140-162,804-846`,
  `MessagingSaveChanges.cs:113-138`, `MessageTemplateRepository.cs:34-48`, `TemplateManagementPorts.cs:28` (repositorio)
- Create: migración `AddWholesaleInvitationNoticeTemplateKind`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WholesaleInvitationNoticeTemplateDefinitionTests.cs`

**Interfaces:**
- Produces: `MessageTemplateKind.WholesaleInvitationNotice`; `MessageTemplateName.WholesaleInvitationNoticeName = "qep_mayorista_invitacion"`,
  `WholesaleInvitationNoticePrefix = "qep_mayorista_invitacion_"` (en `IsReserved`);
  `MessagingErrorCodes.TemplateWholesaleInvitationNoticeExists = "messaging.template.wholesale_invitation_notice_exists"`,
  `TemplateWholesaleInvitationNoticeConflict = "messaging.template.wholesale_invitation_notice_conflict"`;
  `SystemTemplateVariables.WholesaleInvitationNotice` (`.. Fixed`, `mayorista` «Nombre del mayorista» «Distribuciones Andinas»,
  `correo` «Correo de la invitación» «compras@distribuidora.co»);
  `public static class WholesaleInvitationNoticeTemplateDefinition` con `Name`, `Language = "es"`, `Category = "UTILITY"`,
  `BaseBody` (el texto exacto de Global Constraints), `WholesalerKey = "mayorista"`, `EmailKey = "correo"`, `Examples`,
  `NameFor(int)`, `VersionOf(string?)`, `Draft(name, body)`, `Payload(name, body)`, `IsAdoptable(componentsJson)`,
  `internal StoredBody(componentsJson)` — misma forma que `SupportReplyTemplateDefinition` (sin clave vieja admitida);
  `MessageTemplate.AdoptAsWholesaleInvitationNotice(DateTimeOffset)`;
  `IMessageTemplateRepository.FindWholesaleInvitationNoticeAsync(Guid tenantId, Guid connectionId, CancellationToken)`;
  `MessagingDbContext.MessageTemplateWholesaleInvitationNoticeIndex = "UX_message_templates_wholesale_invitation_notice"`.

- [ ] **Step 1: Escribe las pruebas** (calco de `SupportReplyTemplateDefinitionTests`):
  `TheDefinitionIsTheReservedNameInSpanishUtility` (`"qep_mayorista_invitacion"`, `"es"`, `"UTILITY"`),
  `TheBaseBodyIsTheApprovedText` (`Assert.Equal` con el texto exacto de Global Constraints),
  `TheVariablesAreTheFixedOnesPlusWholesalerAndEmail`
  (`["nombre_contacto","asesor","empresa","saludo","mayorista","correo"]`; el cuerpo base usa
  `["nombre_contacto","mayorista","correo"]` por `TemplateComponentsBuilder.VariablesOf`),
  `TheBaseBodyNeitherStartsNorEndsWithAVariable` (las dos regex de `SupportReplyTemplateDefinitionTests.cs:52-53`),
  `TheBaseBodyNamesNoLinkNorToken` (`Assert.DoesNotContain("http", …)`, `Assert.DoesNotContain("token", …)`),
  `NoExampleCarriesACharacterMetaRejects`, `ThePayloadHasOnlyTheBodyWithNamedExamples`,
  `IsAdoptableAcceptsItsOwnComponentsAndRejectsAHeaderOrButtons`, `NameForAndVersionOfRoundTrip` (`1 → qep_mayorista_invitacion`,
  `2 → qep_mayorista_invitacion_v2`; `VersionOf("qep_mayorista_invitacion_v02") == null`), `TheNameAndThePrefixAreReserved`,
  `TheKindIsStoredByNameAtTheEnd` (`Enum.GetValues<MessageTemplateKind>().Last() == WholesaleInvitationNotice`).
- [ ] **Step 2: RED de compilación**
  Run: `dotnet build tests/Modules/Messaging/Modules.Messaging.UnitTests` → `error CS0117: 'MessageTemplateKind' does not contain a definition for 'WholesaleInvitationNotice'`.
- [ ] **Step 3: Implementa** lo de Interfaces. En `MessagingDbContext` suma `'WholesaleInvitationNotice'` al `CHECK`
  `CK_message_templates_kind` y el índice único parcial `(tenant_id, connection_id)` con `kind = 'WholesaleInvitationNotice'`;
  `MessagingSaveChanges` lo traduce por nombre a `TemplateWholesaleInvitationNoticeExists`. Genera la migración: `Up` = drop/add
  del `CHECK` + `CreateIndex`; `Down` = `DELETE … WHERE kind = 'WholesaleInvitationNotice'`, drop índice, `CHECK` anterior (calco
  de `20261116064907_AddSupportFormalAndSurveyTemplateKinds.cs`). El largo de `kind` no cambia.
- [ ] **Step 4: GREEN**
  Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~WholesaleInvitationNoticeTemplateDefinitionTests"` → PASS.
- [ ] **Step 5: Commit** — `feat(messaging): add wholesale invitation notice template kind`

### Task 2: Aprovisionamiento, borrador, señal y pantalla

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/WholesaleInvitationNoticeTemplateProvisioning.cs`,
  `SystemTemplateDrafts.cs`, `TemplateManagement.cs:73-90`, `TemplateManagementDtos.cs:44-70`, `TemplateManagementPorts.cs:79-100`,
  `IMessagingAuditRecorder.cs:136`
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/WholesaleInvitationNoticeTemplateReadiness.cs`
- Modify: `MessagingInfrastructureExtensions.cs:132`, `src/Modules/Messaging/Modules.Messaging.Api/TemplateManagementEndpoints.cs`,
  `src/Bootstrapper/QepServiceCollectionExtensions.cs:1324-1325` (handlers a mano)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/WholesaleInvitationNoticeTemplateProvisioningApiTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `ProvisionWholesaleInvitationNoticeTemplateCommand(Guid TenantId, Guid ConnectionId, string? Body = null) : ICommand<ManagedTemplateDto>`
  + validador (cuerpo con `SystemTemplateVariables.WholesaleInvitationNotice`) + `ProvisionWholesaleInvitationNoticeTemplateHandler`
  (calco paso a paso de `ProvisionSupportReplyTemplateHandler`); `GetWholesaleInvitationNoticeTemplateDraftQuery(Guid TenantId, Guid ConnectionId) : IQuery<SystemTemplateDraftDto>`
  + handler; `MessagingAuditActions.TemplateWholesaleInvitationNoticeProvisioned = "messaging.template.wholesale_invitation_notice_provisioned"`;
  `WholesaleInvitationNoticeTemplateStatusDto(Guid TemplateId, string Status, string? StatusReason)` y el parámetro final
  `WholesaleInvitationNoticeTemplateStatusDto? WholesaleInvitationNoticeTemplate = null` de `TemplateManagementDto`;
  `public interface IWholesaleInvitationNoticeReadiness { Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken); }`;
  rutas `POST /wholesale-invitation-notice` y `GET /wholesale-invitation-notice/draft` con `ProvisionWholesaleInvitationNoticeTemplateRequest(string? Body)`, cuerpo opcional.

- [ ] **Step 1: Escribe las pruebas** (calco de `SupportReplyTemplateProvisioningApiTests`):
  `ProvisioningCreatesTheBodyOnlyTemplateAndTheScreenShowsItPending` (fila `Pending`, `kind = 'WholesaleInvitationNotice'`,
  `wholesaleInvitationNoticeTemplate.status` en `GET /`, payload a Meta sin `HEADER` ni `BUTTONS`),
  `ProvisioningTwiceIsWholesaleInvitationNoticeExistsWithoutTouchingMeta`, `ARejectedOneIsRecreatedAsTheNextVersion`
  (`qep_mayorista_invitacion_v2`), `AnExternalWithTheSameBodyIsAdoptedAndWithAnotherBodyIsConflict`,
  `TheDraftRouteAnswersTheBaseTextWithItsSixVariables`, `WithoutThePermissionTheRoutesAre403`,
  `TheReadinessPortReturnsTheApprovedRowOfThatConnectionOnly`.
- [ ] **Step 2: RED de compilación** — `dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests` → `CS0246 … ProvisionWholesaleInvitationNoticeTemplateCommand`.
- [ ] **Step 3: Implementa** lo de Interfaces. Readiness calco de `SupportReplyTemplateReadiness.cs` con el kind nuevo, registrada
  `AddScoped`. Endpoints: comentario «Mayorista: la plantilla de aviso de invitación, calcada de la de pedido (cuerpo opcional,
  sin encabezado ni gate de módulo)».
- [ ] **Step 4: GREEN** (integración, una prueba por vez) —
  `dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~WholesaleInvitationNoticeTemplateProvisioningApiTests.ProvisioningCreates"` → PASS; luego las demás por nombre.
- [ ] **Step 5: Commit** — `feat(messaging): provision the wholesale invitation notice template`

### Task 3: Envío del aviso (`IWholesaleInvitationNoticeOutbound`)

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/PlainTemplateOutbound.cs` (núcleo, `internal`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/BillingTemplateOutbound.cs:44-123` (delega en el núcleo)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/WholesaleInvitationNoticeOutbound.cs`
- Modify: `MessagingInfrastructureExtensions.cs:33-34`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/WholesaleInvitationNoticeOutboundTests.cs`;
  regresión: `BillingTemplateOutboundTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed record PlainTemplateSend(Guid TenantId, Guid ConnectionId, Guid SentByMemberId, Guid ClientId, string WaId, Guid? CustomerId, string RecipientName, ApprovedTemplate Template, IReadOnlyDictionary<string, string> Values, IReadOnlyList<string>? LegacyPositionalKeys)`;
    `internal abstract record PlainTemplateSendResult { Sent(Guid MessageId, Guid ConversationId); ConnectionUnavailable(string? Reason); Failed(string Code) }`;
    `internal sealed class PlainTemplateOutbound(...)` con `Task<PlainTemplateSendResult> SendAsync(PlainTemplateSend, CancellationToken)`: el cuerpo de hoy de
    `BillingTemplateOutbound.SendAsync` pasos 1-6, con `send.ClientId` en el `OutboundDraft` y, si el reclamo no insertó
    (`!claim.Inserted`) y `claim.Existing.Status` ya no es `Failed`, devuelve `Sent(claim.MessageId, conversation.Id)` sin llamar a Meta.
  - `BillingTemplateOutbound` pasa `ClientId = Guid.CreateVersion7()` y `BillingTemplateDefinitions.For(kind).LegacyPositionalKeys`, y traduce el resultado (comportamiento idéntico).
  - `public sealed record WholesaleInvitationNoticeSend(Guid TenantId, Guid ConnectionId, Guid SentByMemberId, Guid ClientId, string WaId, string RecipientName, ApprovedTemplate Template, IReadOnlyDictionary<string, string> Values)`;
    `public abstract record WholesaleInvitationNoticeSendResult { Sent(Guid MessageId, Guid ConversationId); ConnectionUnavailable(string? Reason); Failed(string Code) }`;
    `public interface IWholesaleInvitationNoticeOutbound { Task<WholesaleInvitationNoticeSendResult> SendAsync(WholesaleInvitationNoticeSend send, CancellationToken cancellationToken); }`;
    la implementación llama al núcleo con `CustomerId: null` y `LegacyPositionalKeys: null`.

- [ ] **Step 1: Escribe las pruebas** (gemelas de `BillingTemplateOutboundTests`):
  `SendsTheNoticeIntoANewOpenUnassignedConversationWithoutCustomer` (conversación `Open`, sin asignar, `customer_id IS NULL`,
  mensaje `Template` con autor `SentByMemberId`), `TheSameClientIdTwiceSendsOnce` (dos `SendAsync` con el mismo `ClientId` ⇒ un
  solo `SendTemplateAsync` en el fake de Meta, una fila, el segundo devuelve `Sent` con el mismo `MessageId`; Review Focus 5),
  `AConnectionThatIsNotActiveReturnsConnectionUnavailableAndWritesNothing`, `MetaRejectingTheTemplateLeavesAFailedMessageAndReturnsFailed`,
  `AMissingValueIsRejectedBeforeAnyCall` (sin `correo` ⇒ `Failed("messaging.message.rejected")`).
- [ ] **Step 2: RED de compilación** — `dotnet build tests/Modules/Messaging/Modules.Messaging.IntegrationTests` → `CS0246 … WholesaleInvitationNoticeSend`.
- [ ] **Step 3: Implementa** lo de Interfaces; registra `services.AddScoped<IWholesaleInvitationNoticeOutbound, WholesaleInvitationNoticeOutbound>()`.
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~WholesaleInvitationNoticeOutboundTests"` y después
  `--filter "FullyQualifiedName~BillingTemplateOutboundTests"` (regresión del núcleo) → PASS.
- [ ] **Step 5: Fin de bloque** — `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests` y, de integración, las tres
  clases nuevas más `BillingTemplateOutboundTests` y `TemplateManagementApiTests`, una por vez → PASS.
- [ ] **Step 6: Commit** — `feat(messaging): send the wholesale invitation notice by phone`

**Revisión del bloque A.**

---

## Bloque B — Mayorista y adaptadores (Tasks 4-6, un implementador)

### Task 4: Dominio y esquema del aviso

**Files:**
- Create: `src/Modules/Wholesale/Modules.Wholesale.Domain/InvitationNoticeStatus.cs`
- Modify: `Distributor.cs:27-69,71-132`, `DistributorInvitation.cs:20-50`, `WholesaleErrorCodes.cs:327-334`
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/WholesaleDbContext.cs:100-180`; Create migración `AddInvitationWhatsAppNotice`
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorTests.cs`, `WholesaleDbContextMappingTests.cs`

**Interfaces:**
- Produces:
  - `public enum InvitationNoticeStatus { Pending, Sent, Failed }`.
  - `Distributor.PhoneMaxLength = 16`; `string? Phone` (E.164), `Guid? WhatsAppConnectionId`.
  - `DistributorInvitation`: `int Generation` (0 al emitir), `InvitationNoticeStatus? NoticeStatus`, `string? NoticeFailureCode`; `NoticeFailureCodeMaxLength = 128`.
  - `Distributor.Invite(…, string? phone, Guid? whatsAppConnectionId, DateTimeOffset now)`: con teléfono, aviso `Pending`; sin él, `null`.
  - `bool Distributor.ChangeNoticeTarget(string? phone, Guid? whatsAppConnectionId)`: sólo `Invited` (afuera no hace nada y
    devuelve `false`); si cambia algo sube `Generation`, deja `NoticeStatus = null` y devuelve `true` (P-3); con `phone == null` limpia los dos. No toca `Version`: lo sube `ChangeTerms`, que corre en el mismo `PUT`.
  - `ResendInvitation(string tokenHash, bool noticeQueued, DateTimeOffset now)`: rota como hoy, `Generation++`; con teléfono,
    `noticeQueued` ⇒ `Pending` sin código, `!noticeQueued` ⇒ `Failed` con `"whatsapp_unavailable"`; sin teléfono, `null`.
  - `bool Distributor.RecordInvitationNotice(int generation, InvitationNoticeStatus status, string? failureCode)`: `false` sin
    efecto si `generation != Invitation.Generation` o `NoticeStatus is null`; no toca `Version` ni `UpdatedAt` (D-3).
  - `WholesaleErrorCodes.DistributorWhatsAppConnectionRequired = "wholesale.distributor.whatsapp_connection_required"`,
    `DistributorWhatsAppUnavailable = "wholesale.distributor.whatsapp_unavailable"` (bloque 422).
  - Columnas: `distributors.phone varchar(16) NULL`, `distributors.whatsapp_connection_id uuid NULL`;
    `distributor_invitations.generation integer NOT NULL DEFAULT 0`, `notice_status varchar(16) NULL` (texto),
    `notice_failure_code varchar(128) NULL`.

- [ ] **Step 1: Escribe las pruebas** en `DistributorTests`: `InviteWithAPhoneStartsTheNoticePendingAtGenerationZero`,
  `InviteWithoutAPhoneHasNoNotice`, `ResendBumpsTheGenerationAndQueuesOrFailsTheNotice` (`noticeQueued: false` ⇒
  `(Failed, "whatsapp_unavailable")`), `ChangeNoticeTargetBumpsTheGenerationOnlyWhenSomethingChanges`,
  `ChangeNoticeTargetWithANullPhoneClearsPhoneAndConnection`, `ChangeNoticeTargetOutsideInvitedIsIgnored`,
  `AStaleGenerationResultChangesNothing` (Review Focus 2), `RecordingTheNoticeKeepsVersionAndUpdatedAt` (Review Focus 3). En
  `WholesaleDbContextMappingTests`: las cinco columnas con su tipo y largo.
- [ ] **Step 2: RED de compilación** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.UnitTests` → `CS1501: No overload for method 'Invite' takes 14 arguments` (o equivalente).
- [ ] **Step 3: Implementa** lo de Interfaces y la migración (aditiva; filas existentes `generation = 0`, aviso `null`). Ajusta
  los llamadores (`InviteDistributorHandler`, `TransitionDistributorHandler`, `DistributorWorld`) con `null`/`false` para que compile.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~DistributorTests|FullyQualifiedName~WholesaleDbContextMappingTests"` → PASS.
- [ ] **Step 5: Commit** — `feat(wholesale): store the invitation notice target and state`

### Task 5: Puertos, adaptadores y escrituras (invitar, editar, reenviar)

**Files:**
- Create: `src/Modules/Wholesale/Modules.Wholesale.Application/WholesaleWhatsAppPorts.cs`, `DistributorWhatsAppNotice.cs`
- Modify: `InviteDistributor.cs`, `UpdateDistributor.cs`, `TransitionDistributor.cs:55-68`, `DistributorWriteRules.cs`, `Ports.cs:175-190`, `WholesaleConflicts.cs`
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/WholesaleEventPublisher.cs`
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Api/WholesaleEndpoints.cs:248-285,327-337`
- Create: `src/Bootstrapper/WholesalePhoneNormalizer.cs`, `WholesaleWhatsAppConnections.cs`, `WholesaleInvitationNoticeSender.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1862-1870`
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorHandlersTests.cs`, `DistributorFakes.cs`, `DistributorWorld.cs`;
  `tests/Bootstrapper/Bootstrapper.UnitTests/WholesaleInvitationNoticeSenderTests.cs`, `WholesaleWhatsAppConnectionsTests.cs`

**Interfaces:**
- Consumes: Task 3 (`IWholesaleInvitationNoticeOutbound`), Task 2 (`IWholesaleInvitationNoticeReadiness`), Task 4.
- Produces (Wholesale.Application, `WholesaleWhatsAppPorts.cs`):
  - `public interface IWholesalePhoneNormalizer { string? ToE164(string phone, string country); }`
  - `public sealed record WholesaleWhatsAppConnection(Guid Id, string Label);`
    `public interface IWholesaleWhatsAppConnections { Task<IReadOnlyList<WholesaleWhatsAppConnection>> ListReadyAsync(Guid tenantId, CancellationToken ct); Task<string?> FindLabelAsync(Guid tenantId, Guid connectionId, CancellationToken ct); }`
  - `public sealed record WholesaleInvitationNotice(Guid MessageId, Guid TenantId, Guid ConnectionId, Guid SentByMemberId, string Phone, string ContactName, string Email);`
    `public abstract record WholesaleNoticeSendResult { Sent; Failed(string Code) }`;
    `public interface IWholesaleInvitationNoticeSender { Task<WholesaleNoticeSendResult> SendAsync(WholesaleInvitationNotice notice, CancellationToken ct); }`
  - `DistributorWhatsAppNotice` (internal static): `Task<string?> NormalizeAsync(ITenantDirectory, IWholesalePhoneNormalizer, Guid tenantId, string? phone, ct)`
    (`ValidationException` con `ValidationFailure("Phone", "The phone is not valid.")`) y
    `Task<Guid> ResolveConnectionAsync(IWholesaleWhatsAppConnections, Guid tenantId, Guid? requested, ct)` con las reglas del contrato;
    `WholesaleConflicts.WhatsAppConnectionRequired()` / `WhatsAppUnavailable()` (`WholesaleDomainException`, español).
  - `IWholesaleEventPublisher.InvitationNoticeRequested(Guid tenantId, Guid distributorId, string phone, Guid connectionId, Guid sentByMemberId, string contactName, string email, int generation, string correlationId, DateTimeOffset occurredAt)`;
    `WholesaleEventPublisher.InvitationNoticeEvent = "wholesale.distributor-invitation-notice.v1"`, payload
    `{ tenantId, distributorId, phone, connectionId, sentByMemberId, contactName, email, generation }` (sin token, sin
    `expiresAt`, sin `businessName`).
  - Comandos: `InviteDistributorCommand` y `UpdateDistributorCommand` suman `string? Phone, Guid? WhatsAppConnectionId`; los
    cuerpos `DistributorInviteRequest` / `DistributorUpdateRequest` suman `string? Phone = null, Guid? WhatsAppConnectionId = null`.
    Validadores: `Phone` recortado ≤ 32 si viene; `WhatsAppConnectionId` `!= Guid.Empty` si viene y hay teléfono.
  - Handlers: suman `ITenantDirectory`, `IWholesalePhoneNormalizer`, `IWholesaleWhatsAppConnections`, `IMembershipDirectory`
    (D-6). Invitar con teléfono: evento del correo **y** del aviso, auditoría con `"whatsAppNotice=true"` (D-8); sin teléfono,
    sólo el correo y `"whatsAppNotice=false"`. Editar en `Invited`: D-9 y P-3; `WrittenFields` suma `"phone"` y
    `"whatsAppConnectionId"`. Reenviar: si hay teléfono, `ListReadyAsync` contiene `WhatsAppConnectionId` ⇒ evento + `true`; si
    no ⇒ `ResendInvitation(…, noticeQueued: false, …)` y `false`. El correo sale siempre.
- Produces (Bootstrapper):
  - `WholesalePhoneNormalizer(IPhoneNumberNormalizer phones) : IWholesalePhoneNormalizer` (calco de `ServiceContractsPhoneNormalizer.cs`).
  - `WholesaleWhatsAppConnections(IIntegrationConnections connections, IWholesaleInvitationNoticeReadiness readiness)`: `ListResolvedActiveAsync(tenantId, IntegrationProviders.WhatsAppCloud.Key)` filtrado por plantilla aprobada; label D-7.
  - `WholesaleInvitationNoticeSender(IWholesaleInvitationNoticeOutbound outbound, IWholesaleInvitationNoticeReadiness readiness, ITenantDirectory tenants)`:
    sin plantilla ⇒ `Failed("template_not_approved")`; sin nombre de tenant ⇒ `Failed("send_failed:messaging.message.rejected")`
    sin llamar; valores `nombre_contacto` = contacto en una línea, `mayorista` = `GetDisplayNameAsync` en una línea (se lee al
    entregar, como el correo), `correo` = email; `WaId` = dígitos del E.164; `ClientId` = `MessageId` (D-5); resultado
    `ConnectionUnavailable(r)` ⇒ `connection_unavailable:{r ?? "unknown"}`, `Failed(c)` ⇒ `send_failed:{c}`.

- [ ] **Step 1: Escribe las pruebas** — `DistributorHandlersTests`:
  `InviteWithAPhonePublishesTheEmailAndATokenlessNoticeAndAuditsTheFlag`,
  `InviteWithoutAPhonePublishesOnlyTheEmailAndIgnoresTheConnection`,
  `InviteWithTwoReadyConnectionsAndNoneChosenIs422ConnectionRequired`, `InviteWithNoReadyConnectionIs422WhatsAppUnavailable`,
  `InviteWithAConnectionThatIsNotReadyIs422WhatsAppUnavailable`, `InviteWithAPhoneThatDoesNotNormalizeIs422OnPhoneAndWritesNothing`,
  `InviteNormalizesThePhoneWithTheTenantCountry` (`FakeTenancy` con `Country = "MX"` ⇒ el normalizador falso recibe `"MX"`),
  `UpdateWithTheSamePhoneAndNullConnectionDoesNotResolveTheConnection` (Review Focus 1: directorio falso que lanza si se llama),
  `UpdateThatChangesThePhoneBumpsTheGenerationAndClearsTheNotice`, `UpdateWithoutAPhoneDeletesPhoneAndConnection`,
  `UpdateOutsideInvitedIgnoresThePhone`, `ResendWithAReadyConnectionQueuesANewNoticeWithTheNewGeneration`,
  `ResendWithTheConnectionNoLongerReadySendsTheEmailAndFailsTheNotice`.
  `Bootstrapper.UnitTests`: `TheNoticeFillsContactWholesalerAndEmail` (exactamente las claves `nombre_contacto`, `mayorista`,
  `correo`; `mayorista` = nombre vigente del tenant), `ATemplateThatIsNotApprovedFailsWithoutSending`,
  `ConnectionUnavailableAndFailedMapToTheFailureCodes`, `TheClientIdIsTheOutboxMessageId`,
  `ListReadyKeepsOnlyConnectionsWithTheApprovedTemplateAndLabelsThemByNumber`.
- [ ] **Step 2: RED de compilación** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.UnitTests` y `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` → `CS0246 … IWholesaleWhatsAppConnections`.
- [ ] **Step 3: Implementa** lo de Interfaces y registra los tres adaptadores `AddScoped` junto a los de Wholesale.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~DistributorHandlersTests"`
  y `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~WholesaleInvitationNotice|FullyQualifiedName~WholesaleWhatsAppConnections"` → PASS.
- [ ] **Step 5: Commit** — `feat(wholesale): queue the whatsapp notice on invite and resend`

### Task 6: Lecturas (detalle, lista y opciones)

**Files:**
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Application/DistributorDtos.cs:22-96`, `DistributorComposer.cs:148-224`, `GetDistributorOptions.cs`
- Test: `DistributorComposerTests.cs`, `DistributorHandlersTests.cs`

**Interfaces:**
- Consumes: Task 4, Task 5 (`IWholesaleWhatsAppConnections`).
- Produces: `DistributorWhatsAppNoticeDto(string Status, string? FailureCode, string ConnectionLabel)` (`<summary>` BFF: el
  detalle pinta el estado sin pedir la conexión aparte); `DistributorInvitationDto` suma `DistributorWhatsAppNoticeDto? WhatsAppNotice`;
  `DistributorDetailDto` y `DistributorSummaryDto` suman `string? Phone`; `DistributorWhatsAppConnectionDto(Guid Id, string Label)` y
  `DistributorOptionsDto` suma `IReadOnlyList<DistributorWhatsAppConnectionDto> WhatsAppConnections`. `DistributorComposer` suma
  `IWholesaleWhatsAppConnections`; `FindLabelAsync` sólo cuando hay aviso (D-7).

- [ ] **Step 1: Escribe las pruebas**: `DetailCarriesThePhoneAndTheNoticeWithItsConnectionLabel`,
  `DetailWithoutPhoneHasANullNotice`, `AFailedNoticeCarriesItsCode`, `ANoticeOnAConnectionThatIsGoneIsLabeledSinNombre`,
  `SummariesCarryThePhone`, `OptionsBringTheReadyWhatsAppConnections` (vacía ⇒ `[]`).
- [ ] **Step 2: RED de compilación** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.UnitTests` → `CS1061 … 'WhatsAppNotice'`.
- [ ] **Step 3: Implementa** lo de Interfaces.
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~DistributorComposerTests|FullyQualifiedName~DistributorHandlersTests"` → PASS.
- [ ] **Step 5: Fin de bloque** — `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests`,
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests`, `dotnet test tests/ArchitectureTests/ArchitectureTests` (incluye
  `WholesaleLayerTests` y `CompositionRootTests`) y, de integración, `DistributorApiTests` (regresión sin teléfono) → PASS.
- [ ] **Step 6: Commit** — `feat(wholesale): expose phone, notice state and ready connections`

**Revisión del bloque B.**

---

## Bloque C — Entrega y cierre (Tasks 7-8, un implementador)

### Task 7: Procesador del aviso con inbox propio

**Files:**
- Create: `src/Modules/Wholesale/Modules.Wholesale.Application/DistributorInvitationNotices.cs`
- Create: `src/Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/WholesaleInbox.cs`,
  `Messaging/ClaimedOutboxConsumer.cs`, `Messaging/InvitationNoticeProcessor.cs`, `Messaging/InvitationNoticeWorker.cs`;
  migración `AddWholesaleInbox`
- Modify: `WholesaleDbContext.cs` (`Inbox`), `WholesaleInfrastructureExtensions.cs:27-35`
- Modify: `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs:89-92`
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/DistributorInvitationNoticesTests.cs`;
  `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/DistributorWhatsAppNoticeApiTests.cs` (host propio por clase con
  `QepApiFactory(…, configureServices)` que reemplaza `IWholesaleWhatsAppConnections` y `IWholesaleInvitationNoticeSender` por fakes)

**Interfaces:**
- Consumes: Tasks 4-6.
- Produces:
  - `public sealed class DistributorInvitationNotices(IDistributorRepository distributors)` con
    `Task<Distributor?> FindCurrentAsync(Guid tenantId, Guid distributorId, int generation, CancellationToken ct)` (`null` si no
    existe, no está `Invited` o la `generation` no es la vigente; D-4) y
    `Task<bool> RecordAsync(Guid tenantId, Guid distributorId, int generation, InvitationNoticeStatus status, string? failureCode, CancellationToken ct)`
    (no guarda: guarda el procesador con el inbox).
  - `WholesaleInboxMessage` / `WholesaleInboxClaims.TryClaimAsync(...)` y `ClaimedOutboxConsumer` copias de
    `PurchasesInbox.cs` y `Purchases/…/ClaimedOutboxConsumer.cs` sobre `WholesaleDbContext` (`wholesale.inbox_messages`, PK
    `(consumer, message_id)`), leyendo el outbox con `WholesaleOutboxMessage` `AsNoTracking`.
  - `InvitationNoticeProcessor` (singleton): `Consumer = "wholesale.invitation-whatsapp-notice"`, evento
    `"wholesale.distributor-invitation-notice.v1"`, curva 5 s / 30 s / 5 min. Por mensaje: payload malformado ⇒ procesado sin
    efecto (log con `MessageId`); `FindCurrentAsync` `null` ⇒ procesado sin enviar; si no, `IWholesaleInvitationNoticeSender.SendAsync`
    con `MessageId = record.Id`, `RecordAsync` (`Sent` o `Failed` con el código), `MarkProcessedAsync` y un solo
    `IWholesaleUnitOfWork.SaveChangesAsync`. Una excepción no marca nada: vuelve al vencer el reclamo (`Pending` mientras tanto).
    Logs sólo con `MessageId` y `DistributorId`, nunca el teléfono.
  - `InvitationNoticeWorker : BackgroundService` calco de `WholesaleOrderEventsWorker.cs` (`DoorbellQueues.Outbox` + `SafetyNet`).
  - `MultiReplicaInventory`: `new("Modules.Wholesale.Infrastructure.Messaging.InvitationNoticeWorker", WorkerClassification.Claimed, "Modules/Wholesale/Modules.Wholesale.Infrastructure/Persistence/WholesaleInbox.cs:<líneas> — ClaimedOutboxConsumer, reclamo en el inbox con claimed_until")`.

- [ ] **Step 1: Escribe las pruebas** — unitarias: `AStaleGenerationIsNotCurrent`, `ADeletedOrAcceptedDistributorIsNotCurrent`,
  `RecordAppliesOnlyTheCurrentGeneration`. Integración:
  `InviteWithAPhoneQueuesTheEmailAndATokenlessNoticeThatEndsSent` (los dos eventos con `payload ->> 'distributorId'`; el token
  plano de `LatestTokenAsync` no aparece en el `payload::text` del aviso y el payload no tiene `expiresAt`; `EventuallyAsync` hasta
  `whatsAppNotice.status == "Sent"`; `version` igual a la de la respuesta del `POST`, Review Focus 3;
  `audit.entries.changed_fields` contiene `whatsAppNotice=true` y no contiene el teléfono, Review Focus 4),
  `WithoutAPhoneOnlyTheEmailIsQueued`, `AFailingSendLeavesTheInvitationAndTheEmailAndShowsFailedWithItsCode` (fake devuelve
  `Failed("template_not_approved")`), `ResendWithAPhoneQueuesANewNoticeAndANewEmail`,
  `ResendWithTheConnectionNoLongerReadySendsTheEmailAndFailsTheNotice`,
  `ANoticeQueuedBeforeAPhoneChangeIsNotSent` (fake bloqueado hasta después del `PUT`; Review Focus 2),
  `AnUnchangedPutWithTheSavedPhoneKeepsTheNotice` (`PUT` con el teléfono guardado y `whatsAppConnectionId: null` ⇒ misma
  `generation` y mismo `whatsAppNotice`; D-9), `TwoReadyConnectionsWithoutChoiceIs422`.
- [ ] **Step 2: RED de compilación** — `dotnet build tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests` → `CS0246 … DistributorInvitationNotices`.
- [ ] **Step 3: Implementa** lo de Interfaces y la migración (tabla nueva, aditiva). Registra `AddScoped<DistributorInvitationNotices>()`,
  `AddSingleton<InvitationNoticeProcessor>()`, `AddHostedService<InvitationNoticeWorker>()`.
- [ ] **Step 4: GREEN** — unitarias por filtro; integración por nombre, una por vez
  (`--filter "FullyQualifiedName~DistributorWhatsAppNoticeApiTests.InviteWithAPhone"` …); `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~MultiReplicaInventoryTests"` → PASS.
- [ ] **Step 5: Commit** — `feat(wholesale): deliver the invitation notice through the outbox`

### Task 8: Documentación y cierre

**Files:**
- Modify: `README.md` (rutas `wholesale-invitation-notice` y los campos nuevos junto a las de mayorista), `CLAUDE.md` (gotchas)

- [ ] **Step 1: Escribe en `CLAUDE.md` dos gotchas**: (1) «El aviso de WhatsApp de la invitación es un evento aparte y sin token
  (`wholesale.distributor-invitation-notice.v1`); su consumidor vive en Mayorista con inbox propio y manda por
  `WholesaleInvitationNoticeSender` de Bootstrapper. El resultado se aplica sólo a la `generation` vigente y no sube `version`»;
  (2) «El `clientId` del aviso es el id del mensaje del outbox: no lo cambies por uno nuevo, o un reintento manda dos avisos».
- [ ] **Step 2: Verificación de bloque** — clase completa `DistributorWhatsAppNoticeApiTests`, luego `DistributorApiTests`, una por vez → PASS.
- [ ] **Step 3: Antes de mergear a `develop`** (con el visto bueno del owner, una por vez y revisando `Get-Process testhost, dotnet` antes de cada una):

```powershell
dotnet build src/Api
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests
dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
dotnet test tests/ArchitectureTests/ArchitectureTests
dotnet format --verify-no-changes --include <archivos tocados>
```

- [ ] **Step 4: Commit** — `docs(wholesale): document the whatsapp invitation notice`

**Revisión del bloque C** y revisión fuerte de toda la rama (envío a terceros, datos personales, consumidor nuevo del outbox).
Despliegue: backend primero (migraciones aditivas; un frontend viejo ignora los campos nuevos).
