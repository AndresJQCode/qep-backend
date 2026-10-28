# Product categories admin + POS quick access — design

Date: 2026-10-26. Decided by the owner. Visual reference (private canvas):
https://claude.ai/artifact/MA8VGzFFfkTZt7ZjPC85az — boards «Productos › Categorías», «POS · chips de
acceso rápido», «POS · modal de categorías abierta».

## Why

At the register the cashier needs one tap per category. A searchable popover is slow, and a chip
per root category buries the grid when a tenant has 50 of them. So the tenant chooses which
categories show as chips at the register (quick access, ordered), and the rest of the tree opens in
a modal. Categories also need an admin screen: today they can only be created from the product form.

## Decisions

- D-1 Quick access is **per tenant**, ordered, **no limit** on how many. Any category can be pinned,
  root or not.
- D-2 Deleting a category with products or with child categories is **blocked** (the FKs are
  already `RESTRICT`). The person moves them first.
- D-3 The register shows **one row** of chips: «Todas», the pinned ones in order, a temporary chip
  for a non-pinned category picked in the modal (with its own ×), and «Más categorías», which opens
  the modal. **No** row of subcategories or siblings.
- D-4 Quick access lives as a nullable column on `catalog.product_categories`
  (`pos_quick_access_position`), not in a POS table: POS has no settings table and the category
  aggregate already belongs to Catalog. Positions are contiguous, 1..N.
- D-5 Changing quick access does **not** bump a category's `Version`: the position is not the
  category's content, and bumping it would make an open rename/delete fail with 412 for no reason.
- D-6 Deploy order: backend first. A frontend facing an old backend (no position field) falls back
  to showing the root categories as chips.

## Backend contract (`qep-backend`, module Catalog; POS reads through the existing lookup)

Base: `/api/v1/tenants/{tenantId}`. Writes need `catalog.product.manage`; reads keep their current
permissions (`catalog.product.read`, `pos.sale.create`).

### Changed reads

- `GET /catalog/categories` → each `CategoryResponse` adds `posQuickAccessPosition: int | null`.
- `GET /pos/categories` → each `PosCategoryResponse` adds `quickAccessPosition: int | null`.
  Order of `items` unchanged (by name); the client sorts the pinned ones by position.

### New writes

1. `PUT /catalog/categories/{categoryId}` — rename and/or move.
   - Body `{ "name": string, "parentId": string | null }`. Header `If-Match: "<version>"` required:
     428 without it, 412 when stale (same pattern the codebase already uses for other `If-Match`
     endpoints — verify and follow it).
   - 200 with the updated `CategoryResponse` (version + 1).
   - Errors: 404 `catalog.category.not_found`; existing name codes (`name_required`,
     `name_too_long`, `name_taken`); `parent_not_found`; `too_deep` (the moved subtree's deepest
     node must stay within `MaxDepth`); new `catalog.category.cycle` (parent is itself or one of its
     descendants). Status codes: whatever the codebase uses for each kind (422 domain, 409 unique,
     etc.) — verify, don't invent.
2. `DELETE /catalog/categories/{categoryId}` — `If-Match` required as above. 204.
   - New codes `catalog.category.has_products` and `catalog.category.has_children`, checked
     **before** hitting the FK (the FK stays as the last line of defence). Products count = any
     product, active or inactive, whose `category_id` is this one.
   - Deleting a pinned category removes it from quick access and re-compacts the positions.
3. `PUT /catalog/categories/pos-quick-access` — replaces the whole ordered list.
   - Body `{ "categoryIds": string[] }` — the order is the position (index 0 → position 1). Empty
     list = nothing pinned. No `If-Match` (whole-list replace, last write wins).
   - Every id must exist in the tenant → else 422 `catalog.category.not_found` (or the codebase's
     convention for an unknown id inside a body). Duplicates → 422
     `catalog.category.quick_access_duplicate`.
   - 200 with the full `CategoriesResponse` so the client can refresh in one go.
   - Route must not collide with `{categoryId}` (use a guid route constraint).
- Each write audits in its handler like the existing category create.
- Migration adds `pos_quick_access_position integer NULL`. Optional partial unique index
  `(tenant_id, pos_quick_access_position) WHERE pos_quick_access_position IS NOT NULL` only if the
  replace is done in a way that never violates it mid-transaction; otherwise skip it and rely on the
  handler.

## Frontend (`qep-frontend`)

### Categorías screen

- Route `/catalog/categories`. A tab bar «Catálogo | Categorías» on the two list pages of
  Productos (`/catalog/products` and `/catalog/categories`); product create/edit pages keep no tabs.
  The sidebar «Productos» item keeps pointing at `/catalog`.
- Read with `catalog.product.read`; every write control needs `catalog.product.manage` (without it
  the screen is read-only, no disabled-looking buttons that lie).
- Left: the tree (`sortAsTree`), columns Nombre (children indented), Productos (the catalog's
  `productCount`: direct products, active and inactive — say so in a column hint, never sum in the
  browser), «Acceso rápido en POS» switch, actions Editar / Eliminar. Search filters the tree
  (`filterCategoryTree`).
- «Nueva categoría»: name + parent (reuse the existing create step / picker pieces where they fit).
- «Editar»: rename + move (parent picker excludes the category itself and its descendants). Sends
  `If-Match`. A 412 says «Alguien cambió esta categoría. Recarga e intenta de nuevo.» and refetches.
- «Eliminar»: confirm dialog; `has_products` → «Tiene productos. Muévelos a otra categoría
  primero.»; `has_children` → «Tiene subcategorías. Muévelas o elimínalas primero.»
- Right: «Acceso rápido del POS» panel — ordered list (position badge, name, path, subir / bajar /
  quitar), «N elegidas», and a preview of the register's chip row. Every change sends the full list
  with `PUT …/pos-quick-access`; controls disabled while it is pending; on success write the
  response into the catalog categories cache and invalidate `['pos', tenant, 'categories']`.
- Loading, empty («Todavía no hay categorías»), error with retry, success.

### POS register

- `CategoryFilter` becomes: one `role="group"` «Categorías» row — «Todas» (pressed when no filter),
  pinned categories sorted by `quickAccessPosition`, a temporary pressed chip for a selected
  category that is not pinned (with a «Quitar filtro <name>» × button), and «Más categorías»
  (`aria-haspopup="dialog"`). No subcategory rows.
- Old backend (every `quickAccessPosition` is `undefined`): treat the root categories as pinned.
  Backend new with nothing pinned: only «Todas» + «Más categorías».
- «Más categorías» opens a dialog «Categorías»: breadcrumb (Todas › Calzado …), search across the
  whole tree (accent/case-folded, shows the parent as context), tiles in a grid with name,
  «N productos» (the POS rolled-up count) and «· N subcategorías», a «Rápido» badge for pinned ones,
  a chevron when it has children. A tile with children drills in; a leaf picks and closes. At a
  non-root level a footer button «Ver todo <name> (N)» picks that level. Esc closes the dialog and
  must not reach the register's window Esc shortcut (it clears the search).
- Keep: hides entirely while loading / on first-load error / without categories; a selected category
  that disappears drops the filter (existing logic in `pos-register-page.tsx`).
- Update the categories gotcha in `qep-frontend/CLAUDE.md`.

## Out of scope

Drag-and-drop ordering (subir/bajar is enough for now), bulk move of products, per-register or
per-user quick access.
