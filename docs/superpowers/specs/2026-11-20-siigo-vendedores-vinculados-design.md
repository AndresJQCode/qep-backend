# Siigo: el vendedor sale de un vínculo asesor → usuario de Siigo

Fecha: 2026-11-20. Repo: `qep-backend`. Par de frontend:
`qep-frontend/docs/superpowers/specs/2026-11-20-siigo-vendedores-vinculados-frontend-design.md`.
Reemplaza el cruce por correo de `2026-11-20-siigo-vendedor-desde-asesor-design.md` (ya desplegado).

## 1. Problema

Hoy el vendedor de cada factura se elige cruzando el correo del asesor del pedido con el `email` de los usuarios de Siigo,
al facturar. Depende de que el dato coincida exacto en los dos sistemas, y en la cuenta real del owner hay usuarios con
correos genéricos (`sac@…`, `compras@…`) y documentos de relleno (`1234`, `12345678`). Un miembro de QEP tampoco tiene
número de identificación (la membresía sólo guarda `DisplayName` y `AdvisorCode`), así que cruzar por documento exigiría
un dato nuevo en Tenancy.

## 2. Decisión (owner, 2026-11-20)

- El vendedor es un **vínculo explícito**: cada asesor (membresía) de QEP se vincula, por conexión, a un usuario de Siigo.
- Se configura en un **paso nuevo del asistente, «Vendedores»**, como los vínculos de impuestos, formas de pago y
  productos.
- El correo deja de decidir al facturar; sólo **sugiere** vínculos («Vincular por correo», como «Vincular por código» en
  productos).
- Un asesor sin vínculo **bloquea** su factura (sin vendedor por defecto), igual que hoy.

## 3. Modelo

### 3.1 Dominio

- `SiigoSellerLink(Guid MembershipId, string SiigoUserId, string SiigoUserName)` en `Modules.Siigo.Domain`. `SiigoUserName`
  es una foto del nombre al vincular (como el nombre del producto en los vínculos de variante): sirve para mostrar un vínculo
  cuyo usuario ya no está activo en Siigo.
- `SiigoSettings.SellerLinks` y `ReplaceSellerLinks(IReadOnlyList<SiigoSellerLink>)`: reemplaza el paso entero, como
  `ReplaceTaxLinks`. Una membresía no se repite; `SiigoUserId` hasta `IdMaxLength`; el nombre hasta 200.
- `SiigoSettings.SellerFor(Guid membershipId) → string?`.
- El mismo usuario de Siigo puede estar en varios vínculos (un usuario compartido por dos asesores es válido).

### 3.2 Persistencia

Tabla `siigo.seller_links`: `connection_id` (FK a `siigo.settings`, cascade), `membership_id`, `siigo_user_id`
(`varchar(50)`), `siigo_user_name` (`varchar(200)`). PK `(connection_id, membership_id)`. Migración aditiva
`AddSiigoSellerLinks`.

### 3.3 Readiness

Paso nuevo `SiigoSetupStep.Sellers`, entre `Payments` y `Products` (el enum se ordena como lo recorre el asistente).
Incompleto con `seller_link_missing` mientras **no haya ningún** vínculo, como impuestos y formas de pago: qué asesor hace
falta depende del pedido, y ese faltante sale al facturar.

## 4. Contrato HTTP (`siigo.settings.manage`, mismas rutas base que el resto del asistente)

### 4.1 `GET …/settings`

`SiigoSettingsDto` agrega `sellers`: una fila por **membresía activa** del tenant más una por cada vínculo de una membresía
que ya no está activa (para poder verlo y quitarlo):

```json
"sellers": [
  { "membershipId": "…", "name": "Vanesa Hernández", "email": "asesora10@ritualbotanico.com", "active": true,
    "siigoUserId": "800", "siigoUserName": "VANESA HERNANDEZ" },
  { "membershipId": "…", "name": null, "email": "asesora3@ritualbotanico.com", "active": true,
    "siigoUserId": null, "siigoUserName": null }
]
```

`name` es el `DisplayName` de la membresía (puede ser null); `email`, el de Identity. El orden es por `name ?? email`. La
lista de membresías sale de un puerto nuevo `ISiigoMemberDirectory.ListActiveAsync(tenantId)` → `(MembershipId,
DisplayName, Email)`, con adaptador en Bootstrapper sobre Tenancy/Identity (Siigo no referencia a Tenancy para esto).
**Todas** las membresías activas, sin filtro de rol: el asesor de un pedido puede ser cualquier miembro (un pedido de la
tienda va al owner o al miembro configurado).

### 4.2 `PUT …/sellers`

```json
{ "sellers": [ { "membershipId": "…", "siigoUserId": "800", "siigoUserName": "VANESA HERNANDEZ" } ] }
```

Con `If-Match`, como los demás pasos. Reemplaza los vínculos enteros; una fila con `siigoUserId: null` no se guarda (es
«sin vincular»). Validación (422 `validation.failed` con el campo): `membershipId` no repetido; `siigoUserId` hasta 50;
`siigoUserName` requerido con `siigoUserId`, hasta 200. Una `membershipId` que no es del tenant ni está en los vínculos
actuales responde 422 `siigo.settings.member_not_found` (no se vincula a alguien que no es miembro). Responde el
`SiigoSettingsDto` entero.

### 4.3 `POST …/sellers/auto-link`

Vincula **sólo** las membresías activas sin vínculo cuyo correo coincide (`Trim()` + `OrdinalIgnoreCase`) con un usuario
**activo** de Siigo (`GET /v1/users`, todas las páginas, leído sin caché porque es una acción explícita). Si un correo
coincide con varios usuarios, gana el de menor `id` numérico. No toca vínculos existentes. Responde
`{ "linked": <n>, "settings": SiigoSettingsDto }`. Usa las credenciales de configuración (`ResolveForSetupAsync`), como el
resto del asistente.

### 4.4 Lista `users` de `…/options/users`

Se queda (el paso la usa para el selector), sin cambios de forma.

## 5. Facturar

- `ExternalInvoicingOrder` y `SiigoInvoiceInput` cambian `AdvisorEmail` por `AdvisorMembershipId` (Guid) más
  `AdvisorLabel` (`DisplayName ?? Email`, null si no resuelve), para el texto del faltante.
- `SiigoInvoiceBuilder.Build(input, settings, overrides)` vuelve a su firma original: el vendedor sale de
  `settings.SellerFor(input.AdvisorMembershipId)`.
- Sin vínculo → `SiigoMissing("seller_unlinked", <membershipId>, <AdvisorLabel>)` (reemplaza a `seller_not_found`).
  Se suma a los demás faltantes.
- **La vista previa y la emisión ya no llaman a `/v1/users`.** Se quitan `SiigoSellerResolver`,
  `SiigoInvoiceSupport.ResolveSellerAsync` y `SetupCredentialsAsync` (si nadie más la usa), y el parámetro `refresh` de
  `ListUsersAsync` (lo usa sólo `auto-link`, que lee sin caché: se conserva el parámetro para él). La vista previa vuelve a
  «sin escribir nada y sin llamar a Siigo»; la emisión vuelve a armar **antes** de pedir credenciales (un faltante es 422
  antes que un 409 de conexión).
- Un vínculo a un usuario que se desactivó en Siigo no se detecta antes de enviar: Siigo rechaza la factura
  (`invalid_seller` u otro código) y la fila queda `Rejected` con su causa, como cualquier rechazo. El paso «Vendedores» lo
  muestra (el usuario no está en la lista activa).

## 6. Despliegue y transición

- Backend primero. Migración aditiva.
- **Al desplegar, ningún asesor está vinculado:** las facturas quedan bloqueadas con `seller_unlinked` hasta que el tenant
  haga el paso. «Vincular por correo» reproduce en un clic lo que hacía el cruce actual.
- La rama `chore/drop-siigo-seller-id` (borra `siigo.settings.seller_id`, ya sin uso) es independiente y puede salir en el
  mismo despliegue.

## 7. Pruebas

- Unitarias: dominio (`ReplaceSellerLinks` repetidos, `SellerFor`), readiness (`seller_link_missing` sin vínculos, completo
  con uno), builder (`seller` del vínculo; `seller_unlinked` con referencia y etiqueta; junto a otros faltantes),
  auto-link (correo con mayúsculas y espacios, inactivo no cuenta, duplicado → menor id, vinculado no se toca).
- Integración (filtrada): `GET settings` lista activos más el vínculo de un miembro que salió; `PUT …/sellers` guarda y
  responde 412/428 como los demás pasos; 422 por membresía ajena; auto-link con un usuario coincidente y uno no; preview
  con asesor sin vínculo → `seller_unlinked` y **ninguna** llamada a `/v1/users`; emisión con vínculo → `"seller": <id>`.
- `SiigoDbContextMappingTests.TheModelHasNoChangesPendingAMigration` sigue en verde.

## 8. Fuera de alcance

- Número de identificación en la membresía.
- Detectar en la vista previa un usuario de Siigo desactivado (exigiría volver a leer `/v1/users` al facturar).
