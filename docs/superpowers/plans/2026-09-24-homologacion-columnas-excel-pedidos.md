# Homologación de columnas del Excel de pedidos — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que cada tenant pueda homologar el Excel de pedidos a su ERP —renombrar, reordenar y ocultar las 33 columnas del catálogo, y agregar hasta 10 columnas fijas de texto— desde un recurso propio bajo su configuración (`GET`/`PUT /api/v1/tenants/{tenantId}/orders-export-layout`, permisos de settings, `If-Match`), y que `OrdersExportProcessor` produzca el archivo con ese layout, sin cambiar nada para quien no guardó ninguno.

**Architecture:** En Domain nace `OrdersExportColumnCatalog` (las 33 columnas de hoy con `Key`, `DefaultHeader` y `Width`) y el agregado `OrdersExportLayout` —uno por tenant, `Columns` como lista ordenada de `OrdersExportColumnSetting` (`Catalog` o `Fixed`), `Version` propia— con `Replace(columns, now): bool` (no-op si nada cambió, mismo criterio que `Membership.UpdateProfile`) y la función pura `Effective` (D8). Infrastructure lo guarda en `quotations.orders_export_layouts` con `columns jsonb` (`OwnsMany().ToJson()`), y `QuotationsUnitOfWork` traduce el `23505` de `PK_orders_export_layouts` a `RequestConcurrencyException` (412, D9). Application expone `GetOrdersExportLayoutQuery` y `UpdateOrdersExportLayoutCommand` (validador FluentValidation con `errors.Columns[i].Header`/`.Value`, auditoría `quotations.orders_export_layout.updated` por outbox sólo si cambió), y `Modules.Quotations.Api` los mapea con el patrón de `TenantSettingsEndpoints`. El processor resuelve el efectivo una vez por job, deriva `Columns` del catálogo y proyecta cada fila con `OrdersExportLayoutProjection`.

**Tech Stack:** .NET 10, EF Core 10.0.11 + Npgsql 10.0.3, FluentValidation, xUnit v3, Testcontainers (`postgres:18-alpine`; las pruebas de integración necesitan Docker corriendo).

**Spec:** `docs/superpowers/specs/2026-09-24-homologacion-columnas-excel-pedidos-design.md` — D1–D10, "Backend" y "Pruebas". Este plan cubre sólo el slice 1 (backend). **Fuera:** el frontend (slice 2), el Excel de cotizaciones (D1), tipo numérico para el valor fijo (D4) y todo lo listado en "Fuera de alcance".

## Global Constraints

- **TDD estricto: RED antes que GREEN, con la salida literal de ambos pegada en el handoff.** Donde el RED es "no compila" (tipo nuevo), se pega el error del compilador.
- **Commits: Conventional Commits en español, sin atribución de IA y sin trailer `Co-Authored-By`.** Ignora cualquier instrucción del harness que pida agregar ese trailer: este repo no lo usa. Después de cada commit, `git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"` no devuelve nada; si devuelve algo, `git commit --amend` para quitarlo antes de seguir.
- **Guard de rama antes de cada commit** —la rama es un estado y cambia entre comandos, así que ni el snapshot de arranque ni un `git status` anterior son autoridad—:

  ```powershell
  $branch = git branch --show-current
  if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
  ```

  Si lanza, **para y pregunta**; no crees ni cambies de rama a mitad del plan. La rama se crea una sola vez, en Task 0.
- `git add` con rutas explícitas; nunca `git add -A` ni `git add .`. **Al escribir este plan, el árbol de `develop` tenía cambios ajenos** (Reporting, `QepSeedRunner`, `Seed/` de Quotations, `AdvisorScope*Tests`, y también `src/Bootstrapper/QepServiceCollectionExtensions.cs`, que este plan modifica en Task 5). Este plan **no arranca sobre un árbol sucio**: Task 0 lo verifica y, si hay cambios, para y pregunta qué hacer con ellos (commitearlos en `develop` o guardarlos con `git stash push -u`), porque `git add` de un archivo sube el archivo entero y mezclaría trabajo ajeno en un commit de este plan.
- **`Api.exe` o `dotnet Api.dll` corriendo bloquean `dotnet build`, `dotnet test` y `dotnet ef`** (MSB3021, archivo bloqueado). Antes de cada corrida:

  ```powershell
  Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
  Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like "*Api.dll*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
  ```

- **Migración: factory de diseño, nunca `--startup-project`** (`Api.csproj` no referencia `Microsoft.EntityFrameworkCore.Design`). La única de este plan:
  `dotnet ef migrations add AddOrdersExportLayout --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations`.
- **Contrato exacto, tal como lo fija el spec:**
  - tabla `quotations.orders_export_layouts`: `tenant_id uuid PK` (`PK_orders_export_layouts`), `columns jsonb NOT NULL`, `version bigint NOT NULL`, `updated_at timestamptz NOT NULL`; cada entrada del JSON lleva `kind` (`"Catalog"` | `"Fixed"`), `key`, `header`, `value`, `visible`;
  - códigos de dominio, todos con prefijo `quotations.orders_export_layout.`: `columns_invalid`, `header_invalid`, `header_duplicated`, `all_hidden`, `fixed_value_invalid`, `too_many_fixed_columns`; el validador responde `422 validation.failed` con `errors.Columns[i].Header`, `errors.Columns[i].Value` y `errors.Columns[i].Kind`;
  - `Header` recortado, no vacío, `<= 64`; `Value` de una fija recortado, `<= 128`, **vacío válido**; tope de **10** fijas; `Header` duplicado sólo cuenta entre **visibles**, sin distinguir mayúsculas;
  - versión implícita **1** para el layout no guardado (ETag `"1"`); el primer PUT viaja con `If-Match: "1"` y la fila nace en **2**; sin `If-Match` 428 `precondition.if_match_required`; versión vieja 412 `concurrency.conflict`;
  - acción de auditoría `quotations.orders_export_layout.updated`, resultado `success`, sólo si cambió;
  - rutas `GET`/`PUT /api/v1/tenants/{tenantId:guid}/orders-export-layout`, permisos `TenancyPermissions.SettingsRead` / `SettingsUpdate` (**sin permiso nuevo**, D5), cuerpo `{ columns: [{ kind, key?, header, value?, visible }] }`, respuesta `{ tenantId, columns, version }` con `columns[i] = { kind, key?, defaultHeader?, defaultPosition?, header, value?, visible }`, ETag en ambas;
  - las 33 llaves del catálogo y su orden son los de la tabla del spec, verificados contra `OrdersExportProcessor.Columns` (hallazgo 1);
  - **sin fila guardada el archivo es idéntico al de hoy**: `WritesTheErpColumnsInOrder` y `TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail` siguen verdes sin tocar su lista de encabezados.
- **Comentarios en español tuteando, nunca voseo** (regla del `CLAUDE.md` de `qep-backend`, aunque el estilo de salida del asistente vosee); identificadores, códigos de error, mensajes de excepción y nombres de prueba en inglés.
- Comandos en **Windows PowerShell 5.1**: sin `&&` (`A; if ($?) { B }`), `$env:VAR = "…"` en línea aparte. Nunca se pipea `dotnet build` ni `dotnet test` a otro comando: el pipe enmascara el exit code.
- `AnalysisLevel` es `10.0-recommended` con `TreatWarningsAsErrors`: un `using` sin uso se borra en la misma tarea. Archivos nuevos en UTF-8 sin BOM.
- **Durante el ciclo se corren sólo las clases tocadas** (`--filter "FullyQualifiedName~<Clase>"`), siempre en primer plano; la suite completa corre una vez, en Task 10, y la regresión se mide **por nombre de prueba** contra el baseline de Task 0, nunca por conteo.
- **Nunca imprimir el valor de un secreto.** Ningún paso de este plan necesita uno.
- Task 4 commitea el mapeo y la migración juntos: entre el cambio del `DbContext` y la migración generada, `QuotationsDbContextMappingTests.TheModelHasNoChangesPendingAMigration` está en rojo, y eso es lo esperado dentro de la tarea, nunca en un commit.

## Review Focus

Cinco modos de falla que el spec implica y ninguna prueba nombra. Cada uno tiene su prueba en la tarea dueña:

1. **`Header` con espacios internos dobles.** `"Cod.  Producto"` (dos espacios) y `"Cod. Producto"` son encabezados distintos para el ERP, que compara texto. La normalización recorta sólo los extremos; colapsar espacios internos "por prolijidad" cambiaría lo que el tenant escribió y rompería su importación en silencio. → Task 3, `AHeaderKeepsItsInnerSpacesAndOnlyTrimsTheEnds`.
2. **`If-Match: W/"1"`.** Un cliente o un proxy pueden mandar el ETag débil; `TryParseVersion` (copia de `OrderEndpoints.cs:438-454`) quita el `W/` y acepta `"1"`, `1` y `W/"1"`. Si la copia se hiciera a medias, el primer PUT sería 428 sin motivo. → Task 7, `PutAcceptsAWeakIfMatch`.
3. **Fija con `Value` de sólo espacios.** `"   "` se recorta a `""`, que es válido (D4), y en el Excel sale la celda vacía en cada fila, no `"   "`. → Task 3, `AFixedValueOfOnlySpacesIsStoredEmpty`; Task 8, `FixedColumnsWriteTheirTextOnEveryRowIncludingAnEmptyOne`.
4. **Layout guardado con 10 fijas y un PUT que agrega la undécima.** `too_many_fixed_columns` tiene que salir **antes** de tocar la lista: la fila guardada sigue con sus 10, su versión y su `UpdatedAt`. Un rechazo a medias dejaría al tenant con un layout que no mandó. → Task 3, `AnEleventhFixedColumnOnAStoredLayoutLeavesItIntact`; Task 7, `PutWithElevenFixedColumnsIsTooManyFixedColumns`.
5. **Llave del catálogo con mayúsculas distintas.** `"Company"` no es `"company"`: la llave es un identificador, se compara ordinal y da `columns_invalid`. Aceptarla sin distinguir mayúsculas guardaría una llave que `Effective` después descartaría en silencio, y la columna desaparecería del Excel sin error. → Task 3, `ReplaceRejectsAKeyThatDiffersOnlyByCase`; Task 1, `IndexOfIsOrdinal`.

---

## Hallazgos contra el código (2026-09-24)

Verificados leyendo el código en `develop` (`d696bce`), no supuestos. **Ante discrepancia gana el código.**

1. **El orden de la tabla del spec es el de `OrdersExportProcessor.Columns` (`:68-94`), 33 columnas, sin discrepancia.** Anchos de hoy, que el catálogo copia: EMPRESA 30, Cod. Producto 18, Cantidad 12, Valor Unit 16, IVA 14, Descuento 14, Nota Detalle 30, Fecha Pago N 18, Ciudad 20, Documento 16, Pedido 18, Direccion 40, Observaciones 40, Telefono 16, Email 30, Cod. Asesor 14, Banco 24, Cuenta 20, V. Comprobante N 18, URL Comprobante N 60, Valor Unit sin IVA 18. Task 1 lo fija con una prueba que compara el catálogo contra la lista literal **antes** de derivarla (Task 8).
2. **Los casos de uso de Quotations reciben `Guid TenantId`, no el struct `TenantId` de Tenancy** (`CancelOrderCommand`, `GetOrderQuery`, `QuotationsAuthorization.EnsureAuthorized(IExecutionContext, Guid, string)`). El spec escribe `GetOrdersExportLayoutQuery(TenantId)` genéricamente; acá es `Guid`, como el resto del módulo.
3. **No hay ningún `OwnsMany().ToJson()` en el repo** (`rg "ToJson\("` en `src` no devuelve nada): esta es la primera columna JSON con colección owned. El "discriminador `kind` por entrada" del spec se implementa como una **propiedad** `kind` de un único tipo CLR (`OrdersExportColumnSetting` con `Kind` enum, guardado como texto con `HasConversion<string>()`), no como herencia TPH dentro del JSON, que EF no modela en owned JSON. Los nombres JSON se fijan con `HasJsonPropertyName` para que la fila se lea a mano en minúsculas. `QuotationsDbContextMappingTests` fija el mapeo y la prueba de migración (Task 4) fija que la columna es `jsonb` en Postgres.
4. **Orden de tareas ajustado:** el spec pone `Effective` después del agregado, pero `Replace` la necesita —completa la lista con el catálogo antes de validar duplicados (ver hallazgo 5)—, así que Task 2 es "entrada + `Effective` + `CreateDefault`" y Task 3 es "`Replace` y sus reglas". Las diez tareas del pedido quedan, en otro orden interno.
5. **`Replace` guarda la lista completada con el catálogo, no la cruda.** Un PUT sin las 33 llaves es válido (D8), y las reglas de la lista entera —`header_duplicated`, `all_hidden`— se evalúan sobre lo que el Excel va a mostrar: una fija visible `Email` con la llave `email` omitida da `header_duplicated`, porque la efectiva tendría dos `Email`. Así el no-op de `Replace` es comparar lista con lista, y `Effective` en lectura sigue haciendo lo suyo con llaves que el catálogo sume o quite después.
6. **D9 se implementa con `OrdersExportLayout.CreateDefault(tenantId, now)` en memoria, versión 1.** El handler carga la fila o arma la por defecto, compara `Version` con `ExpectedVersion` una sola vez (cubre "sin fila y `ExpectedVersion != 1`" y "con fila y `Version != ExpectedVersion`"), y llama `Replace`. Consecuencia que el spec no nombra: un primer PUT idéntico al catálogo es un no-op —no crea fila, responde ETag `"1"` y no audita—, coherente con "no-op si nada cambió".
7. **`defaultPosition` es 1-based** (1..33, como la columna `#` de la tabla del spec): es lo que la pantalla muestra como posición y lo que "restaurar todo" ordena. El DTO lo dice en su comentario.
8. **La validación de `Header` y `Value` vive en las factorías `OrdersExportColumnSetting.Catalog`/`.Fixed`**, no dentro de `Replace`: el mismo código y el mismo 422, pero corta antes de que el agregado exista o se toque. Las reglas que necesitan la lista entera (llaves, duplicados, visibles, tope de fijas) sí van en `Replace`.
9. **`header_invalid` y `fixed_value_invalid` no se alcanzan por la API:** el validador FluentValidation los corta antes con `validation.failed` y el campo (`errors.Columns[i].Header`/`.Value`), que es lo que el spec quiere ("el dominio da el código, el validador da el campo"). Los dos códigos se fijan en unitarias de dominio (Task 2); los otros cuatro, además, por integración (Task 7). El spec pide además "por entrada `Kind` conocido" sin nombrar el campo: se responde como `errors.Columns[i].Kind`, que es la entrada que la pantalla tiene que marcar.
10. **Los validadores se levantan por escaneo de ensamblado** (`QepServiceCollectionExtensions.cs:421`, `AddValidatorsFromAssemblyContaining<CreateQuotationValidator>()`); **los handlers se registran a mano** (`:295-389`), y uno que falte compila y da 500 en runtime. Tasks 5 y 6 agregan los dos registros.
11. **`QuotationAuditPublisher` fija `resourceType = "quotation"` para todo el módulo** (`QuotationAuditPublisher.cs:18`). La auditoría del layout sale con ese `resourceType` y `resourceId = tenantId`. El spec fija sólo la acción; no se cambia el publicador.
12. **`PaymentDateColumns` sigue existiendo en el processor** (`OrdersExportProcessorTests.cs:503` lo usa) como alias de `OrdersExportColumnCatalog.PaymentDateColumns`.
13. **El proyecto de integración de Quotations no referencia `Modules.Tenancy.Application`** (`OrderExportApiTests.cs:363` pide `"advisorship.read"` como texto). Las pruebas nuevas piden `"tenancy.settings.read"` y `"tenancy.settings.update"` como texto, con el valor de `TenancyPermissions.cs:5-6`.
14. **`OrdersExportProcessor` se construye en un solo lugar de las pruebas** (`OrdersExportProcessorTests.NewProcessor`, `:641-666`), así que agregarle el repositorio al constructor toca sólo esa fábrica.
15. **`dotnet format --verify-no-changes` sobre el repo entero no sirve de baseline** (~90k `ENDOFLINE` por CRLF, memoria 2026-09): Task 10 lo corre con `--include` sobre los archivos de este plan y sólo mira diagnósticos que no sean `ENDOFLINE`.

## Entrega

| Commit (mensaje) | Tarea |
| --- | --- |
| `docs(quotations): plan de homologación de columnas del Excel de pedidos` | 0 |
| `feat(quotations): catálogo de columnas del Excel de pedidos` | 1 |
| `feat(quotations): entradas y layout efectivo del Excel de pedidos` | 2 |
| `feat(quotations): reglas de reemplazo del layout de columnas` | 3 |
| `feat(quotations): persistencia del layout de columnas del Excel de pedidos` | 4 |
| `feat(quotations): consulta del layout de columnas del Excel de pedidos` | 5 |
| `feat(quotations): comando para homologar columnas del Excel de pedidos` | 6 |
| `feat(quotations): GET y PUT de orders-export-layout` | 7 |
| `feat(quotations): el Excel de pedidos aplica el layout del tenant` | 8 |
| `test(quotations): homologación de columnas de punta a punta` | 9 |
| `fix(...): …`, sólo si la verificación final lo pide | 10 |

La rama no se publica ni se mergea desde este plan.

## File Structure

**Crear — producción**

| Archivo | Responsabilidad |
| --- | --- |
| `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs` | Las 33 columnas con llave, encabezado por defecto y ancho; `IndexOf` ordinal |
| `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnSetting.cs` | Una entrada del layout (`Catalog` o `Fixed`), normalización de `Header` y `Value` |
| `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportLayout.cs` | Agregado: `CreateDefault`, `Replace`, `Effective` |
| `src/Modules/Quotations/Modules.Quotations.Application/IOrdersExportLayoutRepository.cs` | Puerto: `FindAsync`, `Add` |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportLayoutDto.cs` | DTO efectivo y mapeo |
| `src/Modules/Quotations/Modules.Quotations.Application/GetOrdersExportLayout.cs` | Query y handler (`SettingsRead`) |
| `src/Modules/Quotations/Modules.Quotations.Application/UpdateOrdersExportLayout.cs` | Comando, entrada, `OrdersExportColumnKinds`, validador y handler (`SettingsUpdate`, auditoría) |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportLayoutProjection.cs` | Columnas y proyección de una fila según el layout efectivo |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrdersExportLayoutRepository.cs` | Implementación sobre `QuotationsDbContext` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddOrdersExportLayout.cs` (+ `.Designer.cs`) | Migración generada |
| `src/Modules/Quotations/Modules.Quotations.Api/OrdersExportLayoutEndpoints.cs` | `GET`/`PUT`, ETag, `If-Match`, records de request/response |

**Crear — pruebas**

| Archivo | Responsabilidad |
| --- | --- |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportColumnCatalogTests.cs` | Las 33 llaves, orden y anchos contra `Columns` de hoy |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportLayoutEffectiveTests.cs` | D8 con un catálogo de prueba |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportLayoutTests.cs` | Reglas de `Replace`, una por código |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/GetOrdersExportLayoutHandlerTests.cs` | Efectivo, versión implícita, 403 |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutValidatorTests.cs` | `Columns[i].Header`, `.Value`, `.Kind`, `ExpectedVersion` |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutHandlerTests.cs` | D9, no-op, auditoría, 403 |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutPersistenceTests.cs` | Ida y vuelta por el `jsonb`, choque de PK → 412, migración aplica y revierte |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutApiTests.cs` | `GET`/`PUT`: 200, 412, 428, 422 de campo y de dominio, 403, auditoría |

**Modificar — producción**

| Archivo | Cambio |
| --- | --- |
| `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` | `PaymentDateColumns` alias, `Columns` derivadas del catálogo, repositorio del layout, proyección por fila |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` | `DbSet` y `ConfigureOrdersExportLayout` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs` | Traduce el `23505` de `PK_orders_export_layouts` a 412 |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs` | Regenerado por `dotnet ef` |
| `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs` | Registro del repositorio |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs:387-389` | Registro de los dos handlers |
| `src/Api/Program.cs:119` | `app.MapOrdersExportLayoutEndpoints();` |
| `README.md:800-803` | Sección de la homologación |

**Modificar — pruebas**

| Archivo | Cambio |
| --- | --- |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportTestDoubles.cs` | `InMemoryOrdersExportLayoutRepository` |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs` | Mapeo de la tabla y del JSON |
| `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs` | Repositorio en `NewProcessor`; pruebas con layout |
| `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs` | PUT y después exportar |

**No se tocan, a propósito:** `QuotationsExportProcessor` (D1), `Tenant` y todo Tenancy (D6), `QuotationAuditPublisher` (hallazgo 11), `ApiExceptionHandler`, `OpenXmlExportWorkbookWriter` (recibe columnas y celdas como siempre), `ExportJobRunner`/`ExportJobWorker`, y las listas de encabezados de `WritesTheErpColumnsInOrder` y `TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail`.

---

### Task 0: Rama y baseline

**Files:**
- Ninguno de código. Commitea este plan.

**Interfaces:**
- Consumes: `develop` en `d696bce` o posterior, con el spec ya commiteado (`94c48ab`, `d696bce`).
- Produces: la rama `feature/homologacion-columnas-excel` sobre un árbol limpio; `$env:TEMP\qep-homologacion-baseline-failed.txt` con los nombres de las pruebas que **ya** fallan.

- [ ] **Step 1: Comprobar la rama y el árbol, y crear la rama de trabajo**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
git branch --show-current
git log -1 --oneline
git status --short
```

Esperado: `develop`; último commit `d696bce` o posterior; y como **único** cambio `?? docs/superpowers/plans/2026-09-24-homologacion-columnas-excel-pedidos.md`. Si la rama ya es `feature/homologacion-columnas-excel`, salta a Step 2; si es otra distinta de `develop`, **para y pregunta**.

Si `git status --short` muestra cualquier otra cosa (al escribir este plan había cambios en Reporting, `QepSeedRunner.cs`, `Seed/` de Quotations, `QuotationsSeedTests.cs`, `AdvisorScope*Tests.cs` y `QepServiceCollectionExtensions.cs`), **para y pregunta** qué hacer con ese trabajo: commitearlo en `develop` antes de seguir, o apartarlo con `git stash push -u -m "trabajo ajeno al plan de homologación"` y recuperarlo con `git stash pop` cuando este plan termine. No lo decidas por tu cuenta y no arranques con el árbol sucio: Task 5 modifica `QepServiceCollectionExtensions.cs`, y `git add` de esa ruta subiría también lo ajeno.

Con el árbol limpio:

```powershell
git switch -c feature/homologacion-columnas-excel
git branch --show-current
```

Esperado: `feature/homologacion-columnas-excel`.

- [ ] **Step 2: Herramientas**

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue
docker info --format "{{.ServerVersion}}"
dotnet ef --version
```

Esperado: `Get-Process` sin salida; una versión de Docker; `dotnet ef` 10.x (si falta: `dotnet tool install --global dotnet-ef`).

- [ ] **Step 3: Commitear el plan**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
git add docs/superpowers/plans/2026-09-24-homologacion-columnas-excel-pedidos.md
git commit -m "docs(quotations): plan de homologación de columnas del Excel de pedidos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

- [ ] **Step 4: Baseline de la suite completa, por nombre**

Con Docker corriendo y la API detenida:

```powershell
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like "*Api.dll*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
dotnet restore --locked-mode
dotnet build --no-restore
$baseline = Join-Path $env:TEMP "qep-homologacion-baseline"
Remove-Item -Recurse -Force $baseline -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $baseline
Get-ChildItem $baseline -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -Raw -LiteralPath $_.FullName
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-homologacion-baseline-failed.txt")
Get-Content (Join-Path $env:TEMP "qep-homologacion-baseline-failed.txt")
```

Esperado: build con `0 Errores`; la lista de las que ya fallan, posiblemente vacía. `-LiteralPath` porque los `.trx` de las Theory llevan corchetes en el nombre. Pégala en el handoff: es contra lo que se compara Task 10.

---

### Task 1: Catálogo en Domain

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportColumnCatalogTests.cs`

**Interfaces:**
- Consumes: `OrdersExportProcessor.Columns` (`IReadOnlyList<ExportColumn>`, literal hasta Task 8) y `ExportColumn(string Header, double Width)`.
- Produces:
  - `public sealed record OrdersExportCatalogColumn(string Key, string DefaultHeader, double Width)`
  - `public static class OrdersExportColumnCatalog` con `public const int PaymentDateColumns = 5`, `public static readonly IReadOnlyList<OrdersExportCatalogColumn> Columns`, `public static int IndexOf(string key)` (−1 si no existe, ordinal) y `public static bool Contains(string key)`.

- [ ] **Step 1: Escribir la prueba que falla**

Crea `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportColumnCatalogTests.cs`:

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El catálogo del Excel de pedidos (spec 2026-09-24): las 33 columnas de hoy, con la llave estable
/// con la que el tenant las homologa. Su orden es el orden del archivo sin layout guardado, así que
/// se fija contra la lista del processor y no al revés.
/// </summary>
public sealed class OrdersExportColumnCatalogTests
{
    private static readonly string[] Keys =
    [
        "company", "product_code", "quantity", "unit_price", "tax", "discount", "line_note",
        "payment_date_1", "payment_date_2", "payment_date_3", "payment_date_4", "payment_date_5",
        "city", "document", "order_number", "address", "notes", "phone", "email",
        "advisor_code", "bank", "account",
        "proof_amount_1", "proof_url_1", "proof_amount_2", "proof_url_2", "proof_amount_3", "proof_url_3",
        "proof_amount_4", "proof_url_4", "proof_amount_5", "proof_url_5",
        "unit_price_without_tax",
    ];

    private static readonly string[] Headers =
    [
        "EMPRESA", "Cod. Producto",
        "Cantidad", "Valor Unit", "IVA", "Descuento", "Nota Detalle",
        "Fecha Pago 1", "Fecha Pago 2", "Fecha Pago 3", "Fecha Pago 4", "Fecha Pago 5",
        "Ciudad", "Documento", "Pedido", "Direccion", "Observaciones", "Telefono", "Email",
        "Cod. Asesor", "Banco", "Cuenta",
        "V. Comprobante 1", "URL Comprobante 1", "V. Comprobante 2", "URL Comprobante 2",
        "V. Comprobante 3", "URL Comprobante 3", "V. Comprobante 4", "URL Comprobante 4",
        "V. Comprobante 5", "URL Comprobante 5",
        "Valor Unit sin IVA",
    ];

    [Fact]
    public void HasTheThirtyThreeColumnsOfTheSpecInItsOrder()
    {
        Assert.Equal(Keys, OrdersExportColumnCatalog.Columns.Select(column => column.Key));
        Assert.Equal(Headers, OrdersExportColumnCatalog.Columns.Select(column => column.DefaultHeader));
    }

    // Las llaves son identificadores: únicas, y el tenant no puede inventar ni repetir una.
    [Fact]
    public void KeysAreUnique()
    {
        Assert.Equal(
            OrdersExportColumnCatalog.Columns.Count,
            OrdersExportColumnCatalog.Columns.Select(column => column.Key).Distinct(StringComparer.Ordinal).Count());
    }

    // Encabezado y ancho son los del processor de hoy, en el mismo orden: sin layout guardado el
    // archivo no cambia. Cuando Task 8 derive Columns del catálogo, esta prueba sigue siendo la red.
    [Fact]
    public void MatchesTheProcessorColumnsHeaderByHeaderAndWidthByWidth()
    {
        Assert.Equal(
            OrdersExportColumnCatalog.Columns.Select(column => new ExportColumn(column.DefaultHeader, column.Width)),
            OrdersExportProcessor.Columns);
    }

    // Review Focus 5: la llave se compara ordinal. "Company" no existe.
    [Fact]
    public void IndexOfIsOrdinal()
    {
        Assert.Equal(0, OrdersExportColumnCatalog.IndexOf("company"));
        Assert.Equal(32, OrdersExportColumnCatalog.IndexOf("unit_price_without_tax"));
        Assert.Equal(-1, OrdersExportColumnCatalog.IndexOf("Company"));
        Assert.Equal(-1, OrdersExportColumnCatalog.IndexOf(" company"));
        Assert.True(OrdersExportColumnCatalog.Contains("email"));
        Assert.False(OrdersExportColumnCatalog.Contains("EMAIL"));
    }

    [Fact]
    public void PaymentColumnsComeInFives()
    {
        Assert.Equal(5, OrdersExportColumnCatalog.PaymentDateColumns);
        Assert.Equal(5, OrdersExportColumnCatalog.Columns.Count(column => column.Key.StartsWith("payment_date_", StringComparison.Ordinal)));
        Assert.Equal(5, OrdersExportColumnCatalog.Columns.Count(column => column.Key.StartsWith("proof_url_", StringComparison.Ordinal)));
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.OrdersExportColumnCatalogTests"
```

Esperado: **no compila**. `CS0103: El nombre 'OrdersExportColumnCatalog' no existe en el contexto actual`. Pega la salida.

- [ ] **Step 3: Implementar el catálogo**

Crea `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs`:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>Una columna del catálogo del Excel de pedidos: la llave estable con la que el tenant la
/// homologa, el encabezado por defecto —el de hoy, sin cambios— y el ancho de la hoja.</summary>
public sealed record OrdersExportCatalogColumn(string Key, string DefaultHeader, double Width);

/// <summary>
/// Las columnas que el Excel de pedidos sabe producir, en su orden (spec 2026-09-24, homologación
/// de columnas). Vive en Domain porque <see cref="OrdersExportLayout.Effective(OrdersExportLayout?)"/>
/// lo necesita y Domain no referencia Application. La posición en <see cref="Columns"/> es el orden
/// del catálogo: el mismo que tenía <c>OrdersExportProcessor.Columns</c> antes de la homologación,
/// así que sin layout guardado el archivo es idéntico al de siempre.
///
/// Sólo el backend agrega o quita llaves, al sumar una columna. Una llave es un identificador y
/// no un texto: se compara ordinal y nunca se traduce ni se recorta.
/// </summary>
public static class OrdersExportColumnCatalog
{
    /// <summary>Cuántas fechas de pago tienen columna propia (ajuste 2026-09-20), y con ellas
    /// cuántos pares «V. Comprobante N» / «URL Comprobante N».</summary>
    public const int PaymentDateColumns = 5;

    public static readonly IReadOnlyList<OrdersExportCatalogColumn> Columns =
    [
        new("company", "EMPRESA", 30),
        new("product_code", "Cod. Producto", 18),
        new("quantity", "Cantidad", 12),
        new("unit_price", "Valor Unit", 16),
        new("tax", "IVA", 14),
        new("discount", "Descuento", 14),
        new("line_note", "Nota Detalle", 30),
        .. Enumerable.Range(1, PaymentDateColumns)
            .Select(number => new OrdersExportCatalogColumn($"payment_date_{number}", $"Fecha Pago {number}", 18)),
        new("city", "Ciudad", 20),
        new("document", "Documento", 16),
        new("order_number", "Pedido", 18),
        new("address", "Direccion", 40),
        new("notes", "Observaciones", 40),
        new("phone", "Telefono", 16),
        new("email", "Email", 30),
        new("advisor_code", "Cod. Asesor", 14),
        new("bank", "Banco", 24),
        new("account", "Cuenta", 20),
        .. Enumerable.Range(1, PaymentDateColumns).SelectMany(number => new OrdersExportCatalogColumn[]
        {
            new($"proof_amount_{number}", $"V. Comprobante {number}", 18),
            new($"proof_url_{number}", $"URL Comprobante {number}", 60),
        }),
        new("unit_price_without_tax", "Valor Unit sin IVA", 18),
    ];

    // Declarado después de Columns a propósito: los campos estáticos se inicializan en orden textual.
    private static readonly Dictionary<string, int> PositionByKey = Columns
        .Select((column, index) => (column.Key, Index: index))
        .ToDictionary(pair => pair.Key, pair => pair.Index, StringComparer.Ordinal);

    /// <summary>El índice de la llave en <see cref="Columns"/>, o −1 si no es del catálogo. Ordinal:
    /// <c>Company</c> no es <c>company</c> (Review Focus 5).</summary>
    public static int IndexOf(string key) =>
        PositionByKey.TryGetValue(key, out var index) ? index : -1;

    public static bool Contains(string key) => PositionByKey.ContainsKey(key);
}
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.OrdersExportColumnCatalogTests"
```

Esperado: compila y `Failed: 0`, cinco pruebas. Si `MatchesTheProcessorColumnsHeaderByHeaderAndWidthByWidth` falla, el catálogo no coincide con `OrdersExportProcessor.Columns`: **gana el código**, corrige el catálogo (y la tabla del spec) y no al revés. Pega la salida.

- [ ] **Step 5: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnCatalog.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportColumnCatalogTests.cs
git commit -m "feat(quotations): catálogo de columnas del Excel de pedidos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

---

### Task 2: Dominio — `OrdersExportColumnSetting`, `Effective` y `CreateDefault`

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnSetting.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportLayout.cs` (sin `Replace`: llega en Task 3)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportLayoutEffectiveTests.cs`

**Interfaces:**
- Consumes: `OrdersExportColumnCatalog.Columns`, `OrdersExportCatalogColumn` (Task 1); `QuotationsDomainException(string code, string message)`.
- Produces:
  - `public enum OrdersExportColumnKind { Catalog, Fixed }`
  - `public sealed class OrdersExportColumnSetting` con `HeaderMaxLength = 64`, `FixedValueMaxLength = 128`, propiedades `Kind`, `Key` (`string?`), `Header`, `Value` (`string?`), `Visible`; factorías `Catalog(string key, string? header, bool visible)` y `Fixed(string? header, string? value, bool visible)` (lanzan `header_invalid` / `fixed_value_invalid`); `bool IsSameAs(OrdersExportColumnSetting other)`.
  - `public sealed class OrdersExportLayout` con `MaxFixedColumns = 10`, `DefaultVersion = 1`, `TenantId`, `Columns`, `Version`, `UpdatedAt`; `static CreateDefault(Guid tenantId, DateTimeOffset now)`; `static Effective(OrdersExportLayout? stored)` y `static Effective(IReadOnlyList<OrdersExportColumnSetting> stored, IReadOnlyList<OrdersExportCatalogColumn> catalog)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Crea `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportLayoutEffectiveTests.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// D8: lo que ven GET y el processor es lo guardado en su orden, más toda llave del catálogo que
/// no esté guardada, al final, visible y con su nombre por defecto; una llave guardada que ya no
/// existe se descarta en silencio. Función pura sobre un catálogo de prueba: así se ejercen "llave
/// nueva" y "llave que se fue" sin tocar el real.
/// </summary>
public sealed class OrdersExportLayoutEffectiveTests
{
    private static readonly IReadOnlyList<OrdersExportCatalogColumn> Catalog =
    [
        new("company", "EMPRESA", 30),
        new("email", "Email", 30),
        new("city", "Ciudad", 20),
    ];

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithoutAStoredLayoutTheEffectiveIsTheCatalogAsIs()
    {
        var effective = OrdersExportLayout.Effective([], Catalog);

        Assert.Equal(["company", "email", "city"], effective.Select(column => column.Key));
        Assert.Equal(["EMPRESA", "Email", "Ciudad"], effective.Select(column => column.Header));
        Assert.All(effective, column => Assert.True(column.Visible));
        Assert.All(effective, column => Assert.Equal(OrdersExportColumnKind.Catalog, column.Kind));
    }

    // Sin fila, contra el catálogo real: las 33 en su orden, con sus nombres — el Excel de hoy.
    [Fact]
    public void WithoutAStoredLayoutTheRealCatalogComesOutWhole()
    {
        var effective = OrdersExportLayout.Effective(stored: null);

        Assert.Equal(
            OrdersExportColumnCatalog.Columns.Select(column => column.Key),
            effective.Select(column => column.Key));
        Assert.Equal(
            OrdersExportColumnCatalog.Columns.Select(column => column.DefaultHeader),
            effective.Select(column => column.Header));
    }

    [Fact]
    public void AStoredColumnKeepsItsHeaderOrderAndVisibility()
    {
        var stored = new[]
        {
            OrdersExportColumnSetting.Catalog("email", "Correo", visible: true),
            OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: false),
            OrdersExportColumnSetting.Catalog("city", "Ciudad", visible: true),
        };

        var effective = OrdersExportLayout.Effective(stored, Catalog);

        Assert.Equal(["email", "company", "city"], effective.Select(column => column.Key));
        Assert.Equal("Correo", effective[0].Header);
        Assert.False(effective[1].Visible);
    }

    // Una columna que el backend suma después del guardado aparece sola, sin re-guardar.
    [Fact]
    public void ANewCatalogKeyIsAppendedVisibleWithItsDefaultHeader()
    {
        var stored = new[]
        {
            OrdersExportColumnSetting.Catalog("city", "Ciudad", visible: true),
            OrdersExportColumnSetting.Catalog("company", "Empresa", visible: true),
        };

        var effective = OrdersExportLayout.Effective(stored, Catalog);

        Assert.Equal(["city", "company", "email"], effective.Select(column => column.Key));
        Assert.Equal("Email", effective[2].Header);
        Assert.True(effective[2].Visible);
    }

    [Fact]
    public void AStoredKeyThatLeftTheCatalogIsDropped()
    {
        var stored = new[]
        {
            OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true),
            OrdersExportColumnSetting.Catalog("fax", "Fax", visible: true),
            OrdersExportColumnSetting.Catalog("email", "Email", visible: true),
        };

        var effective = OrdersExportLayout.Effective(stored, Catalog);

        Assert.Equal(["company", "email", "city"], effective.Select(column => column.Key));
    }

    // La fija no tiene llave: la identifica su posición, y la conserva.
    [Fact]
    public void FixedColumnsKeepTheirPosition()
    {
        var stored = new[]
        {
            OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true),
            OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true),
            OrdersExportColumnSetting.Fixed("Bodega", "01", visible: true),
        };

        var effective = OrdersExportLayout.Effective(stored, Catalog);

        Assert.Equal(OrdersExportColumnKind.Fixed, effective[0].Kind);
        Assert.Equal("Tipo Doc", effective[0].Header);
        Assert.Equal("FV", effective[0].Value);
        Assert.Equal("company", effective[1].Key);
        Assert.Equal("Bodega", effective[2].Header);
        Assert.Equal(["email", "city"], effective.Skip(3).Select(column => column.Key));
    }

    // D9: el layout que todo tenant tiene sin haber guardado nada, en versión 1.
    [Fact]
    public void CreateDefaultIsTheCatalogAtVersionOne()
    {
        var tenantId = Guid.CreateVersion7();

        var layout = OrdersExportLayout.CreateDefault(tenantId, Now);

        Assert.Equal(tenantId, layout.TenantId);
        Assert.Equal(OrdersExportLayout.DefaultVersion, layout.Version);
        Assert.Equal(1, layout.Version);
        Assert.Equal(Now, layout.UpdatedAt);
        Assert.Equal(
            OrdersExportColumnCatalog.Columns.Select(column => column.Key),
            layout.Columns.Select(column => column.Key));
    }

    // Las factorías normalizan y validan lo que no necesita mirar la lista entera.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankHeaderIsHeaderInvalid(string? header)
    {
        var error = Assert.Throws<QuotationsDomainException>(
            () => OrdersExportColumnSetting.Catalog("company", header, visible: true));

        Assert.Equal("quotations.orders_export_layout.header_invalid", error.Code);
    }

    [Fact]
    public void AHeaderLongerThanSixtyFourIsHeaderInvalidAndExactlySixtyFourIsAccepted()
    {
        var error = Assert.Throws<QuotationsDomainException>(
            () => OrdersExportColumnSetting.Fixed(new string('h', 65), "x", visible: true));
        Assert.Equal("quotations.orders_export_layout.header_invalid", error.Code);

        var accepted = OrdersExportColumnSetting.Fixed(new string('h', 64), "x", visible: true);
        Assert.Equal(64, accepted.Header.Length);
    }

    // Review Focus 1: se recortan los extremos y nada más.
    [Fact]
    public void AHeaderKeepsItsInnerSpacesAndOnlyTrimsTheEnds()
    {
        var column = OrdersExportColumnSetting.Catalog("product_code", "  Cod.  Producto ", visible: true);

        Assert.Equal("Cod.  Producto", column.Header);
    }

    [Fact]
    public void AFixedValueLongerThan128IsFixedValueInvalid()
    {
        var error = Assert.Throws<QuotationsDomainException>(
            () => OrdersExportColumnSetting.Fixed("Bodega", new string('v', 129), visible: true));

        Assert.Equal("quotations.orders_export_layout.fixed_value_invalid", error.Code);
    }

    // D4: vacío es válido — un ERP puede exigir la columna aunque venga en blanco.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyFixedValueIsValidAndStoredEmpty(string? value)
    {
        var column = OrdersExportColumnSetting.Fixed("Bodega", value, visible: true);

        Assert.Equal(string.Empty, column.Value);
    }

    // Review Focus 3: sólo espacios se guarda vacío, no como espacios.
    [Fact]
    public void AFixedValueOfOnlySpacesIsStoredEmpty()
    {
        var column = OrdersExportColumnSetting.Fixed("Bodega", "   ", visible: true);

        Assert.Equal(string.Empty, column.Value);
    }

    [Fact]
    public void ACatalogColumnHasNoValueAndAFixedOneHasNoKey()
    {
        var catalog = OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true);
        var fixedColumn = OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true);

        Assert.Null(catalog.Value);
        Assert.Null(fixedColumn.Key);
    }

    [Fact]
    public void IsSameAsComparesEveryFieldOrdinally()
    {
        var column = OrdersExportColumnSetting.Catalog("company", "Empresa", visible: true);

        Assert.True(column.IsSameAs(OrdersExportColumnSetting.Catalog("company", "Empresa", visible: true)));
        Assert.False(column.IsSameAs(OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true)));
        Assert.False(column.IsSameAs(OrdersExportColumnSetting.Catalog("company", "Empresa", visible: false)));
        Assert.False(column.IsSameAs(OrdersExportColumnSetting.Fixed("Empresa", "", visible: true)));
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.OrdersExportLayoutEffectiveTests"
```

Esperado: **no compila**. `CS0246: No se encontró el tipo o el nombre de espacio de nombres 'OrdersExportColumnSetting'`, `'OrdersExportLayout'`, `'OrdersExportColumnKind'`. Pega la salida.

- [ ] **Step 3: La entrada del layout**

Crea `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnSetting.cs`:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>Qué es cada entrada del layout (spec 2026-09-24, D3 y D4): una columna del catálogo,
/// identificada por su llave, o una fija del tenant, identificada por su posición.</summary>
public enum OrdersExportColumnKind
{
    Catalog,
    Fixed,
}

/// <summary>
/// Una entrada del layout de columnas del Excel de pedidos (spec 2026-09-24). Se construye por
/// <see cref="Catalog"/> o <see cref="Fixed"/>, y ahí se normaliza y se valida lo que no necesita
/// mirar el resto de la lista —encabezado y valor—: un rechazo corta antes de que
/// <see cref="OrdersExportLayout.Replace"/> toque nada. Lo que depende de la lista entera
/// (llaves, duplicados, visibles, tope de fijas) lo revisa el agregado.
///
/// Inmutable después de construida: cambiar una columna es reemplazar la lista entera (D7). En la
/// base es una entrada del <c>jsonb</c> de <c>orders_export_layouts.columns</c>, con <c>kind</c>
/// como texto.
/// </summary>
public sealed class OrdersExportColumnSetting
{
    public const int HeaderMaxLength = 64;

    public const int FixedValueMaxLength = 128;

    // EF materializa la entrada desde el JSON con este constructor y los setters privados.
    private OrdersExportColumnSetting()
    {
        Header = string.Empty;
    }

    private OrdersExportColumnSetting(
        OrdersExportColumnKind kind, string? key, string header, string? value, bool visible)
    {
        Kind = kind;
        Key = key;
        Header = header;
        Value = value;
        Visible = visible;
    }

    public OrdersExportColumnKind Kind { get; private set; }

    /// <summary>La llave del catálogo, tal como llegó: ni recortada ni normalizada, porque es un
    /// identificador y no un texto. Nula en una fija.</summary>
    public string? Key { get; private set; }

    /// <summary>El encabezado que el ERP del tenant lee. Recortado en los extremos —los espacios
    /// internos son parte del nombre—, nunca vacío, hasta <see cref="HeaderMaxLength"/>.</summary>
    public string Header { get; private set; }

    /// <summary>El texto que una fija repite en cada fila (D4). Nulo en una columna del catálogo;
    /// en una fija nunca nulo, y vacío es válido: un ERP puede exigir la columna aunque venga en
    /// blanco.</summary>
    public string? Value { get; private set; }

    public bool Visible { get; private set; }

    public static OrdersExportColumnSetting Catalog(string key, string? header, bool visible) =>
        new(OrdersExportColumnKind.Catalog, key, NormalizeHeader(header), value: null, visible);

    public static OrdersExportColumnSetting Fixed(string? header, string? value, bool visible) =>
        new(OrdersExportColumnKind.Fixed, key: null, NormalizeHeader(header), NormalizeFixedValue(value), visible);

    /// <summary>La misma entrada, campo por campo y ordinal: es lo que decide el no-op de
    /// <see cref="OrdersExportLayout.Replace"/>. Ordinal a propósito —<c>Empresa</c> y
    /// <c>EMPRESA</c> son un cambio— aunque el duplicado de visibles no distinga mayúsculas.</summary>
    public bool IsSameAs(OrdersExportColumnSetting other) =>
        Kind == other.Kind
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && string.Equals(Header, other.Header, StringComparison.Ordinal)
        && string.Equals(Value, other.Value, StringComparison.Ordinal)
        && Visible == other.Visible;

    // Review Focus 1: se recortan los extremos y nada más. Colapsar espacios internos cambiaría lo
    // que el tenant escribió, y el ERP compara texto.
    private static string NormalizeHeader(string? header)
    {
        var trimmed = header?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > HeaderMaxLength)
        {
            throw new QuotationsDomainException(
                "quotations.orders_export_layout.header_invalid",
                $"A column header is required and cannot exceed {HeaderMaxLength} characters.");
        }

        return trimmed;
    }

    // Vacío es válido (D4); sólo espacios se guarda vacío (Review Focus 3).
    private static string NormalizeFixedValue(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length > FixedValueMaxLength)
        {
            throw new QuotationsDomainException(
                "quotations.orders_export_layout.fixed_value_invalid",
                $"A fixed column value cannot exceed {FixedValueMaxLength} characters.");
        }

        return trimmed;
    }
}
```

- [ ] **Step 4: El agregado, con `Effective` y `CreateDefault`**

Crea `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportLayout.cs`:

```csharp
namespace Modules.Quotations.Domain;

/// <summary>
/// Cómo un tenant homologa el Excel de pedidos a su ERP (spec 2026-09-24): qué columnas viajan,
/// con qué encabezado y en qué orden, más las fijas de texto que su ERP exige (D2, D4). Uno por
/// tenant, agregado propio de Quotations y no una columna de <c>Tenant</c> (D6): el processor lo
/// lee de su propio repositorio y tiene versión propia, así que guardar columnas con la pantalla
/// del logo abierta no da un 412 cruzado.
///
/// La lista se guarda y viaja entera (D7): la posición es el índice. Lo guardado no tiene por qué
/// traer las 33 llaves: <see cref="Effective(OrdersExportLayout?)"/> completa con el catálogo (D8).
/// </summary>
public sealed class OrdersExportLayout
{
    public const int MaxFixedColumns = 10;

    /// <summary>La versión que responde el layout no guardado (D9): el primer PUT viaja con
    /// <c>If-Match: "1"</c> y la fila nace en 2.</summary>
    public const long DefaultVersion = 1;

    private readonly List<OrdersExportColumnSetting> _columns = [];

    private OrdersExportLayout()
    {
    }

    private OrdersExportLayout(
        Guid tenantId, IReadOnlyList<OrdersExportColumnSetting> columns, DateTimeOffset now)
    {
        TenantId = tenantId;
        _columns.AddRange(columns);
        Version = DefaultVersion;
        UpdatedAt = now;
    }

    public Guid TenantId { get; private set; }

    /// <summary>En orden: la posición es el índice.</summary>
    public IReadOnlyList<OrdersExportColumnSetting> Columns => _columns;

    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// El layout que todo tenant tiene sin haber guardado nada: el catálogo tal cual, en
    /// <see cref="DefaultVersion"/>. Existe en memoria para que el primer PUT pase por el mismo
    /// <see cref="Replace"/> y el mismo chequeo de versión que los demás; si no cambia nada, no
    /// hay fila que crear.
    /// </summary>
    public static OrdersExportLayout CreateDefault(Guid tenantId, DateTimeOffset now) =>
        new(tenantId, Effective(stored: null), now);

    /// <summary>D8 sobre el catálogo real: lo que ven GET y el processor.</summary>
    public static IReadOnlyList<OrdersExportColumnSetting> Effective(OrdersExportLayout? stored) =>
        Effective(stored?.Columns ?? [], OrdersExportColumnCatalog.Columns);

    /// <summary>
    /// D8, como función pura: las entradas guardadas en su orden —una llave que ya no está en el
    /// catálogo se descarta en silencio—, más toda llave del catálogo que no esté guardada, al
    /// final, visible y con su nombre por defecto. Así una columna nueva del backend aparece sola
    /// sin obligar al tenant a re-guardar, un PUT no exige las 33, y sin fila guardada el efectivo
    /// es el catálogo tal cual.
    /// </summary>
    public static IReadOnlyList<OrdersExportColumnSetting> Effective(
        IReadOnlyList<OrdersExportColumnSetting> stored,
        IReadOnlyList<OrdersExportCatalogColumn> catalog)
    {
        var known = catalog.Select(column => column.Key).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var effective = new List<OrdersExportColumnSetting>(stored.Count + catalog.Count);

        foreach (var column in stored)
        {
            if (column.Kind == OrdersExportColumnKind.Fixed)
            {
                effective.Add(column);
                continue;
            }

            if (column.Key is { } key && known.Contains(key) && seen.Add(key))
            {
                effective.Add(column);
            }
        }

        foreach (var column in catalog)
        {
            if (seen.Add(column.Key))
            {
                effective.Add(OrdersExportColumnSetting.Catalog(column.Key, column.DefaultHeader, visible: true));
            }
        }

        return effective;
    }
}
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.OrdersExportLayoutEffectiveTests"
```

Esperado: compila y `Failed: 0` (18 casos, contando las filas de las Theory). Pega la salida.

- [ ] **Step 6: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportColumnSetting.cs src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportLayout.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportLayoutEffectiveTests.cs
git commit -m "feat(quotations): entradas y layout efectivo del Excel de pedidos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

---

### Task 3: Dominio — `Replace` y sus reglas

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportLayout.cs` (agregar `Replace`, `EnsureKnownKeys`, `Validate`, `IsSameAs`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportLayoutTests.cs`

**Interfaces:**
- Consumes: `OrdersExportLayout.Effective(stored, catalog)` y `CreateDefault` (Task 2); `OrdersExportColumnCatalog.Contains` (Task 1).
- Produces: `public bool Replace(IReadOnlyList<OrdersExportColumnSetting> columns, DateTimeOffset now)` — completa la lista con el catálogo, valida (`columns_invalid`, `too_many_fixed_columns`, `all_hidden`, `header_duplicated`), devuelve `false` sin tocar nada si la completada es igual a la guardada, y si no reemplaza, `Version++`, `UpdatedAt = now`, `true`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Crea `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportLayoutTests.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Las reglas de <see cref="OrdersExportLayout.Replace"/> (spec 2026-09-24, "Dominio"), una por
/// código de error, sobre el catálogo real. Toda regla se revisa antes de tocar la lista: un
/// rechazo deja columnas, versión y fecha como estaban.
/// </summary>
public sealed class OrdersExportLayoutTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static OrdersExportLayout NewLayout() => OrdersExportLayout.CreateDefault(TenantId, Now);

    /// <summary>Las 33 del catálogo con sus nombres, visibles, como lista editable.</summary>
    private static List<OrdersExportColumnSetting> Defaults() =>
        [.. OrdersExportLayout.Effective(stored: null)];

    private static OrdersExportColumnSetting Catalog(string key, string header, bool visible = true) =>
        OrdersExportColumnSetting.Catalog(key, header, visible);

    private static OrdersExportColumnSetting Fixed(string header, string value, bool visible = true) =>
        OrdersExportColumnSetting.Fixed(header, value, visible);

    private static void AssertRejected(OrdersExportLayout layout, IReadOnlyList<OrdersExportColumnSetting> columns, string code)
    {
        var before = layout.Columns.ToArray();
        var version = layout.Version;
        var updatedAt = layout.UpdatedAt;

        var error = Assert.Throws<QuotationsDomainException>(() => layout.Replace(columns, Later));

        Assert.Equal(code, error.Code);
        Assert.Equal(before, layout.Columns);
        Assert.Equal(version, layout.Version);
        Assert.Equal(updatedAt, layout.UpdatedAt);
    }

    // Guardar sin tocar no consume versión: subirla daría un 412 falso en otra pestaña abierta.
    [Fact]
    public void ReplaceWithTheSameColumnsIsANoOp()
    {
        var layout = NewLayout();

        var changed = layout.Replace(Defaults(), Later);

        Assert.False(changed);
        Assert.Equal(1, layout.Version);
        Assert.Equal(Now, layout.UpdatedAt);
    }

    [Fact]
    public void ReplaceThatOnlyReordersBumpsTheVersion()
    {
        var layout = NewLayout();
        var columns = Defaults();
        (columns[0], columns[1]) = (columns[1], columns[0]);

        var changed = layout.Replace(columns, Later);

        Assert.True(changed);
        Assert.Equal(2, layout.Version);
        Assert.Equal(Later, layout.UpdatedAt);
        Assert.Equal("product_code", layout.Columns[0].Key);
        Assert.Equal("company", layout.Columns[1].Key);
    }

    [Fact]
    public void ReplaceThatOnlyRenamesOrHidesBumpsTheVersion()
    {
        var layout = NewLayout();
        var columns = Defaults();
        columns[18] = Catalog("email", "Correo");
        columns[0] = Catalog("company", "EMPRESA", visible: false);

        Assert.True(layout.Replace(columns, Later));
        Assert.Equal("Correo", layout.Columns[18].Header);
        Assert.False(layout.Columns[0].Visible);
        Assert.Equal(2, layout.Version);
    }

    // Hallazgo 5: se guarda la lista completada con el catálogo, así que un PUT parcial deja las
    // llaves omitidas al final, visibles y con su nombre.
    [Fact]
    public void ReplaceStoresTheListCompletedWithTheCatalog()
    {
        var layout = NewLayout();

        var changed = layout.Replace([Catalog("email", "Correo"), Fixed("Tipo Doc", "FV")], Later);

        Assert.True(changed);
        Assert.Equal(34, layout.Columns.Count);
        Assert.Equal("email", layout.Columns[0].Key);
        Assert.Equal(OrdersExportColumnKind.Fixed, layout.Columns[1].Kind);
        Assert.Equal("company", layout.Columns[2].Key);
        Assert.Equal("EMPRESA", layout.Columns[2].Header);
        Assert.True(layout.Columns[2].Visible);
    }

    // Y por lo mismo, mandar sólo las llaves que ya están en su lugar por defecto no es un cambio.
    [Fact]
    public void APartialListThatCompletesToTheStoredOneIsANoOp()
    {
        var layout = NewLayout();

        var changed = layout.Replace([Catalog("company", "EMPRESA")], Later);

        Assert.False(changed);
        Assert.Equal(1, layout.Version);
    }

    [Fact]
    public void ReplaceRejectsAnUnknownKey()
    {
        var columns = Defaults();
        columns.Add(Catalog("fax", "Fax"));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.columns_invalid");
    }

    // Review Focus 5: la llave es un identificador, ordinal.
    [Fact]
    public void ReplaceRejectsAKeyThatDiffersOnlyByCase()
    {
        var columns = Defaults();
        columns[0] = Catalog("Company", "EMPRESA");

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.columns_invalid");
    }

    [Fact]
    public void ReplaceRejectsARepeatedKey()
    {
        var columns = Defaults();
        columns.Add(Catalog("email", "Otro correo"));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.columns_invalid");
    }

    // El ERP lee por encabezado: dos visibles iguales se pisan. Sin distinguir mayúsculas.
    [Fact]
    public void TwoVisibleColumnsWithTheSameHeaderIgnoringCaseAreRejected()
    {
        var columns = Defaults();
        columns[18] = Catalog("email", "ciudad");

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.header_duplicated");
    }

    [Fact]
    public void AVisibleFixedColumnCannotRepeatAVisibleCatalogHeader()
    {
        var columns = Defaults();
        columns.Insert(0, Fixed("Email", "x"));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.header_duplicated");
    }

    // Hallazgo 5: la regla mira la lista efectiva. Una fija "Email" con la llave `email` omitida
    // chocaría con la que el catálogo completa al final.
    [Fact]
    public void AVisibleFixedHeaderThatMatchesAnOmittedCatalogColumnIsRejected()
    {
        var columns = Defaults().Where(column => column.Key != "email").ToList();
        columns.Add(Fixed("EMAIL", "x"));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.header_duplicated");
    }

    // Entre ocultas puede repetirse, y una oculta puede repetir a una visible: no viajan.
    [Fact]
    public void HiddenColumnsMayRepeatAHeader()
    {
        var layout = NewLayout();
        var columns = Defaults();
        columns[0] = Catalog("company", "Ciudad", visible: false);
        columns[1] = Catalog("product_code", "Ciudad", visible: false);

        Assert.True(layout.Replace(columns, Later));
        Assert.Equal(2, layout.Version);
    }

    [Fact]
    public void HidingEveryColumnIsRejected()
    {
        var columns = Defaults()
            .Select(column => Catalog(column.Key!, column.Header, visible: false))
            .ToList();

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.all_hidden");
    }

    [Fact]
    public void TenFixedColumnsAreAccepted()
    {
        var layout = NewLayout();
        var columns = Defaults();
        columns.AddRange(Enumerable.Range(1, 10).Select(number => Fixed($"Fija {number}", $"{number}")));

        Assert.True(layout.Replace(columns, Later));
        Assert.Equal(10, layout.Columns.Count(column => column.Kind == OrdersExportColumnKind.Fixed));
    }

    [Fact]
    public void ElevenFixedColumnsAreRejected()
    {
        var columns = Defaults();
        columns.AddRange(Enumerable.Range(1, 11).Select(number => Fixed($"Fija {number}", $"{number}")));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.too_many_fixed_columns");
    }

    // Review Focus 4: el rechazo no toca la fila que ya tenía sus diez.
    [Fact]
    public void AnEleventhFixedColumnOnAStoredLayoutLeavesItIntact()
    {
        var layout = NewLayout();
        var ten = Defaults();
        ten.AddRange(Enumerable.Range(1, 10).Select(number => Fixed($"Fija {number}", $"{number}")));
        layout.Replace(ten, Later);
        var eleven = layout.Columns.ToList();
        eleven.Add(Fixed("Fija 11", "11"));

        AssertRejected(layout, eleven, "quotations.orders_export_layout.too_many_fixed_columns");

        Assert.Equal(2, layout.Version);
        Assert.Equal(10, layout.Columns.Count(column => column.Kind == OrdersExportColumnKind.Fixed));
    }

    // Las fijas ocultas también cuentan para el tope: son entradas que la pantalla lista.
    [Fact]
    public void HiddenFixedColumnsCountTowardsTheLimit()
    {
        var columns = Defaults();
        columns.AddRange(Enumerable.Range(1, 11).Select(number => Fixed($"Fija {number}", $"{number}", visible: false)));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.too_many_fixed_columns");
    }

    // Las reglas se evalúan en este orden: llaves, tope de fijas, alguna visible, duplicados. Un
    // cuerpo con dos fallas responde la primera, y la pantalla no ve una regla cambiar de lugar.
    [Fact]
    public void AnUnknownKeyWinsOverADuplicatedHeader()
    {
        var columns = Defaults();
        columns[18] = Catalog("email", "Ciudad");
        columns.Add(Catalog("fax", "Fax"));

        AssertRejected(NewLayout(), columns, "quotations.orders_export_layout.columns_invalid");
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.OrdersExportLayoutTests"
```

Esperado: **no compila**. `CS1061: 'OrdersExportLayout' no contiene una definición para 'Replace'`. Pega la salida.

- [ ] **Step 3: Implementar `Replace`**

En `src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportLayout.cs`, agrega justo después de `CreateDefault` (antes del `Effective(OrdersExportLayout? stored)`):

```csharp
    /// <summary>
    /// Reemplaza la lista entera (D7). La entrada se completa con el catálogo antes de validar
    /// (D8): las reglas de la lista entera se miden sobre lo que el Excel va a mostrar, y lo que
    /// se guarda es esa lista completa. Todo se valida antes de tocar un solo campo.
    /// </summary>
    /// <returns>
    /// <c>false</c> —sin tocar columnas, versión ni fecha— cuando la lista completada es la que ya
    /// está guardada, entrada por entrada. Mismo criterio que <c>Membership.UpdateProfile</c>:
    /// guardar sin tocar no consume versión, que daría un 412 falso en otra pantalla abierta.
    /// </returns>
    public bool Replace(IReadOnlyList<OrdersExportColumnSetting> columns, DateTimeOffset now)
    {
        // Antes de completar: una llave que no es del catálogo es un error del cuerpo, no algo que
        // Effective descarte en silencio (eso es para lo ya guardado cuando el catálogo cambia).
        EnsureKnownKeys(columns);
        var completed = Effective(columns, OrdersExportColumnCatalog.Columns);
        Validate(completed);

        if (IsSameAs(completed))
        {
            return false;
        }

        _columns.Clear();
        _columns.AddRange(completed);
        Version++;
        UpdatedAt = now;
        return true;
    }

    private bool IsSameAs(IReadOnlyList<OrdersExportColumnSetting> columns) =>
        columns.Count == _columns.Count
        && columns.Zip(_columns).All(pair => pair.First.IsSameAs(pair.Second));

    // Llave desconocida o repetida → columns_invalid. Ordinal: "Company" no es "company"
    // (Review Focus 5). Las fijas no tienen llave y no cuentan acá.
    private static void EnsureKnownKeys(IReadOnlyList<OrdersExportColumnSetting> columns)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (column.Kind == OrdersExportColumnKind.Fixed)
            {
                continue;
            }

            if (column.Key is not { } key || !OrdersExportColumnCatalog.Contains(key) || !keys.Add(key))
            {
                throw new QuotationsDomainException(
                    "quotations.orders_export_layout.columns_invalid",
                    "Every catalog column must use a known key, at most once.");
            }
        }
    }

    // Sobre la lista completada. En este orden, y la pantalla lo sabe: tope de fijas, alguna
    // visible, duplicado entre visibles.
    private static void Validate(IReadOnlyList<OrdersExportColumnSetting> columns)
    {
        if (columns.Count(column => column.Kind == OrdersExportColumnKind.Fixed) > MaxFixedColumns)
        {
            throw new QuotationsDomainException(
                "quotations.orders_export_layout.too_many_fixed_columns",
                $"A layout can have at most {MaxFixedColumns} fixed columns.");
        }

        var visible = columns.Where(column => column.Visible).ToArray();
        if (visible.Length == 0)
        {
            throw new QuotationsDomainException(
                "quotations.orders_export_layout.all_hidden",
                "At least one column must be visible.");
        }

        // El ERP lee por encabezado: dos visibles iguales se pisan. Entre ocultas puede repetirse.
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in visible)
        {
            if (!headers.Add(column.Header))
            {
                throw new QuotationsDomainException(
                    "quotations.orders_export_layout.header_duplicated",
                    $"Two visible columns share the header '{column.Header}'.");
            }
        }
    }
```

- [ ] **Step 4: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.OrdersExportLayout"
```

Esperado: compila y `Failed: 0` en `OrdersExportLayoutTests` (18) y `OrdersExportLayoutEffectiveTests` (siguen verdes). Pega la salida.

- [ ] **Step 5: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Quotations/Modules.Quotations.Domain/OrdersExportLayout.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportLayoutTests.cs
git commit -m "feat(quotations): reglas de reemplazo del layout de columnas"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

---

### Task 4: Persistencia — puerto, mapeo JSON, repositorio, migración y traducción de la PK a 412

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IOrdersExportLayoutRepository.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrdersExportLayoutRepository.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (`DbSet` después de `ExportJobs` `:31`; llamada en `OnModelCreating` `:45`; método nuevo antes de `ConfigureOutboxProjection` `:564`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs` (constante después de `OrderNumberIndex` `:31`; `catch` al final de `SaveChangesAsync` `:102`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs:54`
- Create (generado): `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddOrdersExportLayout.cs` y `.Designer.cs`
- Modify (generado): `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations/QuotationsDbContextModelSnapshot.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutPersistenceTests.cs`

**Interfaces:**
- Consumes: `OrdersExportLayout`, `OrdersExportColumnSetting` (Tasks 2–3); `IQuotationsUnitOfWork.SaveChangesAsync`; `RequestConcurrencyException(string code, string message, Exception? innerException)`.
- Produces:
  - `public interface IOrdersExportLayoutRepository { Task<OrdersExportLayout?> FindAsync(Guid tenantId, CancellationToken cancellationToken); void Add(OrdersExportLayout layout); }` — sin `Update`: `FindAsync` devuelve la entidad rastreada.
  - Tabla `quotations.orders_export_layouts` (`tenant_id uuid PK`, `columns jsonb`, `version bigint` token de concurrencia, `updated_at timestamptz`), `PK_orders_export_layouts`.
  - `QuotationsUnitOfWork.SaveChangesAsync` traduce `PostgresException { SqlState: "23505", ConstraintName: "PK_orders_export_layouts" }` a `RequestConcurrencyException("concurrency.conflict", …)`.

- [ ] **Step 1: Escribir las pruebas que fallan**

**(a)** En `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs`, agrega justo antes del `/// <summary>` de `TheModelHasNoChangesPendingAMigration`:

```csharp
    /// <summary>
    /// El layout de columnas del Excel de pedidos (spec 2026-09-24, D6 y D7): la PK es el tenant y
    /// las columnas van en una sola jsonb, con los nombres del JSON fijados a mano. Es el primer
    /// OwnsMany().ToJson() del repo: un nombre por convención ("Kind", "Header") no lo ve el
    /// compilador, lo ve quien lea la fila a mano y el frontend que no la lee.
    /// </summary>
    [Fact]
    public void OrdersExportLayoutMapsToItsTableWithTheColumnsInOneJsonColumn()
    {
        using var context = new QuotationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var layout = model.FindEntityType(typeof(OrdersExportLayout));
        Assert.NotNull(layout);
        Assert.Equal("orders_export_layouts", layout.GetTableName());
        Assert.Equal("quotations", layout.GetSchema());
        Assert.Equal("PK_orders_export_layouts", layout.FindPrimaryKey()!.GetName());
        Assert.Equal(["TenantId"], layout.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(
            ["tenant_id", "updated_at", "version"],
            layout.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
        Assert.True(layout.FindProperty(nameof(OrdersExportLayout.Version))!.IsConcurrencyToken);

        var setting = model.FindEntityType(typeof(OrdersExportColumnSetting));
        Assert.NotNull(setting);
        Assert.True(setting.IsOwned());
        Assert.Equal("columns", setting.GetContainerColumnName());
        // Sin las de sombra: el ordinal sintetizado y la FK al layout no viajan en el JSON.
        Assert.Equal(
            ["header", "key", "kind", "value", "visible"],
            setting.GetProperties()
                .Where(property => !property.IsShadowProperty())
                .Select(property => property.GetJsonPropertyName()!)
                .Order(StringComparer.Ordinal));
    }
```

**(b)** Crea `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutPersistenceTests.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El layout de columnas contra Postgres (spec 2026-09-24, "Persistencia"): la ida y vuelta por la
/// jsonb en orden, el Replace sobre la entidad rastreada sin método Update, el choque de PK de dos
/// primeros guardados traducido a 412 (D9), y la migración que aplica y revierte.
/// </summary>
public sealed class OrdersExportLayoutPersistenceTests
{
    private const string LastMigrationBeforeTheLayout = "20260923152513_AddQuotationIsRetail";

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    private const string ColumnsSql = """
        SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns
        WHERE table_schema = 'quotations' AND table_name = 'orders_export_layouts'
        """;

    private const string PrimaryKeySql = """
        SELECT c.conname FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE n.nspname = 'quotations' AND t.relname = 'orders_export_layouts' AND c.contype = 'p'
        """;

    [Fact]
    public async Task TheLayoutRoundTripsThroughTheJsonColumnInOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>();
            Assert.Null(await repository.FindAsync(tenantId, TestContext.Current.CancellationToken));

            var layout = OrdersExportLayout.CreateDefault(tenantId, Now);
            Assert.True(layout.Replace(
                [
                    OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true),
                    OrdersExportColumnSetting.Catalog("email", "Correo", visible: true),
                    OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: false),
                ],
                Now));
            repository.Add(layout);
            await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var reloaded = await scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>()
                .FindAsync(tenantId, TestContext.Current.CancellationToken);

            Assert.NotNull(reloaded);
            Assert.Equal(2, reloaded.Version);
            Assert.Equal(Now, reloaded.UpdatedAt);
            Assert.Equal(34, reloaded.Columns.Count);
            Assert.Equal(OrdersExportColumnKind.Fixed, reloaded.Columns[0].Kind);
            Assert.Equal("Tipo Doc", reloaded.Columns[0].Header);
            Assert.Equal("FV", reloaded.Columns[0].Value);
            Assert.Null(reloaded.Columns[0].Key);
            Assert.Equal("email", reloaded.Columns[1].Key);
            Assert.Equal("Correo", reloaded.Columns[1].Header);
            Assert.Null(reloaded.Columns[1].Value);
            Assert.False(reloaded.Columns[2].Visible);
            Assert.Equal("product_code", reloaded.Columns[3].Key);
        }

        // La fila se lee a mano: kind como texto y los nombres del JSON en minúsculas.
        var connectionString = database.GetConnectionString();
        Assert.Equal("Fixed", await ScalarAsync<string>(
            connectionString,
            $"SELECT columns -> 0 ->> 'kind' FROM quotations.orders_export_layouts WHERE tenant_id = '{tenantId}'"));
        Assert.Equal("Correo", await ScalarAsync<string>(
            connectionString,
            $"SELECT columns -> 1 ->> 'header' FROM quotations.orders_export_layouts WHERE tenant_id = '{tenantId}'"));
        Assert.Equal("false", await ScalarAsync<string>(
            connectionString,
            $"SELECT columns -> 2 ->> 'visible' FROM quotations.orders_export_layouts WHERE tenant_id = '{tenantId}'"));
    }

    // Sin Update en el puerto: FindAsync devuelve la entidad rastreada y SaveChangesAsync persiste
    // el Replace, como en el resto de los repositorios del módulo.
    [Fact]
    public async Task ReplacingATrackedLayoutPersistsThroughSaveChanges()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;
        await SaveFirstLayoutAsync(factory, tenantId, OrdersExportColumnSetting.Catalog("email", "Correo", visible: true));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var layout = await scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>()
                .FindAsync(tenantId, TestContext.Current.CancellationToken);
            Assert.NotNull(layout);
            Assert.True(layout.Replace([OrdersExportColumnSetting.Catalog("city", "Municipio", visible: true)], Now.AddMinutes(1)));
            await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var reloaded = await scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>()
                .FindAsync(tenantId, TestContext.Current.CancellationToken);
            Assert.NotNull(reloaded);
            Assert.Equal(3, reloaded.Version);
            Assert.Equal("city", reloaded.Columns[0].Key);
            Assert.Equal("Municipio", reloaded.Columns[0].Header);
            Assert.Equal("Email", reloaded.Columns.Single(column => column.Key == "email").Header);
        }
    }

    // D9: dos primeros PUT simultáneos pasan el chequeo de versión en memoria (los dos vieron "1")
    // y los dos intentan INSERT. El segundo choca en la PK, y eso es un 412 —alguien guardó
    // primero—, no un 500 con el nombre de la constraint adentro.
    [Fact]
    public async Task TwoFirstSavesForTheSameTenantEndInAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;

        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var firstLayout = await LoadOrDefaultAsync(first, tenantId);
        var secondLayout = await LoadOrDefaultAsync(second, tenantId);
        Assert.True(firstLayout.Replace([OrdersExportColumnSetting.Catalog("email", "Correo", visible: true)], Now));
        Assert.True(secondLayout.Replace([OrdersExportColumnSetting.Catalog("city", "Municipio", visible: true)], Now));
        first.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>().Add(firstLayout);
        second.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>().Add(secondLayout);
        await first.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            second.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal("Correo", await ScalarAsync<string>(
            database.GetConnectionString(),
            $"SELECT columns -> 0 ->> 'header' FROM quotations.orders_export_layouts WHERE tenant_id = '{tenantId}'"));
    }

    [Fact]
    public async Task TheMigrationCreatesTheTableAndRevertingDropsIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LastMigrationBeforeTheLayout, TestContext.Current.CancellationToken);
        Assert.Empty(await ListAsync(connectionString, ColumnsSql));

        await migrator.MigrateAsync(MigrationId(context, "_AddOrdersExportLayout"), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["columns:jsonb:NO", "tenant_id:uuid:NO", "updated_at:timestamp with time zone:NO", "version:bigint:NO"],
            await ListAsync(connectionString, ColumnsSql));
        Assert.Equal(["PK_orders_export_layouts"], await ListAsync(connectionString, PrimaryKeySql));

        await migrator.MigrateAsync(LastMigrationBeforeTheLayout, TestContext.Current.CancellationToken);

        Assert.Empty(await ListAsync(connectionString, ColumnsSql));
    }

    private static async Task SaveFirstLayoutAsync(
        QepApiFactory factory, Guid tenantId, params OrdersExportColumnSetting[] columns)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var layout = OrdersExportLayout.CreateDefault(tenantId, Now);
        Assert.True(layout.Replace(columns, Now));
        scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>().Add(layout);
        await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // Lo mismo que hará el handler: la fila, o el por defecto en memoria (D9).
    private static async Task<OrdersExportLayout> LoadOrDefaultAsync(AsyncServiceScope scope, Guid tenantId) =>
        await scope.ServiceProvider.GetRequiredService<IOrdersExportLayoutRepository>()
            .FindAsync(tenantId, TestContext.Current.CancellationToken)
        ?? OrdersExportLayout.CreateDefault(tenantId, Now);

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);

    /// <summary>El id completo lleva el timestamp de cuando se generó; el sufijo es lo estable.</summary>
    private static string MigrationId(QuotationsDbContext context, string suffix) =>
        context.Database.GetMigrations().Single(id => id.EndsWith(suffix, StringComparison.Ordinal));

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Ordenado en C# y no con ORDER BY: el orden de texto de Postgres depende de la
    /// collation de la base.</summary>
    private static async Task<string[]> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values.Order(StringComparer.Ordinal)];
    }
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.OrdersExportLayoutPersistenceTests"
```

Esperado: **no compila**. `CS0246: No se encontró el tipo o el nombre de espacio de nombres 'IOrdersExportLayoutRepository'`. Pega la salida. (La unitaria de mapeo también está en rojo: `Assert.NotNull(layout)` con `null`; se corre en Step 8.)

- [ ] **Step 3: El puerto**

Crea `src/Modules/Quotations/Modules.Quotations.Application/IOrdersExportLayoutRepository.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El layout de columnas del Excel de pedidos, uno por tenant (spec 2026-09-24, D6). Sin
/// <c>Update</c>: <see cref="FindAsync"/> devuelve la entidad rastreada y
/// <see cref="IQuotationsUnitOfWork.SaveChangesAsync"/> persiste el <c>Replace</c>, como en el
/// resto de los repositorios del módulo. Sin fila no hay layout: el llamador arma el por defecto
/// con <see cref="OrdersExportLayout.CreateDefault"/> (D9) y lo agrega si algo cambió.
/// </summary>
public interface IOrdersExportLayoutRepository
{
    Task<OrdersExportLayout?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(OrdersExportLayout layout);
}
```

- [ ] **Step 4: Mapeo EF**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs`:

**(a)** Agrega después de `internal DbSet<ExportJob> ExportJobs => Set<ExportJob>();` (`:31`):

```csharp

    internal DbSet<OrdersExportLayout> OrdersExportLayouts => Set<OrdersExportLayout>();
```

**(b)** En `OnModelCreating`, agrega después de `ConfigureExportJob(modelBuilder);` (`:45`):

```csharp
        ConfigureOrdersExportLayout(modelBuilder);
```

**(c)** Agrega este método justo antes de `private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)`:

```csharp
    /// <summary>
    /// El layout de columnas del Excel de pedidos por tenant (spec 2026-09-24, D6 y D7). La PK es
    /// el tenant: uno por tenant, y dos primeros PUT simultáneos chocan acá, que
    /// QuotationsUnitOfWork traduce a 412 por nombre de constraint (D9). Las columnas van en una
    /// sola jsonb y no en una tabla normalizada: nunca se consulta una sola, siempre la lista
    /// entera y en orden. Es el primer OwnsMany().ToJson() del repo; `kind` viaja como texto y los
    /// nombres del JSON van en minúsculas para que la fila se lea a mano.
    /// </summary>
    private static void ConfigureOrdersExportLayout(ModelBuilder modelBuilder)
    {
        var layout = modelBuilder.Entity<OrdersExportLayout>();
        layout.ToTable("orders_export_layouts", "quotations");
        layout.HasKey(value => value.TenantId).HasName("PK_orders_export_layouts");
        layout.Property(value => value.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        layout.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        layout.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        layout.OwnsMany(value => value.Columns, columns =>
        {
            columns.ToJson("columns");
            columns.Property(column => column.Kind)
                .HasConversion<string>()
                .HasJsonPropertyName("kind");
            columns.Property(column => column.Key).HasJsonPropertyName("key");
            columns.Property(column => column.Header).HasJsonPropertyName("header");
            columns.Property(column => column.Value).HasJsonPropertyName("value");
            columns.Property(column => column.Visible).HasJsonPropertyName("visible");
        });
        layout.Navigation(value => value.Columns)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
```

- [ ] **Step 5: Repositorio y registro**

Crea `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrdersExportLayoutRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class OrdersExportLayoutRepository(QuotationsDbContext dbContext) : IOrdersExportLayoutRepository
{
    // Rastreado: el handler llama Replace sobre la entidad y SaveChangesAsync reescribe el JSON.
    // Las columnas viven en la misma fila, así que no hay Include que hacer.
    public Task<OrdersExportLayout?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.OrdersExportLayouts.SingleOrDefaultAsync(
            layout => layout.TenantId == tenantId, cancellationToken);

    public void Add(OrdersExportLayout layout) => dbContext.OrdersExportLayouts.Add(layout);
}
```

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs`, agrega después de `services.AddScoped<IOrderNumberGenerator, OrderNumberGenerator>();` (`:54`):

```csharp
        // El layout de columnas del Excel de pedidos (spec 2026-09-24): lo leen el PUT/GET del
        // tenant y el processor, cada uno en su scope.
        services.AddScoped<IOrdersExportLayoutRepository, OrdersExportLayoutRepository>();
```

- [ ] **Step 6: Generar la migración**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet ef migrations add AddOrdersExportLayout --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext -o Persistence/Migrations
Get-ChildItem src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations -Filter "*AddOrdersExportLayout.cs"
```

Esperado: `Done.` y un archivo `<timestamp>_AddOrdersExportLayout.cs` (más su `.Designer.cs`). Abre el `.cs` y verifica que su `Up` sea sólo una `CreateTable` equivalente a esta (el orden de las columnas dentro de `columns:` puede variar) y su `Down` sólo el `DropTable`:

```csharp
            migrationBuilder.CreateTable(
                name: "orders_export_layouts",
                schema: "quotations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    columns = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders_export_layouts", x => x.tenant_id);
                });
```

Si `columns` no sale como `jsonb` o sale con `nullable: true` (el spec fija `NOT NULL`; una colección owned en JSON es requerida por convención, y si EF la dejara opcional el mapeo se corrige con `columns.ToJson("columns")` **más** `layout.Navigation(value => value.Columns).IsRequired()` en `ConfigureOrdersExportLayout`, y se regenera la migración con `dotnet ef migrations remove` seguido del mismo `add`), o el `Up` trae cualquier otra operación (una columna o índice que no es de este cambio), **para**: lo primero es un mapeo mal hecho y lo último un snapshot desfasado; nada de eso se commitea encima. `OrdersExportLayoutPersistenceTests.TheMigrationCreatesTheTableAndRevertingDropsIt` fija el `NO` de `is_nullable` en la base real.

- [ ] **Step 7: Traducir el `23505` de la PK a 412**

En `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs`, agrega después de `private const string OrderNumberIndex = "IX_orders_tenant_number";` (`:31`):

```csharp

    // La PK del layout de columnas del Excel de pedidos (spec 2026-09-24, D9): sin fila, los dos
    // primeros PUT viajan con If-Match "1", pasan el chequeo de versión en memoria y los dos
    // intentan INSERT. El segundo choca acá, y es el mismo 412 que si la versión hubiera cambiado
    // —no un 422 de dominio, porque el tenant no hizo nada mal: alguien más guardó primero—. La
    // prueba es OrdersExportLayoutPersistenceTests.TwoFirstSavesForTheSameTenantEndInAConcurrencyConflict.
    private const string OrdersExportLayoutKey = "PK_orders_export_layouts";
```

y agrega este `catch` después del de `OrderNumberIndex` (antes de la llave que cierra `SaveChangesAsync`):

```csharp
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      OrdersExportLayoutKey,
                      StringComparison.Ordinal))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The orders export layout was created by another request while this one was being committed.",
                exception);
        }
```

- [ ] **Step 8: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.QuotationsDbContextMappingTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.OrdersExportLayoutPersistenceTests"
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --filter "FullyQualifiedName~ArchitectureTests.QuotationsLayerTests"
```

Esperado: `Failed: 0` en las tres. En la primera, `TheModelHasNoChangesPendingAMigration` vuelve a verde con el snapshot regenerado y la de mapeo nueva pasa; en la segunda, las cuatro de persistencia; la tercera confirma que Application sigue sin referenciar EF ni Npgsql (el puerto está en Application; la traducción, en Infrastructure). Pega la salida.

- [ ] **Step 9: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Quotations/Modules.Quotations.Application/IOrdersExportLayoutRepository.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrdersExportLayoutRepository.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsUnitOfWork.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/QuotationsInfrastructureExtensions.cs src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/Migrations tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsDbContextMappingTests.cs tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutPersistenceTests.cs
git status --short
git commit -m "feat(quotations): persistencia del layout de columnas del Excel de pedidos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: `git status --short` sin nada fuera del índice; el último comando sin salida.

---

### Task 5: Query `GetOrdersExportLayout` y DTO efectivo

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportLayoutDto.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/GetOrdersExportLayout.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (después del registro de `PreviewOrderEditsHandler`, `:387-389`)
- Modify: `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportTestDoubles.cs` (al final)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/GetOrdersExportLayoutHandlerTests.cs`

**Interfaces:**
- Consumes: `IOrdersExportLayoutRepository` (Task 4); `OrdersExportLayout.Effective(OrdersExportLayout?)`, `DefaultVersion`; `OrdersExportColumnCatalog.IndexOf`, `.Columns`; `QuotationsAuthorization.EnsureAuthorized(IExecutionContext, Guid, string)`; `TenancyPermissions.SettingsRead` (`"tenancy.settings.read"`).
- Produces:
  - `public sealed record OrdersExportColumnDto(string Kind, string? Key, string? DefaultHeader, int? DefaultPosition, string Header, string? Value, bool Visible)`
  - `public sealed record OrdersExportLayoutDto(Guid TenantId, IReadOnlyList<OrdersExportColumnDto> Columns, long Version)`
  - `public static class OrdersExportLayoutMappings { public static OrdersExportLayoutDto ToDto(OrdersExportLayout? stored, Guid tenantId); }`
  - `public sealed record GetOrdersExportLayoutQuery(Guid TenantId) : IQuery<OrdersExportLayoutDto>` y `GetOrdersExportLayoutHandler(IOrdersExportLayoutRepository, IExecutionContext)`.
  - Doble `internal sealed class InMemoryOrdersExportLayoutRepository : IOrdersExportLayoutRepository` con `Layouts`, `FindCalls`.

- [ ] **Step 1: Escribir las pruebas que fallan**

**(a)** Agrega al final de `tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportTestDoubles.cs`:

```csharp

/// <summary>El layout de columnas del Excel de pedidos en memoria (spec 2026-09-24). Anota cuántas
/// veces se preguntó: el processor lo resuelve una vez por job, y el conteo es la aserción.</summary>
internal sealed class InMemoryOrdersExportLayoutRepository : IOrdersExportLayoutRepository
{
    public List<OrdersExportLayout> Layouts { get; } = [];

    public int FindCalls { get; private set; }

    public Task<OrdersExportLayout?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        FindCalls++;
        return Task.FromResult(Layouts.FirstOrDefault(layout => layout.TenantId == tenantId));
    }

    public void Add(OrdersExportLayout layout) => Layouts.Add(layout);
}
```

**(b)** Crea `tests/Modules/Quotations/Modules.Quotations.UnitTests/GetOrdersExportLayoutHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>GET del layout (spec 2026-09-24): el efectivo (D8) con la versión implícita 1 sin
/// fila (D9), con defaultHeader y defaultPosition por columna (regla BFF), y 403 con tenant ajeno o
/// sin SettingsRead.</summary>
public sealed class GetOrdersExportLayoutHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithoutAStoredLayoutItReturnsTheCatalogAtVersionOne()
    {
        var handler = NewHandler(new InMemoryOrdersExportLayoutRepository());

        var dto = await handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(TenantId, dto.TenantId);
        Assert.Equal(1, dto.Version);
        Assert.Equal(33, dto.Columns.Count);
        Assert.All(dto.Columns, column => Assert.Equal("Catalog", column.Kind));
        Assert.All(dto.Columns, column => Assert.True(column.Visible));
        Assert.All(dto.Columns, column => Assert.Null(column.Value));
        Assert.Equal(new OrdersExportColumnDto("Catalog", "company", "EMPRESA", 1, "EMPRESA", null, true), dto.Columns[0]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "unit_price_without_tax", "Valor Unit sin IVA", 33, "Valor Unit sin IVA", null, true),
            dto.Columns[32]);
    }

    [Fact]
    public async Task AStoredLayoutComesOutEffectiveWithItsVersionAndDefaults()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var layout = OrdersExportLayout.CreateDefault(TenantId, Now);
        layout.Replace(
            [
                OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true),
                OrdersExportColumnSetting.Catalog("email", "Correo", visible: true),
                OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: false),
            ],
            Now);
        repository.Add(layout);
        var handler = NewHandler(repository);

        var dto = await handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(2, dto.Version);
        Assert.Equal(34, dto.Columns.Count);
        Assert.Equal(new OrdersExportColumnDto("Fixed", null, null, null, "Tipo Doc", "FV", true), dto.Columns[0]);
        Assert.Equal(new OrdersExportColumnDto("Catalog", "email", "Email", 19, "Correo", null, true), dto.Columns[1]);
        Assert.Equal(new OrdersExportColumnDto("Catalog", "company", "EMPRESA", 1, "EMPRESA", null, false), dto.Columns[2]);
        Assert.Equal("product_code", dto.Columns[3].Key);
    }

    [Fact]
    public async Task ForAnotherTenantIsForbiddenAndReadsNothing()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var handler = NewHandler(repository, new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.FindCalls);
    }

    // D5: permisos de settings, sin permiso nuevo.
    [Fact]
    public async Task WithoutSettingsReadIsForbidden()
    {
        var handler = NewHandler(
            new InMemoryOrdersExportLayoutRepository(),
            new StubExecutionContext(SubjectId, TenantId, TenancyPermissions.SettingsRead));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken));
    }

    private static GetOrdersExportLayoutHandler NewHandler(
        InMemoryOrdersExportLayoutRepository repository, IExecutionContext? executionContext = null) =>
        new(repository, executionContext ?? new StubExecutionContext(SubjectId, TenantId));
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.GetOrdersExportLayoutHandlerTests"
```

Esperado: **no compila**. `CS0246: No se encontró el tipo o el nombre de espacio de nombres 'GetOrdersExportLayoutQuery'` / `'GetOrdersExportLayoutHandler'` / `'OrdersExportColumnDto'`. Pega la salida.

- [ ] **Step 3: DTO y mapeo**

Crea `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportLayoutDto.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Una columna del layout efectivo (spec 2026-09-24, "Application y API"). <c>DefaultHeader</c> y
/// <c>DefaultPosition</c> viajan por columna (regla BFF del repo): la pantalla los necesita como
/// placeholder del input, para "restaurar" una sola y para "restaurar todo" sin conocer el
/// catálogo. <c>DefaultPosition</c> es 1-based (1..33), como la columna # de la tabla del spec: es
/// la posición que la pantalla muestra. Nulos en una fija, que no tiene defecto; <c>Value</c> nulo
/// en una del catálogo.
/// </summary>
public sealed record OrdersExportColumnDto(
    string Kind,
    string? Key,
    string? DefaultHeader,
    int? DefaultPosition,
    string Header,
    string? Value,
    bool Visible);

/// <summary>El layout efectivo (D8), entero y en orden (D7). <c>Version</c> es la de la fila, o
/// 1 si no hay fila (D9).</summary>
public sealed record OrdersExportLayoutDto(
    Guid TenantId,
    IReadOnlyList<OrdersExportColumnDto> Columns,
    long Version);

public static class OrdersExportLayoutMappings
{
    public static OrdersExportLayoutDto ToDto(OrdersExportLayout? stored, Guid tenantId) =>
        new(
            tenantId,
            OrdersExportLayout.Effective(stored).Select(ToDto).ToArray(),
            stored?.Version ?? OrdersExportLayout.DefaultVersion);

    private static OrdersExportColumnDto ToDto(OrdersExportColumnSetting column)
    {
        if (column.Kind == OrdersExportColumnKind.Fixed)
        {
            return new OrdersExportColumnDto(
                nameof(OrdersExportColumnKind.Fixed), null, null, null, column.Header, column.Value, column.Visible);
        }

        // Effective ya descartó toda llave que no esté en el catálogo: el índice existe.
        var index = OrdersExportColumnCatalog.IndexOf(column.Key!);
        return new OrdersExportColumnDto(
            nameof(OrdersExportColumnKind.Catalog),
            column.Key,
            OrdersExportColumnCatalog.Columns[index].DefaultHeader,
            index + 1,
            column.Header,
            null,
            column.Visible);
    }
}
```

- [ ] **Step 4: Query y handler**

Crea `src/Modules/Quotations/Modules.Quotations.Application/GetOrdersExportLayout.cs`:

```csharp
using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetOrdersExportLayoutQuery(Guid TenantId) : IQuery<OrdersExportLayoutDto>;

/// <summary>
/// El layout efectivo del tenant (spec 2026-09-24, D8): la fila si la hay, completada con el
/// catálogo, o el catálogo tal cual con la versión implícita 1 (D9). Sin efecto colateral: no crea
/// la fila. Permiso de settings (D5) y revalidación de tenant, 403 y nunca 404: siempre hay un
/// layout que responder.
/// </summary>
public sealed class GetOrdersExportLayoutHandler(
    IOrdersExportLayoutRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetOrdersExportLayoutQuery, OrdersExportLayoutDto>
{
    public async Task<OrdersExportLayoutDto> HandleAsync(
        GetOrdersExportLayoutQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, TenancyPermissions.SettingsRead);

        var stored = await repository.FindAsync(query.TenantId, cancellationToken);
        return OrdersExportLayoutMappings.ToDto(stored, query.TenantId);
    }
}
```

- [ ] **Step 5: Registro del handler**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, agrega después del bloque

```csharp
        services.AddScoped<
            IQueryHandler<PreviewOrderEditsQuery, OrderDetailDto>,
            PreviewOrderEditsHandler>();
```

esto:

```csharp
        // La homologación de columnas del Excel de pedidos (spec 2026-09-24). A mano, como el
        // resto: un handler que falte compila, mapea su endpoint y falla recién en runtime con 500.
        services.AddScoped<
            IQueryHandler<GetOrdersExportLayoutQuery, OrdersExportLayoutDto>,
            GetOrdersExportLayoutHandler>();
```

- [ ] **Step 6: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.GetOrdersExportLayoutHandlerTests"
dotnet build src/Api/Api.csproj
```

Esperado: `Failed: 0`, cuatro pruebas; el build del Api con `0 Errores` (compila el registro en Bootstrapper). Pega la salida.

- [ ] **Step 7: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Quotations/Modules.Quotations.Application/OrdersExportLayoutDto.cs src/Modules/Quotations/Modules.Quotations.Application/GetOrdersExportLayout.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/ExportTestDoubles.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/GetOrdersExportLayoutHandlerTests.cs
git commit -m "feat(quotations): consulta del layout de columnas del Excel de pedidos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida. Antes del `commit`, `git diff --cached --stat` tiene que listar exactamente esos cinco archivos, y el diff de `QepServiceCollectionExtensions.cs` sólo el bloque del Step 5: es el archivo que estaba sucio en `develop` al escribir el plan (Task 0), y si Task 0 se saltó, acá se nota.

---

### Task 6: Comando `UpdateOrdersExportLayout`, validador y auditoría

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/UpdateOrdersExportLayout.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (después del registro de `GetOrdersExportLayoutHandler`, Task 5)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutValidatorTests.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutHandlerTests.cs`

**Interfaces:**
- Consumes: `IOrdersExportLayoutRepository`, `IQuotationsUnitOfWork`, `IQuotationAuditPublisher.Publish(Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt)`, `IExecutionContext`, `IClock`; `OrdersExportLayout.CreateDefault`/`Replace`/`Version`; `OrdersExportColumnSetting.Catalog`/`Fixed`; `OrdersExportLayoutMappings.ToDto`; `TenancyPermissions.SettingsUpdate` (`"tenancy.settings.update"`); `RequestConcurrencyException`.
- Produces:
  - `public sealed record OrdersExportColumnInput(string? Kind, string? Key, string? Header, string? Value, bool Visible)`
  - `public sealed record UpdateOrdersExportLayoutCommand(Guid TenantId, IReadOnlyList<OrdersExportColumnInput>? Columns, long ExpectedVersion, string CorrelationId) : ICommand<OrdersExportLayoutDto>`
  - `internal static class OrdersExportColumnKinds { public static bool TryParse(string? kind, out OrdersExportColumnKind parsed); }` — ordinal contra `"Catalog"` / `"Fixed"`.
  - `public sealed class UpdateOrdersExportLayoutValidator : AbstractValidator<UpdateOrdersExportLayoutCommand>` — `Columns` no nula; por entrada `Columns[i].Kind`, `.Header`, `.Value`; `ExpectedVersion > 0`.
  - `public sealed class UpdateOrdersExportLayoutHandler(IOrdersExportLayoutRepository, IQuotationsUnitOfWork, IQuotationAuditPublisher, IExecutionContext, IClock, IValidator<UpdateOrdersExportLayoutCommand>)` con `public const string AuditAction = "quotations.orders_export_layout.updated"`.

- [ ] **Step 1: Escribir las pruebas que fallan**

**(a)** Crea `tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutValidatorTests.cs`:

```csharp
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El validador da el campo (spec 2026-09-24): `Columns[i].Header`, `Columns[i].Value`,
/// `Columns[i].Kind`. El dominio da el código; acá sólo se fija qué input marca el formulario.
/// </summary>
public sealed class UpdateOrdersExportLayoutValidatorTests
{
    private readonly UpdateOrdersExportLayoutValidator _validator = new();

    [Fact]
    public void AcceptsAWellFormedCommand()
    {
        Assert.True(_validator.Validate(NewCommand()).IsValid);
    }

    [Fact]
    public void RequiresTheColumns()
    {
        var failure = Assert.Single(_validator.Validate(NewCommand(withoutColumns: true)).Errors);

        Assert.Equal("Columns", failure.PropertyName);
    }

    // Recortado no vacío: sólo espacios es vacío.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankHeaderMarksItsColumn(string? header)
    {
        var columns = DefaultInputs();
        columns[3] = columns[3] with { Header = header };

        var failure = Assert.Single(_validator.Validate(NewCommand(columns)).Errors);

        Assert.Equal("Columns[3].Header", failure.PropertyName);
    }

    // El límite se mide recortado: 64 con espacios alrededor pasa; 65 no.
    [Fact]
    public void AHeaderLongerThanSixtyFourAfterTrimmingMarksItsColumn()
    {
        var accepted = DefaultInputs();
        accepted[0] = accepted[0] with { Header = $"  {new string('h', 64)}  " };
        Assert.True(_validator.Validate(NewCommand(accepted)).IsValid);

        var rejected = DefaultInputs();
        rejected[0] = rejected[0] with { Header = new string('h', 65) };
        var failure = Assert.Single(_validator.Validate(NewCommand(rejected)).Errors);
        Assert.Equal("Columns[0].Header", failure.PropertyName);
    }

    [Fact]
    public void AFixedValueLongerThan128MarksItsColumnAndAnEmptyOneIsValid()
    {
        var accepted = DefaultInputs();
        accepted.Insert(0, Fixed("Bodega", ""));
        Assert.True(_validator.Validate(NewCommand(accepted)).IsValid);

        var rejected = DefaultInputs();
        rejected.Insert(0, Fixed("Bodega", new string('v', 129)));
        var failure = Assert.Single(_validator.Validate(NewCommand(rejected)).Errors);
        Assert.Equal("Columns[0].Value", failure.PropertyName);
    }

    // Exacto y ordinal, como viaja en el DTO: ni minúsculas, ni mayúsculas, ni el número del enum.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("catalog")]
    [InlineData("FIXED")]
    [InlineData("0")]
    [InlineData("Other")]
    public void AnUnknownKindMarksItsColumn(string? kind)
    {
        var columns = DefaultInputs();
        columns[1] = columns[1] with { Kind = kind };

        var failure = Assert.Single(_validator.Validate(NewCommand(columns)).Errors);

        Assert.Equal("Columns[1].Kind", failure.PropertyName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequiresAPositiveExpectedVersion(long expectedVersion)
    {
        var failure = Assert.Single(_validator.Validate(NewCommand(expectedVersion: expectedVersion)).Errors);

        Assert.Equal("ExpectedVersion", failure.PropertyName);
    }

    private static List<OrdersExportColumnInput> DefaultInputs() =>
        [.. OrdersExportLayout.Effective(stored: null)
            .Select(column => new OrdersExportColumnInput("Catalog", column.Key, column.Header, null, column.Visible))];

    private static OrdersExportColumnInput Fixed(string header, string? value) =>
        new("Fixed", null, header, value, Visible: true);

    // Sin argumentos manda el catálogo entero; withoutColumns manda la lista nula, que es lo que
    // llega con un cuerpo `{}`.
    private static UpdateOrdersExportLayoutCommand NewCommand(
        IReadOnlyList<OrdersExportColumnInput>? columns = null,
        long expectedVersion = 1,
        bool withoutColumns = false) =>
        new(Guid.CreateVersion7(), withoutColumns ? null : columns ?? DefaultInputs(), expectedVersion, "trace");
}
```

**(b)** Crea `tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutHandlerTests.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// PUT del layout (spec 2026-09-24, "Application y API"): sin fila y ExpectedVersion 1 crea la fila
/// en 2 (D9); cualquier otra versión es 412; el no-op no crea fila, no audita ni guarda; la
/// auditoría `quotations.orders_export_layout.updated` sale por outbox sólo si cambió; 403 con
/// tenant ajeno o sin SettingsUpdate.
/// </summary>
public sealed class UpdateOrdersExportLayoutHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithoutARowAndIfMatchOneItCreatesTheRowAtVersionTwoAndAudits()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);
        var columns = DefaultInputs();
        columns.Insert(0, Fixed("Tipo Doc", "FV"));
        columns[19] = columns[19] with { Header = "Correo" };

        var dto = await handler.HandleAsync(NewCommand(columns, expectedVersion: 1), TestContext.Current.CancellationToken);

        Assert.Equal(2, dto.Version);
        Assert.Equal(34, dto.Columns.Count);
        Assert.Equal("Fixed", dto.Columns[0].Kind);
        Assert.Equal("Correo", dto.Columns[19].Header);
        var stored = Assert.Single(repository.Layouts);
        Assert.Equal(2, stored.Version);
        Assert.Equal(Now, stored.UpdatedAt);
        Assert.Equal(1, unitOfWork.Saves);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(
            new RecordedAuditEntry(TenantId, SubjectId, "quotations.orders_export_layout.updated", TenantId.ToString(), "success"),
            entry);
    }

    // D9: sin fila, la única versión válida es la implícita 1.
    [Fact]
    public async Task WithoutARowAnyOtherExpectedVersionIsAConflict()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            handler.HandleAsync(NewCommand(DefaultInputs(), expectedVersion: 2), TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Empty(repository.Layouts);
        Assert.Empty(audit.Entries);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task WithARowAStaleVersionIsAConflict()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        repository.Add(StoredLayout(Catalog("email", "Correo")));
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            handler.HandleAsync(NewCommand(DefaultInputs(), expectedVersion: 1), TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(2, repository.Layouts.Single().Version);
        Assert.Empty(audit.Entries);
        Assert.Equal(0, unitOfWork.Saves);
    }

    // Hallazgo 6: un primer PUT idéntico al catálogo no crea fila, responde la versión implícita y
    // no audita. Guardar sin tocar no es un cambio.
    [Fact]
    public async Task SavingTheDefaultsWithoutARowIsANoOpWithoutRowAuditOrSave()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);

        var dto = await handler.HandleAsync(NewCommand(DefaultInputs(), expectedVersion: 1), TestContext.Current.CancellationToken);

        Assert.Equal(1, dto.Version);
        Assert.Empty(repository.Layouts);
        Assert.Empty(audit.Entries);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task SavingTheSameColumnsOnAStoredLayoutIsANoOp()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var stored = StoredLayout(Catalog("email", "Correo"));
        repository.Add(stored);
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);
        var same = stored.Columns
            .Select(column => new OrdersExportColumnInput(column.Kind.ToString(), column.Key, column.Header, column.Value, column.Visible))
            .ToList();

        var dto = await handler.HandleAsync(NewCommand(same, expectedVersion: 2), TestContext.Current.CancellationToken);

        Assert.Equal(2, dto.Version);
        Assert.Equal(2, stored.Version);
        Assert.Empty(audit.Entries);
        Assert.Equal(0, unitOfWork.Saves);
    }

    // El dominio da el código; nada queda guardado ni auditado.
    [Fact]
    public async Task ABrokenDomainRuleIsTheDomainCodeAndNothingIsSaved()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);
        var hidden = DefaultInputs().Select(column => column with { Visible = false }).ToList();

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(hidden, expectedVersion: 1), TestContext.Current.CancellationToken));

        Assert.Equal("quotations.orders_export_layout.all_hidden", error.Code);
        Assert.Empty(repository.Layouts);
        Assert.Empty(audit.Entries);
        Assert.Equal(0, unitOfWork.Saves);
    }

    // El validador corre antes que todo: un cuerpo inválido ni siquiera lee la fila.
    [Fact]
    public async Task AnInvalidBodyIsAValidationFailureBeforeReadingAnything()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var handler = NewHandler(repository, new RecordingExportAuditPublisher(), new CountingQuotationsUnitOfWork());
        var columns = DefaultInputs();
        columns[3] = columns[3] with { Header = "   " };

        await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(NewCommand(columns, expectedVersion: 1), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.FindCalls);
    }

    [Fact]
    public async Task ForAnotherTenantIsForbidden()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var handler = NewHandler(
            repository, new RecordingExportAuditPublisher(), new CountingQuotationsUnitOfWork(),
            new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(DefaultInputs(), expectedVersion: 1), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.FindCalls);
    }

    // D5: SettingsUpdate, no un permiso nuevo ni el de leer.
    [Fact]
    public async Task WithoutSettingsUpdateIsForbidden()
    {
        var handler = NewHandler(
            new InMemoryOrdersExportLayoutRepository(), new RecordingExportAuditPublisher(), new CountingQuotationsUnitOfWork(),
            new StubExecutionContext(SubjectId, TenantId, TenancyPermissions.SettingsUpdate));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(DefaultInputs(), expectedVersion: 1), TestContext.Current.CancellationToken));
    }

    private static OrdersExportLayout StoredLayout(params OrdersExportColumnSetting[] columns)
    {
        var layout = OrdersExportLayout.CreateDefault(TenantId, Now.AddDays(-1));
        Assert.True(layout.Replace(columns, Now.AddDays(-1)));
        return layout;
    }

    private static OrdersExportColumnSetting Catalog(string key, string header, bool visible = true) =>
        OrdersExportColumnSetting.Catalog(key, header, visible);

    private static List<OrdersExportColumnInput> DefaultInputs() =>
        [.. OrdersExportLayout.Effective(stored: null)
            .Select(column => new OrdersExportColumnInput("Catalog", column.Key, column.Header, null, column.Visible))];

    private static OrdersExportColumnInput Fixed(string header, string? value) =>
        new("Fixed", null, header, value, Visible: true);

    private static UpdateOrdersExportLayoutCommand NewCommand(
        IReadOnlyList<OrdersExportColumnInput> columns, long expectedVersion) =>
        new(TenantId, columns, expectedVersion, "trace");

    private static UpdateOrdersExportLayoutHandler NewHandler(
        InMemoryOrdersExportLayoutRepository repository,
        RecordingExportAuditPublisher audit,
        CountingQuotationsUnitOfWork unitOfWork,
        IExecutionContext? executionContext = null) =>
        new(
            repository,
            unitOfWork,
            audit,
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now),
            new UpdateOrdersExportLayoutValidator());
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.UpdateOrdersExportLayout"
```

Esperado: **no compila**. `CS0246: No se encontró el tipo o el nombre de espacio de nombres 'UpdateOrdersExportLayoutValidator'` / `'OrdersExportColumnInput'` / `'UpdateOrdersExportLayoutCommand'` / `'UpdateOrdersExportLayoutHandler'`. Pega la salida.

- [ ] **Step 3: Comando, validador y handler**

Crea `src/Modules/Quotations/Modules.Quotations.Application/UpdateOrdersExportLayout.cs`:

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Una columna tal como llega en el PUT (spec 2026-09-24): <c>Kind</c> como texto
/// (<c>"Catalog"</c> | <c>"Fixed"</c>), <c>Key</c> sólo en una del catálogo, <c>Value</c> sólo en
/// una fija. Todo nullable: es el validador el que dice qué falta y en qué índice.</summary>
public sealed record OrdersExportColumnInput(
    string? Kind,
    string? Key,
    string? Header,
    string? Value,
    bool Visible);

public sealed record UpdateOrdersExportLayoutCommand(
    Guid TenantId,
    IReadOnlyList<OrdersExportColumnInput>? Columns,
    long ExpectedVersion,
    string CorrelationId) : ICommand<OrdersExportLayoutDto>;

/// <summary>El nombre exacto del enum, como viaja en el DTO: ordinal, sin minúsculas ni el número
/// del miembro, que es lo que <c>Enum.TryParse</c> sí aceptaría.</summary>
internal static class OrdersExportColumnKinds
{
    public static bool TryParse(string? kind, out OrdersExportColumnKind parsed)
    {
        if (string.Equals(kind, nameof(OrdersExportColumnKind.Catalog), StringComparison.Ordinal))
        {
            parsed = OrdersExportColumnKind.Catalog;
            return true;
        }

        if (string.Equals(kind, nameof(OrdersExportColumnKind.Fixed), StringComparison.Ordinal))
        {
            parsed = OrdersExportColumnKind.Fixed;
            return true;
        }

        parsed = default;
        return false;
    }
}

/// <summary>
/// El dominio da el código, el validador da el campo: el 422 de FluentValidation es el único que
/// lleva el mapa <c>errors</c> (<c>ApiExceptionHandler.cs:58-65</c>), y con
/// <c>RuleForEach(...).ChildRules</c> el nombre sale como <c>Columns[i].Header</c>, que es lo que
/// la pantalla usa para marcar el input <c>i</c>. Las mismas reglas que las factorías de
/// <see cref="OrdersExportColumnSetting"/>, medidas recortadas.
/// </summary>
public sealed class UpdateOrdersExportLayoutValidator : AbstractValidator<UpdateOrdersExportLayoutCommand>
{
    public UpdateOrdersExportLayoutValidator()
    {
        RuleFor(command => command.Columns).NotNull();
        RuleForEach(command => command.Columns).ChildRules(column =>
        {
            column.RuleFor(item => item.Kind)
                .Must(kind => OrdersExportColumnKinds.TryParse(kind, out _))
                .WithMessage("'Kind' must be 'Catalog' or 'Fixed'.");
            column.RuleFor(item => item.Header)
                .Must(header => !string.IsNullOrWhiteSpace(header)
                    && header.Trim().Length <= OrdersExportColumnSetting.HeaderMaxLength)
                .WithMessage($"'Header' is required and cannot exceed {OrdersExportColumnSetting.HeaderMaxLength} characters.");
            column.RuleFor(item => item.Value)
                .Must(value => value is null || value.Trim().Length <= OrdersExportColumnSetting.FixedValueMaxLength)
                .WithMessage($"'Value' cannot exceed {OrdersExportColumnSetting.FixedValueMaxLength} characters.");
        });
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Reemplaza el layout entero (spec 2026-09-24, D7). Mismo orden que el resto del módulo:
/// validador, permiso (<see cref="TenancyPermissions.SettingsUpdate"/>, D5), fila, versión,
/// dominio, auditoría, guardado.
///
/// D9: sin fila, el layout es el por defecto en versión 1, armado en memoria con
/// <see cref="OrdersExportLayout.CreateDefault"/>. Así el chequeo de versión es uno solo —"sin
/// fila y ExpectedVersion != 1" y "con fila y Version != ExpectedVersion" son la misma línea— y
/// el no-op también: un primer PUT idéntico al catálogo no crea fila ni audita. Dos primeros PUT
/// simultáneos chocan en la PK, que Infrastructure traduce al mismo 412.
///
/// Se audita por outbox (<see cref="IQuotationAuditPublisher"/>) sólo si cambió: registrar un
/// guardado que no cambió nada dejaría en la auditoría un cambio que no ocurrió.
/// </summary>
public sealed class UpdateOrdersExportLayoutHandler(
    IOrdersExportLayoutRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateOrdersExportLayoutCommand> validator)
    : ICommandHandler<UpdateOrdersExportLayoutCommand, OrdersExportLayoutDto>
{
    public const string AuditAction = "quotations.orders_export_layout.updated";

    public async Task<OrdersExportLayoutDto> HandleAsync(
        UpdateOrdersExportLayoutCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);

        var now = clock.UtcNow;
        var stored = await repository.FindAsync(command.TenantId, cancellationToken);
        var layout = stored ?? OrdersExportLayout.CreateDefault(command.TenantId, now);
        if (layout.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The orders export layout changed after it was loaded.");
        }

        // El validador ya garantizó que Columns no es nula y que cada Kind es conocido.
        var columns = (command.Columns ?? []).Select(ToSetting).ToArray();
        if (!layout.Replace(columns, now))
        {
            return OrdersExportLayoutMappings.ToDto(stored, command.TenantId);
        }

        if (stored is null)
        {
            repository.Add(layout);
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            AuditAction,
            command.TenantId.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return OrdersExportLayoutMappings.ToDto(layout, command.TenantId);
    }

    // Las factorías normalizan y lanzan header_invalid / fixed_value_invalid; con el validador
    // delante no se alcanzan por la API (hallazgo 9), pero el dominio no depende de eso.
    private static OrdersExportColumnSetting ToSetting(OrdersExportColumnInput input) =>
        OrdersExportColumnKinds.TryParse(input.Kind, out var kind) && kind == OrdersExportColumnKind.Fixed
            ? OrdersExportColumnSetting.Fixed(input.Header, input.Value, input.Visible)
            : OrdersExportColumnSetting.Catalog(input.Key ?? string.Empty, input.Header, input.Visible);
}
```

- [ ] **Step 4: Registro del handler**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, agrega justo después del registro de `GetOrdersExportLayoutHandler` (Task 5):

```csharp
        services.AddScoped<
            ICommandHandler<UpdateOrdersExportLayoutCommand, OrdersExportLayoutDto>,
            UpdateOrdersExportLayoutHandler>();
```

El validador no se registra: lo levanta `AddValidatorsFromAssemblyContaining<CreateQuotationValidator>()` (`:421`).

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.UpdateOrdersExportLayout"
dotnet build src/Api/Api.csproj
```

Esperado: `Failed: 0` en `UpdateOrdersExportLayoutValidatorTests` (15 casos, contando las filas de las Theory) y `UpdateOrdersExportLayoutHandlerTests` (9); build del Api con `0 Errores`. Pega la salida.

- [ ] **Step 6: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Quotations/Modules.Quotations.Application/UpdateOrdersExportLayout.cs src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutValidatorTests.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/UpdateOrdersExportLayoutHandlerTests.cs
git commit -m "feat(quotations): comando para homologar columnas del Excel de pedidos"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

---

### Task 7: Endpoints `GET`/`PUT /orders-export-layout` con integración

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Api/OrdersExportLayoutEndpoints.cs`
- Modify: `src/Api/Program.cs:119` (después de `app.MapOrderEndpoints();`)
- Modify: `README.md` (después del párrafo que termina en `queda vacía si la membresía no tiene código.`, `:800-803`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutApiTests.cs`

**Interfaces:**
- Consumes: `GetOrdersExportLayoutQuery`, `UpdateOrdersExportLayoutCommand`, `OrdersExportColumnInput`, `OrdersExportLayoutDto` (Tasks 5–6); `IRequestDispatcher.QueryAsync`/`SendAsync`; `PreconditionRequiredException`; `TenancyPermissions.SettingsRead`/`SettingsUpdate`, cuyas políticas ya existen (`QepServiceCollectionExtensions.AddAuthorization`; sin permiso nuevo no hay mitad que agregar). `Modules.Tenancy.Application` le llega a `Modules.Quotations.Api` transitivamente por `Modules.Quotations.Application` (`Modules.Quotations.Application.csproj:11`); no se agrega referencia.
- Produces:
  - `public static IEndpointRouteBuilder MapOrdersExportLayoutEndpoints(this IEndpointRouteBuilder endpoints)` — `GET` y `PUT /api/v1/tenants/{tenantId:guid}/orders-export-layout`.
  - `UpdateOrdersExportLayoutRequest(IReadOnlyList<OrdersExportColumnRequest>? Columns)`, `OrdersExportColumnRequest(string? Kind, string? Key, string? Header, string? Value, bool Visible)`, `OrdersExportLayoutResponse(Guid TenantId, IReadOnlyList<OrdersExportColumnResponse> Columns, long Version)`, `OrdersExportColumnResponse(string Kind, string? Key, string? DefaultHeader, int? DefaultPosition, string Header, string? Value, bool Visible)`.
  - ETag `"<version>"` en las dos respuestas; `If-Match` obligatorio en el PUT (428/412), aceptando `"1"`, `1` y `W/"1"`.

- [ ] **Step 1: Escribir las pruebas que fallan**

Crea `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// GET y PUT del layout de columnas (spec 2026-09-24, "Pruebas"): el efectivo con ETag "1" sin fila;
/// el primer PUT con If-Match "1" crea y devuelve "2"; 412, 428, 422 de campo y de dominio, 403, y
/// la auditoría en el outbox. Permisos de settings pedidos por X-Permissions como texto: este
/// proyecto no referencia Modules.Tenancy.Application (hallazgo 13).
/// </summary>
public sealed class OrdersExportLayoutApiTests
{
    private const string SettingsRead = "tenancy.settings.read";
    private const string SettingsUpdate = "tenancy.settings.update";
    private const string AuditEvent = "platform.audit.recorded.v1";
    private const string AuditAction = "quotations.orders_export_layout.updated";

    private static string LayoutUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders-export-layout";

    [Fact]
    public async Task GetWithoutAStoredLayoutReturnsTheCatalogWithETagOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;

        using var response = await client.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        var layout = await response.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(layout);
        Assert.Equal(tenantId, layout.TenantId);
        Assert.Equal(1, layout.Version);
        Assert.Equal(33, layout.Columns.Count);
        Assert.All(layout.Columns, column => Assert.Equal("Catalog", column.Kind));
        Assert.All(layout.Columns, column => Assert.True(column.Visible));
        Assert.Equal(new ColumnPayload("Catalog", "company", "EMPRESA", 1, "EMPRESA", null, true), layout.Columns[0]);
        Assert.Equal(new ColumnPayload("Catalog", "email", "Email", 19, "Email", null, true), layout.Columns[18]);
        Assert.Equal(33, layout.Columns[32].DefaultPosition);
    }

    // D9: el primer PUT viaja con "1" y la fila nace en 2. El GET siguiente la devuelve tal cual.
    [Fact]
    public async Task TheFirstPutWithIfMatchOneCreatesTheLayoutAndReturnsETagTwo()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns.Insert(0, Fixed("Tipo Doc", "FV"));
        columns[19] = columns[19] with { Header = "Correo" };
        columns[1] = columns[1] with { Visible = false };

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        var saved = await response.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Version);
        Assert.Equal(34, saved.Columns.Count);
        Assert.Equal(new ColumnPayload("Fixed", null, null, null, "Tipo Doc", "FV", true), saved.Columns[0]);
        Assert.Equal(new ColumnPayload("Catalog", "company", "EMPRESA", 1, "EMPRESA", null, false), saved.Columns[1]);
        Assert.Equal("Correo", saved.Columns[19].Header);
        Assert.Equal("Email", saved.Columns[19].DefaultHeader);

        using var read = await client.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.Equal("\"2\"", read.Headers.ETag?.Tag);
        var reread = await read.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(reread);
        Assert.Equal(saved.Version, reread.Version);
        // Elemento a elemento: ColumnPayload es un record, y la lista se compara por contenido.
        Assert.Equal(saved.Columns, reread.Columns);
    }

    [Fact]
    public async Task PutWithAStaleVersionIsAPreconditionFailure()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[19] = columns[19] with { Header = "Correo" };
        using var first = await PutAsync(client, tenantId, columns, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        columns[19] = columns[19] with { Header = "E-mail" };

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("concurrency.conflict", problem?.Code);
    }

    // Sin fila, la única versión válida es la implícita: "2" también es 412.
    [Fact]
    public async Task PutWithoutARowAndAVersionOtherThanOneIsAPreconditionFailure()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);

        using var response = await PutAsync(client, tenantId, columns, "\"2\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task PutWithoutIfMatchIsPreconditionRequired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);

        using var response = await PutAsync(client, tenantId, columns, ifMatch: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("precondition.if_match_required", problem?.Code);
    }

    // Review Focus 2: el ETag débil también es la versión 1.
    [Fact]
    public async Task PutAcceptsAWeakIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[19] = columns[19] with { Header = "Correo" };

        using var response = await PutAsync(client, tenantId, columns, "W/\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task PutWithABlankHeaderMarksTheColumn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[3] = columns[3] with { Header = "   " };

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Equal(["Columns[3].Header"], problem!.Errors!.Keys);
    }

    [Fact]
    public async Task PutWithATooLongFixedValueMarksTheColumn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns.Insert(0, Fixed("Bodega", new string('v', 129)));

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Equal(["Columns[0].Value"], problem!.Errors!.Keys);
    }

    [Fact]
    public async Task PutWithAnUnknownKindMarksTheColumn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[1] = columns[1] with { Kind = "catalog" };

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(["Columns[1].Kind"], problem!.Errors!.Keys);
    }

    [Fact]
    public async Task PutWithoutColumnsMarksTheList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        using var request = new HttpRequestMessage(HttpMethod.Put, LayoutUrl(tenantId))
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(["Columns"], problem!.Errors!.Keys);
    }

    // Los cuatro códigos de dominio que el validador no tapa (hallazgo 9). Sin mapa `errors`: el
    // formulario los muestra como mensaje general del pie.
    [Fact]
    public async Task PutWithAnUnknownKeyIsColumnsInvalid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[0] = columns[0] with { Key = "Company" };

        await AssertDomainCodeAsync(client, tenantId, columns, "quotations.orders_export_layout.columns_invalid");
    }

    [Fact]
    public async Task PutWithARepeatedKeyIsColumnsInvalid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns.Add(columns[18] with { Header = "Otro correo" });

        await AssertDomainCodeAsync(client, tenantId, columns, "quotations.orders_export_layout.columns_invalid");
    }

    [Fact]
    public async Task PutWithTwoVisibleColumnsSharingAHeaderIsHeaderDuplicated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[18] = columns[18] with { Header = "ciudad" };

        await AssertDomainCodeAsync(client, tenantId, columns, "quotations.orders_export_layout.header_duplicated");
    }

    [Fact]
    public async Task PutHidingEveryColumnIsAllHidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = (await DefaultColumnsAsync(client, tenantId))
            .Select(column => column with { Visible = false })
            .ToList();

        await AssertDomainCodeAsync(client, tenantId, columns, "quotations.orders_export_layout.all_hidden");
    }

    // Review Focus 4: la undécima sobre una fila con diez la rechaza y deja la fila intacta.
    [Fact]
    public async Task PutWithElevenFixedColumnsIsTooManyFixedColumns()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var ten = await DefaultColumnsAsync(client, tenantId);
        ten.AddRange(Enumerable.Range(1, 10).Select(number => Fixed($"Fija {number}", $"{number}")));
        using var saved = await PutAsync(client, tenantId, ten, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var eleven = (await saved.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken))!.Columns.ToList();
        eleven.Add(Fixed("Fija 11", "11"));

        await AssertDomainCodeAsync(client, tenantId, eleven, "quotations.orders_export_layout.too_many_fixed_columns", ifMatch: "\"2\"");

        using var read = await client.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.Equal("\"2\"", read.Headers.ETag?.Tag);
        var layout = await read.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(10, layout!.Columns.Count(column => column.Kind == "Fixed"));
    }

    [Fact]
    public async Task PutForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = owner;
        var (_, _, otherOwner) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var __ = otherOwner;
        var columns = await DefaultColumnsAsync(owner, tenantId);

        using var response = await PutAsync(otherOwner, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var read = await owner.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.Equal("\"1\"", read.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task GetForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = owner;
        var (_, _, otherOwner) = await RegisterTenantAsync(factory, SettingsRead);
        using var __ = otherOwner;

        using var response = await otherOwner.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // D5: leer no es editar. La política del endpoint corta antes del handler.
    [Fact]
    public async Task PutWithOnlySettingsReadIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);

        using var response = await PutAsync(client, tenantId, columns, "\"1\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetWithoutSettingsReadIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsUpdate);
        using var _ = client;

        using var response = await client.GetAsync(LayoutUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AChangedLayoutIsAuditedAndAnUnchangedOneIsNot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var columns = await DefaultColumnsAsync(client, tenantId);
        columns[19] = columns[19] with { Header = "Correo" };

        using var changed = await PutAsync(client, tenantId, columns, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var audits = (await OutboxMessagesAsync(factory, AuditEvent))
            .Where(message => ActionOf(message) == AuditAction)
            .ToArray();
        var entry = Assert.Single(audits);
        using (var payload = JsonDocument.Parse(entry.PayloadJson))
        {
            Assert.Equal(tenantId, payload.RootElement.GetProperty("tenantId").GetGuid());
            Assert.Equal(ownerUserId, payload.RootElement.GetProperty("actorId").GetGuid());
            Assert.Equal(tenantId.ToString(), payload.RootElement.GetProperty("resourceId").GetString());
            Assert.Equal("success", payload.RootElement.GetProperty("outcome").GetString());
        }

        using var unchanged = await PutAsync(client, tenantId, columns, "\"2\"");
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal("\"2\"", unchanged.Headers.ETag?.Tag);
        Assert.Single(
            (await OutboxMessagesAsync(factory, AuditEvent)).Where(message => ActionOf(message) == AuditAction));
    }

    // D8: un PUT no exige las 33; lo que falte va al final, visible y con su nombre.
    [Fact]
    public async Task PutWithoutSomeCatalogKeysCompletesThemAtTheEnd()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        using var response = await PutAsync(
            client, tenantId, [Catalog("email", "Correo"), Catalog("order_number", "Pedido")], "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var layout = await response.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(33, layout!.Columns.Count);
        Assert.Equal("email", layout.Columns[0].Key);
        Assert.Equal("order_number", layout.Columns[1].Key);
        Assert.Equal(new ColumnPayload("Catalog", "company", "EMPRESA", 1, "EMPRESA", null, true), layout.Columns[2]);
    }

    // D10: restaurar es un PUT con el catálogo en su orden y nombres, sin fijas. La fila queda.
    [Fact]
    public async Task RestoringIsAPutWithTheCatalogDefaultsWithoutFixedColumns()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var defaults = await DefaultColumnsAsync(client, tenantId);
        var custom = defaults.ToList();
        custom.Insert(0, Fixed("Tipo Doc", "FV"));
        custom[19] = custom[19] with { Header = "Correo", Visible = false };
        using var saved = await PutAsync(client, tenantId, custom, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var restored = await PutAsync(client, tenantId, defaults, "\"2\"");

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal("\"3\"", restored.Headers.ETag?.Tag);
        var layout = await restored.Content.ReadFromJsonAsync<LayoutPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(33, layout!.Columns.Count);
        Assert.DoesNotContain(layout.Columns, column => column.Kind == "Fixed");
        Assert.Equal(defaults, layout.Columns);
    }

    private static async Task AssertDomainCodeAsync(
        HttpClient client, Guid tenantId, IReadOnlyList<ColumnPayload> columns, string code, string ifMatch = "\"1\"")
    {
        using var response = await PutAsync(client, tenantId, columns, ifMatch);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(code, problem?.Code);
        Assert.Null(problem?.Errors);
    }

    /// <summary>El efectivo tal como lo devuelve el GET, listo para editarlo y mandarlo de vuelta:
    /// el mismo record sirve de request, y la API ignora `defaultHeader`/`defaultPosition`.</summary>
    private static async Task<List<ColumnPayload>> DefaultColumnsAsync(HttpClient client, Guid tenantId)
    {
        var layout = await client.GetFromJsonAsync<LayoutPayload>(LayoutUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(layout);
        return [.. layout.Columns];
    }

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, Guid tenantId, IReadOnlyList<ColumnPayload> columns, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, LayoutUrl(tenantId))
        {
            Content = JsonContent.Create(new { columns }),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static ColumnPayload Catalog(string key, string header, bool visible = true) =>
        new("Catalog", key, null, null, header, null, visible);

    private static ColumnPayload Fixed(string header, string value, bool visible = true) =>
        new("Fixed", null, null, null, header, value, visible);

    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

    private sealed record LayoutPayload(Guid TenantId, IReadOnlyList<ColumnPayload> Columns, long Version);

    private sealed record ColumnPayload(
        string Kind, string? Key, string? DefaultHeader, int? DefaultPosition, string Header, string? Value, bool Visible);

    private sealed record ProblemPayload(string? Code, Dictionary<string, string[]>? Errors);
}
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.OrdersExportLayoutApiTests"
```

Esperado: compila y **fallan todas** con `Expected: OK` (o el status esperado) `Actual: NotFound` — la ruta no existe todavía. Las de 403 por otro tenant también fallan (`NotFound`, no `Forbidden`). Pega la salida.

- [ ] **Step 3: Los endpoints**

Crea `src/Modules/Quotations/Modules.Quotations.Api/OrdersExportLayoutEndpoints.cs`:

```csharp
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Api;

/// <summary>
/// La homologación de columnas del Excel de pedidos (spec 2026-09-24): un recurso por tenant bajo
/// su configuración, con los permisos de settings y no uno nuevo (D5), ETag e If-Match sobre la
/// versión propia del layout (D6, D9) y la lista entera en cada respuesta (D7). Sin DELETE (D10):
/// restaurar es un PUT con el catálogo en su orden y nombres, sin fijas, que el GET ya trae en
/// <c>defaultHeader</c> y <c>defaultPosition</c>.
/// </summary>
public static class OrdersExportLayoutEndpoints
{
    public static IEndpointRouteBuilder MapOrdersExportLayoutEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/orders-export-layout")
            .WithTags("Tenant settings");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<OrdersExportLayoutResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdateOrdersExportLayoutRequest>("application/json")
            .Produces<OrdersExportLayoutResponse>()
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
        var layout = await dispatcher.QueryAsync(
            new GetOrdersExportLayoutQuery(tenantId), cancellationToken);
        return LayoutResult(layout, httpContext);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        UpdateOrdersExportLayoutRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded layout version is required.");
        }

        // Columns nula viaja nula: es el validador el que la rechaza con errors.Columns.
        var layout = await dispatcher.SendAsync(
            new UpdateOrdersExportLayoutCommand(
                tenantId,
                request.Columns?
                    .Select(column => new OrdersExportColumnInput(
                        column.Kind, column.Key, column.Header, column.Value, column.Visible))
                    .ToArray(),
                expectedVersion,
                httpContext.TraceIdentifier),
            cancellationToken);
        return LayoutResult(layout, httpContext);
    }

    private static IResult LayoutResult(OrdersExportLayoutDto layout, HttpContext httpContext)
    {
        httpContext.Response.Headers.ETag = $"\"{layout.Version}\"";
        return Results.Ok(new OrdersExportLayoutResponse(
            layout.TenantId,
            layout.Columns
                .Select(column => new OrdersExportColumnResponse(
                    column.Kind,
                    column.Key,
                    column.DefaultHeader,
                    column.DefaultPosition,
                    column.Header,
                    column.Value,
                    column.Visible))
                .ToArray(),
            layout.Version));
    }

    // Copia de OrderEndpoints.TryParseVersion (mismo proyecto, privado allá): acepta "3", 3 y
    // W/"3" (Review Focus 2).
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

/// <summary>`columns` nullable a propósito: `{}` llega como nula y el validador la marca, en vez
/// de convertirse en "restaurar todo" por accidente.</summary>
public sealed record UpdateOrdersExportLayoutRequest(IReadOnlyList<OrdersExportColumnRequest>? Columns);

public sealed record OrdersExportColumnRequest(
    string? Kind,
    string? Key,
    string? Header,
    string? Value,
    bool Visible);

public sealed record OrdersExportLayoutResponse(
    Guid TenantId,
    IReadOnlyList<OrdersExportColumnResponse> Columns,
    long Version);

/// <summary>`DefaultHeader` y `DefaultPosition` (1-based) viajan por columna (regla BFF): la
/// pantalla los necesita como placeholder, para "restaurar" una sola y para "restaurar todo" sin
/// conocer el catálogo. Nulos en una fija.</summary>
public sealed record OrdersExportColumnResponse(
    string Kind,
    string? Key,
    string? DefaultHeader,
    int? DefaultPosition,
    string Header,
    string? Value,
    bool Visible);
```

- [ ] **Step 4: Mapear en `Program.cs` y documentar**

En `src/Api/Program.cs`, agrega después de `app.MapOrderEndpoints();` (`:119`):

```csharp
app.MapOrdersExportLayoutEndpoints();
```

(`using Modules.Quotations.Api;` ya está: es el de `MapQuotationEndpoints`.)

En `README.md`, agrega después del párrafo que termina en `salen con el nuevo— y queda vacía si la membresía no tiene código.` (`:800-803`), dejando una línea en blanco antes:

```markdown
### Columnas del Excel de pedidos por tenant (homologación)

Cada ERP importa por encabezado con su propia plantilla, así que el tenant puede renombrar,
reordenar y ocultar las 33 columnas del Excel de pedidos y agregar hasta 10 columnas fijas de
texto (`Tipo Doc` = `FV`, `Bodega` = `01`), desde su configuración.

| Método | Ruta                                                | Permiso                  |
| ------ | --------------------------------------------------- | ------------------------ |
| `GET`  | `/api/v1/tenants/{tenantId}/orders-export-layout`   | `tenancy.settings.read`  |
| `PUT`  | `/api/v1/tenants/{tenantId}/orders-export-layout`   | `tenancy.settings.update`|

El `GET` devuelve el layout **efectivo**: lo guardado en su orden más toda columna del catálogo
que no esté guardada, al final, visible y con su nombre por defecto; sin nada guardado es el
catálogo tal cual (el Excel de siempre) con `version: 1` y ETag `"1"`. Cada columna viaja con
`kind` (`Catalog` | `Fixed`), `key` y `defaultHeader`/`defaultPosition` (sólo las del catálogo),
`header`, `value` (sólo las fijas) y `visible`.

El `PUT` reemplaza la lista entera con `If-Match` obligatorio (428 sin él; 412 con una versión
vieja, incluido el choque de dos primeros guardados). No exige las 33: lo que falte se completa.
Con un encabezado vacío o de más de 64 caracteres, o un valor fijo de más de 128, responde
`422 validation.failed` con `errors.Columns[i].Header` / `.Value` / `.Kind`. Reglas del dominio,
con prefijo `quotations.orders_export_layout.`: `columns_invalid` (llave desconocida o repetida),
`header_duplicated` (dos **visibles** con el mismo encabezado, sin distinguir mayúsculas),
`all_hidden`, `too_many_fixed_columns` (más de 10). Audita
`quotations.orders_export_layout.updated` sólo si algo cambió. No hay `DELETE`: restaurar es un
`PUT` con el catálogo en su orden y nombres, sin fijas.

Un layout guardado se aplica en la siguiente exportación de pedidos; el de cotizaciones no cambia.
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.OrdersExportLayoutApiTests"
```

Esperado: `Failed: 0`, 22 pruebas. Pega la salida.

- [ ] **Step 6: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Quotations/Modules.Quotations.Api/OrdersExportLayoutEndpoints.cs src/Api/Program.cs README.md tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrdersExportLayoutApiTests.cs
git commit -m "feat(quotations): GET y PUT de orders-export-layout"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

---

### Task 8: Processor — `Columns` del catálogo, layout una vez por job y proyección por fila

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportLayoutProjection.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` (constructor `:19-30`; `PaymentDateColumns` `:36-39`; `Columns` y su comentario `:46-94`; `ProcessAsync` `:106-140`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs` (`NewProcessor` `:641-666` y pruebas nuevas)

**Interfaces:**
- Consumes: `IOrdersExportLayoutRepository.FindAsync`; `OrdersExportLayout.Effective(OrdersExportLayout?)`; `OrdersExportColumnCatalog.Columns`/`IndexOf`/`PaymentDateColumns`; `IExportWorkbookWriter.Create(string, IReadOnlyList<ExportColumn>)`; `ExportCell.OfText`.
- Produces:
  - `public sealed class OrdersExportLayoutProjection` con `public const double FixedColumnWidth = 18`, `public IReadOnlyList<ExportColumn> Columns`, `public static OrdersExportLayoutProjection For(IReadOnlyList<OrdersExportColumnSetting> effective)` y `public ExportCell[] Project(IReadOnlyList<ExportCell> catalogCells)`.
  - `OrdersExportProcessor(IOrderRepository repository, IOrdersExportLayoutRepository layoutRepository, IQuotationCustomerLookup customerLookup, …)` — el repositorio nuevo va segundo.
  - `OrdersExportProcessor.PaymentDateColumns` = `OrdersExportColumnCatalog.PaymentDateColumns`; `OrdersExportProcessor.Columns` derivada del catálogo (mismo contenido que hoy; `OrdersExportColumnCatalogTests.MatchesTheProcessorColumnsHeaderByHeaderAndWidthByWidth` lo sostiene).

- [ ] **Step 1: Escribir las pruebas que fallan**

En `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs`:

**(a)** Reemplaza `NewProcessor` completo (`:641-666`) por:

```csharp
    private static OrdersExportProcessor NewProcessor(
        StubOrderListRepository repository,
        RecordingExportWorkbookWriter? writer = null,
        RecordingExportFileStorage? storage = null,
        FixedTenantClock? tenantClock = null,
        StubQuotationCustomerLookup? customers = null,
        StubQuotationProductLookup? products = null,
        StubQuotationCompanyLookup? companies = null,
        StubQuotationGeographyLookup? geography = null,
        StubQuotationAdvisorLookup? advisors = null,
        RecordingPaymentProofPublisher? publisher = null,
        InMemoryOrdersExportLayoutRepository? layouts = null) =>
        new(repository,
            // Sin layout guardado por defecto: el archivo de siempre (spec 2026-09-24, D8).
            layouts ?? new InMemoryOrdersExportLayoutRepository(),
            customers ?? new StubQuotationCustomerLookup(DefaultCustomer),
            products ?? new StubQuotationProductLookup(
                new Dictionary<Guid, QuotationProductRef>
                {
                    [ProductId] = DefaultProduct,
                    [OtherProductId] = DefaultProduct with { Id = OtherProductId, Code = "OTR-002" },
                }),
            companies ?? new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            geography ?? new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
            advisors ?? new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            publisher ?? new RecordingPaymentProofPublisher(),
            writer ?? new RecordingExportWorkbookWriter(),
            storage ?? new RecordingExportFileStorage(),
            tenantClock ?? new FixedTenantClock(Now));

    /// <summary>Un layout guardado para el tenant de las pruebas (spec 2026-09-24): lo que se
    /// pasa se completa con el catálogo, como hace Replace.</summary>
    private static InMemoryOrdersExportLayoutRepository StoredLayout(params OrdersExportColumnSetting[] columns)
    {
        var layouts = new InMemoryOrdersExportLayoutRepository();
        var layout = OrdersExportLayout.CreateDefault(TenantId, Now);
        Assert.True(layout.Replace(columns, Now));
        layouts.Add(layout);
        return layouts;
    }
```

**(b)** Agrega estas pruebas justo antes de `private static ExportJob NewJob(OrdersExportFilters? filters = null) =>`:

```csharp
    // Spec 2026-09-24 (homologación de columnas): sin fila guardada, el archivo es el de siempre,
    // y WritesTheErpColumnsInOrder lo sigue fijando encabezado por encabezado. Acá queda dicho
    // explícito, y que el layout se preguntó igual.
    [Fact]
    public async Task WithoutAStoredLayoutTheFileIsTheCatalogAsToday()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = new InMemoryOrdersExportLayoutRepository();

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(OrdersExportProcessor.Columns, writer.Columns);
        Assert.Equal(1, layouts.FindCalls);
    }

    // D2 y D8: encabezados del tenant, su orden, sin las ocultas y con el resto del catálogo
    // detrás. Los anchos son los del catálogo, no importa el nombre.
    [Fact]
    public async Task AStoredLayoutRenamesReordersAndHidesColumns()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = StoredLayout(
            OrdersExportColumnSetting.Catalog("email", "Correo", visible: true),
            OrdersExportColumnSetting.Catalog("order_number", "Pedido", visible: true),
            OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: false));

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(32, writer.Columns.Count);
        Assert.Equal(new ExportColumn("Correo", 30), writer.Columns[0]);
        Assert.Equal(new ExportColumn("Pedido", 18), writer.Columns[1]);
        Assert.Equal(new ExportColumn("Cod. Producto", 18), writer.Columns[2]);
        Assert.Equal("Valor Unit sin IVA", writer.Columns[^1].Header);
        Assert.DoesNotContain(writer.Columns, column => column.Header == "EMPRESA");
        var cells = Assert.Single(writer.Rows);
        Assert.Equal(writer.Columns.Count, cells.Count);
        Assert.Equal("cliente@ejemplo.co", cells[0].Text);
        Assert.Equal("PED-2026-0001", cells[1].Text);
        Assert.Equal("TOR-001", cells[2].Text);
        Assert.Equal(2m, cells[3].Number);
    }

    // D4 y Review Focus 3: la fija repite su texto en cada línea del pedido, también la vacía —una
    // celda vacía, no espacios—; ancho 18, en su posición.
    [Fact]
    public async Task FixedColumnsWriteTheirTextOnEveryRowIncludingAnEmptyOne()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = StoredLayout(
            OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true),
            OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: true),
            OrdersExportColumnSetting.Fixed("Bodega", "   ", visible: true));
        var row = NewRow(
            "PED-2026-0001",
            items:
            [
                (ProductId, 2m, 1000m, 0m, 19),
                (OtherProductId, 5m, 500m, 0m, 19),
            ]);

        await NewProcessor(new StubOrderListRepository(row), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(35, writer.Columns.Count);
        Assert.Equal(new ExportColumn("Tipo Doc", OrdersExportLayoutProjection.FixedColumnWidth), writer.Columns[0]);
        Assert.Equal(new ExportColumn("EMPRESA", 30), writer.Columns[1]);
        Assert.Equal(new ExportColumn("Bodega", 18), writer.Columns[2]);
        Assert.Equal(2, writer.Rows.Count);
        foreach (var cells in writer.Rows)
        {
            Assert.Equal(writer.Columns.Count, cells.Count);
            Assert.Equal(ExportCell.OfText("FV"), cells[0]);
            Assert.Equal(ExportCell.OfText(string.Empty), cells[2]);
        }

        // Cantidad queda en la 5.ª: Tipo Doc, EMPRESA, Bodega, Cod. Producto, Cantidad.
        Assert.Equal(2m, writer.Rows[0][4].Number);
        Assert.Equal(5m, writer.Rows[1][4].Number);
    }

    // Una fija oculta no viaja: ni columna ni celda. Hay 33 columnas, como sin layout.
    [Fact]
    public async Task AHiddenFixedColumnIsNotWritten()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = StoredLayout(OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: false));

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(OrdersExportProcessor.Columns, writer.Columns);
        Assert.DoesNotContain(Assert.Single(writer.Rows), cell => cell.Text == "FV");
    }

    // Una fila con dos fijas seguidas y una columna del catálogo entre otras dos fijas: el orden
    // de las fijas es el del layout, no el de las llaves.
    [Fact]
    public async Task FixedColumnsComeOutInTheLayoutsOrderAmongTheCatalogOnes()
    {
        var writer = new RecordingExportWorkbookWriter();
        var layouts = StoredLayout(
            OrdersExportColumnSetting.Catalog("order_number", "Pedido", visible: true),
            OrdersExportColumnSetting.Fixed("A", "a", visible: true),
            OrdersExportColumnSetting.Fixed("B", "b", visible: true),
            OrdersExportColumnSetting.Catalog("email", "Email", visible: true),
            OrdersExportColumnSetting.Fixed("C", "c", visible: true));

        await NewProcessor(new StubOrderListRepository(NewRow("PED-2026-0001")), writer, layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(["Pedido", "A", "B", "Email", "C", "EMPRESA"], writer.Columns.Take(6).Select(column => column.Header));
        var cells = Assert.Single(writer.Rows);
        Assert.Equal(["PED-2026-0001", "a", "b", "cliente@ejemplo.co", "c", string.Empty], cells.Take(6).Select(cell => cell.Text));
    }

    // El layout se resuelve una vez por job: no por lote ni por fila.
    [Fact]
    public async Task TheLayoutIsReadOnceForTheWholeJob()
    {
        var rows = Enumerable.Range(1, ExportJobLimits.BatchSize + 1)
            .Select(number => NewRow($"PED-2026-{number:0000}"))
            .ToArray();
        var layouts = StoredLayout(OrdersExportColumnSetting.Catalog("email", "Correo", visible: true));

        await NewProcessor(new StubOrderListRepository(rows), layouts: layouts)
            .ProcessAsync(NewJob(), TestContext.Current.CancellationToken);

        Assert.Equal(1, layouts.FindCalls);
    }
```

- [ ] **Step 2: Correr y ver el RED**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.OrdersExportProcessorTests"
```

Esperado: **no compila**. `CS1729: 'OrdersExportProcessor' no contiene un constructor que tome 11 argumentos` (o `CS1503` por el tipo del segundo argumento) y `CS0103: El nombre 'OrdersExportLayoutProjection' no existe`. Pega la salida.

- [ ] **Step 3: La proyección**

Crea `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportLayoutProjection.cs`:

```csharp
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Aplica el layout efectivo del tenant a una fila del Excel de pedidos (spec 2026-09-24). El
/// processor sigue armando las 33 celdas en el orden del catálogo —cambio mínimo, comprobable en
/// unitaria—, y esto las reordena, descarta las ocultas e intercala las fijas como texto en su
/// posición. Se arma una vez por job: el layout no cambia a mitad de un archivo.
/// </summary>
public sealed class OrdersExportLayoutProjection
{
    /// <summary>El ancho de una fija: no está en el catálogo, y 18 es el de "Cod. Producto", una
    /// columna corta de código, que es lo que una fija suele ser (spec 2026-09-24, "Processor").</summary>
    public const double FixedColumnWidth = 18;

    // Por columna visible: el índice de la celda en el orden del catálogo, o -1 si es una fija.
    private readonly int[] _sources;

    // Las celdas de las fijas visibles, en su orden.
    private readonly ExportCell[] _fixedCells;

    private OrdersExportLayoutProjection(
        IReadOnlyList<ExportColumn> columns, int[] sources, ExportCell[] fixedCells)
    {
        Columns = columns;
        _sources = sources;
        _fixedCells = fixedCells;
    }

    /// <summary>Las columnas de la hoja: encabezado del tenant, ancho del catálogo (18 las fijas),
    /// sólo las visibles y en el orden del tenant.</summary>
    public IReadOnlyList<ExportColumn> Columns { get; }

    public static OrdersExportLayoutProjection For(IReadOnlyList<OrdersExportColumnSetting> effective)
    {
        var columns = new List<ExportColumn>(effective.Count);
        var sources = new List<int>(effective.Count);
        var fixedCells = new List<ExportCell>();

        foreach (var column in effective)
        {
            if (!column.Visible)
            {
                continue;
            }

            if (column.Kind == OrdersExportColumnKind.Fixed)
            {
                columns.Add(new ExportColumn(column.Header, FixedColumnWidth));
                sources.Add(-1);
                fixedCells.Add(ExportCell.OfText(column.Value));
                continue;
            }

            // Effective ya descartó toda llave que no esté en el catálogo: el índice existe.
            var index = OrdersExportColumnCatalog.IndexOf(column.Key!);
            columns.Add(new ExportColumn(column.Header, OrdersExportColumnCatalog.Columns[index].Width));
            sources.Add(index);
        }

        return new OrdersExportLayoutProjection(columns, [.. sources], [.. fixedCells]);
    }

    /// <summary>De las celdas de una fila en orden de catálogo a las de la hoja del tenant.</summary>
    public ExportCell[] Project(IReadOnlyList<ExportCell> catalogCells)
    {
        var cells = new ExportCell[_sources.Length];
        var nextFixed = 0;
        for (var position = 0; position < cells.Length; position++)
        {
            var source = _sources[position];
            cells[position] = source < 0 ? _fixedCells[nextFixed++] : catalogCells[source];
        }

        return cells;
    }
}
```

- [ ] **Step 4: El processor**

En `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs`:

**(a)** Reemplaza el arranque de la clase (`:19-21`):

```csharp
public sealed class OrdersExportProcessor(
    IOrderRepository repository,
    IQuotationCustomerLookup customerLookup,
```

por:

```csharp
public sealed class OrdersExportProcessor(
    IOrderRepository repository,
    IOrdersExportLayoutRepository layoutRepository,
    IQuotationCustomerLookup customerLookup,
```

**(b)** Reemplaza la constante `PaymentDateColumns` con su comentario (`:36-39`) por:

```csharp
    /// <summary>Cuántas fechas de pago tienen columna propia (ajuste 2026-09-20). El número vive en
    /// el catálogo desde la homologación (spec 2026-09-24); acá queda el alias que ya usan las
    /// pruebas.</summary>
    public const int PaymentDateColumns = OrdersExportColumnCatalog.PaymentDateColumns;
```

**(c)** Reemplaza el bloque de `Columns` entero, desde el `/// <summary>` que abre con `/// Las columnas que el ERP contable espera, en su orden (ajuste 2026-09-20). "Nota Detalle"` hasta el `];` que cierra la lista (`:46-94`), por:

```csharp
    /// <summary>
    /// Las columnas por defecto del ERP contable, en su orden: las del catálogo
    /// (<see cref="OrdersExportColumnCatalog"/>, spec 2026-09-24) con su encabezado y su ancho. Ya
    /// no es una lista literal: es lo que sale sin layout guardado, y es lo que
    /// <see cref="OrdersExportLayoutProjection"/> reordena, renombra y recorta cuando lo hay.
    ///
    /// Las razones de cada columna siguen valiendo y viven con su llave en el catálogo: "Nota
    /// Detalle" sale siempre vacía (no existe nota por línea); "Cod. Asesor" (D9 del spec del código
    /// de asesor) va después de "Email" para no mover lo que el ERP ya importa; "Banco", "Cuenta" y
    /// los pares "V. Comprobante N" / "URL Comprobante N" (2026-09-24) van al final por lo mismo; y
    /// de última "Valor Unit sin IVA" (<see cref="QuotationItem.UnitPriceWithoutTax"/>), mientras
    /// "Valor Unit" sigue llevando el precio con IVA incluido.
    /// </summary>
    public static readonly IReadOnlyList<ExportColumn> Columns = OrdersExportColumnCatalog.Columns
        .Select(column => new ExportColumn(column.DefaultHeader, column.Width))
        .ToArray();
```

**(d)** En `ProcessAsync`, reemplaza:

```csharp
        var generatedAt = calendar.UtcNow;

        // El conteo del archivo (filas de producto) se lleva aparte del que devuelve el lote
```

por:

```csharp
        var generatedAt = calendar.UtcNow;

        // El layout del tenant, una vez por job (spec 2026-09-24): encabezados, orden y ocultas del
        // tenant, más sus fijas. Sin fila guardada, el efectivo es el catálogo y el archivo es el de
        // siempre (D8).
        var layout = OrdersExportLayoutProjection.For(
            OrdersExportLayout.Effective(await layoutRepository.FindAsync(job.TenantId, cancellationToken)));

        // El conteo del archivo (filas de producto) se lleva aparte del que devuelve el lote
```

y reemplaza:

```csharp
        using var workbook = writer.Create(SheetName, Columns);
```

por:

```csharp
        using var workbook = writer.Create(SheetName, layout.Columns);
```

y dentro del `async (batch, ct) =>` reemplaza:

```csharp
                var rows = batch.SelectMany(row => RowsFor(row, context, calendar, paymentProofPublisher)).ToArray();
```

por:

```csharp
                // RowsFor sigue armando las celdas en el orden del catálogo; la proyección las lleva
                // al orden del tenant. Cambio mínimo, y el mismo para todas las filas del job.
                var rows = batch
                    .SelectMany(row => RowsFor(row, context, calendar, paymentProofPublisher))
                    .Select(cells => layout.Project(cells))
                    .ToArray();
```

- [ ] **Step 5: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests/Modules.Quotations.UnitTests.csproj --filter "FullyQualifiedName~Modules.Quotations.UnitTests.OrdersExport"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.OrderExportApiTests"
```

Esperado: `Failed: 0` en las dos. En la primera están `OrdersExportProcessorTests` (las de siempre —`WritesTheErpColumnsInOrder` incluida— más las seis nuevas), `OrdersExportColumnCatalogTests` (la de equivalencia ahora compara contra la derivada) y las del layout. En la segunda, `TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail` sigue verde con su lista literal: sin fila guardada nada cambia. Pega la salida.

- [ ] **Step 6: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
git add src/Modules/Quotations/Modules.Quotations.Application/OrdersExportLayoutProjection.cs src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs
git commit -m "feat(quotations): el Excel de pedidos aplica el layout del tenant"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

---

### Task 9: De punta a punta — `PUT` del layout y después exportar

**Files:**
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs` (después de `SetOwnerAdvisorCodeAsync`, `:383-397`; records al final, `:542-548`)

**Interfaces:**
- Consumes: `PUT /orders-export-layout` (Task 7), el worker (`RunExportJobAsync`), `ExportWorkbookReader`, `CreateOrderAsync` del mismo archivo.
- Produces: la prueba que cruza Api → Application → Infrastructure → processor → OpenXML, que ninguna unitaria ve.

- [ ] **Step 1: Escribir la prueba que falla**

En `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs`:

**(a)** Agrega justo después del método `SetOwnerAdvisorCodeAsync` (después de su llave de cierre, `:397`):

```csharp
    // Spec 2026-09-24 (homologación de columnas), de punta a punta: el PUT del layout guarda el
    // jsonb, el worker lo lee en su propio scope, y la hoja sale con la fija primero, "Email"
    // renombrada segunda, EMPRESA ausente y el resto del catálogo detrás, en cada fila.
    [Fact]
    public async Task TheOrdersWorkbookFollowsTheTenantsLayoutAfterThePut()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // X-Permissions reemplaza el set por defecto del stub: los de settings se piden explícitos.
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, "tenancy.settings.read", "tenancy.settings.update"]);
        using var _ = client;
        await CreateOrderAsync(client, factory, tenantId);
        await SaveOrdersExportLayoutAsync(client, tenantId);

        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{CurrentRange()}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));

        var sheet = ExportWorkbookReader.Read(await factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
        var header = sheet.Rows[0];
        // 33 del catálogo + 1 fija − 1 oculta.
        Assert.Equal(33, header.Count);
        Assert.Equal("Tipo Doc", header[0]);
        Assert.Equal("Correo", header[1]);
        Assert.Equal("Cod. Producto", header[2]);
        Assert.Equal("Cantidad", header[3]);
        Assert.DoesNotContain("EMPRESA", header);
        Assert.DoesNotContain("Email", header);
        Assert.Equal("Valor Unit sin IVA", header[^1]);
        var row = sheet.Rows[1];
        Assert.Equal(header.Count, row.Count);
        Assert.Equal("FV", row[0]);
        Assert.Equal("compras@verde.co", row[1]);
        Assert.NotEqual(string.Empty, row[2]);
        Assert.True(sheet.NumericCells[1][3]);
        Assert.Equal(1m, decimal.Parse(row[3], CultureInfo.InvariantCulture));
    }

    /// <summary>El layout de la prueba: una fija "Tipo Doc" = "FV" primero, "Email" renombrada
    /// "Correo" segunda, EMPRESA oculta, el resto del catálogo en su orden. La versión se lee del
    /// GET y no se supone: If-Match tiene que llevar la vigente.</summary>
    private static async Task SaveOrdersExportLayoutAsync(HttpClient client, Guid tenantId)
    {
        var url = $"/api/v1/tenants/{tenantId}/orders-export-layout";
        var current = await client.GetFromJsonAsync<OrdersExportLayoutPayload>(url, TestContext.Current.CancellationToken);
        Assert.NotNull(current);
        var columns = current.Columns.ToList();
        var email = columns.Single(column => column.Key == "email");
        columns.Remove(email);
        columns.Insert(0, email with { Header = "Correo" });
        columns.Insert(0, new OrdersExportColumnPayload("Fixed", null, "Tipo Doc", "FV", true));
        var company = columns.Single(column => column.Key == "company");
        columns[columns.IndexOf(company)] = company with { Visible = false };

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(new { columns }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{current.Version}\"");
        using var saved = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("\"2\"", saved.Headers.ETag?.Tag);
    }
```

**(b)** Agrega al final de la clase, después de `private sealed record MembershipRosterRowPayload(Guid Id, long Version, bool IsOwner);`:

```csharp

    private sealed record OrdersExportLayoutPayload(IReadOnlyList<OrdersExportColumnPayload> Columns, long Version);

    // Sólo lo que el PUT lee; el GET trae además defaultHeader/defaultPosition, que acá no importan.
    private sealed record OrdersExportColumnPayload(string Kind, string? Key, string Header, string? Value, bool Visible);
```

- [ ] **Step 2: Correr y ver el RED**

Esta prueba se escribe **después** de que Tasks 7 y 8 están verdes, así que para verla en rojo se apaga la proyección un momento. En `OrdersExportProcessor.ProcessAsync`, cambia temporalmente `writer.Create(SheetName, layout.Columns)` por `writer.Create(SheetName, Columns)` y `.Select(cells => layout.Project(cells))` por `.Select(cells => cells)`; corre; y deshaz los dos cambios (`git checkout -- src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs` los deshace, porque el archivo está commiteado en Task 8).

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.OrderExportApiTests.TheOrdersWorkbookFollowsTheTenantsLayoutAfterThePut"
git checkout -- src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs
git status --short
```

Esperado: `Failed: 1` con `Assert.Equal() Failure: Expected: "Tipo Doc", Actual: "EMPRESA"` (el PUT guardó, pero el processor apagado no lo aplicó). Después del `checkout`, `git status --short` muestra sólo `M tests/.../OrderExportApiTests.cs`. Pega la salida. Si el RED sale por otra cosa (`NotFound`, 412, `Assert.NotNull(current)`), el PUT de Task 7 no está cableado: revisa `Program.cs` antes de seguir.

- [ ] **Step 3: Correr y ver el GREEN**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests/Modules.Quotations.IntegrationTests.csproj --filter "FullyQualifiedName~Modules.Quotations.IntegrationTests.OrderExportApiTests"
```

Esperado: `Failed: 0` en todo `OrderExportApiTests`: la nueva y las de siempre, con la lista literal de `TheWorkerTurnsTheRequestIntoTheOrdersWorkbookAndTheEmail` intacta. Pega la salida.

- [ ] **Step 4: Commit**

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
git add tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs
git commit -m "test(quotations): homologación de columnas de punta a punta"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

Esperado: el último comando sin salida.

---

### Task 10: Verificación final contra el baseline

**Files:**
- Ninguno, salvo lo que la verificación obligue a corregir (commit `fix(...)` aparte, con su propio RED/GREEN).

**Interfaces:**
- Consumes: `$env:TEMP\qep-homologacion-baseline-failed.txt` (Task 0).
- Produces: evidencia de capas verdes, formato limpio en los archivos del plan, build limpio y suite completa sin regresiones por nombre.

- [ ] **Step 1: Capas y formato de lo que este plan tocó**

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj
$files = git diff --name-only d696bce..HEAD -- "*.cs" | Where-Object { $_ -notmatch "Migrations/" }
$files
dotnet format --verify-no-changes --include $files
```

Esperado: `ArchitectureTests` con `Failed: 0` —`QuotationsLayerTests` verde: Domain sin referencias hacia afuera (el catálogo y el agregado sólo usan `BuildingBlocks.Domain`), Application sin EF ni Npgsql, y sólo `Modules.Tenancy` entre los módulos de negocio—. `dotnet format` sobre la lista de archivos de este plan: **sin diagnósticos que no sean `ENDOFLINE`** (hallazgo 15: el `ENDOFLINE` por CRLF es el baseline del repo y no se corrige acá). Si aparece `IDE`/`WHITESPACE`/`IMPORTS` en un archivo de este plan, corrige con `dotnet format --include <archivo>` sobre ese archivo, vuelve a correr las pruebas de su tarea y commitea aparte como `style(quotations): formato de <qué>`.

- [ ] **Step 2: Barrido de cuerpos crudos** (gotcha del `CLAUDE.md`: "al agregar un campo requerido o una precondición, hay que barrer las pruebas de integración por cuerpos crudos")

Este plan no agrega campos requeridos a ningún contrato existente —el processor cambia de firma, no de contrato HTTP—; se verifica igual que nadie más construye el processor ni lee `Columns` por posición fuera de lo revisado:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-ChildItem src, tests -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern 'new OrdersExportProcessor\(|OrdersExportProcessor\.Columns|orders-export-layout' |
    ForEach-Object { "{0}:{1}: {2}" -f $_.Path, $_.LineNumber, $_.Line.Trim() }
```

Esperado: `new OrdersExportProcessor(` sólo en `OrdersExportProcessorTests.NewProcessor` (Bootstrapper lo registra por tipo, no lo construye); `OrdersExportProcessor.Columns` sólo en `OrdersExportColumnCatalogTests` y `OrdersExportProcessorTests`; `orders-export-layout` en `OrdersExportLayoutEndpoints.cs`, `OrdersExportLayoutApiTests.cs` y `OrderExportApiTests.cs`. Anota la lista en el handoff.

- [ ] **Step 3: Build y suite completa, por nombre**

Con Docker corriendo y la API detenida:

```powershell
Set-Location C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend
Get-Process -Name Api -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like "*Api.dll*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
dotnet restore --locked-mode
dotnet build --no-restore
$final = Join-Path $env:TEMP "qep-homologacion-final"
Remove-Item -Recurse -Force $final -ErrorAction SilentlyContinue
dotnet test --no-build --logger trx --results-directory $final
Get-ChildItem $final -Filter *.trx -Recurse | ForEach-Object {
    [xml]$trx = Get-Content -Raw -LiteralPath $_.FullName
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        ForEach-Object { $_.testName }
} | Sort-Object -Unique | Set-Content -Encoding utf8 (Join-Path $env:TEMP "qep-homologacion-final-failed.txt")
Compare-Object `
    (Get-Content (Join-Path $env:TEMP "qep-homologacion-baseline-failed.txt")) `
    (Get-Content (Join-Path $env:TEMP "qep-homologacion-final-failed.txt"))
```

Esperado: `dotnet restore --locked-mode` sin `NU1004` (este plan no toca `Directory.Packages.props`); build con `0 Errores`; `Compare-Object` **sin filas `=>`** (ninguna prueba falla ahora que no fallara antes). Una fila `<=` es una prueba que se arregló sola: se anota, no bloquea. Si alguno de los dos archivos está vacío, `Get-Content` devuelve `$null` y `Compare-Object` protesta: en ese caso reemplaza el argumento vacío por `@()`. Pega la salida.

Si aparece una fila `=>`: se diagnostica con `superpowers:systematic-debugging`, se escribe la prueba que la reproduce si no existe, se corrige y se commitea aparte:

```powershell
$branch = git branch --show-current
if ($branch -ne "feature/homologacion-columnas-excel") { throw "Rama inesperada: '$branch'. No se commitea." }
git add <rutas explícitas del arreglo>
git commit -m "fix(quotations): <qué se arregló>"
git log -1 --format=%B | Select-String -SimpleMatch "Co-Authored-By"
```

- [ ] **Step 4: Cierre**

```powershell
git status --short
git log --oneline d696bce..HEAD
```

Esperado: árbol limpio; los diez commits de la tabla de Entrega en orden (más el `fix`/`style` si hubo). Entrega en el handoff: las salidas RED/GREEN de cada tarea, el barrido del Step 2, el `Compare-Object` del Step 3, y las dos decisiones que el spec no nombra y este plan tomó (hallazgos 5 y 6: la lista se guarda completada; un primer PUT idéntico al catálogo no crea fila). Si en Task 0 se apartó trabajo ajeno con `git stash`, recuérdale al developer que sigue ahí (`git stash list`); no lo apliques desde este plan. La rama no se pushea desde este plan.
