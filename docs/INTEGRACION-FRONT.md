# Conectar el front (`cor2ba/birven`) con esta API

El front hoy simula el backend con `localStorage` (`lib/services/*`) y dos route handlers (`app/api/payments`). La idea es **mantener la interfaz de los servicios** y cambiar su implementación por llamadas HTTP, como ya indica el README del front. Los tipos de `lib/types` no cambian: la API devuelve esas mismas formas.

## 1. Proxy en Next (recomendado: cookies de primer nivel, sin CORS)

Si el navegador le habla a `/api/*` del propio front y Next lo reenvía a la API, la cookie de sesión es del mismo sitio (funciona en Safari y con bloqueo de cookies de terceros) y no hace falta `SameSite=None`.

```ts
// next.config.ts
const nextConfig: NextConfig = {
  // ...lo que ya tenés
  async rewrites() {
    return [{ source: "/api/:path*", destination: `${process.env.API_URL}/api/:path*` }];
  },
};
```

```bash
# .env.local
API_URL=http://localhost:5180
```

- **Borrá** `src/app/api/payments/route.ts` y `src/app/api/payments/webhook/route.ts`: los archivos del front tienen prioridad sobre un rewrite, y el webhook de Mercado Pago ahora lo recibe la API.
- En la API: `Urls__PublicApi` = URL **pública de la API** (no la del front), porque Mercado Pago le llama directo a `…/api/payments/webhook`. `Urls__Frontend` = URL del front. Con el proxy alcanza `Jwt__CookieSameSite=Lax`.
- Sin proxy (front y API en dominios distintos): `Jwt__CookieSameSite=None`, `Jwt__CookieSecure=true`, `Cors__Origins=<front>` y `credentials: "include"` en cada `fetch`. Es más frágil por el bloqueo de cookies de terceros.

## 2. Cliente HTTP único

```ts
// lib/api/client.ts
export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) { super(message); }
}

export async function api<T>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const { json, headers, ...rest } = init;
  const res = await fetch(path, {
    credentials: "include",
    ...rest,
    headers: { ...(json !== undefined ? { "Content-Type": "application/json" } : {}), ...headers },
    body: json !== undefined ? JSON.stringify(json) : rest.body,
  });
  if (res.status === 204) return undefined as T;
  const data = await res.json().catch(() => ({}));
  // Errores: application/problem+json con `detail` (mensaje en español) y `code` (estable)
  if (!res.ok) throw new ApiError(res.status, data.code ?? "error", data.detail ?? "Algo salió mal. Probá de nuevo.");
  return data as T;
}
```

## 3. Qué reemplaza a qué

| Front hoy | Llamada nueva |
| --- | --- |
| `authService.register(values)` | `POST /api/auth/register` → `{ user, isNew }` (201) |
| `authService.login(email, password)` | `POST /api/auth/login` `{ email, password, remember }` → `{ user, isNew:false }` |
| `authService.logout` | `POST /api/auth/logout` |
| `authService.getUser` / `auth-store.refresh` | `GET /api/auth/me` |
| `authService.updateProfile` | `PATCH /api/account/profile` |
| `authService.logActivity`, `markCouponUsed` | **Se borran**: la API registra la actividad y consume el cupón sola |
| `orderService.claimGuestOrders` | **Se borra** (ver "Pedidos de invitado") |
| `orderService.getByUser` / `useUserOrders` | `GET /api/orders` |
| `orderService.getById` (página de éxito) | `GET /api/orders/{id}` + `POST /api/orders/{id}/reconcile` |
| `orderService.save` | **Se borra**: el pedido lo crea `POST /api/checkout` |
| `validateCoupon` (cliente) | `POST /api/coupons/validate` `{ code, subtotal }` |
| `calculateTotals` para cobrar | `POST /api/checkout/quote` (la UI puede seguir calculando local para el carrito; el cobro siempre usa el del servidor) |
| `submitCheckout` → `/api/payments` | `POST /api/checkout` |
| `NewsletterForm` / `ContactForm` (TODO) | `POST /api/newsletter`, `POST /api/contact` |
| `PRODUCTS`, `CATEGORIES`, `searchProducts` | `GET /api/products`, `/api/categories`, `/api/products/search` (opcional) |
| `commerceConfig`, `SHIPPING_METHODS`, `PROVINCES` | `GET /api/config` (opcional, evita duplicar reglas) |

El store de auth deja de persistir el usuario en `localStorage`: al cargar la app llama a `GET /api/auth/me` (401 → no hay sesión).

## 4. Checkout

El body de `POST /api/checkout` junta lo que antes eran `checkoutSchema` y `paymentRequestSchema`:

```ts
const { order, payment } = await api<CheckoutResponse>("/api/checkout", {
  method: "POST",
  json: {
    clientOrderId: orderId,                 // el `ord_...` que ya generás: hace el envío idempotente
    email, firstName, lastName, phone, street, number, apartment, city, province, zip, notes,
    shippingMethod, paymentMethod, acceptTerms: true,
    couponCode: coupon?.code,
    items: items.map(({ productId, colorId, size, quantity }) => ({ productId, colorId, size, quantity })),
  },
});

if (payment.redirectUrl) window.location.href = payment.redirectUrl; // Mercado Pago (tarjeta / mercadopago)
else router.push(`/checkout/exito?orden=${order.id}`);               // transferencia: payment.message trae las instrucciones
```

- `order.id` es el **id público** (`ord_…`), el que va en `?orden=`. `order.number` es el `BRV-XXXXXX` para mostrar.
- Los precios que manda el carrito se **ignoran**: la API usa los de su catálogo. Si algo cambió (sin stock, color, cupón) responde con `detail` listo para mostrar en un toast.
- `payment.status` es `approved` (ya cobrado) o `pending`.

### Página de éxito (`/checkout/exito`)

Mercado Pago vuelve a esa URL agregando `payment_id`, `status`, etc. Antes de mostrar el estado, confirmá contra la API para no depender de que el webhook haya llegado:

```ts
const paymentId = params.get("payment_id") ?? params.get("collection_id");
const order = paymentId
  ? await api<Order>(`/api/orders/${orderId}/reconcile`, { method: "POST", json: { paymentId } })
  : await api<Order>(`/api/orders/${orderId}`);
```

`reconcile` verifica ese pago contra la API de Mercado Pago y comprueba que pertenezca a ese pedido; el navegador no puede "decir" que pagó.

## 5. Códigos de error útiles (`code`)

`email_taken` · `invalid_credentials` · `locked_out` · `rate_limited` · `coupon_invalid` · `out_of_stock` · `product_unavailable` · `color_unavailable` · `size_unavailable` · `quantity_exceeded` · `gateway_error` · `validation_failed` · `origin_not_allowed`.

## 6. Pedidos de invitado

El front asociaba los pedidos de invitado a la cuenta nueva por email. Sin verificar el email eso permite que alguien se registre con el email ajeno y lea ese historial, así que la API lo trae **apagado** (`Commerce__ClaimGuestOrdersByEmail=false`). El invitado igual puede ver su pedido con el link `…/checkout/exito?orden=…`. Cuando se sume verificación de email se puede activar.

## 7. Cuenta admin

`Admin__Email` / `Admin__Password` crean el usuario admin al arrancar. Con esa cuenta (login normal) se usan `/api/admin/*` para ver pedidos, confirmar transferencias, avanzar el estado con código de seguimiento y ajustar stock. Todavía no hay pantalla: se usa Swagger (`/swagger` en desarrollo) o el archivo `.http`.
