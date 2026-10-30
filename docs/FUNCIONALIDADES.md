# BIRVENN API: todo lo que hace

Estado al 30/10/2026. Complementa al [README](../README.md) (arranque y configuración) y a [INTEGRACION-FRONT.md](INTEGRACION-FRONT.md) (cómo conectar el front). Para el equipo del front hay una guía aparte: [GUIA-FRONT.md](GUIA-FRONT.md).

## 1. Resumen

| Área | Qué hace |
| --- | --- |
| Cuentas | Registro, login, logout, perfil, cupón de bienvenida, historial de actividad, roles Customer y Admin |
| Catálogo | Productos, categorías, búsqueda, filtros, colores, stock por talle |
| Descuentos | Cupones (porcentaje, monto fijo, envío gratis), cupón personal de bienvenida, descuento por transferencia, envío gratis por monto |
| Checkout y pedidos | Cotización, creación idempotente, reserva atómica de stock, vencimiento a las 48 h, historial, pedidos de invitado |
| Pagos | Mercado Pago Checkout Pro (tarjeta y Mercado Pago), transferencia bancaria, webhook firmado, confirmación al volver del checkout |
| Panel interno (API) | Ver pedidos, confirmar transferencias, avanzar estados con código de seguimiento, ajustar stock |
| Soporte | Formulario de contacto y newsletter |

Tecnología: .NET 10, ASP.NET Core con controllers, Entity Framework Core. Base de datos: **Postgres** (Neon en la nube) o **SQLite** (desarrollo local). Sesión por **JWT en cookie httpOnly**.

Dónde corre:

| Entorno | URL | Base | Pagos |
| --- | --- | --- | --- |
| Local (`dotnet run`) | `http://localhost:5180` | SQLite (`birvenn.dev.db`, no se sube a git) | Mercado Pago en modo prueba si el `.env` lo indica, o Mock |
| Render (prueba) | `https://backend-birvenn.onrender.com` | Postgres en Neon | Mercado Pago en modo prueba |

## 2. Cuentas y usuarios

- **Crear usuario:** `POST /api/auth/register`. El email se guarda en minúsculas y es único: si ya existe responde 409 `email_taken`. Al registrarse se crea el **cupón de bienvenida** (15 %, 30 días) y se inicia sesión.
- **Dónde se guardan:** en la tabla `Users` de la base de datos (Neon en Render, el archivo SQLite en local). Guarda email, nombre, apellido, teléfono y cumpleaños opcionales, preferencia de newsletter, rol, contador de intentos fallidos y fecha de bloqueo. Los cupones del usuario van en `Coupons` y su historial en `Activity` (máximo 50 entradas).
- **Contraseña:** nunca se guarda en claro. Se guarda solo el **hash PBKDF2 con sal aleatoria** que genera el `PasswordHasher` de ASP.NET Core Identity. Si cambian los parámetros de hashing, la contraseña se re-hashea sola en el próximo login correcto.
- **Login:** `POST /api/auth/login`. El mensaje de error es siempre el mismo ("Email o contraseña incorrectos"), exista o no el email, y para un email inexistente se hace un hash de relleno para que tarde lo mismo (no se puede averiguar qué emails existen). **5 intentos fallidos bloquean la cuenta 15 minutos** (429 `locked_out`).
- **Sesión:** cookie `birvenn_session` httpOnly (el JavaScript de la página no puede leerla). Dura 12 horas, o 30 días con "recordarme". También se acepta `Authorization: Bearer <jwt>`.
- **Roles:** `Customer` (por defecto) y `Admin`. El admin se crea al arrancar con `Admin__Email` y `Admin__Password`, **solo si no existe**: cambiar la variable después no cambia la clave de un admin ya creado.
- **Perfil:** `PATCH` o `PUT /api/account/profile` (nombre, apellido, teléfono, cumpleaños).

## 3. Catálogo y stock

- `GET /api/products` con filtros `category`, `sizes`, `colors`, `tags` (separados por coma), `priceMax`, `q`, `sort`, `limit` y `offset`. Responde `{ items, total, priceCeiling }`.
- `GET /api/products/{id o slug}`, `GET /api/products/search?q=` (búsqueda del header, sin tildes) y `GET /api/categories`.
- Cada producto trae colores, talles y `stock` por talle (por ejemplo `{ "M": 16 }`).
- El stock se **reserva** al crear el pedido con un UPDATE atómico: dos compras simultáneas de la última unidad no pueden vender de más. Si el pedido vence o se cancela, el stock vuelve.
- Los precios y el stock salen siempre de la base: lo que mande el carrito del front se ignora.

## 4. Descuentos y cupones

| Descuento | Cómo funciona |
| --- | --- |
| Cupón de bienvenida | Personal (de un usuario), 15 % por 30 días, **un solo uso**. Se verifica en la base que exista, sea de esa cuenta, no esté usado ni vencido |
| `STREET10` | Público, 10 % con compra mínima de $ 60.000 |
| `ENVIOFREE` | Público, envío gratis con compra mínima de $ 80.000 |
| Transferencia | 10 % extra automático al elegir `transferencia` (`Commerce__TransferDiscount`) |
| Envío estándar gratis | Automático desde $ 150.000 de subtotal (`Commerce__FreeShippingThreshold`) |

Endpoints relacionados: `POST /api/coupons/validate` (`{ code, subtotal }`), `GET /api/account/coupons` (los del usuario) y el campo `couponCode` de `POST /api/checkout/quote` y `POST /api/checkout`. Los tipos de cupón son `percent`, `fixed` y `free-shipping`.

**Todavía no existe** un endpoint para crear, editar o desactivar cupones: hoy los públicos vienen del seed (`Data/SeedData.cs`) y los de bienvenida se crean solos al registrarse.

## 5. Checkout y pedidos

1. `POST /api/checkout/quote` calcula subtotal, descuentos, envío y total **sin crear nada**.
2. `POST /api/checkout` crea el pedido, reserva el stock e inicia el pago. Se puede comprar como **invitado** o con sesión. Es **idempotente**: reenviar el mismo `clientOrderId` no duplica el pedido.
3. El pedido tiene un id público `ord_...` (128 bits aleatorios) y un número `BRV-XXXXXX` para mostrar.
4. Estados: `pendiente`, `pagado`, `preparando`, `enviado`, `entregado`, `cancelado`.
5. Un pedido sin pagar **vence a las 48 h**: se cancela, se devuelve el stock y se libera el cupón. Si el pago llega tarde, el pedido se recupera si todavía hay stock; si no, queda marcado para reembolso manual.
6. `GET /api/orders` (con sesión) lista los pedidos del usuario; `GET /api/orders/{id}` muestra uno por su id público.

## 6. Pagos

Medios disponibles (`GET /api/config`): `tarjeta` (hasta 3 cuotas, las cuotas sin interés se activan en la cuenta de Mercado Pago), `mercadopago` (dinero en cuenta, QR, tarjetas guardadas) y `transferencia`.

**Mercado Pago Checkout Pro:** el cliente paga en la página de Mercado Pago, así que los datos de tarjeta nunca pasan por esta API. La preferencia lleva **un solo ítem con el total ya calculado**, para que lo cobrado coincida al centavo con lo calculado.

Flujo: `checkout` devuelve `payment.redirectUrl` → el cliente paga en Mercado Pago → vuelve a `/checkout/exito?orden=...` → el front llama a `reconcile` → Mercado Pago además avisa por **webhook**. Ambos caminos aplican el pago al pedido de forma idempotente.

Reglas de seguridad del cobro: el cuerpo del webhook nunca se toma como verdad (se **consulta el pago** a la API de Mercado Pago), la firma `x-signature` se verifica con HMAC, un pago aprobado por un monto distinto al del pedido **no** lo marca como pagado, y reembolsos y contracargos cancelan el pedido.

**Transferencia:** el pedido queda `pendiente` con las instrucciones; cuando llega el dinero, el admin lo confirma con `confirm-payment`.

### Endpoints de pago

| Endpoint | Para qué | Quién lo llama |
| --- | --- | --- |
| `POST /api/checkout` | Crea el pedido e inicia el pago (devuelve el link de Mercado Pago) | Front |
| `POST /api/checkout/quote` | Calcula totales, no cobra | Front |
| `POST /api/orders/{id}/reconcile` | Confirma el pago contra Mercado Pago al volver del checkout | Front |
| `POST /api/payments/webhook` | Notificación de pago, con firma | Mercado Pago |
| `POST /api/admin/orders/{id}/confirm-payment` | Marca pagada una transferencia | Admin |
| `POST /api/dev/payments/simulate` | **Solo pruebas**: fuerza un estado de pago | Desarrollo |

Hay **3 endpoints específicos de pago** (`reconcile`, `webhook`, `simulate`) y otros 3 que forman parte del flujo de cobro (`checkout`, `quote`, `confirm-payment`), más el de cupones.

### Pagos de prueba

- **Simulador local:** con `Payments__Provider=Mock` y `Payments__AllowSimulation=true`, `POST /api/dev/payments/simulate` con `{ orderId, status }` aplica el estado a un pedido. Estados: `approved`, `pending`, `in_process`, `rejected`, `cancelled`, `refunded`, `charged_back`. Devuelve 404 en producción y con Mercado Pago real.
- **Mercado Pago en modo prueba:** con las credenciales de prueba de la aplicación `BIRVENN-test` y un comprador de prueba. Tarjetas: titular `APRO` aprueba, `OTHE` rechaza, `CONT` queda pendiente. No se mueve dinero real.
- **Panel de Webhooks de Mercado Pago:** el botón de simular notificación manda un pago ficticio; la API responde 200 y lo ignora.

## 7. Panel interno (API, requiere rol Admin)

`GET /api/admin/orders` (filtros `status`, `q`, `page`, `pageSize`), `PATCH /api/admin/orders/{id}/status` (pagado, preparando, enviado, entregado o cancelado; acepta `trackingCode`), `POST /api/admin/orders/{id}/confirm-payment` y `PUT /api/admin/products/{id}/stock` (`{ size, quantity }`). **No hay pantalla**: se usa Swagger en local (`/swagger`) o el archivo `BirvennApi.http`.

## 8. Soporte

`POST /api/contact` (queda guardado para que lo responda el equipo) y `POST /api/newsletter` (idempotente: no revela si el email ya estaba suscripto).

## 9. Seguridad

- Precios, stock y cupones se deciden solo en el servidor.
- Contraseñas con PBKDF2, bloqueo por intentos y mensajes que no revelan qué emails existen.
- Cookie httpOnly y verificación del header `Origin` en pedidos que modifican datos (protección CSRF). CORS por lista de orígenes.
- Límite de pedidos por IP y por minuto: login y registro 10, checkout 15, endpoints públicos 60.
- Webhook con firma comparada en tiempo constante.
- Secretos solo por variables de entorno; los errores internos nunca llegan al cliente.
- Swagger y el simulador de pagos están cerrados en producción. Se verificó desde afuera sobre el servicio de Render: `/swagger` y el simulador dan 404, el panel admin sin sesión da 401 y el webhook sin firma da 401.

## 10. Costos de ponerlo en producción

| Pieza | Para probar | Para vender |
| --- | --- | --- |
| API en Render | Plan Free: US$ 0, se duerme a los 15 minutos y tarda cerca de un minuto en despertar | Plan Starter: desde US$ 7 al mes (según el panel de Render al 29/10) |
| Base en Neon | Plan Free (1 GB) | El plan Free alcanza para empezar; se revisa según el uso |
| Front en Vercel | Plan Hobby: gratis, pero **solo para uso personal no comercial** | **Plan Pro: US$ 20 por usuario al mes** (incluye US$ 20 de crédito de uso). Una tienda que vende es uso comercial |
| Mercado Pago | Credenciales de prueba, sin costo | Comisión por cada venta, según la cuenta |

Precios de Vercel según su documentación a 30/10/2026; conviene confirmarlos antes de contratar.

## 11. Los 26 endpoints

| # | Método y ruta | Acceso |
| --- | --- | --- |
| 1 | `POST /api/auth/register` | Público |
| 2 | `POST /api/auth/login` | Público |
| 3 | `POST /api/auth/logout` | Público |
| 4 | `GET /api/auth/me` | Sesión |
| 5 | `PATCH /api/account/profile` | Sesión |
| 6 | `PUT /api/account/profile` | Sesión |
| 7 | `GET /api/account/coupons` | Sesión |
| 8 | `GET /api/products` | Público |
| 9 | `GET /api/products/search` | Público |
| 10 | `GET /api/products/{id o slug}` | Público |
| 11 | `GET /api/categories` | Público |
| 12 | `GET /api/config` | Público |
| 13 | `POST /api/coupons/validate` | Público |
| 14 | `POST /api/checkout/quote` | Público |
| 15 | `POST /api/checkout` | Público (invitado o sesión) |
| 16 | `GET /api/orders` | Sesión |
| 17 | `GET /api/orders/{id}` | Público (con el id del pedido) |
| 18 | `POST /api/orders/{id}/reconcile` | Público (con el id del pedido) |
| 19 | `POST /api/payments/webhook` | Mercado Pago (firmado) |
| 20 | `POST /api/dev/payments/simulate` | Solo desarrollo con Mock |
| 21 | `POST /api/contact` | Público |
| 22 | `POST /api/newsletter` | Público |
| 23 | `GET /api/admin/orders` | Admin |
| 24 | `PATCH /api/admin/orders/{id}/status` | Admin |
| 25 | `POST /api/admin/orders/{id}/confirm-payment` | Admin |
| 26 | `PUT /api/admin/products/{id}/stock` | Admin |

Además: `GET /health` (responde 200 solo si la base responde) y `GET /` (nombre y estado).

## 12. Lo que todavía no está

- Emails transaccionales (confirmación de compra, datos bancarios, envío) y **verificación de email**.
- Crear, editar y desactivar cupones, y administrar productos (alta, baja, precios) desde la API. Hoy solo se ajusta el stock.
- Reembolsos desde la API (hoy llegan por el webhook si se hacen en Mercado Pago).
- Favoritos en el servidor (hoy `localStorage` en el front).
- Pantalla del panel admin.
- Pruebas automatizadas.
- Asociar pedidos de invitado a la cuenta por email (apagado a propósito hasta tener verificación de email).
- Producción real: credenciales productivas de la cuenta del titular del negocio, webhook en modo productivo, un plan de Render que no se duerma y un rol de base con menos privilegios que el dueño.
