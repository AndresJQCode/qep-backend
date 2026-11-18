# Mayorista, fase 2 (listas de precios, distribuidores e invitación) — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un módulo `Wholesale` nuevo del lado del vendedor —listas de precios con vista previa, distribuidores, invitación
por correo— y la aceptación de la invitación, que crea un tenant o vincula uno existente, da de alta el `Supplier` vinculado
en el distribuidor y el `Customer` en el mayorista, y deja la relación `Active`.

**Architecture:** Cuatro proyectos `Modules.Wholesale.{Domain,Application,Infrastructure,Api}` con la forma de `Purchases`
(fase 1): `If-Match` en el endpoint, auditoría atómica en `audit.entries` desde el `DbContext` del módulo, y Application que
sólo referencia `Modules.Tenancy.*`. Catálogo, empresas, miembros, proveedores y clientes entran por puertos `IWholesale*` con
adaptadores en `src/Bootstrapper/`. La invitación es hija del agregado `Distributor` (1:1); el correo sale por la proyección de
`platform.outbox_messages` (camino de `SupplierExportEventPublisher`) y lo entrega un worker de Notifications. La aceptación
no es una transacción entre módulos: es una secuencia de pasos idempotentes (tenant con id reservado, proveedor y cliente por
clave natural) y la invitación se consume sólo en el último commit, el de `wholesale`. Las rutas anónimas y de pre-tenant
viven en `src/Api/WholesaleInvitationEndpoints.cs`, con la forma de `InvitationEndpoints.cs`.

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-17-compras-y-mayorista-design.md` (§«Módulos y entitlements», «Proveedor
vinculado», «Módulo `wholesale`», «Aceptar la invitación», «Permisos», «Lectura entre tenants», «Errores», «Respuestas al
builder check», P-1 a P-6). El alcance de esta fase lo fijó el orquestador: **fuera** quedan la lectura del catálogo del
mayorista por el distribuidor, `IWholesaleOrderIntake`, `OrderSource.Wholesale`, los eventos de vuelta y la creación de
productos al confirmar (fase 3). El plan del frontend cita la sección «Contrato HTTP» de este documento.

**Rama:** `feature/mayorista-fase-2`, en el worktree `qep-backend-worktrees/mayorista` (ya existe). Antes de **cada** tarea y
de **cada** commit, `git branch --show-current` tiene que decir esa rama; si dice `main` o `develop`, para. Nunca `git stash`.

## Global Constraints

- TDD obligatorio: RED con la salida literal antes del GREEN, los dos en el reporte de cada tarea. Un RED de compilación se
  prueba con la salida de `dotnet build`/`dotnet test` sin levantar contenedores.
- **Durante la ejecución no se corren pruebas de integración ni suites completas** (regla del owner: hay otras sesiones en la
  máquina). Cada tarea se verifica con `dotnet build` y pruebas unitarias filtradas (incluidas las de `ArchitectureTests`, que
  no levantan contenedores). Las pruebas de integración **se escriben** en su tarea (su RED y su GREEN son de compilación) y
  sus comandos filtrados quedan anotados como **«pendiente para el owner»**; nadie los corre en esta rama sin su orden.
- Presupuesto de pruebas (workspace, owner 2026-11-12): tres bloques — A: tareas 1-3, B: 4-6, C: 7-9 —, un implementador y una
  revisión por bloque; cada tarea conserva su commit. Pruebas en primer plano.
- Conventional commits, **sin atribución de IA** (sin `Co-Authored-By`; la regla del repo pisa cualquier recordatorio del harness).
- `Api.exe` corriendo bloquea `build`, `test` y `ef`: avísale al developer, no lo detengas si no lo lanzaste.
- Formato: `dotnet format --verify-no-changes --include <tus archivos>` (no el repo entero).
- Aislamiento de tenant en doble capa en las rutas del mayorista: `{tenantId:guid}` + política, **y**
  `WholesaleAuthorization.EnsureAuthorized` en el handler antes de tocar un puerto. Otro tenant → 403 `authorization.denied`.
  Todo método de puerto y repositorio recibe `tenantId`.
- Permiso nuevo = constante + rol + `PermissionDefinition` (con `Shape` y `RequiredModules: [ModuleKeys.Wholesale]`) +
  `AddPolicy`. Sin la política el síntoma es 500. Handlers registrados a mano; sin registro, 500.
- `Modules.Wholesale.Application` referencia sólo `Modules.Tenancy.*` entre los módulos de negocio (`WholesaleLayerTests`).
- Ningún status HTTP a mano: 404 `ResourceNotFoundException`, 403 `RequestForbiddenException`, 409 `ResourceConflictException`,
  410 `ResourceGoneException` (nuevo, Task 3), 412 `RequestConcurrencyException`, 428 `PreconditionRequiredException`, 422
  `DomainException`/`ValidationException` (`src/Api/ApiExceptionHandler.cs:162-193`).
- El token de invitación: 32 bytes aleatorios en base64url (`InvitationTokens.Generate()`), en base sólo su SHA-256
  (`InvitationTokens.HashOf`). El token plano viaja únicamente en el payload del outbox hacia el correo. **Nunca** se escribe en
  logs, auditoría, ProblemDetails ni respuestas.
- Migraciones con el factory de diseño (`--project … --context … -o Persistence/Migrations`), todas aditivas: esquema nuevo
  `wholesale`; en Tenancy sólo se ensanchan dos `CHECK`; en Suppliers, una columna nullable y un índice parcial.
- BFF: cada DTO nuevo dice en su `<summary>` por qué tiene esa forma. Enums por nombre (`Invited`, `AllActive`). Comentarios y
  textos al usuario en español tuteando; identificadores, códigos y rutas tal cual.
- Cada csproj nuevo lleva su `packages.lock.json`. Sin paquetes nuevos: `Directory.Packages.props`, `global.json` y `.mcp.json`
  no se tocan. `docs/estado.md` **no se toca** en esta rama.
- YAGNI de fase 2: sin catálogo para el distribuidor, sin `IWholesaleOrderIntake`, sin `OrderSource.Wholesale`, sin eventos de
  vuelta, sin columnas para fase 3 (salvo `BillingCompanyId`, que esta fase ya usa: D-2).

## Decisiones de este plan (no del owner ni del spec)

Las marcadas (P) necesitan visto bueno del owner; las demás se pueden revertir sin tocar el contrato.

| # | Decisión | Por qué |
|---|---|---|
| D-1 | La invitación es hija 1:1 del agregado `Distributor` (tabla `wholesale.distributor_invitations`, PK `distributor_id`). Reenviar rota token y vigencia sobre la misma fila: el link viejo pasa a 404 | Una sola `version` para la relación y su invitación; mismo criterio que la re-invitación de miembros |
| D-2 | (P) La identidad del mayorista en el `Supplier` del distribuidor sale de una **empresa facturadora** elegida al invitar (`billingCompanyId`, editable): `Name` = nombre del tenant mayorista, `BusinessName` = nombre de la empresa, documento, país, ciudad, teléfono, correo y dirección de la empresa. Sólo empresas con documento `NIT`, `CC`, `CE` o `PASAPORTE` (los que acepta `Supplier`) | Companies no tiene «empresa por defecto» y `Supplier` exige documento y ciudad; el spec no dice de dónde salen. Fase 3 reutiliza el campo como empresa facturadora del pedido |
| D-3 | La aceptación son pasos idempotentes sin transacción común: tenant (id reservado al invitar), módulos, proveedor (por `LinkedTenantId` o documento), cliente (por documento), y al final el commit de `wholesale` que consume la invitación y activa la relación. Un fallo a mitad se reintenta con el mismo link | Cada módulo tiene su unidad de trabajo (precedente `RegistrationEndpoints`); así un reintento nunca crea un segundo tenant ni deja la invitación gastada con la relación a medias |
| D-4 | Quien no tiene cuenta abre sesión con el link: `POST …/session` (Google) y `POST …/session-with-password`. El usuario se aprovisiona ahí, por el correo de la invitación, no al invitar | El login de Google es sólo por invitación (`ProviderLinkingService.cs:45-50`: `invitation_required`); el token prueba el buzón igual que `accept-with-password` de miembros |
| D-5 | (P) Vincular un tenant existente prende `catalog`, `suppliers` y `purchases` (sólo los que falten), con `TenantChange` motivo `contract` y nota «Invitación de mayorista: {nombre}». Las filas nuevas llevan origen `invitation`, que se agrega al `CHECK` de origen | `purchases` depende de `catalog` (`TenantModuleKey.cs:131`); sin él `TenantModuleChangeBatch.Plan` rechaza el lote. El spec sólo nombra los dos |
| D-6 | Vencida → 410 con `ResourceGoneException` nuevo en BuildingBlocks y su línea en `ApiExceptionHandler` | El spec pide 410 y hoy no hay mapeo; la invitación de miembros vencida responde 422 |
| D-7 | El `Customer` del mayorista sale de `ICustomerProvisioning.CreateFromRegisterAsync` (razón social, `NIT`, correo de la invitación) con el **usuario que invitó** como actor y origen `"wholesale"`; si el documento ya existe se reutiliza ese cliente (precedente `ServiceContractsCustomerProvisioning.cs:52-63`) | Quien acepta no es miembro del mayorista; la bitácora del cliente nombra a alguien de la casa |
| D-8 | Proveedor vinculado: documento (tipo y número) bloqueado en el `PUT` (422 `suppliers.supplier.linked_identity_locked`); nombre, contactos, cuentas y desactivar siguen libres. Si el distribuidor ya tenía un proveedor con ese documento y sin vínculo, se vincula ése en vez de crear otro | `Supplier` no tiene borrado; «no se puede borrar mientras tenga órdenes abiertas» es de fase 3. Un duplicado chocaría con `IX_suppliers_tenant_identification` |
| D-9 | Visibilidad `Categories` incluye las descendientes; los ids deben existir en el tenant al guardar; `AllActive` guarda la lista vacía | Igual que el filtro de categoría de la tienda (`StoreCatalogReader.cs:42-52`) |
| D-10 | Precio de lista = `PriceRounding.Apply(base × (1 − % / 100))` con la regla de redondeo del mayorista para su moneda por defecto (`ITenantPriceRounding`). Variante sin precio en esa moneda, o producto o variante inactivos: no se ofrece | Con COP sin decimales, 2 decimales darían precios que el mayorista nunca pondría. Fase 3 reutiliza `WholesalePricing.ListPrice` |
| D-11 | Vista previa por cuerpo (la lista sin guardar): hasta 20 variantes vendibles ordenadas por nombre de producto y posición, más `totalProducts` | La pantalla «Así lo ve el distribuidor» se pinta mientras se edita, como `POST /purchases/orders/preview` |
| D-12 | Nombre de lista único por tenant sin importar mayúsculas → 409 `wholesale.price_list.name_taken` | Dos listas con el mismo nombre no se distinguen en el selector |
| D-13 | Un documento (NIT) por mayorista, en cualquier estado → 409 `wholesale.distributor.identification_taken` | Dos relaciones con el mismo NIT terminarían en el mismo `Customer` |
| D-14 | Borrar un distribuidor sólo mientras `Invited` (el link pasa a 404); bloquear sólo desde `Active`; sin «terminar» | Corregir un correo mal escrito sin estado nuevo. El spec dice «No hay terminar en este corte» |
| D-15 | Asesor = membresía **activa** del tenant, sin filtro de rol (precedente `useStoreAdvisors`); el nombre se muestra aunque después la desactiven («Sin nombre» si ya no hay membresía) | El spec no fija rol; fase 3 decide qué hacer con un asesor ido |
| D-16 | Repetir `accept` después de aceptar, con el mismo usuario, responde 200 con el mismo resultado | Doble clic o reintento de red; con el correo atado sólo ese usuario puede llegar |
| D-17 | Si el tenant reservado ya existe (intento anterior cortado), la aceptación termina sobre él, sin importar el `mode` del cuerpo | Es la única forma de no dejar un tenant huérfano del intento anterior |
| D-18 | El worker de correo lee el payload con `JsonDocument` y el nombre del evento literal, sin referenciar Wholesale (precedente `SupplierExportDeliveryWorker`); el destinatario va en el payload (precedente `BuyerEmailDeliveryWorker`). Base del link: `Notifications:WholesaleInvitationUrl` (precedente `Notifications:InvitationUrl`) | Pedido del orquestador |
| D-19 | Sin `IUserReferenceProbe` de Wholesale: `InvitedByUserId` y `AcceptedByUserId` son trazas, no punteros que la relación necesite vivos | YAGNI; Identity no pierde nada que Wholesale requiera |
| D-20 | `GET /wholesale-invitations/{token}` anónimo devuelve el correo completo y `hasPassword` | Mismo contrato que `GET /invitations/{token}` (`InvitationEndpoints.cs:56-70`) |
| D-21 | `InvitedDistributorModules` vive en `Modules.Wholesale.Application` (`WholesaleModuleSets`), no en Tenancy | Es una regla de Mayorista sobre claves de Tenancy |
| D-22 | `status` desconocido en la lista → 422 `validation.failed` con `errors.Status`; `pageSize` 50 por defecto, tope 200; orden `createdAt` desc | Copia de D-12/D-13 de fase 1 |
| D-23 | Una categoría borrada después de guardar la lista desaparece del detalle y de la vista previa; el siguiente `PUT` guarda el conjunto limpio | El detalle no puede pintar una categoría sin nombre |
| D-24 | Auditoría en el tenant del mayorista: `wholesale.price_list.{created,updated,deleted}`, `wholesale.distributor.{invited,updated,invitation_resent,blocked,unblocked,deleted,accepted}`; `resourceType` `price_list` / `distributor`. En el distribuidor audita Suppliers (`suppliers.supplier.linked`) y Tenancy (`tenancy.tenant.registered`, `tenancy.tenant_modules.changed`) | La traza queda en el tenant dueño del dato |

## Review Focus

1. **Doble clic o dos pestañas en «Aceptar».** Termina con un solo tenant, un proveedor vinculado, un cliente y la relación
   `Active`; la segunda respuesta es la misma 200. Pruebas: `AcceptingTwiceBySameUserReturnsTheSameResult` (Task 5, unitaria) y
   `TwoConcurrentAcceptsCreateOneTenantOneSupplierOneCustomer` (Task 7, integración pendiente).
2. **Falla a mitad de la aceptación y reintento.** El paso del cliente lanza; la invitación sigue `pending` y el reintento usa el
   tenant reservado, sin crear otro. Prueba: `RetryAfterAFailedCustomerStepReusesTheReservedTenant` (Task 5).
3. **Correo con otras mayúsculas o espacios.** Invitación a `ana@distri.co`, sesión de ` Ana@Distri.CO` → acepta; otra cuenta → 403
   `wholesale.invitation.email_mismatch` sin tocar nada. Pruebas: `EmailMatchIgnoresCaseAndSurroundingSpaces` y
   `AnotherAccountIsForbiddenAndNothingIsWritten` (Task 5).
4. **Reenviar invalida el link anterior.** El token viejo → 404, el nuevo vale 72 h desde el reenvío. Pruebas:
   `ResendRotatesTheTokenAndRenewsTheExpiry` (Task 2) y `TheOldLinkIs404AfterAResend` (Task 7, pendiente).
5. **Vista previa y categorías que cambian.** Una variante sin precio en la moneda del mayorista nunca aparece; una categoría
   borrada después de guardar desaparece del detalle y de la vista previa sin 500. Pruebas:
   `PreviewSkipsVariantsWithoutPriceInTheWholesalerCurrency` y `ADeletedCategoryDisappearsFromTheDetailAndThePreview` (Task 4).

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
| --- | --- | --- |
| `src/Modules/Wholesale/Modules.Wholesale.Domain/{….csproj, PriceList.cs, PriceListId.cs, PriceListVisibility.cs, WholesalePricing.cs, Distributor.cs, DistributorId.cs, DistributorStatus.cs, DistributorAction.cs, DistributorInvitation.cs, InvitationViewState.cs, WholesaleDomainException.cs, WholesaleErrorCodes.cs}` | Crear | Agregados y reglas |
| `src/Modules/Wholesale/Modules.Wholesale.Application/{….csproj, WholesalePermissions.cs, WholesaleAuthorization.cs, WholesaleConflicts.cs, WholesaleModuleSets.cs, WholesaleAuditActions.cs, Ports.cs, PriceListDtos.cs, PriceListWriteRules.cs, CreatePriceList.cs, UpdatePriceList.cs, DeletePriceList.cs, GetPriceList.cs, ListPriceLists.cs, PreviewPriceList.cs, DistributorDtos.cs, DistributorComposer.cs, DistributorPaging.cs, InviteDistributor.cs, UpdateDistributor.cs, TransitionDistributor.cs, DeleteDistributor.cs, GetDistributor.cs, ListDistributors.cs, GetDistributorOptions.cs, DistributorInvitationService.cs}` | Crear | Casos de uso, puertos, contrato |
| `src/Modules/Wholesale/Modules.Wholesale.Infrastructure/{….csproj, WholesaleInfrastructureExtensions.cs, WholesaleDatabaseInitializer.cs, Persistence/{WholesaleDbContext.cs, WholesaleDbContextFactory.cs, PriceListRepository.cs, DistributorRepository.cs, WholesaleUnitOfWork.cs, WholesaleAuditPublisher.cs, WholesaleEventPublisher.cs, LikeSearch.cs, Migrations/*_InitialWholesale.cs}}` | Crear | Persistencia, outbox |
| `src/Modules/Wholesale/Modules.Wholesale.Api/{….csproj, WholesaleEndpoints.cs, WholesaleIfMatch.cs}` | Crear | Rutas del mayorista |
| `src/Api/WholesaleInvitationEndpoints.cs` | Crear | Rutas anónimas y de pre-tenant (composición Identity + Tenancy + Wholesale) |
| `src/Bootstrapper/SensitivePaths.cs`, `src/Api/RequestFailureCapture.cs:67,94`, `src/Api/RequestFailureLoggingMiddleware.cs:71` | Crear / Modificar | El token no queda en `platform.request_failures` ni en el log |
| `src/Bootstrapper/{WholesaleCatalogReader.cs, WholesaleCompanyLookup.cs, WholesaleMemberDirectory.cs, WholesaleSupplierLinker.cs, WholesaleCustomerProvisioning.cs}` | Crear | Adaptadores |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Modificar | Handlers (junto a Purchases `:481-511`), validadores (`:1361`), `AddWholesaleInfrastructure` (`:1381`), adaptadores (`:1785-1787`), roles admin (`:2261`) y advisor (`:2284-2343`), definiciones (después de `:3238`), políticas (después de `:3761`) |
| `src/Bootstrapper/Bootstrapper.csproj`, `src/Api/Api.csproj`, `Backend.slnx`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj` | Modificar | Referencias y solución |
| `src/Api/Program.cs` | Modificar | `MapWholesaleEndpoints()` y `MapWholesaleInvitationEndpoints()` junto a `:399`; `InitializeWholesaleDatabaseAsync` después de Purchases (`:527`), dentro de `RunUnderStartupLockAsync` |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/{TenantModuleKey.cs, TenantModule.cs}`, `…Application/{ITenantRegistration.cs, TenantRegistrationService.cs, ITenantModuleGrants.cs, TenantModuleGrantService.cs, ITenantOwnership.cs}`, `…Infrastructure/{TenancyInfrastructureExtensions.cs, Persistence/TenancyDbContext.cs:150-154, Persistence/TenantOwnershipQuery.cs, Persistence/Migrations/*_AddWholesaleModuleKey.cs}` | Modificar / Crear | Clave, origen, alta con id reservado, prender módulos, tenants propios |
| `src/BuildingBlocks/BuildingBlocks.Application/ApplicationExceptions.cs`, `src/Api/ApiExceptionHandler.cs:162-193` | Modificar | 410 |
| `src/Modules/Identity/Modules.Identity.Application/{IUserDirectory.cs, UserDirectory.cs}` | Modificar | Usuario por correo |
| `src/Modules/Suppliers/Modules.Suppliers.Domain/Supplier.cs`, `…Application/{ISupplierRepository.cs, SupplierLinking.cs (nuevo), SupplierAuditActions.cs, SuppliersDtos.cs, SupplierMapping.cs, GetSupplier.cs, ListSuppliers.cs}`, `…Infrastructure/Persistence/{SuppliersDbContext.cs, SupplierRepository.cs, SuppliersUnitOfWork.cs, Migrations/*_AddSupplierLinkedTenant.cs}` | Modificar / Crear | `LinkedTenantId` |
| `src/Modules/Notifications/Modules.Notifications.Application/WholesaleInvitationEmailTemplate.cs`, `…Infrastructure/{Messaging/WholesaleInvitationDeliveryWorker.cs, NotificationsOptions.cs, NotificationsOptionsValidator.cs, NotificationsInfrastructureExtensions.cs:47-60}` | Crear / Modificar | Correo |
| `src/Api/appsettings.json:16`, `src/Api/appsettings.example.json:59`, `k8s/prod-configMap.yaml:83` | Modificar | `Notifications:WholesaleInvitationUrl` |
| `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/*`, `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/*` | Crear | Pruebas del módulo |
| `tests/Modules/Tenancy/…`, `tests/Modules/Suppliers/…`, `tests/Modules/Identity/…`, `tests/Modules/Notifications/…` | Modificar / Crear | Ver cada tarea |
| `tests/ArchitectureTests/ArchitectureTests/{WholesaleLayerTests.cs, CompositionRootTests.cs}` | Crear / Modificar | Capas y 92 permisos |
| `README.md` (módulos y tabla de endpoints, junto a las filas de Purchases), `CLAUDE.md` (gotchas) | Modificar | Documentación |

**Comandos** (PowerShell, desde la raíz del worktree). Unitarias, sí durante la ejecución:

```powershell
dotnet build src/Api
dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Suppliers/Modules.Suppliers.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~<Clase>"
```

Integración, **pendiente para el owner** (cada tarea lista su filtro): `dotnet build` del proyecto de pruebas sí corre en la
tarea; `dotnet test` de estos proyectos, no.

---

## Contrato HTTP

Todas las respuestas de error son ProblemDetails con `code`. Las escrituras sobre un recurso existente exigen `If-Match` con la
`version` (`"7"` o `W/"7"`, como `CatalogIfMatch`): 412 `concurrency.conflict` si no coincide, 428
`precondition.if_match_required` si falta. Montos y porcentajes como decimales JSON. Enums por nombre.

### Listas de precios — `/api/v1/tenants/{tenantId}/wholesale/price-lists`

Permiso: `wholesale.price_list.manage` en todas (no hay permiso de lectura aparte).

| Método y ruta | Cuerpo | Respuesta |
| --- | --- | --- |
| `GET /` | — | `{ items: PriceListSummary[] }` ordenadas por nombre (sin paginar; D-12) |
| `GET /{id}` | — | `PriceListDetail` |
| `POST /` | `PriceListWrite` | 201 `PriceListDetail`, `Location` al detalle |
| `PUT /{id}` | `PriceListWrite` | `PriceListDetail` (`If-Match`) |
| `DELETE /{id}` | — | 204 (`If-Match`); 409 `wholesale.price_list.in_use` si algún distribuidor (cualquier estado) la usa |
| `POST /preview` | `{ discountPercent, visibility, categoryIds }` | `PriceListPreview` — no escribe; mismas validaciones que `PriceListWrite` salvo `name` |

```
PriceListWrite    { name, discountPercent, visibility: 'AllActive'|'Categories', categoryIds: Guid[] }
PriceListSummary  { id, name, discountPercent, visibility, categoryCount, distributorCount, version }
PriceListDetail   { id, name, discountPercent, visibility, categories: [{ id, name }], distributorCount,
                    version, createdAt, updatedAt }
PriceListPreview  { currency, totalProducts,
                    items: [{ variantId, code, name, basePrice, listPrice }] }   // ≤ 20, D-11
```

- `name` recortado, 1-120. `discountPercent` 0-100 con a lo sumo 2 decimales. `categoryIds`: con `Categories`, 1-200 ids sin
  repetir; con `AllActive` se ignora y se guarda vacía. Claves del 422 de validación: `Name`, `DiscountPercent`, `Visibility`,
  `CategoryIds`, `CategoryIds[1]` (repetida).
- `categories` del detalle: sólo las que siguen existiendo, ordenadas por nombre (D-23). `categoryCount` cuenta las guardadas.
- `listPrice` = `basePrice × (1 − discountPercent / 100)` redondeado con la regla del mayorista (D-10); `basePrice` es el precio de
  la variante en `currency`, la moneda por defecto del mayorista. `name` es el nombre visible de la variante
  (`CatalogVariantReads.DisplayNameOf`). Con `Categories`, el filtro incluye las descendientes (D-9).
- Errores: 404 `wholesale.price_list.not_found`; 409 `wholesale.price_list.name_taken`, `wholesale.price_list.in_use`; 422
  `wholesale.price_list.category_not_found` (algún id no es del tenant), `validation.failed`.

### Distribuidores — `/api/v1/tenants/{tenantId}/wholesale/distributors`

Lectura: `wholesale.distributor.read`. Escritura y `GET /options`: `wholesale.distributor.manage`.

| Método y ruta | Cuerpo | Respuesta |
| --- | --- | --- |
| `GET /` `?status=&search=&page=&pageSize=` | — | `{ items: DistributorSummary[], total, page, pageSize, counts: { Invited, Active, Blocked, all } }` |
| `GET /options` | — | `DistributorOptions` |
| `GET /{id}` | — | `DistributorDetail` |
| `POST /` | `{ email, businessName, identificationNumber, contactName, priceListId, advisorId, billingCompanyId }` | 201 `DistributorDetail` (`Invited`); manda el correo |
| `PUT /{id}` | `{ priceListId, advisorId, billingCompanyId }` | `DistributorDetail` (`If-Match`; cualquier estado) |
| `POST /{id}/resend-invitation` | — | `DistributorDetail` (`If-Match`; sólo `Invited`): token y vigencia nuevos, correo nuevo |
| `POST /{id}/block` | — | `DistributorDetail` (`If-Match`; sólo `Active`) |
| `POST /{id}/unblock` | — | `DistributorDetail` (`If-Match`; sólo `Blocked`) |
| `DELETE /{id}` | — | 204 (`If-Match`; sólo `Invited`, D-14) |

```
DistributorSummary { id, status: 'Invited'|'Active'|'Blocked', businessName, identificationNumber, contactName, email,
                     priceList: { id, name, discountPercent }, advisor: { memberId, name },
                     distributorTenant: { id, name } | null, invitationExpired, createdAt }
DistributorDetail  { id, status, version, businessName, identificationNumber, contactName, email,
                     priceList: { id, name, discountPercent }, advisor: { memberId, name },
                     billingCompany: { id, name },
                     distributorTenant: { id, name } | null, customerId: Guid | null,
                     invitation: { sentAt, expiresAt, expired, acceptedAt | null },
                     createdAt, activatedAt | null,
                     allowedActions: ('edit'|'resend'|'delete'|'block'|'unblock')[] }
DistributorOptions { priceLists: [{ id, name, discountPercent }],              // por nombre
                     advisors:   [{ memberId, name }],                         // membresías activas, por nombre (D-15)
                     companies:  [{ id, name, documentType, documentNumber, eligible }] }  // activas; eligible = D-2
```

- `email` recortado y en minúsculas, ≤ 254, formato de correo. `businessName` 1-160, `identificationNumber` 1-32 (las reglas de
  `Customer` y `CustomerIdentification`), `contactName` 1-160. Claves del 422: `Email`, `BusinessName`, `IdentificationNumber`,
  `ContactName`, `PriceListId`, `AdvisorId`, `BillingCompanyId`, `Status`.
- `search`: `businessName`, `identificationNumber` o `email` (ILIKE escapado). `status` uno de `Invited|Active|Blocked` sin
  importar mayúsculas (D-22). `counts` trae las cuatro claves siempre, con `search` aplicado y sin `status`; `all` es la suma.
- `invitationExpired` / `invitation.expired`: `Invited` con `expiresAt` pasado. El vencimiento es perezoso: no hay estado
  `Expired` en la relación.
- `distributorTenant.name` es el nombre vigente del tenant; `advisor.name` el nombre visible de la membresía o `"Sin nombre"`.
- `allowedActions` (con `wholesale.distributor.manage`; sin él, `[]`), siempre en este orden: `Invited` → `edit, resend,
  delete`; `Active` → `edit, block`; `Blocked` → `edit, unblock`.
- Errores: 404 `wholesale.distributor.not_found`, `wholesale.price_list.not_found` (lista del cuerpo); 409
  `wholesale.distributor.identification_taken` (D-13), `wholesale.distributor.invalid_transition` (resend/block/unblock/delete
  fuera de su estado); 422 `wholesale.distributor.advisor_invalid` (no es membresía activa del tenant),
  `wholesale.distributor.company_invalid` (no existe, inactiva o con documento no admitido, D-2), `validation.failed`.

### Invitación — `/api/v1/wholesale-invitations/{token}` (fuera del tenant)

El link del correo es `{Notifications:WholesaleInvitationUrl}/{token}`, por defecto
`http://localhost:3002/wholesale-invitations/{token}`.

| Método y ruta | Autenticación | Límite | Cuerpo | Respuesta |
| --- | --- | --- | --- | --- |
| `GET /` | anónimo | `Public` | — | `InvitationSummary` |
| `POST /session` | bearer de Google (o stub) | `Credentials` | — | `SessionResponse` (el de `/auth/session`) + cookie |
| `POST /session-with-password` | anónimo | `Credentials` | `{ password }` | `SessionResponse` + cookie |
| `GET /linkable-tenants` | sesión QEP | `Public` | — | `{ tenants: LinkableTenant[] }` |
| `POST /accept` | sesión QEP | `Public` | `AcceptInvitation` | `AcceptedInvitation` |

```
InvitationSummary  { wholesalerName, businessName, contactName, email,
                     status: 'pending'|'accepted'|'expired', expiresAt, hasPassword }
LinkableTenant     { tenantId, name, linkable, ineligibleReason: null|'same_tenant'|'already_linked',
                     modulesToEnable: string[] }          // claves que faltan de catalog, suppliers, purchases
AcceptInvitation   { mode: 'NewTenant', displayName, slug } | { mode: 'LinkTenant', tenantId }
AcceptedInvitation { tenantId, tenantName, created, enabledModules: string[] }
```

- `GET /` (D-20): `status` derivado (vencimiento perezoso). `hasPassword` dice si el correo ya tiene usuario con contraseña.
- `POST /session`: el correo del token de Google tiene que estar verificado y ser el de la invitación (sin importar mayúsculas
  ni espacios); el usuario se aprovisiona por ese correo (`IIdentityProvisioning`) y se vincula (`IProviderLinking`).
  `POST /session-with-password`: sin credencial la crea (reglas de contraseña, `errors.Password`); con credencial la verifica
  (401 `identity.credentials.invalid`); en los dos casos marca el correo verificado (`IPasswordCredentials.AcceptInvitationAsync`).
  Las dos exigen invitación `pending`.
- `GET /linkable-tenants`: los tenants `Active` donde la sesión es **owner** (`Tenant.OwnerMembershipId`), todos, con `linkable`
  e `ineligibleReason`; el del mayorista sale `same_tenant` y uno ya vinculado a este mayorista, `already_linked`. Exige el mismo correo.
- `POST /accept` (D-3, D-16, D-17): correo de la sesión = correo de la invitación. `NewTenant`: crea el tenant con
  `InvitedDistributorModules` (`catalog, customers, companies, quotations, orders, reporting, suppliers, purchases`), aunque
  `Registration:PublicTenantSignupEnabled` esté apagado; quien acepta queda owner (rol `admin`). `displayName` y `slug` con las
  reglas y códigos de `register-tenant`. `LinkTenant`: hay que ser owner; se prenden los módulos que falten (D-5). En los dos
  casos: `Supplier` vinculado en el distribuidor, `Customer` en el mayorista y relación `Active`. `created` es `false` al
  vincular. Después de responder, la SPA relee `/auth/me`.
- Errores: 404 `wholesale.invitation.not_found` (token desconocido, rotado o distribuidor borrado); 410
  `wholesale.invitation.expired`; 409 `wholesale.invitation.already_accepted` (otro usuario o ya consumida en `session*`),
  `wholesale.invitation.unavailable` (el tenant del mayorista no está `Active` o apagó `wholesale`, o su empresa facturadora
  está inactiva o tiene un documento que el proveedor no admite; rulings W14 y W16),
  `wholesale.distributor.already_linked` (ese tenant ya tiene relación con este mayorista, P-1); 403
  `wholesale.relationship.inactive` (la relación ya aceptada está bloqueada; rulings W16 y W18), `wholesale.invitation.email_mismatch`, `wholesale.invitation.not_tenant_owner`; 422 `wholesale.invitation.self_link`,
  `tenancy.slug.invalid`, `tenancy.slug.taken`, `tenancy.settings.display_name.invalid`, `validation.failed` (`Mode`,
  `DisplayName`, `Slug`, `TenantId`); 401 sin sesión.

### Proveedores (cambio sobre `/api/v1/tenants/{tenantId}/suppliers`)

- `SupplierResponse` (GET, POST, PUT, activate, deactivate) suma `linkedTenant: { id, name } | null` («Mayorista en QEP»).
- `SupplierListItemResponse` suma `isLinked: bool`.
- El cuerpo de `POST`/`PUT` no cambia: `linkedTenantId` no existe en `SupplierWriteRequest` y uno enviado se ignora.
- `PUT` de un proveedor vinculado que cambie tipo o número de documento → 422 `suppliers.supplier.linked_identity_locked` (D-8).

### Códigos nuevos (resumen)

| Status | Códigos |
| --- | --- |
| 404 | `wholesale.price_list.not_found`, `wholesale.distributor.not_found`, `wholesale.invitation.not_found` |
| 403 | `wholesale.invitation.email_mismatch`, `wholesale.invitation.not_tenant_owner`, `wholesale.relationship.inactive` (al aceptar, W16/W18) |
| 409 | `wholesale.price_list.in_use`, `wholesale.price_list.name_taken`, `wholesale.distributor.identification_taken`, `wholesale.distributor.invalid_transition`, `wholesale.distributor.already_linked`, `wholesale.invitation.already_accepted`, `wholesale.invitation.unavailable` |
| 410 | `wholesale.invitation.expired` |
| 422 | `wholesale.price_list.category_not_found`, `wholesale.distributor.advisor_invalid`, `wholesale.distributor.company_invalid`, `wholesale.invitation.self_link`, `suppliers.supplier.linked_identity_locked` |

`wholesale.product.not_visible` del spec es de fase 3; `wholesale.relationship.inactive` ya se usa al aceptar una relación
bloqueada (403 como el spec, rulings W16 y W18), y fase 3 decide su uso en los pedidos.

---

## Bloque A — Tenancy, costuras y dominio (sin contenedores)

### Task 1: Tenancy — clave `wholesale`, origen `invitation`, alta con id reservado, prender módulos y tenants propios

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs` (campo `Wholesale = Define("wholesale")` después de
  `Purchases`, `<summary>` «Spec 2026-11-17 (mayorista): listas de precios, distribuidores e invitaciones. Depende de `Orders`
  (el pedido del distribuidor es un `Order`) y queda fuera de `DefaultForNewTenants`: se prende por tenant»; `All` lo suma al
  final; `[Wholesale] = [Orders]`).
- Modify: `…Domain/TenantModule.cs:74-81` (`TenantModuleSources.Invitation = "invitation"`, con `<summary>` «Spec 2026-11-17: la
  fila la creó la aceptación de una invitación de mayorista»).
- Modify: `…Infrastructure/Persistence/TenancyDbContext.cs:150-154` (`'wholesale'` al final del `CHECK` de clave; `'invitation'`
  al final del de origen).
- Create (ef): `…/Migrations/*_AddWholesaleModuleKey.cs` con la forma de `20261117221719_AddPurchasesModuleKey.cs`, recreando
  **los dos** `CHECK`; `Down` borra las filas `module_key = 'wholesale'`, actualiza `source = 'invitation'` a `'signup'` y
  recrea los dos sin los valores nuevos.
- Modify: `…Application/ITenantRegistration.cs`, `…Application/TenantRegistrationService.cs`.
- Create: `…Application/ITenantModuleGrants.cs`, `…Application/TenantModuleGrantService.cs`, `…Application/ITenantOwnership.cs`,
  `…Infrastructure/Persistence/TenantOwnershipQuery.cs`; registro en `TenancyInfrastructureExtensions.cs:47-67`.
- Test: `TenantModuleKeysTests.cs` (`:13` lista; la prueba de `:142` pasa a no exigir `All[^1]`), `OperatorReadHandlerTests.cs:68`,
  `TenantRegistrationServiceTests.cs`, `TenantModuleGrantServiceTests.cs` (nuevo, dobles de `TenantModuleTestDoubles.cs`);
  integración (escribir, no correr): `TenantModulesApiTests.cs:102/104/110/146`, `OperatorConsoleApiTests.cs:195/336`,
  `SeedStartupTests.cs:217/250/312/339/419` (`wholesale` va al final en orden alfabético, después de `support`),
  `TenantOwnershipQueryTests.cs` (nuevo).

**Interfaces:**
- Produces:
  - `TenantModuleKeys.Wholesale`, `All.Count == 21`, `All[^1] == Wholesale`, `DependenciesOf(Wholesale) == [Orders]`,
    `DefaultForNewTenants` sin cambios.
  - En `ITenantRegistration`:
    `Task<bool> RegisterInvitedTenantAsync(TenantId tenantId, Guid ownerUserId, TenantRegistrationData data, IReadOnlyList<TenantModuleKey> modules, string correlationId, CancellationToken cancellationToken)`
    — dentro de `BeginTenantChangeScopeAsync(tenantId)`: si el tenant ya existe y su owner es una membresía de `ownerUserId`,
    devuelve `false` sin escribir; si existe con otro owner, `InvalidOperationException`; si no, crea tenant, membresía `admin`
    con origen `Membership.RegistrationOrigin`, una fila por clave de `modules` con origen `invitation`, audita
    `tenancy.tenant.registered` y devuelve `true`. **No** mira `Registration:PublicTenantSignupEnabled`: lo decide quien llama.
  - `ITenantModuleGrants.EnsureActiveAsync(TenantId tenantId, IReadOnlyList<TenantModuleKey> keys, Guid actorUserId, string note, CancellationToken ct) → Task<IReadOnlyList<TenantModuleKey>>`
    (las que prendió, en el orden de `All`; vacío y sin escribir si ya estaban todas). Mismo cuerpo que
    `ChangeTenantModulesHandler.cs:64-122` sin el permiso de operador: candado, `TenantModuleChangeBatch.Plan` con
    `ChangeReason.Contract`, filas nuevas con origen `invitation`, `TenantChange.ForModule` con `note`, auditoría
    `tenancy.tenant_modules.changed` con `actorUserId`, `ITenantAccessVersion.BumpAsync` antes y después del commit.
  - `sealed record OwnedTenant(Guid TenantId, string DisplayName)`; `ITenantOwnership.ListOwnedActiveTenantsAsync(Guid userId, CancellationToken ct) → Task<IReadOnlyList<OwnedTenant>>`
    (tenants `Active` cuyo `OwnerMembershipId` es una membresía `Active` del usuario, por nombre).

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// TenantModuleKeysTests.cs
AllKeepsTheContractOrder                                      // … "purchases", "wholesale"
WholesaleIsLastDependsOnOrdersAndIsOffForNewTenants           // All[^1].Value == "wholesale"; DependenciesOf == [Orders]; DoesNotContain en DefaultForNewTenants
// TenantRegistrationServiceTests.cs
RegisterInvitedTenantCreatesTheTenantWithTheReservedIdAndTheGivenModules   // tenant.Id == reservado; módulos == los 8 pedidos, Source "invitation"; membresía admin; true
RegisterInvitedTenantTwiceForTheSameOwnerReturnsFalseAndWritesNothing      // segunda llamada: false; repos sin Add nuevos; sin auditoría
RegisterInvitedTenantDoesNotReadTheSignupFlag                              // el servicio no recibe IConfiguration (compila sin él)
// TenantModuleGrantServiceTests.cs
EnsureActiveCreatesMissingRowsWithInvitationSourceAndContractReason        // [catalog activo] + pedir [catalog, suppliers, purchases] → devuelve [suppliers, purchases]; 2 TenantChange con nota
EnsureActiveReactivatesAnInactiveRow                                       // suppliers Inactive → Active; StatusChangedAt = now
EnsureActiveWithEverythingActiveWritesNothing                              // devuelve []; sin SaveChanges ni Bump
EnsureActiveBumpsTheAccessVersionBeforeAndAfterTheCommit                   // 2 llamadas a BumpAsync
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~TenantRegistrationServiceTests|FullyQualifiedName~TenantModuleGrantServiceTests"`
Expected: FAIL de compilación (`'TenantModuleKeys' does not contain a definition for 'Wholesale'`).

- [ ] **Step 3: Implementa** la clave, el origen, los dos `CHECK`, los tres servicios y su registro; genera la migración
`dotnet ef migrations add AddWholesaleModuleKey --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations`
y edita `Up`/`Down` a mano. Actualiza las listas literales de las pruebas de integración del bloque **Files**.

- [ ] **Step 4: Corre y verifica que pasan**

Run: el comando del Step 2, más `--filter "FullyQualifiedName~OperatorReadHandlerTests|FullyQualifiedName~TenancyDbContextMappingTests"`.
Run: `dotnet ef migrations has-pending-model-changes --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext` → `No changes have been made to the model since the last migration.`
Run: `dotnet build tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests` → compila.
**Pendiente para el owner:** `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~SeedStartupTests|FullyQualifiedName~TenantOwnershipQueryTests"`.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Tenancy tests/Modules/Tenancy
git commit -m "feat(tenancy): wholesale module key, invited tenant registration and module grants"
```

---

### Task 2: Proyectos del módulo, dominio y permisos

**Files:**
- Create: los cuatro csproj de `src/Modules/Wholesale/` (copia de `Modules.Purchases.*`, Application con
  `InternalsVisibleTo Modules.Wholesale.UnitTests`), entradas en `Backend.slnx` (junto a las de Purchases), `Bootstrapper.csproj`,
  `Api.csproj`, `ArchitectureTests.csproj`.
- Create: todos los archivos de `Modules.Wholesale.Domain` del mapa; `WholesalePermissions.cs`; `WholesaleInfrastructureExtensions.cs`
  y `WholesaleEndpoints.cs` como clases estáticas vacías (para la prueba de capas).
- Create: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/{….csproj (copia de Purchases.UnitTests, más referencia a
  Modules.Tenancy.Domain), PriceListTests.cs, WholesalePricingTests.cs, DistributorTests.cs}`,
  `tests/ArchitectureTests/ArchitectureTests/WholesaleLayerTests.cs` (copia de `PurchasesLayerTests.cs`).
- Modify: `QepServiceCollectionExtensions.cs` (rol admin `:2261`, rol advisor antes de `:2343`, definiciones después de `:3238`,
  políticas después de `:3761`); `CompositionRootTests.cs` (`:327-349` → 92, prueba nueva junto a `:906`).

**Interfaces:**
- Produces (namespace `Modules.Wholesale.Domain`):
  - `readonly record struct PriceListId(Guid Value)` y `DistributorId(Guid Value)` con `New()` (`Guid.CreateVersion7()`).
  - `enum PriceListVisibility { AllActive, Categories }`, `enum DistributorStatus { Invited, Active, Blocked }`,
    `enum DistributorAction { Edit, Resend, Delete, Block, Unblock }`, `enum InvitationViewState { Pending, Accepted, Expired }`.
  - `static class WholesaleErrorCodes`: una constante por cada código `wholesale.*` de «Códigos nuevos».
  - `sealed class WholesaleDomainException(string code, string message) : DomainException`.
  - `static class WholesalePricing { decimal ListPrice(decimal basePrice, decimal discountPercent, PriceRounding rounding); }`
    (`rounding.Apply(basePrice * (1 - discountPercent / 100m))`; `PriceRounding` de `BuildingBlocks.Domain.Pricing`).
  - `sealed class PriceList`: `PriceListId Id`, `Guid TenantId`, `string Name`, `decimal DiscountPercent`,
    `PriceListVisibility Visibility`, `IReadOnlyList<Guid> CategoryIds`, `long Version`, `DateTimeOffset CreatedAt`, `UpdatedAt`;
    `const int NameMaxLength = 120`, `MaxCategories = 200`.
    - `static PriceList Create(Guid tenantId, string name, decimal discountPercent, PriceListVisibility visibility, IReadOnlyCollection<Guid> categoryIds, DateTimeOffset now)` (Version 1).
    - `void Update(string name, decimal discountPercent, PriceListVisibility visibility, IReadOnlyCollection<Guid> categoryIds, DateTimeOffset now)` (Version + 1).
    - Reglas (código del dominio, el validador da el campo): nombre recortado no vacío ≤ 120; descuento 0-100 con ≤ 2 decimales;
      `Categories` sin ids → error; `AllActive` guarda `[]`; ids sin repetir, en el orden recibido.
  - `sealed class DistributorInvitation`: `string TokenHash`, `DateTimeOffset SentAt`, `ExpiresAt`, `Guid ReservedTenantId`,
    `DateTimeOffset? AcceptedAt`, `Guid? AcceptedByUserId`.
  - `sealed class Distributor`: `DistributorId Id`, `Guid TenantId`, `string BusinessName`, `string IdentificationNumber`,
    `string ContactName`, `string Email`, `PriceListId PriceListId`, `Guid AdvisorMemberId`, `Guid BillingCompanyId`,
    `DistributorStatus Status`, `Guid? DistributorTenantId`, `Guid? CustomerId`, `Guid InvitedByUserId`,
    `DateTimeOffset CreatedAt`, `UpdatedAt`, `DateTimeOffset? ActivatedAt`, `long Version`, `DistributorInvitation Invitation`;
    `static readonly TimeSpan InvitationTimeToLive = TimeSpan.FromHours(72)`.
    - `static Distributor Invite(Guid tenantId, string email, string businessName, string identificationNumber, string contactName, PriceListId priceListId, Guid advisorMemberId, Guid billingCompanyId, Guid invitedByUserId, string tokenHash, Guid reservedTenantId, DateTimeOffset now)`
      (`Invited`, Version 1; correo `Trim().ToLowerInvariant()`; textos recortados).
    - `void ChangeTerms(PriceListId priceListId, Guid advisorMemberId, Guid billingCompanyId, DateTimeOffset now)`.
    - `void ResendInvitation(string tokenHash, DateTimeOffset now)` (sólo `Invited`; `SentAt = now`, `ExpiresAt = now + 72 h`; el
      `ReservedTenantId` **no** cambia).
    - `void Block(DateTimeOffset now)`, `void Unblock(DateTimeOffset now)`, `void EnsureDeletable()`.
    - `InvitationViewState InvitationState(DateTimeOffset now)`; `bool InvitedEmailMatches(string? email)` (recorta y compara
      sin mayúsculas).
    - `void Accept(Guid userId, Guid distributorTenantId, Guid customerId, DateTimeOffset now)` — sólo `Invited` y no vencida
      (`wholesale.invitation.expired` / `already_accepted`): `Active`, `ActivatedAt`, `Invitation.AcceptedAt/AcceptedByUserId`.
    - `IReadOnlyList<DistributorAction> AllowedActions()` (contrato). Toda mutación sube `Version` y fija `UpdatedAt`; una
      transición fuera de estado lanza `wholesale.distributor.invalid_transition`.
- Produces (`Modules.Wholesale.Application`): `WholesalePermissions.PriceListManage = "wholesale.price_list.manage"`,
  `DistributorRead = "wholesale.distributor.read"`, `DistributorManage = "wholesale.distributor.manage"`.
  Definiciones, categoría `"Mayorista"`, `RequiredModules: [ModuleKeys.Wholesale]`:
  `(PriceListManage, "Gestionar listas de precios", "Permite crear, editar y borrar las listas de precios de distribuidores.", "medium") { Shape = new("wholesale.price_list", "Listas de precios", "manage") }`,
  `(DistributorRead, "Ver distribuidores", "Permite ver los distribuidores, su lista de precios y su asesor.", "low") { Shape = new("wholesale.distributor", "Distribuidores", "read") }`,
  `(DistributorManage, "Gestionar distribuidores", "Permite invitar, editar, bloquear y desbloquear distribuidores.", "medium") { Shape = new("wholesale.distributor", "Distribuidores", "manage") }`.
  Rol `admin`: los tres (comentario «Spec 2026-11-17 (mayorista), P-3 aprobada»); rol `advisor`: `DistributorRead`.

- [ ] **Step 1: Crea proyectos, referencias y entradas; `dotnet build src/Api` compila con el módulo vacío.**

- [ ] **Step 2: Escribe las pruebas que fallan**

```csharp
// PriceListTests.cs
CreateTrimsTheNameAndStartsAtVersionOne
DiscountOutsideZeroToHundredOrWithThreeDecimalsIsRejected      // [Theory] -0.01, 100.01, 10.005 → código de dominio
CategoriesVisibilityWithoutIdsIsRejected
AllActiveDropsTheCategoryIds                                    // AllActive + [id] → CategoryIds == []
RepeatedCategoryIdsAreKeptOnce
UpdateBumpsTheVersion
// WholesalePricingTests.cs
ListPriceAppliesTheDiscountAndTheTenantRounding                 // (100000, 12.5, (0, Nearest)) → 87500; (19.99, 10, (2, Nearest)) → 17.99
ZeroDiscountKeepsTheBasePriceAndHundredGivesZero
RoundingUpModeNeverGoesBelowTheExactValue                       // (1000, 33.33, (0, Up)) → 667
// DistributorTests.cs
InviteStartsInvitedWithA72HourInvitationAndALowercaseEmail      // " Ana@Distri.CO " → "ana@distri.co"; ExpiresAt == now + 72h
TheInvitationTimeToLiveMatchesTheMembershipOne                  // == Modules.Tenancy.Domain.Membership.DefaultInvitationTimeToLive
ResendRotatesTheTokenAndRenewsTheExpiry                         // Review Focus 4: TokenHash nuevo; ExpiresAt == later + 72h; ReservedTenantId igual
InvitationStateIsExpiredAfterTheWindowAndAcceptedAfterAccept
AcceptActivatesAndRecordsTenantCustomerAndUser
AcceptAfterExpiryThrowsExpiredAndTwiceThrowsAlreadyAccepted
EmailMatchIgnoresCaseAndSurroundingSpaces                       // Review Focus 3 (dominio)
BlockOnlyFromActiveAndUnblockOnlyFromBlocked                    // [Theory] → invalid_transition
DeleteOnlyWhileInvited
AllowedActionsFollowTheState                                    // Invited → [Edit, Resend, Delete]; Active → [Edit, Block]; Blocked → [Edit, Unblock]
// CompositionRootTests.cs
PermissionDiscoveryFindsTheNinetyTwoConstants                   // 89 → 92, comentario «+ los tres de mayorista (spec 2026-11-17)»
TheWholesalePermissionsRequireTheWholesaleModuleAndGoToAdminAndReadToAdvisor
                                                                // Category "Mayorista"; RequiredModules [Wholesale]; admin los 3; advisor sólo DistributorRead; billing y cashier ninguno
```

- [ ] **Step 3: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests`
Expected: FAIL de compilación (`The type or namespace name 'PriceList' could not be found`).

- [ ] **Step 4: Implementa** el dominio con las firmas de arriba (forma de `PurchaseOrder.cs`), las constantes, las tres
definiciones, las tres políticas (`AddPermissionRequirement`) y los roles.

- [ ] **Step 5: Corre y verifica que pasan**

Run: `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests`
Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WholesaleLayerTests|FullyQualifiedName~CompositionRootTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add Backend.slnx src/Modules/Wholesale src/Bootstrapper src/Api/Api.csproj tests/Modules/Wholesale tests/ArchitectureTests
git commit -m "feat(wholesale): price list and distributor aggregates, module projects and permissions"
```

---

### Task 3: Costuras — 410, usuario por correo y proveedor vinculado

**Files:**
- Modify: `src/BuildingBlocks/BuildingBlocks.Application/ApplicationExceptions.cs` (`sealed class ResourceGoneException(string code, string message) : Exception(message)` con `Code`),
  `src/Api/ApiExceptionHandler.cs:162-193` (`ResourceGoneException value => (StatusCodes.Status410Gone, "Gone", value.Code)`).
- Modify: `src/Modules/Identity/Modules.Identity.Application/{IUserDirectory.cs, UserDirectory.cs}`.
- Modify: `src/Modules/Suppliers/…` del mapa; Create: `…Application/SupplierLinking.cs`, migración
  `dotnet ef migrations add AddSupplierLinkedTenant --project src/Modules/Suppliers/Modules.Suppliers.Infrastructure --context SuppliersDbContext -o Persistence/Migrations`.
- Test: `tests/Modules/Suppliers/Modules.Suppliers.UnitTests/{SupplierTests.cs, SupplierLinkingTests.cs (nuevo)}`,
  `tests/Modules/Identity/Modules.Identity.UnitTests/UserDirectoryTests.cs` (nuevo, dobles de `IdentityApplicationTestDoubles.cs`);
  integración (escribir, no correr): `tests/Modules/Suppliers/Modules.Suppliers.IntegrationTests/LinkedSupplierApiTests.cs`.
  El 410 no tiene prueba unitaria propia (no hay `ApiExceptionHandlerTests`): lo cubre `AnExpiredLinkIs410` (Task 7, pendiente).

**Interfaces:**
- Produces:
  - `ResourceGoneException(string code, string message)` → 410.
  - `IUserDirectory.FindUserIdByEmailAsync(string email, CancellationToken ct) → Task<Guid?>` (normaliza con
    `User.NormalizeEmail`; un correo inválido → `null`).
  - Suppliers dominio: `Guid? LinkedTenantId { get; }`;
    `static Supplier CreateLinked(Guid tenantId, Guid linkedTenantId, SupplierWriteData data, ISupplierPhoneNormalizer phones, DateTimeOffset now)`;
    `void LinkTo(Guid linkedTenantId, DateTimeOffset now)` (ya vinculado a ese → no-op; a otro → `suppliers.supplier.linked_elsewhere`,
    sube `Version`); `Update` lanza `suppliers.supplier.linked_identity_locked` si el proveedor está vinculado y cambia tipo o número.
  - `ISupplierRepository`: `Task<Supplier?> FindByLinkedTenantAsync(Guid tenantId, Guid linkedTenantId, CancellationToken ct)`,
    `Task<Supplier?> FindByIdentificationAsync(Guid tenantId, IdentificationType type, string number, CancellationToken ct)`.
  - `SupplierAuditActions.Linked = "suppliers.supplier.linked"`.
  - `ISupplierLinking.EnsureLinkedAsync(Guid tenantId, Guid linkedTenantId, Guid actorUserId, SupplierWriteData data, CancellationToken ct) → Task<Guid>`
    (`SupplierLinking`, sin `IExecutionContext` ni permisos: es una escritura del sistema): 1) por `LinkedTenantId` → devuelve su id
    sin tocarlo; 2) por documento sin vínculo → `LinkTo`; 3) si no, valida la ciudad con `ISupplierGeographyLookup` y
    `CreateLinked`. Audita `Linked` con `actorUserId`. Si el commit choca con `IX_suppliers_tenant_identification` o con el índice
    nuevo, relee una vez desde el paso 1 (carrera de dos pestañas).
  - DTOs: `SupplierLinkedTenantDto(Guid Id, string Name)`; `SupplierResponse` suma `SupplierLinkedTenantDto? LinkedTenant` (el
    nombre, de `ITenantDirectory.GetDisplayNameAsync`; `<summary>`: «Mayorista en QEP; el nombre es el vigente»);
    `SupplierListItemResponse` suma `bool IsLinked`.
  - Infra: columna `linked_tenant_id uuid null`; índice único parcial `IX_suppliers_tenant_linked_tenant (tenant_id, linked_tenant_id) WHERE linked_tenant_id IS NOT NULL`;
    `SuppliersUnitOfWork` traduce ese índice a `ResourceConflictException("suppliers.supplier.linked_elsewhere", …)`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// SupplierTests.cs
CreateLinkedSetsTheLinkedTenant
UpdateOfALinkedSupplierCannotChangeItsIdentification     // número distinto → "suppliers.supplier.linked_identity_locked"; nombre distinto sí pasa
UpdateOfAnUnlinkedSupplierStillChangesItsIdentification
LinkToAnotherTenantThrowsLinkedElsewhere; LinkToTheSameTenantIsANoOp   // Version igual en el no-op
// SupplierLinkingTests.cs (repos y geografía falsos)
AnAlreadyLinkedSupplierIsReturnedUntouched               // Version igual; sin auditoría
AnUnlinkedSupplierWithTheSameDocumentGetsLinked          // mismo id; LinkedTenantId; auditoría "suppliers.supplier.linked"
WithoutMatchesALinkedSupplierIsCreated
AColombianCityThatDoesNotExistIsRejected                 // "suppliers.supplier.city_not_found" (el código que ya usa CreateSupplier)
// UserDirectoryTests.cs
FindUserIdByEmailNormalizesTheAddress                    // " Ana@X.CO " encuentra al usuario de "ana@x.co"; "no-es-correo" → null
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Suppliers/Modules.Suppliers.UnitTests --filter "FullyQualifiedName~SupplierTests|FullyQualifiedName~SupplierLinkingTests"`
Run: `dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --filter "FullyQualifiedName~UserDirectoryTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** lo anterior y genera la migración. Escribe `LinkedSupplierApiTests`:
`AFormCannotSetOrClearTheLink` (POST/PUT con `linkedTenantId` en el JSON → `linkedTenant` sigue `null` / igual),
`ALinkedSupplierShowsItsWholesalerInDetailAndList` (sembrado por `SuppliersDbContext`), `ChangingTheDocumentOfALinkedSupplierIs422`.

- [ ] **Step 4: Corre y verifica que pasan**

Run: los comandos del Step 2 → PASS.
Run: `dotnet ef migrations has-pending-model-changes --project src/Modules/Suppliers/Modules.Suppliers.Infrastructure --context SuppliersDbContext` → sin cambios.
Run: `dotnet build tests/Modules/Suppliers/Modules.Suppliers.IntegrationTests` → compila.
**Pendiente para el owner:** `dotnet test tests/Modules/Suppliers/Modules.Suppliers.IntegrationTests --filter "FullyQualifiedName~LinkedSupplierApiTests"`.

- [ ] **Step 5: Commit**

```powershell
git add src/BuildingBlocks src/Api/ApiExceptionHandler.cs src/Modules/Identity src/Modules/Suppliers tests/Modules/Suppliers tests/Modules/Identity
git commit -m "feat(suppliers): linked tenant on suppliers, user lookup by email and 410 mapping"
```

**Revisión del bloque A.**

---

## Bloque B — Application e Infrastructure

### Task 4: Application — listas de precios y vista previa

**Files:**
- Create: `WholesaleAuthorization.cs` (copia de `PurchasesAuthorization.cs`), `WholesaleConflicts.cs`, `WholesaleAuditActions.cs`,
  `Ports.cs` (parte de listas), `PriceListDtos.cs`, `PriceListWriteRules.cs`, `CreatePriceList.cs`, `UpdatePriceList.cs`,
  `DeletePriceList.cs`, `GetPriceList.cs`, `ListPriceLists.cs`, `PreviewPriceList.cs`.
- Modify: `QepServiceCollectionExtensions.cs` (handlers junto a Purchases `:481-511`, comentario «Wholesale, spec 2026-11-17. A
  mano: un handler olvidado responde 500»; `AddValidatorsFromAssemblyContaining<CreatePriceListValidator>()` junto a `:1361`).
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/{WholesaleFakes.cs, PriceListWriteRulesTests.cs, PriceListHandlersTests.cs, PreviewPriceListTests.cs}`.

**Interfaces:**
- Consumes: dominio y permisos de Task 2; `IExecutionContext`, `IClock`, `ITenantDefaultCurrency`, `ITenantPriceRounding`.
- Produces:
  - DTOs (records con los nombres del contrato): `PriceListSummaryDto`, `PriceListCategoryDto(Guid Id, string Name)`,
    `PriceListDetailDto`, `PriceListsResponse(IReadOnlyList<PriceListSummaryDto> Items)`, `PriceListPreviewItemDto(Guid VariantId, string Code, string Name, decimal BasePrice, decimal ListPrice)`,
    `PriceListPreviewDto(string Currency, int TotalProducts, IReadOnlyList<PriceListPreviewItemDto> Items)`; `Visibility` como string.
  - Comandos planos (para que `errors` diga `CategoryIds[1]`): `CreatePriceListCommand(Guid TenantId, string Name, decimal DiscountPercent, string Visibility, IReadOnlyList<Guid> CategoryIds) : ICommand<PriceListDetailDto>, IPriceListTermsCommand`,
    `UpdatePriceListCommand(Guid TenantId, Guid PriceListId, long ExpectedVersion, …mismos campos)`,
    `PreviewPriceListCommand(Guid TenantId, decimal DiscountPercent, string Visibility, IReadOnlyList<Guid> CategoryIds) : ICommand<PriceListPreviewDto>, IPriceListTermsCommand`,
    `DeletePriceListCommand(Guid TenantId, Guid PriceListId, long ExpectedVersion) : ICommand<bool>`,
    `GetPriceListQuery(Guid TenantId, Guid PriceListId)`, `ListPriceListsQuery(Guid TenantId)`.
  - `PriceListWriteRules : AbstractValidator<IPriceListTermsCommand>`; los validadores de create/update suman `Name`.
  - `Ports.cs`:
    - `IPriceListRepository { Task<PriceList?> GetAsync(Guid tenantId, PriceListId id, CancellationToken ct); Task<IReadOnlyList<PriceList>> ListAsync(Guid tenantId, CancellationToken ct); Task<IReadOnlyDictionary<PriceListId, PriceList>> FindManyAsync(Guid tenantId, IReadOnlyCollection<PriceListId> ids, CancellationToken ct); Task<bool> NameExistsAsync(Guid tenantId, string name, PriceListId? except, CancellationToken ct); void Add(PriceList list); void Remove(PriceList list); }`
    - `IDistributorUsage { Task<IReadOnlyDictionary<PriceListId, int>> CountByPriceListAsync(Guid tenantId, CancellationToken ct); }` (la implementa el repositorio de distribuidores, Task 6)
    - `sealed record WholesaleCategoryRef(Guid Id, string Name)`; `sealed record WholesaleSampleVariant(Guid VariantId, string Code, string Name, decimal BasePrice)`;
      `IWholesaleCatalogReader { Task<IReadOnlyDictionary<Guid, WholesaleCategoryRef>> FindCategoriesAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct); Task<(IReadOnlyList<WholesaleSampleVariant> Items, int TotalProducts)> SampleAsync(Guid tenantId, string currency, IReadOnlyCollection<Guid>? categoryIds, int take, CancellationToken ct); }`
      (`categoryIds` null = todo el catálogo activo; el adaptador expande descendientes, D-9).
    - `IWholesaleUnitOfWork { Task SaveChangesAsync(CancellationToken ct); }`; `IWholesaleAuditPublisher.Publish(…)` (copia de `IPurchasesAuditPublisher`).
  - `WholesaleConflicts`: `PriceListNotFound(Guid)`, `DistributorNotFound(Guid)`, `InvitationNotFound()`, `Stale()`,
    `EnsureVersion(long actual, long expected)`, `EnsureAllowed(Distributor, DistributorAction)`.
  - Handlers (autorizar `PriceListManage` → validar → cargar o 404 → versión 412 → reglas → mutar → auditar → guardar):
    create/update verifican nombre libre (409 `name_taken`) y categorías (`FindCategoriesAsync`; falta alguna → 422
    `category_not_found`); delete con `CountByPriceListAsync` > 0 → 409 `in_use`; get arma `categories` con las que existen
    (D-23); list con `distributorCount`. `PreviewPriceListHandler`: mismas reglas de términos, moneda de
    `ITenantDefaultCurrency`, regla de `ITenantPriceRounding.GetAsync(tenantId, currency)`, `SampleAsync(…, take: 20)` y
    `WholesalePricing.ListPrice` por ítem. Sin repositorio ni auditoría.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// PriceListWriteRulesTests.cs
AValidCommandPasses
DiscountRulesFailOnTheField                         // -1, 101, 10.005 → "DiscountPercent"
CategoriesVisibilityNeedsAtLeastOneId                // "CategoryIds"
ARepeatedCategoryFailsOnTheSecond                    // "CategoryIds[1]"
UnknownVisibilityFails                               // "Visibility"
NameIsRequiredAndAtMost120                           // sólo create/update
// PriceListHandlersTests.cs (WholesaleFakes)
CreateWithATakenNameIs409NameTaken                   // "Mayoristas" vs "mayoristas"
CreateWithACategoryOfAnotherTenantIs422CategoryNotFound
UpdateWithAStaleVersionIs412
DeleteAListInUseIs409InUse                           // cualquier estado del distribuidor
ADeletedCategoryDisappearsFromTheDetailAndThePreview // Review Focus 5
ListCarriesTheDistributorCountPerList
EveryWriteIsAudited                                  // created, updated, deleted con resourceType "price_list"
AnotherTenantIsForbidden                             // 403 authorization.denied antes de tocar el repositorio
// PreviewPriceListTests.cs
PreviewUsesTheWholesalerCurrencyAndRounding          // COP (0, Nearest), base 10000, 15 % → 8500
PreviewSkipsVariantsWithoutPriceInTheWholesalerCurrency   // Review Focus 5: el lector falso sólo devuelve variantes con precio; la prueba de adaptador va en Task 6
PreviewPassesNullCategoriesForAllActive
PreviewWritesNothing                                 // sin SaveChanges ni auditoría
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~PriceList|FullyQualifiedName~PreviewPriceList"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** Application de listas y registra handlers y validadores.

- [ ] **Step 4: Corre y verifica que pasan**, más
`dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~EveryCommandAndQueryHasItsHandlerRegistered|FullyQualifiedName~WholesaleLayerTests"` → PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Wholesale/Modules.Wholesale.Application src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Wholesale
git commit -m "feat(wholesale): price list use cases and list price preview"
```

---

### Task 5: Application — distribuidores, invitación y aceptación

**Files:**
- Create: `WholesaleModuleSets.cs`, `DistributorDtos.cs`, `DistributorComposer.cs`, `DistributorPaging.cs` (copia de
  `PurchaseOrderPaging`), `InviteDistributor.cs`, `UpdateDistributor.cs`, `TransitionDistributor.cs`, `DeleteDistributor.cs`,
  `GetDistributor.cs`, `ListDistributors.cs`, `GetDistributorOptions.cs`, `DistributorInvitationService.cs`; amplía `Ports.cs`.
- Modify: `QepServiceCollectionExtensions.cs` (handlers, `DistributorComposer`, `IDistributorInvitationService` scoped).
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/{WholesaleModuleSetsTests.cs, DistributorWriteRulesTests.cs, DistributorHandlersTests.cs, DistributorComposerTests.cs, DistributorInvitationServiceTests.cs}`.

**Interfaces:**
- Consumes: Task 1 (`ITenantRegistration.RegisterInvitedTenantAsync`, `ITenantModuleGrants`, `ITenantOwnership`), Task 2, Task 4;
  de Tenancy: `InvitationTokens`, `ITenantDirectory` (nombre y estado), `ITenantModules.FindAsync` (módulo `wholesale` del
  mayorista), `IMembershipDirectory.FindActiveMembershipIdAsync`.
- Produces:
  - `WholesaleModuleSets.InvitedDistributor = [Catalog, Customers, Companies, Quotations, Orders, Reporting, Suppliers, Purchases]`,
    `LinkedDistributorRequired = [Catalog, Suppliers, Purchases]`.
  - DTOs del contrato: `DistributorSummaryDto`, `DistributorDetailDto`, `DistributorsResponse(Items, Total, Page, PageSize, DistributorStatusCountsDto Counts)`
    (`[JsonPropertyName]` `"Invited"`, `"Active"`, `"Blocked"`, `"all"`, como `PurchaseOrderStatusCountsDto`), `DistributorOptionsDto`,
    `InvitationSummaryDto(string WholesalerName, string BusinessName, string ContactName, string Email, string Status, DateTimeOffset ExpiresAt)`
    (`hasPassword` lo suma la composición en `src/Api`), `LinkableTenantDto(Guid TenantId, string Name, bool Linkable, string? Reason, IReadOnlyList<string> ModulesToEnable)`,
    `AcceptedInvitationDto(Guid TenantId, string TenantName, bool Created, IReadOnlyList<string> EnabledModules)`.
  - Comandos: `InviteDistributorCommand(Guid TenantId, string Email, string BusinessName, string IdentificationNumber, string ContactName, Guid PriceListId, Guid AdvisorId, Guid BillingCompanyId, string CorrelationId) : ICommand<DistributorDetailDto>`,
    `UpdateDistributorCommand(Guid TenantId, Guid DistributorId, long ExpectedVersion, Guid PriceListId, Guid AdvisorId, Guid BillingCompanyId)`,
    `TransitionDistributorCommand(Guid TenantId, Guid DistributorId, long ExpectedVersion, DistributorAction Action, string CorrelationId)` (`Resend`, `Block`, `Unblock`),
    `DeleteDistributorCommand(Guid TenantId, Guid DistributorId, long ExpectedVersion) : ICommand<bool>`,
    `GetDistributorQuery`, `ListDistributorsQuery(Guid TenantId, string? Status, string? Search, int Page, int PageSize)`, `GetDistributorOptionsQuery(Guid TenantId)`.
  - `Ports.cs` (suma):
    - `IDistributorRepository : IDistributorUsage { Task<Distributor?> GetAsync(Guid tenantId, DistributorId id, CancellationToken ct); Task<Distributor?> FindByTokenHashAsync(string tokenHash, CancellationToken ct); Task<bool> IdentificationExistsAsync(Guid tenantId, string identificationNumber, CancellationToken ct); Task<bool> IsLinkedAsync(Guid wholesalerTenantId, Guid distributorTenantId, CancellationToken ct); Task<IReadOnlySet<Guid>> LinkedTenantIdsAsync(Guid wholesalerTenantId, CancellationToken ct); Task<(IReadOnlyList<Distributor> Items, int Total)> SearchAsync(Guid tenantId, DistributorStatus? status, string? search, int page, int pageSize, CancellationToken ct); Task<IReadOnlyDictionary<DistributorStatus, int>> CountByStatusAsync(Guid tenantId, string? search, CancellationToken ct); void Add(Distributor d); void Remove(Distributor d); }`
      (`FindByTokenHashAsync` es la única lectura sin `tenantId`: el hash es la llave y el llamador no tiene tenant).
    - `IWholesaleEventPublisher.DistributorInvited(Guid tenantId, Guid distributorId, string email, string businessName, string token, DateTimeOffset expiresAt, string correlationId, DateTimeOffset occurredAt)`
      (evento `wholesale.distributor-invited.v1`).
    - `sealed record WholesaleCompanyRef(Guid Id, string Name, string DocumentType, string DocumentNumber, string Country, Guid? CityId, string? CityName, string? Phone, string? Email, string? Address, bool IsActive, bool Eligible)`;
      `IWholesaleCompanyLookup { Task<WholesaleCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken ct); Task<IReadOnlyList<WholesaleCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken ct); }`.
    - `sealed record WholesaleMember(Guid MemberId, string Name)`;
      `IWholesaleMemberDirectory { Task<IReadOnlyList<WholesaleMember>> ListActiveAsync(Guid tenantId, CancellationToken ct); Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct); }`.
    - `IWholesaleSupplierLinker.EnsureLinkedSupplierAsync(Guid distributorTenantId, Guid wholesalerTenantId, string wholesalerName, WholesaleCompanyRef company, Guid actorUserId, CancellationToken ct) → Task<Guid>`.
    - `IWholesaleCustomerProvisioning.EnsureCustomerAsync(Guid wholesalerTenantId, Guid actorUserId, string businessName, string identificationNumber, string email, CancellationToken ct) → Task<Guid>`.
  - `DistributorComposer` (scoped): `DetailAsync(Distributor)`, `SummariesAsync(Guid tenantId, IReadOnlyList<Distributor>)` en
    lote (listas, nombres de miembros, nombre de empresa, nombres de tenants distribuidores), `"Sin nombre"`, `allowedActions`
    filtrado por `HasPermission(DistributorManage)`, acciones en minúscula.
  - Handlers del mayorista: `InviteDistributorHandler` (`DistributorManage`; validador; lista existe o 404; asesor activo o 422
    `advisor_invalid`; empresa `Eligible` y activa o 422 `company_invalid`; NIT libre o 409; `InvitationTokens.Generate()`;
    `Distributor.Invite(…, reservedTenantId: Guid.CreateVersion7())`; audita `invited` (sin el token en `changedFields`); publica el
    evento; guarda). `UpdateDistributorHandler` repite las tres verificaciones. `TransitionDistributorHandler`: `Resend` genera
    token nuevo, publica otra vez y audita `invitation_resent`; `Block`/`Unblock` auditan. `DeleteDistributorHandler`: sólo
    `Invited`. La lista con `DistributorPaging` y `counts` de cuatro claves.
  - `IDistributorInvitationService` (`DistributorInvitationService`, sin `IExecutionContext`: el tenant del mayorista sale de la
    invitación):
    - `Task<InvitationSummaryDto> FindAsync(string token, CancellationToken ct)` (404 si no hay).
    - `Task<Distributor> RequirePendingAsync(string token, CancellationToken ct)` (404/410/409 en ese orden; lo usan las dos rutas
      `session*` antes de tocar Identity).
    - `Task<IReadOnlyList<LinkableTenantDto>> ListLinkableTenantsAsync(string token, Guid userId, string? userEmail, CancellationToken ct)`.
    - `Task<AcceptedInvitationDto> AcceptAsync(string token, Guid userId, string? userEmail, AcceptInvitationChoice choice, string correlationId, CancellationToken ct)`,
      con `abstract record AcceptInvitationChoice`; `sealed record NewTenantChoice(TenantRegistrationData Data)`; `sealed record LinkTenantChoice(Guid TenantId)`.
      Orden fijo (D-3): 1) invitación por hash (404); 2) `InvitedEmailMatches` (403 `email_mismatch`, antes de revelar más);
      3) ya `Accepted`: mismo usuario → devuelve el resultado guardado (D-16), otro → 409; vencida → 410; 4) tenant del mayorista
      `Active` y `wholesale` activo, si no 409 `unavailable`; 5) destino: si existe el tenant reservado → ése (D-17); si no,
      `NewTenant` → `EnsureRegistrableAsync` + `RegisterInvitedTenantAsync(reservado, userId, data, InvitedDistributor)`;
      `LinkTenant` → owner por `ITenantOwnership` (403 `not_tenant_owner`), distinto del mayorista (422 `self_link`), sin relación
      previa con este mayorista (409 `already_linked`), `EnsureActiveAsync(tenant, LinkedDistributorRequired, userId, nota)`;
      6) `EnsureLinkedSupplierAsync`; 7) `EnsureCustomerAsync(…, actor: InvitedByUserId)` (D-7); 8) `distributor.Accept(…)`,
      audita `accepted` (actor `userId`) y guarda; un 412 o el índice único de `(tenant_id, distributor_tenant_id)` releen: ya
      aceptada por este usuario → resultado igual; otro tenant vinculado → 409 `already_linked`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// WholesaleModuleSetsTests.cs
InvitedDistributorModulesAreTheEightOfTheSpecWithoutShopify
EveryModuleSetIsClosedUnderItsDependencies             // para cada clave, DependenciesOf ⊆ el conjunto (InvitedDistributor y LinkedDistributorRequired)
// DistributorWriteRulesTests.cs
InviteRequiresAValidEmailAndTheTextLimits              // "Email", "BusinessName" (161), "IdentificationNumber" (33), "ContactName"
ListRejectsAnUnknownStatusAndAcceptsAnyCase            // "paused" → "Status"; "active" pasa
// DistributorHandlersTests.cs
InviteStoresOnlyTheHashAndPublishesTheTokenOnce        // repo: TokenHash == HashOf(token del evento); auditoría sin token
InviteWithAnInactiveAdvisorIs422AdvisorInvalid
InviteWithANifCompanyIs422CompanyInvalid               // Eligible false
InviteWithATakenIdentificationIs409
ResendPublishesANewTokenAndAuditsInvitationResent
BlockAndUnblockAreAuditedAndOutOfStateIs409InvalidTransition
DeleteOnlyWhileInvitedAndThenTheTokenIsGone            // FindByTokenHashAsync → null
CountsCarryTheFourKeysEvenAtZero
// DistributorComposerTests.cs
AllowedActionsAreEmptyWithoutTheManagePermission
SummariesResolveNamesInOneCallPerPort                  // un FindNamesAsync y un FindManyAsync para 3 distribuidores
AMissingMemberIsSinNombre
// DistributorInvitationServiceTests.cs (todos los puertos falsos, reloj fijo)
NewTenantCreatesTheReservedTenantSupplierAndCustomerAndActivates   // RegisterInvited con los 8; Supplier con wholesalerName y empresa; Customer con NIT; Status Active; Created true
LinkTenantEnablesOnlyTheMissingModules                  // tenant con catalog → EnsureActive pide [catalog, suppliers, purchases]; EnabledModules ["suppliers","purchases"]
EmailMatchIgnoresCaseAndSurroundingSpaces               // Review Focus 3
AnotherAccountIsForbiddenAndNothingIsWritten            // 403 email_mismatch; ningún puerto de escritura llamado
LinkingATenantYouDoNotOwnIs403; LinkingTheWholesalerItselfIs422SelfLink; LinkingATenantAlreadyLinkedIs409
AnExpiredInvitationIs410AndAnUnknownTokenIs404
AWholesalerWithoutTheModuleIs409Unavailable
AcceptingTwiceBySameUserReturnsTheSameResult            // Review Focus 1
RetryAfterAFailedCustomerStepReusesTheReservedTenant    // Review Focus 2: 1.ª llamada, EnsureCustomer lanza → invitación Pending; 2.ª, RegisterInvited devuelve false (ya existe) y termina; un solo tenant
AReservedTenantThatExistsWinsOverTheRequestedMode       // D-17
LinkableTenantsMarkTheWholesalerAndTheAlreadyLinked     // ineligibleReason "same_tenant" / "already_linked"; modulesToEnable correcto
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~Distributor|FullyQualifiedName~WholesaleModuleSets"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** Application de distribuidores y el servicio de invitación; registra todo.

- [ ] **Step 4: Corre y verifica que pasan**, más
`dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~EveryCommandAndQueryHasItsHandlerRegistered|FullyQualifiedName~WholesaleLayerTests"` → PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Wholesale/Modules.Wholesale.Application src/Bootstrapper/QepServiceCollectionExtensions.cs tests/Modules/Wholesale
git commit -m "feat(wholesale): distributor invitations, relationship actions and invitation acceptance"
```

---

### Task 6: Infrastructure y adaptadores

**Files:**
- Create: los archivos de `Modules.Wholesale.Infrastructure` del mapa (`WholesaleInfrastructureExtensions.cs` reemplaza el vacío).
- Create: `src/Bootstrapper/{WholesaleCatalogReader.cs, WholesaleCompanyLookup.cs, WholesaleMemberDirectory.cs, WholesaleSupplierLinker.cs, WholesaleCustomerProvisioning.cs}`.
- Modify: `QepServiceCollectionExtensions.cs` (`services.AddWholesaleInfrastructure(configuration);` junto a `:1381`; adaptadores
  junto a `:1785-1787`), `src/Api/Program.cs` (`InitializeWholesaleDatabaseAsync` después de Purchases `:527`, dentro del candado,
  comentario «Después de Audit. Sin FKs a otros esquemas: listas, miembros, empresas, tenants y clientes son referencias blandas»).
- Test: `tests/Modules/Wholesale/Modules.Wholesale.UnitTests/WholesaleDbContextMappingTests.cs`; integración (escribir, no
  correr): `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/{….csproj (copia de Purchases.IntegrationTests), WholesaleApiHarness.cs, SharedWholesaleHost.cs, WholesaleAdaptersTests.cs}`.

**Interfaces:**
- Consumes: puertos de Tasks 4-5; `ISupplierLinking` (Task 3); `ICustomerProvisioning`; `IProductRepository`,
  `IProductCategoryRepository`, `IOptionDefinitionRepository`, `CatalogVariantReads`; `ICompanyRepository`; `TenancyDbContext`.
- Produces:
  - `WholesaleDbContext` (esquema `wholesale`, historial de migraciones en `wholesale`):
    `wholesale.price_lists` (`id`, `tenant_id`, `name` varchar(120), `normalized_name` varchar(120) = `lower(name)`,
    `discount_percent` numeric(5,2), `visibility` varchar(16), `category_ids` uuid[], `version` concurrencia, `created_at`,
    `updated_at`); único `IX_price_lists_tenant_name (tenant_id, normalized_name)`.
    `wholesale.distributors` (`id`, `tenant_id`, `business_name` varchar(160), `identification_number` varchar(32), `contact_name`
    varchar(160), `email` varchar(254), `price_list_id` FK a `price_lists` `ON DELETE RESTRICT`, `advisor_member_id`,
    `billing_company_id`, `status` varchar(16), `distributor_tenant_id` null, `customer_id` null, `invited_by_user_id`,
    `created_at`, `updated_at`, `activated_at` null, `version`); únicos `IX_distributors_tenant_identification (tenant_id, identification_number)`
    y `IX_distributors_tenant_distributor_tenant (tenant_id, distributor_tenant_id) WHERE distributor_tenant_id IS NOT NULL`;
    índices `(tenant_id, created_at DESC, id DESC)`, `(tenant_id, status)`, `(tenant_id, price_list_id)`.
    `wholesale.distributor_invitations` (`distributor_id` PK y FK cascade, `token_hash` char(64) único
    `IX_distributor_invitations_token_hash`, `sent_at`, `expires_at`, `reserved_tenant_id` único, `accepted_at` null,
    `accepted_by_user_id` null). Proyecciones `ExcludeFromMigrations` de `audit.entries` (forma de `PurchasesDbContext`) y de
    `platform.outbox_messages` (forma de `SuppliersDbContext.cs:164-172`).
  - `WholesaleUnitOfWork`: `DbUpdateConcurrencyException` → 412 `concurrency.conflict`; `IX_price_lists_tenant_name` → 409
    `name_taken`; `IX_distributors_tenant_identification` → 409 `identification_taken`; `IX_distributors_tenant_distributor_tenant`
    → 409 `already_linked`; FK de `price_list_id` → 409 `in_use` (carrera de borrar contra invitar). Discrimina por **nombre**.
  - `WholesaleEventPublisher`: escribe `wholesale.distributor-invited.v1` con payload en minúscula
    `{ tenantId, distributorId, email, businessName, token, expiresAt }`.
  - `WholesaleAuditPublisher`: copia de `PurchasesAuditPublisher` con `Source = "wholesale"`.
  - `AddWholesaleInfrastructure(IServiceCollection, IConfiguration)` y `InitializeWholesaleDatabaseAsync(this IServiceProvider, CancellationToken)`.
  - Adaptadores Bootstrapper:
    - `WholesaleCatalogReader`: `FindCategoriesAsync` sobre `ListCategoriesWithParentsAsync`; `SampleAsync` con
      `IProductRepository.SearchAsync(tenantId, null, null, null, isActive: true, expandedCategoryIds, page 1, pageSize: take)`,
      expansión con `ProductCategoryTree.WithDescendants`/`ListIdsWithDescendantsAsync`, y por producto la primera variante
      `CatalogVariantReads.IsSellable` con `PriceIn(currency)`; `TotalProducts` = total de la búsqueda.
    - `WholesaleCompanyLookup`: `ICompanyRepository`, `Eligible` = tipo de documento en `NIT|CC|CE|PASAPORTE`
      (`IdentificationTypeParser.SupportedWireValues`).
    - `WholesaleMemberDirectory`: membresías `Active` del tenant con su nombre visible (forma de `PurchasesMemberDirectory`).
    - `WholesaleSupplierLinker`: arma `SupplierWriteData` (D-2: `Name` = `wholesalerName`, `BusinessName` = empresa, documento,
      país, ciudad, contacto, `PaymentTermDays` 0, listas vacías) y llama `ISupplierLinking.EnsureLinkedAsync`; `linked_elsewhere`
      sale como 409 `wholesale.distributor.already_linked`.
    - `WholesaleCustomerProvisioning`: `CreateFromRegisterAsync(tenant, actor, new RegisterCustomerData(name, "NIT", nit, null, email), "wholesale")`;
      con `identification_taken` relee por documento (copia de `ServiceContractsCustomerProvisioning.cs:24-67`, incluido el
      `ChangeTracker.Clear()`).
  - Harness: `WholesaleApiHarness` (base y `QepApiFactory` copiados de `PurchasesApiHarness`), `NewTenantAsync(factory, modules?)`
    → `WholesaleTestTenant(TenantId, SubjectId, MemberId)` con miembro «Ana Mayorista»; `CreateManager`, `CreateReader`,
    `CreateCompanyAsync` (NIT, Medellín), `CreateCategoryAsync`, `CreateVariantAsync(code, name, price COP)`,
    `CreatePriceListAsync`, `InviteAsync` que devuelve también el token leído del outbox (`platform.outbox_messages`), `WithIfMatch`.

- [ ] **Step 1: Escribe la prueba que falla**

```csharp
// WholesaleDbContextMappingTests.cs (modelo, sin base)
TheModelUsesTheWholesaleSchemaAndTheNamedUniqueIndexes   // los cinco nombres de índice de arriba
TheTokenHashIsTheOnlyTokenColumn                         // ninguna propiedad mapeada se llama "token"
```
`WholesaleAdaptersTests.cs` (integración, escribir): `SampleSkipsVariantsWithoutPriceAndInactiveProducts`,
`SampleIncludesDescendantCategories`, `CustomerProvisioningReusesAnExistingNit`, `SupplierLinkerCreatesThenReturnsTheSameSupplier`.

- [ ] **Step 2: Corre y verifica que falla**

Run: `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests --filter "FullyQualifiedName~WholesaleDbContextMappingTests"`
Expected: FAIL de compilación (`WholesaleDbContext` no existe).

- [ ] **Step 3: Implementa** y genera la migración:
`dotnet ef migrations add InitialWholesale --project src/Modules/Wholesale/Modules.Wholesale.Infrastructure --context WholesaleDbContext -o Persistence/Migrations`.

- [ ] **Step 4: Verifica**

Run: el comando del Step 2 → PASS.
Run: `dotnet ef migrations has-pending-model-changes --project src/Modules/Wholesale/Modules.Wholesale.Infrastructure --context WholesaleDbContext` → sin cambios.
Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~WholesaleLayerTests|FullyQualifiedName~CompositionRootTests|FullyQualifiedName~MultiReplicaInventoryTests"` → PASS (sin hosted services ni singletons con estado nuevos).
Run: `dotnet build tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests` → compila.
**Pendiente para el owner:** `dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests --filter "FullyQualifiedName~WholesaleAdaptersTests"`.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Wholesale/Modules.Wholesale.Infrastructure src/Bootstrapper src/Api/Program.cs tests/Modules/Wholesale Backend.slnx
git commit -m "feat(wholesale): persistence, invitation outbox and cross-module adapters"
```

**Revisión del bloque B.**

---

## Bloque C — API, correo y documentación

### Task 7: Endpoints del mayorista y de la invitación

**Files:**
- Modify: `src/Modules/Wholesale/Modules.Wholesale.Api/WholesaleEndpoints.cs`; Create: `WholesaleIfMatch.cs` (copia literal de
  `PurchasesIfMatch.cs`, con su aviso).
- Create: `src/Api/WholesaleInvitationEndpoints.cs`, `src/Bootstrapper/SensitivePaths.cs`. Modify: `src/Api/Program.cs`
  (`app.MapWholesaleEndpoints();` y `app.MapWholesaleInvitationEndpoints();` junto a `:399`, comentario «Spec 2026-11-17:
  mayorista»), `src/Api/RequestFailureCapture.cs:67,94`, `src/Api/RequestFailureLoggingMiddleware.cs:71`.
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/SensitivePathsTests.cs` (unitaria, sí corre); integración (escribir, no
  correr): `tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests/{PriceListApiTests.cs, DistributorApiTests.cs, WholesaleAccessApiTests.cs, InvitationAcceptanceApiTests.cs}`.

**Interfaces:**
- Consumes: handlers e `IDistributorInvitationService` (Tasks 4-5); `IUserDirectory.FindUserIdByEmailAsync`/`GetEmailAsync`,
  `IPasswordCredentials`, `IIdentityProvisioning`, `IProviderLinking`, `SessionEstablisher`, `ITenancyUnitOfWork.BeginUserLifecycleScopeAsync`,
  `RegistrationEndpoints.ToRegistrationData`.
- Produces:
  - `MapWholesaleEndpoints(this IEndpointRouteBuilder)`: grupos `/api/v1/tenants/{tenantId:guid}/wholesale/price-lists` y
    `…/wholesale/distributors`, tag `Wholesale`, rutas literales (`/preview`, `/options`) antes de `/{id:guid}`; `POST` →
    `Results.Created(…)`; `DELETE` → 204; escrituras sobre un recurso leen `WholesaleIfMatch.RequireVersion` antes de despachar.
  - `MapWholesaleInvitationEndpoints(this IEndpointRouteBuilder)` (forma de `InvitationEndpoints.cs`):
    - `GET /api/v1/wholesale-invitations/{token}`: `.AllowAnonymous().RequireRateLimiting(RateLimiterPolicies.Public)`;
      `FindAsync` + `hasPassword` (`FindUserIdByEmailAsync` → `HasPasswordAsync`).
    - `POST …/session`: `RequireGoogleBearerOrDevStub`, `RequireRateLimiting(Credentials)`. Orden: `RequirePendingAsync` → claim
      `email` verificado e igual al de la invitación (si no, 403 `email_mismatch`) → dentro de
      `BeginUserLifecycleScopeAsync(email)`: `GetOrProvisionInvitedUserAsync` → `LinkAndActivateAsync` → commit →
      `SessionEstablisher.EstablishAsync`.
    - `POST …/session-with-password`: `AllowAnonymous`, `Credentials`, `.Accepts<WholesaleInvitationPasswordRequest>("application/json")`,
      lectura tolerante del cuerpo como `PasswordAuthEndpoints` (400 si falta). Orden: `RequirePendingAsync` → scope →
      `GetOrProvisionInvitedUserAsync(invitación.Email)` → `AcceptInvitationAsync(userId, password)` → `EstablishAsync`.
    - `GET …/linkable-tenants` y `POST …/accept`: `RequireAuthorization()` + `Public`; usuario por `qep_sub`/`sub` (copia de
      `InvitationEndpoints.RequireUserId`), correo por `IUserDirectory.GetEmailAsync`; `accept` arma la elección: `NewTenant` con
      `ToRegistrationData(displayName, slug, null, null, null)`; validador `AcceptWholesaleInvitationValidator` (`Mode` en
      `NewTenant|LinkTenant`; `DisplayName`/`Slug` con `NewTenant`; `TenantId` con `LinkTenant`).
    - Ninguna ruta deja el token en el servidor: hoy `RequestFailureCapture.Describe` (`:67`, `:94`) guarda
      `Request.Path` crudo en `platform.request_failures` y `RequestFailureLoggingMiddleware.cs:71` lo escribe en el log, así
      que un `accept` fallido guardaría el token. `SensitivePaths.Mask(PathString path) → string` (nuevo, en
      `src/Bootstrapper/SensitivePaths.cs`, junto a `WebhookPaths`) cambia el segmento que sigue a
      `/api/v1/wholesale-invitations/` por `***`, y los dos sitios lo usan. `Instance` del ProblemDetails no cambia: vuelve a
      quien ya tiene el token.

- [ ] **Step 1: Escribe las pruebas que fallan** (las de integración: compilación; su corrida es del owner)

```csharp
// SensitivePathsTests.cs (unitaria)
TheWholesaleInvitationTokenIsMasked        // "/api/v1/wholesale-invitations/abc_DEF-1/accept" → "/api/v1/wholesale-invitations/***/accept"
OtherPathsAreUntouched                     // "/api/v1/tenants/{id}/wholesale/distributors" sin cambios; "/" → "/"
// PriceListApiTests.cs
CreateReturns201AndTheDetail; DuplicateNameIs409NameTaken; DeleteInUseIs409InUse; StaleIfMatchIs412AndMissingIs428
PreviewRoundsWithTheTenantRuleAndOmitsUnpricedVariants
// DistributorApiTests.cs
InviteReturns201InvitedAndQueuesOneEmailEventWithoutTheTokenInTheAudit
ListCountsAndSearch; ResendRotatesTheToken; TheOldLinkIs404AfterAResend   // Review Focus 4
BlockAndUnblock; DeleteOnlyWhileInvited
// WholesaleAccessApiTests.cs
AReaderSeesDistributorsWithEmptyAllowedActionsAndCannotWrite; AnAdvisorReadsDistributors
WithoutTheWholesaleModuleEverythingIs403; AnotherTenantIs403
// InvitationAcceptanceApiTests.cs
TheSummaryIsAnonymousAndShowsPendingThenAccepted; AnExpiredLinkIs410
APasswordSessionProvisionsTheUserAndOpensASession
NewTenantCreatesTheEightModulesWithoutShopifyEvenWithSignupDisabled
NewTenantCreatesTheLinkedSupplierInTheDistributorAndTheCustomerInTheWholesaler
LinkTenantEnablesSuppliersAndPurchasesAndKeepsTheRest
AnotherAccountIs403AndNothingIsWritten
TwoConcurrentAcceptsCreateOneTenantOneSupplierOneCustomer     // Review Focus 1
TheTokenNeverAppearsInAuditOrRequestLogs                      // audit.entries y platform.request_log sin el token
```

- [ ] **Step 2: Verifica que fallan:** `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~SensitivePathsTests"`
y `dotnet build tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests` → FAIL de compilación (`SensitivePaths` /
rutas sin definir en el harness).

- [ ] **Step 3: Implementa** los dos archivos de endpoints y el registro en `Program.cs`.

- [ ] **Step 4: Verifica**

Run: `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~SensitivePathsTests"` → PASS.
Run: `dotnet build src/Api` y `dotnet build tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests` → compilan.
Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~WholesaleLayerTests"` → PASS.
**Pendiente para el owner** (una por vez, sin otra corrida en curso):
`dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests --filter "FullyQualifiedName~PriceListApiTests|FullyQualifiedName~DistributorApiTests|FullyQualifiedName~WholesaleAccessApiTests"`
y `dotnet test tests/Modules/Wholesale/Modules.Wholesale.IntegrationTests --filter "FullyQualifiedName~InvitationAcceptanceApiTests"`.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Wholesale/Modules.Wholesale.Api src/Api/WholesaleInvitationEndpoints.cs src/Api/Program.cs src/Api/RequestFailureCapture.cs src/Api/RequestFailureLoggingMiddleware.cs src/Bootstrapper/SensitivePaths.cs tests/Bootstrapper tests/Modules/Wholesale
git commit -m "feat(wholesale): price list, distributor and invitation endpoints"
```

---

### Task 8: Correo de invitación

**Files:**
- Create: `src/Modules/Notifications/Modules.Notifications.Application/WholesaleInvitationEmailTemplate.cs`,
  `…Infrastructure/Messaging/WholesaleInvitationDeliveryWorker.cs`.
- Modify: `…Infrastructure/NotificationsOptions.cs` (`WholesaleInvitationUrl`, default `"http://localhost:3002/wholesale-invitations"`),
  `NotificationsOptionsValidator.cs` (URL absoluta, mismo mensaje con la clave nueva), `NotificationsInfrastructureExtensions.cs`
  (`services.AddSingleton<OutboxDeliveryWorker, WholesaleInvitationDeliveryWorker>();` junto a `:60`), `src/Api/appsettings.json:16`,
  `src/Api/appsettings.example.json:59`, `k8s/prod-configMap.yaml:83` (`Notifications__WholesaleInvitationUrl: "#{NOTIFICATIONS_WHOLESALE_INVITATION_URL}#"`).
- Test: `tests/Modules/Notifications/Modules.Notifications.UnitTests/{WholesaleInvitationEmailTemplateTests.cs, NotificationsOptionsValidatorTests.cs}`;
  integración (escribir, no correr): `…IntegrationTests/WholesaleInvitationNotificationTests.cs`.

**Interfaces:**
- Produces:
  - `WholesaleInvitationEmailTemplate.TemplateRef = "wholesale-invitation"`;
    `EmailMessage Render(string recipient, string link, string wholesalerName, string businessName, DateTimeOffset expiresAt, EmailRegionalFormat format)`.
    Copia fija (propuesta, ver O-3): asunto «{wholesalerName} te invita a comprarle en QEP»; cuerpo «Hola. {wholesalerName} quiere
    venderle a {businessName} por QEP. Acepta la invitación para crear tus órdenes de compra con sus precios.», botón «Aceptar
    invitación» al `link`, línea «El enlace vence el {expiresAt en formato del tenant}.». Valores escapados para HTML como en
    `InvitationEmailTemplate`.
  - `WholesaleInvitationDeliveryWorker`: `Consumer => "notifications.wholesale-invitation-email"`,
    `EventName => "wholesale.distributor-invited.v1"`; lee el payload con `JsonDocument`; `Notification.CreateEmail(tenantId, distributorId, email, TemplateRef, now)`;
    correo o token vacíos → `MarkFailed("recipient_email_unavailable" | "invitation_token_unavailable")` sin lanzar; nombre del
    mayorista leído al entregar (`TenantDirectory.GetDisplayNameAsync`; vacío → `"tenant_name_unavailable"`); link con
    `InvitationLink.Compose(options.Value.WholesaleInvitationUrl, token)`; formato con `ResolveEmailFormatAsync`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// WholesaleInvitationEmailTemplateTests.cs
SubjectNamesTheWholesaler; BodyCarriesTheLinkAndTheExpiryInTheTenantFormat; NamesAreHtmlEscaped   // "<b>" → "&lt;b&gt;"
// NotificationsOptionsValidatorTests.cs
ARelativeWholesaleInvitationUrlFails                 // "Notifications:WholesaleInvitationUrl must be an absolute URL."
```
`WholesaleInvitationNotificationTests.cs` (escribir): `InvitingADistributorDeliversExactlyOneEmailWithTheLink`,
`AResendDeliversASecondEmailWithTheNewLink`.

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~WholesaleInvitationEmailTemplateTests|FullyQualifiedName~NotificationsOptionsValidatorTests"`
Expected: FAIL de compilación.

- [ ] **Step 3: Implementa** plantilla, worker, opción, validador, registro y configuración.

- [ ] **Step 4: Verifica**

Run: el comando del Step 2 → PASS.
Run: `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~ConfigurationExampleTests|FullyQualifiedName~MultiReplicaInventoryTests"` → PASS.
Run: `dotnet build tests/Modules/Notifications/Modules.Notifications.IntegrationTests` → compila.
**Pendiente para el owner:** `dotnet test tests/Modules/Notifications/Modules.Notifications.IntegrationTests --filter "FullyQualifiedName~WholesaleInvitationNotificationTests"`.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Notifications src/Api/appsettings.json src/Api/appsettings.example.json k8s/prod-configMap.yaml tests/Modules/Notifications
git commit -m "feat(notifications): wholesale distributor invitation email"
```

---

### Task 9: Documentación y cierre

**Files:**
- Modify: `README.md` (módulo `Wholesale` en la lista de módulos y sus rutas en la tabla de endpoints, junto a las de Purchases;
  `Notifications:WholesaleInvitationUrl` donde se documenta `InvitationUrl`), `CLAUDE.md` (gotchas, abajo).

- [ ] **Step 1: Escribe en `CLAUDE.md` tres gotchas**: (1) «La aceptación de una invitación de mayorista no es atómica entre
  módulos: es idempotente por el tenant reservado (`distributor_invitations.reserved_tenant_id`), el proveedor (`LinkedTenantId` o
  documento) y el cliente (documento); la invitación se consume en el último commit. No la cambies a "consumir primero"»; (2)
  «`Supplier.LinkedTenantId` lo escribe sólo `ISupplierLinking`; el formulario no lo acepta y un proveedor vinculado no cambia de
  documento»; (3) «`Notifications:WholesaleInvitationUrl` es otra variable del pipeline (`NOTIFICATIONS_WHOLESALE_INVITATION_URL`);
  sin ella el link sale con el token del pipeline literal».

- [ ] **Step 2: Verificación final (sin integración)**

Run: `dotnet build src/Api` → 0 errores, 0 advertencias nuevas.
Run: `dotnet test tests/Modules/Wholesale/Modules.Wholesale.UnitTests` → PASS.
Run: `dotnet test tests/ArchitectureTests/ArchitectureTests` → PASS (proyecto sin contenedores).
Run: `dotnet format --verify-no-changes --include <archivos tocados>` → sin cambios.
Reporta como **pendiente para el owner**, en una lista, todos los filtros de integración de las Tasks 1, 3, 6, 7 y 8, más la suite
de los proyectos tocados antes de mergear a `develop` (Tenancy, Suppliers, Notifications, Wholesale, ArchitectureTests; Identity
sólo `UserDirectory`), uno por vez.

- [ ] **Step 3: Commit**

```powershell
git add README.md CLAUDE.md
git commit -m "docs(wholesale): module, endpoints and acceptance gotchas"
```

**Revisión del bloque C** y revisión fuerte de toda la rama (el flujo de aceptación es sensible: autenticación, tokens, escritura
entre tenants).

---

## Resoluciones del orquestador (2026-11-17, antes de ejecutar)

Mandan sobre el texto de las tareas donde choquen.

- **O-1 → D-2 se queda.** La identidad del mayorista sale de la empresa facturadora elegida al invitar; si el tenant tiene una sola,
  viene preseleccionada. Sin pantalla de ajustes nueva.
- **O-2 → sin variable nueva.** El link se arma con `AppBaseUrl` + `/wholesale-invitations/{token}` (precedente `AccountLinks`), no
  con `Notifications:WholesaleInvitationUrl`. Sigue la decisión del owner del 2026-11-17 de no sumar variables a `Backend-prod` que
  puedan impedir que el pod arranque (la de la clave única de `qcode-pdf`). Task 8 se ajusta: sin sección ni validador nuevos.
- **O-3 → la copia propuesta en Task 8 se usa tal cual**; el owner la puede ajustar después.
- **O-4 → D-5 se queda:** prender `catalog` es obligatorio porque `purchases` depende de él.
- **O-5 → D-6 se queda:** 410, como pide el spec.
- **O-6 → pendiente del owner** (regla de pruebas del workspace).
- **O-7 → se incluye en Task 7:** agregar la ruta de aceptación de la invitación de miembros a `SensitivePaths`, con su prueba. Es
  una línea y saca un token de los logs; el owner lo puede vetar.

### Adenda al «Contrato HTTP» (tras el plan del frontend; manda sobre la sección del contrato)

- **`DistributorSummary`** suma `version` y `invitation: { expiresAt, canResend } | null` (presente sólo con `Status = Invited`): la
  fila pinta «Vence 20/11 · Reenviar» como el mockup.
- **`PriceListSummary`** suma `createdAt` y `categories: [{ id, name }]` (vacío con `AllActive`): la fila pinta «Creada 02/10/2026»
  y «2 categorías · Hogar, Cocina» sin otra llamada.
- **«Último pedido» no está en fase 2**: no hay pedidos vinculados hasta la fase 3. La columna no se pinta.
- **`GET /api/v1/tenants/{tenantId}/wholesale/categories`** (permiso `wholesale.price_list.manage`): árbol plano `{ id, name,
  parentId }` de las categorías activas del tenant, para elegir la visibilidad sin exigir `catalog.product.read`. Mismo puerto de
  lectura del catálogo que la vista previa.
- **Elegibilidad de un tenant para vincular** (`linkable-tenants`): cada ítem suma `ineligibleReason: string | null` con un código
  (`already_linked`, `same_tenant`, …), para que la pantalla diga por qué no se puede elegir. Sólo lista tenants que el usuario
  administra como owner: es la regla de la aceptación, no un hueco.
- **Proveedor vinculado en órdenes externas:** en fase 2 se puede elegir como cualquier proveedor (la orden es manual). La fase 3
  cambia esa ruta.

## Pendiente del owner

- **O-1 (D-2).** ¿La identidad del mayorista ante el distribuidor sale de la empresa facturadora elegida al invitar? Alternativa:
  un ajuste «empresa del mayorista» por tenant en `/wholesale/settings` (una pantalla más, un solo valor).
- **O-2.** Dar de alta `NOTIFICATIONS_WHOLESALE_INVITATION_URL` en `Backend-prod` (`https://qep.qcode.co/wholesale-invitations`)
  antes de desplegar. Alternativa sin variable nueva: armar el link con `AppBaseUrl` (precedente `AccountLinks`).
- **O-3.** Copia del correo de invitación (Task 8, propuesta).
- **O-4 (D-5).** Vincular un tenant existente también prende `catalog` si estaba apagado (lo exige `purchases`).
- **O-5 (D-6).** Vencida responde 410 como pide el spec, aunque la invitación de miembros vencida responde 422.
- **O-6.** Correr las pruebas de integración y las suites listadas en Task 9; ninguna se corrió en esta rama.
- **O-7.** Hallazgo fuera de alcance: el token de la invitación de **miembros** queda hoy en `platform.request_failures` y en el
  log cuando falla un `POST /api/v1/invitations/{token}/accept…` (`RequestFailureCapture.cs:67,94`). `SensitivePaths` (Task 7) lo
  cubriría con una línea más; no se toca sin tu visto bueno.
- Siguen abiertas para fase 3: P-2, P-4 y P-5.

## Puntos del spec que chocan con el código

- «La empresa facturadora es la por defecto del mayorista»: Companies no tiene empresa por defecto → D-2.
- «Se le prenden `suppliers` y `purchases`»: `purchases` depende también de `catalog` → D-5.
- `wholesale.invitation.expired` 410: no hay 410 en `ApiExceptionHandler` → D-6.
- «El que acepta no tiene tenant»: tampoco puede tener sesión (Google es sólo por invitación) → D-4.
- «El proveedor vinculado no se puede borrar»: `Supplier` no tiene borrado, sólo desactivar → D-8.
- `CK_tenant_modules_source` no admite un origen para la invitación → `invitation` (Task 1).
