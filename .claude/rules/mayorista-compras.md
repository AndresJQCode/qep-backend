---
paths:
  - "src/Modules/Wholesale/**"
  - "tests/Modules/Wholesale/**"
  - "src/Modules/Purchases/**"
  - "tests/Modules/Purchases/**"
  - "src/Modules/Suppliers/**"
  - "tests/Modules/Suppliers/**"
---

# Mayorista, Compras y Proveedores

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **La aceptación de una invitación de mayorista no es atómica entre módulos** (plan mayorista fase 2, D-3). Es idempotente por
  el tenant reservado (`wholesale.distributor_invitations.reserved_tenant_id`), el proveedor (`LinkedTenantId` o documento) y el
  cliente (documento); la invitación se consume en el último commit, el de `wholesale`. No la cambies a «consumir primero»: un
  fallo a mitad dejaría la invitación gastada y la relación a medias, sin link para reintentar. Si otra pestaña crea el tenant
  reservado mientras ésta valida el slug, el `slug.taken` es el propio y la aceptación termina sobre él
  (`DistributorInvitationService`, Review Focus 1).
- **`Supplier.LinkedTenantId` lo escribe sólo `ISupplierLinking`** (D-8). El formulario de proveedores no lo acepta (un
  `linkedTenantId` en el cuerpo se ignora) y un proveedor vinculado no cambia de documento: 422
  `suppliers.supplier.linked_identity_locked`.
- **El link de la invitación de mayorista sale de `Notifications:AppBaseUrl`** (`AccountLinks.WholesaleInvitation`, ruling W1),
  no de una variable propia: no hay `WholesaleInvitationUrl` que dar de alta en el pipeline. El token viaja sólo en el payload
  del outbox; `SensitivePaths.Mask` lo tapa en `platform.request_failures` y en su línea de log, también el de la invitación de
  miembros, y `SensitivePathTelemetry` (registrado en `Program.cs` después de `AddQepLogging`) con la misma regla en el
  `url.path` de los spans de OpenTelemetry y en el scope `RequestPath` de cada log del request. Una ruta nueva con un secreto en
  un segmento se suma ahí.
- **El link de la invitación por sí solo no prueba el buzón, y el correo no tiene que coincidir** (spec 2026-11-19; el link del
  correo sí puede probarlo, abajo). El link identifica la invitación; la acepta cualquier cuenta, una sola vez. Si el correo
  de la cuenta no es el del distribuidor, lo reemplaza en el commit final, con auditoría
  (`wholesale.distributor.email_changed`, sin los correos) y aviso a los dos correos por outbox. `email_mismatch` se retiró.
  No lo vuelvas a exigir: el owner decidió que prevalece el correo de quien acepta.
- **Sin contraseña hace falta el código o la prueba del link del correo** (foco 1 del spec 2026-11-19; spec 2026-11-24). En
  `session-with-password`, una cuenta que no existe o existe sin contraseña necesita un código válido de `email-code`
  (`IEmailChallenges`) **o** el `emailProof` del link del correo con el correo invitado, y siempre la versión de los términos
  antes de escribir nada; sólo quien ya tiene contraseña entra con ella, y a esa cuenta la prueba no la toca. La prueba es un
  secreto aparte del token que viaja sólo en `?ev=` del correo (campo `emailProof` de `wholesale.distributor-invited.v1`); en
  base queda su hash (`distributor_invitations.email_proof_hash`). La decide Wholesale (`DistributorEmailProofs.Proves`) y
  `WholesaleInvitationPasswordSession` sólo recibe el resultado. **Tres anulaciones:** «Copiar» (rota el token y la deja nula),
  «Reenviar» (pone una nueva) y **cambiar el correo** del distribuidor (la deja nula: sin esto, quien tiene el correo viejo
  crearía la cuenta del nuevo). No la pongas en el link de «Copiar», en el de WhatsApp, en una respuesta de la API, en un log
  ni en la auditoría. **Riesgo aceptado por el spec:** quien reenvía el correo de invitación entrega ese paso (el destinatario
  puede crear la cuenta del correo invitado mientras la invitación siga pendiente y la cuenta no tenga contraseña); lo acotan
  las 72 h, el uso único y la rotación con «Copiar» o «Reenviar». Para todo lo demás el código es la única llave, así que
  `IEmailChallenges` sostiene tres invariantes que no se aflojan (revisión final de la rama, hallazgo 1): **a lo sumo un
  desafío vivo por correo** (la emisión corre bajo
  `pg_advisory_xact_lock(EmailChallengeIssuanceLock.Namespace, hashtext(email_hash))` e invalida el anterior en la misma
  transacción), **sólo cuenta el más reciente** (`TryConsumeAsync` actualiza ese y ningún otro; el código de uno anterior es
  un código errado) y **5 desafíos por correo en 24 h móviles** (`MaxIssuesPerDay`; el sexto no se emite y no falla, igual que
  dentro de los 60 s, para no revelar si el correo tiene cuenta). Con varios desafíos vivos, cada intento probaba todos sus
  códigos a la vez. Sin esto, el link más el correo de otra persona bastarían para fijarle una contraseña a su cuenta. Y
  `ITermsAcceptances.RecordAsync` se confirma en su propia conexión con FK a `identity.users`: se llama **después** de que el
  usuario quedó guardado, nunca con un usuario de una unidad de trabajo sin guardar (`WholesaleInvitationPasswordSession`).
- **«Copiar» rota el token** (spec 2026-11-19 §1). La base guarda el hash, así que el link sólo existe en la respuesta de invitar
  y en la de `POST .../invitation-link`, que rota (vigencia nueva, el link anterior pasa a 404). `DistributorDetailDto` no imprime
  `InvitationUrl` en su `ToString` (`PrintMembers`): no lo pongas en un log.
- **El token viaja en el evento del WhatsApp con link** (`wholesale.distributor-invitation-link.v1`), sólo en el payload del
  outbox. **Pero el mensaje de WhatsApp todavía sale sin el link**: falta un ítem aparte de Mensajería (enmascarar el token en
  el mensaje guardado, una clase de plantilla con `{{link}}` y su aprobación en Meta). Hasta entonces el texto no lleva el link;
  no lo metas en el cuerpo de un mensaje guardado.
- **El cliente del mayorista nace en el paso 7 con el correo de la cuenta**, antes del commit final que reemplaza el del
  distribuidor (`DistributorInvitationService`). Es idempotente por documento: en un reintento, un cliente que ya existe con ese
  documento se reutiliza y **no** se actualiza (tampoco su correo).
- **`NewTenant` sí lee nombre y slug del cuerpo** (`WholesaleInvitationEndpoints.cs:323-327`; la regla anterior decía lo
  contrario y el código manda): el nombre es el `displayName` del cuerpo y, si no viene, la razón social del distribuidor; el slug
  es el del cuerpo y, si no viene, el que sugiere `ITenantSlugSuggestions` (spec 2026-11-19 §3b). El país **no** sale del cuerpo:
  es el del distribuidor. Si una carrera se lo gana (`tenancy.slug.taken`), el endpoint reintenta hasta 3 veces,
  cada vez en un scope nuevo: el DbContext de Tenancy del intento fallido se queda con el tenant reservado sin guardar.
- **La empresa del distribuidor nace en el accept** (spec 2026-11-23; owner, 2026-11-24). Si `created`, siempre: también en un
  reintento D-17 con cuerpo `LinkTenant`, porque el tenant reservado ya existe y es el destino aunque el cuerpo pida otro. Vincular
  un tenant que ya existía (`created = false`) la crea **sólo si ese tenant no tiene ninguna empresa**, activa o inactiva
  (`ICompanyProvisioning.EnsureFirstCompanyAsync`, que mira bajo el mismo candado: dos aceptaciones a la vez no crean dos). Es idempotente por tipo y número de documento **exactos** (no `ILIKE`; reutiliza
  también una inactiva) bajo `CompanyProvisioningLock`, y va entre el tenant y el proveedor: un fallo deja la invitación
  pendiente y el reintento termina sobre el tenant reservado. Una invitación sin la ciudad que su país exige (`CityId` en `CO`,
  `CityName` afuera: las filas de antes de la migración) acepta **sin empresa** y sin error, y deja un `Information` con el id del
  distribuidor. Lo que Companies rechaza (`ValidationException`, `CompaniesDomainException`) sale tal cual; no se traga.
  Al invitar, la ciudad colombiana se valida contra Geography (`wholesale.distributor.city_not_found`) justamente para que esto
  no pase.
- **El cliente del mayorista nace con el celular, la ciudad, la dirección y el contacto del distribuidor** (decisión del owner,
  2026-11-24): el puerto `EnsureCustomerAsync` lleva `phone`, `cityId`/`cityName`, `address` y `contactName`. Con teléfono (Customers
  lo exige, `CustomerWriteRules`) y ciudad, el adaptador crea la **ficha completa** por `ICustomerProvisioning.CreateCompleteAsync`
  (CUC, domicilio, ciudad, origen `wholesale`, actor = quien invitó). `Customer` no tiene persona de contacto: el nombre del contacto
  va como `CompleteCustomerData.AddressLabel`, la etiqueta (`Name`) de la dirección principal de la libreta. Esa fila sólo existe con
  ciudad colombiana y calle (`ToFirstAddress`): un distribuidor de afuera o sin dirección no guarda el contacto, y está bien. **Sin
  teléfono, o si Customers rechaza la completa** (`ValidationException`, ciudad inexistente, política del tenant), el adaptador cae a
  `CreateFromRegisterAsync` (ficha incompleta: nombre, documento, teléfono, correo, país) y la aceptación no se rompe; antes de
  caer limpia el `ChangeTracker` para no dejar entidades `Added`. Un cliente que ya existía por documento se reutiliza y no se
  actualiza.
- **Los datos del distribuidor se editan sólo mientras está invitado** (decisión del owner, 2026-11-24): razón social, país,
  tipo y número de documento, contacto, correo, ciudad y dirección, por el mismo `PUT` (`Distributor.ChangeProfile`). En el
  cuerpo son opcionales y **ausente = conserva** (un formulario que no los conoce sigue funcionando; `phone` sigue al revés:
  ausente borra). Las reglas son las de invitar (`DistributorProfileValidator`, compartido: una regla nueva de un dato va ahí),
  más ciudad existente y documento libre entre los demás distribuidores. La ciudad sólo se exige a una invitación vieja sin ella
  cuando se manda la ciudad o cambia el país. Afuera de `Invited`, un valor distinto es
  `wholesale.distributor.invalid_transition`. **Cambiar el correo no reenvía la invitación ni rota el token**: lo hace
  «Reenviar»; no lo automatices. La auditoría nombra los campos cambiados, nunca los valores.
- **Con una aceptación a medias no se cambia el documento** (`UpdateDistributorHandler`): la empresa del distribuidor y el
  cliente del mayorista son idempotentes **por documento**. Si el distribuidor está `Invited`, el `PUT` cambia el tipo o el número
  (normalizados) y hay marca `acceptance_started_at` **o** el tenant reservado ya existe (`ITenantDirectory.GetStatusAsync`, como
  D-17; cubre las aceptaciones a medias anteriores a la columna), responde 409 `wholesale.distributor.acceptance_in_progress` y
  no escribe. Los demás campos se siguen editando y sin cambio de documento no se consulta Tenancy.
- **La aceptación confirma su marca antes de la primera escritura en otro módulo** (`Distributor.StartAcceptance`,
  `DistributorInvitationService`). Reglas que no se aflojan: va **después** de todo lo que rechaza sin escribir (un accept
  rechazado no bloquea el documento) y **antes** del tenant, los módulos, la empresa, el proveedor y el cliente: una validación
  nueva va en `PlanTargetAsync` o antes, y una escritura nueva en el `WriteAsync` del plan o después de la marca. La marca **sube
  `Version`**: un `PUT` que cargó antes pierde con 412, y si el que confirmó primero fue el `PUT`, el guardado de la marca pierde,
  la aceptación suelta lo rastreado y **vuelve a empezar desde la lectura** (hasta 3 veces, después 412). No la cambies a un
  `UPDATE` suelto que no mire la versión: la aceptación seguiría con datos viejos. No consume la invitación, no cambia el estado,
  no se repite en un reintento y **no se borra**.
  **Límites conocidos:** una aceptación que falla después de la marca deja el documento bloqueado aunque no haya creado nada con
  él (Companies rechaza los datos, una carrera por el slug que escribió el distribuidor), y para corregirlo hay que borrar la
  invitación e invitar de nuevo; borrar e invitar con otro documento después de una aceptación a medias sí puede duplicar; y no se
  detectan las vinculaciones de un tenant existente que quedaron a medias antes de la migración y no se han reintentado.
- **El aviso de WhatsApp de la invitación es un evento aparte, y desde el spec 2026-11-19 lleva el token**: invitar y
  «Reenviar» escriben `wholesale.distributor-invitation-link.v1` (token sólo en el payload del outbox). El viejo
  `wholesale.distributor-invitation-notice.v1`, sin token, ya no se escribe: el procesador sólo lo drena. El mensaje de WhatsApp
  sigue saliendo sin el link (ver arriba). Su consumidor vive en Mayorista con inbox propio (`InvitationNoticeProcessor`, consumidor `wholesale.invitation-whatsapp-notice`)
  y manda por `WholesaleInvitationNoticeSender` de Bootstrapper. El resultado se aplica sólo a la `generation` vigente y no sube
  `version` (D-3, D-4); la `generation` es token de concurrencia, así que un resultado leído antes de un `PUT` o un «Reenviar» no
  se escribe.
- **El `clientId` del aviso es el id del mensaje del outbox**: no lo cambies por uno nuevo, o un reintento manda dos avisos
  (D-5). Por lo mismo, una fila `Failed` con `RetryDisabled` es terminal en `PlainTemplateOutbound` (`not_retriable`): repetir el
  `clientId` no la reenvía.
- **«Por aprobar» de un pedido `Wholesale` es `WholesaleApprovedAt`, no un estado** (mayorista fase 3, D-1). `OrderStatus` sigue
  `Open/Archived/Cancelled`; pendiente es `Open` sin `wholesale_approved_at`. Aprobado deja de ser editable (`IsEditable`, D-3):
  la aprobación ya le llevó las líneas al distribuidor y una edición posterior nunca le llegaría. «Lo anuló el comprador» tampoco
  se guarda: se deriva de `cancelled_by` nulo (`Order.CancelledByBuyer`).
- **La orden vinculada guarda costo sin IVA y congela la lista** (D-9). `UnitCost` es el precio de lista del mayorista sin IVA
  (`PurchaseOrderMath.NetOfVat`); `list_base_price`, `list_discount_percent` y `list_unit_price` (con IVA) quedan como foto para
  pintar «base y descuento». Los totales de la orden y del pedido pueden diferir en centavos. La moneda del pedido tiene que ser
  la de la orden: si el mayorista cambió la suya, el envío es 409 `wholesaler_rejected` y una aprobación en otra moneda se
  registra y no se aplica.
- **Los eventos de vuelta del mayorista los consume Purchases con inbox propio, y la creación de productos va en su
  transacción** (`WholesaleOrderEventsProcessor`, consumidor `purchases.wholesale-order-events`). `PurchasesCatalogWriter` enrola
  el `CatalogDbContext` en la conexión y la transacción de `PurchasesDbContext` (calca `ShopifyCatalogWriter`) y abre un punto de
  guardado por producto: en Postgres una sentencia fallida aborta la transacción entera, así que sin el punto de guardado un
  alta fallida dejaría la confirmación sin guardar y el evento reintentando para siempre (ruling X2). Un evento que encuentra la
  orden todavía en borrador (`RetryLater`) no se marca: vuelve cuando vence su reclamo.
- **Un producto creado por Compras no tiene precio de venta (`AllowsNoPrice`) ni origen externo** (D-16, D-17). Nace sin
  `ExternalSource` para que el distribuidor lo edite como cualquier otro, sin precio, y con costo sólo si es mayor que 0. No sale
  en tienda, POS ni cotizaciones hasta que alguien le ponga precio; guardar el producto desde el formulario exige precio.
- **La foto del producto creado por Compras se copia después del commit, nunca dentro de él** (spec 2026-11-21). El evento de
  aprobación no lleva imagen: `WholesaleOrderEventApplier` anota un `PendingProductImage` por producto **creado** (no por uno hallado
  por código) con el `SupplierVariantId` de la línea y el `SupplierTenantId` de la orden, y el procesador llama
  `CopyPendingImagesAsync` ya confirmado. `PurchasesProductImages` (Bootstrapper) busca la foto en el catálogo del **mayorista**, baja
  los bytes y los sube como archivo propio del distribuidor: no guardes la URL del mayorista. No lo muevas a la transacción del
  inbox: el archivo de Storage no se deshace con ella y un reintento dejaría archivos `Available` sin dueño. Una falla de la foto
  se registra y el producto queda sin imagen; no frena la confirmación ni se reintenta. **El adaptador no tiene prueba automática**
  (necesita Catalog y Storage reales): se verifica a mano aprobando una orden con un producto del mayorista que tenga foto.
- **La lista de un distribuidor es una lista de Catalog** (spec 2026-11-22). `distributors.price_list_id` guarda el id de
  `catalog.price_lists`, sin FK (otro esquema), y Mayorista la lee por `IWholesalePriceLists` (`WholesalePriceLists` en
  Bootstrapper), que precia con el mismo `IPriceListPricing` de los canales. `WholesalePermissions.PriceListManage` es el literal
  `catalog.price_list.manage` repetido porque Mayorista no ve Catalog: la definición y la política son las de Catalog, y
  `ThePriceListPermissionIsCatalogsAndGoesToAdminOnly` los amarra. Las rutas de vista previa, categorías y productos piden además
  `wholesale.distributor.read`, que es lo que las cierra con el módulo apagado. `wholesale.price_lists` queda sin uso hasta su
  migración de contrato.
