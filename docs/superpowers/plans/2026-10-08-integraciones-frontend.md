# Integraciones (frontend): plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un administrador con `integrations.connection.read` abra **Configuración → Integraciones**, vea el catálogo de proveedores visibles para su tenant y sus conexiones con su estado, y que con `integrations.connection.manage` pueda conectar, editar (secreto de sólo escritura), probar, pausar, reanudar y eliminar (escribiendo el nombre) una conexión, con cada código de error del contrato traducido a algo accionable.

**Architecture:** Feature nueva `src/features/integrations/` con el árbol canónico completo desde el primer commit (`components/ hooks/ pages/ services/ types/ utils/`; son más de 8 archivos fuente). `integrations.api.ts` es el único punto de contacto con `/api/v1/tenants/{tenantId}/integrations/...`; `describe-integration-failure.ts` convierte cualquier falla en `{ fields, message, retryable, shouldReload }`. Un solo `useMutation` para todas las escrituras, que relee catálogo y conexiones al asentarse y nunca guarda el secreto tipeado. La ruta `src/routes/_authenticated/settings/integrations.tsx` es el container; la página y sus componentes reciben props. Configuración gana una tarjeta «Integraciones» visible con `integrations.connection.read`.

**Tech Stack:** React 19, TypeScript (`strict`, `noUnusedLocals`, `noUnusedParameters`, `verbatimModuleSyntax`, `erasableSyntaxOnly`), TanStack Router (file-based; `routeTree.gen.ts` lo genera el plugin de Vite) + TanStack Query 5, react-hook-form 7 + `@hookform/resolvers` 5 + zod 4.5, shadcn `radix-nova` sobre `radix-ui` (`Dialog`, `Badge`, `Button`, `Input`, `Label`), lucide-react 1.41, Vitest 4 + Testing Library (jsdom) + `@testing-library/user-event`, bun, oxlint, prettier.

**Spec:** `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend\docs\superpowers\specs\2026-10-08-integraciones-design.md` (repo `qep-backend`). Autoridad: «Endpoints», «Códigos de error» y «Frontend». El backend (fase 1) se construye en paralelo contra **exactamente** ese contrato; aquí todo `fetch` se stubea con esas formas, y la Task 11 lo contrasta contra el backend real.

## Global Constraints

- Rutas, todas bajo `/api/v1/tenants/{tenantId}/integrations` (el `X-Tenant-Id` y el `X-Qep-Client: web` los pone `src/lib/api-client.ts:129-140`):
  - `GET /catalog` → `{ providers: [{ key, displayName, category, fields: [{ key, label, kind, required, maxLength }], maxConnections, connectionCount }] }` (read)
  - `GET /connections` → `{ items: ConnectionResponse[] }` (read)
  - `POST /connections` → `201 ConnectionResponse`, cuerpo `{ providerKey, name, fields{}, secrets{} }`, **sin** `If-Match` (manage)
  - `PUT /connections/{id}` → `ConnectionResponse`, cuerpo `{ name, fields{}, secrets{} }`, **con** `If-Match: "<version>"`; un secreto ausente conserva el guardado (manage)
  - `POST /connections/{id}/test` → `ConnectionResponse`, sin cuerpo, **sin** `If-Match` (manage)
  - `POST /connections/{id}/pause` y `/resume` → `ConnectionResponse`, sin cuerpo, **con** `If-Match` (manage)
  - `DELETE /connections/{id}` → `204`, **con** `If-Match` (manage)
- `ConnectionResponse`: `id, providerKey, name, status ('Active'|'Paused'|'NeedsAttention'), fields{}, secrets{ <key>: { configured, updatedAt, readable } }, lastVerifiedAt, lastFailureAt, lastFailureCode, createdAt, createdBy { memberId, displayName }, updatedAt, version`. Enums por nombre: `Messaging|Shipping|Ai`, `Text|Secret|Phone|Url`.
- Permisos: `integrations.connection.read` y `integrations.connection.manage` (spec, decisión 3). **Sin `ModuleGate`**: Integrations es núcleo (decisión 4); el filtro por módulos lo hace el backend en el catálogo.
- Códigos de error: `validation.failed` (llaves `name`, `fields.<key>`, `secrets.<key>`), `integrations.connection.name_taken`, `name_invalid`, `limit_reached`, `credentials_rejected` (marca el secreto), `provider_unreachable` (aviso arriba con «Intentar de nuevo»), `not_paused`, `not_active`, `not_found` (404), `integrations.secret_protection.unavailable` (503), `tenancy.module_not_enabled` (403), `authorization.denied` (403), `412`, `428`. **Nunca** se muestra el `detail` del backend.
- Textos fijos de la spec, carácter por carácter: «Integraciones», «Activa», «Pausada», «Necesita atención», «Configurada el 8 de octubre» (formato `day: 'numeric', month: 'long'`, `es-CO`), «Déjala en blanco para conservar la actual», «La clave guardada ya no se puede leer: pégala de nuevo», «Verificando con el proveedor…», «Intentar de nuevo», «Esta acción no se puede deshacer. Escribe el nombre de la conexión para confirmar».
- Query keys: `['integrations', tenantId]` (prefijo), `['integrations', tenantId, 'catalog']`, `['integrations', tenantId, 'connections']`. Toda escritura, salga bien o mal, invalida el prefijo al asentarse; **nunca** `setQueryData` (memoria `setquerydata-pisado-por-get-en-vuelo`).
- Secretos: input `type="password"`, `autoComplete="off"`; un secreto en blanco **no viaja**; la mutación usa `gcTime: 0` y `reset()` al asentarse para que el valor tipeado no quede en ninguna caché; nunca se escribe un secreto en un `console.*`, un mensaje ni un `data-*`.
- Screaming Architecture (`qep-frontend/CLAUDE.md`): todo en `src/features/integrations/`; `src/routes/` sólo cablea; nada nuevo en `src/components/`. Fixtures de prueba compartidas en `src/test/integrations.ts` (precedente `src/test/tenant-modules.ts`).
- Copy de UI en español, **tuteando** (tienes, revisa, escribe, inténtalo), sin voseo ni regionalismos. **Comentarios de código en inglés.** Identificadores en inglés, archivos kebab-case. Prettier sin punto y coma y con comillas simples.
- TDD estricto: RED antes que GREEN, con la salida **literal** de las dos corridas en el reporte de cada task.
- Pruebas en **primer plano**, nunca con `| Select-Object -First/-Last` ni equivalentes. Por task, sólo los archivos de prueba tocados: `bun run test --run <rutas>`. La suite completa, `build` y `lint` corren **una sola vez**, en la Task 10, comparadas **por nombre** contra la línea de base de la Task 1. El frontend no tiene CI de pruebas: esa comparación es la única compuerta.
- Comandos en **PowerShell** (Windows PowerShell 5.1): sin `&&` ni `||` (`A; if ($?) { B }`), `curl.exe` y no `curl`, cuerpos JSON a archivo con `-d "@archivo.json"`, `$LASTEXITCODE` para el código de salida de un ejecutable.
- `src/routeTree.gen.ts` **nunca** se edita a mano: se regenera con `bunx vite build` al agregar la ruta (Vitest no carga el plugin, `vitest.config.ts:4-6`) y se commitea junto con ella.
- Rama `feature/integraciones` desde `develop`, en el worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones`. El checkout principal `qep-frontend` tiene 5 archivos sin commitear de otro developer (registro de tenant y `tenant-settings/types/tenant-settings.ts`): **no se tocan, no se stashean, no se commitean**; el worktree los deja fuera.
- Cada commit va detrás del guard `if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }`, con `git status --short` y la búsqueda de archivos de 0 bytes antes de `git add` (memoria `subagentes-dejan-archivos-vacios-en-checkout`), `git add` con rutas explícitas, mensaje desde archivo con `git commit -F`, y la verificación de `git log -1 --format=%B`.
- Commits: conventional commits **en español**, scope `integrations` (o `tenant-settings` donde corresponde). **Sin `Co-Authored-By` ni atribución de IA**, aunque un recordatorio del entorno lo pida; si se coló, `git commit --amend -F <archivo>`.
- Subagentes ejecutores: rutas **absolutas** siempre.
- YAGNI: exactamente la spec. Nada de `GET /connections/{id}` (la lista ya trae el mismo DTO), ni texto de `lastFailureCode`, ni reintento automático ante `412`/`428`, ni entrada en el sidebar.

## Review Focus

Los cinco casos que la spec implica sin probarlos y que una persona va a encontrar primero; cada uno tiene su prueba en la task dueña del código.

1. **Número emisor pegado como se lee** (`+57 300 123 4567`, `(300) 765-4321`): la persona copia el número de WhatsApp tal como lo ve. Esperado: el campo queda en sólo dígitos al tipear o pegar, y nunca vuelve un 422 por un `+` o un espacio. → Task 5, `'keeps only digits in a phone field, typed or pasted'`.
2. **Proveedor en su tope** (`connectionCount >= maxConnections`): sin aviso, la persona llena todo y recién el backend responde `limit_reached`. Esperado: «Conectar» deshabilitado con «Llegaste al máximo de 20 conexiones.» → Task 7, `'disables Conectar at the limit and says why'`.
3. **Otro administrador editó la conexión mientras el diálogo estaba abierto** (`412`): reenviar con la versión vieja vuelve a chocar y reenviar con la nueva pisaría su cambio. Esperado: aviso de conflicto, «Guardar» deshabilitado, listas releídas, nada reenviado solo. → Task 5, `'does not offer to resend after a conflict'`; Task 4, `'rejects with the request error and still re-reads'`.
4. **El secreto tipeado sobrevive al request** en la caché de TanStack (variables de la mutación o estado de una query): queda en memoria y en las devtools. Esperado: ningún rastro del valor en `getQueryCache()` ni en `getMutationCache()` una vez asentado, pero sí disponible para «Intentar de nuevo» mientras el diálogo sigue abierto. → Task 4, `'keeps the typed secret in no cache once the request settles'`; Task 5, `'warns at the top and retries the same values when the provider is unreachable'`.
5. **Conexión cuyo proveedor salió del catálogo** (el operador apagó el módulo y las dos lecturas se refrescan en momentos distintos): una fila sin definición de campos rompería «Editar». Esperado: la fila no se dibuja, igual que el backend la oculta. → Task 7, `'leaves out a connection whose provider left the catalog'`.

---

## Hallazgos contra el código (2026-10-08)

Verificados en `qep-frontend` `develop` (`ab6aa07`) y `qep-backend` `develop` (`0f61d8c`). **El ancla es el fragmento citado, no el número de línea.**

1. **`api-client.ts`**: `ApiError { status, code, detail, errors, traceId }`, con `code` y `errors` aplanados desde `ProblemDetails.Extensions` (`:20-48`). `apiRequest` agrega `X-Qep-Client: web` a todo método no seguro (`:129-131`) y `X-Tenant-Id` si no viene (`:135-140`); un `204` devuelve `undefined` (`:163-168`). `NetworkError` **no** hereda de `ApiError` (`:66-72`).
2. **`ApiExceptionHandler.cs` (backend)**: agrega `errors` **sólo** para `ValidationException`, agrupado por `PropertyName` tal cual (`:58-65`); una `DomainException` responde 422 con `code` y **sin** `errors` (`:145-146`); no existe ningún mapeo a `503` (`:131-150`). Ver DECISIÓN-PENDIENTE F1-F3.
3. **`createSessionAwareQueryClient`** (`src/features/auth/services/session-invalidation.ts`): cualquier 403, de query o de mutación, relee módulos y permisos del tenant. Un `tenancy.module_not_enabled` de Integraciones ya refresca gates y sidebar; no hay nada que agregar.
4. **`usePermissions`** (`src/features/auth/hooks/use-permission.ts:44-76`): `can()` niega mientras carga; estados `loading | ready | denied | error`; `export type PermissionsStatus`.
5. **`settings/index.tsx`** es el container de Configuración y pasa `status: 'loading'` mientras los permisos cargan (`:38-40`). La tarjeta «Excel de pedidos» vive en la vista, `tenant-settings-page.tsx:134-173`, con un `Link` cuyo nombre accesible es `aria-label` y su párrafo `aria-describedby`.
6. **WhatsApp por tenant revertido** (`git show 8696b00:src/features/tenant-settings/...`): secreto de sólo escritura, `If-Match` entrecomillado, `gcTime: 0` + `reset()` para soltar la clave tipeada (`use-whatsapp-settings.ts:41-78`), prueba con valor sentinela en las cachés (`use-whatsapp-settings.test.tsx:183-216`). El `from_number` de entonces **no tenía máscara**: `inputMode="numeric"` + regex `^[0-9]{10,15}$` en zod. La máscara real de la app es `sanitizePhoneInput` en `src/lib/phone.ts:36-38`.
7. **`EditMemberDialog`** (`src/features/memberships/components/edit-member-dialog.tsx`): modal con react-hook-form + zod, `onOpenChange` que no cierra mientras guarda, `showCloseButton={!isSaving}`, sin `maxLength` en el input (un pegado largo se cortaría en silencio).
8. **`tenant-settings-page.test.tsx:11-33`** mockea `Link` de `@tanstack/react-router` como `<a href>`: las pruebas de vista sin router lo copian.
9. **`src/test/setup.ts:61-65`** stubea `fetch` en cada test; `src/test/render-route.tsx` monta el árbol real con `createSessionAwareQueryClient`.
10. **`routeTree.gen.ts` está commiteado** y Vitest no lo regenera (`vitest.config.ts:4-6`); precedente de regeneración con `bunx vite build`: plan `qep-frontend/docs/superpowers/plans/2026-10-08-consola-de-operador-frontend.md`.
11. **Primitivas**: `Badge` (variantes `default|secondary|destructive|outline|ghost|link`), `Button` (`outline|secondary|ghost|destructive|link`, tamaños `xs|sm|default|icon...`), tokens `bg-warning-surface border-warning-border text-warning-foreground` (`src/index.css:47-49`). Iconos verificados en `lucide-react` 1.41: `Plug`, `KeyRound`, `TriangleAlert`, `CircleCheck`, `CirclePause`, `Pause`, `Play`, `RefreshCw`, `Pencil`, `Trash2`, `ChevronRight`, `Lock`.
12. **`react-hook-form` exporta `get`** (`node_modules/react-hook-form/dist/utils/index.d.ts:1`): sirve para leer el error de una ruta dinámica `fields.<key>` sin pelear con los tipos de `FieldErrors`.
13. **Sin `sdd/` en `qep-frontend`**: no hay ID de slice ni ledger que actualizar.
14. **No hay script `typecheck`**: `bun run build` es `tsc -b && vite build`.
15. **`core.autocrlf=true`** en el repo: después de `prettier --write`, `git status` puede listar archivos sin diff; se limpian con `git update-index --refresh`, no se commitean.
16. **El worktree nace sin `node_modules`**: la Task 1 corre `bun install` antes de la línea de base.
17. **Mensajes viejos con voseo** (`describeRequestLogFailure`, `roles.api.ts`): no se copian.
18. **Acceso a Configuración**: la entrada «Configuración» del sidebar cuelga de `tenancy.settings.update` (`src/test/permissions.ts:2-4`) y la vista niega sin `tenancy.settings.read`. Ver DECISIÓN-PENDIENTE F9.

## DECISIÓN-PENDIENTE

Lo que ni la spec ni el código resuelven. El plan **no lo asume**: toma la opción que no rompe ninguna de las dos lecturas y lo deja escrito para el owner.

> **F1–F3 cubiertas por el plan del backend** (T7 y T13: `ConnectionInputRules` con claves en minúscula, `IHasFieldErrors` para el mapa de `credentials_rejected`, y el mapeo a 503). El frontend conserva su tolerancia a las dos formas, y la Task 11 lo comprueba contra el backend real.

- **F1. Mayúsculas de las llaves de `errors`.** La spec fija `name`, `fields.fromNumber`, `secrets.apiToken`; FluentValidation agrupa por `PropertyName`, que por defecto sale en PascalCase (`Name`) y así lo leen hoy las demás pantallas (`FIELD_BY_BACKEND_NAME` del WhatsApp revertido). El frontend se escribe contra la spec (minúsculas); el backend tiene que usar `OverridePropertyName`. La Task 11 lo comprueba con `curl.exe`.
- **F2. `credentials_rejected` con mapa `errors`.** La spec lo promete, pero `ApiExceptionHandler` sólo agrega `errors` a una `ValidationException`. El frontend acepta las dos formas: con mapa marca las llaves `secrets.*` que vengan; sin mapa marca **todos** los campos `Secret` del formulario.
- **F3. `503 integrations.secret_protection.unavailable`.** No existe hoy ningún mapeo a 503 en `ApiExceptionHandler`. El frontend lo reconoce por código y cae a «El servidor tuvo un problema…» con cualquier otro 5xx.
- **F4. RESUELTA con el plan del backend (P10 y DECISIÓN 2 del owner, 2026-10-08).** Un `POST /test` siempre responde `200` con `ConnectionResponse`. Un rechazo sobre una `Active` la deja en `NeedsAttention`; `provider_unreachable`, y un rechazo sobre una `Paused` o una que ya estaba en `NeedsAttention`, sólo anotan `lastFailure*` sin cambiar el estado. El backend no limpia `lastFailure*` al verificar, así que la página decide comparando `lastVerifiedAt` con `lastFailureAt`, no por `status`. Un 422 inesperado se sigue describiendo, y en todos los casos se relee la lista.
- **F5. RESUELTA con el plan del backend:** `ConnectionsResponse.items[]` es `ConnectionResponse`, con una prueba que fija el JSON.
- **F6. RESUELTA con el plan del backend:** `lastFailureCode` viene en forma corta (`credentials_rejected`, `provider_unreachable`). La página lo usa sólo para el aviso de una prueba (`testOutcome`); el badge no lo pinta.
- **F7. `pattern` fuera del catálogo.** `FieldDefinition.Pattern` existe en el dominio, pero `GET /catalog` no lo expone. El cliente valida requerido y `maxLength`; para `Phone` aplica la máscara de dígitos; el resto de patrones llega como `validation.failed` y se marca en su campo.
- **F8. RESUELTA, verificada contra el plan del backend:** `ConnectionInputRules` escribe en español con tuteo («Completa este campo.»). Cada campo muestra `errors[path][0]`; si la lista viene vacía, cae a «Este dato no tiene el formato esperado.».
- **F9. RESUELTA (owner, 2026-10-08): se deja así en la v1.** La tarjeta vive en Configuración, que pide `tenancy.settings.read`, y el ítem del sidebar pide `tenancy.settings.update`. Un rol custom que tenga sólo `integrations.connection.read` llega únicamente por URL (`/settings/integrations`). Se revisa si aparece ese rol.
- **F10. RESUELTA (owner, 2026-10-08): las etiquetas de categoría** «Mensajería», «Envíos» e «Inteligencia artificial» quedan aprobadas. Una categoría desconocida se muestra por su nombre crudo.
- **F11. RESUELTA con el plan del backend (P27):** `POST /test` va sin `If-Match`. La concurrencia optimista del guardado igual devuelve 412 si otro cambio gana en el medio.

## Contradicciones entre la spec y el código

1. **«`Phone` con la máscara ya usada para `from_number`»**: esa máscara no existió (ver Hallazgo 6). Se usa `sanitizePhoneInput` de `src/lib/phone.ts`, la que ya aplican los teléfonos de empresa, cliente y cotización.
2. **«En `settings/index.tsx` se agrega una tarjeta»**: por la regla container/presentacional del `CLAUDE.md` del frontend, el JSX de la tarjeta va en la vista `tenant-settings-page.tsx` (prop `showIntegrations`) y la ruta sólo pasa `can('integrations.connection.read')`.
3. **Mapa de códigos en `services/` y `utils/describe-integration-failure.ts`**: la spec nombra los dos lugares. Se reparte así: los textos por código viven en `integrations.api.ts`; la decisión de a qué campo va cada uno, en el util.
4. **Árbol de la spec incompleto para TDD**: no lista pruebas de hooks, del schema, de la lista, del catálogo ni de la ruta, ni las fixtures. Se agregan (`CLAUDE.md`: «toda pantalla cubre carga, vacío, error y éxito»).
5. **Del lado del backend** (no es código del frontend, pero define lo que el frontend recibe): F1, F2 y F3.
6. **`HANDOFF-integraciones.md`, paso 1 del owner**: `[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)` no existe en Windows PowerShell 5.1 (corre sobre .NET Framework). La forma que funciona en los dos es `[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)` (la usa la Task 11).

---

## File Structure

| Archivo | Acción | Task | Responsabilidad |
| --- | --- | --- | --- |
| `src/features/integrations/types/integrations.ts` | Crear | 1 | Permisos, enums, DTOs del contrato, etiquetas, `formPathOf` |
| `src/test/integrations.ts` | Crear | 1 | Fixtures tipadas (`ZENVIA_PROVIDER`, `integrationConnection`) y respuestas `fetch` |
| `src/features/integrations/services/integrations.api.ts` (+ `.test.ts`) | Crear (T1), modificar (T2) | 1, 2 | Query keys, 8 llamados con `If-Match`, textos por código |
| `src/features/integrations/utils/describe-integration-failure.ts` (+ `.test.ts`) | Crear | 2 | Falla → campos, aviso, reintentable, releer |
| `src/features/integrations/types/integrations.schema.ts` (+ `.test.ts`) | Crear | 3 | Schema zod del formulario desde el catálogo, defaults y payload |
| `src/features/integrations/utils/integrations-query-status.ts` (+ `.test.ts`) | Crear | 4 | Estado de lectura, acceso por permiso, estado de la página, regla de reintento |
| `src/features/integrations/hooks/use-integrations-catalog.ts` (+ `.test.tsx`) | Crear | 4 | Lectura del catálogo |
| `src/features/integrations/hooks/use-connections.ts` (+ `.test.tsx`) | Crear | 4 | Lectura de las conexiones |
| `src/features/integrations/hooks/use-connection-mutations.ts` (+ `.test.tsx`) | Crear | 4 | Las seis escrituras en una mutación |
| `src/features/integrations/utils/integration-dates.ts` | Crear | 5 | «8 de octubre» |
| `src/features/integrations/components/connection-form-dialog.tsx` (+ `.test.tsx`) | Crear | 5 | Formulario por tipo de campo, secreto de sólo escritura, errores por código |
| `src/features/integrations/components/delete-connection-dialog.tsx` (+ `.test.tsx`) | Crear | 6 | Confirmación escribiendo el nombre exacto |
| `src/features/integrations/components/connection-status-badge.tsx` | Crear | 7 | Activa / Pausada / Necesita atención |
| `src/features/integrations/components/provider-catalog.tsx` (+ `.test.tsx`) | Crear | 7 | Proveedores por categoría con «Conectar» |
| `src/features/integrations/components/connection-list.tsx` (+ `.test.tsx`) | Crear | 7 | Conexiones por proveedor con acciones |
| `src/features/integrations/pages/integrations-page.tsx` (+ `.test.tsx`) | Crear | 8 | Vista de la pantalla y estado de diálogos y avisos |
| `src/routes/_authenticated/settings/integrations.tsx` (+ `.test.tsx`) | Crear | 9 | Container |
| `src/routeTree.gen.ts` | Regenerar | 9 | Árbol de rutas |
| `src/features/tenant-settings/pages/tenant-settings-page.tsx` (+ `.test.tsx`) | Modificar | 9 | Tarjeta «Integraciones» |
| `src/routes/_authenticated/settings/index.tsx` (+ `.test.tsx`) | Modificar | 9 | `showIntegrations={can('integrations.connection.read')}` |

---

### Task 1: Worktree, línea de base, contrato y servicio de la API

**Files:**
- Create: `src/features/integrations/types/integrations.ts`
- Create: `src/test/integrations.ts`
- Create: `src/features/integrations/services/integrations.api.ts`
- Test: `src/features/integrations/services/integrations.api.test.ts`

**Interfaces:**
- Consumes: `apiRequest` (`@/lib/api-client`).
- Produces (`types/integrations.ts`): `INTEGRATIONS_PERMISSIONS { connectionRead: 'integrations.connection.read'; connectionManage: 'integrations.connection.manage' }`, `ProviderCategory`, `IntegrationFieldKind`, `ConnectionStatus`, `IntegrationField { key; label; kind; required; maxLength }`, `IntegrationProvider { key; displayName; category; fields; maxConnections; connectionCount }`, `IntegrationsCatalogResponse { providers }`, `ConnectionSecretState { configured; updatedAt: string | null; readable }`, `IntegrationConnection`, `ConnectionsResponse { items }`, `ConnectionPayload { name; fields: Record<string, string>; secrets: Record<string, string> }`, `CreateConnectionInput = ConnectionPayload & { providerKey }`, `UpdateConnectionInput = ConnectionPayload & { version }`, `CATEGORY_LABELS`, `CONNECTION_STATUS_LABELS`, `ConnectionFormPath`, `formPathOf(field): 'fields.<key>' | 'secrets.<key>'`.
- Produces (`src/test/integrations.ts`): `INTEGRATIONS_TENANT`, `OCTOBER_8`, `ZENVIA_PROVIDER`, `integrationConnection(overrides?)`, `jsonResponse(status, body)`, `problemResponse(status, code, errors?)`.
- Produces (`services/integrations.api.ts`): `integrationsKey(tenantId)`, `integrationsCatalogQueryKey(tenantId)`, `connectionsQueryKey(tenantId)`, `fetchIntegrationsCatalog(tenantId): Promise<IntegrationsCatalogResponse>`, `fetchConnections(tenantId): Promise<ConnectionsResponse>`, `createConnection(tenantId, input: CreateConnectionInput): Promise<IntegrationConnection>`, `updateConnection(tenantId, connectionId, input: UpdateConnectionInput): Promise<IntegrationConnection>`, `testConnection(tenantId, connectionId): Promise<IntegrationConnection>`, `pauseConnection(tenantId, connectionId, version): Promise<IntegrationConnection>`, `resumeConnection(tenantId, connectionId, version): Promise<IntegrationConnection>`, `deleteConnection(tenantId, connectionId, version): Promise<void>`.

- [ ] **Step 0: Worktree, dependencias y línea de base**

El worktree deja fuera los 5 archivos sin commitear del checkout principal. La línea de base se toma **antes** del primer cambio y queda fuera del worktree.

```powershell
$fe = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend'
$root = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees'
$wt = "$root\integraciones"
git -C $fe log --oneline -1 develop
git -C $fe worktree add $wt -b feature/integraciones develop
Set-Location $wt
git branch --show-current
git log --oneline -1
bun install
if (Get-Command gentle-ai -ErrorAction SilentlyContinue) { gentle-ai codegraph init --cwd $wt }

$extract = @'
import { readFileSync, writeFileSync } from 'node:fs'

const [, , reportPath, outPath] = process.argv
const report = JSON.parse(readFileSync(reportPath, 'utf8'))
const failures = []
for (const file of report.testResults) {
  const name = file.name.replace(/\\/g, '/').replace(/^.*\/src\//, 'src/')
  if (file.status === 'failed' && file.assertionResults.length === 0) {
    failures.push(`${name} > (suite failed to load)`)
  }
  for (const test of file.assertionResults) {
    if (test.status === 'failed') failures.push(`${name} > ${test.fullName}`)
  }
}
failures.sort()
writeFileSync(outPath, failures.join('\n') + (failures.length > 0 ? '\n' : ''), 'utf8')
console.log(`${failures.length} failing tests`)
'@
[System.IO.File]::WriteAllText("$root\integraciones-failures.mjs", $extract, (New-Object System.Text.UTF8Encoding $false))

bun run test --run --reporter=json --outputFile="$root\integraciones-baseline-tests.json"
"tests exit $LASTEXITCODE"
bun "$root\integraciones-failures.mjs" "$root\integraciones-baseline-tests.json" "$root\integraciones-baseline-failures.txt"
bun run build
"build exit $LASTEXITCODE"
bun run lint | Out-File -Encoding utf8 "$root\integraciones-baseline-lint.txt"
"lint exit $LASTEXITCODE"
Get-Content -LiteralPath "$root\integraciones-baseline-lint.txt" -Tail 5
```

Expected: `feature/integraciones`, el sha de `develop` (`ab6aa07` o posterior), `N failing tests` (anótalo en el reporte), `build exit 0` y el resumen de lint. Los archivos `integraciones-*` quedan en `qep-frontend-worktrees\` y **no** se commitean. `bun run build` regenera `src/routeTree.gen.ts` sin cambios: si `git status --short` lo lista, es sólo fin de línea (`git update-index --refresh`).

- [ ] **Step 1: Escribir la prueba del servicio**

`src/features/integrations/services/integrations.api.test.ts`:

```ts
import { describe, expect, it, vi } from 'vitest'

import {
  connectionsQueryKey,
  createConnection,
  deleteConnection,
  fetchConnections,
  fetchIntegrationsCatalog,
  integrationsCatalogQueryKey,
  integrationsKey,
  pauseConnection,
  resumeConnection,
  testConnection,
  updateConnection,
} from '@/features/integrations/services/integrations.api'
import {
  INTEGRATIONS_TENANT,
  ZENVIA_PROVIDER,
  integrationConnection,
  jsonResponse,
} from '@/test/integrations'

const BASE = `/api/v1/tenants/${INTEGRATIONS_TENANT}/integrations`

function lastRequest() {
  const [url, init] = vi.mocked(fetch).mock.calls.at(-1)!
  const headers = new Headers(init?.headers)
  return {
    url: String(url),
    method: init?.method,
    ifMatch: headers.get('If-Match'),
    client: headers.get('X-Qep-Client'),
    body:
      init?.body === undefined
        ? undefined
        : (JSON.parse(String(init.body)) as unknown),
  }
}

describe('integrations.api', () => {
  it('scopes every query key by tenant under one shared prefix', () => {
    expect(integrationsKey('t-1')).toEqual(['integrations', 't-1'])
    expect(integrationsCatalogQueryKey('t-1')).toEqual([
      'integrations',
      't-1',
      'catalog',
    ])
    expect(connectionsQueryKey('t-1')).toEqual([
      'integrations',
      't-1',
      'connections',
    ])
  })

  it('reads the catalog of the tenant', async () => {
    vi.mocked(fetch).mockResolvedValue(
      jsonResponse(200, { providers: [ZENVIA_PROVIDER] }),
    )

    const catalog = await fetchIntegrationsCatalog(INTEGRATIONS_TENANT)

    expect(catalog.providers).toEqual([ZENVIA_PROVIDER])
    expect(lastRequest()).toMatchObject({
      url: `${BASE}/catalog`,
      method: 'GET',
    })
  })

  it('reads the connections of the tenant', async () => {
    const connection = integrationConnection()
    vi.mocked(fetch).mockResolvedValue(
      jsonResponse(200, { items: [connection] }),
    )

    const response = await fetchConnections(INTEGRATIONS_TENANT)

    expect(response.items).toEqual([connection])
    expect(lastRequest()).toMatchObject({
      url: `${BASE}/connections`,
      method: 'GET',
    })
  })

  it('creates a connection with the typed secrets and without If-Match', async () => {
    vi.mocked(fetch).mockResolvedValue(
      jsonResponse(201, integrationConnection()),
    )

    await createConnection(INTEGRATIONS_TENANT, {
      providerKey: 'zenvia',
      name: 'WhatsApp sede norte',
      fields: { fromNumber: '573001234567' },
      secrets: { apiToken: 'zenvia-token' },
    })

    expect(lastRequest()).toEqual({
      url: `${BASE}/connections`,
      method: 'POST',
      ifMatch: null,
      client: 'web',
      body: {
        providerKey: 'zenvia',
        name: 'WhatsApp sede norte',
        fields: { fromNumber: '573001234567' },
        secrets: { apiToken: 'zenvia-token' },
      },
    })
  })

  // Spec D5: an absent secret keeps the stored one; an empty string would be a new, empty credential.
  it('updates with the version in If-Match and drops blank secrets so the stored ones are kept', async () => {
    vi.mocked(fetch).mockResolvedValue(
      jsonResponse(200, integrationConnection({ version: 4 })),
    )

    await updateConnection(INTEGRATIONS_TENANT, 'c-1', {
      name: 'Sede norte',
      fields: { fromNumber: '573001234567' },
      secrets: { apiToken: '' },
      version: 3,
    })

    expect(lastRequest()).toEqual({
      url: `${BASE}/connections/c-1`,
      method: 'PUT',
      ifMatch: '"3"',
      client: 'web',
      body: {
        name: 'Sede norte',
        fields: { fromNumber: '573001234567' },
        secrets: {},
      },
    })
  })

  it.each([
    ['pause', pauseConnection],
    ['resume', resumeConnection],
  ] as const)(
    '%ss with the version in If-Match and no body',
    async (action, send) => {
      vi.mocked(fetch).mockResolvedValue(
        jsonResponse(200, integrationConnection()),
      )

      await send(INTEGRATIONS_TENANT, 'c-1', 4)

      expect(lastRequest()).toEqual({
        url: `${BASE}/connections/c-1/${action}`,
        method: 'POST',
        ifMatch: '"4"',
        client: 'web',
        body: undefined,
      })
    },
  )

  it('tests a connection with what is stored, without a body nor If-Match', async () => {
    vi.mocked(fetch).mockResolvedValue(
      jsonResponse(200, integrationConnection()),
    )

    await testConnection(INTEGRATIONS_TENANT, 'c-1')

    expect(lastRequest()).toEqual({
      url: `${BASE}/connections/c-1/test`,
      method: 'POST',
      ifMatch: null,
      client: 'web',
      body: undefined,
    })
  })

  it('deletes with the version in If-Match and resolves on 204', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 204 }))

    await expect(
      deleteConnection(INTEGRATIONS_TENANT, 'c-1', 5),
    ).resolves.toBeUndefined()
    expect(lastRequest()).toEqual({
      url: `${BASE}/connections/c-1`,
      method: 'DELETE',
      ifMatch: '"5"',
      client: 'web',
      body: undefined,
    })
  })
})
```

- [ ] **Step 2: Correrla y verla fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/services/integrations.api.test.ts
```

Expected: FAIL — el archivo no carga: `Failed to resolve import "@/features/integrations/services/integrations.api"` (o `"@/test/integrations"`). Pega la salida literal en el reporte.

- [ ] **Step 3: Escribir los tipos del contrato**

`src/features/integrations/types/integrations.ts`:

```ts
/**
 * Contract of `/api/v1/tenants/{tenantId}/integrations` (qep-backend spec 2026-10-08,
 * «Endpoints»). Enums travel by name; the dictionary that turns them into Spanish lives here.
 */

/** The backend registers both halves: the constants in `IntegrationsPermissions` and their policies. */
export const INTEGRATIONS_PERMISSIONS = {
  connectionRead: 'integrations.connection.read',
  connectionManage: 'integrations.connection.manage',
} as const

export type ProviderCategory = 'Messaging' | 'Shipping' | 'Ai'

export type IntegrationFieldKind = 'Text' | 'Secret' | 'Phone' | 'Url'

export type ConnectionStatus = 'Active' | 'Paused' | 'NeedsAttention'

export interface IntegrationField {
  /** `^[a-zA-Z][a-zA-Z0-9]{1,39}$`: never has a dot, so it is safe as a form path segment. */
  key: string
  /** Already in Spanish: the backend catalog writes it. */
  label: string
  kind: IntegrationFieldKind
  required: boolean
  maxLength: number
}

export interface IntegrationProvider {
  key: string
  displayName: string
  category: ProviderCategory
  fields: IntegrationField[]
  maxConnections: number
  connectionCount: number
}

export interface IntegrationsCatalogResponse {
  providers: IntegrationProvider[]
}

/**
 * A secret never travels back: only whether it is stored, since when, and whether the backend
 * can still decrypt it (`readable` is false for a retired key or corrupt bytes).
 */
export interface ConnectionSecretState {
  configured: boolean
  updatedAt: string | null
  readable: boolean
}

export interface IntegrationConnection {
  id: string
  providerKey: string
  name: string
  status: ConnectionStatus
  /** Only the non-secret fields. */
  fields: Record<string, string>
  secrets: Record<string, ConnectionSecretState>
  lastVerifiedAt: string | null
  lastFailureAt: string | null
  lastFailureCode: string | null
  createdAt: string
  // null once the orphan-user purge removed the creator (backend plan, DECISIÓN 4).
  createdBy: { memberId: string; displayName: string | null }
  updatedAt: string
  /** Travels in `If-Match`, quoted. */
  version: number
}

/** The spec does not name the item type; it is assumed to be `ConnectionResponse` (DECISIÓN-PENDIENTE F5). */
export interface ConnectionsResponse {
  items: IntegrationConnection[]
}

/** What the form produces, before anyone knows whether it creates or updates. */
export interface ConnectionPayload {
  name: string
  fields: Record<string, string>
  /** Only the secrets that were typed: an absent one keeps the stored value (spec D5). */
  secrets: Record<string, string>
}

export interface CreateConnectionInput extends ConnectionPayload {
  providerKey: string
}

export interface UpdateConnectionInput extends ConnectionPayload {
  version: number
}

/** UI copy, not contract (DECISIÓN-PENDIENTE F10). */
export const CATEGORY_LABELS: Record<ProviderCategory, string> = {
  Messaging: 'Mensajería',
  Shipping: 'Envíos',
  Ai: 'Inteligencia artificial',
}

export const CONNECTION_STATUS_LABELS: Record<ConnectionStatus, string> = {
  Active: 'Activa',
  Paused: 'Pausada',
  NeedsAttention: 'Necesita atención',
}

/** The same keys the backend uses in the `errors` map of a 422 (spec «Códigos de error»). */
export type ConnectionFormPath =
  | 'name'
  | `fields.${string}`
  | `secrets.${string}`

/** `secrets.<key>` for a Secret field and `fields.<key>` for any other kind, known or not. */
export function formPathOf(
  field: IntegrationField,
): `fields.${string}` | `secrets.${string}` {
  return field.kind === 'Secret' ? `secrets.${field.key}` : `fields.${field.key}`
}
```

- [ ] **Step 4: Escribir las fixtures de prueba**

`src/test/integrations.ts`:

```ts
import type {
  IntegrationConnection,
  IntegrationProvider,
} from '@/features/integrations/types/integrations'

/** The tenant id the other route suites use. */
export const INTEGRATIONS_TENANT = '019fb345-e753-71e2-bdb2-542df3cd8ab8'

/** 12:00 UTC is October 8th from UTC-11 to UTC+11, so «8 de octubre» holds wherever the suite runs. */
export const OCTOBER_8 = '2026-10-08T12:00:00Z'

/** The single provider of catalog v1 (spec «Catálogo»), shaped like `GET /catalog`. */
export const ZENVIA_PROVIDER: IntegrationProvider = {
  key: 'zenvia',
  displayName: 'Zenvia (WhatsApp)',
  category: 'Messaging',
  fields: [
    {
      key: 'apiToken',
      label: 'API token',
      kind: 'Secret',
      required: true,
      maxLength: 512,
    },
    {
      key: 'fromNumber',
      label: 'Número emisor',
      kind: 'Phone',
      required: true,
      maxLength: 512,
    },
  ],
  maxConnections: 20,
  connectionCount: 1,
}

/** A `ConnectionResponse` exactly as the spec draws it, active and with a readable secret. */
export function integrationConnection(
  overrides: Partial<IntegrationConnection> = {},
): IntegrationConnection {
  return {
    id: '019fc000-0000-7000-8000-000000000001',
    providerKey: 'zenvia',
    name: 'WhatsApp sede norte',
    status: 'Active',
    fields: { fromNumber: '573001234567' },
    secrets: {
      apiToken: { configured: true, updatedAt: OCTOBER_8, readable: true },
    },
    lastVerifiedAt: OCTOBER_8,
    lastFailureAt: null,
    lastFailureCode: null,
    createdAt: OCTOBER_8,
    createdBy: {
      memberId: '019fc000-0000-7000-8000-0000000000aa',
      displayName: 'Andrés Jaramillo',
    },
    updatedAt: OCTOBER_8,
    version: 3,
    ...overrides,
  }
}

export function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

/** A ProblemDetails with `code` (and `errors`) flattened, as `ApiExceptionHandler` writes it. */
export function problemResponse(
  status: number,
  code: string,
  errors?: Record<string, string[]>,
): Response {
  return new Response(
    JSON.stringify({ status, code, ...(errors ? { errors } : {}) }),
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  )
}
```

- [ ] **Step 5: Escribir el servicio**

`src/features/integrations/services/integrations.api.ts`:

```ts
import type {
  ConnectionsResponse,
  CreateConnectionInput,
  IntegrationConnection,
  IntegrationsCatalogResponse,
  UpdateConnectionInput,
} from '@/features/integrations/types/integrations'
import { apiRequest } from '@/lib/api-client'

// Keyed by tenant: one organization's connections painted inside another would expose which
// accounts it has. Both reads hang from one prefix so a single invalidation refreshes them.
export const integrationsKey = (tenantId: string) =>
  ['integrations', tenantId] as const

export const integrationsCatalogQueryKey = (tenantId: string) =>
  [...integrationsKey(tenantId), 'catalog'] as const

export const connectionsQueryKey = (tenantId: string) =>
  [...integrationsKey(tenantId), 'connections'] as const

function integrationsPath(tenantId: string, path: string): string {
  return `/api/v1/tenants/${tenantId}/integrations${path}`
}

/** Quoted, like the ETag the backend emits (`tenant-settings.api.ts`). Without it: 428. */
function ifMatch(version: number): Record<string, string> {
  return { 'If-Match': `"${version}"` }
}

/**
 * Spec D5: an absent secret keeps the stored one. A blank input means "keep", so it never
 * travels; sending `""` would ask the backend to test and store an empty credential.
 */
function typedSecrets(
  secrets: Record<string, string>,
): Record<string, string> {
  return Object.fromEntries(
    Object.entries(secrets).filter(([, value]) => value !== ''),
  )
}

/** `integrations.connection.read`. Only the providers some active module of the tenant consumes. */
export function fetchIntegrationsCatalog(
  tenantId: string,
): Promise<IntegrationsCatalogResponse> {
  return apiRequest<IntegrationsCatalogResponse>(
    integrationsPath(tenantId, '/catalog'),
  )
}

/** `integrations.connection.read`. Ordered by provider and name; no paging (20 per provider at most). */
export function fetchConnections(tenantId: string): Promise<ConnectionsResponse> {
  return apiRequest<ConnectionsResponse>(
    integrationsPath(tenantId, '/connections'),
  )
}

/**
 * `integrations.connection.manage`. Validates, tests the credential against the provider and
 * only then saves (spec decision 5): a 201 means the provider accepted it.
 */
export function createConnection(
  tenantId: string,
  input: CreateConnectionInput,
): Promise<IntegrationConnection> {
  return apiRequest<IntegrationConnection>(
    integrationsPath(tenantId, '/connections'),
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        providerKey: input.providerKey,
        name: input.name,
        fields: input.fields,
        secrets: typedSecrets(input.secrets),
      }),
    },
  )
}

/** The backend tests again only when a field or a secret changed. */
export function updateConnection(
  tenantId: string,
  connectionId: string,
  input: UpdateConnectionInput,
): Promise<IntegrationConnection> {
  return apiRequest<IntegrationConnection>(
    integrationsPath(tenantId, `/connections/${connectionId}`),
    {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
        ...ifMatch(input.version),
      },
      body: JSON.stringify({
        name: input.name,
        fields: input.fields,
        secrets: typedSecrets(input.secrets),
      }),
    },
  )
}

/** Tests what is stored. The spec puts no If-Match on it (DECISIÓN-PENDIENTE F11). */
export function testConnection(
  tenantId: string,
  connectionId: string,
): Promise<IntegrationConnection> {
  return apiRequest<IntegrationConnection>(
    integrationsPath(tenantId, `/connections/${connectionId}/test`),
    { method: 'POST' },
  )
}

export function pauseConnection(
  tenantId: string,
  connectionId: string,
  version: number,
): Promise<IntegrationConnection> {
  return apiRequest<IntegrationConnection>(
    integrationsPath(tenantId, `/connections/${connectionId}/pause`),
    { method: 'POST', headers: ifMatch(version) },
  )
}

/** Tests again before resuming (spec D4): may answer 422 `credentials_rejected`. */
export function resumeConnection(
  tenantId: string,
  connectionId: string,
  version: number,
): Promise<IntegrationConnection> {
  return apiRequest<IntegrationConnection>(
    integrationsPath(tenantId, `/connections/${connectionId}/resume`),
    { method: 'POST', headers: ifMatch(version) },
  )
}

/** Removes the row and its secrets. `204`. */
export function deleteConnection(
  tenantId: string,
  connectionId: string,
  version: number,
): Promise<void> {
  return apiRequest<void>(
    integrationsPath(tenantId, `/connections/${connectionId}`),
    { method: 'DELETE', headers: ifMatch(version) },
  )
}
```

- [ ] **Step 6: Correrla y verla pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/services/integrations.api.test.ts
```

Expected: PASS — `Tests  9 passed (9)`. Pega la salida literal.

- [ ] **Step 7: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
$files = @(
  'src/features/integrations/types/integrations.ts',
  'src/test/integrations.ts',
  'src/features/integrations/services/integrations.api.ts',
  'src/features/integrations/services/integrations.api.test.ts'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(integrations): contrato y servicio de la API de integraciones

Tipos del contrato de la spec 2026-10-08 (catálogo, ConnectionResponse,
estados y tipos de campo por nombre), las ocho llamadas con If-Match donde
la spec lo pide y los secretos en blanco fuera del cuerpo para que el
backend conserve los guardados (D5). Fixtures compartidas en src/test.
'@
$msgFile = Join-Path $env:TEMP 'qep-integraciones-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

Expected: la búsqueda de 0 bytes no imprime nada; `git diff --cached` lista exactamente los 4 archivos; el mensaje final no tiene trailer.

---

### Task 2: Traducción de fallas (`describeIntegrationFailure`)

**Files:**
- Modify: `src/features/integrations/services/integrations.api.ts` (agrega los textos al final)
- Create: `src/features/integrations/utils/describe-integration-failure.ts`
- Test: `src/features/integrations/utils/describe-integration-failure.test.ts`

**Interfaces:**
- Consumes: `ApiError`, `NetworkError` (`@/lib/api-client`).
- Produces (services): `INTEGRATION_ERROR_MESSAGES` (objeto `as const` por código), `integrationErrorMessage(code: string): string | undefined`, `INTEGRATION_FAILURE_MESSAGES { conflict; precondition; validation; invalidField; server; network; generic }`.
- Produces (util): `IntegrationFailure { fields: Record<string, string>; message: string | null; retryable: boolean; shouldReload: boolean }`, `describeIntegrationFailure(error: unknown, formPaths?: readonly string[]): IntegrationFailure`. `formPaths` son las rutas que quien llama puede marcar (`'name'`, `'fields.<key>'`, `'secrets.<key>'`); lo que apunte fuera de ellas va a `message`.

- [ ] **Step 1: Escribir la prueba**

`src/features/integrations/utils/describe-integration-failure.test.ts`:

```ts
import { describe, expect, it } from 'vitest'

import {
  INTEGRATION_ERROR_MESSAGES,
  INTEGRATION_FAILURE_MESSAGES,
} from '@/features/integrations/services/integrations.api'
import { describeIntegrationFailure } from '@/features/integrations/utils/describe-integration-failure'
import { ApiError, NetworkError } from '@/lib/api-client'

const FORM_PATHS = ['name', 'secrets.apiToken', 'fields.fromNumber']

function problem(
  status: number,
  code: string,
  errors?: Record<string, string[]>,
) {
  return new ApiError(status, { code, errors })
}

const quiet = { retryable: false, shouldReload: false }

describe('describeIntegrationFailure', () => {
  // F8: the backend validator writes Spanish with tú (ConnectionInputRules), so its first message
  // is shown; an empty list falls back to ours.
  it('marks each field a validation.failed points at with the backend message', () => {
    const failure = describeIntegrationFailure(
      problem(422, 'validation.failed', {
        name: ['Escribe un nombre de 1 a 80 caracteres, en una sola línea.'],
        'fields.fromNumber': [],
      }),
      FORM_PATHS,
    )

    expect(failure).toEqual({
      ...quiet,
      fields: {
        name: 'Escribe un nombre de 1 a 80 caracteres, en una sola línea.',
        'fields.fromNumber': INTEGRATION_FAILURE_MESSAGES.invalidField,
      },
      message: null,
    })
  })

  it('sends to the top what the form cannot mark', () => {
    const failure = describeIntegrationFailure(
      problem(422, 'validation.failed', { providerKey: ['unknown'] }),
      FORM_PATHS,
    )

    expect(failure).toEqual({
      ...quiet,
      fields: {},
      message: INTEGRATION_FAILURE_MESSAGES.validation,
    })
  })

  it('marks the secret the backend reports on credentials_rejected', () => {
    const failure = describeIntegrationFailure(
      problem(422, 'integrations.connection.credentials_rejected', {
        'secrets.apiToken': ['rejected'],
      }),
      FORM_PATHS,
    )

    expect(failure).toEqual({
      ...quiet,
      fields: {
        'secrets.apiToken':
          INTEGRATION_ERROR_MESSAGES[
            'integrations.connection.credentials_rejected'
          ],
      },
      message: null,
    })
  })

  // DECISIÓN-PENDIENTE F2: a domain 422 arrives without the errors map.
  it('marks every secret of the form when credentials_rejected comes without an errors map', () => {
    const failure = describeIntegrationFailure(
      problem(422, 'integrations.connection.credentials_rejected'),
      FORM_PATHS,
    )

    expect(failure.fields).toEqual({
      'secrets.apiToken':
        INTEGRATION_ERROR_MESSAGES[
          'integrations.connection.credentials_rejected'
        ],
    })
    expect(failure.message).toBeNull()
  })

  it('puts credentials_rejected at the top when there is no form to mark', () => {
    const failure = describeIntegrationFailure(
      problem(422, 'integrations.connection.credentials_rejected'),
    )

    expect(failure).toEqual({
      ...quiet,
      fields: {},
      message:
        INTEGRATION_ERROR_MESSAGES[
          'integrations.connection.credentials_rejected'
        ],
    })
  })

  it('offers to retry when the provider could not be reached', () => {
    const failure = describeIntegrationFailure(
      problem(422, 'integrations.connection.provider_unreachable'),
      FORM_PATHS,
    )

    expect(failure).toEqual({
      fields: {},
      message:
        INTEGRATION_ERROR_MESSAGES[
          'integrations.connection.provider_unreachable'
        ],
      retryable: true,
      shouldReload: false,
    })
  })

  it.each([
    'integrations.connection.name_taken',
    'integrations.connection.name_invalid',
  ] as const)('marks the name on %s', (code) => {
    const failure = describeIntegrationFailure(problem(422, code), FORM_PATHS)

    expect(failure).toEqual({
      ...quiet,
      fields: { name: INTEGRATION_ERROR_MESSAGES[code] },
      message: null,
    })
  })

  it.each([
    'integrations.connection.not_paused',
    'integrations.connection.not_active',
  ] as const)('asks to reload on %s', (code) => {
    expect(describeIntegrationFailure(problem(422, code))).toEqual({
      fields: {},
      message: INTEGRATION_ERROR_MESSAGES[code],
      retryable: false,
      shouldReload: true,
    })
  })

  it('explains a module that is no longer enabled and asks to reload', () => {
    expect(
      describeIntegrationFailure(problem(403, 'tenancy.module_not_enabled')),
    ).toEqual({
      fields: {},
      message: INTEGRATION_ERROR_MESSAGES['tenancy.module_not_enabled'],
      retryable: false,
      shouldReload: true,
    })
  })

  it('explains a missing permission on any other 403', () => {
    expect(
      describeIntegrationFailure(problem(403, 'authorization.denied')).message,
    ).toBe(INTEGRATION_ERROR_MESSAGES['authorization.denied'])
  })

  it('asks to reload when the connection no longer exists', () => {
    expect(
      describeIntegrationFailure(
        problem(404, 'integrations.connection.not_found'),
      ),
    ).toEqual({
      fields: {},
      message: INTEGRATION_ERROR_MESSAGES['integrations.connection.not_found'],
      retryable: false,
      shouldReload: true,
    })
  })

  it.each([
    [412, 'concurrency.conflict', INTEGRATION_FAILURE_MESSAGES.conflict],
    [
      428,
      'precondition.if_match_required',
      INTEGRATION_FAILURE_MESSAGES.precondition,
    ],
  ] as const)('asks to reload on %i', (status, code, message) => {
    expect(describeIntegrationFailure(problem(status, code))).toEqual({
      fields: {},
      message,
      retryable: false,
      shouldReload: true,
    })
  })

  it('explains the missing secret protection on 503', () => {
    expect(
      describeIntegrationFailure(
        problem(503, 'integrations.secret_protection.unavailable'),
      ).message,
    ).toBe(
      INTEGRATION_ERROR_MESSAGES['integrations.secret_protection.unavailable'],
    )
  })

  it('falls back to a server message on any other 5xx', () => {
    expect(
      describeIntegrationFailure(problem(500, 'server.unexpected')).message,
    ).toBe(INTEGRATION_FAILURE_MESSAGES.server)
  })

  it('explains limit_reached at the top, since no field caused it', () => {
    expect(
      describeIntegrationFailure(
        problem(422, 'integrations.connection.limit_reached'),
        FORM_PATHS,
      ),
    ).toEqual({
      ...quiet,
      fields: {},
      message:
        INTEGRATION_ERROR_MESSAGES['integrations.connection.limit_reached'],
    })
  })

  it('explains a network failure', () => {
    expect(
      describeIntegrationFailure(new NetworkError(new TypeError('offline')))
        .message,
    ).toBe(INTEGRATION_FAILURE_MESSAGES.network)
  })

  it('never words a message with voseo', () => {
    const voseo = [
      'tenés',
      'podés',
      'querés',
      'revisá',
      'intentá',
      'elegí',
      'probá',
      'volvé',
      'recargá',
      'escribí',
      'pegá',
      'avisá',
      'eliminá',
    ]
    const messages = [
      ...Object.values(INTEGRATION_ERROR_MESSAGES),
      ...Object.values(INTEGRATION_FAILURE_MESSAGES),
    ].map((message) => message.toLowerCase())

    expect(
      messages.filter((message) => voseo.some((word) => message.includes(word))),
    ).toEqual([])
  })
})
```

- [ ] **Step 2: Correrla y verla fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/utils/describe-integration-failure.test.ts
```

Expected: FAIL — `Failed to resolve import "@/features/integrations/utils/describe-integration-failure"`.

- [ ] **Step 3: Agregar los textos al servicio**

Al **final** de `src/features/integrations/services/integrations.api.ts`:

```ts
/**
 * Our wording for every code these endpoints answer (spec «Códigos de error»). The backend
 * writes `detail` in English, so it is never shown: an unmapped code falls back to a sentence
 * of ours.
 */
export const INTEGRATION_ERROR_MESSAGES = {
  'integrations.connection.name_taken':
    'Ya tienes una conexión con ese nombre para este proveedor.',
  'integrations.connection.name_invalid':
    'Escribe un nombre de hasta 80 caracteres, en una sola línea.',
  'integrations.connection.limit_reached':
    'Llegaste al máximo de conexiones para este proveedor. Elimina una que ya no uses para crear otra.',
  'integrations.connection.credentials_rejected':
    'El proveedor rechazó la credencial. Revisa que la hayas copiado completa.',
  'integrations.connection.provider_unreachable':
    'No pudimos verificar la credencial con el proveedor. Inténtalo de nuevo en un momento.',
  'integrations.connection.not_paused':
    'Esta conexión ya no está pausada. Revisa cómo quedó.',
  'integrations.connection.not_active':
    'Esta conexión ya no está activa. Revisa cómo quedó.',
  'integrations.connection.not_found': 'Esa conexión ya no existe.',
  'integrations.secret_protection.unavailable':
    'Por ahora no podemos guardar credenciales: falta configurar el cifrado en el servidor. Avísale a soporte.',
  'tenancy.module_not_enabled':
    'Tu plan ya no incluye el módulo que usa este proveedor.',
  'authorization.denied': 'No tienes permiso para gestionar integraciones.',
} as const

/** The message of a known code, or `undefined` for one this build does not know. */
export function integrationErrorMessage(code: string): string | undefined {
  return (INTEGRATION_ERROR_MESSAGES as Partial<Record<string, string>>)[code]
}

export const INTEGRATION_FAILURE_MESSAGES = {
  conflict:
    'Alguien más cambió esta conexión mientras la editabas. Revisa cómo quedó y vuelve a intentarlo.',
  precondition: 'No pudimos verificar la versión. Recarga e intenta de nuevo.',
  validation: 'Revisa los datos e intenta de nuevo.',
  // Fallback only: the backend's own Spanish message wins when it sends one (F8).
  invalidField: 'Este dato no tiene el formato esperado.',
  server: 'El servidor tuvo un problema. Intenta de nuevo en un momento.',
  network:
    'No pudimos conectarnos con el servidor. Revisa tu conexión e intenta de nuevo.',
  generic: 'No pudimos completar la acción. Intenta de nuevo.',
} as const
```

- [ ] **Step 4: Escribir el util**

`src/features/integrations/utils/describe-integration-failure.ts`:

```ts
import {
  INTEGRATION_FAILURE_MESSAGES,
  integrationErrorMessage,
} from '@/features/integrations/services/integrations.api'
import { ApiError, NetworkError } from '@/lib/api-client'

export interface IntegrationFailure {
  /** Form path (`name`, `fields.<key>`, `secrets.<key>`) → message. Only paths the caller can mark. */
  fields: Record<string, string>
  /** For the top of the form or the page. `null` when everything landed on a field. */
  message: string | null
  /** `provider_unreachable`: nothing typed is wrong; the same request may pass in a moment. */
  retryable: boolean
  /** The screen shows something that stopped being true: the version, the status or the row itself. */
  shouldReload: boolean
}

const CODES = {
  validationFailed: 'validation.failed',
  credentialsRejected: 'integrations.connection.credentials_rejected',
  providerUnreachable: 'integrations.connection.provider_unreachable',
  nameTaken: 'integrations.connection.name_taken',
  nameInvalid: 'integrations.connection.name_invalid',
  notPaused: 'integrations.connection.not_paused',
  notActive: 'integrations.connection.not_active',
  notFound: 'integrations.connection.not_found',
  moduleNotEnabled: 'tenancy.module_not_enabled',
  authorizationDenied: 'authorization.denied',
} as const

function failure(
  message: string | null,
  overrides: Partial<IntegrationFailure> = {},
): IntegrationFailure {
  return {
    fields: {},
    message,
    retryable: false,
    shouldReload: false,
    ...overrides,
  }
}

function messageFor(code: string): string {
  return integrationErrorMessage(code) ?? INTEGRATION_FAILURE_MESSAGES.generic
}

/**
 * Turns a failure of any integrations endpoint into something a person can act on. Never the
 * backend `detail`: it is English. `formPaths` are the inputs the caller can mark; a message
 * for a path it cannot mark goes to `message` instead of disappearing.
 */
export function describeIntegrationFailure(
  error: unknown,
  formPaths: readonly string[] = [],
): IntegrationFailure {
  if (error instanceof NetworkError) {
    return failure(INTEGRATION_FAILURE_MESSAGES.network)
  }
  if (!(error instanceof ApiError)) {
    return failure(INTEGRATION_FAILURE_MESSAGES.generic)
  }

  switch (error.status) {
    case 403:
      return error.code === CODES.moduleNotEnabled
        ? failure(messageFor(CODES.moduleNotEnabled), { shouldReload: true })
        : failure(messageFor(CODES.authorizationDenied))
    case 404:
      return failure(messageFor(CODES.notFound), { shouldReload: true })
    case 412:
      // Never offered again: resending with the fresh version would overwrite the other
      // administrator's change, which is what the precondition exists to prevent.
      return failure(INTEGRATION_FAILURE_MESSAGES.conflict, {
        shouldReload: true,
      })
    case 428:
      return failure(INTEGRATION_FAILURE_MESSAGES.precondition, {
        shouldReload: true,
      })
    case 422:
      return describeUnprocessable(error, formPaths)
    default: {
      const known = error.code ? integrationErrorMessage(error.code) : undefined
      if (known) return failure(known)
      return failure(
        error.status >= 500
          ? INTEGRATION_FAILURE_MESSAGES.server
          : INTEGRATION_FAILURE_MESSAGES.generic,
      )
    }
  }
}

function describeUnprocessable(
  error: ApiError,
  formPaths: readonly string[],
): IntegrationFailure {
  switch (error.code) {
    case CODES.validationFailed:
      return fromErrorMap(error.errors ?? {}, formPaths)
    case CODES.credentialsRejected: {
      // The spec sends `errors: { "secrets.<key>": [...] }`; a domain 422 may come without the
      // map (DECISIÓN-PENDIENTE F2), and then every secret of the form is the suspect.
      const reported = Object.keys(error.errors ?? {}).filter((path) =>
        path.startsWith('secrets.'),
      )
      const suspects =
        reported.length > 0
          ? reported
          : formPaths.filter((path) => path.startsWith('secrets.'))
      return onPaths(suspects, messageFor(CODES.credentialsRejected), formPaths)
    }
    case CODES.providerUnreachable:
      return failure(messageFor(CODES.providerUnreachable), {
        retryable: true,
      })
    case CODES.nameTaken:
    case CODES.nameInvalid:
      return onPaths(['name'], messageFor(error.code), formPaths)
    case CODES.notPaused:
    case CODES.notActive:
      return failure(messageFor(error.code), { shouldReload: true })
    default:
      return failure(
        error.code ? messageFor(error.code) : INTEGRATION_FAILURE_MESSAGES.generic,
      )
  }
}

/** The same message on every path the form can mark; with none, it goes to the top. */
function onPaths(
  paths: readonly string[],
  message: string,
  formPaths: readonly string[],
): IntegrationFailure {
  const markable = paths.filter((path) => formPaths.includes(path))
  if (markable.length === 0) return failure(message)
  return failure(null, {
    fields: Object.fromEntries(markable.map((path) => [path, message])),
  })
}

/** One mark per path the form knows; anything else becomes one line at the top. */
function fromErrorMap(
  errors: Record<string, string[]>,
  formPaths: readonly string[],
): IntegrationFailure {
  const fields: Record<string, string> = {}
  let unmarked = false
  for (const path of Object.keys(errors)) {
    if (formPaths.includes(path)) {
      fields[path] =
        errors[path]?.[0] || INTEGRATION_FAILURE_MESSAGES.invalidField
    } else {
      unmarked = true
    }
  }
  const marked = Object.keys(fields).length > 0
  return failure(
    unmarked || !marked ? INTEGRATION_FAILURE_MESSAGES.validation : null,
    { fields },
  )
}
```

- [ ] **Step 5: Correrla y verla pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/utils/describe-integration-failure.test.ts src/features/integrations/services/integrations.api.test.ts
```

Expected: PASS — `Tests  29 passed (29)` (20 + 9).

- [ ] **Step 6: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
$files = @(
  'src/features/integrations/services/integrations.api.ts',
  'src/features/integrations/utils/describe-integration-failure.ts',
  'src/features/integrations/utils/describe-integration-failure.test.ts'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(integrations): traducción de cada código de error a algo accionable

credentials_rejected marca el secreto (con o sin mapa errors),
provider_unreachable queda como aviso reintentable, 412/428/404 y los
cambios de estado piden releer, y nunca se muestra el detail del backend.
'@
$msgFile = Join-Path $env:TEMP 'qep-integraciones-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

---

### Task 3: Schema del formulario desde el catálogo

**Files:**
- Create: `src/features/integrations/types/integrations.schema.ts`
- Test: `src/features/integrations/types/integrations.schema.test.ts`

**Interfaces:**
- Consumes: `formPathOf`, `ConnectionPayload`, `IntegrationConnection`, `IntegrationProvider` (Task 1).
- Produces: `CONNECTION_NAME_MAX_LENGTH = 80`, `CONNECTION_FORM_MESSAGES { nameRequired; nameTooLong; fieldRequired }`, `fieldTooLongMessage(maxLength: number): string`, `ConnectionFormInput` / `ConnectionFormValues` (`{ name: string; fields: Record<string, string>; secrets: Record<string, string> }`), `hasUsableStoredSecret(connection: IntegrationConnection | null, key: string): boolean`, `connectionFormSchema(provider, connection)`, `connectionFormDefaults(provider, connection): ConnectionFormInput`, `toConnectionPayload(values: ConnectionFormValues, provider): ConnectionPayload`.

- [ ] **Step 1: Escribir la prueba**

`src/features/integrations/types/integrations.schema.test.ts`:

```ts
import { describe, expect, it } from 'vitest'

import type { IntegrationProvider } from '@/features/integrations/types/integrations'
import {
  CONNECTION_FORM_MESSAGES,
  connectionFormDefaults,
  connectionFormSchema,
  fieldTooLongMessage,
  toConnectionPayload,
} from '@/features/integrations/types/integrations.schema'
import {
  OCTOBER_8,
  ZENVIA_PROVIDER,
  integrationConnection,
} from '@/test/integrations'

type ParseResult = ReturnType<
  ReturnType<typeof connectionFormSchema>['safeParse']
>

function issuesOf(result: ParseResult): Record<string, string> {
  if (result.success) return {}
  return Object.fromEntries(
    result.error.issues.map((issue) => [issue.path.join('.'), issue.message]),
  )
}

describe('connectionFormSchema', () => {
  it('asks for the name and every required field when connecting', () => {
    const result = connectionFormSchema(ZENVIA_PROVIDER, null).safeParse(
      connectionFormDefaults(ZENVIA_PROVIDER, null),
    )

    expect(issuesOf(result)).toEqual({
      name: CONNECTION_FORM_MESSAGES.nameRequired,
      'secrets.apiToken': CONNECTION_FORM_MESSAGES.fieldRequired,
      'fields.fromNumber': CONNECTION_FORM_MESSAGES.fieldRequired,
    })
  })

  // Spec D5: a blank secret keeps the stored one.
  it('keeps a readable stored secret when it is left blank', () => {
    const connection = integrationConnection()

    const result = connectionFormSchema(ZENVIA_PROVIDER, connection).safeParse(
      connectionFormDefaults(ZENVIA_PROVIDER, connection),
    )

    expect(result.success).toBe(true)
  })

  it('asks to paste again a stored secret the backend can no longer read', () => {
    const connection = integrationConnection({
      secrets: {
        apiToken: { configured: true, updatedAt: OCTOBER_8, readable: false },
      },
    })

    const result = connectionFormSchema(ZENVIA_PROVIDER, connection).safeParse(
      connectionFormDefaults(ZENVIA_PROVIDER, connection),
    )

    expect(issuesOf(result)).toEqual({
      'secrets.apiToken': CONNECTION_FORM_MESSAGES.fieldRequired,
    })
  })

  it('rejects a name longer than 80 characters once trimmed', () => {
    const connection = integrationConnection()
    const schema = connectionFormSchema(ZENVIA_PROVIDER, connection)
    const seeded = connectionFormDefaults(ZENVIA_PROVIDER, connection)

    expect(
      issuesOf(schema.safeParse({ ...seeded, name: `  ${'a'.repeat(81)}  ` })),
    ).toEqual({ name: CONNECTION_FORM_MESSAGES.nameTooLong })
    expect(
      schema.safeParse({ ...seeded, name: `  ${'a'.repeat(80)}  ` }).success,
    ).toBe(true)
  })

  it('rejects a value longer than the maxLength of its catalog field', () => {
    const provider: IntegrationProvider = {
      ...ZENVIA_PROVIDER,
      fields: [
        {
          key: 'fromNumber',
          label: 'Número emisor',
          kind: 'Phone',
          required: true,
          maxLength: 5,
        },
      ],
    }

    const result = connectionFormSchema(provider, null).safeParse({
      name: 'Sede',
      fields: { fromNumber: '573001' },
      secrets: {},
    })

    expect(issuesOf(result)).toEqual({
      'fields.fromNumber': fieldTooLongMessage(5),
    })
  })
})

describe('connectionFormDefaults', () => {
  it('seeds the form from the stored connection, with every secret blank', () => {
    expect(
      connectionFormDefaults(ZENVIA_PROVIDER, integrationConnection()),
    ).toEqual({
      name: 'WhatsApp sede norte',
      fields: { fromNumber: '573001234567' },
      secrets: { apiToken: '' },
    })
  })
})

describe('toConnectionPayload', () => {
  it('trims everything and sends only the secrets that were typed', () => {
    expect(
      toConnectionPayload(
        {
          name: '  Sede norte ',
          fields: { fromNumber: ' 573001234567 ' },
          secrets: { apiToken: '   ' },
        },
        ZENVIA_PROVIDER,
      ),
    ).toEqual({
      name: 'Sede norte',
      fields: { fromNumber: '573001234567' },
      secrets: {},
    })
  })

  it('sends only the fields of the catalog', () => {
    expect(
      toConnectionPayload(
        {
          name: 'Sede',
          fields: { fromNumber: '573001234567', rogue: 'x' },
          secrets: { apiToken: 'zenvia-token', other: 'y' },
        },
        ZENVIA_PROVIDER,
      ),
    ).toEqual({
      name: 'Sede',
      fields: { fromNumber: '573001234567' },
      secrets: { apiToken: 'zenvia-token' },
    })
  })
})
```

- [ ] **Step 2: Correrla y verla fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/types/integrations.schema.test.ts
```

Expected: FAIL — `Failed to resolve import "@/features/integrations/types/integrations.schema"`.

- [ ] **Step 3: Escribir el schema**

`src/features/integrations/types/integrations.schema.ts`:

```ts
import { z } from 'zod'

import {
  formPathOf,
  type ConnectionPayload,
  type IntegrationConnection,
  type IntegrationProvider,
} from '@/features/integrations/types/integrations'

/** Same limit as the aggregate (spec «Reglas del agregado»: trim, 1–80). */
export const CONNECTION_NAME_MAX_LENGTH = 80

export const CONNECTION_FORM_MESSAGES = {
  nameRequired: 'Escribe un nombre para la conexión.',
  nameTooLong: `El nombre puede tener hasta ${CONNECTION_NAME_MAX_LENGTH} caracteres.`,
  fieldRequired: 'Completa este campo.',
} as const

export function fieldTooLongMessage(maxLength: number): string {
  return `Puede tener hasta ${maxLength} caracteres.`
}

const formShape = z.object({
  name: z.string(),
  fields: z.record(z.string(), z.string()),
  secrets: z.record(z.string(), z.string()),
})

export type ConnectionFormInput = z.input<typeof formShape>
export type ConnectionFormValues = z.output<typeof formShape>

/** A stored secret the backend can still decrypt: leaving its input blank keeps it (spec D5). */
export function hasUsableStoredSecret(
  connection: IntegrationConnection | null,
  key: string,
): boolean {
  const stored = connection?.secrets[key]
  return stored?.configured === true && stored.readable
}

/**
 * The rules the catalog can express: the name, `required` and `maxLength`. Patterns are not in
 * `GET /catalog` (DECISIÓN-PENDIENTE F7); a value that breaks one comes back as a
 * `validation.failed` on its own path. A secret is required when connecting, and when editing
 * only if there is no stored value the backend can read.
 */
export function connectionFormSchema(
  provider: IntegrationProvider,
  connection: IntegrationConnection | null,
) {
  return formShape.superRefine((values, context) => {
    const issue = (path: string, message: string) =>
      context.addIssue({ code: 'custom', path: path.split('.'), message })

    const name = values.name.trim()
    if (!name) issue('name', CONNECTION_FORM_MESSAGES.nameRequired)
    else if (name.length > CONNECTION_NAME_MAX_LENGTH) {
      issue('name', CONNECTION_FORM_MESSAGES.nameTooLong)
    }

    for (const field of provider.fields) {
      const isSecret = field.kind === 'Secret'
      const value = (
        (isSecret ? values.secrets : values.fields)[field.key] ?? ''
      ).trim()
      const path = formPathOf(field)

      if (!value) {
        const keepsStored =
          isSecret && hasUsableStoredSecret(connection, field.key)
        if (field.required && !keepsStored) {
          issue(path, CONNECTION_FORM_MESSAGES.fieldRequired)
        }
        continue
      }
      if (value.length > field.maxLength) {
        issue(path, fieldTooLongMessage(field.maxLength))
      }
    }
  })
}

/** Stored values for the plain fields; every secret starts blank, because none ever travels back. */
export function connectionFormDefaults(
  provider: IntegrationProvider,
  connection: IntegrationConnection | null,
): ConnectionFormInput {
  const fields: Record<string, string> = {}
  const secrets: Record<string, string> = {}
  for (const field of provider.fields) {
    if (field.kind === 'Secret') secrets[field.key] = ''
    else fields[field.key] = connection?.fields[field.key] ?? ''
  }
  return { name: connection?.name ?? '', fields, secrets }
}

/**
 * What travels: trimmed, only the catalog's keys (the backend rejects any other with
 * `field_unknown`), and only the secrets that were typed.
 */
export function toConnectionPayload(
  values: ConnectionFormValues,
  provider: IntegrationProvider,
): ConnectionPayload {
  const fields: Record<string, string> = {}
  const secrets: Record<string, string> = {}
  for (const field of provider.fields) {
    const isSecret = field.kind === 'Secret'
    const value = (
      (isSecret ? values.secrets : values.fields)[field.key] ?? ''
    ).trim()
    if (!value) continue
    if (isSecret) secrets[field.key] = value
    else fields[field.key] = value
  }
  return { name: values.name.trim(), fields, secrets }
}
```

- [ ] **Step 4: Correrla y verla pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/types/integrations.schema.test.ts
```

Expected: PASS — `Tests  8 passed (8)`.

- [ ] **Step 5: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
$files = @(
  'src/features/integrations/types/integrations.schema.ts',
  'src/features/integrations/types/integrations.schema.test.ts'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(integrations): schema del formulario de conexión desde el catálogo

Nombre de 1 a 80, requeridos y maxLength por campo del catálogo, y el
secreto opcional al editar sólo si el guardado se puede leer. El payload
recorta, manda sólo las claves del catálogo y omite los secretos en blanco.
'@
$msgFile = Join-Path $env:TEMP 'qep-integraciones-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

---

### Task 4: Hooks de lectura y escritura

**Files:**
- Create: `src/features/integrations/utils/integrations-query-status.ts`
- Test: `src/features/integrations/utils/integrations-query-status.test.ts`
- Create: `src/features/integrations/hooks/use-integrations-catalog.ts`
- Test: `src/features/integrations/hooks/use-integrations-catalog.test.tsx`
- Create: `src/features/integrations/hooks/use-connections.ts`
- Test: `src/features/integrations/hooks/use-connections.test.tsx`
- Create: `src/features/integrations/hooks/use-connection-mutations.ts`
- Test: `src/features/integrations/hooks/use-connection-mutations.test.tsx`

**Interfaces:**
- Consumes: `useActiveTenant` (`@/features/auth/hooks/use-active-tenant`, devuelve `{ tenantId: string | null }`), `PermissionsStatus` (`@/features/auth/hooks/use-permission`), todo el servicio de la Task 1.
- Produces (util): `IntegrationsQueryStatus = 'loading' | 'ready' | 'denied' | 'error'`, `IntegrationsAccess = 'loading' | 'allowed' | 'denied'`, `resolveQueryStatus(query: { isPending; isError; error }, active: boolean): IntegrationsQueryStatus`, `retryUnlessForbidden(failureCount: number, error: unknown): boolean`, `integrationsAccess(permissionsStatus: PermissionsStatus, canRead: boolean): IntegrationsAccess`, `integrationsPageStatus(access: IntegrationsAccess, ...reads: IntegrationsQueryStatus[]): IntegrationsQueryStatus`.
- Produces (hooks): `useIntegrationsCatalog({ enabled }): { providers: IntegrationProvider[]; status: IntegrationsQueryStatus; retry: () => void }`, `useConnections({ enabled }): { connections: IntegrationConnection[]; status: IntegrationsQueryStatus; retry: () => void }`, `useConnectionMutations(): UseConnectionMutationsResult` con `create(input: CreateConnectionInput)`, `update(connectionId: string, input: UpdateConnectionInput)`, `test(connection)`, `pause(connection)`, `resume(connection)` → `Promise<IntegrationConnection>`; `remove(connection) → Promise<void>`; `isSaving: boolean`; `pendingConnectionId: string | null`.

- [ ] **Step 1: Escribir las pruebas**

`src/features/integrations/utils/integrations-query-status.test.ts`:

```ts
import { describe, expect, it } from 'vitest'

import {
  integrationsAccess,
  integrationsPageStatus,
  resolveQueryStatus,
  retryUnlessForbidden,
} from '@/features/integrations/utils/integrations-query-status'
import { ApiError } from '@/lib/api-client'

const forbidden = new ApiError(403, { code: 'authorization.denied' })
const broken = new ApiError(500, { code: 'server.unexpected' })

describe('resolveQueryStatus', () => {
  it.each([
    ['loading while the read is off', false, { isPending: true, isError: false, error: null }, 'loading'],
    ['loading while pending', true, { isPending: true, isError: false, error: null }, 'loading'],
    ['denied on a 403', true, { isPending: false, isError: true, error: forbidden }, 'denied'],
    ['error on any other failure', true, { isPending: false, isError: true, error: broken }, 'error'],
    ['ready with an answer', true, { isPending: false, isError: false, error: null }, 'ready'],
  ] as const)('is %s', (_, active, query, expected) => {
    expect(resolveQueryStatus(query, active)).toBe(expected)
  })
})

describe('integrationsAccess', () => {
  it.each([
    ['loading', false, 'loading'],
    ['denied', false, 'denied'],
    // Unknown is not denied: the backend answers, like ModuleGate fails open.
    ['error', false, 'allowed'],
    ['ready', true, 'allowed'],
    ['ready', false, 'denied'],
  ] as const)('maps permissions %s with read=%s to %s', (status, canRead, expected) => {
    expect(integrationsAccess(status, canRead)).toBe(expected)
  })
})

describe('integrationsPageStatus', () => {
  it('puts the access first, then a denial, an error and loading, in that order', () => {
    expect(integrationsPageStatus('loading', 'ready', 'ready')).toBe('loading')
    expect(integrationsPageStatus('denied', 'ready', 'ready')).toBe('denied')
    expect(integrationsPageStatus('allowed', 'error', 'denied')).toBe('denied')
    expect(integrationsPageStatus('allowed', 'loading', 'error')).toBe('error')
    expect(integrationsPageStatus('allowed', 'ready', 'loading')).toBe('loading')
    expect(integrationsPageStatus('allowed', 'ready', 'ready')).toBe('ready')
  })
})

describe('retryUnlessForbidden', () => {
  it('retries once, but never a 403', () => {
    expect(retryUnlessForbidden(0, broken)).toBe(true)
    expect(retryUnlessForbidden(1, broken)).toBe(false)
    expect(retryUnlessForbidden(0, forbidden)).toBe(false)
  })
})
```

`src/features/integrations/hooks/use-integrations-catalog.test.tsx`:

```tsx
import type { ReactNode } from 'react'

import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { useIntegrationsCatalog } from './use-integrations-catalog'
import {
  ZENVIA_PROVIDER,
  jsonResponse,
  problemResponse,
} from '@/test/integrations'

const TENANT = '019fb345-e753-71e2-bdb2-542df3cd8ab8'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({
  useActiveTenant: () => ({ tenantId: TENANT }),
}))

function setup(enabled = true) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retryDelay: 0 } },
  })
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  )
  const hook = renderHook(
    ({ on }) => useIntegrationsCatalog({ enabled: on }),
    { wrapper, initialProps: { on: enabled } },
  )
  return { queryClient, hook }
}

describe('useIntegrationsCatalog', () => {
  it('reads the providers of the active tenant', async () => {
    vi.mocked(fetch).mockResolvedValue(
      jsonResponse(200, { providers: [ZENVIA_PROVIDER] }),
    )

    const { hook } = setup()

    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    expect(hook.result.current.providers).toEqual([ZENVIA_PROVIDER])
    expect(String(vi.mocked(fetch).mock.calls[0]?.[0])).toBe(
      `/api/v1/tenants/${TENANT}/integrations/catalog`,
    )
  })

  it('asks nothing while it is off', async () => {
    const { hook, queryClient } = setup(false)

    await waitFor(() => expect(queryClient.isFetching()).toBe(0))
    expect(vi.mocked(fetch)).not.toHaveBeenCalled()
    expect(hook.result.current.status).toBe('loading')
    expect(hook.result.current.providers).toEqual([])
  })

  it('reports denied on a 403 without retrying', async () => {
    vi.mocked(fetch).mockImplementation(() =>
      Promise.resolve(problemResponse(403, 'authorization.denied')),
    )

    const { hook } = setup()

    await waitFor(() => expect(hook.result.current.status).toBe('denied'))
    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(1)
  })

  it('reports error after retrying once', async () => {
    vi.mocked(fetch).mockImplementation(() =>
      Promise.resolve(problemResponse(500, 'server.unexpected')),
    )

    const { hook } = setup()

    await waitFor(() => expect(hook.result.current.status).toBe('error'))
    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(2)
  })
})
```

`src/features/integrations/hooks/use-connections.test.tsx`:

```tsx
import type { ReactNode } from 'react'

import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { useConnections } from './use-connections'
import {
  integrationConnection,
  jsonResponse,
  problemResponse,
} from '@/test/integrations'

const TENANT = '019fb345-e753-71e2-bdb2-542df3cd8ab8'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({
  useActiveTenant: () => ({ tenantId: TENANT }),
}))

function setup(enabled = true) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retryDelay: 0 } },
  })
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  )
  const hook = renderHook(({ on }) => useConnections({ enabled: on }), {
    wrapper,
    initialProps: { on: enabled },
  })
  return { queryClient, hook }
}

describe('useConnections', () => {
  it('reads the connections of the active tenant', async () => {
    const connection = integrationConnection()
    vi.mocked(fetch).mockResolvedValue(
      jsonResponse(200, { items: [connection] }),
    )

    const { hook } = setup()

    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    expect(hook.result.current.connections).toEqual([connection])
    expect(String(vi.mocked(fetch).mock.calls[0]?.[0])).toBe(
      `/api/v1/tenants/${TENANT}/integrations/connections`,
    )
  })

  it('asks nothing while it is off', async () => {
    const { hook, queryClient } = setup(false)

    await waitFor(() => expect(queryClient.isFetching()).toBe(0))
    expect(vi.mocked(fetch)).not.toHaveBeenCalled()
    expect(hook.result.current.connections).toEqual([])
  })

  it('reports denied on a 403 without retrying', async () => {
    vi.mocked(fetch).mockImplementation(() =>
      Promise.resolve(problemResponse(403, 'authorization.denied')),
    )

    const { hook } = setup()

    await waitFor(() => expect(hook.result.current.status).toBe('denied'))
    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(1)
  })
})
```

`src/features/integrations/hooks/use-connection-mutations.test.tsx`:

```tsx
import type { ReactNode } from 'react'

import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { useConnectionMutations } from './use-connection-mutations'
import {
  integrationConnection,
  jsonResponse,
  problemResponse,
} from '@/test/integrations'

const TENANT = '019fb345-e753-71e2-bdb2-542df3cd8ab8'
const BASE = `/api/v1/tenants/${TENANT}/integrations`

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({
  useActiveTenant: () => ({ tenantId: TENANT }),
}))

function setup() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  )
  const hook = renderHook(() => useConnectionMutations(), { wrapper })
  return { queryClient, invalidate, hook }
}

function lastRequest() {
  const [url, init] = vi.mocked(fetch).mock.calls.at(-1)!
  return {
    url: String(url),
    method: init?.method,
    ifMatch: new Headers(init?.headers).get('If-Match'),
  }
}

describe('useConnectionMutations', () => {
  it('creates and re-reads the catalog and the connections', async () => {
    const created = integrationConnection()
    vi.mocked(fetch).mockResolvedValue(jsonResponse(201, created))
    const { hook, invalidate } = setup()

    let saved: unknown
    await act(async () => {
      saved = await hook.result.current.create({
        providerKey: 'zenvia',
        name: created.name,
        fields: created.fields,
        secrets: { apiToken: 'zenvia-token' },
      })
    })

    expect(saved).toEqual(created)
    expect(lastRequest()).toMatchObject({
      url: `${BASE}/connections`,
      method: 'POST',
    })
    expect(invalidate).toHaveBeenCalledWith({
      queryKey: ['integrations', TENANT],
    })
  })

  it.each([
    ['pause', 'POST', '/connections/c-1/pause'],
    ['resume', 'POST', '/connections/c-1/resume'],
    ['remove', 'DELETE', '/connections/c-1'],
  ] as const)(
    '%s sends the version of the row in If-Match',
    async (action, method, path) => {
      const connection = integrationConnection({ id: 'c-1', version: 7 })
      vi.mocked(fetch).mockResolvedValue(
        action === 'remove'
          ? new Response(null, { status: 204 })
          : jsonResponse(200, connection),
      )
      const { hook } = setup()

      await act(async () => {
        await hook.result.current[action](connection)
      })

      expect(lastRequest()).toEqual({
        url: `${BASE}${path}`,
        method,
        ifMatch: '"7"',
      })
    },
  )

  it('tests with what is stored', async () => {
    const connection = integrationConnection({ id: 'c-1' })
    vi.mocked(fetch).mockResolvedValue(jsonResponse(200, connection))
    const { hook } = setup()

    await act(async () => {
      await hook.result.current.test(connection)
    })

    expect(lastRequest()).toEqual({
      url: `${BASE}/connections/c-1/test`,
      method: 'POST',
      ifMatch: null,
    })
  })

  it('updates with If-Match and resolves with the saved row', async () => {
    const saved = integrationConnection({
      id: 'c-1',
      name: 'Sede norte',
      version: 4,
    })
    vi.mocked(fetch).mockResolvedValue(jsonResponse(200, saved))
    const { hook } = setup()

    let result: unknown
    await act(async () => {
      result = await hook.result.current.update('c-1', {
        name: 'Sede norte',
        fields: { fromNumber: '573001234567' },
        secrets: {},
        version: 3,
      })
    })

    expect(result).toEqual(saved)
    expect(lastRequest()).toEqual({
      url: `${BASE}/connections/c-1`,
      method: 'PUT',
      ifMatch: '"3"',
    })
  })

  // Review Focus 3: a failure also re-reads, so a 412 never leaves the screen on a dead version.
  it('rejects with the request error and still re-reads', async () => {
    vi.mocked(fetch).mockResolvedValue(
      problemResponse(422, 'integrations.connection.credentials_rejected'),
    )
    const { hook, invalidate } = setup()

    await act(async () => {
      await expect(
        hook.result.current.create({
          providerKey: 'zenvia',
          name: 'Sede',
          fields: { fromNumber: '573001234567' },
          secrets: { apiToken: 'bad-token' },
        }),
      ).rejects.toMatchObject({
        status: 422,
        code: 'integrations.connection.credentials_rejected',
      })
    })

    expect(invalidate).toHaveBeenCalledWith({
      queryKey: ['integrations', TENANT],
    })
    await waitFor(() => expect(hook.result.current.isSaving).toBe(false))
  })

  // Review Focus 4 and spec criterion 3, on this side: the typed credential lives only in the request.
  it('keeps the typed secret in no cache once the request settles', async () => {
    const sentinel = 'sentinel-secret-7f3a91'
    vi.mocked(fetch).mockResolvedValue(
      jsonResponse(201, integrationConnection()),
    )
    const { hook, queryClient } = setup()

    await act(async () => {
      await hook.result.current.create({
        providerKey: 'zenvia',
        name: 'Sede',
        fields: { fromNumber: '573001234567' },
        secrets: { apiToken: sentinel },
      })
    })

    const queries = JSON.stringify(
      queryClient
        .getQueryCache()
        .getAll()
        .map((query) => query.state),
    )
    expect(queries).not.toContain(sentinel)
    await waitFor(() =>
      expect(queryClient.getMutationCache().getAll()).toHaveLength(0),
    )
  })

  it('tells which connection is being written while the request is in flight', async () => {
    const connection = integrationConnection({ id: 'c-1' })
    let answer!: (response: Response) => void
    vi.mocked(fetch).mockReturnValue(
      new Promise<Response>((resolve) => {
        answer = resolve
      }),
    )
    const { hook } = setup()

    let pending!: Promise<unknown>
    act(() => {
      pending = hook.result.current.pause(connection)
    })

    await waitFor(() =>
      expect(hook.result.current.pendingConnectionId).toBe('c-1'),
    )
    expect(hook.result.current.isSaving).toBe(true)

    await act(async () => {
      answer(jsonResponse(200, { ...connection, status: 'Paused' }))
      await pending
    })

    expect(hook.result.current.pendingConnectionId).toBeNull()
    expect(hook.result.current.isSaving).toBe(false)
  })
})
```

- [ ] **Step 2: Correrlas y verlas fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/utils/integrations-query-status.test.ts src/features/integrations/hooks/use-integrations-catalog.test.tsx src/features/integrations/hooks/use-connections.test.tsx src/features/integrations/hooks/use-connection-mutations.test.tsx
```

Expected: FAIL — los cuatro archivos no cargan (`Failed to resolve import` de `integrations-query-status`, `./use-integrations-catalog`, `./use-connections` y `./use-connection-mutations`).

- [ ] **Step 3: Escribir el util de estados**

`src/features/integrations/utils/integrations-query-status.ts`:

```ts
import type { PermissionsStatus } from '@/features/auth/hooks/use-permission'
import { ApiError } from '@/lib/api-client'

export type IntegrationsQueryStatus = 'loading' | 'ready' | 'denied' | 'error'

export type IntegrationsAccess = 'loading' | 'allowed' | 'denied'

/** `active` is false while the read is switched off or there is no tenant yet: that is still loading. */
export function resolveQueryStatus(
  query: { isPending: boolean; isError: boolean; error: unknown },
  active: boolean,
): IntegrationsQueryStatus {
  if (!active || (query.isPending && !query.isError)) return 'loading'
  if (query.error instanceof ApiError && query.error.status === 403) {
    return 'denied'
  }
  if (query.isError) return 'error'
  return 'ready'
}

/** A 403 is a closed answer about this tenant; retrying it only delays the screen that explains it. */
export function retryUnlessForbidden(
  failureCount: number,
  error: unknown,
): boolean {
  return (
    !(error instanceof ApiError && error.status === 403) && failureCount < 1
  )
}

/**
 * Whether the screen may ask for its data. `can()` denies while loading, so loading is its own
 * answer; an unknown answer (`error`) lets the backend decide instead of hiding the screen.
 */
export function integrationsAccess(
  permissionsStatus: PermissionsStatus,
  canRead: boolean,
): IntegrationsAccess {
  if (permissionsStatus === 'loading') return 'loading'
  if (permissionsStatus === 'denied') return 'denied'
  if (permissionsStatus === 'error') return 'allowed'
  return canRead ? 'allowed' : 'denied'
}

/** One status for the page out of the access and its reads: denied, then error, then loading. */
export function integrationsPageStatus(
  access: IntegrationsAccess,
  ...reads: IntegrationsQueryStatus[]
): IntegrationsQueryStatus {
  if (access === 'loading') return 'loading'
  if (access === 'denied') return 'denied'
  if (reads.includes('denied')) return 'denied'
  if (reads.includes('error')) return 'error'
  if (reads.includes('loading')) return 'loading'
  return 'ready'
}
```

- [ ] **Step 4: Escribir los hooks de lectura**

`src/features/integrations/hooks/use-integrations-catalog.ts`:

```ts
import { useQuery } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'
import {
  fetchIntegrationsCatalog,
  integrationsCatalogQueryKey,
} from '@/features/integrations/services/integrations.api'
import {
  resolveQueryStatus,
  retryUnlessForbidden,
} from '@/features/integrations/utils/integrations-query-status'

/** The providers visible to the active tenant. `enabled` is the read permission: without it nothing is asked. */
export function useIntegrationsCatalog({ enabled }: { enabled: boolean }) {
  const { tenantId } = useActiveTenant()
  const active = enabled && tenantId !== null

  const query = useQuery({
    queryKey: integrationsCatalogQueryKey(tenantId ?? 'none'),
    queryFn: () => fetchIntegrationsCatalog(tenantId!),
    enabled: active,
    retry: retryUnlessForbidden,
  })

  return {
    providers: query.data?.providers ?? [],
    status: resolveQueryStatus(query, active),
    retry: () => void query.refetch(),
  }
}
```

`src/features/integrations/hooks/use-connections.ts`:

```ts
import { useQuery } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'
import {
  connectionsQueryKey,
  fetchConnections,
} from '@/features/integrations/services/integrations.api'
import {
  resolveQueryStatus,
  retryUnlessForbidden,
} from '@/features/integrations/utils/integrations-query-status'

/** The tenant's connections whose provider is visible. Same gating as `useIntegrationsCatalog`. */
export function useConnections({ enabled }: { enabled: boolean }) {
  const { tenantId } = useActiveTenant()
  const active = enabled && tenantId !== null

  const query = useQuery({
    queryKey: connectionsQueryKey(tenantId ?? 'none'),
    queryFn: () => fetchConnections(tenantId!),
    enabled: active,
    retry: retryUnlessForbidden,
  })

  return {
    connections: query.data?.items ?? [],
    status: resolveQueryStatus(query, active),
    retry: () => void query.refetch(),
  }
}
```

- [ ] **Step 5: Escribir el hook de escrituras**

`src/features/integrations/hooks/use-connection-mutations.ts`:

```ts
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useCallback } from 'react'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'
import {
  createConnection,
  deleteConnection,
  integrationsKey,
  pauseConnection,
  resumeConnection,
  testConnection,
  updateConnection,
} from '@/features/integrations/services/integrations.api'
import type {
  CreateConnectionInput,
  IntegrationConnection,
  UpdateConnectionInput,
} from '@/features/integrations/types/integrations'

/** One mutation for every write, so "something is saving" and "which row" have a single source. */
type ConnectionCommand =
  | { kind: 'create'; input: CreateConnectionInput }
  | { kind: 'update'; connectionId: string; input: UpdateConnectionInput }
  | { kind: 'test'; connectionId: string }
  | { kind: 'pause'; connectionId: string; version: number }
  | { kind: 'resume'; connectionId: string; version: number }
  | { kind: 'delete'; connectionId: string; version: number }

function runCommand(
  tenantId: string,
  command: ConnectionCommand,
): Promise<IntegrationConnection | undefined> {
  switch (command.kind) {
    case 'create':
      return createConnection(tenantId, command.input)
    case 'update':
      return updateConnection(tenantId, command.connectionId, command.input)
    case 'test':
      return testConnection(tenantId, command.connectionId)
    case 'pause':
      return pauseConnection(tenantId, command.connectionId, command.version)
    case 'resume':
      return resumeConnection(tenantId, command.connectionId, command.version)
    case 'delete':
      return deleteConnection(
        tenantId,
        command.connectionId,
        command.version,
      ).then(() => undefined)
  }
}

export interface UseConnectionMutationsResult {
  create: (input: CreateConnectionInput) => Promise<IntegrationConnection>
  update: (
    connectionId: string,
    input: UpdateConnectionInput,
  ) => Promise<IntegrationConnection>
  test: (connection: IntegrationConnection) => Promise<IntegrationConnection>
  pause: (connection: IntegrationConnection) => Promise<IntegrationConnection>
  resume: (connection: IntegrationConnection) => Promise<IntegrationConnection>
  remove: (connection: IntegrationConnection) => Promise<void>
  isSaving: boolean
  /** The row with a write in flight, for `aria-busy`. */
  pendingConnectionId: string | null
}

/**
 * Every write on the tenant's connections. Each one rejects with the request error: the form
 * needs it to mark its fields, and the page to explain a row action.
 */
export function useConnectionMutations(): UseConnectionMutationsResult {
  const { tenantId } = useActiveTenant()
  const queryClient = useQueryClient()

  const mutation = useMutation({
    mutationFn: (command: ConnectionCommand) => runCommand(tenantId!, command),
    // Never retried: a 412 resent with a fresh version is the silent overwrite If-Match exists to
    // prevent, and a retried POST would test the credential against the provider twice.
    retry: false,
    // The variables carry the typed secrets: nothing keeps them once the request settles.
    gcTime: 0,
    // Re-read instead of patching, on success and on failure: `connectionCount` and the status the
    // backend derives after testing are the server's to compute, and a 412 or a 404 means the
    // lists are stale. Returned, so the caller's await ends with fresh lists.
    onSettled: () =>
      queryClient.invalidateQueries({ queryKey: integrationsKey(tenantId!) }),
  })

  const { mutateAsync, reset } = mutation
  const run = useCallback(
    async (command: ConnectionCommand) => {
      try {
        return await mutateAsync(command)
      } finally {
        // Drops the variables, and the typed secrets in them, from the mutation cache.
        reset()
      }
    },
    [mutateAsync, reset],
  )

  const inFlight = mutation.isPending ? mutation.variables : undefined

  return {
    create: async (input) =>
      (await run({ kind: 'create', input })) as IntegrationConnection,
    update: async (connectionId, input) =>
      (await run({
        kind: 'update',
        connectionId,
        input,
      })) as IntegrationConnection,
    test: async (connection) =>
      (await run({
        kind: 'test',
        connectionId: connection.id,
      })) as IntegrationConnection,
    pause: async (connection) =>
      (await run({
        kind: 'pause',
        connectionId: connection.id,
        version: connection.version,
      })) as IntegrationConnection,
    resume: async (connection) =>
      (await run({
        kind: 'resume',
        connectionId: connection.id,
        version: connection.version,
      })) as IntegrationConnection,
    remove: async (connection) => {
      await run({
        kind: 'delete',
        connectionId: connection.id,
        version: connection.version,
      })
    },
    isSaving: mutation.isPending,
    pendingConnectionId:
      inFlight && 'connectionId' in inFlight ? inFlight.connectionId : null,
  }
}
```

- [ ] **Step 6: Correrlas y verlas pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/utils/integrations-query-status.test.ts src/features/integrations/hooks/use-integrations-catalog.test.tsx src/features/integrations/hooks/use-connections.test.tsx src/features/integrations/hooks/use-connection-mutations.test.tsx
```

Expected: PASS — `Test Files  4 passed (4)`, `Tests  28 passed (28)` (12 + 4 + 3 + 9).

- [ ] **Step 7: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
$files = @(
  'src/features/integrations/utils/integrations-query-status.ts',
  'src/features/integrations/utils/integrations-query-status.test.ts',
  'src/features/integrations/hooks/use-integrations-catalog.ts',
  'src/features/integrations/hooks/use-integrations-catalog.test.tsx',
  'src/features/integrations/hooks/use-connections.ts',
  'src/features/integrations/hooks/use-connections.test.tsx',
  'src/features/integrations/hooks/use-connection-mutations.ts',
  'src/features/integrations/hooks/use-connection-mutations.test.tsx'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(integrations): hooks de catálogo, conexiones y escrituras

Las lecturas no se piden sin el permiso y un 403 no se reintenta. Las seis
escrituras van en una sola mutación sin reintento, que relee catálogo y
conexiones al asentarse (también ante un error) y no deja el secreto
tipeado en ninguna caché.
'@
$msgFile = Join-Path $env:TEMP 'qep-integraciones-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

---

### Task 5: Diálogo de conexión (crear y editar)

**Files:**
- Create: `src/features/integrations/utils/integration-dates.ts`
- Create: `src/features/integrations/components/connection-form-dialog.tsx`
- Test: `src/features/integrations/components/connection-form-dialog.test.tsx`

**Interfaces:**
- Consumes: Task 1 (`formPathOf`, tipos), Task 2 (`describeIntegrationFailure`, `IntegrationFailure`), Task 3 (`connectionFormSchema`, `connectionFormDefaults`, `toConnectionPayload`, `hasUsableStoredSecret`, `ConnectionFormInput`, `ConnectionFormValues`), `sanitizePhoneInput` (`@/lib/phone`), `get` (`react-hook-form`).
- Produces: `formatDayAndMonth(iso: string): string` (`utils/integration-dates.ts`), `CONNECTION_FORM_TEXTS { verifying; keepSecret; unreadableSecret; retry }`, `ConnectionFormDialogProps { provider: IntegrationProvider; connection: IntegrationConnection | null; onSubmit: (payload: ConnectionPayload) => Promise<unknown>; onClose: () => void }`, `ConnectionFormDialog`. Se monta **sólo mientras está abierto** (la página lo renderiza condicional): `defaultValues` siembra una vez y un refetch de la lista no borra lo tipeado. Cierra solo (`onClose`) cuando `onSubmit` resuelve.

- [ ] **Step 1: Escribir la prueba**

`src/features/integrations/components/connection-form-dialog.test.tsx`:

```tsx
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import {
  CONNECTION_FORM_TEXTS,
  ConnectionFormDialog,
  type ConnectionFormDialogProps,
} from './connection-form-dialog'
import {
  INTEGRATION_ERROR_MESSAGES,
  INTEGRATION_FAILURE_MESSAGES,
} from '@/features/integrations/services/integrations.api'
import type { IntegrationProvider } from '@/features/integrations/types/integrations'
import { CONNECTION_FORM_MESSAGES } from '@/features/integrations/types/integrations.schema'
import { ApiError } from '@/lib/api-client'
import {
  OCTOBER_8,
  ZENVIA_PROVIDER,
  integrationConnection,
} from '@/test/integrations'

function renderDialog(overrides: Partial<ConnectionFormDialogProps> = {}) {
  const props: ConnectionFormDialogProps = {
    provider: ZENVIA_PROVIDER,
    connection: null,
    onSubmit: vi.fn().mockResolvedValue(undefined),
    onClose: vi.fn(),
    ...overrides,
  }
  render(<ConnectionFormDialog {...props} />)
  return { props, user: userEvent.setup() }
}

const nameInput = () => screen.getByLabelText('Nombre de la conexión')
const tokenInput = () => screen.getByLabelText('API token')
const phoneInput = () => screen.getByLabelText('Número emisor')

async function fillNew(user: ReturnType<typeof userEvent.setup>) {
  await user.type(nameInput(), 'WhatsApp sede norte')
  await user.type(tokenInput(), 'zenvia-token')
  await user.type(phoneInput(), '573001234567')
}

describe('ConnectionFormDialog', () => {
  it('draws each catalog field by its kind', () => {
    const provider: IntegrationProvider = {
      ...ZENVIA_PROVIDER,
      fields: [
        ...ZENVIA_PROVIDER.fields,
        { key: 'baseUrl', label: 'URL base', kind: 'Url', required: false, maxLength: 200 },
        { key: 'account', label: 'Cuenta', kind: 'Text', required: false, maxLength: 50 },
      ],
    }

    renderDialog({ provider })

    expect(tokenInput()).toHaveAttribute('type', 'password')
    expect(phoneInput()).toHaveAttribute('inputmode', 'numeric')
    expect(screen.getByLabelText('URL base')).toHaveAttribute('type', 'url')
    expect(screen.getByLabelText('Cuenta')).toHaveAttribute('type', 'text')
  })

  // Review Focus 1.
  it('keeps only digits in a phone field, typed or pasted', async () => {
    const { user } = renderDialog()

    await user.type(phoneInput(), '+57 300-123 4567')
    expect(phoneInput()).toHaveValue('573001234567')

    await user.clear(phoneInput())
    await user.click(phoneInput())
    await user.paste('+57 (300) 765 4321')
    expect(phoneInput()).toHaveValue('573007654321')
  })

  it('asks for every required value before sending anything', async () => {
    const { props, user } = renderDialog()

    await user.click(screen.getByRole('button', { name: 'Conectar' }))

    expect(
      await screen.findByText(CONNECTION_FORM_MESSAGES.nameRequired),
    ).toBeInTheDocument()
    expect(
      screen.getAllByText(CONNECTION_FORM_MESSAGES.fieldRequired),
    ).toHaveLength(2)
    expect(props.onSubmit).not.toHaveBeenCalled()
  })

  it('sends what was typed and closes', async () => {
    const { props, user } = renderDialog()

    await fillNew(user)
    await user.click(screen.getByRole('button', { name: 'Conectar' }))

    await waitFor(() => expect(props.onClose).toHaveBeenCalled())
    expect(props.onSubmit).toHaveBeenCalledWith({
      name: 'WhatsApp sede norte',
      fields: { fromNumber: '573001234567' },
      secrets: { apiToken: 'zenvia-token' },
    })
  })

  it('says it is verifying with the provider while the request is in flight', async () => {
    const { user } = renderDialog({
      onSubmit: vi.fn(() => new Promise<never>(() => {})),
    })

    await fillNew(user)
    await user.click(screen.getByRole('button', { name: 'Conectar' }))

    expect(
      await screen.findByRole('button', { name: CONNECTION_FORM_TEXTS.verifying }),
    ).toBeDisabled()
  })

  it('shows a stored secret as configured and keeps it when left blank', async () => {
    const { props, user } = renderDialog({ connection: integrationConnection() })

    expect(nameInput()).toHaveValue('WhatsApp sede norte')
    expect(phoneInput()).toHaveValue('573001234567')
    expect(tokenInput()).toHaveValue('')
    expect(screen.getByText('Configurada el 8 de octubre')).toBeInTheDocument()
    expect(screen.getByText(CONNECTION_FORM_TEXTS.keepSecret)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(props.onSubmit).toHaveBeenCalledWith({
        name: 'WhatsApp sede norte',
        fields: { fromNumber: '573001234567' },
        secrets: {},
      }),
    )
  })

  it('asks to paste again a secret the backend can no longer read', async () => {
    const connection = integrationConnection({
      secrets: {
        apiToken: { configured: true, updatedAt: OCTOBER_8, readable: false },
      },
    })
    const { props, user } = renderDialog({ connection })

    expect(
      screen.getByText(CONNECTION_FORM_TEXTS.unreadableSecret),
    ).toBeInTheDocument()
    expect(screen.queryByText(CONNECTION_FORM_TEXTS.keepSecret)).toBeNull()

    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText(CONNECTION_FORM_MESSAGES.fieldRequired),
    ).toBeInTheDocument()
    expect(props.onSubmit).not.toHaveBeenCalled()
  })

  it('marks the secret when the provider rejects the credential', async () => {
    const { props, user } = renderDialog({
      onSubmit: vi
        .fn()
        .mockRejectedValue(
          new ApiError(422, {
            code: 'integrations.connection.credentials_rejected',
          }),
        ),
    })

    await fillNew(user)
    await user.click(screen.getByRole('button', { name: 'Conectar' }))

    expect(
      await screen.findByText(
        INTEGRATION_ERROR_MESSAGES['integrations.connection.credentials_rejected'],
      ),
    ).toBeInTheDocument()
    expect(tokenInput()).toHaveAttribute('aria-invalid', 'true')
    expect(props.onClose).not.toHaveBeenCalled()
  })

  it('marks the fields a validation.failed points at', async () => {
    const { user } = renderDialog({
      onSubmit: vi.fn().mockRejectedValue(
        new ApiError(422, {
          code: 'validation.failed',
          errors: { name: [], 'fields.fromNumber': [] },
        }),
      ),
    })

    await fillNew(user)
    await user.click(screen.getByRole('button', { name: 'Conectar' }))

    expect(
      await screen.findAllByText(INTEGRATION_FAILURE_MESSAGES.invalidField),
    ).toHaveLength(2)
    expect(nameInput()).toHaveAttribute('aria-invalid', 'true')
    expect(phoneInput()).toHaveAttribute('aria-invalid', 'true')
  })

  // Review Focus 4: the typed secret stays in the form for the retry, and only there.
  it('warns at the top and retries the same values when the provider is unreachable', async () => {
    const onSubmit = vi
      .fn()
      .mockRejectedValueOnce(
        new ApiError(422, {
          code: 'integrations.connection.provider_unreachable',
        }),
      )
      .mockResolvedValueOnce(undefined)
    const { props, user } = renderDialog({ onSubmit })

    await fillNew(user)
    await user.click(screen.getByRole('button', { name: 'Conectar' }))

    expect(
      await screen.findByText(
        INTEGRATION_ERROR_MESSAGES['integrations.connection.provider_unreachable'],
      ),
    ).toBeInTheDocument()
    await user.click(
      screen.getByRole('button', { name: CONNECTION_FORM_TEXTS.retry }),
    )

    await waitFor(() => expect(props.onClose).toHaveBeenCalled())
    expect(onSubmit).toHaveBeenCalledTimes(2)
    expect(onSubmit.mock.calls[1]?.[0]).toEqual(onSubmit.mock.calls[0]?.[0])
    expect(onSubmit.mock.calls[1]?.[0]).toMatchObject({
      secrets: { apiToken: 'zenvia-token' },
    })
  })

  // Review Focus 3.
  it('does not offer to resend after a conflict', async () => {
    const { props, user } = renderDialog({
      connection: integrationConnection(),
      onSubmit: vi
        .fn()
        .mockRejectedValue(new ApiError(412, { code: 'concurrency.conflict' })),
    })

    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText(INTEGRATION_FAILURE_MESSAGES.conflict),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    expect(props.onSubmit).toHaveBeenCalledTimes(1)
  })

  it('draws a field kind it does not know as plain text', () => {
    const provider = {
      ...ZENVIA_PROVIDER,
      fields: [
        { key: 'clientId', label: 'Client id', kind: 'OAuth', required: false, maxLength: 100 },
      ],
    } as unknown as IntegrationProvider

    renderDialog({ provider })

    expect(screen.getByLabelText('Client id')).toHaveAttribute('type', 'text')
  })

  it('closes without sending on Cancelar', async () => {
    const { props, user } = renderDialog()

    await user.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(props.onClose).toHaveBeenCalled()
    expect(props.onSubmit).not.toHaveBeenCalled()
  })
})
```

- [ ] **Step 2: Correrla y verla fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/components/connection-form-dialog.test.tsx
```

Expected: FAIL — `Failed to resolve import "./connection-form-dialog"`.

- [ ] **Step 3: Escribir el formato de fecha**

`src/features/integrations/utils/integration-dates.ts`:

```ts
const DAY_AND_MONTH = new Intl.DateTimeFormat('es-CO', {
  day: 'numeric',
  month: 'long',
})

/** «8 de octubre», the way the spec writes when a secret was configured. In the browser's time zone. */
export function formatDayAndMonth(iso: string): string {
  return DAY_AND_MONTH.format(new Date(iso))
}
```

- [ ] **Step 4: Escribir el diálogo**

`src/features/integrations/components/connection-form-dialog.tsx`:

```tsx
import { useId, useState } from 'react'

import { zodResolver } from '@hookform/resolvers/zod'
import { KeyRound, TriangleAlert } from 'lucide-react'
import {
  get,
  useForm,
  type FieldErrors,
  type FieldPath,
  type UseFormRegister,
} from 'react-hook-form'

import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  formPathOf,
  type ConnectionPayload,
  type IntegrationConnection,
  type IntegrationField,
  type IntegrationProvider,
} from '@/features/integrations/types/integrations'
import {
  connectionFormDefaults,
  connectionFormSchema,
  hasUsableStoredSecret,
  toConnectionPayload,
  type ConnectionFormInput,
  type ConnectionFormValues,
} from '@/features/integrations/types/integrations.schema'
import {
  describeIntegrationFailure,
  type IntegrationFailure,
} from '@/features/integrations/utils/describe-integration-failure'
import { formatDayAndMonth } from '@/features/integrations/utils/integration-dates'
import { sanitizePhoneInput } from '@/lib/phone'

/** Copy fixed by the spec («Frontend»), exported so the tests assert the exact words. */
export const CONNECTION_FORM_TEXTS = {
  verifying: 'Verificando con el proveedor…',
  keepSecret: 'Déjala en blanco para conservar la actual',
  unreadableSecret: 'La clave guardada ya no se puede leer: pégala de nuevo',
  retry: 'Intentar de nuevo',
} as const

export interface ConnectionFormDialogProps {
  provider: IntegrationProvider
  /** `null` to connect a new one; otherwise the row as it was when the dialog opened. */
  connection: IntegrationConnection | null
  /** Rejects with the request error: the dialog needs it to mark its fields. */
  onSubmit: (payload: ConnectionPayload) => Promise<unknown>
  onClose: () => void
}

/**
 * Connects a provider or edits a connection, with the inputs the catalog describes.
 *
 * Mounted only while open (the page renders it conditionally), so `defaultValues` seed it once
 * and a list refetch underneath never wipes what is being typed. The typed secret lives only in
 * this form's state: it is there for «Intentar de nuevo» and gone when the dialog unmounts.
 */
export function ConnectionFormDialog({
  provider,
  connection,
  onSubmit,
  onClose,
}: ConnectionFormDialogProps) {
  const formId = useId()
  const nameId = useId()
  const nameHelpId = useId()
  const nameErrorId = useId()
  const [failure, setFailure] = useState<IntegrationFailure | null>(null)
  const formPaths = ['name', ...provider.fields.map(formPathOf)]

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitting },
  } = useForm<ConnectionFormInput, unknown, ConnectionFormValues>({
    resolver: zodResolver(connectionFormSchema(provider, connection)),
    defaultValues: connectionFormDefaults(provider, connection),
  })

  const submit = async (values: ConnectionFormValues) => {
    setFailure(null)
    try {
      await onSubmit(toConnectionPayload(values, provider))
      onClose()
    } catch (error) {
      const described = describeIntegrationFailure(error, formPaths)
      Object.entries(described.fields).forEach(([path, message], index) => {
        setError(
          path as FieldPath<ConnectionFormInput>,
          { message },
          { shouldFocus: index === 0 },
        )
      })
      setFailure(described.message === null ? null : described)
    }
  }
  const submitForm = handleSubmit(submit)

  const isEdit = connection !== null
  const nameError = errors.name?.message

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        // Closing mid-request would say "done" while the provider is still being asked.
        if (!open && !isSubmitting) onClose()
      }}
    >
      <DialogContent showCloseButton={!isSubmitting} className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>
            {isEdit
              ? `Editar ${connection.name}`
              : `Conectar ${provider.displayName}`}
          </DialogTitle>
          <DialogDescription>
            Probamos la credencial con el proveedor antes de guardarla. Una vez
            guardada, no se vuelve a mostrar.
          </DialogDescription>
        </DialogHeader>

        {failure ? (
          <FailureNotice
            failure={failure}
            disabled={isSubmitting}
            onRetry={() => void submitForm()}
          />
        ) : null}

        <form id={formId} noValidate className="space-y-4" onSubmit={submitForm}>
          <div className="space-y-1.5">
            <Label htmlFor={nameId}>Nombre de la conexión</Label>
            {/* No `maxLength`: a pasted name that is too long would be cut silently instead of
                showing why it is refused. */}
            <Input
              id={nameId}
              autoComplete="off"
              aria-invalid={nameError ? true : undefined}
              aria-describedby={describedBy(nameHelpId, nameError && nameErrorId)}
              {...register('name')}
            />
            <p id={nameHelpId} className="text-xs text-muted-foreground">
              Para reconocerla cuando tengas varias. Por ejemplo: WhatsApp sede
              norte.
            </p>
            <FieldError id={nameErrorId} message={nameError} />
          </div>

          {provider.fields.map((field) => (
            <ConnectionField
              key={field.key}
              field={field}
              connection={connection}
              register={register}
              errors={errors}
            />
          ))}
        </form>

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            disabled={isSubmitting}
            onClick={onClose}
          >
            Cancelar
          </Button>
          <Button
            type="submit"
            form={formId}
            // After a 412, a 404 or a status change, resending cannot work: the page re-reads and
            // the person reopens the dialog on the fresh row.
            disabled={isSubmitting || failure?.shouldReload === true}
          >
            {isSubmitting
              ? CONNECTION_FORM_TEXTS.verifying
              : isEdit
                ? 'Guardar'
                : 'Conectar'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function FailureNotice({
  failure,
  disabled,
  onRetry,
}: {
  failure: IntegrationFailure
  disabled: boolean
  onRetry: () => void
}) {
  if (failure.retryable) {
    // A warning, not an error: nothing typed is wrong (spec D3, «no pude verificar»).
    return (
      <div
        role="alert"
        className="flex flex-col gap-3 rounded-xl border border-warning-border bg-warning-surface px-4 py-3 text-sm text-warning-foreground"
      >
        <p className="flex items-start gap-2">
          <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
          <span>{failure.message}</span>
        </p>
        <Button
          type="button"
          variant="outline"
          size="sm"
          className="self-start"
          disabled={disabled}
          onClick={onRetry}
        >
          {CONNECTION_FORM_TEXTS.retry}
        </Button>
      </div>
    )
  }
  return (
    <p role="alert" className="text-sm text-destructive">
      {failure.message}
    </p>
  )
}

interface ConnectionFieldProps {
  field: IntegrationField
  connection: IntegrationConnection | null
  register: UseFormRegister<ConnectionFormInput>
  errors: FieldErrors<ConnectionFormInput>
}

function ConnectionField({
  field,
  connection,
  register,
  errors,
}: ConnectionFieldProps) {
  const inputId = useId()
  const helpId = useId()
  const noticeId = useId()
  const errorId = useId()
  const path = formPathOf(field) as FieldPath<ConnectionFormInput>
  const registration = register(path)
  const error = (get(errors, path) as { message?: string } | undefined)
    ?.message

  if (field.kind === 'Secret') {
    const stored = connection?.secrets[field.key]
    const unreadable = stored?.configured === true && !stored.readable
    const keepsStored = hasUsableStoredSecret(connection, field.key)
    return (
      <div className="space-y-1.5">
        <Label htmlFor={inputId}>{field.label}</Label>
        {stored?.configured ? (
          <p className="flex items-center gap-2 text-xs text-muted-foreground">
            <KeyRound className="size-3.5 shrink-0" aria-hidden="true" />
            {stored.updatedAt
              ? `Configurada el ${formatDayAndMonth(stored.updatedAt)}`
              : 'Configurada'}
          </p>
        ) : null}
        {unreadable ? (
          <p
            id={noticeId}
            className="flex items-start gap-2 rounded-xl border border-warning-border bg-warning-surface px-3 py-2 text-xs text-warning-foreground"
          >
            <TriangleAlert className="mt-px size-3.5 shrink-0" aria-hidden="true" />
            <span>{CONNECTION_FORM_TEXTS.unreadableSecret}</span>
          </p>
        ) : null}
        <Input
          id={inputId}
          type="password"
          autoComplete="off"
          autoCapitalize="off"
          spellCheck={false}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy(
            unreadable && noticeId,
            keepsStored && helpId,
            error && errorId,
          )}
          {...registration}
        />
        {keepsStored ? (
          <p id={helpId} className="text-xs text-muted-foreground">
            {CONNECTION_FORM_TEXTS.keepSecret}
          </p>
        ) : null}
        <FieldError id={errorId} message={error} />
      </div>
    )
  }

  const isPhone = field.kind === 'Phone'
  const isUrl = field.kind === 'Url'
  return (
    <div className="space-y-1.5">
      <Label htmlFor={inputId}>{field.label}</Label>
      {/* A kind this build does not know (a newer backend) is drawn as plain text, not dropped. */}
      <Input
        id={inputId}
        type={isUrl ? 'url' : 'text'}
        inputMode={isPhone ? 'numeric' : isUrl ? 'url' : undefined}
        autoComplete="off"
        spellCheck={false}
        placeholder={isPhone ? '573001234567' : undefined}
        className={isPhone ? 'tabular-nums' : undefined}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(isPhone && helpId, error && errorId)}
        {...registration}
        onChange={(event) => {
          // The mask every other phone input uses (`lib/phone.ts`): a typed or pasted '+',
          // space or dash never reaches the value, so it never comes back as a 422.
          if (isPhone) event.target.value = sanitizePhoneInput(event.target.value)
          void registration.onChange(event)
        }}
      />
      {isPhone ? (
        <p id={helpId} className="text-xs text-muted-foreground">
          Con indicativo de país, sólo dígitos y sin «+».
        </p>
      ) : null}
      <FieldError id={errorId} message={error} />
    </div>
  )
}

function FieldError({ id, message }: { id: string; message?: string }) {
  if (!message) return null
  return (
    <p id={id} role="alert" className="text-xs text-destructive">
      {message}
    </p>
  )
}

/** `aria-describedby` from the ids that are actually on screen. */
function describedBy(
  ...ids: (string | false | undefined)[]
): string | undefined {
  const present = ids.filter(Boolean).join(' ')
  return present || undefined
}
```

- [ ] **Step 5: Correrla y verla pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/components/connection-form-dialog.test.tsx
```

Expected: PASS — `Tests  13 passed (13)`.

- [ ] **Step 6: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
$files = @(
  'src/features/integrations/utils/integration-dates.ts',
  'src/features/integrations/components/connection-form-dialog.tsx',
  'src/features/integrations/components/connection-form-dialog.test.tsx'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(integrations): diálogo para conectar y editar una conexión

Campos dibujados desde el catálogo por tipo: el secreto como password con
«Configurada el …» y «Déjala en blanco para conservar la actual», el
teléfono con la máscara de dígitos de lib/phone y el aviso cuando la clave
guardada ya no se puede leer. «Verificando con el proveedor…» al guardar,
credentials_rejected marca el secreto y provider_unreachable ofrece
«Intentar de nuevo» con los mismos valores.
'@
$msgFile = Join-Path $env:TEMP 'qep-integraciones-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

---

### Task 6: Diálogo de eliminación por nombre exacto

**Files:**
- Create: `src/features/integrations/components/delete-connection-dialog.tsx`
- Test: `src/features/integrations/components/delete-connection-dialog.test.tsx`

**Interfaces:**
- Consumes: `describeIntegrationFailure` (Task 2), `INTEGRATION_FAILURE_MESSAGES` (Task 2), `IntegrationConnection` (Task 1).
- Produces: `DELETE_CONNECTION_TEXT`, `DeleteConnectionDialogProps { connection: IntegrationConnection; onConfirm: () => Promise<void>; onClose: () => void }`, `DeleteConnectionDialog`. Montado sólo mientras está abierto; llama `onClose` cuando `onConfirm` resuelve.

- [ ] **Step 1: Escribir la prueba**

`src/features/integrations/components/delete-connection-dialog.test.tsx`:

```tsx
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import {
  DELETE_CONNECTION_TEXT,
  DeleteConnectionDialog,
  type DeleteConnectionDialogProps,
} from './delete-connection-dialog'
import { INTEGRATION_FAILURE_MESSAGES } from '@/features/integrations/services/integrations.api'
import { ApiError } from '@/lib/api-client'
import { integrationConnection } from '@/test/integrations'

function renderDialog(overrides: Partial<DeleteConnectionDialogProps> = {}) {
  const props: DeleteConnectionDialogProps = {
    connection: integrationConnection(),
    onConfirm: vi.fn().mockResolvedValue(undefined),
    onClose: vi.fn(),
    ...overrides,
  }
  render(<DeleteConnectionDialog {...props} />)
  return { props, user: userEvent.setup() }
}

const nameInput = () => screen.getByLabelText('Nombre de la conexión')
const confirmButton = () => screen.getByRole('button', { name: 'Eliminar' })

describe('DeleteConnectionDialog', () => {
  it('uses the copy of the spec', () => {
    renderDialog()

    expect(DELETE_CONNECTION_TEXT).toBe(
      'Esta acción no se puede deshacer. Escribe el nombre de la conexión para confirmar',
    )
    expect(screen.getByText(DELETE_CONNECTION_TEXT)).toBeInTheDocument()
  })

  it('keeps Eliminar disabled until the exact name is typed', async () => {
    const { user } = renderDialog()

    expect(confirmButton()).toBeDisabled()

    await user.type(nameInput(), 'whatsapp sede norte')
    expect(confirmButton()).toBeDisabled()

    await user.clear(nameInput())
    await user.type(nameInput(), 'WhatsApp sede norte ')
    expect(confirmButton()).toBeDisabled()

    await user.clear(nameInput())
    await user.type(nameInput(), 'WhatsApp sede norte')
    expect(confirmButton()).toBeEnabled()
  })

  it('deletes and closes once confirmed', async () => {
    const { props, user } = renderDialog()

    await user.type(nameInput(), 'WhatsApp sede norte')
    await user.click(confirmButton())

    await waitFor(() => expect(props.onClose).toHaveBeenCalled())
    expect(props.onConfirm).toHaveBeenCalledTimes(1)
  })

  it('says why it could not delete and stays open', async () => {
    const { props, user } = renderDialog({
      onConfirm: vi
        .fn()
        .mockRejectedValue(new ApiError(412, { code: 'concurrency.conflict' })),
    })

    await user.type(nameInput(), 'WhatsApp sede norte')
    await user.click(confirmButton())

    expect(
      await screen.findByText(INTEGRATION_FAILURE_MESSAGES.conflict),
    ).toBeInTheDocument()
    expect(props.onClose).not.toHaveBeenCalled()
    expect(confirmButton()).toBeEnabled()
  })

  it('cannot be dismissed while the request is in flight', async () => {
    const { user } = renderDialog({
      onConfirm: vi.fn(() => new Promise<never>(() => {})),
    })

    await user.type(nameInput(), 'WhatsApp sede norte')
    await user.click(confirmButton())

    expect(
      await screen.findByRole('button', { name: 'Eliminando…' }),
    ).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
  })
})
```

- [ ] **Step 2: Correrla y verla fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/components/delete-connection-dialog.test.tsx
```

Expected: FAIL — `Failed to resolve import "./delete-connection-dialog"`.

- [ ] **Step 3: Escribir el diálogo**

`src/features/integrations/components/delete-connection-dialog.tsx`:

```tsx
import { useId, useState } from 'react'

import { Trash2 } from 'lucide-react'

import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { INTEGRATION_FAILURE_MESSAGES } from '@/features/integrations/services/integrations.api'
import type { IntegrationConnection } from '@/features/integrations/types/integrations'
import { describeIntegrationFailure } from '@/features/integrations/utils/describe-integration-failure'

/** Fixed by the spec («Frontend»), word for word. */
export const DELETE_CONNECTION_TEXT =
  'Esta acción no se puede deshacer. Escribe el nombre de la conexión para confirmar'

export interface DeleteConnectionDialogProps {
  connection: IntegrationConnection
  onConfirm: () => Promise<void>
  onClose: () => void
}

/**
 * Deletes a connection and its secrets after the person types its exact name (spec decision 6).
 * Mounted only while open, so the typed name never survives into the next deletion.
 */
export function DeleteConnectionDialog({
  connection,
  onConfirm,
  onClose,
}: DeleteConnectionDialogProps) {
  const formId = useId()
  const inputId = useId()
  const [typed, setTyped] = useState('')
  const [isDeleting, setIsDeleting] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)
  // Exact: neither trimmed nor case-folded. Typing the name is the whole safeguard.
  const matches = typed === connection.name

  const confirm = async () => {
    if (!matches || isDeleting) return
    setIsDeleting(true)
    setFailure(null)
    try {
      await onConfirm()
      onClose()
    } catch (error) {
      setFailure(
        describeIntegrationFailure(error).message ??
          INTEGRATION_FAILURE_MESSAGES.generic,
      )
      setIsDeleting(false)
    }
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !isDeleting) onClose()
      }}
    >
      <DialogContent showCloseButton={!isDeleting}>
        <DialogHeader>
          <DialogTitle>{`Eliminar ${connection.name}`}</DialogTitle>
          <DialogDescription>{DELETE_CONNECTION_TEXT}</DialogDescription>
        </DialogHeader>

        <form
          id={formId}
          noValidate
          className="space-y-1.5"
          onSubmit={(event) => {
            event.preventDefault()
            void confirm()
          }}
        >
          <Label htmlFor={inputId}>Nombre de la conexión</Label>
          <Input
            id={inputId}
            autoComplete="off"
            spellCheck={false}
            placeholder={connection.name}
            value={typed}
            onChange={(event) => setTyped(event.target.value)}
          />
        </form>

        {failure ? (
          <p role="alert" className="text-sm text-destructive">
            {failure}
          </p>
        ) : null}

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            disabled={isDeleting}
            onClick={onClose}
          >
            Cancelar
          </Button>
          <Button
            type="submit"
            form={formId}
            variant="destructive"
            disabled={!matches || isDeleting}
          >
            <Trash2 aria-hidden="true" />
            {isDeleting ? 'Eliminando…' : 'Eliminar'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
```

- [ ] **Step 4: Correrla y verla pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/components/delete-connection-dialog.test.tsx
```

Expected: PASS — `Tests  5 passed (5)`.

- [ ] **Step 5: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
$files = @(
  'src/features/integrations/components/delete-connection-dialog.tsx',
  'src/features/integrations/components/delete-connection-dialog.test.tsx'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(integrations): eliminar una conexión escribiendo su nombre exacto

Sin recortar ni ignorar mayúsculas, con el texto de la spec, sin cerrarse
a mitad del DELETE y explicando el motivo cuando falla.
'@
$msgFile = Join-Path $env:TEMP 'qep-integraciones-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

---

### Task 7: Badge de estado, catálogo de proveedores y lista de conexiones

**Files:**
- Create: `src/features/integrations/components/connection-status-badge.tsx`
- Create: `src/features/integrations/components/provider-catalog.tsx`
- Test: `src/features/integrations/components/provider-catalog.test.tsx`
- Create: `src/features/integrations/components/connection-list.tsx`
- Test: `src/features/integrations/components/connection-list.test.tsx`

**Interfaces:**
- Consumes: `CATEGORY_LABELS`, `CONNECTION_STATUS_LABELS`, tipos (Task 1); `formatDayAndMonth` (Task 5); `Badge`, `Button`.
- Produces: `ConnectionStatusBadge({ status: ConnectionStatus })`; `ProviderCatalogProps { providers: IntegrationProvider[]; canManage: boolean; disabled: boolean; onConnect: (provider: IntegrationProvider) => void }`, `ProviderCatalog`; `ConnectionListProps { connections: IntegrationConnection[]; providers: IntegrationProvider[]; canManage: boolean; busy: boolean; pendingConnectionId: string | null; onEdit; onTest; onPause; onResume; onDelete: (connection: IntegrationConnection) => void }`, `ConnectionList`. Nombres accesibles de las acciones: `Conectar <displayName>`, `Editar <name>`, `Probar <name>`, `Pausar <name>`, `Reanudar <name>`, `Eliminar <name>`.

- [ ] **Step 1: Escribir las pruebas**

`src/features/integrations/components/provider-catalog.test.tsx`:

```tsx
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { ProviderCatalog, type ProviderCatalogProps } from './provider-catalog'
import type { IntegrationProvider } from '@/features/integrations/types/integrations'
import { ZENVIA_PROVIDER } from '@/test/integrations'

function renderCatalog(overrides: Partial<ProviderCatalogProps> = {}) {
  const props: ProviderCatalogProps = {
    providers: [ZENVIA_PROVIDER],
    canManage: true,
    disabled: false,
    onConnect: vi.fn(),
    ...overrides,
  }
  render(<ProviderCatalog {...props} />)
  return { props, user: userEvent.setup() }
}

describe('ProviderCatalog', () => {
  it('groups the providers under the label of their category', () => {
    renderCatalog()

    const group = screen.getByRole('region', { name: 'Mensajería' })
    expect(within(group).getByText('Zenvia (WhatsApp)')).toBeInTheDocument()
    expect(within(group).getByText('1 de 20 conexiones')).toBeInTheDocument()
  })

  it('labels a category it does not know by its name', () => {
    const provider = {
      ...ZENVIA_PROVIDER,
      category: 'Payments',
    } as unknown as IntegrationProvider

    renderCatalog({ providers: [provider] })

    expect(screen.getByRole('region', { name: 'Payments' })).toBeInTheDocument()
  })

  it('offers Conectar only to whoever can manage', () => {
    renderCatalog({ canManage: false })

    expect(
      screen.queryByRole('button', { name: 'Conectar Zenvia (WhatsApp)' }),
    ).toBeNull()
  })

  it('connects the chosen provider', async () => {
    const { props, user } = renderCatalog()

    await user.click(
      screen.getByRole('button', { name: 'Conectar Zenvia (WhatsApp)' }),
    )

    expect(props.onConnect).toHaveBeenCalledWith(ZENVIA_PROVIDER)
  })

  // Review Focus 2.
  it('disables Conectar at the limit and says why', () => {
    renderCatalog({
      providers: [{ ...ZENVIA_PROVIDER, connectionCount: 20 }],
    })

    expect(
      screen.getByRole('button', { name: 'Conectar Zenvia (WhatsApp)' }),
    ).toBeDisabled()
    expect(
      screen.getByText('Llegaste al máximo de 20 conexiones.'),
    ).toBeInTheDocument()
  })

  it('says so when no module of the plan uses integrations', () => {
    renderCatalog({ providers: [] })

    expect(
      screen.getByText('Ningún módulo de tu plan usa integraciones por ahora.'),
    ).toBeInTheDocument()
  })
})
```

`src/features/integrations/components/connection-list.test.tsx`:

```tsx
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { ConnectionList, type ConnectionListProps } from './connection-list'
import type { ConnectionStatus } from '@/features/integrations/types/integrations'
import { ZENVIA_PROVIDER, integrationConnection } from '@/test/integrations'

function renderList(overrides: Partial<ConnectionListProps> = {}) {
  const props: ConnectionListProps = {
    connections: [integrationConnection()],
    providers: [ZENVIA_PROVIDER],
    canManage: true,
    busy: false,
    pendingConnectionId: null,
    onEdit: vi.fn(),
    onTest: vi.fn(),
    onPause: vi.fn(),
    onResume: vi.fn(),
    onDelete: vi.fn(),
    ...overrides,
  }
  render(<ConnectionList {...props} />)
  return { props, user: userEvent.setup() }
}

describe('ConnectionList', () => {
  it('groups the connections under their provider', () => {
    renderList()

    const group = screen.getByRole('region', { name: 'Zenvia (WhatsApp)' })
    expect(within(group).getByText('WhatsApp sede norte')).toBeInTheDocument()
  })

  it.each([
    ['Active', 'Activa'],
    ['Paused', 'Pausada'],
    ['NeedsAttention', 'Necesita atención'],
  ] as const)('labels %s as %s', (status: ConnectionStatus, label) => {
    renderList({ connections: [integrationConnection({ status })] })

    expect(screen.getByText(label)).toBeInTheDocument()
  })

  it('offers Pausar only to an active connection and Reanudar only to a paused one', () => {
    renderList({
      connections: [
        integrationConnection({ id: 'a', name: 'Norte', status: 'Active' }),
        integrationConnection({ id: 'p', name: 'Sur', status: 'Paused' }),
        integrationConnection({ id: 'n', name: 'Centro', status: 'NeedsAttention' }),
      ],
    })

    expect(screen.getByRole('button', { name: 'Pausar Norte' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reanudar Norte' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Reanudar Sur' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Pausar Sur' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Pausar Centro' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Reanudar Centro' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Probar Centro' })).toBeInTheDocument()
  })

  it('hides every action without the manage permission', () => {
    renderList({ canManage: false })

    expect(screen.queryAllByRole('button')).toHaveLength(0)
    expect(screen.getByText('WhatsApp sede norte')).toBeInTheDocument()
  })

  it('hands the row to each action', async () => {
    const connection = integrationConnection()
    const { props, user } = renderList({ connections: [connection] })

    await user.click(screen.getByRole('button', { name: 'Editar WhatsApp sede norte' }))
    await user.click(screen.getByRole('button', { name: 'Probar WhatsApp sede norte' }))
    await user.click(screen.getByRole('button', { name: 'Pausar WhatsApp sede norte' }))
    await user.click(screen.getByRole('button', { name: 'Eliminar WhatsApp sede norte' }))

    expect(props.onEdit).toHaveBeenCalledWith(connection)
    expect(props.onTest).toHaveBeenCalledWith(connection)
    expect(props.onPause).toHaveBeenCalledWith(connection)
    expect(props.onDelete).toHaveBeenCalledWith(connection)
  })

  it('disables the actions while a write is in flight and marks its row', () => {
    const connection = integrationConnection()
    renderList({
      connections: [connection],
      busy: true,
      pendingConnectionId: connection.id,
    })

    for (const button of screen.getAllByRole('button')) {
      expect(button).toBeDisabled()
    }
    expect(screen.getByRole('listitem')).toHaveAttribute('aria-busy', 'true')
  })

  // Review Focus 5.
  it('leaves out a connection whose provider left the catalog', () => {
    renderList({
      connections: [integrationConnection({ providerKey: 'ghost', name: 'Fantasma' })],
    })

    expect(screen.queryByText('Fantasma')).toBeNull()
    expect(screen.getByText('Todavía no tienes conexiones.')).toBeInTheDocument()
  })

  it('says so when there are no connections', () => {
    renderList({ connections: [] })

    expect(screen.getByText('Todavía no tienes conexiones.')).toBeInTheDocument()
  })

  it('says when it was last verified', () => {
    renderList()

    expect(screen.getByText('Verificada el 8 de octubre')).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: Correrlas y verlas fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/components/provider-catalog.test.tsx src/features/integrations/components/connection-list.test.tsx
```

Expected: FAIL — `Failed to resolve import "./provider-catalog"` y `"./connection-list"`.

- [ ] **Step 3: Escribir el badge**

`src/features/integrations/components/connection-status-badge.tsx`:

```tsx
import {
  CircleCheck,
  CirclePause,
  TriangleAlert,
  type LucideIcon,
} from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import {
  CONNECTION_STATUS_LABELS,
  type ConnectionStatus,
} from '@/features/integrations/types/integrations'
import { cn } from '@/lib/utils'

/** The icon repeats the meaning so the status never depends on color alone. */
const TONES: Record<ConnectionStatus, { icon: LucideIcon; className: string }> = {
  Active: {
    icon: CircleCheck,
    className: 'border-transparent bg-accent text-accent-foreground',
  },
  Paused: {
    icon: CirclePause,
    className: 'border-border bg-muted text-muted-foreground',
  },
  NeedsAttention: {
    icon: TriangleAlert,
    className: 'border-warning-border bg-warning-surface text-warning-foreground',
  },
}

export function ConnectionStatusBadge({ status }: { status: ConnectionStatus }) {
  // A status this build does not know falls back to the neutral tone and its raw name.
  const tone = TONES[status] ?? TONES.Paused
  const Icon = tone.icon
  return (
    <Badge variant="outline" className={cn(tone.className)}>
      <Icon aria-hidden="true" />
      {CONNECTION_STATUS_LABELS[status] ?? status}
    </Badge>
  )
}
```

- [ ] **Step 4: Escribir el catálogo**

`src/features/integrations/components/provider-catalog.tsx`:

```tsx
import { useId } from 'react'

import { Plug } from 'lucide-react'

import { Button } from '@/components/ui/button'
import {
  CATEGORY_LABELS,
  type IntegrationProvider,
  type ProviderCategory,
} from '@/features/integrations/types/integrations'

export interface ProviderCatalogProps {
  providers: IntegrationProvider[]
  /** `integrations.connection.manage`. Without it the catalog is read-only. */
  canManage: boolean
  /** A write is in flight. */
  disabled: boolean
  onConnect: (provider: IntegrationProvider) => void
}

/** The providers visible to the tenant (the backend filters them by its active modules), by category. */
export function ProviderCatalog({
  providers,
  canManage,
  disabled,
  onConnect,
}: ProviderCatalogProps) {
  if (providers.length === 0) {
    return (
      <p className="text-sm text-muted-foreground">
        Ningún módulo de tu plan usa integraciones por ahora.
      </p>
    )
  }

  // In the order the backend sends them.
  const categories = [...new Set(providers.map((provider) => provider.category))]

  return (
    <div className="space-y-5">
      {categories.map((category) => (
        <CategoryGroup
          key={category}
          category={category}
          providers={providers.filter((provider) => provider.category === category)}
          canManage={canManage}
          disabled={disabled}
          onConnect={onConnect}
        />
      ))}
    </div>
  )
}

function CategoryGroup({
  category,
  providers,
  canManage,
  disabled,
  onConnect,
}: Omit<ProviderCatalogProps, 'providers'> & {
  category: ProviderCategory
  providers: IntegrationProvider[]
}) {
  const headingId = useId()
  return (
    <section aria-labelledby={headingId} className="space-y-2">
      <h3 id={headingId} className="text-sm font-medium text-muted-foreground">
        {/* A category this build does not know keeps its own name instead of disappearing. */}
        {CATEGORY_LABELS[category] ?? category}
      </h3>
      <ul className="grid gap-2 sm:grid-cols-2">
        {providers.map((provider) => (
          <ProviderCard
            key={provider.key}
            provider={provider}
            canManage={canManage}
            disabled={disabled}
            onConnect={onConnect}
          />
        ))}
      </ul>
    </section>
  )
}

function ProviderCard({
  provider,
  canManage,
  disabled,
  onConnect,
}: Omit<ProviderCatalogProps, 'providers'> & { provider: IntegrationProvider }) {
  // Said before anything is typed: otherwise the limit only shows up as a 422 after the whole form.
  const atLimit = provider.connectionCount >= provider.maxConnections
  return (
    <li className="flex items-center gap-3 rounded-xl border border-border bg-background p-4">
      <span className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-muted text-muted-foreground">
        <Plug className="size-5" aria-hidden="true" />
      </span>
      <div className="min-w-0 flex-1 space-y-0.5">
        <p className="text-sm font-medium">{provider.displayName}</p>
        <p className="text-xs text-muted-foreground">
          {atLimit
            ? `Llegaste al máximo de ${provider.maxConnections} conexiones.`
            : `${provider.connectionCount} de ${provider.maxConnections} conexiones`}
        </p>
      </div>
      {canManage ? (
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={disabled || atLimit}
          aria-label={`Conectar ${provider.displayName}`}
          onClick={() => onConnect(provider)}
        >
          Conectar
        </Button>
      ) : null}
    </li>
  )
}
```

- [ ] **Step 5: Escribir la lista**

`src/features/integrations/components/connection-list.tsx`:

```tsx
import { useId } from 'react'

import { Pause, Pencil, Play, RefreshCw, Trash2 } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { ConnectionStatusBadge } from '@/features/integrations/components/connection-status-badge'
import type {
  IntegrationConnection,
  IntegrationProvider,
} from '@/features/integrations/types/integrations'
import { formatDayAndMonth } from '@/features/integrations/utils/integration-dates'

export interface ConnectionListProps {
  connections: IntegrationConnection[]
  providers: IntegrationProvider[]
  /** `integrations.connection.manage`. Without it the rows have no actions. */
  canManage: boolean
  /** A write is in flight: one at a time. */
  busy: boolean
  pendingConnectionId: string | null
  onEdit: (connection: IntegrationConnection) => void
  onTest: (connection: IntegrationConnection) => void
  onPause: (connection: IntegrationConnection) => void
  onResume: (connection: IntegrationConnection) => void
  onDelete: (connection: IntegrationConnection) => void
}

type RowProps = Omit<ConnectionListProps, 'connections' | 'providers'>

/**
 * The tenant's connections under the provider they belong to, in catalog order. A connection
 * whose provider is not in the catalog is left out: the backend hides those too (spec
 * «Visibilidad»), and without its field definitions there is nothing to edit.
 */
export function ConnectionList({
  connections,
  providers,
  ...row
}: ConnectionListProps) {
  const groups = providers
    .map((provider) => ({
      provider,
      connections: connections.filter(
        (connection) => connection.providerKey === provider.key,
      ),
    }))
    .filter((group) => group.connections.length > 0)

  if (groups.length === 0) {
    return (
      <p className="text-sm text-muted-foreground">
        Todavía no tienes conexiones.
      </p>
    )
  }

  return (
    <div className="space-y-5">
      {groups.map((group) => (
        <ProviderGroup
          key={group.provider.key}
          provider={group.provider}
          connections={group.connections}
          {...row}
        />
      ))}
    </div>
  )
}

function ProviderGroup({
  provider,
  connections,
  ...row
}: RowProps & {
  provider: IntegrationProvider
  connections: IntegrationConnection[]
}) {
  const headingId = useId()
  return (
    <section aria-labelledby={headingId} className="space-y-2">
      <h3 id={headingId} className="text-sm font-medium text-muted-foreground">
        {provider.displayName}
      </h3>
      <ul className="space-y-2">
        {connections.map((connection) => (
          <ConnectionRow key={connection.id} connection={connection} {...row} />
        ))}
      </ul>
    </section>
  )
}

function ConnectionRow({
  connection,
  canManage,
  busy,
  pendingConnectionId,
  onEdit,
  onTest,
  onPause,
  onResume,
  onDelete,
}: RowProps & { connection: IntegrationConnection }) {
  const { name } = connection
  return (
    <li
      aria-busy={pendingConnectionId === connection.id ? true : undefined}
      className="flex flex-wrap items-center gap-3 rounded-xl border border-border bg-background p-4"
    >
      <div className="min-w-0 flex-1 space-y-1">
        <div className="flex flex-wrap items-center gap-2">
          <p className="truncate text-sm font-medium">{name}</p>
          <ConnectionStatusBadge status={connection.status} />
        </div>
        {connection.lastVerifiedAt ? (
          <p className="text-xs text-muted-foreground">
            {`Verificada el ${formatDayAndMonth(connection.lastVerifiedAt)}`}
          </p>
        ) : null}
      </div>
      {canManage ? (
        // Each action names its row: a list of identical «Pausar» buttons reads as noise to a screen reader.
        <div className="flex flex-wrap gap-1">
          <Button type="button" variant="ghost" size="sm" disabled={busy} aria-label={`Editar ${name}`} onClick={() => onEdit(connection)}>
            <Pencil aria-hidden="true" />
            Editar
          </Button>
          <Button type="button" variant="ghost" size="sm" disabled={busy} aria-label={`Probar ${name}`} onClick={() => onTest(connection)}>
            <RefreshCw aria-hidden="true" />
            Probar
          </Button>
          {connection.status === 'Active' ? (
            <Button type="button" variant="ghost" size="sm" disabled={busy} aria-label={`Pausar ${name}`} onClick={() => onPause(connection)}>
              <Pause aria-hidden="true" />
              Pausar
            </Button>
          ) : null}
          {connection.status === 'Paused' ? (
            <Button type="button" variant="ghost" size="sm" disabled={busy} aria-label={`Reanudar ${name}`} onClick={() => onResume(connection)}>
              <Play aria-hidden="true" />
              Reanudar
            </Button>
          ) : null}
          <Button type="button" variant="destructive" size="sm" disabled={busy} aria-label={`Eliminar ${name}`} onClick={() => onDelete(connection)}>
            <Trash2 aria-hidden="true" />
            Eliminar
          </Button>
        </div>
      ) : null}
    </li>
  )
}
```

- [ ] **Step 6: Correrlas y verlas pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/components/provider-catalog.test.tsx src/features/integrations/components/connection-list.test.tsx
```

Expected: PASS — `Test Files  2 passed (2)`, `Tests  17 passed (17)` (6 + 11).

- [ ] **Step 7: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
$files = @(
  'src/features/integrations/components/connection-status-badge.tsx',
  'src/features/integrations/components/provider-catalog.tsx',
  'src/features/integrations/components/provider-catalog.test.tsx',
  'src/features/integrations/components/connection-list.tsx',
  'src/features/integrations/components/connection-list.test.tsx'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(integrations): catálogo de proveedores y lista de conexiones

Proveedores por categoría con «Conectar» deshabilitado en el tope, y
conexiones por proveedor con su badge (Activa, Pausada, Necesita atención)
y las acciones que su estado admite. Una conexión cuyo proveedor salió del
catálogo no se dibuja, igual que la oculta el backend.
'@
$msgFile = Join-Path $env:TEMP 'qep-integraciones-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

---

### Task 8: Página de Integraciones

**Files:**
- Create: `src/features/integrations/pages/integrations-page.tsx`
- Test: `src/features/integrations/pages/integrations-page.test.tsx`

**Interfaces:**
- Consumes: `ConnectionFormDialog` (Task 5), `DeleteConnectionDialog` (Task 6), `ProviderCatalog`, `ConnectionList` (Task 7), `describeIntegrationFailure`, `INTEGRATION_FAILURE_MESSAGES` (Task 2), `IntegrationsQueryStatus` (Task 4), `PageContainer`, `Link`.
- Produces: `IntegrationsPageProps { status: IntegrationsQueryStatus; providers; connections; canManage; isSaving; pendingConnectionId; onRetry: () => void; onCreate: (input: CreateConnectionInput) => Promise<IntegrationConnection>; onUpdate: (connectionId: string, input: UpdateConnectionInput) => Promise<IntegrationConnection>; onTest, onPause, onResume: (connection) => Promise<IntegrationConnection>; onDelete: (connection) => Promise<void> }` — las mismas firmas que `useConnectionMutations`, para que la ruta las pase sin adaptar. `IntegrationsPage` (vista sin hooks de datos; sólo estado de UI: diálogo abierto y aviso).

- [ ] **Step 1: Escribir la prueba**

`src/features/integrations/pages/integrations-page.test.tsx`:

```tsx
import type { ReactNode } from 'react'

import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { IntegrationsPage, type IntegrationsPageProps } from './integrations-page'
import { INTEGRATION_ERROR_MESSAGES } from '@/features/integrations/services/integrations.api'
import { ApiError } from '@/lib/api-client'
import { ZENVIA_PROVIDER, integrationConnection } from '@/test/integrations'

// `Link` needs a mounted router; these tests exercise the view, not navigation. The real target
// is covered by `routes/_authenticated/settings/integrations.test.tsx`. Same mock as
// `tenant-settings-page.test.tsx:11-33`.
vi.mock('@tanstack/react-router', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-router')>()),
  Link: ({
    to,
    children,
    ...rest
  }: {
    to: string
    children?: ReactNode
    className?: string
  }) => (
    <a href={to} {...rest}>
      {children}
    </a>
  ),
}))

function renderPage(overrides: Partial<IntegrationsPageProps> = {}) {
  const props: IntegrationsPageProps = {
    status: 'ready',
    providers: [ZENVIA_PROVIDER],
    connections: [integrationConnection()],
    canManage: true,
    isSaving: false,
    pendingConnectionId: null,
    onRetry: vi.fn(),
    onCreate: vi
      .fn()
      .mockResolvedValue(integrationConnection({ id: 'c-new', name: 'Sede sur' })),
    onUpdate: vi.fn().mockResolvedValue(integrationConnection()),
    onTest: vi.fn().mockResolvedValue(integrationConnection()),
    onPause: vi
      .fn()
      .mockResolvedValue(integrationConnection({ status: 'Paused' })),
    onResume: vi.fn().mockResolvedValue(integrationConnection()),
    onDelete: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  }
  render(<IntegrationsPage {...props} />)
  return { props, user: userEvent.setup() }
}

describe('IntegrationsPage', () => {
  it('says it is loading', () => {
    renderPage({ status: 'loading' })

    expect(screen.getByText('Cargando integraciones…')).toBeInTheDocument()
  })

  it('says the person cannot see the integrations', () => {
    renderPage({ status: 'denied' })

    expect(
      screen.getByText(
        'No tienes permiso para ver las integraciones de esta organización.',
      ),
    ).toBeInTheDocument()
  })

  it('offers to retry a failed read', async () => {
    const { props, user } = renderPage({ status: 'error' })

    await user.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(props.onRetry).toHaveBeenCalled()
  })

  it('with an empty catalog says no module uses integrations and offers nothing to connect', () => {
    renderPage({ providers: [], connections: [] })

    expect(
      screen.getByText('Ningún módulo de tu plan usa integraciones por ahora.'),
    ).toBeInTheDocument()
    expect(screen.getByText('Todavía no tienes conexiones.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Conectar/ })).toBeNull()
  })

  it('lists the connections and the providers', () => {
    renderPage()

    expect(
      screen.getByRole('heading', { level: 1, name: 'Integraciones' }),
    ).toBeInTheDocument()
    const connections = screen.getByRole('region', { name: 'Tus conexiones' })
    expect(within(connections).getByText('WhatsApp sede norte')).toBeInTheDocument()
    expect(within(connections).getByText('Activa')).toBeInTheDocument()
    const catalog = screen.getByRole('region', { name: 'Proveedores disponibles' })
    expect(
      within(catalog).getByRole('button', { name: 'Conectar Zenvia (WhatsApp)' }),
    ).toBeEnabled()
    expect(screen.getByRole('link', { name: 'Configuración' })).toHaveAttribute(
      'href',
      '/settings',
    )
  })

  it('is read-only without the manage permission', () => {
    renderPage({ canManage: false })

    expect(
      screen.getByText(
        'No tienes permiso para gestionar integraciones: las ves en modo lectura.',
      ),
    ).toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: /^(Conectar|Editar|Probar|Pausar|Eliminar)/ }),
    ).toBeNull()
    expect(screen.getByText('WhatsApp sede norte')).toBeInTheDocument()
  })

  it('connects a provider from the catalog and says so', async () => {
    const { props, user } = renderPage({ connections: [] })

    await user.click(
      screen.getByRole('button', { name: 'Conectar Zenvia (WhatsApp)' }),
    )
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText('Nombre de la conexión'), 'Sede sur')
    await user.type(within(dialog).getByLabelText('API token'), 'zenvia-token')
    await user.type(within(dialog).getByLabelText('Número emisor'), '573009876543')
    await user.click(within(dialog).getByRole('button', { name: 'Conectar' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(props.onCreate).toHaveBeenCalledWith({
      providerKey: 'zenvia',
      name: 'Sede sur',
      fields: { fromNumber: '573009876543' },
      secrets: { apiToken: 'zenvia-token' },
    })
    expect(screen.getByRole('status')).toHaveTextContent('Conectaste «Sede sur».')
  })

  it('edits a connection with its stored values and the version it opened with', async () => {
    const connection = integrationConnection()
    const { props, user } = renderPage({ connections: [connection] })

    await user.click(
      screen.getByRole('button', { name: 'Editar WhatsApp sede norte' }),
    )
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByLabelText('Nombre de la conexión')).toHaveValue(
      'WhatsApp sede norte',
    )
    await user.click(within(dialog).getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(props.onUpdate).toHaveBeenCalledWith(connection.id, {
        name: 'WhatsApp sede norte',
        fields: { fromNumber: '573001234567' },
        secrets: {},
        version: 3,
      }),
    )
  })

  it('pauses and says so', async () => {
    const connection = integrationConnection()
    const { props, user } = renderPage({ connections: [connection] })

    await user.click(
      screen.getByRole('button', { name: 'Pausar WhatsApp sede norte' }),
    )

    expect(props.onPause).toHaveBeenCalledWith(connection)
    expect(await screen.findByRole('status')).toHaveTextContent(
      'Pausaste «WhatsApp sede norte».',
    )
  })

  it('explains a failed action instead of hiding it', async () => {
    const { user } = renderPage({
      onPause: vi.fn().mockRejectedValue(
        new ApiError(422, { code: 'integrations.connection.not_active' }),
      ),
    })

    await user.click(
      screen.getByRole('button', { name: 'Pausar WhatsApp sede norte' }),
    )

    expect(await screen.findByRole('alert')).toHaveTextContent(
      INTEGRATION_ERROR_MESSAGES['integrations.connection.not_active'],
    )
  })

  // F4: the test always answers 200; the outcome comes from lastVerifiedAt vs lastFailureAt, not
  // from status (an unreachable provider leaves the connection Active).
  it.each([
    {
      label: 'rejected on an Active connection',
      connection: {
        status: 'NeedsAttention' as const,
        lastFailureAt: '2026-10-09T10:00:00Z',
        lastFailureCode: 'credentials_rejected',
      },
      text: 'El proveedor rechazó la credencial de «WhatsApp sede norte». Edítala y pega una nueva.',
    },
    {
      label: 'unreachable, status unchanged',
      connection: {
        status: 'Active' as const,
        lastFailureAt: '2026-10-09T10:00:00Z',
        lastFailureCode: 'provider_unreachable',
      },
      text: 'No pudimos verificar «WhatsApp sede norte» con el proveedor. Inténtalo de nuevo en unos minutos.',
    },
    {
      label: 'rejected on a Paused connection',
      connection: {
        status: 'Paused' as const,
        lastFailureAt: '2026-10-09T10:00:00Z',
        lastFailureCode: 'credentials_rejected',
      },
      text: 'El proveedor rechazó la credencial de «WhatsApp sede norte». Edítala y pega una nueva.',
    },
  ])('reports a failed test: $label', async ({ connection, text }) => {
    const { user } = renderPage({
      onTest: vi.fn().mockResolvedValue(integrationConnection(connection)),
    })

    await user.click(
      screen.getByRole('button', { name: 'Probar WhatsApp sede norte' }),
    )

    expect(await screen.findByRole('alert')).toHaveTextContent(text)
  })

  it('reports a passing test even after an older failure', async () => {
    const { user } = renderPage({
      onTest: vi.fn().mockResolvedValue(
        integrationConnection({
          lastVerifiedAt: '2026-10-09T11:00:00Z',
          lastFailureAt: '2026-10-09T10:00:00Z',
          lastFailureCode: 'provider_unreachable',
        }),
      ),
    })

    await user.click(
      screen.getByRole('button', { name: 'Probar WhatsApp sede norte' }),
    )

    expect(await screen.findByRole('status')).toHaveTextContent(
      'La conexión «WhatsApp sede norte» funciona.',
    )
  })

  it('deletes only after typing the exact name, and says so', async () => {
    const connection = integrationConnection()
    const { props, user } = renderPage({ connections: [connection] })

    await user.click(
      screen.getByRole('button', { name: 'Eliminar WhatsApp sede norte' }),
    )
    const dialog = await screen.findByRole('dialog')
    await user.type(
      within(dialog).getByLabelText('Nombre de la conexión'),
      'WhatsApp sede norte',
    )
    await user.click(within(dialog).getByRole('button', { name: 'Eliminar' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(props.onDelete).toHaveBeenCalledWith(connection)
    expect(screen.getByRole('status')).toHaveTextContent(
      'Eliminaste «WhatsApp sede norte».',
    )
  })
})
```

- [ ] **Step 2: Correrla y verla fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/pages/integrations-page.test.tsx
```

Expected: FAIL — `Failed to resolve import "./integrations-page"`.

- [ ] **Step 3: Escribir la página**

`src/features/integrations/pages/integrations-page.tsx`:

```tsx
import { useId, useState } from 'react'

import { Link } from '@tanstack/react-router'
import { Lock } from 'lucide-react'

import { PageContainer } from '@/components/page-container'
import { Button } from '@/components/ui/button'
import { ConnectionFormDialog } from '@/features/integrations/components/connection-form-dialog'
import { ConnectionList } from '@/features/integrations/components/connection-list'
import { DeleteConnectionDialog } from '@/features/integrations/components/delete-connection-dialog'
import { ProviderCatalog } from '@/features/integrations/components/provider-catalog'
import { INTEGRATION_FAILURE_MESSAGES } from '@/features/integrations/services/integrations.api'
import type {
  CreateConnectionInput,
  IntegrationConnection,
  IntegrationProvider,
  UpdateConnectionInput,
} from '@/features/integrations/types/integrations'
import { describeIntegrationFailure } from '@/features/integrations/utils/describe-integration-failure'
import type { IntegrationsQueryStatus } from '@/features/integrations/utils/integrations-query-status'
import { cn } from '@/lib/utils'

export interface IntegrationsPageProps {
  status: IntegrationsQueryStatus
  providers: IntegrationProvider[]
  connections: IntegrationConnection[]
  /** `integrations.connection.manage`. Without it the screen is read-only. */
  canManage: boolean
  isSaving: boolean
  pendingConnectionId: string | null
  onRetry: () => void
  onCreate: (input: CreateConnectionInput) => Promise<IntegrationConnection>
  onUpdate: (
    connectionId: string,
    input: UpdateConnectionInput,
  ) => Promise<IntegrationConnection>
  onTest: (connection: IntegrationConnection) => Promise<IntegrationConnection>
  onPause: (connection: IntegrationConnection) => Promise<IntegrationConnection>
  onResume: (connection: IntegrationConnection) => Promise<IntegrationConnection>
  onDelete: (connection: IntegrationConnection) => Promise<void>
}

/**
 * Frozen when opened: the lists refetch underneath, and the version sent in `If-Match` must be the
 * one the person saw (same rule as `EditMemberDialog`'s snapshot).
 */
type OpenDialog =
  | { kind: 'create'; provider: IntegrationProvider }
  | {
      kind: 'edit'
      provider: IntegrationProvider
      connection: IntegrationConnection
    }
  | { kind: 'delete'; connection: IntegrationConnection }

interface Notice {
  tone: 'success' | 'error'
  text: string
}

/**
 * What a test said (F4, settled with the backend plan): the test always answers 200 with the
 * connection. It passed when `lastVerifiedAt` is newer than `lastFailureAt` — the backend never
 * clears `lastFailure*` on success, so `status` alone cannot tell: an unreachable provider leaves
 * an Active connection Active, and a rejection on a Paused one leaves it Paused.
 */
function testOutcome(connection: IntegrationConnection): Notice {
  const verified = connection.lastVerifiedAt
    ? Date.parse(connection.lastVerifiedAt)
    : Number.NEGATIVE_INFINITY
  const failed = connection.lastFailureAt
    ? Date.parse(connection.lastFailureAt)
    : Number.NEGATIVE_INFINITY
  if (verified > failed) {
    return connection.status === 'Paused'
      ? {
          tone: 'success',
          text: `La credencial de «${connection.name}» funciona. La conexión sigue pausada.`,
        }
      : { tone: 'success', text: `La conexión «${connection.name}» funciona.` }
  }
  if (connection.lastFailureCode === 'credentials_rejected') {
    return {
      tone: 'error',
      text: `El proveedor rechazó la credencial de «${connection.name}». Edítala y pega una nueva.`,
    }
  }
  return {
    tone: 'error',
    text: `No pudimos verificar «${connection.name}» con el proveedor. Inténtalo de nuevo en unos minutos.`,
  }
}

/**
 * Ajustes → Integraciones (spec «Frontend»). A view: the route resolves the data and the writes;
 * this only holds which dialog is open and the last notice.
 */
export function IntegrationsPage({
  status,
  providers,
  connections,
  canManage,
  isSaving,
  pendingConnectionId,
  onRetry,
  onCreate,
  onUpdate,
  onTest,
  onPause,
  onResume,
  onDelete,
}: IntegrationsPageProps) {
  const connectionsTitleId = useId()
  const catalogTitleId = useId()
  const [dialog, setDialog] = useState<OpenDialog | null>(null)
  const [notice, setNotice] = useState<Notice | null>(null)

  if (status === 'loading') {
    return (
      <PageContainer>
        <p className="text-sm text-muted-foreground">Cargando integraciones…</p>
      </PageContainer>
    )
  }

  if (status === 'denied') {
    return (
      <PageContainer>
        <p role="alert" className="text-sm">
          No tienes permiso para ver las integraciones de esta organización.
        </p>
      </PageContainer>
    )
  }

  if (status === 'error') {
    return (
      <PageContainer>
        <p role="alert" className="text-sm">
          No pudimos cargar las integraciones. Intenta de nuevo en un momento.
        </p>
        <Button type="button" variant="outline" className="mt-3" onClick={onRetry}>
          Reintentar
        </Button>
      </PageContainer>
    )
  }

  const runRowAction = async (action: () => Promise<Notice>) => {
    setNotice(null)
    try {
      setNotice(await action())
    } catch (error) {
      setNotice({
        tone: 'error',
        text:
          describeIntegrationFailure(error).message ??
          INTEGRATION_FAILURE_MESSAGES.generic,
      })
    }
  }

  const openEdit = (connection: IntegrationConnection) => {
    const provider = providers.find((item) => item.key === connection.providerKey)
    if (!provider) return
    setNotice(null)
    setDialog({ kind: 'edit', provider, connection })
  }

  return (
    <PageContainer variant="form" className="flex flex-col gap-6">
      {/* Reached only from Configuración; it has no sidebar entry. */}
      <Link
        to="/settings"
        className="self-start text-sm font-medium text-primary underline-offset-4 hover:underline"
      >
        <span aria-hidden="true">← </span>
        Configuración
      </Link>
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Integraciones</h1>
        <p className="text-sm text-muted-foreground">
          Conecta las cuentas de tu empresa con otras plataformas. Cada
          credencial se prueba con el proveedor antes de guardarse y nunca se
          vuelve a mostrar.
        </p>
      </header>

      {!canManage ? (
        <p
          role="status"
          className="flex items-start gap-2 rounded-xl border border-border bg-muted px-4 py-3 text-sm text-muted-foreground"
        >
          <Lock className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
          No tienes permiso para gestionar integraciones: las ves en modo
          lectura.
        </p>
      ) : null}

      {notice ? (
        <p
          role={notice.tone === 'error' ? 'alert' : 'status'}
          className={cn(
            'rounded-xl border px-4 py-3 text-sm',
            notice.tone === 'error'
              ? 'border-destructive/40 text-destructive'
              : 'border-border text-foreground',
          )}
        >
          {notice.text}
        </p>
      ) : null}

      <section aria-labelledby={connectionsTitleId} className="space-y-3">
        <h2 id={connectionsTitleId} className="text-base font-semibold">
          Tus conexiones
        </h2>
        <ConnectionList
          connections={connections}
          providers={providers}
          canManage={canManage}
          busy={isSaving}
          pendingConnectionId={pendingConnectionId}
          onEdit={openEdit}
          onTest={(connection) =>
            void runRowAction(async () => testOutcome(await onTest(connection)))
          }
          onPause={(connection) =>
            void runRowAction(async () => {
              const paused = await onPause(connection)
              return { tone: 'success', text: `Pausaste «${paused.name}».` }
            })
          }
          onResume={(connection) =>
            void runRowAction(async () => {
              const resumed = await onResume(connection)
              return { tone: 'success', text: `Reanudaste «${resumed.name}».` }
            })
          }
          onDelete={(connection) => {
            setNotice(null)
            setDialog({ kind: 'delete', connection })
          }}
        />
      </section>

      <section aria-labelledby={catalogTitleId} className="space-y-3">
        <h2 id={catalogTitleId} className="text-base font-semibold">
          Proveedores disponibles
        </h2>
        <ProviderCatalog
          providers={providers}
          canManage={canManage}
          disabled={isSaving}
          onConnect={(provider) => {
            setNotice(null)
            setDialog({ kind: 'create', provider })
          }}
        />
      </section>

      {dialog?.kind === 'create' ? (
        <ConnectionFormDialog
          provider={dialog.provider}
          connection={null}
          onClose={() => setDialog(null)}
          onSubmit={async (payload) => {
            const saved = await onCreate({
              providerKey: dialog.provider.key,
              ...payload,
            })
            setNotice({ tone: 'success', text: `Conectaste «${saved.name}».` })
          }}
        />
      ) : null}

      {dialog?.kind === 'edit' ? (
        <ConnectionFormDialog
          provider={dialog.provider}
          connection={dialog.connection}
          onClose={() => setDialog(null)}
          onSubmit={async (payload) => {
            const saved = await onUpdate(dialog.connection.id, {
              ...payload,
              version: dialog.connection.version,
            })
            setNotice({ tone: 'success', text: `Guardaste «${saved.name}».` })
          }}
        />
      ) : null}

      {dialog?.kind === 'delete' ? (
        <DeleteConnectionDialog
          connection={dialog.connection}
          onClose={() => setDialog(null)}
          onConfirm={async () => {
            await onDelete(dialog.connection)
            setNotice({
              tone: 'success',
              text: `Eliminaste «${dialog.connection.name}».`,
            })
          }}
        />
      ) : null}
    </PageContainer>
  )
}
```

- [ ] **Step 4: Correrla y verla pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/features/integrations/pages/integrations-page.test.tsx
```

Expected: PASS — `Tests  12 passed (12)`.

- [ ] **Step 5: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
$files = @(
  'src/features/integrations/pages/integrations-page.tsx',
  'src/features/integrations/pages/integrations-page.test.tsx'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(integrations): página de Integraciones

Carga, sin permiso, error con reintento, catálogo vacío y modo lectura;
conectar, editar con la versión con que se abrió, probar, pausar,
reanudar y eliminar, cada uno con su aviso de éxito o su motivo de falla.
'@
$msgFile = Join-Path $env:TEMP 'qep-integraciones-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

---

### Task 9: Ruta `/settings/integrations` y tarjeta en Configuración

**Files:**
- Create: `src/routes/_authenticated/settings/integrations.tsx`
- Test: `src/routes/_authenticated/settings/integrations.test.tsx`
- Regenerate: `src/routeTree.gen.ts`
- Modify: `src/features/tenant-settings/pages/tenant-settings-page.tsx` (props `:17-36`, destructuring `:42-53`, imports `:1-2`, JSX antes del cierre `</PageContainer>` `:174`)
- Modify: `src/features/tenant-settings/pages/tenant-settings-page.test.tsx` (dos pruebas al final del `describe`)
- Modify: `src/routes/_authenticated/settings/index.tsx` (`:3-8` imports, `:36-51` JSX)
- Modify: `src/routes/_authenticated/settings/index.test.tsx` (`stubBackend` `:32-48` y dos pruebas)

**Interfaces:**
- Consumes: `usePermissions` (`can`, `status`), los tres hooks y `integrationsAccess` / `integrationsPageStatus` (Task 4), `IntegrationsPage` (Task 8), `INTEGRATIONS_PERMISSIONS` (Task 1).
- Produces: `Route` en `/_authenticated/settings/integrations`; prop `showIntegrations?: boolean` (default `false`) en `TenantSettingsPage`, con un enlace cuyo nombre accesible es «Conexiones con plataformas externas» y `href` `/settings/integrations`.

- [ ] **Step 1: Escribir las pruebas**

`src/routes/_authenticated/settings/integrations.test.tsx`:

```tsx
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import type { IntegrationConnection } from '@/features/integrations/types/integrations'
import {
  INTEGRATIONS_TENANT,
  ZENVIA_PROVIDER,
  integrationConnection,
  jsonResponse,
} from '@/test/integrations'
import { renderRoute } from '@/test/render-route'

const session = {
  userId: 'u-1',
  email: 'owner@qcode.co',
  activeTenantIds: [INTEGRATIONS_TENANT],
}

const READ_AND_MANAGE = [
  'tenancy.settings.read',
  'integrations.connection.read',
  'integrations.connection.manage',
]

interface RecordedRequest {
  url: string
  method: string
  body: unknown
}

/** Session, permissions and the integrations endpoints for real; everything else gets `{}`. */
function stubBackend({
  permissions = READ_AND_MANAGE,
  connections = [integrationConnection()],
}: { permissions?: string[]; connections?: IntegrationConnection[] } = {}) {
  const requests: RecordedRequest[] = []
  let items = connections
  vi.mocked(fetch).mockImplementation((input, init) => {
    const url = String(input)
    const method = init?.method ?? 'GET'
    requests.push({
      url,
      method,
      body: init?.body ? (JSON.parse(String(init.body)) as unknown) : undefined,
    })
    if (url.includes('/auth/me')) return Promise.resolve(jsonResponse(200, session))
    if (url.includes('/authorization/me')) {
      return Promise.resolve(
        jsonResponse(200, {
          tenantId: INTEGRATIONS_TENANT,
          userId: 'u-1',
          permissions,
        }),
      )
    }
    if (url.endsWith('/integrations/catalog')) {
      return Promise.resolve(
        jsonResponse(200, {
          providers: [{ ...ZENVIA_PROVIDER, connectionCount: items.length }],
        }),
      )
    }
    if (url.endsWith('/integrations/connections') && method === 'POST') {
      const created = integrationConnection({
        id: '019fc000-0000-7000-8000-000000000002',
        name: 'Sede sur',
        fields: { fromNumber: '573009876543' },
      })
      items = [...items, created]
      return Promise.resolve(jsonResponse(201, created))
    }
    if (url.endsWith('/integrations/connections')) {
      return Promise.resolve(jsonResponse(200, { items }))
    }
    return Promise.resolve(jsonResponse(200, {}))
  })
  return { requests }
}

describe('/settings/integrations', () => {
  it('reads the catalog and the connections of the active tenant', async () => {
    stubBackend()

    renderRoute('/settings/integrations')

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Integraciones' }),
    ).toBeInTheDocument()
    expect(await screen.findByText('WhatsApp sede norte')).toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: 'Conectar Zenvia (WhatsApp)' }),
    ).toBeEnabled()
  })

  it('asks nothing about integrations without integrations.connection.read', async () => {
    const { requests } = stubBackend({ permissions: ['tenancy.settings.read'] })

    renderRoute('/settings/integrations')

    expect(
      await screen.findByText(
        'No tienes permiso para ver las integraciones de esta organización.',
      ),
    ).toBeInTheDocument()
    expect(requests.some((request) => request.url.includes('/integrations/'))).toBe(
      false,
    )
  })

  it('connects a provider and shows the new connection once the lists reload', async () => {
    const user = userEvent.setup()
    const { requests } = stubBackend({ connections: [] })

    renderRoute('/settings/integrations')

    await user.click(
      await screen.findByRole('button', { name: 'Conectar Zenvia (WhatsApp)' }),
    )
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText('Nombre de la conexión'), 'Sede sur')
    await user.type(within(dialog).getByLabelText('API token'), 'zenvia-token')
    await user.type(within(dialog).getByLabelText('Número emisor'), '573009876543')
    await user.click(within(dialog).getByRole('button', { name: 'Conectar' }))

    expect(await screen.findByText('Conectaste «Sede sur».')).toBeInTheDocument()
    expect(screen.getByText('Sede sur')).toBeInTheDocument()
    expect(screen.getByText('1 de 20 conexiones')).toBeInTheDocument()
    expect(requests.find((request) => request.method === 'POST')).toMatchObject({
      url: `/api/v1/tenants/${INTEGRATIONS_TENANT}/integrations/connections`,
      body: {
        providerKey: 'zenvia',
        name: 'Sede sur',
        fields: { fromNumber: '573009876543' },
        secrets: { apiToken: 'zenvia-token' },
      },
    })
  })
})
```

En `src/routes/_authenticated/settings/index.test.tsx`, reemplaza `stubBackend` (`:32-48`) por esta versión, que agrega `permissions` con el mismo default de hoy:

```tsx
function stubBackend(
  modules: unknown = {},
  permissions: string[] = ['tenancy.settings.read', 'tenancy.settings.update'],
) {
  vi.mocked(fetch).mockImplementation((input) => {
    const url = String(input)
    if (url.includes('/auth/me')) return Promise.resolve(json(200, session))
    if (url.includes('/authorization/me'))
      return Promise.resolve(
        json(200, {
          tenantId: TENANT,
          userId: 'u-1',
          permissions,
        }),
      )
    if (url.endsWith('/settings')) return Promise.resolve(json(200, settings))
    if (url.endsWith('/modules')) return Promise.resolve(json(200, modules))
    return Promise.resolve(json(200, {}))
  })
}
```

Y agrega al final del `describe('/settings', ...)`:

```tsx
  it('links to the integrations page with integrations.connection.read', async () => {
    stubBackend({}, [
      'tenancy.settings.read',
      'tenancy.settings.update',
      'integrations.connection.read',
    ])

    renderRoute('/settings')

    expect(
      await screen.findByRole('link', {
        name: 'Conexiones con plataformas externas',
      }),
    ).toHaveAttribute('href', '/settings/integrations')
  })

  it('hides the integrations card without integrations.connection.read', async () => {
    stubBackend()

    renderRoute('/settings')

    // Same moment the page is ready: the orders export link is drawn with the modules unknown.
    expect(
      await screen.findByRole('link', { name: 'Columnas del Excel de pedidos' }),
    ).toBeInTheDocument()
    expect(
      screen.queryByRole('link', { name: 'Conexiones con plataformas externas' }),
    ).toBeNull()
  })
```

En `src/features/tenant-settings/pages/tenant-settings-page.test.tsx`, agrega al final del `describe('TenantSettingsPage', ...)`:

```tsx
  it('links to the integrations page when it may be read', () => {
    render(
      <TenantSettingsPage
        settings={SETTINGS}
        status="ready"
        canManage={false}
        logo={idleLogo()}
        showIntegrations
        onSave={vi.fn()}
      />,
    )

    expect(
      screen.getByRole('link', { name: 'Conexiones con plataformas externas' }),
    ).toHaveAttribute('href', '/settings/integrations')
  })

  it('leaves the integrations card out by default', () => {
    render(
      <TenantSettingsPage
        settings={SETTINGS}
        status="ready"
        canManage
        logo={idleLogo()}
        onSave={vi.fn()}
      />,
    )

    expect(screen.queryByRole('heading', { name: 'Integraciones' })).toBeNull()
    expect(
      screen.queryByRole('link', { name: 'Conexiones con plataformas externas' }),
    ).toBeNull()
  })
```

- [ ] **Step 2: Correrlas y verlas fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/routes/_authenticated/settings/integrations.test.tsx src/routes/_authenticated/settings/index.test.tsx src/features/tenant-settings/pages/tenant-settings-page.test.tsx
```

Expected: FAIL — las 3 de `integrations.test.tsx` (el árbol no tiene `/settings/integrations`: `Unable to find role="heading"` / `Unable to find an element with the text`), `'links to the integrations page with integrations.connection.read'` y `'links to the integrations page when it may be read'` (no hay enlace). Las pruebas que ya existían y las dos «hides/leaves out» pasan.

- [ ] **Step 3: Escribir la ruta**

`src/routes/_authenticated/settings/integrations.tsx`:

```tsx
import { createFileRoute } from '@tanstack/react-router'

import { usePermissions } from '@/features/auth/hooks/use-permission'
import { useConnectionMutations } from '@/features/integrations/hooks/use-connection-mutations'
import { useConnections } from '@/features/integrations/hooks/use-connections'
import { useIntegrationsCatalog } from '@/features/integrations/hooks/use-integrations-catalog'
import { IntegrationsPage } from '@/features/integrations/pages/integrations-page'
import { INTEGRATIONS_PERMISSIONS } from '@/features/integrations/types/integrations'
import {
  integrationsAccess,
  integrationsPageStatus,
} from '@/features/integrations/utils/integrations-query-status'

export const Route = createFileRoute('/_authenticated/settings/integrations')({
  component: IntegrationsRoute,
})

/**
 * Container of the integrations screen: hooks live here, the view has none (same split as
 * `settings/orders-export-columns.tsx`). No `ModuleGate`: Integrations is core (spec decision 4)
 * and the backend already filters the catalog by the tenant's active modules.
 *
 * Nothing is asked until the permissions answer: `can()` denies while they load, and asking
 * without `integrations.connection.read` is a guaranteed 403.
 */
function IntegrationsRoute() {
  const { can, status: permissionsStatus } = usePermissions()
  const access = integrationsAccess(
    permissionsStatus,
    can(INTEGRATIONS_PERMISSIONS.connectionRead),
  )
  const enabled = access === 'allowed'
  const catalog = useIntegrationsCatalog({ enabled })
  const connections = useConnections({ enabled })
  const mutations = useConnectionMutations()

  return (
    <IntegrationsPage
      status={integrationsPageStatus(access, catalog.status, connections.status)}
      providers={catalog.providers}
      connections={connections.connections}
      canManage={can(INTEGRATIONS_PERMISSIONS.connectionManage)}
      isSaving={mutations.isSaving}
      pendingConnectionId={mutations.pendingConnectionId}
      onRetry={() => {
        catalog.retry()
        connections.retry()
      }}
      onCreate={mutations.create}
      onUpdate={mutations.update}
      onTest={mutations.test}
      onPause={mutations.pause}
      onResume={mutations.resume}
      onDelete={mutations.remove}
    />
  )
}
```

- [ ] **Step 4: Regenerar el árbol de rutas**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bunx vite build
"vite exit $LASTEXITCODE"
Select-String -Path src\routeTree.gen.ts -Pattern "settings/integrations" | Measure-Object | Select-Object -ExpandProperty Count
git status --short src/routeTree.gen.ts
```

Expected: `vite exit 0`, un conteo mayor que 0 y `M src/routeTree.gen.ts`. `vite build` no corre `tsc`: el chequeo de tipos va en la Task 10.

- [ ] **Step 5: Agregar la tarjeta a Configuración**

En `src/features/tenant-settings/pages/tenant-settings-page.tsx`:

Reemplaza el import de iconos (`:2`):

```tsx
import { ChevronRight, Lock, Plug, Table2 } from 'lucide-react'
```

En `TenantSettingsPageProps`, justo después de `showOrdersExport?: boolean` (`:28`):

```tsx
  /** `integrations.connection.read`: the card links to the integrations page. Off by default, so a
   * page rendered without that answer never shows it. The container decides. */
  showIntegrations?: boolean
```

En la desestructuración, justo después de `showOrdersExport = true,` (`:47`):

```tsx
  showIntegrations = false,
```

Y justo antes del `</PageContainer>` final (`:174`), después del bloque `{showOrdersExport ? (...) : null}`:

```tsx
      {showIntegrations ? (
        // Its own page (spec 2026-10-08, decision 7), like the orders export columns. Visible in
        // read mode too: the page itself is read with integrations.connection.read.
        <SettingsSection
          title="Integraciones"
          description="Tiene su propia página."
        >
          <Link
            to="/settings/integrations"
            aria-label="Conexiones con plataformas externas"
            aria-describedby="integrations-link-description"
            className="group flex items-center gap-4 rounded-xl border border-border bg-background p-4 transition-colors hover:bg-muted/50 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
          >
            <span className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-muted text-muted-foreground">
              <Plug className="size-5" aria-hidden="true" />
            </span>
            <span className="min-w-0 flex-1 space-y-0.5">
              <span className="block text-sm font-medium">
                Conexiones con plataformas externas
              </span>
              <span
                id="integrations-link-description"
                className="block text-sm text-muted-foreground"
              >
                Conecta las cuentas de tu empresa en otras plataformas y revisa
                si siguen funcionando.
              </span>
            </span>
            <ChevronRight
              className="size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
              aria-hidden="true"
            />
          </Link>
        </SettingsSection>
      ) : null}
```

En `src/routes/_authenticated/settings/index.tsx`, agrega el import junto a los demás de features (después de `:4`):

```tsx
import { INTEGRATIONS_PERMISSIONS } from '@/features/integrations/types/integrations'
```

Y en el JSX, justo después de `showOrdersExport={showOrdersExport}` (`:43`):

```tsx
      showIntegrations={can(INTEGRATIONS_PERMISSIONS.connectionRead)}
```

- [ ] **Step 6: Correrlas y verlas pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run test --run src/routes/_authenticated/settings/integrations.test.tsx src/routes/_authenticated/settings/index.test.tsx src/features/tenant-settings/pages/tenant-settings-page.test.tsx
```

Expected: PASS — los tres archivos en verde (`Test Files  3 passed (3)`), incluidas todas las pruebas que ya existían en `index.test.tsx` y `tenant-settings-page.test.tsx`.

- [ ] **Step 7: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/integraciones') { throw "rama inesperada" }
$files = @(
  'src/routes/_authenticated/settings/integrations.tsx',
  'src/routes/_authenticated/settings/integrations.test.tsx',
  'src/routes/_authenticated/settings/index.tsx',
  'src/routes/_authenticated/settings/index.test.tsx',
  'src/features/tenant-settings/pages/tenant-settings-page.tsx',
  'src/features/tenant-settings/pages/tenant-settings-page.test.tsx'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files src/routeTree.gen.ts
git diff --cached --name-only
$msg = @'
feat(integrations): ruta /settings/integrations y tarjeta en Configuración

La ruta es el container: no pide nada sin integrations.connection.read y
pasa las escrituras tal cual a la página. Configuración gana la tarjeta
«Integraciones», visible con ese mismo permiso. routeTree.gen.ts
regenerado con vite build.
'@
$msgFile = Join-Path $env:TEMP 'qep-integraciones-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

Expected: `git diff --cached` lista los 6 archivos más `src/routeTree.gen.ts`, y ningún archivo de `features/auth/` ni `tenant-settings/types/`.

---

### Task 10: Verificación completa contra la línea de base

**Files:** ninguno, salvo que algo falle (el arreglo va en el archivo dueño y en un commit `fix(integrations): …`).

- [ ] **Step 1: Suite completa, fallas por nombre**

```powershell
$root = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees'
$wt = "$root\integraciones"
Set-Location $wt
bun run test --run --reporter=json --outputFile="$root\integraciones-final-tests.json"
"tests exit $LASTEXITCODE"
bun "$root\integraciones-failures.mjs" "$root\integraciones-final-tests.json" "$root\integraciones-final-failures.txt"
$base = @(Get-Content -LiteralPath "$root\integraciones-baseline-failures.txt" | Where-Object { $_ })
$final = @(Get-Content -LiteralPath "$root\integraciones-final-failures.txt" | Where-Object { $_ })
"--- nuevas (no estaban en la línea de base):"
$final | Where-Object { $base -notcontains $_ }
"--- arregladas de paso:"
$base | Where-Object { $final -notcontains $_ }
```

Expected: la sección «nuevas» **vacía**. Cualquier nombre ahí es una regresión de esta rama aunque esté en un archivo que no se tocó.

- [ ] **Step 2: Tipos y build**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
bun run build
"build exit $LASTEXITCODE"
git status --short src/routeTree.gen.ts
```

Expected: `build exit 0` (como en la línea de base) y `routeTree.gen.ts` sin cambios (si aparece, `git update-index --refresh`; si sigue con diff real, regenerarlo era parte de la Task 9 y se commitea como `fix(integrations): …`).

- [ ] **Step 3: Lint**

```powershell
$root = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees'
Set-Location "$root\integraciones"
bun run lint | Out-File -Encoding utf8 "$root\integraciones-final-lint.txt"
"lint exit $LASTEXITCODE"
Get-Content -LiteralPath "$root\integraciones-final-lint.txt" -Tail 5
Select-String -LiteralPath "$root\integraciones-final-lint.txt" -Pattern 'features[\\/]integrations|settings[\\/]integrations|test[\\/]integrations|tenant-settings-page|settings[\\/]index'
```

Expected: el mismo resumen que `integraciones-baseline-lint.txt` y **ninguna** línea del `Select-String`.

- [ ] **Step 4: Formato de lo tocado**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
$forkPoint = git merge-base develop HEAD
$touched = @(git diff --name-only "$forkPoint..HEAD" -- src | Where-Object { $_ -match '\.(ts|tsx)$' -and $_ -notmatch 'routeTree\.gen\.ts$' })
bunx prettier --check $touched
```

Expected: `All matched files use Prettier code style!`

- [ ] **Step 5: Si algo falló**

Arréglalo en el archivo dueño (la task que lo creó), corre sólo esa prueba en RED→GREEN y commitea con el mismo bloque de la Task 1, Step 7, cambiando `$files` y el mensaje a `fix(integrations): <qué y por qué>`. Repite los Steps 1-4 **una** vez. En el reporte: fallas de la línea de base, «nuevas» (vacío), «arregladas de paso», `build exit`, resumen de lint antes y después, y la salida literal RED/GREEN de cada task.

---

### Task 11: Humo E2E contra el backend real

Requiere el backend de la fase 1 y, para la Parte B, al owner frente al navegador. No produce commits salvo que aparezca un defecto (entonces `fix(integrations): …` con su prueba RED→GREEN en la task dueña).

**Files:** ninguno.

- [ ] **Step 0: Precondición**

```powershell
$be = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones'
if (-not (Test-Path "$be\src\Modules\Integrations")) { throw "el backend de Integraciones no está en $be: la Task 11 queda bloqueada" }
git -C $be branch --show-current
git -C $be log --oneline -1
```

Si el backend vive en otro checkout, ajusta `$be` a ese path absoluto y anótalo en el reporte. Si no existe, la task queda **bloqueada** y se reporta así.

- [ ] **Step 1: Llave de cifrado en user-secrets, sin imprimirla**

```powershell
$be = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones'
dotnet user-secrets list --project "$be\src\Api" | Select-String -Pattern "Integrations:SecretProtection" | Measure-Object | Select-Object -ExpandProperty Count
```

Expected: `2` (`ActiveKeyId` y `Keys:k1`). Si da `0`, genera una llave **en el proceso** (en Windows PowerShell 5.1 no existe `RandomNumberGenerator::Fill`) y descarta la salida de `set`, que repite el valor:

```powershell
$be = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones'
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
dotnet user-secrets set "Integrations:SecretProtection:Keys:k1" ([Convert]::ToBase64String($bytes)) --project "$be\src\Api" | Out-Null
dotnet user-secrets set "Integrations:SecretProtection:ActiveKeyId" "k1" --project "$be\src\Api" | Out-Null
dotnet user-secrets list --project "$be\src\Api" | Select-String -Pattern "Integrations:SecretProtection" | Measure-Object | Select-Object -ExpandProperty Count
```

- [ ] **Step 2 (Parte A): contrato con `curl.exe` y el stub de headers**

Levanta la API con el stub (memorias `api-local-qep-backend` y `launchsettings-pisa-las-variables`), **en segundo plano**, y espera `Now listening on: http://localhost:5199`:

```powershell
$be = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones'
docker start postgres18
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:5199"
$env:Authentication__UseDevelopmentStub = "true"
$env:Storage__R2__PublicBucket = "qep-public"
$env:Storage__R2__PublicBaseUrl = "https://cdn.qep.test"
dotnet run --project "$be\src\Api" --no-launch-profile -p:NuGetAudit=false
```

Con un tenant simulado (sin fila, así `FindAsync` es `null` y todo proveedor es visible):

```powershell
$tenant = [guid]::NewGuid().ToString()
$subject = [guid]::NewGuid().ToString()
$api = "http://localhost:5199/api/v1/tenants/$tenant/integrations"
$h = @('-H', "X-Subject-Id: $subject", '-H', "X-Tenant-Id: $tenant", '-H', 'X-Permissions: integrations.connection.read,integrations.connection.manage')

curl.exe -s -i @h "$api/catalog"
curl.exe -s -i @h "$api/connections"

$empty = Join-Path $env:TEMP 'qep-int-empty.json'
[System.IO.File]::WriteAllText($empty, '{"providerKey":"zenvia","name":"","fields":{},"secrets":{}}', (New-Object System.Text.UTF8Encoding $false))
curl.exe -s -i -X POST @h -H "Content-Type: application/json" -d "@$empty" "$api/connections"

$bad = Join-Path $env:TEMP 'qep-int-bad-token.json'
[System.IO.File]::WriteAllText($bad, '{"providerKey":"zenvia","name":"Humo","fields":{"fromNumber":"573001234567"},"secrets":{"apiToken":"token-invalido-de-humo"}}', (New-Object System.Text.UTF8Encoding $false))
curl.exe -s -i -X POST @h -H "Content-Type: application/json" -d "@$bad" "$api/connections"
```

Expected y qué anotar (salida literal en el reporte):
1. `GET /catalog` → `200` con `"providers":[{"key":"zenvia",...,"category":"Messaging","fields":[{"key":"apiToken",...,"kind":"Secret"...},{"key":"fromNumber",...,"kind":"Phone"...}],"maxConnections":20,"connectionCount":0}]`. Compara cada nombre de propiedad con `types/integrations.ts`.
2. `GET /connections` → `200` con `{"items":[]}` (F5).
3. `POST` vacío → `422`, `"code":"validation.failed"` y `errors` con las llaves **`name`**, **`fields.fromNumber`**, **`secrets.apiToken`** en minúscula (F1). Si salen `Name` u otra forma, es un defecto del backend: anótalo y no lo compenses en el frontend.
4. `POST` con token falso → `422 "code":"integrations.connection.credentials_rejected"`; anota si trae `errors` (F2). Si responde `503 integrations.secret_protection.unavailable`, falta el Step 1 (y queda probado F3). Si responde `422 provider_unreachable`, no hay salida a Zenvia desde esta máquina: anótalo.
5. Ningún cuerpo contiene `token-invalido-de-humo`.

Detén **sólo** el proceso `dotnet` que lanzaste en este step.

- [ ] **Step 3 (Parte B): pantalla con auth real, junto al owner**

```powershell
$be = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\integraciones'
dotnet run --project "$be\src\Api" --launch-profile http -p:NuGetAudit=false
```

En otra terminal (el puerto 3002 es fijo: si el checkout principal ya tiene `bun dev` corriendo, **pregúntale al owner**, no lo detengas):

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\integraciones'
Set-Location $wt
[System.IO.File]::WriteAllText("$wt\.env.local", "QEP_API_PROXY_TARGET=http://localhost:5000`n", (New-Object System.Text.UTF8Encoding $false))
bun dev
```

El owner entra a `http://localhost:3002` con Google, como `admin` de un tenant con Cotizaciones activo (recarga la SPA si la API se reinició: memoria `permiso-nuevo-exige-reiniciar-y-recargar`), y marca ✔/✘:

1. Configuración muestra la tarjeta «Integraciones» y su enlace abre `/settings/integrations`.
2. «Proveedores disponibles» muestra «Zenvia (WhatsApp)» con «0 de 20 conexiones».
3. «Conectar» con un token falso: el botón dice «Verificando con el proveedor…», después el campo «API token» queda marcado con el mensaje de credencial rechazada y la lista sigue vacía.
4. Pegar el número como `+57 300 123 4567` deja `573001234567`.
5. (Sólo si el owner decide pegar un token real de Zenvia; nadie más lo ve ni lo escribe) la conexión queda «Activa»; «Editar» muestra «Configurada el <hoy>» y «Déjala en blanco para conservar la actual»; «Guardar» sin tocar el token la deja «Activa».
6. «Pausar» → «Pausada»; «Reanudar» → «Activa» (o «Necesita atención» con el motivo, si el token ya no sirve).
7. «Eliminar» exige el nombre exacto y la conexión desaparece.
8. En DevTools → Network, la búsqueda del token tipeado no encuentra ninguna respuesta que lo contenga (criterio 3 de la spec).

Al terminar: detén **sólo** los dos procesos que lanzaste en este step y borra `$wt\.env.local` (`Remove-Item -LiteralPath "$wt\.env.local"`). Anota en el reporte cada ✔/✘ y, para cada ✘, el defecto y la task dueña.
