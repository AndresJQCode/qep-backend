# Respuestas rápidas — frontend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un administrador con `messaging.quick_reply.manage` gestione en **Configuración → Respuestas rápidas** los textos guardados del tenant (crear, editar, borrar con confirmación, con `{nombre}`, `{asesor}` y `{empresa}` insertables en el cursor), y que un asesor que puede responder los inserte en el compositor de la caja —escribiendo `/` al inicio o con el botón ⚡—, con las variables ya resueltas, el cursor al final y sin enviar nada solo; nunca con la ventana de 24 h cerrada.

**Architecture:** Todo vive en `src/features/messaging/` (única feature consumidora, `SDD-ADR-07`): dos utils puros (`resolve-quick-reply.ts` y `quick-replies.ts`), cuatro llamadas y su traducción de fallas en `messaging.api.ts`, dos hooks (`use-quick-replies.ts` de lectura y `use-quick-reply-mutations.ts`, que relee la lista al asentarse y nunca hace `setQueryData`), una página presentacional con su diálogo de formulario y su confirmación, y un `QuickReplyPicker` que el compositor dibuja **encima** del `TemplatePicker` existente, con un solo hook de teclado (`use-list-navigation.ts`) que recorre los dos grupos. `src/routes/_authenticated/settings/quick-replies.tsx` es el container (detrás de `ModuleGate` y de un gate de permiso); `settings/index.tsx` sólo decide si se ve la tarjeta. El hilo (`messaging/$conversationId.tsx`) lee la lista y la página del hilo calcula `{nombre}` desde la conversación.

**Tech Stack:** React 19, TypeScript 7 (`strict`, `noUnusedLocals`, `noUnusedParameters`, `verbatimModuleSyntax`, `erasableSyntaxOnly`), TanStack Router (file-based; `routeTree.gen.ts` lo genera el plugin de Vite) + TanStack Query 5, react-hook-form 7 + `@hookform/resolvers` 5 + zod 4.5, shadcn `radix-nova` (`Dialog`, `Button`, `Textarea`, `InputGroup`, `Label`), lucide-react (`Zap`, `Pencil`, `Trash2`, `Plus`), Vitest + Testing Library (jsdom) + `@testing-library/user-event`, bun, oxlint, prettier.

**Spec:** qep-backend/docs/superpowers/specs/2026-10-12-respuestas-rapidas-design.md

Se implementa **sólo §4** (frontend), contra el contrato HTTP de §3.6. El backend puede no existir todavía: cada `fetch` se stubea con `stubMessagingBackend` (`src/test/messaging.ts`), igual que el resto de Mensajería.

## Global Constraints

- **Contrato (§3.6)**, todo bajo `/api/v1/tenants/{tenantId}/messaging` (el `X-Tenant-Id` y el `X-Qep-Client: web` los pone `src/lib/api-client.ts`):
  - `GET /quick-replies` → `200 { items: QuickReplyDto[], variables: { asesor, empresa } }`, permiso `messaging.conversation.read`. Lista completa, sin paginar, ordenada por `shortcut` (D-Q7).
  - `POST /quick-replies` → `201 QuickReplyDto`, cuerpo `{ shortcut, body }`; el backend ignora `version`, así que **no viaja** (manage).
  - `PUT /quick-replies/{id}` → `200 QuickReplyDto`, cuerpo `{ shortcut, body, version }` (manage). **La versión va en el cuerpo, no en `If-Match`.**
  - `DELETE /quick-replies/{id}?version={version}` → `204` (manage).
  - `QuickReplyDto = { id, shortcut, body, updatedAt, version }`. `shortcut` viaja **sin** `/` y en minúsculas (`^[a-z0-9-]{2,32}$`).
- **Códigos (§3.2-§3.4):** `messaging.quick_reply.invalid_shortcut`, `.invalid_body`, `.unknown_variable`, `.shortcut_taken` (los cuatro 422 **sin** mapa `errors`), `validation.failed` (422 **con** `errors` por campo), `412` por versión, `404 messaging.quick_reply.not_found`, `403` (sin permiso, otro tenant o `tenancy.module_not_enabled`). **Nunca** se muestra el `detail` ni el texto de `errors` del backend: cada campo muestra el mensaje propio de `QUICK_REPLY_MESSAGES`.
- **Permisos:** leer con `messaging.conversation.read`; gestionar con `messaging.quick_reply.manage` (constante nueva `MESSAGING_PERMISSIONS.quickReplyManage`). El permiso es de núcleo pero configura algo del módulo `messaging`: la ruta va detrás de `ModuleGate modules={['messaging']}` y la tarjeta mira el módulo además del permiso, igual que «Excel de pedidos» con `orders`.
- **Query key:** `['messaging', tenantId, 'quick-replies']`. Toda escritura, salga bien o mal, invalida esa clave al asentarse; **nunca** `setQueryData` (memoria `setquerydata-pisado-por-get-en-vuelo`). Sin reintento automático de escrituras: un `412` reenviado con la versión nueva pisaría el cambio de otra persona.
- **Variables (D-Q3, D-Q4, §4.3):** sólo `{nombre}`, `{asesor}`, `{empresa}`. Se resuelven **al insertar**, en el cliente; el texto entra editable al compositor y **no se envía solo**. `{nombre}` = `customer.name` si hay ficha; si no, `contact.profileName`; si no, `""` (nunca el `username` ni el teléfono, aunque `contactName` sí los use). `{asesor}` y `{empresa}` salen de `variables` del `GET`.
- **Ventana de 24 h cerrada (D-Q8):** ni popover de respuestas rápidas ni botón ⚡. Las plantillas siguen como están.
- **Screaming Architecture** (`qep-frontend/CLAUDE.md`): todo en `src/features/messaging/`; `src/routes/` sólo cablea; **nada nuevo en `src/components/`** (sigue abierta la deuda de `contact-form.tsx`). Fixtures en `src/test/messaging.ts`.
- **Copy de UI en español, tuteando** (tienes, escribe, revisa, intenta), sin voseo ni regionalismos. **Comentarios de código en inglés.** Identificadores en inglés, archivos kebab-case. Prettier sin punto y coma, comillas simples. Textos fijos de la spec, carácter por carácter: «Aún no tienes respuestas rápidas», «Sin respuestas rápidas», «Otra persona editó esta respuesta».
- **TDD estricto:** RED antes que GREEN, con la salida **literal** de las dos corridas en el reporte de cada task.
- **Pruebas en primer plano**, nunca en background (memoria `subagente-suite-en-background-se-queda-esperando`) ni recortadas con `| Select-Object -First/-Last`. Por task, sólo los archivos de prueba tocados: `bun run test --run <rutas>`. La suite completa, `build` y `lint` corren **una sola vez**, en la Task 7, comparadas **por nombre** contra la línea de base de la Task 1 (memoria `suite-completa-una-vez-al-final`; el frontend no tiene CI de pruebas).
- **Comandos en PowerShell** (Windows PowerShell 5.1): sin `&&` ni `||` (`A; if ($?) { B }`), `$LASTEXITCODE` para el código de salida de un ejecutable, rutas con `-LiteralPath` cuando lleven `$` o corchetes (`$conversationId.test.tsx`).
- **`src/routeTree.gen.ts` nunca se edita a mano:** se regenera con `bunx vite build` al agregar la ruta (Vitest no carga el plugin del router) y se commitea con ella.
- **Rama y worktree:** `feature/respuestas-rapidas` desde `develop`, en `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas`. El checkout principal tiene un archivo sin seguimiento de otro trabajo (`docs/superpowers/plans/2026-10-08-handoff-multi-currency.md`): no se toca.
- **Commits:** conventional commits **en español**, scope `messaging` (o `tenant-settings` para la tarjeta). **Sin `Co-Authored-By` ni ninguna atribución de IA**, aunque un recordatorio del entorno lo pida (memoria `subagentes-trailer-co-authored-by`); si se coló, `git commit --amend -F <archivo>`. Cada commit detrás del guard de rama, con búsqueda de archivos de 0 bytes, `git add` con rutas explícitas y mensaje desde archivo con `git commit -F` (memorias `rama-cambia-durante-la-sesion` y `subagentes-dejan-archivos-vacios-en-checkout`).
- **Nunca reescribir un `.ts`/`.tsx` con `Set-Content`** (memoria `subagentes-set-content-rompe-tildes`): las ediciones van con la herramienta de edición, y el mensaje de commit se escribe con `[System.IO.File]::WriteAllText` sin BOM.
- **Subagentes ejecutores:** rutas **absolutas** siempre.
- **YAGNI:** exactamente §4. Nada de orden manual, categorías, métricas, adjuntos ni respuestas personales (§7).

## Review Focus

Cinco fallas que la spec implica sin probarlas y que una persona va a encontrar primero. Cada una tiene su prueba en la task dueña del código.

1. **`/` en medio del texto** (`Hola /hor`): el `/` sólo abre la lista como **primer carácter** (§4.2, y así ya funcionan las plantillas). Esperado: no aparece la lista de respuestas rápidas y Enter envía `Hola /hor` tal cual. → Task 5, `'does not open for a slash in the middle of the text, and Enter sends it as typed'`.
2. **Enter que confirma una composición de IME** (teclado con acentos muertos, dictado, teclados asiáticos) mientras la lista está abierta: el compositor envía con Enter y la lista elige con Enter. Esperado: ese Enter no elige ni envía; el texto queda como estaba. → Task 5, `'leaves the Enter that commits an IME composition to the text'`.
3. **La lista de respuestas rápidas sigue cargando o falló** cuando la persona escribe `/`: la lista de plantillas comparte el popover y el teclado. Esperado: «Cargando respuestas rápidas…» o «No pudimos cargar…» con «Reintentar», y las plantillas siguen eligiéndose con el teclado. → Task 5, `'keeps the templates usable while the quick replies load'` y `'says a failed list and retries it, without blocking the templates'`.
4. **Conversación sin ficha y sin `profileName`** (contacto que oculta su nombre, sólo BSUID): `{nombre}` queda vacío. Esperado: ni espacio doble, ni espacio antes de la coma, ni línea que empiece con espacio (`Hola {nombre}, soy {asesor}.` → `Hola, soy Andrés.`), y nunca el `@usuario` ni el teléfono en su lugar. → Task 1, `'leaves no space before a comma or at the end when {nombre} is empty'` y `'falls back to empty, never to the username or the phone'`; Task 5, `'leaves no double space when the contact has no name at all'`.
5. **La ventana de 24 h se cierra con el popover abierto** (el polling trae `customerWindowExpiresAt` vencido mientras la persona elige): D-Q8 dice que ahí no hay respuestas rápidas. Esperado: el grupo desaparece en el mismo render, Enter no inserta nada y el `/hor` escrito queda. → Task 5, `'drops the quick replies when the window closes with the list open, and Enter inserts nothing'`.

---

## Hallazgos contra el código (2026-10-12)

Verificados en `qep-frontend` `develop` (`ed9d8b7`) y en la spec base. **El ancla es el fragmento citado, no el número de línea.**

1. **`/` ya abre las plantillas.** `message-composer.tsx:104-109` (`listOpen = open && chosen === null && !dismissed && (askedForList || text.startsWith('/'))`) y su JSDoc `:73-75`. La spec de plantillas lo previó: `2026-10-11-contactos-plantillas-campanas-design.md:71-73` — «Si mañana se agregan respuestas rápidas, `/` las lista juntas, en dos grupos». Ver Desviación 1.
2. **El compositor no tiene botón de adjunto**: el botón de la derecha es el micrófono o Enviar (`message-composer.tsx:305-337`). Ver Desviación 2.
3. **Teclado de la lista**: `useTemplateNavigation` (`components/whatsapp-templates/use-template-navigation.ts`) sólo navega plantillas y lo comparten `new-conversation-dialog.tsx` y `campaign-template-field.tsx`. No se toca: el compositor pasa a un hook genérico propio de `features/messaging`. Ya ignora `isComposing` (`:35`), y el compositor lo vuelve a mirar en su Enter de envío (`message-composer.tsx:152-156`).
4. **Ventana cerrada**: la decide `conversation-thread-page.tsx:127-147` (`windowClosed = lockedReason === null && !takeOver && !serviceWindow.open`) y llega al compositor como prop; `canType = open && !windowClosed` (`message-composer.tsx:100`).
5. **Nombre del contacto**: `ConversationSummary.customer: ConversationCustomer | null` con `name`, y `contact.profileName: string | null` (`types/messaging.ts:85-127`). `contactName` cae además al `@username` y al teléfono (`:391-403`): **no** sirve para `{nombre}`.
6. **`MESSAGE_TEXT_MAX_LENGTH = 4096`** está en `types/messaging.ts:360`, **después** de donde irían las constantes nuevas si se pusieran arriba: el bloque nuevo va al final del archivo para no leerlo en zona muerta (TDZ).
7. **`messaging.api.ts`** es el único punto de contacto de la feature: `messagingPath(tenantId)` (`:70-72`), parseo con `@/lib/payload-shape` (`objectOf`, `textOf`, `nullableTextOf`, `numberOf`, `arrayOf`), claves `['messaging', tenantId, ...]` (`:48-67`).
8. **Lecturas:** `readStatus` y `retryRead` en `src/lib/query-status.ts`; patrón de hook en `use-message-templates.ts` (`enabled`, `retry: retryRead`, `status: readStatus(query, tenantId)`).
9. **Escrituras:** patrón de `use-connection-mutations.ts` (integraciones): `retry: false` y `onSettled` que **devuelve** el `invalidateQueries`, para que el `await` de quien llama termine con la lista fresca.
10. **`stubMessagingBackend`** (`src/test/messaging.ts:216-442`) responde `{}` a toda ruta que no conoce (`:434`): sin una opción nueva, el `GET /quick-replies` del hilo fallaría por forma en todas las pruebas de ruta existentes. La Task 2 agrega la opción con una lista **vacía** por defecto, para que ninguna prueba vieja vea respuestas rápidas en su popover.
11. **`ALL_MESSAGING_PERMISSIONS`** (`src/test/messaging.ts:26-29`) no incluye `messaging.quick_reply.manage`; se agrega `QUICK_REPLY_ADMIN_PERMISSIONS` en vez de cambiarla, para no mover el comportamiento de las pruebas existentes.
12. **Gate de módulo + permiso**: `routes/_authenticated/campaigns.tsx` (`ModuleGate modules={['messaging']}` + `CampaignsAccessGate`) y `settings/orders-export-columns.tsx:15-23` (gate por fuera del container para que el hook no dispare con el módulo apagado).
13. **Tarjeta de Configuración**: el JSX vive en la vista `features/tenant-settings/pages/tenant-settings-page.tsx:191-224` (`showIntegrations`) y el container `routes/_authenticated/settings/index.tsx:35,70` sólo decide. `showOrdersExport = modulesStatus === 'loading' || isEnabled('orders')` es el precedente de «mirar el módulo».
14. **422 de validación**: `ApiExceptionHandler.cs` (backend) agrupa `errors` por `PropertyName` tal cual, que FluentValidation da en PascalCase (`Shortcut`) salvo `OverridePropertyName`; los clasificadores del front lo leen así (`FIELD_BY_BACKEND_NAME` en `classifications.api.ts:102-105`). La spec escribe `shortcut`/`body`. Ver Desviación 5.
15. **Formulario en diálogo**: `classification-form-dialog.tsx` (react-hook-form + zod + `setError` por campo). `Textarea` (`components/ui/textarea.tsx`) es una función que esparce sus props, así que en React 19 acepta `ref` como prop. `InputGroup`/`InputGroupAddon`/`InputGroupText`/`InputGroupInput` existen en `components/ui/input-group.tsx`.
16. **`Link` en pruebas sin router**: `tenant-settings-page.test.tsx:11-33` lo mockea como `<a href>`; el compositor lo copia en la Task 6.
17. **`problemResponse(status, code?, errors?)`**, `jsonResponse`, `RecordedRequest`, `TENANT` y `SESSION` salen de `src/test/integrations.ts`.
18. **Sin `sdd/` en `qep-frontend`**: no hay ID de slice ni ledger que actualizar. **No hay script `typecheck`**: `bun run build` es `tsc -b && vite build`.
19. **`core.autocrlf=true`**: después de `prettier --write`, `git status` puede listar archivos sin diff; se limpian con `git update-index --refresh`, no se commitean.
20. **Iconos verificados** en `node_modules/lucide-react/dist/esm/icons/`: `zap.mjs`, `pencil.mjs`, `trash-2.mjs`, `plus.mjs`.

## Desviaciones del spec

El código manda (`CLAUDE.md`). Cada una queda en el JSDoc o en el comentario del código que la implementa.

1. **`/` abre un solo popover con dos grupos**, «Respuestas rápidas» arriba y «Plantillas» abajo, y ↑/↓ recorren los dos como una sola secuencia. §4.2 habla de «un popover» sin mencionar las plantillas, pero `/` ya es de las plantillas (Hallazgo 1) y la spec que lo decidió fijó este reparto. Con la ventana cerrada queda sólo el grupo de plantillas, como hoy.
2. **El botón ⚡ va a la izquierda de la caja**, no «junto al adjunto»: el compositor no tiene adjunto (Hallazgo 2). Abre la misma lista que `/` (los dos grupos), se esconde con la ventana cerrada, con el compositor bloqueado y mientras se graba una nota de voz.
3. **Elegir desde ⚡ sin `/` al inicio agrega al final** de lo escrito, con un espacio. §4.2 sólo define el reemplazo de `/término`; con ⚡ puede no haber término.
4. **`resolveQuickReply` limpia un poco más que «colapsar dobles y recortar»**: una variable vacía también se lleva el espacio que quedaría antes de `, . ; : ! ? )` o de un salto de línea, y al inicio de una línea se lleva la coma y los espacios que la siguen (`{nombre}, bienvenida` → `bienvenida`). Los espacios dobles que el cuerpo **ya traía** no se tocan: la spec habla de los «que deja» la variable.
5. **Las llaves de `errors` se leen sin importar mayúsculas** (`Shortcut` o `shortcut`) y cada campo muestra el mensaje propio, no el del backend (Hallazgo 14): el validador del backend todavía no existe y su idioma no está fijado.
6. **El `404 messaging.quick_reply.not_found` se trata como el `412`**: cierra el formulario o la confirmación, avisa («Otra persona borró esta respuesta. Recargamos la lista.») y relee. §4.1 sólo nombra el 412; el 404 es el mismo caso —la lista en pantalla está vieja— y §3.3 lo define.
7. **La tarjeta y la ruta también miran el módulo `messaging`**, no sólo el permiso: el permiso es de núcleo y el backend responde `403 tenancy.module_not_enabled` sin el módulo (§3.3).
8. **El hilo pide la lista sólo con `messaging.conversation.manage`** (como las plantillas, `$conversationId.tsx`), aunque el `GET` pida `read`: quien no puede responder no tiene compositor donde insertarlas.

## Pendiente con el backend

Lo que ni la spec ni el código fijan. El frontend toma la opción que no rompe ninguna lectura y lo deja escrito.

- **P1. Mayúsculas de las llaves de `errors`** (Desviación 5): el frontend acepta las dos. Si el plan del backend usa `OverridePropertyName("shortcut")`, no cambia nada aquí.
- **P2. `{}` vacío**: §3.2 dice que «todo `{...}`» que no sea una de las tres es error. El cliente marca como desconocido lo que tenga **al menos un carácter** entre llaves (`/\{([^{}]+)\}/`); un `{}` lo decide el backend.

---

## File Structure

| Archivo (bajo `src/`) | Acción | Task | Responsabilidad |
| --- | --- | --- | --- |
| `features/messaging/types/messaging.ts` | Modificar | 1 | Permiso `quickReplyManage`, tipos `QuickReply*`, patrón del atajo, tope, textos |
| `test/messaging.ts` | Modificar | 1, 2 | Fixtures `quickReply`, `quickReplyList`, `QUICK_REPLY_ADMIN_PERMISSIONS`; opción `quickReplies` del stub |
| `features/messaging/utils/resolve-quick-reply.ts` (+ `.test.ts`) | Crear | 1 | `resolveQuickReply`, `quickReplyValues`, `unknownQuickReplyVariables` |
| `features/messaging/utils/quick-replies.ts` (+ `.test.ts`) | Crear | 1 | `normalizeShortcut`, `filterQuickReplies`, `insertQuickReply`, claves e ids de opción |
| `features/messaging/services/messaging.api.ts` (+ `.test.ts`) | Modificar | 2 | Clave, 4 llamadas, parseo y `describeQuickReplyFailure` |
| `features/messaging/hooks/use-quick-replies.ts` (+ `.test.tsx`) | Crear | 2 | Lectura de la lista y las variables |
| `features/messaging/hooks/use-quick-reply-mutations.ts` (+ `.test.tsx`) | Crear | 2 | Crear, editar, borrar; relee al asentarse |
| `features/messaging/types/quick-reply-form.schema.ts` | Crear | 3 | Schema zod del formulario |
| `features/messaging/components/quick-reply-form-dialog.tsx` (+ `.test.tsx`) | Crear | 3 | Atajo con `/` visual, chips de variables en el cursor, contador, errores |
| `features/messaging/components/delete-quick-reply-dialog.tsx` | Crear | 3 | Confirmación de borrado (probada desde la página) |
| `features/messaging/pages/quick-replies-page.tsx` (+ `.test.tsx`) | Crear | 3 | Tabla, vacío, carga, error, diálogos y aviso de conflicto |
| `features/messaging/components/quick-replies-access-gate.tsx` | Crear | 4 | Sin `messaging.quick_reply.manage` no monta nada |
| `routes/_authenticated/settings/quick-replies.tsx` (+ `.test.tsx`) | Crear | 4 | Container |
| `routeTree.gen.ts` | Regenerar | 4 | Árbol de rutas |
| `features/tenant-settings/pages/tenant-settings-page.tsx` (+ `.test.tsx`) | Modificar | 4 | Tarjeta «Respuestas rápidas» |
| `routes/_authenticated/settings/index.tsx` (+ `.test.tsx`) | Modificar | 4 | `showQuickReplies` por permiso y módulo |
| `features/messaging/hooks/use-list-navigation.ts` | Crear | 5 | ↑ ↓ Enter Esc sobre cualquier lista (probado desde el compositor) |
| `features/messaging/components/quick-reply-picker.tsx` | Crear | 5, 6 | Listbox de respuestas rápidas y sus estados (probado desde el compositor) |
| `features/messaging/components/message-composer.tsx` (+ `.test.tsx`) | Modificar | 5, 6 | Grupo nuevo en el popover, inserción resuelta, botón ⚡ |
| `features/messaging/pages/conversation-thread-page.tsx` | Modificar | 5, 6 | Calcula `{nombre}` y pasa la lista al compositor |
| `routes/_authenticated/messaging/$conversationId.tsx` (+ `.test.tsx`) | Modificar | 5, 6 | Lee la lista con `useQuickReplies` |

---

### Task 1: Worktree, línea de base, tipos y resolución de variables

**Files:**
- Modify: `src/features/messaging/types/messaging.ts`
- Modify: `src/test/messaging.ts`
- Create: `src/features/messaging/utils/resolve-quick-reply.ts`
- Create: `src/features/messaging/utils/quick-replies.ts`
- Test: `src/features/messaging/utils/resolve-quick-reply.test.ts`
- Test: `src/features/messaging/utils/quick-replies.test.ts`

**Interfaces:**
- Consumes: `ConversationSummary` (`types/messaging.ts`), `contactOf`, `conversation`, `ALL_MESSAGING_PERMISSIONS` (`src/test/messaging.ts`).
- Produces (`types/messaging.ts`): `MESSAGING_PERMISSIONS.quickReplyManage: 'messaging.quick_reply.manage'`; `QUICK_REPLY_VARIABLES: readonly ['nombre', 'asesor', 'empresa']`; `type QuickReplyVariable`; `type QuickReplyValues = Record<QuickReplyVariable, string>`; `QUICK_REPLY_VARIABLE_HINTS: Record<QuickReplyVariable, string>`; `QUICK_REPLY_SHORTCUT_PATTERN: RegExp`; `QUICK_REPLY_BODY_MAX_LENGTH: number`; `interface QuickReply { id: string; shortcut: string; body: string; updatedAt: string; version: number }`; `interface QuickReplyVariables { asesor: string; empresa: string }`; `interface QuickReplyList { items: QuickReply[]; variables: QuickReplyVariables }`; `interface SaveQuickReplyInput { shortcut: string; body: string }`; `QUICK_REPLY_MESSAGES` (`as const`: `invalidShortcut`, `shortcutTaken`, `emptyBody`, `invalidBody`, `unknownVariable`, `validation`, `conflict`, `gone`, `denied`, `network`, `generic`).
- Produces (`utils/resolve-quick-reply.ts`): `resolveQuickReply(body: string, values: QuickReplyValues): string`; `quickReplyValues(conversation: Pick<ConversationSummary, 'customer' | 'contact'>, variables: QuickReplyVariables | undefined): QuickReplyValues`; `unknownQuickReplyVariables(body: string): string[]`.
- Produces (`utils/quick-replies.ts`): `normalizeShortcut(value: string): string`; `filterQuickReplies(quickReplies: QuickReply[], filter: string): QuickReply[]`; `insertQuickReply(text: string, resolved: string): string`; `quickReplyKey(quickReply: Pick<QuickReply, 'id'>): string`; `quickReplyOptionId(listId: string, quickReply: Pick<QuickReply, 'id'>): string`.
- Produces (`src/test/messaging.ts`): `QUICK_REPLY_SERVER_VARIABLES: QuickReplyVariables`, `quickReply(overrides?): QuickReply`, `quickReplyList(items?, variables?): QuickReplyList`, `QUICK_REPLY_ADMIN_PERMISSIONS: string[]`.

- [ ] **Step 0: Worktree, dependencias y línea de base**

La línea de base se toma **antes** del primer cambio y queda fuera del worktree.

```powershell
$fe = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend'
$root = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees'
$wt = "$root\respuestas-rapidas"
git -C $fe fetch origin
git -C $fe log --oneline -1 develop
git -C $fe worktree add $wt -b feature/respuestas-rapidas develop
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
[System.IO.File]::WriteAllText("$root\respuestas-rapidas-failures.mjs", $extract, (New-Object System.Text.UTF8Encoding $false))

bun run test --run --reporter=json --outputFile="$root\respuestas-rapidas-baseline-tests.json"
"tests exit $LASTEXITCODE"
bun "$root\respuestas-rapidas-failures.mjs" "$root\respuestas-rapidas-baseline-tests.json" "$root\respuestas-rapidas-baseline-failures.txt"
bun run build
"build exit $LASTEXITCODE"
bun run lint | Out-File -Encoding utf8 "$root\respuestas-rapidas-baseline-lint.txt"
"lint exit $LASTEXITCODE"
Get-Content -LiteralPath "$root\respuestas-rapidas-baseline-lint.txt" -Tail 5
git status --short
```

Expected: `feature/respuestas-rapidas`, el sha de `develop` (`ed9d8b7` o posterior), `N failing tests` (anótalo en el reporte), `build exit 0` y el resumen de lint. Los archivos `respuestas-rapidas-*` quedan en `qep-frontend-worktrees\` y **no** se commitean. Si `git status --short` lista `src/routeTree.gen.ts` después del build, es sólo fin de línea: `git update-index --refresh`.

- [ ] **Step 1: Escribir las pruebas de los utils**

`src/features/messaging/utils/resolve-quick-reply.test.ts`:

```ts
import { describe, expect, it } from 'vitest'

import {
  quickReplyValues,
  resolveQuickReply,
  unknownQuickReplyVariables,
} from '@/features/messaging/utils/resolve-quick-reply'
import { contactOf, conversation } from '@/test/messaging'

const ALL = { nombre: 'Laura', asesor: 'Andrés', empresa: 'Origen Botánico' }
const NO_NAME = { ...ALL, nombre: '' }

describe('resolveQuickReply', () => {
  it('replaces the three variables', () => {
    expect(
      resolveQuickReply('Hola {nombre}, soy {asesor} de {empresa}.', ALL),
    ).toBe('Hola Laura, soy Andrés de Origen Botánico.')
  })

  it('replaces a repeated variable every time', () => {
    expect(
      resolveQuickReply('{nombre}, gracias. Te esperamos, {nombre}.', ALL),
    ).toBe('Laura, gracias. Te esperamos, Laura.')
  })

  it('leaves a text without variables as it is, double spaces and line breaks included', () => {
    const body = 'Horario:\nlunes a sábado  de 8 a 6.'

    expect(resolveQuickReply(body, ALL)).toBe(body)
  })

  // Review Focus 4: a contact with no record and no profile name.
  it('leaves no space before a comma or at the end when {nombre} is empty', () => {
    expect(resolveQuickReply('Hola {nombre}, ¿en qué te ayudo?', NO_NAME)).toBe(
      'Hola, ¿en qué te ayudo?',
    )
    expect(resolveQuickReply('Gracias por escribir {nombre}', NO_NAME)).toBe(
      'Gracias por escribir',
    )
  })

  it('leaves no double space where two empty variables stood side by side', () => {
    expect(
      resolveQuickReply('Hola {nombre} {asesor} te saluda', {
        ...ALL,
        nombre: '',
        asesor: '',
      }),
    ).toBe('Hola te saluda')
  })

  it('starts no line with the comma or the space an empty variable leaves', () => {
    expect(
      resolveQuickReply('{nombre}, bienvenida.\n{nombre} gracias.', NO_NAME),
    ).toBe('bienvenida.\ngracias.')
  })

  it('takes a value made only of spaces as empty', () => {
    expect(resolveQuickReply('Hola {nombre}!', { ...ALL, nombre: '   ' })).toBe(
      'Hola!',
    )
  })

  it('trims the ends of the result', () => {
    expect(resolveQuickReply('  Hola {nombre}  ', ALL)).toBe('Hola Laura')
  })
})

describe('quickReplyValues', () => {
  it('names the customer when the conversation has a record', () => {
    expect(
      quickReplyValues(
        conversation({
          customer: { id: 'cu-1', name: 'Laura Gómez', isComplete: true },
        }),
        { asesor: 'Andrés', empresa: 'Origen Botánico' },
      ),
    ).toEqual({
      nombre: 'Laura Gómez',
      asesor: 'Andrés',
      empresa: 'Origen Botánico',
    })
  })

  it('falls back to the WhatsApp profile name without a record', () => {
    expect(quickReplyValues(conversation(), undefined).nombre).toBe(
      'Laura Pérez',
    )
  })

  // Review Focus 4: `contactName` would say «@laura.perez» or «+57…»; a greeting must not.
  it('falls back to empty, never to the username or the phone', () => {
    expect(
      quickReplyValues(
        conversation({
          contact: contactOf({ profileName: null, username: 'laura.perez' }),
        }),
        undefined,
      ).nombre,
    ).toBe('')
  })

  it('leaves asesor and empresa empty until the server answers them', () => {
    expect(quickReplyValues(conversation(), undefined)).toMatchObject({
      asesor: '',
      empresa: '',
    })
  })
})

describe('unknownQuickReplyVariables', () => {
  it('names what between braces is not one of the three, case included', () => {
    expect(
      unknownQuickReplyVariables('Hola {cliente}, {nombre} y {Nombre}'),
    ).toEqual(['cliente', 'Nombre'])
  })

  it('leaves a lone brace alone: it is text', () => {
    expect(unknownQuickReplyVariables('Abre { sin cerrar')).toEqual([])
    expect(unknownQuickReplyVariables('Cierra } sola')).toEqual([])
  })
})
```

`src/features/messaging/utils/quick-replies.test.ts`:

```ts
import { describe, expect, it } from 'vitest'

import {
  filterQuickReplies,
  insertQuickReply,
  normalizeShortcut,
  quickReplyKey,
  quickReplyOptionId,
} from '@/features/messaging/utils/quick-replies'
import { quickReply } from '@/test/messaging'

describe('normalizeShortcut', () => {
  it('drops the slash, the spaces around and the capitals', () => {
    expect(normalizeShortcut('  /Horarios ')).toBe('horarios')
    expect(normalizeShortcut('envio-gratis')).toBe('envio-gratis')
  })
})

describe('filterQuickReplies', () => {
  // Ordered by shortcut, as the backend sends them (D-Q7).
  const AHORRO = quickReply({
    id: 'a',
    shortcut: 'ahorro',
    body: 'Cuenta de ahorros 123.',
  })
  const CUENTA = quickReply({ id: 'b', shortcut: 'cuenta', body: 'Te paso.' })
  const CUENTA_AHORROS = quickReply({
    id: 'c',
    shortcut: 'cuenta-ahorros',
    body: 'Número 123.',
  })
  const MI_CUENTA = quickReply({ id: 'd', shortcut: 'mi-cuenta', body: 'Ok.' })
  const PAGO = quickReply({ id: 'e', shortcut: 'pago', body: 'Paga a la cuenta.' })
  const ALL = [AHORRO, CUENTA, CUENTA_AHORROS, MI_CUENTA, PAGO]

  it('returns everything for an empty term', () => {
    expect(filterQuickReplies(ALL, '  ')).toEqual(ALL)
  })

  it('puts a shortcut that starts with the term first, then one that contains it, then a body', () => {
    expect(filterQuickReplies(ALL, 'cuenta')).toEqual([
      CUENTA,
      CUENTA_AHORROS,
      MI_CUENTA,
      AHORRO,
      PAGO,
    ])
  })

  it('ignores case and accents', () => {
    const envio = quickReply({
      id: 'f',
      shortcut: 'envio-gratis',
      body: 'El envío es gratis.',
    })

    expect(filterQuickReplies([PAGO, envio], 'ENVÍO')).toEqual([envio])
  })

  it('drops what matches nowhere', () => {
    expect(filterQuickReplies(ALL, 'horario')).toEqual([])
  })
})

describe('insertQuickReply', () => {
  it('replaces the whole slash term', () => {
    expect(insertQuickReply('/hor', 'Hola Laura')).toBe('Hola Laura')
  })

  it('appends after what was typed, with one space', () => {
    expect(insertQuickReply('Buen día.  ', 'Hola Laura')).toBe(
      'Buen día. Hola Laura',
    )
  })

  it('fills an empty box', () => {
    expect(insertQuickReply('', 'Hola Laura')).toBe('Hola Laura')
  })
})

describe('quick reply option identity', () => {
  it('keys a quick reply apart from any template key', () => {
    expect(quickReplyKey({ id: 'qr-1' })).toBe('quick-reply:qr-1')
    expect(quickReplyOptionId('list', { id: 'qr-1' })).toBe('list-qr-1')
  })
})
```

- [ ] **Step 2: Correrlas y verlas fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/features/messaging/utils/resolve-quick-reply.test.ts src/features/messaging/utils/quick-replies.test.ts
```

Expected: FAIL — los dos archivos no cargan: `Failed to resolve import "@/features/messaging/utils/resolve-quick-reply"` y `"@/features/messaging/utils/quick-replies"`. Pega la salida literal en el reporte.

- [ ] **Step 3: Agregar el permiso y los tipos**

En `src/features/messaging/types/messaging.ts`, dentro de `MESSAGING_PERMISSIONS`, después de `campaignManage`:

```ts
  /**
   * Create, edit and delete the tenant's quick replies. What advisors send on the company's behalf
   * is decided by whoever runs it, so admin only by default (spec 2026-10-12, D-Q6).
   */
  quickReplyManage: 'messaging.quick_reply.manage',
```

Y **al final del archivo** (después de `MESSAGE_TEXT_MAX_LENGTH`, que se usa aquí):

```ts
/** The three closed variables of a quick reply (spec 2026-10-12, D-Q3). Any other is a domain error. */
export const QUICK_REPLY_VARIABLES = ['nombre', 'asesor', 'empresa'] as const
export type QuickReplyVariable = (typeof QUICK_REPLY_VARIABLES)[number]

/** What each variable becomes once inserted into a conversation (spec 2026-10-12 §4.3). */
export type QuickReplyValues = Record<QuickReplyVariable, string>

export const QUICK_REPLY_VARIABLE_HINTS: Record<QuickReplyVariable, string> = {
  nombre: 'El nombre del cliente',
  asesor: 'El nombre de quien responde',
  empresa: 'El nombre de tu empresa',
}

/** The shortcut as the backend stores it: lower case and without `/` (spec 2026-10-12 §3.1). */
export const QUICK_REPLY_SHORTCUT_PATTERN = /^[a-z0-9-]{2,32}$/

/** WhatsApp's text cap, the same the composer has (spec 2026-10-12 §3.1). */
export const QUICK_REPLY_BODY_MAX_LENGTH = MESSAGE_TEXT_MAX_LENGTH

/** `QuickReplyDto` (spec 2026-10-12 §3.6). */
export interface QuickReply {
  id: string
  /** Without `/`: the screens draw it. */
  shortcut: string
  /** Raw, with its variables: they are resolved on insertion, never on the server (D-Q4). */
  body: string
  updatedAt: string
  /** Travels in the `PUT` body and in the `DELETE` query; a mismatch is 412. */
  version: number
}

/**
 * Resolved by the server for whoever calls (D-Q5): the SPA knows neither its own advisor name nor,
 * inside the inbox, the tenant's display name.
 */
export interface QuickReplyVariables {
  asesor: string
  empresa: string
}

/** `QuickReplyListDto`: the whole list, ordered by shortcut, unpaged (D-Q7). */
export interface QuickReplyList {
  items: QuickReply[]
  variables: QuickReplyVariables
}

/** What the form produces. The version is added by whoever updates, from the row the person saw. */
export interface SaveQuickReplyInput {
  shortcut: string
  body: string
}

/** UI copy of the quick replies screens, shared by the form schema and the failure mapping. */
export const QUICK_REPLY_MESSAGES = {
  invalidShortcut:
    'Usa de 2 a 32 caracteres: letras sin tilde, números o guiones.',
  shortcutTaken: 'Ya tienes una respuesta rápida con ese atajo.',
  emptyBody: 'Escribe el texto de la respuesta.',
  invalidBody: 'Escribe de 1 a 4096 caracteres.',
  unknownVariable: 'Solo puedes usar {nombre}, {asesor} y {empresa}.',
  validation: 'Revisa los datos e intenta de nuevo.',
  conflict:
    'Otra persona editó esta respuesta. Recargamos la lista: revísala e intenta de nuevo.',
  gone: 'Otra persona borró esta respuesta. Recargamos la lista.',
  denied: 'No tienes permiso para gestionar respuestas rápidas.',
  network:
    'No pudimos conectarnos con el servidor. Revisa tu conexión e intenta de nuevo.',
  generic: 'No pudimos guardar el cambio. Intenta de nuevo.',
} as const
```

- [ ] **Step 4: Agregar las fixtures**

En `src/test/messaging.ts`, agrega `QuickReply`, `QuickReplyList` y `QuickReplyVariables` a la importación de tipos de `@/features/messaging/types/messaging`, y después de `messageTemplate`:

```ts
/** What `GET .../quick-replies` resolves for the caller (spec 2026-10-12, D-Q5). */
export const QUICK_REPLY_SERVER_VARIABLES: QuickReplyVariables = {
  asesor: 'Andrés',
  empresa: 'Origen Botánico',
}

/** A quick reply as the backend returns it: the shortcut without `/`. */
export function quickReply(overrides: Partial<QuickReply> = {}): QuickReply {
  return {
    id: 'qr-1',
    shortcut: 'horarios',
    body: 'Hola {nombre}, atendemos de lunes a sábado de 8 a 6.',
    updatedAt: '2026-10-12T15:00:00Z',
    version: 1,
    ...overrides,
  }
}

/** `QuickReplyListDto`, ordered by shortcut like the backend (D-Q7). */
export function quickReplyList(
  items: QuickReply[] = [
    quickReply(),
    quickReply({
      id: 'qr-2',
      shortcut: 'pago',
      body: 'Soy {asesor} de {empresa}. Puedes pagar por transferencia.',
    }),
  ],
  variables: QuickReplyVariables = QUICK_REPLY_SERVER_VARIABLES,
): QuickReplyList {
  return { items, variables }
}

/** The inbox permissions plus the one that manages quick replies (admin by default, D-Q6). */
export const QUICK_REPLY_ADMIN_PERMISSIONS = [
  ...ALL_MESSAGING_PERMISSIONS,
  'messaging.quick_reply.manage',
]
```

- [ ] **Step 5: Escribir `resolve-quick-reply.ts`**

`src/features/messaging/utils/resolve-quick-reply.ts`:

```ts
import {
  type ConversationSummary,
  QUICK_REPLY_VARIABLES,
  type QuickReplyValues,
  type QuickReplyVariable,
  type QuickReplyVariables,
} from '@/features/messaging/types/messaging'

const VARIABLE = /\{(nombre|asesor|empresa)\}/g

// For the backend every `{...}` is a variable (spec 2026-10-12 §3.2); a lone brace is text.
const ANY_VARIABLE = /\{([^{}]+)\}/g

/**
 * The body of a quick reply as it lands in the composer (spec 2026-10-12 §4.3). An empty variable
 * leaves no trace: it takes the space before it when a punctuation mark, a line break or the end
 * follows, and at the start of a line it takes the comma and the spaces after it. Double spaces the
 * body already had are kept: only the ones the variable would leave are removed.
 */
export function resolveQuickReply(
  body: string,
  values: QuickReplyValues,
): string {
  let out = ''
  let last = 0
  for (const match of body.matchAll(VARIABLE)) {
    const start = match.index ?? 0
    out += body.slice(last, start)
    last = start + match[0].length
    const value = values[match[1] as QuickReplyVariable].trim()
    if (value) {
      out += value
      continue
    }
    if (out === '' || out.endsWith('\n')) {
      // Nothing before it on the line: what follows it would start the line.
      if (/[,;:]/.test(body[last] ?? '')) last += 1
      while (body[last] === ' ' || body[last] === '\t') last += 1
      continue
    }
    const next = body[last]
    if (next === undefined || /[\s,.;:!?)]/.test(next)) {
      out = out.replace(/[ \t]+$/, '')
    }
  }
  return (out + body.slice(last)).trim()
}

/**
 * The values for one conversation. `{nombre}` is the record's name, else the WhatsApp profile name,
 * else empty — never `contactName`'s `@username` or phone: a greeting with a phone in it reads as
 * a bug. `{asesor}` and `{empresa}` come from the server, and are empty until it answers.
 */
export function quickReplyValues(
  conversation: Pick<ConversationSummary, 'customer' | 'contact'>,
  variables: QuickReplyVariables | undefined,
): QuickReplyValues {
  return {
    nombre:
      conversation.customer?.name ?? conversation.contact.profileName ?? '',
    asesor: variables?.asesor ?? '',
    empresa: variables?.empresa ?? '',
  }
}

/** What between braces the backend would reject with `messaging.quick_reply.unknown_variable`. */
export function unknownQuickReplyVariables(body: string): string[] {
  const known: readonly string[] = QUICK_REPLY_VARIABLES
  return [...body.matchAll(ANY_VARIABLE)]
    .map((match) => match[1] ?? '')
    .filter((name) => !known.includes(name))
}
```

- [ ] **Step 6: Escribir `quick-replies.ts`**

`src/features/messaging/utils/quick-replies.ts`:

```ts
import type { QuickReply } from '@/features/messaging/types/messaging'

/** A typed shortcut as the backend stores it: no `/`, no spaces around, lower case (spec §3.1). */
export function normalizeShortcut(value: string): string {
  return value.trim().replace(/^\/+/, '').toLowerCase()
}

function fold(value: string): string {
  return value
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
}

/**
 * Filters by what is typed after the `/`, in the shortcut and in the body (spec 2026-10-12 §4.2),
 * ignoring case and accents. A shortcut that starts with the term comes first, then one that
 * contains it, then a body that does: Enter takes the first one, and `/hor` must mean `/horarios`
 * even when another body mentions «ahorros». Within each group the backend order is kept.
 */
export function filterQuickReplies(
  quickReplies: QuickReply[],
  filter: string,
): QuickReply[] {
  const term = fold(filter.trim())
  if (!term) return quickReplies
  const rank = (quickReply: QuickReply): number => {
    const shortcut = fold(quickReply.shortcut)
    if (shortcut.startsWith(term)) return 0
    if (shortcut.includes(term)) return 1
    return fold(quickReply.body).includes(term) ? 2 : 3
  }
  return quickReplies
    .map((quickReply) => ({ quickReply, rank: rank(quickReply) }))
    .filter((entry) => entry.rank < 3)
    .sort((a, b) => a.rank - b.rank)
    .map((entry) => entry.quickReply)
}

/**
 * Where a chosen quick reply lands. A `/` at the start means the whole box is the term being
 * filtered, so the text replaces it (spec §4.2); from the button there may be no term, and what
 * was typed stays in front.
 */
export function insertQuickReply(text: string, resolved: string): string {
  if (text.startsWith('/')) return resolved
  const kept = text.trimEnd()
  return kept ? `${kept} ${resolved}` : resolved
}

/** The composer's keyboard runs quick replies and templates as one list: keys must never collide. */
export function quickReplyKey(quickReply: Pick<QuickReply, 'id'>): string {
  return `quick-reply:${quickReply.id}`
}

/** The id the filtering box points at with `aria-activedescendant`. */
export function quickReplyOptionId(
  listId: string,
  quickReply: Pick<QuickReply, 'id'>,
): string {
  return `${listId}-${quickReply.id}`
}
```

- [ ] **Step 7: Correrlas y verlas pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/features/messaging/utils/resolve-quick-reply.test.ts src/features/messaging/utils/quick-replies.test.ts
```

Expected: PASS — `Tests  23 passed (23)`. Pega la salida literal.

- [ ] **Step 8: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/respuestas-rapidas') { throw "rama inesperada" }
$files = @(
  'src/features/messaging/types/messaging.ts',
  'src/test/messaging.ts',
  'src/features/messaging/utils/resolve-quick-reply.ts',
  'src/features/messaging/utils/resolve-quick-reply.test.ts',
  'src/features/messaging/utils/quick-replies.ts',
  'src/features/messaging/utils/quick-replies.test.ts'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(messaging): resolución de variables de respuestas rápidas

Tipos del contrato de la spec 2026-10-12 (QuickReplyDto, variables del
llamador, permiso messaging.quick_reply.manage) y los utils puros: resolver
{nombre}, {asesor} y {empresa} sin dejar espacios dobles cuando una viene
vacía, el nombre desde la ficha o el perfil de WhatsApp, y el filtro por
atajo y cuerpo que usa el compositor.
'@
$msgFile = Join-Path $env:TEMP 'qep-respuestas-rapidas-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

Expected: la búsqueda de 0 bytes no imprime nada; `git diff --cached` lista exactamente los 6 archivos; el mensaje final no tiene trailer.

---

### Task 2: Servicio de la API, stub y hooks

**Files:**
- Modify: `src/features/messaging/services/messaging.api.ts` (imports + bloque al final)
- Modify: `src/test/messaging.ts` (opción `quickReplies` del stub)
- Create: `src/features/messaging/hooks/use-quick-replies.ts`
- Create: `src/features/messaging/hooks/use-quick-reply-mutations.ts`
- Test: `src/features/messaging/services/messaging.api.test.ts` (dos `describe` al final)
- Test: `src/features/messaging/hooks/use-quick-replies.test.tsx`
- Test: `src/features/messaging/hooks/use-quick-reply-mutations.test.tsx`

**Interfaces:**
- Consumes: `apiRequest`, `ApiError`, `NetworkError` (`@/lib/api-client`); `objectOf`, `textOf`, `nullableTextOf`, `numberOf`, `arrayOf` (`@/lib/payload-shape`); `readStatus`, `retryRead` (`@/lib/query-status`); `useActiveTenant`; tipos y `QUICK_REPLY_MESSAGES` de la Task 1.
- Produces (services): `quickRepliesQueryKey(tenantId: string): readonly ['messaging', string, 'quick-replies']`; `fetchQuickReplies(tenantId: string): Promise<QuickReplyList>`; `createQuickReply(tenantId: string, input: SaveQuickReplyInput): Promise<QuickReply>`; `updateQuickReply(tenantId: string, quickReplyId: string, input: SaveQuickReplyInput & { version: number }): Promise<QuickReply>`; `deleteQuickReply(tenantId: string, quickReplyId: string, version: number): Promise<void>`; `type QuickReplyField = 'shortcut' | 'body'`; `interface QuickReplyFailure { fields: Partial<Record<QuickReplyField, string>>; message: string | null; stale: boolean }`; `describeQuickReplyFailure(error: unknown): QuickReplyFailure`.
- Produces (hooks): `useQuickReplies({ enabled }: { enabled: boolean }): { quickReplies: QuickReply[] | undefined; variables: QuickReplyVariables | undefined; status: ReadStatus; retry: () => void }`; `QUICK_REPLIES_STALE_TIME_MS`; `useQuickReplyMutations(): UseQuickReplyMutationsResult` con `create(input: SaveQuickReplyInput): Promise<QuickReply>`, `update(quickReply: QuickReply, input: SaveQuickReplyInput): Promise<QuickReply>`, `remove(quickReply: QuickReply): Promise<void>`, `isSaving: boolean`.
- Produces (stub): `MessagingBackendOptions.quickReplies?: (request: RecordedRequest, id: string | undefined) => Answer | undefined`. Sin opción, o si devuelve `undefined`: `GET` → lista **vacía** con `QUICK_REPLY_SERVER_VARIABLES`; `POST` → `201` con lo enviado (`id: 'qr-new'`); `PUT` → `200` con lo enviado y `version + 1`; `DELETE` → `204`.

- [ ] **Step 1: Escribir las pruebas del servicio**

Agrega a las importaciones de `src/features/messaging/services/messaging.api.test.ts`: `QUICK_REPLY_MESSAGES` desde `@/features/messaging/types/messaging`; `quickReply` y `quickReplyList` a la importación de `@/test/messaging`; y `createQuickReply`, `deleteQuickReply`, `describeQuickReplyFailure`, `fetchQuickReplies`, `quickRepliesQueryKey` y `updateQuickReply` a la de `./messaging.api`. Al final del archivo:

```ts
describe('quick replies', () => {
  const QR_BASE = `/api/v1/tenants/${TENANT}/messaging/quick-replies`

  it('keys the list by tenant under messaging', () => {
    expect(quickRepliesQueryKey('t-1')).toEqual([
      'messaging',
      't-1',
      'quick-replies',
    ])
  })

  it('reads the list and the variables of whoever calls', async () => {
    const backend = stubMessagingBackend({
      quickReplies: () => jsonResponse(200, quickReplyList()),
    })

    const list = await fetchQuickReplies(TENANT)

    expect(backend.messagingRequests().at(-1)).toMatchObject({
      url: QR_BASE,
      method: 'GET',
    })
    expect(list).toEqual(quickReplyList())
  })

  it('reads a missing variable as empty, not as an error', async () => {
    stubMessagingBackend({
      quickReplies: () =>
        jsonResponse(200, {
          items: [],
          variables: { asesor: null, empresa: 'Origen Botánico' },
        }),
    })

    const list = await fetchQuickReplies(TENANT)

    expect(list.variables).toEqual({ asesor: '', empresa: 'Origen Botánico' })
  })

  it('rejects an answer without items as a shape error', async () => {
    stubMessagingBackend({ quickReplies: () => jsonResponse(200, {}) })

    await expect(fetchQuickReplies(TENANT)).rejects.toBeInstanceOf(
      PayloadShapeError,
    )
  })

  it('creates without a version: the backend ignores it on POST', async () => {
    const backend = stubMessagingBackend()

    const created = await createQuickReply(TENANT, {
      shortcut: 'horarios',
      body: 'Atendemos de 8 a 6.',
    })

    const request = backend.messagingRequests().at(-1)!
    expect(request).toMatchObject({ url: QR_BASE, method: 'POST' })
    expect(request.headers.get('X-Qep-Client')).toBe('web')
    expect(request.body).toEqual({
      shortcut: 'horarios',
      body: 'Atendemos de 8 a 6.',
    })
    expect(created).toMatchObject({ id: 'qr-new', shortcut: 'horarios' })
  })

  it('updates with the version in the body', async () => {
    const backend = stubMessagingBackend()

    const updated = await updateQuickReply(TENANT, 'qr-1', {
      shortcut: 'horarios',
      body: 'Atendemos de 9 a 5.',
      version: 3,
    })

    expect(backend.messagingRequests().at(-1)).toMatchObject({
      url: `${QR_BASE}/qr-1`,
      method: 'PUT',
      body: { shortcut: 'horarios', body: 'Atendemos de 9 a 5.', version: 3 },
    })
    expect(updated.version).toBe(4)
  })

  it('deletes with the version in the query and resolves on 204', async () => {
    const backend = stubMessagingBackend()

    await expect(
      deleteQuickReply(TENANT, 'qr-1', 3),
    ).resolves.toBeUndefined()
    expect(backend.messagingRequests().at(-1)).toMatchObject({
      url: `${QR_BASE}/qr-1?version=3`,
      method: 'DELETE',
    })
  })
})

describe('describeQuickReplyFailure', () => {
  it.each([
    [
      'messaging.quick_reply.invalid_shortcut',
      'shortcut',
      QUICK_REPLY_MESSAGES.invalidShortcut,
    ],
    [
      'messaging.quick_reply.shortcut_taken',
      'shortcut',
      QUICK_REPLY_MESSAGES.shortcutTaken,
    ],
    [
      'messaging.quick_reply.invalid_body',
      'body',
      QUICK_REPLY_MESSAGES.invalidBody,
    ],
    [
      'messaging.quick_reply.unknown_variable',
      'body',
      QUICK_REPLY_MESSAGES.unknownVariable,
    ],
  ])('marks the field of %s', (code, field, message) => {
    expect(describeQuickReplyFailure(new ApiError(422, { code }))).toEqual({
      fields: { [field]: message },
      message: null,
      stale: false,
    })
  })

  // Desviación 5: FluentValidation names the property as declared unless told otherwise.
  it('marks the fields validation.failed names, in either case, with our own text', () => {
    const failure = describeQuickReplyFailure(
      new ApiError(422, {
        code: 'validation.failed',
        errors: {
          Shortcut: ['The shortcut is invalid.'],
          body: ['Too long.'],
        },
      }),
    )

    expect(failure).toEqual({
      fields: {
        shortcut: QUICK_REPLY_MESSAGES.invalidShortcut,
        body: QUICK_REPLY_MESSAGES.invalidBody,
      },
      message: null,
      stale: false,
    })
  })

  it('sends to the top what the form cannot mark', () => {
    expect(
      describeQuickReplyFailure(
        new ApiError(422, {
          code: 'validation.failed',
          errors: { Version: ['Required.'] },
        }),
      ),
    ).toEqual({
      fields: {},
      message: QUICK_REPLY_MESSAGES.validation,
      stale: false,
    })
  })

  it('asks to re-read the list on 412 and on 404', () => {
    expect(describeQuickReplyFailure(new ApiError(412))).toEqual({
      fields: {},
      message: QUICK_REPLY_MESSAGES.conflict,
      stale: true,
    })
    expect(
      describeQuickReplyFailure(
        new ApiError(404, { code: 'messaging.quick_reply.not_found' }),
      ),
    ).toEqual({ fields: {}, message: QUICK_REPLY_MESSAGES.gone, stale: true })
  })

  it('explains a 403, the network and anything else, never with the backend detail', () => {
    expect(
      describeQuickReplyFailure(
        new ApiError(403, {
          code: 'tenancy.module_not_enabled',
          detail: 'Module messaging is not enabled.',
        }),
      ).message,
    ).toBe(QUICK_REPLY_MESSAGES.denied)
    expect(
      describeQuickReplyFailure(new NetworkError(new TypeError('offline')))
        .message,
    ).toBe(QUICK_REPLY_MESSAGES.network)
    expect(describeQuickReplyFailure(new ApiError(500)).message).toBe(
      QUICK_REPLY_MESSAGES.generic,
    )
  })
})
```

- [ ] **Step 2: Escribir las pruebas de los hooks**

`src/features/messaging/hooks/use-quick-replies.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { useQuickReplies } from '@/features/messaging/hooks/use-quick-replies'
import { fetchQuickReplies } from '@/features/messaging/services/messaging.api'
import { ApiError } from '@/lib/api-client'
import { QUICK_REPLY_SERVER_VARIABLES, quickReplyList } from '@/test/messaging'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({
  useActiveTenant: () => ({ tenantId: 't-1' }),
}))

vi.mock(
  '@/features/messaging/services/messaging.api',
  async (importOriginal) => ({
    ...(await importOriginal<
      typeof import('@/features/messaging/services/messaging.api')
    >()),
    fetchQuickReplies: vi.fn(),
  }),
)

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>
}

afterEach(() => vi.mocked(fetchQuickReplies).mockReset())

describe('useQuickReplies', () => {
  it('reads the list and the variables of the active tenant', async () => {
    vi.mocked(fetchQuickReplies).mockResolvedValue(quickReplyList())
    const { result } = renderHook(() => useQuickReplies({ enabled: true }), {
      wrapper,
    })

    await waitFor(() => expect(result.current.status).toBe('ready'))

    expect(result.current.quickReplies).toHaveLength(2)
    expect(result.current.variables).toEqual(QUICK_REPLY_SERVER_VARIABLES)
    expect(fetchQuickReplies).toHaveBeenCalledWith('t-1')
  })

  it('asks nothing while disabled', () => {
    const { result } = renderHook(() => useQuickReplies({ enabled: false }), {
      wrapper,
    })

    expect(fetchQuickReplies).not.toHaveBeenCalled()
    expect(result.current.quickReplies).toBeUndefined()
  })

  it('reports a 403 as denied', async () => {
    vi.mocked(fetchQuickReplies).mockRejectedValue(new ApiError(403))
    const { result } = renderHook(() => useQuickReplies({ enabled: true }), {
      wrapper,
    })

    await waitFor(() => expect(result.current.status).toBe('denied'))
  })
})
```

`src/features/messaging/hooks/use-quick-reply-mutations.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { useQuickReplyMutations } from '@/features/messaging/hooks/use-quick-reply-mutations'
import {
  createQuickReply,
  deleteQuickReply,
  updateQuickReply,
} from '@/features/messaging/services/messaging.api'
import { ApiError } from '@/lib/api-client'
import { quickReply } from '@/test/messaging'

vi.mock('@/features/auth/hooks/use-active-tenant', () => ({
  useActiveTenant: () => ({ tenantId: 't-1' }),
}))

vi.mock(
  '@/features/messaging/services/messaging.api',
  async (importOriginal) => ({
    ...(await importOriginal<
      typeof import('@/features/messaging/services/messaging.api')
    >()),
    createQuickReply: vi.fn(),
    updateQuickReply: vi.fn(),
    deleteQuickReply: vi.fn(),
  }),
)

const INPUT = { shortcut: 'horarios', body: 'Atendemos de 9 a 5.' }
const LIST_KEY = ['messaging', 't-1', 'quick-replies']

function setup() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const invalidate = vi.spyOn(client, 'invalidateQueries')
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  )
  const { result } = renderHook(() => useQuickReplyMutations(), { wrapper })
  return { result, invalidate }
}

afterEach(() => {
  vi.mocked(createQuickReply).mockReset()
  vi.mocked(updateQuickReply).mockReset()
  vi.mocked(deleteQuickReply).mockReset()
})

describe('useQuickReplyMutations', () => {
  it('creates with the typed values and re-reads the list', async () => {
    vi.mocked(createQuickReply).mockResolvedValue(quickReply())
    const { result, invalidate } = setup()

    await act(() => result.current.create(INPUT))

    expect(createQuickReply).toHaveBeenCalledWith('t-1', INPUT)
    expect(invalidate).toHaveBeenCalledWith({ queryKey: LIST_KEY })
  })

  it('updates with the version of the row the person saw', async () => {
    vi.mocked(updateQuickReply).mockResolvedValue(quickReply({ version: 5 }))
    const { result } = setup()

    await act(() => result.current.update(quickReply({ version: 4 }), INPUT))

    expect(updateQuickReply).toHaveBeenCalledWith('t-1', 'qr-1', {
      ...INPUT,
      version: 4,
    })
  })

  it('deletes with the version of the row', async () => {
    vi.mocked(deleteQuickReply).mockResolvedValue(undefined)
    const { result, invalidate } = setup()

    await act(() => result.current.remove(quickReply({ version: 4 })))

    expect(deleteQuickReply).toHaveBeenCalledWith('t-1', 'qr-1', 4)
    expect(invalidate).toHaveBeenCalledWith({ queryKey: LIST_KEY })
  })

  // A 412 resent with a fresh version would overwrite the other person's change.
  it('re-reads the list after a 412, rejects with it, and never retries', async () => {
    const conflict = new ApiError(412)
    vi.mocked(updateQuickReply).mockRejectedValue(conflict)
    const { result, invalidate } = setup()

    await act(async () => {
      await expect(
        result.current.update(quickReply(), INPUT),
      ).rejects.toBe(conflict)
    })

    expect(updateQuickReply).toHaveBeenCalledTimes(1)
    expect(invalidate).toHaveBeenCalledWith({ queryKey: LIST_KEY })
  })
})
```

- [ ] **Step 3: Correrlas y verlas fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/features/messaging/services/messaging.api.test.ts src/features/messaging/hooks/use-quick-replies.test.tsx src/features/messaging/hooks/use-quick-reply-mutations.test.tsx
```

Expected: FAIL — `messaging.api.test.ts` falla con `createQuickReply is not a function` (o `SyntaxError: The requested module './messaging.api' does not provide an export named 'createQuickReply'`), y los dos hooks no cargan: `Failed to resolve import "@/features/messaging/hooks/use-quick-replies"` / `"...use-quick-reply-mutations"`. Pega la salida literal.

- [ ] **Step 4: Agregar la opción del stub**

En `src/test/messaging.ts` (`RecordedRequest` y `jsonResponse` ya se importan desde `@/test/integrations`), agrega a `MessagingBackendOptions`, después de `customers`:

```ts
  /**
   * `/messaging/quick-replies[/{id}]`, any method. `undefined` falls through to the default: an
   * empty list on GET, so no older test sees quick replies in its popover, and the writes echoed.
   */
  quickReplies?: (
    request: RecordedRequest,
    id: string | undefined,
  ) => Answer | undefined
```

Dentro de `stubMessagingBackend`, **antes** de `const templatesMatch = ...`:

```ts
    const quickRepliesMatch = path.match(
      /\/messaging\/quick-replies(?:\/([^/]+))?$/,
    )
    if (quickRepliesMatch) {
      const id = quickRepliesMatch[1]
      return settle(
        options.quickReplies?.(request, id) ??
          defaultQuickReplyAnswer(request, id),
      )
    }
```

Y al final del archivo:

```ts
/** What a backend that accepts everything answers on `/messaging/quick-replies`. */
function defaultQuickReplyAnswer(
  request: RecordedRequest,
  id: string | undefined,
): Response {
  if (request.method === 'DELETE') return new Response(null, { status: 204 })
  if (request.method === 'GET') return jsonResponse(200, quickReplyList([]))
  const input = request.body as {
    shortcut: string
    body: string
    version?: number
  }
  return jsonResponse(
    request.method === 'POST' ? 201 : 200,
    quickReply({
      id: id ?? 'qr-new',
      shortcut: input.shortcut,
      body: input.body,
      version: (input.version ?? 0) + 1,
    }),
  )
}
```

- [ ] **Step 5: Escribir el servicio**

En `src/features/messaging/services/messaging.api.ts`, agrega a la importación de `@/features/messaging/types/messaging`: `QUICK_REPLY_MESSAGES`, `type QuickReply`, `type QuickReplyList` y `type SaveQuickReplyInput`. Al final del archivo:

```ts
// --- Quick replies (spec 2026-10-12) ------------------------------------------------------------

/** One list per tenant: the settings page and every thread read the same cache. */
export const quickRepliesQueryKey = (tenantId: string) =>
  ['messaging', tenantId, 'quick-replies'] as const

/**
 * Requires `messaging.conversation.read`. Every quick reply of the tenant ordered by shortcut,
 * unpaged (D-Q7), plus `{asesor}` and `{empresa}` resolved for the caller (D-Q5). A variable the
 * server could not resolve reads as empty: the text still goes in, and the advisor sees the gap.
 */
export async function fetchQuickReplies(
  tenantId: string,
): Promise<QuickReplyList> {
  const payload = objectOf(
    await apiRequest<unknown>(`${messagingPath(tenantId)}/quick-replies`),
    'quickReplies',
  )
  const variables = objectOf(payload.variables, 'quickReplies.variables')
  return {
    items: arrayOf(payload, 'items', 'quickReplies').map(parseQuickReply),
    variables: {
      asesor:
        nullableTextOf(variables, 'asesor', 'quickReplies.variables') ?? '',
      empresa:
        nullableTextOf(variables, 'empresa', 'quickReplies.variables') ?? '',
    },
  }
}

/** Requires `messaging.quick_reply.manage`. The backend ignores `version` on POST, so it is not sent. */
export async function createQuickReply(
  tenantId: string,
  input: SaveQuickReplyInput,
): Promise<QuickReply> {
  return parseQuickReply(
    await apiRequest<unknown>(`${messagingPath(tenantId)}/quick-replies`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ shortcut: input.shortcut, body: input.body }),
    }),
  )
}

/** Requires `messaging.quick_reply.manage`. The version travels in the body (spec §3.6); a mismatch is 412. */
export async function updateQuickReply(
  tenantId: string,
  quickReplyId: string,
  input: SaveQuickReplyInput & { version: number },
): Promise<QuickReply> {
  return parseQuickReply(
    await apiRequest<unknown>(
      `${messagingPath(tenantId)}/quick-replies/${quickReplyId}`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          shortcut: input.shortcut,
          body: input.body,
          version: input.version,
        }),
      },
    ),
  )
}

/** Requires `messaging.quick_reply.manage`. The version travels in the query (spec §3.6). `204`. */
export async function deleteQuickReply(
  tenantId: string,
  quickReplyId: string,
  version: number,
): Promise<void> {
  await apiRequest<void>(
    `${messagingPath(tenantId)}/quick-replies/${quickReplyId}?version=${version}`,
    { method: 'DELETE' },
  )
}

export type QuickReplyField = 'shortcut' | 'body'

const QUICK_REPLY_FIELDS: readonly QuickReplyField[] = ['shortcut', 'body']

export interface QuickReplyFailure {
  /** Our own text per field: the backend's is not shown (Desviación 5). */
  fields: Partial<Record<QuickReplyField, string>>
  /** What goes above the form or in the confirmation; null when a field says it all. */
  message: string | null
  /** 412 or 404: the list on screen is stale. Close the form and re-read; never resend. */
  stale: boolean
}

// The domain 422s come without the `errors` map (`ApiExceptionHandler`): the code says the field.
const QUICK_REPLY_CODE_FIELDS: Record<
  string,
  { field: QuickReplyField; message: string }
> = {
  'messaging.quick_reply.invalid_shortcut': {
    field: 'shortcut',
    message: QUICK_REPLY_MESSAGES.invalidShortcut,
  },
  'messaging.quick_reply.shortcut_taken': {
    field: 'shortcut',
    message: QUICK_REPLY_MESSAGES.shortcutTaken,
  },
  'messaging.quick_reply.invalid_body': {
    field: 'body',
    message: QUICK_REPLY_MESSAGES.invalidBody,
  },
  'messaging.quick_reply.unknown_variable': {
    field: 'body',
    message: QUICK_REPLY_MESSAGES.unknownVariable,
  },
}

const FIELD_MESSAGES: Record<QuickReplyField, string> = {
  shortcut: QUICK_REPLY_MESSAGES.invalidShortcut,
  body: QUICK_REPLY_MESSAGES.invalidBody,
}

/** Turns a failed write on a quick reply into something the form or the page can act on. */
export function describeQuickReplyFailure(error: unknown): QuickReplyFailure {
  const none: QuickReplyFailure = { fields: {}, message: null, stale: false }
  if (error instanceof NetworkError) {
    return { ...none, message: QUICK_REPLY_MESSAGES.network }
  }
  if (!(error instanceof ApiError)) {
    return { ...none, message: QUICK_REPLY_MESSAGES.generic }
  }
  if (error.status === 412) {
    return { ...none, message: QUICK_REPLY_MESSAGES.conflict, stale: true }
  }
  if (error.status === 404) {
    return { ...none, message: QUICK_REPLY_MESSAGES.gone, stale: true }
  }
  if (error.status === 403) {
    return { ...none, message: QUICK_REPLY_MESSAGES.denied }
  }
  if (error.status === 422) {
    const byCode = error.code ? QUICK_REPLY_CODE_FIELDS[error.code] : undefined
    if (byCode) return { ...none, fields: { [byCode.field]: byCode.message } }
    const fields: Partial<Record<QuickReplyField, string>> = {}
    let unplaced = false
    for (const key of Object.keys(error.errors ?? {})) {
      // FluentValidation keeps the declared name (`Shortcut`); the spec writes `shortcut`.
      const field = QUICK_REPLY_FIELDS.find((name) => name === key.toLowerCase())
      if (field) fields[field] = FIELD_MESSAGES[field]
      else unplaced = true
    }
    const marked = Object.keys(fields).length > 0
    return {
      ...none,
      fields,
      message: unplaced || !marked ? QUICK_REPLY_MESSAGES.validation : null,
    }
  }
  return { ...none, message: QUICK_REPLY_MESSAGES.generic }
}

function parseQuickReply(payload: unknown): QuickReply {
  const item = objectOf(payload, 'quickReply')
  return {
    id: textOf(item, 'id', 'quickReply'),
    shortcut: textOf(item, 'shortcut', 'quickReply'),
    body: textOf(item, 'body', 'quickReply'),
    updatedAt: textOf(item, 'updatedAt', 'quickReply'),
    version: numberOf(item, 'version', 'quickReply'),
  }
}
```

- [ ] **Step 6: Escribir los hooks**

`src/features/messaging/hooks/use-quick-replies.ts`:

```ts
import { useQuery } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'
import {
  fetchQuickReplies,
  quickRepliesQueryKey,
} from '@/features/messaging/services/messaging.api'
import { readStatus, retryRead } from '@/lib/query-status'

/**
 * A minute fresh: switching threads remounts the composer and must not ask again each time, and a
 * change made in Settings invalidates the key anyway (`useQuickReplyMutations`).
 */
export const QUICK_REPLIES_STALE_TIME_MS = 60_000

/** The tenant's quick replies and the caller's `{asesor}`/`{empresa}` (spec 2026-10-12 §3.6). */
export function useQuickReplies({ enabled }: { enabled: boolean }) {
  const { tenantId } = useActiveTenant()
  const query = useQuery({
    queryKey: quickRepliesQueryKey(tenantId ?? 'none'),
    queryFn: () => fetchQuickReplies(tenantId!),
    enabled: enabled && tenantId !== null,
    staleTime: QUICK_REPLIES_STALE_TIME_MS,
    retry: retryRead,
  })

  return {
    quickReplies: query.data?.items,
    variables: query.data?.variables,
    status: readStatus(query, tenantId),
    retry: () => void query.refetch(),
  }
}
```

`src/features/messaging/hooks/use-quick-reply-mutations.ts`:

```ts
import { useMutation, useQueryClient } from '@tanstack/react-query'

import { useActiveTenant } from '@/features/auth/hooks/use-active-tenant'
import {
  createQuickReply,
  deleteQuickReply,
  quickRepliesQueryKey,
  updateQuickReply,
} from '@/features/messaging/services/messaging.api'
import type {
  QuickReply,
  SaveQuickReplyInput,
} from '@/features/messaging/types/messaging'

/** One mutation for every write, so "something is saving" has a single source. */
type QuickReplyCommand =
  | { kind: 'create'; input: SaveQuickReplyInput }
  | { kind: 'update'; quickReply: QuickReply; input: SaveQuickReplyInput }
  | { kind: 'delete'; quickReply: QuickReply }

function runCommand(
  tenantId: string,
  command: QuickReplyCommand,
): Promise<QuickReply | undefined> {
  switch (command.kind) {
    case 'create':
      return createQuickReply(tenantId, command.input)
    case 'update':
      // The version of the row the person opened: never the one a refetch brought meanwhile.
      return updateQuickReply(tenantId, command.quickReply.id, {
        ...command.input,
        version: command.quickReply.version,
      })
    case 'delete':
      return deleteQuickReply(
        tenantId,
        command.quickReply.id,
        command.quickReply.version,
      ).then(() => undefined)
  }
}

export interface UseQuickReplyMutationsResult {
  create: (input: SaveQuickReplyInput) => Promise<QuickReply>
  update: (
    quickReply: QuickReply,
    input: SaveQuickReplyInput,
  ) => Promise<QuickReply>
  remove: (quickReply: QuickReply) => Promise<void>
  isSaving: boolean
}

/**
 * Every write on the tenant's quick replies. Each one rejects with the request error: the form
 * needs it to mark its fields, and the page to explain a 412.
 */
export function useQuickReplyMutations(): UseQuickReplyMutationsResult {
  const { tenantId } = useActiveTenant()
  const queryClient = useQueryClient()

  const mutation = useMutation({
    mutationFn: (command: QuickReplyCommand) => runCommand(tenantId!, command),
    // Never retried: a 412 resent with a fresh version is the silent overwrite the version exists
    // to prevent.
    retry: false,
    // Re-read instead of patching, on success and on failure: a 412 or a 404 means the list is
    // stale, and patching races a GET in flight (memoria setquerydata-pisado-por-get-en-vuelo).
    // Returned, so the caller's await ends with the fresh list.
    onSettled: () =>
      queryClient.invalidateQueries({
        queryKey: quickRepliesQueryKey(tenantId!),
      }),
  })

  const { mutateAsync } = mutation

  return {
    create: async (input) =>
      (await mutateAsync({ kind: 'create', input })) as QuickReply,
    update: async (quickReply, input) =>
      (await mutateAsync({ kind: 'update', quickReply, input })) as QuickReply,
    remove: async (quickReply) => {
      await mutateAsync({ kind: 'delete', quickReply })
    },
    isSaving: mutation.isPending,
  }
}
```

- [ ] **Step 7: Correrlas y verlas pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/features/messaging/services/messaging.api.test.ts src/features/messaging/hooks/use-quick-replies.test.tsx src/features/messaging/hooks/use-quick-reply-mutations.test.tsx
```

Expected: PASS — todas las pruebas de los tres archivos, incluidas las que `messaging.api.test.ts` ya tenía (anota el total que imprime). Pega la salida literal.

- [ ] **Step 8: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/respuestas-rapidas') { throw "rama inesperada" }
$files = @(
  'src/features/messaging/services/messaging.api.ts',
  'src/features/messaging/services/messaging.api.test.ts',
  'src/test/messaging.ts',
  'src/features/messaging/hooks/use-quick-replies.ts',
  'src/features/messaging/hooks/use-quick-replies.test.tsx',
  'src/features/messaging/hooks/use-quick-reply-mutations.ts',
  'src/features/messaging/hooks/use-quick-reply-mutations.test.tsx'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(messaging): servicio y hooks de respuestas rápidas

Las cuatro llamadas de la spec 2026-10-12 §3.6 (la versión en el cuerpo
del PUT y en la query del DELETE), la traducción de cada código a su campo
o a un aviso, la lectura con las variables del llamador y una sola
mutación que relee la lista al asentarse, sin reintentar un 412.
'@
$msgFile = Join-Path $env:TEMP 'qep-respuestas-rapidas-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

Expected: 0 bytes vacío; exactamente los 7 archivos; sin trailer.

---

### Task 3: Pantalla de gestión, formulario y confirmación

**Files:**
- Create: `src/features/messaging/types/quick-reply-form.schema.ts`
- Create: `src/features/messaging/components/quick-reply-form-dialog.tsx`
- Create: `src/features/messaging/components/delete-quick-reply-dialog.tsx`
- Create: `src/features/messaging/pages/quick-replies-page.tsx`
- Test: `src/features/messaging/components/quick-reply-form-dialog.test.tsx`
- Test: `src/features/messaging/pages/quick-replies-page.test.tsx`

**Interfaces:**
- Consumes: `describeQuickReplyFailure` (Task 2); `normalizeShortcut` (Task 1); `unknownQuickReplyVariables` (Task 1); `QUICK_REPLY_*` y tipos (Task 1); `Dialog*`, `Button`, `InputGroup*`, `Label`, `Textarea`, `PageContainer`.
- Produces: `quickReplyFormSchema`, `type QuickReplyFormValues = { shortcut: string; body: string }`; `QuickReplyFormDialog(props: QuickReplyFormDialogProps)` con `{ quickReply: QuickReply | null; onSave: (input: SaveQuickReplyInput) => Promise<void>; onStale: (message: string) => void; onClose: () => void }`; `DeleteQuickReplyDialog(props: { quickReply: QuickReply; isDeleting: boolean; failure: string | null; onConfirm: () => void; onCancel: () => void })`; `QuickRepliesPage(props: QuickRepliesPageProps)` con `{ status: ReadStatus; quickReplies: QuickReply[] | undefined; onRetry: () => void; onCreate: (input: SaveQuickReplyInput) => Promise<QuickReply>; onUpdate: (quickReply: QuickReply, input: SaveQuickReplyInput) => Promise<QuickReply>; onDelete: (quickReply: QuickReply) => Promise<void> }`.

- [ ] **Step 1: Escribir la prueba del formulario**

`src/features/messaging/components/quick-reply-form-dialog.test.tsx`:

```tsx
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ComponentProps } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { QuickReplyFormDialog } from '@/features/messaging/components/quick-reply-form-dialog'
import { QUICK_REPLY_MESSAGES } from '@/features/messaging/types/messaging'
import { ApiError, NetworkError } from '@/lib/api-client'
import { quickReply } from '@/test/messaging'

function renderDialog(
  overrides: Partial<ComponentProps<typeof QuickReplyFormDialog>> = {},
) {
  const props: ComponentProps<typeof QuickReplyFormDialog> = {
    quickReply: null,
    onSave: vi.fn(async () => {}),
    onStale: vi.fn(),
    onClose: vi.fn(),
    ...overrides,
  }
  return { ...render(<QuickReplyFormDialog {...props} />), props }
}

const shortcut = () => screen.getByLabelText('Atajo')
const body = () => screen.getByLabelText('Respuesta') as HTMLTextAreaElement

async function fill(user: ReturnType<typeof userEvent.setup>, values: {
  shortcut: string
  body: string
}) {
  await user.type(shortcut(), values.shortcut)
  await user.click(body())
  if (values.body) await user.paste(values.body)
}

describe('QuickReplyFormDialog', () => {
  it('inserts a variable where the cursor is and leaves the cursor after it', async () => {
    const user = userEvent.setup()
    renderDialog()
    await user.click(body())
    await user.paste('Hola , ¿en qué te ayudo?')
    body().setSelectionRange(5, 5)

    await user.click(screen.getByRole('button', { name: '{nombre}' }))

    expect(body()).toHaveValue('Hola {nombre}, ¿en qué te ayudo?')
    expect(body().selectionStart).toBe(13)
  })

  it('counts the characters of the body against 4096', async () => {
    const user = userEvent.setup()
    renderDialog()

    await user.click(body())
    await user.paste('Hola')

    expect(screen.getByText('4 / 4096')).toBeInTheDocument()
  })

  it('saves the shortcut without the slash, in lower case, and the body trimmed', async () => {
    const user = userEvent.setup()
    const { props } = renderDialog()

    await fill(user, { shortcut: '/Horarios', body: '  Atendemos de 8 a 6.  ' })
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(props.onSave).toHaveBeenCalledWith({
      shortcut: 'horarios',
      body: 'Atendemos de 8 a 6.',
    })
  })

  it('refuses a shortcut the backend pattern would reject, before asking', async () => {
    const user = userEvent.setup()
    const { props } = renderDialog()

    await fill(user, { shortcut: 'envío gratis', body: 'Gratis desde 100.000.' })
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText(QUICK_REPLY_MESSAGES.invalidShortcut),
    ).toBeInTheDocument()
    expect(shortcut()).toHaveAttribute('aria-invalid', 'true')
    expect(props.onSave).not.toHaveBeenCalled()
  })

  it('refuses a variable that is not one of the three', async () => {
    const user = userEvent.setup()
    const { props } = renderDialog()

    await fill(user, { shortcut: 'hola', body: 'Hola {cliente}' })
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText(QUICK_REPLY_MESSAGES.unknownVariable),
    ).toBeInTheDocument()
    expect(props.onSave).not.toHaveBeenCalled()
  })

  it('refuses an empty body', async () => {
    const user = userEvent.setup()
    renderDialog()

    await fill(user, { shortcut: 'hola', body: '' })
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText(QUICK_REPLY_MESSAGES.emptyBody),
    ).toBeInTheDocument()
  })

  it('marks the shortcut when the server says it is taken', async () => {
    const user = userEvent.setup()
    renderDialog({
      onSave: vi.fn(async () => {
        throw new ApiError(422, {
          code: 'messaging.quick_reply.shortcut_taken',
        })
      }),
    })

    await fill(user, { shortcut: 'horarios', body: 'Atendemos de 8 a 6.' })
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText(QUICK_REPLY_MESSAGES.shortcutTaken),
    ).toBeInTheDocument()
    expect(shortcut()).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('marks both fields a validation.failed names, whatever their case', async () => {
    const user = userEvent.setup()
    renderDialog({
      onSave: vi.fn(async () => {
        throw new ApiError(422, {
          code: 'validation.failed',
          errors: { Shortcut: ['Invalid.'], body: ['Too long.'] },
        })
      }),
    })

    await fill(user, { shortcut: 'horarios', body: 'Atendemos de 8 a 6.' })
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText(QUICK_REPLY_MESSAGES.invalidShortcut),
    ).toBeInTheDocument()
    expect(screen.getByText(QUICK_REPLY_MESSAGES.invalidBody)).toBeInTheDocument()
    expect(body()).toHaveAttribute('aria-invalid', 'true')
  })

  it('hands a 412 to the page instead of offering to resend', async () => {
    const user = userEvent.setup()
    const { props } = renderDialog({
      quickReply: quickReply(),
      onSave: vi.fn(async () => {
        throw new ApiError(412)
      }),
    })

    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(props.onStale).toHaveBeenCalledWith(QUICK_REPLY_MESSAGES.conflict),
    )
  })

  it('says a network failure at the top and keeps what was typed', async () => {
    const user = userEvent.setup()
    renderDialog({
      onSave: vi.fn(async () => {
        throw new NetworkError(new TypeError('offline'))
      }),
    })

    await fill(user, { shortcut: 'horarios', body: 'Atendemos de 8 a 6.' })
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      QUICK_REPLY_MESSAGES.network,
    )
    expect(body()).toHaveValue('Atendemos de 8 a 6.')
  })

  it('starts an edit from the row', () => {
    renderDialog({ quickReply: quickReply() })

    expect(
      screen.getByRole('dialog', { name: 'Editar respuesta rápida' }),
    ).toBeInTheDocument()
    expect(shortcut()).toHaveValue('horarios')
    expect(body()).toHaveValue(quickReply().body)
  })
})
```

- [ ] **Step 2: Escribir la prueba de la página**

`src/features/messaging/pages/quick-replies-page.test.tsx`:

```tsx
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ComponentProps } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { QuickRepliesPage } from '@/features/messaging/pages/quick-replies-page'
import {
  QUICK_REPLY_MESSAGES,
  type QuickReply,
  type SaveQuickReplyInput,
} from '@/features/messaging/types/messaging'
import { ApiError } from '@/lib/api-client'
import { quickReply } from '@/test/messaging'

const HORARIOS = quickReply()
const PAGO = quickReply({
  id: 'qr-2',
  shortcut: 'pago',
  body: 'Soy {asesor}.\nPaga por transferencia.',
  version: 4,
})

function renderPage(
  overrides: Partial<ComponentProps<typeof QuickRepliesPage>> = {},
) {
  const props: ComponentProps<typeof QuickRepliesPage> = {
    status: 'ready',
    quickReplies: [HORARIOS, PAGO],
    onRetry: vi.fn(),
    onCreate: vi.fn(async (input: SaveQuickReplyInput) =>
      quickReply({ id: 'qr-new', ...input }),
    ),
    onUpdate: vi.fn(async (row: QuickReply, input: SaveQuickReplyInput) => ({
      ...row,
      ...input,
      version: row.version + 1,
    })),
    onDelete: vi.fn(async () => {}),
    ...overrides,
  }
  return { ...render(<QuickRepliesPage {...props} />), props }
}

describe('QuickRepliesPage', () => {
  it('lists each quick reply with its slash shortcut and a one-line preview', () => {
    renderPage()

    const rows = screen.getAllByRole('row').slice(1)
    expect(rows).toHaveLength(2)
    expect(
      within(rows[0]!).getByRole('cell', { name: '/horarios' }),
    ).toBeInTheDocument()
    expect(within(rows[1]!).getByText(/Soy \{asesor\}/)).toHaveClass(
      'truncate',
    )
  })

  it('says there are none yet and creates the first one', async () => {
    const user = userEvent.setup()
    const { props } = renderPage({ quickReplies: [] })

    expect(
      screen.getByText('Aún no tienes respuestas rápidas'),
    ).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Crear la primera' }))
    const dialog = screen.getByRole('dialog', {
      name: 'Nueva respuesta rápida',
    })
    await user.type(within(dialog).getByLabelText('Atajo'), 'horarios')
    await user.click(within(dialog).getByLabelText('Respuesta'))
    await user.paste('Atendemos de 8 a 6.')
    await user.click(within(dialog).getByRole('button', { name: 'Guardar' }))

    expect(props.onCreate).toHaveBeenCalledWith({
      shortcut: 'horarios',
      body: 'Atendemos de 8 a 6.',
    })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })

  it.each([
    ['loading', 'Cargando respuestas rápidas…'],
    ['denied', 'No tienes permiso para ver las respuestas rápidas.'],
    ['error', 'No pudimos cargar las respuestas rápidas.'],
  ] as const)('says so while the list is %s', (status, text) => {
    renderPage({ status, quickReplies: undefined })

    expect(screen.getByRole('status')).toHaveTextContent(text)
  })

  it('retries a list that failed to load', async () => {
    const user = userEvent.setup()
    const { props } = renderPage({ status: 'error', quickReplies: undefined })

    await user.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(props.onRetry).toHaveBeenCalledTimes(1)
  })

  it('edits a row from its values and saves through onUpdate', async () => {
    const user = userEvent.setup()
    const { props } = renderPage()

    await user.click(screen.getByRole('button', { name: 'Editar /pago' }))
    const dialog = screen.getByRole('dialog', {
      name: 'Editar respuesta rápida',
    })
    const shortcut = within(dialog).getByLabelText('Atajo')
    await user.clear(shortcut)
    await user.type(shortcut, 'pagos')
    await user.click(within(dialog).getByRole('button', { name: 'Guardar' }))

    expect(props.onUpdate).toHaveBeenCalledWith(PAGO, {
      shortcut: 'pagos',
      body: PAGO.body,
    })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })

  it('keeps the form open and marks a taken shortcut', async () => {
    const user = userEvent.setup()
    renderPage({
      onCreate: vi.fn(async () => {
        throw new ApiError(422, {
          code: 'messaging.quick_reply.shortcut_taken',
        })
      }),
    })

    await user.click(screen.getByRole('button', { name: 'Nueva respuesta' }))
    const dialog = screen.getByRole('dialog')
    await user.type(within(dialog).getByLabelText('Atajo'), 'pago')
    await user.click(within(dialog).getByLabelText('Respuesta'))
    await user.paste('Otra cuenta.')
    await user.click(within(dialog).getByRole('button', { name: 'Guardar' }))

    expect(
      await within(dialog).findByText(QUICK_REPLY_MESSAGES.shortcutTaken),
    ).toBeInTheDocument()
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('closes the form and says why when someone else edited the row (412)', async () => {
    const user = userEvent.setup()
    renderPage({
      onUpdate: vi.fn(async () => {
        throw new ApiError(412)
      }),
    })

    await user.click(screen.getByRole('button', { name: 'Editar /horarios' }))
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Otra persona editó esta respuesta',
    )
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('deletes after confirming', async () => {
    const user = userEvent.setup()
    const { props } = renderPage()

    await user.click(screen.getByRole('button', { name: 'Borrar /pago' }))
    const dialog = screen.getByRole('dialog', { name: '¿Borrar /pago?' })
    await user.click(within(dialog).getByRole('button', { name: 'Borrar' }))

    expect(props.onDelete).toHaveBeenCalledWith(PAGO)
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })

  it('keeps the row when the deletion is cancelled', async () => {
    const user = userEvent.setup()
    const { props } = renderPage()

    await user.click(screen.getByRole('button', { name: 'Borrar /pago' }))
    await user.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(props.onDelete).not.toHaveBeenCalled()
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('closes the confirmation and says why when the row changed underneath (412)', async () => {
    const user = userEvent.setup()
    renderPage({
      onDelete: vi.fn(async () => {
        throw new ApiError(412)
      }),
    })

    await user.click(screen.getByRole('button', { name: 'Borrar /pago' }))
    await user.click(
      within(screen.getByRole('dialog')).getByRole('button', {
        name: 'Borrar',
      }),
    )

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Otra persona editó esta respuesta',
    )
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('keeps the confirmation open with the reason when the deletion fails otherwise', async () => {
    const user = userEvent.setup()
    renderPage({
      onDelete: vi.fn(async () => {
        throw new ApiError(500)
      }),
    })

    await user.click(screen.getByRole('button', { name: 'Borrar /pago' }))
    const dialog = screen.getByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Borrar' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      QUICK_REPLY_MESSAGES.generic,
    )
  })
})
```

- [ ] **Step 3: Correrlas y verlas fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/features/messaging/components/quick-reply-form-dialog.test.tsx src/features/messaging/pages/quick-replies-page.test.tsx
```

Expected: FAIL — los dos archivos no cargan: `Failed to resolve import "@/features/messaging/components/quick-reply-form-dialog"` y `"@/features/messaging/pages/quick-replies-page"`. Pega la salida literal.

- [ ] **Step 4: Escribir el schema**

`src/features/messaging/types/quick-reply-form.schema.ts`:

```ts
import { z } from 'zod'

import {
  QUICK_REPLY_BODY_MAX_LENGTH,
  QUICK_REPLY_MESSAGES,
  QUICK_REPLY_SHORTCUT_PATTERN,
} from '@/features/messaging/types/messaging'
import { normalizeShortcut } from '@/features/messaging/utils/quick-replies'
import { unknownQuickReplyVariables } from '@/features/messaging/utils/resolve-quick-reply'

/**
 * Client-side mirror of the rules of `QuickReply` (spec 2026-10-12 §3.2), as a courtesy: the
 * backend rules regardless. The shortcut is checked as it will travel — without `/`, lower case —
 * so `/Horarios` passes and is sent as `horarios`.
 */
export const quickReplyFormSchema = z.object({
  shortcut: z
    .string()
    .refine(
      (value) => QUICK_REPLY_SHORTCUT_PATTERN.test(normalizeShortcut(value)),
      QUICK_REPLY_MESSAGES.invalidShortcut,
    ),
  body: z
    .string()
    .trim()
    .min(1, QUICK_REPLY_MESSAGES.emptyBody)
    .max(QUICK_REPLY_BODY_MAX_LENGTH, QUICK_REPLY_MESSAGES.invalidBody)
    .refine(
      (value) => unknownQuickReplyVariables(value).length === 0,
      QUICK_REPLY_MESSAGES.unknownVariable,
    ),
})

export type QuickReplyFormValues = z.infer<typeof quickReplyFormSchema>
```

- [ ] **Step 5: Escribir el formulario**

`src/features/messaging/components/quick-reply-form-dialog.tsx`:

```tsx
import { useRef, useState } from 'react'

import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'

import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  InputGroup,
  InputGroupAddon,
  InputGroupInput,
  InputGroupText,
} from '@/components/ui/input-group'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { describeQuickReplyFailure } from '@/features/messaging/services/messaging.api'
import {
  QUICK_REPLY_BODY_MAX_LENGTH,
  QUICK_REPLY_MESSAGES,
  QUICK_REPLY_VARIABLE_HINTS,
  QUICK_REPLY_VARIABLES,
  type QuickReply,
  type QuickReplyVariable,
  type SaveQuickReplyInput,
} from '@/features/messaging/types/messaging'
import {
  quickReplyFormSchema,
  type QuickReplyFormValues,
} from '@/features/messaging/types/quick-reply-form.schema'
import { normalizeShortcut } from '@/features/messaging/utils/quick-replies'
import { cn } from '@/lib/utils'

const FORM_ID = 'quick-reply-form'

export interface QuickReplyFormDialogProps {
  /** `null` creates one; a row edits it, with the values and the version the person saw. */
  quickReply: QuickReply | null
  /** Rejects with the request error, so the form can mark its fields. */
  onSave: (input: SaveQuickReplyInput) => Promise<void>
  /** A 412 or a 404: the list underneath is stale. The page closes the form and says why. */
  onStale: (message: string) => void
  onClose: () => void
}

/**
 * Create or edit a quick reply (spec 2026-10-12 §4.1). The `/` in front of the shortcut is only
 * drawn: it never travels. The chips write a variable where the caret is, because the person is
 * mid-sentence when they need one. Mounted only while open, so each opening starts from its row.
 */
export function QuickReplyFormDialog({
  quickReply,
  onSave,
  onStale,
  onClose,
}: QuickReplyFormDialogProps) {
  const isEdit = quickReply !== null
  const [failure, setFailure] = useState<string | null>(null)
  const bodyElement = useRef<HTMLTextAreaElement | null>(null)
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    getValues,
    watch,
    formState: { errors, isSubmitting, isSubmitted },
  } = useForm<QuickReplyFormValues>({
    resolver: zodResolver(quickReplyFormSchema),
    defaultValues: {
      shortcut: quickReply?.shortcut ?? '',
      body: quickReply?.body ?? '',
    },
  })
  const bodyField = register('body')
  const bodyLength = watch('body').length

  const insertVariable = (variable: QuickReplyVariable) => {
    const element = bodyElement.current
    const current = getValues('body')
    const start = element?.selectionStart ?? current.length
    const end = element?.selectionEnd ?? current.length
    const token = `{${variable}}`
    setValue('body', current.slice(0, start) + token + current.slice(end), {
      shouldDirty: true,
      shouldValidate: isSubmitted,
    })
    // `setValue` writes the DOM value of a registered native field at once: the caret can move now.
    const caret = start + token.length
    element?.focus()
    element?.setSelectionRange(caret, caret)
  }

  const submit = async (values: QuickReplyFormValues) => {
    setFailure(null)
    try {
      await onSave({
        shortcut: normalizeShortcut(values.shortcut),
        body: values.body,
      })
    } catch (error) {
      const described = describeQuickReplyFailure(error)
      if (described.stale) {
        onStale(described.message ?? QUICK_REPLY_MESSAGES.conflict)
        return
      }
      for (const [field, message] of Object.entries(described.fields)) {
        setError(field as keyof QuickReplyFormValues, { message })
      }
      setFailure(described.message)
    }
  }

  return (
    <Dialog
      open
      onOpenChange={(next) => {
        if (!next && !isSubmitting) onClose()
      }}
    >
      <DialogContent showCloseButton={!isSubmitting}>
        <DialogHeader>
          <DialogTitle>
            {isEdit ? 'Editar respuesta rápida' : 'Nueva respuesta rápida'}
          </DialogTitle>
          <DialogDescription>
            En la caja de Mensajería, escribe / y el atajo para insertarla.
          </DialogDescription>
        </DialogHeader>

        <form
          noValidate
          id={FORM_ID}
          className="space-y-4"
          onSubmit={handleSubmit(submit)}
        >
          {failure ? (
            <p role="alert" className="text-sm text-destructive">
              {failure}
            </p>
          ) : null}

          <div className="space-y-1.5">
            <Label htmlFor="quick-reply-shortcut">Atajo</Label>
            <InputGroup>
              <InputGroupAddon>
                <InputGroupText aria-hidden="true">/</InputGroupText>
              </InputGroupAddon>
              <InputGroupInput
                id="quick-reply-shortcut"
                placeholder="horarios"
                autoComplete="off"
                spellCheck={false}
                aria-invalid={Boolean(errors.shortcut)}
                aria-describedby={
                  errors.shortcut
                    ? 'quick-reply-shortcut-error'
                    : 'quick-reply-shortcut-hint'
                }
                {...register('shortcut')}
              />
            </InputGroup>
            {errors.shortcut ? (
              <p
                id="quick-reply-shortcut-error"
                className="text-xs text-destructive"
              >
                {errors.shortcut.message}
              </p>
            ) : (
              <p
                id="quick-reply-shortcut-hint"
                className="text-xs text-muted-foreground"
              >
                Sin la barra: de 2 a 32 letras sin tilde, números o guiones.
              </p>
            )}
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="quick-reply-body">Respuesta</Label>
            <div
              role="group"
              aria-label="Insertar variable"
              className="flex flex-wrap gap-1.5"
            >
              {QUICK_REPLY_VARIABLES.map((variable) => (
                <Button
                  key={variable}
                  type="button"
                  size="xs"
                  variant="outline"
                  title={QUICK_REPLY_VARIABLE_HINTS[variable]}
                  // The textarea keeps the focus and the caret the variable goes into.
                  onMouseDown={(event) => event.preventDefault()}
                  onClick={() => insertVariable(variable)}
                >
                  {`{${variable}}`}
                </Button>
              ))}
            </div>
            <Textarea
              id="quick-reply-body"
              rows={6}
              aria-invalid={Boolean(errors.body)}
              aria-describedby={
                errors.body
                  ? 'quick-reply-body-error quick-reply-body-count'
                  : 'quick-reply-body-count'
              }
              {...bodyField}
              ref={(element) => {
                bodyField.ref(element)
                bodyElement.current = element
              }}
            />
            <div className="flex items-start justify-between gap-2 text-xs">
              {errors.body ? (
                <p id="quick-reply-body-error" className="text-destructive">
                  {errors.body.message}
                </p>
              ) : (
                <span />
              )}
              {/* No `maxLength`: a long paste would be cut without a word. The counter and the
                schema say it instead. */}
              <p
                id="quick-reply-body-count"
                className={cn(
                  'shrink-0 text-muted-foreground',
                  bodyLength > QUICK_REPLY_BODY_MAX_LENGTH && 'text-destructive',
                )}
              >
                {`${bodyLength} / ${QUICK_REPLY_BODY_MAX_LENGTH}`}
              </p>
            </div>
          </div>
        </form>

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            onClick={onClose}
            disabled={isSubmitting}
          >
            Cancelar
          </Button>
          <Button type="submit" form={FORM_ID} disabled={isSubmitting}>
            {isSubmitting ? 'Guardando…' : 'Guardar'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
```

- [ ] **Step 6: Escribir la confirmación**

`src/features/messaging/components/delete-quick-reply-dialog.tsx`:

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
import type { QuickReply } from '@/features/messaging/types/messaging'

/** Asks before deleting (spec 2026-10-12 §4.1). Mounted only while open; the page owns the request. */
export function DeleteQuickReplyDialog({
  quickReply,
  isDeleting,
  failure,
  onConfirm,
  onCancel,
}: {
  quickReply: QuickReply
  isDeleting: boolean
  /** Why the last attempt failed, when it was not a stale row (that one closes the dialog). */
  failure: string | null
  onConfirm: () => void
  onCancel: () => void
}) {
  return (
    <Dialog
      open
      onOpenChange={(next) => {
        if (!next && !isDeleting) onCancel()
      }}
    >
      <DialogContent showCloseButton={!isDeleting}>
        <DialogHeader>
          <DialogTitle>{`¿Borrar /${quickReply.shortcut}?`}</DialogTitle>
          <DialogDescription>
            Tus asesores dejarán de verla en la caja. Esta acción no se puede
            deshacer.
          </DialogDescription>
        </DialogHeader>
        {failure ? (
          <p role="alert" className="text-sm text-destructive">
            {failure}
          </p>
        ) : null}
        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            onClick={onCancel}
            disabled={isDeleting}
          >
            Cancelar
          </Button>
          <Button
            type="button"
            variant="destructive"
            onClick={onConfirm}
            disabled={isDeleting}
          >
            {isDeleting ? 'Borrando…' : 'Borrar'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
```

- [ ] **Step 7: Escribir la página**

`src/features/messaging/pages/quick-replies-page.tsx`:

```tsx
import { useState } from 'react'

import { Pencil, Plus, Trash2 } from 'lucide-react'

import { PageContainer } from '@/components/page-container'
import { Button } from '@/components/ui/button'
import { DeleteQuickReplyDialog } from '@/features/messaging/components/delete-quick-reply-dialog'
import { QuickReplyFormDialog } from '@/features/messaging/components/quick-reply-form-dialog'
import { describeQuickReplyFailure } from '@/features/messaging/services/messaging.api'
import {
  QUICK_REPLY_MESSAGES,
  type QuickReply,
  type SaveQuickReplyInput,
} from '@/features/messaging/types/messaging'
import type { ReadStatus } from '@/lib/query-status'

export interface QuickRepliesPageProps {
  status: ReadStatus
  quickReplies: QuickReply[] | undefined
  onRetry: () => void
  onCreate: (input: SaveQuickReplyInput) => Promise<QuickReply>
  onUpdate: (
    quickReply: QuickReply,
    input: SaveQuickReplyInput,
  ) => Promise<QuickReply>
  onDelete: (quickReply: QuickReply) => Promise<void>
}

/**
 * Frozen when opened: the list refetches underneath, and the version sent must be the one the
 * person saw (same rule as the integrations page).
 */
type OpenDialog =
  | { kind: 'create' }
  | { kind: 'edit'; quickReply: QuickReply }
  | { kind: 'delete'; quickReply: QuickReply }

/**
 * Settings → Respuestas rápidas (spec 2026-10-12 §4.1). Presentational: the route hands it the list
 * and the writes. A 412 or a 404 closes whatever was open and says why; the writes already re-read
 * the list when they settle, so what the person sees next is the other person's change.
 */
export function QuickRepliesPage({
  status,
  quickReplies,
  onRetry,
  onCreate,
  onUpdate,
  onDelete,
}: QuickRepliesPageProps) {
  const [dialog, setDialog] = useState<OpenDialog | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [isDeleting, setDeleting] = useState(false)
  const [deleteFailure, setDeleteFailure] = useState<string | null>(null)

  const open = (next: OpenDialog) => {
    setNotice(null)
    setDeleteFailure(null)
    setDialog(next)
  }
  const closeAsStale = (message: string) => {
    setDialog(null)
    setNotice(message)
  }

  const save = async (input: SaveQuickReplyInput) => {
    if (dialog?.kind === 'edit') await onUpdate(dialog.quickReply, input)
    else await onCreate(input)
    setDialog(null)
  }

  const confirmDelete = async () => {
    if (dialog?.kind !== 'delete') return
    setDeleteFailure(null)
    setDeleting(true)
    try {
      await onDelete(dialog.quickReply)
      setDialog(null)
    } catch (error) {
      const described = describeQuickReplyFailure(error)
      if (described.stale) {
        closeAsStale(described.message ?? QUICK_REPLY_MESSAGES.conflict)
      } else {
        setDeleteFailure(described.message)
      }
    } finally {
      setDeleting(false)
    }
  }

  return (
    <PageContainer variant="form">
      <header className="mb-6 flex flex-wrap items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="text-2xl font-semibold tracking-tight">
            Respuestas rápidas
          </h1>
          <p className="text-sm text-muted-foreground">
            Los textos que tus asesores insertan en la caja de Mensajería
            escribiendo / y el atajo.
          </p>
        </div>
        {quickReplies && quickReplies.length > 0 ? (
          <Button type="button" onClick={() => open({ kind: 'create' })}>
            <Plus aria-hidden="true" />
            Nueva respuesta
          </Button>
        ) : null}
      </header>

      {notice ? (
        <div
          role="alert"
          className="mb-4 flex items-start justify-between gap-3 rounded-lg border border-warning-border bg-warning-surface px-4 py-3 text-sm text-warning-foreground"
        >
          <p>{notice}</p>
          <Button
            type="button"
            size="xs"
            variant="ghost"
            onClick={() => setNotice(null)}
          >
            Cerrar
          </Button>
        </div>
      ) : null}

      <QuickReplyList
        status={status}
        quickReplies={quickReplies}
        onRetry={onRetry}
        onCreate={() => open({ kind: 'create' })}
        onEdit={(quickReply) => open({ kind: 'edit', quickReply })}
        onDelete={(quickReply) => open({ kind: 'delete', quickReply })}
      />

      {dialog?.kind === 'create' || dialog?.kind === 'edit' ? (
        <QuickReplyFormDialog
          quickReply={dialog.kind === 'edit' ? dialog.quickReply : null}
          onSave={save}
          onStale={closeAsStale}
          onClose={() => setDialog(null)}
        />
      ) : null}
      {dialog?.kind === 'delete' ? (
        <DeleteQuickReplyDialog
          quickReply={dialog.quickReply}
          isDeleting={isDeleting}
          failure={deleteFailure}
          onConfirm={() => void confirmDelete()}
          onCancel={() => setDialog(null)}
        />
      ) : null}
    </PageContainer>
  )
}

function QuickReplyList({
  status,
  quickReplies,
  onRetry,
  onCreate,
  onEdit,
  onDelete,
}: {
  status: ReadStatus
  quickReplies: QuickReply[] | undefined
  onRetry: () => void
  onCreate: () => void
  onEdit: (quickReply: QuickReply) => void
  onDelete: (quickReply: QuickReply) => void
}) {
  if (!quickReplies) {
    const message =
      status === 'denied'
        ? 'No tienes permiso para ver las respuestas rápidas.'
        : status === 'error' || status === 'not-found'
          ? 'No pudimos cargar las respuestas rápidas.'
          : 'Cargando respuestas rápidas…'
    return (
      <div
        role="status"
        className="flex flex-col items-start gap-3 text-sm text-muted-foreground"
      >
        <p>{message}</p>
        {status === 'error' ? (
          <Button type="button" variant="outline" onClick={onRetry}>
            Reintentar
          </Button>
        ) : null}
      </div>
    )
  }

  if (quickReplies.length === 0) {
    return (
      <div className="flex flex-col items-center gap-3 rounded-xl border border-dashed border-border p-8 text-center">
        <p className="font-medium">Aún no tienes respuestas rápidas</p>
        <p className="max-w-md text-sm text-muted-foreground">
          Guarda los textos que tus asesores escriben una y otra vez: el
          saludo, los horarios, los datos de pago.
        </p>
        <Button type="button" onClick={onCreate}>
          <Plus aria-hidden="true" />
          Crear la primera
        </Button>
      </div>
    )
  }

  return (
    <table className="w-full table-fixed text-sm">
      <caption className="sr-only">Respuestas rápidas</caption>
      <thead className="border-b border-border text-left text-xs text-muted-foreground">
        <tr>
          <th scope="col" className="w-40 py-2 pr-3 font-medium">
            Atajo
          </th>
          <th scope="col" className="py-2 pr-3 font-medium">
            Respuesta
          </th>
          <th scope="col" className="w-24 py-2">
            <span className="sr-only">Acciones</span>
          </th>
        </tr>
      </thead>
      <tbody>
        {quickReplies.map((quickReply) => (
          <tr key={quickReply.id} className="border-b border-border">
            <td className="py-2 pr-3 font-mono">/{quickReply.shortcut}</td>
            <td className="py-2 pr-3">
              {/* One line: `truncate` also folds the line breaks of the body. */}
              <p className="truncate" title={quickReply.body}>
                {quickReply.body}
              </p>
            </td>
            <td className="py-2">
              <div className="flex justify-end gap-1">
                <Button
                  type="button"
                  size="icon-sm"
                  variant="ghost"
                  aria-label={`Editar /${quickReply.shortcut}`}
                  onClick={() => onEdit(quickReply)}
                >
                  <Pencil aria-hidden="true" />
                </Button>
                <Button
                  type="button"
                  size="icon-sm"
                  variant="ghost"
                  aria-label={`Borrar /${quickReply.shortcut}`}
                  onClick={() => onDelete(quickReply)}
                >
                  <Trash2 aria-hidden="true" />
                </Button>
              </div>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
```

- [ ] **Step 8: Correrlas y verlas pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/features/messaging/components/quick-reply-form-dialog.test.tsx src/features/messaging/pages/quick-replies-page.test.tsx
```

Expected: PASS — `Tests  24 passed (24)` (11 del formulario y 13 de la página, contando las tres filas del `it.each`). Pega la salida literal.

- [ ] **Step 9: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/respuestas-rapidas') { throw "rama inesperada" }
$files = @(
  'src/features/messaging/types/quick-reply-form.schema.ts',
  'src/features/messaging/components/quick-reply-form-dialog.tsx',
  'src/features/messaging/components/quick-reply-form-dialog.test.tsx',
  'src/features/messaging/components/delete-quick-reply-dialog.tsx',
  'src/features/messaging/pages/quick-replies-page.tsx',
  'src/features/messaging/pages/quick-replies-page.test.tsx'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(messaging): pantalla de gestión de respuestas rápidas

Tabla con atajo y vista previa de una línea, estado vacío, formulario con
el / visual, chips que insertan {nombre}, {asesor} y {empresa} en el cursor
y contador de 4096, y confirmación de borrado. Un 422 marca su campo; un
412 o un 404 cierran lo abierto y avisan que otra persona lo cambió.
'@
$msgFile = Join-Path $env:TEMP 'qep-respuestas-rapidas-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

Expected: 0 bytes vacío; exactamente los 6 archivos; sin trailer.

---

### Task 4: Ruta `/settings/quick-replies` y tarjeta en Configuración

**Files:**
- Create: `src/features/messaging/components/quick-replies-access-gate.tsx`
- Create: `src/routes/_authenticated/settings/quick-replies.tsx`
- Regenerate: `src/routeTree.gen.ts`
- Modify: `src/features/tenant-settings/pages/tenant-settings-page.tsx`
- Modify: `src/routes/_authenticated/settings/index.tsx`
- Test: `src/routes/_authenticated/settings/quick-replies.test.tsx`
- Test: `src/features/tenant-settings/pages/tenant-settings-page.test.tsx` (dos pruebas al final del `describe` raíz)
- Test: `src/routes/_authenticated/settings/index.test.tsx` (tres pruebas al final del `describe('/settings')`)

**Interfaces:**
- Consumes: `ModuleGate` (`@/components/module-gate`), `usePermissions`, `useTenantModules`, `useQuickReplies`, `useQuickReplyMutations` (Task 2), `QuickRepliesPage` (Task 3), `MESSAGING_PERMISSIONS.quickReplyManage` (Task 1).
- Produces: `QuickRepliesAccessGate({ children }: { children: ReactNode })`; ruta `'/_authenticated/settings/quick-replies'`; prop `TenantSettingsPageProps.showQuickReplies?: boolean` (default `false`).

- [ ] **Step 1: Escribir las pruebas**

`src/routes/_authenticated/settings/quick-replies.test.tsx`:

```tsx
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { MODULE_NOT_IN_PLAN_MESSAGE } from '@/features/auth/types/tenant-modules'
import { jsonResponse, problemResponse } from '@/test/integrations'
import {
  QUICK_REPLY_ADMIN_PERMISSIONS,
  quickReply,
  quickReplyList,
  stubMessagingBackend,
  TENANT,
} from '@/test/messaging'
import { renderRoute } from '@/test/render-route'
import { MODULE_OFF, tenantModulesResponse } from '@/test/tenant-modules'

const listRequests = (backend: ReturnType<typeof stubMessagingBackend>) =>
  backend
    .messagingRequests()
    .filter((r) => r.method === 'GET' && r.url.endsWith('/quick-replies'))

describe('/settings/quick-replies', () => {
  it('lists the quick replies with messaging.quick_reply.manage', async () => {
    stubMessagingBackend({
      permissions: QUICK_REPLY_ADMIN_PERMISSIONS,
      quickReplies: (request) =>
        request.method === 'GET'
          ? jsonResponse(200, quickReplyList())
          : undefined,
    })

    renderRoute('/settings/quick-replies')

    expect(
      await screen.findByRole('heading', { name: 'Respuestas rápidas' }),
    ).toBeInTheDocument()
    expect(
      await screen.findByRole('cell', { name: '/horarios' }),
    ).toBeInTheDocument()
  })

  it('asks nothing without messaging.quick_reply.manage', async () => {
    const backend = stubMessagingBackend()

    renderRoute('/settings/quick-replies')

    expect(
      await screen.findByText(
        'No tienes permiso para gestionar respuestas rápidas.',
      ),
    ).toBeInTheDocument()
    expect(
      backend.messagingRequests().filter((r) => r.url.includes('/quick-replies')),
    ).toEqual([])
  })

  it('explains the plan and asks nothing with the messaging module off', async () => {
    const backend = stubMessagingBackend({
      permissions: QUICK_REPLY_ADMIN_PERMISSIONS,
      modules: tenantModulesResponse({ messaging: MODULE_OFF }, TENANT),
    })

    renderRoute('/settings/quick-replies')

    expect(
      await screen.findByText(MODULE_NOT_IN_PLAN_MESSAGE),
    ).toBeInTheDocument()
    expect(
      backend.messagingRequests().filter((r) => r.url.includes('/quick-replies')),
    ).toEqual([])
  })

  it('creates the first one and shows it after the re-read', async () => {
    const user = userEvent.setup()
    let created = false
    const backend = stubMessagingBackend({
      permissions: QUICK_REPLY_ADMIN_PERMISSIONS,
      quickReplies: (request) => {
        if (request.method === 'POST') created = true
        if (request.method !== 'GET') return undefined
        return jsonResponse(
          200,
          quickReplyList(
            created
              ? [quickReply({ body: 'Atendemos de 8 a 6.', version: 1 })]
              : [],
          ),
        )
      },
    })

    renderRoute('/settings/quick-replies')
    await user.click(
      await screen.findByRole('button', { name: 'Crear la primera' }),
    )
    await user.type(screen.getByLabelText('Atajo'), 'horarios')
    await user.click(screen.getByLabelText('Respuesta'))
    await user.paste('Atendemos de 8 a 6.')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByRole('cell', { name: '/horarios' }),
    ).toBeInTheDocument()
    expect(
      backend.messagingRequests().find((r) => r.method === 'POST')?.body,
    ).toEqual({ shortcut: 'horarios', body: 'Atendemos de 8 a 6.' })
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('re-reads the list and says why after someone else edited the row (412)', async () => {
    const user = userEvent.setup()
    const backend = stubMessagingBackend({
      permissions: QUICK_REPLY_ADMIN_PERMISSIONS,
      quickReplies: (request) =>
        request.method === 'PUT'
          ? problemResponse(412)
          : request.method === 'GET'
            ? jsonResponse(200, quickReplyList([quickReply()]))
            : undefined,
    })

    renderRoute('/settings/quick-replies')
    await user.click(
      await screen.findByRole('button', { name: 'Editar /horarios' }),
    )
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Otra persona editó esta respuesta',
    )
    await waitFor(() => expect(listRequests(backend)).toHaveLength(2))
  })
})
```

En `src/features/tenant-settings/pages/tenant-settings-page.test.tsx`, al final del `describe` raíz (después de `'leaves the integrations card out by default'`):

```tsx
  it('links to the quick replies page when they may be managed', () => {
    render(
      <TenantSettingsPage
        settings={SETTINGS}
        status="ready"
        canManage={false}
        logo={idleLogo()}
        showQuickReplies
        onSave={vi.fn()}
      />,
    )

    expect(
      screen.getByRole('link', { name: 'Textos guardados para Mensajería' }),
    ).toHaveAttribute('href', '/settings/quick-replies')
  })

  it('leaves the quick replies card out by default', () => {
    render(
      <TenantSettingsPage
        settings={SETTINGS}
        status="ready"
        canManage
        logo={idleLogo()}
        onSave={vi.fn()}
      />,
    )

    expect(
      screen.queryByRole('heading', { name: 'Respuestas rápidas' }),
    ).toBeNull()
  })
```

En `src/routes/_authenticated/settings/index.test.tsx`, al final del `describe('/settings')` (los permisos van como texto, igual que en las pruebas de integraciones; `tenantModulesResponse`, `MODULE_OFF` y `TENANT` ya están en el archivo):

```tsx
  it('links to the quick replies page with messaging.quick_reply.manage', async () => {
    stubBackend({}, [
      'tenancy.settings.read',
      'tenancy.settings.update',
      'messaging.quick_reply.manage',
    ])

    renderRoute('/settings')

    expect(
      await screen.findByRole('link', {
        name: 'Textos guardados para Mensajería',
      }),
    ).toHaveAttribute('href', '/settings/quick-replies')
  })

  it('hides the quick replies card without messaging.quick_reply.manage', async () => {
    stubBackend()

    renderRoute('/settings')

    expect(
      await screen.findByRole('link', {
        name: 'Columnas del Excel de pedidos',
      }),
    ).toBeInTheDocument()
    expect(
      screen.queryByRole('link', { name: 'Textos guardados para Mensajería' }),
    ).toBeNull()
  })

  it('hides the quick replies card when the messaging module is off', async () => {
    stubBackend(tenantModulesResponse({ messaging: MODULE_OFF }, TENANT), [
      'tenancy.settings.read',
      'tenancy.settings.update',
      'messaging.quick_reply.manage',
    ])

    renderRoute('/settings')

    expect(
      await screen.findByRole('link', {
        name: 'Columnas del Excel de pedidos',
      }),
    ).toBeInTheDocument()
    expect(
      screen.queryByRole('link', { name: 'Textos guardados para Mensajería' }),
    ).toBeNull()
  })
```

- [ ] **Step 2: Correrlas y verlas fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/routes/_authenticated/settings/quick-replies.test.tsx src/features/tenant-settings/pages/tenant-settings-page.test.tsx src/routes/_authenticated/settings/index.test.tsx
```

Expected: FAIL — `quick-replies.test.tsx` no encuentra la ruta (el router muestra su «Not Found» y `findByRole('heading', { name: 'Respuestas rápidas' })` vence); las dos pruebas nuevas de la vista y la de `index.test.tsx` con permiso fallan con `Unable to find role="link" and name "Textos guardados para Mensajería"`. Las dos que esperan **no** ver la tarjeta pasan desde ya (anótalo: son guardas, no RED). Pega la salida literal.

- [ ] **Step 3: Escribir el gate de permiso**

`src/features/messaging/components/quick-replies-access-gate.tsx`:

```tsx
import type { ReactNode } from 'react'

import { PageContainer } from '@/components/page-container'
import { Card } from '@/components/ui/card'
import { usePermissions } from '@/features/auth/hooks/use-permission'
import {
  MESSAGING_PERMISSIONS,
  QUICK_REPLY_MESSAGES,
} from '@/features/messaging/types/messaging'

/**
 * Without `messaging.quick_reply.manage` the settings page does not mount, so its read never
 * starts. Deny-by-default while loading, same as `CampaignsAccessGate`. Usability, not
 * authorization: the backend authorizes each request regardless (AGENTS.md §6).
 */
export function QuickRepliesAccessGate({ children }: { children: ReactNode }) {
  const { can, status } = usePermissions()

  if (status === 'loading') {
    return (
      <PageContainer>
        <Card className="p-8 text-center text-sm text-muted-foreground">
          Cargando permisos...
        </Card>
      </PageContainer>
    )
  }

  if (!can(MESSAGING_PERMISSIONS.quickReplyManage)) {
    return (
      <PageContainer>
        <Card className="p-8 text-center text-sm text-muted-foreground">
          {QUICK_REPLY_MESSAGES.denied}
        </Card>
      </PageContainer>
    )
  }

  return children
}
```

- [ ] **Step 4: Escribir la ruta**

`src/routes/_authenticated/settings/quick-replies.tsx`:

```tsx
import { createFileRoute } from '@tanstack/react-router'

import { ModuleGate } from '@/components/module-gate'
import { QuickRepliesAccessGate } from '@/features/messaging/components/quick-replies-access-gate'
import { useQuickReplies } from '@/features/messaging/hooks/use-quick-replies'
import { useQuickReplyMutations } from '@/features/messaging/hooks/use-quick-reply-mutations'
import { QuickRepliesPage } from '@/features/messaging/pages/quick-replies-page'

export const Route = createFileRoute('/_authenticated/settings/quick-replies')(
  {
    component: QuickRepliesGate,
  },
)

// The permission is core but what it configures belongs to messaging (spec 2026-10-12 §3.3: the
// backend answers 403 tenancy.module_not_enabled without it). Both gates sit outside the container
// so `useQuickReplies` never fires for a tenant without the module or a member without the right.
function QuickRepliesGate() {
  return (
    <ModuleGate modules={['messaging']}>
      <QuickRepliesAccessGate>
        <QuickRepliesRoute />
      </QuickRepliesAccessGate>
    </ModuleGate>
  )
}

/** Container of the quick replies screen: hooks here, the view has none (`settings/integrations`). */
function QuickRepliesRoute() {
  const list = useQuickReplies({ enabled: true })
  const mutations = useQuickReplyMutations()

  return (
    <QuickRepliesPage
      status={list.status}
      quickReplies={list.quickReplies}
      onRetry={list.retry}
      onCreate={mutations.create}
      onUpdate={mutations.update}
      onDelete={mutations.remove}
    />
  )
}
```

- [ ] **Step 5: Regenerar el árbol de rutas**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bunx vite build
"vite exit $LASTEXITCODE"
git diff --stat -- src/routeTree.gen.ts
```

Expected: `vite exit 0` y `src/routeTree.gen.ts` con la ruta `/_authenticated/settings/quick-replies` agregada (unas decenas de líneas). Nunca se edita a mano.

- [ ] **Step 6: Agregar la tarjeta a la vista**

En `src/features/tenant-settings/pages/tenant-settings-page.tsx`:

1. Agrega `Zap` a la importación de `lucide-react`: `import { ChevronRight, Lock, Plug, Table2, Zap } from 'lucide-react'`.
2. En `TenantSettingsPageProps`, después de `showIntegrations`:

```tsx
  /** `messaging.quick_reply.manage` and the messaging module: the card links to the quick replies
   * page. Off by default, so a page rendered without that answer never shows it. The container
   * decides. */
  showQuickReplies?: boolean
```

3. En la desestructuración de props, después de `showIntegrations = false,`: `showQuickReplies = false,`.
4. Justo antes del `</PageContainer>` final (después del bloque `{showIntegrations ? (...) : null}`):

```tsx
      {showQuickReplies ? (
        // Its own page (spec 2026-10-12 §4.1), like the integrations card.
        <SettingsSection
          title="Respuestas rápidas"
          description="Tiene su propia página."
        >
          <Link
            to="/settings/quick-replies"
            aria-label="Textos guardados para Mensajería"
            aria-describedby="quick-replies-link-description"
            className="group flex items-center gap-4 rounded-xl border border-border bg-background p-4 transition-colors hover:bg-muted/50 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
          >
            <span className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-muted text-muted-foreground">
              <Zap className="size-5" aria-hidden="true" />
            </span>
            <span className="min-w-0 flex-1 space-y-0.5">
              <span className="block text-sm font-medium">
                Textos guardados para Mensajería
              </span>
              <span
                id="quick-replies-link-description"
                className="block text-sm text-muted-foreground"
              >
                Crea los textos que tus asesores insertan en la caja con / y un
                atajo, como /horarios.
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

- [ ] **Step 7: Decidir la tarjeta en el container**

En `src/routes/_authenticated/settings/index.tsx`:

1. Agrega la importación: `import { MESSAGING_PERMISSIONS } from '@/features/messaging/types/messaging'`.
2. Después de `const showOrdersExport = ...`:

```tsx
  // Core permission over something of the messaging module (spec 2026-10-12 §3.3): the module
  // decides too, with the same rule as the orders export while the modules load or fail.
  const showQuickReplies =
    can(MESSAGING_PERMISSIONS.quickReplyManage) &&
    (modulesStatus === 'loading' || isEnabled('messaging'))
```

3. En el JSX, después de `showIntegrations={...}`: `showQuickReplies={showQuickReplies}`.

`can` ya viene de `usePermissions()` más arriba en la misma función.

- [ ] **Step 8: Correrlas y verlas pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/routes/_authenticated/settings/quick-replies.test.tsx src/features/tenant-settings/pages/tenant-settings-page.test.tsx src/routes/_authenticated/settings/index.test.tsx
```

Expected: PASS — los tres archivos completos, con las 5 pruebas nuevas de la ruta, las 2 de la vista y las 3 de `index.test.tsx`. Pega la salida literal.

- [ ] **Step 9: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/respuestas-rapidas') { throw "rama inesperada" }
$files = @(
  'src/features/messaging/components/quick-replies-access-gate.tsx',
  'src/routes/_authenticated/settings/quick-replies.tsx',
  'src/routes/_authenticated/settings/quick-replies.test.tsx',
  'src/routeTree.gen.ts',
  'src/features/tenant-settings/pages/tenant-settings-page.tsx',
  'src/features/tenant-settings/pages/tenant-settings-page.test.tsx',
  'src/routes/_authenticated/settings/index.tsx',
  'src/routes/_authenticated/settings/index.test.tsx'
)
bunx prettier --write ($files | Where-Object { $_ -ne 'src/routeTree.gen.ts' })
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(messaging): ruta y tarjeta de respuestas rápidas en configuración

/settings/quick-replies detrás del módulo de mensajería y del permiso
messaging.quick_reply.manage, sin pedir nada si falta alguno. La tarjeta de
Configuración aparece con el permiso y el módulo, como la de Excel de
pedidos.
'@
$msgFile = Join-Path $env:TEMP 'qep-respuestas-rapidas-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

Expected: 0 bytes vacío; exactamente los 8 archivos; sin trailer.

---

### Task 5: Respuestas rápidas con `/` en el compositor, y el hilo que las lee

**Files:**
- Create: `src/features/messaging/hooks/use-list-navigation.ts`
- Create: `src/features/messaging/components/quick-reply-picker.tsx`
- Modify: `src/features/messaging/components/message-composer.tsx`
- Modify: `src/features/messaging/pages/conversation-thread-page.tsx`
- Modify: `src/routes/_authenticated/messaging/$conversationId.tsx`
- Test: `src/features/messaging/components/message-composer.test.tsx`
- Test: `src/routes/_authenticated/messaging/$conversationId.test.tsx` (un `describe` al final)

**Interfaces:**
- Consumes: `filterQuickReplies`, `insertQuickReply`, `quickReplyKey`, `quickReplyOptionId` (Task 1); `resolveQuickReply`, `quickReplyValues` (Task 1); `useQuickReplies` (Task 2); `filterTemplates`, `templateKey`, `templateOptionId` (`@/components/whatsapp-templates/message-template`).
- Produces: `useListNavigation<T>(options: readonly T[], keyOf: (option: T) => string, handlers: { onChoose: (option: T) => void; onClose: () => void }): { active: T | null; activeKey: string | null; onKeyDown: (event: KeyboardEvent) => boolean }`; `QuickReplyPicker(props: { listId: string; quickReplies: QuickReply[] | undefined; status: ReadStatus; filter: string; activeKey: string | null; onChoose: (quickReply: QuickReply) => void; onRetry: () => void })`; `export interface ComposerQuickReplies { items: QuickReply[] | undefined; status: ReadStatus; values: QuickReplyValues; onRetry: () => void }`; prop nueva `MessageComposerProps.quickReplies: ComposerQuickReplies`; `export interface ThreadQuickReplies { items: QuickReply[] | undefined; status: ReadStatus; variables: QuickReplyVariables | undefined; onRetry: () => void }`; prop nueva `ConversationThreadPageProps.quickReplies: ThreadQuickReplies`.

- [ ] **Step 1: Ajustar el render de prueba y escribir las pruebas del compositor**

En `src/features/messaging/components/message-composer.test.tsx`:

1. Cambia la primera línea a `import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'`.
2. Cambia la importación del compositor a `import { type ComposerQuickReplies, MessageComposer } from '@/features/messaging/components/message-composer'`.
3. Cambia la importación de tipos a `import type { MessageReplyTo, QuickReplyValues } from '@/features/messaging/types/messaging'`.
4. Cambia `import { messageTemplate } from '@/test/messaging'` a `import { messageTemplate, quickReply } from '@/test/messaging'`.
5. Después de `const TEMPLATES = [...]`:

```tsx
const VALUES: QuickReplyValues = {
  nombre: 'Laura',
  asesor: 'Andrés',
  empresa: 'Origen Botánico',
}

const QUICK_REPLIES = [
  quickReply({
    id: 'qr-1',
    shortcut: 'horarios',
    body: 'Hola {nombre}, atendemos de 8 a 6.',
  }),
  quickReply({
    id: 'qr-2',
    shortcut: 'pago',
    body: 'Soy {asesor} de {empresa}. Paga por transferencia.',
  }),
]

function quickRepliesProp(
  overrides: Partial<ComposerQuickReplies> = {},
): ComposerQuickReplies {
  return {
    items: QUICK_REPLIES,
    status: 'ready',
    values: VALUES,
    onRetry: vi.fn(),
    ...overrides,
  }
}
```

6. En `renderComposer`, dentro de `props`, después de `onSendTemplate: vi.fn(),` (lista **vacía** por defecto: las pruebas de plantillas existentes cuentan opciones y no deben ver respuestas rápidas):

```tsx
    quickReplies: quickRepliesProp({ items: [] }),
```

7. Al final del archivo:

```tsx
describe('MessageComposer quick replies with a slash', () => {
  const quickList = () =>
    screen.getByRole('listbox', { name: 'Respuestas rápidas' })
  const box = () => screen.getByRole('textbox', { name: 'Mensaje' })

  it('lists the quick replies above the templates when the box starts with a slash', async () => {
    const user = userEvent.setup()
    renderComposer({ quickReplies: quickRepliesProp() })

    await user.type(box(), '/')

    expect(within(quickList()).getAllByRole('option')).toHaveLength(2)
    expect(
      within(
        screen.getByRole('listbox', { name: 'Plantillas aprobadas' }),
      ).getAllByRole('option'),
    ).toHaveLength(3)
    // The first quick reply is the active one: Enter takes it.
    expect(
      within(quickList()).getByRole('option', { name: /horarios/ }),
    ).toHaveAttribute('aria-selected', 'true')
  })

  it('filters by shortcut and by body', async () => {
    const user = userEvent.setup()
    renderComposer({ quickReplies: quickRepliesProp() })

    await user.type(box(), '/pag')
    expect(within(quickList()).getAllByRole('option')).toHaveLength(1)
    expect(within(quickList()).getByRole('option')).toHaveTextContent('/pago')

    await user.clear(box())
    await user.type(box(), '/transfer')
    expect(within(quickList()).getByRole('option')).toHaveTextContent('/pago')
  })

  it('Enter puts the resolved text in place of the slash term, with the cursor at the end, and sends nothing', async () => {
    const user = userEvent.setup()
    const { props } = renderComposer({ quickReplies: quickRepliesProp() })
    const resolved = 'Hola Laura, atendemos de 8 a 6.'

    await user.type(box(), '/hor{Enter}')

    expect(box()).toHaveValue(resolved)
    expect(box()).toHaveFocus()
    expect((box() as HTMLTextAreaElement).selectionStart).toBe(resolved.length)
    expect(props.onSend).not.toHaveBeenCalled()
    expect(screen.queryByRole('listbox')).toBeNull()
  })

  it('runs the arrows from the quick replies into the templates and back', async () => {
    const user = userEvent.setup()
    renderComposer({ quickReplies: quickRepliesProp() })

    await user.type(box(), '/')
    await user.keyboard('{ArrowDown}')
    expect(
      within(quickList()).getByRole('option', { name: /pago/ }),
    ).toHaveAttribute('aria-selected', 'true')

    await user.keyboard('{ArrowDown}')
    expect(
      screen.getByRole('option', { name: /recordatorio_pedido/ }),
    ).toHaveAttribute('aria-selected', 'true')
    expect(box()).toHaveAttribute(
      'aria-activedescendant',
      expect.stringContaining('recordatorio_pedido'),
    )

    await user.keyboard('{ArrowUp}{ArrowUp}')
    expect(
      within(quickList()).getByRole('option', { name: /horarios/ }),
    ).toHaveAttribute('aria-selected', 'true')
    expect(box()).toHaveAttribute(
      'aria-activedescendant',
      expect.stringContaining('qr-1'),
    )
  })

  it('Escape closes the list and keeps the slash term', async () => {
    const user = userEvent.setup()
    renderComposer({ quickReplies: quickRepliesProp() })

    await user.type(box(), '/hor')
    await user.keyboard('{Escape}')

    expect(screen.queryByRole('listbox')).toBeNull()
    expect(box()).toHaveValue('/hor')
  })

  it('inserts with a click too', async () => {
    const user = userEvent.setup()
    renderComposer({ quickReplies: quickRepliesProp() })

    await user.type(box(), '/')
    await user.click(within(quickList()).getByRole('option', { name: /pago/ }))

    expect(box()).toHaveValue(
      'Soy Andrés de Origen Botánico. Paga por transferencia.',
    )
  })

  // Review Focus 1
  it('does not open for a slash in the middle of the text, and Enter sends it as typed', async () => {
    const user = userEvent.setup()
    const { props } = renderComposer({ quickReplies: quickRepliesProp() })

    await user.type(box(), 'Hola /hor')
    expect(
      screen.queryByRole('listbox', { name: 'Respuestas rápidas' }),
    ).toBeNull()

    await user.keyboard('{Enter}')
    expect(props.onSend).toHaveBeenCalledWith('Hola /hor', null)
  })

  // Review Focus 2
  it('leaves the Enter that commits an IME composition to the text', async () => {
    const user = userEvent.setup()
    const { props } = renderComposer({ quickReplies: quickRepliesProp() })

    await user.type(box(), '/hor')
    fireEvent.keyDown(box(), { key: 'Enter', isComposing: true })

    expect(box()).toHaveValue('/hor')
    expect(props.onSend).not.toHaveBeenCalled()
    expect(quickList()).toBeInTheDocument()
  })

  // Review Focus 3
  it('keeps the templates usable while the quick replies load', async () => {
    const user = userEvent.setup()
    renderComposer({
      quickReplies: quickRepliesProp({ items: undefined, status: 'loading' }),
    })

    await user.type(box(), '/')
    expect(
      screen.getByText('Cargando respuestas rápidas…'),
    ).toBeInTheDocument()

    await user.type(box(), 'recor{Enter}')
    expect(screen.getByLabelText('first_name')).toBeInTheDocument()
  })

  it('says a failed list and retries it, without blocking the templates', async () => {
    const user = userEvent.setup()
    const quickReplies = quickRepliesProp({ items: undefined, status: 'error' })
    renderComposer({ quickReplies })

    await user.type(box(), '/')
    expect(
      screen.getByText('No pudimos cargar las respuestas rápidas.'),
    ).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Reintentar' }))
    expect(quickReplies.onRetry).toHaveBeenCalledTimes(1)

    await user.keyboard('{Enter}')
    expect(screen.getByLabelText('first_name')).toBeInTheDocument()
  })

  // Review Focus 5
  it('drops the quick replies when the window closes with the list open, and Enter inserts nothing', async () => {
    const user = userEvent.setup()
    const { props, rerender } = renderComposer({
      quickReplies: quickRepliesProp(),
    })

    await user.type(box(), '/hor')
    rerender(<MessageComposer {...props} windowClosed />)

    expect(
      screen.queryByRole('listbox', { name: 'Respuestas rápidas' }),
    ).toBeNull()
    await user.keyboard('{Enter}')
    expect(box()).toHaveValue('/hor')
    expect(props.onSend).not.toHaveBeenCalled()
  })

  it('offers only the templates with the window closed', async () => {
    const user = userEvent.setup()
    renderComposer({ windowClosed: true, quickReplies: quickRepliesProp() })

    await user.type(box(), '/')

    expect(
      screen.queryByRole('listbox', { name: 'Respuestas rápidas' }),
    ).toBeNull()
    expect(
      screen.getByRole('listbox', { name: 'Plantillas aprobadas' }),
    ).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: Escribir las pruebas del hilo**

En `src/routes/_authenticated/messaging/$conversationId.test.tsx`, agrega `quickReply` y `quickReplyList` a la importación de `@/test/messaging`. Al final del archivo:

```tsx
describe('quick replies in the thread', () => {
  const listOf = (body: string) =>
    jsonResponse(200, quickReplyList([quickReply({ body })]))

  it('inserts a quick reply with the customer name and the server variables, and sends it as text', async () => {
    const user = userEvent.setup()
    const backend = stubMessagingBackend({
      conversation: (id) =>
        jsonResponse(
          200,
          conversation({
            id,
            customer: { id: 'cu-1', name: 'Laura Gómez', isComplete: true },
          }),
        ),
      quickReplies: (request) =>
        request.method === 'GET'
          ? listOf('Hola {nombre}, soy {asesor} de {empresa}.')
          : undefined,
    })

    renderRoute('/messaging/conv-1')
    const box = await screen.findByRole('textbox', { name: 'Mensaje' })
    await user.type(box, '/')
    await screen.findByRole('option', { name: /horarios/ })
    await user.keyboard('{Enter}')

    expect(box).toHaveValue('Hola Laura Gómez, soy Andrés de Origen Botánico.')
    await user.keyboard('{Enter}')
    await waitFor(() => {
      const send = backend
        .messagingRequests()
        .find((r) => r.method === 'POST' && r.url.endsWith('/messages'))
      expect((send?.body as { text?: string } | undefined)?.text).toBe(
        'Hola Laura Gómez, soy Andrés de Origen Botánico.',
      )
    })
  })

  // Review Focus 4
  it('leaves no double space when the contact has no name at all', async () => {
    const user = userEvent.setup()
    stubMessagingBackend({
      conversation: (id) =>
        jsonResponse(
          200,
          conversation({
            id,
            contact: contactOf({ profileName: null, username: 'laura.perez' }),
          }),
        ),
      quickReplies: (request) =>
        request.method === 'GET'
          ? listOf('Hola {nombre}, soy {asesor}.')
          : undefined,
    })

    renderRoute('/messaging/conv-1')
    const box = await screen.findByRole('textbox', { name: 'Mensaje' })
    await user.type(box, '/')
    await screen.findByRole('option', { name: /horarios/ })
    await user.keyboard('{Enter}')

    expect(box).toHaveValue('Hola, soy Andrés.')
  })

  it('does not ask for the quick replies of someone who cannot answer', async () => {
    const backend = stubMessagingBackend({
      permissions: ALL_SIDEBAR_PERMISSIONS,
    })

    renderRoute('/messaging/conv-1')

    expect(
      await screen.findByText(
        'No tienes permiso para responder. Puedes leer la conversación.',
      ),
    ).toBeInTheDocument()
    expect(
      backend.messagingRequests().some((r) => r.url.includes('/quick-replies')),
    ).toBe(false)
  })
})
```

- [ ] **Step 3: Correrlas y verlas fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/features/messaging/components/message-composer.test.tsx 'src/routes/_authenticated/messaging/$conversationId.test.tsx'
```

Expected: FAIL — en el compositor, las pruebas nuevas no encuentran `listbox` «Respuestas rápidas» (`Unable to find role="listbox" and name "Respuestas rápidas"`) y la de inserción ve el valor `/hor` en vez del texto resuelto; las de foco 1, 2 y 5 que esperan **no** ver la lista pasan desde ya (guardas: anótalo). En el hilo, las dos de inserción vencen esperando la opción `/horarios`; la de permisos pasa desde ya. Las pruebas existentes de los dos archivos siguen en verde. Pega la salida literal.

- [ ] **Step 4: Escribir el hook de teclado**

`src/features/messaging/hooks/use-list-navigation.ts`:

```ts
import { type KeyboardEvent, useState } from 'react'

/**
 * ↑ ↓ Enter Esc over a list while the focus stays in the box that filters it. Same contract as
 * `useTemplateNavigation` (`components/whatsapp-templates`, shared with campaigns and the new
 * conversation dialog, so it is not touched), over any option: the composer runs the quick replies
 * and the templates as one sequence (spec 2026-10-11 §2, point 5). `options` holds only what can be
 * chosen, in the order the screen draws it.
 */
export function useListNavigation<T>(
  options: readonly T[],
  keyOf: (option: T) => string,
  {
    onChoose,
    onClose,
  }: { onChoose: (option: T) => void; onClose: () => void },
) {
  const [chosenKey, setChosenKey] = useState<string | null>(null)
  // Derived, not an effect: a filter that drops the active one hands the highlight to the first.
  const active =
    options.find((option) => keyOf(option) === chosenKey) ?? options[0] ?? null

  const move = (step: 1 | -1) => {
    if (options.length === 0) return
    const from = active === null ? -1 : options.indexOf(active)
    const next = (from + step + options.length) % options.length
    setChosenKey(keyOf(options[next]!))
  }

  /** Returns true when the key was for the list, so the caller leaves the box alone. */
  const onKeyDown = (event: KeyboardEvent): boolean => {
    // The Enter that commits an IME composition belongs to the text (Review Focus 2).
    if (event.nativeEvent.isComposing) return false
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault()
        move(1)
        return true
      case 'ArrowUp':
        event.preventDefault()
        move(-1)
        return true
      case 'Enter':
        if (active === null) return false
        event.preventDefault()
        onChoose(active)
        return true
      case 'Escape':
        event.preventDefault()
        onClose()
        return true
      default:
        return false
    }
  }

  return {
    active,
    activeKey: active === null ? null : keyOf(active),
    onKeyDown,
  }
}
```

- [ ] **Step 5: Escribir el picker**

`src/features/messaging/components/quick-reply-picker.tsx`:

```tsx
import { Button } from '@/components/ui/button'
import type { QuickReply } from '@/features/messaging/types/messaging'
import {
  filterQuickReplies,
  quickReplyKey,
  quickReplyOptionId,
} from '@/features/messaging/utils/quick-replies'
import type { ReadStatus } from '@/lib/query-status'
import { cn } from '@/lib/utils'

interface QuickReplyPickerProps {
  /** Ties the listbox to the box that filters it (`aria-controls`). */
  listId: string
  quickReplies: QuickReply[] | undefined
  status: ReadStatus
  /** What is typed after the `/`. */
  filter: string
  activeKey: string | null
  onChoose: (quickReply: QuickReply) => void
  onRetry: () => void
}

/**
 * The tenant's quick replies as a listbox, drawn above the templates (spec 2026-10-12 §4.2). Same
 * contract as `TemplatePicker`: it does not own the keyboard — the box that filters it drives
 * `activeKey` — and a click never takes the focus away from the box. The body is shown raw, with
 * its variables: what lands in the box is resolved, and the advisor reads it there before sending.
 */
export function QuickReplyPicker({
  listId,
  quickReplies,
  status,
  filter,
  activeKey,
  onChoose,
  onRetry,
}: QuickReplyPickerProps) {
  return (
    <div className="flex max-h-72 flex-col overflow-hidden rounded-xl border border-border bg-popover text-popover-foreground shadow-[var(--shadow-md)]">
      <div className="border-b border-border px-3 py-2">
        <span className="text-xs font-medium text-muted-foreground">
          Respuestas rápidas
        </span>
      </div>
      <Body
        listId={listId}
        quickReplies={quickReplies}
        status={status}
        filter={filter}
        activeKey={activeKey}
        onChoose={onChoose}
        onRetry={onRetry}
      />
    </div>
  )
}

function Body({
  listId,
  quickReplies,
  status,
  filter,
  activeKey,
  onChoose,
  onRetry,
}: QuickReplyPickerProps) {
  if (!quickReplies) {
    const message =
      status === 'denied'
        ? 'No tienes permiso para ver las respuestas rápidas.'
        : status === 'error' || status === 'not-found'
          ? 'No pudimos cargar las respuestas rápidas.'
          : 'Cargando respuestas rápidas…'
    return (
      <div
        role="status"
        className="flex flex-col items-start gap-2 px-3 py-3 text-sm text-muted-foreground"
      >
        <p>{message}</p>
        {status === 'error' ? (
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={onRetry}
            onMouseDown={(event) => event.preventDefault()}
          >
            Reintentar
          </Button>
        ) : null}
      </div>
    )
  }

  if (quickReplies.length === 0) {
    return (
      <div
        role="status"
        className="flex flex-col items-start gap-2 px-3 py-3 text-sm text-muted-foreground"
      >
        <p>Sin respuestas rápidas</p>
      </div>
    )
  }

  const visible = filterQuickReplies(quickReplies, filter)
  if (visible.length === 0) {
    return (
      <p role="status" className="px-3 py-3 text-sm text-muted-foreground">
        {`Ninguna respuesta rápida coincide con «${filter.trim()}».`}
      </p>
    )
  }

  return (
    <ul
      id={listId}
      role="listbox"
      aria-label="Respuestas rápidas"
      className="min-h-0 overflow-y-auto py-1"
    >
      {visible.map((quickReply) => {
        const active = quickReplyKey(quickReply) === activeKey
        return (
          <li
            key={quickReply.id}
            id={quickReplyOptionId(listId, quickReply)}
            role="option"
            aria-selected={active}
            // The box keeps the focus: a click must not move it.
            onMouseDown={(event) => event.preventDefault()}
            onClick={() => onChoose(quickReply)}
            className={cn(
              'flex cursor-pointer flex-col gap-0.5 px-3 py-2 text-sm hover:bg-muted',
              active && 'bg-muted',
            )}
          >
            <span className="font-mono font-medium">/{quickReply.shortcut}</span>
            <span className="truncate text-xs text-muted-foreground">
              {quickReply.body}
            </span>
          </li>
        )
      })}
    </ul>
  )
}
```

- [ ] **Step 6: Cambiar el compositor**

En `src/features/messaging/components/message-composer.tsx`:

1. Reemplaza el bloque de importaciones (líneas 1-24) por:

```tsx
import {
  type KeyboardEvent,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
} from 'react'

import { FileText, Lock, Mic, Send, UserRound, X } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { QuickReplyPicker } from '@/features/messaging/components/quick-reply-picker'
import { ReplyQuote } from '@/features/messaging/components/reply-quote'
import { TemplateComposer } from '@/features/messaging/components/template-composer'
import { TemplatePicker } from '@/components/whatsapp-templates/template-picker'
import { VoiceRecorderBar } from '@/features/messaging/components/voice-recorder-bar'
import { useListNavigation } from '@/features/messaging/hooks/use-list-navigation'
import { useVoiceRecorder } from '@/features/messaging/hooks/use-voice-recorder'
import {
  MESSAGE_TEXT_MAX_LENGTH,
  type MessageReplyTo,
  type MessageTemplate,
  type QuickReply,
  type QuickReplyValues,
} from '@/features/messaging/types/messaging'
import { WINDOW_CLOSED_NOTICE } from '@/features/messaging/utils/customer-window'
import {
  filterQuickReplies,
  insertQuickReply,
  quickReplyKey,
  quickReplyOptionId,
} from '@/features/messaging/utils/quick-replies'
import { resolveQuickReply } from '@/features/messaging/utils/resolve-quick-reply'
import {
  filterTemplates,
  templateKey,
  templateOptionId,
} from '@/components/whatsapp-templates/message-template'
import type { VoiceRecording } from '@/features/messaging/utils/voice-recorder'
import type { ReadStatus } from '@/lib/query-status'
```

(`useTemplateNavigation` deja de importarse: con `noUnusedLocals` un import sobrante rompe el build.)

2. Después de `export interface ComposerTemplates { ... }`:

```tsx
/** The tenant's quick replies and what their variables become in this conversation. */
export interface ComposerQuickReplies {
  items: QuickReply[] | undefined
  status: ReadStatus
  /** `{nombre}`, `{asesor}` and `{empresa}` for this conversation (spec 2026-10-12 §4.3). */
  values: QuickReplyValues
  onRetry: () => void
}

/** One sequence for the keyboard: the quick replies first, then the sendable templates. */
type ComposerOption =
  | { kind: 'quickReply'; quickReply: QuickReply }
  | { kind: 'template'; template: MessageTemplate }

function optionKey(option: ComposerOption): string {
  return option.kind === 'quickReply'
    ? quickReplyKey(option.quickReply)
    : templateKey(option.template)
}
```

3. En `MessageComposerProps`, después de `templates: ComposerTemplates`:

```tsx
  /** Offered with the templates on `/`, only while free text can be sent (spec 2026-10-12 D-Q8). */
  quickReplies: ComposerQuickReplies
```

4. Reemplaza el párrafo final del JSDoc del componente («A `/` as the first character opens the templates…») por:

```tsx
 * A `/` as the first character opens one list over the box: the quick replies (spec 2026-10-12
 * §4.2), then the templates (spec 2026-10-11 §7.2), as the templates spec foresaw («`/` las lista
 * juntas, en dos grupos»). The focus never leaves the box, so typing keeps filtering and the
 * arrows run through both groups. A quick reply lands resolved and editable, never sent. With the
 * window closed the box takes only that `/`, and the list has only the templates.
```

5. Agrega `quickReplies,` a la desestructuración de props, después de `templates,`.
6. Después de `const listId = useId()`:

```tsx
  const quickListId = useId()
  const box = useRef<HTMLTextAreaElement>(null)
  // Set when a quick reply lands: the next paint puts the caret after it (spec 2026-10-12 §4.2).
  const caretToEnd = useRef(false)
```

7. Después de `const filter = text.startsWith('/') ? text.slice(1) : ''`:

```tsx
  // Free text only goes out inside the window, so only there does the list offer it (D-Q8).
  const offersQuickReplies = canType
```

8. Reemplaza el bloque `const navigation = useTemplateNavigation(filterTemplates(templates.items ?? [], filter), { onChoose: choose, onClose: closeList })` (las cuatro líneas después de `closeList`; tras el cambio de imports ya no está en la línea 121) por:

```tsx
  const insert = (quickReply: QuickReply) => {
    setText(
      insertQuickReply(
        text,
        resolveQuickReply(quickReply.body, quickReplies.values),
      ),
    )
    setAskedForList(false)
    setDismissed(true)
    caretToEnd.current = true
  }
  const options: ComposerOption[] = [
    ...(offersQuickReplies
      ? filterQuickReplies(quickReplies.items ?? [], filter)
      : []
    ).map((quickReply) => ({ kind: 'quickReply' as const, quickReply })),
    ...filterTemplates(templates.items ?? [], filter)
      .filter((template) => template.sendable)
      .map((template) => ({ kind: 'template' as const, template })),
  ]
  const navigation = useListNavigation(options, optionKey, {
    onChoose: (option) =>
      option.kind === 'quickReply'
        ? insert(option.quickReply)
        : choose(option.template),
    onClose: closeList,
  })
  const active = navigation.active
  const activeOptionId =
    !listOpen || active === null
      ? undefined
      : active.kind === 'quickReply'
        ? quickReplyOptionId(quickListId, active.quickReply)
        : templateOptionId(listId, templateKey(active.template))

  useLayoutEffect(() => {
    const element = box.current
    if (!caretToEnd.current || element === null) return
    caretToEnd.current = false
    element.focus()
    element.setSelectionRange(element.value.length, element.value.length)
  }, [text])
```

9. En el JSX del popover, reemplaza el `<TemplatePicker listId={listId} ... onRetry={templates.onRetry} />` de la rama `: (` (el que sigue a `<TemplateComposer ... />`) por:

```tsx
            <div className="flex flex-col gap-2">
              {offersQuickReplies ? (
                <QuickReplyPicker
                  listId={quickListId}
                  quickReplies={quickReplies.items}
                  status={quickReplies.status}
                  filter={filter}
                  activeKey={navigation.activeKey}
                  onChoose={insert}
                  onRetry={quickReplies.onRetry}
                />
              ) : null}
              <TemplatePicker
                listId={listId}
                templates={templates.items}
                status={templates.status}
                filter={filter}
                activeKey={navigation.activeKey}
                onChoose={choose}
                canRefresh={templates.canRefresh}
                onRefresh={templates.onRefresh}
                isRefreshing={templates.isRefreshing}
                refreshFailed={templates.refreshFailed}
                unavailable={templates.unavailable}
                onRetry={templates.onRetry}
              />
            </div>
```

10. En el `<Textarea>`: agrega `ref={box}` como primera prop, y reemplaza `aria-controls` y `aria-activedescendant` por:

```tsx
            aria-controls={
              listOpen
                ? [offersQuickReplies ? quickListId : null, listId]
                    .filter(Boolean)
                    .join(' ')
                : undefined
            }
            aria-activedescendant={activeOptionId}
```

11. En el `<span id={hintId} className="sr-only">`, cambia el texto de la ventana abierta a `'Enter envía, Shift+Enter agrega una línea. Escribe una barra para elegir una respuesta rápida o una plantilla.'`.

El `onKeyDown` del compositor no cambia: ya delega en `navigation.onKeyDown` y frena el Enter con la lista abierta.

- [ ] **Step 7: Calcular las variables en la página del hilo**

En `src/features/messaging/pages/conversation-thread-page.tsx`:

1. Agrega `QuickReply` y `QuickReplyVariables` a la importación de tipos de `@/features/messaging/types/messaging`, y `import { quickReplyValues } from '@/features/messaging/utils/resolve-quick-reply'`.
2. Antes de `export interface ConversationThreadPageProps`:

```tsx
/** The tenant's quick replies as the route reads them; the page adds what `{nombre}` is here. */
export interface ThreadQuickReplies {
  items: QuickReply[] | undefined
  status: ReadStatus
  /** `{asesor}` and `{empresa}` as the server resolved them; undefined until the list answers. */
  variables: QuickReplyVariables | undefined
  onRetry: () => void
}
```

3. En `ConversationThreadPageProps`, después de `templates: ComposerTemplates`:

```tsx
  /** The tenant's quick replies, for the `/` list (spec 2026-10-12 §4.2). */
  quickReplies: ThreadQuickReplies
```

4. Agrega `quickReplies,` a la desestructuración, después de `templates,`.
5. En `<MessageComposer>`, después de `templates={templates}`:

```tsx
        quickReplies={{
          items: quickReplies.items,
          status: quickReplies.status,
          values: quickReplyValues(conversation, quickReplies.variables),
          onRetry: quickReplies.onRetry,
        }}
```

- [ ] **Step 8: Leer la lista en la ruta del hilo**

En `src/routes/_authenticated/messaging/$conversationId.tsx`:

1. `import { useQuickReplies } from '@/features/messaging/hooks/use-quick-replies'`.
2. Después de `const templates = useMessageTemplates(...)`:

```tsx
  // Only who can answer inserts them, like the templates (Desviación 8); the GET itself asks for read.
  const quickReplies = useQuickReplies({ enabled: canManage })
```

3. En `<ConversationThreadPage>`, después de la prop `templates={{ ... }}`:

```tsx
      quickReplies={{
        items: quickReplies.quickReplies,
        status: quickReplies.status,
        variables: quickReplies.variables,
        onRetry: quickReplies.retry,
      }}
```

- [ ] **Step 9: Correrlas y verlas pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/features/messaging/components/message-composer.test.tsx 'src/routes/_authenticated/messaging/$conversationId.test.tsx'
```

Expected: PASS — los dos archivos completos, las pruebas viejas de plantillas, voz y ventana incluidas. Pega la salida literal.

- [ ] **Step 10: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/respuestas-rapidas') { throw "rama inesperada" }
$files = @(
  'src/features/messaging/hooks/use-list-navigation.ts',
  'src/features/messaging/components/quick-reply-picker.tsx',
  'src/features/messaging/components/message-composer.tsx',
  'src/features/messaging/components/message-composer.test.tsx',
  'src/features/messaging/pages/conversation-thread-page.tsx',
  'src/routes/_authenticated/messaging/$conversationId.tsx',
  'src/routes/_authenticated/messaging/$conversationId.test.tsx'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(messaging): respuestas rápidas con / en el compositor

El / al inicio abre un solo popover con dos grupos, respuestas rápidas y
plantillas, y las flechas los recorren como una lista. Elegir una respuesta
reemplaza el /término por el texto con {nombre}, {asesor} y {empresa}
resueltos, deja el cursor al final y no envía. Con la ventana de 24 h
cerrada sólo quedan las plantillas. El hilo lee la lista sólo para quien
puede responder.
'@
$msgFile = Join-Path $env:TEMP 'qep-respuestas-rapidas-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

Expected: 0 bytes vacío; exactamente los 7 archivos (el `$` del nombre va entre comillas simples, así que `git add` lo recibe literal); sin trailer.

---

### Task 6: Botón ⚡, lista vacía con enlace a Configuración

**Files:**
- Modify: `src/features/messaging/components/quick-reply-picker.tsx`
- Modify: `src/features/messaging/components/message-composer.tsx`
- Modify: `src/features/messaging/pages/conversation-thread-page.tsx`
- Modify: `src/routes/_authenticated/messaging/$conversationId.tsx`
- Test: `src/features/messaging/components/message-composer.test.tsx`
- Test: `src/routes/_authenticated/messaging/$conversationId.test.tsx`

**Interfaces:**
- Consumes: `Link` (`@tanstack/react-router`), la ruta `/settings/quick-replies` (Task 4), `MESSAGING_PERMISSIONS.quickReplyManage`.
- Produces: `QuickReplyPicker` gana `canManage: boolean`; `ComposerQuickReplies` y `ThreadQuickReplies` ganan `canManage: boolean` («`messaging.quick_reply.manage`: la lista vacía enlaza a donde se crean»).

- [ ] **Step 1: Escribir las pruebas**

En `src/features/messaging/components/message-composer.test.tsx`:

1. Cambia `import type { ComponentProps } from 'react'` por `import type { ComponentProps, ReactNode } from 'react'`.
2. Después del `vi.mock` de `voice-recorder`:

```tsx
// `Link` needs a mounted router, and these tests exercise the composer, not the navigation (same
// resource as `tenant-settings-page.test.tsx`). The real destination is tested in the thread route.
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
```

3. En `quickRepliesProp`, agrega `canManage: false,` después de `values: VALUES,`.
4. Al final del archivo:

```tsx
describe('MessageComposer quick replies button', () => {
  const box = () => screen.getByRole('textbox', { name: 'Mensaje' })
  const button = () =>
    screen.queryByRole('button', { name: 'Respuestas rápidas' })

  it('opens the same list and inserts after what was typed, back in the box', async () => {
    const user = userEvent.setup()
    renderComposer({ quickReplies: quickRepliesProp() })

    await user.type(box(), 'Buen día.')
    await user.click(button()!)
    expect(box()).toHaveFocus()
    await user.click(
      within(
        screen.getByRole('listbox', { name: 'Respuestas rápidas' }),
      ).getByRole('option', { name: /horarios/ }),
    )

    expect(box()).toHaveValue('Buen día. Hola Laura, atendemos de 8 a 6.')
    expect(box()).toHaveFocus()
  })

  it('closes the list it opened', async () => {
    const user = userEvent.setup()
    renderComposer({ quickReplies: quickRepliesProp() })

    await user.click(button()!)
    expect(screen.getByRole('listbox', { name: 'Respuestas rápidas' })).toBeInTheDocument()
    await user.click(button()!)

    expect(screen.queryByRole('listbox')).toBeNull()
  })

  it('is not offered where free text cannot go', () => {
    const { unmount } = renderComposer({
      windowClosed: true,
      quickReplies: quickRepliesProp(),
    })
    expect(button()).toBeNull()
    unmount()

    renderComposer({
      lockedReason: 'Esta conversación está resuelta. Reábrela para responder.',
      quickReplies: quickRepliesProp(),
    })
    expect(button()).toBeNull()
  })

  it('is not offered while a voice note is being recorded', async () => {
    const user = userEvent.setup()
    recorderSession()
    renderComposer({ quickReplies: quickRepliesProp() })

    await user.click(screen.getByRole('button', { name: 'Grabar nota de voz' }))
    await screen.findByRole('button', { name: 'Detener' })

    expect(button()).toBeNull()
  })

  it('says there are none, and links to create them with the manage permission', async () => {
    const user = userEvent.setup()
    renderComposer({
      quickReplies: quickRepliesProp({ items: [], canManage: true }),
    })

    await user.type(box(), '/')

    expect(screen.getByText('Sin respuestas rápidas')).toBeInTheDocument()
    expect(
      screen.getByRole('link', { name: 'Crear respuestas rápidas' }),
    ).toHaveAttribute('href', '/settings/quick-replies')
  })

  it('says there are none, without a link, without the manage permission', async () => {
    const user = userEvent.setup()
    renderComposer({ quickReplies: quickRepliesProp({ items: [] }) })

    await user.type(box(), '/')

    expect(screen.getByText('Sin respuestas rápidas')).toBeInTheDocument()
    expect(
      screen.queryByRole('link', { name: 'Crear respuestas rápidas' }),
    ).toBeNull()
  })
})
```

En `src/routes/_authenticated/messaging/$conversationId.test.tsx`, agrega `QUICK_REPLY_ADMIN_PERMISSIONS` a la importación de `@/test/messaging`, y dentro del `describe('quick replies in the thread')`, al final:

```tsx
  it('takes someone who can manage them from an empty list to the settings page', async () => {
    const user = userEvent.setup()
    stubMessagingBackend({ permissions: QUICK_REPLY_ADMIN_PERMISSIONS })

    renderRoute('/messaging/conv-1')
    await user.type(
      await screen.findByRole('textbox', { name: 'Mensaje' }),
      '/',
    )
    await user.click(
      await screen.findByRole('link', { name: 'Crear respuestas rápidas' }),
    )

    expect(
      await screen.findByText('Aún no tienes respuestas rápidas'),
    ).toBeInTheDocument()
  })
```

- [ ] **Step 2: Correrlas y verlas fallar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/features/messaging/components/message-composer.test.tsx 'src/routes/_authenticated/messaging/$conversationId.test.tsx'
```

Expected: FAIL — `button()!` es `null` en las dos primeras (`TypeError` o `Unable to find role="button" and name "Respuestas rápidas"`), y la del enlace no lo encuentra (`Unable to find role="link" and name "Crear respuestas rápidas"`), ni en el compositor ni en el hilo. Las que esperan **no** ver el botón o el enlace pasan desde ya (guardas: anótalo). Pega la salida literal.

- [ ] **Step 3: El enlace en la lista vacía**

En `src/features/messaging/components/quick-reply-picker.tsx`:

1. Primera línea: `import { Link } from '@tanstack/react-router'`.
2. En `QuickReplyPickerProps`, después de `onRetry`:

```tsx
  /** `messaging.quick_reply.manage`: the empty list links to where they are created. */
  canManage: boolean
```

3. Agrega `canManage` a la desestructuración de `QuickReplyPicker` y pásalo a `<Body ... canManage={canManage} />`; agrégalo también a la desestructuración de `Body`.
4. Reemplaza el bloque `if (quickReplies.length === 0) { ... }` por:

```tsx
  if (quickReplies.length === 0) {
    return (
      <div
        role="status"
        className="flex flex-col items-start gap-2 px-3 py-3 text-sm text-muted-foreground"
      >
        <p>Sin respuestas rápidas</p>
        {canManage ? (
          <Link
            to="/settings/quick-replies"
            className="font-medium text-primary underline-offset-4 hover:underline"
          >
            Crear respuestas rápidas
          </Link>
        ) : null}
      </div>
    )
  }
```

- [ ] **Step 4: El botón ⚡ y el permiso en el compositor**

En `src/features/messaging/components/message-composer.tsx`:

1. Importación de iconos: `import { FileText, Lock, Mic, Send, UserRound, X, Zap } from 'lucide-react'`.
2. En `ComposerQuickReplies`, después de `values`:

```tsx
  /** `messaging.quick_reply.manage`: the empty list links to where they are created. */
  canManage: boolean
```

3. En `<QuickReplyPicker>`, agrega `canManage={quickReplies.canManage}`.
4. En la fila de la caja (`<div className="flex items-end gap-2">`), **antes** del `<Textarea>`:

```tsx
          {offersQuickReplies ? (
            // Where an attachment would go in other chats: this composer has none (Desviación 2).
            <Button
              type="button"
              size="icon"
              variant="ghost"
              aria-label="Respuestas rápidas"
              aria-expanded={listOpen}
              className="rounded-full"
              // The box keeps the focus the arrows and Enter depend on.
              onMouseDown={(event) => event.preventDefault()}
              onClick={() => {
                if (listOpen) {
                  closeList()
                } else {
                  setDismissed(false)
                  setAskedForList(true)
                }
                box.current?.focus()
              }}
            >
              <Zap aria-hidden="true" />
            </Button>
          ) : null}
```

La fila sólo se dibuja con la grabadora en reposo, así que el botón desaparece mientras se graba sin otra condición.

- [ ] **Step 5: El permiso por la página y la ruta del hilo**

En `src/features/messaging/pages/conversation-thread-page.tsx`:

1. En `ThreadQuickReplies`, después de `variables`:

```tsx
  /** `messaging.quick_reply.manage`: the empty list links to where they are created. */
  canManage: boolean
```

2. En el objeto `quickReplies={{ ... }}` de `<MessageComposer>`, agrega `canManage: quickReplies.canManage,`.

En `src/routes/_authenticated/messaging/$conversationId.tsx`, en el objeto `quickReplies={{ ... }}`, agrega:

```tsx
        canManage: can(MESSAGING_PERMISSIONS.quickReplyManage),
```

(`can` y `MESSAGING_PERMISSIONS` ya están en el archivo.)

- [ ] **Step 6: Correrlas y verlas pasar**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
bun run test --run src/features/messaging/components/message-composer.test.tsx 'src/routes/_authenticated/messaging/$conversationId.test.tsx'
```

Expected: PASS — los dos archivos completos. Pega la salida literal.

- [ ] **Step 7: Commit**

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/respuestas-rapidas') { throw "rama inesperada" }
$files = @(
  'src/features/messaging/components/quick-reply-picker.tsx',
  'src/features/messaging/components/message-composer.tsx',
  'src/features/messaging/components/message-composer.test.tsx',
  'src/features/messaging/pages/conversation-thread-page.tsx',
  'src/routes/_authenticated/messaging/$conversationId.tsx',
  'src/routes/_authenticated/messaging/$conversationId.test.tsx'
)
bunx prettier --write $files
git update-index --refresh | Out-Null
git status --short
Get-ChildItem "$wt\src" -Recurse -File | Where-Object { $_.Length -eq 0 } | Select-Object -ExpandProperty FullName
git add -- $files
git diff --cached --name-only
$msg = @'
feat(messaging): botón de respuestas rápidas y lista vacía con enlace

El botón con el rayo abre la misma lista que el / para quien no conoce los
atajos y deja el foco en la caja; elegir desde ahí agrega el texto después
de lo escrito. No aparece con la ventana cerrada, con la caja bloqueada ni
mientras se graba. Sin respuestas, quien puede gestionarlas ve el enlace a
Configuración.
'@
$msgFile = Join-Path $env:TEMP 'qep-respuestas-rapidas-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log -1 --format=%B
```

Expected: 0 bytes vacío; exactamente los 6 archivos; sin trailer.

---

### Task 7: Suite completa, build y lint contra la línea de base

**Files:**
- Ninguno nuevo. Sólo se commitea si `prettier --check` encuentra algo de esta rama sin formatear.

**Interfaces:**
- Consumes: `respuestas-rapidas-baseline-*.txt` y `respuestas-rapidas-failures.mjs` de la Task 1.
- Produces: `respuestas-rapidas-final-*` en `qep-frontend-worktrees\` (no se commitean) y el reporte de cierre.

- [ ] **Step 1: Correr todo una vez**

```powershell
$root = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees'
$wt = "$root\respuestas-rapidas"
Set-Location $wt
if ((git branch --show-current) -ne 'feature/respuestas-rapidas') { throw "rama inesperada" }
bun run test --run --reporter=json --outputFile="$root\respuestas-rapidas-final-tests.json"
"tests exit $LASTEXITCODE"
bun "$root\respuestas-rapidas-failures.mjs" "$root\respuestas-rapidas-final-tests.json" "$root\respuestas-rapidas-final-failures.txt"
Compare-Object (Get-Content -LiteralPath "$root\respuestas-rapidas-baseline-failures.txt") (Get-Content -LiteralPath "$root\respuestas-rapidas-final-failures.txt")
bun run build
"build exit $LASTEXITCODE"
bun run lint | Out-File -Encoding utf8 "$root\respuestas-rapidas-final-lint.txt"
"lint exit $LASTEXITCODE"
Get-Content -LiteralPath "$root\respuestas-rapidas-final-lint.txt" -Tail 5
$changed = git diff --name-only develop...HEAD -- src
bunx prettier --check $changed
"prettier exit $LASTEXITCODE"
git status --short
```

Expected:
- `Compare-Object` **no imprime nada**, o sólo líneas `<=` (fallas de la base que ya no fallan). Una línea `=>` es una regresión de esta rama: se corrige en la task dueña del archivo, con su RED/GREEN, antes de seguir.
- `build exit 0` (`tsc -b` incluido: es el único typecheck del repo).
- El resumen de lint con el mismo número de avisos que la base, o menos; ningún error nuevo en archivos de esta rama.
- `prettier exit 0`.
- `git status --short` vacío, salvo `src/routeTree.gen.ts` por fin de línea (`git update-index --refresh`).

Pega la salida literal de cada bloque en el reporte.

- [ ] **Step 2: Repasar los cinco casos del Review Focus**

```powershell
Set-Location 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
$results = Get-Content -LiteralPath '..\respuestas-rapidas-final-tests.json' -Raw | ConvertFrom-Json
$names = @(
  'does not open for a slash in the middle of the text, and Enter sends it as typed',
  'leaves the Enter that commits an IME composition to the text',
  'keeps the templates usable while the quick replies load',
  'says a failed list and retries it, without blocking the templates',
  'leaves no space before a comma or at the end when {nombre} is empty',
  'falls back to empty, never to the username or the phone',
  'leaves no double space when the contact has no name at all',
  'drops the quick replies when the window closes with the list open, and Enter inserts nothing'
)
foreach ($file in $results.testResults) {
  foreach ($test in $file.assertionResults) {
    if ($names -contains $test.title) { "{0} -> {1}" -f $test.title, $test.status }
  }
}
```

Expected: las ocho líneas, todas `-> passed`.

- [ ] **Step 3: Commit de formato, sólo si hizo falta**

Si el Step 1 marcó archivos sin formatear:

```powershell
$wt = 'C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-frontend-worktrees\respuestas-rapidas'
Set-Location $wt
if ((git branch --show-current) -ne 'feature/respuestas-rapidas') { throw "rama inesperada" }
$changed = git diff --name-only develop...HEAD -- src
bunx prettier --write $changed
git update-index --refresh | Out-Null
git status --short
git add -- $changed
$msg = @'
style(messaging): formato de respuestas rápidas
'@
$msgFile = Join-Path $env:TEMP 'qep-respuestas-rapidas-commit.txt'
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding $false))
git commit -F $msgFile
if ((git log -1 --format=%B) -match 'Co-Authored-By|Generated with') { throw "atribución en el commit: git commit --amend -F $msgFile" }
git log --oneline develop..HEAD
```

Si no hizo falta, sólo `git log --oneline develop..HEAD`. Expected: seis commits `feat(messaging): ...` (más el `style` si existió), ninguno con trailer.

- [ ] **Step 4: Reporte de cierre**

En el reporte final (no en un archivo del repo): rama y último sha; la salida literal de las corridas; cuántas fallas tenía la base y cuántas el final, por nombre; las Desviaciones 1-8 y P1-P2 tal como quedaron; y lo que **no** se probó contra un backend real, porque el backend de §3 puede no existir aún:

1. Que `GET /quick-replies` devuelva `variables` con `asesor` y `empresa` y `items` ordenados por `shortcut`.
2. Las mayúsculas de las llaves de `errors` en un `422 validation.failed` (P1).
3. Que `PUT` lea `version` del cuerpo y `DELETE` de la query, y que una versión vieja dé `412`.
4. Que `admin` vea la tarjeta **después** de reiniciar la API y recargar la sesión (§6; memoria `permiso-nuevo-exige-reiniciar-y-recargar`).

Despliegue (§6): backend primero (trae `AddQuickReplies`), después este frontend. **No se mergea a `main`**: el destino de un merge de agente es `develop`, y sólo si el owner lo pide.
