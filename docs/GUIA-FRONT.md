# Guía para el front: qué se puede agregar a la web de BIRVENN

Para quien arma el front (`cor2ba/birven`). Acá está **todo lo que la API ya permite hacer**, con el endpoint de cada cosa. La conexión base (proxy de Next, cliente HTTP, qué reemplaza a qué) está en [INTEGRACION-FRONT.md](INTEGRACION-FRONT.md); el detalle de cada regla, en [FUNCIONALIDADES.md](FUNCIONALIDADES.md).

## 0. Antes de empezar

| | |
| --- | --- |
| API local | `http://localhost:5180` (con `dotnet run` en `backend-birvenn`, Swagger en `/swagger`) |
| API en la nube (prueba) | `https://backend-birvenn.onrender.com` |
| Formato | JSON. Los errores son `application/problem+json` con `detail` (mensaje en español para mostrar) y `code` (identificador estable) |
| Sesión | Cookie `birvenn_session` httpOnly: no se lee desde JavaScript, el navegador la manda sola. Usá el proxy `rewrites` de Next (INTEGRACION-FRONT, sección 1) para que funcione entre Vercel y Render |
| Dormida | La API de prueba en Render se duerme tras 15 minutos sin uso: la primera llamada puede tardar cerca de un minuto. Mostrá un estado de carga |
| Mutaciones | Las llamadas que modifican datos deben mandar el header `Origin` del sitio (el navegador lo hace solo). Si falla, el error es `origin_not_allowed` |

En los ejemplos, `api<T>(ruta, { method, json })` es el cliente único de INTEGRACION-FRONT, sección 2.

## 1. Qué se puede construir

| Página o función | Qué ofrece la API | Endpoints |
| --- | --- | --- |
| Home y listados | Categorías y productos con filtros y orden | `GET /api/categories`, `GET /api/products` |
| Buscador del header | Búsqueda sin tildes | `GET /api/products/search?q=` |
| Ficha de producto | Colores, talles, **stock por talle**, descripción, materiales, cuidados | `GET /api/products/{id o slug}` |
| Carrito | Totales calculados por el servidor, aviso "te faltan $ X para envío gratis" | `POST /api/checkout/quote` |
| Cupones | Validar un código y mostrar el motivo si no sirve | `POST /api/coupons/validate` |
| Checkout | Tres medios de pago, envío, cupón, compra de invitado o con cuenta | `POST /api/checkout`, `GET /api/config` |
| Página de éxito | Confirmar el pago al volver de Mercado Pago | `POST /api/orders/{id}/reconcile`, `GET /api/orders/{id}` |
| Cuenta | Registro, login, perfil, cupón de bienvenida, mis cupones, mis pedidos | `/api/auth/*`, `/api/account/*`, `GET /api/orders` |
| Newsletter y contacto | Formularios que guardan el mensaje | `POST /api/newsletter`, `POST /api/contact` |
| Panel interno (opcional) | Pantalla para ver pedidos, confirmar transferencias, cargar el código de seguimiento y ajustar stock | `/api/admin/*` |

## 2. Catálogo

```ts
// Listado con filtros. Todos opcionales; los listados se separan con coma.
const { items, total, priceCeiling } = await api<ProductList>(
  "/api/products?category=buzos&sizes=M,L&colors=negro&priceMax=120000&sort=precio-asc&limit=12&offset=0",
);
```

- `priceCeiling` es el precio máximo del catálogo: sirve para el tope del slider de precio.
- Cada producto trae `colors` (`id`, `name`, `hex`), `sizes` y `stock` por talle, por ejemplo `{ "M": 16, "XXL": 0 }`. Deshabilitá los talles con stock 0.
- `GET /api/config` trae los métodos de envío (con precio y plazo), los medios de pago, las provincias y el umbral de envío gratis: no hace falta duplicar esas reglas en el front.

## 3. Carrito y descuentos

```ts
const quote = await api<QuoteResponse>("/api/checkout/quote", {
  method: "POST",
  json: { items, couponCode, shippingMethod: "estandar", paymentMethod: "transferencia" },
});
// quote.totals, quote.coupon, quote.couponError, quote.remainingForFreeShipping
```

Descuentos que ya existen: cupón de bienvenida personal (15 %, 30 días, un solo uso), `STREET10` (10 %, compra mínima $ 60.000), `ENVIOFREE` (envío gratis, mínimo $ 80.000), **10 % extra por transferencia** y envío estándar gratis desde $ 150.000. Al validar un cupón, `reason` trae el texto para mostrar ("El cupón está vencido", "Ya usaste este cupón"...).

El carrito puede seguir calculando en el cliente para que la UI sea instantánea, pero **el cobro siempre usa el cálculo del servidor**: los precios que mande el front se ignoran.

## 4. Checkout y pago

`POST /api/checkout` (ejemplo completo en INTEGRACION-FRONT, sección 4). Medios de pago:

| `paymentMethod` | Qué pasa |
| --- | --- |
| `tarjeta` | Redirige a Mercado Pago solo con crédito y débito, hasta 3 cuotas |
| `mercadopago` | Redirige a Mercado Pago con todos los medios (dinero en cuenta, QR, tarjetas guardadas) |
| `transferencia` | No hay redirección: `payment.message` trae las instrucciones y el pedido queda pendiente hasta que el equipo confirme la transferencia |

```ts
const { order, payment } = await api<CheckoutResponse>("/api/checkout", { method: "POST", json: body });
if (payment.redirectUrl) window.location.href = payment.redirectUrl;
else router.push(`/checkout/exito?orden=${order.id}`);
```

- `clientOrderId` hace el envío **idempotente**: si el cliente toca dos veces el botón, no se duplica el pedido.
- Si algo cambió (sin stock, cupón vencido, color o talle no disponible) la API responde con un `detail` listo para un toast.
- El stock se reserva al crear el pedido y un pedido sin pagar **vence a las 48 h**: mostrale al cliente cuánto tiempo tiene.
- **Página de éxito:** al volver, Mercado Pago agrega `payment_id` a la URL. Llamá a `reconcile` con ese id antes de mostrar el estado, para no depender de que el webhook haya llegado. Para transferencias o un pedido sin `payment_id`, usá `GET /api/orders/{id}`.

## 5. Cuenta de usuario

| Pantalla | Endpoint | Notas |
| --- | --- | --- |
| Registro | `POST /api/auth/register` | Crea la cuenta, inicia sesión y entrega el cupón de bienvenida. 409 `email_taken` si el email ya existe |
| Ingreso | `POST /api/auth/login` | `remember: true` mantiene la sesión 30 días. 429 `locked_out` tras 5 intentos fallidos |
| Salir | `POST /api/auth/logout` | |
| Sesión actual | `GET /api/auth/me` | Llamalo al cargar la app: 401 significa que no hay sesión. Trae cupones y actividad |
| Editar perfil | `PATCH /api/account/profile` | Nombre, apellido, teléfono y cumpleaños |
| Mis cupones | `GET /api/account/coupons` | |
| Mis pedidos | `GET /api/orders` | Incluye estado y código de seguimiento |

Las contraseñas las guarda la API con hash (el front nunca las ve ni las guarda). **No persistas el usuario en `localStorage`**: la fuente de verdad es `GET /api/auth/me`.

Estados de un pedido: `pendiente`, `pagado`, `preparando`, `enviado`, `entregado`, `cancelado`. Cuando está `enviado`, `trackingCode` trae el código de seguimiento.

## 6. Newsletter y contacto

```ts
await api("/api/newsletter", { method: "POST", json: { email } });
await api("/api/contact", { method: "POST", json: { name, email, topic, orderNumber, message } });
```

Los dos responden 202. El de newsletter no revela si el email ya estaba suscripto.

## 7. Panel interno (pantalla opcional)

Se puede armar una pantalla solo para el equipo, con login normal de una cuenta Admin:

- Lista de pedidos con filtros: `GET /api/admin/orders?status=pendiente&q=&page=1&pageSize=20`.
- Confirmar una transferencia recibida: `POST /api/admin/orders/{id}/confirm-payment`.
- Avanzar el pedido y cargar el código de seguimiento: `PATCH /api/admin/orders/{id}/status` con `{ status, trackingCode }`.
- Ajustar stock por talle: `PUT /api/admin/products/{id}/stock` con `{ size, quantity }`.

Hoy esto se hace con Swagger o el archivo `BirvennApi.http`; la pantalla no existe todavía.

## 8. Códigos de error

`email_taken`, `invalid_credentials`, `locked_out`, `rate_limited`, `coupon_invalid`, `out_of_stock`, `product_unavailable`, `color_unavailable`, `size_unavailable`, `quantity_exceeded`, `gateway_error`, `validation_failed` y `origin_not_allowed`. Mostrá siempre `detail`; usá `code` solo para decidir qué hacer (por ejemplo, llevar al login si es `session_invalid`).

Códigos HTTP: 422 validación, 401 sin sesión, 403 sin permiso, 409 conflicto (stock, email repetido), 429 demasiados intentos, 502 falla de la pasarela de pago.

## 9. Cómo probar sin cobrar

- **En local** (`dotnet run` con la pasarela Mock): el pago se aprueba al instante, y `POST /api/dev/payments/simulate` con `{ orderId, status }` fuerza otros estados (`rejected`, `refunded`, `pending`...).
- **Con Mercado Pago de prueba** (también en la API de Render): pagá con un **comprador de prueba** (se crea en el panel de Mercado Pago, en Cuentas de prueba) y las tarjetas de prueba: titular `APRO` aprueba, `OTHE` rechaza, `CONT` queda pendiente. No se mueve dinero real.

## 10. Qué NO tiene todavía la API (no lo asumas)

- **Favoritos en el servidor:** siguen en `localStorage`.
- **Emails** de confirmación, datos bancarios o envío, y **verificación de email**.
- **Pedidos de invitado asociados a la cuenta** por email: está apagado a propósito (sin verificar el email, cualquiera podría leer pedidos ajenos). El invitado ve su pedido con el link de éxito.
- **Reseñas, listas de deseos compartidas, recomendaciones y stock "avisame cuando vuelva":** no hay endpoints.
- **Alta, edición y baja de productos o cupones desde la API.**
- **Reembolsos** desde la API.

Si el front necesita algo de esta lista, pedilo y se agrega al back: lo más probable es que sea un endpoint nuevo.

## 11. Costo de publicar el front en Vercel

El plan **Hobby es gratis pero solo para uso personal no comercial** (según los términos de Vercel). Una tienda que cobra es uso comercial, así que para vender corresponde el **plan Pro: US$ 20 por usuario al mes**. Para probar y desarrollar, Hobby alcanza. Confirmá el precio vigente en `vercel.com/pricing` antes de contratar.
