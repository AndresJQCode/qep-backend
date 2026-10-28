# WhatsApp por tenant (Zenvia) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que cada tenant elija desde Configuración si sus cotizaciones salen por la cuenta de WhatsApp de QEP (`Shared`), por su propia cuenta de Zenvia (`Own`, con la API key cifrada en reposo y de sólo escritura) o sin WhatsApp (`Disabled`, la cotización se marca enviada y la pantalla lo dice), y que `POST .../send` resuelva ese canal en cada envío sin romper a los tenants de hoy.

**Architecture:** Backend: un agregado nuevo `TenantWhatsAppSettings` en Quotations.Domain, tabla `quotations.tenant_whatsapp_settings` (migración `AddTenantWhatsAppSettings`), cifrado AES-256-GCM en Quotations.Infrastructure con la llave en configuración secreta (`Quotations:SecretProtection`), un `IWhatsAppChannelResolver` que `SendQuotationHandler` consulta en una etapa nueva `Channel`, tres casos de uso (`GetWhatsAppSettings`, `UpdateWhatsAppSettings`, `GetWhatsAppChannel`) con el gate `TenantModuleGuard` del spec de entitlements, y un `WhatsAppTokenRekeyWorker` que re-cifra al arrancar. Frontend: una `SettingsSection` "WhatsApp" con React Hook Form + zod en `features/tenant-settings`, y el flujo de envío de `features/quotes` pide el canal con `fetchQuery` dentro de `start()`, confirma cuando está desactivado y avisa con `toast.warning` según `whatsAppOutcome`.

**Tech Stack:** Backend .NET 10 (SDK de `global.json`), EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers (`postgres:18-alpine`, Docker corriendo), `System.Security.Cryptography.AesGcm` (en la caja: **sin paquetes nuevos**). Frontend React 19 + TypeScript, TanStack Query/Router, React Hook Form 7 + `@hookform/resolvers` 5 + zod 4, Radix (`components/ui`), sonner, Vitest + Testing Library, oxlint, bun.

**Spec:** `C:\Users\andre\AppData\Local\Temp\claude\c--Users-andre-OneDrive-Documentos2-repositories-QCode-templates-qep-qep-backend\b6ad7612-c9ff-4dff-a4c1-51df3b0a1bcb\scratchpad\specs\2026-10-07-whatsapp-por-tenant-design.md` (en adelante, «el spec»). Depende de `...\scratchpad\specs\2026-10-07-modulos-por-tenant-design.md` («el spec de entitlements»), que se implementa **antes**. Quien ejecute lee los dos.

## Precondición (bloqueante)

El spec de entitlements está implementado y commiteado en una rama de cada repo **antes** de empezar. Este plan lo da por hecho y usa, sin crearlos:

| Pieza | Dónde (según el spec de entitlements) | Cómo se usa acá |
| --- | --- | --- |
| `ITenantModules` (`Task<TenantModuleSet?> FindAsync(Guid, CancellationToken)`) | `Modules.Tenancy.Application` | parámetro de los handlers de settings |
| `TenantModuleGuard.EnsureEnabledAsync(ITenantModules, Guid, TenantModuleKey, CancellationToken)` | `Modules.Tenancy.Application` | gate de los dos handlers de settings |
| `TenantModuleKeys.Quotations`, `TenantModuleKeys.All`, `TenantModuleSet.FromStored(...)` | `Modules.Tenancy.Domain` | gate y dobles de prueba |
| 403 `tenancy.module_not_enabled` (`RequestForbiddenException`) | `ApiExceptionHandler.cs` | código esperado |
| tabla `tenancy.tenant_modules` (`tenant_id`, `module_key`, …) | migración `AddTenantModules` | prueba del gate con tenant real |
| `useTenantModules()` → `{ isEnabled(key), item(key), status }` | `qep-frontend/src/features/auth/hooks/use-tenant-modules.ts` | montar o no la sección |

La Task B0 y la Task F0 lo verifican con `rg` y **se detienen** si falta algo. Si los namespaces reales difieren de la tabla, se ajustan los `using` de este plan al código (gana el código, CLAUDE.md) y se anota en el handoff.

## Decisiones tomadas al planear (el owner estaba dormido; se anotan para su revisión)

1. **Ramas desde la rama de entitlements, en worktrees.** El checkout principal de `qep-backend` está en `feature/retencion-excel-pedidos` y puede haber otra sesión trabajando ahí. Se trabaja en `...\qep\qep-backend-worktrees\whatsapp-por-tenant` y `...\qep\qep-frontend-worktrees\whatsapp-por-tenant` (bajo el home, no en `/tmp`, por CodeGraph). La rama base se llama `feature/modulos-por-tenant` en este plan; si se llamó distinto, se usa la que contiene `TenantModuleGuard` (B0/F0 lo resuelven).
2. **El puerto del repositorio suma `FindReadOnlyAsync`** (sin tracking) junto a `FindAsync` (rastreado) y `Add`. El spec pide que el resolver lea «sin tracking» y que el puerto no tenga `Update`; las dos cosas se cumplen y el resolver se puede probar con un doble.
3. **Mensajes del validador:** los de `Provider`, `ApiKey`, `FromNumber` y `TemplateId` —los que la pantalla pinta bajo su input— en español con tuteo; los de `Mode` y `ExpectedVersion` —inalcanzables desde la pantalla— en inglés, como el resto de validadores del módulo.
4. **`ApiKey`, `FromNumber` y `TemplateId` se recortan** (`Trim`) antes de validarse y de guardarse. El spec lo dice de la key; extenderlo a los otros dos es inocuo y evita un 422 por un espacio pegado al copiar.
5. **`DbSet` se llama `WhatsAppSettings`**, no `TenantWhatsAppSettings`: un miembro con el mismo nombre que su tipo confunde la lectura de `QuotationsDbContext`.
6. **`TryParseVersion` se copia** en `WhatsAppSettingsEndpoints` (tercera copia, mismo precedente que `OrdersExportLayoutEndpoints.cs:103-104`).
7. **Frontend:** el formulario se vuelve a montar con `key={settings.version}` después de guardar o releer, en vez de `reset()`: así «el input de la API key queda vacío después de guardar» sale gratis y no hay estado viejo que limpiar. El aviso del pie vive fuera del formulario para sobrevivir a ese remontaje.
8. **Frontend:** `fetchWhatsAppChannel` valida que `enabled` sea booleano y, si no, lanza: una respuesta con otra forma (el `json(200, {})` de los mocks de prueba existentes, un proxy) cuenta como canal desconocido y sigue el flujo de hoy, que es lo que el spec pide para cualquier falla de esa consulta.
9. **Frontend:** el `PUT` de settings invalida la caché del canal (`['quotes', tenantId, 'whatsapp-channel']`): sin eso, quien desactiva WhatsApp y envía en la misma pestaña dentro del `staleTime` vería el flujo de antes (Review Focus 3).
10. **La sección se oculta mientras los módulos cargan** (`isEnabled` da `false` en `'loading'`, regla del hook de entitlements); el resto de la página no espera por ella.
11. **No se agregan paquetes** en ningún repo. `AesGcm`, `RandomNumberGenerator` y `[GeneratedRegex]` están en la caja; RHF, zod y sonner ya están en el frontend.

## Global Constraints

**Del spec** (valores tal cual):

- Modos: `Shared`, `Own`, `Disabled`; proveedor: `Zenvia`. Viajan y se guardan por nombre.
- Sin fila ⇒ canal `Shared`. «Sin fila» y `Shared` son el mismo canal. `whatsAppOutcome`: `"Accepted"` | `"Disabled"`, nulo fuera del envío.
- Tabla `quotations.tenant_whatsapp_settings` con las columnas, tipos y las cuatro `CHECK` del bloque SQL del spec («Modelo de datos»), PK `PK_tenant_whatsapp_settings`, sin FK a `tenancy.tenants`, sin `updated_by`, `version` como token de concurrencia, sin backfill.
- Cifrado: `api_token_ciphertext` = `nonce(12) || ciphertext || tag(16)`; AAD = UTF-8 de `"quotations.whatsapp.api_token:" + tenantId.ToString("D")`; ids de llave `^[a-z0-9]{1,32}$`; llaves de 32 bytes en base64.
- Configuración `Quotations:SecretProtection` (`ActiveKeyId`, `Keys:<id>`), clase `SecretProtectionOptions` propia, validada aparte. **Vacío = ausente** en todas partes. En `Production` todo exigido; fuera de `Production` sólo la activa si está declarada.
- API key: recortada, `^[\x21-\x7E]{1,512}$`; `FromNumber` sólo dígitos, 10–15; `TemplateId` GUID formato `D`.
- La API key **nunca** sale: ni en `GET`, `PUT`, `ProblemDetails`, `platform.request_failures` ni logs. `UpdateWhatsAppSettingsCommand`, `UpdateWhatsAppSettingsRequest` y `ZenviaSenderSettings` sobreescriben `ToString()` con `***`.
- Endpoints: `GET`/`PUT /api/v1/tenants/{tenantId}/quotations/whatsapp-settings` (`tenancy.settings.read` / `tenancy.settings.update` + capacidad `quotations`, `If-Match` en el `PUT`), `GET /api/v1/tenants/{tenantId}/quotations/whatsapp-channel` (`quotations.quotation.manage`). Sin permiso nuevo.
- Códigos: `quotation.whatsapp_settings.incomplete`, `quotation.whatsapp.credentials_rejected` (sólo `Own`, 401/403 de Zenvia), `quotation.whatsapp.send_failed`, `quotation.whatsapp.settings_unreadable` (etapa `Channel`), `tenancy.module_not_enabled`, `concurrency.conflict` (412), `precondition.if_match_required` (428), `validation.failed` (422 con `errors`).
- Auditoría por outbox, recurso = `tenantId`: `quotations.whatsapp_settings.mode_changed`, `quotations.whatsapp_settings.api_key_replaced`, `quotations.whatsapp_settings.updated`. El re-cifrado del worker no se audita.
- `QuotationsOptionsValidator` **sigue exigiendo** `Quotations:WhatsApp:{ApiToken,FromNumber,TemplateId}` en `Production`.
- Textos de producto exactos (copiar del spec, con tuteo): los de la sección «Cómo se hace explícito que no salió nada», «Frontend → Sección WhatsApp», «Envío» y los de `QuotationChangeSummary`. Este plan los repite literal en cada tarea.
- Criterio 1: las pruebas de **integración** existentes no se editan; las unitarias `SendQuotationHandlerTests`, `ZenviaWhatsAppSenderTests` y `QuotationChangeSummaryTests` sólo reciben ediciones mecánicas, mismas afirmaciones.

**Del proceso:**

- **TDD estricto por tarea**: prueba que falla → correrla y ver el RED esperado → implementar → GREEN → commit. En el handoff va la salida **literal** de las dos corridas (resumen Superado/Con error/Omitido y el mensaje de cada falla). Un RED que falla por otra causa (compilación de otra cosa, Docker apagado) no cuenta.
- Todo comando en **PowerShell**: `$env:VAR = "…"` en línea aparte, `A; if ($?) { B }`, nunca `&&`; `curl.exe`, no `curl`. **Nunca** se pipea `dotnet build`/`dotnet test` a otro comando (el pipe enmascara el exit code).
- **Antes de cada `dotnet build`, `dotnet test` o `dotnet ef`** se corre este bloque (`Api.exe` y `dotnet Api.dll` corriendo bloquean los binarios con MSB3021):

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like '*Api.dll*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

- Proyectos de prueba (rutas relativas al worktree del backend):
  - unitarias: `tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj`
  - integración: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj` (Docker corriendo)
  - arquitectura: `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`
- Por tarea se corren **sólo las clases que la tarea toca** con `--filter "FullyQualifiedName~<Clase>"`, un proyecto por comando, en **primer plano**. La suite completa corre una sola vez, en B13.
- xUnit v3: toda llamada que acepte `CancellationToken` recibe `TestContext.Current.CancellationToken` (xUnit1051 es error con `TreatWarningsAsErrors`).
- **CA1873**: un argumento de `[LoggerMessage]` que se calcula (parsear, formatear, `ToString()` costoso) va a una variable local detrás de `logger.IsEnabled(...)`; pasar locales ya calculadas (un `Guid`, un `string`) no lo dispara. El worker de B12 sólo pasa locales.
- **Paquetes**: ninguno nuevo. Si alguien igual toca `Directory.Packages.props`, se regeneran los 74 `packages.lock.json` con `dotnet restore --force-evaluate` y se commitean juntos; si no, el `docker build` (`--locked-mode`) muere con `NU1004`.
- **`ConfigurationExampleTests`**: toda propiedad pública de una clase con `SectionName` tiene que estar en `src/Api/appsettings.example.json`. `SecretProtectionOptions` lo exige en B2.
- **Secretos**: nunca imprimir un valor. Las pruebas usan llaves de prueba **calculadas en el harness** (bytes `0..31` y `100..131` pasados a base64), nunca una cadena con forma de llave en el código ni en `appsettings*.json`. `dotnet user-secrets set` va siempre con `| Out-Null`, y la verificación cuenta (`| Select-String -Pattern "..." | Measure-Object`).
- Migración con el factory de diseño, nunca a mano ni con `--startup-project`:
  `dotnet ef migrations add AddTenantWhatsAppSettings --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`. El snapshot sólo cambia regenerado por ese comando.
- **Commits**: Conventional Commits en español, **sin atribución de IA ni trailer `Co-Authored-By`** aunque el harness lo pida. Un solo comando con guard de rama y rutas explícitas (nunca `git add -A` ni `git add .`):

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add <rutas>; git commit -m "<mensaje>"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

  La segunda línea no devuelve nada; si devuelve algo, `git commit --amend -m "<mismo mensaje>"` antes de seguir.
- Idioma: prosa, comentarios y `<summary>` en español con tuteo (nunca voseo); identificadores, códigos y mensajes de excepción técnicos en inglés. Los comentarios nuevos dicen el porqué y citan el spec («spec 2026-10-07»).
- **Frontend**: `bun run test --run <archivo>` por tarea; `bun run lint` (oxlint) y `bun x prettier --check <archivos tocados>` antes de cada commit; `bun run build` (incluye `tsc -b`) en F8. Screaming Architecture (`SDD-ADR-07`): todo lo nuevo vive en `src/features/<feature>/`; nada va a `src/components/`.

## Review Focus

Los cinco casos que el spec implica y que ninguna prueba que él nombra cubre, del más probable al menos probable. Cada uno tiene su prueba en la tarea dueña:

1. **API key pegada con un salto de línea o espacios alrededor** (copiar de la consola de Zenvia lo trae casi siempre). Lo esperable: se guarda recortada y el envío manda el token sin espacios, no un `credentials_rejected` un día después. → B8, `UpdateWhatsAppSettingsHandlerTests.AnApiKeyWithSurroundingWhitespaceIsProtectedTrimmed`.
2. **`mode` en minúsculas o ausente** (`{ "mode": "own" }`, `{}`) desde un cliente que no es la SPA. Lo esperable: 422 con `errors.Mode`, nunca 500 ni un `Own` aceptado por `Enum.TryParse` sin distinguir mayúsculas. → B7, `UpdateWhatsAppSettingsValidatorTests.ModeIsOrdinalAndRequired`.
3. **Desactivar WhatsApp y enviar en la misma pestaña** antes de que venza el `staleTime` del canal. Lo esperable: el envío siguiente ya abre la confirmación. → F2, `use-whatsapp-settings.test.tsx` «invalidates the cached WhatsApp channel after saving».
4. **Pasar de `Own` con la key ilegible a «Cuenta de QEP»**. Lo esperable: se guarda `{ "mode": "Shared" }` sin exigir la key (el input está oculto). → F3, `whatsapp-settings-section.test.tsx` «saves Shared without asking for an unreadable key».
5. **«Reemplazar», escribir algo y «Cancelar»**. Lo esperable: el `PUT` no lleva `apiKey`; lo escrito no se cuela. → F3, «drops a typed key when the replacement is cancelled».

---

## Hallazgos contra el código (2026-10-07, `qep-backend` en `feature/retencion-excel-pedidos` = `717adea`, `qep-frontend` en `main` = `a8c324a`)

1. **El envío es el único llamador del sender.** `SendQuotationCommand` sólo lo despachan `QuotationEndpoints.cs:447-449` y se registra en `QepServiceCollectionExtensions.cs:345-347` (`ICommandHandler<SendQuotationCommand, QuotationDto>`). Cambiar el tipo de resultado toca esos dos lugares y nada más. Ninguna prueba unitaria usa el valor de retorno de `HandleAsync` (todas miran los dobles), así que el cambio a `SendQuotationResult` es mecánico para ellas.
2. **Los handlers se registran a mano; los validadores, por escaneo.** `AddValidatorsFromAssemblyContaining<CreateQuotationValidator>()` (`QepServiceCollectionExtensions.cs:435`) recoge el validador nuevo solo; los tres handlers nuevos se agregan junto a los del layout (`:398-403`). Sin registro, el endpoint mapea y falla en runtime con 500.
3. **Las migraciones corren antes que los hosted services.** `Program.cs:143-183` inicializa cada base con `MigrateAsync` antes de `app.RunAsync()` (`:185`), así que `WhatsAppTokenRekeyWorker` encuentra la tabla creada en cualquier arranque normal y de prueba.
4. **`ZenviaWhatsAppSender` es `internal`** y `Modules.Quotations.Infrastructure.csproj:7-8` da `InternalsVisibleTo` a las dos suites de Quotations: los dobles de integración pueden reemplazar `ZenviaHttpClient` (internal) sin volverlo público.
5. **No hay captura de logs reutilizable en integración.** `RecordingLogger<T>` (`Modules.Quotations.IntegrationTests/RecordingLogger.cs`) es un `ILogger<T>` suelto, no un `ILoggerProvider`. B10 agrega `CapturedLogs : ILoggerProvider`.
6. **Los helpers del harness reciben `QepApiFactory`**, no `WebApplicationFactory<Program>` (`CreateClient`, `RegisterTenantAsync`). Un host derivado con `WithWebHostBuilder` necesita su propio `CreateClientFor`; el tenant se registra en el host base (misma base de datos) y se usa desde el derivado con los mismos headers.
7. **El cliente que siembra `CreateActiveCustomerAsync` tiene teléfono** (`"310 935 2187"`, `QuotationsApiHarness.cs:292`): un envío por la cuenta propia no cae en `recipient_missing`.
8. **`AuthenticationStubGuardTests` levanta en `Production`** pero falla en el registro de servicios, antes de validar opciones: el validador nuevo de producción no la afecta.
9. **Frontend:** no existe `use-quote-send-flow.test.tsx`; el flujo se prueba hoy indirectamente en `quotes-list-page.test.tsx` y `editar.test.tsx`, cuyos mocks responden `json(200, {})` o una página a cualquier `GET` desconocido. De ahí la decisión 8: esa respuesta tiene que caer al flujo de hoy.
10. **Frontend:** no hay `AlertDialog`, `Form` ni `Field` en `components/ui/`; los diálogos de cotización usan `Dialog` + `DialogFooter` + `Button` (`quote-send-recipient-dialog.tsx`). El `PDF` se descarga con `useDownloadQuotePdf()` (`hooks/use-download-quote-pdf.ts`), que llama a `exportQuotePdf` y abre la URL firmada.
11. **Frontend:** voseo vivo en lo que se toca: `quotes.api.ts:558` («Agregalo»), `:560` («Intentá»), `:566` («Miralo», «reintentá») y `quote-send-failure-dialog.tsx:64` («podés»). Se corrigen al tocar el archivo; la única prueba que mira ese renglón usa `/quedó en borrador/i` y sigue verde.

## Contrato HTTP resultante (para el frontend)

```jsonc
// GET .../quotations/whatsapp-settings  (sin fila)            200, ETag: "1"
{ "tenantId": "…", "mode": "Shared", "provider": null,
  "apiKeyConfigured": false, "apiKeyUpdatedAt": null, "apiKeyReadable": null,
  "fromNumber": null, "templateId": null,
  "modes": ["Shared", "Own", "Disabled"], "providers": ["Zenvia"], "version": 1 }

// PUT .../quotations/whatsapp-settings, If-Match: "1"
{ "mode": "Own", "provider": "Zenvia", "apiKey": "<token>",
  "fromNumber": "573001234567", "templateId": "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f" }
// → 200, ETag: "2", mismo cuerpo que el GET con "apiKeyConfigured": true, "apiKeyReadable": true

// GET .../quotations/whatsapp-channel
{ "enabled": true, "mode": "Own" }

// POST .../quotations/{id}/send → QuotationResponse + "whatsAppOutcome": "Accepted" | "Disabled"
```

| Ruta | HTTP | `code` | Cuándo |
| --- | --- | --- | --- |
| settings | 403 | `authorization.denied` | sin permiso u otro tenant |
| settings | 403 | `tenancy.module_not_enabled` | tenant real sin `quotations` |
| `PUT` settings | 428 | `precondition.if_match_required` | sin `If-Match` válido |
| `PUT` settings | 412 | `concurrency.conflict` | versión vieja o dos primeros guardados a la vez |
| `PUT` settings | 422 | `validation.failed` + `errors.{Mode,Provider,ApiKey,FromNumber,TemplateId,ExpectedVersion}` | formato o faltantes en `Own` |
| `send` | 422 | `quotation.whatsapp.credentials_rejected` | `Own` y Zenvia 401/403 |
| `send` | 422 | `quotation.whatsapp.send_failed` | otro no-2xx (en `Shared`, también 401/403) |
| `send` | 422 | `quotation.whatsapp.settings_unreadable` | la key guardada no descifra |
| channel | 403 | `authorization.denied` | sin `quotations.quotation.manage` u otro tenant |

---
## File Structure

Rutas relativas a cada worktree. `Q` = `src/Modules/Quotations`, `QU` = `tests/Modules/Quotations/Modules.Quotations.UnitTests`, `QI` = `tests/Modules/Quotations/Modules.Quotations.IntegrationTests`.

### Backend — crear

| Archivo | Tarea | Responsabilidad |
| --- | --- | --- |
| `Q/Modules.Quotations.Domain/WhatsAppMode.cs` | B1 | `WhatsAppMode`, `WhatsAppProvider` |
| `Q/Modules.Quotations.Domain/ProtectedSecret.cs` | B1 | value object opaco `(KeyId, Ciphertext)` |
| `Q/Modules.Quotations.Domain/TenantWhatsAppSettings.cs` | B1 | agregado + `WhatsAppSettingsChanges` |
| `Q/Modules.Quotations.Application/IWhatsAppSecretProtector.cs` | B2 | puerto de cifrado |
| `Q/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptions.cs` | B2 | sección `Quotations:SecretProtection` |
| `Q/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptionsValidator.cs` | B2 | validación al arrancar |
| `Q/Modules.Quotations.Infrastructure/Whatsapp/AesGcmWhatsAppSecretProtector.cs` | B2 | AES-256-GCM |
| `Q/Modules.Quotations.Application/ITenantWhatsAppSettingsRepository.cs` | B3 | puerto del repositorio |
| `Q/Modules.Quotations.Infrastructure/Persistence/TenantWhatsAppSettingsRepository.cs` | B3 | adaptador EF |
| `Q/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_AddTenantWhatsAppSettings.cs` (+ `.Designer.cs`) | B3 | tabla nueva (generada) |
| `Q/Modules.Quotations.Infrastructure/Whatsapp/ZenviaSenderSettings.cs` | B4 | `ZenviaSenderSettings`, `ZenviaAccount` |
| `Q/Modules.Quotations.Infrastructure/Whatsapp/ZenviaHttpClient.cs` | B4 | el `HttpClient` compartido |
| `Q/Modules.Quotations.Application/IWhatsAppChannelResolver.cs` | B5 | `WhatsAppChannel`, `IWhatsAppChannelResolver` |
| `Q/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppChannelResolver.cs` | B5 | resuelve el canal por envío |
| `Q/Modules.Quotations.Application/WhatsAppSettingsDto.cs` | B7 | DTOs, `WhatsAppModes`, mapeo |
| `Q/Modules.Quotations.Application/UpdateWhatsAppSettings.cs` | B7–B8 | comando, validador, handler |
| `Q/Modules.Quotations.Application/GetWhatsAppSettings.cs` | B9 | query + handler |
| `Q/Modules.Quotations.Application/GetWhatsAppChannel.cs` | B9 | query + handler |
| `Q/Modules.Quotations.Api/WhatsAppSettingsEndpoints.cs` | B11 | `GET`/`PUT` settings + records HTTP |
| `Q/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppTokenRekeyWorker.cs` | B13 | re-cifrado al arrancar |
| `QU/WhatsAppSettingsTestDoubles.cs` | B2–B8 | dobles del repositorio, protector, módulos |
| `QU/TenantWhatsAppSettingsTests.cs` | B1 | |
| `QU/SecretProtectionOptionsValidatorTests.cs`, `QU/AesGcmWhatsAppSecretProtectorTests.cs` | B2 | |
| `QU/WhatsAppChannelResolverTests.cs` | B5 | |
| `QU/UpdateWhatsAppSettingsValidatorTests.cs` | B7 | |
| `QU/UpdateWhatsAppSettingsHandlerTests.cs` | B8 | |
| `QU/GetWhatsAppSettingsHandlerTests.cs`, `QU/GetWhatsAppChannelHandlerTests.cs` | B9 | |
| `QI/WhatsAppTestHarness.cs` | B3, B11 | URLs, dobles y extensiones de host |
| `QI/WhatsAppSettingsPersistenceTests.cs` | B3 | |
| `QI/WhatsAppSettingsApiTests.cs` | B11 | |
| `QI/WhatsAppSendChannelApiTests.cs`, `QI/WhatsAppSecretLeakTests.cs` | B12 | |
| `QI/WhatsAppTokenRekeyWorkerTests.cs` | B13 | |

### Backend — modificar

| Archivo | Tarea | Cambio |
| --- | --- | --- |
| `Q/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs` | B2, B3, B4, B5, B13 | registros |
| `src/Api/appsettings.example.json` | B2 | sección `SecretProtection` vacía |
| `QI/QuotationsApiHarness.cs` | B2 | fija las llaves de prueba |
| `Q/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` | B3 | `DbSet` + configuración |
| `Q/Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs` | B3 | PK → 412 |
| `Q/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs` | B3 | regenerado |
| `Q/Modules.Quotations.Infrastructure/Whatsapp/ZenviaWhatsAppSender.cs` | B4 | recibe `ZenviaSenderSettings`, errores sin cuerpo en `Tenant` |
| `QU/ZenviaWhatsAppSenderTests.cs` | B4 | edición mecánica + pruebas nuevas |
| `Q/Modules.Quotations.Domain/QuotationSendStage.cs` | B6 | `Channel` |
| `Q/Modules.Quotations.Application/QuotationChangeSummary.cs` | B6 | textos sin WhatsApp, `SendFailed(stage, whatsAppSkipped)` |
| `QU/QuotationChangeSummaryTests.cs` | B6 | edición mecánica + pruebas nuevas |
| `Q/Modules.Quotations.Application/SendQuotation.cs` | B6, B10 | resolver, `Disabled`, `SendQuotationResult` |
| `Q/Modules.Quotations.Application/QuotationsDtos.cs` | B10 | `QuotationResponse.WhatsAppOutcome` |
| `Q/Modules.Quotations.Api/QuotationEndpoints.cs` | B10, B11 | `send` con outcome, `GET /whatsapp-channel` |
| `QU/QuotationsTestDoubles.cs` | B10 | `StubWhatsAppChannelResolver` |
| `QU/SendQuotationHandlerTests.cs` | B10 | edición mecánica + pruebas nuevas |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | B10, B11 | handlers |
| `src/Api/Program.cs` | B11 | `app.MapWhatsAppSettingsEndpoints()` |
| `README.md`, `k8s/prod-secret.yaml`, `k8s/prod-configMap.yaml` | B14 | operación |

### Frontend — crear / modificar

| Archivo | Tarea |
| --- | --- |
| `src/features/tenant-settings/types/whatsapp-settings.ts` (crear) | F1 |
| `src/features/tenant-settings/services/whatsapp-settings.api.ts` (+ `.test.ts`) (crear) | F1 |
| `src/features/tenant-settings/hooks/use-whatsapp-settings.ts` (+ `.test.tsx`) (crear) | F2 |
| `src/features/tenant-settings/types/whatsapp-settings.schema.ts` (crear) | F3 |
| `src/features/tenant-settings/components/whatsapp-settings-section.tsx` (+ `.test.tsx`) (crear) | F3 |
| `src/features/tenant-settings/pages/tenant-settings-page.tsx` (+ test) (modificar) | F4 |
| `src/routes/_authenticated/settings/index.tsx` (+ `index.test.tsx`) (modificar) | F4 |
| `src/features/quotes/services/quotes.api.ts` (+ test), `src/features/quotes/types/quote.ts` (modificar) | F5 |
| `src/features/quotes/components/quote-send-without-whatsapp-dialog.tsx` (+ test) (crear) | F6 |
| `src/features/quotes/components/quote-send-failure-dialog.tsx` (+ test) (modificar) | F6 |
| `src/features/quotes/hooks/use-quote-send-flow.tsx` (modificar), `use-quote-send-flow.test.tsx` (crear) | F7 |

---

# Parte 1 — Backend (`qep-backend`, rama `feature/whatsapp-por-tenant`)

Todo bloque de comandos de esta parte empieza con:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\whatsapp-por-tenant
```

### Task B0: Preparación del worktree y verificación de la precondición

**Files:** ninguno (no hay commit).

- [ ] **Step 1: Crear el worktree desde la rama de entitlements**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git fetch origin
git branch -a --list "*modulos-por-tenant*"
```

Esperado: aparece la rama de entitlements. Si tiene otro nombre, buscarla con `git log --all --oneline -S "TenantModuleGuard" | Select-Object -First 5` y `git branch -a --contains <sha>`, y usar ese nombre abajo.

```powershell
$base = "feature/modulos-por-tenant"
git worktree add -b feature/whatsapp-por-tenant ..\qep-backend-worktrees\whatsapp-por-tenant $base
Set-Location ..\qep-backend-worktrees\whatsapp-por-tenant
git branch --show-current
```

Esperado: `feature/whatsapp-por-tenant`.

- [ ] **Step 2: Verificar la precondición (se detiene si falta algo)**

```powershell
rg -n "class TenantModuleGuard|static .*EnsureEnabledAsync" src/Modules/Tenancy
rg -n "interface ITenantModules" src/Modules/Tenancy
rg -n "Quotations\s*=|static .*Quotations" src/Modules/Tenancy/Modules.Tenancy.Domain
rg -n "module_not_enabled" src
rg -n "tenant_modules" src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations --files-with-matches
```

Esperado: cada comando devuelve al menos una línea. Anota los **namespaces** reales de `TenantModuleGuard`, `ITenantModules`, `TenantModuleKeys` y `TenantModuleSet`; si no son `Modules.Tenancy.Application` / `Modules.Tenancy.Domain`, ajusta los `using` de este plan. Si falta cualquiera, **detente** y repórtalo: este plan no implementa entitlements.

- [ ] **Step 3: Índice de CodeGraph del worktree** (CLAUDE.md: cada worktree con su `.codegraph/`, nunca copiado)

```powershell
gentle-ai codegraph init --cwd (Get-Location).Path
```

- [ ] **Step 4: Línea base verde**

Bloque de guardia de `Api.exe` (Global Constraints) y después:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --no-build
```

Esperado: build sin errores; unitarias de Quotations todas `Superado`. Anota el conteo en el handoff: es la referencia de «no rompí nada». Si hay rojas previas, anótalas por **nombre** (memoria: comparar por nombre, no por conteo).

---

### Task B1: Agregado `TenantWhatsAppSettings`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/WhatsAppMode.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/ProtectedSecret.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/TenantWhatsAppSettings.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/TenantWhatsAppSettingsTests.cs`

**Interfaces:**
- Consumes: `QuotationsDomainException(string code, string message)`.
- Produces:
  - `enum WhatsAppMode { Shared, Own, Disabled }`, `enum WhatsAppProvider { Zenvia }`
  - `sealed record ProtectedSecret(string KeyId, byte[] Ciphertext)` con `const int KeyIdMaxLength = 32`
  - `sealed record WhatsAppSettingsChanges(bool ModeChanged, bool ApiKeyReplaced, bool DetailsChanged, bool KeyRotated)` con `bool Any`
  - `sealed class TenantWhatsAppSettings`: `TenantId`, `Mode`, `Provider?`, `ApiToken?`, `ApiTokenUpdatedAt?`, `FromNumber?`, `TemplateId?`, `Version`, `UpdatedAt`; `const long DefaultVersion = 1`, `const int FromNumberMaxLength = 15`, `const int TemplateIdMaxLength = 64`; `static CreateEmpty(Guid, DateTimeOffset)`; `WhatsAppSettingsChanges Configure(WhatsAppMode mode, WhatsAppProvider? provider, ProtectedSecret? newToken, ProtectedSecret? rekeyedToken, string? fromNumber, string? templateId, DateTimeOffset now)`; `bool Reprotect(ProtectedSecret rekeyed, DateTimeOffset now)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.UnitTests/TenantWhatsAppSettingsTests.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La configuración de WhatsApp por tenant (spec 2026-10-07, «Domain»): tres modos que se cambian
/// en cualquier dirección sin perder la cuenta propia, un guardado = una versión, y la rotación de
/// llave que no cuenta como «la key cambió».
/// </summary>
public sealed class TenantWhatsAppSettingsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Created = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Created.AddHours(1);
    private static readonly DateTimeOffset Latest = Created.AddHours(2);
    private const string FromNumber = "573001234567";
    private const string TemplateId = "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f";

    private static ProtectedSecret Token(string keyId = "k1") => new(keyId, [1, 2, 3, 4]);

    private static TenantWhatsAppSettings OwnConfigured()
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Created);
        settings.Configure(
            WhatsAppMode.Own, WhatsAppProvider.Zenvia, Token(), null, FromNumber, TemplateId, Created);
        return settings;
    }

    [Fact]
    public void CreateEmptyIsSharedAtVersionOneWithoutCredentials()
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Created);

        Assert.Equal(TenantId, settings.TenantId);
        Assert.Equal(WhatsAppMode.Shared, settings.Mode);
        Assert.Equal(1, settings.Version);
        Assert.Null(settings.Provider);
        Assert.Null(settings.ApiToken);
        Assert.Null(settings.ApiTokenUpdatedAt);
        Assert.Null(settings.FromNumber);
        Assert.Null(settings.TemplateId);
    }

    public static TheoryData<WhatsAppProvider?, bool, string?, string?> IncompleteOwn => new()
    {
        { null, true, FromNumber, TemplateId },
        { WhatsAppProvider.Zenvia, false, FromNumber, TemplateId },
        { WhatsAppProvider.Zenvia, true, null, TemplateId },
        { WhatsAppProvider.Zenvia, true, FromNumber, null },
    };

    [Theory]
    [MemberData(nameof(IncompleteOwn))]
    public void OwnWithoutAnyOfTheFourPiecesIsIncomplete(
        WhatsAppProvider? provider, bool withToken, string? fromNumber, string? templateId)
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Created);

        var error = Assert.Throws<QuotationsDomainException>(() => settings.Configure(
            WhatsAppMode.Own, provider, withToken ? Token() : null, null, fromNumber, templateId, Later));

        Assert.Equal("quotation.whatsapp_settings.incomplete", error.Code);
        Assert.Equal(WhatsAppMode.Shared, settings.Mode);
        Assert.Equal(1, settings.Version);
    }

    [Theory]
    [InlineData(WhatsAppMode.Shared)]
    [InlineData(WhatsAppMode.Disabled)]
    public void LeavingOwnKeepsTokenNumberAndTemplate(WhatsAppMode target)
    {
        var settings = OwnConfigured();
        var token = settings.ApiToken;

        var changes = settings.Configure(target, null, null, null, null, null, Later);

        Assert.True(changes.ModeChanged);
        Assert.Equal(target, settings.Mode);
        Assert.Same(token, settings.ApiToken);
        Assert.Equal(WhatsAppProvider.Zenvia, settings.Provider);
        Assert.Equal(FromNumber, settings.FromNumber);
        Assert.Equal(TemplateId, settings.TemplateId);
    }

    [Fact]
    public void BackToOwnWithEverythingStoredNeedsNothingNew()
    {
        var settings = OwnConfigured();
        settings.Configure(WhatsAppMode.Disabled, null, null, null, null, null, Later);

        var changes = settings.Configure(WhatsAppMode.Own, null, null, null, null, null, Latest);

        Assert.True(changes.ModeChanged);
        Assert.False(changes.ApiKeyReplaced);
        Assert.Equal(WhatsAppMode.Own, settings.Mode);
    }

    [Fact]
    public void AnIdenticalConfigureReportsNothingAndKeepsTheVersion()
    {
        var settings = OwnConfigured();
        var version = settings.Version;

        var changes = settings.Configure(
            WhatsAppMode.Own, WhatsAppProvider.Zenvia, null, null, FromNumber, TemplateId, Later);

        Assert.False(changes.Any);
        Assert.Equal(version, settings.Version);
        Assert.Equal(Created, settings.UpdatedAt);
    }

    [Fact]
    public void ANullNewTokenKeepsTheStoredOne()
    {
        var settings = OwnConfigured();
        var token = settings.ApiToken;

        settings.Configure(WhatsAppMode.Own, null, null, null, null, "11111111-2222-3333-4444-555555555555", Later);

        Assert.Same(token, settings.ApiToken);
        Assert.Equal(Created, settings.ApiTokenUpdatedAt);
    }

    // El dominio no ve el texto: un token nuevo siempre es reemplazo, aunque sea el mismo.
    [Fact]
    public void ANewTokenIsAReplacementAndMovesItsTimestamp()
    {
        var settings = OwnConfigured();
        var replacement = Token();

        var changes = settings.Configure(WhatsAppMode.Own, null, replacement, null, null, null, Later);

        Assert.True(changes.ApiKeyReplaced);
        Assert.False(changes.ModeChanged);
        Assert.Same(replacement, settings.ApiToken);
        Assert.Equal(Later, settings.ApiTokenUpdatedAt);
    }

    [Fact]
    public void ChangingTheModeReportsModeChanged()
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Created);

        var changes = settings.Configure(WhatsAppMode.Disabled, null, null, null, null, null, Later);

        Assert.Equal(new WhatsAppSettingsChanges(true, false, false, false), changes);
        Assert.Equal(2, settings.Version);
        Assert.Equal(Later, settings.UpdatedAt);
    }

    [Fact]
    public void ARekeyedTokenIsKeyRotatedAndKeepsTheTimestamp()
    {
        var settings = OwnConfigured();
        var rekeyed = Token("k2");

        var changes = settings.Configure(WhatsAppMode.Own, null, null, rekeyed, null, null, Later);

        Assert.Equal(new WhatsAppSettingsChanges(false, false, false, true), changes);
        Assert.Same(rekeyed, settings.ApiToken);
        Assert.Equal(Created, settings.ApiTokenUpdatedAt);
    }

    // Un guardado es un incremento, por muchas clases de cambio que traiga.
    [Fact]
    public void ModeDetailsAndRekeyTogetherBumpTheVersionExactlyOnce()
    {
        var settings = OwnConfigured();
        var version = settings.Version;

        var changes = settings.Configure(
            WhatsAppMode.Disabled, null, null, Token("k2"), "573009876543", null, Later);

        Assert.True(changes.ModeChanged);
        Assert.True(changes.DetailsChanged);
        Assert.True(changes.KeyRotated);
        Assert.Equal(version + 1, settings.Version);
    }

    [Fact]
    public void ReprotectBumpsTheVersionOnceAndKeepsTheTimestamp()
    {
        var settings = OwnConfigured();
        var version = settings.Version;
        var rekeyed = Token("k2");

        Assert.True(settings.Reprotect(rekeyed, Later));

        Assert.Same(rekeyed, settings.ApiToken);
        Assert.Equal(version + 1, settings.Version);
        Assert.Equal(Created, settings.ApiTokenUpdatedAt);
        Assert.Equal(Later, settings.UpdatedAt);
    }

    [Fact]
    public void ReprotectWithoutATokenDoesNothing()
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Created);

        Assert.False(settings.Reprotect(Token("k2"), Later));
        Assert.Equal(1, settings.Version);
    }

    [Fact]
    public void ANewTokenAndARekeyedOneTogetherAreAProgrammingError()
    {
        var settings = OwnConfigured();

        Assert.Throws<ArgumentException>(() => settings.Configure(
            WhatsAppMode.Own, null, Token(), Token("k2"), null, null, Later));
    }

    [Fact]
    public void ProtectedSecretToStringDoesNotPrintTheBytes()
    {
        Assert.Equal("ProtectedSecret { KeyId = k1 }", Token().ToString());
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

Bloque de guardia y:

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~TenantWhatsAppSettingsTests"
```

Esperado: falla la compilación con `CS0246: The type or namespace name 'TenantWhatsAppSettings' could not be found` (y `WhatsAppMode`, `ProtectedSecret`).

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Domain/WhatsAppMode.cs`:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>
/// Por dónde sale el WhatsApp de una cotización (spec 2026-10-07). Se guarda y viaja por nombre,
/// como el resto de enums del módulo: sumar un modo no renumera nada.
/// </summary>
public enum WhatsAppMode
{
    /// <summary>La cuenta de Zenvia de QEP, la global. «Sin fila» significa lo mismo, a
    /// propósito: un tenant nuevo envía como hoy.</summary>
    Shared,

    /// <summary>La cuenta de Zenvia del propio tenant.</summary>
    Own,

    /// <summary>Sin WhatsApp: enviar marca la cotización como enviada y el PDF lo comparte la
    /// persona por su cuenta.</summary>
    Disabled
}

/// <summary>Proveedor de la cuenta propia. Hoy sólo Zenvia; el select de la pantalla ya lo lee de
/// la respuesta para no conocer el enum.</summary>
public enum WhatsAppProvider
{
    Zenvia
}
```

`src/Modules/Quotations/Modules.Quotations.Domain/ProtectedSecret.cs`:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>
/// Un secreto cifrado y el id de la llave que lo cifró (spec 2026-10-07, «Manejo del secreto»).
/// Opaco para el dominio: cifrar y descifrar es de <c>IWhatsAppSecretProtector</c>, en
/// Infrastructure; acá sólo se guarda y se reemplaza.
/// </summary>
public sealed record ProtectedSecret(string KeyId, byte[] Ciphertext)
{
    /// <summary>El ancho de <c>api_token_key_id</c> y del patrón de ids <c>^[a-z0-9]{1,32}$</c>.</summary>
    public const int KeyIdMaxLength = 32;

    // El ToString de un record imprime todas sus propiedades. Los bytes no le sirven a nadie en un
    // log y no hay por qué sacarlos de la base.
    public override string ToString() => $"ProtectedSecret {{ KeyId = {KeyId} }}";
}
```

`src/Modules/Quotations/Modules.Quotations.Domain/TenantWhatsAppSettings.cs`:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>Qué clase de cambio dejó un guardado. Todo en falso es un no-op: no sube versión ni
/// se audita (spec 2026-10-07, «Domain»).</summary>
public sealed record WhatsAppSettingsChanges(
    bool ModeChanged,
    bool ApiKeyReplaced,
    bool DetailsChanged,
    bool KeyRotated)
{
    public bool Any => ModeChanged || ApiKeyReplaced || DetailsChanged || KeyRotated;
}

/// <summary>
/// La configuración de WhatsApp de un tenant (spec 2026-10-07). Una fila por tenant, PK
/// <see cref="TenantId"/>.
///
/// Cambiar a <see cref="WhatsAppMode.Shared"/> o <see cref="WhatsAppMode.Disabled"/>
/// <b>conserva</b> la cuenta propia (decisión 8 del spec): volver a <see cref="WhatsAppMode.Own"/>
/// no obliga a pegar de nuevo la API key.
/// </summary>
public sealed class TenantWhatsAppSettings
{
    public const long DefaultVersion = 1;
    public const int FromNumberMaxLength = 15;
    public const int TemplateIdMaxLength = 64;

    // EF Core materializa por acá.
    private TenantWhatsAppSettings()
    {
    }

    private TenantWhatsAppSettings(Guid tenantId, DateTimeOffset now)
    {
        TenantId = tenantId;
        Mode = WhatsAppMode.Shared;
        Version = DefaultVersion;
        UpdatedAt = now;
    }

    public Guid TenantId { get; private set; }

    public WhatsAppMode Mode { get; private set; }

    public WhatsAppProvider? Provider { get; private set; }

    public ProtectedSecret? ApiToken { get; private set; }

    public DateTimeOffset? ApiTokenUpdatedAt { get; private set; }

    public string? FromNumber { get; private set; }

    public string? TemplateId { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>En memoria y en versión 1, para que el primer PUT pase por el mismo chequeo de
    /// versión que los demás (mismo criterio que <c>OrdersExportLayout.CreateDefault</c>).</summary>
    public static TenantWhatsAppSettings CreateEmpty(Guid tenantId, DateTimeOffset now) =>
        new(tenantId, now);

    /// <summary>
    /// Aplica un guardado. Los nulos <b>conservan</b> lo guardado; con valor, lo reemplazan.
    /// <paramref name="newToken"/> siempre cuenta como reemplazo (el dominio no ve el texto) y mueve
    /// <see cref="ApiTokenUpdatedAt"/>; <paramref name="rekeyedToken"/> es la misma key cifrada con
    /// otra llave y no lo mueve. Con cualquier cambio la versión sube una sola vez.
    /// </summary>
    public WhatsAppSettingsChanges Configure(
        WhatsAppMode mode,
        WhatsAppProvider? provider,
        ProtectedSecret? newToken,
        ProtectedSecret? rekeyedToken,
        string? fromNumber,
        string? templateId,
        DateTimeOffset now)
    {
        if (newToken is not null && rekeyedToken is not null)
        {
            throw new ArgumentException(
                "A new token and a re-encrypted one cannot be applied in the same save.",
                nameof(rekeyedToken));
        }

        var nextProvider = provider ?? Provider;
        var nextToken = newToken ?? rekeyedToken ?? ApiToken;
        var nextFromNumber = fromNumber ?? FromNumber;
        var nextTemplateId = templateId ?? TemplateId;

        if (mode == WhatsAppMode.Own &&
            (nextProvider is null || nextToken is null || nextFromNumber is null || nextTemplateId is null))
        {
            throw new QuotationsDomainException(
                "quotation.whatsapp_settings.incomplete",
                "The own WhatsApp account needs a provider, an API key, a sender number and a template.");
        }

        var changes = new WhatsAppSettingsChanges(
            ModeChanged: mode != Mode,
            ApiKeyReplaced: newToken is not null,
            DetailsChanged: nextProvider != Provider
                || !string.Equals(nextFromNumber, FromNumber, StringComparison.Ordinal)
                || !string.Equals(nextTemplateId, TemplateId, StringComparison.Ordinal),
            KeyRotated: rekeyedToken is not null);

        if (!changes.Any)
        {
            return changes;
        }

        Mode = mode;
        Provider = nextProvider;
        FromNumber = nextFromNumber;
        TemplateId = nextTemplateId;
        if (newToken is not null)
        {
            ApiToken = newToken;
            ApiTokenUpdatedAt = now;
        }
        else if (rekeyedToken is not null)
        {
            ApiToken = rekeyedToken;
        }

        Version++;
        UpdatedAt = now;
        return changes;
    }

    /// <summary>Lo mismo que <c>rekeyedToken</c> para quien re-cifra sin un PUT
    /// (<c>WhatsAppTokenRekeyWorker</c>). Sin key guardada no hay nada que re-cifrar.</summary>
    public bool Reprotect(ProtectedSecret rekeyed, DateTimeOffset now)
    {
        if (ApiToken is null)
        {
            return false;
        }

        ApiToken = rekeyed;
        Version++;
        UpdatedAt = now;
        return true;
    }
}
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~TenantWhatsAppSettingsTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: todas `Superado` (16 casos con la teoría), arquitectura verde.

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Domain/WhatsAppMode.cs src/Modules/Quotations/Modules.Quotations.Domain/ProtectedSecret.cs src/Modules/Quotations/Modules.Quotations.Domain/TenantWhatsAppSettings.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/TenantWhatsAppSettingsTests.cs; git commit -m "feat(quotations): agregado de configuración de WhatsApp por tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---
### Task B2: Cifrado AES-256-GCM y su configuración

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppSecretProtector.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptions.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptionsValidator.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/AesGcmWhatsAppSecretProtector.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:65-69`
- Modify: `src/Api/appsettings.example.json` (sección `Quotations`, después de `PaymentProofs`)
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs:876-878`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/SecretProtectionOptionsValidatorTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/AesGcmWhatsAppSecretProtectorTests.cs`

**Interfaces:**
- Consumes: `ProtectedSecret` (B1).
- Produces:
  - `public interface IWhatsAppSecretProtector { string? ActiveKeyId { get; } bool HasKey(string keyId); ProtectedSecret Protect(Guid tenantId, string plaintext); string Unprotect(Guid tenantId, ProtectedSecret secret); bool TryUnprotect(Guid tenantId, ProtectedSecret secret, out string? plaintext); }` (Application)
  - `public sealed class SecretProtectionOptions { const string SectionName = "Quotations:SecretProtection"; string? ActiveKeyId; Dictionary<string, string?> Keys; }`
  - `internal sealed class AesGcmWhatsAppSecretProtector(IOptions<SecretProtectionOptions>)`, singleton.
  - `QuotationsApiHarness.TestSecretProtectionKey` (`string`, base64 de los bytes `0..31`), público para B11–B13.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.UnitTests/SecretProtectionOptionsValidatorTests.cs`:

```csharp
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Quotations.Infrastructure.SecretProtection;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-10-07, «Validación al arrancar»: en producción todo exigido (falla rápido por una
/// llave mal pegada en el pipeline); fuera de producción sólo la activa, para que unos
/// user-secrets mal cargados no tumben las pruebas de integración. Ningún mensaje lleva un valor.
/// </summary>
public sealed class SecretProtectionOptionsValidatorTests
{
    private static readonly string GoodKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly string ShortKey = Convert.ToBase64String(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
    private const string NotBase64 = "esto-no-es-base64-SENTINEL";

    private static SecretProtectionOptions Options(string? active, params (string Id, string? Value)[] keys)
    {
        var options = new SecretProtectionOptions { ActiveKeyId = active };
        foreach (var (id, value) in keys)
        {
            options.Keys[id] = value;
        }

        return options;
    }

    private static SecretProtectionOptionsValidator ValidatorFor(string environment) =>
        new(new StubHostEnvironment(environment));

    [Fact]
    public void ProductionWithAnActiveKeyOf32BytesIsValid()
    {
        var result = ValidatorFor(Environments.Production).Validate(null, Options("k1", ("k1", GoodKey)));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProductionWithoutActiveKeyIdFails(string? active)
    {
        var result = ValidatorFor(Environments.Production).Validate(null, Options(active, ("k1", GoodKey)));

        Assert.True(result.Failed);
        Assert.Contains("Quotations:SecretProtection:ActiveKeyId", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ProductionWithoutTheActiveKeyValueFails(string? value)
    {
        var result = ValidatorFor(Environments.Production).Validate(null, Options("k1", ("k1", value)));

        Assert.True(result.Failed);
        Assert.Contains("Quotations:SecretProtection:Keys:k1", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("K1")]
    [InlineData("key-1")]
    [InlineData("k_1")]
    public void ProductionRejectsAKeyIdOutsideThePattern(string id)
    {
        var result = ValidatorFor(Environments.Production)
            .Validate(null, Options("k1", ("k1", GoodKey), (id, GoodKey)));

        Assert.True(result.Failed);
        Assert.Contains($"Quotations:SecretProtection:Keys:{id}", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadKeyValues))]
    public void ProductionRejectsAnyDeclaredKeyThatIsNot32Bytes(string bad)
    {
        var result = ValidatorFor(Environments.Production)
            .Validate(null, Options("k1", ("k1", GoodKey), ("k2", bad)));

        Assert.True(result.Failed);
        Assert.Contains("Quotations:SecretProtection:Keys:k2", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(bad, result.FailureMessage, StringComparison.Ordinal);
    }

    public static TheoryData<string> BadKeyValues => new() { ShortKey, NotBase64 };

    [Fact]
    public void OutsideProductionAMissingSectionIsValid()
    {
        var result = ValidatorFor(Environments.Development).Validate(null, new SecretProtectionOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OutsideProductionAMalformedInactiveKeyIsNotValidated()
    {
        var result = ValidatorFor(Environments.Development)
            .Validate(null, Options("test", ("test", GoodKey), ("k1", NotBase64)));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OutsideProductionAMalformedActiveKeyFails()
    {
        var result = ValidatorFor(Environments.Development).Validate(null, Options("k1", ("k1", NotBase64)));

        Assert.True(result.Failed);
        Assert.Contains("Quotations:SecretProtection:Keys:k1", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(NotBase64, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideProductionAnActiveKeyWithoutValueFails()
    {
        var result = ValidatorFor(Environments.Development).Validate(null, Options("k1", ("k1", "")));

        Assert.True(result.Failed);
    }

    // El harness fija Keys:k1 = "" para tapar los user-secrets del developer: vacío es ausente.
    [Fact]
    public void AnEmptyInactiveKeyIsAbsentEvenInProduction()
    {
        var result = ValidatorFor(Environments.Production)
            .Validate(null, Options("test", ("test", GoodKey), ("k1", "")));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void NoFailureMessageEverContainsAKeyValue()
    {
        var result = ValidatorFor(Environments.Production)
            .Validate(null, Options("k1", ("k1", ShortKey), ("BAD", GoodKey)));

        Assert.True(result.Failed);
        Assert.DoesNotContain(ShortKey, result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(GoodKey, result.FailureMessage, StringComparison.Ordinal);
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Modules.Quotations.UnitTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
```

`tests/Modules/Quotations/Modules.Quotations.UnitTests/AesGcmWhatsAppSecretProtectorTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.SecretProtection;
using Modules.Quotations.Infrastructure.Whatsapp;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-10-07, «Decisión: AES-256-GCM»: nonce aleatorio por cifrado, AAD atado al tenant,
/// llave elegida por el <c>KeyId</c> de la fila, y errores que nombran la clave de configuración
/// y nunca un valor.
/// </summary>
public sealed class AesGcmWhatsAppSecretProtectorTests
{
    private static readonly byte[] K1 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] K2 = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private const string Token = "zenvia-token-SENTINEL-123";

    private static AesGcmWhatsAppSecretProtector Protector(string? active, params (string Id, byte[]? Key)[] keys)
    {
        var options = new SecretProtectionOptions { ActiveKeyId = active };
        foreach (var (id, key) in keys)
        {
            options.Keys[id] = key is null ? "" : Convert.ToBase64String(key);
        }

        return new AesGcmWhatsAppSecretProtector(Options.Create(options));
    }

    [Fact]
    public void ProtectThenUnprotectRoundTrips()
    {
        var protector = Protector("k1", ("k1", K1));

        var secret = protector.Protect(TenantId, Token);

        Assert.Equal("k1", secret.KeyId);
        Assert.Equal(Token, protector.Unprotect(TenantId, secret));
    }

    [Fact]
    public void TwoProtectionsOfTheSameTextDiffer()
    {
        var protector = Protector("k1", ("k1", K1));

        var first = protector.Protect(TenantId, Token);
        var second = protector.Protect(TenantId, Token);

        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.Equal(12 + Encoding.UTF8.GetByteCount(Token) + 16, first.Ciphertext.Length);
    }

    [Fact]
    public void AnotherTenantCannotUnprotect()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(TenantId, Token);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Guid.CreateVersion7(), secret));
    }

    // El AAD es exactamente el UTF-8 de "quotations.whatsapp.api_token:" + tenantId en formato D:
    // un texto cifrado a mano con ese AAD descifra.
    [Fact]
    public void TheAssociatedDataIsThePrefixAndTheTenantInFormatD()
    {
        var protector = Protector("k1", ("k1", K1));
        var plaintext = Encoding.UTF8.GetBytes(Token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(K1, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag,
                Encoding.UTF8.GetBytes("quotations.whatsapp.api_token:" + TenantId.ToString("D")));
        }

        var manual = new ProtectedSecret("k1", [.. nonce, .. ciphertext, .. tag]);

        Assert.Equal(Token, protector.Unprotect(TenantId, manual));
    }

    [Fact]
    public void AnUnknownKeyIdThrowsNamingTheConfigurationKeyAndNoKey()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = new ProtectedSecret("k9", new byte[40]);

        var error = Assert.Throws<InvalidOperationException>(() => protector.Unprotect(TenantId, secret));

        Assert.Contains("Quotations:SecretProtection:Keys:k9", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(K1), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterChangingTheActiveKeyOldSecretsReadWithTheirKeyAndNewOnesUseTheNewKey()
    {
        var old = Protector("k1", ("k1", K1)).Protect(TenantId, Token);
        var rotated = Protector("k2", ("k1", K1), ("k2", K2));

        var fresh = rotated.Protect(TenantId, Token);

        Assert.Equal(Token, rotated.Unprotect(TenantId, old));
        Assert.Equal("k2", fresh.KeyId);
        Assert.Equal(Token, rotated.Unprotect(TenantId, fresh));
    }

    [Fact]
    public void HasKeyTreatsAnEmptyValueAsAbsent()
    {
        var protector = Protector("k1", ("k1", K1), ("k2", null));

        Assert.True(protector.HasKey("k1"));
        Assert.False(protector.HasKey("k2"));
        Assert.False(protector.HasKey("k3"));
    }

    [Fact]
    public void AnEmptyActiveKeyIdIsNullAndProtectThrows()
    {
        var protector = Protector("", ("k1", K1));

        Assert.Null(protector.ActiveKeyId);
        var error = Assert.Throws<InvalidOperationException>(() => protector.Protect(TenantId, Token));
        Assert.Contains("Quotations:SecretProtection:ActiveKeyId", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryUnprotectIsFalseWithoutThrowingWhenTheKeyIsMissing()
    {
        var secret = Protector("k1", ("k1", K1)).Protect(TenantId, Token);

        Assert.False(Protector("k2", ("k2", K2)).TryUnprotect(TenantId, secret, out var plaintext));
        Assert.Null(plaintext);
    }

    [Fact]
    public void TryUnprotectIsFalseWhenAByteWasAltered()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(TenantId, Token);
        var damaged = secret.Ciphertext.ToArray();
        damaged[20] ^= 0xFF;

        Assert.False(protector.TryUnprotect(TenantId, secret with { Ciphertext = damaged }, out _));
    }

    [Fact]
    public void TryUnprotectIsFalseForAnotherTenant()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(TenantId, Token);

        Assert.False(protector.TryUnprotect(Guid.CreateVersion7(), secret, out _));
    }

    [Fact]
    public void TheCiphertextDoesNotContainTheTokenBytes()
    {
        var secret = Protector("k1", ("k1", K1)).Protect(TenantId, Token);

        Assert.True(secret.Ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Token)) < 0);
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~SecretProtectionOptionsValidatorTests|FullyQualifiedName~AesGcmWhatsAppSecretProtectorTests"
```

Esperado: compilación fallida, `CS0234: The type or namespace name 'SecretProtection' does not exist in the namespace 'Modules.Quotations.Infrastructure'`.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppSecretProtector.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Cifra y descifra la API key de la cuenta propia de WhatsApp de un tenant (spec 2026-10-07,
/// «Manejo del secreto»). La implementación vive en Infrastructure y la llave fuera de la base:
/// un volcado de la base sola no alcanza para leer una key (criterio 6).
/// </summary>
public interface IWhatsAppSecretProtector
{
    /// <summary>La llave con la que se cifra; <c>null</c> si falta o está vacía.</summary>
    string? ActiveKeyId { get; }

    /// <summary>La llave está configurada (vacío = ausente). No dice que sea la que cifró una
    /// fila: para eso, <see cref="TryUnprotect"/>.</summary>
    bool HasKey(string keyId);

    /// <summary>Cifra con <see cref="ActiveKeyId"/>; sin ella lanza.</summary>
    ProtectedSecret Protect(Guid tenantId, string plaintext);

    /// <summary>Descifra con la llave del <c>KeyId</c> del secreto; lanza si no puede.</summary>
    string Unprotect(Guid tenantId, ProtectedSecret secret);

    /// <summary><c>false</c> —sin lanzar y sin loguear— con la llave ausente, bytes dañados, AAD
    /// de otro tenant o una llave distinta con el mismo id.</summary>
    bool TryUnprotect(Guid tenantId, ProtectedSecret secret, out string? plaintext);
}
```

`src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptions.cs`:

```csharp
namespace Modules.Quotations.Infrastructure.SecretProtection;

/// <summary>
/// La sección <c>Quotations:SecretProtection</c> (spec 2026-10-07). Propia y registrada aparte de
/// <see cref="QuotationsOptions"/> para que sus reglas no se mezclen con las de WhatsApp y PDF.
///
/// <c>ActiveKeyId</c> no es secreto (va en el ConfigMap); cada <c>Keys:&lt;id&gt;</c> sí (va en el
/// Secret, desde una variable secreta del pipeline). <b>Vacío = ausente</b> en todas partes: el
/// harness de integración fija <c>Keys:k1 = ""</c> para tapar los user-secrets del developer.
/// </summary>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "Quotations:SecretProtection";

    public string? ActiveKeyId { get; init; }

    public Dictionary<string, string?> Keys { get; init; } = new(StringComparer.Ordinal);

    internal string? EffectiveActiveKeyId =>
        string.IsNullOrWhiteSpace(ActiveKeyId) ? null : ActiveKeyId.Trim();

    internal string? KeyValue(string keyId) =>
        Keys.TryGetValue(keyId, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    internal static string KeyPath(string keyId) => $"{SectionName}:Keys:{keyId}";
}
```

`src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptionsValidator.cs`:

```csharp
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Modules.Quotations.Infrastructure.SecretProtection;

/// <summary>
/// Falla rápido al arrancar (spec 2026-10-07, «Validación al arrancar»). En <c>Production</c>, todo:
/// activa presente y con valor, todo id con el patrón y toda llave declarada de 32 bytes —una llave
/// mal pegada en la variable del pipeline se descubre en el deploy, no cuando alguien guarda—.
/// Fuera de <c>Production</c>, sólo la activa si está declarada: las pruebas de integración corren
/// en <c>Development</c> con los user-secrets de quien las corre, y el harness no puede borrar una
/// <c>Keys:&lt;id&gt;</c> que no conoce.
///
/// Ningún mensaje lleva un valor: nombran la clave de configuración o el id.
/// </summary>
internal sealed partial class SecretProtectionOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<SecretProtectionOptions>
{
    private const int KeyLength = 32;

    [GeneratedRegex("^[a-z0-9]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();

    public ValidateOptionsResult Validate(string? name, SecretProtectionOptions options)
    {
        var failures = new List<string>();
        var active = options.EffectiveActiveKeyId;

        if (environment.IsProduction())
        {
            foreach (var keyId in options.Keys.Keys)
            {
                if (!KeyIdPattern().IsMatch(keyId))
                {
                    failures.Add(
                        $"{SecretProtectionOptions.KeyPath(keyId)} has an invalid id: key ids must match ^[a-z0-9]{{1,32}}$.");
                    continue;
                }

                if (options.KeyValue(keyId) is { } value && !IsValidKey(value))
                {
                    failures.Add(InvalidKey(keyId));
                }
            }

            if (active is null)
            {
                failures.Add(
                    $"{SecretProtectionOptions.SectionName}:ActiveKeyId is required in Production: "
                    + "without it no tenant can save its own WhatsApp API key.");
            }
            else
            {
                CheckActivePresence(options, active, failures);
            }
        }
        else if (active is not null && CheckActivePresence(options, active, failures)
            && !IsValidKey(options.KeyValue(active)!))
        {
            failures.Add(InvalidKey(active));
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    // true si la activa tiene id válido y valor; el formato del valor lo mira quien llama.
    private static bool CheckActivePresence(
        SecretProtectionOptions options, string active, List<string> failures)
    {
        if (!KeyIdPattern().IsMatch(active))
        {
            failures.Add(
                $"{SecretProtectionOptions.SectionName}:ActiveKeyId must match ^[a-z0-9]{{1,32}}$.");
            return false;
        }

        if (options.KeyValue(active) is null)
        {
            failures.Add(
                $"{SecretProtectionOptions.KeyPath(active)} is required: it is the active key "
                + $"({SecretProtectionOptions.SectionName}:ActiveKeyId).");
            return false;
        }

        return true;
    }

    private static bool IsValidKey(string value)
    {
        try
        {
            return Convert.FromBase64String(value).Length == KeyLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string InvalidKey(string keyId) =>
        $"{SecretProtectionOptions.KeyPath(keyId)} must be {KeyLength} bytes encoded in base64.";
}
```

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/AesGcmWhatsAppSecretProtector.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.SecretProtection;

namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>
/// AES-256-GCM en la caja de .NET, sin Data Protection (spec 2026-10-07, decisión 1): la llave
/// vive fuera de la base. Formato: <c>nonce(12) || ciphertext || tag(16)</c>, nonce aleatorio por
/// cifrado. AAD = UTF-8 de <c>"quotations.whatsapp.api_token:" + tenantId.ToString("D")</c>:
/// una fila copiada a otro tenant por SQL no descifra. El formato <c>D</c> es explícito porque
/// cualquier otro no descifraría lo ya guardado.
///
/// Singleton: lee las opciones una vez, al arrancar, que es cuando se validaron.
/// </summary>
internal sealed class AesGcmWhatsAppSecretProtector(IOptions<SecretProtectionOptions> options)
    : IWhatsAppSecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string AssociatedDataPrefix = "quotations.whatsapp.api_token:";

    private readonly SecretProtectionOptions settings = options.Value;

    public string? ActiveKeyId => settings.EffectiveActiveKeyId;

    public bool HasKey(string keyId) => settings.KeyValue(keyId) is not null;

    public ProtectedSecret Protect(Guid tenantId, string plaintext)
    {
        var keyId = ActiveKeyId ?? throw new InvalidOperationException(
            $"{SecretProtectionOptions.SectionName}:ActiveKeyId is not configured: "
            + "the WhatsApp API key cannot be encrypted.");
        var key = KeyBytes(keyId);

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + plain.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(
            nonce,
            plain,
            output.AsSpan(NonceSize, plain.Length),
            output.AsSpan(NonceSize + plain.Length, TagSize),
            AssociatedData(tenantId));

        return new ProtectedSecret(keyId, output);
    }

    public string Unprotect(Guid tenantId, ProtectedSecret secret)
    {
        var key = KeyBytes(secret.KeyId);
        var data = secret.Ciphertext;
        if (data.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The protected secret is malformed.");
        }

        var length = data.Length - NonceSize - TagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            data.AsSpan(0, NonceSize),
            data.AsSpan(NonceSize, length),
            data.AsSpan(NonceSize + length, TagSize),
            plain,
            AssociatedData(tenantId));

        return Encoding.UTF8.GetString(plain);
    }

    public bool TryUnprotect(Guid tenantId, ProtectedSecret secret, out string? plaintext)
    {
        try
        {
            plaintext = Unprotect(tenantId, secret);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or CryptographicException)
        {
            plaintext = null;
            return false;
        }
    }

    private byte[] KeyBytes(string keyId)
    {
        var value = settings.KeyValue(keyId) ?? throw new InvalidOperationException(
            $"{SecretProtectionOptions.KeyPath(keyId)} is not configured: "
            + "a WhatsApp API key stored with that key cannot be used.");
        try
        {
            var key = Convert.FromBase64String(value);
            if (key.Length == 32)
            {
                return key;
            }
        }
        catch (FormatException)
        {
            // Cae al mensaje de abajo: el de FormatException no nombra la clave.
        }

        throw new InvalidOperationException(
            $"{SecretProtectionOptions.KeyPath(keyId)} must be 32 bytes encoded in base64.");
    }

    private static byte[] AssociatedData(Guid tenantId) =>
        Encoding.UTF8.GetBytes(AssociatedDataPrefix + tenantId.ToString("D"));
}
```

En `QuotationsInfrastructureExtensions.cs`, agregar `using Modules.Quotations.Infrastructure.SecretProtection;` y, justo después de `services.AddSingleton<IValidateOptions<QuotationsOptions>, QuotationsOptionsValidator>();` (`:67`):

```csharp
        // Spec 2026-10-07: la llave del cifrado de la API key de WhatsApp por tenant. Sección y
        // validador propios, no parte de QuotationsOptions, para que sus reglas se prueben solas.
        services.AddOptions<SecretProtectionOptions>()
            .Bind(configuration.GetSection(SecretProtectionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SecretProtectionOptions>, SecretProtectionOptionsValidator>();
        services.AddSingleton<IWhatsAppSecretProtector, AesGcmWhatsAppSecretProtector>();
```

En `src/Api/appsettings.example.json`, dentro de `"Quotations"`, cambiar el cierre de `PaymentProofs` para sumar la sección **vacía** (nunca un valor con forma de llave: termina copiado a un ambiente):

```json
    "PaymentProofs": {
      "PublicLinks": false
    },
    "SecretProtection": {
      "ActiveKeyId": "",
      "Keys": {}
    }
```

En `QuotationsApiHarness.cs`, agregar al principio de la clase (después de `NewYearsEveInBogota`, `:51`):

```csharp
    /// <summary>La llave con la que las pruebas cifran la API key de WhatsApp (spec 2026-10-07).
    /// Calculada, no escrita: un literal con forma de llave en el repo termina copiado a un
    /// ambiente. Bytes 0..31.</summary>
    public static string TestSecretProtectionKey { get; } =
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());
```

y, en `ConfigureWebHost`, después de las tres líneas `Quotations:WhatsApp:*` (`:876-878`):

```csharp
            // Mismo criterio que Zenvia: fijadas, nunca heredadas de los user-secrets. "test" es la
            // activa; "k1" —el id que el README sugiere para local— se vacía para que una llave
            // mal pegada en la máquina de quien corre las pruebas no las tumbe al arrancar.
            builder.UseSetting("Quotations:SecretProtection:ActiveKeyId", "test");
            builder.UseSetting("Quotations:SecretProtection:Keys:test", TestSecretProtectionKey);
            builder.UseSetting("Quotations:SecretProtection:Keys:k1", string.Empty);
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~SecretProtectionOptionsValidatorTests|FullyQualifiedName~AesGcmWhatsAppSecretProtectorTests|FullyQualifiedName~QuotationsOptionsValidatorTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~OrdersExportLayoutApiTests"
```

Esperado: las tres corridas en verde. `QuotationsOptionsValidatorTests` sin cambios (las claves globales siguen exigidas en producción). `ConfigurationExampleTests` (en `ArchitectureTests`) verde con la sección nueva; si falla con `Quotations:SecretProtection:...`, el JSON del ejemplo no quedó dentro de `"Quotations"`. La corrida de integración prueba que el host arranca con las llaves del harness.

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppSecretProtector.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptions.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptionsValidator.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/AesGcmWhatsAppSecretProtector.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs src/Api/appsettings.example.json tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/SecretProtectionOptionsValidatorTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/AesGcmWhatsAppSecretProtectorTests.cs; git commit -m "feat(quotations): cifrado AES-GCM de la API key de WhatsApp con llave en configuración"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B3: Persistencia, migración y traducción de la PK

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/ITenantWhatsAppSettingsRepository.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/TenantWhatsAppSettingsRepository.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:33,48,~620`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs:38,110-122`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:57`
- Create (generado): `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<ts>_AddTenantWhatsAppSettings.cs` y `.Designer.cs`; modificado (generado): `QuotationsDbContextModelSnapshot.cs`
- Create: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs` (primera versión)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSettingsPersistenceTests.cs`

**Interfaces:**
- Consumes: `TenantWhatsAppSettings`, `ProtectedSecret` (B1); `IWhatsAppSecretProtector` (B2); `QuotationsApiHarness.StartDatabaseAsync`, `QepApiFactory`, `RegisterTenantAsync`.
- Produces:
  - `public interface ITenantWhatsAppSettingsRepository { Task<TenantWhatsAppSettings?> FindAsync(Guid, CancellationToken); Task<TenantWhatsAppSettings?> FindReadOnlyAsync(Guid, CancellationToken); void Add(TenantWhatsAppSettings); }`
  - `QuotationsDbContext.WhatsAppSettings` (`internal DbSet<TenantWhatsAppSettings>`)
  - `WhatsAppTestHarness` (static, `QI`): `ScalarAsync<T>(string connectionString, string sql)`, `ExecuteAsync(string connectionString, string sql)`, `const string TemplateId`, `const string FromNumber`, `const string SentinelApiKey`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs`:

```csharp
using Npgsql;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas de WhatsApp por tenant (spec 2026-10-07). Aparte de
/// <see cref="QuotationsApiHarness"/> porque nada de esto lo usa otra suite.
/// </summary>
internal static class WhatsAppTestHarness
{
    /// <summary>Una key de Zenvia inventada y fácil de buscar: la prueba de fugas la persigue por
    /// respuestas, logs y <c>platform.request_failures</c>. ASCII visible, sin espacios: pasa el
    /// validador.</summary>
    public const string SentinelApiKey = "zenvia-key-SENTINEL-7f3a9c";

    public const string FromNumber = "573001234567";

    public const string TemplateId = "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f";

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static async Task<int> ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
```

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSettingsPersistenceTests.cs`:

```csharp
using System.Text;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using static Modules.Quotations.IntegrationTests.WhatsAppTestHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La configuración de WhatsApp contra Postgres (spec 2026-10-07, «Modelo de datos»): ida y vuelta
/// con la key cifrada en dos columnas de la misma fila, la key que nunca queda en claro, y dos
/// primeros guardados que chocan en la PK y salen como 412.
/// </summary>
public sealed class WhatsAppSettingsPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheOwnAccountRoundTripsWithTheKeyEncryptedWithTheActiveKey()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<IWhatsAppSecretProtector>();
            var repository = scope.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>();
            Assert.Null(await repository.FindAsync(tenantId, TestContext.Current.CancellationToken));

            var settings = TenantWhatsAppSettings.CreateEmpty(tenantId, Now);
            settings.Configure(
                WhatsAppMode.Own, WhatsAppProvider.Zenvia, protector.Protect(tenantId, SentinelApiKey),
                null, FromNumber, TemplateId, Now);
            repository.Add(settings);
            await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<IWhatsAppSecretProtector>();
            var reloaded = await scope.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>()
                .FindReadOnlyAsync(tenantId, TestContext.Current.CancellationToken);

            Assert.NotNull(reloaded);
            Assert.Equal(WhatsAppMode.Own, reloaded.Mode);
            Assert.Equal(WhatsAppProvider.Zenvia, reloaded.Provider);
            Assert.Equal(FromNumber, reloaded.FromNumber);
            Assert.Equal(TemplateId, reloaded.TemplateId);
            Assert.Equal(2, reloaded.Version);
            Assert.Equal(Now, reloaded.ApiTokenUpdatedAt);
            Assert.NotNull(reloaded.ApiToken);
            Assert.Equal("test", reloaded.ApiToken.KeyId);
            Assert.Equal(SentinelApiKey, protector.Unprotect(tenantId, reloaded.ApiToken));
        }

        Assert.Equal("Own", await ScalarAsync<string>(
            database.GetConnectionString(),
            $"SELECT mode FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'"));
    }

    // Criterio 6: la columna guardada no tiene la key en claro.
    [Fact]
    public async Task TheStoredCiphertextDoesNotContainTheKey()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<IWhatsAppSecretProtector>();
            var settings = TenantWhatsAppSettings.CreateEmpty(tenantId, Now);
            settings.Configure(
                WhatsAppMode.Own, WhatsAppProvider.Zenvia, protector.Protect(tenantId, SentinelApiKey),
                null, FromNumber, TemplateId, Now);
            scope.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>().Add(settings);
            await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var stored = await ScalarAsync<byte[]>(
            database.GetConnectionString(),
            $"SELECT api_token_ciphertext FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'");

        Assert.True(stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes(SentinelApiKey)) < 0);
    }

    // Sin fila, los dos primeros PUT viajan con If-Match "1" y los dos intentan INSERT.
    [Fact]
    public async Task TwoFirstSavesForTheSameTenantEndInAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;

        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var firstSettings = TenantWhatsAppSettings.CreateEmpty(tenantId, Now);
        var secondSettings = TenantWhatsAppSettings.CreateEmpty(tenantId, Now);
        firstSettings.Configure(WhatsAppMode.Disabled, null, null, null, null, null, Now);
        secondSettings.Configure(WhatsAppMode.Disabled, null, null, null, null, null, Now);
        first.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>().Add(firstSettings);
        second.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>().Add(secondSettings);
        await first.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            second.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~WhatsAppSettingsPersistenceTests"
```

Esperado: compilación fallida, `CS0246: The type or namespace name 'ITenantWhatsAppSettingsRepository' could not be found`.

- [ ] **Step 3: Implementar el puerto, el adaptador y el mapeo**

`src/Modules/Quotations/Modules.Quotations.Application/ITenantWhatsAppSettingsRepository.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// La configuración de WhatsApp, una por tenant (spec 2026-10-07). Sin <c>Update</c>:
/// <see cref="FindAsync"/> devuelve la entidad rastreada y
/// <see cref="IQuotationsUnitOfWork.SaveChangesAsync"/> persiste el <c>Configure</c>, como el resto
/// de repositorios del módulo. Sin fila, el llamador arma
/// <see cref="TenantWhatsAppSettings.CreateEmpty"/> y la agrega si algo cambió.
/// </summary>
public interface ITenantWhatsAppSettingsRepository
{
    /// <summary>Rastreada: para el PUT y el re-cifrado.</summary>
    Task<TenantWhatsAppSettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Sin tracking: el envío y los GET sólo leen.</summary>
    Task<TenantWhatsAppSettings?> FindReadOnlyAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(TenantWhatsAppSettings settings);
}
```

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/TenantWhatsAppSettingsRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class TenantWhatsAppSettingsRepository(QuotationsDbContext dbContext)
    : ITenantWhatsAppSettingsRepository
{
    public Task<TenantWhatsAppSettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.WhatsAppSettings.SingleOrDefaultAsync(
            settings => settings.TenantId == tenantId, cancellationToken);

    public Task<TenantWhatsAppSettings?> FindReadOnlyAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.WhatsAppSettings.AsNoTracking().SingleOrDefaultAsync(
            settings => settings.TenantId == tenantId, cancellationToken);

    public void Add(TenantWhatsAppSettings settings) => dbContext.WhatsAppSettings.Add(settings);
}
```

En `QuotationsDbContext.cs`: después de `internal DbSet<OrdersExportLayout> OrdersExportLayouts …` (`:33`):

```csharp
    internal DbSet<TenantWhatsAppSettings> WhatsAppSettings => Set<TenantWhatsAppSettings>();
```

en `OnModelCreating`, después de `ConfigureOrdersExportLayout(modelBuilder);` (`:48`):

```csharp
        ConfigureTenantWhatsAppSettings(modelBuilder);
```

y el método, después de `ConfigureOrdersExportLayout`:

```csharp
    /// <summary>
    /// Spec 2026-10-07, «Modelo de datos». PK por tenant nombrada a propósito: dos primeros PUT
    /// simultáneos chocan acá y QuotationsUnitOfWork lo traduce a 412 por nombre. La key cifrada es
    /// un owned opcional en dos columnas de la misma fila (como BillingAccount en quotations); el
    /// CHECK token_pair garantiza que viajan juntas. Sin FK a tenancy.tenants: un DbContext por
    /// módulo, mismo criterio que orders_export_layouts.
    /// </summary>
    private static void ConfigureTenantWhatsAppSettings(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<TenantWhatsAppSettings>();
        settings.ToTable("tenant_whatsapp_settings", "quotations", table =>
        {
            table.HasCheckConstraint(
                "CK_tenant_whatsapp_settings_mode",
                "mode IN ('Shared', 'Own', 'Disabled')");
            table.HasCheckConstraint(
                "CK_tenant_whatsapp_settings_provider",
                "provider IS NULL OR provider IN ('Zenvia')");
            table.HasCheckConstraint(
                "CK_tenant_whatsapp_settings_token_pair",
                "(api_token_ciphertext IS NULL) = (api_token_key_id IS NULL)");
            table.HasCheckConstraint(
                "CK_tenant_whatsapp_settings_own_complete",
                "mode <> 'Own' OR (provider IS NOT NULL AND api_token_ciphertext IS NOT NULL "
                + "AND from_number IS NOT NULL AND template_id IS NOT NULL)");
        });
        settings.HasKey(value => value.TenantId).HasName("PK_tenant_whatsapp_settings");
        settings.Property(value => value.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        settings.Property(value => value.Mode)
            .HasColumnName("mode")
            .HasConversion<string>()
            .HasMaxLength(16);
        settings.Property(value => value.Provider)
            .HasColumnName("provider")
            .HasConversion<string>()
            .HasMaxLength(16);
        settings.Property(value => value.ApiTokenUpdatedAt).HasColumnName("api_token_updated_at");
        settings.Property(value => value.FromNumber)
            .HasColumnName("from_number")
            .HasMaxLength(TenantWhatsAppSettings.FromNumberMaxLength);
        settings.Property(value => value.TemplateId)
            .HasColumnName("template_id")
            .HasMaxLength(TenantWhatsAppSettings.TemplateIdMaxLength);
        settings.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        settings.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        settings.OwnsOne(value => value.ApiToken, token =>
        {
            token.Property(value => value.KeyId)
                .HasColumnName("api_token_key_id")
                .HasMaxLength(ProtectedSecret.KeyIdMaxLength);
            token.Property(value => value.Ciphertext).HasColumnName("api_token_ciphertext");
        });
        settings.Navigation(value => value.ApiToken).IsRequired(false);
    }
```

En `QuotationsUnitOfWork.cs`, después de `OrdersExportLayoutKey` (`:38`):

```csharp
    // La PK de la configuración de WhatsApp (spec 2026-10-07): mismo caso que el layout. Sin fila,
    // dos primeros PUT con If-Match "1" intentan INSERT y el segundo choca acá: es un 412, no un
    // 422, porque el tenant no hizo nada mal.
    private const string TenantWhatsAppSettingsKey = "PK_tenant_whatsapp_settings";
```

y un `catch` más, después del de `OrdersExportLayoutKey` (`:110-122`):

```csharp
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      TenantWhatsAppSettingsKey,
                      StringComparison.Ordinal))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The WhatsApp settings were created by another request while this one was being committed.",
                exception);
        }
```

En `QuotationsInfrastructureExtensions.cs`, después de `services.AddScoped<IOrdersExportLayoutRepository, OrdersExportLayoutRepository>();` (`:57`):

```csharp
        // Spec 2026-10-07: la configuración de WhatsApp por tenant. Scoped: la leen el PUT/GET, el
        // envío y el re-cifrado, cada uno en su scope.
        services.AddScoped<ITenantWhatsAppSettingsRepository, TenantWhatsAppSettingsRepository>();
```

- [ ] **Step 4: Generar la migración**

Bloque de guardia y:

```powershell
dotnet build --no-restore
dotnet ef migrations add AddTenantWhatsAppSettings --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
git status --short src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations
```

Esperado: tres archivos (`<ts>_AddTenantWhatsAppSettings.cs`, su `.Designer.cs` y el snapshot modificado). Revisa el `Up` a ojo, sin editarlo: un solo `CreateTable` de `tenant_whatsapp_settings` en el schema `quotations`, con `api_token_ciphertext` `bytea` nullable, `api_token_key_id` `character varying(32)` nullable, `from_number` `character varying(15)`, `template_id` `character varying(64)`, `mode` `character varying(16)` no nulo, `version` `bigint`, PK `PK_tenant_whatsapp_settings` y las cuatro `CheckConstraint`. Si el `Up` toca cualquier otra tabla, el modelo tenía cambios previos sin migrar: **detente** y repórtalo. `Down` = `DropTable`.

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~WhatsAppSettingsPersistenceTests|FullyQualifiedName~OrdersExportLayoutPersistenceTests|FullyQualifiedName~OrdersMigrationTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: todo verde. `OrdersExportLayoutPersistenceTests` y `OrdersMigrationTests` son la guarda de que la migración nueva no rompió las anteriores.

- [ ] **Step 6: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/ITenantWhatsAppSettingsRepository.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/TenantWhatsAppSettingsRepository.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSettingsPersistenceTests.cs; git commit -m "feat(quotations): tabla tenant_whatsapp_settings y su repositorio"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---
### Task B4: `ZenviaWhatsAppSender` por cuenta, con errores sin cuerpo crudo en la cuenta propia

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaSenderSettings.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaHttpClient.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaWhatsAppSender.cs:1-107`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:100-122`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ZenviaWhatsAppSenderTests.cs`

**Interfaces:**
- Consumes: `WhatsAppOptions` (existente).
- Produces:
  - `internal enum ZenviaAccount { Qep, Tenant }`
  - `internal sealed record ZenviaSenderSettings(string ApiToken, string FromNumber, string TemplateId, string BaseUrl, ZenviaAccount Account)` con `static ZenviaSenderSettings ForQep(WhatsAppOptions)` y `ToString()` con `***`
  - `internal sealed class ZenviaHttpClient(HttpClient client) { HttpClient Client }`, singleton
  - `ZenviaWhatsAppSender(HttpClient httpClient, ZenviaSenderSettings settings, ILogger<ZenviaWhatsAppSender> logger)`
  - Códigos: `quotation.whatsapp.credentials_rejected` (sólo `Tenant`, 401/403).

- [ ] **Step 1: Edición mecánica de las pruebas existentes y pruebas nuevas que fallan**

En `ZenviaWhatsAppSenderTests.cs`:

1. Quitar `using Microsoft.Extensions.Options;` y `using Modules.Quotations.Infrastructure;` (dejan de usarse).
2. Reemplazar `NewSender` (`:177-200`) por esta versión —mismas piezas, la configuración viaja como `ZenviaSenderSettings`; las pruebas existentes no cambian sus afirmaciones (criterio 1):

```csharp
    private static (IWhatsAppSender Sender, RequestCapture Capture, RecordingLogger Logger)
        NewSender(
            HttpStatusCode status = HttpStatusCode.OK,
            string responseBody = "{}",
            ZenviaAccount account = ZenviaAccount.Qep)
    {
        var capture = new RequestCapture();
        var logger = new RecordingLogger();
        var settings = new ZenviaSenderSettings(
            "token-de-prueba", FromNumber, TemplateId, "https://api.zenvia.com", account);

        return (
            new ZenviaWhatsAppSender(
                new HttpClient(new CapturingHandler(capture, status, responseBody)),
                settings,
                logger),
            capture,
            logger);
    }
```

3. Agregar, antes de `NewSender`:

```csharp
    // Spec 2026-10-07, «Sólo escritura y nunca en un log»: el sender de una cuenta propia nunca
    // mete el cuerpo de Zenvia en el mensaje —que llega a ProblemDetails, al log y a
    // platform.request_failures—; sólo el estado y, si cumple el patrón, el código de Zenvia.
    private const string SentinelBody = "cuerpo-SENTINEL-de-zenvia";

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ATenantAccountWithRejectedCredentialsSaysSoWithoutTheBody(HttpStatusCode status)
    {
        var (sender, _, _) = NewSender(status, $$"""{"message":"{{SentinelBody}}"}""", ZenviaAccount.Tenant);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.credentials_rejected", error.Code);
        Assert.Equal($"Zenvia responded {(int)status}.", error.Message);
        Assert.DoesNotContain(SentinelBody, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATenantAccountWithAnotherFailureIsSendFailedWithoutTheBody()
    {
        var (sender, _, _) = NewSender(HttpStatusCode.InternalServerError, SentinelBody, ZenviaAccount.Tenant);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.send_failed", error.Code);
        Assert.Equal("Zenvia responded 500.", error.Message);
    }

    [Fact]
    public async Task ATenantAccountCarriesTheZenviaErrorCodeButNotTheBody()
    {
        var (sender, _, _) = NewSender(
            HttpStatusCode.BadRequest, $$"""{"code":"XYZ","message":"{{SentinelBody}}"}""", ZenviaAccount.Tenant);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.send_failed", error.Code);
        Assert.Equal("Zenvia responded 400 (code: XYZ).", error.Message);
        Assert.DoesNotContain(SentinelBody, error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> CodesOutsideThePattern => new()
    {
        "con espacios",
        new string('A', 65),
        "texto<libre>",
    };

    [Theory]
    [MemberData(nameof(CodesOutsideThePattern))]
    public async Task ATenantAccountDropsACodeOutsideThePattern(string code)
    {
        var body = JsonSerializer.Serialize(new { code });
        var (sender, _, _) = NewSender(HttpStatusCode.BadRequest, body, ZenviaAccount.Tenant);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken));

        Assert.Equal("Zenvia responded 400.", error.Message);
    }

    // La cuenta de QEP: el administrador del tenant no puede arreglar sus credenciales, así que un
    // 401 no es "revisa tu API key". Y conserva el cuerpo, que es la única pista para diagnosticar
    // la plantilla de QEP (decisión 14).
    [Fact]
    public async Task TheQepAccountTreatsRejectedCredentialsAsAGenericSendFailure()
    {
        var (sender, _, _) = NewSender(HttpStatusCode.Unauthorized, SentinelBody, ZenviaAccount.Qep);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.send_failed", error.Code);
        Assert.Contains(SentinelBody, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZenviaSenderSettingsToStringHidesTheToken()
    {
        var settings = new ZenviaSenderSettings(
            "token-SENTINEL", FromNumber, TemplateId, "https://api.zenvia.com", ZenviaAccount.Tenant);

        Assert.DoesNotContain("token-SENTINEL", settings.ToString(), StringComparison.Ordinal);
        Assert.Contains("***", settings.ToString(), StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~ZenviaWhatsAppSenderTests"
```

Esperado: compilación fallida, `CS0246: The type or namespace name 'ZenviaSenderSettings' could not be found` y `'ZenviaAccount' could not be found`.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaSenderSettings.cs`:

```csharp
namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>De quién es la cuenta de Zenvia (spec 2026-10-07): cambia cómo se reportan las
/// fallas, no cómo se envía.</summary>
internal enum ZenviaAccount
{
    /// <summary>La cuenta global de QEP (modo <c>Shared</c>).</summary>
    Qep,

    /// <summary>La cuenta propia del tenant (modo <c>Own</c>).</summary>
    Tenant
}

/// <summary>
/// Con qué cuenta sale un envío. <see cref="ToString"/> esconde el token: es un record que lo
/// lleva en claro, y el de un record imprime todas sus propiedades (spec 2026-10-07).
/// </summary>
internal sealed record ZenviaSenderSettings(
    string ApiToken,
    string FromNumber,
    string TemplateId,
    string BaseUrl,
    ZenviaAccount Account)
{
    public static ZenviaSenderSettings ForQep(WhatsAppOptions options) =>
        new(options.ApiToken, options.FromNumber, options.TemplateId, options.BaseUrl, ZenviaAccount.Qep);

    public override string ToString() =>
        $"ZenviaSenderSettings {{ ApiToken = ***, FromNumber = {FromNumber}, TemplateId = {TemplateId}, "
        + $"BaseUrl = {BaseUrl}, Account = {Account} }}";
}
```

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaHttpClient.cs`:

```csharp
namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>
/// El único <see cref="HttpClient"/> de Zenvia del proceso (spec 2026-10-07): lo comparten el sender
/// de la cuenta de QEP y los que se arman por envío para cada cuenta propia. Uno por envío agotaría
/// sockets. El token viaja por request en <c>X-API-TOKEN</c>, así que compartirlo no mezcla
/// credenciales. Envoltorio y no <see cref="HttpClient"/> suelto para que las pruebas de
/// integración lo reemplacen sin tocar ningún otro cliente HTTP del host.
/// </summary>
internal sealed class ZenviaHttpClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}
```

En `ZenviaWhatsAppSender.cs`:

- `using`: quitar `using Microsoft.Extensions.Options;`; agregar `using System.Net;` y `using System.Text.RegularExpressions;`.
- Reemplazar la firma y el campo (`:21-33`):

```csharp
internal sealed partial class ZenviaWhatsAppSender(
    HttpClient httpClient,
    ZenviaSenderSettings settings,
    ILogger<ZenviaWhatsAppSender> logger)
    : IWhatsAppSender
{
    // El formato con el que el cliente ve el monto y la vigencia lo fija el locale de la
    // plantilla (`es` en Zenvia), no el contrato de Application — por eso se arma acá.
    private static readonly CultureInfo Colombia = CultureInfo.GetCultureInfo("es-CO");

    private const string UnknownMessageId = "(unknown)";

    // Spec 2026-10-07: el código de error de Zenvia sólo entra al mensaje si tiene esta forma. El
    // patrón impide que un texto libre del cuerpo se cuele por ese campo.
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ZenviaErrorCodePattern();
```

- Actualizar el `<summary>` de la clase (`:11-20`): ya no lee `Quotations:WhatsApp:*` por su cuenta; lo arma `AddWhatsAppSender` para la cuenta de QEP y `WhatsAppChannelResolver` por envío para una cuenta propia.
- Reemplazar el bloque de error (`:93-98`) por:

```csharp
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response.StatusCode, body);
        }
```

- Agregar, después de `ReadMessageId`:

```csharp
    /// <summary>
    /// Cuenta de QEP: como siempre, <c>send_failed</c> con el cuerpo (decisión 14 del spec).
    /// Cuenta propia: 401/403 son <c>credentials_rejected</c> —lo único que el administrador puede
    /// arreglar desde Configuración— y nunca el cuerpo crudo, que podría devolver lo que la persona
    /// pegó: sólo el estado y, si cumple el patrón, el código de Zenvia.
    /// </summary>
    private QuotationsDomainException Failure(HttpStatusCode status, string body)
    {
        var statusCode = (int)status;
        if (settings.Account == ZenviaAccount.Qep)
        {
            return new QuotationsDomainException(
                "quotation.whatsapp.send_failed", $"Zenvia responded {statusCode}: {body}");
        }

        var zenviaCode = ReadErrorCode(body);
        var message = zenviaCode is null
            ? $"Zenvia responded {statusCode}."
            : $"Zenvia responded {statusCode} (code: {zenviaCode}).";

        return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new QuotationsDomainException("quotation.whatsapp.credentials_rejected", message)
            : new QuotationsDomainException("quotation.whatsapp.send_failed", message);
    }

    // Tolerante, como ReadMessageId: la forma de error de Zenvia no está verificada en el código.
    private static string? ReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() is { } value
                && ZenviaErrorCodePattern().IsMatch(value)
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
```

El resto del cuerpo (`settings.FromNumber`, `settings.TemplateId`, `settings.BaseUrl`, `settings.ApiToken`) compila sin cambios: los nombres coinciden con las propiedades del record.

En `QuotationsInfrastructureExtensions.cs`, reemplazar `AddWhatsAppSender` (`:100-122`) por:

```csharp
    private static void AddWhatsAppSender(IServiceCollection services, IConfigurationSection whatsApp)
    {
        // Spec 2026-10-07: un solo HttpClient para la cuenta de QEP y las de cada tenant, siempre
        // registrado (las cuentas propias lo necesitan aunque la de QEP no esté configurada).
        // `new HttpClient()` sin IHttpClientFactory, mismo criterio que InfobipEmailChannel.
        services.AddSingleton(_ => new ZenviaHttpClient(new HttpClient()));

        var configured =
            !string.IsNullOrWhiteSpace(whatsApp[nameof(WhatsAppOptions.ApiToken)]) &&
            !string.IsNullOrWhiteSpace(whatsApp[nameof(WhatsAppOptions.FromNumber)]) &&
            !string.IsNullOrWhiteSpace(whatsApp[nameof(WhatsAppOptions.TemplateId)]);

        if (configured)
        {
            services.AddSingleton<IWhatsAppSender>(sp =>
                new ZenviaWhatsAppSender(
                    sp.GetRequiredService<ZenviaHttpClient>().Client,
                    ZenviaSenderSettings.ForQep(
                        sp.GetRequiredService<IOptions<QuotationsOptions>>().Value.WhatsApp),
                    sp.GetRequiredService<ILogger<ZenviaWhatsAppSender>>()));
        }
        else
        {
            services.AddSingleton<IWhatsAppSender, LogWhatsAppSender>();
        }
    }
```

(El `<summary>` de `:77-99` sigue valiendo; sumarle una línea: «Desde el spec 2026-10-07 éste es el sender de la cuenta de QEP; el canal de cada envío lo decide `WhatsAppChannelResolver`».)

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~ZenviaWhatsAppSenderTests"
```

Esperado: todas `Superado`, incluidas las diez existentes sin tocar sus aserciones (`SendSurfacesAZenviaRejectionAsADomainError` sigue viendo `INVALID_TEMPLATE` porque la cuenta por defecto es `Qep`).

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaSenderSettings.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaHttpClient.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaWhatsAppSender.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ZenviaWhatsAppSenderTests.cs; git commit -m "feat(quotations): Zenvia por cuenta, sin el cuerpo crudo en los errores de una cuenta propia"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B5: `WhatsAppChannelResolver`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppChannelResolver.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppChannelResolver.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs` (registro)
- Create: `tests/Modules/Quotations/Modules.Quotations.UnitTests/WhatsAppSettingsTestDoubles.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/WhatsAppChannelResolverTests.cs`

**Interfaces:**
- Consumes: `ITenantWhatsAppSettingsRepository` (B3), `IWhatsAppSecretProtector` (B2), `ZenviaHttpClient`, `ZenviaSenderSettings`, `ZenviaAccount` (B4), `IWhatsAppSender` global.
- Produces:
  - `public sealed record WhatsAppChannel(WhatsAppMode Mode, IWhatsAppSender? Sender)` — `Sender` nulo ⇔ `Disabled`
  - `public interface IWhatsAppChannelResolver { Task<WhatsAppChannel> ResolveAsync(Guid tenantId, CancellationToken cancellationToken); }`
  - Dobles (`QU/WhatsAppSettingsTestDoubles.cs`): `InMemoryTenantWhatsAppSettingsRepository` (`Rows`, `FindCalls`, `Add`), `FakeWhatsAppSecretProtector` (`ActiveKeyId`, `KnownKeys`, `ProtectedPlaintexts`, `Seed(keyId, plaintext)`, `Garbage(keyId)`), `StubTenantModules` (`AllEnabled()`, `Without(key)`, `UnknownTenant()`, `Calls`). B8 y B9 los usan.

- [ ] **Step 1: Escribir los dobles y las pruebas que fallan**

Antes de crear `StubTenantModules`, comprobar si el plan de entitlements ya dejó uno:

```powershell
rg -n "ITenantModules" tests/Modules/Quotations/Modules.Quotations.UnitTests
```

Si aparece un doble de `ITenantModules`, **no** crees `StubTenantModules`: usa el existente y adapta las llamadas de B8/B9 (`AllEnabled()`, `Without(TenantModuleKeys.Quotations)`, `UnknownTenant()`) a su API. Si no aparece, crea el de abajo.

`tests/Modules/Quotations/Modules.Quotations.UnitTests/WhatsAppSettingsTestDoubles.cs`:

```csharp
using System.Security.Cryptography;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>La configuración de WhatsApp en memoria (spec 2026-10-07). Las dos lecturas devuelven
/// la misma instancia: lo que la prueba mira es qué hizo el handler con ella.</summary>
internal sealed class InMemoryTenantWhatsAppSettingsRepository : ITenantWhatsAppSettingsRepository
{
    public List<TenantWhatsAppSettings> Rows { get; } = [];

    public int FindCalls { get; private set; }

    public Task<TenantWhatsAppSettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        FindCalls++;
        return Task.FromResult(Rows.FirstOrDefault(row => row.TenantId == tenantId));
    }

    public Task<TenantWhatsAppSettings?> FindReadOnlyAsync(Guid tenantId, CancellationToken cancellationToken) =>
        FindAsync(tenantId, cancellationToken);

    public void Add(TenantWhatsAppSettings settings) => Rows.Add(settings);
}

/// <summary>
/// Un protector sin criptografía: recuerda qué texto corresponde a cada arreglo de bytes (por
/// referencia) y "descifra" sólo si conoce la llave del <c>KeyId</c> y los bytes. Con eso se
/// simulan los tres casos que importan —legible, llave retirada, bytes dañados— sin depender de
/// AES, que tiene sus propias pruebas.
/// </summary>
internal sealed class FakeWhatsAppSecretProtector : IWhatsAppSecretProtector
{
    private readonly Dictionary<byte[], string> _plaintexts = new(ReferenceEqualityComparer.Instance);

    public string? ActiveKeyId { get; set; } = "k2";

    public HashSet<string> KnownKeys { get; } = ["k1", "k2"];

    /// <summary>Lo que se mandó a cifrar, en orden.</summary>
    public List<string> ProtectedPlaintexts { get; } = [];

    public bool HasKey(string keyId) => KnownKeys.Contains(keyId);

    public ProtectedSecret Protect(Guid tenantId, string plaintext)
    {
        var keyId = ActiveKeyId ?? throw new InvalidOperationException(
            "Quotations:SecretProtection:ActiveKeyId is not configured.");
        ProtectedPlaintexts.Add(plaintext);
        return Seed(keyId, plaintext);
    }

    /// <summary>Un secreto legible, cifrado "con" <paramref name="keyId"/>, sin pasar por
    /// <see cref="ProtectedPlaintexts"/>: lo que ya estaba guardado antes de la prueba.</summary>
    public ProtectedSecret Seed(string keyId, string plaintext)
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        _plaintexts[bytes] = plaintext;
        return new ProtectedSecret(keyId, bytes);
    }

    /// <summary>Bytes que no descifran con ninguna llave: un tag inválido.</summary>
    public static ProtectedSecret Garbage(string keyId) => new(keyId, RandomNumberGenerator.GetBytes(16));

    public string Unprotect(Guid tenantId, ProtectedSecret secret) =>
        TryUnprotect(tenantId, secret, out var plaintext)
            ? plaintext!
            : throw new CryptographicException("The protected secret cannot be decrypted.");

    public bool TryUnprotect(Guid tenantId, ProtectedSecret secret, out string? plaintext)
    {
        plaintext = null;
        return KnownKeys.Contains(secret.KeyId) && _plaintexts.TryGetValue(secret.Ciphertext, out plaintext);
    }
}

/// <summary>Las capacidades del tenant (spec de entitlements). <see cref="UnknownTenant"/> es el
/// tenant del stub de desarrollo: <c>FindAsync</c> en null y el guard no lanza.</summary>
internal sealed class StubTenantModules(TenantModuleSet? modules) : ITenantModules
{
    public int Calls { get; private set; }

    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(modules);
    }

    public static StubTenantModules AllEnabled() => new(TenantModuleSet.FromStored(TenantModuleKeys.All));

    public static StubTenantModules Without(TenantModuleKey key) =>
        new(TenantModuleSet.FromStored(TenantModuleKeys.All.Where(candidate => candidate != key)));

    public static StubTenantModules UnknownTenant() => new(null);
}
```

`tests/Modules/Quotations/Modules.Quotations.UnitTests/WhatsAppChannelResolverTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure;
using Modules.Quotations.Infrastructure.Whatsapp;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-10-07, «Reglas de resolución»: sin fila y <c>Shared</c> usan el sender global, <c>Own</c>
/// arma un Zenvia con las credenciales del tenant y <c>Disabled</c> no tiene sender. Una key que no
/// descifra es <c>settings_unreadable</c>, no un 500.
/// </summary>
public sealed class WhatsAppChannelResolverTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private const string TenantToken = "token-del-tenant";
    private const string TenantFrom = "573005556677";
    private const string TenantTemplate = "11111111-2222-3333-4444-555555555555";

    private static readonly WhatsAppQuotationMessage Message = new(
        ToPhone: "3001234567", FullName: "Juan Pérez", OrderNumber: "COT-1",
        Total: 1000m, ValidUntil: new DateOnly(2026, 10, 30), DocumentUrl: "https://assets/x.pdf");

    private sealed record Harness(
        WhatsAppChannelResolver Resolver,
        InMemoryTenantWhatsAppSettingsRepository Repository,
        FakeWhatsAppSecretProtector Protector,
        RecordingWhatsAppSender Shared,
        Capture Zenvia);

    private sealed class Capture
    {
        public string? Token { get; set; }

        public string Json { get; set; } = "{}";
    }

    private sealed class CapturingHandler(Capture capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture.Token = request.Headers.TryGetValues("X-API-TOKEN", out var values) ? values.First() : null;
            capture.Json = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"z1"}""") };
        }
    }

    private static Harness NewResolver()
    {
        var repository = new InMemoryTenantWhatsAppSettingsRepository();
        var protector = new FakeWhatsAppSecretProtector();
        var shared = new RecordingWhatsAppSender();
        var capture = new Capture();
        var resolver = new WhatsAppChannelResolver(
            repository,
            protector,
            shared,
            new ZenviaHttpClient(new HttpClient(new CapturingHandler(capture))),
            Options.Create(new QuotationsOptions()),
            NullLogger<ZenviaWhatsAppSender>.Instance);
        return new Harness(resolver, repository, protector, shared, capture);
    }

    private static TenantWhatsAppSettings Stored(WhatsAppMode mode, ProtectedSecret token)
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Now);
        settings.Configure(WhatsAppMode.Own, WhatsAppProvider.Zenvia, token, null, TenantFrom, TenantTemplate, Now);
        if (mode != WhatsAppMode.Own)
        {
            settings.Configure(mode, null, null, null, null, null, Now);
        }

        return settings;
    }

    [Fact]
    public async Task WithoutARowItIsTheSharedSender()
    {
        var harness = NewResolver();

        var channel = await harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Equal(WhatsAppMode.Shared, channel.Mode);
        Assert.Same(harness.Shared, channel.Sender);
    }

    // Las credenciales propias guardadas se ignoran en Shared.
    [Fact]
    public async Task SharedIsTheSharedSenderEvenWithAnOwnAccountStored()
    {
        var harness = NewResolver();
        harness.Repository.Add(Stored(WhatsAppMode.Shared, harness.Protector.Seed("k2", TenantToken)));

        var channel = await harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Equal(WhatsAppMode.Shared, channel.Mode);
        Assert.Same(harness.Shared, channel.Sender);
    }

    [Fact]
    public async Task OwnBuildsAZenviaSenderWithTheTenantCredentials()
    {
        var harness = NewResolver();
        harness.Repository.Add(Stored(WhatsAppMode.Own, harness.Protector.Seed("k1", TenantToken)));

        var channel = await harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken);
        Assert.Equal(WhatsAppMode.Own, channel.Mode);
        Assert.NotNull(channel.Sender);
        await channel.Sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken);

        Assert.Equal(TenantToken, harness.Zenvia.Token);
        var body = JsonDocument.Parse(harness.Zenvia.Json).RootElement;
        Assert.Equal(TenantFrom, body.GetProperty("from").GetString());
        Assert.Equal(TenantTemplate, body.GetProperty("contents")[0].GetProperty("templateId").GetString());
        Assert.Null(harness.Shared.Sent);
    }

    [Fact]
    public async Task DisabledHasNoSender()
    {
        var harness = NewResolver();
        harness.Repository.Add(Stored(WhatsAppMode.Disabled, harness.Protector.Seed("k2", TenantToken)));

        var channel = await harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Equal(WhatsAppMode.Disabled, channel.Mode);
        Assert.Null(channel.Sender);
    }

    [Fact]
    public async Task AnOwnKeyThatCannotBeDecryptedIsSettingsUnreadable()
    {
        var harness = NewResolver();
        harness.Repository.Add(Stored(WhatsAppMode.Own, FakeWhatsAppSecretProtector.Garbage("k1")));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.settings_unreadable", error.Code);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task AnOwnKeyStoredWithARetiredKeyIsSettingsUnreadable()
    {
        var harness = NewResolver();
        harness.Repository.Add(Stored(WhatsAppMode.Own, harness.Protector.Seed("k1", TenantToken)));
        harness.Protector.KnownKeys.Remove("k1");

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.settings_unreadable", error.Code);
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~WhatsAppChannelResolverTests"
```

Esperado: compilación fallida, `CS0246: The type or namespace name 'WhatsAppChannelResolver' could not be found` (y `IWhatsAppChannelResolver`/`WhatsAppChannel` si algo los nombra).

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppChannelResolver.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>El canal efectivo de un envío. <see cref="Sender"/> es nulo si y sólo si
/// <see cref="Mode"/> es <see cref="WhatsAppMode.Disabled"/>.</summary>
public sealed record WhatsAppChannel(WhatsAppMode Mode, IWhatsAppSender? Sender);

/// <summary>
/// Resuelve por dónde sale el WhatsApp de un tenant, al principio de cada envío (spec 2026-10-07,
/// «Reglas de resolución»): una lectura por PK, sin caché. Una caché obligaría a invalidar en el
/// PUT y entre réplicas, y el envío ya paga qcode-pdf, R2 y Zenvia.
/// </summary>
public interface IWhatsAppChannelResolver
{
    Task<WhatsAppChannel> ResolveAsync(Guid tenantId, CancellationToken cancellationToken);
}
```

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppChannelResolver.cs`:

```csharp
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>
/// Spec 2026-10-07: sin fila o <c>Shared</c> ⇒ el <see cref="IWhatsAppSender"/> global de hoy;
/// <c>Own</c> ⇒ un <see cref="ZenviaWhatsAppSender"/> armado por envío con la key descifrada, sobre
/// el <see cref="ZenviaHttpClient"/> compartido; <c>Disabled</c> ⇒ sin sender. <c>BaseUrl</c> sigue
/// siendo global. Scoped: lee por el DbContext del request.
/// </summary>
internal sealed class WhatsAppChannelResolver(
    ITenantWhatsAppSettingsRepository repository,
    IWhatsAppSecretProtector protector,
    IWhatsAppSender sharedSender,
    ZenviaHttpClient zenviaHttpClient,
    IOptions<QuotationsOptions> options,
    ILogger<ZenviaWhatsAppSender> senderLogger)
    : IWhatsAppChannelResolver
{
    public async Task<WhatsAppChannel> ResolveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var settings = await repository.FindReadOnlyAsync(tenantId, cancellationToken);
        if (settings is null || settings.Mode == WhatsAppMode.Shared)
        {
            return new WhatsAppChannel(WhatsAppMode.Shared, sharedSender);
        }

        if (settings.Mode == WhatsAppMode.Disabled)
        {
            return new WhatsAppChannel(WhatsAppMode.Disabled, null);
        }

        return new WhatsAppChannel(WhatsAppMode.Own, OwnSender(settings));
    }

    private ZenviaWhatsAppSender OwnSender(TenantWhatsAppSettings settings)
    {
        string token;
        try
        {
            // El CHECK own_complete garantiza la key en Own; el null se trata igual que ilegible.
            var secret = settings.ApiToken ?? throw new CryptographicException("No API key is stored.");
            token = protector.Unprotect(settings.TenantId, secret);
        }
        catch (Exception exception) when (exception is InvalidOperationException or CryptographicException)
        {
            // Código propio y no el genérico quotation.send.failed: es el único que la pantalla puede
            // convertir en "pide que la revisen en Configuración". El mensaje de la interna nombra
            // la clave de configuración, nunca un valor.
            throw new QuotationsDomainException(
                "quotation.whatsapp.settings_unreadable",
                "The tenant's WhatsApp API key cannot be decrypted with the configured keys.",
                exception);
        }

        return new ZenviaWhatsAppSender(
            zenviaHttpClient.Client,
            new ZenviaSenderSettings(
                token,
                settings.FromNumber!,
                settings.TemplateId!,
                options.Value.WhatsApp.BaseUrl,
                ZenviaAccount.Tenant),
            senderLogger);
    }
}
```

En `QuotationsInfrastructureExtensions.cs`, después del registro del repositorio de B3:

```csharp
        // Spec 2026-10-07: el canal de cada envío. Scoped porque lee la fila por el DbContext del
        // request; depende del IWhatsAppSender global (singleton) que registra AddWhatsAppSender.
        services.AddScoped<IWhatsAppChannelResolver, WhatsAppChannelResolver>();
```

y `using Modules.Quotations.Infrastructure.Whatsapp;` si no estaba (ya está, `:15`).

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~WhatsAppChannelResolverTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: seis `Superado`; arquitectura verde.

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppChannelResolver.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppChannelResolver.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/WhatsAppSettingsTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/WhatsAppChannelResolverTests.cs; git commit -m "feat(quotations): resolver del canal de WhatsApp por tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---
### Task B6: Etapa `Channel` y los textos del historial sin WhatsApp

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationSendStage.cs:29-34`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationChangeSummary.cs:50-82`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs:189` (mecánico)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationChangeSummaryTests.cs`

**Interfaces:**
- Produces: `QuotationSendStage.Channel` (último miembro); `QuotationChangeSummary.SentWithoutWhatsApp()`, `QuotationChangeSummary.ResentWithoutWhatsApp()`, `QuotationChangeSummary.SendFailed(QuotationSendStage stage, bool whatsAppSkipped)` (reemplaza a `SendFailed(QuotationSendStage)`). B10 los usa.

- [ ] **Step 1: Edición mecánica y pruebas nuevas que fallan**

En `QuotationChangeSummaryTests.cs`, las dos llamadas existentes (`:106` y `:118`) pasan a `QuotationChangeSummary.SendFailed(QuotationSendStage.Recipient, whatsAppSkipped: false)` y `QuotationChangeSummary.SendFailed(QuotationSendStage.Persistence, whatsAppSkipped: false)`; sus aserciones no cambian (criterio 1). Agregar después de la segunda:

```csharp
    // Spec 2026-10-07, «Cómo se hace explícito que no salió nada», punto 4: el evento sigue siendo
    // Sent/Resent, pero el resumen dice que no salió ningún WhatsApp.
    [Fact]
    public void SentWithoutWhatsAppSaysNoMessageLeft() =>
        Assert.Equal(
            "Marcada como enviada sin WhatsApp: el envío por WhatsApp está desactivado para la empresa.",
            QuotationChangeSummary.SentWithoutWhatsApp());

    [Fact]
    public void ResentWithoutWhatsAppSaysNoMessageLeft() =>
        Assert.Equal(
            "Marcada como reenviada sin WhatsApp: el envío por WhatsApp está desactivado para la empresa.",
            QuotationChangeSummary.ResentWithoutWhatsApp());

    [Fact]
    public void SendFailedByChannelSendsToTheSettings() =>
        Assert.Equal(
            "No se pudo enviar la cotización: no pudimos leer la configuración de WhatsApp de la " +
            "empresa. Pide a un administrador que la revise en Configuración.",
            QuotationChangeSummary.SendFailed(QuotationSendStage.Channel, whatsAppSkipped: false));

    // Con Disabled el "el mensaje salió" de Persistence es falso: se puede reintentar sin miedo.
    [Fact]
    public void SendFailedByPersistenceWithoutWhatsAppSaysItIsSafeToRetry() =>
        Assert.Equal(
            "No se pudo enviar la cotización: no pudimos registrar el envío. No se mandó ningún " +
            "WhatsApp, así que puedes reintentar.",
            QuotationChangeSummary.SendFailed(QuotationSendStage.Persistence, whatsAppSkipped: true));
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~QuotationChangeSummaryTests"
```

Esperado: compilación fallida, `CS1739: The best overload for 'SendFailed' does not have a parameter named 'whatsAppSkipped'` y `CS0117: 'QuotationSendStage' does not contain a definition for 'Channel'`.

- [ ] **Step 3: Implementar**

`QuotationSendStage.cs`, después de `Persistence` (al final: se guarda como texto, el orden no se persiste):

```csharp
    /// <summary>Guardar la cotización ya enviada. El mensaje salió: ver
    /// <c>QuotationSendFailure.Stage</c> para por qué este caso es el más incómodo.</summary>
    Persistence,

    /// <summary>Leer la configuración de WhatsApp del tenant (spec 2026-10-07). Falla cuando la
    /// API key guardada de una cuenta propia no se puede descifrar.</summary>
    Channel
```

`QuotationChangeSummary.cs`, después de `Resent()` (`:52`):

```csharp
    /// <summary>Spec 2026-10-07: con WhatsApp desactivado el evento sigue siendo Sent, pero quien
    /// lee el historial tiene que saber que al cliente no le llegó nada.</summary>
    public static string SentWithoutWhatsApp() =>
        "Marcada como enviada sin WhatsApp: el envío por WhatsApp está desactivado para la empresa.";

    public static string ResentWithoutWhatsApp() =>
        "Marcada como reenviada sin WhatsApp: el envío por WhatsApp está desactivado para la empresa.";
```

Reemplazar `SendFailed` y `StageReason` (`:62-82`):

```csharp
    public static string SendFailed(QuotationSendStage stage, bool whatsAppSkipped) =>
        Trim($"No se pudo enviar la cotización: {StageReason(stage, whatsAppSkipped)}");

    // En segunda persona y accionable donde se puede hacer algo, y neutro donde no: decirle
    // "revisa" a alguien por una caída de Zenvia lo manda a buscar un problema que no tiene.
    // whatsAppSkipped (spec 2026-10-07): con WhatsApp desactivado, "el mensaje salió" es falso.
    private static string StageReason(QuotationSendStage stage, bool whatsAppSkipped) => stage switch
    {
        QuotationSendStage.Advisor =>
            "no pudimos identificar a la asesora que la envía.",
        QuotationSendStage.Channel =>
            "no pudimos leer la configuración de WhatsApp de la empresa. Pide a un administrador que la revise en Configuración.",
        QuotationSendStage.Pdf =>
            "falló la generación del PDF.",
        QuotationSendStage.Publish =>
            "falló la publicación del PDF que el cliente tiene que poder descargar.",
        QuotationSendStage.Recipient =>
            "el cliente no tiene un número de WhatsApp válido. Revisa sus datos de contacto.",
        QuotationSendStage.WhatsApp =>
            "WhatsApp rechazó el mensaje.",
        QuotationSendStage.Persistence when whatsAppSkipped =>
            "no pudimos registrar el envío. No se mandó ningún WhatsApp, así que puedes reintentar.",
        QuotationSendStage.Persistence =>
            "el mensaje salió pero no pudimos registrar el envío. Revisa con el cliente antes de reintentar.",
        _ => "falló por un motivo no previsto."
    };
```

En `SendQuotation.cs:189`, mecánico (B10 lo conecta de verdad): `QuotationChangeSummary.SendFailed(stage, whatsAppSkipped: false),`.

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~QuotationChangeSummaryTests|FullyQualifiedName~SendQuotationHandlerTests"
```

Esperado: todas `Superado` (las de `SendQuotationHandlerTests` son la guarda de que el cambio mecánico no movió nada).

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Domain/QuotationSendStage.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationChangeSummary.cs src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationChangeSummaryTests.cs; git commit -m "feat(quotations): historial del envío sin WhatsApp y etapa Channel"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B7: Comando, validador y DTOs de la configuración

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppSettingsDto.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/UpdateWhatsAppSettings.cs` (comando y validador; el handler llega en B8)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateWhatsAppSettingsValidatorTests.cs`

**Interfaces:**
- Consumes: `WhatsAppMode`, `WhatsAppProvider`, `TenantWhatsAppSettings` (B1), `IWhatsAppSecretProtector` (B2).
- Produces:
  - `public sealed record WhatsAppSettingsDto(Guid TenantId, string Mode, string? Provider, bool ApiKeyConfigured, DateTimeOffset? ApiKeyUpdatedAt, bool? ApiKeyReadable, string? FromNumber, string? TemplateId, IReadOnlyList<string> Modes, IReadOnlyList<string> Providers, long Version)`
  - `public sealed record WhatsAppChannelDto(bool Enabled, string Mode)`
  - `internal static class WhatsAppModes { All; Providers; bool TryParse(string?, out WhatsAppMode); bool IsProvider(string?) }`
  - `internal static class WhatsAppSettingsMappings { WhatsAppSettingsDto ToDto(TenantWhatsAppSettings, IWhatsAppSecretProtector) }`
  - `public sealed record UpdateWhatsAppSettingsCommand(Guid TenantId, string Mode, string? Provider, string? ApiKey, string? FromNumber, string? TemplateId, long ExpectedVersion) : ICommand<WhatsAppSettingsDto>` con `ToString()` sin la key
  - `public sealed partial class UpdateWhatsAppSettingsValidator : AbstractValidator<UpdateWhatsAppSettingsCommand>`

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateWhatsAppSettingsValidatorTests.cs`:

```csharp
using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-10-07, «Validador»: sólo formato y sólo de lo que viene. Lo requerido en Own depende
/// de la fila y lo decide el handler (B8); si lo exigiera el validador, <c>{ "mode": "Own" }</c>
/// —volver a la cuenta propia ya guardada— sería siempre 422. En Shared y Disabled los campos de
/// la cuenta propia se ignoran. Ningún mensaje lleva el valor de la key.
/// </summary>
public sealed class UpdateWhatsAppSettingsValidatorTests
{
    private const string ValidTemplate = "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f";
    private static readonly UpdateWhatsAppSettingsValidator Validator = new();

    private static UpdateWhatsAppSettingsCommand Command(
        string mode = "Own",
        string? provider = null,
        string? apiKey = null,
        string? fromNumber = null,
        string? templateId = null,
        long expectedVersion = 1) =>
        new(Guid.CreateVersion7(), mode, provider, apiKey, fromNumber, templateId, expectedVersion);

    private static IReadOnlyList<string> FailedProperties(UpdateWhatsAppSettingsCommand command) =>
        [.. Validator.Validate(command).Errors.Select(error => error.PropertyName).Distinct()];

    [Theory]
    [InlineData("Shared")]
    [InlineData("Own")]
    [InlineData("Disabled")]
    public void EachModeByItsExactNameIsValid(string mode) =>
        Assert.Empty(FailedProperties(Command(mode)));

    // Review Focus 2: ordinal. Enum.TryParse con ignoreCase aceptaría "own".
    [Theory]
    [InlineData("own")]
    [InlineData("OWN")]
    [InlineData("")]
    [InlineData("Enabled")]
    [InlineData("1")]
    public void ModeIsOrdinalAndRequired(string mode) =>
        Assert.Equal(new[] { "Mode" }, FailedProperties(Command(mode)));

    // Volver a la cuenta propia guardada: el validador no exige nada (lo decide el handler).
    [Fact]
    public void OwnAloneIsValid() => Assert.Empty(FailedProperties(Command("Own")));

    [Fact]
    public void AFullValidOwnBodyIsValid() =>
        Assert.Empty(FailedProperties(Command(
            "Own", "Zenvia", "  abc-123_XYZ.!~  ", " 573001234567 ", ValidTemplate)));

    [Theory]
    [InlineData("zenvia")]
    [InlineData("Twilio")]
    [InlineData("")]
    public void TheProviderIsZenviaByItsExactName(string provider) =>
        Assert.Equal(new[] { "Provider" }, FailedProperties(Command("Own", provider: provider)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("con espacio")]
    [InlineData("tab\tdentro")]
    [InlineData("control\u0001")]
    [InlineData("contraseña")]
    public void TheApiKeyIsVisibleAsciiWithoutSpaces(string apiKey) =>
        Assert.Equal(new[] { "ApiKey" }, FailedProperties(Command("Own", apiKey: apiKey)));

    [Fact]
    public void TheApiKeyHasAtMost512Characters()
    {
        Assert.Empty(FailedProperties(Command("Own", apiKey: new string('a', 512))));
        Assert.Equal(new[] { "ApiKey" }, FailedProperties(Command("Own", apiKey: new string('a', 513))));
    }

    [Theory]
    [InlineData("+573001234567")]
    [InlineData("300123456")]
    [InlineData("5730012345678901")]
    [InlineData("57 3001234567")]
    [InlineData("57300123456a")]
    [InlineData("")]
    public void TheFromNumberIsTenToFifteenDigits(string fromNumber) =>
        Assert.Equal(new[] { "FromNumber" }, FailedProperties(Command("Own", fromNumber: fromNumber)));

    [Theory]
    [InlineData("plantilla")]
    [InlineData("{9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f}")]
    [InlineData("9b2f4c1e3d5a4e6b8c7d1a2b3c4d5e6f")]
    [InlineData("")]
    public void TheTemplateIdIsAGuidInFormatD(string templateId) =>
        Assert.Equal(new[] { "TemplateId" }, FailedProperties(Command("Own", templateId: templateId)));

    // Decisión 28: fuera de Own los campos propios ni se validan ni se guardan.
    [Theory]
    [InlineData("Shared")]
    [InlineData("Disabled")]
    public void OutsideOwnTheOwnAccountFieldsAreIgnored(string mode) =>
        Assert.Empty(FailedProperties(Command(
            mode, provider: "Twilio", apiKey: "con espacio", fromNumber: "+57", templateId: "x")));

    [Fact]
    public void TheExpectedVersionIsPositive() =>
        Assert.Equal(new[] { "ExpectedVersion" }, FailedProperties(Command("Shared", expectedVersion: 0)));

    [Fact]
    public void NoErrorMessageContainsTheRejectedKey()
    {
        const string rejected = "SENTINEL con espacio";

        var errors = Validator.Validate(Command("Own", apiKey: rejected)).Errors;

        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.DoesNotContain("SENTINEL", error.ErrorMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void TheCommandToStringDoesNotContainTheKey()
    {
        var command = Command("Own", "Zenvia", "key-SENTINEL", "573001234567", ValidTemplate);

        Assert.DoesNotContain("key-SENTINEL", command.ToString(), StringComparison.Ordinal);
        Assert.Contains("***", command.ToString(), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~UpdateWhatsAppSettingsValidatorTests"
```

Esperado: compilación fallida, `CS0246: The type or namespace name 'UpdateWhatsAppSettingsCommand' could not be found`.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Application/WhatsAppSettingsDto.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// La configuración de WhatsApp tal como la dibuja Configuración (spec 2026-10-07). Regla BFF:
/// <list type="bullet">
/// <item><c>ApiKeyConfigured</c> y <c>ApiKeyUpdatedAt</c>: el estado «configurada el 7 de
/// octubre» sin exponer la key (ni sus últimos caracteres, decisión 2).</item>
/// <item><c>ApiKeyReadable</c>: la key guardada se descifra de verdad (valor descartado en el
/// acto); <c>null</c> sin key. Detecta llave retirada <b>y</b> bytes que no descifran, para que la
/// pantalla pida volver a pegarla sin deducirlo de un envío fallido.</item>
/// <item><c>Modes</c> y <c>Providers</c>: colecciones fijas y completas; el select no conoce el
/// enum del backend.</item>
/// </list>
/// En Shared y Disabled viajan igual el proveedor, el número, la plantilla y el estado de la key:
/// son lo que la pantalla muestra al volver a «Cuenta propia».
/// </summary>
public sealed record WhatsAppSettingsDto(
    Guid TenantId,
    string Mode,
    string? Provider,
    bool ApiKeyConfigured,
    DateTimeOffset? ApiKeyUpdatedAt,
    bool? ApiKeyReadable,
    string? FromNumber,
    string? TemplateId,
    IReadOnlyList<string> Modes,
    IReadOnlyList<string> Providers,
    long Version);

/// <summary>
/// Lo que el flujo de envío necesita saber antes de enviar (spec 2026-10-07). Aparte de
/// <see cref="WhatsAppSettingsDto"/> porque los roles son editables: quien puede enviar no
/// necesariamente puede leer Configuración. <c>Mode</c> viaja para que la pantalla sume la pista
/// «revisa la plantilla en Configuración» sólo cuando la cuenta es la propia.
/// </summary>
public sealed record WhatsAppChannelDto(bool Enabled, string Mode);

/// <summary>Los nombres exactos, como viajan: ordinal, sin minúsculas ni el número del miembro,
/// que es lo que <c>Enum.TryParse</c> sí aceptaría.</summary>
internal static class WhatsAppModes
{
    public static readonly IReadOnlyList<string> All =
        [nameof(WhatsAppMode.Shared), nameof(WhatsAppMode.Own), nameof(WhatsAppMode.Disabled)];

    public static readonly IReadOnlyList<string> Providers = [nameof(WhatsAppProvider.Zenvia)];

    public static bool TryParse(string? value, out WhatsAppMode mode)
    {
        foreach (var candidate in Enum.GetValues<WhatsAppMode>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.Ordinal))
            {
                mode = candidate;
                return true;
            }
        }

        mode = default;
        return false;
    }

    public static bool IsProvider(string? value) =>
        string.Equals(value, nameof(WhatsAppProvider.Zenvia), StringComparison.Ordinal);
}

internal static class WhatsAppSettingsMappings
{
    public static WhatsAppSettingsDto ToDto(
        TenantWhatsAppSettings settings, IWhatsAppSecretProtector protector) =>
        new(
            settings.TenantId,
            settings.Mode.ToString(),
            settings.Provider?.ToString(),
            ApiKeyConfigured: settings.ApiToken is not null,
            settings.ApiTokenUpdatedAt,
            ApiKeyReadable: settings.ApiToken is null
                ? null
                // El valor descifrado se descarta en el acto: sólo sale el booleano.
                : protector.TryUnprotect(settings.TenantId, settings.ApiToken, out _),
            settings.FromNumber,
            settings.TemplateId,
            WhatsAppModes.All,
            WhatsAppModes.Providers,
            settings.Version);
}
```

`src/Modules/Quotations/Modules.Quotations.Application/UpdateWhatsAppSettings.cs` (primera parte; B8 agrega el handler al final del mismo archivo):

```csharp
using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El PUT de la configuración de WhatsApp (spec 2026-10-07). <c>Mode</c> como texto: es el
/// validador el que dice si es uno de los tres. Los campos de la cuenta propia son opcionales:
/// nulos conservan lo guardado, y fuera de <c>Own</c> se ignoran.
///
/// <see cref="ToString"/> esconde la key: el de un record imprime todas sus propiedades, y basta
/// un <c>{Command}</c> en un log o un depurador que lo imprima para filtrarla.
/// </summary>
public sealed record UpdateWhatsAppSettingsCommand(
    Guid TenantId,
    string Mode,
    string? Provider,
    string? ApiKey,
    string? FromNumber,
    string? TemplateId,
    long ExpectedVersion) : ICommand<WhatsAppSettingsDto>
{
    public override string ToString() =>
        $"UpdateWhatsAppSettingsCommand {{ TenantId = {TenantId}, Mode = {Mode}, Provider = {Provider}, "
        + $"ApiKey = {(ApiKey is null ? "null" : "***")}, FromNumber = {FromNumber}, "
        + $"TemplateId = {TemplateId}, ExpectedVersion = {ExpectedVersion} }}";
}

/// <summary>
/// Sólo <b>formato</b> y sólo de lo que viene (spec 2026-10-07, «Validador»): el validador no ve la
/// fila, así que lo requerido en Own lo decide el handler. Los mensajes de los cuatro campos que la
/// pantalla marca van en español; los de <c>Mode</c> y <c>ExpectedVersion</c>, inalcanzables desde
/// la pantalla, en inglés como el resto del módulo. Ninguno usa <c>{PropertyValue}</c>: el mensaje
/// termina en <c>ValidationException.Message</c>, que llega a ProblemDetails, al log y a
/// <c>platform.request_failures</c>.
/// </summary>
public sealed partial class UpdateWhatsAppSettingsValidator : AbstractValidator<UpdateWhatsAppSettingsCommand>
{
    public const string ProviderInvalidMessage = "Elige un proveedor válido: hoy sólo Zenvia.";

    public const string ApiKeyInvalidMessage =
        "La API key sólo puede tener letras, números y símbolos, sin espacios: vuelve a copiarla de Zenvia.";

    public const string FromNumberInvalidMessage =
        "Escribe el número con indicativo de país, sólo dígitos y sin '+' (entre 10 y 15).";

    public const string TemplateIdInvalidMessage =
        "El id de la plantilla tiene la forma 9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f.";

    [GeneratedRegex(@"^[\x21-\x7E]{1,512}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex("^[0-9]{10,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex FromNumberPattern();

    public UpdateWhatsAppSettingsValidator()
    {
        RuleFor(command => command.Mode)
            .Must(mode => WhatsAppModes.TryParse(mode, out _))
            .WithMessage("'Mode' must be 'Shared', 'Own' or 'Disabled'.");
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);

        When(command => string.Equals(command.Mode, nameof(WhatsAppMode.Own), StringComparison.Ordinal), () =>
        {
            RuleFor(command => command.Provider)
                .Must(WhatsAppModes.IsProvider)
                .When(command => command.Provider is not null)
                .WithMessage(ProviderInvalidMessage);
            RuleFor(command => command.ApiKey)
                .Must(apiKey => ApiKeyPattern().IsMatch(apiKey!.Trim()))
                .When(command => command.ApiKey is not null)
                .WithMessage(ApiKeyInvalidMessage);
            RuleFor(command => command.FromNumber)
                .Must(fromNumber => FromNumberPattern().IsMatch(fromNumber!.Trim()))
                .When(command => command.FromNumber is not null)
                .WithMessage(FromNumberInvalidMessage);
            RuleFor(command => command.TemplateId)
                .Must(templateId => Guid.TryParseExact(templateId!.Trim(), "D", out _))
                .When(command => command.TemplateId is not null)
                .WithMessage(TemplateIdInvalidMessage);
        });
    }
}
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~UpdateWhatsAppSettingsValidatorTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: todas `Superado`. Si `ModeIsOrdinalAndRequired("")` reporta además `ExpectedVersion`, revisa el helper `Command` (versión 1 por defecto).

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/WhatsAppSettingsDto.cs src/Modules/Quotations/Modules.Quotations.Application/UpdateWhatsAppSettings.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateWhatsAppSettingsValidatorTests.cs; git commit -m "feat(quotations): validador y DTOs de la configuración de WhatsApp"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---
### Task B8: `UpdateWhatsAppSettingsHandler`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/UpdateWhatsAppSettings.cs` (agregar el handler al final)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateWhatsAppSettingsHandlerTests.cs`

**Interfaces:**
- Consumes: B1 (`TenantWhatsAppSettings`), B2 (`IWhatsAppSecretProtector`), B3 (`ITenantWhatsAppSettingsRepository`), B7 (comando, validador, `WhatsAppModes`, `WhatsAppSettingsMappings`), `TenantModuleGuard`/`ITenantModules`/`TenantModuleKeys` (entitlements), `IQuotationAuditPublisher`, `IQuotationsUnitOfWork`, `QuotationsAuthorization`, `TenancyPermissions.SettingsUpdate`. Dobles: `InMemoryTenantWhatsAppSettingsRepository`, `FakeWhatsAppSecretProtector`, `StubTenantModules` (B5), `RecordingExportAuditPublisher`, `CountingQuotationsUnitOfWork`, `StubExecutionContext`, `PermissionlessExecutionContext`, `FixedClock` (existentes).
- Produces: `UpdateWhatsAppSettingsHandler` con las constantes `ModeChangedAction`, `ApiKeyReplacedAction`, `UpdatedAction` y los cinco mensajes de faltantes (`ProviderRequiredMessage`, `ApiKeyRequiredMessage`, `ApiKeyUnreadableMessage`, `FromNumberRequiredMessage`, `TemplateIdRequiredMessage`).

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateWhatsAppSettingsHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El PUT de la configuración de WhatsApp (spec 2026-10-07, «Casos de uso»), en su orden: autoriza
/// antes de validar, gate de capacidad, formato, versión, lo requerido en Own según la fila, campos
/// propios ignorados fuera de Own, drenaje de rotación que nunca convierte un guardado en 500, un
/// Configure por guardado y una auditoría por clase de cambio.
/// </summary>
public sealed class UpdateWhatsAppSettingsHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Earlier = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 22, 0, 0, TimeSpan.Zero);
    private const string Key = "zenvia-token-123";
    private const string FromNumber = "573001234567";
    private const string TemplateId = "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f";

    private sealed record Harness(
        UpdateWhatsAppSettingsHandler Handler,
        InMemoryTenantWhatsAppSettingsRepository Repository,
        FakeWhatsAppSecretProtector Protector,
        RecordingExportAuditPublisher Audit,
        CountingQuotationsUnitOfWork UnitOfWork,
        StubTenantModules Modules);

    private static Harness NewHandler(
        IExecutionContext? executionContext = null, StubTenantModules? modules = null)
    {
        var repository = new InMemoryTenantWhatsAppSettingsRepository();
        var protector = new FakeWhatsAppSecretProtector();
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        modules ??= StubTenantModules.AllEnabled();
        var handler = new UpdateWhatsAppSettingsHandler(
            repository,
            unitOfWork,
            audit,
            protector,
            modules,
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now),
            new UpdateWhatsAppSettingsValidator());
        return new Harness(handler, repository, protector, audit, unitOfWork, modules);
    }

    private static UpdateWhatsAppSettingsCommand Command(
        string mode,
        long expectedVersion = 1,
        string? provider = null,
        string? apiKey = null,
        string? fromNumber = null,
        string? templateId = null) =>
        new(TenantId, mode, provider, apiKey, fromNumber, templateId, expectedVersion);

    private static UpdateWhatsAppSettingsCommand FullOwn(long expectedVersion = 1, string apiKey = Key) =>
        Command("Own", expectedVersion, "Zenvia", apiKey, FromNumber, TemplateId);

    /// <summary>Una fila Own guardada antes de la prueba, en versión 2.</summary>
    private static TenantWhatsAppSettings OwnRow(ProtectedSecret token)
    {
        var row = TenantWhatsAppSettings.CreateEmpty(TenantId, Earlier);
        row.Configure(WhatsAppMode.Own, WhatsAppProvider.Zenvia, token, null, FromNumber, TemplateId, Earlier);
        return row;
    }

    private static IReadOnlyList<string> Actions(Harness harness) =>
        [.. harness.Audit.Entries.Select(entry => entry.Action)];

    [Fact]
    public async Task WithoutPermissionAndAnInvalidBodyIsForbiddenNotAValidationFailure()
    {
        var harness = NewHandler(new PermissionlessExecutionContext(SubjectId, TenantId));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            harness.Handler.HandleAsync(Command("own", expectedVersion: 0), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(0, harness.Modules.Calls);
        Assert.Equal(0, harness.Repository.FindCalls);
    }

    [Fact]
    public async Task ForAnotherTenantIsForbidden()
    {
        var harness = NewHandler(new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            harness.Handler.HandleAsync(Command("Disabled"), TestContext.Current.CancellationToken));

        Assert.Equal(0, harness.Repository.FindCalls);
    }

    [Fact]
    public async Task WithoutTheQuotationsModuleIsModuleNotEnabledBeforeReadingOrValidating()
    {
        var harness = NewHandler(modules: StubTenantModules.Without(TenantModuleKeys.Quotations));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            harness.Handler.HandleAsync(Command("own", expectedVersion: 0), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal(0, harness.Repository.FindCalls);
    }

    // El tenant del stub de desarrollo no existe en tenancy.tenants: el guard no lanza.
    [Fact]
    public async Task AnUnknownTenantPassesTheGate()
    {
        var harness = NewHandler(modules: StubTenantModules.UnknownTenant());

        var dto = await harness.Handler.HandleAsync(Command("Disabled"), TestContext.Current.CancellationToken);

        Assert.Equal("Disabled", dto.Mode);
    }

    [Fact]
    public async Task OwnWithoutARowAndAnEmptyBodyListsTheFourMissingFieldsAtOnce()
    {
        var harness = NewHandler();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Handler.HandleAsync(Command("Own"), TestContext.Current.CancellationToken));

        Assert.Equal(
            new[] {"ApiKey", "FromNumber", "Provider", "TemplateId"},
            error.Errors.Select(failure => failure.PropertyName).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(error.Errors, failure =>
            failure.PropertyName == "ApiKey" && failure.ErrorMessage == UpdateWhatsAppSettingsHandler.ApiKeyRequiredMessage);
        Assert.Empty(harness.Repository.Rows);
    }

    [Fact]
    public async Task BackToOwnWithEverythingStoredAndReadableNeedsOnlyTheMode()
    {
        var harness = NewHandler();
        var row = OwnRow(harness.Protector.Seed("k2", Key));
        row.Configure(WhatsAppMode.Disabled, null, null, null, null, null, Earlier);
        harness.Repository.Add(row);

        var dto = await harness.Handler.HandleAsync(Command("Own", expectedVersion: 3), TestContext.Current.CancellationToken);

        Assert.Equal("Own", dto.Mode);
        Assert.Equal(4, dto.Version);
        Assert.True(dto.ApiKeyReadable);
        Assert.Equal(new[] {UpdateWhatsAppSettingsHandler.ModeChangedAction}, Actions(harness));
    }

    [Fact]
    public async Task OwnWithAnUnreadableKeyAndNoNewKeyAsksToPasteItAgain()
    {
        var harness = NewHandler();
        var row = OwnRow(FakeWhatsAppSecretProtector.Garbage("k2"));
        row.Configure(WhatsAppMode.Shared, null, null, null, null, null, Earlier);
        harness.Repository.Add(row);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Handler.HandleAsync(Command("Own", expectedVersion: 3), TestContext.Current.CancellationToken));

        var failure = Assert.Single(error.Errors);
        Assert.Equal("ApiKey", failure.PropertyName);
        Assert.Equal(UpdateWhatsAppSettingsHandler.ApiKeyUnreadableMessage, failure.ErrorMessage);
    }

    [Fact]
    public async Task DisabledWithOwnFieldsInTheBodyDoesNotStoreThem()
    {
        var harness = NewHandler();

        await harness.Handler.HandleAsync(
            Command("Disabled", 1, "Zenvia", Key, FromNumber, TemplateId), TestContext.Current.CancellationToken);

        var row = Assert.Single(harness.Repository.Rows);
        Assert.Equal(WhatsAppMode.Disabled, row.Mode);
        Assert.Null(row.Provider);
        Assert.Null(row.ApiToken);
        Assert.Null(row.FromNumber);
        Assert.Null(row.TemplateId);
        Assert.Empty(harness.Protector.ProtectedPlaintexts);
    }

    [Fact]
    public async Task AFirstOwnSaveCreatesTheRowAtVersionTwoAndAuditsEachClassOfChange()
    {
        var harness = NewHandler();

        var dto = await harness.Handler.HandleAsync(FullOwn(), TestContext.Current.CancellationToken);

        Assert.Equal(2, dto.Version);
        Assert.True(dto.ApiKeyConfigured);
        Assert.True(dto.ApiKeyReadable);
        Assert.Equal(Now, dto.ApiKeyUpdatedAt);
        Assert.Equal(1, harness.UnitOfWork.Saves);
        Assert.Equal(
            new[] {
                UpdateWhatsAppSettingsHandler.ModeChangedAction,
                UpdateWhatsAppSettingsHandler.ApiKeyReplacedAction,
                UpdateWhatsAppSettingsHandler.UpdatedAction,
            },
            Actions(harness));
        Assert.All(harness.Audit.Entries, entry =>
        {
            Assert.Equal(TenantId, entry.TenantId);
            Assert.Equal(SubjectId, entry.ActorId);
            Assert.Equal(TenantId.ToString(), entry.ResourceId);
            Assert.Equal("success", entry.Outcome);
        });
    }

    // Review Focus 1: lo que se pega de la consola de Zenvia trae casi siempre un salto de línea.
    [Fact]
    public async Task AnApiKeyWithSurroundingWhitespaceIsProtectedTrimmed()
    {
        var harness = NewHandler();

        await harness.Handler.HandleAsync(FullOwn(apiKey: "  zenvia-token-123\r\n"), TestContext.Current.CancellationToken);

        Assert.Equal(new[] {Key}, harness.Protector.ProtectedPlaintexts);
    }

    [Fact]
    public async Task WithoutApiKeyTheStoredSecretIsKept()
    {
        var harness = NewHandler();
        var token = harness.Protector.Seed("k2", Key);
        harness.Repository.Add(OwnRow(token));

        await harness.Handler.HandleAsync(
            Command("Own", 2, templateId: "11111111-2222-3333-4444-555555555555"),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(harness.Repository.Rows);
        Assert.Same(token, row.ApiToken);
        Assert.Same(token.Ciphertext, row.ApiToken!.Ciphertext);
        Assert.Equal(new[] {UpdateWhatsAppSettingsHandler.UpdatedAction}, Actions(harness));
    }

    [Fact]
    public async Task ReplacingTheKeyAloneAuditsOnlyTheReplacement()
    {
        var harness = NewHandler();
        harness.Repository.Add(OwnRow(harness.Protector.Seed("k2", Key)));

        await harness.Handler.HandleAsync(Command("Own", 2, apiKey: Key), TestContext.Current.CancellationToken);

        Assert.Equal(new[] {UpdateWhatsAppSettingsHandler.ApiKeyReplacedAction}, Actions(harness));
    }

    [Fact]
    public async Task AKeyInANonActiveKeyIsReencryptedWithTheActiveOne()
    {
        var harness = NewHandler();
        var row = OwnRow(harness.Protector.Seed("k1", Key));
        harness.Repository.Add(row);
        var updatedAt = row.ApiTokenUpdatedAt;

        await harness.Handler.HandleAsync(Command("Own", 2), TestContext.Current.CancellationToken);

        Assert.Equal("k2", row.ApiToken!.KeyId);
        Assert.Equal(new[] {Key}, harness.Protector.ProtectedPlaintexts);
        Assert.Equal(updatedAt, row.ApiTokenUpdatedAt);
        Assert.Equal(new[] {UpdateWhatsAppSettingsHandler.UpdatedAction}, Actions(harness));
    }

    // Decisión 25: el drenaje nunca convierte un guardado en un 500.
    [Fact]
    public async Task DisabledOverANonActiveKeyThatCannotBeDecryptedSavesTheModeWithoutReencrypting()
    {
        var harness = NewHandler();
        var garbage = FakeWhatsAppSecretProtector.Garbage("k1");
        var row = OwnRow(garbage);
        harness.Repository.Add(row);

        var dto = await harness.Handler.HandleAsync(Command("Disabled", 2), TestContext.Current.CancellationToken);

        Assert.Equal(WhatsAppMode.Disabled, row.Mode);
        Assert.Same(garbage, row.ApiToken);
        Assert.Empty(harness.Protector.ProtectedPlaintexts);
        Assert.False(dto.ApiKeyReadable);
        Assert.Equal(1, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AModeChangeAndADrainBumpTheVersionOnceAndPublishTwoActions()
    {
        var harness = NewHandler();
        harness.Repository.Add(OwnRow(harness.Protector.Seed("k1", Key)));

        var dto = await harness.Handler.HandleAsync(Command("Disabled", 2), TestContext.Current.CancellationToken);

        Assert.Equal(3, dto.Version);
        Assert.Equal(
            new[] {UpdateWhatsAppSettingsHandler.ModeChangedAction, UpdateWhatsAppSettingsHandler.UpdatedAction},
            Actions(harness));
    }

    [Fact]
    public async Task ANoOpSavesNothingAndAuditsNothing()
    {
        var harness = NewHandler();
        harness.Repository.Add(OwnRow(harness.Protector.Seed("k2", Key)));

        var dto = await harness.Handler.HandleAsync(
            Command("Own", 2, "Zenvia", null, FromNumber, TemplateId), TestContext.Current.CancellationToken);

        Assert.Equal(2, dto.Version);
        Assert.Equal(0, harness.UnitOfWork.Saves);
        Assert.Empty(harness.Audit.Entries);
    }

    [Fact]
    public async Task SharedWithoutARowIsANoOpThatAddsNoRow()
    {
        var harness = NewHandler();

        var dto = await harness.Handler.HandleAsync(Command("Shared"), TestContext.Current.CancellationToken);

        Assert.Equal("Shared", dto.Mode);
        Assert.Equal(1, dto.Version);
        Assert.Empty(harness.Repository.Rows);
        Assert.Equal(0, harness.UnitOfWork.Saves);
        Assert.Empty(harness.Audit.Entries);
    }

    [Fact]
    public async Task WithoutARowAnyVersionOtherThanOneIsAConflict()
    {
        var harness = NewHandler();

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            harness.Handler.HandleAsync(Command("Disabled", 2), TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
    }

    [Fact]
    public async Task AStaleVersionIsAConflict()
    {
        var harness = NewHandler();
        harness.Repository.Add(OwnRow(harness.Protector.Seed("k2", Key)));

        await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            harness.Handler.HandleAsync(Command("Disabled", 1), TestContext.Current.CancellationToken));

        Assert.Equal(0, harness.UnitOfWork.Saves);
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~UpdateWhatsAppSettingsHandlerTests"
```

Esperado: compilación fallida, `CS0246: The type or namespace name 'UpdateWhatsAppSettingsHandler' could not be found`.

- [ ] **Step 3: Implementar**

Al principio de `UpdateWhatsAppSettings.cs`, sumar `using FluentValidation.Results;`, `using Modules.Tenancy.Application;` y `using Modules.Tenancy.Domain;`. Al final del archivo:

```csharp
/// <summary>
/// Guarda la configuración de WhatsApp (spec 2026-10-07, «Casos de uso»). Mismo esqueleto que
/// <see cref="UpdateOrdersExportLayoutHandler"/>, en este orden:
/// <list type="number">
/// <item>autoriza antes de validar (hallazgo B1 del spec del layout: un 422 a quien no tiene permiso
/// confirma que el cuerpo se leyó);</item>
/// <item>gate de capacidad: <c>tenancy.*</c> es núcleo y el enmascaramiento no apaga este endpoint
/// aunque el tenant no tenga cotizaciones;</item>
/// <item>formato (validador);</item>
/// <item>fila o <see cref="TenantWhatsAppSettings.CreateEmpty"/>, y versión (412);</item>
/// <item>en Own, lo requerido según la fila, todo junto en una sola ValidationException;</item>
/// <item>fuera de Own, los campos propios del cuerpo se ignoran;</item>
/// <item>drenaje de rotación, que se salta si la key no descifra (decisión 25);</item>
/// <item>un solo Configure: una versión por guardado;</item>
/// <item>si cambió algo: Add si no había fila, una auditoría por clase de cambio, un guardado.</item>
/// </list>
/// </summary>
public sealed class UpdateWhatsAppSettingsHandler(
    ITenantWhatsAppSettingsRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IWhatsAppSecretProtector protector,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateWhatsAppSettingsCommand> validator)
    : ICommandHandler<UpdateWhatsAppSettingsCommand, WhatsAppSettingsDto>
{
    public const string ModeChangedAction = "quotations.whatsapp_settings.mode_changed";
    public const string ApiKeyReplacedAction = "quotations.whatsapp_settings.api_key_replaced";
    public const string UpdatedAction = "quotations.whatsapp_settings.updated";

    public const string ProviderRequiredMessage = "Elige el proveedor de tu cuenta de WhatsApp.";
    public const string ApiKeyRequiredMessage = "Pega la API key de tu cuenta de Zenvia.";
    public const string ApiKeyUnreadableMessage = "La API key guardada ya no se puede leer: vuelve a pegarla.";
    public const string FromNumberRequiredMessage = "Escribe el número emisor de tu cuenta de Zenvia.";
    public const string TemplateIdRequiredMessage = "Escribe el id de la plantilla aprobada.";

    public async Task<WhatsAppSettingsDto> HandleAsync(
        UpdateWhatsAppSettingsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, TenantModuleKeys.Quotations, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var now = clock.UtcNow;
        var stored = await repository.FindAsync(command.TenantId, cancellationToken);
        var settings = stored ?? TenantWhatsAppSettings.CreateEmpty(command.TenantId, now);
        if (settings.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The WhatsApp settings changed after they were loaded.");
        }

        if (!WhatsAppModes.TryParse(command.Mode, out var mode))
        {
            // El validador ya lo rechazó; llegar acá es un error de programación.
            throw new InvalidOperationException("The WhatsApp mode was not validated.");
        }

        // Decisión 28: fuera de Own los campos de la cuenta propia ni se validan ni se guardan.
        var own = mode == WhatsAppMode.Own;
        WhatsAppProvider? provider = own && command.Provider is not null ? WhatsAppProvider.Zenvia : null;
        var apiKey = own ? command.ApiKey?.Trim() : null;
        var fromNumber = own ? command.FromNumber?.Trim() : null;
        var templateId = own ? command.TemplateId?.Trim() : null;

        if (own)
        {
            EnsureOwnIsComplete(settings, provider, apiKey, fromNumber, templateId);
        }

        var rekeyed = apiKey is null ? Drain(settings) : null;
        var newToken = apiKey is null ? null : protector.Protect(command.TenantId, apiKey);
        var changes = settings.Configure(mode, provider, newToken, rekeyed, fromNumber, templateId, now);
        if (!changes.Any)
        {
            return WhatsAppSettingsMappings.ToDto(settings, protector);
        }

        if (stored is null)
        {
            repository.Add(settings);
        }

        Audit(command.TenantId, changes, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return WhatsAppSettingsMappings.ToDto(settings, protector);
    }

    // Lo requerido en Own depende de la fila: por eso no es del validador (decisión 29). Un error
    // por campo, todos juntos, en el mismo mapa errors que el validador.
    private void EnsureOwnIsComplete(
        TenantWhatsAppSettings settings,
        WhatsAppProvider? provider,
        string? apiKey,
        string? fromNumber,
        string? templateId)
    {
        var failures = new List<ValidationFailure>();
        if (provider is null && settings.Provider is null)
        {
            failures.Add(new ValidationFailure(nameof(UpdateWhatsAppSettingsCommand.Provider), ProviderRequiredMessage));
        }

        if (apiKey is null)
        {
            if (settings.ApiToken is null)
            {
                failures.Add(new ValidationFailure(nameof(UpdateWhatsAppSettingsCommand.ApiKey), ApiKeyRequiredMessage));
            }
            else if (!protector.TryUnprotect(settings.TenantId, settings.ApiToken, out _))
            {
                failures.Add(new ValidationFailure(nameof(UpdateWhatsAppSettingsCommand.ApiKey), ApiKeyUnreadableMessage));
            }
        }

        if (fromNumber is null && settings.FromNumber is null)
        {
            failures.Add(new ValidationFailure(nameof(UpdateWhatsAppSettingsCommand.FromNumber), FromNumberRequiredMessage));
        }

        if (templateId is null && settings.TemplateId is null)
        {
            failures.Add(new ValidationFailure(nameof(UpdateWhatsAppSettingsCommand.TemplateId), TemplateIdRequiredMessage));
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }

    // Red adicional de la rotación (spec, «Rotación», punto 4): la key guardada en una llave que no
    // es la activa se re-cifra en cualquier PUT que no traiga apiKey. Si no descifra, se salta.
    private ProtectedSecret? Drain(TenantWhatsAppSettings settings)
    {
        var active = protector.ActiveKeyId;
        if (settings.ApiToken is not { } current || active is null ||
            string.Equals(current.KeyId, active, StringComparison.Ordinal))
        {
            return null;
        }

        return protector.TryUnprotect(settings.TenantId, current, out var plaintext)
            ? protector.Protect(settings.TenantId, plaintext!)
            : null;
    }

    // El publicador no lleva campos: la acción dice la clase de cambio. Ni el modo de destino, ni
    // el número, ni la plantilla, ni la key.
    private void Audit(Guid tenantId, WhatsAppSettingsChanges changes, DateTimeOffset now)
    {
        if (changes.ModeChanged)
        {
            Publish(tenantId, ModeChangedAction, now);
        }

        if (changes.ApiKeyReplaced)
        {
            Publish(tenantId, ApiKeyReplacedAction, now);
        }

        if (changes.DetailsChanged || changes.KeyRotated)
        {
            Publish(tenantId, UpdatedAction, now);
        }
    }

    private void Publish(Guid tenantId, string action, DateTimeOffset now) =>
        auditPublisher.Publish(
            tenantId, executionContext.SubjectId, action, tenantId.ToString(), "success", now);
}
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~UpdateWhatsAppSettingsHandlerTests|FullyQualifiedName~UpdateWhatsAppSettingsValidatorTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: todas `Superado`. `QuotationsLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules` sigue verde: `Modules.Tenancy.Domain` empieza con `Modules.Tenancy`.

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/UpdateWhatsAppSettings.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateWhatsAppSettingsHandlerTests.cs; git commit -m "feat(quotations): guardar la configuración de WhatsApp con drenaje de rotación"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B9: Leer la configuración y el canal

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/GetWhatsAppSettings.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/GetWhatsAppChannel.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/GetWhatsAppSettingsHandlerTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/GetWhatsAppChannelHandlerTests.cs`

**Interfaces:**
- Consumes: B3, B7 (`WhatsAppSettingsMappings`, DTOs), dobles de B5.
- Produces: `GetWhatsAppSettingsQuery(Guid TenantId) : IQuery<WhatsAppSettingsDto>` + `GetWhatsAppSettingsHandler(ITenantWhatsAppSettingsRepository, IWhatsAppSecretProtector, ITenantModules, IExecutionContext, IClock)`; `GetWhatsAppChannelQuery(Guid TenantId) : IQuery<WhatsAppChannelDto>` + `GetWhatsAppChannelHandler(ITenantWhatsAppSettingsRepository, IExecutionContext)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Quotations/Modules.Quotations.UnitTests/GetWhatsAppSettingsHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-10-07: el GET nunca crea fila, dice si la key se puede leer descifrándola de
/// verdad (TryUnprotect, valor descartado) y no expone el token ni por nombre de propiedad.</summary>
public sealed class GetWhatsAppSettingsHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static (GetWhatsAppSettingsHandler Handler, InMemoryTenantWhatsAppSettingsRepository Repository, FakeWhatsAppSecretProtector Protector)
        NewHandler(IExecutionContext? executionContext = null, StubTenantModules? modules = null)
    {
        var repository = new InMemoryTenantWhatsAppSettingsRepository();
        var protector = new FakeWhatsAppSecretProtector();
        var handler = new GetWhatsAppSettingsHandler(
            repository,
            protector,
            modules ?? StubTenantModules.AllEnabled(),
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));
        return (handler, repository, protector);
    }

    private static TenantWhatsAppSettings OwnRow(ProtectedSecret token, WhatsAppMode finalMode = WhatsAppMode.Own)
    {
        var row = TenantWhatsAppSettings.CreateEmpty(TenantId, Now);
        row.Configure(WhatsAppMode.Own, WhatsAppProvider.Zenvia, token, null, "573001234567",
            "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f", Now);
        if (finalMode != WhatsAppMode.Own)
        {
            row.Configure(finalMode, null, null, null, null, null, Now);
        }

        return row;
    }

    [Fact]
    public async Task WithoutARowItIsSharedAtVersionOneWithTheFullCollections()
    {
        var (handler, repository, _) = NewHandler();

        var dto = await handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(
            new WhatsAppSettingsDto(TenantId, "Shared", null, false, null, null, null, null,
                dto.Modes, dto.Providers, 1),
            dto);
        Assert.Equal(new[] {"Shared", "Own", "Disabled"}, dto.Modes);
        Assert.Equal(new[] {"Zenvia"}, dto.Providers);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task AReadableKeyIsReportedReadable()
    {
        var (handler, repository, protector) = NewHandler();
        repository.Add(OwnRow(protector.Seed("k2", "token")));

        var dto = await handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.True(dto.ApiKeyConfigured);
        Assert.True(dto.ApiKeyReadable);
        Assert.Equal(Now, dto.ApiKeyUpdatedAt);
    }

    [Fact]
    public async Task AKeyStoredWithAKeyIdThatIsNotConfiguredIsNotReadable()
    {
        var (handler, repository, protector) = NewHandler();
        repository.Add(OwnRow(protector.Seed("k1", "token")));
        protector.KnownKeys.Remove("k1");

        var dto = await handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.True(dto.ApiKeyConfigured);
        Assert.False(dto.ApiKeyReadable);
    }

    [Fact]
    public async Task AlteredBytesWithTheKeyConfiguredAreNotReadable()
    {
        var (handler, repository, _) = NewHandler();
        repository.Add(OwnRow(FakeWhatsAppSecretProtector.Garbage("k2")));

        var dto = await handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.False(dto.ApiKeyReadable);
    }

    // Lo que la pantalla muestra al volver a "Cuenta propia".
    [Theory]
    [InlineData(WhatsAppMode.Shared)]
    [InlineData(WhatsAppMode.Disabled)]
    public async Task SharedAndDisabledStillReturnTheStoredOwnAccount(WhatsAppMode mode)
    {
        var (handler, repository, protector) = NewHandler();
        repository.Add(OwnRow(protector.Seed("k2", "token"), mode));

        var dto = await handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(mode.ToString(), dto.Mode);
        Assert.Equal("Zenvia", dto.Provider);
        Assert.Equal("573001234567", dto.FromNumber);
        Assert.True(dto.ApiKeyReadable);
    }

    [Fact]
    public async Task WithoutTheQuotationsModuleIsModuleNotEnabled()
    {
        var (handler, repository, _) = NewHandler(modules: StubTenantModules.Without(TenantModuleKeys.Quotations));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal(0, repository.FindCalls);
    }

    [Fact]
    public async Task WithoutSettingsReadIsForbidden()
    {
        var (handler, _, _) = NewHandler(new StubExecutionContext(SubjectId, TenantId, TenancyPermissions.SettingsRead));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
    }

    // Criterio 5: ninguna propiedad del DTO es el token, ni lo será sin que esta lista cambie.
    [Fact]
    public void NoPropertyOfTheDtoCarriesTheKey()
    {
        var names = typeof(WhatsAppSettingsDto).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal);

        Assert.Equal(
            new[] {
                "ApiKeyConfigured", "ApiKeyReadable", "ApiKeyUpdatedAt", "FromNumber", "Mode", "Modes",
                "Provider", "Providers", "TemplateId", "TenantId", "Version",
            },
            names);
    }
}
```

`tests/Modules/Quotations/Modules.Quotations.UnitTests/GetWhatsAppChannelHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-10-07: el canal que el flujo de envío lee antes de decidir qué mostrar, con el
/// permiso de enviar (no el de leer Configuración). Sin fila, habilitado y Shared.</summary>
public sealed class GetWhatsAppChannelHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static (GetWhatsAppChannelHandler Handler, InMemoryTenantWhatsAppSettingsRepository Repository)
        NewHandler(IExecutionContext? executionContext = null)
    {
        var repository = new InMemoryTenantWhatsAppSettingsRepository();
        return (
            new GetWhatsAppChannelHandler(repository, executionContext ?? new StubExecutionContext(SubjectId, TenantId)),
            repository);
    }

    [Fact]
    public async Task WithoutARowItIsEnabledAndShared()
    {
        var (handler, _) = NewHandler();

        var dto = await handler.HandleAsync(new GetWhatsAppChannelQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(new WhatsAppChannelDto(true, "Shared"), dto);
    }

    [Theory]
    [InlineData(WhatsAppMode.Shared, true)]
    [InlineData(WhatsAppMode.Own, true)]
    [InlineData(WhatsAppMode.Disabled, false)]
    public async Task EnabledIsEveryModeButDisabled(WhatsAppMode mode, bool enabled)
    {
        var (handler, repository) = NewHandler();
        var row = TenantWhatsAppSettings.CreateEmpty(TenantId, Now);
        row.Configure(WhatsAppMode.Own, WhatsAppProvider.Zenvia, new ProtectedSecret("k1", [1]), null,
            "573001234567", "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f", Now);
        row.Configure(mode, null, null, null, null, null, Now);
        repository.Add(row);

        var dto = await handler.HandleAsync(new GetWhatsAppChannelQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(new WhatsAppChannelDto(enabled, mode.ToString()), dto);
    }

    [Fact]
    public async Task WithoutQuotationManageIsForbidden()
    {
        var (handler, _) = NewHandler(
            new StubExecutionContext(SubjectId, TenantId, QuotationsPermissions.QuotationManage));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetWhatsAppChannelQuery(TenantId), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ForAnotherTenantIsForbidden()
    {
        var (handler, _) = NewHandler(new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetWhatsAppChannelQuery(TenantId), TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~GetWhatsAppSettingsHandlerTests|FullyQualifiedName~GetWhatsAppChannelHandlerTests"
```

Esperado: compilación fallida, `CS0246: The type or namespace name 'GetWhatsAppSettingsHandler' could not be found` y `'GetWhatsAppChannelHandler' could not be found`.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Application/GetWhatsAppSettings.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

public sealed record GetWhatsAppSettingsQuery(Guid TenantId) : IQuery<WhatsAppSettingsDto>;

/// <summary>
/// La configuración de WhatsApp para Configuración (spec 2026-10-07). Sin efecto colateral: sin
/// fila responde <see cref="TenantWhatsAppSettings.CreateEmpty"/> (Shared, versión 1) y no la crea.
/// Gate de capacidad después de autorizar y antes de leer, igual que el PUT. La key se descifra
/// para saber si es legible y el valor se descarta en el acto.
/// </summary>
public sealed class GetWhatsAppSettingsHandler(
    ITenantWhatsAppSettingsRepository repository,
    IWhatsAppSecretProtector protector,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<GetWhatsAppSettingsQuery, WhatsAppSettingsDto>
{
    public async Task<WhatsAppSettingsDto> HandleAsync(
        GetWhatsAppSettingsQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, TenancyPermissions.SettingsRead);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, query.TenantId, TenantModuleKeys.Quotations, cancellationToken);

        var stored = await repository.FindReadOnlyAsync(query.TenantId, cancellationToken);
        return WhatsAppSettingsMappings.ToDto(
            stored ?? TenantWhatsAppSettings.CreateEmpty(query.TenantId, clock.UtcNow), protector);
    }
}
```

`src/Modules/Quotations/Modules.Quotations.Application/GetWhatsAppChannel.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetWhatsAppChannelQuery(Guid TenantId) : IQuery<WhatsAppChannelDto>;

/// <summary>
/// El canal antes de enviar (spec 2026-10-07, «Cómo se hace explícito que no salió nada», punto 1).
/// Permiso de enviar y no de Configuración, porque los roles son editables. Sin gate de capacidad:
/// <c>quotations.quotation.manage</c> ya cae con el enmascaramiento de entitlements.
/// </summary>
public sealed class GetWhatsAppChannelHandler(
    ITenantWhatsAppSettingsRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetWhatsAppChannelQuery, WhatsAppChannelDto>
{
    public async Task<WhatsAppChannelDto> HandleAsync(
        GetWhatsAppChannelQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, QuotationsPermissions.QuotationManage);

        var stored = await repository.FindReadOnlyAsync(query.TenantId, cancellationToken);
        var mode = stored?.Mode ?? WhatsAppMode.Shared;
        return new WhatsAppChannelDto(mode != WhatsAppMode.Disabled, mode.ToString());
    }
}
```

(`using Modules.Tenancy.Application;` en `GetWhatsAppChannel.cs` sólo si `IExecutionContext` vive ahí; `UpdateOrdersExportLayout.cs` lo importa por eso. Si el compilador marca el `using` como innecesario, quítalo.)

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~GetWhatsAppSettingsHandlerTests|FullyQualifiedName~GetWhatsAppChannelHandlerTests"
```

Esperado: todas `Superado`.

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/GetWhatsAppSettings.cs src/Modules/Quotations/Modules.Quotations.Application/GetWhatsAppChannel.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/GetWhatsAppSettingsHandlerTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/GetWhatsAppChannelHandlerTests.cs; git commit -m "feat(quotations): leer la configuración y el canal de WhatsApp"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---
### Task B10: El envío resuelve el canal y devuelve `whatsAppOutcome`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs:17-168,179-196`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs:446-450`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs:439-452`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:345-347`
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (doble nuevo)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/SendQuotationHandlerTests.cs`

**Interfaces:**
- Consumes: `IWhatsAppChannelResolver`, `WhatsAppChannel` (B5), `QuotationSendStage.Channel`, `QuotationChangeSummary.SentWithoutWhatsApp/ResentWithoutWhatsApp/SendFailed(stage, whatsAppSkipped)` (B6).
- Produces:
  - `public enum QuotationWhatsAppOutcome { Accepted, Disabled }`
  - `public sealed record SendQuotationResult(QuotationDto Quotation, QuotationWhatsAppOutcome WhatsApp)`; `SendQuotationCommand : ICommand<SendQuotationResult>`
  - `QuotationResponse.WhatsAppOutcome` (`string?`, último parámetro, default `null`)
  - `StubWhatsAppChannelResolver(WhatsAppChannel? channel, Exception? failure = null)` en `QuotationsTestDoubles.cs`

- [ ] **Step 1: Edición mecánica de las pruebas existentes y pruebas nuevas que fallan**

En `QuotationsTestDoubles.cs`, después de `RecordingWhatsAppSender` (`:33-43`):

```csharp
/// <summary>El canal que el envío resuelve (spec 2026-10-07). Con <paramref name="failure"/>,
/// la falla de la etapa Channel.</summary>
internal sealed class StubWhatsAppChannelResolver(WhatsAppChannel? channel, Exception? failure = null)
    : IWhatsAppChannelResolver
{
    public Task<WhatsAppChannel> ResolveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        failure is null
            ? Task.FromResult(channel!)
            : Task.FromException<WhatsAppChannel>(failure);
}
```

En `SendQuotationHandlerTests.cs`, `NewHandler` (`:342-413`) — edición mecánica: tres parámetros nuevos con default y el resolver en lugar del sender. Las pruebas existentes no se tocan (criterio 1):

```csharp
    private static Harness NewHandler(
        bool withValidUntil = true,
        bool alreadySent = false,
        Exception? whatsAppFailure = null,
        QuotationPartyDetails? billing = null,
        WhatsAppMode mode = WhatsAppMode.Shared,
        Exception? channelFailure = null,
        IQuotationsUnitOfWork? unitOfWork = null)
```

y, en el cuerpo, reemplazar el argumento

```csharp
            whatsAppFailure is null
                ? sender
                : new FailingWhatsAppSender(whatsAppFailure),
```

por

```csharp
            new StubWhatsAppChannelResolver(
                mode == WhatsAppMode.Disabled
                    ? new WhatsAppChannel(WhatsAppMode.Disabled, null)
                    : new WhatsAppChannel(
                        mode,
                        whatsAppFailure is null ? sender : new FailingWhatsAppSender(whatsAppFailure)),
                channelFailure),
```

y `new NoOpQuotationsUnitOfWork(),` por `unitOfWork ?? new NoOpQuotationsUnitOfWork(),`.

Agregar las pruebas nuevas antes de `NewCommand`:

```csharp
    // Spec 2026-10-07, «Envío»: con WhatsApp desactivado, PDF y snapshots como siempre, sin copia
    // pública ni mensaje; la cotización pasa a Sent y el resultado lo dice.
    [Fact]
    public async Task WithWhatsAppDisabledTheQuotationIsSentWithoutPublishingOrSending()
    {
        var harness = NewHandler(mode: WhatsAppMode.Disabled);

        var result = await harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(QuotationWhatsAppOutcome.Disabled, result.WhatsApp);
        Assert.Null(harness.Storage.PublishedKey);
        Assert.Null(harness.Sender.Sent);
        Assert.Equal(QuotationStatus.Sent, harness.Repository.Quotation.Status);
        var entry = Assert.Single(harness.Repository.HistoryEntries);
        Assert.Equal(QuotationHistoryEventType.Sent, entry.EventType);
        Assert.Equal(
            "Marcada como enviada sin WhatsApp: el envío por WhatsApp está desactivado para la empresa.",
            entry.Details);
        Assert.Equal("quotation.quotation.sent", Assert.Single(harness.Audit.Actions));
    }

    [Fact]
    public async Task ResendingWithWhatsAppDisabledIsAResentWithItsOwnText()
    {
        var harness = NewHandler(alreadySent: true, mode: WhatsAppMode.Disabled);

        var result = await harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(QuotationWhatsAppOutcome.Disabled, result.WhatsApp);
        var entry = Assert.Single(harness.Repository.HistoryEntries);
        Assert.Equal(QuotationHistoryEventType.Resent, entry.EventType);
        Assert.Equal(
            "Marcada como reenviada sin WhatsApp: el envío por WhatsApp está desactivado para la empresa.",
            entry.Details);
    }

    // El destinatario del cuerpo se ignora: Billing sin datos propios no falla con Disabled.
    [Fact]
    public async Task WithWhatsAppDisabledTheRecipientIsIgnored()
    {
        var harness = NewHandler(mode: WhatsAppMode.Disabled);

        var result = await harness.Handler.HandleAsync(NewCommand("Billing"), TestContext.Current.CancellationToken);

        Assert.Equal(QuotationWhatsAppOutcome.Disabled, result.WhatsApp);
    }

    [Theory]
    [InlineData(WhatsAppMode.Shared)]
    [InlineData(WhatsAppMode.Own)]
    public async Task SharedAndOwnAreAccepted(WhatsAppMode mode)
    {
        var harness = NewHandler(mode: mode);

        var result = await harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(QuotationWhatsAppOutcome.Accepted, result.WhatsApp);
        Assert.NotNull(harness.Sender.Sent);
        Assert.Equal(RecordingPdfStorage.PublicUrl, harness.Sender.Sent.DocumentUrl);
        Assert.Equal(
            "Enviada al cliente con su PDF.",
            Assert.Single(harness.Repository.HistoryEntries).Details);
    }

    // settings_unreadable es de dominio: se relanza tal cual, no como quotation.send.failed.
    [Fact]
    public async Task AChannelFailureKeepsItsCodeAndIsAnnotatedAtTheChannelStage()
    {
        var harness = NewHandler(channelFailure: new QuotationsDomainException(
            "quotation.whatsapp.settings_unreadable", "The tenant's WhatsApp API key cannot be decrypted."));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.settings_unreadable", error.Code);
        Assert.Equal(
            "No se pudo enviar la cotización: no pudimos leer la configuración de WhatsApp de la " +
            "empresa. Pide a un administrador que la revise en Configuración.",
            harness.FailureLog.HistoryEntry?.Details);
        Assert.Equal(QuotationStatus.Draft, harness.Repository.Quotation.Status);
        Assert.Null(harness.Storage.PublishedKey);
    }

    [Fact]
    public async Task APersistenceFailureWithWhatsAppDisabledSaysNoMessageWasSent()
    {
        var harness = NewHandler(
            mode: WhatsAppMode.Disabled,
            unitOfWork: new CountingQuotationsUnitOfWork { Failure = new InvalidOperationException("db down") });

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.send.failed", error.Code);
        Assert.Equal(
            "No se pudo enviar la cotización: no pudimos registrar el envío. No se mandó ningún " +
            "WhatsApp, así que puedes reintentar.",
            harness.FailureLog.HistoryEntry?.Details);
    }
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~SendQuotationHandlerTests"
```

Esperado: compilación fallida, `CS1503: Argument 8: cannot convert from 'StubWhatsAppChannelResolver' to 'IWhatsAppSender'` y `CS0246: The type or namespace name 'QuotationWhatsAppOutcome' could not be found`.

- [ ] **Step 3: Implementar**

En `SendQuotation.cs`, el comando (`:17-18`) pasa a:

```csharp
public sealed record SendQuotationCommand(
    Guid TenantId, Guid QuotationId, string? Recipient = null) : ICommand<SendQuotationResult>;

/// <summary>Qué pasó con el WhatsApp (spec 2026-10-07). <c>Accepted</c> y no <c>Sent</c>: un 2xx
/// de Zenvia significa que encoló el mensaje; la entrega la resuelve Meta después.</summary>
public enum QuotationWhatsAppOutcome
{
    Accepted,
    Disabled
}

/// <summary>La cotización enviada y si salió un WhatsApp. El endpoint lo suma a la respuesta como
/// <c>whatsAppOutcome</c>: con Disabled, un 200 con la cotización en Sent no se distingue de un
/// envío real si nadie lo dice (la trampa del "envío fantasma").</summary>
public sealed record SendQuotationResult(QuotationDto Quotation, QuotationWhatsAppOutcome WhatsApp);
```

En el handler, el parámetro `IWhatsAppSender whatsAppSender,` (`:36`) pasa a `IWhatsAppChannelResolver channelResolver,`, y `ICommandHandler<SendQuotationCommand, QuotationDto>` (`:41`) a `ICommandHandler<SendQuotationCommand, SendQuotationResult>`. Reemplazar `HandleAsync` desde la firma hasta el cierre del `catch` (`:43-168`) por:

```csharp
    public async Task<SendQuotationResult> HandleAsync(
        SendQuotationCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);

        var quotation = await repository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        // Antes de cualquier efecto externo: firmar la URL del PDF y entregarle el mensaje a
        // WhatsApp no se deshacen, y una cotización que no puede pasar a Sent no puede haberle
        // llegado al cliente. `Send` vuelve a comprobarlo al final — este llamado es para el
        // orden, no para reemplazar la invariante del agregado.
        //
        // Queda **fuera** del try de más abajo a propósito: una cotización anulada o vencida no es
        // un envío que falló, es un envío que nunca empezó, y anotarlo en el registro de fallas lo
        // llenaría de ruido que no se arregla mirando una traza.
        quotation.EnsureSendable();

        // Se lee antes de mutar: `Send` deja la cotización en Sent venga de donde venga, así que
        // después de llamarlo ya no hay forma de saber si esto fue el primer envío o un reenvío.
        var isResend = quotation.Status == QuotationStatus.Sent;

        // El paso en curso. Se va moviendo para que la falla diga **dónde** se cayó, que es lo
        // único que separa "hay que revisar el teléfono del cliente" de "está caído Zenvia".
        var stage = QuotationSendStage.Advisor;
        MemberId? sentBy = null;
        // Spec 2026-10-07: con WhatsApp desactivado, "el mensaje salió" deja de ser cierto, y el
        // historial de una falla de Persistence tiene que decirlo.
        var whatsAppSkipped = false;

        try
        {
            // Se resuelve primero, antes de generar nada: si quien envía no es miembro del
            // tenant, es mejor saberlo antes de pagar una llamada a `qcode-pdf` — y además deja
            // el dato disponible para anotar quién intentó, si algo falla más adelante.
            sentBy = await QuotationAdvisorResolver.ResolveAsync(
                membershipDirectory, executionContext, command.TenantId, cancellationToken);

            // Spec 2026-10-07: por dónde sale el WhatsApp, una lectura por envío y sin caché. Una
            // key que no descifra es settings_unreadable, de dominio: el catch la relanza tal cual
            // y la pantalla manda a Configuración.
            stage = QuotationSendStage.Channel;
            var channel = await channelResolver.ResolveAsync(command.TenantId, cancellationToken);
            whatsAppSkipped = channel.Sender is null;

            // El documento se genera acá, no lo sube el navegador: así el PDF que recibe el
            // cliente no depende de qué pantalla lo pidió ni de qué versión del frontend estaba
            // abierta. Sólo cuesta una llamada a `qcode-pdf` si la cotización cambió.
            stage = QuotationSendStage.Pdf;
            // El catálogo de hoy para las líneas que el envío va a congelar (owner, 2026-09-26). Se
            // lee acá y no al marcarla enviada: es lo mismo que el composer va a imprimir en el PDF,
            // y una falla del catálogo tiene que cortar antes de que el mensaje salga. Cuenta como
            // paso del documento porque es su contenido.
            var products = await QuotationItemProductLabel.ResolveMissingAsync(
                productLookup, command.TenantId, quotation, cancellationToken);
            var pdf = await pdfProvider.EnsureCurrentAsync(quotation, cancellationToken);

            // Meta **no puede** bajar el PDF desde una URL prefirmada de R2: le falla y descarta
            // el mensaje entero, minutos después de que Zenvia ya respondió 200. Por eso se
            // publica una copia con clave aleatoria, que además evita que Meta sirva de su caché
            // el documento viejo en un reenvío. La copia la limpia el lifecycle del bucket.
            // Sin WhatsApp no hay copia: sólo existe para Meta (spec 2026-10-07, decisión 10).
            string? documentUrl = null;
            if (channel.Sender is not null)
            {
                stage = QuotationSendStage.Publish;
                documentUrl = await pdfStorage.PublishAsync(pdf.StorageKey, cancellationToken);
            }

            // Validar al cliente es regla del envío, no del canal: corre también sin WhatsApp.
            stage = QuotationSendStage.Recipient;
            var customer = await customerLookup.FindAsync(
                command.TenantId, quotation.ClientId, cancellationToken);
            QuotationCustomerEligibility.Ensure(customer, command.TenantId, quotation.ClientId);

            // El WhatsApp se manda antes de tocar el agregado y a propósito: si Zenvia falla, la
            // cotización tiene que seguir en borrador — "Enviar" significa que de verdad llegó, no
            // que quedó marcada como enviada sin que nadie la haya recibido. Así la persona
            // simplemente reintenta el mismo botón en vez de quedar en un estado a medio camino
            // que ningún otro flujo sabe destrabar. Con WhatsApp desactivado el destinatario del
            // cuerpo se ignora: no hay a quién mandarle nada.
            if (channel.Sender is { } sender)
            {
                // A quien se le manda. Se resuelve despues de validar al cliente porque el default
                // --y el respaldo de nombre-- sigue saliendo de ahi.
                var (toPhone, fullName) = ResolveRecipient(command.Recipient, quotation, customer!);

                stage = QuotationSendStage.WhatsApp;
                await sender.SendQuotationAsync(
                    new WhatsAppQuotationMessage(
                        ToPhone: toPhone,
                        FullName: fullName,
                        OrderNumber: quotation.QuotationNumber,
                        Total: quotation.Total,
                        ValidUntil: quotation.ValidUntil!.Value,
                        DocumentUrl: documentUrl!),
                    cancellationToken);
            }

            stage = QuotationSendStage.Persistence;
            var now = clock.UtcNow;
            quotation.Send(sentBy.Value, now, products);

            repository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(),
                quotation.Id,
                isResend ? QuotationHistoryEventType.Resent : QuotationHistoryEventType.Sent,
                sentBy,
                HistorySummary(isResend, whatsAppSkipped),
                now));
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                isResend ? "quotation.quotation.resent" : "quotation.quotation.sent",
                quotation.Id.ToString(),
                "success",
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return new SendQuotationResult(
                quotation.ToDto(),
                whatsAppSkipped ? QuotationWhatsAppOutcome.Disabled : QuotationWhatsAppOutcome.Accepted);
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(quotation, stage, sentBy, whatsAppSkipped);

            // Un error de dominio ya se explica solo: tiene código propio y un mensaje escrito
            // para el caso. Se relanza tal cual, para no esconder
            // `quotation.whatsapp.recipient_missing` detrás de un genérico.
            if (exception is QuotationsDomainException)
            {
                throw;
            }

            // Todo lo demás salía como `500 server.unexpected`: la pantalla decía que algo falló
            // y no había forma de saber qué. Ahora sale con código propio y nombrando el paso.
            // La original viaja como `InnerException`, así que el log de fallas de la API la
            // guarda entera --con su traza-- cuando este error llegue al manejador.
            throw new QuotationsDomainException(
                "quotation.send.failed",
                $"The quotation could not be sent ({stage}).",
                exception);
        }
    }

    // Spec 2026-10-07, punto 4: el evento sigue siendo Sent/Resent (es lo que cambió de estado),
    // pero el resumen dice que no salió WhatsApp.
    private static string HistorySummary(bool isResend, bool whatsAppSkipped) =>
        (isResend, whatsAppSkipped) switch
        {
            (true, true) => QuotationChangeSummary.ResentWithoutWhatsApp(),
            (false, true) => QuotationChangeSummary.SentWithoutWhatsApp(),
            (true, false) => QuotationChangeSummary.Resent(),
            (false, false) => QuotationChangeSummary.Sent(),
        };
```

`RecordFailureAsync` (`:179-196`) suma el parámetro y lo pasa:

```csharp
    private async Task RecordFailureAsync(
        Quotation quotation,
        QuotationSendStage stage,
        MemberId? attemptedBy,
        bool whatsAppSkipped)
    {
        var historyEntry = QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.SendFailed,
            attemptedBy,
            QuotationChangeSummary.SendFailed(stage, whatsAppSkipped),
            clock.UtcNow);
```

(el resto del método igual).

En `QuotationsDtos.cs`, el último parámetro de `QuotationResponse` (`:450`) pasa a:

```csharp
    IReadOnlyCollection<int> AvailableGlobalScaleFloors,
    /// <summary>Spec 2026-10-07: <c>"Accepted"</c> | <c>"Disabled"</c> en la respuesta de
    /// <c>POST .../send</c>, nulo en el resto. Aditivo, mismo criterio que <c>AdvisorName</c>: un
    /// frontend desplegado antes no se entera. La pantalla lo lee de acá y no del canal que pidió
    /// antes: si alguien desactivó WhatsApp entre el diálogo y la confirmación, manda la
    /// respuesta.</summary>
    string? WhatsAppOutcome = null);
```

En `QuotationEndpoints.cs`, `SendQuotationAsync` (`:439-452`):

```csharp
    private static async Task<IResult> SendQuotationAsync(
        Guid tenantId,
        Guid quotationId,
        IRequestDispatcher dispatcher,
        IQuotationResponseComposer composer,
        CancellationToken cancellationToken,
        SendQuotationRequest? request = null)
    {
        var result = await dispatcher.SendAsync(
            new SendQuotationCommand(tenantId, quotationId, request?.Recipient),
            cancellationToken);

        var response = await composer.ComposeAsync(tenantId, result.Quotation, cancellationToken);
        return Results.Ok(response with { WhatsAppOutcome = result.WhatsApp.ToString() });
    }
```

En `QepServiceCollectionExtensions.cs:345-347`: `ICommandHandler<SendQuotationCommand, SendQuotationResult>,`.

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
dotnet build --no-restore
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~SendQuotationHandlerTests|FullyQualifiedName~QuotationChangeSummaryTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationSendVoidApiTests|FullyQualifiedName~QuotationAdvisorNameApiTests"
```

Esperado: build limpio; unitarias todas `Superado` (las existentes de `SendQuotationHandlerTests` sin tocar sus aserciones); las de integración del envío existentes **sin editar**, verdes (criterio 1). Si `dotnet build` marca otro llamador de `SendQuotationCommand` o de `HandleAsync` que espere `QuotationDto`, el hallazgo 1 quedó viejo: corrígelo con `.Quotation` y anótalo.

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Application/SendQuotation.cs src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/SendQuotationHandlerTests.cs; git commit -m "feat(quotations): el envío resuelve el canal de WhatsApp del tenant y dice si salió"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B11: Endpoints de configuración y canal, con sus pruebas de API

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Api/WhatsAppSettingsEndpoints.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs` (`GET /whatsapp-channel`, después de `MapPost("/export", …)`, `:28-32`)
- Modify: `src/Api/Program.cs:139`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:398-403`
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs` (helpers HTTP y de host)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSettingsApiTests.cs`

**Interfaces:**
- Consumes: B7–B9 (comando, queries, DTOs), `TenancyPermissions`, `QuotationsPermissions`, `PreconditionRequiredException`, `IRequestDispatcher`.
- Produces:
  - `WhatsAppSettingsEndpoints.MapWhatsAppSettingsEndpoints(this IEndpointRouteBuilder)`
  - `public sealed record UpdateWhatsAppSettingsRequest(string? Mode, string? Provider = null, string? ApiKey = null, string? FromNumber = null, string? TemplateId = null)` con `ToString()` sin la key
  - `public sealed record WhatsAppSettingsResponse(...)` (mismos campos que `WhatsAppSettingsDto`), `public sealed record WhatsAppChannelResponse(bool Enabled, string Mode)`
  - `WhatsAppTestHarness`: `WhatsAppSettingsUrl(Guid)`, `WhatsAppChannelUrl(Guid)`, `SettingsPermissions`, `PutSettingsAsync(HttpClient, Guid, object, string? ifMatch)`, `CreateClientFor(WebApplicationFactory<Program>, Guid subjectId, Guid tenantId, params string[])`. B12 y B13 los usan.

- [ ] **Step 1: Ampliar el harness y escribir las pruebas que fallan**

En `WhatsAppTestHarness.cs`, sumar los `using` y miembros:

```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Tenancy.Application;
using Npgsql;
```

```csharp
    public static string WhatsAppSettingsUrl(Guid tenantId) =>
        $"/api/v1/tenants/{tenantId}/quotations/whatsapp-settings";

    public static string WhatsAppChannelUrl(Guid tenantId) =>
        $"/api/v1/tenants/{tenantId}/quotations/whatsapp-channel";

    public static readonly string[] SettingsPermissions =
        [TenancyPermissions.SettingsRead, TenancyPermissions.SettingsUpdate];

    /// <summary>El cuerpo va como objeto anónimo a propósito: así una prueba manda exactamente lo
    /// que quiere (campos ausentes incluidos), sin un record que los rellene con null.</summary>
    public static async Task<HttpResponseMessage> PutSettingsAsync(
        HttpClient client, Guid tenantId, object body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, WhatsAppSettingsUrl(tenantId))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static object OwnBody(string apiKey = SentinelApiKey) => new
    {
        mode = "Own",
        provider = "Zenvia",
        apiKey,
        fromNumber = FromNumber,
        templateId = TemplateId,
    };

    /// <summary><see cref="QuotationsApiHarness.CreateClient"/> pide un <c>QepApiFactory</c>; un
    /// host derivado con <c>WithWebHostBuilder</c> no lo es. Mismos headers del stub.</summary>
    public static HttpClient CreateClientFor(
        WebApplicationFactory<Program> factory, Guid subjectId, Guid tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }
```

> El `PUT` lleva `X-Qep-Client: web` explícito. Con el stub de desarrollo no hace falta (`Program.cs:64-67` no registra el CSRF en modo stub), pero así las pruebas no dependen de eso.

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSettingsApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Api;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using static Modules.Quotations.IntegrationTests.WhatsAppTestHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-10-07, «Endpoints» y «Pruebas → Integración»: el GET sin fila, el primer guardado Own
/// sin que la key vuelva nunca, conservar el texto cifrado sin apiKey y al ir y volver de Own, 428
/// y 412, el no-op de Shared sin fila, Disabled sobre bytes dañados (200, no 500), los cuatro
/// faltantes, los 403 y el gate de capacidad con un tenant real.
/// </summary>
public sealed class WhatsAppSettingsApiTests
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

    private static Task<byte[]> CiphertextAsync(string connectionString, Guid tenantId) =>
        ScalarAsync<byte[]>(
            connectionString,
            $"SELECT api_token_ciphertext FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'");

    [Fact]
    public async Task WithoutARowTheSettingsAreSharedAtVersionOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var response = await client.GetAsync(WhatsAppSettingsUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        var body = await JsonAsync(response);
        Assert.Equal("Shared", body.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("provider").ValueKind);
        Assert.False(body.GetProperty("apiKeyConfigured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("apiKeyReadable").ValueKind);
        Assert.Equal(
            new[] { "Shared", "Own", "Disabled" },
            body.GetProperty("modes").EnumerateArray().Select(mode => mode.GetString()).ToArray());
        Assert.Equal(
            new[] { "Zenvia" },
            body.GetProperty("providers").EnumerateArray().Select(provider => provider.GetString()).ToArray());
        Assert.Equal(1, body.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task TheFirstOwnSaveReturnsETagTwoAndTheKeyNeverComesBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var put = await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"");
        var putRaw = await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var get = await client.GetAsync(WhatsAppSettingsUrl(tenantId), TestContext.Current.CancellationToken);
        var getRaw = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("\"2\"", put.Headers.ETag?.Tag);
        Assert.DoesNotContain(SentinelApiKey, putRaw, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelApiKey, getRaw, StringComparison.Ordinal);
        var body = JsonDocument.Parse(getRaw).RootElement;
        Assert.Equal("Own", body.GetProperty("mode").GetString());
        Assert.True(body.GetProperty("apiKeyConfigured").GetBoolean());
        Assert.True(body.GetProperty("apiKeyReadable").GetBoolean());
        Assert.Equal(FromNumber, body.GetProperty("fromNumber").GetString());
        Assert.Equal(TemplateId, body.GetProperty("templateId").GetString());
    }

    [Fact]
    public async Task APutWithoutApiKeyKeepsTheCiphertext()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;
        (await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        var before = await CiphertextAsync(database.GetConnectionString(), tenantId);

        var response = await PutSettingsAsync(
            client, tenantId, new { mode = "Own", templateId = "11111111-2222-3333-4444-555555555555" }, "\"2\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await CiphertextAsync(database.GetConnectionString(), tenantId));
    }

    // Criterio 4: Own → Shared → Own no obliga a reescribir las credenciales.
    [Fact]
    public async Task OwnToSharedAndBackKeepsTheSameCiphertext()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;
        (await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        var before = await CiphertextAsync(database.GetConnectionString(), tenantId);

        (await PutSettingsAsync(client, tenantId, new { mode = "Shared" }, "\"2\"")).EnsureSuccessStatusCode();
        var back = await PutSettingsAsync(client, tenantId, new { mode = "Own" }, "\"3\"");

        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal("Own", (await JsonAsync(back)).GetProperty("mode").GetString());
        Assert.Equal(before, await CiphertextAsync(database.GetConnectionString(), tenantId));
    }

    [Fact]
    public async Task APutWithoutIfMatchIsPreconditionRequired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var response = await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, ifMatch: null);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
        Assert.Equal("precondition.if_match_required", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AStaleVersionIsAConflict()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;
        (await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"")).EnsureSuccessStatusCode();

        var response = await PutSettingsAsync(client, tenantId, new { mode = "Shared" }, "\"1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("concurrency.conflict", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SharedWithoutARowIsANoOpThatCreatesNoRow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var response = await PutSettingsAsync(client, tenantId, new { mode = "Shared" }, "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await JsonAsync(response)).GetProperty("version").GetInt64());
        Assert.Equal(0L, await ScalarAsync<long>(
            database.GetConnectionString(),
            $"SELECT count(*) FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'"));
    }

    // Decisión 25: el drenaje se salta y el guardado no es un 500.
    [Fact]
    public async Task DisabledOverDamagedBytesSavesAndTheKeyIsReportedUnreadable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;
        (await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        await ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE quotations.tenant_whatsapp_settings "
            + "SET api_token_ciphertext = set_byte(api_token_ciphertext, 20, get_byte(api_token_ciphertext, 20) # 255) "
            + $"WHERE tenant_id = '{tenantId}'");

        var put = await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"2\"");
        var get = await client.GetAsync(WhatsAppSettingsUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False((await JsonAsync(get)).GetProperty("apiKeyReadable").GetBoolean());
    }

    [Fact]
    public async Task OwnWithoutARowListsTheFourMissingFields()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var response = await PutSettingsAsync(client, tenantId, new { mode = "Own" }, "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("validation.failed", body.GetProperty("code").GetString());
        Assert.Equal(
            new[] { "ApiKey", "FromNumber", "Provider", "TemplateId" },
            body.GetProperty("errors").EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task WithoutSettingsUpdateThePutIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, TenancyPermissions.SettingsRead);
        using var _ = client;

        var response = await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (_, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;

        var response = await client.GetAsync(
            WhatsAppSettingsUrl(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El stub con un tenant inventado no ejercita el guard (FindAsync en null): hace falta uno real
    // al que se le quita quotations.
    [Fact]
    public async Task WithoutTheQuotationsModuleTheSettingsAreModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = client;
        Assert.Equal(1, await ExecuteAsync(
            database.GetConnectionString(),
            $"DELETE FROM tenancy.tenant_modules WHERE tenant_id = '{tenantId}' AND module_key = 'quotations'"));

        var get = await client.GetAsync(WhatsAppSettingsUrl(tenantId), TestContext.Current.CancellationToken);
        var put = await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"");

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await JsonAsync(get)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await JsonAsync(put)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheChannelWithoutARowIsEnabledAndShared()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationManage);
        using var _ = client;

        var response = await client.GetAsync(WhatsAppChannelUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"enabled":true,"mode":"Shared"}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheChannelWhenDisabledIsNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. SettingsPermissions, QuotationsPermissions.QuotationManage]);
        using var _ = client;
        (await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"")).EnsureSuccessStatusCode();

        var response = await client.GetAsync(WhatsAppChannelUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(
            """{"enabled":false,"mode":"Disabled"}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheChannelWithoutQuotationManageIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationRead);
        using var _ = client;

        var response = await client.GetAsync(WhatsAppChannelUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheChannelForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (_, _, client) = await RegisterTenantAsync(factory, QuotationsPermissions.QuotationManage);
        using var _ = client;

        var response = await client.GetAsync(
            WhatsAppChannelUrl(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Criterio 5: la misma afirmación que la del comando, sobre el cuerpo HTTP.
    [Fact]
    public void TheRequestToStringDoesNotContainTheKey()
    {
        var request = new UpdateWhatsAppSettingsRequest("Own", "Zenvia", SentinelApiKey, FromNumber, TemplateId);

        Assert.DoesNotContain(SentinelApiKey, request.ToString(), StringComparison.Ordinal);
        Assert.Contains("***", request.ToString(), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~WhatsAppSettingsApiTests"
```

Esperado: compilación fallida, `CS0246: The type or namespace name 'UpdateWhatsAppSettingsRequest' could not be found`.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Api/WhatsAppSettingsEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Api;

/// <summary>
/// La configuración de WhatsApp del tenant (spec 2026-10-07): un recurso bajo su configuración, con
/// los permisos de settings y no uno nuevo, ETag e If-Match sobre su versión propia. Grupo y tag
/// propios ("Tenant settings"), igual que <see cref="OrdersExportLayoutEndpoints"/>: colgarlo del
/// grupo de cotizaciones lo dejaría con dos tags en OpenAPI. <c>whatsapp-settings</c> no choca con
/// <c>/{quotationId:guid}</c> por la restricción de guid.
/// </summary>
public static class WhatsAppSettingsEndpoints
{
    public static IEndpointRouteBuilder MapWhatsAppSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/quotations/whatsapp-settings")
            .WithTags("Tenant settings");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<WhatsAppSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdateWhatsAppSettingsRequest>("application/json")
            .Produces<WhatsAppSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var settings = await dispatcher.QueryAsync(new GetWhatsAppSettingsQuery(tenantId), cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        UpdateWhatsAppSettingsRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded WhatsApp settings version is required.");
        }

        // Mode ausente viaja vacío: es el validador el que lo rechaza con errors.Mode.
        var settings = await dispatcher.SendAsync(
            new UpdateWhatsAppSettingsCommand(
                tenantId,
                request.Mode ?? string.Empty,
                request.Provider,
                request.ApiKey,
                request.FromNumber,
                request.TemplateId,
                expectedVersion),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static IResult SettingsResult(WhatsAppSettingsDto settings, HttpContext httpContext)
    {
        httpContext.Response.Headers.ETag = $"\"{settings.Version}\"";
        return Results.Ok(new WhatsAppSettingsResponse(
            settings.TenantId,
            settings.Mode,
            settings.Provider,
            settings.ApiKeyConfigured,
            settings.ApiKeyUpdatedAt,
            settings.ApiKeyReadable,
            settings.FromNumber,
            settings.TemplateId,
            settings.Modes,
            settings.Providers,
            settings.Version));
    }

    // Copia de OrdersExportLayoutEndpoints.TryParseVersion (privado allá, mismo proyecto): acepta
    // "3", 3 y W/"3".
    private static bool TryParseVersion(string? etag, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        return long.TryParse(normalized, out version) && version > 0;
    }
}

/// <summary>
/// Spec 2026-10-07. <c>apiKey</c> ausente o null conserva la guardada: no hay forma de "borrarla",
/// se cambia de modo. Fuera de <c>Own</c> los campos de la cuenta propia se ignoran.
/// <see cref="ToString"/> esconde la key: el de un record imprime todas sus propiedades.
/// </summary>
public sealed record UpdateWhatsAppSettingsRequest(
    string? Mode,
    string? Provider = null,
    string? ApiKey = null,
    string? FromNumber = null,
    string? TemplateId = null)
{
    public override string ToString() =>
        $"UpdateWhatsAppSettingsRequest {{ Mode = {Mode}, Provider = {Provider}, "
        + $"ApiKey = {(ApiKey is null ? "null" : "***")}, FromNumber = {FromNumber}, TemplateId = {TemplateId} }}";
}

/// <summary>Ver <see cref="WhatsAppSettingsDto"/> para por qué viaja cada campo.</summary>
public sealed record WhatsAppSettingsResponse(
    Guid TenantId,
    string Mode,
    string? Provider,
    bool ApiKeyConfigured,
    DateTimeOffset? ApiKeyUpdatedAt,
    bool? ApiKeyReadable,
    string? FromNumber,
    string? TemplateId,
    IReadOnlyList<string> Modes,
    IReadOnlyList<string> Providers,
    long Version);

/// <summary>Ver <see cref="WhatsAppChannelDto"/>.</summary>
public sealed record WhatsAppChannelResponse(bool Enabled, string Mode);
```

En `QuotationEndpoints.cs`, después del `MapPost("/export", …)` (`:28-32`):

```csharp
        // Spec 2026-10-07: el canal de WhatsApp antes de enviar. Permiso de enviar y no de
        // Configuración (los roles son editables). Va en este grupo porque es parte del envío; no
        // choca con /{quotationId:guid} por la restricción de guid, igual que /export.
        group.MapGet("/whatsapp-channel", GetWhatsAppChannelAsync)
            .RequireAuthorization(QuotationsPermissions.QuotationManage)
            .Produces<WhatsAppChannelResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
```

y el handler, junto a los demás métodos privados:

```csharp
    private static async Task<IResult> GetWhatsAppChannelAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var channel = await dispatcher.QueryAsync(new GetWhatsAppChannelQuery(tenantId), cancellationToken);
        return Results.Ok(new WhatsAppChannelResponse(channel.Enabled, channel.Mode));
    }
```

En `Program.cs`, después de `app.MapOrdersExportLayoutEndpoints();` (`:139`): `app.MapWhatsAppSettingsEndpoints();`.

En `QepServiceCollectionExtensions.cs`, después del registro de `UpdateOrdersExportLayoutHandler` (`:401-403`):

```csharp
        // La configuración de WhatsApp por tenant (spec 2026-10-07). A mano, como el resto: un
        // handler que falte compila, mapea su endpoint y falla recién en runtime con 500.
        services.AddScoped<
            IQueryHandler<GetWhatsAppSettingsQuery, WhatsAppSettingsDto>,
            GetWhatsAppSettingsHandler>();
        services.AddScoped<
            ICommandHandler<UpdateWhatsAppSettingsCommand, WhatsAppSettingsDto>,
            UpdateWhatsAppSettingsHandler>();
        services.AddScoped<
            IQueryHandler<GetWhatsAppChannelQuery, WhatsAppChannelDto>,
            GetWhatsAppChannelHandler>();
```

El validador lo recoge `AddValidatorsFromAssemblyContaining<CreateQuotationValidator>()` (`:435`) sin tocar nada.

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~WhatsAppSettingsApiTests|FullyQualifiedName~OrdersExportLayoutApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
```

Esperado: todo verde. Si `WithoutTheQuotationsModuleTheSettingsAreModuleNotEnabled` falla con 200, revisa si `ITenantModules` del plan de entitlements cachea por tenant: el spec de entitlements dice una consulta por request; si se cacheó, la prueba necesita invalidar esa caché y el hallazgo va al handoff.

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Api/WhatsAppSettingsEndpoints.cs src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs src/Api/Program.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSettingsApiTests.cs; git commit -m "feat(quotations): endpoints de configuración y canal de WhatsApp"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---
### Task B12: Envío por canal de punta a punta, y la key que no se fuga

**Files:**
- Modify: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs` (dobles y extensiones de host)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSendChannelApiTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSecretLeakTests.cs`

Esta tarea no cambia código de producción: prueba de punta a punta lo que B2–B11 construyeron. Por eso el «RED» es de compilación (las extensiones no existen) y, una vez que compila, cualquier rojo es un defecto real de una tarea anterior: se arregla en el archivo de esa tarea, con su prueba unitaria que lo reproduzca primero, y se anota en el handoff.

**Interfaces:**
- Consumes: B11 (`PutSettingsAsync`, `OwnBody`, `CreateClientFor`, URLs, `SettingsPermissions`), `ZenviaHttpClient` (B4, internal, visible por `InternalsVisibleTo`), `QuotationsApiHarness` (`ManagerPermissions`, `CreateActiveCustomerAsync`, `CreateProductWithScalesAsync`, `CreateCompanyWithBankAccountAsync`, `CreateQuotationAsync`, `QuotationsUrl`).
- Produces (en `WhatsAppTestHarness`; B13 los usa): `SentinelZenviaBody`, `SendPermissions`, `WithWhatsAppSender`, `WithZenviaHandler`, `WithCapturedLogs`, `WithSecretProtection`, `CreateSendableQuotationAsync`, `SendAsync`, `RequestFailuresTextAsync`; clases `RecordingIntegrationWhatsAppSender`, `CapturingZenviaHandler`, `CapturedLogs : ILoggerProvider`.

- [ ] **Step 1: Ampliar el harness**

Sumar a `WhatsAppTestHarness.cs` los `using`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Whatsapp;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
```

los miembros de la clase estática:

```csharp
    /// <summary>Un cuerpo de Zenvia inventado: no puede aparecer en ningún camino de salida.</summary>
    public const string SentinelZenviaBody = "zenvia-body-SENTINEL-5b2d1e";

    public static readonly string[] SendPermissions = [.. ManagerPermissions, .. SettingsPermissions];

    /// <summary>Reemplaza el sender global (el de la cuenta de QEP) por uno que anota. Mismo
    /// mecanismo que <c>WithExportProcessors</c>: el real se saca primero.</summary>
    public static WebApplicationFactory<Program> WithWhatsAppSender(
        this WebApplicationFactory<Program> factory, IWhatsAppSender sender) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWhatsAppSender>();
            services.AddSingleton(sender);
        }));

    /// <summary>Reemplaza el HttpClient de Zenvia, que comparten la cuenta de QEP y las propias:
    /// lo que salga hacia Zenvia lo ve el handler de la prueba.</summary>
    public static WebApplicationFactory<Program> WithZenviaHandler(
        this WebApplicationFactory<Program> factory, HttpMessageHandler handler) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ZenviaHttpClient>();
            services.AddSingleton(new ZenviaHttpClient(new HttpClient(handler)));
        }));

    /// <summary>Un proveedor de logs más: LoggerFactory recibe todos los ILoggerProvider
    /// registrados, así que esto ve lo mismo que la consola.</summary>
    public static WebApplicationFactory<Program> WithCapturedLogs(
        this WebApplicationFactory<Program> factory, CapturedLogs logs) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<ILoggerProvider>(logs)));

    /// <summary>Pisa la llave activa y declara las que se pasen. Se aplica después del
    /// ConfigureWebHost del harness, así que gana.</summary>
    public static WebApplicationFactory<Program> WithSecretProtection(
        this WebApplicationFactory<Program> factory, string activeKeyId, params (string Id, string Value)[] keys) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Quotations:SecretProtection:ActiveKeyId", activeKeyId);
            foreach (var (id, value) in keys)
            {
                builder.UseSetting($"Quotations:SecretProtection:Keys:{id}", value);
            }
        });

    /// <summary>Lo mínimo que <c>Quotation.EnsureComplete</c> pide para enviar —un producto,
    /// vigencia y cuenta de cobro— sin mandarla todavía (a diferencia de CreateSentQuotationAsync).
    /// El cliente sembrado tiene teléfono: un envío por la cuenta propia no cae en
    /// recipient_missing.</summary>
    public static async Task<Guid> CreateSendableQuotationAsync(HttpClient client, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(
            client,
            tenantId,
            clientId,
            billingAccount: new QuotationBillingAccountRequest(
                billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency));
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        return quotation.Id;
    }

    public static Task<HttpResponseMessage> SendAsync(
        HttpClient client, Guid tenantId, Guid quotationId, object? body = null) =>
        body is null
            ? client.PostAsync($"{QuotationsUrl(tenantId)}/{quotationId}/send", null, TestContext.Current.CancellationToken)
            : client.PostAsJsonAsync($"{QuotationsUrl(tenantId)}/{quotationId}/send", body, TestContext.Current.CancellationToken);

    /// <summary>Mensaje y detalle de todas las fallas guardadas: lo que lee la pantalla de Log
    /// con <c>platform.request_log.read</c>.</summary>
    public static Task<string> RequestFailuresTextAsync(string connectionString) =>
        ScalarAsync<string>(
            connectionString,
            "SELECT coalesce(string_agg(message || ' ' || detail, ' '), '') FROM platform.request_failures");
```

y, al final del archivo, las tres clases:

```csharp
/// <summary>El sender de la cuenta de QEP, pero anotando. Propio de este proyecto: el de
/// QuotationsTestDoubles es internal de las unitarias.</summary>
internal sealed class RecordingIntegrationWhatsAppSender : IWhatsAppSender
{
    public ConcurrentQueue<WhatsAppQuotationMessage> Sent { get; } = new();

    public Task SendQuotationAsync(WhatsAppQuotationMessage message, CancellationToken cancellationToken)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>Zenvia de mentira: anota token y cuerpo de cada request y responde lo que la prueba
/// pida.</summary>
internal sealed class CapturingZenviaHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
{
    public ConcurrentQueue<(string? Token, string Json)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.TryGetValues("X-API-TOKEN", out var values) ? values.FirstOrDefault() : null;
        var json = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((token, json));
        return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
    }
}

/// <summary>Todo lo que se loguea, ya formateado y con la excepción entera (mensaje, internas y
/// pila): es lo mismo que guarda el log JSON de producción.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public string AllText => string.Join('\n', _entries);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
    }
}
```

- [ ] **Step 2: Escribir las pruebas**

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSendChannelApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using static Modules.Quotations.IntegrationTests.WhatsAppTestHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-10-07, «Pruebas → Integración», envío: Disabled marca enviada y lo dice sin llamar a
/// nadie (también en reenvío), Own manda con el token, el número y la plantilla del tenant, y Shared
/// —explícito o volviendo de Disabled— usa el sender global. Las pruebas de envío existentes, sin
/// fila, quedan como están (criterio 1).
/// </summary>
public sealed class WhatsAppSendChannelApiTests
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

    [Fact]
    public async Task WithWhatsAppDisabledTheQuotationIsSentAndTheResponseSaysNoWhatsAppLeft()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var sender = new RecordingIntegrationWhatsAppSender();
        using var host = factory.WithWhatsAppSender(sender);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SendPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SendPermissions);
        (await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"")).EnsureSuccessStatusCode();
        var quotationId = await CreateSendableQuotationAsync(client, tenantId);

        // Billing sin datos propios fallaría con WhatsApp; con Disabled el destinatario se ignora.
        var response = await SendAsync(client, tenantId, quotationId, new { recipient = "Billing" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("Sent", body.GetProperty("status").GetString());
        Assert.Equal("Disabled", body.GetProperty("whatsAppOutcome").GetString());
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ResendingWithWhatsAppDisabledSaysSoInTheResponseAndTheHistory()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var sender = new RecordingIntegrationWhatsAppSender();
        using var host = factory.WithWhatsAppSender(sender);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SendPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SendPermissions);
        var quotationId = await CreateSendableQuotationAsync(client, tenantId);
        (await SendAsync(client, tenantId, quotationId)).EnsureSuccessStatusCode();
        (await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"")).EnsureSuccessStatusCode();

        var response = await SendAsync(client, tenantId, quotationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Disabled", (await JsonAsync(response)).GetProperty("whatsAppOutcome").GetString());
        Assert.Single(sender.Sent);
        var history = await client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationsUrl(tenantId)}/{quotationId}/history", TestContext.Current.CancellationToken);
        Assert.NotNull(history);
        Assert.Contains(history.Items, item =>
            item.Details == "Marcada como reenviada sin WhatsApp: el envío por WhatsApp está desactivado para la empresa.");
    }

    [Fact]
    public async Task OwnSendsWithTheTenantTokenNumberAndTemplate()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var zenvia = new CapturingZenviaHandler(HttpStatusCode.OK, """{"id":"zid-1"}""");
        using var host = factory.WithZenviaHandler(zenvia);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SendPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SendPermissions);
        (await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        var quotationId = await CreateSendableQuotationAsync(client, tenantId);

        var response = await SendAsync(client, tenantId, quotationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Accepted", (await JsonAsync(response)).GetProperty("whatsAppOutcome").GetString());
        var (token, json) = Assert.Single(zenvia.Requests);
        Assert.Equal(SentinelApiKey, token);
        var payload = JsonDocument.Parse(json).RootElement;
        Assert.Equal(FromNumber, payload.GetProperty("from").GetString());
        Assert.Equal(TemplateId, payload.GetProperty("contents")[0].GetProperty("templateId").GetString());
    }

    [Fact]
    public async Task BackToSharedFromDisabledSendsThroughTheGlobalSender()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var sender = new RecordingIntegrationWhatsAppSender();
        using var host = factory.WithWhatsAppSender(sender);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SendPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SendPermissions);
        (await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"")).EnsureSuccessStatusCode();
        (await PutSettingsAsync(client, tenantId, new { mode = "Shared" }, "\"2\"")).EnsureSuccessStatusCode();
        var quotationId = await CreateSendableQuotationAsync(client, tenantId);

        var response = await SendAsync(client, tenantId, quotationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Accepted", (await JsonAsync(response)).GetProperty("whatsAppOutcome").GetString());
        Assert.Single(sender.Sent);
    }
}
```

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSecretLeakTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using static Modules.Quotations.IntegrationTests.WhatsAppTestHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Criterio 5 del spec 2026-10-07: la API key —y el cuerpo crudo de la respuesta de la cuenta
/// propia— no salen por ningún camino. Tres fallas que llevan una key centinela por adentro (un
/// 422 del validador, un 500 sin llave activa y un 401 de Zenvia en el envío), y después de cada
/// una: ni la respuesta, ni los logs capturados, ni platform.request_failures la contienen. La
/// traza de OTel no se lee: RecordException registra el mismo mensaje y la misma cadena de
/// excepciones que estos caminos.
/// </summary>
public sealed class WhatsAppSecretLeakTests
{
    private static async Task AssertNothingLeaksAsync(
        string connectionString, CapturedLogs logs, string responseBody, params string[] secrets)
    {
        var failures = await RequestFailuresTextAsync(connectionString);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, responseBody, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, logs.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, failures, StringComparison.Ordinal);
        }
    }

    private static Task<long> FailuresWithStatusAsync(string connectionString, int status) =>
        ScalarAsync<long>(connectionString, $"SELECT count(*) FROM platform.request_failures WHERE status_code = {status}");

    [Fact]
    public async Task AValidationFailureCarryingTheKeyDoesNotLeakIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SettingsPermissions);

        var response = await PutSettingsAsync(
            client,
            tenantId,
            new { mode = "Own", provider = "Zenvia", apiKey = SentinelApiKey, fromNumber = FromNumber, templateId = "no-es-un-guid" },
            "\"1\"");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(await FailuresWithStatusAsync(database.GetConnectionString(), 422) >= 1);
        await AssertNothingLeaksAsync(database.GetConnectionString(), logs, body, SentinelApiKey);
    }

    // Sin llave activa, Protect lanza: es el 500 que el spec acepta fuera de producción. Su mensaje
    // nombra la clave de configuración, no la key.
    [Fact]
    public async Task AServerErrorWhileProtectingTheKeyDoesNotLeakIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var logs = new CapturedLogs();
        using var host = factory.WithSecretProtection(string.Empty).WithCapturedLogs(logs);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SettingsPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SettingsPermissions);

        var response = await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(await FailuresWithStatusAsync(database.GetConnectionString(), 500) >= 1);
        await AssertNothingLeaksAsync(database.GetConnectionString(), logs, body, SentinelApiKey);
    }

    // Zenvia responde 401 con un cuerpo que, por si acaso, repite la key: ninguno de los dos sale.
    [Fact]
    public async Task ZenviaRejectingTheOwnCredentialsDoesNotLeakTheKeyNorItsBody()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var logs = new CapturedLogs();
        var zenviaBody = JsonSerializer.Serialize(new { message = SentinelZenviaBody, echo = SentinelApiKey });
        var zenvia = new CapturingZenviaHandler(HttpStatusCode.Unauthorized, zenviaBody);
        using var host = factory.WithZenviaHandler(zenvia).WithCapturedLogs(logs);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SendPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SendPermissions);
        (await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        var quotationId = await CreateSendableQuotationAsync(client, tenantId);

        var response = await SendAsync(client, tenantId, quotationId);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "quotation.whatsapp.credentials_rejected",
            JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.Equal(SentinelApiKey, Assert.Single(zenvia.Requests).Token);
        await AssertNothingLeaksAsync(
            database.GetConnectionString(), logs, body, SentinelApiKey, SentinelZenviaBody);
    }
}
```

- [ ] **Step 3: Correr y ver el RED de compilación, después el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~WhatsAppSendChannelApiTests|FullyQualifiedName~WhatsAppSecretLeakTests"
```

Primera corrida, antes de Step 1 (si lo hiciste en otro orden, salta): compilación fallida con `CS1061: 'QuotationsApiHarness.QepApiFactory' does not contain a definition for 'WithWhatsAppSender'`. Con Step 1 hecho: esperado todo `Superado`. Un rojo acá es un defecto de B2–B11 (ver la nota al principio de la tarea). En particular:

- `AServerErrorWhileProtectingTheKeyDoesNotLeakIt` con 200: el `UseSetting("…ActiveKeyId", "")` no pisó la activa; revisa que `WithSecretProtection` se aplique al host que atiende el `PUT` (`host`, no `factory`).
- Una fuga en `logs.AllText` desde una línea de `ZenviaWhatsAppSender`: el mensaje de `Tenant` está armando con el cuerpo; vuelve a B4.

- [ ] **Step 4: Regresión del criterio 1**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~QuotationSendVoidApiTests|FullyQualifiedName~OrderApiTests|FullyQualifiedName~DocumentNumberingApiTests"
```

Esperado: verdes sin haber editado ninguna de esas pruebas.

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSendChannelApiTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSecretLeakTests.cs; git commit -m "test(quotations): envío por canal de WhatsApp y la API key que no se fuga"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B13: `WhatsAppTokenRekeyWorker`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppTokenRekeyWorker.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs` (registro)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTokenRekeyWorkerTests.cs`

**Interfaces:**
- Consumes: `IWhatsAppSecretProtector` (B2), `QuotationsDbContext.WhatsAppSettings`, `ITenantWhatsAppSettingsRepository`, `IQuotationsUnitOfWork` (B3), `TenantWhatsAppSettings.Reprotect` (B1), `IClock`, harness de B11–B12 (`WithSecretProtection`, `WithCapturedLogs`, `CreateClientFor`, `PutSettingsAsync`, `OwnBody`).
- Produces: `internal sealed partial class WhatsAppTokenRekeyWorker : BackgroundService` con `public Task Completion { get; }`.

- [ ] **Step 1: Escribir la prueba que falla**

`tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTokenRekeyWorkerTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Quotations.Infrastructure.Whatsapp;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using static Modules.Quotations.IntegrationTests.WhatsAppTestHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-10-07, «Rotación», punto 3: en cada arranque el worker re-cifra con la activa toda
/// fila en otra llave, se salta la que no descifra, es idempotente y loguea sólo tenant id, key id
/// y conteos. Las afirmaciones negativas (algo NO cambió) van siempre después de esperar
/// <see cref="WhatsAppTokenRekeyWorker.Completion"/>: un sondeo no distingue "no cambió" de
/// "todavía no cambió".
/// </summary>
public sealed class WhatsAppTokenRekeyWorkerTests
{
    private static readonly string OldKey =
        Convert.ToBase64String(Enumerable.Range(100, 32).Select(index => (byte)index).ToArray());

    private static async Task AwaitRekeyAsync(WebApplicationFactory<Program> host)
    {
        var worker = host.Services.GetServices<IHostedService>().OfType<WhatsAppTokenRekeyWorker>().Single();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    private static Task<string> KeyIdAsync(string connectionString, Guid tenantId) =>
        ScalarAsync<string>(connectionString,
            $"SELECT api_token_key_id FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'");

    private static Task<long> VersionAsync(string connectionString, Guid tenantId) =>
        ScalarAsync<long>(connectionString,
            $"SELECT version FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{tenantId}'");

    [Fact]
    public async Task AStartWithANewActiveKeyReencryptsTheOldRowsSkipsTheDamagedOneAndIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);

        // Cuatro tenants: A y B en la llave vieja, C en la vieja y dañado, D ya en la activa.
        var tenants = new List<(Guid TenantId, Guid OwnerId)>();
        for (var index = 0; index < 4; index++)
        {
            var (tenantId, ownerId, client) = await RegisterTenantAsync(factory, SettingsPermissions);
            client.Dispose();
            tenants.Add((tenantId, ownerId));
        }

        var (a, b, c, d) = (tenants[0], tenants[1], tenants[2], tenants[3]);
        using (var oldHost = factory.WithSecretProtection("old", ("old", OldKey)))
        {
            await AwaitRekeyAsync(oldHost);
            foreach (var tenant in new[] { a, b, c })
            {
                using var client = CreateClientFor(oldHost, tenant.OwnerId, tenant.TenantId, SettingsPermissions);
                (await PutSettingsAsync(client, tenant.TenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
            }
        }

        using (var client = CreateClientFor(factory, d.OwnerId, d.TenantId, SettingsPermissions))
        {
            (await PutSettingsAsync(client, d.TenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        }

        await ExecuteAsync(
            connectionString,
            "UPDATE quotations.tenant_whatsapp_settings "
            + "SET api_token_ciphertext = set_byte(api_token_ciphertext, 20, get_byte(api_token_ciphertext, 20) # 255) "
            + $"WHERE tenant_id = '{c.TenantId}'");
        var damagedCiphertext = await ScalarAsync<byte[]>(connectionString,
            $"SELECT api_token_ciphertext FROM quotations.tenant_whatsapp_settings WHERE tenant_id = '{c.TenantId}'");
        var versionD = await VersionAsync(connectionString, d.TenantId);

        // Segundo arranque: "test" activa, "old" todavía declarada (fase b de la rotación).
        var logs = new CapturedLogs();
        using (var newHost = factory
                   .WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey))
                   .WithCapturedLogs(logs))
        {
            await AwaitRekeyAsync(newHost);

            Assert.Equal("test", await KeyIdAsync(connectionString, a.TenantId));
            Assert.Equal("test", await KeyIdAsync(connectionString, b.TenantId));
            Assert.Equal("old", await KeyIdAsync(connectionString, c.TenantId));
            Assert.Equal(versionD, await VersionAsync(connectionString, d.TenantId));

            using var client = CreateClientFor(newHost, a.OwnerId, a.TenantId, SettingsPermissions);
            var settings = await client.GetFromJsonAsync<JsonElement>(
                WhatsAppSettingsUrl(a.TenantId), TestContext.Current.CancellationToken);
            Assert.True(settings.GetProperty("apiKeyReadable").GetBoolean());
        }

        Assert.Contains(logs.Entries, entry => entry.Contains("rekey finished: 2 re-encrypted, 1 skipped", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry =>
            entry.Contains(c.TenantId.ToString(), StringComparison.Ordinal) && entry.Contains("old", StringComparison.Ordinal));
        Assert.DoesNotContain(SentinelApiKey, logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(damagedCiphertext), logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(damagedCiphertext), logs.AllText, StringComparison.OrdinalIgnoreCase);

        // Tercer arranque: idempotente. A y B no cambian de versión; C se vuelve a saltar.
        var versionA = await VersionAsync(connectionString, a.TenantId);
        var versionB = await VersionAsync(connectionString, b.TenantId);
        var thirdLogs = new CapturedLogs();
        using (var thirdHost = factory
                   .WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey))
                   .WithCapturedLogs(thirdLogs))
        {
            await AwaitRekeyAsync(thirdHost);
        }

        Assert.Equal(versionA, await VersionAsync(connectionString, a.TenantId));
        Assert.Equal(versionB, await VersionAsync(connectionString, b.TenantId));
        Assert.Contains(thirdLogs.Entries, entry => entry.Contains("rekey finished: 0 re-encrypted, 1 skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAnActiveKeyTheWorkerDoesNothingAndFinishes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var logs = new CapturedLogs();
        using var host = factory.WithSecretProtection(string.Empty).WithCapturedLogs(logs);

        await AwaitRekeyAsync(host);

        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("rekey finished", StringComparison.Ordinal));
    }
}
```

> `CreateClientFor(factory, …)` acepta el `QepApiFactory` base porque hereda de `WebApplicationFactory<Program>`.

- [ ] **Step 2: Correr y ver el RED**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~WhatsAppTokenRekeyWorkerTests"
```

Esperado: compilación fallida, `CS0246: The type or namespace name 'WhatsAppTokenRekeyWorker' could not be found`.

- [ ] **Step 3: Implementar**

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppTokenRekeyWorker.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;

namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>
/// Re-cifra con la llave activa, una vez por arranque, toda API key de WhatsApp guardada con otra
/// llave (spec 2026-10-07, «Rotación», punto 3; decisión 26). Sin él, una llave filtrada no salía
/// de circulación hasta que cada tenant guardara su configuración.
/// <list type="bullet">
/// <item>Idempotente: en el siguiente arranque esas filas ya no califican.</item>
/// <item>Una fila que no descifra, o que otro guardado cambió en el medio, se salta y se loguea con
/// tenant id y key id: nunca un valor ni un texto cifrado.</item>
/// <item>Cualquier otra falla se loguea y el worker termina: no tumba el host ni reintenta en
/// bucle.</item>
/// <item>No audita: no hay una persona detrás y no cambia ningún valor de la configuración.</item>
/// </list>
/// <see cref="Completion"/> se completa siempre, haya re-cifrado, saltado o fallado: las pruebas lo
/// esperan antes de afirmar que algo no cambió.
/// </summary>
internal sealed partial class WhatsAppTokenRekeyWorker(
    IServiceScopeFactory scopeFactory,
    IWhatsAppSecretProtector protector,
    ILogger<WhatsAppTokenRekeyWorker> logger) : BackgroundService
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "WhatsApp API key of tenant {TenantId} stored with key {KeyId} could not be decrypted; rekey skipped.")]
    private static partial void LogUnreadable(ILogger logger, Guid tenantId, string keyId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "WhatsApp API key of tenant {TenantId} changed while it was being re-encrypted from key {KeyId}; rekey skipped.")]
    private static partial void LogConflict(ILogger logger, Guid tenantId, string keyId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "rekey finished: {Reencrypted} re-encrypted, {Skipped} skipped")]
    private static partial void LogFinished(ILogger logger, int reencrypted, int skipped);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "WhatsApp API key rekey failed; it will run again on the next start.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // StartAsync corre ExecuteAsync hasta el primer await: sin esto, una base lenta
            // frenaría el arranque del host.
            await Task.Yield();
            await RekeyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // El host se apaga: no es una falla.
        }
        catch (Exception exception)
        {
            LogFailed(logger, exception);
        }
        finally
        {
            _completion.TrySetResult();
        }
    }

    private async Task RekeyAsync(CancellationToken cancellationToken)
    {
        var active = protector.ActiveKeyId;
        if (active is null)
        {
            return;
        }

        // Son pocas (una por tenant como mucho): se leen los ids de una vez y cada fila se procesa
        // en su propio scope, para que un conflicto no ensucie el DbContext de las demás.
        List<Guid> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
            pending = await dbContext.WhatsAppSettings
                .AsNoTracking()
                .Where(settings => settings.ApiToken != null && settings.ApiToken.KeyId != active)
                .Select(settings => settings.TenantId)
                .ToListAsync(cancellationToken);
        }

        var reencrypted = 0;
        var skipped = 0;
        foreach (var tenantId in pending)
        {
            if (await RekeyOneAsync(tenantId, active, cancellationToken))
            {
                reencrypted++;
            }
            else
            {
                skipped++;
            }
        }

        LogFinished(logger, reencrypted, skipped);
    }

    private async Task<bool> RekeyOneAsync(Guid tenantId, string active, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITenantWhatsAppSettingsRepository>();
        var settings = await repository.FindAsync(tenantId, cancellationToken);
        var current = settings?.ApiToken;
        if (settings is null || current is null ||
            string.Equals(current.KeyId, active, StringComparison.Ordinal))
        {
            // Otro guardado o el worker de otra réplica ya la dejó bien entre la lista y esta lectura.
            LogConflict(logger, tenantId, current?.KeyId ?? "(none)");
            return false;
        }

        if (!protector.TryUnprotect(tenantId, current, out var plaintext))
        {
            LogUnreadable(logger, tenantId, current.KeyId);
            return false;
        }

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        settings.Reprotect(protector.Protect(tenantId, plaintext!), clock.UtcNow);
        try
        {
            await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (RequestConcurrencyException)
        {
            // Alguien guardó la fila en el medio: el PUT ya la dejó bien o la deja el próximo arranque.
            LogConflict(logger, tenantId, current.KeyId);
            return false;
        }
    }
}
```

En `QuotationsInfrastructureExtensions.cs`, junto a `services.AddHostedService<QuotationExpirationWorker>();` (`:69`):

```csharp
        // Spec 2026-10-07: re-cifra al arrancar las API keys de WhatsApp guardadas con una llave
        // que ya no es la activa. Corre una vez por arranque, después de las migraciones
        // (Program.cs las aplica antes de RunAsync).
        services.AddHostedService<WhatsAppTokenRekeyWorker>();
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~WhatsAppTokenRekeyWorkerTests|FullyQualifiedName~WhatsAppSettingsApiTests"
```

Esperado: todo verde. Si `AwaitRekeyAsync` falla con `Sequence contains no elements`, el worker no quedó registrado como `IHostedService`; si da timeout, `Completion` no se completa en algún camino (revisa el `finally`). Si el log final no aparece, revisa el nivel mínimo de `Logging:LogLevel` en `appsettings.Development.json`: el mensaje es `Information`.

- [ ] **Step 5: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppTokenRekeyWorker.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTokenRekeyWorkerTests.cs; git commit -m "feat(quotations): re-cifrado de las API keys de WhatsApp al arrancar"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task B14: Operación (README, k8s) y verificación completa

**Files:**
- Modify: `README.md` (sección nueva después de «Plantilla de WhatsApp (Zenvia)», `:1483`, y su tabla `:1493`)
- Modify: `k8s/prod-secret.yaml` (después de `:42`)
- Modify: `k8s/prod-configMap.yaml` (después de `:89`)

- [ ] **Step 1: k8s**

`k8s/prod-secret.yaml`, después de `Quotations__WhatsApp__FromNumber`:

```yaml
  # Spec 2026-10-07: llave AES-256 (32 bytes en base64) que cifra la API key de WhatsApp de cada
  # tenant. El valor vive en la variable secreta QUOTATIONS_SECRET_PROTECTION_KEY_K1 del grupo
  # Backend-prod, con copia en la bóveda del owner. No se edita con kubectl: el pipeline vuelve a
  # aplicar este manifiesto en cada deploy.
  Quotations__SecretProtection__Keys__k1: "#{QUOTATIONS_SECRET_PROTECTION_KEY_K1}#"
```

`k8s/prod-configMap.yaml`, después de `Quotations__WhatsApp__TemplateId`:

```yaml
  # Spec 2026-10-07: id (no secreto) de la llave con la que se cifra. Rotar = dos despliegues: declarar
  # Keys__k2 con ésta todavía en k1, y después cambiar ésta a k2 (README § WhatsApp por tenant).
  Quotations__SecretProtection__ActiveKeyId: "k1"
```

- [ ] **Step 2: README**

Corregir la fila de la tabla de «Plantilla de WhatsApp (Zenvia)» (`:1493`): `documentUrl` → «URL pública de la copia que publica `IQuotationPdfStorage.PublishAsync` (`SendQuotation.cs`); Meta no puede bajar una prefirmada de R2».

Agregar la sección (en tuteo, sin ningún valor real):

````markdown
### WhatsApp por tenant

Cada tenant elige en Configuración por dónde salen sus cotizaciones (spec 2026-10-07):

| Modo | Qué pasa al enviar | `whatsAppOutcome` |
| --- | --- | --- |
| sin fila / `Shared` | la cuenta de QEP (`Quotations:WhatsApp:*`, o `LogWhatsAppSender` fuera de producción) | `Accepted` |
| `Own` | la cuenta de Zenvia del tenant: su token, su número, su plantilla | `Accepted` |
| `Disabled` | PDF y snapshots sí; ni copia pública ni mensaje. La cotización queda en `Sent` y la respuesta lo dice | `Disabled` |

La API key de la cuenta propia se guarda cifrada con AES-256-GCM en
`quotations.tenant_whatsapp_settings` (`nonce || ciphertext || tag`, AAD atado al tenant). La llave
vive **fuera** de la base, en `Quotations:SecretProtection`:

| Clave | Dónde vive en prod | Contenido |
| --- | --- | --- |
| `Quotations:SecretProtection:ActiveKeyId` | ConfigMap | id de llave (`^[a-z0-9]{1,32}$`); no es secreto |
| `Quotations:SecretProtection:Keys:<id>` | Secret, desde la variable secreta del grupo `Backend-prod` | 32 bytes en base64 |

En `Production` todo es obligatorio y se valida al arrancar; fuera de producción la sección puede
faltar (el `GET` funciona y un `PUT` en `Own` con key responde 500 nombrando la clave que falta).

#### Generar una llave en local

`dotnet user-secrets set` imprime la clave **y su valor**, y `list` imprime todos los valores: el
`set` va con `| Out-Null` y la verificación cuenta.

```powershell
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$key = [Convert]::ToBase64String($bytes)
dotnet user-secrets set "Quotations:SecretProtection:Keys:k1" $key --project src/Api | Out-Null
Remove-Variable key, bytes
dotnet user-secrets set "Quotations:SecretProtection:ActiveKeyId" "k1" --project src/Api | Out-Null
dotnet user-secrets list --project src/Api | Select-String -Pattern "SecretProtection:Keys:k1" | Measure-Object
```

El último comando tiene que dar `Count 1`.

#### Custodia de la llave de producción

- Fuente de verdad: la variable **secreta** `QUOTATIONS_SECRET_PROTECTION_KEY_K1` del grupo
  `Backend-prod` (Azure DevOps). Respaldo: una copia en la bóveda del owner. Perder las dos es
  perder todas las API keys guardadas: cada tenant en `Own` tendría que volver a pegar la suya.
- No se edita con `kubectl`: el pipeline vuelve a aplicar `k8s/prod-secret.yaml` en cada deploy.
- Para ver que existe, sin su valor: `kubectl --context contabo-prod -n <ns> describe secret <nombre>`.
  Nunca `get -o yaml`, `-o json` ni `custom-columns` sobre `.data`.

#### Rotación (dos despliegues)

Nunca se retira una llave a la que todavía apunta alguna fila.

1. **Declarar**: variable `QUOTATIONS_SECRET_PROTECTION_KEY_K2` en el grupo y línea
   `Quotations__SecretProtection__Keys__k2` en `prod-secret.yaml`, con `ActiveKeyId` todavía en `k1`.
   Desplegar.
2. **Activar**: `ActiveKeyId = k2` en el ConfigMap. Desplegar. `WhatsAppTokenRekeyWorker` re-cifra
   al arrancar cada fila en otra llave, y cualquier `PUT` que encuentre una rezagada la drena.
3. **Contar** (sólo ids y conteos):

   ```sql
   SELECT api_token_key_id, count(*) FROM quotations.tenant_whatsapp_settings GROUP BY 1;
   ```

   Si quedan filas en `k1`, el log del worker dice cuáles se saltó y por qué (tenant id y key id) y
   su línea final da `rekey finished: n re-encrypted, m skipped`. Con `m = 0` y filas en `k1`, es la
   ventana del rolling update: un reinicio lo resuelve.

   ```powershell
   kubectl --context contabo-prod -n <namespace> rollout restart deployment/<deployment>
   kubectl --context contabo-prod -n <namespace> rollout status deployment/<deployment>
   ```

4. **Retirar** `k1` (variable y línea del Secret) sólo cuando el conteo da 0 en `k1`. Si la llave se
   filtró: las mismas fases y, además, pedirles a los tenants en `Own` que roten su token en Zenvia.

#### Orden de despliegue

1. Variable secreta en `Backend-prod` (y su copia en la bóveda). Sin ella, el pod nuevo entra en
   crash-loop por `SecretProtectionOptionsValidator`.
2. Backend: código, migración `AddTenantWhatsAppSettings`, la línea del Secret y la del ConfigMap,
   en el mismo merge.
3. Frontend, después. Si sale antes, `GET .../whatsapp-channel` da 404 y el envío sigue como hoy.

**Rollback del backend:** la imagen vieja ignora la tabla, así que **todos** los tenants vuelven a
la cuenta de QEP, incluidos los que eligieron `Disabled`. Avisarles antes, o revertir sólo con la
tabla sin filas `Disabled`/`Own`.
````

- [ ] **Step 3: Suite completa y formato**

Bloque de guardia y:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build
```

Esperado: `restore` sin `NU1004` (no se tocó ningún paquete); build limpio; suite completa verde salvo las rojas previas anotadas en B0 **por nombre** (memoria: `OrderExportApiTests` tiene dos rojas previas del NIT). Cualquier roja nueva se investiga antes de seguir.

Formato sólo de los `.cs` tocados, filtrando el ruido previo del repo (`ENDOFLINE`, `CHARSET`):

```powershell
$files = @(git diff --name-only $base -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
$report = Join-Path $env:TEMP "qep-whatsapp-por-tenant-format"
Remove-Item -Recurse -Force $report -ErrorAction SilentlyContinue
dotnet format Backend.slnx --verify-no-changes --no-restore --include $files --report $report
$reportFile = Join-Path $report "format-report.json"
if (Test-Path $reportFile) {
    (Get-Content $reportFile -Raw | ConvertFrom-Json) | ForEach-Object {
        $document = $_
        $document.FileChanges | Where-Object { $_.DiagnosticId -notin @("ENDOFLINE", "CHARSET") } |
            ForEach-Object { "{0}:{1} {2} {3}" -f $document.FilePath, $_.LineNumber, $_.DiagnosticId, $_.FormatDescription }
    }
}
```

(`$base` es la rama de entitlements de B0; si abriste otra sesión, vuelve a fijarla.) Esperado: sin salida. Lo que salga en archivos de este plan se corrige y va en el commit de abajo.

- [ ] **Step 4: Commit**

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add README.md k8s/prod-secret.yaml k8s/prod-configMap.yaml; git commit -m "docs(quotations): operación del WhatsApp por tenant y su llave de cifrado"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

- [ ] **Step 5: Handoff del backend**

No se hace `push` ni merge (eso lo confirma el developer). El handoff lleva: commits (`git log --oneline $base..HEAD`), la salida RED/GREEN literal de cada tarea, el resultado de la suite completa con las rojas previas por nombre, las desviaciones (namespaces de entitlements, cualquier hallazgo nuevo) y el recordatorio de los pasos manuales del owner: crear `QUOTATIONS_SECRET_PROTECTION_KEY_K1` en `Backend-prod` **antes** del merge y guardar la copia en la bóveda.

---
# Parte 2 — Frontend (`qep-frontend`, rama `feature/whatsapp-por-tenant`)

Todo bloque de comandos de esta parte empieza con:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\whatsapp-por-tenant
```

Guard de commit de esta parte (mismo criterio que el backend):

```powershell
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add <rutas>; git commit -m "<mensaje>"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Antes de cada commit: `bun run lint` y `bun x prettier --check <archivos de la tarea>`; si prettier marca algo, `bun x prettier --write <esos archivos>` y volver a correr la prueba de la tarea.

La parte 2 puede empezar cuando la parte 1 llegó a B11 (el contrato HTTP ya existe), pero **se despliega después** del backend (B14, «Orden de despliegue»). Las pruebas del frontend no dependen del backend: todo va con `fetch` mockeado (`src/test/setup.ts`).

### Task F0: Preparación

**Files:** ninguno (no hay commit).

- [ ] **Step 1: Worktree desde la rama de entitlements del frontend**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend
git fetch origin
git branch -a --list "*modulos-por-tenant*"
$base = "feature/modulos-por-tenant"
git worktree add -b feature/whatsapp-por-tenant ..\qep-frontend-worktrees\whatsapp-por-tenant $base
Set-Location ..\qep-frontend-worktrees\whatsapp-por-tenant
git branch --show-current
bun install --frozen-lockfile
```

Si la rama de entitlements se llama distinto, búscala con `git log --all --oneline -S "useTenantModules" | Select-Object -First 5`.

- [ ] **Step 2: Verificar la precondición**

```powershell
rg -n "export function useTenantModules" src/features/auth/hooks/use-tenant-modules.ts
rg -n "isEnabled" src/routes/_authenticated/settings/index.tsx
rg -ln "use-tenant-modules" src/routes/_authenticated/settings
```

Esperado: el hook existe, y el container de settings ya lo usa (para `showOrdersExport`). Si falta, **detente**. Anota si `src/routes/_authenticated/settings/index.test.tsx` ya mockea `use-tenant-modules` o responde `/modules` en su `stubBackend`: F4 se adapta a lo que encuentres.

- [ ] **Step 3: Línea base**

```powershell
bun run test --run
bun run lint
```

Esperado: verde. Anota las rojas previas **por nombre** (memoria: el frontend no tiene CI de pruebas; se compara por nombre, no por archivo).

---

### Task F1: Tipos y servicio de la configuración de WhatsApp

**Files:**
- Create: `src/features/tenant-settings/types/whatsapp-settings.ts`
- Create: `src/features/tenant-settings/services/whatsapp-settings.api.ts`
- Test: `src/features/tenant-settings/services/whatsapp-settings.api.test.ts`

**Interfaces:**
- Consumes: `apiRequest`, `ApiError` (`@/lib/api-client`); `CONFLICT_MESSAGE` (`tenant-settings.api.ts`); `TenantSettingsStatus`.
- Produces:
  - tipos `WhatsAppMode`, `WhatsAppProvider`, `WhatsAppSettingsDto`, `UpdateWhatsAppSettingsInput`, `WhatsAppSettingsField`, `WhatsAppSettingsStatus`; funciones `hasUsableStoredApiKey(settings)`, `canResumeOwnAccount(settings)`
  - `whatsAppSettingsQueryKey(tenantId)` = `['whatsapp-settings', tenantId]`, `fetchWhatsAppSettings(tenantId)`, `updateWhatsAppSettings(tenantId, input)`, `whatsAppSettingsFieldErrors(error)`, `describeWhatsAppSettingsFailure(error)` → `{ fields, message, shouldReload }`, `isModuleNotEnabled(error)`

- [ ] **Step 1: Escribir la prueba que falla**

`src/features/tenant-settings/services/whatsapp-settings.api.test.ts`:

```ts
import { describe, expect, it, vi } from 'vitest'

import { CONFLICT_MESSAGE } from '@/features/tenant-settings/services/tenant-settings.api'
import {
  describeWhatsAppSettingsFailure,
  fetchWhatsAppSettings,
  isModuleNotEnabled,
  updateWhatsAppSettings,
  whatsAppSettingsFieldErrors,
  whatsAppSettingsQueryKey,
} from '@/features/tenant-settings/services/whatsapp-settings.api'
import type { WhatsAppSettingsDto } from '@/features/tenant-settings/types/whatsapp-settings'
import { ApiError } from '@/lib/api-client'

const TENANT = 't-1'
const URL = `/api/v1/tenants/${TENANT}/quotations/whatsapp-settings`

const SETTINGS: WhatsAppSettingsDto = {
  tenantId: TENANT,
  mode: 'Shared',
  provider: null,
  apiKeyConfigured: false,
  apiKeyUpdatedAt: null,
  apiKeyReadable: null,
  fromNumber: null,
  templateId: null,
  modes: ['Shared', 'Own', 'Disabled'],
  providers: ['Zenvia'],
  version: 1,
}

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function lastPut() {
  const [url, init] = vi.mocked(fetch).mock.calls.at(-1)!
  return {
    url: String(url),
    method: init?.method,
    ifMatch: new Headers(init?.headers).get('If-Match'),
    client: new Headers(init?.headers).get('X-Qep-Client'),
    body: JSON.parse(String(init?.body)) as Record<string, unknown>,
  }
}

describe('whatsapp-settings.api', () => {
  it('reads the settings of the tenant', async () => {
    vi.mocked(fetch).mockResolvedValue(json(200, SETTINGS))

    const settings = await fetchWhatsAppSettings(TENANT)

    expect(settings.mode).toBe('Shared')
    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(URL)
  })

  it('builds a tenant-scoped query key', () => {
    expect(whatsAppSettingsQueryKey(TENANT)).toEqual(['whatsapp-settings', TENANT])
  })

  // Spec 2026-10-07: sin reemplazo, el PUT no lleva apiKey y el backend conserva la guardada.
  it('saves the own account without apiKey when it is not being replaced', async () => {
    vi.mocked(fetch).mockResolvedValue(json(200, { ...SETTINGS, version: 3 }))

    await updateWhatsAppSettings(TENANT, {
      version: 2,
      mode: 'Own',
      provider: 'Zenvia',
      fromNumber: '573001234567',
      templateId: '9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f',
    })

    const put = lastPut()
    expect(put.url).toBe(URL)
    expect(put.method).toBe('PUT')
    expect(put.ifMatch).toBe('"2"')
    expect(put.client).toBe('web')
    expect(put.body).toEqual({
      mode: 'Own',
      provider: 'Zenvia',
      fromNumber: '573001234567',
      templateId: '9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f',
    })
  })

  it('sends the apiKey only when one was typed', async () => {
    vi.mocked(fetch).mockResolvedValue(json(200, SETTINGS))

    await updateWhatsAppSettings(TENANT, {
      version: 1,
      mode: 'Own',
      provider: 'Zenvia',
      apiKey: 'zenvia-token',
      fromNumber: '573001234567',
      templateId: '9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f',
    })

    expect(lastPut().body.apiKey).toBe('zenvia-token')
  })

  it.each(['Shared', 'Disabled'] as const)(
    'sends only the mode for %s, even if own fields are present',
    async (mode) => {
      vi.mocked(fetch).mockResolvedValue(json(200, SETTINGS))

      await updateWhatsAppSettings(TENANT, {
        version: 4,
        mode,
        provider: 'Zenvia',
        apiKey: 'no-debe-viajar',
        fromNumber: '573001234567',
        templateId: '9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f',
      })

      expect(lastPut().body).toEqual({ mode })
    },
  )

  it('maps a 422 to the fields of the form with the backend message', () => {
    const error = new ApiError(422, {
      code: 'validation.failed',
      errors: {
        ApiKey: ['Pega la API key de tu cuenta de Zenvia.'],
        TemplateId: ['Escribe el id de la plantilla aprobada.'],
      },
    })

    expect(whatsAppSettingsFieldErrors(error)).toEqual({
      apiKey: 'Pega la API key de tu cuenta de Zenvia.',
      templateId: 'Escribe el id de la plantilla aprobada.',
    })
    expect(describeWhatsAppSettingsFailure(error)).toEqual({
      fields: {
        apiKey: 'Pega la API key de tu cuenta de Zenvia.',
        templateId: 'Escribe el id de la plantilla aprobada.',
      },
      message: null,
      shouldReload: false,
    })
  })

  it('puts a 422 without a field it can mark in the footer', () => {
    const error = new ApiError(422, {
      code: 'validation.failed',
      errors: { Mode: ["'Mode' must be 'Shared', 'Own' or 'Disabled'."] },
    })

    expect(describeWhatsAppSettingsFailure(error)).toEqual({
      fields: {},
      message: 'No pudimos guardar la configuración de WhatsApp. Intenta de nuevo.',
      shouldReload: false,
    })
  })

  it('asks to reload on 412 and 428', () => {
    expect(describeWhatsAppSettingsFailure(new ApiError(412, { code: 'concurrency.conflict' }))).toEqual({
      fields: {},
      message: CONFLICT_MESSAGE,
      shouldReload: true,
    })
    expect(
      describeWhatsAppSettingsFailure(new ApiError(428, { code: 'precondition.if_match_required' })),
    ).toEqual({
      fields: {},
      message: 'No pudimos verificar la versión. Recarga e intenta de nuevo.',
      shouldReload: true,
    })
  })

  it('recognizes a tenant without quotations', () => {
    const error = new ApiError(403, { code: 'tenancy.module_not_enabled' })

    expect(isModuleNotEnabled(error)).toBe(true)
    expect(isModuleNotEnabled(new ApiError(403, { code: 'authorization.denied' }))).toBe(false)
    expect(describeWhatsAppSettingsFailure(error).message).toBe(
      'Tu plan no incluye cotizaciones, así que no hay envío por WhatsApp que configurar.',
    )
  })
})
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
bun run test --run src/features/tenant-settings/services/whatsapp-settings.api.test.ts
```

Esperado: falla al resolver el import, `Failed to resolve import "@/features/tenant-settings/services/whatsapp-settings.api"`.

- [ ] **Step 3: Implementar**

`src/features/tenant-settings/types/whatsapp-settings.ts`:

```ts
import type { TenantSettingsStatus } from '@/features/tenant-settings/types/tenant-settings'

/** Espeja `WhatsAppMode` del backend (spec 2026-10-07). Viaja por nombre. */
export type WhatsAppMode = 'Shared' | 'Own' | 'Disabled'

export type WhatsAppProvider = 'Zenvia'

/**
 * `GET/PUT .../quotations/whatsapp-settings`. La API key nunca viaja: sólo si está
 * configurada, cuándo se actualizó y si el backend la puede leer (`null` sin key). En
 * `Shared` y `Disabled` llegan igual el proveedor, el número y la plantilla guardados: son lo
 * que la pantalla muestra al volver a "Cuenta propia".
 */
export interface WhatsAppSettingsDto {
  tenantId: string
  mode: WhatsAppMode
  provider: WhatsAppProvider | null
  apiKeyConfigured: boolean
  apiKeyUpdatedAt: string | null
  apiKeyReadable: boolean | null
  fromNumber: string | null
  templateId: string | null
  /** Colecciones fijas y completas: el select no conoce el enum del backend. */
  modes: WhatsAppMode[]
  providers: WhatsAppProvider[]
  version: number
}

/** Lo que manda el `PUT`. `apiKey` ausente conserva la guardada; fuera de `Own` sólo viaja
 * `mode` (lo garantiza `updateWhatsAppSettings`). */
export interface UpdateWhatsAppSettingsInput {
  version: number
  mode: WhatsAppMode
  provider?: WhatsAppProvider
  apiKey?: string
  fromNumber?: string
  templateId?: string
}

export type WhatsAppSettingsField = 'provider' | 'apiKey' | 'fromNumber' | 'templateId'

export type WhatsAppSettingsStatus = TenantSettingsStatus

/** Hay key guardada y el backend la descifra: se puede volver a `Own` sin pegarla. */
export function hasUsableStoredApiKey(settings: WhatsAppSettingsDto): boolean {
  return settings.apiKeyConfigured && settings.apiKeyReadable === true
}

/** La cuenta propia está completa y legible: un `{ mode: 'Own' }` la reactiva sin 422. */
export function canResumeOwnAccount(settings: WhatsAppSettingsDto): boolean {
  return (
    settings.provider !== null &&
    settings.fromNumber !== null &&
    settings.templateId !== null &&
    hasUsableStoredApiKey(settings)
  )
}
```

`src/features/tenant-settings/services/whatsapp-settings.api.ts`:

```ts
import { CONFLICT_MESSAGE } from '@/features/tenant-settings/services/tenant-settings.api'
import type {
  UpdateWhatsAppSettingsInput,
  WhatsAppSettingsDto,
  WhatsAppSettingsField,
} from '@/features/tenant-settings/types/whatsapp-settings'
import { ApiError, apiRequest } from '@/lib/api-client'

// Keyed by tenant, como el resto de settings: la cuenta de WhatsApp de una organización pintada
// en otra es su número y su plantilla expuestos donde no corresponde.
export function whatsAppSettingsQueryKey(tenantId: string) {
  return ['whatsapp-settings', tenantId] as const
}

function settingsPath(tenantId: string): string {
  return `/api/v1/tenants/${tenantId}/quotations/whatsapp-settings`
}

/** Requiere `tenancy.settings.read` y la capacidad `quotations` (spec 2026-10-07). */
export function fetchWhatsAppSettings(tenantId: string): Promise<WhatsAppSettingsDto> {
  return apiRequest<WhatsAppSettingsDto>(settingsPath(tenantId))
}

/**
 * Fuera de `Own` sólo viaja `mode`: el backend conserva la cuenta propia guardada y la pantalla
 * no tiene por qué mandar campos que se ignoran. `apiKey` sólo si se escribió una: ausente
 * conserva la guardada. `version` en `If-Match`, entrecomillado; sin él es un 428.
 */
export function updateWhatsAppSettings(
  tenantId: string,
  input: UpdateWhatsAppSettingsInput,
): Promise<WhatsAppSettingsDto> {
  const body =
    input.mode === 'Own'
      ? {
          mode: input.mode,
          ...(input.provider ? { provider: input.provider } : {}),
          ...(input.apiKey ? { apiKey: input.apiKey } : {}),
          ...(input.fromNumber ? { fromNumber: input.fromNumber } : {}),
          ...(input.templateId ? { templateId: input.templateId } : {}),
        }
      : { mode: input.mode }

  return apiRequest<WhatsAppSettingsDto>(settingsPath(tenantId), {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
      'If-Match': `"${input.version}"`,
    },
    body: JSON.stringify(body),
  })
}

const MODULE_NOT_ENABLED_CODE = 'tenancy.module_not_enabled'

const GENERIC_MESSAGE = 'No pudimos guardar la configuración de WhatsApp. Intenta de nuevo.'

/** `PropertyName` del comando, tal cual lo agrupa `ApiExceptionHandler`. */
const FIELD_BY_BACKEND_NAME: Record<string, WhatsAppSettingsField> = {
  Provider: 'provider',
  ApiKey: 'apiKey',
  FromNumber: 'fromNumber',
  TemplateId: 'templateId',
}

/** Mensajes por campo de un 422; vacío cuando el error no apunta a un campo. Misma forma que
 * `taxRateFieldErrors` (`catalog.api.ts`): el texto del backend ya viene en español para estos
 * cuatro campos (spec 2026-10-07). */
export function whatsAppSettingsFieldErrors(
  error: unknown,
): Partial<Record<WhatsAppSettingsField, string>> {
  if (!(error instanceof ApiError) || error.status !== 422) return {}

  return Object.entries(error.errors ?? {}).reduce<
    Partial<Record<WhatsAppSettingsField, string>>
  >((accumulator, [name, messages]) => {
    const field = FIELD_BY_BACKEND_NAME[name]
    if (field && messages[0]) accumulator[field] = messages[0]
    return accumulator
  }, {})
}

export function isModuleNotEnabled(error: unknown): boolean {
  return (
    error instanceof ApiError && error.status === 403 && error.code === MODULE_NOT_ENABLED_CODE
  )
}

export interface WhatsAppSettingsFailure {
  fields: Partial<Record<WhatsAppSettingsField, string>>
  /** Para el pie. `null` cuando todo lo que llegó tiene un input donde pintarse. */
  message: string | null
  shouldReload: boolean
}

/** Traduce un fallo del `PUT` a algo accionable. Nunca el `detail` del backend. */
export function describeWhatsAppSettingsFailure(error: unknown): WhatsAppSettingsFailure {
  if (!(error instanceof ApiError)) {
    return { fields: {}, message: GENERIC_MESSAGE, shouldReload: false }
  }

  switch (error.status) {
    case 403:
      return {
        fields: {},
        message: isModuleNotEnabled(error)
          ? 'Tu plan no incluye cotizaciones, así que no hay envío por WhatsApp que configurar.'
          : 'No tienes permiso para editar esta configuración.',
        shouldReload: false,
      }
    case 412:
      return { fields: {}, message: CONFLICT_MESSAGE, shouldReload: true }
    case 428:
      return {
        fields: {},
        message: 'No pudimos verificar la versión. Recarga e intenta de nuevo.',
        shouldReload: true,
      }
    default: {
      const fields = whatsAppSettingsFieldErrors(error)
      return {
        fields,
        message: Object.keys(fields).length > 0 ? null : GENERIC_MESSAGE,
        shouldReload: false,
      }
    }
  }
}
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
bun run test --run src/features/tenant-settings/services/whatsapp-settings.api.test.ts
```

Esperado: todas en verde.

- [ ] **Step 5: Commit**

```powershell
bun run lint
bun x prettier --check src/features/tenant-settings/types/whatsapp-settings.ts src/features/tenant-settings/services/whatsapp-settings.api.ts src/features/tenant-settings/services/whatsapp-settings.api.test.ts
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/features/tenant-settings/types/whatsapp-settings.ts src/features/tenant-settings/services/whatsapp-settings.api.ts src/features/tenant-settings/services/whatsapp-settings.api.test.ts; git commit -m "feat(tenant-settings): servicio de la configuración de WhatsApp"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F2: Canal de WhatsApp y mensajes del envío en `features/quotes`

**Files:**
- Modify: `src/features/quotes/services/quotes.api.ts:286-312,552-567`
- Modify: `src/features/quotes/types/quote.ts:350-352`
- Test: `src/features/quotes/services/quotes.api.test.ts`

**Interfaces:**
- Produces: `WhatsAppChannelMode` (`'Shared' | 'Own' | 'Disabled'`), `WhatsAppChannel` (`{ enabled: boolean; mode: WhatsAppChannelMode }`), `whatsAppChannelQueryKey(tenantId)` = `['quotes', tenantId, 'whatsapp-channel']`, `fetchWhatsAppChannel(tenantId)` (lanza ante una forma inesperada), `OWN_WHATSAPP_ACCOUNT_HINT`, `quoteSendFailureHint(error, mode)`; `Quote.whatsAppOutcome?: 'Accepted' | 'Disabled' | null`. F3 y F7 los usan.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `quotes.api.test.ts`, sumar a los imports `fetchWhatsAppChannel`, `whatsAppChannelQueryKey`, `quoteSendFailureHint`, `OWN_WHATSAPP_ACCOUNT_HINT` (desde `@/features/quotes/services/quotes.api`, junto a los que ya importa) y agregar al final del archivo:

```ts
describe('WhatsApp channel (spec 2026-10-07)', () => {
  it('reads the channel of the tenant', async () => {
    vi.mocked(fetch).mockResolvedValue(jsonResponse({ enabled: false, mode: 'Disabled' }))

    const channel = await fetchWhatsAppChannel(TENANT_ID)

    expect(channel).toEqual({ enabled: false, mode: 'Disabled' })
    expect(vi.mocked(fetch).mock.calls.at(-1)?.[0]).toBe(`${BASE}/whatsapp-channel`)
  })

  it('builds a key under the quotes of the tenant', () => {
    expect(whatsAppChannelQueryKey(TENANT_ID)).toEqual(['quotes', TENANT_ID, 'whatsapp-channel'])
  })

  // Una respuesta con otra forma es un canal desconocido, no "desactivado".
  it.each([{}, { enabled: 'no', mode: 'Shared' }, { enabled: true, mode: 'Otro' }])(
    'rejects an unexpected payload %j',
    async (body) => {
      vi.mocked(fetch).mockResolvedValue(jsonResponse(body))

      await expect(fetchWhatsAppChannel(TENANT_ID)).rejects.toThrow()
    },
  )

  it.each([
    [
      'quotation.whatsapp.credentials_rejected',
      'La cuenta de WhatsApp de tu empresa rechazó las credenciales. Pide a un administrador que revise la API key en Configuración.',
    ],
    [
      'quotation.whatsapp.settings_unreadable',
      'No pudimos leer la configuración de WhatsApp de tu empresa. Pide a un administrador que la revise en Configuración.',
    ],
    [
      'quotation.whatsapp.send_failed',
      'No pudimos enviar la cotización por WhatsApp. Intenta de nuevo en un momento.',
    ],
  ])('explains %s', (code, message) => {
    expect(describeQuoteFailure(new ApiError(422, { code }))).toBe(message)
  })

  it('adds the own-account hint only for a send failure through the own account', () => {
    const sendFailed = new ApiError(422, { code: 'quotation.whatsapp.send_failed' })

    expect(quoteSendFailureHint(sendFailed, 'Own')).toBe(OWN_WHATSAPP_ACCOUNT_HINT)
    expect(quoteSendFailureHint(sendFailed, 'Shared')).toBeNull()
    expect(quoteSendFailureHint(sendFailed, null)).toBeNull()
    expect(
      quoteSendFailureHint(new ApiError(422, { code: 'quotation.whatsapp.credentials_rejected' }), 'Own'),
    ).toBeNull()
  })
})
```

(`describeQuoteFailure`, `ApiError`, `jsonResponse`, `TENANT_ID` y `BASE` ya existen en el archivo.)

- [ ] **Step 2: Correr y ver el RED**

```powershell
bun run test --run src/features/quotes/services/quotes.api.test.ts
```

Esperado: rojas las nuevas (`fetchWhatsAppChannel is not a function` / `does not provide an export named 'fetchWhatsAppChannel'`); las existentes, verdes.

- [ ] **Step 3: Implementar**

En `src/features/quotes/types/quote.ts`, antes de `items: QuoteItem[]` (`:351`):

```ts
  /**
   * Sólo en la respuesta de `POST .../send` (spec 2026-10-07): `'Disabled'` es que la cotización
   * quedó enviada sin que saliera ningún WhatsApp. La pantalla lo lee de acá y no del canal que
   * pidió antes de enviar: si alguien lo desactivó en el medio, manda la respuesta.
   */
  whatsAppOutcome?: 'Accepted' | 'Disabled' | null
```

En `quotes.api.ts`, después de `sendQuote` (`:312`):

```ts
/** Espeja `WhatsAppMode` del backend. */
export type WhatsAppChannelMode = 'Shared' | 'Own' | 'Disabled'

/** `GET .../quotations/whatsapp-channel` (spec 2026-10-07), con el permiso de enviar. */
export interface WhatsAppChannel {
  enabled: boolean
  mode: WhatsAppChannelMode
}

const WHATSAPP_CHANNEL_MODES: readonly string[] = ['Shared', 'Own', 'Disabled']

/** Bajo `['quotes', tenantId]` como el resto del feature. La configuración la invalida al
 * guardar (`use-whatsapp-settings.ts`). */
export function whatsAppChannelQueryKey(tenantId: string) {
  return ['quotes', tenantId, 'whatsapp-channel'] as const
}

/**
 * Lanza ante una respuesta con otra forma: para el flujo de envío, no poder leer el canal es
 * "desconocido" y sigue el camino de siempre (spec, «Cómo se hace explícito que no salió nada»,
 * punto 1). Tratar un cuerpo raro como `enabled: false` abriría la confirmación de "sin
 * WhatsApp" a quien sí lo tiene.
 */
export async function fetchWhatsAppChannel(tenantId: string): Promise<WhatsAppChannel> {
  const body = await apiRequest<Partial<WhatsAppChannel> | undefined>(
    quotationsPath(tenantId, '/whatsapp-channel'),
  )
  if (
    typeof body?.enabled !== 'boolean' ||
    typeof body.mode !== 'string' ||
    !WHATSAPP_CHANNEL_MODES.includes(body.mode)
  ) {
    throw new Error('Unexpected WhatsApp channel payload.')
  }
  return { enabled: body.enabled, mode: body.mode }
}

/** La pista de una falla de Zenvia por la cuenta propia (spec 2026-10-07, «Envío»). Con la de
 * QEP o el canal desconocido no se agrega: la plantilla de QEP no la arregla nadie del tenant. */
export const OWN_WHATSAPP_ACCOUNT_HINT =
  'Si sigue fallando, pide a un administrador que revise en Configuración el número emisor y la plantilla de la cuenta de Zenvia de tu empresa.'

export function quoteSendFailureHint(
  error: unknown,
  channelMode: WhatsAppChannelMode | null,
): string | null {
  return error instanceof ApiError &&
    error.code === 'quotation.whatsapp.send_failed' &&
    channelMode === 'Own'
    ? OWN_WHATSAPP_ACCOUNT_HINT
    : null
}
```

En `QUOTE_CODE_MESSAGES`, reemplazar las entradas de WhatsApp y del envío (`:557-566`) — tuteo en lo que se toca:

```ts
  'quotation.whatsapp.recipient_missing':
    'Este cliente no tiene un teléfono cargado. Agrégalo para poder enviarle la cotización por WhatsApp.',
  'quotation.whatsapp.send_failed':
    'No pudimos enviar la cotización por WhatsApp. Intenta de nuevo en un momento.',
  // Spec 2026-10-07: sólo con la cuenta propia del tenant (401/403 de Zenvia), que es lo único que
  // un administrador puede arreglar desde Configuración.
  'quotation.whatsapp.credentials_rejected':
    'La cuenta de WhatsApp de tu empresa rechazó las credenciales. Pide a un administrador que revise la API key en Configuración.',
  // La API key guardada ya no descifra (llave retirada o bytes dañados): el envío se cortó antes del PDF.
  'quotation.whatsapp.settings_unreadable':
    'No pudimos leer la configuración de WhatsApp de tu empresa. Pide a un administrador que la revise en Configuración.',
  // Lo que antes salía como `500 server.unexpected` y dejaba a la pantalla sin nada que decir:
  // un timeout contra el servicio de PDF, una caída de red, cualquier falla que nadie modeló.
  // El backend la registra entera antes de contestar, así que acá se puede mandar a mirarla en
  // vez de pedir que se reintente a ciegas. La cotización sigue en borrador.
  'quotation.send.failed':
    'No pudimos enviar la cotización y el error quedó registrado. Míralo en Reportes → Envíos fallidos, o reintenta en un momento.',
```

(El comentario de `:552-556` sobre `SendQuotation.cs` se conserva tal cual, arriba de `recipient_missing`.)

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
bun run test --run src/features/quotes/services/quotes.api.test.ts src/features/quotes/components/quote-send-failure-dialog.test.tsx
```

Esperado: todo verde (el diálogo de falla es la guarda de los textos que cambiaron).

- [ ] **Step 5: Commit**

```powershell
bun run lint
bun x prettier --check src/features/quotes/services/quotes.api.ts src/features/quotes/services/quotes.api.test.ts src/features/quotes/types/quote.ts
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/features/quotes/services/quotes.api.ts src/features/quotes/services/quotes.api.test.ts src/features/quotes/types/quote.ts; git commit -m "feat(quotes): canal de WhatsApp y mensajes de sus fallas"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F3: Hook `useWhatsAppSettings`

**Files:**
- Create: `src/features/tenant-settings/hooks/use-whatsapp-settings.ts`
- Test: `src/features/tenant-settings/hooks/use-whatsapp-settings.test.tsx`

**Interfaces:**
- Consumes: F1 (servicio), F2 (`whatsAppChannelQueryKey`), `useActiveTenant`.
- Produces: `useWhatsAppSettings({ enabled }: { enabled: boolean })` → `{ settings: WhatsAppSettingsDto | undefined; status: WhatsAppSettingsStatus; save: (input: UpdateWhatsAppSettingsInput) => Promise<WhatsAppSettingsDto>; isSaving: boolean; retry: () => void }`.

- [ ] **Step 1: Escribir la prueba que falla**

`src/features/tenant-settings/hooks/use-whatsapp-settings.test.tsx`:

```tsx
import type { ReactNode } from 'react'

import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { useWhatsAppSettings } from './use-whatsapp-settings'
import type { WhatsAppSettingsDto } from '@/features/tenant-settings/types/whatsapp-settings'

const TENANT = '019fb345-e753-71e2-bdb2-542df3cd8ab8'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({
  useActiveTenant: () => ({ tenantId: TENANT }),
}))

const SETTINGS: WhatsAppSettingsDto = {
  tenantId: TENANT,
  mode: 'Shared',
  provider: null,
  apiKeyConfigured: false,
  apiKeyUpdatedAt: null,
  apiKeyReadable: null,
  fromNumber: null,
  templateId: null,
  modes: ['Shared', 'Own', 'Disabled'],
  providers: ['Zenvia'],
  version: 1,
}

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function setup(enabled = true) {
  const queryClient = new QueryClient({
    defaultOptions: { mutations: { retry: false }, queries: { retry: false, retryDelay: 0 } },
  })
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  )
  const hook = renderHook(() => useWhatsAppSettings({ enabled }), { wrapper })
  return { queryClient, invalidate, hook }
}

describe('useWhatsAppSettings', () => {
  it('reads the settings of the active tenant', async () => {
    vi.mocked(fetch).mockResolvedValue(json(200, SETTINGS))

    const { hook } = setup()

    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    expect(hook.result.current.settings).toEqual(SETTINGS)
  })

  it('does not ask anything while the capability is off', async () => {
    const { hook } = setup(false)

    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(vi.mocked(fetch)).not.toHaveBeenCalled()
    expect(hook.result.current.status).toBe('loading')
  })

  it.each(['authorization.denied', 'tenancy.module_not_enabled'])(
    'reports denied on a 403 %s without retrying',
    async (code) => {
      vi.mocked(fetch).mockResolvedValue(json(403, { code }))

      const { hook } = setup()

      await waitFor(() => expect(hook.result.current.status).toBe('denied'))
      expect(vi.mocked(fetch)).toHaveBeenCalledTimes(1)
    },
  )

  // Review Focus 3: quien desactiva WhatsApp y envía en la misma pestaña tiene que ver el
  // canal nuevo, no el que quedó en caché.
  it('invalidates the cached WhatsApp channel after saving', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(json(200, SETTINGS))
    const { hook, queryClient, invalidate } = setup()
    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    const saved = { ...SETTINGS, mode: 'Disabled' as const, version: 2 }
    vi.mocked(fetch).mockResolvedValueOnce(json(200, saved))

    await hook.result.current.save({ version: 1, mode: 'Disabled' })

    expect(queryClient.getQueryData(['whatsapp-settings', TENANT])).toEqual(saved)
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['quotes', TENANT, 'whatsapp-channel'] })
  })

  it('reloads the settings after a 412', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(json(200, SETTINGS))
    const { hook, invalidate } = setup()
    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    vi.mocked(fetch).mockResolvedValueOnce(json(412, { code: 'concurrency.conflict' }))

    await expect(hook.result.current.save({ version: 1, mode: 'Disabled' })).rejects.toThrow()

    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['whatsapp-settings', TENANT] }),
    )
  })
})
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
bun run test --run src/features/tenant-settings/hooks/use-whatsapp-settings.test.tsx
```

Esperado: `Failed to resolve import "./use-whatsapp-settings"`.

- [ ] **Step 3: Implementar**

`src/features/tenant-settings/hooks/use-whatsapp-settings.ts`:

```ts
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'
import { whatsAppChannelQueryKey } from '@/features/quotes/services/quotes.api'
import {
  describeWhatsAppSettingsFailure,
  fetchWhatsAppSettings,
  updateWhatsAppSettings,
  whatsAppSettingsQueryKey,
} from '@/features/tenant-settings/services/whatsapp-settings.api'
import type {
  UpdateWhatsAppSettingsInput,
  WhatsAppSettingsStatus,
} from '@/features/tenant-settings/types/whatsapp-settings'
import { ApiError } from '@/lib/api-client'

/**
 * Lee y guarda la configuración de WhatsApp del tenant activo (spec 2026-10-07). Calco de
 * `useOrdersExportLayout`: key y versión propias, y la mutación escribe la caché en vez de
 * invalidar. `enabled` es la capacidad `quotations`: sin ella no se pregunta nada.
 *
 * `save` es `mutateAsync` porque el formulario necesita el error para marcar sus campos con
 * `setError` (React Hook Form), no sólo un estado.
 */
export function useWhatsAppSettings({ enabled }: { enabled: boolean }) {
  const { tenantId } = useActiveTenant()
  const queryClient = useQueryClient()
  const queryKey = whatsAppSettingsQueryKey(tenantId ?? 'none')

  const query = useQuery({
    queryKey,
    queryFn: () => fetchWhatsAppSettings(tenantId!),
    enabled: enabled && tenantId !== null,
    // Un 403 —sin permiso o sin la capacidad— es definitivo, no transitorio.
    retry: (failureCount, error) =>
      !(error instanceof ApiError && error.status === 403) && failureCount < 1,
  })

  const mutation = useMutation({
    mutationFn: (input: UpdateWhatsAppSettingsInput) => updateWhatsAppSettings(tenantId!, input),
    retry: false,
    // `cancelQueries` antes de escribir: un GET en vuelo que aterrice después dejaría la caché en
    // una versión que el servidor ya superó (memoria setquerydata-pisado-por-get-en-vuelo).
    onSuccess: async (saved) => {
      await queryClient.cancelQueries({ queryKey })
      queryClient.setQueryData(queryKey, saved)
      // El flujo de envío pide el canal con staleTime: sin esto, desactivar WhatsApp y enviar en
      // la misma pestaña mostraría el flujo de antes (Review Focus 3).
      void queryClient.invalidateQueries({ queryKey: whatsAppChannelQueryKey(tenantId!) })
    },
    onError: (error) => {
      if (describeWhatsAppSettingsFailure(error).shouldReload) {
        void queryClient.invalidateQueries({ queryKey })
      }
    },
  })

  const status: WhatsAppSettingsStatus =
    !enabled || tenantId === null || (query.isPending && !query.isError)
      ? 'loading'
      : query.error instanceof ApiError && query.error.status === 403
        ? 'denied'
        : query.isError
          ? 'error'
          : 'ready'

  return {
    settings: query.data,
    status,
    save: mutation.mutateAsync,
    isSaving: mutation.isPending,
    retry: () => void query.refetch(),
  }
}
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
bun run test --run src/features/tenant-settings/hooks/use-whatsapp-settings.test.tsx
```

Esperado: todas en verde.

- [ ] **Step 5: Commit**

```powershell
bun run lint
bun x prettier --check src/features/tenant-settings/hooks/use-whatsapp-settings.ts src/features/tenant-settings/hooks/use-whatsapp-settings.test.tsx
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/features/tenant-settings/hooks/use-whatsapp-settings.ts src/features/tenant-settings/hooks/use-whatsapp-settings.test.tsx; git commit -m "feat(tenant-settings): hook de la configuración de WhatsApp"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---
### Task F4: Sección «WhatsApp» (React Hook Form + zod)

**Files:**
- Create: `src/features/tenant-settings/types/whatsapp-settings.schema.ts`
- Create: `src/features/tenant-settings/components/whatsapp-settings-section.tsx`
- Test: `src/features/tenant-settings/components/whatsapp-settings-section.test.tsx`

**Interfaces:**
- Consumes: F1 (tipos, `hasUsableStoredApiKey`, `canResumeOwnAccount`, `describeWhatsAppSettingsFailure`), `SettingsSection`, `components/ui/{button,input,label,select,switch}`.
- Produces:
  - `whatsAppSettingsFormSchema({ storedKeyUsable })`, `whatsAppFormDefaults(settings)`, `toUpdateWhatsAppSettingsInput(values, settings)`, tipos `WhatsAppSettingsFormInput`/`WhatsAppSettingsFormValues`, `WHATSAPP_FORM_MESSAGES`
  - `WhatsAppSettingsSection({ settings, status, canManage, isSaving, onSave, onRetry })` y `CHANNEL_NOTICES`. Con `status === 'denied'` no dibuja nada. F5 la monta.

- [ ] **Step 1: Escribir la prueba que falla**

`src/features/tenant-settings/components/whatsapp-settings-section.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import {
  CHANNEL_NOTICES,
  WhatsAppSettingsSection,
  type WhatsAppSettingsSectionProps,
} from './whatsapp-settings-section'
import type { WhatsAppSettingsDto } from '@/features/tenant-settings/types/whatsapp-settings'
import { ApiError } from '@/lib/api-client'

const TEMPLATE = '9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f'

const SHARED: WhatsAppSettingsDto = {
  tenantId: 't-1',
  mode: 'Shared',
  provider: null,
  apiKeyConfigured: false,
  apiKeyUpdatedAt: null,
  apiKeyReadable: null,
  fromNumber: null,
  templateId: null,
  modes: ['Shared', 'Own', 'Disabled'],
  providers: ['Zenvia'],
  version: 1,
}

// 15:00 UTC: el 7 de octubre en cualquier huso de las pruebas.
const OWN: WhatsAppSettingsDto = {
  ...SHARED,
  mode: 'Own',
  provider: 'Zenvia',
  apiKeyConfigured: true,
  apiKeyUpdatedAt: '2026-10-07T15:00:00Z',
  apiKeyReadable: true,
  fromNumber: '573001234567',
  templateId: TEMPLATE,
  version: 2,
}

const OWN_UNREADABLE: WhatsAppSettingsDto = { ...OWN, apiKeyReadable: false }
const DISABLED_WITH_OWN: WhatsAppSettingsDto = { ...OWN, mode: 'Disabled', version: 3 }
const DISABLED_WITHOUT_OWN: WhatsAppSettingsDto = { ...SHARED, mode: 'Disabled', version: 2 }

function renderSection(
  settings: WhatsAppSettingsDto,
  overrides: Partial<WhatsAppSettingsSectionProps> = {},
) {
  const onSave = vi.fn().mockResolvedValue(settings)
  const onRetry = vi.fn()
  const props: WhatsAppSettingsSectionProps = {
    settings,
    status: 'ready',
    canManage: true,
    isSaving: false,
    onSave,
    onRetry,
    ...overrides,
  }
  const view = render(<WhatsAppSettingsSection {...props} />)
  return { ...view, props, onSave, onRetry, user: userEvent.setup() }
}

const accountSelect = () => screen.getByRole('combobox', { name: 'Cuenta desde la que se envía' })

async function chooseAccount(user: ReturnType<typeof userEvent.setup>, name: string) {
  await user.click(accountSelect())
  await user.click(await screen.findByRole('option', { name }))
}

describe('WhatsAppSettingsSection', () => {
  it.each([
    [SHARED, CHANNEL_NOTICES.Shared],
    [OWN, CHANNEL_NOTICES.Own],
    [OWN_UNREADABLE, CHANNEL_NOTICES.OwnUnreadable],
    [DISABLED_WITH_OWN, CHANNEL_NOTICES.Disabled],
  ])('tells which channel is in effect', (settings, notice) => {
    renderSection(settings)

    expect(screen.getByRole('status')).toHaveTextContent(notice)
  })

  it('uses the exact notice texts of the spec', () => {
    expect(CHANNEL_NOTICES).toEqual({
      Shared: 'Tus cotizaciones salen por la cuenta de WhatsApp de QEP.',
      Own: 'Tus cotizaciones salen por la cuenta de Zenvia de tu empresa.',
      OwnUnreadable:
        'Tus cotizaciones deberían salir por la cuenta de Zenvia de tu empresa, pero no están saliendo: vuelve a pegar la API key.',
      Disabled:
        'El envío por WhatsApp está desactivado: al enviar, la cotización se marca como enviada y el PDF lo compartes tú.',
    })
  })

  it('hides the account fields with the switch off', () => {
    renderSection(DISABLED_WITHOUT_OWN)

    expect(screen.getByRole('switch', { name: 'Enviar cotizaciones por WhatsApp' })).not.toBeChecked()
    expect(screen.queryByRole('combobox', { name: 'Cuenta desde la que se envía' })).toBeNull()
    expect(screen.queryByLabelText('Número emisor')).toBeNull()
  })

  it('shows the stored own account and hides it, without losing it, under the QEP account', async () => {
    const { user } = renderSection(OWN)

    expect(accountSelect()).toHaveTextContent('Cuenta propia')
    expect(screen.getByLabelText('Número emisor')).toHaveValue('573001234567')
    expect(screen.getByLabelText('Id de la plantilla')).toHaveValue(TEMPLATE)

    await chooseAccount(user, 'Cuenta de QEP (predeterminada)')

    expect(screen.queryByLabelText('Número emisor')).toBeNull()
    expect(
      screen.getByText('Tu cuenta propia queda guardada: puedes volver a ella cuando quieras.'),
    ).toBeInTheDocument()
  })

  it('proposes the own account when turning on from Disabled with everything stored and readable', async () => {
    const { user } = renderSection(DISABLED_WITH_OWN)

    await user.click(screen.getByRole('switch', { name: 'Enviar cotizaciones por WhatsApp' }))

    expect(accountSelect()).toHaveTextContent('Cuenta propia')
  })

  it.each([
    ['nothing stored', DISABLED_WITHOUT_OWN],
    ['an unreadable key', { ...DISABLED_WITH_OWN, apiKeyReadable: false }],
    ['no template', { ...DISABLED_WITH_OWN, templateId: null }],
  ])('proposes the QEP account when turning on from Disabled with %s', async (_, settings) => {
    const { user } = renderSection(settings)

    await user.click(screen.getByRole('switch', { name: 'Enviar cotizaciones por WhatsApp' }))

    expect(accountSelect()).toHaveTextContent('Cuenta de QEP (predeterminada)')
  })

  it('shows a configured key as a state with "Reemplazar" instead of an input', () => {
    renderSection(OWN)

    expect(screen.getByText(/API key configurada · actualizada el 7 de oct\.? de 2026/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reemplazar' })).toBeInTheDocument()
    expect(screen.queryByLabelText('API key')).toBeNull()
  })

  it('requires pasting the key again when it cannot be read', async () => {
    const { user, onSave } = renderSection(OWN_UNREADABLE)

    expect(
      screen.getByText(
        'La API key guardada ya no se puede leer. Vuelve a pegarla para seguir enviando con tu cuenta.',
      ),
    ).toBeInTheDocument()
    const number = screen.getByLabelText('Número emisor')
    await user.clear(number)
    await user.type(number, '573009998877')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Pega la API key de tu cuenta de Zenvia.')).toBeInTheDocument()
    expect(onSave).not.toHaveBeenCalled()
  })

  it('marks the key as required for a new own account', async () => {
    const { user, onSave } = renderSection(SHARED)

    await chooseAccount(user, 'Cuenta propia')
    await user.type(screen.getByLabelText('Número emisor'), '573001234567')
    await user.type(screen.getByLabelText('Id de la plantilla'), TEMPLATE)
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Pega la API key de tu cuenta de Zenvia.')).toBeInTheDocument()
    expect(onSave).not.toHaveBeenCalled()
  })

  it('sends the typed key and leaves the input empty after saving', async () => {
    const { user, onSave, rerender, props } = renderSection(OWN_UNREADABLE)

    await user.type(screen.getByLabelText('API key'), '  nueva-key-123  ')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(onSave).toHaveBeenCalledWith({
      version: 2,
      mode: 'Own',
      provider: 'Zenvia',
      apiKey: 'nueva-key-123',
      fromNumber: '573001234567',
      templateId: TEMPLATE,
    })
    rerender(<WhatsAppSettingsSection {...props} settings={{ ...OWN, version: 3 }} />)
    await user.click(screen.getByRole('button', { name: 'Reemplazar' }))
    expect(screen.getByLabelText('API key')).toHaveValue('')
  })

  // Review Focus 4: con la key ilegible, volver a la cuenta de QEP no exige pegarla.
  it('saves Shared without asking for an unreadable key', async () => {
    const { user, onSave } = renderSection(OWN_UNREADABLE)

    await chooseAccount(user, 'Cuenta de QEP (predeterminada)')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(onSave).toHaveBeenCalledWith({ version: 2, mode: 'Shared' })
  })

  // Review Focus 5: lo escrito en un reemplazo cancelado no se cuela en el PUT.
  it('drops a typed key when the replacement is cancelled', async () => {
    const { user, onSave } = renderSection(OWN)

    await user.click(screen.getByRole('button', { name: 'Reemplazar' }))
    await user.type(screen.getByLabelText('API key'), 'tecleada')
    await user.click(screen.getByRole('button', { name: 'Cancelar' }))
    const number = screen.getByLabelText('Número emisor')
    await user.clear(number)
    await user.type(number, '573009998877')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(onSave).toHaveBeenCalledTimes(1)
    expect(onSave.mock.calls[0]?.[0]).not.toHaveProperty('apiKey')
  })

  it('marks the field the backend rejected', async () => {
    const onSave = vi.fn().mockRejectedValue(
      new ApiError(422, {
        code: 'validation.failed',
        errors: { TemplateId: ['El id de la plantilla tiene la forma 9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f.'] },
      }),
    )
    const { user } = renderSection(OWN, { onSave })

    const number = screen.getByLabelText('Número emisor')
    await user.clear(number)
    await user.type(number, '573009998877')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText('El id de la plantilla tiene la forma 9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f.'),
    ).toBeInTheDocument()
  })

  it('is read-only without tenancy.settings.update', () => {
    renderSection(OWN, { canManage: false })

    expect(screen.getByRole('switch', { name: 'Enviar cotizaciones por WhatsApp' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Reemplazar' })).toBeNull()
    expect(screen.getByLabelText('Número emisor')).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Guardar' })).toBeNull()
  })

  it('renders nothing when denied', () => {
    const { container } = renderSection(SHARED, { status: 'denied' })

    expect(container).toBeEmptyDOMElement()
  })

  it('offers a retry when the settings could not be loaded', async () => {
    const { user, onRetry } = renderSection(SHARED, { status: 'error', settings: undefined })

    await user.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(onRetry).toHaveBeenCalledTimes(1)
  })
})
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
bun run test --run src/features/tenant-settings/components/whatsapp-settings-section.test.tsx
```

Esperado: `Failed to resolve import "./whatsapp-settings-section"`.

- [ ] **Step 3: Implementar el schema**

`src/features/tenant-settings/types/whatsapp-settings.schema.ts`:

```ts
import { z } from 'zod'

import {
  canResumeOwnAccount,
  type UpdateWhatsAppSettingsInput,
  type WhatsAppProvider,
  type WhatsAppSettingsDto,
} from '@/features/tenant-settings/types/whatsapp-settings'

/** Los mismos textos que el backend (validador y handler, spec 2026-10-07): la pantalla los
 * marca antes de mandar, y si igual llega un 422 dice lo mismo. */
export const WHATSAPP_FORM_MESSAGES = {
  providerRequired: 'Elige el proveedor de tu cuenta de WhatsApp.',
  apiKeyRequired: 'Pega la API key de tu cuenta de Zenvia.',
  apiKeyInvalid:
    'La API key sólo puede tener letras, números y símbolos, sin espacios: vuelve a copiarla de Zenvia.',
  fromNumberRequired: 'Escribe el número emisor de tu cuenta de Zenvia.',
  fromNumberInvalid: "Escribe el número con indicativo de país, sólo dígitos y sin '+' (entre 10 y 15).",
  templateIdRequired: 'Escribe el id de la plantilla aprobada.',
  templateIdInvalid: 'El id de la plantilla tiene la forma 9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f.',
} as const

// ASCII visible, sin espacios: el token viaja en un header (X-API-TOKEN).
const API_KEY_PATTERN = /^[\x21-\x7E]{1,512}$/
const FROM_NUMBER_PATTERN = /^[0-9]{10,15}$/
const GUID_PATTERN = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/

const formShape = z.object({
  /** El switch "Enviar cotizaciones por WhatsApp": apagado es `Disabled`. */
  enabled: z.boolean(),
  account: z.enum(['Shared', 'Own']),
  provider: z.string(),
  apiKey: z.string(),
  /** "Reemplazar" abierto sobre una key guardada y legible. */
  replacingApiKey: z.boolean(),
  fromNumber: z.string(),
  templateId: z.string(),
})

export type WhatsAppSettingsFormInput = z.input<typeof formShape>
export type WhatsAppSettingsFormValues = z.output<typeof formShape>

/**
 * Fuera de "Cuenta propia" no se valida nada: esos campos ni viajan. La key se exige cuando su
 * input está a la vista —sin key guardada, guardada pero ilegible, o "Reemplazar" abierto—.
 */
export function whatsAppSettingsFormSchema({ storedKeyUsable }: { storedKeyUsable: boolean }) {
  return formShape.superRefine((values, context) => {
    if (!values.enabled || values.account !== 'Own') return

    const issue = (path: keyof WhatsAppSettingsFormValues, message: string) =>
      context.addIssue({ code: 'custom', path: [path], message })

    if (!values.provider) issue('provider', WHATSAPP_FORM_MESSAGES.providerRequired)

    if (!storedKeyUsable || values.replacingApiKey) {
      const apiKey = values.apiKey.trim()
      if (!apiKey) issue('apiKey', WHATSAPP_FORM_MESSAGES.apiKeyRequired)
      else if (!API_KEY_PATTERN.test(apiKey)) issue('apiKey', WHATSAPP_FORM_MESSAGES.apiKeyInvalid)
    }

    const fromNumber = values.fromNumber.trim()
    if (!fromNumber) issue('fromNumber', WHATSAPP_FORM_MESSAGES.fromNumberRequired)
    else if (!FROM_NUMBER_PATTERN.test(fromNumber)) {
      issue('fromNumber', WHATSAPP_FORM_MESSAGES.fromNumberInvalid)
    }

    const templateId = values.templateId.trim()
    if (!templateId) issue('templateId', WHATSAPP_FORM_MESSAGES.templateIdRequired)
    else if (!GUID_PATTERN.test(templateId)) {
      issue('templateId', WHATSAPP_FORM_MESSAGES.templateIdInvalid)
    }
  })
}

/** Lo guardado, como lo muestra el formulario. Desde `Disabled` la cuenta propuesta es la propia
 * sólo si está completa y legible (spec, «Sección WhatsApp», punto 2). */
export function whatsAppFormDefaults(settings: WhatsAppSettingsDto): WhatsAppSettingsFormInput {
  return {
    enabled: settings.mode !== 'Disabled',
    account:
      settings.mode === 'Own' || (settings.mode === 'Disabled' && canResumeOwnAccount(settings))
        ? 'Own'
        : 'Shared',
    provider: settings.provider ?? settings.providers[0] ?? '',
    apiKey: '',
    replacingApiKey: false,
    fromNumber: settings.fromNumber ?? '',
    templateId: settings.templateId ?? '',
  }
}

/** Lo que viaja. Fuera de `Own`, sólo el modo; en `Own`, la key sólo si se escribió una. */
export function toUpdateWhatsAppSettingsInput(
  values: WhatsAppSettingsFormValues,
  settings: WhatsAppSettingsDto,
): UpdateWhatsAppSettingsInput {
  const mode = values.enabled ? values.account : 'Disabled'
  if (mode !== 'Own') return { version: settings.version, mode }

  const apiKey = values.apiKey.trim()
  return {
    version: settings.version,
    mode,
    provider: values.provider as WhatsAppProvider,
    fromNumber: values.fromNumber.trim(),
    templateId: values.templateId.trim(),
    ...(apiKey ? { apiKey } : {}),
  }
}
```

- [ ] **Step 4: Implementar el componente**

`src/features/tenant-settings/components/whatsapp-settings-section.tsx`:

```tsx
import { useId, useMemo, useState } from 'react'

import { zodResolver } from '@hookform/resolvers/zod'
import { Controller, useForm } from 'react-hook-form'

import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { SettingsSection } from '@/features/tenant-settings/components/settings-section'
import { describeWhatsAppSettingsFailure } from '@/features/tenant-settings/services/whatsapp-settings.api'
import {
  canResumeOwnAccount,
  hasUsableStoredApiKey,
  type UpdateWhatsAppSettingsInput,
  type WhatsAppSettingsDto,
  type WhatsAppSettingsField,
  type WhatsAppSettingsStatus,
} from '@/features/tenant-settings/types/whatsapp-settings'
import {
  toUpdateWhatsAppSettingsInput,
  whatsAppFormDefaults,
  whatsAppSettingsFormSchema,
  type WhatsAppSettingsFormInput,
  type WhatsAppSettingsFormValues,
} from '@/features/tenant-settings/types/whatsapp-settings.schema'

const TITLE = 'WhatsApp'
const DESCRIPTION = 'Cómo le llegan las cotizaciones a tus clientes.'

/** El canal en efecto, según lo guardado (spec 2026-10-07, «Sección WhatsApp», punto 1). */
export const CHANNEL_NOTICES = {
  Shared: 'Tus cotizaciones salen por la cuenta de WhatsApp de QEP.',
  Own: 'Tus cotizaciones salen por la cuenta de Zenvia de tu empresa.',
  OwnUnreadable:
    'Tus cotizaciones deberían salir por la cuenta de Zenvia de tu empresa, pero no están saliendo: vuelve a pegar la API key.',
  Disabled:
    'El envío por WhatsApp está desactivado: al enviar, la cotización se marca como enviada y el PDF lo compartes tú.',
} as const

const UPDATED_AT_FORMAT = new Intl.DateTimeFormat('es-CO', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
})

function channelNotice(settings: WhatsAppSettingsDto): string {
  if (settings.mode === 'Disabled') return CHANNEL_NOTICES.Disabled
  if (settings.mode === 'Own') {
    return settings.apiKeyReadable === false ? CHANNEL_NOTICES.OwnUnreadable : CHANNEL_NOTICES.Own
  }
  return CHANNEL_NOTICES.Shared
}

export interface WhatsAppSettingsSectionProps {
  settings: WhatsAppSettingsDto | undefined
  status: WhatsAppSettingsStatus
  /** `tenancy.settings.update`. Sin él, todo en lectura y sin barra de guardado. */
  canManage: boolean
  isSaving: boolean
  /** Rechaza con el error del `PUT`: el formulario lo necesita para marcar los campos. */
  onSave: (input: UpdateWhatsAppSettingsInput) => Promise<unknown>
  onRetry: () => void
}

/**
 * La sección "WhatsApp" de Configuración (spec 2026-10-07), con guardado y versión propios: no es
 * parte de `TenantSettingsForm`, que manda su propio `PUT /settings`. Vista sin hooks de datos: la
 * ruta le pasa lo que lee `useWhatsAppSettings`.
 *
 * El formulario se monta con `key={settings.version}`: después de guardar o de releer por un 412
 * vuelve a sembrarse con lo nuevo, y el input de la key queda vacío sin limpiar nada a mano. El
 * aviso del pie vive acá afuera para sobrevivir a ese remontaje.
 */
export function WhatsAppSettingsSection({
  settings,
  status,
  canManage,
  isSaving,
  onSave,
  onRetry,
}: WhatsAppSettingsSectionProps) {
  const [failureMessage, setFailureMessage] = useState<string | null>(null)

  // Sin permiso o sin la capacidad: la tarjeta no se muestra.
  if (status === 'denied') return null

  if (status === 'loading') {
    return (
      <SettingsSection title={TITLE} description={DESCRIPTION}>
        <p className="text-sm text-muted-foreground">Cargando…</p>
      </SettingsSection>
    )
  }

  if (status === 'error' || !settings) {
    return (
      <SettingsSection title={TITLE} description={DESCRIPTION}>
        <p role="alert" className="text-sm">
          No pudimos cargar la configuración de WhatsApp. Intenta de nuevo en un momento.
        </p>
        <Button type="button" variant="outline" onClick={onRetry}>
          Reintentar
        </Button>
      </SettingsSection>
    )
  }

  return (
    <SettingsSection title={TITLE} description={DESCRIPTION}>
      <p role="status" className="rounded-xl border border-border bg-muted px-4 py-3 text-sm">
        {channelNotice(settings)}
      </p>
      <WhatsAppSettingsForm
        key={settings.version}
        settings={settings}
        canManage={canManage}
        isSaving={isSaving}
        onSave={onSave}
        failureMessage={failureMessage}
        onFailure={setFailureMessage}
      />
    </SettingsSection>
  )
}

interface WhatsAppSettingsFormProps {
  settings: WhatsAppSettingsDto
  canManage: boolean
  isSaving: boolean
  onSave: (input: UpdateWhatsAppSettingsInput) => Promise<unknown>
  failureMessage: string | null
  onFailure: (message: string | null) => void
}

function WhatsAppSettingsForm({
  settings,
  canManage,
  isSaving,
  onSave,
  failureMessage,
  onFailure,
}: WhatsAppSettingsFormProps) {
  const switchId = useId()
  const accountId = useId()
  const providerId = useId()
  const apiKeyId = useId()
  const apiKeyErrorId = useId()
  const fromNumberId = useId()
  const fromNumberHelpId = useId()
  const templateId = useId()
  const templateHelpId = useId()

  const storedKeyUsable = hasUsableStoredApiKey(settings)
  const resolver = useMemo(
    () => zodResolver(whatsAppSettingsFormSchema({ storedKeyUsable })),
    [storedKeyUsable],
  )
  const {
    control,
    register,
    handleSubmit,
    watch,
    setValue,
    setError,
    reset,
    formState: { errors, isDirty },
  } = useForm<WhatsAppSettingsFormInput, unknown, WhatsAppSettingsFormValues>({
    resolver,
    defaultValues: whatsAppFormDefaults(settings),
  })

  const enabled = watch('enabled')
  const account = watch('account')
  const replacingApiKey = watch('replacingApiKey')
  const showOwnFields = enabled && account === 'Own'
  const showApiKeyInput = !storedKeyUsable || replacingApiKey
  const ownAccountStored = settings.apiKeyConfigured || settings.fromNumber !== null

  const onSubmit = async (values: WhatsAppSettingsFormValues) => {
    onFailure(null)
    try {
      await onSave(toUpdateWhatsAppSettingsInput(values, settings))
    } catch (error) {
      const failure = describeWhatsAppSettingsFailure(error)
      for (const [field, message] of Object.entries(failure.fields) as [
        WhatsAppSettingsField,
        string,
      ][]) {
        setError(field, { message })
      }
      onFailure(failure.message)
    }
  }

  return (
    <form className="space-y-5" noValidate onSubmit={handleSubmit(onSubmit)}>
      <div className="flex items-center justify-between gap-4">
        <Label htmlFor={switchId}>Enviar cotizaciones por WhatsApp</Label>
        <Controller
          control={control}
          name="enabled"
          render={({ field }) => (
            <Switch
              id={switchId}
              checked={field.value}
              disabled={!canManage}
              onCheckedChange={(checked) => {
                field.onChange(checked)
                // Encender desde Disabled propone la cuenta propia sólo si el PUT la aceptaría.
                if (checked && settings.mode === 'Disabled') {
                  setValue('account', canResumeOwnAccount(settings) ? 'Own' : 'Shared', {
                    shouldDirty: true,
                  })
                }
              }}
            />
          )}
        />
      </div>

      {enabled ? (
        <div className="space-y-2">
          <Label htmlFor={accountId}>Cuenta desde la que se envía</Label>
          <Controller
            control={control}
            name="account"
            render={({ field }) => (
              <Select value={field.value} onValueChange={field.onChange} disabled={!canManage}>
                <SelectTrigger id={accountId} className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Shared">Cuenta de QEP (predeterminada)</SelectItem>
                  <SelectItem value="Own">Cuenta propia</SelectItem>
                </SelectContent>
              </Select>
            )}
          />
          {account === 'Shared' && ownAccountStored ? (
            <p className="text-sm text-muted-foreground">
              Tu cuenta propia queda guardada: puedes volver a ella cuando quieras.
            </p>
          ) : null}
        </div>
      ) : null}

      {showOwnFields ? (
        <>
          <div className="space-y-2">
            <Label htmlFor={providerId}>Proveedor</Label>
            <Controller
              control={control}
              name="provider"
              render={({ field }) => (
                <Select value={field.value} onValueChange={field.onChange} disabled={!canManage}>
                  <SelectTrigger id={providerId} className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {settings.providers.map((provider) => (
                      <SelectItem key={provider} value={provider}>
                        {provider}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              )}
            />
            {errors.provider ? (
              <p role="alert" className="text-sm text-destructive">
                {errors.provider.message}
              </p>
            ) : null}
          </div>

          <div className="space-y-2">
            {settings.apiKeyConfigured && settings.apiKeyReadable === false ? (
              <p role="alert" className="text-sm text-destructive">
                La API key guardada ya no se puede leer. Vuelve a pegarla para seguir enviando con
                tu cuenta.
              </p>
            ) : null}
            {showApiKeyInput ? (
              <>
                <Label htmlFor={apiKeyId}>API key</Label>
                <div className="flex gap-2">
                  <Input
                    id={apiKeyId}
                    type="password"
                    autoComplete="off"
                    disabled={!canManage}
                    aria-invalid={errors.apiKey ? true : undefined}
                    aria-describedby={errors.apiKey ? apiKeyErrorId : undefined}
                    {...register('apiKey')}
                  />
                  {storedKeyUsable ? (
                    <Button
                      type="button"
                      variant="ghost"
                      onClick={() => {
                        setValue('apiKey', '', { shouldDirty: true })
                        setValue('replacingApiKey', false, { shouldDirty: true })
                      }}
                    >
                      Cancelar
                    </Button>
                  ) : null}
                </div>
              </>
            ) : (
              <div className="flex items-center justify-between gap-4">
                <p className="text-sm">
                  API key configurada · actualizada el{' '}
                  {settings.apiKeyUpdatedAt
                    ? UPDATED_AT_FORMAT.format(new Date(settings.apiKeyUpdatedAt))
                    : ''}
                </p>
                {canManage ? (
                  <Button
                    type="button"
                    variant="outline"
                    onClick={() => setValue('replacingApiKey', true, { shouldDirty: true })}
                  >
                    Reemplazar
                  </Button>
                ) : null}
              </div>
            )}
            {errors.apiKey ? (
              <p id={apiKeyErrorId} role="alert" className="text-sm text-destructive">
                {errors.apiKey.message}
              </p>
            ) : null}
          </div>

          <div className="space-y-2">
            <Label htmlFor={fromNumberId}>Número emisor</Label>
            <Input
              id={fromNumberId}
              inputMode="numeric"
              placeholder="573001234567"
              disabled={!canManage}
              aria-describedby={fromNumberHelpId}
              aria-invalid={errors.fromNumber ? true : undefined}
              {...register('fromNumber')}
            />
            <p id={fromNumberHelpId} className="text-sm text-muted-foreground">
              El número de WhatsApp registrado en Zenvia, con indicativo de país y sin '+'.
            </p>
            {errors.fromNumber ? (
              <p role="alert" className="text-sm text-destructive">
                {errors.fromNumber.message}
              </p>
            ) : null}
          </div>

          <div className="space-y-2">
            <Label htmlFor={templateId}>Id de la plantilla</Label>
            <Input
              id={templateId}
              disabled={!canManage}
              aria-describedby={templateHelpId}
              aria-invalid={errors.templateId ? true : undefined}
              {...register('templateId')}
            />
            <p id={templateHelpId} className="text-sm text-muted-foreground">
              La plantilla tiene que estar aprobada por Meta y usar exactamente estas variables:{' '}
              <code>fullname</code>, <code>order_number</code>, <code>total</code>,{' '}
              <code>valid_until</code>, y el PDF como documento del encabezado. Si usa otras, el
              envío falla sin que lo veamos al guardar.
            </p>
            {errors.templateId ? (
              <p role="alert" className="text-sm text-destructive">
                {errors.templateId.message}
              </p>
            ) : null}
          </div>
        </>
      ) : null}

      {failureMessage ? (
        <p role="alert" className="text-sm text-destructive">
          {failureMessage}
        </p>
      ) : null}

      {canManage && isDirty ? (
        <div className="flex justify-end gap-2">
          <Button type="button" variant="outline" disabled={isSaving} onClick={() => reset()}>
            Descartar
          </Button>
          <Button type="submit" disabled={isSaving}>
            {isSaving ? 'Guardando…' : 'Guardar'}
          </Button>
        </div>
      ) : null}
    </form>
  )
}
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
bun run test --run src/features/tenant-settings/components/whatsapp-settings-section.test.tsx
```

Esperado: todas en verde. Si la de la fecha falla, imprime `UPDATED_AT_FORMAT.format(new Date('2026-10-07T15:00:00Z'))` en la prueba: el ICU de Node puede dar «7 oct 2026» en vez de «7 de oct. de 2026»; ajusta la regex de la prueba a lo que dé, no el formato (el texto exacto del spec es un ejemplo de estado, no un contrato de bytes), y anótalo.

Si `user.click` sobre una opción de Radix Select no la elige en jsdom, revisa que `src/test/setup.ts` tenga los polyfills de pointer capture (`hasPointerCapture`, `scrollIntoView`): el informe de exploración dice que sí.

- [ ] **Step 6: Commit**

```powershell
bun run lint
bun x prettier --check src/features/tenant-settings/types/whatsapp-settings.schema.ts src/features/tenant-settings/components/whatsapp-settings-section.tsx src/features/tenant-settings/components/whatsapp-settings-section.test.tsx
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/features/tenant-settings/types/whatsapp-settings.schema.ts src/features/tenant-settings/components/whatsapp-settings-section.tsx src/features/tenant-settings/components/whatsapp-settings-section.test.tsx; git commit -m "feat(tenant-settings): sección de WhatsApp en Configuración"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F5: Montar la sección en Configuración

**Files:**
- Modify: `src/features/tenant-settings/pages/tenant-settings-page.tsx` (prop nueva y su lugar, entre `TenantSettingsForm` y «Excel de pedidos»)
- Modify: `src/routes/_authenticated/settings/index.tsx` (container)
- Test: `src/features/tenant-settings/pages/tenant-settings-page.test.tsx`
- Test: `src/routes/_authenticated/settings/index.test.tsx`

**Interfaces:**
- Consumes: F3 (`useWhatsAppSettings`), F4 (`WhatsAppSettingsSection`), `useTenantModules` (entitlements), `usePermissions`.
- Produces: `TenantSettingsPage` con `whatsAppSection?: ReactNode`.

- [ ] **Step 1: Escribir las pruebas que fallan**

En `tenant-settings-page.test.tsx`, agregar (usa `SETTINGS` e `idleLogo` del archivo):

```tsx
  // Spec 2026-10-07: la sección de WhatsApp va entre el formulario y "Excel de pedidos". La vista
  // la recibe armada: decidir si se monta (capacidad quotations) es del container.
  it('places the WhatsApp section between the form and the orders export section', () => {
    render(
      <TenantSettingsPage
        settings={SETTINGS}
        status="ready"
        canManage
        logo={idleLogo()}
        onSave={vi.fn()}
        whatsAppSection={<section aria-label="Sección de WhatsApp" />}
      />,
    )

    const whatsApp = screen.getByRole('region', { name: 'Sección de WhatsApp' })
    const ordersExport = screen.getByRole('heading', { name: 'Excel de pedidos' })
    const nameInput = screen.getByLabelText('Nombre')
    expect(nameInput.compareDocumentPosition(whatsApp) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(whatsApp.compareDocumentPosition(ordersExport) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('renders no WhatsApp section when the container does not pass one', () => {
    render(
      <TenantSettingsPage settings={SETTINGS} status="ready" canManage logo={idleLogo()} onSave={vi.fn()} />,
    )

    expect(screen.queryByRole('region', { name: 'Sección de WhatsApp' })).toBeNull()
  })
```

> Si el plan de entitlements ocultó «Excel de pedidos» detrás de una prop `showOrdersExport` sin default `true`, pásala en estos dos `render`.

En `src/routes/_authenticated/settings/index.test.tsx`:

1. Mock de módulos al principio del archivo (si F0 encontró uno del plan de entitlements, **extiende ése** en lugar de crear otro):

```tsx
const modules = vi.hoisted(() => ({ quotations: true }))

vi.mock('@/features/auth/hooks/use-tenant-modules', () => ({
  useTenantModules: () => ({
    status: 'ready',
    isEnabled: (key: string) => (key === 'quotations' ? modules.quotations : true),
    item: () => undefined,
  }),
}))
```

2. En `stubBackend`, antes del `return` de `/settings`, responder la configuración de WhatsApp:

```tsx
    if (url.endsWith('/quotations/whatsapp-settings'))
      return Promise.resolve(
        json(200, {
          tenantId: TENANT,
          mode: 'Shared',
          provider: null,
          apiKeyConfigured: false,
          apiKeyUpdatedAt: null,
          apiKeyReadable: null,
          fromNumber: null,
          templateId: null,
          modes: ['Shared', 'Own', 'Disabled'],
          providers: ['Zenvia'],
          version: 1,
        }),
      )
```

3. Las pruebas:

```tsx
  it('shows the WhatsApp section to a tenant with quotations', async () => {
    modules.quotations = true
    stubBackend()

    renderRoute('/settings')

    expect(await screen.findByRole('heading', { name: 'WhatsApp' })).toBeInTheDocument()
    expect(
      await screen.findByText('Tus cotizaciones salen por la cuenta de WhatsApp de QEP.'),
    ).toBeInTheDocument()
  })

  it('does not mount the WhatsApp section without quotations', async () => {
    modules.quotations = false
    stubBackend()

    renderRoute('/settings')

    expect(await screen.findByRole('heading', { name: 'Configuración' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'WhatsApp' })).toBeNull()
    expect(
      vi.mocked(fetch).mock.calls.some(([input]) => String(input).includes('whatsapp-settings')),
    ).toBe(false)
    modules.quotations = true
  })
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
bun run test --run src/features/tenant-settings/pages/tenant-settings-page.test.tsx src/routes/_authenticated/settings/index.test.tsx
```

Esperado: rojas las nuevas (`Unable to find role="region" and name "Sección de WhatsApp"`; `Unable to find role="heading" and name "WhatsApp"`). `tsc` del editor marca `whatsAppSection` como prop desconocida; vitest no tipa, así que el rojo es de comportamiento.

- [ ] **Step 3: Implementar**

En `tenant-settings-page.tsx`:

- `import type { ReactNode } from 'react'` al principio.
- En `TenantSettingsPageProps`:

```tsx
  /** La sección de WhatsApp ya armada (spec 2026-10-07), o nada: montarla depende de la
   * capacidad `quotations`, que resuelve el container. Tiene guardado y versión propios. */
  whatsAppSection?: ReactNode
```

- Desestructurarla en la firma (`whatsAppSection,`) y renderizarla justo después del cierre de `<TenantSettingsForm … />` (`:128`), antes del comentario de «Excel de pedidos»:

```tsx
      {whatsAppSection}
```

- Corregir de paso el comentario viejo de `:36`: `routes/_authenticated/settings.tsx` → `routes/_authenticated/settings/index.tsx`.

En `src/routes/_authenticated/settings/index.tsx`, sumar los imports:

```tsx
import { useWhatsAppSettings } from '@/features/tenant-settings/hooks/use-whatsapp-settings'
import { WhatsAppSettingsSection } from '@/features/tenant-settings/components/whatsapp-settings-section'
```

(`useTenantModules` ya está importado por entitlements; si no, `import { useTenantModules } from '@/features/auth/hooks/use-tenant-modules'`.) En `SettingsRoute`, después de `usePermissions()`:

```tsx
  // Spec 2026-10-07: sin la capacidad quotations no hay envío que configurar. Mientras los módulos
  // cargan, isEnabled da false y la sección espera; el resto de la página no.
  const { isEnabled } = useTenantModules()
  const showWhatsApp = isEnabled('quotations')
  const whatsApp = useWhatsAppSettings({ enabled: showWhatsApp })
```

(si entitlements ya desestructuró `isEnabled`, reúsalo) y pasarle a la página:

```tsx
      whatsAppSection={
        showWhatsApp ? (
          <WhatsAppSettingsSection
            settings={whatsApp.settings}
            status={whatsApp.status}
            canManage={can('tenancy.settings.update')}
            isSaving={whatsApp.isSaving}
            onSave={whatsApp.save}
            onRetry={whatsApp.retry}
          />
        ) : null
      }
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
bun run test --run src/features/tenant-settings src/routes/_authenticated/settings
```

Esperado: todo verde, incluidas las pruebas existentes de la página y de la ruta.

- [ ] **Step 5: Commit**

```powershell
bun run lint
bun x prettier --check src/features/tenant-settings/pages/tenant-settings-page.tsx src/features/tenant-settings/pages/tenant-settings-page.test.tsx src/routes/_authenticated/settings/index.tsx src/routes/_authenticated/settings/index.test.tsx
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/features/tenant-settings/pages/tenant-settings-page.tsx src/features/tenant-settings/pages/tenant-settings-page.test.tsx src/routes/_authenticated/settings/index.tsx src/routes/_authenticated/settings/index.test.tsx; git commit -m "feat(tenant-settings): montar la sección de WhatsApp según la capacidad de cotizaciones"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---
### Task F6: Diálogos del envío sin WhatsApp y la pista de cuenta propia

**Files:**
- Create: `src/features/quotes/components/quote-send-without-whatsapp-dialog.tsx`
- Test: `src/features/quotes/components/quote-send-without-whatsapp-dialog.test.tsx`
- Modify: `src/features/quotes/components/quote-send-failure-dialog.tsx:33-65`
- Test: `src/features/quotes/components/quote-send-failure-dialog.test.tsx`

**Interfaces:**
- Produces: `QuoteSendWithoutWhatsAppDialog({ isOpen, isResend, isSending?, onConfirm, onCancel })`; `QuoteSendFailureDialog` suma `hint?: string | null`. F7 los usa.

- [ ] **Step 1: Escribir las pruebas que fallan**

`src/features/quotes/components/quote-send-without-whatsapp-dialog.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { QuoteSendWithoutWhatsAppDialog } from './quote-send-without-whatsapp-dialog'

// Spec 2026-10-07, «Cómo se hace explícito que no salió nada», punto 1: textos exactos.
describe('QuoteSendWithoutWhatsAppDialog', () => {
  it('warns that a first send leaves no message and confirms with "Marcar como enviada"', async () => {
    const onConfirm = vi.fn()
    render(
      <QuoteSendWithoutWhatsAppDialog isOpen isResend={false} onConfirm={onConfirm} onCancel={vi.fn()} />,
    )

    expect(
      screen.getByText(
        'El envío por WhatsApp está desactivado para tu empresa. La cotización quedará marcada como enviada, pero el cliente no recibirá ningún mensaje: descarga el PDF y compártelo tú.',
      ),
    ).toBeInTheDocument()
    await userEvent.setup().click(screen.getByRole('button', { name: 'Marcar como enviada' }))
    expect(onConfirm).toHaveBeenCalledTimes(1)
  })

  it('uses the resend wording', () => {
    render(<QuoteSendWithoutWhatsAppDialog isOpen isResend onConfirm={vi.fn()} onCancel={vi.fn()} />)

    expect(
      screen.getByText(
        'El envío por WhatsApp está desactivado para tu empresa. La cotización quedará marcada como reenviada, pero el cliente no recibirá ningún mensaje: descarga el PDF y compártelo tú.',
      ),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Marcar como reenviada' })).toBeInTheDocument()
  })

  it('cancels', async () => {
    const onCancel = vi.fn()
    render(
      <QuoteSendWithoutWhatsAppDialog isOpen isResend={false} onConfirm={vi.fn()} onCancel={onCancel} />,
    )

    await userEvent.setup().click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(onCancel).toHaveBeenCalledTimes(1)
  })
})
```

En `quote-send-failure-dialog.test.tsx` (mantiene su mock de permisos y su router), agregar dos casos con el mismo `render` que ya usa el archivo para abrir el diálogo, pasando `hint`:

```tsx
  it('adds the hint it is given under the explanation', async () => {
    renderDialog(new ApiError(422, { code: 'quotation.whatsapp.send_failed' }), {
      hint: 'Si sigue fallando, pide a un administrador que revise en Configuración el número emisor y la plantilla de la cuenta de Zenvia de tu empresa.',
    })

    expect(
      await screen.findByText(
        'Si sigue fallando, pide a un administrador que revise en Configuración el número emisor y la plantilla de la cuenta de Zenvia de tu empresa.',
      ),
    ).toBeInTheDocument()
  })

  it('adds nothing without a hint', async () => {
    renderDialog(new ApiError(422, { code: 'quotation.whatsapp.send_failed' }))

    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    expect(screen.queryByText(/Si sigue fallando/)).toBeNull()
  })
```

> `renderDialog(error, props?)` es el nombre de referencia: si el archivo arma el diálogo con otro helper, usa ése y pásale `hint`. Si no hay helper, monta `<QuoteSendFailureDialog error={…} hint={…} onClose={vi.fn()} />` dentro del mismo router que usan los demás casos.

- [ ] **Step 2: Correr y ver el RED**

```powershell
bun run test --run src/features/quotes/components/quote-send-without-whatsapp-dialog.test.tsx src/features/quotes/components/quote-send-failure-dialog.test.tsx
```

Esperado: `Failed to resolve import "./quote-send-without-whatsapp-dialog"`, y roja `adds the hint it is given under the explanation`.

- [ ] **Step 3: Implementar**

`src/features/quotes/components/quote-send-without-whatsapp-dialog.tsx`:

```tsx
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'

/**
 * La confirmación de enviar con WhatsApp desactivado (spec 2026-10-07). Reemplaza la pregunta de
 * destinatario: no hay a quién mandarle nada. Existe porque un 200 con la cotización en "Enviada"
 * no se distingue de un envío real si nadie lo dice antes —la trampa del "envío fantasma"—.
 */
export function QuoteSendWithoutWhatsAppDialog({
  isOpen,
  isResend,
  isSending = false,
  onConfirm,
  onCancel,
}: {
  isOpen: boolean
  /** Reenvío: "reenviada" en el texto y en el botón, como el resto del flujo. */
  isResend: boolean
  isSending?: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  const verb = isResend ? 'reenviada' : 'enviada'
  const actionLabel = isResend ? 'Marcar como reenviada' : 'Marcar como enviada'
  const description = `El envío por WhatsApp está desactivado para tu empresa. La cotización quedará marcada como ${verb}, pero el cliente no recibirá ningún mensaje: descarga el PDF y compártelo tú.`

  return (
    <Dialog
      open={isOpen}
      onOpenChange={(open) => {
        if (!open && !isSending) onCancel()
      }}
    >
      <DialogContent showCloseButton={!isSending}>
        <DialogHeader>
          <DialogTitle>WhatsApp desactivado</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onCancel} disabled={isSending}>
            Cancelar
          </Button>
          <Button type="button" onClick={onConfirm} disabled={isSending}>
            {isSending ? `${actionLabel}…` : actionLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
```

En `quote-send-failure-dialog.tsx`:

- La firma suma la prop:

```tsx
export function QuoteSendFailureDialog({
  error,
  hint = null,
  onClose,
}: {
  /** El error de la mutación, o `null` cuando no hay ninguno que mostrar. */
  error: unknown | null
  /** Una línea más bajo la explicación (spec 2026-10-07): la pista de la cuenta propia de
   * WhatsApp. La decide el flujo, que sabe por qué canal se intentó. */
  hint?: string | null
  onClose: () => void
}) {
```

- Después de `</DialogHeader>` (`:60`):

```tsx
        {hint ? <p className="text-sm">{hint}</p> : null}
```

- Tuteo en el renglón de `:62-65`: «…así que puedes corregir lo que haga falta y volver a intentar.»

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
bun run test --run src/features/quotes/components/quote-send-without-whatsapp-dialog.test.tsx src/features/quotes/components/quote-send-failure-dialog.test.tsx
```

Esperado: todas en verde, incluida la existente que busca `/quedó en borrador/i`.

- [ ] **Step 5: Commit**

```powershell
bun run lint
bun x prettier --check src/features/quotes/components/quote-send-without-whatsapp-dialog.tsx src/features/quotes/components/quote-send-without-whatsapp-dialog.test.tsx src/features/quotes/components/quote-send-failure-dialog.tsx src/features/quotes/components/quote-send-failure-dialog.test.tsx
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/features/quotes/components/quote-send-without-whatsapp-dialog.tsx src/features/quotes/components/quote-send-without-whatsapp-dialog.test.tsx src/features/quotes/components/quote-send-failure-dialog.tsx src/features/quotes/components/quote-send-failure-dialog.test.tsx; git commit -m "feat(quotes): confirmación de envío sin WhatsApp y pista de cuenta propia"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F7: El flujo de envío pide el canal y avisa según `whatsAppOutcome`

**Files:**
- Modify: `src/features/quotes/hooks/use-quote-send-flow.tsx` (reescritura completa, abajo)
- Test: `src/features/quotes/hooks/use-quote-send-flow.test.tsx` (nuevo; hoy no existe)

**Interfaces:**
- Consumes: F2 (`fetchWhatsAppChannel`, `whatsAppChannelQueryKey`, `quoteSendFailureHint`, `WhatsAppChannel`, `WhatsAppChannelMode`, `Quote.whatsAppOutcome`), F6 (diálogos), `useDownloadQuotePdf`, `useSendQuote`, `useQuotesTenant`.
- Produces: `useQuoteSendFlow()` con la misma interfaz pública (`start`, `isSending`, `sendingQuoteId`, `dialogs`); constantes exportadas `DISABLED_SENT_MESSAGE`, `DISABLED_RESENT_MESSAGE`.

- [ ] **Step 1: Escribir la prueba que falla**

`src/features/quotes/hooks/use-quote-send-flow.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { toast } from 'sonner'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { useQuoteSendFlow } from './use-quote-send-flow'
import type { Quote } from '@/features/quotes/types/quote'

const TENANT = 'tenant-1'
const SEND_URL = `/api/v1/tenants/${TENANT}/quotations/q1/send`

vi.mock('@/features/quotes/hooks/use-quotes-tenant', () => ({
  useQuotesTenant: () => ({ tenantId: TENANT }),
}))

// El diálogo de falla lee permisos para ofrecer el enlace al log; acá no hace falta el enlace.
vi.mock('@/features/auth/hooks/use-permission', () => ({
  usePermissions: () => ({ can: () => false, status: 'ready', sessionLost: false }),
}))

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn() },
}))

// Dos teléfonos distintos: sin canal desactivado, el flujo preguntaría a quién.
const QUOTE = {
  id: 'q1',
  sentAt: null,
  client: { name: 'Ferretería El Tornillo', phone: '3001234567' },
  parties: [],
} as unknown as Quote

const QUOTE_TWO_PHONES = {
  ...QUOTE,
  parties: [{ role: 'Billing', name: 'Facturación', phone: '3009998877' }],
} as unknown as Quote

const SENT_QUOTE = { ...QUOTE, status: 'Sent', sentAt: '2026-10-07T15:00:00Z' } as unknown as Quote

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function mockBackend(
  options: { channel?: () => Promise<Response>; send?: () => Response } = {},
) {
  vi.mocked(fetch).mockImplementation(async (input) => {
    const url = String(input)
    if (url.endsWith('/whatsapp-channel')) {
      return options.channel ? options.channel() : json(200, { enabled: true, mode: 'Shared' })
    }
    if (url.endsWith('/send')) {
      return options.send ? options.send() : json(200, { ...SENT_QUOTE, whatsAppOutcome: 'Accepted' })
    }
    if (url.endsWith('/pdf')) {
      return json(200, { url: 'https://r2.test/q1.pdf', generatedAt: '2026-10-07T15:00:00Z' })
    }
    return json(404, {})
  })
}

function posts(): string[] {
  return vi
    .mocked(fetch)
    .mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === 'POST')
    .map(([input]) => String(input))
}

function Harness({ quote }: { quote: Quote }) {
  const flow = useQuoteSendFlow()
  return (
    <>
      <button type="button" onClick={() => flow.start(quote)}>
        Iniciar envío
      </button>
      {flow.dialogs}
    </>
  )
}

function renderFlow(quote: Quote = QUOTE) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  render(
    <QueryClientProvider client={queryClient}>
      <Harness quote={quote} />
    </QueryClientProvider>,
  )
  return userEvent.setup()
}

describe('useQuoteSendFlow and the WhatsApp channel (spec 2026-10-07)', () => {
  // Los vi.fn del mock de sonner viven todo el archivo: sin limpiar, el success de una prueba
  // anterior haría fallar el `not.toHaveBeenCalled` de la siguiente.
  beforeEach(() => {
    vi.mocked(toast.success).mockClear()
    vi.mocked(toast.warning).mockClear()
  })

  it('asks for the channel when starting and waits for it before deciding', async () => {
    let answer: (response: Response) => void = () => {}
    mockBackend({ channel: () => new Promise<Response>((resolve) => (answer = resolve)) })
    const user = renderFlow()

    await user.click(screen.getByRole('button', { name: 'Iniciar envío' }))
    await waitFor(() =>
      expect(vi.mocked(fetch).mock.calls.some(([input]) => String(input).endsWith('/whatsapp-channel'))).toBe(true),
    )
    expect(posts()).toEqual([])

    answer(json(200, { enabled: true, mode: 'Shared' }))

    await waitFor(() => expect(posts()).toEqual([SEND_URL]))
  })

  it('confirms instead of asking the recipient when WhatsApp is disabled', async () => {
    mockBackend({ channel: async () => json(200, { enabled: false, mode: 'Disabled' }) })
    const user = renderFlow(QUOTE_TWO_PHONES)

    await user.click(screen.getByRole('button', { name: 'Iniciar envío' }))

    expect(
      await screen.findByText(
        'El envío por WhatsApp está desactivado para tu empresa. La cotización quedará marcada como enviada, pero el cliente no recibirá ningún mensaje: descarga el PDF y compártelo tú.',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByText('¿A quién le mandamos la cotización?')).toBeNull()
    expect(posts()).toEqual([])

    await user.click(screen.getByRole('button', { name: 'Marcar como enviada' }))

    await waitFor(() => expect(posts()).toEqual([SEND_URL]))
    const sendCall = vi.mocked(fetch).mock.calls.find(([input]) => String(input) === SEND_URL)
    expect((sendCall?.[1] as RequestInit | undefined)?.body).toBeUndefined()
  })

  it('uses the resend wording when the quote was already sent', async () => {
    mockBackend({ channel: async () => json(200, { enabled: false, mode: 'Disabled' }) })
    const user = renderFlow(SENT_QUOTE)

    await user.click(screen.getByRole('button', { name: 'Iniciar envío' }))

    expect(await screen.findByRole('button', { name: 'Marcar como reenviada' })).toBeInTheDocument()
  })

  it('follows the usual path when the channel cannot be read', async () => {
    mockBackend({ channel: async () => json(404, {}) })
    const user = renderFlow()

    await user.click(screen.getByRole('button', { name: 'Iniciar envío' }))

    await waitFor(() => expect(posts()).toEqual([SEND_URL]))
    expect(screen.queryByRole('button', { name: 'Marcar como enviada' })).toBeNull()
  })

  it('warns with a download action when the response says WhatsApp was disabled', async () => {
    vi.spyOn(window, 'open').mockReturnValue(null)
    mockBackend({ send: () => json(200, { ...SENT_QUOTE, whatsAppOutcome: 'Disabled' }) })
    const user = renderFlow()

    await user.click(screen.getByRole('button', { name: 'Iniciar envío' }))

    await waitFor(() =>
      expect(toast.warning).toHaveBeenCalledWith(
        'Cotización marcada como enviada. No se envió WhatsApp: está desactivado para tu empresa.',
        expect.objectContaining({ action: expect.objectContaining({ label: 'Descargar PDF' }) }),
      ),
    )
    expect(toast.success).not.toHaveBeenCalled()

    const options = vi.mocked(toast.warning).mock.calls.at(-1)?.[1] as {
      action: { onClick: () => void }
    }
    options.action.onClick()
    await waitFor(() => expect(posts()).toContain(`/api/v1/tenants/${TENANT}/quotations/q1/pdf`))
  })

  it('warns with the resend wording on a resend without WhatsApp', async () => {
    mockBackend({ send: () => json(200, { ...SENT_QUOTE, whatsAppOutcome: 'Disabled' }) })
    const user = renderFlow(SENT_QUOTE)

    await user.click(screen.getByRole('button', { name: 'Iniciar envío' }))

    await waitFor(() =>
      expect(toast.warning).toHaveBeenCalledWith(
        'Cotización marcada como reenviada. No se envió WhatsApp: está desactivado para tu empresa.',
        expect.anything(),
      ),
    )
  })

  it('keeps the usual text when WhatsApp accepted the message', async () => {
    mockBackend()
    const user = renderFlow()

    await user.click(screen.getByRole('button', { name: 'Iniciar envío' }))

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Cotización enviada por WhatsApp.'))
  })

  it.each([
    ['the own account', { enabled: true, mode: 'Own' }, true],
    ['the QEP account', { enabled: true, mode: 'Shared' }, false],
  ])('adds the settings hint to a Zenvia failure only through %s', async (_, channel, withHint) => {
    mockBackend({
      channel: async () => json(200, channel),
      send: () => json(422, { code: 'quotation.whatsapp.send_failed' }),
    })
    const user = renderFlow()

    await user.click(screen.getByRole('button', { name: 'Iniciar envío' }))

    expect(await screen.findByText('No pudimos enviar la cotización')).toBeInTheDocument()
    expect(screen.queryByText(/Si sigue fallando, pide a un administrador/) !== null).toBe(withHint)
  })

  it('adds no hint when the channel is unknown', async () => {
    mockBackend({
      channel: async () => json(500, {}),
      send: () => json(422, { code: 'quotation.whatsapp.send_failed' }),
    })
    const user = renderFlow()

    await user.click(screen.getByRole('button', { name: 'Iniciar envío' }))

    expect(await screen.findByText('No pudimos enviar la cotización')).toBeInTheDocument()
    expect(screen.queryByText(/Si sigue fallando/)).toBeNull()
  })
})
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
bun run test --run src/features/quotes/hooks/use-quote-send-flow.test.tsx
```

Esperado: rojas las que dependen del canal (p. ej. `confirms instead of asking the recipient…`: `Unable to find an element with the text: El envío por WhatsApp está desactivado…`; `asks for the channel…`: el `waitFor` del `/whatsapp-channel` vence). `keeps the usual text…` puede pasar ya: es el comportamiento de hoy.

- [ ] **Step 3: Implementar**

`src/features/quotes/hooks/use-quote-send-flow.tsx` completo:

```tsx
import { useState, type ReactNode } from 'react'

import { useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'

import { QuoteSendFailureDialog } from '@/features/quotes/components/quote-send-failure-dialog'
import { QuoteSendRecipientDialog } from '@/features/quotes/components/quote-send-recipient-dialog'
import { QuoteSendWithoutWhatsAppDialog } from '@/features/quotes/components/quote-send-without-whatsapp-dialog'
import { useDownloadQuotePdf } from '@/features/quotes/hooks/use-download-quote-pdf'
import { quoteDetailQueryKey } from '@/features/quotes/hooks/use-quote'
import { useQuotesTenant } from '@/features/quotes/hooks/use-quotes-tenant'
import { useSendQuote } from '@/features/quotes/hooks/use-send-quote'
import {
  fetchQuote,
  fetchWhatsAppChannel,
  quoteSendFailureHint,
  whatsAppChannelQueryKey,
  type QuoteRecipient,
  type WhatsAppChannel,
  type WhatsAppChannelMode,
} from '@/features/quotes/services/quotes.api'
import type { Quote } from '@/features/quotes/types/quote'
import { quoteSendRecipients } from '@/features/quotes/utils/quote-send-recipients'

/** Spec 2026-10-07, «Cómo se hace explícito que no salió nada», punto 3. */
export const DISABLED_SENT_MESSAGE =
  'Cotización marcada como enviada. No se envió WhatsApp: está desactivado para tu empresa.'
export const DISABLED_RESENT_MESSAGE =
  'Cotización marcada como reenviada. No se envió WhatsApp: está desactivado para tu empresa.'

/** Corto a propósito: el canal cambia poco, pero quien lo cambia en otra pestaña tiene que verlo
 * en el siguiente envío. En la misma pestaña, guardar la configuración lo invalida. */
const CHANNEL_STALE_TIME_MS = 10_000

export interface QuoteSendFlow {
  /**
   * Arranca el envío: pide el canal de WhatsApp, y con él desactivado confirma; si no, pregunta el
   * destinatario si hay dos, o manda derecho si hay uno solo.
   *
   * Acepta la cotización entera —la pantalla del detalle ya la tiene— o sólo su id, que es todo
   * lo que hay en una fila del listado.
   */
  start: (quote: Quote | string) => void
  isSending: boolean
  /** Qué cotización se está mandando, para que la fila que la disparó lo diga. */
  sendingQuoteId: string | null
  /** Las modales del flujo, para ubicar en la pantalla que lo dispara. */
  dialogs: ReactNode
}

/**
 * Enviar una cotización de punta a punta: saber por dónde sale, preguntar a quién, mandar,
 * avisar, y explicar el fallo cuando lo hay.
 *
 * Es un hook y no piezas sueltas porque el envío se ofrece desde dos lugares —el detalle y el
 * menú de cada fila del listado— y las modales comparten estado con lo que las abre.
 *
 * El canal (spec 2026-10-07) se pide con `fetchQuery` **dentro** de `start()`, igual que la
 * cotización desde el listado: una consulta montada aparte correría en paralelo con el clic y, si
 * no hubiera llegado, el flujo decidiría sin saberlo. Si no se puede leer, el canal es
 * desconocido y el flujo sigue el de siempre: la respuesta del envío (`whatsAppOutcome`) es la
 * que decide qué se le dice a la persona.
 */
export function useQuoteSendFlow(): QuoteSendFlow {
  const { tenantId } = useQuotesTenant()
  const queryClient = useQueryClient()
  // El error del envío se guarda en vez de leerse de `sendQuote.isError` porque la modal se
  // cierra: derivado del estado de la mutación volvería a abrirse en cada render hasta resetearla.
  const [sendFailure, setSendFailure] = useState<unknown | null>(null)
  // La cotización que se está por mandar. Con dos teléfonos posibles sobrevive a la pregunta:
  // la modal confirma con un destinatario, no con la cotización.
  const [target, setTarget] = useState<Quote | null>(null)
  // La que espera la confirmación de "sin WhatsApp".
  const [withoutWhatsAppTarget, setWithoutWhatsAppTarget] = useState<Quote | null>(null)
  // Por qué canal se intentó: decide la pista de la modal de falla.
  const [channelMode, setChannelMode] = useState<WhatsAppChannelMode | null>(null)
  const [resolvingQuoteId, setResolvingQuoteId] = useState<string | null>(null)
  const sendQuote = useSendQuote()
  const downloadPdf = useDownloadQuotePdf()

  const send = (quote: Quote, recipient?: QuoteRecipient) => {
    // El verbo se decide acá, antes de mutar: al volver, la cotización ya tiene `sentAt` nuevo
    // y un reenvío sería indistinguible de un primer envío.
    const isResend = quote.sentAt !== null
    setSendFailure(null)
    sendQuote.mutate(
      { quoteId: quote.id, recipient },
      {
        onSuccess: (sent) => {
          setTarget(null)
          setWithoutWhatsAppTarget(null)
          // La respuesta manda: si alguien desactivó WhatsApp entre el diálogo y la confirmación,
          // el canal que se leyó antes ya no es cierto.
          if (sent.whatsAppOutcome === 'Disabled') {
            toast.warning(isResend ? DISABLED_RESENT_MESSAGE : DISABLED_SENT_MESSAGE, {
              action: { label: 'Descargar PDF', onClick: () => downloadPdf.mutate(quote.id) },
            })
            return
          }
          toast.success(
            isResend ? 'Cotización reenviada por WhatsApp.' : 'Cotización enviada por WhatsApp.',
          )
        },
        onError: (error) => {
          setTarget(null)
          setWithoutWhatsAppTarget(null)
          setSendFailure(error)
        },
      },
    )
  }

  const readChannel = async (): Promise<WhatsAppChannel | null> => {
    try {
      return await queryClient.fetchQuery({
        queryKey: whatsAppChannelQueryKey(tenantId!),
        queryFn: () => fetchWhatsAppChannel(tenantId!),
        staleTime: CHANNEL_STALE_TIME_MS,
      })
    } catch {
      // Red caída, 404 de un backend anterior o una respuesta con otra forma: canal desconocido.
      return null
    }
  }

  const start = async (quote: Quote | string) => {
    const quoteId = typeof quote === 'string' ? quote : quote.id
    let resolved = typeof quote === 'string' ? null : quote
    let channel: WhatsAppChannel | null = null

    setResolvingQuoteId(quoteId)
    try {
      if (!resolved) {
        try {
          resolved = await queryClient.fetchQuery({
            queryKey: quoteDetailQueryKey(tenantId!, quoteId),
            queryFn: () => fetchQuote(tenantId!, quoteId),
          })
        } catch (error) {
          // No poder traerla es no poder mandarla: se cuenta en la misma modal, con el mismo
          // enlace al log, en vez de dejar el clic sin respuesta.
          setSendFailure(error)
          return
        }
      }
      channel = await readChannel()
    } finally {
      setResolvingQuoteId(null)
    }

    // TypeScript no lo estrecha solo: `resolved` se reasigna adentro del `try`.
    if (!resolved) return

    setChannelMode(channel?.mode ?? null)
    if (channel && !channel.enabled) {
      setWithoutWhatsAppTarget(resolved)
      return
    }

    setTarget(resolved)
    // Con datos propios de facturación y teléfono propio hay dos destinatarios posibles: se
    // pregunta antes de mandar, en vez de decidir por la persona. Sin segundo teléfono no se
    // pregunta nada y se manda al cliente, que es lo que el backend hace sin destinatario.
    if (quoteSendRecipients(resolved)) return
    send(resolved)
  }

  const recipientOptions = target ? quoteSendRecipients(target) : null
  // Nada que mostrar, nada montado: la modal del fallo lee los permisos de quien mira, y esa
  // consulta no la tiene que pagar una pantalla en la que nadie envió nada.
  const hasDialogs =
    sendFailure != null ||
    withoutWhatsAppTarget != null ||
    (target != null && recipientOptions != null)

  return {
    start: (quote) => void start(quote),
    isSending: sendQuote.isPending || resolvingQuoteId !== null,
    sendingQuoteId:
      resolvingQuoteId ??
      (sendQuote.isPending ? (sendQuote.variables?.quoteId ?? null) : null),
    dialogs: !hasDialogs ? null : (
      <>
        <QuoteSendFailureDialog
          error={sendFailure}
          hint={quoteSendFailureHint(sendFailure, channelMode)}
          onClose={() => setSendFailure(null)}
        />
        {withoutWhatsAppTarget ? (
          <QuoteSendWithoutWhatsAppDialog
            isOpen
            isResend={withoutWhatsAppTarget.sentAt !== null}
            isSending={sendQuote.isPending}
            onConfirm={() => send(withoutWhatsAppTarget)}
            onCancel={() => setWithoutWhatsAppTarget(null)}
          />
        ) : null}
        {target && recipientOptions ? (
          <QuoteSendRecipientDialog
            isOpen
            onOpenChange={(open) => {
              if (!open) setTarget(null)
            }}
            options={recipientOptions}
            onConfirm={(recipient) => send(target, recipient)}
            isSending={sendQuote.isPending}
            actionLabel={target.sentAt ? 'Reenviar' : 'Enviar'}
          />
        ) : null}
      </>
    ),
  }
}
```

- [ ] **Step 4: Correr y ver el GREEN, y las pruebas que ya usaban el flujo**

```powershell
bun run test --run src/features/quotes/hooks/use-quote-send-flow.test.tsx src/features/quotes/pages/quotes-list-page.test.tsx "src/routes/_authenticated/quotes"
```

Esperado: todo verde. Las pruebas existentes del listado y del editor siguen verdes porque su mock responde `json(200, {})` o una página al `GET /whatsapp-channel`, y `fetchWhatsAppChannel` lo rechaza como forma inesperada (canal desconocido, flujo de siempre; decisión 8). Si alguna cuenta llamadas a `fetch` y ahora ve una más, ajústala sumando la del canal y anótalo: es el único cambio de comportamiento visible para esas pruebas.

- [ ] **Step 5: Commit**

```powershell
bun run lint
bun x prettier --check src/features/quotes/hooks/use-quote-send-flow.tsx src/features/quotes/hooks/use-quote-send-flow.test.tsx
if ((git branch --show-current) -ne "feature/whatsapp-por-tenant") { throw "ABORT: rama equivocada" }; git add src/features/quotes/hooks/use-quote-send-flow.tsx src/features/quotes/hooks/use-quote-send-flow.test.tsx; git commit -m "feat(quotes): el envío lee el canal de WhatsApp y dice si no salió mensaje"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

---

### Task F8: Verificación completa del frontend

**Files:** ninguno nuevo.

- [ ] **Step 1: Suite, lint, tipos y build**

```powershell
bun run test --run
bun run lint
bun run build
bun x prettier --check src/features/tenant-settings src/features/quotes src/routes/_authenticated/settings
```

Esperado: suite verde salvo las rojas previas de F0 **por nombre**; oxlint sin errores; `tsc -b` y `vite build` sin errores (acá aparece cualquier prop o tipo que vitest no tipó, p. ej. `whatsAppSection` o `hint`); prettier sin diferencias en lo tocado.

- [ ] **Step 2: Handoff del frontend**

Sin `push` (memoria `gh-cli-sin-sesion`: se empuja la rama y se entrega la URL compare sólo cuando el developer lo confirme). El handoff lleva: `git log --oneline $base..HEAD`, RED/GREEN literales por tarea, resultado de F8, desviaciones (helpers de prueba existentes que hubo que adaptar, formato de fecha de ICU) y el recordatorio del orden: este frontend sale **después** del backend.

---

## Self-review (hecho al escribir el plan)

**Cobertura del spec:**

| Sección del spec | Tarea |
| --- | --- |
| Reglas de resolución (sin fila/Shared/Own/Disabled) | B5, B10, B12 |
| Cómo se hace explícito que no salió nada (4 lugares) | B9/B11 (canal), B10 (respuesta e historial), F6–F7 (diálogo y aviso) |
| Manejo del secreto: AES-GCM, formato, AAD, opciones, validador, custodia, rotación, drenaje | B2, B8 (drenaje), B13 (worker), B14 (docs/k8s) |
| Sólo escritura y nunca en un log (ToString, mensajes, Zenvia sin cuerpo, GET sin valor) | B4, B7, B9, B11, B12 |
| Modelo de datos y migración | B1, B3 |
| Domain (`Configure`, `Reprotect`, `CreateEmpty`) | B1 |
| Application (puertos, casos de uso, gate, auditoría, DTO) | B2, B3, B5, B7–B9 |
| Validador | B7 |
| Infrastructure (repositorio, UoW 412, protector, worker, sender, `ZenviaHttpClient`, resolver) | B2–B5, B13 |
| Envío (`Channel`, Disabled, `SendFailed`, `SendQuotationResult`, `WhatsAppOutcome`) | B6, B10 |
| Endpoints y códigos de error | B11 |
| Validación al arrancar | B2 |
| Despliegue y operación, README | B14 |
| Frontend: sección, envío, archivos | F1–F7 |
| Pruebas unitarias, de integración y de frontend que lista el spec | cada tarea dueña; `QuotationsOptionsValidatorTests` y `ConfigurationExampleTests` en B2 |

**DECISIÓN-PENDIENTE del spec** (no se implementan, quedan para el owner): número emisor legible con `tenancy.settings.read`; «Probar envío»; validar contra Zenvia al guardar; futuro de `Shared`; otros proveedores; notificar `credentials_rejected` repetido.

**Tipos y nombres cruzados verificados:** `WhatsAppChannel(Mode, Sender)`, `IWhatsAppChannelResolver.ResolveAsync`, `ITenantWhatsAppSettingsRepository.{FindAsync,FindReadOnlyAsync,Add}`, `UpdateWhatsAppSettingsHandler.{ModeChangedAction,ApiKeyReplacedAction,UpdatedAction}`, `QuotationChangeSummary.SendFailed(stage, whatsAppSkipped)`, `SendQuotationResult(Quotation, WhatsApp)`, `ZenviaSenderSettings(ApiToken, FromNumber, TemplateId, BaseUrl, Account)`, `whatsAppChannelQueryKey` (F2, usado en F3 y F7), `quoteSendFailureHint` (F2, usado en F7), `WhatsAppSettingsSection` props (F4, usadas en F5).

## Ejecución

El owner delegó la ejecución; este plan no arranca nada. Recomendación: **subagent-driven** (superpowers:subagent-driven-development), una tarea por subagente con revisión entre tareas: son 24 tareas encadenadas por interfaces (B1→B13, F1→F7) y un error en el cifrado o en la fuga de la key cuesta caro de deshacer en producción. Backend y frontend pueden correr en paralelo desde B11 (memoria `sdd-subagentes-ritmo-rapido`), cada uno en su worktree, sin escritores paralelos dentro del mismo repo.
