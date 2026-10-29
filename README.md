# BIRVENN API

Backend de la tienda BIRVENN (streetwear). Reemplaza el "backend simulado" del front (`localStorage` + route handlers) por una API real: catálogo con stock, cuentas, cupones, pedidos y cobro con **Mercado Pago (Checkout Pro)**.

**Stack:** .NET 10 · ASP.NET Core (controllers) · EF Core 10 · PostgreSQL (SQLite para probar local) · JWT en cookie httpOnly · Swagger.

> El front es [`cor2ba/birven`](https://github.com/cor2ba/birven) (Next.js 16, repo privado). Cómo conectarlo: [`docs/INTEGRACION-FRONT.md`](docs/INTEGRACION-FRONT.md).

---

## Qué resuelve

| Antes (front simulado) | Ahora (API) |
| --- | --- |
| Usuarios y hash de contraseña en `localStorage` | Cuentas con PBKDF2 (ASP.NET Identity hasher), sesión en cookie httpOnly, bloqueo tras 5 intentos fallidos |
| Catálogo y stock en un `.ts` | Catálogo y stock en base de datos; el stock se **reserva** al crear el pedido (UPDATE atómico, no hay sobreventa) |
| `/api/payments` calculaba totales en el servidor de Next | `POST /api/checkout`: precios, cupón, envío y descuentos se calculan acá con las mismas reglas de `pricing.ts` |
| Cupón de bienvenida "TODO validar" | Cupón personal: dueño, vencimiento y uso único verificados en base |
| Webhook vacío | Webhook de Mercado Pago con **firma HMAC verificada**, consulta del pago a la API de MP y actualización idempotente |
| Pedidos en `localStorage` | Pedidos en base, historial por usuario, seguimiento y panel admin |

## Arranque rápido (sin instalar nada: SQLite)

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --urls http://localhost:5180
```

En `Development` se usa SQLite (`birvenn.dev.db`), pasarela **Mock** (aprueba tarjeta y Mercado Pago al instante), simulador de estados de pago y Swagger en `http://localhost:5180/swagger`. Se crea un admin de desarrollo (`admin@birvenn.local` / `DevAdmin123`, solo en `Development`). Hay requests de ejemplo en [`BirvennApi.http`](BirvennApi.http).

### Con Postgres (docker compose)

```bash
cp .env.example .env      # completar Jwt__Key (mínimo 32 caracteres)
docker compose up --build  # API en http://localhost:8080 y Postgres 16
```

> El `Dockerfile` y el `docker-compose.yml` siguen el estilo de tus otros backends, pero **no se pudieron probar** en la máquina donde se armó esto (no tiene Docker). Compilan con `dotnet publish`.

## Configuración

Se lee de `appsettings.json`, variables de entorno (`Seccion__Clave`) y `.env`. Referencia completa en [`.env.example`](.env.example).

| Variable | Para qué |
| --- | --- |
| `Database__Provider` | `Postgres` (default) o `Sqlite` |
| `ConnectionStrings__Default` | Acepta la URL `postgresql://...` de Supabase/Neon/Render o el formato Npgsql |
| `Jwt__Key` | **Obligatoria**, mínimo 32 caracteres |
| `Jwt__CookieSameSite` / `Jwt__CookieSecure` | `Lax` si front y API comparten sitio (o hay proxy); `None` + HTTPS si están en dominios distintos |
| `Urls__Frontend` | URL pública del front. Arma las `back_urls` de Mercado Pago |
| `Urls__PublicApi` | URL pública **https** de esta API. Mercado Pago llama a `…/api/payments/webhook` |
| `Cors__Origins` | Orígenes permitidos (coma) |
| `Payments__Provider` | `Mock` (solo desarrollo) o `MercadoPago` |
| `MercadoPago__AccessToken` / `__WebhookSecret` | Credenciales de la aplicación y clave secreta de Webhooks |
| `Commerce__*` | Umbral de envío gratis, descuento por transferencia, cuotas, vencimiento de pedidos, texto de transferencia |
| `Admin__Email` / `Admin__Password` | Crea (o promueve) el usuario admin al arrancar |
| `Network__TrustProxy` | `true` detrás de Render/Cloud Run/Nginx (IP real para el rate limit) |

La API **falla al arrancar** si falta `Jwt__Key`, si en `Production` se usa la pasarela `Mock`, o si `MercadoPago` no tiene token y clave de webhook.

## Contrato de la API

Las formas de `User`, `Coupon`, `Order`, `Product` y `Category` son las mismas que ya usa el front (`lib/types`), con unos pocos campos extra (`trackingCode`, `paidAt`, `expiresAt`, `role`). Los valores de enums son los del front: `pendiente`, `estandar`, `tarjeta`, `free-shipping`, etc.

**Errores:** siempre `application/problem+json` con `detail` (mensaje en español para mostrar) y `code` (identificador estable). Validación → `422`, no autenticado → `401`, sin permiso → `403`, conflicto (stock, email repetido) → `409`, límite de intentos → `429`, falla de la pasarela → `502`.

**Sesión:** `register`/`login` setean la cookie `birvenn_session` (httpOnly). También se acepta `Authorization: Bearer <jwt>`.

### Cuenta
| Método | Ruta | Notas |
| --- | --- | --- |
| POST | `/api/auth/register` | 201. Crea la cuenta, el cupón de bienvenida (15 %, 30 días) e inicia sesión |
| POST | `/api/auth/login` | `remember` = cookie persistente (30 días) o de sesión |
| POST | `/api/auth/logout` | 204 |
| GET | `/api/auth/me` | Usuario con cupones y actividad al día |
| PATCH | `/api/account/profile` | Datos personales |
| GET | `/api/account/coupons` | Cupones del usuario |

### Catálogo y reglas
| Método | Ruta | Notas |
| --- | --- | --- |
| GET | `/api/products` | `category`, `sizes`, `colors`, `tags` (coma), `priceMax`, `q`, `sort`, `limit`, `offset` |
| GET | `/api/products/{id o slug}` | |
| GET | `/api/products/search?q=` | Búsqueda del header, sin tildes |
| GET | `/api/categories` | |
| GET | `/api/config` | Envíos, medios de pago, umbral de envío gratis, provincias |

### Compra
| Método | Ruta | Notas |
| --- | --- | --- |
| POST | `/api/coupons/validate` | `{ code, subtotal }` → `{ valid, coupon?, reason? }` |
| POST | `/api/checkout/quote` | Totales calculados por el servidor (no crea nada) |
| POST | `/api/checkout` | Crea el pedido, reserva stock, inicia el pago. Invitado o con sesión. Idempotente con `clientOrderId` |
| GET | `/api/orders` | Historial del usuario (requiere sesión) |
| GET | `/api/orders/{id}` | Por id público. Para `/checkout/exito?orden=…` |
| POST | `/api/orders/{id}/reconcile` | Confirma el pago contra Mercado Pago al volver del checkout |
| POST | `/api/payments/webhook` | Lo llama Mercado Pago (firmado) |

### Soporte y panel interno
| Método | Ruta | Notas |
| --- | --- | --- |
| POST | `/api/contact`, `/api/newsletter` | 202 |
| GET | `/api/admin/orders` | Rol Admin. Filtros `status`, `q`, `page`, `pageSize` |
| PATCH | `/api/admin/orders/{id}/status` | pagado → preparando → enviado → entregado (o cancelado). Acepta `trackingCode` |
| POST | `/api/admin/orders/{id}/confirm-payment` | Marca pagada una transferencia recibida |
| PUT | `/api/admin/products/{id}/stock` | `{ size, quantity }` |

## Flujo de pago con Mercado Pago

```
Front                         API                              Mercado Pago
  | POST /api/checkout  ------->|  valida, calcula, reserva stock, crea Order(pendiente)
  |                             |  POST /checkout/preferences ---->|
  |<-- { payment.redirectUrl } -|<---------- init_point -----------|
  | redirige al cliente ------------------------------------------>|  el cliente paga
  |<-------- back_url /checkout/exito?orden={id}&payment_id=... ---|
  | POST /api/orders/{id}/reconcile {paymentId} -> consulta GET /v1/payments/{id} (no confía en el navegador)
  |                             |<--- POST /api/payments/webhook (x-signature) --- Mercado Pago
  |                             |  verifica firma -> GET /v1/payments/{id} -> aplica al pedido (idempotente)
```

- **Un solo ítem por pedido** (`BIRVENN — Pedido BRV-XXXXXX`) con el total ya calculado. Cupón, descuento por transferencia y envío no se pueden expresar como ítems de la preferencia, y así lo cobrado coincide al centavo con lo calculado. El detalle de prendas queda en `description` y en la base.
- Con **tarjeta** solo se ofrecen crédito/débito; con **Mercado Pago**, todos los medios. Se limita a `Commerce:Installments` cuotas (las **cuotas sin interés** se activan en tu cuenta de Mercado Pago, no por API).
- Un pago **rechazado** no cancela el pedido (Checkout Pro deja reintentar). Un pago **aprobado por un monto distinto** al del pedido **no** lo marca como pagado. Reembolsos y contracargos cancelan el pedido y quedan anotados.
- **Transferencia**: el pedido queda `pendiente` con el texto de `Commerce:TransferInstructions`; el equipo lo confirma con `confirm-payment`.
- Un pedido sin pagar **vence a las 48 h**: se cancela, se devuelve el stock y se libera el cupón. Si el pago llega tarde, el pedido se recupera si todavía hay stock; si no, queda marcado para reembolso manual (log `REEMBOLSO MANUAL`).

### Configurar Mercado Pago

Los nombres de los menús pueden cambiar; la guía oficial está en <https://www.mercadopago.com.ar/developers>.

1. Entrar a **Tus integraciones** → **Crear aplicación** → pagos online → **Checkout Pro**.
2. Copiar el **Access Token de prueba** (`TEST-…`) a `MercadoPago__AccessToken`. Crear en el panel un **vendedor y un comprador de prueba** y pagar con el comprador usando las tarjetas de prueba de la documentación (titular `APRO` aprueba, `OTHE` rechaza).
3. En **Webhooks** → configurar notificaciones: URL `https://TU-API/api/payments/webhook`, evento **Pagos**. Copiar la **clave secreta** a `MercadoPago__WebhookSecret`.
4. Para producción: usar las credenciales productivas (`APP_USR-…`) y repetir el paso 3 en modo productivo.

## Seguridad

Hecho: precios, stock y cupones solo del servidor · contraseñas con PBKDF2 y mensaje de error genérico (sin enumerar emails, con hash de relleno para igualar tiempos) · bloqueo de cuenta 15 min tras 5 fallos · rate limiting por IP (login, checkout, endpoints públicos) · cookie httpOnly + verificación de `Origin` en peticiones que modifican datos (CSRF) · CORS por lista · firma del webhook comparada en tiempo constante · el cuerpo del webhook nunca se toma como verdad: se consulta el pago · secretos solo por entorno · errores internos nunca llegan al cliente.

A tener en cuenta:

- **Pedidos de invitado:** el id público (`ord_…`, 128 bits aleatorios) funciona como llave para verlos. Quien tenga el link ve la dirección y el email de ese pedido.
- **`Commerce__ClaimGuestOrdersByEmail` (apagado):** el front asocia pedidos de invitado a la cuenta nueva por coincidencia de email. Sin verificar el email, cualquiera podría registrarse con el email de otra persona y leer su historial; por eso queda apagado hasta que exista verificación por mail.
- `appsettings.Development.json` trae una clave y un admin **solo de desarrollo**; no se cargan en `Production`.

## Probar gratis en la nube

1. **Base:** Neon (1 GB, sin tarjeta) o Supabase (500 MB, se pausa tras 1 semana sin uso). Copiar la URL de conexión (`postgresql://…`). En Supabase, si la conexión directa falla, usar la del *pooler* en modo sesión.
2. **API:** Render (plan free, se duerme a los 15 min) con el [`render.yaml`](render.yaml), o cualquier host que corra el `Dockerfile` (Cloud Run, Koyeb…). Completar las variables de entorno de [`.env.example`](.env.example).
3. Las migraciones se aplican solas al arrancar (`Database__AutoMigrate`).
4. Es para **probar**: el free de Render no es apto para producción y un servidor dormido tarda ~1 min en atender el primer webhook (Mercado Pago reintenta).

## Estructura

```
Controllers/   Auth, catálogo, checkout/pedidos, pagos (webhook), soporte, admin
Services/      Pricing (port de pricing.ts), Coupon, Auth, Catalog, Checkout, Order, OrderLifecycle (stock), worker de vencimiento
Payments/      IPaymentGateway + Mock + Mercado Pago (cliente HTTP, firma, procesador idempotente)
Data/          DbContext, migraciones (Postgres), seed (catálogo del front, cupones, admin)
Models/ Dtos/  Entidades y contratos JSON
Infrastructure Errores uniformes, CSRF, validación, helpers
```

Para sumar otra pasarela (Stripe, Ualá…): implementar `IPaymentGateway` y registrarla en `Program.cs`.

## Cómo se probó

Se compiló sin advertencias y se ejercitó la API en local (SQLite): unas 150 comprobaciones sobre catálogo, sesión, cupones, totales, checkout idempotente, compras simultáneas de las últimas unidades, vencimiento, panel admin y seguridad. Mercado Pago se probó contra **un simulador local de su API** (creación de preferencia, firma del webhook, monto distinto, reembolso, reintento, reconcile, falla de la pasarela).

**No** se probó contra Mercado Pago real (no hay credenciales en este entorno): antes de cobrar, hacé una compra completa con el Access Token de prueba y las cuentas de prueba, y revisá que la preferencia y el webhook se comporten como se espera.

## Pendientes sugeridos

Emails transaccionales (confirmación, datos bancarios, envío) · verificación de email · favoritos en el servidor (hoy `localStorage`) · pantalla de panel admin · reembolsos por API · ítems detallados en la preferencia · pruebas automatizadas.
