---
paths:
  - "src/Modules/Tenancy/**"
  - "tests/Modules/Tenancy/**"
  - "src/Modules/Identity/**"
  - "tests/Modules/Identity/**"
  - "src/Modules/ServiceContracts/**"
  - "tests/Modules/ServiceContracts/**"
  - "src/Modules/ApiAccess/**"
  - "tests/Modules/ApiAccess/**"
  - "src/Modules/Authorization/**"
  - "tests/Modules/Authorization/**"
  - "src/Bootstrapper/QepServiceCollectionExtensions.cs"
---

# Tenancy, Identidad, contratos de servicio y API externa

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **Defecto conocido sin cerrar:** un registro de tenant que falla después de aprovisionar
  deja el usuario en `identity.users` **sin membresía**. `RegistrationEndpoints.cs:89-104`
  aprovisiona antes de crear el tenant, en módulos con unidades de trabajo distintas y sin
  compensación.
- **Sólo `admin` es rol de sistema** (spec 2026-11-25, roles por tenant). `advisor`, `billing`, `cashier`,
  `scheduling_manager` y `scheduling_provider` son plantillas (`RoleTemplate`, en `QepServiceCollectionExtensions`) con las
  que `TenantRoleSeeder` crea filas de `authorization.roles` en cada tenant: en el alta (`TenantRolesProvisioning`; si falla,
  queda un log `Error` y el alta sigue) y al arrancar (`SeedTenantRolesAsync`; si falla, el arranque falla). La marca de
  `authorization.role_seeds` hace que se siembre una sola vez: no revive un rol borrado ni pisa uno editado. **Un permiso nuevo
  en una plantilla sólo llega a los tenants nuevos** (decisión 2 del owner): en los existentes lo marca el administrador. **Renombrar
  o partir un permiso exige una migración de datos sobre `authorization.roles`** (precedente: `RenamePermissionsToOrders`); sin ella
  los roles guardados siguen nombrando el código viejo. Toda plantilla es un subconjunto de `admin`
  (`CompositionRootTests.EveryRoleTemplateIsASubsetOfAdmin`): lo que `admin` no lleva no se puede marcar ni guardar. **Un rol no se
  borra si lo tiene una membresía `Invited`, `Active` o `Suspended`** (`422 authorization.role.in_use`); `Removed` y `Expired` no
  cuentan.
- **Un rol que no concede ningún permiso ofrecido en el tenant no se agrega ni se invita** (spec 2026-11-15): `422
tenancy.membership.role_unavailable`, y el catálogo lo marca `assignable: false`. Cajero sin `pos` es el caso típico,
  pero vale también para un rol custom de sólo permisos de un módulo apagado. Quien ya tiene el rol lo conserva: el `PUT`
  sólo rechaza los roles **agregados**, porque la pantalla reenvía todos al guardar. La regla vive en `RoleAssignability`
  y la comparten `/authorization/catalog` y `RoleReferenceValidator`; no la copies en un handler. Una prueba que invite o
  asigne un rol de módulo prende el módulo antes (`PosApiHarness.InviteCashierAsync` ya lo hace).
- **El módulo de cobros recurrentes se llama `ServiceContracts` en el código y `billing` en todo lo desplegado** (owner,
  2026-10-30). Carpeta `src/Modules/ServiceContracts/`, namespaces `Modules.ServiceContracts.*`, agregado `ServiceContract`
  (antes `BillingService`), tipos `ServiceContracts*` (`ServiceContractsDbContext`, `ServiceContractsPermissions`,
  `IServiceContractsAuditPublisher`…), en pantalla «Contratos de servicio». **No cambiaron, y no se cambian sin migración de
  datos ni despliegue conjunto con el frontend:** el esquema `billing`, la clave de módulo `billing` (`TenantModuleKey.ServiceContracts`),
  los permisos `billing.service.read|manage`, las rutas `/api/v1/tenants/{id}/billing` y `/billing` del SPA, las acciones de
  auditoría y los códigos de error `billing.*`, la sección `Billing` de configuración (`ServiceContractsOptions.SectionName`) y el id
  de la migración `20261029180546_InitialBilling`. Conservan «Billing» a propósito porque nombran el cobro y no el módulo, o son de
  otro módulo: `BillingCycle`, `BillingChannel`, `CustomerBillingProfile`; `MessageTemplateKind.BillingReminder|BillingOverdue`
  (persistidos como texto), `IBillingTemplateOutbound` y `BillingTemplateDefinitions` (Messaging), las plantillas `Billing*Email*`
  (Notifications), `BillingAccount`/`BillingParty` (Quotations: la facturación de un pedido) y el rol sembrado `billing`
  (`RoleTemplateKeys.Billing`). Un `grep Billing` que encuentre algo fuera de esa lista dentro del módulo es un resto del renombre.
- **«Admin» para vincular un tenant a un mayorista es el rol de sistema `admin` en una membresía activa** (spec 2026-11-19,
  `ITenantOwnership.ListAdministeredActiveTenantsAsync`), además del owner (`owner_membership_id`). Un rol custom con permisos
  parecidos no cuenta, y una membresía suspendida o pendiente tampoco. Es una consulta de Tenancy, no un cruce con
  Authorization: no la reemplaces por una pregunta de permisos.
- **Los términos se aceptan por usuario y la versión sale de `Terms:CurrentVersion`** (`ITermsAcceptances`). Sin versión
  configurada el alta de un distribuidor nuevo falla cerrado (`identity.terms.unavailable`); nunca se acepta una versión
  inventada. `RecordAsync` se confirma solo, con FK a `identity.users`: el usuario tiene que estar ya guardado.
- **La API externa no usa `IExecutionContext` sino `IApiCaller`** (spec 2026-10-30). El tenant sale de la clave, no de la ruta ni de
  un header, y una clave nunca recibe claims `permission`: ninguna política interna pasa con ella. Un scope nuevo se registra con
  `ApiScopeDefinition` en DI (la constante vive en el módulo dueño) y sus rutas cuelgan del grupo de `ExternalApiEndpoints`, que fija
  el esquema `ApiKey`; una ruta externa fuera de ese grupo no recibe ese esquema. Los `code` `external.*` son contrato público con
  los integradores: no se renombran.
- **Un tenant nace con la moneda de su país y con compra mínima de 1 unidad** (owner, 2026-11-23). `Tenant.Create` con país toma
  `TenantCurrencies.ForCountry` (CO → COP, eurozona → EUR, el resto → USD); sin país (el signup público no lo trae) queda la de la
  columna, COP. Después del commit, `TenantRegistrationService` llama `INewTenantProvisioning` (signup e invitación, sólo si el alta
  crea el tenant) y `QuotationsNewTenantProvisioning` guarda 1 unidad y ningún monto mínimo. **No es atómico con el alta:** si falla,
  el tenant queda con el por defecto de Quotations (6 unidades, 500.000 COP / 200 USD). Un tenant viejo sin fila no se toca.
- **El idioma (`es-419`/`es-ES`) se resuelve en un solo lugar: `SupportedLanguages.Effective(elegido, delTenant)`** (spec
  2026-11-22, i18n). Lo usan `/auth/preferences`, `/auth/me` y los correos (`OutboxDeliveryWorker.ResolveLanguageAsync`); un camino
  nuevo que arme la regla a mano puede decir en el correo otro idioma que en la pantalla, sin error. El idioma **no** es el formato
  regional: no lo derives de `DefaultCulture` (`es-CO`) ni cambies separadores con él. La preferencia guarda `null` («el del
  tenant»), nunca una copia del idioma del tenant. El glosario de España del backend («presupuesto», «número de caso», «comercial»;
  `QuotationsExportKindText`, `SupportOpeningEmailTemplate`, `QuotationsExportProcessor`, `QuotationPdfFileName` y `quotation.typ`)
  tiene que coincidir con el diccionario `es-ES` del frontend: si cambia uno, cambian los dos. Y el idioma entra a la huella del PDF
  **sólo** cuando no es `es-419` (`QuotationPdfSectionsBuilder.Fingerprint`): hacerlo siempre cambia la huella de todos los PDF
  guardados y el siguiente export los regenera a todos.
