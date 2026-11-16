# Soporte (PQRSF) — Variables en los documentos legales del portal, backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development o superpowers:executing-plans.
> Un solo bloque, un implementador, una revisión. Steps use checkbox (`- [ ]`).

**Goal:** que los tres documentos legales y la pregunta de autorización usen `{{empresa}}`, `{{tipo_documento}}`,
`{{documento_identificacion}}` y `{{nombre_comercial}}`, que la consola conozca la lista y que el portal y la foto de la aceptación
reciban el texto resuelto.

**Architecture:** todo en el módulo Support, sin migración. La lista y el análisis de tokens viven en el dominio
(`SupportLegalVariables`); la validación al publicar, en `SupportLegalDocumentVersion.Create`; la resolución, en un solo lugar,
`SupportServedLegal.ReadAsync`. Deploy: backend antes que la consola.

**Spec:** [`docs/superpowers/specs/2026-11-16-support-legal-document-variables-design.md`](../specs/2026-11-16-support-legal-document-variables-design.md).

## Antes de empezar

- Worktree `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\support-legal-variables`,
  rama `feature/support-legal-variables`. Rutas absolutas siempre. `git branch --show-current` antes de cada commit.
- Conventional commits en inglés, `git commit -m "…" -- <rutas>`, **sin `Co-Authored-By` ni atribución de IA**.
- **Pruebas (owner):** se escriben y **no se corren**. RED = `dotnet build` del proyecto de pruebas fallando por
  compilación; GREEN = `dotnet build Backend.slnx` sin errores ni avisos. Salidas literales al reporte, más la lista
  exacta de `--filter` para que el owner corra después.
- Si `Api.exe` corre, el build falla por archivo bloqueado: avisa, no lo detengas.
- `docs/estado.md` no se toca en esta rama.
- Si aparece la necesidad de una migración, se frena y se reporta.

## Global Constraints

- `SupportDomainException` → 422, ningún status a mano.
- El código de error es `support.legal_document.unknown_variable` (singular, como lo fijó el contrato con la consola), aunque los
  demás de los documentos dicen `legal_documents`.
- Ningún valor resuelto entra al HTML sin codificar; la pregunta es texto plano y va sin codificar (V-2).
- `SupportLayerTests` no se afloja: el dominio no conoce DTOs de Application.

## Review Focus

1. Lo que la validación acepta, la resolución lo resuelve: las dos usan el mismo patrón de nombre.
2. Codificación: `&` y `<` en la razón social llegan como `&amp;` y `&lt;` en el HTML y tal cual en la pregunta.
3. La foto de la aceptación guarda lo resuelto; la versión guardada conserva el token.
4. Sin empresa y con un token de empresa: `404 support.public.not_found`, no un 500.

## Bloque único

### Task 1: dominio, publicación y consola

**Archivos:** `Modules.Support.Domain/SupportLegalVariables.cs` (nuevo), `SupportLegalDocumentVersion.cs`, `SupportErrorCodes.cs`,
`Modules.Support.Application/SupportDtos.cs`, `Settings/GetLegalDocuments.cs`; pruebas `SupportLegalVariablesTests` (nueva),
`SupportLegalDocumentVersionTests`, `SupportLegalDocumentsApiTests`.

- `SupportLegalVariables`: `All` (nombre, etiqueta, ejemplo, en el orden del spec), `UnknownIn(text)`, `FillHtml(html, values)` y
  `FillText(text, values)` (null si un token de la lista no tiene valor), con `SupportLegalVariableValues(TenantName,
  OrganizationName, OrganizationDocumentTypeCode, OrganizationDocumentNumber)`.
- `SupportErrorCodes.LegalDocumentUnknownVariable = "support.legal_document.unknown_variable"`.
- `SupportLegalDocumentVersion.Create`: después de las reglas del contenido, un token desconocido en el HTML o en la pregunta es
  `SupportDomainException(LegalDocumentUnknownVariable)`.
- `SupportLegalDocumentsDto` suma `IReadOnlyList<SupportLegalVariableDto> Variables` **al final**; `GetLegalDocumentsHandler` lo
  llena desde `SupportLegalVariables.All`.
- Pruebas unitarias: la lista, desconocidos (`{{ciudad}}`, `{{Empresa}}`, `{{}}`), espacios, HTML codificado con `&` y `<`, texto sin
  codificar, sin empresa → null, `nombre_comercial` sin empresa, token desconocido literal al resolver, `Create` con token
  desconocido en contenido y en pregunta.
- Integración: `GET` lista las variables; publicar con desconocida es 422 sin versión; publicar con conocidas guarda los tokens.

- [x] RED → GREEN → commit `feat(support): validate and list legal document variables`.

### Task 2: servir y radicar resuelto

**Archivos:** `Modules.Support.Application/Public/SupportServedLegal.cs`, `Public/GetPublicForm.cs`, `Public/FilePublicTicket.cs`;
prueba de integración `SupportLegalVariablesApiTests` (nueva) y `SupportPublicHarness` (nombre de la empresa sembrada).

- `ReadAsync` recibe `SupportPublicOrganizationDto? organization` y resuelve cada documento (`FillHtml`) y la pregunta (`FillText`,
  sólo la del tenant). Sin valor o fuera de los topes → `SupportPublicResolver.NotFound()`.
- `GetPublicForm` lee la empresa una vez; `FilePublicTicket` suma `ISupportOrganizationDirectory` y la lee antes de `ReadAsync`.
- Integración: `GET /form` sirve HTML y pregunta resueltos (con `&` codificado sólo en el HTML); radicar con solicitante y anónimo
  guarda en `support.legal_acceptances` el HTML y la pregunta resueltos; sin empresa y con `{{empresa}}` es 404, con sólo
  `{{nombre_comercial}}` es 200.

- [x] RED → GREEN → commit `feat(support): serve legal documents with their variables resolved`. (Desvío: estas pruebas son
  todas HTTP y compilan sin el cambio, así que su RED no es de compilación; es de comportamiento y se ve al correrlas sobre el
  commit anterior.)

### Cierre

- [ ] README § Formulario público de Soporte y tabla de la consola. `dotnet build Backend.slnx` sin avisos. Lista de `--filter`
  para el owner. Una revisión del bloque.

## Contrato para las pantallas (BFF)

| Campo / código | Uso en la consola |
| --- | --- |
| `variables` de `GET …/settings/legal-documents` | Menú «Insertar variable» del editor y de la pregunta; `example` para la vista previa. |
| `422 support.legal_document.unknown_variable` | «Hay una variable que no existe. Usa las de la lista.» |
| Tokens en `contentHtml` y `authorizationQuestion` de las lecturas | El editor los muestra como están; la consola nunca los resuelve. |
