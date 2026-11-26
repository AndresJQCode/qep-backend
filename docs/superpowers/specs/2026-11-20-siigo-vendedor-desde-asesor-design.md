# Siigo: el vendedor de la factura es el asesor del pedido

Fecha: 2026-11-20. Repo: `qep-backend`. Par de frontend:
`qep-frontend/docs/superpowers/specs/2026-11-20-siigo-vendedor-desde-asesor-frontend-design.md`.

## 1. Problema

Hoy el vendedor de toda factura de Siigo es uno solo por conexión: `SiigoSettings.SellerId`, elegido en el paso
Documento del asistente. `SiigoInvoiceBuilder.cs` manda `body["seller"] = settings.SellerId` sin mirar el pedido, así que
en Siigo todas las ventas quedan a nombre de la misma persona (y las comisiones y reportes por vendedor de Siigo no sirven).

## 2. Decisión (owner, 2026-11-20)

- El vendedor de cada factura es **el asesor del pedido** (`Order.AdvisorId`).
- **No se configura** en el asistente de la conexión: `SellerId` desaparece de settings.
- El cruce es por **correo**: el email del asesor (Identity, vía `IQuotationAdvisorLookup`) contra el `email` de los
  usuarios de Siigo (`GET /v1/users`).
- Si no hay cruce, **la factura se bloquea** con el error «El asesor no existe como usuario en Siigo». No hay vendedor
  por defecto: volvería a meter la configuración que se quita y le cargaría la venta a otra persona.

## 3. Respuesta de Siigo (verificada con un tenant real, 2026-11-20)

```json
{
  "pagination": { "page": 1, "page_size": 25, "total_results": 16 },
  "results": [
    { "id": 800, "username": "asesora10@…", "first_name": "VANESA", "last_name": "HERNANDEZ",
      "email": "asesora10@…", "active": true, "identification": "1007689178" }
  ]
}
```

`page_size` por defecto es 25 y la respuesta está paginada.

## 4. Diseño

### 4.1 Contrato de Quotations

`ExternalInvoicingOrder` agrega `string? AdvisorEmail` (al final del record). `ExternalOrderInvoicingService` lo resuelve
con `IQuotationAdvisorLookup.FindAsync(tenantId, [order.AdvisorId])` → `QuotationAdvisor.Email`. Null si la membresía o el
usuario ya no resuelven.

`SiigoInvoiceInput` agrega `string? AdvisorEmail`; `Bootstrapper/SiigoOrderSource.cs` lo copia.

### 4.2 Usuarios de Siigo con correo

- `ISiigoApi.ListUsersAsync` devuelve `IReadOnlyList<SiigoUser>`, con
  `record SiigoUser(string Id, string Name, string? Email)`. Sólo usuarios con `active: true` (como hoy).
- Se sigue cacheando con `ListCacheTtl` (5 min) por conexión, como las demás listas.

### 4.3 Paginación de las listas (bug existente)

`SiigoApiClient.ReadOptionsAsync` lee sólo la primera página. Con más de 25 usuarios, bodegas, centros de costo, formas
de pago o impuestos, el resto no aparece. Se corrige para todas las listas: la primera petición va **tal cual, sin
parámetros de página** (los endpoints que devuelven un arreglo pelado no reciben nada nuevo). Sólo si la respuesta trae
`pagination` y faltan resultados se piden `page=2..n` con el `page_size` que Siigo informó, hasta juntar `total_results` o
recibir una página vacía. Tope: 100 páginas. (Revisión final, 2026-11-20: mandar `page`/`page_size` a endpoints no
verificados arriesgaba un 400 que tumbaría el asistente.)

### 4.4 Resolución del vendedor

Función pura en Application, `SiigoSellerResolver.Resolve(string? advisorEmail, IReadOnlyList<SiigoUser> users) → string?`:

- Compara `Trim()` + `OrdinalIgnoreCase`.
- Advisor email null o vacío → null.
- Más de un usuario activo con el mismo correo → el de **menor `id` numérico** (determinista; Siigo no debería permitirlo).

### 4.5 Builder y faltante

- `SiigoInvoiceBuilder.Build(input, settings, overrides)` recibe además `string? sellerId`.
- `sellerId` null → `SiigoMissing("seller_not_found", null, input.AdvisorEmail)`. Se suma a los demás faltantes
  (no corta antes), así la pantalla muestra todo junto. Nueva constante `SiigoMissing.SellerNotFound`.
- `Label` es el correo del asesor: es un miembro del tenant, no un dato del cliente. Se actualiza el comentario de
  `SiigoMissing` ("nunca lleva datos del cliente" sigue siendo cierto).
- `body["seller"] = IdValue(sellerId)`.

### 4.6 Handlers

- **Usuarios cacheados:** la lista se cachea 5 minutos por réplica. Si el asesor no aparece, se relee **una vez** sin caché
  (`SiigoInvoiceSupport.ResolveSellerAsync`): el arreglo de un `seller_not_found` es crear el usuario en Siigo, y la caché
  no puede seguir negándolo después.
- **Preview** (`PreviewSiigoInvoiceHandler`): pasa a leer credenciales **de configuración**
  (`SiigoInvoiceSupport.SetupCredentialsAsync`, que acepta una conexión pausada, como las listas del asistente) y la lista
  de usuarios antes de armar. Cambia el principio de la spec 2026-11-18 §6.3 «sin llamar a Siigo»: ahora es
  «sin escribir nada y sin llamar a Siigo salvo la lista de usuarios, cacheada». Se actualiza el `<summary>`. Si Siigo no
  responde, el preview responde el mismo 503 `Unavailable` que ya devuelven las listas.
- **Issue** (`IssueSiigoInvoiceHandler`): hoy arma (`:159`) antes de pedir credenciales (`:167`). Se invierte: credenciales,
  usuarios, resolver, armar. Con `seller_not_found` responde igual que cualquier otro faltante (el 422 que ya mapea
  `NotReady`). El camino de reanudación (`:136`) no cambia: reenvía el `RequestJson` ya armado, con su vendedor.

### 4.7 Lo que se quita

- `SiigoSettings.SellerId`, `SiigoSettingsDto.SellerId`, `UpdateSiigoDocument.SellerId` (comando, validador,
  `OverridePropertyName("sellerId")`), el campo del request en `SiigoEndpoints.cs` y su paso a `SiigoSettingsSupport`.
- `SiigoSettingsReadiness.SellerMissing` (`seller_missing`). Una conexión cuyo único faltante era el vendedor queda lista.
- La columna `siigo.settings.seller_id` (la única tabla que la tiene) **no se borra en este release**: las migraciones
  corren al arrancar, y borrarla rompería a las réplicas viejas durante un despliegue gradual y a un rollback de imagen.
  Queda mapeada como propiedad sombra de EF, sin lectura ni escritura, y la borra una migración en un release posterior.
  El vendedor usado en cada factura queda en el `RequestJson` de su fila.
- La lista `users` de `ListSiigoOptions` **se queda**: deja de tener consumidor en el asistente, pero es barata y útil para
  diagnosticar. (Si el plan confirma que nadie la usa, se puede quitar en otro cambio.)

### 4.8 Compatibilidad del request

El `PUT` del paso Documento con `sellerId` en el cuerpo (frontend viejo durante el despliegue) se ignora: System.Text.Json
no falla por propiedades desconocidas. Orden de despliegue: **backend primero**; el frontend viejo seguiría mostrando el
selector «Vendedor», que deja de tener efecto, y el readiness ya no pide `seller_missing`.

## 5. Pruebas

- Unitarias: `SiigoSellerResolver` (coincide, mayúsculas/espacios, sin correo, sin coincidencia, duplicado → menor id);
  builder (`seller_not_found` junto a otros faltantes; `seller` en el cuerpo); readiness sin `seller_missing`;
  paginación del cliente con un `HttpMessageHandler` falso (2 páginas, `total_results` 30; sin `pagination`).
- Integración (filtrada por nombre): preview con asesor sin usuario en Siigo → `missing` con `seller_not_found` y el correo;
  issue con asesor sin usuario → 422 sin crear filas; issue con asesor válido → el cuerpo enviado lleva su `id`.
- `ArchitectureTests`: Siigo sigue sin referenciar Quotations.

## 6. Fuera de alcance

- Mapear miembros a usuarios de Siigo a mano (tabla de equivalencias). Si el owner lo pide después, el resolver es el
  único punto a cambiar.
- Cruce por identificación: los miembros de QEP no tienen documento.
