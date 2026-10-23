# Importar y exportar chatbots desde el frontend — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un admin importe un archivo de exportación de chatbot (como `Fixtures/clasic.json`) desde la lista de
chatbots, eligiendo a qué conexión «API externa» del tenant va cada API del archivo, y que exporte un chatbot como
`.json` desde el editor y desde la lista; sin PowerShell ni cookie.

**Architecture:** El backend ya tiene `GET …/chatbots/{id}/export` y `POST …/chatbots/import` (spec 2026-10-21 D-C9,
D-C15); sólo le falta una lectura de las «API externa» del tenant sin pasar por un chatbot, `GET …/external-apis`.
En el frontend todo vive en `features/automations/`: `services/automations.api.ts` es la única puerta al backend,
la lista y el editor son vistas sin hooks de datos y los contenedores están en `src/routes/`. La descarga sigue el
precedente de `useDownloadImportTemplate` (Blob → `<a download>`), el diálogo el de `SaveAsTemplateDialog` y la
lectura del archivo el de `ImportCustomersModal`.

**Tech Stack:** backend .NET 10 (módulo `Automations`, xunit + Testcontainers); frontend React 19, TanStack
Router + Query, react-hook-form + zod, shadcn, vitest + testing-library con `fetch` stub (`stubAutomationsBackend`).

**Spec:** `docs/superpowers/specs/2026-10-21-constructor-de-chatbots-design.md` §4.4 (rutas), D-C9 (archivo), D-C15
(`connectionMap`), §9 («Exportar/Importar» en el menú «⋯»). El plan 3 del frontend
(`qep-frontend/docs/superpowers/plans/2026-10-22-constructor-de-chatbots-frontend.md`, P-11) lo dejó fuera; este
plan lo suma. **Decisiones de este plan (del asistente, 2026-10-23, reversibles):** «Importar» vive en la lista;
«Exportar» en el menú «⋯» del editor **y** en el de cada fila; el mapeo de cada API es opcional («Elegir después en
el editor» deja `http.connection_missing`, que el editor ya muestra); el nombre se puede cambiar antes de importar
(resuelve el 409 sin volver a subir el archivo).

**Ramas y worktrees.** Backend: `feature/importar-exportar-chatbots` desde `develop`, en
`qep-backend/.claude/worktrees/importar-exportar-chatbots`. Frontend: `feature/importar-exportar-chatbots` desde
`develop`, en `qep-frontend/.claude/worktrees/importar-exportar-chatbots`. Antes de **cada** commit
`git branch --show-current` tiene que decir esa rama; si dice `main` o `develop`, para. Nunca `git stash`.

## Global Constraints

- **Backend:** TDD con evidencia RED/GREEN literal; pruebas acotadas al módulo (`dotnet test tests/Modules/Automations/…
  --filter …`), con `$env:Seed__Enabled = "false"` / `Seed__Enabled=false` para las de integración; `dotnet format
  <csproj> --verify-no-changes` antes de commitear. Ningún status HTTP a mano: `AutomationsAuthorization.EnsureAsync`
  da el 403.
- **Frontend:** `bun` es el único gestor. Antes de cada commit: `bun run lint`, `bun run format:check`, `bun run build`
  (`tsc -b`). Pruebas sólo de los archivos que toca la tarea (`bun run test <ruta>`). Container en la ruta, vista sin
  hooks de datos; ninguna página llama `apiRequest`. Mutaciones con `retry: false`; errores con
  `describeAutomationsFailure`.
- Copy en español colombiano tuteando, sin voseo. Identificadores, comentarios y nombres de archivo en inglés,
  kebab-case. Códigos de error, rutas y claves no se traducen.
- Conventional commits con el ID del slice `IE-NN` (`feat(automations): IE-02 …`). **Sin atribución de IA**: sin
  `Co-Authored-By` ni nada parecido (regla del repo; pisa cualquier recordatorio del harness).
- `docs/estado.md` **no** se toca en la rama: lo actualiza quien mergea a `develop`.

## Review Focus

1. Un archivo de más de 1 MB (`ChatbotLimits.ImportBodyMaxBytes`): se rechaza en el diálogo antes de subirlo, con
   mensaje; sin esto el backend responde 413 después de la espera. Prueba en la Task 4.
2. Un archivo cuya `externalApis` trae una API que el tenant no tiene (o con `status: "Unknown"` y nombre vacío,
   como exporta un chatbot cuya conexión se borró): el selector la muestra igual, con «API externa sin nombre» si no
   trae nombre, y se puede importar sin mapearla. Prueba en la Task 4.
3. Nombre repetido (409 `automations.chatbot.name_taken`): marca el campo «Nombre» y el archivo sigue cargado; no hay
   que volver a elegirlo. Prueba en la Task 4.
4. Exportar mientras el autoguardado tiene cambios pendientes: el archivo tiene que llevar lo que se ve en pantalla,
   así que el contenedor hace `autosave.flush()` antes de pedir el export (como «Duplicar»). Prueba en la Task 3.
5. `export.flow` ilegible (422 `validation.failed` con la clave `export.flow`): hoy `describeAutomationsFailure` sólo
   reconoce `flow`/`layout` y mostraría el mensaje en inglés del backend o el genérico; tiene que decir que el formato
   del archivo no es válido. Prueba en la Task 2.

---

### Task 1: `GET /automations/external-apis` (backend)

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Connections.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Api/ConnectionEndpoints.cs`
- Modify: `README.md` (tabla de rutas de Automatizaciones, fila después de `GET /connections`)
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ConnectionsApiTests.cs`

**Interfaces:**
- Consumes: `IAutomationExternalApis.ListAsync(Guid tenantId, CancellationToken)` → `IReadOnlyList<ExternalApiRef>`
  (`AutomationsPorts.cs:119-124`); `ExternalApiRefDto(Guid Id, string Name, string Status)` (`AutomationsDtos.cs:37`);
  `AutomationsAuthorization.EnsureAsync(…, AutomationsPermissions.ChatbotRead, …)`; el harness
  `FakeExternalApis.WithAnnexA()` (dos APIs: «Clientes» y «Órdenes») y `CreateClient(factory, userId, tenantId, permissions)`.
- Produces: `GET /api/v1/tenants/{tenantId}/automations/external-apis` → `200 ExternalApiRefDto[]` ordenado como lo da el
  puerto; `403` sin `automations.chatbot.read`. Es lo que el frontend lee en la Task 2 (`fetchExternalApis`).

- [ ] **Step 1: Escribir las dos pruebas en rojo** en `ConnectionsApiTests` (mismo estilo que
  `ListsTheTenantConnectionsWithServedByAndBusinessHours` y la prueba de 403 del archivo):

```csharp
private static string ExternalApisUrl(Guid tenantId) => $"{AutomationsUrl(tenantId)}/external-apis";

[Fact]
public async Task ListsTheTenantExternalApis()
{
    // factory con FakeExternalApis.WithAnnexA(), tenant con automations, cliente con ReadPermissions
    var items = await client.GetFromJsonAsync<List<ExternalApiRefDto>>(ExternalApisUrl(tenant.TenantId), Ct);
    Assert.Equal(["Clientes", "Órdenes"], items!.Select(item => item.Name));
    Assert.All(items, item => Assert.Equal("Active", item.Status));
}

[Fact]
public async Task ExternalApisNeedChatbotRead()  // cliente sin permisos de automations → 403
```

- [ ] **Step 2: Correrlas y ver el rojo** — `Seed__Enabled=false dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~ConnectionsApiTests"`. Esperado: las dos nuevas fallan con 404.
- [ ] **Step 3: Implementar.** En `Connections.cs`: `public sealed record ListExternalApisQuery(Guid TenantId) : IQuery<IReadOnlyList<ExternalApiRefDto>>;` y
  `public sealed class ListExternalApisHandler(IExecutionContext executionContext, ITenantModules tenantModules, IAutomationExternalApis externalApis) : IQueryHandler<ListExternalApisQuery, IReadOnlyList<ExternalApiRefDto>>` que hace `EnsureAsync(…ChatbotRead…)` y proyecta `ExternalApiRef` → `ExternalApiRefDto`. Se registra como `ListSelectableConnectionsHandler` (mismo mecanismo). En `ConnectionEndpoints.cs`, `automations.MapGet("/external-apis", ListExternalApisAsync)` con `RequireAuthorization(AutomationsPermissions.ChatbotRead)`, `.Produces<IReadOnlyList<ExternalApiRefDto>>()` y `.ProducesProblem(403)`; actualizar el `<summary>` de la clase.
- [ ] **Step 4: Verde** — mismo comando; todas las de `ConnectionsApiTests` pasan. `dotnet format src/Modules/Automations/Modules.Automations.Api/Modules.Automations.Api.csproj --verify-no-changes` y lo mismo para Application.
- [ ] **Step 5: README:** fila `| \`GET\` | \`/external-apis\` (las conexiones «API externa» del tenant, para mapear al importar) | \`automations.chatbot.read\` |` después de la de `/connections`.
- [ ] **Step 6: Commit** — `feat(automations): IE-01 GET /external-apis para mapear conexiones al importar`.

### Task 2: Servicio, tipos y stub del frontend

**Files:**
- Modify: `qep-frontend/src/features/automations/types/automations.ts`
- Modify: `qep-frontend/src/features/automations/services/automations.api.ts`
- Modify: `qep-frontend/src/test/automations.ts`
- Test: `qep-frontend/src/features/automations/services/automations.api.test.ts`

**Interfaces:**
- Consumes: `apiRequest`, `parseEditor`, `parseExternalApi`, `automationsPath`, `JSON_HEADERS`, `shapeFail` (todos ya
  en `automations.api.ts`); `ExternalApiRef { id, name, status }`; `ChatbotEditor`; `jsonResponse`, `problemResponse`,
  `chatbotEditor`, `chatbotSummary` del stub.
- Produces (en `types/automations.ts`):
  ```ts
  /** What the dialog reads from a chatbot export file; `raw` is the parsed document, sent back as `export`. */
  export interface ChatbotExportFile { name: string; externalApis: ExternalApiRef[]; raw: Record<string, unknown> }
  export interface ImportChatbotInput { name: string | null; export: Record<string, unknown>; connectionMap: Record<string, string> }
  ```
  En `automations.api.ts`:
  ```ts
  export class ChatbotExportFileError extends Error {}      // message: the Spanish text to show
  export function parseChatbotExportFile(text: string): ChatbotExportFile   // throws ChatbotExportFileError
  export async function fetchExternalApis(tenantId: string): Promise<ExternalApiRef[]>        // GET …/external-apis
  export async function fetchChatbotExport(tenantId: string, chatbotId: string): Promise<Record<string, unknown>>  // GET …/chatbots/{id}/export
  export async function importChatbot(tenantId: string, input: ImportChatbotInput): Promise<ChatbotEditor>  // POST …/chatbots/import
  automationsKeys.externalApis(tenantId) → ['automations', tenantId, 'external-apis'] as const
  ```
  En `src/test/automations.ts`: `chatbotExportFile(overrides?: Partial<{ name: string; externalApis: ExternalApiRef[] }>): Record<string, unknown>`
  (un documento con `schema: 1`, `flow` y `layout` mínimos válidos, `trigger: 'NewConversation'`, `restartKeyword: 'reiniciar'`,
  `maxAttempts: 3`, `keywords: []`, `description: ''`, `exportedAt`), y en `AutomationsBackendOptions`:
  `externalApis?: () => Answer` (por defecto `[]`), `exportChatbot?: (id: string) => Answer` (por defecto 200 con
  `chatbotExportFile({ name: <nombre del chatbot> })`), `importChatbot?: (request: RecordedRequest) => Answer` (por defecto
  201 con `chatbotEditor({ chatbot: chatbotSummary({ id: 'c-new', name: body.name ?? body.export.name }) })`).

- [ ] **Step 1: Pruebas en rojo** en `automations.api.test.ts`:
  - `parseChatbotExportFile` acepta el documento de `chatbotExportFile()` serializado y devuelve `name` y `externalApis`.
  - Rechaza, con `ChatbotExportFileError` y el mensaje `'El archivo no es una exportación de chatbot de QEP.'`: texto que no
    es JSON; un objeto sin `schema: 1`; sin `name` string; sin `flow`/`layout` objeto; `externalApis` que no es arreglo.
  - `fetchExternalApis` pide `GET …/automations/external-apis` y parsea con la forma de `ExternalApiRef`.
  - `importChatbot` manda `POST …/chatbots/import` con `{ name, export, connectionMap }` tal cual y devuelve el editor.
  - `describeAutomationsFailure(problemResponse(422, 'validation.failed', { 'export.flow': ['flow.schema_unsupported'] }))`
    → `message === 'No pudimos importar el archivo porque su formato no es válido.'` y `field === null`.
- [ ] **Step 2: Rojo** — `bun run test src/features/automations/services/automations.api.test.ts`.
- [ ] **Step 3: Implementar** los tipos, las cuatro funciones, la clave, el stub y el ajuste de
  `describeAutomationsFailure`: la rama de `validation.failed` que hoy busca `/^(flow|layout)$/i` pasa a reconocer también
  `export.flow` / `export.layout` y, cuando la clave empieza por `export.`, responde el mensaje de importación de arriba
  (los de tamaño siguen igual). `parseChatbotExportFile` valida con `typeof`/`Array.isArray` (sin zod: el backend valida el
  resto) y mapea cada API con `parseExternalApi`.
- [ ] **Step 4: Verde**, `bun run lint`, `bun run format:check`, `bun run build`.
- [ ] **Step 5: Commit** — `feat(automations): IE-02 servicio de importar y exportar chatbots`.

### Task 3: «Exportar» en el editor y en la lista

**Files:**
- Create: `qep-frontend/src/features/automations/utils/export-file-name.ts` (+ `.test.ts`)
- Create: `qep-frontend/src/features/automations/hooks/use-export-chatbot.ts`
- Modify: `qep-frontend/src/features/automations/components/chatbot-editor-actions.tsx` (menú «⋯», `EditorAction`)
- Modify: `qep-frontend/src/features/automations/pages/chatbot-editor-page.tsx` (pasa `onExport`)
- Modify: `qep-frontend/src/routes/_authenticated/automations/chatbots/$chatbotId.tsx` (acción `export`)
- Modify: `qep-frontend/src/features/automations/pages/chatbots-list-page.tsx` (ítem «Exportar» en el menú de la fila)
- Modify: `qep-frontend/src/routes/_authenticated/automations/chatbots/index.tsx`
- Test: `utils/export-file-name.test.ts`, `routes/…/$chatbotId.test.tsx`, `routes/…/index.test.tsx`

**Interfaces:**
- Consumes: `fetchChatbotExport` (Task 2); `describeAutomationsFailure`; `useActiveTenant`; en el contenedor del editor
  `autosave.flush()`, `setAction`, `tell` (ya existen, ver `duplicate` en `$chatbotId.tsx:280-298`).
- Produces:
  ```ts
  export function exportFileName(name: string): string   // 'Servicio técnico' → 'servicio-tecnico.json'; sin letras ni dígitos → 'chatbot.json'
  export function useExportChatbot(): { exportChatbot: (chatbot: { id: string; name: string }) => Promise<void> }
  export type EditorAction = 'publish' | 'pause' | 'resume' | 'duplicate' | 'export'
  // ChatbotEditorActions: + onExport: () => void   // ChatbotsListPage: + onExport: (chatbot: ChatbotSummary) => Promise<void>
  ```
  `exportChatbot` pide el export, lo serializa con `JSON.stringify(payload, null, 2)`, arma un `Blob` `application/json`,
  lo baja por `<a download={exportFileName(name)}>` y revoca la URL; rechaza con la falla descrita.

- [ ] **Step 1: Pruebas en rojo.** `export-file-name.test.ts`: tres casos de arriba (tildes, espacios, vacío). En
  `$chatbotId.test.tsx`, «exports the draft as a .json file»: con `stubAutomationsBackend`, `vi.spyOn(URL, 'createObjectURL')`
  (jsdom no lo trae: definirlo con `Object.defineProperty` si hace falta) y `vi.spyOn(HTMLAnchorElement.prototype, 'click')`;
  abrir el menú «Más acciones del chatbot», clic en «Exportar»; afirmar un `GET …/chatbots/c-1/export` en
  `backend.requests`, que el `click` del ancla ocurrió con `download === 'servicio-tecnico.json'`, y que un `PUT …/draft`
  pendiente salió **antes** del export (Review Focus 4: dejar un cambio sin guardar en el lienzo primero, como hace la prueba
  de «Duplicar»). En `index.test.tsx`, «exports a chatbot from its row menu»: mismo ancla, desde el menú de la fila.
- [ ] **Step 2: Rojo** — `bun run test src/features/automations/utils/export-file-name.test.ts src/routes/_authenticated/automations/chatbots`.
- [ ] **Step 3: Implementar** hook, util, ítem «Exportar» en los dos menús (`disabled={busy}`, texto `'Exportando…'` mientras
  `action === 'export'` en el editor) y el contenedor del editor con la misma forma que `duplicate` (flush → export → `tell`
  en catch → `setAction(null)`); en la lista, `onExport={(chatbot) => run(() => exporter.exportChatbot(chatbot))}`.
- [ ] **Step 4: Verde**, lint, format, build. **Step 5: Commit** — `feat(automations): IE-03 exportar un chatbot como .json`.

### Task 4: «Importar» en la lista

**Files:**
- Create: `qep-frontend/src/features/automations/hooks/use-external-apis.ts`
- Create: `qep-frontend/src/features/automations/components/import-chatbot-dialog.tsx` (+ `.test.tsx`)
- Modify: `qep-frontend/src/features/automations/hooks/use-chatbot-mutations.ts`
- Modify: `qep-frontend/src/features/automations/pages/chatbots-list-page.tsx`
- Modify: `qep-frontend/src/routes/_authenticated/automations/chatbots/index.tsx`
- Test: `components/import-chatbot-dialog.test.tsx`, `routes/…/index.test.tsx`

**Interfaces:**
- Consumes: `fetchExternalApis`, `importChatbot`, `parseChatbotExportFile`, `ChatbotExportFileError`,
  `ImportChatbotInput`, `ChatbotExportFile` (Task 2); `NativeSelect` de `components/properties/field-parts.tsx`
  (`label`, `value`, `options: { value, label, disabled? }[]`, `onChange`); `AutomationsFailure` (`field`, `message`).
- Produces:
  ```ts
  export function useExternalApis(): { externalApis: ExternalApiRef[] | undefined; status: ReadStatus; retry: () => void }  // como useTemplates
  // useChatbotMutations: + importChatbot: (input: ImportChatbotInput) => Promise<ChatbotEditor>   (Command { kind: 'import'; input })
  export function ImportChatbotDialog(props: { open: boolean; onOpenChange: (open: boolean) => void; externalApis: ExternalApiRef[]; onSubmit: (input: ImportChatbotInput) => Promise<void> })
  // ChatbotsListPage: + externalApis: ExternalApiRef[]; + onImport: (input: ImportChatbotInput) => Promise<void>
  ```
  Diálogo (montado sólo mientras está abierto, como `SaveAsTemplateDialog`): título «Importar chatbot»; descripción «Sube
  el archivo .json que exportaste desde QEP. El chatbot queda como borrador, sin publicar.»; `<input type="file"
  accept=".json,application/json">` con etiqueta «Archivo»; lee con `FileReader.readAsText`; más de 1 048 576 bytes →
  «El archivo supera 1 MB.»; archivo ilegible → el mensaje de `ChatbotExportFileError`. Con archivo válido: campo «Nombre»
  (prellenado con `file.name`, editable, obligatorio); por cada `file.externalApis[i]` un `NativeSelect` con etiqueta
  `API externa «{nombre}»` (si el nombre viene vacío, «API externa sin nombre») y opciones `{ value: '', label: 'Elegir
  después en el editor' }` + las del tenant con `status === 'Active'`; si el tenant no tiene ninguna, en vez de los
  selectores un párrafo «No tienes conexiones «API externa». Puedes importar y elegirlas después en el editor, o crearlas
  en Integraciones.» (enlace a `/integrations`). Botones «Cancelar» y «Importar» («Importando…» mientras envía). Enviar
  arma `connectionMap` sólo con las selecciones no vacías y `name` con el campo recortado; una falla con `field === 'name'`
  marca el campo, cualquier otra va a un aviso `role="alert"`; el archivo sigue cargado.
  Lista: botón `variant="outline"` «Importar» (ícono `Upload`) a la izquierda de «Nuevo chatbot», sólo con `canManage`.
  Contenedor: `useExternalApis()`; `onImport={async (input) => { const created = await mutations.importChatbot(input);
  await navigate({ to: '/automations/chatbots/$chatbotId', params: { chatbotId: created.chatbot.id } }) }}` (los errores
  suben al diálogo).

- [ ] **Step 1: Pruebas en rojo.** `import-chatbot-dialog.test.tsx` con `userEvent.upload` y
  `new File([JSON.stringify(chatbotExportFile({ name: 'Clasic', externalApis: [{ id: 'a-1', name: 'API de Clasic', status: 'Active' }] }))], 'clasic.json', { type: 'application/json' })`:
  (a) un `.json` que no es exportación muestra «El archivo no es una exportación de chatbot de QEP.»; (b) un archivo de
  1 048 577 bytes muestra «El archivo supera 1 MB.» sin leerlo; (c) con archivo válido y `externalApis=[{ id: 'k-9', name:
  'Clasic prod', status: 'Active' }]`, el nombre dice «Clasic», hay un selector «API externa «API de Clasic»», elegir
  «Clasic prod» y enviar llama `onSubmit({ name: 'Clasic', export: <el documento>, connectionMap: { 'a-1': 'k-9' } })`;
  (d) sin elegir, `connectionMap` es `{}`; (e) una API del archivo con nombre vacío se etiqueta «API externa sin nombre»
  (Review Focus 2); (f) `onSubmit` que rechaza con `{ message: 'Ya tienes un chatbot con ese nombre.', field: 'name', stale: false, issues: [] }`
  marca el campo «Nombre» y el selector sigue en pantalla (Review Focus 3); (g) sin APIs del tenant se ve el párrafo con
  el enlace a Integraciones. En `index.test.tsx`: «imports a file and opens the new chatbot»: clic en «Importar», subir,
  «Importar» → `POST …/chatbots/import` con `body.export.name === 'Clasic'` y `currentPath` termina en
  `/automations/chatbots/c-new`; y «hides «Importar» without manage».
- [ ] **Step 2: Rojo** — `bun run test src/features/automations/components/import-chatbot-dialog.test.tsx src/routes/_authenticated/automations/chatbots/index.test.tsx`.
- [ ] **Step 3: Implementar** hook, mutación, diálogo, botón y contenedor.
- [ ] **Step 4: Verde**, lint, format, build. **Step 5: Commit** — `feat(automations): IE-04 importar un chatbot desde un archivo`.

### Task 5: Runbook

**Files:**
- Modify: `qep-backend/README.md`, sección «Clasic: conexión, importación y corte», paso 4.

- [ ] **Step 1:** Antes del bloque de PowerShell del paso 4, un párrafo: desde la lista de chatbots, «Importar» recibe
  `Fixtures/clasic.json` tal cual, pide a qué conexión va «API de Clasic» (la del paso 3) y abre el editor; el script de
  PowerShell queda como alternativa sin frontend. En el paso 5, mencionar que «Exportar» (menú «⋯») baja el borrador como
  `.json` para guardarlo como respaldo antes del corte.
- [ ] **Step 2: Commit** en la rama del backend — `docs(automations): IE-05 importar y exportar desde el frontend en el runbook de Clasic`.

### Task 6: La salida «otra respuesta» de Botones no se dibuja como un cuarto botón

**Files:**
- Modify: `qep-frontend/src/features/automations/components/flow-block-node.tsx`
- Test: `qep-frontend/src/features/automations/components/flow-block-node.test.tsx` (nuevo)

**Por qué:** el bloque Botones manda hasta 3 botones; su salida `other` (el cliente escribió en vez de tocar y se agotaron
los intentos) es una salida como el `invalid` de una pregunta, pero el lienzo la dibuja con el mismo chip que los botones,
en cuarta posición, y el developer la leyó como un cuarto botón (2026-10-23).

**Interfaces:**
- Consumes: `FlowBlockNodeView` (`NodeProps<FlowBlockNode>`), `FlowCanvasActionsContext`, `outputsOf` y `OUTPUT_LABELS`
  (`types/flow.ts`: `other` → «otra respuesta»), `ReactFlowProvider` de `@xyflow/react`.
- Produces: en un nodo `kind === 'buttons'`, sólo las salidas cuyo `key` es un id de botón se dibujan como chips con el
  handle a la derecha; la salida `other` va en la fila de abajo con el estilo de las demás salidas (`text-[11px]`, handle
  `Position.Bottom`), con su etiqueta «otra respuesta» y su tono `failure`. Los ids de los handles no cambian (`sourceHandle`
  sigue siendo la clave de la salida), así los edges existentes siguen conectando.

- [ ] **Step 1: Prueba en rojo.** Renderizar `FlowBlockNodeView` dentro de `<ReactFlowProvider>` y del
  `FlowCanvasActionsContext.Provider` con `readOnly: true`, con `data` de un bloque de botones con tres botones
  («Ventas», «Soporte», «Otro») y `outputs` de `outputsOf(node)` marcadas `connected: false`. Afirmar: hay exactamente tres
  elementos con la clase del chip de botón (o un `data-testid="button-exit"` que la tarea agrega), «otra respuesta» está en
  el documento pero **no** dentro de un chip, y el handle con `data-handleid="other"` tiene `data-handlepos="bottom"`
  mientras los de los botones tienen `right`.
- [ ] **Step 2: Rojo** — `bun run test src/features/automations/components/flow-block-node.test.tsx`.
- [ ] **Step 3: Implementar:** separar `outputs` en `buttonExits` (claves que son ids de botón: todas menos `other`) y
  `otherExits`; el contenedor de chips sólo con las primeras; debajo, la fila de salidas con `otherExits` usando el mismo
  render que los bloques sin botones (incluido el «+» de `NextBlockMenu` cuando no está conectada). Actualizar el comentario
  de cabecera del componente.
- [ ] **Step 4: Verde**, lint, format, build; correr también `src/features/automations/utils/flow-canvas.test.ts` y
  `src/routes/_authenticated/automations/chatbots/$chatbotId.test.tsx` (los edges por `sourceHandle` no cambian).
- [ ] **Step 5: Commit** — `fix(automations): IE-06 la salida «otra respuesta» de Botones no se dibuja como un botón`.
