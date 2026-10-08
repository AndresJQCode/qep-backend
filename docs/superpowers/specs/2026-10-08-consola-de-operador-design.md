# Consola de operador: tenant operador, ciclo de vida de módulos y estado del tenant

> Spec cruzado (backend + frontend). Construye sobre `feature/modulos-por-tenant`
> (`docs/superpowers/specs/2026-10-07-modulos-por-tenant-design.md`, en adelante **el spec de
> entitlements**). Diseñado con el owner el 2026-10-07; maquetas en el lienzo «Consola de operador
> QEP» (claude.ai, privado del owner).

## Problema

Hoy un módulo se prende y se apaga **por SQL** (`README.md`, «Módulos por tenant»). Apagarlo es un
`DELETE`: no queda quién, cuándo ni por qué. No existe ningún rol por encima de los tenants, así que
nadie puede administrar la plataforma desde la aplicación. Y el estado del tenant (`TenantStatus`)
existe en el modelo pero **solo se asigna `Active`** y casi no se aplica: `Tenant.EnsureActive`
(`Tenant.cs:211-218`) protege únicamente la edición de ajustes y del logo; la resolución de permisos
mira la membresía, no el tenant (`AuthorizationService.cs:33-39`). Un tenant marcado `Suspended`
hoy seguiría trabajando.

**Qué reemplaza del spec de entitlements:** la decisión 10 (sin endpoint de administración, sólo
SQL) y la 14 (la fila es el entitlement, apagar es `DELETE`). Hace una excepción a la regla de no
filtrar `GET /authorization/roles`: ese endpoint sí oculta `operator.*` (ver §2).

## Objetivo

QEP es una suite modular que QCode vende por módulos. QCode es un tenant más con un privilegio que
nadie más tiene: **activar y desactivar módulos de los demás tenants y activarlos o inactivarlos a
ellos**. Esta entrega le da a QCode una consola dentro de la SPA para hacerlo sin SQL, con historial
auditable, y hace que el estado del tenant se cumpla.

### Criterios de éxito

1. Un usuario del tenant operador con el permiso adecuado lista los tenants, ve sus módulos, los
   activa o desactiva con motivo obligatorio, y ve el historial, todo desde la SPA.
2. Ningún usuario de otro tenant puede obtener los permisos de operador, ni siquiera con un rol
   personalizado que los contenga: se descartan al resolver permisos.
3. Desactivar un módulo no borra la fila ni datos: cambia su estado y deja una entrada de historial.
4. Un cambio que dejaría un módulo activo sin sus dependencias activas se rechaza; la consola
   ofrece la cascada y la manda como un solo lote atómico.
5. Un tenant inactivo desaparece del selector de sesión y **todo** request a ese tenant recibe 403,
   incluso con una sesión ya abierta. Reactivarlo deja todo como estaba.
6. El tenant operador no se puede inactivar.

## Decisiones del owner (2026-10-07)

| # | Decisión |
| - | -------- |
| O1 | No son feature flags: activar un módulo es un *entitlement*. |
| O2 | QCode es el **tenant operador**, identificado por configuración `Platform:OperatorTenantId` (no por API ni por columna). |
| O3 | Estados del módulo por tenant: **activo / inactivo, con historial**. `read_only` y vigencias (`ends_at`) quedan para cuando exista el cobro; el modelo debe admitirlos sin rediseño. |
| O4 | Desactivar deja de ser `DELETE`: la fila queda con su estado. Historial en tabla aparte con **motivo cerrado** + nota libre. Auditoría en la misma transacción. Un único camino de dominio para cambiar estado. |
| O5 | Dependencias: el backend es estricto (rechaza estados inconsistentes) y acepta **un lote** de cambios atómico; la consola calcula la cascada y pide confirmación. |
| O6 | Permisos con prefijo **`operator.`**: `operator.tenants.read`, `operator.modules.manage`. El `admin` del tenant operador los recibe automáticamente; fuera del tenant operador se descartan siempre y el editor de roles no los muestra. |
| O7 | Consola v1 **mínima**: lista de tenants, detalle con módulos (interruptores, cascada, motivo obligatorio) e historial del tenant. Ficha extendida e historial global, después. |
| O8 | Estado del tenant: **Activo / Inactivo** con motivo `nonpayment` (falta de pago) o `cancellation` (ya no continúa). Inactivo bloquea todo acceso, no borra nada, conserva los módulos. Se reutiliza `TenantStatus.Suspended`; en la UI se lee «Inactivo». |
| O9 | La consola vive dentro de la SPA de QEP (sección «Plataforma»), no en una app aparte. |

## Alcance

**Entra:** configuración del tenant operador; permisos `operator.*` y su filtro; columna de estado +
historial de módulos; estado del tenant aplicado en sesión y permisos; API de operador; consola en la
SPA (tres vistas de las maquetas + estado del tenant); migración y README.

**No entra:** cobro, planes, Wompi, Suscripciones; `read_only` y `ends_at`; chequeo de módulos en
workers/outbox (decisión 6 del spec de entitlements, se mantiene); ficha extendida del tenant;
historial global; renombrar `platform.request_log.*`; retirar el white-label; política de retención
de datos de tenants que se van (Ley 1581).

## Modelo

### 1. El tenant operador

- `OperatorTenantOptions` (`SectionName = "Platform"`, clave `OperatorTenantId`, `Guid?`). El
  nombre de la clase evita `PlatformOptions`, que se confundiría con el módulo `Modules.Platform`
  (log de fallas); la clave de configuración se mantiene como la aprobó el owner (O2). Vive en
  `Tenancy.Infrastructure` (mismo criterio que `EntitlementsOptions`: ninguna capa Application usa
  `IOptions`). Se registra con `ValidateOnStart`.
- Validador: **opcional en todos los ambientes**, pero si está presente no puede ser
  `Guid.Empty`. Sin operador configurado no hay consola: los permisos `operator.*` se descartan en
  todos los tenants, los endpoints de operador responden 403 y la app sigue funcionando. En
  `Production`, al arrancar sin la clave se escribe un log de advertencia (no falla: ver D10).
- Puerto `IOperatorTenant` en `Tenancy.Application`:

  ```csharp
  public interface IOperatorTenant
  {
      /// true sólo para el tenant configurado en Platform:OperatorTenantId.
      bool IsOperator(Guid tenantId);
  }
  ```

  Implementación en `Tenancy.Infrastructure`, singleton, lee las opciones. No consulta la base: que
  el id exista como tenant no se valida al arrancar (no hay base disponible en la validación de
  opciones); si no existe, la consola simplemente no es alcanzable por nadie.
- La clave va en `appsettings.example.json` (lo exige `ConfigurationExampleTests`). **No** va en
  `appsettings.json` ni se agrega un marcador al ConfigMap de producción: un valor inválido ahí
  tumbaría los pods al desplegar `main`, donde el CI no corre pruebas. El owner agrega
  `Platform__OperatorTenantId` con el id real de QCode cuando quiera habilitar la consola (ver
  «Despliegue»).

### 2. Permisos de operador

| Permiso | Para qué | Riesgo |
| --- | --- | --- |
| `operator.tenants.read` | Ver la lista de tenants, sus módulos y su historial | medium |
| `operator.modules.manage` | Activar o desactivar módulos de un tenant | high |
| `operator.tenants.manage` | Activar o inactivar un tenant | high |

`operator.tenants.manage` no estaba en la conversación con el owner (el estado del tenant se decidió
después): ver «Decisiones tomadas sin el owner», D1.

- Constantes en `OperatorPermissions` (`Tenancy.Application`). Registradas como
  `PermissionDefinition` con categoría **`"Operator"`** (no `"Platform"`, que ya es la del log de
  fallas) y `RequiredModules: []` (núcleo: el enmascarado por módulos no las toca; el operador no se
  bloquea a sí mismo apagando módulos), y con su política en `AddAuthorization` (las dos mitades:
  sin la política, 500). El front agrega la etiqueta de la categoría en `PERMISSION_CATEGORY_LABEL`.
- Incluidas en el rol de fábrica `admin`. Como los roles de fábrica son globales, **el filtro de
  operador** es lo que las vuelve efectivas sólo en el tenant operador:

  **Filtro de operador.** Una función pura en `Authorization.Application`
  (`OperatorPermissionFilter.Apply(permissions, isOperatorTenant)`) quita todo permiso con prefijo
  `operator.` cuando `isOperatorTenant` es `false`. Se aplica en los mismos tres lugares que
  `ModuleEntitlementMask` y justo después de él:

  | Lugar | Cambio |
  | --- | --- |
  | `AuthorizationService.ResolvePermissionsAsync` (cookie real) | después del enmascarado por módulos |
  | `DevelopmentAuthenticationHandler` (stub) | siempre, exista o no la fila del tenant: el stub no debe poder autodeclararse operador con `X-Permissions` |
  | `GET /authorization/catalog` | quita `operator.*` de `permissions` y de cada `roles[].permissions` fuera del tenant operador |

- **`GET /authorization/roles`** también oculta `operator.*` fuera del tenant operador (el rol
  `admin` de fábrica los lleva y no deben aparecer en el editor de otros tenants).
- **`operator.*` sólo vive en roles de sistema** (D11). Crear o editar un rol personalizado con un
  permiso `operator.*` se rechaza en **cualquier** tenant, incluido el operador, con
  `422 authorization.role.permission_operator_only` (en `RoleCommands`, junto a
  `EnsureKnownPermissions`, misma familia que `authorization.role.*`). El catálogo
  (`/authorization/catalog`) no ofrece `operator.*` como casilla en ningún tenant. Un rol
  personalizado que ya los tuviera por SQL deja de poder guardarse hasta quitarlos: se acepta.
- **Radio de explosión, dicho explícitamente:** dentro del tenant QCode, quien pueda asignar roles
  (`advisorship.manage`) puede convertir a alguien en `admin` y, con eso, en operador de plataforma.
  Es aceptado: con 1–3 personas en QCode, administrar el tenant QCode **es** administrar la
  plataforma.

### 3. Ciclo de vida del módulo por tenant

**Tabla `tenancy.tenant_modules` (cambia):**

| Columna | Cambio |
| --- | --- |
| `status` | **nueva**, `varchar(16) NOT NULL DEFAULT 'active'`, `CHECK (status IN ('active','inactive'))`. El default **se queda** y se declara en el modelo (`HasDefaultValue("active")`): durante el rolling update los pods viejos insertan filas (signup, semilla) sin la columna, y el SQL manual del README también. |
| `status_changed_at` | **nueva**, `timestamptz NOT NULL DEFAULT now()` (`HasDefaultValueSql("now()")`, mismo motivo); la migración actualiza las existentes con `enabled_at`. |
| `enabled_at` | sin cambio: cuándo se creó la fila por primera vez. |
| `source` | el `CHECK` agrega `'operator'`: una fila creada desde la consola (el módulo nunca había existido para ese tenant). |
| `note` | sin cambio (nota de creación). El motivo de cada cambio vive en el historial. |

`read_only` no se agrega al `CHECK` hoy (O3): agregarlo mañana es una migración que amplía el
`CHECK` y un valor más en el enum de dominio.

**Contratado = fila con `status = 'active'`.** `TenantModules.FindAsync` pasa a leer sólo las filas
activas; `TenantModuleSet.FromStored` y todo lo que está encima (efectivo, `missingDependencies`,
enmascarado, guard, endpoint de capacidades) **no cambia**. Ese es el punto de compatibilidad con el
código que ya consume entitlements (POS, WhatsApp por tenant).

**Tabla nueva `tenancy.tenant_changes` (historial único de módulos y estado del tenant):**

| Columna | Tipo |
| --- | --- |
| `id` | `uuid` PK (v7) |
| `tenant_id` | `uuid NOT NULL`, FK a `tenancy.tenants` `ON DELETE CASCADE` |
| `batch_id` | `uuid NOT NULL` — uno por operación; agrupa la cascada en la UI |
| `kind` | `varchar(16) NOT NULL`, `CHECK (kind IN ('module','tenant_status'))` |
| `module_key` | `varchar(32) NULL` — obligatorio con `kind = 'module'`, nulo con `'tenant_status'` (`CHECK`) |
| `from_status` | `varchar(20) NULL` (`NULL` = la fila del módulo no existía) |
| `to_status` | `varchar(20) NOT NULL` — `active`/`inactive` para módulos; `Active`/`Suspended` para el tenant (el nombre del enum, como `tenants.status`) |
| `reason` | `varchar(16) NOT NULL`, `CHECK (reason IN ('contract','courtesy','nonpayment','cancellation','correction'))` |
| `note` | `varchar(300) NULL` |
| `actor_user_id` | `uuid NOT NULL` |
| `occurred_at` | `timestamptz NOT NULL` |

Una sola tabla y no dos (revisión, YAGNI): el historial que la consola muestra es uno, paginado y
agrupado por `batch_id`. Índice `(tenant_id, occurred_at DESC)`. Sin navegación; la FK se declara en
el modelo de EF por la misma razón que `tenant_modules` (orden de los `INSERT`). Entidad de dominio
`TenantChange` con dos fábricas (`ForModule`, `ForTenantStatus`).

**Dominio (`Tenancy.Domain`):**

- `TenantModuleStatus` (`Active`, `Inactive`) y `ChangeReason` (`Contract`, `Courtesy`,
  `Nonpayment`, `Cancellation`, `Correction`), con su texto de base en minúscula.
- `TenantModule` gana `Status` y `StatusChangedAt`, y dos métodos: `Activate(at)` y `Deactivate(at)`.
  Cada uno falla con `TenantDomainException` si el módulo ya está en ese estado (no hay cambios
  vacíos). `TenantModule.Create` se mantiene, crea en `Active` y fija `StatusChangedAt = enabledAt`.
  El comentario de la clase («apagar es borrarla») se actualiza.
- Desactivar una clave **sin fila** es `tenancy.modules.no_changes`: no se crea una fila inactiva.
- **Motivos válidos por dirección** (D2):
  - activar: `contract`, `courtesy`, `correction`;
  - desactivar: `nonpayment`, `cancellation`, `correction`.
- `TenantModuleChangeBatch` (servicio de dominio puro): recibe el estado guardado actual (claves con
  su estado, incluidas las inactivas), la lista de cambios pedidos, el motivo, y devuelve el plan o
  falla:
  - `tenancy.modules.no_changes` — lista vacía o algún cambio que no cambia nada;
  - `tenancy.modules.duplicate_key` — la misma clave dos veces;
  - `tenancy.modules.mixed_directions` — activaciones y desactivaciones en el mismo lote (la cascada
    siempre va en una sola dirección; mezclar no tiene caso de uso y complica el motivo);
  - `tenancy.modules.reason_not_allowed` — motivo que no corresponde a la dirección;
  - `tenancy.modules.inconsistent_dependencies` — ver la regla de abajo. El mensaje (sólo para
    logs; la SPA nunca muestra el `detail`) nombra los pares `módulo → dependencia` rotos.

  **Regla de consistencia (exacta).** Sea `S'` el estado guardado después de aplicar el lote y `L`
  el conjunto de claves del lote. El lote se rechaza si existe un par `(m, d)` con `m` activo en
  `S'`, `d ∈ TenantModuleKeys.DependenciesOf(m)` (dependencias **directas**), `d` no activo en `S'`,
  **y** `m ∈ L` o `d ∈ L`. Así:

  - desactivar `customers` con `quotations` activo fuera del lote → rechazado (`d ∈ L`);
  - activar `quotations` sin `customers` activo → rechazado (`m ∈ L`);
  - un par roto heredado del SQL manual que el lote no toca (`orders` activo sin `quotations`) no
    bloquea activar `pos`: ninguno de los dos está en `L`. El lote no empeora nada.

  Basta con dependencias directas: si el lote deja consistente cada par que toca, los transitivos
  quedan cubiertos par por par.

**Concurrencia (dos operadores a la vez).** El handler abre una transacción explícita y toma
`pg_advisory_xact_lock` sobre el tenant destino **antes de leer** el estado, con el mismo patrón que
`TenancyUnitOfWork.BeginUserLifecycleScopeAsync` (`TenancyUnitOfWork.cs:30-42`). Sin el candado, en
READ COMMITTED, «A activa `quotations`» y «B desactiva `customers`» pasarían los dos la validación
(write skew). Además, `TenancyUnitOfWork` traduce la violación de `PK_tenant_modules` (dos
activaciones simultáneas de una clave sin fila) a `tenancy.modules.no_changes`, discriminando por
nombre de constraint, como ya hace con `IX_tenants_slug`; sin eso sería un 500.

### 4. Estado del tenant

- `Tenant` gana `Suspend(ChangeReason reason, DateTimeOffset at)` y `Reactivate(ChangeReason reason,
  DateTimeOffset at)`:
  - `Suspend` sólo desde `Active`; motivos `nonpayment` o `cancellation`. Otro motivo:
    `tenancy.tenant.reason_not_allowed`. Ya inactivo: `tenancy.tenant.already_inactive`.
  - `Reactivate` sólo desde `Suspended`; motivos `contract`, `courtesy` o `correction`. Ya activo:
    `tenancy.tenant.already_active`.
  - Ambos suben `Version` y `UpdatedAt` (concurrencia optimista existente).
  - **No emiten eventos de dominio**: `OutboxWriter` (`OutboxWriter.cs:24-25`) lanza ante un evento
    sin mapear, y nadie consume este cambio fuera de Tenancy. El historial y la auditoría alcanzan.
  - Los demás valores de `TenantStatus` (`Provisioning`, `Failed`, `Decommissioning`,
    `Decommissioned`) no se tocan: nadie los asigna hoy. Deuda aparte.
- **No se puede inactivar el tenant operador:** el handler lo rechaza con
  `422 tenancy.tenant.operator_cannot_be_suspended` antes de tocar el agregado (el dominio no conoce
  la configuración).
- **Historial:** una fila en `tenancy.tenant_changes` con `kind = 'tenant_status'` (§3).

**Dónde se aplica (enforcement):**

| Punto | Cambio |
| --- | --- |
| Sesión — `IActiveTenantsQuery.ListActiveTenantsAsync` (login y `/auth/me`) | excluye tenants con `status <> 'Active'`. Un usuario cuya única empresa está inactiva queda sin tenants, como un usuario sin membresías activas. |
| Permisos — `IMembershipDirectory.FindActiveRolesAsync` | devuelve `null` si el tenant no está `Active`. Con eso `ResolvePermissionsAsync` devuelve `null`, `ExternalClaimsTransformation` no agrega el claim de tenant y todo endpoint del tenant responde 403. Cubre sesiones ya abiertas: los permisos se resuelven en cada request. |
| Stub de desarrollo | consulta el estado con un puerto **propio**: `ITenantDirectory` (ya existe) gana `GetStatusAsync(Guid tenantId) → TenantStatus?` (`null` = sin fila). Si hay fila y no es `Active`, no emite claims de tenant ni permisos (mismo efecto que el camino real). Sin fila (tenant simulado), sin cambio. **`ITenantModules.FindAsync` no cambia su semántica de `null`** («tenant simulado, no enmascarar»): usarlo para expresar «inactivo» le daría al stub todos los permisos de `X-Permissions` sin enmascarar. |
| Endpoint de capacidades `GET /tenants/{id}/modules` | sin cambio de código: ya exige el claim de tenant, que un tenant inactivo no tiene → 403. |

El enmascarado de módulos y el estado del tenant son independientes: inactivar un tenant **no**
toca `tenant_modules`, así que al reactivarlo vuelve con los mismos módulos.

`MembershipRepository.ListActiveTenantsByUserAsync` (usado por `MembershipActivationService`) queda
sin filtrar por estado del tenant: su resultado se descarta en `/auth/session`
(`AuthSessionEndpoints.cs:98-105`), así que no abre acceso. Aceptar una invitación a un tenant
inactivo activa la membresía, pero la sesión no lo lista y los permisos lo niegan.

### 5. API de operador

Grupo `/api/v1/tenants/{tenantId:guid}/operator`. `{tenantId}` es el **tenant desde el que actúa el
operador** (el de QCode), igual que en el resto de la API; el tenant administrado va como
`{targetTenantId:guid}`. Doble capa, como exige el `CLAUDE.md`:

1. la política del endpoint exige el permiso `operator.*` (que sólo existe en el tenant operador);
2. el handler revalida con `OperatorAuthorization.EnsureAuthorized(executionContext, tenantId,
   permission, operatorTenant)`: el `tenantId` de la ruta coincide con el claim, el permiso está en
   los claims **y** `IOperatorTenant.IsOperator(tenantId)`. Cualquier falla: `403`.

Aquí un `404` por `targetTenantId` inexistente **sí** es correcto: el operador ve todos los tenants
por diseño, así que confirmar que un id no existe no filtra nada que no pueda listar. **Orden de
chequeos:** primero `OperatorAuthorization.EnsureAuthorized`, después la búsqueda del destino, para
que el 404 no le sirva de oráculo a quien no es operador.

**Entradas como texto, validadas antes de convertir.** Los comandos y queries llevan claves,
estados y motivos como `string`; un validador de FluentValidation rechaza valores desconocidos con
`422 validation.failed` y **después** se convierten a `TenantModuleKey`/enums. Convertir antes
haría que `TenantModuleKey.Parse` (`TenantModuleKey.cs:22-24`, lanza `ArgumentException`) saliera
como 500. Lo mismo para los parámetros de query: `module` (clave conocida), `search` (≤ 100),
`page` ≥ 1, `pageSize` entre 1 y 100.

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /operator/tenants?search=&page=&pageSize=` | `operator.tenants.read` | `OperatorTenantPageDto` |
| `GET /operator/tenants/{targetTenantId}` | `operator.tenants.read` | `OperatorTenantDetailDto` |
| `POST /operator/tenants/{targetTenantId}/modules/changes` | `operator.modules.manage` | `OperatorTenantDetailDto` (el detalle actualizado) |
| `POST /operator/tenants/{targetTenantId}/status` | `operator.tenants.manage` | `OperatorTenantDetailDto`; exige `If-Match` con la versión del tenant (`428` sin él, `412` desactualizado) |
| `GET /operator/tenants/{targetTenantId}/history?module=&page=&pageSize=` | `operator.tenants.read` | `OperatorHistoryPageDto` |

**DTOs (forma BFF; el motivo de cada decisión va escrito en el DTO):**

```jsonc
// OperatorTenantPageDto — paginado, orden por nombre y luego id; search en nombre o slug (ILIKE, comodines escapados)
{
  "items": [
    { "tenantId": "…", "slug": "origen-botanico", "displayName": "Origen Botánico",
      "status": "Active", "createdAt": "2026-08-12T13:30:00Z",
      "activeModules": 6, "totalModules": 7, "isOperator": false }
  ],
  "total": 6, "page": 1, "pageSize": 25,
  "summary": { "total": 6, "withoutModules": 1, "inactive": 0 }
}
```

- `summary` viaja con la página por la misma razón que `/reports/orders/summary`: sumarlo en el
  cliente depende de la página que se mire. Cuenta sobre todos los tenants, sin el filtro `search`.
- `totalModules` es `TenantModuleKeys.All.Count`: la pantalla no conoce la lista del backend.

```jsonc
// OperatorTenantDetailDto
{
  "tenantId": "…", "slug": "…", "displayName": "…", "createdAt": "…",
  "status": "Active", "statusChangedAt": "…|null", "statusReason": "nonpayment|null",
  "version": 7, "isOperator": false,
  "modules": [   // siempre las siete, en el orden de TenantModuleKeys.All, aunque no tengan fila
    { "key": "quotations", "status": "active",            // "active" | "inactive" | "none" (sin fila)
      "enabled": true,                                     // efectivo (dependencias incluidas)
      "dependencies": ["catalog", "customers", "companies"],
      "since": "2026-08-12T13:30:00Z|null",               // status_changed_at
      "source": "signup|backfill|seed|manual|operator|null",
      "lastReason": "contract|…|null" }                   // del último cambio en el historial
  ]
}
```

- `dependencies` viaja en cada módulo para que la consola calcule la cascada **sin** duplicar el
  grafo del backend.
- `"none"` existe porque «nunca se activó» y «se desactivó» se dibujan distinto.
- `statusChangedAt`/`statusReason` salen del último registro `kind = 'tenant_status'` de
  `tenant_changes` (`null` si no hay).

```jsonc
// POST …/modules/changes — cuerpo
{ "changes": [ { "key": "customers", "status": "inactive" },
               { "key": "quotations", "status": "inactive" },
               { "key": "orders", "status": "inactive" } ],
  "reason": "cancellation", "note": "No lo usan por ahora" }
```

- Validador de FluentValidation (texto libre ⇒ validador, aunque el dominio valide): `changes` no
  vacío y con a lo más siete elementos, `key` conocida, `status` `active|inactive`, `reason` conocida,
  `note` ≤ 300. Errores de forma: `422 validation.failed` con `errors`. Las reglas de negocio (D2,
  consistencia) son códigos de dominio.
- Un cambio a `active` de una clave **sin fila** crea la fila con `source = 'operator'`.
- Todo en **una transacción** (`ITenancyUnitOfWork`, con el candado de §3): filas de
  `tenant_modules`, filas del historial con un mismo `batch_id`, y un registro de auditoría
  `tenancy.tenant_modules.changed` (`IAuditRecorder`, como el resto de Tenancy).
  **Auditoría:** `tenantId` = el tenant **destino**, `resourceType = "tenant"`,
  `resourceId` = id del destino, `changed_fields` = un string por cambio con la forma
  `"customers:active->inactive"` más `"reason:cancellation"`.
- Sin `If-Match` (D3): el candado serializa las operaciones sobre el mismo tenant.

```jsonc
// POST …/status — cuerpo (If-Match: "7")
{ "status": "inactive", "reason": "nonpayment", "note": "Factura de septiembre sin pagar" }
```

- Mismo patrón: validador de forma, reglas en el dominio, historial (`kind = 'tenant_status'`) +
  auditoría `tenancy.tenant.status_changed` (mismos `tenantId`/`resource*` que arriba) en la misma
  transacción.

```jsonc
// OperatorHistoryPageDto — lotes del más reciente al más antiguo
{
  "items": [
    { "batchId": "…", "kind": "module",                  // "module" | "tenant_status" (mismo texto que la columna)
      "occurredAt": "…", "actorUserId": "…", "actorEmail": "andres@…|null",
      "reason": "courtesy", "note": "…|null",
      "changes": [ { "moduleKey": "reporting", "fromStatus": "inactive", "toStatus": "active" } ] },
    { "batchId": "…", "kind": "tenant_status", "occurredAt": "…", "actorUserId": "…",
      "actorEmail": "…", "reason": "nonpayment", "note": null,
      "changes": [ { "moduleKey": null, "fromStatus": "Active", "toStatus": "Suspended" } ] }
  ],
  "total": 3, "page": 1, "pageSize": 25
}
```

- Sale de `tenant_changes`, agrupado por `batch_id` y paginado **por lote** (no por fila). Con el
  filtro `module`, se listan los lotes que tocan ese módulo (con todos sus cambios) y no los de
  estado del tenant.
- `actorEmail` se resuelve con `IUserDirectory.GetEmailAsync` directamente desde
  `Tenancy.Application` (ya referencia `Identity.Application`; precedente `ListMemberships.cs:102`),
  deduplicando los actores de la página. Usuario que ya no existe: `null`, y la UI muestra el id
  corto.
- **El historial es inmutable:** no hay endpoint para editarlo ni borrarlo.

### 6. Frontend (`qep-frontend`)

Screaming architecture (`SDD-ADR-07`): todo vive en `src/features/operator/`
(`components/ hooks/ pages/ services/ types/ utils/`); `src/routes/` sólo cablea.

- **Tenant desde el que se actúa:** `{tenantId}` de las rutas de la API es
  `useActiveTenant().tenantId` (el `X-Tenant-Id` lo pone `api-client.ts`). La consola sólo es
  alcanzable con QCode como tenant activo.
- **Rutas** (en inglés, como el resto de la app; la UI dice «Plataforma»), con pestañas como
  **subruta** (precedente: `orders/$orderId.tsx` + `index.tsx` + `editar.tsx`; `reports-tabs.tsx`),
  no `?tab=`:
  - `src/routes/_authenticated/operator.tsx` — layout con `OperatorReadGate` + `Outlet`, para que
    ninguna query salga sin `operator.tenants.read`. Sin `ModuleGate`: operador es núcleo.
  - `operator/index.tsx` — redirige a `/operator/tenants` (como `catalog/index.tsx`).
  - `operator/tenants/index.tsx` — lista.
  - `operator/tenants/$targetTenantId.tsx` — layout: cabecera, estado del tenant, pestañas, `Outlet`.
  - `operator/tenants/$targetTenantId/index.tsx` (módulos) y `…/history.tsx` (historial).
  - `src/routeTree.gen.ts` lo genera el plugin.
- **Gate:** `OperatorReadGate` en `features/operator/components/`, mismo patrón que
  `QuotesReadGate` (`usePermissions().can`, tarjetas «Cargando permisos…» / «No tienes permiso…»).
  No existe un 403 global.
- **Sidebar:** un ítem al final, `{ to: '/operator/tenants', label: 'Plataforma', icon: <uno sin
  usar>, permission: 'operator.tenants.read' }`. **Falla cerrado:** con permisos en `error`,
  `loading` o `denied` no se dibuja (hoy `visibleSidebarItems` devuelve todo ante `error`,
  `sidebar-nav-items.ts:254`; el cambio aplica sólo a este ítem), y `NavSkeleton` no le reserva
  fila. El permiso **no** se agrega a `ALL_SIDEBAR_PERMISSIONS` de las pruebas existentes.
- **Lista:** búsqueda (con debounce), tabla con nombre + slug, barra de módulos activos (n de 7),
  estado (Activo/Inactivo), creado, acción «Gestionar módulos»; tarjetas de resumen (`summary`);
  marca «Operador»; aviso «Sin módulos» cuando `activeModules = 0`.
- **Detalle — módulos:** como la maqueta. Un `Switch` (Radix, `components/ui/switch.tsx`) por
  módulo con `checked` = estado del servidor y `onCheckedChange` que **sólo abre el diálogo** (no
  cambia nada en local); dependencias, «desde», origen y último motivo. La cascada es una función
  pura en `features/operator/utils/`, basada en **`status`, no en `enabled`** (activo =
  `status === 'active'`), con `dependencies` **directas**: activar suma, en clausura transitiva, las
  dependencias con `status !== 'active'`; desactivar suma, en clausura transitiva invertida, los
  dependientes con `status === 'active'`. Diálogo con la lista, **motivo obligatorio filtrado por
  dirección** (D2) y nota opcional; confirmar manda **un** `POST …/modules/changes`. Sin
  `operator.modules.manage`, los interruptores se ven deshabilitados.
- **Errores:** `describeOperatorFailure()` con un diccionario por código (patrón
  `tenant-settings.api.ts:120-160`); nunca se muestra el `detail` del backend. Ante
  `inconsistent_dependencies` o `no_changes`: «El estado de los módulos cambió. Revisa cómo quedó.»
  y se recarga el detalle (con la cascada calculada en el cliente, sólo aparecen si otro operador
  cambió algo entre la lectura y el envío).
- **Caché después de una escritura** (`POST …/modules/changes` o `…/status`): `cancelQueries` antes
  de `setQueryData` del detalle (precedente `use-tenant-settings.ts:43-57`), e invalidar el
  historial de ese tenant y la lista. **Si el destino es el propio tenant activo** (QCode cambiando
  sus módulos), invalidar también `tenantModulesQueryKey(tenantId)` y
  `effectivePermissionsQueryKey(tenantId)` (`tenancy.api.ts`), o su sidebar queda viejo.
- **Query keys** con el tenant activo, como el resto: `['operator', tenantId, 'tenants', {search,
  page, pageSize}]`, `['operator', tenantId, 'tenant', targetTenantId]`, `['operator', tenantId,
  'history', targetTenantId, {module, page, pageSize}]`. Las respuestas se parsean validando la
  forma, como `parseTenantModules`.
- **Detalle — estado del tenant:** en la cabecera, el estado y un botón «Inactivar» / «Reactivar»
  (oculto si `isOperator`; deshabilitado sin `operator.tenants.manage`). Diálogo con motivo
  filtrado por dirección y nota; manda `If-Match: "<version>"` (formato de
  `tenant-settings.api.ts:43`). En `412`/`428`: invalida, recarga y avisa en línea; sin reintento.
- **Historial:** lista por lotes, filtro por módulo, motivo traducido, quién (email), nota; leyenda
  «El historial no se puede editar ni borrar»; paginación con `ListPagination`.
- **Lista:** búsqueda con `useDebouncedValue`; barra «n de 7» (`Progress` o barritas); tabla con
  las primitivas `DataTable*`; paginación con `ListPagination`.
- **Etiquetas:** `MODULE_LABELS` y `TENANT_MODULE_KEYS` de `@/features/auth/types/tenant-modules`
  (precedente: `module-gate.tsx` ya las importa; no se mueven). **No** confundir con el
  `MODULE_LABELS` local de `features/reports/types/request-log.ts`. Textos nuevos:
  - motivos: `contract` Contrató, `courtesy` Cortesía, `nonpayment` Falta de pago, `cancellation`
    Ya no continúa, `correction` Corrección;
  - estado del tenant: `Active` Activo, `Suspended` Inactivo;
  - estado del módulo: `active` Activo, `inactive` Inactivo, `none` Nunca activado;
  - origen: `signup` Registro, `backfill` Migración, `seed` Semilla, `manual` SQL manual,
    `operator` Consola;
  - categoría de permisos `Operator`: «Operador de plataforma» en `PERMISSION_CATEGORY_LABEL`;
  - un mensaje por cada código de error de §5 y para el 404 («Ese tenant no existe»).
- **Tenant inactivo con la sesión abierta** (usuario de ese tenant): `useSession` tiene
  `staleTime: Infinity`, así que la SPA no se entera sola. Cuando `usePermissions` resuelve
  `denied`, se refresca la sesión; si el tenant ya no está en `activeTenantIds`,
  `useActiveTenant` lo descarta y el gate muestra el selector o la pantalla sin espacios. El texto
  de esa pantalla no cambia en esta entrega (DECISIÓN-PENDIENTE).
- **Textos** en español colombiano tuteando; comentarios de código en inglés (convención del front).
- **Caché de módulos del tenant administrado:** después de un cambio, el detalle se reemplaza con la
  respuesta del `POST`. Los usuarios **del tenant afectado** siguen con el `staleTime` de 5 minutos
  de `useTenantModules` (DECISIÓN-PENDIENTE heredada).

### 7. Migración

Una migración en Tenancy, generada con el factory de diseño (no a mano), p. ej.
`AddOperatorConsole`:

- `tenant_modules`: `status` con `DEFAULT 'active'` y `status_changed_at` con `DEFAULT now()`,
  **ambos declarados en el modelo** para que el snapshot coincida; `UPDATE … SET status_changed_at =
  enabled_at` para las filas existentes; nuevo `CHECK` de status; `CHECK` de `source` ampliado con
  `'operator'`.
- Tabla `tenant_changes` con sus `CHECK`, índice y FK.
- **La migración generada se revisa y se edita:** EF puede emitir `defaultValue: ""` para un string
  requerido; el default `'active'` tiene que existir antes de crear el `CHECK`, o el `CHECK` falla
  sobre las filas existentes. El orden queda: columnas con default → `UPDATE` de
  `status_changed_at` → `CHECK`s → tabla nueva.
- `Down`: borra las filas `inactive` (en el modelo anterior, inactivo = sin fila), pasa
  `source = 'operator'` a `'manual'` (el `CHECK` restaurado no lo acepta) y quita columnas y tabla.
  **Ojo:** un `Down` devuelve el acceso a los tenants `Suspended`, porque el código anterior ignora
  el estado del tenant.

`README.md` § «Módulos por tenant»: la vía normal pasa a ser la consola. El SQL de respaldo se
reescribe: prender = `INSERT … (…, status, status_changed_at) … ON CONFLICT (tenant_id,
module_key) DO UPDATE SET status = 'active', status_changed_at = now()`; apagar =
`UPDATE … SET status = 'inactive', status_changed_at = now()`. Se aclara que el SQL no deja
historial. Se documenta `Platform:OperatorTenantId`.

## Errores y casos borde

| Caso | Resultado |
| --- | --- |
| Usuario de un tenant que no es el operador llama a `/operator/*` con un rol personalizado que tiene `operator.*` (insertado por SQL) | el filtro lo quita al resolver permisos → 403 de la política |
| Crear o editar un rol personalizado con `operator.*`, en cualquier tenant | `422 authorization.role.permission_operator_only` |
| Stub de desarrollo con `X-Permissions: operator.tenants.read` sobre un tenant no operador | el filtro lo quita → 403 |
| `Platform:OperatorTenantId` ausente (cualquier ambiente) | sin consola: 403 para todos; la app sigue funcionando; en producción, advertencia en el log al arrancar |
| `Platform:OperatorTenantId` = `Guid.Empty` | el arranque falla (`ValidateOnStart`) |
| Clave de módulo, estado o motivo desconocidos en el cuerpo o en `?module=` | `422 validation.failed` (nunca 500) |
| Dos operadores cambian módulos del mismo tenant a la vez | el candado los serializa; el segundo valida contra el estado ya cambiado |
| Desactivar `customers` con `quotations` y `orders` activos, sin incluirlos | `422 tenancy.modules.inconsistent_dependencies` nombrando `quotations`, `orders` |
| Estado guardado ya inconsistente (p. ej. `orders` activo sin `quotations` por SQL) y el lote activa `pos` | se permite: la regla mira sólo lo que el lote deja activo y sus dependencias (`pos` → `catalog`, `companies`) |
| …y el lote activa `quotations` sin `customers` | rechazado: `quotations` queda activo sin una dependencia |
| Activar un módulo ya activo | `422 tenancy.modules.no_changes` |
| Inactivar el tenant operador | `422 tenancy.tenant.operator_cannot_be_suspended` |
| `POST …/status` sin `If-Match` / con versión vieja | `428` / `412` |
| `targetTenantId` inexistente | `404` |
| Usuario con sesión abierta cuando su tenant pasa a inactivo | su siguiente request a ese tenant: 403 |
| Tenant inactivo reactivado | sus usuarios vuelven a verlo al iniciar sesión o al revalidar `/auth/me`; módulos intactos |

## Pruebas (TDD: RED antes que GREEN)

**Backend — unitarias:** `TenantModule.Activate/Deactivate`; `TenantModuleChangeBatch` (cada código de
error, cascada válida, estado previo inconsistente); `Tenant.Suspend/Reactivate` (motivos por
dirección, estados, versión); `OperatorPermissionFilter`; `OperatorTenantOptionsValidator` (ausente
válido, `Guid.Empty` inválido); validadores de forma (claves/motivos desconocidos → `validation.failed`);
handlers de operador con dobles (doble capa: tenant de ruta ≠ claim, no operador, sin permiso; el
chequeo de operador va antes del 404); `RoleCommands` rechaza `operator.*` en roles personalizados.
La regla de consistencia se prueba con los tres ejemplos de §3 como casos explícitos.

**Backend — integración** (con Testcontainers, una base por clase según el harness existente):

- lista, detalle, cambios y historial por HTTP como operador; 403 desde un tenant no operador con el
  permiso inyectado por `X-Permissions`; 403 con rol personalizado que contiene `operator.*`;
- `POST …/modules/changes`: filas, historial (un `batch_id`), auditoría y respuesta en la misma
  transacción; un lote inconsistente no deja ninguna fila cambiada;
- desactivar un módulo enmascara sus permisos en el siguiente request del tenant afectado
  (`/authorization/me`), y reactivarlo los devuelve;
- inactivar un tenant: `/auth/me` ya no lo lista y un request con sesión real recibe 403; reactivarlo
  lo devuelve con los mismos módulos; `428`/`412`;
- migración: filas existentes quedan `active` con `status_changed_at = enabled_at`; un `INSERT` sin
  `status` (como el de un pod viejo o el signup) sigue funcionando por el default;
- el stub de desarrollo sobre un tenant `Suspended` no emite permisos;
- dos `POST …/modules/changes` concurrentes sobre el mismo tenant no dejan un estado inconsistente
  (o, si el harness no permite concurrencia real confiable, prueba de que el candado se toma antes
  de leer);
- `ArchitectureTests`: sin cambios de capas (todo en módulos existentes).

**Barrido de cuerpos crudos** (`CLAUDE.md`): agregar `Platform:OperatorTenantId` no es un campo
requerido de request, pero cualquier harness que construya `PermissionDefinition` o el rol `admin`
a mano debe revisarse.

**Frontend (Vitest + Testing Library):** cálculo de cascada (función pura); diálogo (motivo
obligatorio, motivos por dirección, lote enviado); lista (búsqueda, resumen, sin módulos); historial
(filtro, agrupación); visibilidad del sidebar y las rutas por permiso; mapeo de códigos de error.

## Despliegue

1. Backend antes que frontend.
2. Se puede desplegar sin configurar nada: sin `Platform__OperatorTenantId` la consola no existe y
   todo lo demás funciona.
3. Para habilitarla, el owner agrega `Platform__OperatorTenantId: "<id del tenant QCode>"` al
   ConfigMap de producción y reinicia el despliegue. El id se obtiene con
   `SELECT id FROM tenancy.tenants WHERE slug = '<slug de QCode>';`.
4. Después, el `admin` de QCode ve «Plataforma» (puede requerir recargar la SPA).

## Decisiones tomadas sin el owner

| # | Decisión | Por qué |
| - | -------- | ------- |
| D1 | Tercer permiso `operator.tenants.manage` para el estado del tenant | Inactivar un tenant corta el acceso a toda una empresa; es más grave que tocar un módulo y merece delegarse por separado. El `admin` del operador lo tiene igual. |
| D2 | Motivos permitidos según la dirección (activar: contrató/cortesía/corrección; desactivar: falta de pago/ya no continúa/corrección) | Evita registros sin sentido («activado por falta de pago») y deja el historial legible para el cobro futuro. |
| D3 | `POST …/modules/changes` sin `If-Match`, con `pg_advisory_xact_lock` por tenant destino | Los módulos no tienen una versión de agregado; el candado evita el write skew sin agregar una. El estado del tenant sí lleva `If-Match` porque ya existe la versión del agregado. |
| D4 | Lotes de una sola dirección | La cascada siempre va en un sentido; mezclar complicaría el motivo sin caso de uso. |
| D5 | Signup, semilla y backfill **no** escriben historial | Mantiene la decisión 20 del spec de entitlements; el origen sigue visible en `source`. El historial es de decisiones de un operador. |
| D6 | Rutas bajo `/tenants/{tenantId}/operator/…` en vez de `/operator/…` | Conserva el patrón del repo: la ruta lleva el tenant desde el que se actúa y el handler lo revalida contra el claim. |
| D7 | `404` para un `targetTenantId` inexistente | El operador puede listar todos los tenants; la regla de «403, nunca 404» protege el aislamiento entre tenants, que aquí no aplica. |
| D8 | Inactivar no toca `tenant_modules` | Reactivar debe dejar todo como estaba (O8). |
| D9 | La regla de consistencia sólo mira los pares que el lote toca | No bloquear la reparación de estados inconsistentes heredados del SQL manual, sin dejar entrar inconsistencias nuevas. |
| D10 | `Platform:OperatorTenantId` opcional también en producción (advertencia, no falla) | El id de QCode no está en el repo y el CI despliega `main` sin pruebas: un marcador en el ConfigMap tumbaría los pods. Sin la clave, la consola simplemente no existe. |
| D11 | `operator.*` sólo en roles de sistema (`admin`), nunca en roles personalizados | Con 1–3 operadores no hace falta delegar con roles personalizados, y cierra un camino de escalamiento. Se puede abrir después dentro del tenant operador. |
| D12 | Un solo historial (`tenant_changes`) para módulos y estado del tenant | La consola muestra un historial; dos tablas obligaban a unir y paginar dos fuentes. |
| D13 | Rutas de la SPA en inglés (`/operator/...`); la UI dice «Plataforma» | Consistencia con el resto de rutas (`/catalog`, `/orders`, `/reports`). |

## DECISIÓN-PENDIENTE

- **Qué ve el usuario de un tenant inactivo.** Hoy queda como «sin empresas activas». ¿Un mensaje
  propio («La cuenta de tu empresa está inactiva; escríbenos a …») con un contacto? Requiere que la
  sesión sepa que existe un tenant inactivo, lo que hoy no expone.
- **Propagación inmediata** de un cambio de módulos a los usuarios del tenant afectado (hoy hasta 5
  minutos por el `staleTime`). Heredada del spec de entitlements.
- **Workers, outbox y enlaces públicos** de un tenant inactivo (envíos programados, PDF públicos,
  comprobantes): siguen funcionando. ¿Se cortan?
- **Retención de datos** de un tenant que «ya no continúa» (Ley 1581).
- Los valores sin uso de `TenantStatus` (`Provisioning`, `Failed`, `Decommissioning`,
  `Decommissioned`).

## Historial de revisión

- 2026-10-08 — segunda pasada pedida por el owner: dos revisiones adversariales independientes
  (backend + contrato; frontend). Se corrigieron: la regla de consistencia del lote (estaba mal
  definida para lotes de sólo desactivación), el write skew entre operadores (candado), los
  defaults que rompían el rolling update y el SQL del README, el puerto del estado del tenant para
  el stub, las claves desconocidas que salían como 500, el marcador del ConfigMap que tumbaba los
  pods, el radio de explosión de `operator.*` (sólo roles de sistema), un solo historial, y las
  convenciones del front (gate, sidebar que falla cerrado, cascada por `status`, caché, pestañas
  como subruta, etiquetas).
