# Integraciones (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un módulo `Integrations` dueño de las conexiones de cada tenant con plataformas externas: catálogo en código filtrado por módulos activos, conexiones con nombre (varias por proveedor), credenciales cifradas de sólo escritura que se prueban contra el proveedor antes de guardarse, pausar/reanudar/probar/eliminar con `If-Match`, permisos propios, auditoría atómica, eventos de outbox, rotación de llave y dos puertos para consumidores futuros.

**Architecture:** Módulo nuevo de cuatro capas (`Domain` → `Application` → `Infrastructure` → `Api`) con su `IntegrationsDbContext` en el esquema `integrations`. El protector AES-256-GCM, sus opciones, su validador, el worker de re-cifrado y sus pruebas **se mueven** desde lo que `0f61d8c` retiró de Quotations (recuperados de `6612298`) y se adaptan: AAD por conexión y campo, sección `Integrations:SecretProtection`. La auditoría va por un puerto propio atado a `IntegrationsDbContext` (no por el `IAuditRecorder` compartido, que ya liga Tenancy) y los eventos por una proyección de `platform.outbox_messages`, como Pos. Bootstrapper cablea permisos, políticas, el rol `admin`, los handlers y el adaptador del nombre del autor.

**Tech Stack:** .NET 10 (SDK `10.0.400`, `global.json`), EF Core + Npgsql, FluentValidation 12, `IHttpClientFactory` (del shared framework `Microsoft.AspNetCore.App`, sin paquetes nuevos), xUnit v3, Testcontainers (`postgres:18-alpine`).

**Spec:** `docs/superpowers/specs/2026-10-08-integraciones-design.md` (autoridad). Este plan cubre **sólo backend**: «Modelo», «Backend», «Nunca en un log», «Despliegue» y las pruebas de backend. La fase 0 («Lo que se le quita a Quotations…») ya está hecha en `0f61d8c`. El frontend lo planea otra persona contra el contrato HTTP del spec: rutas, forma JSON de los DTO y códigos de error **no se cambian** sin cambiar el spec. Quien ejecuta lee el spec y este plan; también `HANDOFF-integraciones.md` (carpeta contenedora), secciones «Fase 1» y «Reglas que aplican».

---

## Antes de empezar (leer una vez)

- **Rutas.** Todo comando usa rutas absolutas. Abajo:
  - `$Main` = `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend` (checkout principal, en `develop`; **tiene cambios sin commitear de otra persona** en `Tenancy/…/Tenant.cs` y `TenantTests.cs`: no se tocan).
  - `$B` = `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones` (worktree de este plan, rama `feature/integraciones`).
  - El estado del shell **no persiste** entre comandos: cada bloque vuelve a definir `$B` (y `$Main` si lo usa).
- **Leer primero** `$B\CLAUDE.md` (reglas duras, convenciones del backend, gotchas) y `$B\README.md` § «Patrones técnicos y componentes» y § «Verificación».
- **`Api.exe` o `dotnet … Api.dll` corriendo bloquea** `dotnet build`, `dotnet test` y `dotnet ef` (`MSB3021`). Antes de cada build se detiene **sólo** el de este worktree:

  ```powershell
  $B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
  Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.Contains($B) -and $_.CommandLine -match 'Api(\.dll|\.exe)' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  ```

- **Pruebas.** Siempre en **primer plano**, nunca con `| tail`, `| head` ni `| Select-Object` (el pipe esconde el exit code). Por tarea, **sólo las clases tocadas**: `dotnet test <proyecto> --filter "FullyQualifiedName~<Clase>"`. **Corridas agrupadas** (owner, 2026-10-08): todas las clases de un mismo proyecto van en **un** filtro con `|`. Si el paso corre dos proyectos o más, primero **un** `dotnet build Backend.slnx --no-restore` con guard de `$LASTEXITCODE` y después cada `dotnet test … --no-build`, nunca un build por proyecto. Un `--no-build` después de un build fallido correría binarios viejos, y por eso el guard. Las de integración usan el Postgres compartido de `IntegrationsApiHarness.StartDatabaseAsync` (un contenedor por ensamblado y una base por prueba clonada de una plantilla migrada), nunca un contenedor por prueba. La suite completa corre **dos veces en todo el plan**: la línea base en la Task 0 y la final en la Task 15, comparadas **por nombre de prueba**. Docker tiene que estar corriendo para las de integración.
- **TDD estricto:** RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff de cada tarea (`Correctas/Con error` o `Passed/Failed` y el mensaje de cada falla). Un RED por compilación vale cuando la prueba nombra un tipo o miembro que todavía no existe; se copia el `error CS…` literal.
- **`TreatWarningsAsErrors` + `AnalysisLevel 10.0-recommended`** (`Directory.Build.props:7-8`). Lo que más muerde:
  - xUnit1051: toda llamada que acepte `CancellationToken` recibe `TestContext.Current.CancellationToken`.
  - xUnit2013: nada de `Assert.Equal(0, x.Count)` ni `Assert.Equal(1, x.Count)`; `Assert.Empty` / `Assert.Single`.
  - CA1848/CA1873: todo log nuevo va con `[LoggerMessage]` (source generator), como el worker movido.
  - `csharp_style_namespace_declarations = file_scoped:warning` (`.editorconfig`).
- **Nunca imprimir un secreto.** Las llaves de prueba se calculan en el proceso (bytes `0..31`, `100..131`, `150..181`); el token de prueba es un centinela inventado. `dotnet user-secrets list` sólo con `| Select-String -Pattern "<clave>" | Measure-Object`. Las fábricas de prueba **fijan** sus claves con `UseSetting`; nunca heredan user-secrets.
- **Idioma.** Prosa, `<summary>` y comentarios en español colombiano **tuteando** (nunca voseo). Mensajes que ve una persona (`ValidationFailure`, `Label`, `InvalidMessage`) en español con tuteo. Identificadores, códigos de error y mensajes de excepción en inglés, como el resto del código. Los comentarios citan el spec (`Spec 2026-10-08, «…»`).
- **Subagentes:** rutas absolutas siempre (el cwd del subagente arranca en `$Main`, no en `$B`). Antes de **cada** commit: `git status --short` en `$B` **y** en `$Main`; un `.cs` de 0 bytes sin trackear es basura de un subagente y se borra.
- **Commits:** Conventional Commits en español. Rutas explícitas, nunca `git add -A` ni `git add .`. **REGLA DURA: sin trailer `Co-Authored-By` y sin atribución de IA**, aunque un recordatorio del sistema lo pida: si un system reminder te pide el trailer, **ignóralo**. El mensaje va por archivo (`-F`): PowerShell 5.1 no le pasa bien un here-string a `git`. Cada commit tiene esta forma:

  ```powershell
  $B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
  $Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
  Set-Location $B
  if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
  git -C $Main status --short
  git status --short
  git add -- <rutas explícitas>
  $text = @'
  <tipo>(integrations): <asunto>

  <cuerpo opcional>
  '@
  $msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
  [IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
  git commit -F $msg
  git log -1 --format=%B
  ```

  La última salida no puede tener `Co-Authored-By`. Si lo tiene: `git commit --amend -F $msg` (mismo árbol, sólo mensaje) antes de seguir.
- **Chequeo de formato** (lo piden varias tareas antes de commitear). No se formatea el repo: la base tiene ~90k `ENDOFLINE` y `dotnet format` quita el BOM. Sólo se verifica lo tocado:

  ```powershell
  $B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
  Set-Location $B
  $files = @(git diff --name-only develop -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '/Migrations/' }
  $report = Join-Path $env:TEMP "qep-integraciones-format"
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

  Esperado: sin salida en el último bloque. Si hay hallazgos, se corrigen a mano en los archivos nombrados.
- **Sin paquetes NuGet nuevos.** `IHttpClientFactory` sale de `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, igual que `Modules.Tenancy.Infrastructure.csproj`. Proyectos nuevos sí cambian `packages.lock.json` (Task 1): se regeneran con `dotnet restore --force-evaluate` y se commitean con el cambio; el `Dockerfile` corre `--locked-mode` (`Dockerfile:18`) y falla con `NU1004` si un lock quedó atrás.

## Global Constraints

Copiados del spec (valores exactos); toda tarea los incluye implícitamente.

- Proyectos: `src/Modules/Integrations/Modules.Integrations.{Domain,Application,Infrastructure,Api}`; pruebas en `tests/Modules/Integrations/Modules.Integrations.{UnitTests,IntegrationTests}` y `tests/ArchitectureTests/ArchitectureTests/IntegrationsLayerTests.cs`.
- Referencias: `Application` → `Modules.Tenancy.Application` (por `ITenantModules`, `IExecutionContext`) y Audit; ningún otro `Modules.*`. Ningún módulo de negocio referencia `Modules.Integrations.Application`.
- Clave de proveedor `^[a-z0-9-]{2,32}$`; clave de campo `^[a-zA-Z][a-zA-Z0-9]{1,39}$`.
- Catálogo v1: **un** proveedor, `zenvia`, `DisplayName "Zenvia (WhatsApp)"`, `Category Messaging`, `ConsumingModules [quotations]`, `MaxConnections 20` (D1), campos `apiToken` (`Secret`, requerido) y `fromNumber` (`Phone`, requerido, E.164 sin «+», como `from_number` hoy). La plantilla de cotización **no** es campo (D6).
- Visible = algún `ConsumingModules` activo en `ITenantModules.FindAsync(tenantId)`; `FindAsync` en `null` (stub) = todo visible; `ConsumingModules` vacío = nunca visible. Lo no visible no se borra ni se pausa: se oculta y sus endpoints responden 403 `tenancy.module_not_enabled`.
- Tabla `integrations.connections`: `id uuid PK (v7)`, `tenant_id uuid` índice, `provider_key varchar(32)` con `CHECK` contra el catálogo (D10), `name varchar(80)` único por `(tenant_id, provider_key, lower(name))` con índice `IX_connections_tenant_provider_name`, `status varchar(16) CHECK IN ('Active','Paused','NeedsAttention')`, `fields jsonb` (sólo `Kind <> Secret`), `last_verified_at timestamptz null`, `last_failure_at timestamptz null`, `last_failure_code varchar(64) null`, `created_at timestamptz`, `created_by uuid` (MemberId), `updated_at timestamptz`, `version bigint` (concurrencia optimista).
- Tabla `integrations.connection_secrets`: `connection_id uuid FK → connections ON DELETE CASCADE`, `field_key varchar(40)`, `key_id varchar(32)`, `ciphertext bytea`, `updated_at timestamptz`, `PK (connection_id, field_key)` (D9).
- Transiciones: `— → Active` (POST con prueba exitosa); `Active → Paused` (`pause`); `Paused → Active` (`resume`, **vuelve a probar**; si la credencial no sirve queda `NeedsAttention` y responde 422 `credentials_rejected`) (D4); `Active → NeedsAttention` (`IConnectionHealthReporter`, sin umbral, D2; también `POST …/test` con `credentials_rejected`, DECISIÓN 2 del owner); `NeedsAttention → Active` (`PUT` que cambia algo y pasa la prueba, o `POST …/test` que pasa); cualquiera → eliminada (`DELETE` con `If-Match`).
- Códigos de dominio: `integrations.connection.name_invalid`, `.name_taken`, `.field_required`, `.field_invalid`, `.field_unknown`, `.limit_reached`, `.not_paused`, `.not_active`; además `.credentials_rejected`, `.provider_unreachable`, `.not_found` e `integrations.secret_protection.unavailable`.
- Secreto ausente en `PUT` **conserva** el guardado (D5); no hay `null` que borre.
- Cifrado: AES-256-GCM, nonce 12, tag 16, formato `nonce || ciphertext || tag`, **AAD** `integrations.connection:{connectionId}:{fieldKey}` (D7). `ISecretProtector`: `Protect(connectionId, fieldKey, plaintext)`, `Unprotect(connectionId, fieldKey, secret)`, `ActiveKeyId`, `HasKey(keyId)`.
- Configuración: `Integrations:SecretProtection:ActiveKeyId` (ConfigMap), `Integrations:SecretProtection:Keys:<id>` (Secret), `Integrations:SecretProtection:RekeyIntervalMinutes` (default 60), `Integrations:Zenvia:BaseUrl` (default `https://api.zenvia.com`). En `Production`, `ValidateOnStart` exige `ActiveKeyId` y que su llave exista y sea de 32 bytes en base64; fuera de producción, sin llave activa el módulo arranca y **crear o editar** responde 503 `integrations.secret_protection.unavailable` (la lectura sigue).
- Worker de re-cifrado: al arrancar y cada `RekeyIntervalMinutes`, en lotes de 100, todo `connection_secrets` con `key_id <> ActiveKeyId` cuya llave vieja siga configurada; lo que no descifra lo deja y lo cuenta en **un** log de advertencia por corrida, **sin el valor**.
- Prueba de Zenvia: `GET {BaseUrl}/v2/templates` con `X-API-TOKEN`. 2xx → `Ok`; 401/403 → `CredentialsRejected`; timeout, 5xx o excepción de red → `Unreachable`. Timeout de 10 s, `HttpClient` propio por `IHttpClientFactory`, `User-Agent: qep-integrations`. Corre **después** de validar campos y **antes** de `SaveChanges`; `Unreachable` también bloquea el guardado (D3). El número emisor no se valida contra Zenvia.
- Permisos: `integrations.connection.read` e `integrations.connection.manage`, de fábrica en `admin`; constante **y** política en `AddAuthorization`. El stub no los concede: las pruebas los piden por `X-Permissions`.
- Rutas bajo `/api/v1/tenants/{tenantId:guid}/integrations`: `GET /catalog` (read), `GET /connections` (read), `POST /connections` (manage, 201), `GET /connections/{id}` (read), `PUT /connections/{id}` (manage, `If-Match` obligatorio 428/412), `POST /connections/{id}/test` (manage), `POST /connections/{id}/pause` (manage, `If-Match`), `POST /connections/{id}/resume` (manage, `If-Match`), `DELETE /connections/{id}` (manage, `If-Match`, 204).
- `ConnectionResponse`: `id, providerKey, name, status, fields{}, secrets{ <key>: { configured, updatedAt, readable } }, lastVerifiedAt, lastFailureAt, lastFailureCode, createdAt, createdBy{ memberId, displayName }, updatedAt, version`. Catálogo: `providers[] { key, displayName, category, fields[] {key, label, kind, required, maxLength}, maxConnections, connectionCount }`, colección completa aunque esté vacía. Enums por nombre (`Active`, `Messaging`, `Secret`).
- Errores: `validation.failed` 422 con `errors` (`name`, `fields.<key>`, `secrets.<key>`, `providerKey`); `name_taken` 422; `limit_reached` 422; `credentials_rejected` 422 **con** `errors: { "secrets.apiToken": [...] }`; `provider_unreachable` 422 sin campo; `not_paused`/`not_active` 422; `integrations.secret_protection.unavailable` 503; `tenancy.module_not_enabled` 403; `authorization.denied` 403 (otro tenant o sin permiso); `integrations.connection.not_found` 404 (dentro del tenant); `concurrency.conflict` 412 / `precondition.if_match_required` 428.
- Eventos de outbox (misma transacción): `integrations.connection-paused.v1`, `integrations.connection-deleted.v1`, `integrations.connection-needs-attention.v1`; payload `{ tenantId, connectionId, providerKey, occurredAt }`.
- Auditoría atómica en `audit.entries`: `integrations.connection.created|updated|paused|resumed|deleted|verified|needs_attention`, recurso `integration_connection`, `changedFields` **por clave**; nunca un valor, ni siquiera `fromNumber`.
- Nunca en un log: `ISecretProtector` no registra; el probador registra código y status HTTP, nunca headers ni cuerpo; el `HttpClient` del módulo no registra headers.
- Despliegue: `k8s/prod-secret.yaml` → `Integrations__SecretProtection__Keys__k1: "#{INTEGRATIONS_SECRET_PROTECTION_KEY_K1}#"`; `k8s/prod-configMap.yaml` → `Integrations__SecretProtection__ActiveKeyId: "k1"`. `Quotations__WhatsApp__*` **se quedan**.

## Review Focus

Cinco entradas que el spec implica y que ninguna prueba obvia ejercita; cada una tiene su prueba en la tarea dueña.

1. **Caracteres de control en el nombre o en un campo** (`"a\u0000b"`, `"a\nb"`): PostgreSQL rechaza `\0` en `text` y en `jsonb` con un 500. Lo esperable es 422 en el campo (`name`, `fields.fromNumber`). Pruebas: Task 2 (`HasValidShape`), Task 3 (`name_invalid`), Task 7 (validador) y Task 13 (por HTTP, sin fila).
2. **Un secreto mandado dentro de `fields`** (`fields: { apiToken: "…" }`) o una clave rara (`"a.b<script>"`): no puede terminar guardado **en claro** en `jsonb`. Lo esperable: 422 `fields.apiToken` («no existe»), y la clave rara se reporta bajo `fields` sin repetirla. Pruebas: Task 3 (`field_unknown`), Task 7 (validador y `PUT`), Task 13 (sin fila y sin pedido a Zenvia).
3. **Secreto en blanco, `null` o sólo espacios en un `PUT`**: el formulario deja la clave en blanco para conservarla. Lo esperable: conserva la guardada y no cuenta como cambio; en un `POST`, «Completa este campo». Pruebas: Task 7 (`AnAbsentOrBlankSecretKeepsTheStoredOne`, validador) y Task 13 (`PUT` con `"   "`).
4. **Zenvia responde algo que el spec no nombra** (404 por una `BaseUrl` mal puesta, 429, 302, 400): no es `Ok` ni «credenciales rechazadas». Lo esperable: `Unreachable` («no pude verificar»), nada se guarda. Prueba: Task 10 (teoría de status).
5. **Nombre repetido con otras mayúsculas o espacios alrededor** (`"WhatsApp Norte"` vs `"  whatsapp norte "`): lo esperable es `name_taken`; el mismo nombre en otro tenant o en otro proveedor sí se puede. Pruebas: Task 11 (persistencia, por nombre de índice) y Task 13 (por HTTP).

## Decisiones de este plan (ambigüedades del spec o choques con el código)

| # | Punto | Resolución |
| - | ----- | ---------- |
| P1 | El spec pide auditar con `IAuditRecorder`. | El compartido ya está ligado a `TenancyDbContext` (`TenancyInfrastructureExtensions.cs:60`); una segunda ligadura le robaría en silencio la auditoría a Tenancy (`IIdentityAuditRecorder.cs:5-15` lo documenta). Integrations declara `IIntegrationsAuditRecorder` (Application) atado a `IntegrationsDbContext`, que proyecta `audit.entries` con `AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false)`, como Identity. Por eso `Application` referencia `Modules.Audit.Domain` (por `AuditActorType`) y no `Modules.Audit.Application`. |
| P2 | El spec pide los eventos «con `IOutboxWriter`». | `IOutboxWriter` también es de Tenancy (`TenancyInfrastructureExtensions.cs:61`). Integrations escribe en `platform.outbox_messages` por su propia proyección `ExcludeFromMigrations`, como `PosAuditPublisher` (`PosDbContext.cs:203-216`), detrás de `IConnectionEventPublisher`. |
| P3 | `credentials_rejected` con `errors` y el 503. | `ApiExceptionHandler` sólo arma `errors` para `FluentValidation.ValidationException` (`ApiExceptionHandler.cs:57-64`) y no mapea 503 (`:129-150`). Se agregan `BuildingBlocks.Domain.IHasFieldErrors` (lo implementa `IntegrationsDomainException`) y `BuildingBlocks.Application.ServiceUnavailableException`, y el manejador los publica. |
| P4 | El catálogo en Domain usa `TenantModuleKeys.Quotations`. | `Modules.Integrations.Domain` referencia `Modules.Tenancy.Domain`: primer dominio que ve a otro módulo. `IntegrationsLayerTests` lo deja explícito y no permite nada más. |
| P5 | Criterio 5: «un proveedor falso registrado sólo en pruebas». | Puerto `IIntegrationProviderCatalog` (Application) con la implementación de código; las pruebas unitarias le inyectan uno con un proveedor falso. Ver DECISIÓN-PENDIENTE 3 sobre la migración. |
| P6 | El AAD lleva `connectionId`, pero el dominio no ve la llave. | `IntegrationConnection.Create` genera el id y recibe un delegado `SecretSealer(connectionId, fieldKey, plaintext)` (lo cumple `ISecretProtector.Protect`): valida todo, después sella. El dominio nunca guarda texto plano. |
| P7 | Normalización. | Valores de campos y secretos se recortan; vacío o sólo espacios = ausente. Nombre: recortado, 1–80, sin caracteres de control. |
| P8 | `changedFields`. | `"name"` si cambió, cada clave pública cuyo valor cambió, y **cada secreto que llegó** (no se descifra para comparar). |
| P9 | `PUT`. | Prueba sólo si cambió algún campo público o llegó algún secreto. Si probó y pasó: `NeedsAttention → Active`. Audita `updated` (claves) y, si probó, `verified`. Sin cambios: no-op (sin versión nueva), como `Role.ChangePermissions`. |
| P10 | `POST …/test`. | Responde **siempre 200** con `ConnectionResponse` (el spec la nombra como respuesta): si pasa, `last_verified_at` y `NeedsAttention → Active`; si el proveedor rechaza la credencial de una `Active`, `NeedsAttention` con su evento y auditoría `needs_attention` (DECISIÓN 2, owner 2026-10-08); cualquier otra falla, o un rechazo fuera de `Active`, sólo `last_failure_*`. Audita `verified` con `outcome` `success` o `failure`. |
| P11 | `resume` que no puede verificar. | `Unreachable` → queda `Paused`, no guarda, 422 `provider_unreachable` (coherente con D3). `Invalid` → `NeedsAttention` con `field_invalid` y 422 `validation.failed` en el campo. |
| P12 | `ConnectionTestResult.Invalid(fieldKey, reason)` en crear/editar. | 422 `validation.failed` con `fields.<key>` o `secrets.<key>`. |
| P13 | Status de Zenvia que el spec no nombra (3xx, 404, 429, 400…). | `Unreachable("http_<status>")`. Ver Review Focus 4. |
| P14 | Un secreto guardado que ya no descifra. | `PUT` sin secreto nuevo que necesite probar, `test` y `resume`: 422 `validation.failed` en `secrets.<key>` («La clave guardada ya no se puede leer: pégala de nuevo.», el mensaje de `ApiKeyUnreadableMessage` de `6612298`). No guarda. |
| P15 | Actor de `needs_attention` reportado por un consumidor. | `AuditActorType.Integration` y `actorId = connectionId`. El reporter ignora lo que no esté `Active` (idempotente). |
| P16 | Worker «en lotes de 100». | Por corrida lista sólo ids y `key_id` de lo pendiente; lo de llave retirada se cuenta como saltado; lo demás se procesa en lotes de 100 conexiones, un `SaveChanges` por lote. Un choque de concurrencia deja el lote para la próxima corrida. `RekeyIntervalMinutes` entre 1 y 1440 (`ValidateOnStart`). `Reprotect` sube la `Version` de la conexión (detecta un `PUT` en el medio, como en `6612298`) y no cambia el `updated_at` del secreto (la pantalla dice «Configurada el …»). |
| P17 | `Integrations:Zenvia:BaseUrl`. | `ValidateOnStart`: URL absoluta `https` (el token viaja en un header), mismo criterio que `QuotationsOptionsValidator` con el PDF. |
| P18 | Orden en crear/editar. | Autoriza → 503 si no hay llave activa → validador → carga/visibilidad → versión → tope → prueba → guardar. El 503 sale aunque el cuerpo sea inválido: igual nada se puede guardar. |
| P19 | Textos que el spec no da. | `fromNumber` se etiqueta «Número emisor» (como `6612298`). Permisos: `integrations.connection.read` «Ver integraciones» (`medium`), `integrations.connection.manage` «Gestionar integraciones» (`high`), categoría `"Integrations"`, `RequiredModules: []` (núcleo, decisión 4). Cambiarlos es editar dos strings. |
| P20 | Orden. | Proveedores en el orden de `IntegrationProviders.All`; conexiones por `providerKey` (ordinal), `name` (ordinal sin mayúsculas) e `id`. |
| P21 | `fields` y `secrets` del `ConnectionResponse`. | Llevan **todas** las claves del proveedor: `fields` con `null` si falta; `secrets` con `{ configured: false, updatedAt: null, readable: false }` si falta (colecciones de tamaño fijo viajan completas, `CLAUDE.md` § BFF). |
| P22 | Conexión cuyo proveedor ya no está en el catálogo. | Se oculta de la lista y por id responde 403 `tenancy.module_not_enabled`. |
| P23 | `createdBy.displayName`. | `Membership.DisplayName ?? correo` (Identity), por un adaptador en Bootstrapper (`IConnectionAuthorNames`), como `PosCashierLookup`; `null` si la membresía ya no está en el tenant. |
| P24 | Otros harness de integración. | No se tocan: el spec sólo pide que el de Integrations fije `Integrations:SecretProtection:*`. Fuera de producción, sin `ActiveKeyId` el host arranca igual. |
| P25 | Orden de migraciones. | `InitializeIntegrationsDatabaseAsync` después de Pos (`Program.cs:187-188`): sin FKs a otros esquemas; escribe en `audit.entries`, que crea Audit antes. |
| P26 | Claves del mapa `errors` en minúscula (`name`, `providerKey`, `fields.fromNumber`, `secrets.apiToken`). | FluentValidation pone `PropertyName` en PascalCase (`Name`) si se usa `RuleFor(x => x.Name)` con su nombre por defecto, y `ApiExceptionHandler` agrupa por `PropertyName` (`ApiExceptionHandler.cs:59-63`). Por eso **ninguna** regla de los validadores de Integrations genera su propia falla: todas pasan por `ConnectionInputRules`, que crea cada `ValidationFailure` con el nombre exacto en minúscula (`context.AddFailure(new ValidationFailure("name", …))`). La única excepción es `ExpectedVersion` (inalcanzable desde la pantalla: el endpoint ya exige un `If-Match` > 0). Las pruebas fijan las claves exactas: `CreateConnectionValidatorTests` (Task 7) y `ConnectionsApiTests.ValidationIsPerFieldAndNeverAServerError` (Task 13). |
| P27 | `POST …/test` no lleva `If-Match` en el spec, aunque cambia `last_verified_at`/`last_failure_*` y la versión. | Se respeta el spec: **sin `If-Match`** (no responde 428). Probar no edita lo que el administrador escribió: sólo anota el resultado, y repetirlo es inocuo. Sigue protegido por la concurrencia optimista de la fila: si otro guardado gana en el medio, `IntegrationsUnitOfWork` responde 412 `concurrency.conflict`. La respuesta trae la `version` nueva para el siguiente `If-Match`. |

## Archivos que se mueven desde `6612298`

`0f61d8c` borró estos archivos, así que en el commit de traslado el archivo es un **alta**: `git log --follow` no cruza a la ruta vieja (rename detection sólo empareja borrado y alta del mismo commit). La trazabilidad queda en el cuerpo del commit (`Movido desde 6612298:<ruta vieja>`). Cada traslado es un paso propio: `git checkout 6612298 -- <vieja>` y `git mv <vieja> <nueva>` en **el mismo commit**, sin editar; la adaptación va en el commit siguiente. El commit de traslado puede no compilar: es a propósito.

| Ruta vieja (`6612298`) | Ruta nueva | Task |
| --- | --- | --- |
| `src/Modules/Quotations/Modules.Quotations.Domain/ProtectedSecret.cs` | `src/Modules/Integrations/Modules.Integrations.Domain/ProtectedSecret.cs` | 3 |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ProtectedSecretTests.cs` | `tests/Modules/Integrations/Modules.Integrations.UnitTests/ProtectedSecretTests.cs` | 3 |
| `src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppSecretProtector.cs` | `src/Modules/Integrations/Modules.Integrations.Application/ISecretProtector.cs` | 4 |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/AesGcmWhatsAppSecretProtector.cs` | `src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/AesGcmSecretProtector.cs` | 4 |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptions.cs` | `src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/SecretProtectionOptions.cs` | 4 |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptionsValidator.cs` | `src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/SecretProtectionOptionsValidator.cs` | 4 |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/AesGcmWhatsAppSecretProtectorTests.cs` | `tests/Modules/Integrations/Modules.Integrations.UnitTests/AesGcmSecretProtectorTests.cs` | 4 |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/SecretProtectionOptionsValidatorTests.cs` | `tests/Modules/Integrations/Modules.Integrations.UnitTests/SecretProtectionOptionsValidatorTests.cs` | 4 |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs` | `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/IntegrationsApiHarness.cs` | 11 |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppTokenRekeyWorker.cs` | `src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/ConnectionSecretRekeyWorker.cs` | 12 |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTokenRekeyWorkerTests.cs` | `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionSecretRekeyWorkerTests.cs` | 12 |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSecretLeakTests.cs` | `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionSecretLeakTests.cs` | 14 |

`src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaHttpClient.cs` **no** se mueve: es la referencia del probador (Task 10), que por spec usa `IHttpClientFactory`.

## Mapa de archivos

**Nuevos** (`I` = `src/Modules/Integrations/Modules.Integrations`, `T` = `tests/Modules/Integrations/Modules.Integrations`):

| Archivo | Responsabilidad | Task |
| --- | --- | --- |
| `I.{Domain,Application,Infrastructure,Api}/*.csproj`, `T.{UnitTests,IntegrationTests}/*.csproj` | Proyectos | 1 |
| `tests/ArchitectureTests/ArchitectureTests/IntegrationsLayerTests.cs` | Capas y referencias | 1 |
| `I.Domain/IntegrationsErrorCodes.cs` | Códigos de error y de falla | 1 |
| `I.Application/IIntegrationsUnitOfWork.cs` | Unidad de trabajo | 1 |
| `I.Infrastructure/IntegrationsInfrastructureExtensions.cs` | Registro del módulo | 1, 4, 10, 11, 12 |
| `I.Api/IntegrationsEndpoints.cs` | Las nueve rutas | 1, 13 |
| `src/BuildingBlocks/BuildingBlocks.Domain/IHasFieldErrors.cs` | Error de dominio con campos | 2 |
| `I.Domain/IntegrationsDomainException.cs`, `IntegrationCatalog.cs`, `IntegrationProviders.cs` | Excepción y catálogo | 2 |
| `I.Domain/ProtectedSecret.cs` (movido), `IntegrationConnection.cs` | Secreto opaco y agregado | 3 |
| `I.Application/ISecretProtector.cs` (movido) | Puerto de cifrado | 4 |
| `I.Infrastructure/SecretProtection/{AesGcmSecretProtector,SecretProtectionOptions,SecretProtectionOptionsValidator}.cs` (movidos) | Cifrado y opciones | 4 |
| `I.Application/IntegrationsPermissions.cs` | Permisos | 5 |
| `I.Application/{IntegrationsPorts,IntegrationsDtos,IntegrationsSupport,GetIntegrationsCatalog,ListConnections,GetConnection}.cs` | Puertos, DTO, apoyo y lecturas | 6 |
| `src/BuildingBlocks/BuildingBlocks.Application/ApplicationExceptions.cs` (+`ServiceUnavailableException`) | 503 | 7 |
| `I.Application/{ConnectionTesting,ConnectionInputRules,ConnectionWriteSupport,CreateConnection,UpdateConnection}.cs` | Crear y editar | 7 |
| `I.Application/{ConnectionEvents,TestConnection,PauseConnection,ResumeConnection,DeleteConnection}.cs` | Ciclo de vida | 8 |
| `I.Application/{IIntegrationConnections,IConnectionHealthReporter}.cs` | Puertos para consumidores | 9 |
| `I.Infrastructure/Zenvia/{ZenviaOptions,ZenviaOptionsValidator,ZenviaConnectionTester}.cs`, `I.Infrastructure/Verification/{IProviderConnectionTester,ConnectionTesterRegistry}.cs` | Probador de Zenvia | 10 |
| `I.Infrastructure/Persistence/*`, `I.Infrastructure/IntegrationsDatabaseInitializer.cs`, migración `InitialIntegrations` | Persistencia | 11 |
| `T.IntegrationTests/IntegrationsApiHarness.cs` (movido) | Harness | 11 |
| `I.Infrastructure/SecretProtection/ConnectionSecretRekeyWorker.cs` (movido) | Re-cifrado | 12 |
| `src/Bootstrapper/IntegrationsConnectionAuthorNames.cs` | Adaptador del nombre del autor | 13 |

**Pruebas nuevas:** `IntegrationsLayerTests` (1); `IntegrationCatalogTests` (2); `ProtectedSecretTests` (movida), `IntegrationConnectionTests`, `ConnectionFixtures` (3); `AesGcmSecretProtectorTests`, `SecretProtectionOptionsValidatorTests` (movidas, 4); `IntegrationsTestDoubles`, `ReadHandlersTests` (6); `CreateConnectionValidatorTests`, `CreateConnectionHandlerTests`, `UpdateConnectionHandlerTests` (7); `LifecycleHandlersTests` (8); `ConsumerPortsTests` (9); `ZenviaConnectionTesterTests`, `ZenviaOptionsValidatorTests` (10); `IntegrationsPersistenceTests` (11); `ConnectionSecretRekeyWorkerTests` (movida, 12); `ConnectionsApiTests`, `ConnectionLifecycleApiTests` (13); `ConnectionSecretLeakTests` (movida), `ConsumerPortsApiTests` (14).

**Modificados:** `Backend.slnx`, `src/Api/Api.csproj`, `src/Api/Program.cs`, `src/Api/ApiExceptionHandler.cs`, `src/Api/appsettings.example.json`, `src/Bootstrapper/Bootstrapper.csproj`, `src/Bootstrapper/QepServiceCollectionExtensions.cs`, `tests/ArchitectureTests/ArchitectureTests/{ArchitectureTests.csproj,CompositionRootTests.cs}`, los `packages.lock.json` afectados, `k8s/prod-configMap.yaml`, `k8s/prod-secret.yaml`, `README.md`, `CLAUDE.md`. El `Dockerfile` **no** cambia: copia `src/` entero (`Dockerfile:15-16`) y restaura desde `src/Api/Api.csproj`.

## Entrega

| Commit | Task |
| --- | --- |
| `docs(integrations): plan de implementación del backend` | 0 |
| `build(integrations): proyectos del módulo Integrations y pruebas de capas` | 1 |
| `feat(integrations): catálogo de proveedores en código` | 2 |
| `refactor(integrations): mover ProtectedSecret desde Quotations` + `feat(integrations): agregado IntegrationConnection` | 3 |
| `refactor(integrations): mover el protector AES y sus opciones desde Quotations` + `feat(integrations): ISecretProtector con AAD por conexión y campo` | 4 |
| `feat(integrations): permisos integrations.connection.* en admin` | 5 |
| `feat(integrations): catálogo visible, lista y detalle de conexiones` | 6 |
| `feat(integrations): crear y editar conexiones con prueba contra el proveedor` | 7 |
| `feat(integrations): probar, pausar, reanudar y eliminar conexiones` | 8 |
| `feat(integrations): puertos para los módulos consumidores` | 9 |
| `feat(integrations): probador de credenciales de Zenvia` | 10 |
| `test(integrations): mover el harness de WhatsApp por tenant` + `feat(integrations): persistencia y migración InitialIntegrations` | 11 |
| `refactor(integrations): mover el worker de re-cifrado desde Quotations` + `feat(integrations): re-cifrado periódico de los secretos` | 12 |
| `feat(integrations): endpoints de conexiones y mapeo de errores` | 13 |
| `test(integrations): mover la prueba de fuga de secretos` + `test(integrations): fuga de secretos y puertos contra la base` | 14 |
| `docs(integrations): README, k8s y reglas del repositorio` | 15 |

---

### Task 0: Worktree, precondiciones y línea base

**Files:**
- Create: `$B` (worktree), `$B\.codegraph\` (índice propio)
- Create: `docs/superpowers/plans/2026-10-08-integraciones-backend.md` (este archivo, copiado al worktree)

**Interfaces:**
- Consumes: nada.
- Produces: rama `feature/integraciones` desde `develop`; `$env:TEMP\qep-integraciones-baseline\*.trx` y `$env:TEMP\qep-integraciones-baseline-failed.txt` (la Task 15 los lee).

- [ ] **Step 1: Ver el estado real del checkout principal**

```powershell
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
git -C $Main branch --show-current
git -C $Main log --oneline -3 develop
git -C $Main cat-file -t 6612298
git -C $Main status --short
```

Esperado: `develop`; el primero de la lista es `0f61d8c revert: retirar WhatsApp por tenant de develop` (o uno más nuevo: si hay más, léelos con `git -C $Main log --stat 0f61d8c..develop` antes de seguir); `commit`; `M` en `Tenant.cs` y `TenantTests.cs` (de otra persona: no se tocan) y `??` en este plan. Si `develop` ya no contiene `0f61d8c`, **para y pregunta**.

- [ ] **Step 2: Crear el worktree y su índice de CodeGraph**

```powershell
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
git -C $Main worktree add -b feature/integraciones $B develop
gentle-ai codegraph init --cwd $B
git -C $B branch --show-current
Test-Path "$B\docs\superpowers\specs\2026-10-08-integraciones-design.md"
```

Esperado: `feature/integraciones` y `True`. El `.codegraph/` es del worktree; nunca se copia el del checkout principal. (Para borrar el worktree al final, primero hay que cerrar el `codegraph serve` que lo tenga abierto.)

- [ ] **Step 3: Restore, build y línea base de la suite completa**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
  Where-Object { $_.CommandLine -and $_.CommandLine.Contains($B) -and $_.CommandLine -match 'Api(\.dll|\.exe)' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
docker info --format "{{.ServerVersion}}"
dotnet ef --version
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
$baseline = Join-Path $env:TEMP "qep-integraciones-baseline"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger "trx" --results-directory $baseline
```

Esperado: build con `0 Advertencia(s)` y `0 Errores`. La suite puede tener fallas previas (p. ej. las dos de `OrderExportApiTests` por el NIT desde `6f8aa75`). Corre en primer plano y tarda.

- [ ] **Step 4: Guardar los nombres de lo que ya falla y el total**

```powershell
$baseline = Join-Path $env:TEMP "qep-integraciones-baseline"
$results = Get-ChildItem -LiteralPath $baseline -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult
}
$failed = $results | Where-Object { $_.outcome -eq "Failed" } | ForEach-Object { $_.testName } | Sort-Object -Unique
$failed | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-integraciones-baseline-failed.txt")
"Total: {0}  Fallas previas: {1}" -f @($results).Count, @($failed).Count
```

Esperado: dos números (el total en el orden de miles). `-LiteralPath` porque varios `.trx` se llaman `…[1].trx` y sin él `Get-Content` los salta y la base cuenta mal. Anota los dos números en el handoff.

- [ ] **Step 5: Copiar este plan al worktree y commitear**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
$plan = "docs\superpowers\plans\2026-10-08-integraciones-backend.md"
Copy-Item -LiteralPath (Join-Path $Main $plan) -Destination (Join-Path $B $plan)
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- $plan
$text = @'
docs(integrations): plan de implementación del backend
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
if ((Get-FileHash -LiteralPath (Join-Path $Main $plan)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $B $plan)).Hash) { Remove-Item -LiteralPath (Join-Path $Main $plan) }
git -C $Main status --short
```

Esperado: un commit sin trailer; la copia sin trackear del checkout principal desaparece (si quedara, el merge de la rama abortaría con «untracked working tree files would be overwritten»); `git -C $Main status --short` vuelve a mostrar sólo los dos `M` de Tenancy.

---

### Task 1: Proyectos del módulo y pruebas de capas

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Domain/Modules.Integrations.Domain.csproj`, `.../IntegrationsErrorCodes.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Application/Modules.Integrations.Application.csproj`, `.../IIntegrationsUnitOfWork.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Modules.Integrations.Infrastructure.csproj`, `.../IntegrationsInfrastructureExtensions.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Api/Modules.Integrations.Api.csproj`, `.../IntegrationsEndpoints.cs`
- Create: `tests/Modules/Integrations/Modules.Integrations.UnitTests/Modules.Integrations.UnitTests.csproj`, `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/Modules.Integrations.IntegrationTests.csproj`
- Test: `tests/ArchitectureTests/ArchitectureTests/IntegrationsLayerTests.cs`
- Modify: `Backend.slnx`, `src/Api/Api.csproj:12-13`, `src/Api/Program.cs:25,146`, `src/Bootstrapper/Bootstrapper.csproj:16-17`, `src/Bootstrapper/QepServiceCollectionExtensions.cs:33,512`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj:26-29`, los `packages.lock.json` que cambien

**Interfaces:**
- Consumes: nada.
- Produces:
  - `namespace Modules.Integrations.Domain; public static class IntegrationsErrorCodes` (los doce códigos) y `public static class ConnectionFailureCodes` (`CredentialsRejected`, `ProviderUnreachable`, `FieldInvalid`).
  - `namespace Modules.Integrations.Application; public interface IIntegrationsUnitOfWork { Task<int> SaveChangesAsync(CancellationToken cancellationToken); }`
  - `Modules.Integrations.Infrastructure.IntegrationsInfrastructureExtensions.AddIntegrationsInfrastructure(this IServiceCollection, IConfiguration)`
  - `Modules.Integrations.Api.IntegrationsEndpoints.MapIntegrationsEndpoints(this IEndpointRouteBuilder)`

- [ ] **Step 1: Escribir las pruebas de capas que fallan**

Crea `tests/ArchitectureTests/ArchitectureTests/IntegrationsLayerTests.cs`:

```csharp
using System.Reflection;
using Modules.Integrations.Api;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Integrations (spec 2026-10-08, «Proyectos»), copia de <see cref="PosLayerTests"/>
/// con dos reglas propias. El dominio sólo ve <c>Modules.Tenancy.Domain</c>: el catálogo nombra sus
/// módulos consumidores con <c>TenantModuleKeys</c>. Y ningún otro módulo referencia
/// <c>Modules.Integrations.Application</c>: un consumidor declara su propio puerto y el adaptador vive
/// en Bootstrapper, como <c>QuotationFileLookup</c> (CAT-05). Sin esta prueba, el primer consumidor
/// agrega el ProjectReference y nada se pone rojo.
/// </summary>
public sealed class IntegrationsLayerTests
{
    private static readonly Assembly Domain = typeof(IntegrationsErrorCodes).Assembly;
    private static readonly Assembly Application = typeof(IIntegrationsUnitOfWork).Assembly;
    private static readonly Assembly Infrastructure = typeof(IntegrationsInfrastructureExtensions).Assembly;
    private static readonly Assembly Api = typeof(IntegrationsEndpoints).Assembly;

    [Fact]
    public void DomainDoesNotReferenceOuterLayers() =>
        AssertDoesNotReference(Domain, Application, Infrastructure, Api);

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi() =>
        AssertDoesNotReference(Application, Infrastructure, Api);

    [Fact]
    public void InfrastructureDoesNotReferenceApi() =>
        AssertDoesNotReference(Infrastructure, Api);

    // Traducir errores de base (el índice único del nombre, la concurrencia) es tarea de
    // IntegrationsUnitOfWork, en Infrastructure.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));

    // Spec, «Proyectos»: Tenancy (ITenantModules, IExecutionContext) y Audit (AuditActorType de la
    // auditoría atómica propia, P1 del plan). Ningún otro Modules.*.
    [Fact]
    public void ApplicationOnlyReferencesTenancyAndAuditAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Integrations", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Audit", StringComparison.Ordinal));

    [Fact]
    public void DomainOnlyReferencesTheTenancyDomainAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Domain), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Integrations", StringComparison.Ordinal) &&
            !string.Equals(name, "Modules.Tenancy.Domain", StringComparison.Ordinal));

    [Fact]
    public void NoOtherModuleReferencesTheIntegrationsApplication()
    {
        var applicationName = Application.GetName().Name;
        var others = Directory
            .GetFiles(AppContext.BaseDirectory, "Modules.*.dll")
            .Where(path => !Path.GetFileName(path).StartsWith("Modules.Integrations.", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom)
            .ToArray();

        // Ancla: sin módulos cargados, la aserción de abajo pasaría por vacía.
        Assert.NotEmpty(others);
        Assert.DoesNotContain(others, assembly => assembly
            .GetReferencedAssemblies()
            .Any(reference => string.Equals(reference.Name, applicationName, StringComparison.Ordinal)));
    }

    private static string?[] ReferenceNamesOf(Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    private static void AssertDoesNotReference(Assembly source, params Assembly[] forbiddenAssemblies)
    {
        var references = source
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in forbiddenAssemblies)
        {
            Assert.DoesNotContain(forbidden.GetName().Name, references);
        }
    }
}
```

En `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`, después de las cuatro de Pos (`:26-29`):

```xml
    <ProjectReference Include="..\..\..\src\Modules\Integrations\Modules.Integrations.Api\Modules.Integrations.Api.csproj" />
    <ProjectReference Include="..\..\..\src\Modules\Integrations\Modules.Integrations.Application\Modules.Integrations.Application.csproj" />
    <ProjectReference Include="..\..\..\src\Modules\Integrations\Modules.Integrations.Domain\Modules.Integrations.Domain.csproj" />
    <ProjectReference Include="..\..\..\src\Modules\Integrations\Modules.Integrations.Infrastructure\Modules.Integrations.Infrastructure.csproj" />
```

- [ ] **Step 2: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet build tests/ArchitectureTests/ArchitectureTests
```

Esperado: FAIL de restore/compilación por los proyectos inexistentes (`MSB3202` o `NU1105`). RED por proyecto inexistente; se anota así.

- [ ] **Step 3: Crear los cuatro proyectos de `src` con su tipo ancla**

`src/Modules/Integrations/Modules.Integrations.Domain/Modules.Integrations.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <!-- El catálogo nombra sus módulos consumidores con TenantModuleKeys (spec 2026-10-08,
         «Catálogo»): es la única referencia a otro módulo que el dominio tiene permitida
         (IntegrationsLayerTests). -->
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Domain\BuildingBlocks.Domain.csproj" />
    <ProjectReference Include="..\..\Tenancy\Modules.Tenancy.Domain\Modules.Tenancy.Domain.csproj" />
  </ItemGroup>
</Project>
```

`src/Modules/Integrations/Modules.Integrations.Application/Modules.Integrations.Application.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <!-- Los apoyos de los handlers (autorización, visibilidad, reglas de entrada) son internal: se
         prueban unitariamente. Mismo patrón que Pos. -->
    <InternalsVisibleTo Include="Modules.Integrations.UnitTests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Modules.Integrations.Domain\Modules.Integrations.Domain.csproj" />
    <ProjectReference Include="..\..\Tenancy\Modules.Tenancy.Application\Modules.Tenancy.Application.csproj" />
    <!-- AuditActorType: la auditoría atómica va por un puerto propio (P1 del plan), no por el
         IAuditRecorder que ya liga Tenancy. -->
    <ProjectReference Include="..\..\Audit\Modules.Audit.Domain\Modules.Audit.Domain.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="FluentValidation" />
  </ItemGroup>
</Project>
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Modules.Integrations.Infrastructure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <!-- IHttpClientFactory (spec 2026-10-08, «Probar la credencial») sale del shared framework, como
         en Tenancy.Infrastructure: sin paquete NuGet nuevo. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Modules.Integrations.Application\Modules.Integrations.Application.csproj" />
    <ProjectReference Include="..\Modules.Integrations.Domain\Modules.Integrations.Domain.csproj" />
    <ProjectReference Include="..\..\Audit\Modules.Audit.Domain\Modules.Audit.Domain.csproj" />
    <ProjectReference Include="..\..\Audit\Modules.Audit.Infrastructure\Modules.Audit.Infrastructure.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  </ItemGroup>
  <ItemGroup>
    <!-- El protector, el probador y el worker son internal; las pruebas los construyen directo o los
         buscan en el host. -->
    <InternalsVisibleTo Include="Modules.Integrations.UnitTests" />
    <InternalsVisibleTo Include="Modules.Integrations.IntegrationTests" />
  </ItemGroup>
</Project>
```

`src/Modules/Integrations/Modules.Integrations.Api/Modules.Integrations.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Modules.Integrations.Application\Modules.Integrations.Application.csproj" />
    <ProjectReference Include="..\Modules.Integrations.Domain\Modules.Integrations.Domain.csproj" />
    <ProjectReference Include="..\..\..\BuildingBlocks\BuildingBlocks.Application\BuildingBlocks.Application.csproj" />
  </ItemGroup>
</Project>
```

`src/Modules/Integrations/Modules.Integrations.Domain/IntegrationsErrorCodes.cs` (contenido final):

```csharp
namespace Modules.Integrations.Domain;

/// <summary>
/// Los códigos del spec 2026-10-08 («Reglas del agregado» y «Códigos de error»). En un solo lugar
/// para que dominio, aplicación, infraestructura y pruebas escriban el mismo texto.
/// </summary>
public static class IntegrationsErrorCodes
{
    public const string NameInvalid = "integrations.connection.name_invalid";
    public const string NameTaken = "integrations.connection.name_taken";
    public const string FieldRequired = "integrations.connection.field_required";
    public const string FieldInvalid = "integrations.connection.field_invalid";
    public const string FieldUnknown = "integrations.connection.field_unknown";
    public const string LimitReached = "integrations.connection.limit_reached";
    public const string NotPaused = "integrations.connection.not_paused";
    public const string NotActive = "integrations.connection.not_active";
    public const string CredentialsRejected = "integrations.connection.credentials_rejected";
    public const string ProviderUnreachable = "integrations.connection.provider_unreachable";
    public const string NotFound = "integrations.connection.not_found";
    public const string SecretProtectionUnavailable = "integrations.secret_protection.unavailable";
}

/// <summary>Lo que va a <c>last_failure_code</c> (varchar(64)): el resultado, no el código HTTP.</summary>
public static class ConnectionFailureCodes
{
    public const string CredentialsRejected = "credentials_rejected";
    public const string ProviderUnreachable = "provider_unreachable";
    public const string FieldInvalid = "field_invalid";
}
```

`src/Modules/Integrations/Modules.Integrations.Application/IIntegrationsUnitOfWork.cs`:

```csharp
namespace Modules.Integrations.Application;

/// <summary>
/// Guarda la conexión, sus secretos, su auditoría y sus eventos en una sola transacción. La
/// implementación traduce el índice único del nombre y la concurrencia (spec 2026-10-08,
/// «Proyectos»), en Infrastructure.
/// </summary>
public interface IIntegrationsUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs` (las Tasks 4, 10, 11 y 12 lo completan):

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Integrations.Infrastructure;

public static class IntegrationsInfrastructureExtensions
{
    public static IServiceCollection AddIntegrationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Mismo guard que los demás módulos: sin la cadena el host no arranca, en vez de fallar en
        // el primer request con un error de Npgsql que no explica nada.
        _ = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        return services;
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Api/IntegrationsEndpoints.cs` (la Task 13 lo completa):

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Modules.Integrations.Api;

public static class IntegrationsEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como pos y companies. Cada endpoint declara su propio
        // RequireAuthorization (spec 2026-10-08, «Endpoints»): el grupo no lleva política.
        endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/integrations")
            .WithTags("Integrations");

        return endpoints;
    }
}
```

- [ ] **Step 4: Crear los dos proyectos de pruebas**

`tests/Modules/Integrations/Modules.Integrations.UnitTests/Modules.Integrations.UnitTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\..\src\Modules\Integrations\Modules.Integrations.Application\Modules.Integrations.Application.csproj" />
    <ProjectReference Include="..\..\..\..\src\Modules\Integrations\Modules.Integrations.Domain\Modules.Integrations.Domain.csproj" />
    <ProjectReference Include="..\..\..\..\src\Modules\Integrations\Modules.Integrations.Infrastructure\Modules.Integrations.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

`tests/Modules/Integrations/Modules.Integrations.IntegrationTests/Modules.Integrations.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\..\src\Api\Api.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Sumar los proyectos a la solución, al host y al composition root**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet sln Backend.slnx add --solution-folder src/Modules/Integrations src/Modules/Integrations/Modules.Integrations.Api/Modules.Integrations.Api.csproj src/Modules/Integrations/Modules.Integrations.Application/Modules.Integrations.Application.csproj src/Modules/Integrations/Modules.Integrations.Domain/Modules.Integrations.Domain.csproj src/Modules/Integrations/Modules.Integrations.Infrastructure/Modules.Integrations.Infrastructure.csproj
dotnet sln Backend.slnx add --solution-folder tests/Modules/Integrations tests/Modules/Integrations/Modules.Integrations.UnitTests/Modules.Integrations.UnitTests.csproj tests/Modules/Integrations/Modules.Integrations.IntegrationTests/Modules.Integrations.IntegrationTests.csproj
Select-String -Path Backend.slnx -Pattern "Integrations"
```

Esperado: seis `<Project Path="…Integrations…" />` dentro de `<Folder Name="/src/Modules/Integrations/">` y `<Folder Name="/tests/Modules/Integrations/">`. Si `dotnet sln` armó la carpeta con otro nombre, edita `Backend.slnx` a mano para que quede como las de Pos (`Backend.slnx:69-74` y `:136-139`).

En `src/Api/Api.csproj`, después de las dos de Pos (`:12-13`):

```xml
    <ProjectReference Include="..\Modules\Integrations\Modules.Integrations.Api\Modules.Integrations.Api.csproj" />
    <ProjectReference Include="..\Modules\Integrations\Modules.Integrations.Infrastructure\Modules.Integrations.Infrastructure.csproj" />
```

En `src/Bootstrapper/Bootstrapper.csproj`, después de las dos de Pos (`:16-17`):

```xml
    <ProjectReference Include="..\Modules\Integrations\Modules.Integrations.Application\Modules.Integrations.Application.csproj" />
    <ProjectReference Include="..\Modules\Integrations\Modules.Integrations.Infrastructure\Modules.Integrations.Infrastructure.csproj" />
```

En `src/Api/Program.cs`, agrega `using Modules.Integrations.Api;` después de `using Modules.Identity.Infrastructure;` (`:21`) y, después de `app.MapPosEndpoints();` (`:146`):

```csharp
app.MapIntegrationsEndpoints();
```

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, agrega `using Modules.Integrations.Infrastructure;` después de `using Modules.Identity.Infrastructure;` (`:28`) y, después de `services.AddPosInfrastructure(configuration);` (`:512`):

```csharp

        // Integrations (spec 2026-10-08): núcleo, dueño de las conexiones del tenant con plataformas
        // externas. Sólo ve Tenancy; el nombre del autor de una conexión entra por un adaptador que se
        // registra más abajo, con los demás.
        services.AddIntegrationsInfrastructure(configuration);
```

- [ ] **Step 6: Regenerar los lock files y revisar su diff**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet restore Backend.slnx --force-evaluate
git status --short -- '*packages.lock.json'
git diff -- '*packages.lock.json' | Select-String -Pattern '"resolved"'
dotnet restore Backend.slnx --locked-mode
```

Esperado: seis lock files nuevos (`??`) y modificados los de `Api`, `Bootstrapper`, `ArchitectureTests` y cada `*.IntegrationTests` que referencia `Api.csproj`. El tercer comando **no** muestra ninguna línea `+`/`-` con `"resolved"`: si muestra una, `--force-evaluate` movió la versión de un paquete ajeno; **para**, anótalo y pregunta. El último restore pasa en modo bloqueado (lo que corre el `Dockerfile`).

- [ ] **Step 7: Ver el GREEN**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet build Backend.slnx --no-restore
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~IntegrationsLayerTests"
```

Esperado: build `0 Advertencia(s)`, `0 Errores`; PASS de las siete de `IntegrationsLayerTests`.

- [ ] **Step 8: Chequeo de formato y commit**

Corre el **chequeo de formato** de «Antes de empezar». Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
$locks = @(git ls-files --modified --others --exclude-standard -- '*packages.lock.json')
git add -- Backend.slnx src/Modules/Integrations tests/Modules/Integrations tests/ArchitectureTests/ArchitectureTests/IntegrationsLayerTests.cs tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj src/Api/Api.csproj src/Api/Program.cs src/Bootstrapper/Bootstrapper.csproj src/Bootstrapper/QepServiceCollectionExtensions.cs $locks
$text = @'
build(integrations): proyectos del módulo Integrations y pruebas de capas
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

Esperado: un commit sin trailer; `git status --short` limpio después (`bin/` y `obj/` ya están ignorados).

---

### Task 2: Catálogo de proveedores en código

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Domain/IHasFieldErrors.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationsDomainException.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationCatalog.cs` (`IntegrationCategory`, `FieldKind`, `FieldDefinition`, `IntegrationProvider`)
- Create: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationProviders.cs` (`ZenviaFieldKeys`, `IntegrationProviders`)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationCatalogTests.cs`

**Interfaces:**
- Consumes: `IntegrationsErrorCodes` (Task 1); `Modules.Tenancy.Domain.TenantModuleKey`, `TenantModuleKeys`, `TenantModuleSet` (existentes, `TenantModuleKey.cs:8-62`, `TenantModuleSet.cs:8-50`).
- Produces:
  - `namespace BuildingBlocks.Domain; public interface IHasFieldErrors { IReadOnlyDictionary<string, string[]> FieldErrors { get; } }`
  - `public sealed class IntegrationsDomainException : DomainException, IHasFieldErrors` con `(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null)` y `(string code, string message, Exception innerException)`.
  - `public enum IntegrationCategory { Messaging, Shipping, Ai }`; `public enum FieldKind { Text, Secret, Phone, Url }`.
  - `public sealed class FieldDefinition` — `FieldDefinition(string key, string label, FieldKind kind, bool required, int maxLength, string? pattern, string invalidMessage)`; `Key`, `Label`, `Kind`, `Required`, `MaxLength`, `Pattern` (`Regex?`), `InvalidMessage`, `IsSecret`; `const int KeyMaxLength = 40`; `static bool IsValidKey(string? key)`; `bool IsTooLong(string value)`; `bool HasValidShape(string value)`.
  - `public sealed class IntegrationProvider` — `IntegrationProvider(string key, string displayName, IntegrationCategory category, IReadOnlyList<TenantModuleKey> consumingModules, IReadOnlyList<FieldDefinition> fields, int maxConnections)`; `Key`, `DisplayName`, `Category`, `ConsumingModules`, `Fields`, `PublicFields`, `SecretFields`, `MaxConnections`; `const int KeyMaxLength = 32`; `FieldDefinition? FindField(string key)`; `bool IsVisibleFor(TenantModuleSet? modules)`.
  - `public static class ZenviaFieldKeys { ApiToken = "apiToken"; FromNumber = "fromNumber"; }`
  - `public static class IntegrationProviders` — `DefaultMaxConnections = 20`, `Zenvia`, `All`, `Find(string? key)`, `Parse(string key)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationCatalogTests.cs`:

```csharp
using BuildingBlocks.Domain;
using Modules.Integrations.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Catálogo» y «Visibilidad para un tenant»: la lista cerrada de la versión 1, la
/// forma de claves y campos, y quién ve qué proveedor.
/// </summary>
public sealed class IntegrationCatalogTests
{
    private static readonly IntegrationProvider WithoutConsumers = new(
        "orphan", "Sin consumidor", IntegrationCategory.Ai, [],
        [new FieldDefinition("apiKey", "Clave", FieldKind.Secret, required: true, maxLength: 64, pattern: null, invalidMessage: "Revisa la clave.")],
        maxConnections: 1);

    [Fact]
    public void TheFirstCatalogHasOnlyZenvia() =>
        Assert.Equal(["zenvia"], IntegrationProviders.All.Select(provider => provider.Key));

    [Fact]
    public void ZenviaDeclaresWhatTheSpecSays()
    {
        var zenvia = IntegrationProviders.Zenvia;

        Assert.Equal("Zenvia (WhatsApp)", zenvia.DisplayName);
        Assert.Equal(IntegrationCategory.Messaging, zenvia.Category);
        Assert.Equal([TenantModuleKeys.Quotations], zenvia.ConsumingModules);
        Assert.Equal(20, zenvia.MaxConnections);
        Assert.Equal(["apiToken", "fromNumber"], zenvia.Fields.Select(field => field.Key));

        var apiToken = zenvia.FindField(ZenviaFieldKeys.ApiToken);
        Assert.NotNull(apiToken);
        Assert.Equal(FieldKind.Secret, apiToken.Kind);
        Assert.True(apiToken.Required);
        Assert.Equal(512, apiToken.MaxLength);
        Assert.True(apiToken.IsSecret);

        var fromNumber = zenvia.FindField(ZenviaFieldKeys.FromNumber);
        Assert.NotNull(fromNumber);
        Assert.Equal(FieldKind.Phone, fromNumber.Kind);
        Assert.True(fromNumber.Required);
        Assert.Equal(15, fromNumber.MaxLength);

        Assert.Equal(["fromNumber"], zenvia.PublicFields.Select(field => field.Key));
        Assert.Equal(["apiToken"], zenvia.SecretFields.Select(field => field.Key));
    }

    [Fact]
    public void ParseAndFindAreOrdinal()
    {
        Assert.Same(IntegrationProviders.Zenvia, IntegrationProviders.Parse("zenvia"));
        Assert.Same(IntegrationProviders.Zenvia, IntegrationProviders.Find("zenvia"));
        Assert.Null(IntegrationProviders.Find("Zenvia"));
        Assert.Null(IntegrationProviders.Find(null));
        Assert.Throws<ArgumentException>(() => IntegrationProviders.Parse("whatsapp"));
    }

    [Fact]
    public void AProviderIsVisibleWhenOneOfItsModulesIsEnabled() =>
        Assert.True(IntegrationProviders.Zenvia.IsVisibleFor(TenantModuleSet.FromStored(TenantModuleKeys.All)));

    [Fact]
    public void AProviderIsHiddenWhenItsModulesAreOff() =>
        Assert.False(IntegrationProviders.Zenvia.IsVisibleFor(
            TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Quotations]))));

    // Contratado pero apagado por una dependencia: cuenta lo efectivo, como en TenantModuleGuard.
    [Fact]
    public void AContractedButIneffectiveModuleDoesNotMakeItVisible() =>
        Assert.False(IntegrationProviders.Zenvia.IsVisibleFor(TenantModuleSet.FromStored([TenantModuleKeys.Quotations])));

    [Fact]
    public void TheDevelopmentStubSeesEverything() =>
        Assert.True(IntegrationProviders.Zenvia.IsVisibleFor(null));

    [Fact]
    public void AProviderWithoutConsumersIsNeverVisible()
    {
        Assert.False(WithoutConsumers.IsVisibleFor(null));
        Assert.False(WithoutConsumers.IsVisibleFor(TenantModuleSet.FromStored(TenantModuleKeys.All)));
    }

    [Theory]
    [InlineData("z")]
    [InlineData("Zenvia")]
    [InlineData("zen_via")]
    [InlineData("a23456789012345678901234567890123")]
    public void AProviderKeyOutsideItsShapeIsAProgrammingError(string key) =>
        Assert.Throws<ArgumentException>(() => new IntegrationProvider(
            key, "X", IntegrationCategory.Messaging, [TenantModuleKeys.Quotations], WithoutConsumers.Fields, 1));

    [Theory]
    [InlineData("a")]
    [InlineData("1abc")]
    [InlineData("api-token")]
    [InlineData("api_token")]
    public void AFieldKeyOutsideItsShapeIsAProgrammingError(string key) =>
        Assert.Throws<ArgumentException>(() =>
            new FieldDefinition(key, "X", FieldKind.Text, required: false, maxLength: 10, pattern: null, invalidMessage: "X"));

    [Theory]
    [InlineData("573001234567", true)]
    [InlineData("5730012345", true)]
    [InlineData("+573001234567", false)]
    [InlineData("57300 12345", false)]
    [InlineData("573001234", false)]
    [InlineData("57300\u00001234567", false)]
    public void TheSenderNumberIsE164WithoutPlus(string value, bool valid) =>
        Assert.Equal(valid, IntegrationProviders.Zenvia.FindField(ZenviaFieldKeys.FromNumber)!.HasValidShape(value));

    [Theory]
    [InlineData("abc-DEF_123.xyz", true)]
    [InlineData("con espacio", false)]
    [InlineData("tab\tdentro", false)]
    [InlineData("ñandú", false)]
    public void TheApiTokenIsVisibleAsciiWithoutSpaces(string value, bool valid) =>
        Assert.Equal(valid, IntegrationProviders.Zenvia.FindField(ZenviaFieldKeys.ApiToken)!.HasValidShape(value));

    // Review Focus 1: un \0 llega a PostgreSQL como un 500; el catálogo lo rechaza antes, aunque el
    // campo no tenga patrón.
    [Fact]
    public void AControlCharacterIsNeverAValidShape()
    {
        var free = new FieldDefinition("note", "Nota", FieldKind.Text, required: false, maxLength: 40, pattern: null, invalidMessage: "X");

        Assert.True(free.HasValidShape("texto libre"));
        Assert.False(free.HasValidShape("a\u0000b"));
        Assert.False(free.HasValidShape("a\nb"));
    }

    [Fact]
    public void TooLongIsMeasuredInCharacters()
    {
        var fromNumber = IntegrationProviders.Zenvia.FindField(ZenviaFieldKeys.FromNumber)!;

        Assert.False(fromNumber.IsTooLong("123456789012345"));
        Assert.True(fromNumber.IsTooLong("1234567890123456"));
    }

    [Fact]
    public void TheDomainExceptionCarriesItsCodeAndOptionalFieldErrors()
    {
        DomainException plain = new IntegrationsDomainException(IntegrationsErrorCodes.NameTaken, "taken");
        var withFields = new IntegrationsDomainException(
            IntegrationsErrorCodes.CredentialsRejected,
            "rejected",
            new Dictionary<string, string[]> { ["secrets.apiToken"] = ["mensaje"] });

        Assert.Equal(IntegrationsErrorCodes.NameTaken, plain.Code);
        Assert.Empty(((IHasFieldErrors)plain).FieldErrors);
        Assert.Equal(["secrets.apiToken"], withFields.FieldErrors.Keys);
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~IntegrationCatalogTests"
```

Esperado: FAIL de compilación, `error CS0246: The type or namespace name 'IntegrationProvider' could not be found` (y `IHasFieldErrors`, `FieldDefinition`, `IntegrationProviders`).

- [ ] **Step 3: Implementar**

`src/BuildingBlocks/BuildingBlocks.Domain/IHasFieldErrors.cs`:

```csharp
namespace BuildingBlocks.Domain;

/// <summary>
/// Un error que sabe a qué campos del formulario se refiere. <c>ApiExceptionHandler</c> publica
/// <see cref="FieldErrors"/> como <c>errors</c>, con la misma forma que el 422 de FluentValidation,
/// que es el único mapa que el formulario sabe leer para marcar un input (spec 2026-10-08:
/// <c>credentials_rejected</c> marca <c>secrets.apiToken</c>). Las claves son las del cuerpo; los
/// mensajes nunca llevan el valor que se mandó.
/// </summary>
public interface IHasFieldErrors
{
    IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
```

`src/Modules/Integrations/Modules.Integrations.Domain/IntegrationsDomainException.cs`:

```csharp
using BuildingBlocks.Domain;

namespace Modules.Integrations.Domain;

/// <summary>
/// Regla de negocio de Integrations: <c>ApiExceptionHandler</c> la responde 422 con su código. Si el
/// error es de un campo (spec 2026-10-08, «Códigos de error»), lleva <see cref="FieldErrors"/>.
/// </summary>
public sealed class IntegrationsDomainException : DomainException, IHasFieldErrors
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public IntegrationsDomainException(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(code, message) =>
        FieldErrors = fieldErrors ?? NoFieldErrors;

    /// <summary>Para traducir una falla de base (el índice único del nombre) sin perder la original.</summary>
    public IntegrationsDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException) =>
        FieldErrors = NoFieldErrors;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
```

`src/Modules/Integrations/Modules.Integrations.Domain/IntegrationCatalog.cs`:

```csharp
using System.Text.RegularExpressions;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.Domain;

public enum IntegrationCategory
{
    Messaging,
    Shipping,
    Ai,
}

public enum FieldKind
{
    Text,
    Secret,
    Phone,
    Url,
}

/// <summary>
/// Un campo que el proveedor pide para conectarse (spec 2026-10-08, «Catálogo»). Su clave es la del
/// JSON y la de <c>fields.&lt;key&gt;</c> en el mapa <c>errors</c>. <see cref="Label"/> e
/// <see cref="InvalidMessage"/> los ve una persona: en español, tuteando, y nunca con el valor.
/// </summary>
public sealed class FieldDefinition
{
    public const int KeyMaxLength = 40;

    private static readonly Regex KeyShape = new(
        "^[a-zA-Z][a-zA-Z0-9]{1,39}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public FieldDefinition(
        string key,
        string label,
        FieldKind kind,
        bool required,
        int maxLength,
        string? pattern,
        string invalidMessage)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException($"'{key}' is not a valid field key: it must match {KeyShape}.", nameof(key));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidMessage);

        Key = key;
        Label = label;
        Kind = kind;
        Required = required;
        MaxLength = maxLength;
        Pattern = pattern is null ? null : new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        InvalidMessage = invalidMessage;
    }

    public string Key { get; }

    public string Label { get; }

    public FieldKind Kind { get; }

    public bool Required { get; }

    public int MaxLength { get; }

    public Regex? Pattern { get; }

    public string InvalidMessage { get; }

    public bool IsSecret => Kind == FieldKind.Secret;

    public static bool IsValidKey(string? key) => key is not null && KeyShape.IsMatch(key);

    public bool IsTooLong(string value) => value.Length > MaxLength;

    /// <summary>
    /// Sin caracteres de control —un <c>\0</c> en <c>text</c> o en <c>jsonb</c> lo rechaza PostgreSQL
    /// con un 500 (Review Focus 1)— y con el patrón del catálogo, si lo hay.
    /// </summary>
    public bool HasValidShape(string value) =>
        !value.Any(char.IsControl) && (Pattern is null || Pattern.IsMatch(value));
}

/// <summary>
/// Un proveedor del catálogo (spec 2026-10-08, «Catálogo»). Un proveedor nuevo es otra instancia en
/// <see cref="IntegrationProviders"/>, su probador y sus pruebas, más la migración que cambia el
/// <c>CHECK</c> de <c>provider_key</c> (D10).
/// </summary>
public sealed class IntegrationProvider
{
    public const int KeyMaxLength = 32;

    private static readonly Regex KeyShape = new(
        "^[a-z0-9-]{2,32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IntegrationProvider(
        string key,
        string displayName,
        IntegrationCategory category,
        IReadOnlyList<TenantModuleKey> consumingModules,
        IReadOnlyList<FieldDefinition> fields,
        int maxConnections)
    {
        if (!KeyShape.IsMatch(key))
        {
            throw new ArgumentException($"'{key}' is not a valid provider key: it must match {KeyShape}.", nameof(key));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);
        if (fields.Count == 0)
        {
            throw new ArgumentException("A provider needs at least one field.", nameof(fields));
        }

        if (fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != fields.Count)
        {
            throw new ArgumentException("Field keys must be unique within a provider.", nameof(fields));
        }

        Key = key;
        DisplayName = displayName;
        Category = category;
        ConsumingModules = consumingModules.ToArray();
        Fields = fields.ToArray();
        PublicFields = Fields.Where(field => !field.IsSecret).ToArray();
        SecretFields = Fields.Where(field => field.IsSecret).ToArray();
        MaxConnections = maxConnections;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public IntegrationCategory Category { get; }

    /// <summary>Los módulos que usan este proveedor; vacío = nunca visible.</summary>
    public IReadOnlyList<TenantModuleKey> ConsumingModules { get; }

    public IReadOnlyList<FieldDefinition> Fields { get; }

    /// <summary>Los que van a <c>fields</c> (jsonb).</summary>
    public IReadOnlyList<FieldDefinition> PublicFields { get; }

    /// <summary>Los que van a <c>connection_secrets</c>, cifrados.</summary>
    public IReadOnlyList<FieldDefinition> SecretFields { get; }

    /// <summary>Tope por tenant y proveedor (D1).</summary>
    public int MaxConnections { get; }

    public FieldDefinition? FindField(string key) =>
        Fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Spec 2026-10-08, «Visibilidad»: alguno de sus módulos consumidores está efectivo. Con el stub de
    /// desarrollo (<paramref name="modules"/> en <c>null</c>), todo; sin consumidores, nunca.
    /// </summary>
    public bool IsVisibleFor(TenantModuleSet? modules) =>
        ConsumingModules.Count > 0 && (modules is null || ConsumingModules.Any(modules.IsEnabled));
}
```

`src/Modules/Integrations/Modules.Integrations.Domain/IntegrationProviders.cs`:

```csharp
using Modules.Tenancy.Domain;

namespace Modules.Integrations.Domain;

public static class ZenviaFieldKeys
{
    public const string ApiToken = "apiToken";
    public const string FromNumber = "fromNumber";
}

/// <summary>
/// La lista cerrada de proveedores, como <see cref="TenantModuleKeys.All"/> (spec 2026-10-08,
/// «Catálogo»). La versión 1 tiene uno: OpenAI, Meta y transportadoras entran con el spec de su
/// consumidor, porque sin módulo consumidor nunca serían visibles (decisión 4).
/// </summary>
public static class IntegrationProviders
{
    /// <summary>D1: tope de conexiones por tenant y proveedor.</summary>
    public const int DefaultMaxConnections = 20;

    /// <summary>
    /// La cuenta de Zenvia y el número emisor. La plantilla de cotización no es campo de la conexión
    /// (D6): es del consumidor. Los patrones son los de <c>UpdateWhatsAppSettingsValidator</c> de
    /// <c>6612298</c>: token en ASCII visible y número E.164 sin «+».
    /// </summary>
    public static readonly IntegrationProvider Zenvia = new(
        "zenvia",
        "Zenvia (WhatsApp)",
        IntegrationCategory.Messaging,
        [TenantModuleKeys.Quotations],
        [
            new FieldDefinition(
                ZenviaFieldKeys.ApiToken,
                "API token",
                FieldKind.Secret,
                required: true,
                maxLength: 512,
                pattern: @"^[\x21-\x7E]+$",
                invalidMessage: "La clave sólo puede tener letras, números y símbolos, sin espacios: vuelve a copiarla de Zenvia."),
            new FieldDefinition(
                ZenviaFieldKeys.FromNumber,
                "Número emisor",
                FieldKind.Phone,
                required: true,
                maxLength: 15,
                pattern: "^[0-9]{10,15}$",
                invalidMessage: "Escribe el número con indicativo de país, sólo dígitos y sin '+' (entre 10 y 15)."),
        ],
        DefaultMaxConnections);

    public static readonly IReadOnlyList<IntegrationProvider> All = [Zenvia];

    /// <summary>Ordinal, como el <c>CHECK</c>: <c>"Zenvia"</c> no es <c>"zenvia"</c>.</summary>
    public static IntegrationProvider? Find(string? key) =>
        key is null
            ? null
            : All.FirstOrDefault(provider => string.Equals(provider.Key, key, StringComparison.Ordinal));

    /// <summary>Como <c>TenantModuleKey.Parse</c>: lanza con una clave desconocida.</summary>
    public static IntegrationProvider Parse(string key) =>
        Find(key) ?? throw new ArgumentException($"'{key}' is not a known integration provider key.", nameof(key));
}
```

- [ ] **Step 4: Ver el GREEN**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet build Backend.slnx --no-restore -v q; if ($LASTEXITCODE -ne 0) { throw "build falló (si el paso espera un RED de compilación, ése es el RED)" }
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --no-build --filter "FullyQualifiedName~IntegrationCatalogTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~IntegrationsLayerTests"
```

Esperado: PASS en las dos (la segunda confirma que el dominio sólo ve `Modules.Tenancy.Domain`).

- [ ] **Step 5: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/BuildingBlocks/BuildingBlocks.Domain/IHasFieldErrors.cs src/Modules/Integrations/Modules.Integrations.Domain tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationCatalogTests.cs
$text = @'
feat(integrations): catálogo de proveedores en código

Zenvia como único proveedor de la versión 1, visibilidad por módulos activos y
IHasFieldErrors para que un error de dominio pueda marcar un campo.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 3: `ProtectedSecret` movido y agregado `IntegrationConnection`

**Files:**
- Move: `6612298:src/Modules/Quotations/Modules.Quotations.Domain/ProtectedSecret.cs` → `src/Modules/Integrations/Modules.Integrations.Domain/ProtectedSecret.cs`
- Move: `6612298:tests/Modules/Quotations/Modules.Quotations.UnitTests/ProtectedSecretTests.cs` → `tests/Modules/Integrations/Modules.Integrations.UnitTests/ProtectedSecretTests.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationConnection.cs` (`ConnectionStatus`, `SecretSealer`, `ConnectionSecret`, `IntegrationConnection`)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/ConnectionFixtures.cs`, `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationConnectionTests.cs`

**Interfaces:**
- Consumes: `IntegrationProvider`, `IntegrationProviders.Zenvia`, `ZenviaFieldKeys`, `FieldDefinition.IsTooLong/HasValidShape`, `IntegrationsDomainException`, `IntegrationsErrorCodes` (Tasks 1-2).
- Produces:
  - `public sealed record ProtectedSecret(string KeyId, byte[] Ciphertext)` con `const int KeyIdMaxLength = 32`.
  - `public enum ConnectionStatus { Active, Paused, NeedsAttention }`
  - `public delegate ProtectedSecret SecretSealer(Guid connectionId, string fieldKey, string plaintext);`
  - `public sealed class ConnectionSecret` — `FieldKey`, `KeyId`, `UpdatedAt`, `ProtectedSecret Protected`; campo privado `_ciphertext` (lo mapea EF en la Task 11).
  - `public sealed class IntegrationConnection` — `const int NameMaxLength = 80`, `const int FailureCodeMaxLength = 64`; propiedades `Id` (`Guid`), `TenantId`, `ProviderKey`, `Name`, `Status`, `Fields` (`IReadOnlyDictionary<string, string>`), `Secrets` (`IReadOnlyList<ConnectionSecret>`), `LastVerifiedAt`, `LastFailureAt`, `LastFailureCode`, `CreatedAt`, `CreatedBy`, `UpdatedAt`, `Version` (`long`).
  - `static IntegrationConnection Create(IntegrationProvider provider, Guid tenantId, string name, IReadOnlyDictionary<string, string> fields, IReadOnlyDictionary<string, string> secrets, SecretSealer seal, Guid createdBy, DateTimeOffset now)`
  - `IReadOnlyList<string> Update(IntegrationProvider provider, string name, IReadOnlyDictionary<string, string> fields, IReadOnlyDictionary<string, string> secrets, SecretSealer seal, DateTimeOffset now, bool verified)`
  - `void Pause(DateTimeOffset now)`, `void EnsurePaused()`, `void Resume(DateTimeOffset now)`, `void MarkVerified(DateTimeOffset now)`, `void MarkNeedsAttention(string failureCode, DateTimeOffset now)`, `void RecordFailure(string failureCode, DateTimeOffset now)`, `void Reprotect(string fieldKey, ProtectedSecret secret, DateTimeOffset now)`.

- [ ] **Step 1: Traer `ProtectedSecret` y su prueba, sin editarlos, y commitear el traslado**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git checkout 6612298 -- src/Modules/Quotations/Modules.Quotations.Domain/ProtectedSecret.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ProtectedSecretTests.cs
git mv src/Modules/Quotations/Modules.Quotations.Domain/ProtectedSecret.cs src/Modules/Integrations/Modules.Integrations.Domain/ProtectedSecret.cs
git mv tests/Modules/Quotations/Modules.Quotations.UnitTests/ProtectedSecretTests.cs tests/Modules/Integrations/Modules.Integrations.UnitTests/ProtectedSecretTests.cs
git -C $Main status --short
git status --short
$text = @'
refactor(integrations): mover ProtectedSecret desde Quotations

Movido desde 6612298:src/Modules/Quotations/Modules.Quotations.Domain/ProtectedSecret.cs
y 6612298:tests/Modules/Quotations/Modules.Quotations.UnitTests/ProtectedSecretTests.cs,
sin cambios. La adaptación va en el commit siguiente.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

Esperado: antes del commit, `git status --short` muestra sólo las dos altas `A` en las rutas nuevas (nada bajo `Quotations`). El commit queda con `namespace Modules.Quotations.*` adentro: lo arregla el Step 4.

- [ ] **Step 2: Adaptar la prueba movida y escribir las del agregado**

Reemplaza el contenido de `tests/Modules/Integrations/Modules.Integrations.UnitTests/ProtectedSecretTests.cs`:

```csharp
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Un <see cref="ProtectedSecret"/> con una mitad nula no puede existir (spec 2026-10-08, «Secreto en
/// reposo»; viene de 6612298): sin esto, <c>TryUnprotect</c> rompería su contrato de no lanzar ante
/// una fila a medias.
/// </summary>
public sealed class ProtectedSecretTests
{
    [Fact]
    public void ANullKeyIdIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new ProtectedSecret(null!, [1, 2, 3]));

    [Fact]
    public void ANullCiphertextIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new ProtectedSecret("k1", null!));

    [Fact]
    public void ToStringShowsTheKeyButNeverTheBytes() =>
        Assert.Equal("ProtectedSecret { KeyId = k1 }", new ProtectedSecret("k1", [1, 2, 3]).ToString());
}
```

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/ConnectionFixtures.cs`:

```csharp
using System.Text;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>Valores fijos de las pruebas del agregado. Ninguno es un secreto real.</summary>
internal static class ConnectionFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Later = Now.AddHours(1);
    public static readonly Guid TenantId = Guid.CreateVersion7();
    public static readonly Guid MemberId = Guid.CreateVersion7();
    public const string Token = "zenvia-token-TEST-1";
    public const string FromNumber = "573001234567";

    public static Dictionary<string, string> Fields(string fromNumber = FromNumber) =>
        new(StringComparer.Ordinal) { [ZenviaFieldKeys.FromNumber] = fromNumber };

    public static Dictionary<string, string> Secrets(string token = Token) =>
        new(StringComparer.Ordinal) { [ZenviaFieldKeys.ApiToken] = token };

    public static Dictionary<string, string> None() => new(StringComparer.Ordinal);

    public static IntegrationConnection Create(RecordingSealer sealer, string name = "WhatsApp sede norte") =>
        IntegrationConnection.Create(
            IntegrationProviders.Zenvia, TenantId, name, Fields(), Secrets(), sealer.Seal, MemberId, Now);
}

/// <summary>
/// Un "cifrado" legible: deja ver con qué conexión, campo y texto se selló. El dominio nunca descifra,
/// así que alcanza con anotar.
/// </summary>
internal sealed class RecordingSealer
{
    public List<(Guid ConnectionId, string FieldKey, string Plaintext)> Calls { get; } = [];

    public string KeyId { get; set; } = "k1";

    public ProtectedSecret Seal(Guid connectionId, string fieldKey, string plaintext)
    {
        Calls.Add((connectionId, fieldKey, plaintext));
        return new ProtectedSecret(KeyId, Encoding.UTF8.GetBytes($"{connectionId:D}|{fieldKey}|{plaintext}"));
    }

    public static string Open(ProtectedSecret secret) => Encoding.UTF8.GetString(secret.Ciphertext);
}
```

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationConnectionTests.cs`:

```csharp
using Modules.Integrations.Domain;
using Modules.Tenancy.Domain;
using static Modules.Integrations.UnitTests.ConnectionFixtures;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Reglas del agregado» y «Estados y transiciones»: validar todo antes de asignar,
/// un secreto ausente conserva el guardado, <c>changedFields</c> por clave y nunca por valor, y las
/// transiciones con sus dos códigos.
/// </summary>
public sealed class IntegrationConnectionTests
{
    private static readonly IntegrationProvider OtherProvider = new(
        "otro", "Otro", IntegrationCategory.Messaging, [TenantModuleKeys.Quotations],
        [new FieldDefinition("apiKey", "Clave", FieldKind.Secret, required: true, maxLength: 64, pattern: null, invalidMessage: "Revisa la clave.")],
        maxConnections: 1);

    private static IntegrationsDomainException Rejects(Action action) =>
        Assert.Throws<IntegrationsDomainException>(action);

    private static IntegrationConnection CreateWith(
        Dictionary<string, string> fields, Dictionary<string, string> secrets, RecordingSealer? sealer = null) =>
        IntegrationConnection.Create(
            IntegrationProviders.Zenvia, TenantId, "Norte", fields, secrets, (sealer ?? new RecordingSealer()).Seal, MemberId, Now);

    [Fact]
    public void CreateIsActiveVerifiedAtVersionOneAndKeepsOnlyPublicFieldsInFields()
    {
        var connection = Create(new RecordingSealer());

        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(Now, connection.LastVerifiedAt);
        Assert.Equal(1, connection.Version);
        Assert.Equal("zenvia", connection.ProviderKey);
        Assert.Equal(TenantId, connection.TenantId);
        Assert.Equal(MemberId, connection.CreatedBy);
        Assert.Equal(Now, connection.CreatedAt);
        Assert.Equal(Now, connection.UpdatedAt);
        Assert.Equal(["fromNumber"], connection.Fields.Keys);
        Assert.Equal(FromNumber, connection.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Null(connection.LastFailureCode);
        var secret = Assert.Single(connection.Secrets);
        Assert.Equal(ZenviaFieldKeys.ApiToken, secret.FieldKey);
        Assert.Equal("k1", secret.KeyId);
        Assert.Equal(Now, secret.UpdatedAt);
    }

    // D7: el AAD lleva la conexión y el campo, así que el id existe antes de sellar.
    [Fact]
    public void CreateSealsEachSecretWithItsOwnConnectionAndField()
    {
        var sealer = new RecordingSealer();

        var connection = Create(sealer);

        var call = Assert.Single(sealer.Calls);
        Assert.Equal((connection.Id, ZenviaFieldKeys.ApiToken, Token), call);
        Assert.NotEqual(Guid.Empty, connection.Id);
    }

    [Fact]
    public void CreateTrimsTheNameAndTheValues()
    {
        var sealer = new RecordingSealer();

        var connection = IntegrationConnection.Create(
            IntegrationProviders.Zenvia, TenantId, "  Sede norte  ", Fields("  573001234567 "), Secrets(" tok-1 "),
            sealer.Seal, MemberId, Now);

        Assert.Equal("Sede norte", connection.Name);
        Assert.Equal("573001234567", connection.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Equal("tok-1", Assert.Single(sealer.Calls).Plaintext);
    }

    // Review Focus 1: \0 y saltos de línea no llegan a la base.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u0000b")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void CreateRejectsAnInvalidNameWithoutSealingAnything(string name)
    {
        var sealer = new RecordingSealer();

        var error = Rejects(() => Create(sealer, name));

        Assert.Equal(IntegrationsErrorCodes.NameInvalid, error.Code);
        Assert.Empty(sealer.Calls);
    }

    [Fact]
    public void CreateRejectsAMissingOrBlankRequiredField()
    {
        Assert.Equal(IntegrationsErrorCodes.FieldRequired, Rejects(() => CreateWith(None(), Secrets())).Code);
        Assert.Equal(IntegrationsErrorCodes.FieldRequired, Rejects(() => CreateWith(Fields("   "), Secrets())).Code);
        Assert.Equal(IntegrationsErrorCodes.FieldRequired, Rejects(() => CreateWith(Fields(), None())).Code);
    }

    // Review Focus 2: un secreto mandado como campo público terminaría en jsonb, en claro.
    [Fact]
    public void CreateRejectsASecretSentAsAPublicFieldAndAnyUnknownKey()
    {
        var withSecretInFields = Fields();
        withSecretInFields[ZenviaFieldKeys.ApiToken] = Token;
        var withUnknownField = Fields();
        withUnknownField["templateId"] = "x";
        var withPublicInSecrets = Secrets();
        withPublicInSecrets[ZenviaFieldKeys.FromNumber] = FromNumber;

        Assert.Equal(IntegrationsErrorCodes.FieldUnknown, Rejects(() => CreateWith(withSecretInFields, Secrets())).Code);
        Assert.Equal(IntegrationsErrorCodes.FieldUnknown, Rejects(() => CreateWith(withUnknownField, Secrets())).Code);
        Assert.Equal(IntegrationsErrorCodes.FieldUnknown, Rejects(() => CreateWith(Fields(), withPublicInSecrets)).Code);
    }

    [Theory]
    [InlineData("+573001234567")]
    [InlineData("57300123")]
    [InlineData("5730012345678901")]
    [InlineData("57300\u00001234567")]
    public void CreateRejectsAFieldWithoutItsShapeOrTooLong(string fromNumber)
    {
        var sealer = new RecordingSealer();

        var error = Rejects(() => CreateWith(Fields(fromNumber), Secrets(), sealer));

        Assert.Equal(IntegrationsErrorCodes.FieldInvalid, error.Code);
        Assert.DoesNotContain(fromNumber, error.Message, StringComparison.Ordinal);
        Assert.Empty(sealer.Calls);
    }

    // D5: un secreto ausente conserva el guardado.
    [Fact]
    public void UpdateWithAnAbsentSecretKeepsTheStoredOneAndReportsOnlyWhatChanged()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);
        var stored = connection.Secrets[0].Protected;

        var changed = connection.Update(
            IntegrationProviders.Zenvia, "WhatsApp sede norte", Fields("573009999999"), None(), sealer.Seal, Later, verified: false);

        Assert.Equal(["fromNumber"], changed);
        Assert.Equal("573009999999", connection.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Equal(stored.Ciphertext, connection.Secrets[0].Protected.Ciphertext);
        Assert.Equal(Now, connection.Secrets[0].UpdatedAt);
        Assert.Equal(2, connection.Version);
        Assert.Equal(Later, connection.UpdatedAt);
    }

    [Fact]
    public void UpdateWithANewSecretReplacesItAndReportsItsKeyNeverItsValue()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);

        var changed = connection.Update(
            IntegrationProviders.Zenvia, "WhatsApp sede norte", Fields(), Secrets("nuevo-token"), sealer.Seal, Later, verified: true);

        Assert.Equal(["apiToken"], changed);
        Assert.DoesNotContain("nuevo-token", changed);
        Assert.EndsWith("|nuevo-token", RecordingSealer.Open(connection.Secrets[0].Protected), StringComparison.Ordinal);
        Assert.Equal(Later, connection.Secrets[0].UpdatedAt);
        Assert.Equal(Later, connection.LastVerifiedAt);
    }

    [Fact]
    public void UpdateRenamingReportsTheNameKey()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);

        Assert.Equal(["name"], connection.Update(
            IntegrationProviders.Zenvia, "Sede sur", Fields(), None(), sealer.Seal, Later, verified: false));
        Assert.Equal("Sede sur", connection.Name);
    }

    // Sin cambios no hay versión nueva: si no, quien tiene la pantalla abierta recibiría un 412 falso.
    [Fact]
    public void UpdateWithoutChangesIsANoOp()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);

        var changed = connection.Update(
            IntegrationProviders.Zenvia, " WhatsApp sede norte ", Fields(), None(), sealer.Seal, Later, verified: false);

        Assert.Empty(changed);
        Assert.Equal(1, connection.Version);
        Assert.Equal(Now, connection.UpdatedAt);
    }

    [Fact]
    public void AVerifiedUpdateBringsNeedsAttentionBackToActive()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);
        connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, Now);

        connection.Update(
            IntegrationProviders.Zenvia, "WhatsApp sede norte", Fields("573009999999"), None(), sealer.Seal, Later, verified: true);

        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(Later, connection.LastVerifiedAt);
    }

    [Fact]
    public void UpdateWithAnotherProviderIsAProgrammingError()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);

        Assert.Throws<ArgumentException>(() => connection.Update(
            OtherProvider, "Norte", None(), None(), sealer.Seal, Later, verified: false));
    }

    [Fact]
    public void PauseMovesActiveToPausedAndOnlyFromActive()
    {
        var connection = Create(new RecordingSealer());

        connection.Pause(Later);

        Assert.Equal(ConnectionStatus.Paused, connection.Status);
        Assert.Equal(2, connection.Version);
        Assert.Equal(IntegrationsErrorCodes.NotActive, Rejects(() => connection.Pause(Later)).Code);

        var attention = Create(new RecordingSealer());
        attention.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, Now);
        Assert.Equal(IntegrationsErrorCodes.NotActive, Rejects(() => attention.Pause(Later)).Code);
    }

    [Fact]
    public void ResumeOnlyFromPausedAndMarksItVerified()
    {
        var connection = Create(new RecordingSealer());

        Assert.Equal(IntegrationsErrorCodes.NotPaused, Rejects(() => connection.Resume(Later)).Code);
        Assert.Equal(IntegrationsErrorCodes.NotPaused, Rejects(connection.EnsurePaused).Code);

        connection.Pause(Now);
        connection.EnsurePaused();
        connection.Resume(Later);

        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(Later, connection.LastVerifiedAt);
    }

    [Fact]
    public void MarkNeedsAttentionRecordsTheFailure()
    {
        var connection = Create(new RecordingSealer());

        connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, Later);

        Assert.Equal(ConnectionStatus.NeedsAttention, connection.Status);
        Assert.Equal(Later, connection.LastFailureAt);
        Assert.Equal("credentials_rejected", connection.LastFailureCode);
        Assert.Equal(2, connection.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void AFailureCodeMustFitItsColumn(string code)
    {
        var connection = Create(new RecordingSealer());

        Assert.ThrowsAny<ArgumentException>(() => connection.MarkNeedsAttention(code, Later));
        Assert.ThrowsAny<ArgumentException>(() => connection.RecordFailure(code, Later));
    }

    [Fact]
    public void MarkVerifiedBringsNeedsAttentionBackAndRecordFailureKeepsTheStatus()
    {
        var connection = Create(new RecordingSealer());

        connection.RecordFailure(ConnectionFailureCodes.ProviderUnreachable, Now);
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal("provider_unreachable", connection.LastFailureCode);

        connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, Now);
        connection.MarkVerified(Later);
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(Later, connection.LastVerifiedAt);
    }

    // P16: re-cifrar con otra llave sube la versión (un PUT en el medio choca) pero no cambia la fecha
    // en que se configuró el secreto.
    [Fact]
    public void ReprotectChangesTheKeyButNotWhenTheSecretWasSet()
    {
        var connection = Create(new RecordingSealer());
        var rekeyed = new RecordingSealer { KeyId = "k2" }.Seal(connection.Id, ZenviaFieldKeys.ApiToken, Token);

        connection.Reprotect(ZenviaFieldKeys.ApiToken, rekeyed, Later);

        Assert.Equal("k2", connection.Secrets[0].KeyId);
        Assert.Equal(Now, connection.Secrets[0].UpdatedAt);
        Assert.Equal(2, connection.Version);
        Assert.Throws<InvalidOperationException>(() => connection.Reprotect(ZenviaFieldKeys.FromNumber, rekeyed, Later));
    }

    [Fact]
    public void ASecretPrintsItsKeyButNeverItsBytes() =>
        Assert.Equal(
            "ConnectionSecret { FieldKey = apiToken, KeyId = k1 }",
            Create(new RecordingSealer()).Secrets[0].ToString());
}
```

- [ ] **Step 3: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~IntegrationConnectionTests|FullyQualifiedName~ProtectedSecretTests"
```

Esperado: FAIL de compilación: `error CS0246: … 'IntegrationConnection' could not be found` y `'ProtectedSecret' could not be found` (sigue en `namespace Modules.Quotations.Domain`).

- [ ] **Step 4: Adaptar `ProtectedSecret` y escribir el agregado**

Reemplaza el contenido de `src/Modules/Integrations/Modules.Integrations.Domain/ProtectedSecret.cs`:

```csharp
namespace Modules.Integrations.Domain;

/// <summary>
/// Un secreto cifrado y el id de la llave que lo cifró (spec 2026-10-08, «Secreto en reposo»; viene de
/// Quotations, 6612298). Opaco para el dominio: cifrar y descifrar es de <c>ISecretProtector</c>, en
/// Infrastructure; acá sólo se guarda y se reemplaza.
/// </summary>
public sealed record ProtectedSecret(string KeyId, byte[] Ciphertext)
{
    // Una mitad nula no existe. Sin esto, una fila con una sola de las dos columnas llegaría hasta
    // TryUnprotect, que promete no lanzar. Se redeclaran las propiedades posicionales para validar al
    // construir.
    public string KeyId { get; init; } = KeyId ?? throw new ArgumentNullException(nameof(KeyId));

    public byte[] Ciphertext { get; init; } = Ciphertext ?? throw new ArgumentNullException(nameof(Ciphertext));

    /// <summary>El ancho de <c>connection_secrets.key_id</c> y del patrón de ids <c>^[a-z0-9]{1,32}$</c>.</summary>
    public const int KeyIdMaxLength = 32;

    // El ToString de un record imprime todas sus propiedades. Los bytes no le sirven a nadie en un log.
    public override string ToString() => $"ProtectedSecret {{ KeyId = {KeyId} }}";
}
```

Crea `src/Modules/Integrations/Modules.Integrations.Domain/IntegrationConnection.cs`:

```csharp
namespace Modules.Integrations.Domain;

public enum ConnectionStatus
{
    Active,
    Paused,
    NeedsAttention,
}

/// <summary>
/// Cifra un secreto para una conexión y un campo (AAD, D7). Lo cumple <c>ISecretProtector.Protect</c>:
/// el dominio sella sin ver la llave ni guardar el texto plano.
/// </summary>
public delegate ProtectedSecret SecretSealer(Guid connectionId, string fieldKey, string plaintext);

/// <summary>Una fila de <c>integrations.connection_secrets</c> (D9).</summary>
public sealed class ConnectionSecret
{
    // EF lee y escribe los bytes por este campo: un byte[] público invitaría a mutarlo por afuera.
    private byte[] _ciphertext = [];

    private ConnectionSecret()
    {
    }

    internal ConnectionSecret(string fieldKey, ProtectedSecret secret, DateTimeOffset updatedAt)
    {
        FieldKey = fieldKey;
        KeyId = secret.KeyId;
        _ciphertext = secret.Ciphertext;
        UpdatedAt = updatedAt;
    }

    public string FieldKey { get; private set; } = string.Empty;

    public string KeyId { get; private set; } = string.Empty;

    /// <summary>Cuándo se guardó este valor («Configurada el …»). Re-cifrarlo no lo cambia.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public ProtectedSecret Protected => new(KeyId, _ciphertext);

    internal void Replace(ProtectedSecret secret, DateTimeOffset updatedAt)
    {
        KeyId = secret.KeyId;
        _ciphertext = secret.Ciphertext;
        UpdatedAt = updatedAt;
    }

    internal void Reprotect(ProtectedSecret secret)
    {
        KeyId = secret.KeyId;
        _ciphertext = secret.Ciphertext;
    }

    public override string ToString() => $"ConnectionSecret {{ FieldKey = {FieldKey}, KeyId = {KeyId} }}";
}

/// <summary>
/// Una conexión del tenant con un proveedor (spec 2026-10-08, «Conexión»). Valida todo antes de
/// asignar nada, guarda los campos públicos en <see cref="Fields"/> y los secretos sellados en
/// <see cref="Secrets"/>. Nunca devuelve ni registra un valor: <c>changedFields</c> va por clave.
/// </summary>
public sealed class IntegrationConnection
{
    public const int NameMaxLength = 80;
    public const int FailureCodeMaxLength = 64;

    private const string NameKey = "name";

    private readonly List<ConnectionSecret> _secrets = [];

    private IntegrationConnection()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string ProviderKey { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public ConnectionStatus Status { get; private set; }

    public IReadOnlyDictionary<string, string> Fields { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<ConnectionSecret> Secrets => _secrets;

    public DateTimeOffset? LastVerifiedAt { get; private set; }

    public DateTimeOffset? LastFailureAt { get; private set; }

    public string? LastFailureCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>MemberId de quien la creó.</summary>
    public Guid CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>
    /// Una conexión <c>Active</c> y ya verificada: sólo se llama después de una prueba exitosa contra
    /// el proveedor (decisión 5). Genera el id antes de sellar porque el AAD lo lleva (D7).
    /// </summary>
    public static IntegrationConnection Create(
        IntegrationProvider provider,
        Guid tenantId,
        string name,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        SecretSealer seal,
        Guid createdBy,
        DateTimeOffset now)
    {
        var normalizedName = NormalizeName(name);
        var normalizedFields = Normalize(provider, fields, secret: false, requireAll: true);
        var normalizedSecrets = Normalize(provider, secrets, secret: true, requireAll: true);

        var connection = new IntegrationConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ProviderKey = provider.Key,
            Name = normalizedName,
            Status = ConnectionStatus.Active,
            Fields = normalizedFields,
            LastVerifiedAt = now,
            CreatedAt = now,
            CreatedBy = createdBy,
            UpdatedAt = now,
            Version = 1,
        };

        foreach (var (key, plaintext) in normalizedSecrets)
        {
            connection._secrets.Add(new ConnectionSecret(key, seal(connection.Id, key, plaintext), now));
        }

        return connection;
    }

    /// <summary>
    /// Reemplaza nombre y campos públicos; un secreto ausente conserva el guardado (D5). Con
    /// <paramref name="verified"/> la prueba ya pasó: anota la verificación y saca de
    /// <c>NeedsAttention</c>. Sin cambios y sin verificación es un no-op: no hay versión nueva.
    /// </summary>
    /// <returns>Las claves que cambiaron (<c>name</c>, campos y secretos que llegaron), ordenadas.</returns>
    public IReadOnlyList<string> Update(
        IntegrationProvider provider,
        string name,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        SecretSealer seal,
        DateTimeOffset now,
        bool verified)
    {
        if (!string.Equals(provider.Key, ProviderKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("The provider does not match the connection.", nameof(provider));
        }

        var normalizedName = NormalizeName(name);
        var normalizedFields = Normalize(provider, fields, secret: false, requireAll: true);
        var replacedSecrets = Normalize(provider, secrets, secret: true, requireAll: false);

        var changed = new SortedSet<string>(StringComparer.Ordinal);
        if (!string.Equals(normalizedName, Name, StringComparison.Ordinal))
        {
            changed.Add(NameKey);
        }

        foreach (var key in normalizedFields.Keys.Union(Fields.Keys, StringComparer.Ordinal))
        {
            if (!normalizedFields.TryGetValue(key, out var next)
                || !Fields.TryGetValue(key, out var current)
                || !string.Equals(next, current, StringComparison.Ordinal))
            {
                changed.Add(key);
            }
        }

        // P8: un secreto que llegó cuenta como cambio; no se descifra el guardado para comparar.
        changed.UnionWith(replacedSecrets.Keys);

        if (changed.Count == 0 && !verified)
        {
            return [];
        }

        Name = normalizedName;
        Fields = normalizedFields;
        foreach (var (key, plaintext) in replacedSecrets)
        {
            var protectedSecret = seal(Id, key, plaintext);
            var existing = _secrets.Find(secret => string.Equals(secret.FieldKey, key, StringComparison.Ordinal));
            if (existing is null)
            {
                _secrets.Add(new ConnectionSecret(key, protectedSecret, now));
            }
            else
            {
                existing.Replace(protectedSecret, now);
            }
        }

        if (verified)
        {
            LastVerifiedAt = now;
            if (Status == ConnectionStatus.NeedsAttention)
            {
                Status = ConnectionStatus.Active;
            }
        }

        Touch(now);
        return changed.ToArray();
    }

    public void Pause(DateTimeOffset now)
    {
        if (Status != ConnectionStatus.Active)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.NotActive, "Only an active connection can be paused.");
        }

        Status = ConnectionStatus.Paused;
        Touch(now);
    }

    /// <summary>Lo que <c>resume</c> exige antes de llamar al proveedor (D4).</summary>
    public void EnsurePaused()
    {
        if (Status != ConnectionStatus.Paused)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.NotPaused, "Only a paused connection can be resumed.");
        }
    }

    /// <summary>Después de una prueba exitosa (D4).</summary>
    public void Resume(DateTimeOffset now)
    {
        EnsurePaused();
        Status = ConnectionStatus.Active;
        LastVerifiedAt = now;
        Touch(now);
    }

    public void MarkVerified(DateTimeOffset now)
    {
        LastVerifiedAt = now;
        if (Status == ConnectionStatus.NeedsAttention)
        {
            Status = ConnectionStatus.Active;
        }

        Touch(now);
    }

    public void MarkNeedsAttention(string failureCode, DateTimeOffset now)
    {
        EnsureFailureCode(failureCode);
        Status = ConnectionStatus.NeedsAttention;
        LastFailureAt = now;
        LastFailureCode = failureCode;
        Touch(now);
    }

    /// <summary>Anota una prueba fallida sin cambiar el estado (P10).</summary>
    public void RecordFailure(string failureCode, DateTimeOffset now)
    {
        EnsureFailureCode(failureCode);
        LastFailureAt = now;
        LastFailureCode = failureCode;
        Touch(now);
    }

    /// <summary>
    /// El mismo valor, cifrado con otra llave (el worker de rotación). Sube la versión para que un
    /// <c>PUT</c> en el medio choque (P16); no cambia la fecha en que se configuró el secreto.
    /// </summary>
    public void Reprotect(string fieldKey, ProtectedSecret secret, DateTimeOffset now)
    {
        var existing = _secrets.Find(stored => string.Equals(stored.FieldKey, fieldKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The connection has no secret '{fieldKey}' to re-encrypt.");
        existing.Reprotect(secret);
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    private static void EnsureFailureCode(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (failureCode.Length > FailureCodeMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureCode), $"A failure code is at most {FailureCodeMaxLength} characters.");
        }
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > NameMaxLength || trimmed.Any(char.IsControl))
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.NameInvalid,
                $"A connection name is 1 to {NameMaxLength} characters on a single line.");
        }

        return trimmed;
    }

    private static Dictionary<string, string> Normalize(
        IntegrationProvider provider, IReadOnlyDictionary<string, string> values, bool secret, bool requireAll)
    {
        foreach (var key in values.Keys)
        {
            if (provider.FindField(key) is not { } known || known.IsSecret != secret)
            {
                throw new IntegrationsDomainException(
                    IntegrationsErrorCodes.FieldUnknown,
                    secret
                        ? "A secret was sent that the provider does not declare as secret."
                        : "A field was sent that the provider does not declare as public.");
            }
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in secret ? provider.SecretFields : provider.PublicFields)
        {
            var value = values.TryGetValue(definition.Key, out var raw) ? raw.Trim() : string.Empty;
            if (value.Length == 0)
            {
                if (requireAll && definition.Required)
                {
                    throw new IntegrationsDomainException(
                        IntegrationsErrorCodes.FieldRequired, $"The field '{definition.Key}' is required.");
                }

                continue;
            }

            if (definition.IsTooLong(value) || !definition.HasValidShape(value))
            {
                throw new IntegrationsDomainException(
                    IntegrationsErrorCodes.FieldInvalid, $"The field '{definition.Key}' does not have a valid value.");
            }

            normalized[definition.Key] = value;
        }

        return normalized;
    }
}
```

- [ ] **Step 5: Ver el GREEN**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~IntegrationConnectionTests|FullyQualifiedName~ProtectedSecretTests"
```

Esperado: PASS de todas.

- [ ] **Step 6: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/Modules/Integrations/Modules.Integrations.Domain tests/Modules/Integrations/Modules.Integrations.UnitTests
$text = @'
feat(integrations): agregado IntegrationConnection

ProtectedSecret pasa al namespace de Integrations. El agregado valida todo antes
de asignar, sella cada secreto con su conexión y su campo, conserva el secreto
ausente y devuelve changedFields por clave.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 4: Protector AES movido, `ISecretProtector` y opciones

**Files:**
- Move: los cuatro de producción y las dos pruebas de la tabla «Archivos que se mueven» marcados Task 4
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs`
- Modify: `src/Api/appsettings.example.json:102-105`
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/AesGcmSecretProtectorTests.cs`, `tests/Modules/Integrations/Modules.Integrations.UnitTests/SecretProtectionOptionsValidatorTests.cs`, `tests/ArchitectureTests/ArchitectureTests/ConfigurationExampleTests.cs` (sin cambios; se corre)

**Interfaces:**
- Consumes: `ProtectedSecret` (Task 3).
- Produces:
  - `namespace Modules.Integrations.Application; public interface ISecretProtector { string? ActiveKeyId { get; } bool HasKey(string keyId); ProtectedSecret Protect(Guid connectionId, string fieldKey, string plaintext); string Unprotect(Guid connectionId, string fieldKey, ProtectedSecret secret); bool TryUnprotect(Guid connectionId, string fieldKey, ProtectedSecret secret, [NotNullWhen(true)] out string? plaintext); }`
  - `namespace Modules.Integrations.Infrastructure.SecretProtection; public sealed class SecretProtectionOptions` con `const string SectionName = "Integrations:SecretProtection"`, `string? ActiveKeyId`, `Dictionary<string, string?> Keys` (la Task 12 suma `RekeyIntervalMinutes`).
  - `internal sealed class AesGcmSecretProtector(IOptions<SecretProtectionOptions>) : ISecretProtector`; `internal sealed partial class SecretProtectionOptionsValidator(IHostEnvironment) : IValidateOptions<SecretProtectionOptions>`.
  - Registro singleton de `ISecretProtector`, opciones con `ValidateOnStart`.

- [ ] **Step 1: Traer los seis archivos, sin editarlos, y commitear el traslado**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
$moves = @(
  @("src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppSecretProtector.cs", "src/Modules/Integrations/Modules.Integrations.Application/ISecretProtector.cs"),
  @("src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/AesGcmWhatsAppSecretProtector.cs", "src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/AesGcmSecretProtector.cs"),
  @("src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptions.cs", "src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/SecretProtectionOptions.cs"),
  @("src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptionsValidator.cs", "src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/SecretProtectionOptionsValidator.cs"),
  @("tests/Modules/Quotations/Modules.Quotations.UnitTests/AesGcmWhatsAppSecretProtectorTests.cs", "tests/Modules/Integrations/Modules.Integrations.UnitTests/AesGcmSecretProtectorTests.cs"),
  @("tests/Modules/Quotations/Modules.Quotations.UnitTests/SecretProtectionOptionsValidatorTests.cs", "tests/Modules/Integrations/Modules.Integrations.UnitTests/SecretProtectionOptionsValidatorTests.cs")
)
New-Item -ItemType Directory -Force "src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection" | Out-Null
foreach ($move in $moves) {
  git checkout 6612298 -- $move[0]
  git mv $move[0] $move[1]
}
git -C $Main status --short
git status --short
$text = @'
refactor(integrations): mover el protector AES y sus opciones desde Quotations

Movidos sin cambios desde 6612298:
- src/Modules/Quotations/Modules.Quotations.Application/IWhatsAppSecretProtector.cs
- src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/AesGcmWhatsAppSecretProtector.cs
- src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptions.cs
- src/Modules/Quotations/Modules.Quotations.Infrastructure/SecretProtection/SecretProtectionOptionsValidator.cs
- tests/Modules/Quotations/Modules.Quotations.UnitTests/AesGcmWhatsAppSecretProtectorTests.cs
- tests/Modules/Quotations/Modules.Quotations.UnitTests/SecretProtectionOptionsValidatorTests.cs
La adaptación va en el commit siguiente.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

Esperado: seis altas `A` en las rutas nuevas, nada bajo `Quotations`.

- [ ] **Step 2: Adaptar las pruebas movidas**

Reemplaza el contenido de `tests/Modules/Integrations/Modules.Integrations.UnitTests/AesGcmSecretProtectorTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.SecretProtection;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Secreto en reposo» (viene de 6612298): nonce aleatorio por cifrado, AAD atado a la
/// conexión y al campo (D7), llave elegida por el <c>KeyId</c> de la fila, y errores que nombran la
/// clave de configuración y nunca un valor.
/// </summary>
public sealed class AesGcmSecretProtectorTests
{
    private static readonly byte[] K1 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] K2 = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private const string FieldKey = "apiToken";
    private const string Token = "zenvia-token-SENTINEL-123";

    private static AesGcmSecretProtector Protector(string? active, params (string Id, byte[]? Key)[] keys)
    {
        var options = new SecretProtectionOptions { ActiveKeyId = active };
        foreach (var (id, key) in keys)
        {
            options.Keys[id] = key is null ? "" : Convert.ToBase64String(key);
        }

        return new AesGcmSecretProtector(Options.Create(options));
    }

    [Fact]
    public void ProtectThenUnprotectRoundTrips()
    {
        var protector = Protector("k1", ("k1", K1));

        var secret = protector.Protect(ConnectionId, FieldKey, Token);

        Assert.Equal("k1", secret.KeyId);
        Assert.Equal(Token, protector.Unprotect(ConnectionId, FieldKey, secret));
    }

    [Fact]
    public void TwoProtectionsOfTheSameTextDiffer()
    {
        var protector = Protector("k1", ("k1", K1));

        var first = protector.Protect(ConnectionId, FieldKey, Token);
        var second = protector.Protect(ConnectionId, FieldKey, Token);

        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.Equal(12 + Encoding.UTF8.GetByteCount(Token) + 16, first.Ciphertext.Length);
    }

    [Fact]
    public void AnotherConnectionCannotUnprotect()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(ConnectionId, FieldKey, Token);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Guid.CreateVersion7(), FieldKey, secret));
    }

    // D7: un texto cifrado copiado a otro campo de la misma conexión tampoco descifra.
    [Fact]
    public void AnotherFieldCannotUnprotect()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(ConnectionId, FieldKey, Token);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(ConnectionId, "otherSecret", secret));
    }

    // El AAD es exactamente "integrations.connection:" + connectionId en formato D + ":" + fieldKey: un
    // texto cifrado a mano con ese AAD descifra.
    [Fact]
    public void TheAssociatedDataIsThePrefixTheConnectionInFormatDAndTheField()
    {
        var protector = Protector("k1", ("k1", K1));
        var plaintext = Encoding.UTF8.GetBytes(Token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(K1, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag,
                Encoding.UTF8.GetBytes("integrations.connection:" + ConnectionId.ToString("D") + ":" + FieldKey));
        }

        var manual = new ProtectedSecret("k1", [.. nonce, .. ciphertext, .. tag]);

        Assert.Equal(Token, protector.Unprotect(ConnectionId, FieldKey, manual));
    }

    [Fact]
    public void AnUnknownKeyIdThrowsNamingTheConfigurationKeyAndNoKey()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = new ProtectedSecret("k9", new byte[40]);

        var error = Assert.Throws<InvalidOperationException>(() => protector.Unprotect(ConnectionId, FieldKey, secret));

        Assert.Contains("Integrations:SecretProtection:Keys:k9", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(K1), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterChangingTheActiveKeyOldSecretsReadWithTheirKeyAndNewOnesUseTheNewKey()
    {
        var old = Protector("k1", ("k1", K1)).Protect(ConnectionId, FieldKey, Token);
        var rotated = Protector("k2", ("k1", K1), ("k2", K2));

        var fresh = rotated.Protect(ConnectionId, FieldKey, Token);

        Assert.Equal(Token, rotated.Unprotect(ConnectionId, FieldKey, old));
        Assert.Equal("k2", fresh.KeyId);
        Assert.Equal(Token, rotated.Unprotect(ConnectionId, FieldKey, fresh));
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
        var error = Assert.Throws<InvalidOperationException>(() => protector.Protect(ConnectionId, FieldKey, Token));
        Assert.Contains("Integrations:SecretProtection:ActiveKeyId", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryUnprotectIsFalseWithoutThrowingWhenTheKeyIsMissing()
    {
        var secret = Protector("k1", ("k1", K1)).Protect(ConnectionId, FieldKey, Token);

        Assert.False(Protector("k2", ("k2", K2)).TryUnprotect(ConnectionId, FieldKey, secret, out var plaintext));
        Assert.Null(plaintext);
    }

    [Fact]
    public void TryUnprotectIsFalseWhenAByteWasAltered()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(ConnectionId, FieldKey, Token);
        var damaged = secret.Ciphertext.ToArray();
        damaged[20] ^= 0xFF;

        Assert.False(protector.TryUnprotect(ConnectionId, FieldKey, secret with { Ciphertext = damaged }, out _));
    }

    [Fact]
    public void TryUnprotectIsFalseForAnotherConnectionOrField()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(ConnectionId, FieldKey, Token);

        Assert.False(protector.TryUnprotect(Guid.CreateVersion7(), FieldKey, secret, out _));
        Assert.False(protector.TryUnprotect(ConnectionId, "otherSecret", secret, out _));
    }

    [Fact]
    public void TheCiphertextDoesNotContainTheTokenBytes()
    {
        var secret = Protector("k1", ("k1", K1)).Protect(ConnectionId, FieldKey, Token);

        Assert.True(secret.Ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Token)) < 0);
    }
}
```

Reemplaza el contenido de `tests/Modules/Integrations/Modules.Integrations.UnitTests/SecretProtectionOptionsValidatorTests.cs`:

```csharp
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Integrations.Infrastructure.SecretProtection;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Secreto en reposo» (viene de 6612298): en producción todo exigido —una llave mal
/// pegada en el pipeline se descubre en el deploy—; fuera de producción sólo la activa, para que unos
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
        Assert.Contains("Integrations:SecretProtection:ActiveKeyId", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ProductionWithoutTheActiveKeyValueFails(string? value)
    {
        var result = ValidatorFor(Environments.Production).Validate(null, Options("k1", ("k1", value)));

        Assert.True(result.Failed);
        Assert.Contains("Integrations:SecretProtection:Keys:k1", result.FailureMessage, StringComparison.Ordinal);
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
        Assert.Contains($"Integrations:SecretProtection:Keys:{id}", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadKeyValues))]
    public void ProductionRejectsAnyDeclaredKeyThatIsNot32Bytes(string bad)
    {
        var result = ValidatorFor(Environments.Production)
            .Validate(null, Options("k1", ("k1", GoodKey), ("k2", bad)));

        Assert.True(result.Failed);
        Assert.Contains("Integrations:SecretProtection:Keys:k2", result.FailureMessage, StringComparison.Ordinal);
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
        Assert.Contains("Integrations:SecretProtection:Keys:k1", result.FailureMessage, StringComparison.Ordinal);
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

    internal sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Modules.Integrations.UnitTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
```

- [ ] **Step 3: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~AesGcmSecretProtectorTests|FullyQualifiedName~SecretProtectionOptionsValidatorTests"
```

Esperado: FAIL de compilación: los cuatro archivos de producción movidos siguen en `namespace Modules.Quotations.*` y nombran `Modules.Quotations.Application`/`Domain` (`error CS0234`/`CS0246`); las pruebas nombran `AesGcmSecretProtector` en el namespace nuevo (`CS0246`).

- [ ] **Step 4: Adaptar los cuatro archivos de producción y registrarlos**

Reemplaza el contenido de `src/Modules/Integrations/Modules.Integrations.Application/ISecretProtector.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

/// <summary>
/// Cifra y descifra los secretos de las conexiones (spec 2026-10-08, «Secreto en reposo»; era
/// <c>IWhatsAppSecretProtector</c> en 6612298). La implementación vive en Infrastructure y la llave
/// fuera de la base: un volcado de la base sola no alcanza para leer una credencial. Atado a conexión y
/// campo (D7). Nunca registra nada.
/// </summary>
public interface ISecretProtector
{
    /// <summary>La llave con la que se cifra; <c>null</c> si falta o está vacía.</summary>
    string? ActiveKeyId { get; }

    /// <summary>La llave está configurada (vacío = ausente). No dice que sea la que cifró una fila:
    /// para eso, <see cref="TryUnprotect"/>.</summary>
    bool HasKey(string keyId);

    /// <summary>Cifra con <see cref="ActiveKeyId"/>; sin ella lanza.</summary>
    ProtectedSecret Protect(Guid connectionId, string fieldKey, string plaintext);

    /// <summary>Descifra con la llave del <c>KeyId</c> del secreto; lanza si no puede.</summary>
    string Unprotect(Guid connectionId, string fieldKey, ProtectedSecret secret);

    /// <summary><c>false</c> —sin lanzar y sin registrar— con la llave ausente, bytes dañados, AAD de
    /// otra conexión o de otro campo, o una llave distinta con el mismo id.</summary>
    bool TryUnprotect(Guid connectionId, string fieldKey, ProtectedSecret secret, [NotNullWhen(true)] out string? plaintext);
}
```

Reemplaza el contenido de `src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/AesGcmSecretProtector.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.SecretProtection;

/// <summary>
/// AES-256-GCM en la caja de .NET, sin Data Protection (viene de 6612298): la llave vive fuera de la
/// base. Formato: <c>nonce(12) || ciphertext || tag(16)</c>, nonce aleatorio por cifrado. AAD = UTF-8
/// de <c>"integrations.connection:" + connectionId.ToString("D") + ":" + fieldKey</c> (spec
/// 2026-10-08, D7): un texto cifrado copiado a otra conexión o a otro campo no descifra. El prefijo
/// viejo (<c>quotations.whatsapp.api_token:</c>) no se conserva: nada cifrado con él llegó a producción.
///
/// Singleton: lee las opciones una vez, al arrancar, que es cuando se validaron.
/// </summary>
internal sealed class AesGcmSecretProtector(IOptions<SecretProtectionOptions> options) : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string AssociatedDataPrefix = "integrations.connection:";

    private readonly SecretProtectionOptions settings = options.Value;

    public string? ActiveKeyId => settings.EffectiveActiveKeyId;

    public bool HasKey(string keyId) => settings.KeyValue(keyId) is not null;

    public ProtectedSecret Protect(Guid connectionId, string fieldKey, string plaintext)
    {
        var keyId = ActiveKeyId ?? throw new InvalidOperationException(
            $"{SecretProtectionOptions.SectionName}:ActiveKeyId is not configured: "
            + "the connection secret cannot be encrypted.");
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
            AssociatedData(connectionId, fieldKey));

        return new ProtectedSecret(keyId, output);
    }

    public string Unprotect(Guid connectionId, string fieldKey, ProtectedSecret secret)
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
            AssociatedData(connectionId, fieldKey));

        return Encoding.UTF8.GetString(plain);
    }

    public bool TryUnprotect(
        Guid connectionId, string fieldKey, ProtectedSecret secret, [NotNullWhen(true)] out string? plaintext)
    {
        try
        {
            plaintext = Unprotect(connectionId, fieldKey, secret);
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
            + "a connection secret stored with that key cannot be used.");
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

    private static byte[] AssociatedData(Guid connectionId, string fieldKey) =>
        Encoding.UTF8.GetBytes(AssociatedDataPrefix + connectionId.ToString("D") + ":" + fieldKey);
}
```

Reemplaza el contenido de `src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/SecretProtectionOptions.cs`:

```csharp
namespace Modules.Integrations.Infrastructure.SecretProtection;

/// <summary>
/// La sección <c>Integrations:SecretProtection</c> (spec 2026-10-08, «Secreto en reposo»; era
/// <c>Quotations:SecretProtection</c> en 6612298). <c>ActiveKeyId</c> no es secreto (va en el
/// ConfigMap); cada <c>Keys:&lt;id&gt;</c> sí (va en el Secret, desde una variable secreta del
/// pipeline). <b>Vacío = ausente</b> en todas partes: el harness de integración fija
/// <c>Keys:k1 = ""</c> para tapar los user-secrets del developer.
/// </summary>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "Integrations:SecretProtection";

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

En `src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/SecretProtectionOptionsValidator.cs` cambian **sólo** tres cosas; el resto (patrón `^[a-z0-9]{1,32}$`, 32 bytes, mensajes sin valores) queda como vino:

1. `namespace Modules.Quotations.Infrastructure.SecretProtection;` → `namespace Modules.Integrations.Infrastructure.SecretProtection;`
2. La primera línea del `<summary>`: `/// Falla rápido al arrancar (spec 2026-10-07, «Validación al arrancar»). En <c>Production</c>, todo:` → `/// Falla rápido al arrancar (spec 2026-10-08, «Secreto en reposo»; viene de 6612298). En <c>Production</c>, todo:`
3. El mensaje de la llave activa faltante en producción:

```csharp
                failures.Add(
                    $"{SecretProtectionOptions.SectionName}:ActiveKeyId is required in Production: "
                    + "without it no connection can be created or edited.");
```

Reemplaza `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs` entero:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Infrastructure.SecretProtection;

namespace Modules.Integrations.Infrastructure;

public static class IntegrationsInfrastructureExtensions
{
    public static IServiceCollection AddIntegrationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Mismo guard que los demás módulos: sin la cadena el host no arranca, en vez de fallar en
        // el primer request con un error de Npgsql que no explica nada.
        _ = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        // Spec 2026-10-08, «Secreto en reposo»: en Production ValidateOnStart exige la llave activa
        // (sin ella el pod entra en crash-loop, a propósito); fuera de producción el host arranca y
        // crear o editar responde 503.
        services.AddOptions<SecretProtectionOptions>()
            .Bind(configuration.GetSection(SecretProtectionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SecretProtectionOptions>, SecretProtectionOptionsValidator>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        return services;
    }
}
```

- [ ] **Step 5: Ver el GREEN de las movidas y el RED del ejemplo de configuración**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet build Backend.slnx --no-restore -v q; if ($LASTEXITCODE -ne 0) { throw "build falló (si el paso espera un RED de compilación, ése es el RED)" }
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --no-build --filter "FullyQualifiedName~AesGcmSecretProtectorTests|FullyQualifiedName~SecretProtectionOptionsValidatorTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~ConfigurationExampleTests"
```

Esperado: PASS en la primera. FAIL en `EveryBoundConfigurationKeyIsDocumentedInTheExample` nombrando `Integrations:SecretProtection:ActiveKeyId, Integrations:SecretProtection:Keys`: es el RED que pide documentarlas.

- [ ] **Step 6: Documentar las claves y ver el GREEN**

En `src/Api/appsettings.example.json`, reemplaza el cierre de `Quotations` (`:102-106`):

```json
    "PaymentProofs": {
      "PublicLinks": false
    }
  }
}
```

por:

```json
    "PaymentProofs": {
      "PublicLinks": false
    }
  },
  "Integrations": {
    "SecretProtection": {
      "ActiveKeyId": "",
      "Keys": {}
    }
  }
}
```

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests"
```

Esperado: PASS.

- [ ] **Step 7: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/Modules/Integrations tests/Modules/Integrations/Modules.Integrations.UnitTests src/Api/appsettings.example.json
$text = @'
feat(integrations): ISecretProtector con AAD por conexión y campo

El protector AES-256-GCM pasa a Integrations con la sección
Integrations:SecretProtection y el AAD integrations.connection:{id}:{campo}.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 5: Permisos `integrations.connection.*` en `admin`

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Application/IntegrationsPermissions.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` — `using`, rol `admin` (`:719-722`), `PermissionDefinition` (después de `:1131-1137`), `AddAuthorization` (`:1397-1399`)
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:166-173` y una prueba nueva

**Interfaces:**
- Consumes: nada.
- Produces: `namespace Modules.Integrations.Application; public static class IntegrationsPermissions { public const string ConnectionRead = "integrations.connection.read"; public const string ConnectionManage = "integrations.connection.manage"; }`, con su política (nombre = permiso) y su `PermissionDefinition` (`Category "Integrations"`, `RequiredModules: []`).

- [ ] **Step 1: Escribir las pruebas que fallan**

En `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`, agrega `using Modules.Integrations.Application;` después de `using Modules.Authorization.Application;` (`:8`) y reemplaza la prueba de conteo (`:166-173`) por estas dos:

```csharp
    /// <summary>Ancla: sin esto, las dos de arriba pasarían por vacías. Son las 35 del spec de
    /// entitlements, las 6 de POS, las tres <c>operator.*</c> y las dos <c>integrations.*</c> de los
    /// specs de 2026-10-08.</summary>
    [Fact]
    public void PermissionDiscoveryFindsTheFortySixConstants()
    {
        // +6 de PosPermissions (spec 2026-10-07), +3 de OperatorPermissions y +2 de
        // IntegrationsPermissions (specs 2026-10-08).
        Assert.Equal(46, PermissionConstants().Length);
    }

    /// <summary>
    /// Spec 2026-10-08 (Integraciones), «Permisos»: los dos de fábrica en admin y núcleo
    /// (<c>RequiredModules</c> vacío): Integrations no se apaga; lo que se filtra por módulo es el
    /// catálogo de proveedores.
    /// </summary>
    [Fact]
    public void TheAdminRoleCarriesTheIntegrationsPermissionsAsCore()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();
        string[] integrationsPermissions =
            [IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage];

        Assert.All(integrationsPermissions, permission =>
            Assert.Contains(permission, catalog.PermissionsFor("admin")));
        var definitions = catalog.ListPermissions()
            .Where(definition => integrationsPermissions.Contains(definition.Permission))
            .ToArray();
        Assert.Equal(2, definitions.Length);
        Assert.All(definitions, definition =>
        {
            Assert.Equal("Integrations", definition.Category);
            Assert.Empty(definition.RequiredModules!);
        });
    }
```

- [ ] **Step 2: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: FAIL de compilación, `error CS0103: The name 'IntegrationsPermissions' does not exist in the current context`.

- [ ] **Step 3: Crear las constantes y ver el segundo RED**

`src/Modules/Integrations/Modules.Integrations.Application/IntegrationsPermissions.cs`:

```csharp
namespace Modules.Integrations.Application;

/// <summary>
/// Spec 2026-10-08, «Permisos». Núcleo (<c>RequiredModules</c> vacío, decisión 4): lo que se filtra por
/// módulo es el catálogo de proveedores, no el permiso. Cada uno necesita su política en
/// <c>AddAuthorization</c>: sin ella <c>RequireAuthorization</c> no resuelve y el síntoma es 500, no 403.
/// </summary>
public static class IntegrationsPermissions
{
    public const string ConnectionRead = "integrations.connection.read";
    public const string ConnectionManage = "integrations.connection.manage";
}
```

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: compila; FAIL en `EveryPermissionConstantDeclaresItsModules` (nombra `integrations.connection.manage, integrations.connection.read`) y en `TheAdminRoleCarriesTheIntegrationsPermissionsAsCore`. `PermissionDiscoveryFindsTheFortySixConstants` ya pasa.

- [ ] **Step 4: Las dos mitades en Bootstrapper**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, agrega `using Modules.Integrations.Application;` antes de `using Modules.Integrations.Infrastructure;` (lo agregó la Task 1).

En el rol `admin`, reemplaza el cierre de la lista (`:719-722`):

```csharp
                OperatorPermissions.TenantsRead,
                OperatorPermissions.ModulesManage,
                OperatorPermissions.TenantsManage
            ]));
```

por:

```csharp
                OperatorPermissions.TenantsRead,
                OperatorPermissions.ModulesManage,
                OperatorPermissions.TenantsManage,
                // Spec 2026-10-08 (Integraciones), decisión 3: de fábrica en admin. El rol vive en
                // código, así que no hay migración de datos.
                IntegrationsPermissions.ConnectionRead,
                IntegrationsPermissions.ConnectionManage
            ]));
```

Después de la última `PermissionDefinition` (`OperatorPermissions.TenantsManage`, `:1131-1137`):

```csharp
        // Spec 2026-10-08 (Integraciones), «Permisos»: núcleo, para que el enmascarado por módulos no
        // las toque. Gestionar es "high": cambia las credenciales con las que la empresa le habla a sus
        // clientes.
        services.AddSingleton(new PermissionDefinition(
            IntegrationsPermissions.ConnectionRead,
            "Ver integraciones",
            "Permite ver las conexiones del tenant con plataformas externas, sin sus credenciales.",
            "Integrations",
            "medium",
            RequiredModules: []));
        services.AddSingleton(new PermissionDefinition(
            IntegrationsPermissions.ConnectionManage,
            "Gestionar integraciones",
            "Permite conectar, probar, pausar, reanudar y eliminar conexiones con plataformas externas y cambiar sus credenciales.",
            "Integrations",
            "high",
            RequiredModules: []));
```

En `AddAuthorization`, reemplaza el cierre (`:1397-1399`):

```csharp
            .AddPolicy(
                OperatorPermissions.TenantsManage,
                policy => AddPermissionRequirement(policy, OperatorPermissions.TenantsManage));
```

por:

```csharp
            .AddPolicy(
                OperatorPermissions.TenantsManage,
                policy => AddPermissionRequirement(policy, OperatorPermissions.TenantsManage))
            .AddPolicy(
                IntegrationsPermissions.ConnectionRead,
                policy => AddPermissionRequirement(policy, IntegrationsPermissions.ConnectionRead))
            .AddPolicy(
                IntegrationsPermissions.ConnectionManage,
                policy => AddPermissionRequirement(policy, IntegrationsPermissions.ConnectionManage));
```

- [ ] **Step 5: Ver el GREEN**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: PASS de toda la clase.

- [ ] **Step 6: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/Modules/Integrations/Modules.Integrations.Application/IntegrationsPermissions.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs
$text = @'
feat(integrations): permisos integrations.connection.* en admin

Constantes, definiciones núcleo y políticas: sin la política el síntoma sería 500.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 6: Puertos, DTO y lecturas — catálogo visible, lista y detalle

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Application/IntegrationsPorts.cs` (`IIntegrationProviderCatalog`, `IntegrationProviderCatalog`, `IIntegrationConnectionRepository`, `IConnectionAuthorNames`)
- Create: `src/Modules/Integrations/Modules.Integrations.Application/IntegrationsDtos.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Application/IntegrationsSupport.cs` (`IntegrationsAuthorization`, `IntegrationsNotFound`, `ProviderVisibility`, `ConnectionLoader`, `ConnectionMapping`)
- Create: `src/Modules/Integrations/Modules.Integrations.Application/{GetIntegrationsCatalog,ListConnections,GetConnection}.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro de handlers antes de `services.AddValidatorsFromAssemblyContaining<UpdateTenantSettingsValidator>();`, `:488`)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationsTestDoubles.cs`, `tests/Modules/Integrations/Modules.Integrations.UnitTests/ReadHandlersTests.cs`

**Interfaces:**
- Consumes: `IntegrationConnection`, `IntegrationProvider`, `IntegrationProviders`, `ISecretProtector`, `IntegrationsPermissions`, `IntegrationsErrorCodes` (Tasks 2-5); `ITenantModules`, `TenantModuleGuard.ModuleNotEnabledCode`, `IExecutionContext` (Tenancy.Application).
- Produces:
  - `public interface IIntegrationProviderCatalog { IReadOnlyList<IntegrationProvider> All { get; } IntegrationProvider? Find(string? key); }` y `public sealed class IntegrationProviderCatalog : IIntegrationProviderCatalog`.
  - `public interface IIntegrationConnectionRepository { Task<IntegrationConnection?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken); Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid tenantId, CancellationToken); Task<int> CountAsync(Guid tenantId, string providerKey, CancellationToken); void Add(IntegrationConnection); void Remove(IntegrationConnection); }`
  - `public interface IConnectionAuthorNames { Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken); }`
  - DTO: `IntegrationsCatalogResponse(IReadOnlyList<ProviderResponse> Providers)`, `ProviderResponse(string Key, string DisplayName, string Category, IReadOnlyList<ProviderFieldResponse> Fields, int MaxConnections, int ConnectionCount)`, `ProviderFieldResponse(string Key, string Label, string Kind, bool Required, int MaxLength)`, `ConnectionsResponse(IReadOnlyList<ConnectionResponse> Items)`, `ConnectionResponse(Guid Id, string ProviderKey, string Name, string Status, IReadOnlyDictionary<string, string?> Fields, IReadOnlyDictionary<string, SecretStateResponse> Secrets, DateTimeOffset? LastVerifiedAt, DateTimeOffset? LastFailureAt, string? LastFailureCode, DateTimeOffset CreatedAt, ConnectionAuthorResponse CreatedBy, DateTimeOffset UpdatedAt, long Version)`, `SecretStateResponse(bool Configured, DateTimeOffset? UpdatedAt, bool Readable)`, `ConnectionAuthorResponse(Guid MemberId, string? DisplayName)`.
  - Internos que usan las Tasks 7-9: `IntegrationsAuthorization.EnsureAuthorized(IExecutionContext, Guid tenantId, string permission)` y `.Denied()`; `IntegrationsNotFound.Connection(Guid)`; `ProviderVisibility.VisibleAsync(catalog, tenantModules, tenantId, ct)`, `.EnsureVisibleAsync(tenantModules, tenantId, provider, ct)`, `.Hidden(string providerKey)`; `ConnectionLoader.LoadVisibleAsync(repository, catalog, tenantModules, tenantId, connectionId, ct)` → `(IntegrationConnection Connection, IntegrationProvider Provider)`; `ConnectionMapping.ToProvider(provider, count)`, `.ToResponse(connection, provider, protector, IReadOnlyDictionary<Guid, string> names)`, `.ToResponseAsync(connection, provider, protector, authorNames, ct)`.
  - Consultas: `GetIntegrationsCatalogQuery(Guid TenantId) : IQuery<IntegrationsCatalogResponse>`, `ListConnectionsQuery(Guid TenantId) : IQuery<ConnectionsResponse>`, `GetConnectionQuery(Guid TenantId, Guid ConnectionId) : IQuery<ConnectionResponse>`, con sus handlers (constructores en el código de abajo).
  - Dobles de prueba (los usan las Tasks 7-9): `IntegrationsTestBed` (clase `partial`), `FakeExecutionContext`, `FakeMembershipDirectory`, `FakeClock`, `FakeTenantModules`, `InMemoryConnectionRepository`, `FakeUnitOfWork`, `FakeSecretProtector`, `FakeAuthorNames`.

- [ ] **Step 1: Escribir los dobles y las pruebas que fallan**

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationsTestDoubles.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

internal sealed class FakeExecutionContext(Guid tenantId, Guid subjectId, params string[] permissions)
    : IExecutionContext
{
    public Guid SubjectId { get; } = subjectId;

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
}

internal sealed class FakeMembershipDirectory : IMembershipDirectory
{
    public Dictionary<(Guid UserId, Guid TenantId), Guid> Active { get; } = [];

    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>?>(null);

    public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Active.TryGetValue((userId, tenantId), out var id) ? id : (Guid?)null);

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(Active.Where(entry => entry.Key.UserId == userId).Select(entry => entry.Value).ToList());
}

internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

/// <summary>Sin entrada = tenant que no está en <c>tenancy.tenants</c> (el stub): <c>null</c>, todo visible.</summary>
internal sealed class FakeTenantModules : ITenantModules
{
    public Dictionary<Guid, TenantModuleSet> Sets { get; } = [];

    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<TenantModuleSet?>(Sets.TryGetValue(tenantId, out var set) ? set : null);
}

internal sealed class InMemoryConnectionRepository : IIntegrationConnectionRepository
{
    public List<IntegrationConnection> Connections { get; } = [];

    public Task<IntegrationConnection?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(Connections.SingleOrDefault(connection => connection.TenantId == tenantId && connection.Id == connectionId));

    public Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IntegrationConnection>>(Connections.Where(connection => connection.TenantId == tenantId).ToList());

    public Task<int> CountAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
        Task.FromResult(Connections.Count(connection =>
            connection.TenantId == tenantId && string.Equals(connection.ProviderKey, providerKey, StringComparison.Ordinal)));

    public void Add(IntegrationConnection connection) => Connections.Add(connection);

    public void Remove(IntegrationConnection connection) => Connections.Remove(connection);
}

internal sealed class FakeUnitOfWork : IIntegrationsUnitOfWork
{
    public int Saves { get; private set; }

    public Exception? FailWith { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (FailWith is { } failure)
        {
            throw failure;
        }

        Saves++;
        return Task.FromResult(1);
    }
}

/// <summary>
/// Un "cifrado" legible que respeta el contrato de <see cref="ISecretProtector"/>: sólo abre lo que se
/// selló para la misma conexión y el mismo campo. <see cref="Unreadable"/> simula una llave retirada.
/// </summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    public string? ActiveKeyId { get; set; } = "test";

    public HashSet<(Guid ConnectionId, string FieldKey)> Unreadable { get; } = [];

    public bool HasKey(string keyId) => string.Equals(keyId, "test", StringComparison.Ordinal);

    public ProtectedSecret Protect(Guid connectionId, string fieldKey, string plaintext) =>
        ActiveKeyId is { } keyId
            ? new ProtectedSecret(keyId, Encoding.UTF8.GetBytes($"{connectionId:D}|{fieldKey}|{plaintext}"))
            : throw new InvalidOperationException("No active key.");

    public string Unprotect(Guid connectionId, string fieldKey, ProtectedSecret secret) =>
        TryUnprotect(connectionId, fieldKey, secret, out var plaintext)
            ? plaintext
            : throw new CryptographicException("Unreadable.");

    public bool TryUnprotect(
        Guid connectionId, string fieldKey, ProtectedSecret secret, [NotNullWhen(true)] out string? plaintext)
    {
        var prefix = $"{connectionId:D}|{fieldKey}|";
        var text = Encoding.UTF8.GetString(secret.Ciphertext);
        if (Unreadable.Contains((connectionId, fieldKey)) || !text.StartsWith(prefix, StringComparison.Ordinal))
        {
            plaintext = null;
            return false;
        }

        plaintext = text[prefix.Length..];
        return true;
    }
}

internal sealed class FakeAuthorNames : IConnectionAuthorNames
{
    public Dictionary<Guid, string> Names { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            Names.Where(entry => memberIds.Contains(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value));
}

/// <summary>
/// Todo lo que un handler necesita, con valores por defecto: un tenant, un sujeto con membresía activa
/// y los dos permisos. Clase partial: las Tasks 7, 8 y 9 suman su parte en archivos propios.
/// </summary>
internal sealed partial class IntegrationsTestBed
{
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);
    public const string Token = "zenvia-token-TEST-1";
    public const string FromNumber = "573001234567";
    public const string AuthorName = "Laura Gómez";

    public IntegrationsTestBed(IIntegrationProviderCatalog? catalog = null)
    {
        Catalog = catalog ?? new IntegrationProviderCatalog();
        Memberships.Active[(SubjectId, TenantId)] = MemberId;
        AuthorNames.Names[MemberId] = AuthorName;
    }

    public Guid TenantId { get; } = Guid.CreateVersion7();

    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public Guid MemberId { get; } = Guid.CreateVersion7();

    public IIntegrationProviderCatalog Catalog { get; }

    public FakeClock Clock { get; } = new(Now);

    public FakeTenantModules Modules { get; } = new();

    public FakeMembershipDirectory Memberships { get; } = new();

    public InMemoryConnectionRepository Repository { get; } = new();

    public FakeUnitOfWork UnitOfWork { get; } = new();

    public FakeSecretProtector Protector { get; } = new();

    public FakeAuthorNames AuthorNames { get; } = new();

    public string[] Permissions { get; set; } =
        [IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage];

    public FakeExecutionContext Context(Guid? tenantId = null) => new(tenantId ?? TenantId, SubjectId, Permissions);

    public IntegrationConnection Seed(string name = "WhatsApp sede norte", Guid? tenantId = null)
    {
        var connection = IntegrationConnection.Create(
            IntegrationProviders.Zenvia,
            tenantId ?? TenantId,
            name,
            new Dictionary<string, string> { [ZenviaFieldKeys.FromNumber] = FromNumber },
            new Dictionary<string, string> { [ZenviaFieldKeys.ApiToken] = Token },
            Protector.Protect,
            MemberId,
            Now);
        Repository.Connections.Add(connection);
        return connection;
    }

    public void ShowEverything() =>
        Modules.Sets[TenantId] = TenantModuleSet.FromStored(TenantModuleKeys.All);

    public void HideQuotations() =>
        Modules.Sets[TenantId] = TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Quotations]));

    public GetIntegrationsCatalogHandler CatalogHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, Modules, context ?? Context());

    public ListConnectionsHandler ListHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, Modules, Protector, AuthorNames, context ?? Context());

    public GetConnectionHandler GetHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, Modules, Protector, AuthorNames, context ?? Context());
}
```

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/ReadHandlersTests.cs`:

```csharp
using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Endpoints» (las tres lecturas) y «Visibilidad para un tenant»: sólo lo visible,
/// nada borrado por estar oculto, orden estable, 403 antes de leer y 404 dentro del tenant.
/// </summary>
public sealed class ReadHandlersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCatalogShowsTheVisibleProvidersWithTheirFieldsAndCount()
    {
        var bed = new IntegrationsTestBed();
        bed.ShowEverything();
        bed.Seed("Norte");
        bed.Seed("Sur");
        bed.Seed("De otro tenant", tenantId: Guid.CreateVersion7());

        var catalog = await bed.CatalogHandler().HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), Ct);

        var zenvia = Assert.Single(catalog.Providers);
        Assert.Equal("zenvia", zenvia.Key);
        Assert.Equal("Zenvia (WhatsApp)", zenvia.DisplayName);
        Assert.Equal("Messaging", zenvia.Category);
        Assert.Equal(20, zenvia.MaxConnections);
        Assert.Equal(2, zenvia.ConnectionCount);
        Assert.Equal(["apiToken", "fromNumber"], zenvia.Fields.Select(field => field.Key));
        Assert.Equal(["Secret", "Phone"], zenvia.Fields.Select(field => field.Kind));
        Assert.All(zenvia.Fields, field => Assert.True(field.Required));
    }

    [Fact]
    public async Task TheCatalogIsEmptyButPresentWhenNoConsumingModuleIsOn()
    {
        var bed = new IntegrationsTestBed();
        bed.HideQuotations();

        var catalog = await bed.CatalogHandler().HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), Ct);

        Assert.Empty(catalog.Providers);
    }

    [Fact]
    public async Task TheDevelopmentStubSeesTheWholeCatalog()
    {
        var bed = new IntegrationsTestBed();

        var catalog = await bed.CatalogHandler().HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), Ct);

        Assert.Single(catalog.Providers);
    }

    [Fact]
    public async Task ReadingNeedsTheReadPermissionAndTheRouteTenant()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        var otherTenant = bed.Context(Guid.CreateVersion7());
        bed.Permissions = [];
        var noPermission = bed.Context();

        foreach (var context in new IExecutionContext[] { otherTenant, noPermission })
        {
            Assert.Equal("authorization.denied", (await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                bed.CatalogHandler(context).HandleAsync(new GetIntegrationsCatalogQuery(bed.TenantId), Ct))).Code);
            Assert.Equal("authorization.denied", (await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                bed.ListHandler(context).HandleAsync(new ListConnectionsQuery(bed.TenantId), Ct))).Code);
            Assert.Equal("authorization.denied", (await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                bed.GetHandler(context).HandleAsync(new GetConnectionQuery(bed.TenantId, connection.Id), Ct))).Code);
        }
    }

    [Fact]
    public async Task TheListIsOrderedByProviderThenNameAndCarriesNoSecretValue()
    {
        var bed = new IntegrationsTestBed();
        bed.Seed("sur");
        bed.Seed("Norte");
        bed.Seed("alto");

        var list = await bed.ListHandler().HandleAsync(new ListConnectionsQuery(bed.TenantId), Ct);

        Assert.Equal(["alto", "Norte", "sur"], list.Items.Select(item => item.Name));
        var first = list.Items[0];
        Assert.Equal("Active", first.Status);
        Assert.Equal(IntegrationsTestBed.FromNumber, first.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Equal(["apiToken"], first.Secrets.Keys);
        Assert.True(first.Secrets[ZenviaFieldKeys.ApiToken].Configured);
        Assert.True(first.Secrets[ZenviaFieldKeys.ApiToken].Readable);
        Assert.Equal(IntegrationsTestBed.Now, first.Secrets[ZenviaFieldKeys.ApiToken].UpdatedAt);
        Assert.Equal(bed.MemberId, first.CreatedBy.MemberId);
        Assert.Equal(IntegrationsTestBed.AuthorName, first.CreatedBy.DisplayName);
        Assert.DoesNotContain(IntegrationsTestBed.Token, JsonSerializer.Serialize(list), StringComparison.Ordinal);
    }

    // Spec, «Visibilidad»: lo de un proveedor oculto no se borra ni se pausa, sólo no se muestra.
    [Fact]
    public async Task TheListHidesConnectionsOfHiddenProvidersWithoutTouchingThem()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.HideQuotations();

        var list = await bed.ListHandler().HandleAsync(new ListConnectionsQuery(bed.TenantId), Ct);

        Assert.Empty(list.Items);
        Assert.Same(connection, Assert.Single(bed.Repository.Connections));
        Assert.Equal(ConnectionStatus.Active, connection.Status);
    }

    [Fact]
    public async Task AnUnreadableSecretIsConfiguredButNotReadable()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Protector.Unreadable.Add((connection.Id, ZenviaFieldKeys.ApiToken));

        var response = await bed.GetHandler().HandleAsync(new GetConnectionQuery(bed.TenantId, connection.Id), Ct);

        Assert.True(response.Secrets[ZenviaFieldKeys.ApiToken].Configured);
        Assert.False(response.Secrets[ZenviaFieldKeys.ApiToken].Readable);
    }

    // Un id de otro tenant, pedido por la ruta del propio, responde igual que uno inexistente.
    [Fact]
    public async Task AnIdOfAnotherTenantIsNotFoundInsideTheRouteTenant()
    {
        var bed = new IntegrationsTestBed();
        var foreign = bed.Seed(tenantId: Guid.CreateVersion7());

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.GetHandler().HandleAsync(new GetConnectionQuery(bed.TenantId, foreign.Id), Ct));

        Assert.Equal("integrations.connection.not_found", error.Code);
    }

    [Fact]
    public async Task AConnectionOfAHiddenProviderIsModuleNotEnabled()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.HideQuotations();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.GetHandler().HandleAsync(new GetConnectionQuery(bed.TenantId, connection.Id), Ct));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
    }

    [Fact]
    public async Task AnAuthorWhoseMembershipIsGoneHasNoName()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.AuthorNames.Names.Clear();

        var response = await bed.GetHandler().HandleAsync(new GetConnectionQuery(bed.TenantId, connection.Id), Ct);

        Assert.Equal(bed.MemberId, response.CreatedBy.MemberId);
        Assert.Null(response.CreatedBy.DisplayName);
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~ReadHandlersTests"
```

Esperado: FAIL de compilación: `error CS0246: … 'IIntegrationConnectionRepository' could not be found` (y `IConnectionAuthorNames`, `IIntegrationProviderCatalog`, `GetIntegrationsCatalogHandler`, …).

- [ ] **Step 3: Implementar puertos, DTO, apoyo y las tres lecturas**

`src/Modules/Integrations/Modules.Integrations.Application/IntegrationsPorts.cs`:

```csharp
using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

/// <summary>
/// El catálogo como puerto (P5 del plan): el código usa <see cref="IntegrationProviderCatalog"/>, que es
/// <see cref="IntegrationProviders.All"/>; las pruebas inyectan uno con un proveedor falso para
/// demostrar el criterio 5 del spec sin tocar los handlers.
/// </summary>
public interface IIntegrationProviderCatalog
{
    IReadOnlyList<IntegrationProvider> All { get; }

    IntegrationProvider? Find(string? key);
}

public sealed class IntegrationProviderCatalog : IIntegrationProviderCatalog
{
    public IReadOnlyList<IntegrationProvider> All => IntegrationProviders.All;

    public IntegrationProvider? Find(string? key) => IntegrationProviders.Find(key);
}

/// <summary>Todo método recibe <c>tenantId</c>: el id de otro tenant responde igual que uno inexistente.</summary>
public interface IIntegrationConnectionRepository
{
    /// <summary>Con tracking, secretos incluidos.</summary>
    Task<IntegrationConnection?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Todas las del tenant, sin tracking y sin orden: el handler ordena. Hay tope de 20 por
    /// proveedor, así que no se pagina.</summary>
    Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<int> CountAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken);

    void Add(IntegrationConnection connection);

    void Remove(IntegrationConnection connection);
}

/// <summary>
/// El nombre de quien creó cada conexión (<c>createdBy.displayName</c>). Lo resuelve un adaptador en
/// Bootstrapper —membresía de Tenancy y correo de Identity—, como <c>PosCashierLookup</c>: Integrations
/// no referencia Identity (P23).
/// </summary>
public interface IConnectionAuthorNames
{
    /// <summary>Sólo las membresías del tenant; una que no está, no aparece en el diccionario.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}
```

`src/Modules/Integrations/Modules.Integrations.Application/IntegrationsDtos.cs`:

```csharp
namespace Modules.Integrations.Application;

// BFF (CLAUDE.md, «Convenciones del backend»): la pantalla Ajustes → Integraciones dibuja el formulario
// desde el catálogo y no conoce los enums del backend, así que todo viaja por nombre (Active, Messaging,
// Secret) y en camelCase. Las claves de los diccionarios viajan tal cual (fromNumber, apiToken).

/// <summary>Los proveedores visibles para el tenant, completa aunque esté vacía: la pantalla dibuja
/// «no hay integraciones para tus módulos» sin preguntar nada más.</summary>
public sealed record IntegrationsCatalogResponse(IReadOnlyList<ProviderResponse> Providers);

/// <summary><c>connectionCount</c> viaja calculado para que la tarjeta muestre «2 de 20» sin pedir la
/// lista.</summary>
public sealed record ProviderResponse(
    string Key,
    string DisplayName,
    string Category,
    IReadOnlyList<ProviderFieldResponse> Fields,
    int MaxConnections,
    int ConnectionCount);

/// <summary>Sin el patrón: el formulario no valida con regex del backend; el 422 marca el campo.</summary>
public sealed record ProviderFieldResponse(string Key, string Label, string Kind, bool Required, int MaxLength);

/// <summary>Sin paginación: hay tope de 20 conexiones por proveedor (spec, «Endpoints»).</summary>
public sealed record ConnectionsResponse(IReadOnlyList<ConnectionResponse> Items);

/// <summary>
/// <c>fields</c> y <c>secrets</c> llevan <b>todas</b> las claves del proveedor (P21), para que el
/// formulario no tenga que cruzar con el catálogo para saber qué falta. Nunca un valor de secreto.
/// </summary>
public sealed record ConnectionResponse(
    Guid Id,
    string ProviderKey,
    string Name,
    string Status,
    IReadOnlyDictionary<string, string?> Fields,
    IReadOnlyDictionary<string, SecretStateResponse> Secrets,
    DateTimeOffset? LastVerifiedAt,
    DateTimeOffset? LastFailureAt,
    string? LastFailureCode,
    DateTimeOffset CreatedAt,
    ConnectionAuthorResponse CreatedBy,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary><c>readable</c> descifra de verdad y descarta el valor: si la llave se retiró o los bytes
/// se dañaron, la pantalla pide pegar la clave otra vez.</summary>
public sealed record SecretStateResponse(bool Configured, DateTimeOffset? UpdatedAt, bool Readable);

/// <summary>El nombre viaja resuelto (nombre de la membresía o correo) para que la lista no pida los
/// miembros aparte; <c>null</c> si la membresía ya no está en el tenant.</summary>
public sealed record ConnectionAuthorResponse(Guid MemberId, string? DisplayName);
```

`src/Modules/Integrations/Modules.Integrations.Application/IntegrationsSupport.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>
/// Copia de <c>PosAuthorization</c>: tenant de la ruta distinto del activo, o permiso faltante → 403.
/// Nunca 404: confirmaría que el id existe en otro tenant (doble capa, <c>CLAUDE.md</c>).
/// </summary>
internal static class IntegrationsAuthorization
{
    public static void EnsureAuthorized(IExecutionContext executionContext, Guid tenantId, string permission)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this integrations operation for this tenant.");
}

/// <summary>404 dentro del tenant de la ruta: el repositorio filtra por tenant.</summary>
internal static class IntegrationsNotFound
{
    public static ResourceNotFoundException Connection(Guid connectionId) =>
        new(IntegrationsErrorCodes.NotFound, $"Connection '{connectionId}' was not found.");
}

/// <summary>
/// Spec 2026-10-08, «Visibilidad para un tenant»: lo de un proveedor oculto responde 403
/// <c>tenancy.module_not_enabled</c>, igual que el resto de la app con un módulo apagado.
/// </summary>
internal static class ProviderVisibility
{
    public static async Task<IReadOnlyList<IntegrationProvider>> VisibleAsync(
        IIntegrationProviderCatalog catalog, ITenantModules tenantModules, Guid tenantId, CancellationToken cancellationToken)
    {
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        return catalog.All.Where(provider => provider.IsVisibleFor(modules)).ToArray();
    }

    public static async Task EnsureVisibleAsync(
        ITenantModules tenantModules, Guid tenantId, IntegrationProvider provider, CancellationToken cancellationToken)
    {
        if (!provider.IsVisibleFor(await tenantModules.FindAsync(tenantId, cancellationToken)))
        {
            throw Hidden(provider.Key);
        }
    }

    public static RequestForbiddenException Hidden(string providerKey) =>
        new(
            TenantModuleGuard.ModuleNotEnabledCode,
            $"The '{providerKey}' integration is not available: none of the modules that use it is enabled for this tenant.");
}

internal static class ConnectionLoader
{
    /// <summary>404 si no está en el tenant; 403 si su proveedor no es visible o ya no está en el
    /// catálogo (P22).</summary>
    public static async Task<(IntegrationConnection Connection, IntegrationProvider Provider)> LoadVisibleAsync(
        IIntegrationConnectionRepository repository,
        IIntegrationProviderCatalog catalog,
        ITenantModules tenantModules,
        Guid tenantId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var connection = await repository.FindAsync(tenantId, connectionId, cancellationToken)
            ?? throw IntegrationsNotFound.Connection(connectionId);
        var provider = catalog.Find(connection.ProviderKey) ?? throw ProviderVisibility.Hidden(connection.ProviderKey);
        await ProviderVisibility.EnsureVisibleAsync(tenantModules, tenantId, provider, cancellationToken);
        return (connection, provider);
    }
}

internal static class ConnectionMapping
{
    public static ProviderResponse ToProvider(IntegrationProvider provider, int connectionCount) =>
        new(
            provider.Key,
            provider.DisplayName,
            provider.Category.ToString(),
            provider.Fields
                .Select(field => new ProviderFieldResponse(field.Key, field.Label, field.Kind.ToString(), field.Required, field.MaxLength))
                .ToArray(),
            provider.MaxConnections,
            connectionCount);

    public static async Task<ConnectionResponse> ToResponseAsync(
        IntegrationConnection connection,
        IntegrationProvider provider,
        ISecretProtector protector,
        IConnectionAuthorNames authorNames,
        CancellationToken cancellationToken)
    {
        var names = await authorNames.FindAsync(connection.TenantId, [connection.CreatedBy], cancellationToken);
        return ToResponse(connection, provider, protector, names);
    }

    public static ConnectionResponse ToResponse(
        IntegrationConnection connection,
        IntegrationProvider provider,
        ISecretProtector protector,
        IReadOnlyDictionary<Guid, string> authorNames) =>
        new(
            connection.Id,
            connection.ProviderKey,
            connection.Name,
            connection.Status.ToString(),
            provider.PublicFields.ToDictionary(
                field => field.Key,
                field => connection.Fields.GetValueOrDefault(field.Key),
                StringComparer.Ordinal),
            provider.SecretFields.ToDictionary(
                field => field.Key,
                field => SecretState(connection, field.Key, protector),
                StringComparer.Ordinal),
            connection.LastVerifiedAt,
            connection.LastFailureAt,
            connection.LastFailureCode,
            connection.CreatedAt,
            new ConnectionAuthorResponse(connection.CreatedBy, authorNames.GetValueOrDefault(connection.CreatedBy)),
            connection.UpdatedAt,
            connection.Version);

    private static SecretStateResponse SecretState(IntegrationConnection connection, string fieldKey, ISecretProtector protector)
    {
        var stored = connection.Secrets.FirstOrDefault(secret => string.Equals(secret.FieldKey, fieldKey, StringComparison.Ordinal));
        if (stored is null)
        {
            return new SecretStateResponse(Configured: false, UpdatedAt: null, Readable: false);
        }

        // Spec, «readable»: descifra de verdad y descarta el valor.
        var readable = protector.TryUnprotect(connection.Id, fieldKey, stored.Protected, out _);
        return new SecretStateResponse(Configured: true, stored.UpdatedAt, readable);
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Application/GetIntegrationsCatalog.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record GetIntegrationsCatalogQuery(Guid TenantId) : IQuery<IntegrationsCatalogResponse>;

/// <summary>Spec 2026-10-08, <c>GET /catalog</c>: sólo los proveedores visibles, en el orden del catálogo.</summary>
public sealed class GetIntegrationsCatalogHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetIntegrationsCatalogQuery, IntegrationsCatalogResponse>
{
    public async Task<IntegrationsCatalogResponse> HandleAsync(
        GetIntegrationsCatalogQuery query, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, IntegrationsPermissions.ConnectionRead);

        var visible = await ProviderVisibility.VisibleAsync(catalog, tenantModules, query.TenantId, cancellationToken);
        var connections = await repository.ListAsync(query.TenantId, cancellationToken);

        return new IntegrationsCatalogResponse(visible
            .Select(provider => ConnectionMapping.ToProvider(
                provider,
                connections.Count(connection => string.Equals(connection.ProviderKey, provider.Key, StringComparison.Ordinal))))
            .ToArray());
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Application/ListConnections.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record ListConnectionsQuery(Guid TenantId) : IQuery<ConnectionsResponse>;

/// <summary>
/// Spec 2026-10-08, <c>GET /connections</c>: las del tenant cuyo proveedor es visible, por proveedor y
/// nombre (P20). Los nombres de los autores se piden en una sola llamada.
/// </summary>
public sealed class ListConnectionsHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    ITenantModules tenantModules,
    ISecretProtector protector,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext)
    : IQueryHandler<ListConnectionsQuery, ConnectionsResponse>
{
    public async Task<ConnectionsResponse> HandleAsync(ListConnectionsQuery query, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, IntegrationsPermissions.ConnectionRead);

        var visible = (await ProviderVisibility.VisibleAsync(catalog, tenantModules, query.TenantId, cancellationToken))
            .ToDictionary(provider => provider.Key, StringComparer.Ordinal);
        var connections = (await repository.ListAsync(query.TenantId, cancellationToken))
            .Where(connection => visible.ContainsKey(connection.ProviderKey))
            .OrderBy(connection => connection.ProviderKey, StringComparer.Ordinal)
            .ThenBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(connection => connection.Id)
            .ToArray();
        var names = await authorNames.FindAsync(
            query.TenantId, connections.Select(connection => connection.CreatedBy).Distinct().ToArray(), cancellationToken);

        return new ConnectionsResponse(connections
            .Select(connection => ConnectionMapping.ToResponse(connection, visible[connection.ProviderKey], protector, names))
            .ToArray());
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Application/GetConnection.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record GetConnectionQuery(Guid TenantId, Guid ConnectionId) : IQuery<ConnectionResponse>;

public sealed class GetConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    ITenantModules tenantModules,
    ISecretProtector protector,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext)
    : IQueryHandler<GetConnectionQuery, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(GetConnectionQuery query, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, IntegrationsPermissions.ConnectionRead);
        var (connection, provider) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, query.TenantId, query.ConnectionId, cancellationToken);
        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }
}
```

- [ ] **Step 4: Ver el GREEN de las unitarias**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~ReadHandlersTests"
```

Esperado: PASS.

- [ ] **Step 5: Registrar los handlers (RED y GREEN de `CompositionRootTests`)**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~EveryCommandAndQueryHasItsHandlerRegistered"
```

Esperado: FAIL nombrando `GetConnectionQuery -> IQueryHandler\`2, GetIntegrationsCatalogQuery -> …, ListConnectionsQuery -> …`.

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, justo antes de `services.AddValidatorsFromAssemblyContaining<UpdateTenantSettingsValidator>();` (`:488`):

```csharp
        // Integrations (spec 2026-10-08). A mano, como el resto: un handler que falte compila, mapea
        // su endpoint y falla recién en runtime con 500.
        services.AddScoped<
            IQueryHandler<GetIntegrationsCatalogQuery, IntegrationsCatalogResponse>,
            GetIntegrationsCatalogHandler>();
        services.AddScoped<
            IQueryHandler<ListConnectionsQuery, ConnectionsResponse>,
            ListConnectionsHandler>();
        services.AddScoped<
            IQueryHandler<GetConnectionQuery, ConnectionResponse>,
            GetConnectionHandler>();
```

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~IntegrationsLayerTests"
```

Esperado: PASS de las dos clases.

- [ ] **Step 6: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/Modules/Integrations/Modules.Integrations.Application tests/Modules/Integrations/Modules.Integrations.UnitTests src/Bootstrapper/QepServiceCollectionExtensions.cs
$text = @'
feat(integrations): catálogo visible, lista y detalle de conexiones
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 7: Crear y editar conexiones con prueba contra el proveedor

**Files:**
- Modify: `src/BuildingBlocks/BuildingBlocks.Application/ApplicationExceptions.cs` (+`ServiceUnavailableException`)
- Create: `src/Modules/Integrations/Modules.Integrations.Application/ConnectionTesting.cs` (`ConnectionTestOutcome`, `ConnectionTestResult`, `IConnectionTester`)
- Create: `src/Modules/Integrations/Modules.Integrations.Application/ConnectionInputRules.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Application/ConnectionWriteSupport.cs` (`IIntegrationsAuditRecorder`, `ConnectionAuditActions`, `ConnectionAudit`, `SecretProtectionGuard`, `ConcurrencyGuard`, `IntegrationsMember`, `StoredSecrets`, `ConnectionSecrets`, `ConnectionVerification`, `CommandText`)
- Create: `src/Modules/Integrations/Modules.Integrations.Application/{CreateConnection,UpdateConnection}.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers y validadores)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationsTestBed.Writes.cs`, `CreateConnectionValidatorTests.cs`, `CreateConnectionHandlerTests.cs`, `UpdateConnectionHandlerTests.cs`

**Interfaces:**
- Consumes: todo lo de la Task 6; `IntegrationConnection.Create/Update`, `SecretSealer` (Task 3); `ISecretProtector.Protect` (Task 4); `IMembershipDirectory.FindActiveMembershipIdAsync` (Tenancy).
- Produces:
  - `namespace BuildingBlocks.Application; public sealed class ServiceUnavailableException(string code, string message) : Exception(message) { public string Code { get; } }`
  - `public enum ConnectionTestOutcome { Ok, CredentialsRejected, Unreachable, Invalid }`; `public sealed record ConnectionTestResult(ConnectionTestOutcome Outcome, string? FieldKey = null, string? Reason = null)` con `static ConnectionTestResult Ok`, `static ConnectionTestResult CredentialsRejected`, `static ConnectionTestResult Unreachable(string reason)`, `static ConnectionTestResult Invalid(string fieldKey, string reason)`.
  - `public interface IConnectionTester { Task<ConnectionTestResult> TestAsync(IntegrationProvider provider, IReadOnlyDictionary<string, string> fields, IReadOnlyDictionary<string, string> secrets, CancellationToken cancellationToken); }`
  - `public interface IIntegrationsAuditRecorder { void Record(Guid tenantId, Guid actorId, AuditActorType actorType, string action, Guid connectionId, string outcome, IReadOnlyCollection<string> changedFields, DateTimeOffset occurredAt); }`
  - `public static class ConnectionAuditActions` (`ResourceType`, `Created`, `Updated`, `Paused`, `Resumed`, `Deleted`, `Verified`, `NeedsAttention`, `Success`, `Failure`).
  - Internos: `ConnectionInputRules.{CheckName, CheckValues, Normalize, ThrowIfAny, AddTo, PropertyFor}` y sus mensajes; `ConnectionAudit.{ByMember, KeysOf}`; `SecretProtectionGuard.EnsureAvailable(ISecretProtector)`; `ConcurrencyGuard.EnsureVersion(IntegrationConnection, long)`; `IntegrationsMember.ResolveAsync(...)`; `ConnectionSecrets.{Read, Unreadable}`; `ConnectionVerification.{EnsurePassesAsync, Failure, FailureCode}`; `CommandText.Keys(...)`.
  - `CreateConnectionCommand(Guid TenantId, string? ProviderKey, string? Name, IReadOnlyDictionary<string, string?>? Fields, IReadOnlyDictionary<string, string?>? Secrets) : ICommand<ConnectionResponse>` + `CreateConnectionValidator(IIntegrationProviderCatalog)` + `CreateConnectionHandler`.
  - `UpdateConnectionCommand(Guid TenantId, Guid ConnectionId, long ExpectedVersion, string? Name, IReadOnlyDictionary<string, string?>? Fields, IReadOnlyDictionary<string, string?>? Secrets) : ICommand<ConnectionResponse>` + `UpdateConnectionValidator` + `UpdateConnectionHandler`.
  - Dobles: `FakeConnectionTester`, `TesterCall`, `RecordingAuditRecorder`, `AuditRecord`, `FakeIntegrationProviderCatalog`; `IntegrationsTestBed.Tester`, `.Audit`, `.CreateHandler()`, `.UpdateHandler()`.

- [ ] **Step 1: Escribir los dobles y las pruebas que fallan**

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationsTestBed.Writes.cs`:

```csharp
using Modules.Audit.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.UnitTests;

internal sealed partial class IntegrationsTestBed
{
    public FakeConnectionTester Tester { get; } = new();

    public RecordingAuditRecorder Audit { get; } = new();

    public CreateConnectionHandler CreateHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Protector, Tester, Modules, Memberships, AuthorNames,
            context ?? Context(), Clock, new CreateConnectionValidator(Catalog));

    public UpdateConnectionHandler UpdateHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Protector, Tester, Modules, AuthorNames,
            context ?? Context(), Clock, new UpdateConnectionValidator());
}

internal sealed record TesterCall(
    string ProviderKey, IReadOnlyDictionary<string, string> Fields, IReadOnlyDictionary<string, string> Secrets);

/// <summary>El proveedor de mentira: anota lo que se probó y responde <see cref="Result"/>.</summary>
internal sealed class FakeConnectionTester : IConnectionTester
{
    public ConnectionTestResult Result { get; set; } = ConnectionTestResult.Ok;

    public List<TesterCall> Calls { get; } = [];

    public Task<ConnectionTestResult> TestAsync(
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        Calls.Add(new TesterCall(
            provider.Key, new Dictionary<string, string>(fields), new Dictionary<string, string>(secrets)));
        return Task.FromResult(Result);
    }
}

internal sealed record AuditRecord(
    Guid TenantId,
    Guid ActorId,
    AuditActorType ActorType,
    string Action,
    Guid ConnectionId,
    string Outcome,
    IReadOnlyCollection<string> ChangedFields,
    DateTimeOffset OccurredAt);

internal sealed class RecordingAuditRecorder : IIntegrationsAuditRecorder
{
    public List<AuditRecord> Entries { get; } = [];

    public void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        Guid connectionId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        Entries.Add(new AuditRecord(tenantId, actorId, actorType, action, connectionId, outcome, changedFields, occurredAt));
}

/// <summary>Criterio 5 del spec: un catálogo con un proveedor que sólo existe en las pruebas.</summary>
internal sealed class FakeIntegrationProviderCatalog(params IntegrationProvider[] providers) : IIntegrationProviderCatalog
{
    public IReadOnlyList<IntegrationProvider> All => providers;

    public IntegrationProvider? Find(string? key) =>
        providers.FirstOrDefault(provider => string.Equals(provider.Key, key, StringComparison.Ordinal));
}
```

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/CreateConnectionValidatorTests.cs`:

```csharp
using FluentValidation.Results;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Códigos de error»: <c>validation.failed</c> con <c>errors</c> por
/// <c>name</c>, <c>providerKey</c>, <c>fields.&lt;key&gt;</c> y <c>secrets.&lt;key&gt;</c>, en minúscula
/// y exactos (P26): es lo único que el formulario sabe leer para marcar el input.
/// </summary>
public sealed class CreateConnectionValidatorTests
{
    private static CreateConnectionCommand Command(
        string? name = "WhatsApp sede norte",
        string? providerKey = "zenvia",
        Dictionary<string, string?>? fields = null,
        Dictionary<string, string?>? secrets = null) =>
        new(
            Guid.CreateVersion7(),
            providerKey,
            name,
            fields ?? new Dictionary<string, string?> { [ZenviaFieldKeys.FromNumber] = "573001234567" },
            secrets ?? new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "zenvia-token-TEST-1" });

    private static async Task<List<ValidationFailure>> ErrorsAsync(CreateConnectionCommand command) =>
        (await new CreateConnectionValidator(new IntegrationProviderCatalog())
            .ValidateAsync(command, TestContext.Current.CancellationToken)).Errors;

    [Fact]
    public async Task AValidZenviaBodyPasses() =>
        Assert.Empty(await ErrorsAsync(Command()));

    [Theory]
    [InlineData(null)]
    [InlineData("whatsapp")]
    [InlineData("Zenvia")]
    public async Task AnUnknownProviderGoesToProviderKey(string? providerKey)
    {
        var error = Assert.Single(await ErrorsAsync(Command(providerKey: providerKey)));

        Assert.Equal("providerKey", error.PropertyName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("a\u0000b")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task AnInvalidNameGoesToName(string? name)
    {
        var error = Assert.Single(await ErrorsAsync(Command(name: name)));

        Assert.Equal("name", error.PropertyName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMissingSecretIsRequiredOnCreate(string? token)
    {
        var error = Assert.Single(await ErrorsAsync(Command(
            secrets: new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = token })));

        Assert.Equal("secrets.apiToken", error.PropertyName);
        Assert.Equal("Completa este campo.", error.ErrorMessage);
    }

    // Review Focus 2: un secreto dentro de fields terminaría en jsonb, en claro.
    [Fact]
    public async Task ASecretSentAsAFieldIsUnknownAndNamedByItsKey()
    {
        var errors = await ErrorsAsync(Command(fields: new Dictionary<string, string?>
        {
            [ZenviaFieldKeys.FromNumber] = "573001234567",
            [ZenviaFieldKeys.ApiToken] = "zenvia-token-TEST-1",
        }));

        var error = Assert.Single(errors);
        Assert.Equal("fields.apiToken", error.PropertyName);
        Assert.Equal("Este campo no existe para este proveedor.", error.ErrorMessage);
    }

    [Fact]
    public async Task AKeyThatIsNotAFieldKeyIsReportedWithoutEchoingIt()
    {
        var errors = await ErrorsAsync(Command(fields: new Dictionary<string, string?>
        {
            [ZenviaFieldKeys.FromNumber] = "573001234567",
            ["a.b<script>"] = "x",
        }));

        var error = Assert.Single(errors);
        Assert.Equal("fields", error.PropertyName);
        Assert.DoesNotContain("script", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("+573001234567", "Escribe el número con indicativo de país, sólo dígitos y sin '+' (entre 10 y 15).")]
    [InlineData("57300\u00001234567", "Escribe el número con indicativo de país, sólo dígitos y sin '+' (entre 10 y 15).")]
    [InlineData("5730012345678901", "Usa máximo 15 caracteres.")]
    public async Task AFromNumberWithoutItsShapeGoesToItsField(string fromNumber, string message)
    {
        var error = Assert.Single(await ErrorsAsync(Command(
            fields: new Dictionary<string, string?> { [ZenviaFieldKeys.FromNumber] = fromNumber })));

        Assert.Equal("fields.fromNumber", error.PropertyName);
        Assert.Equal(message, error.ErrorMessage);
    }

    [Fact]
    public async Task NullFieldsAndSecretsAreRequiredNotAServerError()
    {
        var errors = await new CreateConnectionValidator(new IntegrationProviderCatalog()).ValidateAsync(
            new CreateConnectionCommand(Guid.CreateVersion7(), "zenvia", "Norte", null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(["fields.fromNumber", "secrets.apiToken"], errors.Errors.Select(error => error.PropertyName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task NoMessageCarriesTheValueThatWasSent()
    {
        var errors = await ErrorsAsync(Command(
            fields: new Dictionary<string, string?> { [ZenviaFieldKeys.FromNumber] = "+57SENTINEL" },
            secrets: new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "con espacio SENTINEL" }));

        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.DoesNotContain("SENTINEL", error.ErrorMessage, StringComparison.Ordinal));
    }
}
```

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/CreateConnectionHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, <c>POST /connections</c> y decisión 5: valida, prueba y recién ahí guarda. Una
/// credencial rechazada o un proveedor inalcanzable no dejan nada guardado.
/// </summary>
public sealed class CreateConnectionHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreateConnectionCommand Command(IntegrationsTestBed bed, string name = "WhatsApp sede norte") =>
        new(
            bed.TenantId,
            "zenvia",
            name,
            new Dictionary<string, string?> { [ZenviaFieldKeys.FromNumber] = IntegrationsTestBed.FromNumber },
            new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = IntegrationsTestBed.Token });

    [Fact]
    public async Task CreatesAnActiveConnectionAfterAPassingTestAndAuditsKeysNotValues()
    {
        var bed = new IntegrationsTestBed();

        var response = await bed.CreateHandler().HandleAsync(Command(bed), Ct);

        Assert.Equal("Active", response.Status);
        Assert.Equal(1, response.Version);
        Assert.Equal(IntegrationsTestBed.Now, response.LastVerifiedAt);
        Assert.Equal(bed.MemberId, response.CreatedBy.MemberId);
        Assert.Equal(IntegrationsTestBed.AuthorName, response.CreatedBy.DisplayName);
        Assert.True(response.Secrets[ZenviaFieldKeys.ApiToken].Readable);
        var call = Assert.Single(bed.Tester.Calls);
        Assert.Equal("zenvia", call.ProviderKey);
        Assert.Equal(IntegrationsTestBed.Token, call.Secrets[ZenviaFieldKeys.ApiToken]);
        Assert.Equal(IntegrationsTestBed.FromNumber, call.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Single(bed.Repository.Connections);
        Assert.Equal(1, bed.UnitOfWork.Saves);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal("integrations.connection.created", audit.Action);
        Assert.Equal("success", audit.Outcome);
        Assert.Equal(AuditActorType.Human, audit.ActorType);
        Assert.Equal(bed.SubjectId, audit.ActorId);
        Assert.Equal(["apiToken", "fromNumber", "name"], audit.ChangedFields);
    }

    [Fact]
    public async Task RejectedCredentialsSaveNothingAndMarkTheSecretField()
    {
        var bed = new IntegrationsTestBed();
        bed.Tester.Result = ConnectionTestResult.CredentialsRejected;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("integrations.connection.credentials_rejected", error.Code);
        Assert.Equal(["secrets.apiToken"], error.FieldErrors.Keys);
        Assert.Empty(bed.Repository.Connections);
        Assert.Equal(0, bed.UnitOfWork.Saves);
        Assert.Empty(bed.Audit.Entries);
    }

    // D3: «no pude verificar» también bloquea el guardado.
    [Fact]
    public async Task AnUnreachableProviderBlocksTheSave()
    {
        var bed = new IntegrationsTestBed();
        bed.Tester.Result = ConnectionTestResult.Unreachable("timeout");

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("integrations.connection.provider_unreachable", error.Code);
        Assert.Empty(error.FieldErrors);
        Assert.Empty(bed.Repository.Connections);
    }

    // P12: si el proveedor dice que un campo no sirve, el 422 marca ese campo.
    [Fact]
    public async Task AFieldTheProviderRejectsGoesToThatField()
    {
        var bed = new IntegrationsTestBed();
        bed.Tester.Result = ConnectionTestResult.Invalid(ZenviaFieldKeys.FromNumber, "unknown sender");

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("fields.fromNumber", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(bed.Repository.Connections);
    }

    // P18: sin llave activa nada se puede guardar, así que el 503 sale antes de validar o probar.
    [Fact]
    public async Task WithoutAnActiveKeyItIs503BeforeValidatingOrCallingTheProvider()
    {
        var bed = new IntegrationsTestBed();
        bed.Protector.ActiveKeyId = null;

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            bed.CreateHandler().HandleAsync(Command(bed, name: ""), Ct));

        Assert.Equal("integrations.secret_protection.unavailable", error.Code);
        Assert.Empty(bed.Tester.Calls);
    }

    [Fact]
    public async Task WithoutManageOrForAnotherTenantItIsForbiddenBeforeAnything()
    {
        var bed = new IntegrationsTestBed();
        var otherTenant = bed.Context(Guid.CreateVersion7());
        bed.Permissions = [IntegrationsPermissions.ConnectionRead];

        foreach (var context in new[] { bed.Context(), otherTenant })
        {
            var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                bed.CreateHandler(context).HandleAsync(Command(bed, name: ""), Ct));
            Assert.Equal("authorization.denied", error.Code);
        }

        Assert.Empty(bed.Tester.Calls);
    }

    [Fact]
    public async Task AHiddenProviderIsModuleNotEnabled()
    {
        var bed = new IntegrationsTestBed();
        bed.HideQuotations();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Empty(bed.Tester.Calls);
    }

    // D1: el tope se mira antes de llamar al proveedor.
    [Fact]
    public async Task TheTwentyFirstConnectionIsLimitReachedWithoutCallingTheProvider()
    {
        var bed = new IntegrationsTestBed();
        for (var index = 0; index < 20; index++)
        {
            bed.Seed($"Línea {index}");
        }

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("integrations.connection.limit_reached", error.Code);
        Assert.Empty(bed.Tester.Calls);
    }

    [Fact]
    public async Task ASubjectWithoutAnActiveMembershipIsForbidden()
    {
        var bed = new IntegrationsTestBed();
        bed.Memberships.Active.Clear();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("authorization.denied", error.Code);
    }

    // Criterio 5 del spec: un proveedor nuevo es una entrada de catálogo y su probador; los handlers
    // no cambian. Éste sólo existe en esta prueba.
    [Fact]
    public async Task AProviderRegisteredOnlyInTestsWorksWithoutTouchingTheHandlers()
    {
        var fakeCrm = new IntegrationProvider(
            "fake-crm",
            "CRM de prueba",
            IntegrationCategory.Messaging,
            [TenantModuleKeys.Quotations],
            [
                new FieldDefinition("endpoint", "URL", FieldKind.Url, required: true, maxLength: 200, pattern: "^https://", invalidMessage: "Usa una URL https."),
                new FieldDefinition("apiKey", "Clave", FieldKind.Secret, required: true, maxLength: 100, pattern: null, invalidMessage: "Revisa la clave."),
            ],
            maxConnections: 2);
        var bed = new IntegrationsTestBed(new FakeIntegrationProviderCatalog(IntegrationProviders.Zenvia, fakeCrm));

        var response = await bed.CreateHandler().HandleAsync(
            new CreateConnectionCommand(
                bed.TenantId,
                "fake-crm",
                "CRM",
                new Dictionary<string, string?> { ["endpoint"] = "https://crm.example.com" },
                new Dictionary<string, string?> { ["apiKey"] = "crm-key" }),
            Ct);

        Assert.Equal("fake-crm", response.ProviderKey);
        Assert.Equal(["endpoint"], response.Fields.Keys);
        Assert.Equal(["apiKey"], response.Secrets.Keys);
        Assert.Equal("fake-crm", Assert.Single(bed.Tester.Calls).ProviderKey);
    }
}
```

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/UpdateConnectionHandlerTests.cs`:

```csharp
using System.Text;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, <c>PUT /connections/{id}</c>: If-Match, secreto ausente conserva (D5), prueba sólo
/// si cambió algo que importa (P9), y una prueba fallida no toca nada.
/// </summary>
public sealed class UpdateConnectionHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UpdateConnectionCommand Update(
        IntegrationsTestBed bed,
        IntegrationConnection connection,
        string name = "WhatsApp sede norte",
        string? fromNumber = IntegrationsTestBed.FromNumber,
        Dictionary<string, string?>? secrets = null,
        long? version = null) =>
        new(
            bed.TenantId,
            connection.Id,
            version ?? connection.Version,
            name,
            new Dictionary<string, string?> { [ZenviaFieldKeys.FromNumber] = fromNumber },
            secrets ?? []);

    private static string StoredToken(IntegrationConnection connection) =>
        Encoding.UTF8.GetString(connection.Secrets[0].Protected.Ciphertext).Split('|')[2];

    [Fact]
    public async Task RenamingOnlyDoesNotCallTheProvider()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var response = await bed.UpdateHandler().HandleAsync(Update(bed, connection, name: "Sede sur"), Ct);

        Assert.Equal("Sede sur", response.Name);
        Assert.Equal(2, response.Version);
        Assert.Empty(bed.Tester.Calls);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal("integrations.connection.updated", audit.Action);
        Assert.Equal(["name"], audit.ChangedFields);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    // D5 y Review Focus 3: ausente, null, vacío o sólo espacios conservan la clave guardada.
    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("spaces")]
    public async Task AnAbsentOrBlankSecretKeepsTheStoredOne(string shape)
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        var secrets = shape switch
        {
            "absent" => new Dictionary<string, string?>(),
            "null" => new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = null },
            "empty" => new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "" },
            _ => new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "   " },
        };

        await bed.UpdateHandler().HandleAsync(Update(bed, connection, fromNumber: "573009999999", secrets: secrets), Ct);

        Assert.Equal(IntegrationsTestBed.Token, Assert.Single(bed.Tester.Calls).Secrets[ZenviaFieldKeys.ApiToken]);
        Assert.Equal(IntegrationsTestBed.Token, StoredToken(connection));
        Assert.Equal(["fromNumber"], bed.Audit.Entries[0].ChangedFields);
        Assert.Equal("integrations.connection.verified", bed.Audit.Entries[1].Action);
    }

    [Fact]
    public async Task ANewSecretIsTestedAndReplacesTheStoredOne()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        await bed.UpdateHandler().HandleAsync(
            Update(bed, connection, secrets: new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "nuevo-token" }),
            Ct);

        Assert.Equal("nuevo-token", Assert.Single(bed.Tester.Calls).Secrets[ZenviaFieldKeys.ApiToken]);
        Assert.Equal("nuevo-token", StoredToken(connection));
        Assert.Equal(["apiToken"], bed.Audit.Entries[0].ChangedFields);
    }

    [Fact]
    public async Task RejectedCredentialsLeaveTheConnectionUntouched()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Tester.Result = ConnectionTestResult.CredentialsRejected;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.UpdateHandler().HandleAsync(
            Update(bed, connection, secrets: new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "otro" }), Ct));

        Assert.Equal("integrations.connection.credentials_rejected", error.Code);
        Assert.Equal(1, connection.Version);
        Assert.Equal(IntegrationsTestBed.Token, StoredToken(connection));
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task APassingTestBringsNeedsAttentionBackToActive()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, IntegrationsTestBed.Now);

        var response = await bed.UpdateHandler().HandleAsync(Update(bed, connection, fromNumber: "573009999999"), Ct);

        Assert.Equal("Active", response.Status);
    }

    [Fact]
    public async Task AStaleVersionIs412BeforeCallingTheProvider()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            bed.UpdateHandler().HandleAsync(Update(bed, connection, fromNumber: "573009999999", version: 7), Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Empty(bed.Tester.Calls);
    }

    // P14: sin la clave guardada no hay con qué probar el número nuevo.
    [Fact]
    public async Task AnUnreadableStoredSecretWithoutANewOneIs422OnTheSecretField()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Protector.Unreadable.Add((connection.Id, ZenviaFieldKeys.ApiToken));

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.UpdateHandler().HandleAsync(Update(bed, connection, fromNumber: "573009999999"), Ct));

        Assert.Equal("secrets.apiToken", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(bed.Tester.Calls);
    }

    [Fact]
    public async Task AnUnreadableStoredSecretIsFineWhenANewOneComes()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Protector.Unreadable.Add((connection.Id, ZenviaFieldKeys.ApiToken));

        var response = await bed.UpdateHandler().HandleAsync(
            Update(bed, connection, secrets: new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "nuevo-token" }),
            Ct);

        Assert.Equal(2, response.Version);
    }

    [Fact]
    public async Task NothingChangedIsANoOp()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var response = await bed.UpdateHandler().HandleAsync(Update(bed, connection), Ct);

        Assert.Equal(1, response.Version);
        Assert.Equal(0, bed.UnitOfWork.Saves);
        Assert.Empty(bed.Audit.Entries);
        Assert.Empty(bed.Tester.Calls);
    }

    [Fact]
    public async Task AnIdOfAnotherTenantIsNotFound()
    {
        var bed = new IntegrationsTestBed();
        var foreign = bed.Seed(tenantId: Guid.CreateVersion7());

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.UpdateHandler().HandleAsync(Update(bed, foreign), Ct));

        Assert.Equal("integrations.connection.not_found", error.Code);
    }

    // Review Focus 2, en el PUT.
    [Fact]
    public async Task ASecretSentAsAFieldIsRejectedBeforeAnything()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        var command = Update(bed, connection) with
        {
            Fields = new Dictionary<string, string?>
            {
                [ZenviaFieldKeys.FromNumber] = IntegrationsTestBed.FromNumber,
                [ZenviaFieldKeys.ApiToken] = "en-claro",
            },
        };

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.UpdateHandler().HandleAsync(command, Ct));

        Assert.Equal("fields.apiToken", Assert.Single(error.Errors).PropertyName);
        Assert.DoesNotContain("en-claro", connection.Fields.Values);
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~CreateConnectionValidatorTests|FullyQualifiedName~CreateConnectionHandlerTests|FullyQualifiedName~UpdateConnectionHandlerTests"
```

Esperado: FAIL de compilación: `error CS0246: … 'IConnectionTester' could not be found` (y `ConnectionTestResult`, `IIntegrationsAuditRecorder`, `CreateConnectionCommand`, `ServiceUnavailableException`, …).

- [ ] **Step 3: Implementar**

En `src/BuildingBlocks/BuildingBlocks.Application/ApplicationExceptions.cs`, al final:

```csharp

// 503: lo pedido es válido pero el servidor no puede cumplirlo por su configuración (spec 2026-10-08:
// sin llave activa, Integrations no puede cifrar). Distinta de un 500: no es un error del código, y
// el cliente puede mostrar "todavía no disponible" en vez de "algo falló".
public sealed class ServiceUnavailableException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
```

`src/Modules/Integrations/Modules.Integrations.Application/ConnectionTesting.cs`:

```csharp
using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

public enum ConnectionTestOutcome
{
    Ok,
    CredentialsRejected,
    Unreachable,
    Invalid,
}

/// <summary>Spec 2026-10-08, «Probar la credencial»: <c>Ok</c>, <c>CredentialsRejected</c>,
/// <c>Unreachable(reason)</c> o <c>Invalid(fieldKey, reason)</c>. <c>Reason</c> es técnico y nunca
/// lleva un valor: no sale por HTTP.</summary>
public sealed record ConnectionTestResult(ConnectionTestOutcome Outcome, string? FieldKey = null, string? Reason = null)
{
    public static ConnectionTestResult Ok { get; } = new(ConnectionTestOutcome.Ok);

    public static ConnectionTestResult CredentialsRejected { get; } = new(ConnectionTestOutcome.CredentialsRejected);

    public static ConnectionTestResult Unreachable(string reason) => new(ConnectionTestOutcome.Unreachable, Reason: reason);

    public static ConnectionTestResult Invalid(string fieldKey, string reason) =>
        new(ConnectionTestOutcome.Invalid, fieldKey, reason);
}

/// <summary>
/// Prueba una credencial contra el proveedor. Un adaptador por proveedor en Infrastructure, elegido por
/// <c>provider.Key</c> (spec, «Probar la credencial»). Nunca registra secretos, headers ni cuerpos.
/// </summary>
public interface IConnectionTester
{
    Task<ConnectionTestResult> TestAsync(
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken);
}
```

`src/Modules/Integrations/Modules.Integrations.Application/ConnectionInputRules.cs`:

```csharp
using FluentValidation;
using FluentValidation.Results;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

/// <summary>
/// Las reglas de entrada que ve el formulario (spec 2026-10-08, «Códigos de error»). Cada falla nace
/// acá con su nombre de propiedad exacto y en minúscula (<c>name</c>, <c>providerKey</c>,
/// <c>fields.fromNumber</c>, <c>secrets.apiToken</c>; P26): ninguna regla de los validadores deja que
/// FluentValidation derive el nombre, que saldría en PascalCase. Los mensajes van en español, tuteando,
/// y nunca llevan el valor que se mandó: terminan en ProblemDetails, en el log y en
/// <c>platform.request_failures</c>.
/// </summary>
internal static class ConnectionInputRules
{
    public const string NameMessage = "Escribe un nombre de 1 a 80 caracteres, en una sola línea.";
    public const string ProviderUnknownMessage = "Elige un proveedor del catálogo.";
    public const string RequiredMessage = "Completa este campo.";
    public const string UnknownFieldMessage = "Este campo no existe para este proveedor.";
    public const string UnknownKeysMessage = "Llegaron campos que este proveedor no tiene.";
    public const string CredentialsRejectedMessage = "El proveedor rechazó esta clave: revísala y vuelve a pegarla.";
    public const string ProviderRejectedFieldMessage = "El proveedor no aceptó este valor: revísalo.";
    public const string UnreadableSecretMessage = "La clave guardada ya no se puede leer: pégala de nuevo.";

    private const string FieldsPrefix = "fields";
    private const string SecretsPrefix = "secrets";

    public static string TooLongMessage(int maxLength) => $"Usa máximo {maxLength} caracteres.";

    public static IEnumerable<ValidationFailure> CheckName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > IntegrationConnection.NameMaxLength || trimmed.Any(char.IsControl))
        {
            yield return new ValidationFailure("name", NameMessage);
        }
    }

    /// <param name="requireSecrets">En el POST todo secreto requerido tiene que venir; en el PUT el
    /// ausente conserva el guardado (D5).</param>
    public static IEnumerable<ValidationFailure> CheckValues(
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string?>? fields,
        IReadOnlyDictionary<string, string?>? secrets,
        bool requireSecrets) =>
        CheckGroup(provider, fields, secret: false, require: true)
            .Concat(CheckGroup(provider, secrets, secret: true, require: requireSecrets));

    /// <summary>Los valores no vacíos del grupo, recortados. Lo inválido ya lo rechazó el validador.</summary>
    public static Dictionary<string, string> Normalize(
        IntegrationProvider provider, IReadOnlyDictionary<string, string?>? values, bool secret)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values is null)
        {
            return normalized;
        }

        foreach (var definition in secret ? provider.SecretFields : provider.PublicFields)
        {
            if (values.TryGetValue(definition.Key, out var raw) && raw?.Trim() is { Length: > 0 } value)
            {
                normalized[definition.Key] = value;
            }
        }

        return normalized;
    }

    public static void ThrowIfAny(IEnumerable<ValidationFailure> failures)
    {
        var list = failures.ToList();
        if (list.Count > 0)
        {
            throw new ValidationException(list);
        }
    }

    public static void AddTo<T>(ValidationContext<T> context, IEnumerable<ValidationFailure> failures)
    {
        foreach (var failure in failures)
        {
            context.AddFailure(failure);
        }
    }

    /// <summary><c>fields.&lt;key&gt;</c> o <c>secrets.&lt;key&gt;</c> según el catálogo; <c>fields</c>
    /// si la clave no es del proveedor.</summary>
    public static string PropertyFor(IntegrationProvider provider, string? fieldKey) =>
        provider.FindField(fieldKey ?? string.Empty) is { } definition
            ? $"{(definition.IsSecret ? SecretsPrefix : FieldsPrefix)}.{definition.Key}"
            : FieldsPrefix;

    private static IEnumerable<ValidationFailure> CheckGroup(
        IntegrationProvider provider, IReadOnlyDictionary<string, string?>? values, bool secret, bool require)
    {
        var prefix = secret ? SecretsPrefix : FieldsPrefix;
        values ??= new Dictionary<string, string?>();

        // Review Focus 2: un secreto dentro de fields es "no existe", no un valor que se guarda en claro.
        // Una clave con forma rara se reporta bajo el grupo, sin repetirla en el mapa errors.
        var weirdKey = false;
        foreach (var key in values.Keys)
        {
            if (provider.FindField(key) is { } known && known.IsSecret == secret)
            {
                continue;
            }

            if (FieldDefinition.IsValidKey(key))
            {
                yield return new ValidationFailure($"{prefix}.{key}", UnknownFieldMessage);
            }
            else
            {
                weirdKey = true;
            }
        }

        if (weirdKey)
        {
            yield return new ValidationFailure(prefix, UnknownKeysMessage);
        }

        foreach (var definition in secret ? provider.SecretFields : provider.PublicFields)
        {
            var property = $"{prefix}.{definition.Key}";
            var value = values.TryGetValue(definition.Key, out var raw) ? raw?.Trim() : null;
            if (string.IsNullOrEmpty(value))
            {
                if (require && definition.Required)
                {
                    yield return new ValidationFailure(property, RequiredMessage);
                }

                continue;
            }

            if (definition.IsTooLong(value))
            {
                yield return new ValidationFailure(property, TooLongMessage(definition.MaxLength));
            }
            else if (!definition.HasValidShape(value))
            {
                yield return new ValidationFailure(property, definition.InvalidMessage);
            }
        }
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Application/ConnectionWriteSupport.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Audit.Domain;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>
/// Auditoría atómica de Integrations (ADR 0019): la entrada se acumula en <c>IntegrationsDbContext</c> y
/// commitea con el cambio. Deliberadamente no es el <c>IAuditRecorder</c> compartido, que ya está
/// ligado a <c>TenancyDbContext</c>: una segunda ligadura le robaría la auditoría a Tenancy (P1;
/// precedente <c>IIdentityAuditRecorder</c>). <paramref name="changedFields"/> va por clave, nunca por
/// valor (spec, «Auditoría»).
/// </summary>
public interface IIntegrationsAuditRecorder
{
    void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        Guid connectionId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt);
}

/// <summary>Spec 2026-10-08, «Auditoría».</summary>
public static class ConnectionAuditActions
{
    public const string ResourceType = "integration_connection";
    public const string Created = "integrations.connection.created";
    public const string Updated = "integrations.connection.updated";
    public const string Paused = "integrations.connection.paused";
    public const string Resumed = "integrations.connection.resumed";
    public const string Deleted = "integrations.connection.deleted";
    public const string Verified = "integrations.connection.verified";
    public const string NeedsAttention = "integrations.connection.needs_attention";
    public const string Success = "success";
    public const string Failure = "failure";
}

internal static class ConnectionAudit
{
    public static void ByMember(
        IIntegrationsAuditRecorder recorder,
        IExecutionContext executionContext,
        IntegrationConnection connection,
        string action,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt,
        string outcome = ConnectionAuditActions.Success) =>
        recorder.Record(
            connection.TenantId, executionContext.SubjectId, AuditActorType.Human, action, connection.Id,
            outcome, changedFields, occurredAt);

    /// <summary>Lo que se audita al crear: el nombre y cada clave, ordenadas.</summary>
    public static string[] KeysOf(IntegrationConnection connection)
    {
        string[] keys = ["name", .. connection.Fields.Keys, .. connection.Secrets.Select(secret => secret.FieldKey)];
        Array.Sort(keys, StringComparer.Ordinal);
        return keys;
    }
}

internal static class SecretProtectionGuard
{
    /// <summary>Spec, «Secreto en reposo»: fuera de producción, sin llave activa, crear o editar
    /// responde 503 (la lectura sigue funcionando).</summary>
    public static void EnsureAvailable(ISecretProtector protector)
    {
        if (protector.ActiveKeyId is null)
        {
            throw new ServiceUnavailableException(
                IntegrationsErrorCodes.SecretProtectionUnavailable,
                "Integrations:SecretProtection:ActiveKeyId is not configured: connections cannot be created or edited.");
        }
    }
}

internal static class ConcurrencyGuard
{
    public static void EnsureVersion(IntegrationConnection connection, long expectedVersion)
    {
        if (connection.Version != expectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The connection changed after it was loaded.");
        }
    }
}

/// <summary>Subject autenticado → su membresía activa (copia de <c>PosCashierResolver</c>):
/// <c>created_by</c> es un MemberId.</summary>
internal static class IntegrationsMember
{
    public static async Task<Guid> ResolveAsync(
        IMembershipDirectory membershipDirectory,
        IExecutionContext executionContext,
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken)
        ?? throw new RequestForbiddenException(
            "authorization.denied", "The subject does not have an active membership in this tenant.");
}

/// <summary>Los secretos guardados, en claro, para probarlos. <see cref="ToString"/> sólo muestra
/// claves: el de un record imprimiría todo.</summary>
internal sealed record StoredSecrets(IReadOnlyDictionary<string, string> Plain, IReadOnlyList<string> Unreadable)
{
    public override string ToString() =>
        $"StoredSecrets {{ Keys = [{string.Join(", ", Plain.Keys)}], Unreadable = [{string.Join(", ", Unreadable)}] }}";
}

internal static class ConnectionSecrets
{
    public static StoredSecrets Read(ISecretProtector protector, IntegrationConnection connection)
    {
        var plain = new Dictionary<string, string>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        foreach (var secret in connection.Secrets)
        {
            if (protector.TryUnprotect(connection.Id, secret.FieldKey, secret.Protected, out var value))
            {
                plain[secret.FieldKey] = value;
            }
            else
            {
                unreadable.Add(secret.FieldKey);
            }
        }

        return new StoredSecrets(plain, unreadable);
    }

    /// <summary>P14: llave retirada o bytes dañados; la pantalla pide pegar la clave otra vez.</summary>
    public static ValidationException Unreadable(IEnumerable<string> fieldKeys) =>
        new(fieldKeys
            .Select(key => new ValidationFailure($"secrets.{key}", ConnectionInputRules.UnreadableSecretMessage))
            .ToList());
}

internal static class ConnectionVerification
{
    /// <summary>Spec, «Probar la credencial»: después de validar y antes de guardar; cualquier cosa
    /// que no sea <c>Ok</c> bloquea (decisión 5 y D3).</summary>
    public static async Task EnsurePassesAsync(
        IConnectionTester tester,
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        var result = await tester.TestAsync(provider, fields, secrets, cancellationToken);
        if (result.Outcome != ConnectionTestOutcome.Ok)
        {
            throw Failure(provider, result);
        }
    }

    /// <summary>
    /// <c>credentials_rejected</c> marca todos los secretos del proveedor en <c>errors</c> (spec,
    /// «Códigos de error»); <c>provider_unreachable</c> no marca campo (el formulario lo muestra como
    /// aviso); <c>Invalid</c> marca el campo que el proveedor nombró (P12).
    /// </summary>
    public static Exception Failure(IntegrationProvider provider, ConnectionTestResult result) =>
        result.Outcome switch
        {
            ConnectionTestOutcome.CredentialsRejected => new IntegrationsDomainException(
                IntegrationsErrorCodes.CredentialsRejected,
                "The provider rejected the credentials.",
                provider.SecretFields.ToDictionary(
                    field => $"secrets.{field.Key}",
                    _ => new[] { ConnectionInputRules.CredentialsRejectedMessage },
                    StringComparer.Ordinal)),
            ConnectionTestOutcome.Invalid => new ValidationException(
            [
                new ValidationFailure(
                    ConnectionInputRules.PropertyFor(provider, result.FieldKey),
                    ConnectionInputRules.ProviderRejectedFieldMessage),
            ]),
            ConnectionTestOutcome.Unreachable => new IntegrationsDomainException(
                IntegrationsErrorCodes.ProviderUnreachable,
                "The provider could not be reached to verify the credentials; nothing was saved."),
            _ => new InvalidOperationException("A successful test is not a failure."),
        };

    public static string FailureCode(ConnectionTestResult result) =>
        result.Outcome switch
        {
            ConnectionTestOutcome.CredentialsRejected => ConnectionFailureCodes.CredentialsRejected,
            ConnectionTestOutcome.Invalid => ConnectionFailureCodes.FieldInvalid,
            ConnectionTestOutcome.Unreachable => ConnectionFailureCodes.ProviderUnreachable,
            _ => throw new InvalidOperationException("A successful test has no failure code."),
        };
}

internal static class CommandText
{
    /// <summary>Sólo las claves: los comandos se pueden terminar imprimiendo en un log.</summary>
    public static string Keys(IReadOnlyDictionary<string, string?>? values) =>
        values is null ? string.Empty : string.Join(", ", values.Keys);
}
```

`src/Modules/Integrations/Modules.Integrations.Application/CreateConnection.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>
/// El cuerpo de <c>POST /connections</c>: <c>{ providerKey, name, fields{}, secrets{} }</c>.
/// <see cref="ToString"/> sólo muestra claves: el de un record imprimiría los secretos.
/// </summary>
public sealed record CreateConnectionCommand(
    Guid TenantId,
    string? ProviderKey,
    string? Name,
    IReadOnlyDictionary<string, string?>? Fields,
    IReadOnlyDictionary<string, string?>? Secrets) : ICommand<ConnectionResponse>
{
    public override string ToString() =>
        $"CreateConnectionCommand {{ TenantId = {TenantId}, ProviderKey = {ProviderKey}, Name = {Name}, "
        + $"Fields = [{CommandText.Keys(Fields)}], Secrets = [{CommandText.Keys(Secrets)}] }}";
}

/// <summary>
/// Requeridos, largos, patrones, claves desconocidas y <c>providerKey</c> fuera del catálogo (spec,
/// «Códigos de error»). Cada falla la arma <see cref="ConnectionInputRules"/> con su nombre exacto
/// (P26).
/// </summary>
public sealed class CreateConnectionValidator : AbstractValidator<CreateConnectionCommand>
{
    public CreateConnectionValidator(IIntegrationProviderCatalog catalog)
    {
        RuleFor(command => command.Name)
            .Custom((name, context) => ConnectionInputRules.AddTo(context, ConnectionInputRules.CheckName(name)));
        RuleFor(command => command.ProviderKey).Custom((providerKey, context) =>
        {
            if (catalog.Find(providerKey) is not { } provider)
            {
                context.AddFailure(new FluentValidation.Results.ValidationFailure(
                    "providerKey", ConnectionInputRules.ProviderUnknownMessage));
                return;
            }

            var command = context.InstanceToValidate;
            ConnectionInputRules.AddTo(
                context, ConnectionInputRules.CheckValues(provider, command.Fields, command.Secrets, requireSecrets: true));
        });
    }
}

/// <summary>
/// Spec 2026-10-08, <c>POST /connections</c>, en este orden (P18): autoriza (403 antes de leer el
/// cuerpo), 503 sin llave activa, valida, visibilidad (403 <c>tenancy.module_not_enabled</c>), membresía,
/// tope (D1), prueba contra el proveedor (decisión 5) y recién ahí crea, audita y guarda.
/// </summary>
public sealed class CreateConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    ISecretProtector protector,
    IConnectionTester tester,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateConnectionCommand> validator)
    : ICommandHandler<CreateConnectionCommand, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(CreateConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        SecretProtectionGuard.EnsureAvailable(protector);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // El validador ya garantizó que el proveedor existe.
        var provider = catalog.Find(command.ProviderKey)!;
        await ProviderVisibility.EnsureVisibleAsync(tenantModules, command.TenantId, provider, cancellationToken);
        var author = await IntegrationsMember.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        if (await repository.CountAsync(command.TenantId, provider.Key, cancellationToken) >= provider.MaxConnections)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.LimitReached,
                $"A tenant can have at most {provider.MaxConnections} connections of this provider.");
        }

        var fields = ConnectionInputRules.Normalize(provider, command.Fields, secret: false);
        var secrets = ConnectionInputRules.Normalize(provider, command.Secrets, secret: true);
        await ConnectionVerification.EnsurePassesAsync(tester, provider, fields, secrets, cancellationToken);

        var now = clock.UtcNow;
        var connection = IntegrationConnection.Create(
            provider, command.TenantId, command.Name!, fields, secrets, protector.Protect, author, now);
        repository.Add(connection);
        ConnectionAudit.ByMember(
            auditRecorder, executionContext, connection, ConnectionAuditActions.Created, ConnectionAudit.KeysOf(connection), now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Application/UpdateConnection.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por If-Match.</param>
public sealed record UpdateConnectionCommand(
    Guid TenantId,
    Guid ConnectionId,
    long ExpectedVersion,
    string? Name,
    IReadOnlyDictionary<string, string?>? Fields,
    IReadOnlyDictionary<string, string?>? Secrets) : ICommand<ConnectionResponse>
{
    public override string ToString() =>
        $"UpdateConnectionCommand {{ TenantId = {TenantId}, ConnectionId = {ConnectionId}, "
        + $"ExpectedVersion = {ExpectedVersion}, Name = {Name}, Fields = [{CommandText.Keys(Fields)}], "
        + $"Secrets = [{CommandText.Keys(Secrets)}] }}";
}

/// <summary>
/// Lo que no depende del proveedor. Campos y secretos se validan en el handler, con el proveedor de la
/// conexión ya cargada, por las mismas <see cref="ConnectionInputRules"/>.
/// </summary>
public sealed class UpdateConnectionValidator : AbstractValidator<UpdateConnectionCommand>
{
    public UpdateConnectionValidator()
    {
        // Inalcanzable desde la pantalla: el endpoint ya exige un If-Match mayor que cero.
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
        RuleFor(command => command.Name)
            .Custom((name, context) => ConnectionInputRules.AddTo(context, ConnectionInputRules.CheckName(name)));
    }
}

/// <summary>
/// Spec 2026-10-08, <c>PUT /connections/{id}</c>: secreto ausente conserva (D5); prueba sólo si cambió
/// un campo público o llegó un secreto (P9), con los secretos guardados descifrados para lo que no
/// llegó; una prueba fallida no guarda nada; sin cambios, no-op.
/// </summary>
public sealed class UpdateConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    ISecretProtector protector,
    IConnectionTester tester,
    ITenantModules tenantModules,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateConnectionCommand> validator)
    : ICommandHandler<UpdateConnectionCommand, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(UpdateConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        SecretProtectionGuard.EnsureAvailable(protector);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var (connection, provider) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, command.TenantId, command.ConnectionId, cancellationToken);
        ConcurrencyGuard.EnsureVersion(connection, command.ExpectedVersion);
        ConnectionInputRules.ThrowIfAny(
            ConnectionInputRules.CheckValues(provider, command.Fields, command.Secrets, requireSecrets: false));

        var fields = ConnectionInputRules.Normalize(provider, command.Fields, secret: false);
        var replacedSecrets = ConnectionInputRules.Normalize(provider, command.Secrets, secret: true);
        var mustTest = replacedSecrets.Count > 0 || !SameValues(connection.Fields, fields);
        if (mustTest)
        {
            var stored = ConnectionSecrets.Read(protector, connection);
            var missing = stored.Unreadable.Where(key => !replacedSecrets.ContainsKey(key)).ToArray();
            if (missing.Length > 0)
            {
                throw ConnectionSecrets.Unreadable(missing);
            }

            var merged = new Dictionary<string, string>(stored.Plain, StringComparer.Ordinal);
            foreach (var (key, value) in replacedSecrets)
            {
                merged[key] = value;
            }

            await ConnectionVerification.EnsurePassesAsync(tester, provider, fields, merged, cancellationToken);
        }

        var now = clock.UtcNow;
        var changed = connection.Update(
            provider, command.Name!, fields, replacedSecrets, protector.Protect, now, verified: mustTest);
        if (changed.Count == 0)
        {
            return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
        }

        ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Updated, changed, now);
        if (mustTest)
        {
            ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Verified, [], now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }

    private static bool SameValues(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> next) =>
        current.Count == next.Count
        && current.All(pair => next.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.Ordinal));
}
```

- [ ] **Step 4: Ver el GREEN de las unitarias**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~CreateConnectionValidatorTests|FullyQualifiedName~CreateConnectionHandlerTests|FullyQualifiedName~UpdateConnectionHandlerTests|FullyQualifiedName~ReadHandlersTests"
```

Esperado: PASS de las cuatro clases.

- [ ] **Step 5: Registrar handlers y validadores (RED y GREEN de `CompositionRootTests`)**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~EveryCommandAndQueryHasItsHandlerRegistered"
```

Esperado: FAIL nombrando `CreateConnectionCommand -> ICommandHandler\`2, UpdateConnectionCommand -> ICommandHandler\`2`.

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, a continuación de los tres de la Task 6:

```csharp
        services.AddScoped<
            ICommandHandler<CreateConnectionCommand, ConnectionResponse>,
            CreateConnectionHandler>();
        services.AddScoped<
            ICommandHandler<UpdateConnectionCommand, ConnectionResponse>,
            UpdateConnectionHandler>();
```

y, después de `services.AddValidatorsFromAssemblyContaining<OpenCashSessionValidator>();` (`:494`):

```csharp
        services.AddValidatorsFromAssemblyContaining<CreateConnectionValidator>();
```

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~IntegrationsLayerTests"
```

Esperado: PASS de las dos clases (la de capas confirma que Application sólo ve Tenancy y Audit).

- [ ] **Step 6: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/BuildingBlocks/BuildingBlocks.Application/ApplicationExceptions.cs src/Modules/Integrations/Modules.Integrations.Application tests/Modules/Integrations/Modules.Integrations.UnitTests src/Bootstrapper/QepServiceCollectionExtensions.cs
$text = @'
feat(integrations): crear y editar conexiones con prueba contra el proveedor

Valida con claves de error exactas, prueba antes de guardar, conserva el secreto
ausente y responde 503 sin llave activa (ServiceUnavailableException nueva).
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 8: Probar, pausar, reanudar y eliminar

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Application/ConnectionEvents.cs` (`ConnectionEvents`, `IConnectionEventPublisher`)
- Create: `src/Modules/Integrations/Modules.Integrations.Application/{TestConnection,PauseConnection,ResumeConnection,DeleteConnection}.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (cuatro handlers)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationsTestBed.Lifecycle.cs`, `tests/Modules/Integrations/Modules.Integrations.UnitTests/LifecycleHandlersTests.cs`

**Interfaces:**
- Consumes: Tasks 3, 6 y 7 (`ConnectionLoader`, `ConcurrencyGuard`, `ConnectionSecrets`, `ConnectionVerification`, `ConnectionAudit`, `IConnectionTester`, `IIntegrationsAuditRecorder`).
- Produces:
  - `public static class ConnectionEvents { Paused = "integrations.connection-paused.v1"; Deleted = "integrations.connection-deleted.v1"; NeedsAttention = "integrations.connection-needs-attention.v1"; }`
  - `public interface IConnectionEventPublisher { void Publish(string eventName, IntegrationConnection connection, DateTimeOffset occurredAt); }`
  - `TestConnectionCommand(Guid TenantId, Guid ConnectionId) : ICommand<ConnectionResponse>`; `PauseConnectionCommand(Guid TenantId, Guid ConnectionId, long ExpectedVersion) : ICommand<ConnectionResponse>`; `ResumeConnectionCommand(Guid TenantId, Guid ConnectionId, long ExpectedVersion) : ICommand<ConnectionResponse>`; `DeleteConnectionCommand(Guid TenantId, Guid ConnectionId, long ExpectedVersion) : ICommand<bool>`, con sus handlers.
  - Dobles: `RecordingEventPublisher`, `PublishedEvent`; `IntegrationsTestBed.Events`, `.TestHandler()`, `.PauseHandler()`, `.ResumeHandler()`, `.DeleteHandler()`.

- [ ] **Step 1: Escribir los dobles y las pruebas que fallan**

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationsTestBed.Lifecycle.cs`:

```csharp
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.UnitTests;

internal sealed partial class IntegrationsTestBed
{
    public RecordingEventPublisher Events { get; } = new();

    public TestConnectionHandler TestHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Events, Protector, Tester, Modules, AuthorNames, context ?? Context(), Clock);

    public PauseConnectionHandler PauseHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Events, Protector, Modules, AuthorNames, context ?? Context(), Clock);

    public ResumeConnectionHandler ResumeHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Events, Protector, Tester, Modules, AuthorNames, context ?? Context(), Clock);

    public DeleteConnectionHandler DeleteHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Events, Modules, context ?? Context(), Clock);
}

internal sealed record PublishedEvent(
    string EventName, Guid TenantId, Guid ConnectionId, string ProviderKey, DateTimeOffset OccurredAt);

internal sealed class RecordingEventPublisher : IConnectionEventPublisher
{
    public List<PublishedEvent> Published { get; } = [];

    public void Publish(string eventName, IntegrationConnection connection, DateTimeOffset occurredAt) =>
        Published.Add(new PublishedEvent(eventName, connection.TenantId, connection.Id, connection.ProviderKey, occurredAt));
}
```

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/LifecycleHandlersTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Estados y transiciones» y los endpoints <c>test</c>, <c>pause</c>, <c>resume</c> y
/// <c>DELETE</c>: If-Match donde el spec lo pide, eventos de outbox y auditoría en el mismo guardado.
/// </summary>
public sealed class LifecycleHandlersTests
{
    private static readonly DateTimeOffset Later = IntegrationsTestBed.Now.AddHours(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // P10: siempre 200 con la conexión; si pasa, anota la verificación y saca de NeedsAttention.
    [Fact]
    public async Task APassingTestRecordsTheVerificationAndBringsNeedsAttentionBack()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, IntegrationsTestBed.Now);
        bed.Clock.UtcNow = Later;

        var response = await bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct);

        Assert.Equal("Active", response.Status);
        Assert.Equal(Later, response.LastVerifiedAt);
        Assert.Equal(IntegrationsTestBed.Token, Assert.Single(bed.Tester.Calls).Secrets[ZenviaFieldKeys.ApiToken]);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal("integrations.connection.verified", audit.Action);
        Assert.Equal("success", audit.Outcome);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    // P10 y DECISIÓN 2 (owner 2026-10-08): un rechazo sobre una Active la pasa a NeedsAttention, con
    // evento y auditoría, y responde 200 con la conexión.
    [Fact]
    public async Task ARejectedTestOnAnActiveConnectionMovesItToNeedsAttention()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Tester.Result = ConnectionTestResult.CredentialsRejected;
        bed.Clock.UtcNow = Later;

        var response = await bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct);

        Assert.Equal("NeedsAttention", response.Status);
        Assert.Equal("credentials_rejected", response.LastFailureCode);
        Assert.Equal(Later, response.LastFailureAt);
        Assert.Equal(
            ["integrations.connection.verified", "integrations.connection.needs_attention"],
            bed.Audit.Entries.Select(entry => entry.Action));
        Assert.Equal("failure", bed.Audit.Entries[0].Outcome);
        Assert.Equal("integrations.connection-needs-attention.v1", Assert.Single(bed.Events.Published).EventName);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    // Unreachable no es un rechazo: anota la falla y deja el estado.
    [Fact]
    public async Task AnUnreachableTestRecordsTheFailureAndKeepsTheStatus()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Tester.Result = ConnectionTestResult.Unreachable("timeout");
        bed.Clock.UtcNow = Later;

        var response = await bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct);

        Assert.Equal("Active", response.Status);
        Assert.Equal("provider_unreachable", response.LastFailureCode);
        Assert.Equal(Later, response.LastFailureAt);
        Assert.Equal(2, response.Version);
        Assert.Equal("failure", Assert.Single(bed.Audit.Entries).Outcome);
        Assert.Empty(bed.Events.Published);
    }

    // Sólo una Active transiciona: una Paused sigue Paused (resume vuelve a probar, D4) y una que ya
    // estaba en NeedsAttention no repite el evento.
    [Theory]
    [InlineData("Paused")]
    [InlineData("NeedsAttention")]
    public async Task ARejectedTestOutsideActiveOnlyRecordsTheFailure(string status)
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        if (status == "Paused")
        {
            connection.Pause(IntegrationsTestBed.Now);
        }
        else
        {
            connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, IntegrationsTestBed.Now);
        }

        bed.Tester.Result = ConnectionTestResult.CredentialsRejected;
        bed.Clock.UtcNow = Later;

        var response = await bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct);

        Assert.Equal(status, response.Status);
        Assert.Equal(Later, response.LastFailureAt);
        Assert.Equal("failure", Assert.Single(bed.Audit.Entries).Outcome);
        Assert.Empty(bed.Events.Published);
    }

    [Fact]
    public async Task ATestWithAnUnreadableSecretIs422AndSavesNothing()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Protector.Unreadable.Add((connection.Id, ZenviaFieldKeys.ApiToken));

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct));

        Assert.Equal("secrets.apiToken", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(bed.Tester.Calls);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task PauseMovesActiveToPausedAuditsAndPublishes()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var response = await bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 1), Ct);

        Assert.Equal("Paused", response.Status);
        Assert.Equal(2, response.Version);
        Assert.Equal("integrations.connection.paused", Assert.Single(bed.Audit.Entries).Action);
        var published = Assert.Single(bed.Events.Published);
        Assert.Equal("integrations.connection-paused.v1", published.EventName);
        Assert.Equal(connection.Id, published.ConnectionId);
        Assert.Equal("zenvia", published.ProviderKey);
    }

    [Fact]
    public async Task PausingTwiceIsNotActiveAndAStaleVersionIs412()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        await bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 1), Ct);

        Assert.Equal("integrations.connection.not_active", (await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 2), Ct))).Code);
        Assert.Equal("concurrency.conflict", (await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 1), Ct))).Code);
    }

    // D4: reanudar vuelve a probar.
    [Fact]
    public async Task ResumeRetestsAndActivates()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        connection.Pause(IntegrationsTestBed.Now);

        var response = await bed.ResumeHandler().HandleAsync(new ResumeConnectionCommand(bed.TenantId, connection.Id, 2), Ct);

        Assert.Equal("Active", response.Status);
        Assert.Single(bed.Tester.Calls);
        Assert.Equal("integrations.connection.resumed", Assert.Single(bed.Audit.Entries).Action);
    }

    // Spec, «Paused → Active»: si la credencial ya no sirve, queda NeedsAttention (guardado) y el resume
    // responde 422 credentials_rejected.
    [Fact]
    public async Task ResumeWithRejectedCredentialsLeavesNeedsAttentionSavedAndAnswers422()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        connection.Pause(IntegrationsTestBed.Now);
        bed.Tester.Result = ConnectionTestResult.CredentialsRejected;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            bed.ResumeHandler().HandleAsync(new ResumeConnectionCommand(bed.TenantId, connection.Id, 2), Ct));

        Assert.Equal("integrations.connection.credentials_rejected", error.Code);
        Assert.Equal(["secrets.apiToken"], error.FieldErrors.Keys);
        Assert.Equal(ConnectionStatus.NeedsAttention, connection.Status);
        Assert.Equal("credentials_rejected", connection.LastFailureCode);
        Assert.Equal(1, bed.UnitOfWork.Saves);
        Assert.Equal("integrations.connection.needs_attention", Assert.Single(bed.Audit.Entries).Action);
        Assert.Equal("integrations.connection-needs-attention.v1", Assert.Single(bed.Events.Published).EventName);
    }

    // P11: «no pude verificar» deja la conexión pausada y no guarda nada.
    [Fact]
    public async Task ResumeWithAnUnreachableProviderStaysPausedAndSavesNothing()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        connection.Pause(IntegrationsTestBed.Now);
        bed.Tester.Result = ConnectionTestResult.Unreachable("timeout");

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            bed.ResumeHandler().HandleAsync(new ResumeConnectionCommand(bed.TenantId, connection.Id, 2), Ct));

        Assert.Equal("integrations.connection.provider_unreachable", error.Code);
        Assert.Equal(ConnectionStatus.Paused, connection.Status);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task ResumingAnActiveConnectionIsNotPausedWithoutCallingTheProvider()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            bed.ResumeHandler().HandleAsync(new ResumeConnectionCommand(bed.TenantId, connection.Id, 1), Ct));

        Assert.Equal("integrations.connection.not_paused", error.Code);
        Assert.Empty(bed.Tester.Calls);
    }

    [Fact]
    public async Task DeleteRemovesAuditsAndPublishes()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        Assert.True(await bed.DeleteHandler().HandleAsync(new DeleteConnectionCommand(bed.TenantId, connection.Id, 1), Ct));

        Assert.Empty(bed.Repository.Connections);
        Assert.Equal("integrations.connection.deleted", Assert.Single(bed.Audit.Entries).Action);
        Assert.Equal("integrations.connection-deleted.v1", Assert.Single(bed.Events.Published).EventName);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task DeleteWithAStaleVersionIs412AndKeepsTheConnection()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            bed.DeleteHandler().HandleAsync(new DeleteConnectionCommand(bed.TenantId, connection.Id, 9), Ct));

        Assert.Single(bed.Repository.Connections);
    }

    [Fact]
    public async Task AHiddenProviderBlocksTheLifecycleWith403()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.HideQuotations();

        Assert.Equal("tenancy.module_not_enabled", (await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 1), Ct))).Code);
        Assert.Equal("tenancy.module_not_enabled", (await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct))).Code);
    }

    [Fact]
    public async Task WithoutManageEveryLifecycleCommandIsForbidden()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Permissions = [IntegrationsPermissions.ConnectionRead];

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 1), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.ResumeHandler().HandleAsync(new ResumeConnectionCommand(bed.TenantId, connection.Id, 1), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.DeleteHandler().HandleAsync(new DeleteConnectionCommand(bed.TenantId, connection.Id, 1), Ct));
        Assert.Empty(bed.Tester.Calls);
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~LifecycleHandlersTests"
```

Esperado: FAIL de compilación: `error CS0246: … 'IConnectionEventPublisher' could not be found` (y `TestConnectionHandler`, `PauseConnectionCommand`, …).

- [ ] **Step 3: Implementar**

`src/Modules/Integrations/Modules.Integrations.Application/ConnectionEvents.cs`:

```csharp
using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

/// <summary>
/// Spec 2026-10-08, «Eventos de outbox»: para que un consumidor suelte una conexión que ya no sirve.
/// Sin consumidores en este spec.
/// </summary>
public static class ConnectionEvents
{
    public const string Paused = "integrations.connection-paused.v1";
    public const string Deleted = "integrations.connection-deleted.v1";
    public const string NeedsAttention = "integrations.connection-needs-attention.v1";
}

/// <summary>
/// Escribe el evento en <c>platform.outbox_messages</c> en la misma transacción que el cambio. Puerto
/// propio y no el <c>IOutboxWriter</c> de Tenancy, que está ligado a <c>TenancyDbContext</c> (P2).
/// Payload: <c>{ tenantId, connectionId, providerKey, occurredAt }</c>; nunca un valor de campo.
/// </summary>
public interface IConnectionEventPublisher
{
    void Publish(string eventName, IntegrationConnection connection, DateTimeOffset occurredAt);
}
```

`src/Modules/Integrations/Modules.Integrations.Application/TestConnection.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record TestConnectionCommand(Guid TenantId, Guid ConnectionId) : ICommand<ConnectionResponse>;

/// <summary>
/// Spec 2026-10-08, <c>POST /connections/{id}/test</c>: la misma prueba con lo guardado. Sin
/// <c>If-Match</c> (P27). Siempre responde la conexión (P10): si pasa, <c>last_verified_at</c> y
/// <c>NeedsAttention → Active</c>. Si el proveedor rechaza la credencial de una conexión
/// <c>Active</c>, pasa a <c>NeedsAttention</c> con su evento y su auditoría, igual que el reporter
/// (DECISIÓN 2, owner 2026-10-08, coherente con D2). Cualquier otra falla —o un rechazo sobre una
/// <c>Paused</c> o una que ya estaba en <c>NeedsAttention</c>— sólo anota <c>last_failure_*</c>.
/// Audita siempre <c>verified</c> con su <c>outcome</c>.
/// </summary>
public sealed class TestConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    IConnectionEventPublisher eventPublisher,
    ISecretProtector protector,
    IConnectionTester tester,
    ITenantModules tenantModules,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<TestConnectionCommand, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(TestConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        var (connection, provider) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, command.TenantId, command.ConnectionId, cancellationToken);

        var stored = ConnectionSecrets.Read(protector, connection);
        if (stored.Unreadable.Count > 0)
        {
            throw ConnectionSecrets.Unreadable(stored.Unreadable);
        }

        var result = await tester.TestAsync(provider, connection.Fields, stored.Plain, cancellationToken);
        var now = clock.UtcNow;
        if (result.Outcome == ConnectionTestOutcome.Ok)
        {
            connection.MarkVerified(now);
            ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Verified, [], now);
        }
        else
        {
            var failureCode = ConnectionVerification.FailureCode(result);
            ConnectionAudit.ByMember(
                auditRecorder, executionContext, connection, ConnectionAuditActions.Verified, [], now, ConnectionAuditActions.Failure);
            if (result.Outcome == ConnectionTestOutcome.CredentialsRejected && connection.Status == ConnectionStatus.Active)
            {
                // Un rechazo no es intermitente (D2): la conexión sale de los consumidores ya.
                connection.MarkNeedsAttention(failureCode, now);
                ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.NeedsAttention, [], now);
                eventPublisher.Publish(ConnectionEvents.NeedsAttention, connection, now);
            }
            else
            {
                connection.RecordFailure(failureCode, now);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Application/PauseConnection.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record PauseConnectionCommand(Guid TenantId, Guid ConnectionId, long ExpectedVersion)
    : ICommand<ConnectionResponse>;

/// <summary>Spec 2026-10-08, <c>POST …/pause</c> con If-Match: <c>Active → Paused</c>; si no está activa,
/// <c>not_active</c>. Publica <c>connection-paused</c> para que un consumidor la suelte.</summary>
public sealed class PauseConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    IConnectionEventPublisher eventPublisher,
    ISecretProtector protector,
    ITenantModules tenantModules,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<PauseConnectionCommand, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(PauseConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        var (connection, provider) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, command.TenantId, command.ConnectionId, cancellationToken);
        ConcurrencyGuard.EnsureVersion(connection, command.ExpectedVersion);

        var now = clock.UtcNow;
        connection.Pause(now);
        ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Paused, [], now);
        eventPublisher.Publish(ConnectionEvents.Paused, connection, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Application/ResumeConnection.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record ResumeConnectionCommand(Guid TenantId, Guid ConnectionId, long ExpectedVersion)
    : ICommand<ConnectionResponse>;

/// <summary>
/// Spec 2026-10-08, <c>POST …/resume</c> con If-Match: <b>vuelve a probar</b> (D4). Si pasa, Active. Si
/// la credencial ya no sirve, queda <c>NeedsAttention</c> —guardado, con su evento— y responde 422
/// <c>credentials_rejected</c>. Si el proveedor no responde, sigue pausada y no se guarda nada (P11).
/// </summary>
public sealed class ResumeConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    IConnectionEventPublisher eventPublisher,
    ISecretProtector protector,
    IConnectionTester tester,
    ITenantModules tenantModules,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ResumeConnectionCommand, ConnectionResponse>
{
    public async Task<ConnectionResponse> HandleAsync(ResumeConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        var (connection, provider) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, command.TenantId, command.ConnectionId, cancellationToken);
        ConcurrencyGuard.EnsureVersion(connection, command.ExpectedVersion);
        connection.EnsurePaused();

        var stored = ConnectionSecrets.Read(protector, connection);
        if (stored.Unreadable.Count > 0)
        {
            throw ConnectionSecrets.Unreadable(stored.Unreadable);
        }

        var result = await tester.TestAsync(provider, connection.Fields, stored.Plain, cancellationToken);
        var now = clock.UtcNow;
        switch (result.Outcome)
        {
            case ConnectionTestOutcome.Ok:
                connection.Resume(now);
                ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Resumed, [], now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);

            case ConnectionTestOutcome.Unreachable:
                throw ConnectionVerification.Failure(provider, result);

            default:
                // El estado nuevo se guarda antes de responder el 422: la pantalla tiene que ver
                // "Necesita atención" al recargar.
                connection.MarkNeedsAttention(ConnectionVerification.FailureCode(result), now);
                ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.NeedsAttention, [], now);
                eventPublisher.Publish(ConnectionEvents.NeedsAttention, connection, now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                throw ConnectionVerification.Failure(provider, result);
        }
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Application/DeleteConnection.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

public sealed record DeleteConnectionCommand(Guid TenantId, Guid ConnectionId, long ExpectedVersion) : ICommand<bool>;

/// <summary>Spec 2026-10-08, <c>DELETE /connections/{id}</c> con If-Match: borra la fila y, en cascada,
/// sus secretos (D9). La confirmación escribiendo el nombre es de la pantalla.</summary>
public sealed class DeleteConnectionHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    IConnectionEventPublisher eventPublisher,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeleteConnectionCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteConnectionCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        var (connection, _) = await ConnectionLoader.LoadVisibleAsync(
            repository, catalog, tenantModules, command.TenantId, command.ConnectionId, cancellationToken);
        ConcurrencyGuard.EnsureVersion(connection, command.ExpectedVersion);

        var now = clock.UtcNow;
        repository.Remove(connection);
        ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Deleted, [], now);
        eventPublisher.Publish(ConnectionEvents.Deleted, connection, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
```

- [ ] **Step 4: Ver el GREEN de las unitarias**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~LifecycleHandlersTests"
```

Esperado: PASS.

- [ ] **Step 5: Registrar los handlers (RED y GREEN de `CompositionRootTests`)**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~EveryCommandAndQueryHasItsHandlerRegistered"
```

Esperado: FAIL nombrando los cuatro comandos nuevos. En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, a continuación de los de la Task 7:

```csharp
        services.AddScoped<
            ICommandHandler<TestConnectionCommand, ConnectionResponse>,
            TestConnectionHandler>();
        services.AddScoped<
            ICommandHandler<PauseConnectionCommand, ConnectionResponse>,
            PauseConnectionHandler>();
        services.AddScoped<
            ICommandHandler<ResumeConnectionCommand, ConnectionResponse>,
            ResumeConnectionHandler>();
        services.AddScoped<
            ICommandHandler<DeleteConnectionCommand, bool>,
            DeleteConnectionHandler>();
```

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"
```

Esperado: PASS.

- [ ] **Step 6: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/Modules/Integrations/Modules.Integrations.Application tests/Modules/Integrations/Modules.Integrations.UnitTests src/Bootstrapper/QepServiceCollectionExtensions.cs
$text = @'
feat(integrations): probar, pausar, reanudar y eliminar conexiones
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 9: Puertos para los módulos consumidores

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Application/IIntegrationConnections.cs` (`ResolvedConnection`, `ConnectionSummary`, `IIntegrationConnections`, `IntegrationConnections`)
- Create: `src/Modules/Integrations/Modules.Integrations.Application/IConnectionHealthReporter.cs` (`IConnectionHealthReporter`, `ConnectionHealthReporter`)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationsTestBed.Ports.cs`, `tests/Modules/Integrations/Modules.Integrations.UnitTests/ConsumerPortsTests.cs`

**Interfaces:**
- Consumes: Tasks 6-8.
- Produces (los registra la Task 11; nadie los usa todavía):
  - `public sealed record ResolvedConnection(Guid Id, string ProviderKey, string Name, IReadOnlyDictionary<string, string> Fields, IReadOnlyDictionary<string, string> Secrets)` (con `ToString` sin secretos); `public sealed record ConnectionSummary(Guid Id, string ProviderKey, string Name)`.
  - `public interface IIntegrationConnections { Task<ResolvedConnection?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken ct); Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken ct); }` y `public sealed class IntegrationConnections(IIntegrationConnectionRepository, IIntegrationProviderCatalog, ITenantModules, ISecretProtector)`.
  - `public interface IConnectionHealthReporter { Task ReportCredentialsRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken ct); }` y `public sealed class ConnectionHealthReporter(IIntegrationConnectionRepository, IIntegrationsUnitOfWork, IIntegrationsAuditRecorder, IConnectionEventPublisher, IClock)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/IntegrationsTestBed.Ports.cs`:

```csharp
using Modules.Integrations.Application;

namespace Modules.Integrations.UnitTests;

internal sealed partial class IntegrationsTestBed
{
    public IntegrationConnections ConnectionsPort() => new(Repository, Catalog, Modules, Protector);

    public ConnectionHealthReporter HealthReporter() => new(Repository, UnitOfWork, Audit, Events, Clock);
}
```

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/ConsumerPortsTests.cs`:

```csharp
using Modules.Audit.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Puertos para los consumidores»: <c>ResolveAsync</c> sólo entrega lo Active y
/// visible del tenant, con los secretos en claro y en memoria; el reporter pasa a NeedsAttention sin
/// umbral (D2).
/// </summary>
public sealed class ConsumerPortsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ResolveReturnsAnActiveVisibleConnectionWithItsSecretsInClear()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var resolved = await bed.ConnectionsPort().ResolveAsync(bed.TenantId, connection.Id, Ct);

        Assert.NotNull(resolved);
        Assert.Equal(connection.Id, resolved.Id);
        Assert.Equal("zenvia", resolved.ProviderKey);
        Assert.Equal(IntegrationsTestBed.FromNumber, resolved.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Equal(IntegrationsTestBed.Token, resolved.Secrets[ZenviaFieldKeys.ApiToken]);
        Assert.DoesNotContain(IntegrationsTestBed.Token, resolved.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveIsNullForPausedNeedsAttentionMissingOtherTenantHiddenOrUnreadable()
    {
        var bed = new IntegrationsTestBed();
        var paused = bed.Seed("Pausada");
        paused.Pause(IntegrationsTestBed.Now);
        var attention = bed.Seed("Atención");
        attention.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, IntegrationsTestBed.Now);
        var foreign = bed.Seed("Ajena", tenantId: Guid.CreateVersion7());
        var unreadable = bed.Seed("Ilegible");
        bed.Protector.Unreadable.Add((unreadable.Id, ZenviaFieldKeys.ApiToken));
        var port = bed.ConnectionsPort();

        Assert.Null(await port.ResolveAsync(bed.TenantId, paused.Id, Ct));
        Assert.Null(await port.ResolveAsync(bed.TenantId, attention.Id, Ct));
        Assert.Null(await port.ResolveAsync(bed.TenantId, Guid.CreateVersion7(), Ct));
        Assert.Null(await port.ResolveAsync(bed.TenantId, foreign.Id, Ct));
        Assert.Null(await port.ResolveAsync(bed.TenantId, unreadable.Id, Ct));

        var hidden = bed.Seed("Oculta");
        bed.HideQuotations();
        Assert.Null(await port.ResolveAsync(bed.TenantId, hidden.Id, Ct));
    }

    [Fact]
    public async Task ListActiveReturnsOnlyTheActiveOnesOfThatProviderByName()
    {
        var bed = new IntegrationsTestBed();
        bed.Seed("sur");
        bed.Seed("Norte");
        bed.Seed("Pausada").Pause(IntegrationsTestBed.Now);

        var active = await bed.ConnectionsPort().ListActiveAsync(bed.TenantId, "zenvia", Ct);

        Assert.Equal(["Norte", "sur"], active.Select(summary => summary.Name));
        Assert.Empty(await bed.ConnectionsPort().ListActiveAsync(bed.TenantId, "otro", Ct));
        bed.HideQuotations();
        Assert.Empty(await bed.ConnectionsPort().ListActiveAsync(bed.TenantId, "zenvia", Ct));
    }

    // D2 y P15: sin umbral; actor = la conexión, tipo Integration.
    [Fact]
    public async Task TheReporterMovesActiveToNeedsAttentionAuditsAsIntegrationAndPublishes()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        await bed.HealthReporter().ReportCredentialsRejectedAsync(bed.TenantId, connection.Id, "credentials_rejected", Ct);

        Assert.Equal(ConnectionStatus.NeedsAttention, connection.Status);
        Assert.Equal("credentials_rejected", connection.LastFailureCode);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal("integrations.connection.needs_attention", audit.Action);
        Assert.Equal(AuditActorType.Integration, audit.ActorType);
        Assert.Equal(connection.Id, audit.ActorId);
        Assert.Equal("integrations.connection-needs-attention.v1", Assert.Single(bed.Events.Published).EventName);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task TheReporterIgnoresWhatIsNotActive()
    {
        var bed = new IntegrationsTestBed();
        var paused = bed.Seed("Pausada");
        paused.Pause(IntegrationsTestBed.Now);
        var attention = bed.Seed("Atención");
        attention.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, IntegrationsTestBed.Now);
        var reporter = bed.HealthReporter();

        await reporter.ReportCredentialsRejectedAsync(bed.TenantId, paused.Id, "credentials_rejected", Ct);
        await reporter.ReportCredentialsRejectedAsync(bed.TenantId, attention.Id, "credentials_rejected", Ct);
        await reporter.ReportCredentialsRejectedAsync(bed.TenantId, Guid.CreateVersion7(), "credentials_rejected", Ct);

        Assert.Equal(ConnectionStatus.Paused, paused.Status);
        Assert.Equal(0, bed.UnitOfWork.Saves);
        Assert.Empty(bed.Audit.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task TheReporterRejectsAFailureCodeThatDoesNotFit(string failureCode)
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            bed.HealthReporter().ReportCredentialsRejectedAsync(bed.TenantId, connection.Id, failureCode, Ct));
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~ConsumerPortsTests"
```

Esperado: FAIL de compilación: `error CS0246: … 'IntegrationConnections' could not be found` y `'ConnectionHealthReporter' could not be found`.

- [ ] **Step 3: Implementar**

`src/Modules/Integrations/Modules.Integrations.Application/IIntegrationConnections.cs`:

```csharp
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>
/// Una conexión lista para usar. Los secretos vienen en claro, en memoria, para ese request: el
/// consumidor no los persiste ni los registra. <see cref="ToString"/> no los imprime.
/// </summary>
public sealed record ResolvedConnection(
    Guid Id,
    string ProviderKey,
    string Name,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, string> Secrets)
{
    public override string ToString() =>
        $"ResolvedConnection {{ Id = {Id}, ProviderKey = {ProviderKey}, Name = {Name} }}";
}

public sealed record ConnectionSummary(Guid Id, string ProviderKey, string Name);

/// <summary>
/// Spec 2026-10-08, «Puertos para los consumidores». Cada consumidor declara su propio puerto en su
/// Application y el adaptador vive en Bootstrapper: un módulo de negocio nunca referencia
/// <c>Modules.Integrations.Application</c> (IntegrationsLayerTests).
/// </summary>
public interface IIntegrationConnections
{
    /// <summary>Sólo Active y visible para el tenant. <c>null</c> si no existe, es de otro tenant, está
    /// Paused o NeedsAttention, o un secreto no descifra (DECISIÓN-PENDIENTE 1): el consumidor decide qué
    /// hacer con "no hay conexión".</summary>
    Task<ResolvedConnection?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Las Active de un proveedor, para que la pantalla del consumidor deje elegir una.</summary>
    Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken);
}

public sealed class IntegrationConnections(
    IIntegrationConnectionRepository repository,
    IIntegrationProviderCatalog catalog,
    ITenantModules tenantModules,
    ISecretProtector protector) : IIntegrationConnections
{
    public async Task<ResolvedConnection?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await repository.FindAsync(tenantId, connectionId, cancellationToken);
        if (connection is not { Status: ConnectionStatus.Active }
            || catalog.Find(connection.ProviderKey) is not { } provider
            || !provider.IsVisibleFor(await tenantModules.FindAsync(tenantId, cancellationToken)))
        {
            return null;
        }

        var stored = ConnectionSecrets.Read(protector, connection);
        if (stored.Unreadable.Count > 0)
        {
            return null;
        }

        return new ResolvedConnection(
            connection.Id,
            connection.ProviderKey,
            connection.Name,
            new Dictionary<string, string>(connection.Fields, StringComparer.Ordinal),
            stored.Plain);
    }

    public async Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(
        Guid tenantId, string providerKey, CancellationToken cancellationToken)
    {
        if (catalog.Find(providerKey) is not { } provider
            || !provider.IsVisibleFor(await tenantModules.FindAsync(tenantId, cancellationToken)))
        {
            return [];
        }

        return (await repository.ListAsync(tenantId, cancellationToken))
            .Where(connection => connection.Status == ConnectionStatus.Active
                && string.Equals(connection.ProviderKey, provider.Key, StringComparison.Ordinal))
            .OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(connection => connection.Id)
            .Select(connection => new ConnectionSummary(connection.Id, connection.ProviderKey, connection.Name))
            .ToArray();
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Application/IConnectionHealthReporter.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

/// <summary>
/// Spec 2026-10-08: un consumidor la llama tras un 401/403 <b>definitivo</b> del proveedor. Sin umbral
/// (D2): un rechazo de credenciales no es intermitente.
/// </summary>
public interface IConnectionHealthReporter
{
    Task ReportCredentialsRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken);
}

/// <summary>
/// <c>Active → NeedsAttention</c>, audita y publica en la misma transacción. Lo que no está Active se
/// ignora: reportar dos veces no duplica nada (P15). No hay una persona detrás: el actor es la propia
/// conexión, con tipo <see cref="AuditActorType.Integration"/>.
/// </summary>
public sealed class ConnectionHealthReporter(
    IIntegrationConnectionRepository repository,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    IConnectionEventPublisher eventPublisher,
    IClock clock) : IConnectionHealthReporter
{
    public async Task ReportCredentialsRejectedAsync(
        Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (failureCode.Length > IntegrationConnection.FailureCodeMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureCode), $"A failure code is at most {IntegrationConnection.FailureCodeMaxLength} characters.");
        }

        var connection = await repository.FindAsync(tenantId, connectionId, cancellationToken);
        if (connection is not { Status: ConnectionStatus.Active })
        {
            return;
        }

        var now = clock.UtcNow;
        connection.MarkNeedsAttention(failureCode, now);
        auditRecorder.Record(
            tenantId,
            connection.Id,
            AuditActorType.Integration,
            ConnectionAuditActions.NeedsAttention,
            connection.Id,
            ConnectionAuditActions.Success,
            [],
            now);
        eventPublisher.Publish(ConnectionEvents.NeedsAttention, connection, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
```

- [ ] **Step 4: Ver el GREEN**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet build Backend.slnx --no-restore -v q; if ($LASTEXITCODE -ne 0) { throw "build falló (si el paso espera un RED de compilación, ése es el RED)" }
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --no-build --filter "FullyQualifiedName~ConsumerPortsTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~IntegrationsLayerTests"
```

Esperado: PASS en las dos.

- [ ] **Step 5: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/Modules/Integrations/Modules.Integrations.Application tests/Modules/Integrations/Modules.Integrations.UnitTests
$text = @'
feat(integrations): puertos para los módulos consumidores

IIntegrationConnections (sólo Active y visible, secretos en memoria) e
IConnectionHealthReporter (NeedsAttention sin umbral). Sin consumidores todavía.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 10: Probador de credenciales de Zenvia

**Files:**
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Zenvia/{ZenviaOptions,ZenviaOptionsValidator,ZenviaConnectionTester}.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Verification/{IProviderConnectionTester,ConnectionTesterRegistry}.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs`
- Modify: `src/Api/appsettings.example.json` (bloque `Integrations` de la Task 4)
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/{ZenviaConnectionTesterTests,ZenviaOptionsValidatorTests,UnitTestLogs}.cs`

**Interfaces:**
- Consumes: `IConnectionTester`, `ConnectionTestResult`, `ConnectionTestOutcome` (Task 7); `IntegrationProviders.Zenvia`, `ZenviaFieldKeys` (Task 2). Referencia de diseño: `6612298:src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/ZenviaHttpClient.cs` (un cliente HTTP compartido, el token por request en `X-API-TOKEN`).
- Produces:
  - `public sealed class ZenviaOptions { const string SectionName = "Integrations:Zenvia"; string BaseUrl = "https://api.zenvia.com"; }`; `internal sealed class ZenviaOptionsValidator : IValidateOptions<ZenviaOptions>`.
  - `internal interface IProviderConnectionTester { string ProviderKey { get; } Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, string> fields, IReadOnlyDictionary<string, string> secrets, CancellationToken); }`
  - `internal sealed partial class ZenviaConnectionTester` con `public const string HttpClientName = "integrations.zenvia"` y `internal static void ConfigureClient(HttpClient)`.
  - `internal sealed class ConnectionTesterRegistry(IEnumerable<IProviderConnectionTester>) : IConnectionTester` (singleton).

- [ ] **Step 1: Escribir las pruebas que fallan**

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/UnitTestLogs.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Modules.Integrations.UnitTests;

/// <summary>Todo lo que se registra, ya formateado y con la excepción entera: lo que guardaría el log
/// JSON de producción. Copia de <c>CapturedLogs</c> de 6612298.</summary>
internal sealed class UnitTestLogs : ILoggerProvider
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

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/ZenviaConnectionTesterTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.Verification;
using Modules.Integrations.Infrastructure.Zenvia;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Probar la credencial»: <c>GET {BaseUrl}/v2/templates</c> con <c>X-API-TOKEN</c>
/// (respuesta 1 del owner), 2xx Ok, 401/403 rechazado, todo lo demás «no pude verificar». Se arma por
/// el registro real del módulo —timeout, User-Agent y sin loggers de headers—; sólo el handler primario
/// es de mentira.
/// </summary>
public sealed class ZenviaConnectionTesterTests : IDisposable
{
    private const string Token = "zenvia-token-SENTINEL-4c1e";
    private const string Body = "zenvia-body-SENTINEL-9d2f";

    private readonly StubZenviaHandler _zenvia = new();
    private readonly UnitTestLogs _logs = new();
    private readonly ServiceProvider _services;

    public ZenviaConnectionTesterTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unit;Username=x;Password=x",
                ["Integrations:Zenvia:BaseUrl"] = "https://zenvia.test",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_logs));
        services.AddIntegrationsInfrastructure(configuration);
        services.AddHttpClient(ZenviaConnectionTester.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _zenvia);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private Task<ConnectionTestResult> TestAsync(CancellationToken? cancellationToken = null) =>
        _services.GetRequiredService<IConnectionTester>().TestAsync(
            IntegrationProviders.Zenvia,
            new Dictionary<string, string> { [ZenviaFieldKeys.FromNumber] = "573001234567" },
            new Dictionary<string, string> { [ZenviaFieldKeys.ApiToken] = Token },
            cancellationToken ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task ItAsksForTheTemplatesWithTheTokenHeaderAndItsUserAgent()
    {
        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        var request = Assert.Single(_zenvia.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://zenvia.test/v2/templates", request.Uri?.ToString());
        Assert.Equal(Token, request.Token);
        Assert.Contains("qep-integrations", request.UserAgent, StringComparison.Ordinal);
    }

    // Review Focus 4: lo que el spec no nombra no es Ok ni rechazo.
    [Theory]
    [InlineData(200, ConnectionTestOutcome.Ok)]
    [InlineData(204, ConnectionTestOutcome.Ok)]
    [InlineData(401, ConnectionTestOutcome.CredentialsRejected)]
    [InlineData(403, ConnectionTestOutcome.CredentialsRejected)]
    [InlineData(302, ConnectionTestOutcome.Unreachable)]
    [InlineData(400, ConnectionTestOutcome.Unreachable)]
    [InlineData(404, ConnectionTestOutcome.Unreachable)]
    [InlineData(429, ConnectionTestOutcome.Unreachable)]
    [InlineData(500, ConnectionTestOutcome.Unreachable)]
    [InlineData(503, ConnectionTestOutcome.Unreachable)]
    public async Task EveryStatusHasItsOutcome(int status, ConnectionTestOutcome expected)
    {
        _zenvia.Status = (HttpStatusCode)status;

        Assert.Equal(expected, (await TestAsync()).Outcome);
    }

    [Fact]
    public async Task ATimeoutIsUnreachable()
    {
        _zenvia.Throw = new TaskCanceledException("timed out", new TimeoutException());

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("timeout", result.Reason);
    }

    [Fact]
    public async Task ANetworkFailureIsUnreachable()
    {
        _zenvia.Throw = new HttpRequestException("connection refused");

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("network", result.Reason);
    }

    [Fact]
    public async Task ACancellationByTheCallerPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestAsync(cancellation.Token));
    }

    [Fact]
    public void TheClientWaitsTenSeconds() =>
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            _services.GetRequiredService<IHttpClientFactory>().CreateClient(ZenviaConnectionTester.HttpClientName).Timeout);

    // Spec, «Nunca en un log»: código y status, nunca headers ni cuerpo.
    [Fact]
    public async Task NothingLoggedCarriesTheTokenOrTheBody()
    {
        _zenvia.Status = HttpStatusCode.Unauthorized;
        _zenvia.Body = $"{{\"message\":\"{Body}\",\"echo\":\"{Token}\"}}";

        await TestAsync();

        Assert.Contains(_logs.Entries, entry => entry.Contains("401", StringComparison.Ordinal));
        Assert.DoesNotContain(Token, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Body, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCatalogProviderHasATester()
    {
        var keys = _services.GetServices<IProviderConnectionTester>().Select(tester => tester.ProviderKey).ToHashSet();

        Assert.All(IntegrationProviders.All, provider => Assert.Contains(provider.Key, keys));
    }

    [Fact]
    public async Task AProviderWithoutATesterIsAProgrammingError()
    {
        var unknown = new IntegrationProvider(
            "sin-probador", "X", IntegrationCategory.Messaging, [TenantModuleKeys.Quotations],
            [new FieldDefinition("apiKey", "Clave", FieldKind.Secret, required: true, maxLength: 10, pattern: null, invalidMessage: "X")],
            maxConnections: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _services.GetRequiredService<IConnectionTester>().TestAsync(
            unknown, new Dictionary<string, string>(), new Dictionary<string, string>(), TestContext.Current.CancellationToken));
    }
}

internal sealed record StubZenviaRequest(HttpMethod Method, Uri? Uri, string? Token, string UserAgent);

/// <summary>Zenvia de mentira: anota cada request y responde lo que la prueba pida.</summary>
internal sealed class StubZenviaHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = "[]";

    public Exception? Throw { get; set; }

    public ConcurrentQueue<StubZenviaRequest> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(new StubZenviaRequest(
            request.Method,
            request.RequestUri,
            request.Headers.TryGetValues("X-API-TOKEN", out var tokens) ? tokens.FirstOrDefault() : null,
            request.Headers.UserAgent.ToString()));
        if (Throw is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
    }
}
```

Crea `tests/Modules/Integrations/Modules.Integrations.UnitTests/ZenviaOptionsValidatorTests.cs`:

```csharp
using Modules.Integrations.Infrastructure.Zenvia;

namespace Modules.Integrations.UnitTests;

/// <summary>P17: el token viaja en un header, así que la URL base de la prueba es https absoluta.</summary>
public sealed class ZenviaOptionsValidatorTests
{
    [Theory]
    [InlineData("https://api.zenvia.com", true)]
    [InlineData("https://zenvia.test/", true)]
    [InlineData("http://api.zenvia.com", false)]
    [InlineData("api.zenvia.com", false)]
    [InlineData("", false)]
    public void TheBaseUrlIsAbsoluteHttps(string baseUrl, bool valid)
    {
        var result = new ZenviaOptionsValidator().Validate(null, new ZenviaOptions { BaseUrl = baseUrl });

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.Contains("Integrations:Zenvia:BaseUrl", result.FailureMessage, StringComparison.Ordinal);
        }
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~ZenviaConnectionTesterTests|FullyQualifiedName~ZenviaOptionsValidatorTests"
```

Esperado: FAIL de compilación: `error CS0234: The type or namespace name 'Zenvia' does not exist in the namespace 'Modules.Integrations.Infrastructure'` (y `Verification`).

- [ ] **Step 3: Implementar**

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Zenvia/ZenviaOptions.cs`:

```csharp
namespace Modules.Integrations.Infrastructure.Zenvia;

/// <summary>
/// La sección <c>Integrations:Zenvia</c> (spec 2026-10-08, «Probar la credencial»). Sólo la usa la
/// prueba de la credencial: el sender global de Quotations sigue leyendo <c>Quotations:WhatsApp:BaseUrl</c>
/// hasta el spec del consumidor.
/// </summary>
public sealed class ZenviaOptions
{
    public const string SectionName = "Integrations:Zenvia";

    public string BaseUrl { get; init; } = "https://api.zenvia.com";
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Zenvia/ZenviaOptionsValidator.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace Modules.Integrations.Infrastructure.Zenvia;

/// <summary>P17: el API token viaja en un header; por http saldría en claro. Mismo criterio que
/// <c>QuotationsOptionsValidator</c> con el PDF.</summary>
internal sealed class ZenviaOptionsValidator : IValidateOptions<ZenviaOptions>
{
    public ValidateOptionsResult Validate(string? name, ZenviaOptions options) =>
        Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{ZenviaOptions.SectionName}:BaseUrl must be an absolute https URL: the API token travels in a header.");
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Verification/IProviderConnectionTester.cs`:

```csharp
using Modules.Integrations.Application;

namespace Modules.Integrations.Infrastructure.Verification;

/// <summary>El probador de un proveedor. Un proveedor nuevo trae el suyo (criterio 5 del spec).</summary>
internal interface IProviderConnectionTester
{
    string ProviderKey { get; }

    Task<ConnectionTestResult> TestAsync(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken);
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Verification/ConnectionTesterRegistry.cs`:

```csharp
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Verification;

/// <summary>Elige el probador por <c>provider.Key</c> (spec, «Probar la credencial»). Un proveedor del
/// catálogo sin probador es un error de programación, no una credencial mala.</summary>
internal sealed class ConnectionTesterRegistry(IEnumerable<IProviderConnectionTester> testers) : IConnectionTester
{
    private readonly Dictionary<string, IProviderConnectionTester> _byKey =
        testers.ToDictionary(tester => tester.ProviderKey, StringComparer.Ordinal);

    public Task<ConnectionTestResult> TestAsync(
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken) =>
        _byKey.TryGetValue(provider.Key, out var tester)
            ? tester.TestAsync(fields, secrets, cancellationToken)
            : throw new InvalidOperationException($"No connection tester is registered for provider '{provider.Key}'.");
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Zenvia/ZenviaConnectionTester.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Verification;

namespace Modules.Integrations.Infrastructure.Zenvia;

/// <summary>
/// Spec 2026-10-08, «Probar la credencial»: <c>GET {BaseUrl}/v2/templates</c> con <c>X-API-TOKEN</c>
/// (respuesta 1 del owner). 2xx → Ok; 401/403 → rechazada; timeout, red y cualquier otro status →
/// «no pude verificar» (P13). El número emisor no se valida contra Zenvia. Registra sólo el status y
/// el resultado: nunca headers ni cuerpo (no lo lee), y el <c>HttpClient</c> no tiene loggers de
/// headers (<c>RemoveAllLoggers</c>).
/// </summary>
internal sealed partial class ZenviaConnectionTester(
    IHttpClientFactory httpClientFactory,
    IOptions<ZenviaOptions> options,
    ILogger<ZenviaConnectionTester> logger) : IProviderConnectionTester
{
    public const string HttpClientName = "integrations.zenvia";

    private const string TemplatesPath = "v2/templates";

    public string ProviderKey => IntegrationProviders.Zenvia.Key;

    [LoggerMessage(Level = LogLevel.Information, Message = "Zenvia credential test answered HTTP {StatusCode}: {Outcome}")]
    private static partial void LogAnswered(ILogger logger, int statusCode, ConnectionTestOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Zenvia credential test could not reach the provider: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);

    /// <summary>Spec: 10 s por prueba y <c>User-Agent: qep-integrations</c>.</summary>
    internal static void ConfigureClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-integrations");
    }

    public async Task<ConnectionTestResult> TestAsync(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        var baseUri = new Uri(options.Value.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, TemplatesPath));
        request.Headers.TryAddWithoutValidation("X-API-TOKEN", secrets[ZenviaFieldKeys.ApiToken]);

        try
        {
            using var response = await httpClientFactory
                .CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var result = Classify(response.StatusCode);
            LogAnswered(logger, (int)response.StatusCode, result.Outcome);
            return result;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // El timeout del HttpClient, no una cancelación de quien llama.
            LogUnreachable(logger, "timeout");
            return ConnectionTestResult.Unreachable("timeout");
        }
        catch (HttpRequestException)
        {
            LogUnreachable(logger, "network");
            return ConnectionTestResult.Unreachable("network");
        }
    }

    internal static ConnectionTestResult Classify(HttpStatusCode status)
    {
        var code = (int)status;
        if (code is >= 200 and < 300)
        {
            return ConnectionTestResult.Ok;
        }

        return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? ConnectionTestResult.CredentialsRejected
            : ConnectionTestResult.Unreachable($"http_{code}");
    }
}
```

En `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs`, agrega los `using`:

```csharp
using Modules.Integrations.Infrastructure.Verification;
using Modules.Integrations.Infrastructure.Zenvia;
```

y, antes del `return services;`:

```csharp
        // Spec 2026-10-08, «Probar la credencial». IHttpClientFactory con un cliente propio del
        // módulo; sin redirecciones automáticas (el token no viaja a otro host: un 3xx es «no pude
        // verificar», P13) y sin los loggers por defecto, que pueden registrar headers.
        services.AddOptions<ZenviaOptions>()
            .Bind(configuration.GetSection(ZenviaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ZenviaOptions>, ZenviaOptionsValidator>();
        services.AddHttpClient(ZenviaConnectionTester.HttpClientName, ZenviaConnectionTester.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<IProviderConnectionTester, ZenviaConnectionTester>();
        services.AddSingleton<IConnectionTester, ConnectionTesterRegistry>();
```

- [ ] **Step 4: Ver el GREEN y el RED del ejemplo de configuración**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet build Backend.slnx --no-restore -v q; if ($LASTEXITCODE -ne 0) { throw "build falló (si el paso espera un RED de compilación, ése es el RED)" }
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --no-build --filter "FullyQualifiedName~ZenviaConnectionTesterTests|FullyQualifiedName~ZenviaOptionsValidatorTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~ConfigurationExampleTests"
```

Esperado: PASS en la primera; FAIL en la segunda nombrando `Integrations:Zenvia:BaseUrl`.

- [ ] **Step 5: Documentar la clave y ver el GREEN**

En `src/Api/appsettings.example.json`, dentro del bloque `Integrations` de la Task 4, reemplaza:

```json
    "SecretProtection": {
      "ActiveKeyId": "",
      "Keys": {}
    }
  }
}
```

por:

```json
    "SecretProtection": {
      "ActiveKeyId": "",
      "Keys": {}
    },
    "Zenvia": {
      "BaseUrl": "https://api.zenvia.com"
    }
  }
}
```

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests|FullyQualifiedName~CompositionRootTests"
```

Esperado: PASS (la segunda confirma que el registro nuevo no rompe `AddQepPlatform`).

- [ ] **Step 6: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/Modules/Integrations/Modules.Integrations.Infrastructure tests/Modules/Integrations/Modules.Integrations.UnitTests src/Api/appsettings.example.json
$text = @'
feat(integrations): probador de credenciales de Zenvia

GET /v2/templates con X-API-TOKEN por IHttpClientFactory: 10 s, User-Agent
qep-integrations, sin redirecciones ni loggers de headers.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 11: Persistencia, migración `InitialIntegrations` y harness de integración

**Files:**
- Move: `6612298:tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs` → `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/IntegrationsApiHarness.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/{IntegrationsDbContext,IntegrationsDbContextFactory,IntegrationsOutboxMessage,IntegrationConnectionRepository,IntegrationsUnitOfWork,IntegrationsAuditRecorder,IntegrationsEventPublisher}.cs`
- Create: `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsDatabaseInitializer.cs`
- Create (generada y editada): `src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/Migrations/<timestamp>_InitialIntegrations.cs` (+ `.Designer.cs`, `IntegrationsDbContextModelSnapshot.cs`)
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs`, `src/Api/Program.cs:187-188`
- Test: `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/IntegrationsPersistenceTests.cs`

**Interfaces:**
- Consumes: Tasks 3-10.
- Produces:
  - `public sealed class IntegrationsDbContext` con `const string Schema = "integrations"`, `const string ConnectionNameIndex = "IX_connections_tenant_provider_name"`, `DbSet<IntegrationConnection> Connections`, y las proyecciones internas `AuditEntries` y `Outbox`.
  - `public static Task InitializeIntegrationsDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)`.
  - Registros: `IIntegrationConnectionRepository`, `IIntegrationsUnitOfWork`, `IIntegrationsAuditRecorder`, `IConnectionEventPublisher` (scoped), `IIntegrationProviderCatalog` (singleton), `IIntegrationConnections`, `IConnectionHealthReporter` (scoped).
  - Harness (lo usan las Tasks 12-14): `IntegrationsApiHarness` con `SentinelApiToken`, `SentinelZenviaBody`, `FromNumber`, `ZenviaBaseUrl`, `TestSecretProtectionKey`, `ReadPermissions`, `ManagePermissions`, `CatalogUrl/ConnectionsUrl/ConnectionUrl`, `StartDatabaseAsync`, `QepApiFactory` (con `ZenviaHandler`), `RegisteredTenant(Guid TenantId, Guid OwnerUserId, string Email)`, `RegisterTenantAsync`, `CreateClient`, `ZenviaBody`, `SendAsync`, `CreateConnectionAsync`, `ProblemAsync`, `OwnerMembershipIdAsync`, `SetModuleStatusAsync`, `ScalarAsync<T>`, `ExecuteAsync`, `CountConnectionsAsync`, `SeedConnectionAsync`, `WithCapturedLogs`, `WithSecretProtection`, `RequestFailuresTextAsync`, `AuditAndOutboxTextAsync`; y las clases `FakeZenviaHandler`, `ZenviaRequest`, `CapturedLogs`.

- [ ] **Step 1: Traer el harness de WhatsApp por tenant, sin editarlo, y commitear el traslado**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git checkout 6612298 -- tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs
git mv tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs tests/Modules/Integrations/Modules.Integrations.IntegrationTests/IntegrationsApiHarness.cs
git -C $Main status --short
git status --short
$text = @'
test(integrations): mover el harness de WhatsApp por tenant

Movido sin cambios desde
6612298:tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTestHarness.cs.
La adaptación va en el commit siguiente.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

- [ ] **Step 2: Reescribir el harness y escribir las pruebas de persistencia**

Reemplaza el contenido de `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/IntegrationsApiHarness.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Zenvia;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas de Integrations (spec 2026-10-08). Viene de <c>WhatsAppTestHarness</c>
/// (6612298): centinela, Zenvia de mentira, logs capturados y llaves fijadas. El host fija todo lo que
/// activa una integración externa o un secreto; nunca lo hereda de los user-secrets de quien corre las
/// pruebas (memoria «user-secrets rompen las pruebas de integración»).
/// </summary>
internal static class IntegrationsApiHarness
{
    /// <summary>Un token inventado y fácil de buscar: la prueba de fugas lo persigue por respuestas,
    /// logs, auditoría, outbox y <c>platform.request_failures</c>. ASCII visible, sin espacios.</summary>
    public const string SentinelApiToken = "zenvia-token-SENTINEL-7f3a9c";

    /// <summary>Un cuerpo de Zenvia inventado: no puede aparecer en ningún camino de salida.</summary>
    public const string SentinelZenviaBody = "zenvia-body-SENTINEL-5b2d1e";

    public const string FromNumber = "573001234567";

    public const string ZenviaBaseUrl = "https://zenvia.test";

    /// <summary>La llave con la que el host cifra. Calculada, no escrita: un literal con forma de llave
    /// en el repo termina copiado a un ambiente. Bytes 0..31.</summary>
    public static string TestSecretProtectionKey { get; } =
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());

    public static readonly string[] ReadPermissions = [IntegrationsPermissions.ConnectionRead];

    public static readonly string[] ManagePermissions =
        [IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage];

    public static string CatalogUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/integrations/catalog";

    public static string ConnectionsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/integrations/connections";

    public static string ConnectionUrl(Guid tenantId, Guid connectionId) => $"{ConnectionsUrl(tenantId)}/{connectionId}";

    private const string TemplateDatabase = "qep_template";

    // Un solo contenedor por ensamblado, con una plantilla ya migrada. Antes cada prueba arrancaba su
    // contenedor y su host migraba los 13 módulos —por eso Quotations tarda 15 minutos—; ahora cada
    // prueba clona la plantilla (CREATE DATABASE … TEMPLATE, decenas de milisegundos) y el arranque
    // de su host sólo comprueba que no hay migraciones pendientes. El aislamiento es el mismo: una
    // base por prueba, que se borra al terminar.
    private static readonly Lazy<Task<PostgreSqlContainer>> SharedServer = new(StartServerAsync);

    // Postgres rechaza dos CREATE DATABASE simultáneos desde la misma plantilla ("source database
    // is being accessed by other users"): los clones van de a uno.
    private static readonly SemaphoreSlim CloneGate = new(1, 1);

    /// <summary>Una base limpia y migrada para esta prueba. Se borra en <c>DisposeAsync</c>.</summary>
    public static async Task<TestDatabase> StartDatabaseAsync()
    {
        var server = await SharedServer.Value;
        var name = $"t_{Guid.CreateVersion7():N}";
        await CloneGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await ExecuteAdminAsync(server, $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\"");
        }
        finally
        {
            CloneGate.Release();
        }

        return new TestDatabase(server, name);
    }

    private static async Task<PostgreSqlContainer> StartServerAsync()
    {
        // Sin el token de una prueba: el servidor es de todas, y cancelar la primera no puede
        // dejar a las demás sin base.
        var server = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await server.StartAsync(CancellationToken.None);
        await ExecuteAdminAsync(server, $"CREATE DATABASE \"{TemplateDatabase}\"");

        // El host migra al arrancar (los *DatabaseInitializer de cada módulo): arrancarlo una vez
        // contra la plantilla la deja con todas las migraciones, igual que cada prueba la tenía antes.
        var templateConnectionString = ConnectionStringFor(server, TemplateDatabase);
        using (var factory = new QepApiFactory(templateConnectionString))
        using (factory.CreateClient())
        {
        }

        // Una conexión viva a la plantilla haría fallar todos los clones.
        NpgsqlConnection.ClearAllPools();
        await ExecuteAdminAsync(server, $"ALTER DATABASE \"{TemplateDatabase}\" WITH ALLOW_CONNECTIONS false");
        return server;
    }

    private static string ConnectionStringFor(PostgreSqlContainer server, string database) =>
        new NpgsqlConnectionStringBuilder(server.GetConnectionString()) { Database = database }.ConnectionString;

    private static async Task ExecuteAdminAsync(PostgreSqlContainer server, string sql)
    {
        // Contra la base "qep" del contenedor, nunca contra la plantilla ni contra la de una prueba.
        await using var connection = new NpgsqlConnection(server.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>La base de una prueba. Expone lo mismo que las pruebas le pedían al contenedor.</summary>
    public sealed class TestDatabase(PostgreSqlContainer server, string name) : IAsyncDisposable
    {
        public string GetConnectionString() => ConnectionStringFor(server, name);

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(GetConnectionString()));
            // FORCE: un worker del host que todavía no soltó su conexión no puede dejar la base viva.
            await ExecuteAdminAsync(server, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    public sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        /// <summary>Lo que sale hacia Zenvia lo ve este handler; las pruebas cambian su respuesta.</summary>
        public FakeZenviaHandler ZenviaHandler { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijados, nunca heredados (mismo criterio que PosApiHarness y QuotationsApiHarness).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:ExportLoad:Quotations", "0");
            builder.UseSetting("Quotations:WhatsApp:ApiToken", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:FromNumber", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:TemplateId", string.Empty);
            // El signup concede los seis módulos de fábrica, quotations incluido: Zenvia es visible.
            builder.UseSetting("Entitlements:GrantDefaultModulesOnSignup", "true");
            // Spec 2026-10-08, «Pruebas»: "test" es la activa; "k1" —el id que el README sugiere para
            // local— se vacía para que una llave mal pegada en la máquina de quien corre no las tumbe.
            builder.UseSetting("Integrations:SecretProtection:ActiveKeyId", "test");
            builder.UseSetting("Integrations:SecretProtection:Keys:test", TestSecretProtectionKey);
            builder.UseSetting("Integrations:SecretProtection:Keys:k1", string.Empty);
            builder.UseSetting("Integrations:Zenvia:BaseUrl", ZenviaBaseUrl);
            builder.ConfigureTestServices(services => services
                .AddHttpClient(ZenviaConnectionTester.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => ZenviaHandler));
        }
    }

    internal sealed record RegisteredTenant(Guid TenantId, Guid OwnerUserId, string Email);

    public static HttpClient CreateClient(
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

    /// <summary>Copia de <c>PosApiHarness.RegisterTenantAsync</c>: un tenant real, con el dueño y su
    /// membresía activa (de ahí sale <c>createdBy</c>).</summary>
    public static async Task<RegisteredTenant> RegisterTenantAsync(WebApplicationFactory<Program> factory)
    {
        var email = $"owner-{Guid.CreateVersion7():N}@example.com";
        using var bootstrap = CreateClient(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        bootstrap.DefaultRequestHeaders.Add("X-Email", email);
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Integrations Test Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisterTenantResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return new RegisteredTenant(registered.TenantId, registered.OwnerUserId, email);
    }

    public static Task<Guid> OwnerMembershipIdAsync(string connectionString, RegisteredTenant tenant) =>
        ScalarAsync<Guid>(
            connectionString,
            "SELECT id FROM tenancy.memberships WHERE tenant_id = @tenantId AND user_id = @userId",
            ("tenantId", tenant.TenantId),
            ("userId", tenant.OwnerUserId));

    /// <summary>Objeto anónimo a propósito: así una prueba manda exactamente lo que quiere.</summary>
    public static object ZenviaBody(
        string name = "WhatsApp sede norte", string? apiToken = SentinelApiToken, string? fromNumber = FromNumber) =>
        new
        {
            providerKey = "zenvia",
            name,
            fields = new Dictionary<string, string?> { ["fromNumber"] = fromNumber },
            secrets = new Dictionary<string, string?> { ["apiToken"] = apiToken },
        };

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<ConnectionResponse> CreateConnectionAsync(
        HttpClient client, Guid tenantId, string name = "WhatsApp sede norte", string apiToken = SentinelApiToken)
    {
        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenantId), ZenviaBody(name, apiToken));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(connection);
        return connection;
    }

    /// <summary>El <c>code</c> y las claves de <c>errors</c> de un ProblemDetails.</summary>
    public static async Task<(string? Code, string[] ErrorKeys)> ProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
        var keys = root.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        return (code, keys);
    }

    /// <summary>Spec 2026-10-08 (consola): apagar un módulo deja la fila inactiva.</summary>
    public static Task<int> SetModuleStatusAsync(string connectionString, Guid tenantId, string moduleKey, string status) =>
        ExecuteAsync(
            connectionString,
            "UPDATE tenancy.tenant_modules SET status = @status, status_changed_at = now() WHERE tenant_id = @tenantId AND module_key = @moduleKey",
            ("status", status),
            ("tenantId", tenantId),
            ("moduleKey", moduleKey));

    public static async Task<T> ScalarAsync<T>(
        string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static async Task<int> ExecuteAsync(
        string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public static Task<long> CountConnectionsAsync(string connectionString, Guid tenantId) =>
        ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM integrations.connections WHERE tenant_id = @tenantId",
            ("tenantId", tenantId));

    /// <summary>Una conexión de Zenvia escrita directo por el repositorio del host (sin HTTP ni prueba
    /// contra el proveedor), cifrada con la llave activa de ese host.</summary>
    public static async Task<Guid> SeedConnectionAsync(
        WebApplicationFactory<Program> host, Guid tenantId, string name, string apiToken = SentinelApiToken)
    {
        using var scope = host.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var connection = IntegrationConnection.Create(
            IntegrationProviders.Zenvia,
            tenantId,
            name,
            new Dictionary<string, string> { [ZenviaFieldKeys.FromNumber] = FromNumber },
            new Dictionary<string, string> { [ZenviaFieldKeys.ApiToken] = apiToken },
            protector.Protect,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().Add(connection);
        await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return connection.Id;
    }

    /// <summary>Un proveedor de logs más: LoggerFactory recibe todos los ILoggerProvider registrados,
    /// así que esto ve lo mismo que la consola.</summary>
    public static WebApplicationFactory<Program> WithCapturedLogs(
        this WebApplicationFactory<Program> factory, CapturedLogs logs) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<ILoggerProvider>(logs)));

    /// <summary>Pisa la llave activa y declara las que se pasen. Se aplica después del ConfigureWebHost
    /// del harness, así que gana.</summary>
    public static WebApplicationFactory<Program> WithSecretProtection(
        this WebApplicationFactory<Program> factory, string activeKeyId, params (string Id, string Value)[] keys) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Integrations:SecretProtection:ActiveKeyId", activeKeyId);
            foreach (var (id, value) in keys)
            {
                builder.UseSetting($"Integrations:SecretProtection:Keys:{id}", value);
            }
        });

    /// <summary>Mensaje y detalle de todas las fallas guardadas: lo que lee la pantalla de Log.</summary>
    public static Task<string> RequestFailuresTextAsync(string connectionString) =>
        ScalarAsync<string>(
            connectionString,
            "SELECT coalesce(string_agg(message || ' ' || detail, ' '), '') FROM platform.request_failures");

    /// <summary>Todo lo que Integrations dejó en auditoría y outbox, como texto.</summary>
    public static Task<string> AuditAndOutboxTextAsync(string connectionString) =>
        ScalarAsync<string>(
            connectionString,
            """
            SELECT coalesce((SELECT string_agg(action || ' ' || resource_id || ' ' || changed_fields::text, ' ')
                             FROM audit.entries WHERE source = 'integrations'), '')
                || ' '
                || coalesce((SELECT string_agg(payload::text, ' ')
                             FROM platform.outbox_messages WHERE event_name LIKE 'integrations.%'), '')
            """);

    private sealed record RegisterTenantResponseDto(Guid TenantId, Guid OwnerUserId);
}

internal sealed record ZenviaRequest(HttpMethod Method, Uri? Uri, string? Token, string UserAgent);

/// <summary>Zenvia de mentira: anota cada request y responde lo que la prueba pida. Lo comparte el host
/// entero; <see cref="HttpMessageHandler"/> no guarda estado al desecharse, así que sobrevive a que
/// IHttpClientFactory recicle su cadena.</summary>
internal sealed class FakeZenviaHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = "[]";

    public Exception? Throw { get; set; }

    public ConcurrentQueue<ZenviaRequest> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(new ZenviaRequest(
            request.Method,
            request.RequestUri,
            request.Headers.TryGetValues("X-API-TOKEN", out var tokens) ? tokens.FirstOrDefault() : null,
            request.Headers.UserAgent.ToString()));
        if (Throw is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
    }
}

/// <summary>Todo lo que se registra, ya formateado y con la excepción entera (mensaje, internas y
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

Crea `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/IntegrationsPersistenceTests.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Audit.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Npgsql;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Lo que sólo la base hace cumplir (spec 2026-10-08, «Conexión»): los CHECK, el índice único del
/// nombre traducido por su nombre, la concurrencia sobre la versión, la cascada de los secretos, y
/// auditoría y outbox en la misma transacción.
/// </summary>
public sealed class IntegrationsPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARoundTripKeepsNameFieldsSecretStatusAndVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var id = await SeedConnectionAsync(factory, tenantId, "WhatsApp Norte");

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>();
        var connection = await repository.FindAsync(tenantId, id, Ct);

        Assert.NotNull(connection);
        Assert.Equal("WhatsApp Norte", connection.Name);
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(1, connection.Version);
        Assert.Equal(FromNumber, connection.Fields[ZenviaFieldKeys.FromNumber]);
        var secret = Assert.Single(connection.Secrets);
        Assert.Equal("test", secret.KeyId);
        Assert.True(scope.ServiceProvider.GetRequiredService<ISecretProtector>()
            .TryUnprotect(id, secret.FieldKey, secret.Protected, out var plaintext));
        Assert.Equal(SentinelApiToken, plaintext);
        Assert.Null(await repository.FindAsync(Guid.CreateVersion7(), id, Ct));
    }

    // D9 y Review Focus 2: el token nunca está en fields, y lo cifrado no lo contiene.
    [Fact]
    public async Task TheSecretIsNeverInFieldsNorReadableInTheCiphertext()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var id = await SeedConnectionAsync(factory, Guid.CreateVersion7(), "Norte");

        var fields = await ScalarAsync<string>(connectionString, "SELECT fields::text FROM integrations.connections WHERE id = @id", ("id", id));
        var clean = await ScalarAsync<bool>(
            connectionString,
            "SELECT position(convert_to(@token, 'UTF8') in ciphertext) = 0 FROM integrations.connection_secrets WHERE connection_id = @id",
            ("token", SentinelApiToken),
            ("id", id));

        Assert.Contains(FromNumber, fields, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelApiToken, fields, StringComparison.Ordinal);
        Assert.True(clean);
    }

    [Fact]
    public async Task TheProviderKeyCheckRejectsAKeyOutsideTheCatalog()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        _ = factory.Services;

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.GetConnectionString(),
            """
            INSERT INTO integrations.connections (id, tenant_id, provider_key, name, status, fields, created_at, created_by, updated_at, version)
            VALUES (@id, @tenantId, 'otro', 'x', 'Active', '{}'::jsonb, now(), @memberId, now(), 1)
            """,
            ("id", Guid.CreateVersion7()),
            ("tenantId", Guid.CreateVersion7()),
            ("memberId", Guid.CreateVersion7())));

        Assert.Equal("CK_connections_provider_key", error.ConstraintName);
    }

    [Fact]
    public async Task TheStatusCheckRejectsAnUnknownStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var id = await SeedConnectionAsync(factory, Guid.CreateVersion7(), "Norte");

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE integrations.connections SET status = 'Broken' WHERE id = @id",
            ("id", id)));

        Assert.Equal("CK_connections_status", error.ConstraintName);
    }

    // Review Focus 5: único por (tenant, proveedor, lower(name)); el dominio recorta los espacios.
    [Fact]
    public async Task ANameRepeatedWithOtherCaseOrSpacesIsNameTakenOnlyInsideTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        await SeedConnectionAsync(factory, tenantId, "WhatsApp Norte");

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            SeedConnectionAsync(factory, tenantId, "  whatsapp norte  "));

        Assert.Equal("integrations.connection.name_taken", error.Code);
        Assert.NotEqual(Guid.Empty, await SeedConnectionAsync(factory, Guid.CreateVersion7(), "WhatsApp Norte"));
    }

    [Fact]
    public async Task TwoWritersOnTheSameVersionCollide()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var id = await SeedConnectionAsync(factory, tenantId, "Norte");
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var mine = await first.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().FindAsync(tenantId, id, Ct);
        var theirs = await second.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().FindAsync(tenantId, id, Ct);

        mine!.Pause(DateTimeOffset.UtcNow);
        await first.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
        theirs!.Pause(DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            second.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal("concurrency.conflict", error.Code);
    }

    [Fact]
    public async Task DeletingAConnectionCascadesItsSecrets()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var id = await SeedConnectionAsync(factory, tenantId, "Norte");

        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>();
            repository.Remove((await repository.FindAsync(tenantId, id, Ct))!);
            await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM integrations.connection_secrets WHERE connection_id = @id", ("id", id)));
    }

    [Fact]
    public async Task AuditAndEventsCommitWithTheConnectionInOneSave()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        Guid id;

        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var now = DateTimeOffset.UtcNow;
            var connection = IntegrationConnection.Create(
                IntegrationProviders.Zenvia,
                tenantId,
                "Norte",
                new Dictionary<string, string> { [ZenviaFieldKeys.FromNumber] = FromNumber },
                new Dictionary<string, string> { [ZenviaFieldKeys.ApiToken] = SentinelApiToken },
                services.GetRequiredService<ISecretProtector>().Protect,
                Guid.CreateVersion7(),
                now);
            id = connection.Id;
            services.GetRequiredService<IIntegrationConnectionRepository>().Add(connection);
            services.GetRequiredService<IIntegrationsAuditRecorder>().Record(
                tenantId, Guid.CreateVersion7(), AuditActorType.Human, ConnectionAuditActions.Created, connection.Id,
                ConnectionAuditActions.Success, ["apiToken", "fromNumber", "name"], now);
            services.GetRequiredService<IConnectionEventPublisher>().Publish(ConnectionEvents.Paused, connection, now);
            await services.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_type = 'integration_connection' AND source = 'integrations' AND resource_id = @id",
            ("id", id.ToString())));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'integrations.connection-paused.v1' AND payload->>'connectionId' = @id AND payload->>'providerKey' = 'zenvia'",
            ("id", id.ToString())));
    }
}
```

- [ ] **Step 3: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
  Where-Object { $_.CommandLine -and $_.CommandLine.Contains($B) -and $_.CommandLine -match 'Api(\.dll|\.exe)' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --filter "FullyQualifiedName~IntegrationsPersistenceTests"
```

Esperado: compila; FAIL en todas: las que siembran por el repositorio con `System.InvalidOperationException: No service for type 'Modules.Integrations.Application.IIntegrationConnectionRepository' has been registered.`, y las de SQL crudo con `42P01: relation "integrations.connections" does not exist`. Copia el mensaje literal que salga.

- [ ] **Step 4: Implementar la persistencia**

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsOutboxMessage.cs`:

```csharp
namespace Modules.Integrations.Infrastructure.Persistence;

// Proyección de escritura del Outbox de plataforma, propiedad de Tenancy. Mapeada como
// ExcludeFromMigrations: Integrations inserta acá y no crea la tabla (copia de PosOutboxMessage).
internal sealed class IntegrationsOutboxMessage
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public int Attempts { get; init; }

    public string? LastError { get; init; }
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsDbContext.cs`:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>integrations</c> (spec 2026-10-08, «Conexión»): <c>connections</c> y su tabla hija
/// <c>connection_secrets</c> (D9). Proyecta además <c>audit.entries</c> (auditoría atómica, ADR 0019,
/// como Identity) y <c>platform.outbox_messages</c> (como Pos), las dos <c>ExcludeFromMigrations</c>.
/// </summary>
public sealed class IntegrationsDbContext(DbContextOptions<IntegrationsDbContext> options) : DbContext(options)
{
    public const string Schema = "integrations";

    /// <summary>El único por (tenant_id, provider_key, lower(name)). Va en la migración con SQL —EF no
    /// modela índices por expresión— y IntegrationsUnitOfWork lo traduce por este nombre.</summary>
    public const string ConnectionNameIndex = "IX_connections_tenant_provider_name";

    private static readonly JsonSerializerOptions FieldsJson = new(JsonSerializerDefaults.General);

    // fields es jsonb con sólo los campos públicos; el dominio lo expone como diccionario de sólo lectura.
    private static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> FieldsConverter = new(
        fields => JsonSerializer.Serialize(fields, FieldsJson),
        json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, FieldsJson) ?? new Dictionary<string, string>());

    // Igualdad por contenido y hash independiente del orden: dos diccionarios iguales enumerados en
    // otro orden no cuentan como cambio.
    private static readonly ValueComparer<IReadOnlyDictionary<string, string>> FieldsComparer = new(
        (left, right) => left!.Count == right!.Count
            && left.All(pair => right.ContainsKey(pair.Key) && right[pair.Key] == pair.Value),
        fields => fields.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
        fields => new Dictionary<string, string>(fields));

    public DbSet<IntegrationConnection> Connections => Set<IntegrationConnection>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal DbSet<IntegrationsOutboxMessage> Outbox => Set<IntegrationsOutboxMessage>();

    /// <summary>D10: el CHECK sale del catálogo, así que un proveedor nuevo cambia el modelo y pide su
    /// migración (EF avisa de cambios pendientes).</summary>
    internal static string ProviderKeyCheck() =>
        "provider_key IN (" + string.Join(",", IntegrationProviders.All.Select(provider => $"'{provider.Key}'")) + ")";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureConnection(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureConnection(ModelBuilder modelBuilder)
    {
        var connection = modelBuilder.Entity<IntegrationConnection>();
        connection.ToTable("connections", Schema, table =>
        {
            table.HasCheckConstraint("CK_connections_provider_key", ProviderKeyCheck());
            table.HasCheckConstraint("CK_connections_status", "status IN ('Active','Paused','NeedsAttention')");
        });
        connection.HasKey(value => value.Id);
        connection.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        connection.Property(value => value.TenantId).HasColumnName("tenant_id");
        connection.Property(value => value.ProviderKey).HasColumnName("provider_key").HasMaxLength(IntegrationProvider.KeyMaxLength);
        connection.Property(value => value.Name).HasColumnName("name").HasMaxLength(IntegrationConnection.NameMaxLength);
        connection.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        connection.Property(value => value.Fields)
            .HasColumnName("fields")
            .HasColumnType("jsonb")
            .HasConversion(FieldsConverter, FieldsComparer);
        connection.Property(value => value.LastVerifiedAt).HasColumnName("last_verified_at");
        connection.Property(value => value.LastFailureAt).HasColumnName("last_failure_at");
        connection.Property(value => value.LastFailureCode)
            .HasColumnName("last_failure_code")
            .HasMaxLength(IntegrationConnection.FailureCodeMaxLength);
        connection.Property(value => value.CreatedAt).HasColumnName("created_at");
        // tenancy.memberships(id), sin FK: otro módulo.
        connection.Property(value => value.CreatedBy).HasColumnName("created_by");
        connection.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        connection.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        connection.HasIndex(value => value.TenantId).HasDatabaseName("IX_connections_tenant");

        connection.OwnsMany(value => value.Secrets, secret =>
        {
            secret.ToTable("connection_secrets", Schema);
            secret.WithOwner().HasForeignKey("ConnectionId");
            secret.Property<Guid>("ConnectionId").HasColumnName("connection_id");
            secret.HasKey("ConnectionId", nameof(ConnectionSecret.FieldKey));
            secret.Property(value => value.FieldKey).HasColumnName("field_key").HasMaxLength(FieldDefinition.KeyMaxLength);
            secret.Property(value => value.KeyId).HasColumnName("key_id").HasMaxLength(ProtectedSecret.KeyIdMaxLength);
            secret.Property<byte[]>("_ciphertext").HasColumnName("ciphertext").IsRequired();
            secret.Property(value => value.UpdatedAt).HasColumnName("updated_at");
            secret.Ignore(value => value.Protected);
            // El worker de rotación recorre por key_id (D9).
            secret.HasIndex(value => value.KeyId).HasDatabaseName("IX_connection_secrets_key_id");
        });
        connection.Navigation(value => value.Secrets).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<IntegrationsOutboxMessage>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name").HasMaxLength(200);
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        outbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        outbox.Property(value => value.Attempts).HasColumnName("attempts");
        outbox.Property(value => value.LastError).HasColumnName("last_error");
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>Para <c>dotnet ef</c>: <c>Api.csproj</c> no referencia EF Design (CLAUDE.md, gotchas).</summary>
public sealed class IntegrationsDbContextFactory : IDesignTimeDbContextFactory<IntegrationsDbContext>
{
    public IntegrationsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__QepDatabase")
            ?? "Host=localhost;Port=5432;Database=qep;Username=qep;Password=qep_dev";
        var options = new DbContextOptionsBuilder<IntegrationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IntegrationsDbContext.Schema))
            .Options;
        return new IntegrationsDbContext(options);
    }
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationConnectionRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

internal sealed class IntegrationConnectionRepository(IntegrationsDbContext dbContext) : IIntegrationConnectionRepository
{
    public Task<IntegrationConnection?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.Connections.SingleOrDefaultAsync(
            connection => connection.TenantId == tenantId && connection.Id == connectionId, cancellationToken);

    public async Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Connections
            .AsNoTracking()
            .Where(connection => connection.TenantId == tenantId)
            .ToListAsync(cancellationToken);

    public Task<int> CountAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
        dbContext.Connections.CountAsync(
            connection => connection.TenantId == tenantId && connection.ProviderKey == providerKey, cancellationToken);

    public void Add(IntegrationConnection connection) => dbContext.Connections.Add(connection);

    public void Remove(IntegrationConnection connection) => dbContext.Connections.Remove(connection);
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsUnitOfWork.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Npgsql;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>
/// Traduce los errores de base por nombre de índice, no sólo por SqlState (CLAUDE.md): el único del
/// nombre es <c>name_taken</c>; la versión vieja es <c>concurrency.conflict</c>.
/// </summary>
internal sealed class IntegrationsUnitOfWork(IntegrationsDbContext dbContext) : IIntegrationsUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Primero: DbUpdateConcurrencyException hereda de DbUpdateException.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The connection changed while the operation was being committed.",
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, IntegrationsDbContext.ConnectionNameIndex))
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.NameTaken,
                "Another connection of this provider already uses that name.",
                exception);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsAuditRecorder.cs`:

```csharp
using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Integrations.Application;

namespace Modules.Integrations.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019) para Integrations: la entrada se acumula en
// IntegrationsDbContext y commitea o revierte con el cambio. audit.entries es del módulo Audit;
// acá se proyecta ExcludeFromMigrations, igual que Identity y Tenancy (P1).
internal sealed class IntegrationsAuditRecorder(IntegrationsDbContext dbContext) : IIntegrationsAuditRecorder
{
    private const string Source = "integrations";

    public void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        Guid connectionId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            actorType,
            action,
            ConnectionAuditActions.ResourceType,
            connectionId.ToString(),
            outcome,
            JsonSerializer.Serialize(changedFields),
            Source,
            occurredAt));
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/IntegrationsEventPublisher.cs`:

```csharp
using System.Text.Json;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>Spec 2026-10-08, «Eventos de outbox»: misma transacción que el cambio (P2).</summary>
internal sealed class IntegrationsEventPublisher(IntegrationsDbContext dbContext) : IConnectionEventPublisher
{
    public void Publish(string eventName, IntegrationConnection connection, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new IntegrationsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = JsonSerializer.Serialize(new ConnectionEventPayload(
                connection.TenantId, connection.Id, connection.ProviderKey, occurredAt)),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });

    // Nombres en minúscula como el resto de los payloads del outbox; nunca un valor de campo.
    private sealed record ConnectionEventPayload(
        Guid tenantId, Guid connectionId, string providerKey, DateTimeOffset occurredAt);
}
```

`src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsDatabaseInitializer.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Infrastructure.Persistence;

namespace Modules.Integrations.Infrastructure;

public static class IntegrationsDatabaseInitializer
{
    public static async Task InitializeIntegrationsDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
```

Reemplaza `src/Modules/Integrations/Modules.Integrations.Infrastructure/IntegrationsInfrastructureExtensions.cs` entero (incluye lo de las Tasks 4 y 10):

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Infrastructure.Persistence;
using Modules.Integrations.Infrastructure.SecretProtection;
using Modules.Integrations.Infrastructure.Verification;
using Modules.Integrations.Infrastructure.Zenvia;

namespace Modules.Integrations.Infrastructure;

public static class IntegrationsInfrastructureExtensions
{
    public static IServiceCollection AddIntegrationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Mismo guard que los demás módulos: sin la cadena el host no arranca, en vez de fallar en
        // el primer request con un error de Npgsql que no explica nada.
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<IntegrationsDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IntegrationsDbContext.Schema)));

        services.AddScoped<IIntegrationConnectionRepository, IntegrationConnectionRepository>();
        services.AddScoped<IIntegrationsUnitOfWork, IntegrationsUnitOfWork>();
        services.AddScoped<IIntegrationsAuditRecorder, IntegrationsAuditRecorder>();
        services.AddScoped<IConnectionEventPublisher, IntegrationsEventPublisher>();
        services.AddSingleton<IIntegrationProviderCatalog, IntegrationProviderCatalog>();
        // Spec, «Puertos para los consumidores»: definidos y probados; nadie los usa todavía.
        services.AddScoped<IIntegrationConnections, IntegrationConnections>();
        services.AddScoped<IConnectionHealthReporter, ConnectionHealthReporter>();

        // Spec 2026-10-08, «Secreto en reposo»: en Production ValidateOnStart exige la llave activa
        // (sin ella el pod entra en crash-loop, a propósito); fuera de producción el host arranca y
        // crear o editar responde 503.
        services.AddOptions<SecretProtectionOptions>()
            .Bind(configuration.GetSection(SecretProtectionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SecretProtectionOptions>, SecretProtectionOptionsValidator>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        // Spec 2026-10-08, «Probar la credencial». IHttpClientFactory con un cliente propio del
        // módulo; sin redirecciones automáticas (el token no viaja a otro host: un 3xx es «no pude
        // verificar», P13) y sin los loggers por defecto, que pueden registrar headers.
        services.AddOptions<ZenviaOptions>()
            .Bind(configuration.GetSection(ZenviaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ZenviaOptions>, ZenviaOptionsValidator>();
        services.AddHttpClient(ZenviaConnectionTester.HttpClientName, ZenviaConnectionTester.ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<IProviderConnectionTester, ZenviaConnectionTester>();
        services.AddSingleton<IConnectionTester, ConnectionTesterRegistry>();

        return services;
    }
}
```

En `src/Api/Program.cs`, agrega `using Modules.Integrations.Infrastructure;` junto a `using Modules.Integrations.Api;` y, después de `InitializePosDatabaseAsync` (`:187-188`):

```csharp
// Integrations (spec 2026-10-08): sin FKs a otros esquemas. Va después de Audit porque escribe en
// audit.entries, que crea la migración de Audit.
await app.Services.InitializeIntegrationsDatabaseAsync(
    app.Lifetime.ApplicationStopping);
```

- [ ] **Step 5: Generar la migración y agregarle el índice por expresión**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet ef migrations add InitialIntegrations --project src/Modules/Integrations/Modules.Integrations.Infrastructure --context IntegrationsDbContext -o Persistence/Migrations
Select-String -Path src/Modules/Integrations/Modules.Integrations.Infrastructure/Persistence/Migrations/*_InitialIntegrations.cs -Pattern 'name: "connections"|name: "connection_secrets"|"entries"|"outbox_messages"|CK_connections_provider_key|ON DELETE|onDelete'
```

Esperado: crea las dos tablas de `integrations` con `CK_connections_provider_key` (`provider_key IN ('zenvia')`), `CK_connections_status`, `IX_connections_tenant`, `IX_connection_secrets_key_id` y la FK `onDelete: ReferentialAction.Cascade`. **Ninguna** línea con `"entries"` ni `"outbox_messages"`: si aparecen, la proyección perdió el `ExcludeFromMigrations`; **para**.

En el `Up` del `<timestamp>_InitialIntegrations.cs` generado, al final, después del último `CreateIndex`:

```csharp
            // Spec 2026-10-08: el nombre es único por tenant y proveedor sin importar mayúsculas. EF no
            // modela índices por expresión, así que va a mano; IntegrationsUnitOfWork lo traduce a
            // integrations.connection.name_taken por este nombre.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_connections_tenant_provider_name\" ON integrations.connections (tenant_id, provider_key, lower(name));");
```

(El `Down` generado borra las tablas, y el índice con ellas.)

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet ef migrations has-pending-model-changes --project src/Modules/Integrations/Modules.Integrations.Infrastructure --context IntegrationsDbContext
```

Esperado: `No changes have been made to the model since the last migration.`

- [ ] **Step 6: Ver el GREEN**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet build Backend.slnx --no-restore -v q; if ($LASTEXITCODE -ne 0) { throw "build falló (si el paso espera un RED de compilación, ése es el RED)" }
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --no-build --filter "FullyQualifiedName~IntegrationsPersistenceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~IntegrationsLayerTests|FullyQualifiedName~CompositionRootTests"
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --no-build --filter "FullyQualifiedName~ZenviaConnectionTesterTests"
```

Esperado: PASS de las tres (la última confirma que el registro reescrito sigue armando el probador).

- [ ] **Step 7: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/Modules/Integrations/Modules.Integrations.Infrastructure tests/Modules/Integrations/Modules.Integrations.IntegrationTests src/Api/Program.cs
$text = @'
feat(integrations): persistencia y migración InitialIntegrations

Esquema integrations con connections y connection_secrets, CHECKs, índice único
por lower(name) traducido por nombre, auditoría atómica y outbox en la misma
transacción. Harness de integración adaptado.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 12: Re-cifrado periódico de los secretos

**Files:**
- Move: `6612298:src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppTokenRekeyWorker.cs` → `src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/ConnectionSecretRekeyWorker.cs`
- Move: `6612298:tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTokenRekeyWorkerTests.cs` → `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionSecretRekeyWorkerTests.cs`
- Modify: `src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/{SecretProtectionOptions,SecretProtectionOptionsValidator}.cs`, `IntegrationsInfrastructureExtensions.cs`, `src/Api/appsettings.example.json`
- Test: `tests/Modules/Integrations/Modules.Integrations.UnitTests/SecretProtectionOptionsValidatorTests.cs` (una teoría nueva)

**Interfaces:**
- Consumes: `IntegrationsDbContext`, `IIntegrationsUnitOfWork`, `ISecretProtector`, `IntegrationConnection.Reprotect` (Tasks 3, 4, 11); harness (Task 11).
- Produces:
  - `SecretProtectionOptions.RekeyIntervalMinutes` (`int`, default 60, entre 1 y 1440).
  - `internal sealed partial class ConnectionSecretRekeyWorker : BackgroundService` con `Task FirstRunCompletion`, `internal TimeSpan Interval`, `internal const int BatchSize = 100`, `internal Task<RekeyRunResult> RunOnceAsync(CancellationToken)`; `internal sealed record RekeyRunResult(int Reencrypted, int Skipped)`.

- [ ] **Step 1: Traer el worker y su prueba, sin editarlos, y commitear el traslado**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git checkout 6612298 -- src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppTokenRekeyWorker.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTokenRekeyWorkerTests.cs
git mv src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppTokenRekeyWorker.cs src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/ConnectionSecretRekeyWorker.cs
git mv tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTokenRekeyWorkerTests.cs tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionSecretRekeyWorkerTests.cs
git -C $Main status --short
git status --short
$text = @'
refactor(integrations): mover el worker de re-cifrado desde Quotations

Movidos sin cambios desde
6612298:src/Modules/Quotations/Modules.Quotations.Infrastructure/Whatsapp/WhatsAppTokenRekeyWorker.cs
y 6612298:tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppTokenRekeyWorkerTests.cs.
La adaptación va en el commit siguiente.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

- [ ] **Step 2: Adaptar la prueba movida y sumar la del intervalo**

Reemplaza el contenido de `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionSecretRekeyWorkerTests.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Integrations.Application;
using Modules.Integrations.Infrastructure.SecretProtection;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-10-08, «Secreto en reposo» (viene de 6612298): al arrancar y cada intervalo el worker
/// re-cifra con la activa todo secreto en otra llave configurada; lo de una llave retirada o que no
/// descifra lo deja y lo cuenta en una advertencia por corrida, sin valores; es idempotente. Las
/// afirmaciones negativas van después de esperar <see cref="ConnectionSecretRekeyWorker.FirstRunCompletion"/>:
/// un sondeo no distingue "no cambió" de "todavía no cambió".
/// </summary>
public sealed class ConnectionSecretRekeyWorkerTests
{
    private static readonly string OldKey =
        Convert.ToBase64String(Enumerable.Range(100, 32).Select(index => (byte)index).ToArray());

    private static readonly string GoneKey =
        Convert.ToBase64String(Enumerable.Range(150, 32).Select(index => (byte)index).ToArray());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConnectionSecretRekeyWorker WorkerOf(WebApplicationFactory<Program> host) =>
        host.Services.GetServices<IHostedService>().OfType<ConnectionSecretRekeyWorker>().Single();

    private static Task AwaitFirstRunAsync(WebApplicationFactory<Program> host) =>
        WorkerOf(host).FirstRunCompletion.WaitAsync(TimeSpan.FromSeconds(30), Ct);

    private static Task<string> KeyIdAsync(string connectionString, Guid connectionId) =>
        ScalarAsync<string>(
            connectionString,
            "SELECT key_id FROM integrations.connection_secrets WHERE connection_id = @id",
            ("id", connectionId));

    private static Task<long> VersionAsync(string connectionString, Guid connectionId) =>
        ScalarAsync<long>(connectionString, "SELECT version FROM integrations.connections WHERE id = @id", ("id", connectionId));

    [Fact]
    public async Task ANewActiveKeyReencryptsOldSecretsSkipsDamagedAndRetiredOnesAndIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();

        // A y B en la llave vieja; C en la vieja y dañado; E en una llave que el host nuevo ya no
        // declara (retirada); D ya en la activa.
        Guid a, b, c, e;
        using (var oldHost = factory.WithSecretProtection("old", ("old", OldKey)))
        {
            await AwaitFirstRunAsync(oldHost);
            a = await SeedConnectionAsync(oldHost, tenantId, "A");
            b = await SeedConnectionAsync(oldHost, tenantId, "B");
            c = await SeedConnectionAsync(oldHost, tenantId, "C");
        }

        using (var goneHost = factory.WithSecretProtection("gone", ("gone", GoneKey)))
        {
            await AwaitFirstRunAsync(goneHost);
            e = await SeedConnectionAsync(goneHost, tenantId, "E");
        }

        var d = await SeedConnectionAsync(factory, tenantId, "D");
        await ExecuteAsync(
            connectionString,
            "UPDATE integrations.connection_secrets SET ciphertext = set_byte(ciphertext, 20, get_byte(ciphertext, 20) # 255) WHERE connection_id = @id",
            ("id", c));
        var damaged = await ScalarAsync<byte[]>(
            connectionString, "SELECT ciphertext FROM integrations.connection_secrets WHERE connection_id = @id", ("id", c));
        var versionD = await VersionAsync(connectionString, d);

        // Segundo arranque: "test" activa, "old" todavía declarada (fase b de la rotación).
        var logs = new CapturedLogs();
        using (var newHost = factory
                   .WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey))
                   .WithCapturedLogs(logs))
        {
            await AwaitFirstRunAsync(newHost);

            Assert.Equal("test", await KeyIdAsync(connectionString, a));
            Assert.Equal("test", await KeyIdAsync(connectionString, b));
            Assert.Equal("old", await KeyIdAsync(connectionString, c));
            Assert.Equal("gone", await KeyIdAsync(connectionString, e));
            Assert.Equal(versionD, await VersionAsync(connectionString, d));

            using var scope = newHost.Services.CreateScope();
            var connection = await scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().FindAsync(tenantId, a, Ct);
            var secret = Assert.Single(connection!.Secrets);
            Assert.True(scope.ServiceProvider.GetRequiredService<ISecretProtector>()
                .TryUnprotect(a, secret.FieldKey, secret.Protected, out var plaintext));
            Assert.Equal(SentinelApiToken, plaintext);
        }

        Assert.Contains(logs.Entries, entry => entry.Contains("rekey finished: 2 re-encrypted, 2 skipped", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry => entry.Contains("2 connection secrets could not be re-encrypted", StringComparison.Ordinal));
        Assert.DoesNotContain(SentinelApiToken, logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(damaged), logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(damaged), logs.AllText, StringComparison.OrdinalIgnoreCase);

        // Tercer arranque: idempotente. A y B no cambian de versión; C y E se vuelven a saltar.
        var versionA = await VersionAsync(connectionString, a);
        var versionB = await VersionAsync(connectionString, b);
        var thirdLogs = new CapturedLogs();
        using (var thirdHost = factory
                   .WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey))
                   .WithCapturedLogs(thirdLogs))
        {
            await AwaitFirstRunAsync(thirdHost);
        }

        Assert.Equal(versionA, await VersionAsync(connectionString, a));
        Assert.Equal(versionB, await VersionAsync(connectionString, b));
        Assert.Contains(thirdLogs.Entries, entry => entry.Contains("rekey finished: 0 re-encrypted, 2 skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAnActiveKeyTheWorkerDoesNothingAndFinishes()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var logs = new CapturedLogs();
        using var host = factory.WithSecretProtection(string.Empty).WithCapturedLogs(logs);

        await AwaitFirstRunAsync(host);

        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("rekey finished", StringComparison.Ordinal));
    }

    // Spec: "al arrancar y cada RekeyIntervalMinutes". El cuerpo del ciclo es RunOnceAsync: correrlo otra
    // vez con el host vivo agarra lo que llegó después del arranque.
    [Fact]
    public async Task EachRunPicksUpWhatArrivedAfterTheStartAndTheIntervalComesFromConfiguration()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        using var current = factory.WithSecretProtection("test", ("old", OldKey), ("test", TestSecretProtectionKey));
        await AwaitFirstRunAsync(current);
        Assert.Equal(TimeSpan.FromMinutes(60), WorkerOf(current).Interval);

        Guid late;
        using (var oldHost = factory.WithSecretProtection("old", ("old", OldKey)))
        {
            late = await SeedConnectionAsync(oldHost, tenantId, "Tarde");
        }

        var result = await WorkerOf(current).RunOnceAsync(Ct);

        Assert.Equal(1, result.Reencrypted);
        Assert.Equal("test", await KeyIdAsync(connectionString, late));

        using var tuned = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Integrations:SecretProtection:RekeyIntervalMinutes", "15"));
        Assert.Equal(TimeSpan.FromMinutes(15), WorkerOf(tuned).Interval);
    }
}
```

En `tests/Modules/Integrations/Modules.Integrations.UnitTests/SecretProtectionOptionsValidatorTests.cs`, antes de la clase anidada `StubHostEnvironment`:

```csharp
    // P16: el PeriodicTimer del worker no acepta cero, y más de un día deja una llave filtrada en
    // circulación demasiado tiempo.
    [Theory]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(1441, false)]
    [InlineData(1, true)]
    [InlineData(60, true)]
    [InlineData(1440, true)]
    public void TheRekeyIntervalIsBetweenOneMinuteAndOneDay(int minutes, bool valid)
    {
        foreach (var environment in new[] { Environments.Production, Environments.Development })
        {
            var options = new SecretProtectionOptions
            {
                ActiveKeyId = "k1",
                Keys = new(StringComparer.Ordinal) { ["k1"] = GoodKey },
                RekeyIntervalMinutes = minutes,
            };

            var result = ValidatorFor(environment).Validate(null, options);

            Assert.Equal(valid, result.Succeeded);
            if (!valid)
            {
                Assert.Contains("Integrations:SecretProtection:RekeyIntervalMinutes", result.FailureMessage, StringComparison.Ordinal);
            }
        }
    }
```

- [ ] **Step 3: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --filter "FullyQualifiedName~SecretProtectionOptionsValidatorTests"
```

Esperado: FAIL de compilación: el worker movido sigue en `namespace Modules.Quotations.Infrastructure.Whatsapp` y nombra `IWhatsAppSecretProtector`, `QuotationsDbContext`, `ITenantWhatsAppSettingsRepository` (`error CS0246`); la prueba nombra `RekeyIntervalMinutes` (`error CS0117`).

- [ ] **Step 4: Implementar**

En `SecretProtectionOptions.cs`, después de `Keys`:

```csharp
    /// <summary>Cada cuánto corre el re-cifrado, además de al arrancar (spec 2026-10-08). Entre 1 y
    /// 1440.</summary>
    public int RekeyIntervalMinutes { get; init; } = 60;
```

En `SecretProtectionOptionsValidator.cs`, justo antes de `return failures.Count > 0`:

```csharp
        // P16, en todo ambiente: el PeriodicTimer no acepta cero.
        if (options.RekeyIntervalMinutes is < 1 or > 1440)
        {
            failures.Add($"{SecretProtectionOptions.SectionName}:RekeyIntervalMinutes must be between 1 and 1440.");
        }

```

Reemplaza el contenido de `src/Modules/Integrations/Modules.Integrations.Infrastructure/SecretProtection/ConnectionSecretRekeyWorker.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Infrastructure.Persistence;

namespace Modules.Integrations.Infrastructure.SecretProtection;

internal sealed record RekeyRunResult(int Reencrypted, int Skipped);

/// <summary>
/// Re-cifra con la llave activa todo secreto guardado con otra llave que siga configurada (spec
/// 2026-10-08, «Secreto en reposo»; era <c>WhatsAppTokenRekeyWorker</c> en 6612298). Al arrancar y cada
/// <see cref="SecretProtectionOptions.RekeyIntervalMinutes"/>. Rotar es cambiar <c>ActiveKeyId</c> y dejar
/// la vieja declarada: nadie le pide nada al tenant (criterio 4).
/// <list type="bullet">
/// <item>Por corrida lista sólo ids y <c>key_id</c> de lo pendiente; lo procesa en lotes de
/// <see cref="BatchSize"/> conexiones, un guardado por lote (P16).</item>
/// <item>Lo de una llave retirada, lo que no descifra y lo de un lote que chocó con un <c>PUT</c> se
/// salta y se cuenta en <b>una</b> advertencia por corrida, sin valores ni texto cifrado.</item>
/// <item>Idempotente: en la corrida siguiente lo re-cifrado ya no califica.</item>
/// <item>Cualquier otra falla se registra y espera a la próxima corrida: no tumba el host.</item>
/// <item>No audita: no hay una persona detrás y no cambia ningún valor.</item>
/// </list>
/// <see cref="FirstRunCompletion"/> se completa siempre tras la primera corrida: las pruebas la esperan
/// antes de afirmar que algo no cambió.
/// </summary>
internal sealed partial class ConnectionSecretRekeyWorker(
    IServiceScopeFactory scopeFactory,
    ISecretProtector protector,
    IOptions<SecretProtectionOptions> options,
    ILogger<ConnectionSecretRekeyWorker> logger) : BackgroundService
{
    internal const int BatchSize = 100;

    private readonly TaskCompletionSource _firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstRunCompletion => _firstRun.Task;

    internal TimeSpan Interval { get; } = TimeSpan.FromMinutes(options.Value.RekeyIntervalMinutes);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Skipped} connection secrets could not be re-encrypted to key {ActiveKeyId} (key retired, ciphertext unreadable or row changed meanwhile); they keep their current key.")]
    private static partial void LogSkipped(ILogger logger, int skipped, string activeKeyId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "rekey finished: {Reencrypted} re-encrypted, {Skipped} skipped")]
    private static partial void LogFinished(ILogger logger, int reencrypted, int skipped);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Connection secret rekey failed; it runs again on the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // StartAsync corre ExecuteAsync hasta el primer await: sin esto, una base lenta frenaría el
            // arranque del host.
            await Task.Yield();
            try
            {
                await RunSafelyAsync(stoppingToken);
            }
            finally
            {
                _firstRun.TrySetResult();
            }

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // El host se apaga: no es una falla.
        }
    }

    internal async Task<RekeyRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var active = protector.ActiveKeyId;
        if (active is null)
        {
            return new RekeyRunResult(0, 0);
        }

        List<Guid> connectionIds;
        var skipped = 0;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
            var pending = await dbContext.Connections
                .AsNoTracking()
                .SelectMany(
                    connection => connection.Secrets,
                    (connection, secret) => new { connection.Id, secret.KeyId })
                .Where(row => row.KeyId != active)
                .ToListAsync(cancellationToken);

            // Llave retirada: no hay con qué descifrar; se cuenta y se deja.
            skipped += pending.Count(row => !protector.HasKey(row.KeyId));
            connectionIds = pending
                .Where(row => protector.HasKey(row.KeyId))
                .Select(row => row.Id)
                .Distinct()
                .ToList();
        }

        var reencrypted = 0;
        foreach (var batch in connectionIds.Chunk(BatchSize))
        {
            var (done, notDone) = await RekeyBatchAsync(batch, active, cancellationToken);
            reencrypted += done;
            skipped += notDone;
        }

        if (skipped > 0)
        {
            LogSkipped(logger, skipped, active);
        }

        LogFinished(logger, reencrypted, skipped);
        return new RekeyRunResult(reencrypted, skipped);
    }

    private async Task RunSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunOnceAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, exception);
        }
    }

    private async Task<(int Reencrypted, int Skipped)> RekeyBatchAsync(
        Guid[] connectionIds, string active, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        var connections = await dbContext.Connections
            .Where(connection => connectionIds.Contains(connection.Id))
            .ToListAsync(cancellationToken);

        var reencrypted = 0;
        var skipped = 0;
        foreach (var connection in connections)
        {
            var stale = connection.Secrets
                .Where(secret => !string.Equals(secret.KeyId, active, StringComparison.Ordinal) && protector.HasKey(secret.KeyId))
                .ToArray();
            foreach (var secret in stale)
            {
                if (protector.TryUnprotect(connection.Id, secret.FieldKey, secret.Protected, out var plaintext))
                {
                    connection.Reprotect(secret.FieldKey, protector.Protect(connection.Id, secret.FieldKey, plaintext), now);
                    reencrypted++;
                }
                else
                {
                    skipped++;
                }
            }
        }

        if (reencrypted == 0)
        {
            return (0, skipped);
        }

        try
        {
            await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(cancellationToken);
            return (reencrypted, skipped);
        }
        catch (RequestConcurrencyException)
        {
            // Un PUT guardó una de estas conexiones en el medio: el lote vuelve en la próxima corrida.
            return (0, skipped + reencrypted);
        }
    }
}
```

En `IntegrationsInfrastructureExtensions.cs`, después de `services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();`:

```csharp
        services.AddHostedService<ConnectionSecretRekeyWorker>();
```

En `src/Api/appsettings.example.json`, en el bloque `SecretProtection`:

```json
    "SecretProtection": {
      "ActiveKeyId": "",
      "Keys": {},
      "RekeyIntervalMinutes": 60
    },
```

- [ ] **Step 5: Ver el GREEN**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet build Backend.slnx --no-restore -v q; if ($LASTEXITCODE -ne 0) { throw "build falló (si el paso espera un RED de compilación, ése es el RED)" }
dotnet test tests/Modules/Integrations/Modules.Integrations.UnitTests --no-build --filter "FullyQualifiedName~SecretProtectionOptionsValidatorTests"
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --no-build --filter "FullyQualifiedName~ConnectionSecretRekeyWorkerTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~ConfigurationExampleTests"
```

Esperado: PASS de las tres.

- [ ] **Step 6: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/Modules/Integrations/Modules.Integrations.Infrastructure tests/Modules/Integrations src/Api/appsettings.example.json
$text = @'
feat(integrations): re-cifrado periódico de los secretos

Al arrancar y cada RekeyIntervalMinutes, en lotes de 100; lo que no descifra o
tiene la llave retirada se cuenta en una advertencia por corrida, sin valores.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 13: Endpoints, mapeo de errores y adaptador del autor

**Files:**
- Modify: `src/Modules/Integrations/Modules.Integrations.Api/IntegrationsEndpoints.cs` (las nueve rutas y los dos cuerpos)
- Modify: `src/Api/ApiExceptionHandler.cs:57-64` (`errors` desde `IHasFieldErrors`) y `:129-150` (503)
- Create: `src/Bootstrapper/IntegrationsConnectionAuthorNames.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro del adaptador después de `services.AddScoped<IPosCashierLookup, PosCashierLookup>();`, `:525`)
- Test: `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionsApiTests.cs`, `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionLifecycleApiTests.cs`

**Interfaces:**
- Consumes: todo lo anterior; `IHasFieldErrors` (Task 2), `ServiceUnavailableException` (Task 7); `IMembershipRepository.ListByIdsAsync(TenantId, IReadOnlyCollection<MembershipId>, CancellationToken)` (`IMembershipRepository.cs:53-56`), `IUserDirectory.GetEmailAsync(Guid, CancellationToken)` (`IUserDirectory.cs:10`).
- Produces: las rutas del spec; `CreateConnectionRequest(string? ProviderKey, string? Name, Dictionary<string, string?>? Fields, Dictionary<string, string?>? Secrets)` y `UpdateConnectionRequest(string? Name, Dictionary<string, string?>? Fields, Dictionary<string, string?>? Secrets)`; `ApiExceptionHandler` publica `errors` para `IHasFieldErrors` y responde 503 a `ServiceUnavailableException`.

- [ ] **Step 1: Escribir las pruebas de API que fallan**

Crea `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionsApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Integrations.Application;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-10-08, «Endpoints» y «Códigos de error», por HTTP: crear con prueba contra Zenvia, el
/// contrato JSON en camelCase, cada código con su status, aislamiento y visibilidad por módulo.
/// </summary>
public sealed class ConnectionsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Criterio 2 del spec.
    [Fact]
    public async Task AnAdminConnectsZenviaAndTheCatalogCountsIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(Ct);
        // El contrato del spec, literal: camelCase, enums por nombre, claves de diccionario tal cual.
        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            Assert.Equal("zenvia", root.GetProperty("providerKey").GetString());
            Assert.Equal("Active", root.GetProperty("status").GetString());
            Assert.Equal(FromNumber, root.GetProperty("fields").GetProperty("fromNumber").GetString());
            Assert.True(root.GetProperty("secrets").GetProperty("apiToken").GetProperty("configured").GetBoolean());
            Assert.True(root.GetProperty("secrets").GetProperty("apiToken").GetProperty("readable").GetBoolean());
            Assert.Equal(1, root.GetProperty("version").GetInt64());
            Assert.True(root.GetProperty("createdBy").TryGetProperty("memberId", out _));
        }

        var created = JsonSerializer.Deserialize<ConnectionResponse>(json, JsonSerializerOptions.Web);
        Assert.NotNull(created);
        Assert.Equal(ConnectionUrl(tenant.TenantId, created.Id), response.Headers.Location?.OriginalString);
        Assert.Equal(await OwnerMembershipIdAsync(connectionString, tenant), created.CreatedBy.MemberId);
        Assert.Equal(tenant.Email, created.CreatedBy.DisplayName);
        Assert.NotNull(created.LastVerifiedAt);

        var request = Assert.Single(factory.ZenviaHandler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{ZenviaBaseUrl}/v2/templates", request.Uri?.ToString());
        Assert.Equal(SentinelApiToken, request.Token);
        Assert.Contains("qep-integrations", request.UserAgent, StringComparison.Ordinal);

        var catalog = await client.GetFromJsonAsync<IntegrationsCatalogResponse>(CatalogUrl(tenant.TenantId), Ct);
        Assert.Equal(1, Assert.Single(catalog!.Providers).ConnectionCount);
        var list = await client.GetFromJsonAsync<ConnectionsResponse>(ConnectionsUrl(tenant.TenantId), Ct);
        Assert.Equal(created.Id, Assert.Single(list!.Items).Id);
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'integrations.connection.created' AND tenant_id = @tenantId",
            ("tenantId", tenant.TenantId)));
    }

    // F2 del plan de frontend: credentials_rejected trae errors con la clave del secreto.
    [Fact]
    public async Task RejectedCredentialsAnswer422OnTheSecretFieldAndSaveNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        factory.ZenviaHandler.Status = HttpStatusCode.Unauthorized;

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, errorKeys) = await ProblemAsync(response);
        Assert.Equal("integrations.connection.credentials_rejected", code);
        Assert.Equal(["secrets.apiToken"], errorKeys);
        Assert.Equal(0L, await CountConnectionsAsync(database.GetConnectionString(), tenant.TenantId));
    }

    // D3: «no pude verificar» bloquea y no marca ningún campo.
    [Fact]
    public async Task AnUnreachableProviderAnswers422WithoutAFieldAndSavesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        factory.ZenviaHandler.Throw = new TaskCanceledException("timed out", new TimeoutException());

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, errorKeys) = await ProblemAsync(response);
        Assert.Equal("integrations.connection.provider_unreachable", code);
        Assert.Empty(errorKeys);
        Assert.Equal(0L, await CountConnectionsAsync(database.GetConnectionString(), tenant.TenantId));
    }

    // F1 del plan de frontend y Review Focus 1-2: claves exactas en minúscula, nunca un 500, sin
    // llamar a Zenvia y sin fila.
    [Fact]
    public async Task ValidationIsPerFieldWithExactKeysAndNeverAServerError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), new
        {
            providerKey = "zenvia",
            name = "a\u0000b",
            fields = new Dictionary<string, string?> { ["fromNumber"] = "+573001234567", ["apiToken"] = SentinelApiToken },
            secrets = new Dictionary<string, string?>(),
        });
        var unknownProvider = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), new
        {
            providerKey = "whatsapp",
            name = "Norte",
            fields = new Dictionary<string, string?>(),
            secrets = new Dictionary<string, string?>(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, errorKeys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["fields.apiToken", "fields.fromNumber", "name", "secrets.apiToken"], errorKeys);
        Assert.Equal(["providerKey"], (await ProblemAsync(unknownProvider)).ErrorKeys);
        Assert.Empty(factory.ZenviaHandler.Requests);
        Assert.Equal(0L, await CountConnectionsAsync(database.GetConnectionString(), tenant.TenantId));
    }

    // Review Focus 5.
    [Fact]
    public async Task ANameRepeatedWithOtherCaseIsNameTakenOnlyInsideTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        using var otherClient = CreateClient(factory, other.OwnerUserId, other.TenantId, ManagePermissions);
        await CreateConnectionAsync(client, tenant.TenantId, "WhatsApp Norte");

        var repeated = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody("  whatsapp norte  "));
        var elsewhere = await SendAsync(otherClient, HttpMethod.Post, ConnectionsUrl(other.TenantId), ZenviaBody("WhatsApp Norte"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, repeated.StatusCode);
        Assert.Equal("integrations.connection.name_taken", (await ProblemAsync(repeated)).Code);
        Assert.Equal(1L, await CountConnectionsAsync(database.GetConnectionString(), tenant.TenantId));
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
    }

    // D1.
    [Fact]
    public async Task TheTwentyFirstConnectionIsLimitReached()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        for (var index = 1; index <= 20; index++)
        {
            await CreateConnectionAsync(client, tenant.TenantId, $"Línea {index}");
        }

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody("Línea 21"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.connection.limit_reached", (await ProblemAsync(response)).Code);
        Assert.Equal(20, factory.ZenviaHandler.Requests.Count);
    }

    [Fact]
    public async Task AnotherTenantIs403TheIdIs404InsideItsOwnRouteAndAReaderCannotWrite()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var a = await RegisterTenantAsync(factory);
        var b = await RegisterTenantAsync(factory);
        using var ownerA = CreateClient(factory, a.OwnerUserId, a.TenantId, ManagePermissions);
        using var ownerB = CreateClient(factory, b.OwnerUserId, b.TenantId, ManagePermissions);
        using var readerA = CreateClient(factory, a.OwnerUserId, a.TenantId, ReadPermissions);
        var created = await CreateConnectionAsync(ownerA, a.TenantId);

        var crossRead = await SendAsync(ownerB, HttpMethod.Get, ConnectionUrl(a.TenantId, created.Id));
        var crossWrite = await SendAsync(ownerB, HttpMethod.Post, ConnectionsUrl(a.TenantId), ZenviaBody("Intrusa"));
        var foreignId = await SendAsync(ownerB, HttpMethod.Get, ConnectionUrl(b.TenantId, created.Id));
        var readerWrite = await SendAsync(readerA, HttpMethod.Post, ConnectionsUrl(a.TenantId), ZenviaBody("Otra"));

        Assert.Equal(HttpStatusCode.Forbidden, crossRead.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(crossRead)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, crossWrite.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignId.StatusCode);
        Assert.Equal("integrations.connection.not_found", (await ProblemAsync(foreignId)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, readerWrite.StatusCode);
        Assert.Equal(1L, await CountConnectionsAsync(database.GetConnectionString(), a.TenantId));
    }

    // Criterio 6 del spec.
    [Fact]
    public async Task TurningOffTheConsumingModuleHidesTheConnectionAndTurningItOnBringsItBackIntact()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);

        await SetModuleStatusAsync(connectionString, tenant.TenantId, "quotations", "inactive");

        var catalog = await client.GetFromJsonAsync<IntegrationsCatalogResponse>(CatalogUrl(tenant.TenantId), Ct);
        var list = await client.GetFromJsonAsync<ConnectionsResponse>(ConnectionsUrl(tenant.TenantId), Ct);
        var hidden = await SendAsync(client, HttpMethod.Get, ConnectionUrl(tenant.TenantId, created.Id));
        var newOne = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody("Otra"));
        Assert.Empty(catalog!.Providers);
        Assert.Empty(list!.Items);
        Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(hidden)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, newOne.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(newOne)).Code);
        Assert.Equal(1L, await CountConnectionsAsync(connectionString, tenant.TenantId));

        await SetModuleStatusAsync(connectionString, tenant.TenantId, "quotations", "active");

        var back = await client.GetFromJsonAsync<ConnectionResponse>(ConnectionUrl(tenant.TenantId, created.Id), Ct);
        Assert.Equal(created.Version, back!.Version);
        Assert.Equal("Active", back.Status);
        Assert.True(back.Secrets["apiToken"].Readable);
    }
}
```

Crea `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionLifecycleApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Modules.Integrations.Application;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-10-08, ciclo de vida por HTTP: If-Match 428/412 donde el spec lo pide, <c>test</c> sin
/// If-Match y siempre 200 (P10, P27), eventos y auditoría, cascada de secretos, el PUT que conserva el
/// secreto y el 503 sin llave activa.
/// </summary>
public sealed class ConnectionLifecycleApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ConnectionResponse> ReadAsync(HttpResponseMessage response)
    {
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.NotNull(connection);
        return connection;
    }

    private static Task<long> EventsAsync(string connectionString, string eventName, Guid connectionId) =>
        ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = @eventName AND payload->>'connectionId' = @id",
            ("eventName", eventName),
            ("id", connectionId.ToString()));

    [Fact]
    public async Task PauseResumeTestAndDeleteFollowTheirContract()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);
        var url = ConnectionUrl(tenant.TenantId, created.Id);

        var pauseWithoutIfMatch = await SendAsync(client, HttpMethod.Post, $"{url}/pause");
        var pauseStale = await SendAsync(client, HttpMethod.Post, $"{url}/pause", ifMatch: "\"7\"");
        var paused = await SendAsync(client, HttpMethod.Post, $"{url}/pause", ifMatch: "\"1\"");
        var pausedAgain = await SendAsync(client, HttpMethod.Post, $"{url}/pause", ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.PreconditionRequired, pauseWithoutIfMatch.StatusCode);
        Assert.Equal("precondition.if_match_required", (await ProblemAsync(pauseWithoutIfMatch)).Code);
        Assert.Equal(HttpStatusCode.PreconditionFailed, pauseStale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(pauseStale)).Code);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.Equal("Paused", (await ReadAsync(paused)).Status);
        Assert.Equal("integrations.connection.not_active", (await ProblemAsync(pausedAgain)).Code);

        // D4: reanudar vuelve a probar; con la credencial rechazada queda NeedsAttention y responde 422.
        factory.ZenviaHandler.Status = HttpStatusCode.Unauthorized;
        var resumed = await SendAsync(client, HttpMethod.Post, $"{url}/resume", ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resumed.StatusCode);
        var (resumeCode, resumeKeys) = await ProblemAsync(resumed);
        Assert.Equal("integrations.connection.credentials_rejected", resumeCode);
        Assert.Equal(["secrets.apiToken"], resumeKeys);
        var attention = await client.GetFromJsonAsync<ConnectionResponse>(url, Ct);
        Assert.Equal("NeedsAttention", attention!.Status);
        Assert.Equal(3L, attention.Version);
        Assert.Equal("credentials_rejected", attention.LastFailureCode);

        // NeedsAttention → Active con un test que pasa (sin If-Match, P27).
        factory.ZenviaHandler.Status = HttpStatusCode.OK;
        var tested = await SendAsync(client, HttpMethod.Post, $"{url}/test");
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        var active = await ReadAsync(tested);
        Assert.Equal("Active", active.Status);
        Assert.Equal(4L, active.Version);

        var deleteWithoutIfMatch = await SendAsync(client, HttpMethod.Delete, url);
        var deleteStale = await SendAsync(client, HttpMethod.Delete, url, ifMatch: "\"3\"");
        var deleted = await SendAsync(client, HttpMethod.Delete, url, ifMatch: "\"4\"");
        var gone = await SendAsync(client, HttpMethod.Get, url);

        Assert.Equal(HttpStatusCode.PreconditionRequired, deleteWithoutIfMatch.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, deleteStale.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("integrations.connection.not_found", (await ProblemAsync(gone)).Code);
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM integrations.connection_secrets WHERE connection_id = @id", ("id", created.Id)));

        Assert.Equal(1L, await EventsAsync(connectionString, ConnectionEvents.Paused, created.Id));
        Assert.Equal(1L, await EventsAsync(connectionString, ConnectionEvents.NeedsAttention, created.Id));
        Assert.Equal(1L, await EventsAsync(connectionString, ConnectionEvents.Deleted, created.Id));
        var actions = await ScalarAsync<string>(
            connectionString,
            "SELECT string_agg(action, ',') FROM audit.entries WHERE resource_type = 'integration_connection' AND resource_id = @id",
            ("id", created.Id.ToString()));
        foreach (var action in new[] { "created", "paused", "needs_attention", "verified", "deleted" })
        {
            Assert.Contains($"integrations.connection.{action}", actions, StringComparison.Ordinal);
        }
    }

    // F4 del plan de frontend y P10: una prueba fallida responde 200 con la falla anotada. Unreachable
    // deja el estado; un rechazo sobre una Active la pasa a NeedsAttention con su evento (DECISIÓN 2,
    // owner 2026-10-08). Sin If-Match (F11 / P27).
    [Fact]
    public async Task AFailedTestAnswers200WithTheFailureAndNeedsNoIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);
        var url = ConnectionUrl(tenant.TenantId, created.Id);

        factory.ZenviaHandler.Throw = new HttpRequestException("connection refused");
        var unreachable = await SendAsync(client, HttpMethod.Post, $"{url}/test");
        factory.ZenviaHandler.Throw = null;
        factory.ZenviaHandler.Status = HttpStatusCode.Unauthorized;
        var rejected = await SendAsync(client, HttpMethod.Post, $"{url}/test");

        Assert.Equal(HttpStatusCode.OK, unreachable.StatusCode);
        var afterUnreachable = await ReadAsync(unreachable);
        Assert.Equal("Active", afterUnreachable.Status);
        Assert.Equal("provider_unreachable", afterUnreachable.LastFailureCode);
        Assert.Equal(2L, afterUnreachable.Version);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var afterRejected = await ReadAsync(rejected);
        Assert.Equal("NeedsAttention", afterRejected.Status);
        Assert.Equal("credentials_rejected", afterRejected.LastFailureCode);
        Assert.NotNull(afterRejected.LastFailureAt);
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'integrations.connection.verified' AND outcome = 'failure' AND resource_id = @id",
            ("id", created.Id.ToString())));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'integrations.connection-needs-attention.v1'"));
    }

    // D5, P9 y Review Focus 3.
    [Fact]
    public async Task PutKeepsAnAbsentOrBlankSecretAndOnlyTestsWhenSomethingThatMattersChanged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);
        var url = ConnectionUrl(tenant.TenantId, created.Id);

        var renamed = await SendAsync(client, HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = FromNumber },
            secrets = new Dictionary<string, string?>(),
        }, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(2L, (await ReadAsync(renamed)).Version);
        Assert.Single(factory.ZenviaHandler.Requests);

        var newNumber = await SendAsync(client, HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = "573009999999" },
            secrets = new Dictionary<string, string?> { ["apiToken"] = "   " },
        }, "\"2\"");
        Assert.Equal(HttpStatusCode.OK, newNumber.StatusCode);
        Assert.Equal(3L, (await ReadAsync(newNumber)).Version);
        Assert.Equal(2, factory.ZenviaHandler.Requests.Count);
        Assert.Equal(SentinelApiToken, factory.ZenviaHandler.Requests.Last().Token);

        var withoutIfMatch = await SendAsync(client, HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = FromNumber },
            secrets = new Dictionary<string, string?>(),
        });
        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutIfMatch.StatusCode);

        factory.ZenviaHandler.Status = HttpStatusCode.Unauthorized;
        var rejected = await SendAsync(client, HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = "573009999999" },
            secrets = new Dictionary<string, string?> { ["apiToken"] = "otro-token" },
        }, "\"3\"");
        Assert.Equal("integrations.connection.credentials_rejected", (await ProblemAsync(rejected)).Code);
        var unchanged = await client.GetFromJsonAsync<ConnectionResponse>(url, Ct);
        Assert.Equal(3L, unchanged!.Version);
        Assert.Equal("573009999999", unchanged.Fields["fromNumber"]);
    }

    // F3 del plan de frontend: 503 sin llave activa; la lectura y lo que no cifra siguen.
    [Fact]
    public async Task WithoutAnActiveKeyWritesAre503AndReadsStillWork()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);
        var url = ConnectionUrl(tenant.TenantId, created.Id);
        using var withoutKey = factory.WithSecretProtection(string.Empty);
        using var degraded = CreateClient(withoutKey, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var create = await SendAsync(degraded, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody("Otra"));
        var update = await SendAsync(degraded, HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = FromNumber },
            secrets = new Dictionary<string, string?>(),
        }, "\"1\"");
        var read = await degraded.GetFromJsonAsync<ConnectionResponse>(url, Ct);
        var pause = await SendAsync(degraded, HttpMethod.Post, $"{url}/pause", ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
        Assert.Equal("integrations.secret_protection.unavailable", (await ProblemAsync(create)).Code);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, update.StatusCode);
        Assert.True(read!.Secrets["apiToken"].Readable);
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
    }
}
```

- [ ] **Step 2: Ver el RED**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
  Where-Object { $_.CommandLine -and $_.CommandLine.Contains($B) -and $_.CommandLine -match 'Api(\.dll|\.exe)' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --filter "FullyQualifiedName~ConnectionsApiTests|FullyQualifiedName~ConnectionLifecycleApiTests"
```

Esperado: FAIL de todas: las rutas no están mapeadas, `Assert.Equal() Failure: Values differ — Expected: Created — Actual: NotFound` (o `MethodNotAllowed`).

- [ ] **Step 3: Las nueve rutas**

Reemplaza `src/Modules/Integrations/Modules.Integrations.Api/IntegrationsEndpoints.cs` entero:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Integrations.Application;

namespace Modules.Integrations.Api;

public static class IntegrationsEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como pos y companies. Cada endpoint declara su propio
        // RequireAuthorization (spec 2026-10-08, «Endpoints»): el grupo no lleva política. Los
        // handlers revalidan tenant y permiso (doble capa) y responden 403, nunca 404, ante otro tenant.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/integrations")
            .WithTags("Integrations");

        group.MapGet("/catalog", GetCatalogAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionRead)
            .Produces<IntegrationsCatalogResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/connections", ListAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionRead)
            .Produces<ConnectionsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/connections", CreateAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Accepts<CreateConnectionRequest>("application/json")
            .Produces<ConnectionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/connections/{connectionId:guid}", GetAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionRead)
            .Produces<ConnectionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/connections/{connectionId:guid}", UpdateAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Accepts<UpdateConnectionRequest>("application/json")
            .Produces<ConnectionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // P27: sin If-Match, como dice el spec; sólo anota el resultado de la prueba.
        group.MapPost("/connections/{connectionId:guid}/test", TestAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Produces<ConnectionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/connections/{connectionId:guid}/pause", PauseAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Produces<ConnectionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/connections/{connectionId:guid}/resume", ResumeAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Produces<ConnectionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/connections/{connectionId:guid}", DeleteAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    private static async Task<IResult> GetCatalogAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetIntegrationsCatalogQuery(tenantId), cancellationToken));

    private static async Task<IResult> ListAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListConnectionsQuery(tenantId), cancellationToken));

    private static async Task<IResult> CreateAsync(
        Guid tenantId, CreateConnectionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var connection = await dispatcher.SendAsync(
            new CreateConnectionCommand(tenantId, request.ProviderKey, request.Name, request.Fields, request.Secrets),
            cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/integrations/connections/{connection.Id}", connection);
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetConnectionQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        Guid connectionId,
        UpdateConnectionRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateConnectionCommand(
                tenantId, connectionId, RequireVersion(httpContext), request.Name, request.Fields, request.Secrets),
            cancellationToken));

    private static async Task<IResult> TestAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new TestConnectionCommand(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> PauseAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new PauseConnectionCommand(tenantId, connectionId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> ResumeAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ResumeConnectionCommand(tenantId, connectionId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> DeleteAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(
            new DeleteConnectionCommand(tenantId, connectionId, RequireVersion(httpContext)), cancellationToken);
        return Results.NoContent();
    }

    // Mismo contrato que /pos y /orders-export-layout: sin If-Match 428, vieja 412 (en el handler).
    private static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded connection version is required.");

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

/// <summary>El cuerpo de <c>POST /connections</c>. <see cref="ToString"/> sólo muestra claves.</summary>
public sealed record CreateConnectionRequest(
    string? ProviderKey,
    string? Name,
    Dictionary<string, string?>? Fields,
    Dictionary<string, string?>? Secrets)
{
    public override string ToString() =>
        $"CreateConnectionRequest {{ ProviderKey = {ProviderKey}, Name = {Name}, "
        + $"Fields = [{(Fields is null ? string.Empty : string.Join(", ", Fields.Keys))}], "
        + $"Secrets = [{(Secrets is null ? string.Empty : string.Join(", ", Secrets.Keys))}] }}";
}

/// <summary>El cuerpo de <c>PUT /connections/{id}</c>: un secreto ausente conserva el guardado (D5).</summary>
public sealed record UpdateConnectionRequest(
    string? Name,
    Dictionary<string, string?>? Fields,
    Dictionary<string, string?>? Secrets)
{
    public override string ToString() =>
        $"UpdateConnectionRequest {{ Name = {Name}, "
        + $"Fields = [{(Fields is null ? string.Empty : string.Join(", ", Fields.Keys))}], "
        + $"Secrets = [{(Secrets is null ? string.Empty : string.Join(", ", Secrets.Keys))}] }}";
}
```

- [ ] **Step 4: `errors` desde `IHasFieldErrors` y el 503 en `ApiExceptionHandler`**

En `src/Api/ApiExceptionHandler.cs`, reemplaza el bloque de `errors` (`:57-64`):

```csharp
        if (exception is ValidationException validationException)
        {
            problem.Extensions["errors"] = validationException.Errors
                .GroupBy(error => error.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.ErrorMessage).ToArray());
        }
```

por:

```csharp
        if (exception is ValidationException validationException)
        {
            problem.Extensions["errors"] = validationException.Errors
                .GroupBy(error => error.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.ErrorMessage).ToArray());
        }
        else if (exception is IHasFieldErrors { FieldErrors.Count: > 0 } withFieldErrors)
        {
            // Spec 2026-10-08 (Integraciones): un error de dominio que marca un campo
            // (credentials_rejected → secrets.apiToken) viaja con el mismo mapa que el 422 de
            // FluentValidation, el único que el formulario sabe leer.
            problem.Extensions["errors"] = withFieldErrors.FieldErrors;
        }
```

Y en `MapException`, después del brazo de `PreconditionRequiredException` (`:139-140`):

```csharp
            ServiceUnavailableException value =>
                (StatusCodes.Status503ServiceUnavailable, "Service unavailable", value.Code),
```

(`IHasFieldErrors` está en `BuildingBlocks.Domain`, que el archivo ya importa en `:2`; `ServiceUnavailableException`, en `BuildingBlocks.Application`, `:1`.)

- [ ] **Step 5: El adaptador del nombre del autor**

Crea `src/Bootstrapper/IntegrationsConnectionAuthorNames.cs`:

```csharp
using Modules.Identity.Application;
using Modules.Integrations.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// <c>createdBy.displayName</c> de una conexión (P23): el nombre de la membresía o, si no tiene, el
/// correo del usuario. Mismas fuentes que <see cref="PosCashierLookup"/>; vive acá porque Integrations
/// no referencia Identity. Una membresía que ya no está en el tenant no aparece.
/// </summary>
internal sealed class IntegrationsConnectionAuthorNames(
    IMembershipRepository memberships,
    IUserDirectory users) : IConnectionAuthorNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        if (memberIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var scoped = await memberships.ListByIdsAsync(
            new TenantId(tenantId), memberIds.Distinct().Select(id => new MembershipId(id)).ToArray(), cancellationToken);
        var names = new Dictionary<Guid, string>(scoped.Count);
        foreach (var membership in scoped)
        {
            if (!string.IsNullOrWhiteSpace(membership.DisplayName))
            {
                names[membership.Id.Value] = membership.DisplayName;
                continue;
            }

            var email = await users.GetEmailAsync(membership.UserId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(email))
            {
                names[membership.Id.Value] = email;
            }
        }

        return names;
    }
}
```

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, después de `services.AddScoped<IPosCashierLookup, PosCashierLookup>();` (`:525`):

```csharp

        // Integrations (spec 2026-10-08, P23): el nombre del autor de una conexión sale de Tenancy e
        // Identity; Integrations no referencia Identity.
        services.AddScoped<IConnectionAuthorNames, IntegrationsConnectionAuthorNames>();
```

- [ ] **Step 6: Ver el GREEN**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
  Where-Object { $_.CommandLine -and $_.CommandLine.Contains($B) -and $_.CommandLine -match 'Api(\.dll|\.exe)' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
dotnet build Backend.slnx --no-restore -v q; if ($LASTEXITCODE -ne 0) { throw "build falló (si el paso espera un RED de compilación, ése es el RED)" }
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --no-build --filter "FullyQualifiedName~ConnectionsApiTests|FullyQualifiedName~ConnectionLifecycleApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~IntegrationsLayerTests"
```

Esperado: PASS de las tres clases. `ApiExceptionHandler` lo usan todos los módulos: su suite completa corre en la Task 15.

- [ ] **Step 7: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- src/Modules/Integrations/Modules.Integrations.Api src/Api/ApiExceptionHandler.cs src/Bootstrapper/IntegrationsConnectionAuthorNames.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Integrations/Modules.Integrations.IntegrationTests
$text = @'
feat(integrations): endpoints de conexiones y mapeo de errores

Las nueve rutas bajo /integrations con If-Match donde el spec lo pide.
ApiExceptionHandler publica errors para IHasFieldErrors y responde 503 a
ServiceUnavailableException.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 14: Fuga de secretos y puertos de consumidores contra la base

**Files:**
- Move: `6612298:tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSecretLeakTests.cs` → `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionSecretLeakTests.cs`
- Test: `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionSecretLeakTests.cs`, `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConsumerPortsApiTests.cs`

**Interfaces:**
- Consumes: todo lo anterior (no cambia código de producción: si una prueba falla, el arreglo va al archivo de producción que la causa, en esta misma tarea).
- Produces: la prueba del criterio 3 del spec y la de los puertos contra la base.

- [ ] **Step 1: Traer la prueba de fuga, sin editarla, y commitear el traslado**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git checkout 6612298 -- tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSecretLeakTests.cs
git mv tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSecretLeakTests.cs tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionSecretLeakTests.cs
git -C $Main status --short
git status --short
$text = @'
test(integrations): mover la prueba de fuga de secretos

Movida sin cambios desde
6612298:tests/Modules/Quotations/Modules.Quotations.IntegrationTests/WhatsAppSecretLeakTests.cs.
La adaptación va en el commit siguiente.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

- [ ] **Step 2: Adaptar la prueba de fuga y escribir la de los puertos**

Reemplaza el contenido de `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConnectionSecretLeakTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Integrations.Application;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Criterio 3 del spec 2026-10-08 (viene de <c>WhatsAppSecretLeakTests</c>, 6612298): ninguna
/// respuesta, log, auditoría, outbox ni <c>platform.request_failures</c> contiene el valor de un
/// secreto. Tres fallas que llevan un token centinela adentro —un 422 del validador, el 503 sin llave
/// activa y un 401 de Zenvia que repite el token en su cuerpo— y un ciclo de vida completo que además
/// no puede dejar un valor de campo (ni <c>fromNumber</c>) en auditoría u outbox.
/// </summary>
public sealed class ConnectionSecretLeakTests
{
    private const string SecondSentinel = "zenvia-token-SENTINEL-2b8e";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertNothingLeaksAsync(
        string connectionString, CapturedLogs logs, IEnumerable<string> responseBodies, params string[] values)
    {
        var failures = await RequestFailuresTextAsync(connectionString);
        var trail = await AuditAndOutboxTextAsync(connectionString);
        var bodies = string.Join('\n', responseBodies);
        foreach (var value in values)
        {
            Assert.DoesNotContain(value, bodies, StringComparison.Ordinal);
            Assert.DoesNotContain(value, logs.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain(value, failures, StringComparison.Ordinal);
            Assert.DoesNotContain(value, trail, StringComparison.Ordinal);
        }
    }

    // Control positivo: sin esto, un CapturedLogs que no engancha el host vuelve vacía la ausencia.
    private static void AssertLogged(CapturedLogs logs, string expectedFragment) =>
        Assert.Contains(logs.Entries, entry => entry.Contains(expectedFragment, StringComparison.Ordinal));

    private static Task<long> FailuresWithStatusAsync(string connectionString, int status) =>
        ScalarAsync<long>(connectionString, $"SELECT count(*) FROM platform.request_failures WHERE status_code = {status}");

    [Fact]
    public async Task AValidationFailureCarryingTheTokenDoesNotLeakIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        var tenant = await RegisterTenantAsync(host);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody(name: new string('x', 81)));
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(await FailuresWithStatusAsync(connectionString, 422) >= 1);
        AssertLogged(logs, "API request failed with code");
        await AssertNothingLeaksAsync(connectionString, logs, [body], SentinelApiToken);
    }

    // Sin llave activa: 503. Su mensaje nombra la clave de configuración, no el token.
    [Fact]
    public async Task AServiceUnavailableWithoutAnActiveKeyDoesNotLeakIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithSecretProtection(string.Empty).WithCapturedLogs(logs);
        var tenant = await RegisterTenantAsync(host);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(await FailuresWithStatusAsync(connectionString, 503) >= 1);
        AssertLogged(logs, "Unhandled API exception");
        Assert.Contains("integrations.secret_protection.unavailable", await RequestFailuresTextAsync(connectionString) + body, StringComparison.Ordinal);
        await AssertNothingLeaksAsync(connectionString, logs, [body], SentinelApiToken);
    }

    // Zenvia responde 401 con un cuerpo que, por si acaso, repite el token: ninguno de los dos sale.
    [Fact]
    public async Task ZenviaRejectingTheCredentialsDoesNotLeakTheTokenNorItsBody()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        factory.ZenviaHandler.Status = HttpStatusCode.Unauthorized;
        factory.ZenviaHandler.Body = JsonSerializer.Serialize(new { message = SentinelZenviaBody, echo = SentinelApiToken });
        var tenant = await RegisterTenantAsync(host);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.connection.credentials_rejected", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.Equal(SentinelApiToken, Assert.Single(factory.ZenviaHandler.Requests).Token);
        AssertLogged(logs, "Zenvia credential test answered HTTP 401");
        await AssertNothingLeaksAsync(connectionString, logs, [body], SentinelApiToken, SentinelZenviaBody);
    }

    // Spec, «Auditoría»: nunca un valor de campo, ni público. Y el token nunca en fields.
    [Fact]
    public async Task AWholeLifecycleNeverWritesASecretOrAFieldValueOutsideTheConnection()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var logs = new CapturedLogs();
        using var host = factory.WithCapturedLogs(logs);
        var tenant = await RegisterTenantAsync(host);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var bodies = new List<string>();

        async Task<HttpResponseMessage> CallAsync(HttpMethod method, string url, object? body = null, string? ifMatch = null)
        {
            var response = await SendAsync(client, method, url, body, ifMatch);
            bodies.Add(await response.Content.ReadAsStringAsync(Ct));
            return response;
        }

        var created = await CallAsync(HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());
        var id = (await created.Content.ReadFromJsonAsync<ConnectionResponse>(Ct))!.Id;
        var url = ConnectionUrl(tenant.TenantId, id);
        (await CallAsync(HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = "573009999999" },
            secrets = new Dictionary<string, string?> { ["apiToken"] = SecondSentinel },
        }, "\"1\"")).EnsureSuccessStatusCode();
        (await CallAsync(HttpMethod.Post, $"{url}/test")).EnsureSuccessStatusCode();
        (await CallAsync(HttpMethod.Post, $"{url}/pause", ifMatch: "\"3\"")).EnsureSuccessStatusCode();
        (await CallAsync(HttpMethod.Post, $"{url}/resume", ifMatch: "\"4\"")).EnsureSuccessStatusCode();

        var fields = await ScalarAsync<string>(connectionString, "SELECT fields::text FROM integrations.connections WHERE id = @id", ("id", id));
        Assert.DoesNotContain(SentinelApiToken, fields, StringComparison.Ordinal);
        Assert.DoesNotContain(SecondSentinel, fields, StringComparison.Ordinal);

        (await CallAsync(HttpMethod.Delete, url, ifMatch: "\"5\"")).EnsureSuccessStatusCode();

        AssertLogged(logs, "Zenvia credential test answered HTTP 200");
        await AssertNothingLeaksAsync(connectionString, logs, bodies, SentinelApiToken, SecondSentinel);
        var trail = await AuditAndOutboxTextAsync(connectionString);
        Assert.Contains("fromNumber", trail, StringComparison.Ordinal);
        Assert.DoesNotContain(FromNumber, trail, StringComparison.Ordinal);
        Assert.DoesNotContain("573009999999", trail, StringComparison.Ordinal);
    }
}
```

Crea `tests/Modules/Integrations/Modules.Integrations.IntegrationTests/ConsumerPortsApiTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Application;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-10-08, «Pruebas»: <c>IIntegrationConnections.ResolveAsync</c> contra la base (Active sí;
/// Paused, NeedsAttention, otro tenant y módulo apagado no) e <c>IConnectionHealthReporter</c>
/// (NeedsAttention, auditoría con actor Integration, evento, idempotente).
/// </summary>
public sealed class ConsumerPortsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePortsSeeOnlyActiveVisibleConnectionsOfTheirTenant()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var active = await CreateConnectionAsync(client, tenant.TenantId, "Activa");
        var paused = await CreateConnectionAsync(client, tenant.TenantId, "Pausada");
        (await SendAsync(client, HttpMethod.Post, $"{ConnectionUrl(tenant.TenantId, paused.Id)}/pause", ifMatch: "\"1\""))
            .EnsureSuccessStatusCode();
        var rejected = await CreateConnectionAsync(client, tenant.TenantId, "Rechazada");

        using (var scope = factory.Services.CreateScope())
        {
            var reporter = scope.ServiceProvider.GetRequiredService<IConnectionHealthReporter>();
            await reporter.ReportCredentialsRejectedAsync(tenant.TenantId, rejected.Id, "credentials_rejected", Ct);
        }

        // Idempotente: el segundo reporte, con la conexión ya en NeedsAttention, no deja nada.
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IConnectionHealthReporter>()
                .ReportCredentialsRejectedAsync(tenant.TenantId, rejected.Id, "credentials_rejected", Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var connections = scope.ServiceProvider.GetRequiredService<IIntegrationConnections>();

            var resolved = await connections.ResolveAsync(tenant.TenantId, active.Id, Ct);
            Assert.NotNull(resolved);
            Assert.Equal(SentinelApiToken, resolved.Secrets["apiToken"]);
            Assert.Equal(FromNumber, resolved.Fields["fromNumber"]);
            Assert.Null(await connections.ResolveAsync(tenant.TenantId, paused.Id, Ct));
            Assert.Null(await connections.ResolveAsync(tenant.TenantId, rejected.Id, Ct));
            Assert.Null(await connections.ResolveAsync(Guid.CreateVersion7(), active.Id, Ct));
            Assert.Equal([active.Id], (await connections.ListActiveAsync(tenant.TenantId, "zenvia", Ct)).Select(summary => summary.Id));
        }

        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'integrations.connection.needs_attention' AND actor_type = 'Integration' AND resource_id = @id",
            ("id", rejected.Id.ToString())));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'integrations.connection-needs-attention.v1' AND payload->>'connectionId' = @id",
            ("id", rejected.Id.ToString())));

        await SetModuleStatusAsync(connectionString, tenant.TenantId, "quotations", "inactive");
        using (var scope = factory.Services.CreateScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IIntegrationConnections>()
                .ResolveAsync(tenant.TenantId, active.Id, Ct));
        }
    }
}
```

- [ ] **Step 3: Correr (RED si algo fuga; si no, GREEN directo)**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
dotnet test tests/Modules/Integrations/Modules.Integrations.IntegrationTests --filter "FullyQualifiedName~ConnectionSecretLeakTests|FullyQualifiedName~ConsumerPortsApiTests"
```

Esperado: PASS. Esta tarea verifica comportamiento que las anteriores ya construyeron; el RED honesto es el de la versión movida sin adaptar (no compila, Step 1). Si alguna aserción de fuga falla, **no la aflojes**: busca qué camino imprime el valor (un `ToString` de record, un mensaje de excepción, un log) y arréglalo en ese archivo de producción, dentro de esta tarea, con la salida literal del RED y del GREEN en el handoff.

- [ ] **Step 4: Chequeo de formato y commit**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- tests/Modules/Integrations/Modules.Integrations.IntegrationTests
$text = @'
test(integrations): fuga de secretos y puertos contra la base

Centinela por respuestas, logs, auditoría, outbox y request_failures; los dos
puertos de consumidores contra PostgreSQL.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

---

### Task 15: README, k8s, reglas del repositorio y verificación completa

**Files:**
- Modify: `README.md` — validadores (`:94-98`), tabla de § «Configuración» (después de `:135`), § «Módulos por tenant» (`:620-628`), tabla de § «API implementada» (después de `:770`), sección nueva antes de `## Verificación` (`:1756`)
- Modify: `k8s/prod-configMap.yaml` (después de `:89`), `k8s/prod-secret.yaml` (después de `:42`)
- Modify: `CLAUDE.md:255-258`
- Fuera del repo (no se commitea): `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\HANDOFF-integraciones.md`

**Interfaces:**
- Consumes: todo el plan; la línea base de la Task 0.
- Produces: documentación al día y la comparación de la suite contra la línea base.

- [ ] **Step 1: README**

1. Validadores (`:94-98`): reemplaza `` `CorsSettingsValidator` y `OperatorTenantOptionsValidator`) `` por `` `CorsSettingsValidator`, `OperatorTenantOptionsValidator` y los dos de Integrations, `SecretProtectionOptionsValidator` y `ZenviaOptionsValidator`) ``.
2. Tabla de § «Configuración», después de la fila de `Quotations:PaymentProofs:PublicLinks` (`:135`):

```markdown
| `Integrations:SecretProtection:ActiveKeyId`            | ausente (user-secrets en local)                                                               | Id de la llave con la que se cifran las credenciales de las conexiones (`^[a-z0-9]{1,32}$`). **En `Production` es obligatoria**, y su llave tiene que existir y ser de 32 bytes en base64: sin ella la API no arranca. Fuera de producción, sin ella la API arranca y crear o editar una conexión responde `503 integrations.secret_protection.unavailable`. Ver [Integraciones](#integraciones-conexiones-del-tenant) |
| `Integrations:SecretProtection:Keys:<id>`              | user-secrets                                                                                  | Llave AES-256 (32 bytes en base64). Es un secreto: en k8s va en el Secret, nunca en el ConfigMap |
| `Integrations:SecretProtection:RekeyIntervalMinutes`   | `60`                                                                                          | Cada cuánto el worker re-cifra con la llave activa, además de al arrancar. Entre 1 y 1440 |
| `Integrations:Zenvia:BaseUrl`                          | `https://api.zenvia.com`                                                                      | URL de la prueba de credenciales de Zenvia (`GET /v2/templates`). HTTPS absoluta. El sender global de cotizaciones sigue usando `Quotations:WhatsApp:BaseUrl` |
```

3. § «Módulos por tenant» (`:620-628`): reemplaza `Identidad, Tenancy, Authorization, Storage, Platform, Geography, Audit y Notifications son núcleo y no se apagan.` por `Identidad, Tenancy, Authorization, Storage, Platform, Geography, Audit, Notifications e Integrations son núcleo y no se apagan; Integrations filtra su catálogo de proveedores por los módulos activos del tenant.`
4. Tabla de § «API implementada», después de la fila de `/operator/tenants` (`:770`):

```markdown
| `/api/v1/tenants/{tenantId}/integrations`          | `catalog` (`GET`), `connections` (`GET`, `POST`), y por conexión `GET`, `PUT` (`If-Match`), `test` (`POST`), `pause` y `resume` (`POST`, `If-Match`), `DELETE` (`If-Match`) | `integrations.connection.read` / `.manage` |
```

5. Sección nueva, justo antes de `## Verificación`:

````markdown
### Integraciones (conexiones del tenant)

Cada tenant conecta sus propias cuentas de plataformas externas en **Ajustes → Integraciones**
(spec 2026-10-08). Hoy el catálogo tiene un proveedor, **Zenvia (WhatsApp)**, visible para los
tenants con `quotations` activo; un proveedor nuevo es otra entrada de `IntegrationProviders`, su
probador y una migración que cambia el `CHECK` de `provider_key`. Varias conexiones por proveedor
(tope de 20), cada una con nombre. Ningún módulo **usa** todavía las conexiones: cada consumidor lo
hará en su spec, por los puertos `IIntegrationConnections` e `IConnectionHealthReporter`.

Al guardar, la credencial se prueba contra el proveedor (Zenvia: `GET /v2/templates` con
`X-API-TOKEN`); si la rechaza o no responde, no se guarda nada. Las credenciales son de sólo
escritura: ninguna respuesta, log, auditoría ni evento las lleva, y se guardan cifradas con
AES-256-GCM en `integrations.connection_secrets` (`nonce || ciphertext || tag`, AAD
`integrations.connection:{connectionId}:{fieldKey}`). La llave vive **fuera** de la base:

| Clave | Dónde vive en prod | Contenido |
| --- | --- | --- |
| `Integrations:SecretProtection:ActiveKeyId` | ConfigMap | id de llave (`^[a-z0-9]{1,32}$`); no es secreto |
| `Integrations:SecretProtection:Keys:<id>` | Secret, desde la variable secreta del grupo `Backend-prod` | 32 bytes en base64 |

#### Generar una llave en local

`dotnet user-secrets set` imprime la clave **y su valor**, y `list` imprime todos los valores: el
`set` va con `| Out-Null` y la verificación cuenta.

```powershell
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$key = [Convert]::ToBase64String($bytes)
dotnet user-secrets set "Integrations:SecretProtection:Keys:k1" $key --project src/Api | Out-Null
Remove-Variable key, bytes
dotnet user-secrets set "Integrations:SecretProtection:ActiveKeyId" "k1" --project src/Api | Out-Null
dotnet user-secrets list --project src/Api | Select-String -Pattern "SecretProtection:Keys:k1" | Measure-Object
```

El último comando tiene que dar `Count 1`.

#### Custodia de la llave de producción

- Para generarla sin imprimirla: el mismo bloque, pero terminando en `Set-Clipboard $key` en vez de
  `user-secrets set`; se pega en la variable secreta y en la bóveda, y después se vacía el
  portapapeles (`Set-Clipboard -Value $null`).
- Fuente de verdad: la variable **secreta** `INTEGRATIONS_SECRET_PROTECTION_KEY_K1` del grupo
  `Backend-prod` (Azure DevOps). Respaldo: una copia en la bóveda del owner. Perder las dos es perder
  todas las credenciales guardadas: cada tenant tendría que volver a pegar las suyas.
- No se edita con `kubectl`: el pipeline vuelve a aplicar `k8s/prod-secret.yaml` en cada deploy.
- Para ver que existe, sin su valor: `kubectl --context contabo-prod -n <ns> describe secret <nombre>`.
  Nunca `get -o yaml`, `-o json` ni `custom-columns` sobre `.data`.

#### Rotación (dos despliegues)

Nunca se retira una llave a la que todavía apunta algún secreto.

1. **Declarar**: variable `INTEGRATIONS_SECRET_PROTECTION_KEY_K2` en el grupo y línea
   `Integrations__SecretProtection__Keys__k2` en `prod-secret.yaml`, con `ActiveKeyId` todavía en `k1`.
   Desplegar.
2. **Activar**: `ActiveKeyId = k2` en el ConfigMap. Desplegar. `ConnectionSecretRekeyWorker` re-cifra al
   arrancar y cada `RekeyIntervalMinutes`, en lotes de 100; un `PUT` con un secreto nuevo también lo
   deja en `k2`.
3. **Contar** (sólo ids y conteos):

   ```sql
   SELECT key_id, count(*) FROM integrations.connection_secrets GROUP BY 1;
   ```

   Si quedan en `k1`, el log del worker dice cuántos se saltó (`n connection secrets could not be
   re-encrypted …`) y su línea final da `rekey finished: n re-encrypted, m skipped`. Con `m = 0` y
   filas en `k1`, es la ventana del rolling update: la corrida siguiente lo resuelve.
4. **Retirar** `k1` (variable y línea del Secret) sólo cuando el conteo da 0 en `k1`. Si la llave se
   filtró: las mismas fases y, además, pedirles a los tenants que roten sus tokens en el proveedor.

#### Orden de despliegue

1. Variable secreta `INTEGRATIONS_SECRET_PROTECTION_KEY_K1` en `Backend-prod` (y su copia en la
   bóveda). Sin ella, el pod nuevo entra en crash-loop por `SecretProtectionOptionsValidator`: es a
   propósito.
2. Backend: código, migración `InitialIntegrations`, la línea del Secret y la del ConfigMap, en el mismo
   merge. `Quotations__WhatsApp__*` se quedan: el sender global sigue hasta el spec del consumidor.
3. Frontend, después. **Rollback del backend:** el frontend muestra la tarjeta y la API responde 404;
   nada se corrompe porque ningún consumidor depende del módulo.
````

Chequeo:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
Select-String -Path README.md -Pattern "Integrations:SecretProtection:ActiveKeyId|### Integraciones \(conexiones del tenant\)|/integrations"
```

Esperado: al menos la fila de configuración, el título de la sección y la fila de la API.

- [ ] **Step 2: k8s**

En `k8s/prod-configMap.yaml`, después de `Quotations__WhatsApp__TemplateId` (`:89`):

```yaml
  # Spec 2026-10-08 (Integraciones): id (no secreto) de la llave con la que se cifran las credenciales
  # de las conexiones de cada tenant. Rotar = dos despliegues: declarar Keys__k2 con ésta todavía en
  # k1, y después cambiar ésta a k2 (README § Integraciones). La llave vive en prod-secret.yaml.
  Integrations__SecretProtection__ActiveKeyId: "k1"
```

En `k8s/prod-secret.yaml`, después de `Quotations__WhatsApp__FromNumber` (`:42`):

```yaml
  # Spec 2026-10-08 (Integraciones): llave AES-256 (32 bytes en base64) que cifra las credenciales de
  # las conexiones de cada tenant. El valor vive en la variable secreta
  # INTEGRATIONS_SECRET_PROTECTION_KEY_K1 del grupo Backend-prod, con copia en la bóveda del owner.
  # Sin ella, SecretProtectionOptionsValidator deja el pod en crash-loop, a propósito. No se edita con
  # kubectl: el pipeline vuelve a aplicar este manifiesto en cada deploy.
  Integrations__SecretProtection__Keys__k1: "#{INTEGRATIONS_SECRET_PROTECTION_KEY_K1}#"
```

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
Select-String -Path k8s/prod-configMap.yaml, k8s/prod-secret.yaml -Pattern "SecretProtection"
```

Esperado: exactamente las dos líneas nuevas de `Integrations__SecretProtection__*` (y sus comentarios); **ninguna** `Quotations__SecretProtection__*` (la fase 0 ya las quitó).

- [ ] **Step 3: `CLAUDE.md`**

Reemplaza (`:255-258`):

```markdown
- **`Conversations` no existe**, aunque los requisitos la supongan. Los trece módulos
  construidos son Audit, Authorization, Catalog, Companies, Customers, Geography, Identity,
  Notifications, Pos, Quotations, Reporting, Storage y Tenancy — cada uno con su
  `<Modulo>LayerTests.cs` en `tests/ArchitectureTests/`.
```

por:

```markdown
- **`Conversations` no existe**, aunque los requisitos la supongan. Los catorce módulos
  construidos son Audit, Authorization, Catalog, Companies, Customers, Geography, Identity,
  Integrations, Notifications, Pos, Quotations, Reporting, Storage y Tenancy — cada uno con su
  `<Modulo>LayerTests.cs` en `tests/ArchitectureTests/`.
- **Integrations no usa el `IAuditRecorder` ni el `IOutboxWriter` compartidos:** los dos están
  ligados a `TenancyDbContext`, y una segunda ligadura le robaría en silencio la auditoría o el outbox a
  Tenancy. Un módulo nuevo con auditoría atómica declara su propio puerto (`IIntegrationsAuditRecorder`,
  como `IIdentityAuditRecorder`). Y un consumidor de una conexión nunca referencia
  `Modules.Integrations.Application`: declara su puerto y el adaptador va en Bootstrapper
  (`IntegrationsLayerTests`).
```

- [ ] **Step 4: Criterio 1 del spec — Quotations no ganó nada de Zenvia por tenant**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
$before = git grep -l -i -E "zenvia|SecretProtection|ProtectedSecret" 0f61d8c -- src/Modules/Quotations | ForEach-Object { $_ -replace '^0f61d8c:', '' }
$after = git grep -l -i -E "zenvia|SecretProtection|ProtectedSecret" -- src/Modules/Quotations
Compare-Object $before $after
```

Esperado: sin salida. Las coincidencias son el sender global que ya está en `main` (`ZenviaWhatsAppSender`, `LogWhatsAppSender`, `QuotationsOptions*`, `QuotationsInfrastructureExtensions`, …) y las migraciones históricas `AddTenantWhatsAppSettings`/`DropTenantWhatsAppSettings` que la fase 0 dejó a propósito.

- [ ] **Step 5: Formato, restore bloqueado, build y modelo sin cambios pendientes**

Corre el **chequeo de formato**. Después:

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
  Where-Object { $_.CommandLine -and $_.CommandLine.Contains($B) -and $_.CommandLine -match 'Api(\.dll|\.exe)' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
dotnet restore Backend.slnx --locked-mode
dotnet build Backend.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project src/Modules/Integrations/Modules.Integrations.Infrastructure --context IntegrationsDbContext
```

Esperado: restore en verde (prueba que ningún `packages.lock.json` quedó atrás: es lo que corre el `Dockerfile`); build `0 Advertencia(s)`, `0 Errores`; `No changes have been made to the model since the last migration.`

- [ ] **Step 6: Suite completa, una sola vez, comparada por nombre contra la línea base**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
Set-Location $B
$final = Join-Path $env:TEMP "qep-integraciones-final"
Remove-Item -Recurse -Force $final -ErrorAction SilentlyContinue
dotnet test Backend.slnx --no-build --logger "trx" --results-directory $final
```

Corre en primer plano y tarda. Después, la comparación (sólo imprime lo nuevo):

```powershell
$baselineDir = Join-Path $env:TEMP "qep-integraciones-baseline"
$final = Join-Path $env:TEMP "qep-integraciones-final"
$baselineFailed = @(Get-Content -LiteralPath (Join-Path $env:TEMP "qep-integraciones-baseline-failed.txt"))
$baselineTotal = @(Get-ChildItem -LiteralPath $baselineDir -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult
}).Count
$results = @(Get-ChildItem -LiteralPath $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -LiteralPath $_.FullName -Raw
    $trx.TestRun.Results.UnitTestResult
})
$failed = @($results | Where-Object { $_.outcome -eq "Failed" } | ForEach-Object { $_.testName } | Sort-Object -Unique)
$new = @($failed | Where-Object { $baselineFailed -notcontains $_ })
"Base: {0} pruebas, {1} fallas previas" -f $baselineTotal, $baselineFailed.Count
"Final: {0} pruebas, {1} fallas" -f $results.Count, $failed.Count
"Fallas nuevas: {0}" -f $new.Count
$new
```

Esperado: el total final es el de la base más las pruebas nuevas de Integrations (del mismo orden: si la base dio miles y el final cientos, un `.trx` se perdió —revisa el `-LiteralPath`— y la comparación no vale); `Fallas nuevas: 0`. Una falla nueva fuera de Integrations, Bootstrapper, ArchitectureTests o el manejo de errores (`ApiExceptionHandler`) se compara por nombre antes de llamarla regresión; dentro de esos, es de esta rama hasta que se demuestre lo contrario. Anota las cuatro líneas en el handoff.

- [ ] **Step 7: Commit de la documentación**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones"
$Main = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Set-Location $B
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
git -C $Main status --short
git status --short
git add -- README.md CLAUDE.md k8s/prod-configMap.yaml k8s/prod-secret.yaml
$text = @'
docs(integrations): README, k8s y reglas del repositorio

Configuración, rotación de la llave y despliegue de Integraciones; la llave k1
en el Secret y su id en el ConfigMap.
'@
$msg = Join-Path $env:TEMP "qep-integraciones-commit.txt"
[IO.File]::WriteAllText($msg, $text, (New-Object Text.UTF8Encoding $false))
git commit -F $msg
git log -1 --format=%B
```

- [ ] **Step 8: Handoff y checklist de cierre**

Actualiza la tabla «Estado de los repos» y la sección «Fase 1» de `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\HANDOFF-integraciones.md` (carpeta contenedora, no es repo: no se commitea) con la rama, el último commit, los números de la Step 6 y las DECISIÓN-PENDIENTE de abajo.

- [ ] `git log --format=%B develop..HEAD | Select-String -SimpleMatch "Co-Authored-By"` no imprime nada.
- [ ] `git -C $Main status --short` sólo muestra los dos `M` de Tenancy que había al empezar.
- [ ] Ningún archivo de `qep-frontend` tocado; el contrato HTTP del spec sin cambios (`ConnectionsApiTests.AnAdminConnectsZenviaAndTheCatalogCountsIt` lo fija en camelCase).
- [ ] No hay push ni merge en este plan: lo decide el owner (`superpowers:finishing-a-development-branch`). Antes de desplegar, la variable `INTEGRATIONS_SECRET_PROTECTION_KEY_K1` tiene que existir en `Backend-prod`.

---

## Cobertura del spec

| Spec | Task |
| --- | --- |
| Catálogo en código, `Parse`, visibilidad por módulos y por stub | 2 |
| Agregado: `Create`/`Update`, secreto ausente conserva, `changedFields`, transiciones, `Reprotect`, códigos | 3 |
| Secreto en reposo: `ProtectedSecret`, `ISecretProtector`, AES-GCM con AAD nuevo, opciones y validador | 3, 4 |
| Permisos y `admin` | 5 |
| `GET /catalog`, `GET /connections`, `GET /connections/{id}` | 6, 13 |
| `POST`/`PUT` con prueba antes de guardar, tope, 503 | 7, 13 |
| `test`, `pause`, `resume`, `DELETE`, eventos de outbox | 8, 13 |
| Puertos para consumidores | 9, 14 |
| Probador de Zenvia, timeout, User-Agent, sin logs de headers | 10 |
| Tablas, `CHECK`s, índice único por nombre, UoW, auditoría atómica | 11 |
| Worker de re-cifrado | 12 |
| Mapeo central de errores (`errors` en `credentials_rejected`, 503) | 13 |
| «Nunca en un log» (criterio 3) | 10, 14 |
| `IntegrationsLayerTests`, `ConfigurationExampleTests` | 1, 4, 10, 12 |
| Despliegue: k8s, README | 15 |
| Criterios 1-6 | 15 (1), 13 (2), 14 (3), 12 (4), 7 (5), 13 (6) |

## DECISIÓN-PENDIENTE

1. **RESUELTA (owner, 2026-10-08): `ResolveAsync` con un secreto que ya no descifra devuelve `null`.** Sin registrar nada, porque Application no tiene logging. El spec del primer consumidor decidirá si además reporta `NeedsAttention` por `IConnectionHealthReporter`.
2. **RESUELTA (owner, 2026-10-08): un `POST …/test` con `credentials_rejected` sobre una `Active` la pasa a `NeedsAttention`**, con evento `connection-needs-attention.v1` y auditoría `needs_attention` (coherente con D2). `Unreachable` y los rechazos sobre `Paused` o `NeedsAttention` sólo anotan `last_failure_*`. Aplicado en `TestConnectionHandler`, `LifecycleHandlersTests` y `AFailedTestAnswers200WithTheFailureAndNeedsNoIfMatch`. Corregir la tabla de transiciones del spec.
3. **RESUELTA (owner, 2026-10-08): manda D10.** Un proveedor nuevo es su clase en el catálogo, su probador, sus pruebas y **la migración del `CHECK`**, sin cambiar handlers ni tablas. El criterio 5 se demuestra con un proveedor falso en pruebas unitarias (Task 7). Corregir el criterio 5 del spec con ese texto.
4. **RESUELTA (owner, 2026-10-08): `created_by` no retiene al usuario.** Integrations no registra `IUserReferenceProbe`, porque una conexión no es historia de negocio. Si la purga de huérfanos borra la membresía, `createdBy.displayName` sale `null` y el `memberId` se conserva. Dejarlo escrito en el spec.
5. **RESUELTA (owner, 2026-10-08): sin límite propio de pruebas por ahora.** Queda sólo el rate limit general de la API. Se revisa si aparece abuso o si Zenvia limita.

## Respuestas del owner fuera de este plan

Cambian el alcance del spec; necesitan una enmienda antes de planearse.

- **Respuesta 3 — «Sí», el operador (QCode) ve las conexiones de los tenants, sin secretos.** Falta decidir: ruta (¿bajo `/operator/tenants/{id}/integrations`?), permiso (¿`operator.tenants.read` o uno nuevo `operator.*`, con su política y el filtro de operador?), forma del DTO (¿con `fields` públicos o sólo nombre, estado y fechas?) y si leer queda auditado.
- **Respuesta 5 — aviso cuando una conexión pasa a `NeedsAttention`: se respondió sólo con una dirección de correo.** Falta decidir: ¿un destinatario fijo de QCode o los administradores del tenant (o ambos)?, la plantilla y su texto, el canal (Notifications por Infobip), en qué transiciones (reporter, `resume`, ¿`test`?) y cómo no mandar el mismo aviso dos veces.
- **Respuesta 2 — la cuenta global de Zenvia es del tenant Origen Botánico.** Es del spec del consumidor Quotations (cuando ese tenant conecte su cuenta y se retiren `Quotations__WhatsApp__*`); no toca este plan.
- **Respuesta 1 — `GET https://api.zenvia.com/v2/templates/` sirve para probar el token.** Usada en la Task 10 como `{BaseUrl}/v2/templates`, la ruta del spec; la respuesta del owner la escribe con barra final. Si el humo contra Zenvia real diera 404, ésa es la primera sospecha.

## Lo que el spec dice y el código contradice

Para corregir el spec (el código gana, `CLAUDE.md`):

- «Auditoría: camino atómico (`IAuditRecorder` …)» — el `IAuditRecorder` compartido ya está ligado a `TenancyDbContext` (`TenancyInfrastructureExtensions.cs:60`); usarlo desde Integrations le robaría la auditoría a Tenancy (`IIdentityAuditRecorder.cs:5-15`). P1.
- «Se escriben con `IOutboxWriter`» — también ligado a `TenancyDbContext` (`TenancyInfrastructureExtensions.cs:61`). P2.
- «Referencias permitidas: Application → Tenancy.Application y Audit.Application» — con el puerto propio, Application necesita `Modules.Audit.Domain` (no `Audit.Application`), y el catálogo en Domain obliga a `Domain → Modules.Tenancy.Domain`. P1, P4.
- `credentials_rejected` con `errors` y el 503 — `ApiExceptionHandler` no los soportaba (`ApiExceptionHandler.cs:57-64`, `:129-150`). P3.
- «La semilla del rol de sistema `admin` en Authorization» — `admin` es una `RoleDefinition` en código, en Bootstrapper (`QepServiceCollectionExtensions.cs:654-722`); no hay semilla ni migración.
- Worker: «Mismo comportamiento que hoy» — el de `6612298` corría una vez por arranque, una fila por scope y un log por fila saltada; el spec pide intervalo, lotes de 100 y un log por corrida. El plan sigue el spec. P16.
- «`HttpClient` registrado con `IHttpClientFactory`» — el código evita `IHttpClientFactory` a propósito en Quotations y Notifications (`QuotationsInfrastructureExtensions.cs:109-111`). El plan sigue el spec, sin paquete nuevo (FrameworkReference).
- `createdBy.displayName` — Integrations no puede resolverlo solo (el correo es de Identity); va por un adaptador en Bootstrapper. P23.
- Traslado con `git mv` «para conservar historia» — como `0f61d8c` ya borró los archivos, el commit de traslado es un alta y `git log --follow` no llega a `6612298`; la trazabilidad queda en el cuerpo del commit.
- Criterio 5 contra D10: ver DECISIÓN-PENDIENTE 3.
