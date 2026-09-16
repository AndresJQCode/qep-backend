# Comprobantes de pago v2: temporal, procesar y mover al bucket público — diseño

**Fecha:** 2026-09-16 (diseño acordado con el owner el 2026-09-15 y el 2026-09-16)
**Rama:** `feature/comprobantes-publicos-v2` (backend), más un cambio de una línea en `qep-frontend`.
**Spec anterior:** [2026-09-15-comprobantes-publicos-design.md](2026-09-15-comprobantes-publicos-design.md). Esta versión cambia **dónde vive** el comprobante; el Excel, la columna `public_storage_key` y el puerto `IPaymentProofPublisher` de v1 se conservan.

## Contexto

v1 está desplegado desde el 2026-09-15: al adjuntar un comprobante a un pedido, se **copia** del bucket privado (`qep-private`) al público (`qep`) con una clave aleatoria, y el original sigue en el privado. El owner no quiere el comprobante duplicado: quiere que se suba a una ruta temporal, que al terminar bien el proceso las imágenes bajen de calidad y pasen a WebP, y que el archivo se **mueva** al bucket público.

Lo que ya existe y este diseño reusa:

- **La ruta temporal.** Toda subida va con URL firmada a `staging/tenants/{tenant}/{id}`; `CompleteUpload` valida tamaño, tipo y antivirus y recién ahí promueve a `files/` y borra el temporal (`CompleteUpload.cs:31-131`).
- **El procesado de imágenes.** `ImageSharpVariantGenerator` (SixLabors.ImageSharp 3.1.12) ya genera WebP con tope de píxeles de entrada y salida.
- **El barrido de abandonados.** `StagingCleanupWorker` borra lo que quedó en `PendingUpload` con más de `StagingRetentionHours` (24 h), cada `StagingCleanupMinutes` (60 min).
- **Las sondas de referencia entre módulos.** `IUserReferenceProbe` (BuildingBlocks) y `OrphanUserCleanupWorker`: cada módulo dice si todavía referencia algo, sin que el worker conozca a los demás.
- **El outbox transaccional** con inbox idempotente por consumidor, que ya usan `SessionRevocationWorker` y `OrphanUserCleanupWorker`.

Datos verificados que condicionan el diseño:

- `FileOwnerType` tiene `User = 1`, `Entity = 2`, `System = 3`, `Product = 4` (`FileResourceEnums.cs:19-28`); `Product` se agregó en CAT-05 para distinguir archivos de catálogo.
- El endpoint de subida rechaza un `ownerType` desconocido con `storage.file.owner_type_invalid` (`StorageEndpoints.cs:84-98`).
- En `qep-frontend`, `uploadQuoteFile` sube con `ownerType: 'User'` y sólo lo usan los comprobantes: `use-convert-quote-to-order.ts` y `use-add-order-payment-proofs.ts`.
- `OrderPaymentProofResolver` acepta `application/pdf`, `image/jpeg` e `image/png`; `FileUploadPolicy` ya acepta `.webp`.
- `IssueDownloadUrlHandler` firma con `Content-Disposition: attachment` sobre `resource.StorageKey` (`IssueDownloadUrl.cs:43-52`).

## Decisiones

| # | Decisión | Por qué |
| --- | --- | --- |
| D1 | Los PDF se mueven tal cual; sólo las imágenes se recomprimen y pasan a WebP. | Un PDF no se convierte a WebP ni se le baja la calidad. |
| D2 | Aplica sólo a comprobantes de pago, con `FileOwnerType.PaymentProof = 5`. | Storage es genérico; el resto de los archivos sigue como hoy. Mismo patrón que `Product` en CAT-05. |
| D3 | El comprobante se mueve al público **al adjuntarlo** al pedido. | Nada llega al público sin un pedido que lo justifique. |
| D4 | Entre la subida y el adjunto, el archivo espera en `staging/` y **nunca se promueve** a `files/`. | Es la ruta temporal que pidió el owner. |
| D5 | El endpoint de descarga de Storage devuelve la URL pública cuando el comprobante ya se movió. | Storage es dueño de dónde está el archivo; el frontend y el contrato de la API no cambian. |
| D6 | La limpieza corre como workers dentro de la API, no como API más `CronJob`. | Sigue el patrón del repo, no necesita credencial de máquina ni abre un endpoint destructivo sin tenant. |
| D7 | Imágenes: lado mayor ≤ 2000 px, WebP calidad 80, sin agrandar. | El texto de un comprobante sigue legible y el archivo pesa una fracción del original. |
| D8 | La imagen se procesa **al completar la subida**; al adjuntar sólo se mueve. | El error de una imagen corrupta aparece al subirla, la conversión sigue rápida y el rollback no deshace un procesamiento. |
| D9 | Al adjuntar se **copia** antes de guardar el pedido; el temporal se borra **después**, por un evento de outbox que consume Storage. | No hay transacción entre las bases de Quotations y Storage; así el comprobante nunca deja de existir en algún lado. |
| D10 | `FileResource` gana un método propio para registrar el movimiento de un comprobante; `Publish` no se toca. | `Publish` sólo acepta imágenes y protege el endpoint de publicación de Storage. |
| D11 | `StagingCleanupWorker` también borra comprobantes `Available`, sin mover, con más de 24 h y **sin referencia** de ningún módulo. | Con D4 quedan `Available` en `staging/`, y el barrido actual sólo mira `PendingUpload`. |
| D12 | Un worker de reconciliación recorre sólo `payment-proofs/` del bucket público, salta lo de menos de 24 h, corre una vez al día y arranca en modo solo-registrar. | Borra objetos cuya URL puede estar en un Excel ya enviado: primero se verifica en los logs. |
| D13 | Los comprobantes `User` (los de v1 y los de la transición) no cambian. Sin migración ni backfill. | Siguen funcionando con el original privado y su copia pública. |
| D14 | Se despliega primero el backend y después el frontend. | El backend nuevo funciona igual con el frontend viejo; al revés, el endpoint rechazaría `PaymentProof`. |

Los nombres de tipos, métodos, eventos y claves de configuración de abajo son **nombres propuestos**; el plan los fija.

## Sección 1: subida y procesamiento

**Cómo se reconoce un comprobante.** Se agrega `FileOwnerType.PaymentProof = 5`. En `qep-frontend`, `uploadQuoteFile` manda `ownerType: 'PaymentProof'`.

**Qué hace `CompleteUpload` con un `PaymentProof`.** Las validaciones de hoy no cambian: tamaño, que el contenido coincida con el tipo declarado y antivirus. Si el veredicto es limpio:

| Tipo | Qué pasa |
| --- | --- |
| `image/jpeg`, `image/png`, `image/webp` | Se redimensiona a lado mayor ≤ 2000 px, sin agrandar, y se codifica WebP calidad 80 con ImageSharp, en un puerto nuevo de Storage (`IPaymentProofImageProcessor`). El resultado **reemplaza** al objeto en `staging/`, y el `FileResource` actualiza tipo (`image/webp`), extensión del nombre (`.webp`), tamaño y checksum. |
| `application/pdf` | Queda tal cual. |

En los dos casos: sin promoción a `files/`, sin miniatura, y el recurso pasa a `Available` con `MarkClean`, que ya existe y no cambia la clave.

**Si la imagen no se puede procesar**, cuarentena y `storage.file.rejected` / `image_processing_failed`, el mismo camino que usa hoy la miniatura (`CompleteUpload.cs:79-91`).

**En Quotations**, `OrderPaymentProofResolver` acepta además `image/webp`.

## Sección 2: el movimiento al adjuntar

En `ConvertQuotationToOrderHandler` y `AddOrderPaymentProofsHandler`, sin cambiar su forma actual:

1. **Antes del dominio, copia al público.** `PaymentProofCopies` y `PublicPaymentProofPublisher` ya copian desde `resource.StorageKey`; para un `PaymentProof` esa clave es la de `staging/`. Sólo se suma `image/webp → .webp` a la tabla de extensiones. El temporal **no** se borra acá.
2. **Se guarda el pedido** con la clave en `OrderPaymentProof.PublicStorageKey`, como hoy, y en la **misma transacción** se escribe en el outbox `quotations.order.payment-proofs-attached.v1` con `tenantId`, `orderId` y, por cada comprobante nuevo, `fileId` y `publicStorageKey`.
3. **Si algo falla antes de guardar**, el rollback de v1 borra las copias públicas; el original sigue en `staging/`.
4. **Un worker de Storage** (`PaymentProofMoveWorker`) consume el evento con su inbox. Para cada `fileId` de un `FileResource` `PaymentProof` todavía sin mover, en este orden:
   1. **borra el objeto de `staging/`**, que ya tiene su copia pública. Borrar una clave que ya no existe no falla, así que el paso es repetible;
   2. **en un solo `SaveChanges`**, llama a `FileResource.MoveToPublic(publicStorageKey, occurredAt)` y marca el inbox.

   Si el borrado falla, no se guarda nada y el mensaje vuelve en el tick siguiente. Si falla el guardado, el tick siguiente repite el borrado (sin efecto) y guarda. El orden inverso —guardar y después borrar— dejaría un objeto huérfano en `staging/` para siempre si el borrado fallara: el inbox ya estaría marcado, y el barrido de la sección 3 sólo mira comprobantes **sin** mover.

`MoveToPublic` sólo es válido para `OwnerType == PaymentProof` y `Status == Available`, acepta cualquier tipo del comprobante (PDF incluido), registra `PublicStorageKey` y `PublishedAt`, y es idempotente con la misma clave. Los comprobantes `User` se ignoran.

**La ventana.** Entre que se guarda el pedido y que el worker procesa (segundos), el archivo existe en los dos buckets y la descarga desde la app usa el temporal. Entre el borrado del temporal y el guardado del paso 4 (milisegundos), una descarga desde la app fallaría y funcionaría al reintentar; el Excel no se ve afectado, porque enlaza la copia pública.

## Sección 3: descarga desde la app y comprobantes que nadie adjuntó

**Descarga.** En `IssueDownloadUrlHandler`, si el recurso es `PaymentProof`, tiene `PublicStorageKey` y no se pidió variante, devuelve `IPublicObjectStorage.GetUrl(PublicStorageKey)`. Todo lo demás firma como hoy. La auditoría `storage.file.downloaded` se registra igual.

- **Cambio de comportamiento aceptado:** un comprobante ya movido se **abre** en una pestaña en vez de descargarse con su nombre original, porque un objeto público no lleva `Content-Disposition` por request. Es lo mismo que hace el enlace «Ver» del Excel.

**Comprobantes que nadie adjuntó.** `StagingCleanupWorker`, con el mismo reloj y `StagingRetentionHours`, suma una segunda búsqueda: `PaymentProof`, `Available`, sin `PublicStorageKey` y con `CreatedAt` anterior al corte. Para cada uno:

- consulta `IFileReferenceProbe` (BuildingBlocks, mismo diseño que `IUserReferenceProbe`; Quotations la implementa: ¿algún `OrderPaymentProof` tiene ese `FileId`?);
- si está referenciado, lo deja: es un comprobante adjunto cuyo evento de la sección 2 todavía no se procesó;
- si no, borra el objeto de `staging/`, marca el recurso como eliminado y audita `storage.file.purged` con motivo `payment_proof_not_attached`.

**Carrera aceptada.** Adjuntar un comprobante subido hace más de 24 h mientras el barrido lo borra hace fallar la copia: el request devuelve error, no se guarda nada y la asesora lo vuelve a subir. Sin pérdida de datos.

## Sección 4: reconciliación, convivencia y despliegue

**Reconciliación de objetos huérfanos.** `PaymentProofOrphanCleanupWorker`, en Storage:

- recorre **sólo** el prefijo `payment-proofs/` del bucket público, paginando; nunca `tenants/.../media`, `quotations/` ni el bucket privado. `IPublicObjectStorage` gana un listado por prefijo con la fecha de modificación de cada objeto;
- salta los objetos con menos de `Storage:PaymentProofOrphanCleanup:MinimumAgeHours` (24), porque al adjuntar se copia antes de guardar el pedido;
- un objeto está referenciado si lo tiene algún `FileResource.PublicStorageKey` (Storage) o algún `OrderPaymentProof.PublicStorageKey` (Quotations, por `IPublicObjectReferenceProbe`), lo que cubre v1 y v2;
- si nadie lo referencia: con `Storage:PaymentProofOrphanCleanup:DryRun = true` sólo registra en el log la clave que borraría; con `false` la borra y audita `storage.public_object.purged`;
- corre cada `Storage:PaymentProofOrphanCleanup:IntervalHours` (24).

`DryRun` va en `true` en `appsettings.json` y en `k8s/prod-configMap.yaml`. Se apaga a mano después de revisar en los logs de producción que lo que marcaría como huérfano realmente lo es.

**Convivencia con v1.** Los comprobantes `User` conservan su original privado, su copia pública y su clave en `OrderPaymentProof`: el Excel los sigue enlazando, la app los sigue descargando del privado, el barrido de la sección 3 no los mira y la reconciliación los encuentra referenciados.

**Orden de despliegue.** Primero el backend (acepta `PaymentProof`, mueve, limpia en solo-registrar); después el frontend (manda `PaymentProof`). Mientras quede abierta una pestaña con el frontend viejo, sus comprobantes siguen el camino de v1.

## Riesgos y contras aceptados

- **Se descarta el original de las imágenes.** Queda el WebP de ≤ 2000 px y calidad 80 como única evidencia del pago (D7).
- **Nada se revoca.** Un comprobante movido es público para quien tenga la URL, igual que en v1.
- **El nombre original se pierde** al abrir un comprobante movido desde la app (sección 3).
- **`DryRun` se apaga a mano.** Mientras esté en `true`, los huérfanos se acumulan en el bucket público.

## Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias:**

- el procesador reduce a lado mayor ≤ 2000 px, no agranda, codifica WebP calidad 80 y rechaza una imagen corrupta;
- `FileResource.MoveToPublic` sólo acepta `PaymentProof` `Available`, acepta PDF y es idempotente;
- `OrderPaymentProofResolver` acepta `image/webp`;
- la tabla de extensiones del publicador suma `.webp`;
- `IssueDownloadUrlHandler` devuelve la URL pública sólo para un `PaymentProof` movido.

**De integración:**

- un `PaymentProof` de imagen queda WebP, `Available`, en `staging/` y sin variantes; un PDF queda intacto;
- convertir y sumar comprobantes copian al público y escriben el evento en el outbox; el worker completa el movimiento y borra el temporal;
- una falla antes de guardar borra la copia pública y deja el temporal;
- el barrido borra el comprobante no adjuntado con más de 24 h y respeta el adjuntado;
- la reconciliación, con `DryRun = false`, borra el huérfano viejo, respeta el referenciado y el reciente, y no lista otros prefijos; con `DryRun = true` no borra nada;
- los comprobantes `User` siguen el camino de v1;
- toda factoría de integración fija `Storage:PaymentProofOrphanCleanup:DryRun` explícitamente.

**Frontend (`qep-frontend`):** `uploadQuoteFile` manda `ownerType: 'PaymentProof'` (Vitest).

## Enmiendas (2026-09-16, después de escribir el plan)

Al escribir el plan aparecieron conflictos entre el código y este spec. Se resuelven así:

| # | Decisión | Por qué |
| --- | --- | --- |
| D15 | `SoftDeleteFileHandler` y `UnpublishFileHandler` rechazan un `PaymentProof` que algún pedido referencia (`IFileReferenceProbe`) con `storage.file.invalid_state`. `PublishFileHandler` rechaza siempre un `PaymentProof`, con el mismo código: un comprobante sólo llega al público por el movimiento de la sección 2. | Decidido por el owner. Hoy los dos primeros borran lo que haya en `PublicStorageKey` (`SoftDeleteFile.cs:35-44`, `SetFilePublication.cs:98-111`), y para un comprobante movido eso es la copia que enlaza el Excel: la evidencia del pago. `PublishFileHandler` copiaría desde un temporal que ya no existe. |
| D16 | Un comprobante ya movido no se puede adjuntar a otro pedido. `QuotationFileLookup` lo informa como no disponible (`IsAvailable = false` si es `PaymentProof` y ya tiene `PublicStorageKey`), y `OrderPaymentProofResolver` lo rechaza con el código que ya usa, `order.payment_proof.file_not_available`. | Sin esto, la copia al público sale de un temporal borrado y el request termina en 500. No se inventa un código: el archivo ya no está disponible para adjuntar. |
| D17 | Una migración de Quotations agrega índices sobre `order_payment_proofs.file_id` y `order_payment_proofs.public_storage_key`. | Las dos sondas nuevas consultan esa tabla en cada barrido. |
| D18 | `FileUserReferenceProbe` cuenta también los archivos `PaymentProof` de un usuario, no sólo los `User`. | El frontend sube los comprobantes con `ownerId` = el usuario. Sin esto, pasarlos a `PaymentProof` dejaría de retener a quien los subió, y `OrphanUserCleanupWorker` podría borrar un usuario que todavía es dueño de comprobantes. |

Dos consecuencias que se aceptan y quedan documentadas:

- Con `Quotations:PaymentProofs:PublicLinks=false`, un `PaymentProof` adjunto no tiene clave pública, no genera evento y se queda en `staging/`. Se sigue descargando desde la app, y el barrido de la sección 3 lo respeta porque está referenciado. Producción tiene la opción encendida.
- Storage no tiene inbox: la sección 2 necesita una tabla de inbox nueva y su migración, con el mismo diseño que los inbox de los demás módulos.

## Fuera de alcance

- Migrar los comprobantes v1 (`User`) al esquema v2.
- Cambiar el flujo de imágenes de producto u otros archivos de Storage.
- Una API o `CronJob` para disparar la limpieza, y el botón en el panel de operadores.
- Despublicar un comprobante o revocar su URL.
- Configuración por tenant.
