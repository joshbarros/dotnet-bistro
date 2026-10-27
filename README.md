# dotnet-bistro

Table reservations with a waitlist. The scarce number is the **covers left** in a dinner sitting, and the **Sittings** module is its only writer. A reservation asks for 1 to 6 covers. When the sitting is full a guest can **join the waitlist** instead of being turned away: the line is first come, first served, and when covers come back (a cancellation, a refund, a poison rejection) the same transaction that gives them back seats the people waiting, **strictly in order**: if the party at the head of the line does not fit yet, nobody behind it jumps ahead, and new reservations cannot slip past a line either. Leaving the line is a conditional `DELETE` that races a promotion safely: exactly one of them wins the row, and either way every cover is free or held, never both, never lost. Two further rules hold in the same transactions: a guest may hold at most **`MaxPerGuest`** covers of a sitting (an atomic upsert that only moves while the new total fits, checked again at promotion), and the covers left are taken with a single conditional `UPDATE`. A reference that contains the poison token `POISON` is committed as rejected by the Confirmations module, the message is dead-lettered, and a compensating command gives the covers back, which seats the next party in line. A later refund does the same, but it does not roll back the confirmation that was already issued.

One ASP.NET Core process, one Postgres database. An HTTP write saves the order and an outbox row in one local transaction; a `BackgroundService` relay publishes outbox rows to RabbitMQ (the work queue) and Kafka (the event log); the next module consumes from RabbitMQ, claims an inbox key in the same transaction as its own write, and writes its own row and outbox row. Saga steps never call each other in-process. Delivery is at-least-once: the relay can publish and crash before it marks a row sent, so the row goes out again, and every consumer dedupes with its inbox key. Each saga step only moves an order forward from the status it expects.

Everything behind the saga is signed in: **ASP.NET Core Identity** with **JWT access tokens and rotating refresh tokens** (see [Authentication and authorization](#authentication-and-authorization)). Customers see and refund only their own orders; staff see everything and open sittings.

> The demo credentials in `compose.yaml` and `appsettings.Development.json` are for local use only. Set `JWT_KEY` and `ADMIN_PASSWORD` (see `.env.example`) before putting this anywhere shared. Compose binds every port to `127.0.0.1`.

![The Bistro UI signed in as the administrator: a full sitting with a party seated and two parties waitlisted in order](docs/screenshot.png)

*The UI reads everything from the API; shown signed in as the seeded administrator, on a sitting that is full: the second party asked to wait and sits at number 1 in the line, the third is number 2.*

## Order statuses

```mermaid
flowchart LR
    Requested -->|covers held| Held
    Requested -->|full and the guest asked to wait| Waitlisted
    Waitlisted -->|covers come back, in order| Held
    Waitlisted -->|leaves the line| Refunded
    Requested -->|full without the waitlist, or over the limit: no hold written| Rejected
    Waitlisted -->|over the limit at promotion| Rejected
    Held -->|confirmation issued| Sold
    Held -->|poison reference: confirmation rejected| Rejected
    Held -->|refund| Refunded
    Sold -->|refund| Refunded
```

`Refunded` and `Rejected` (after a hold) put the covers back through a `ReleaseCovers` command, and Sittings releases a hold at most once, gives the guest their allowance back with it, and then seats the waitlist. For a waitlisted order the same command just removes its place in the line. The confirmation has its own status (`Issued` or `Rejected`) and a refund never changes it. If the refund gets there before the final mark, the late mark finds the order is no longer `Held` and does nothing.

## How the modules talk

```mermaid
sequenceDiagram
    participant C as Client
    participant O as Orders
    participant T as Sittings
    participant E as Confirmations

    C->>O: POST /api/orders
    Note over O: order + outbox row, one transaction
    O-->>C: 202 Requested
    O->>T: covers.reserve (relay → RabbitMQ, copy to Kafka)
    Note over T: inbox claim, per-guest limit upsert, conditional UPDATE (not while a line exists), hold, outbox row
    alt covers left cover the order and the guest is within the limit
        T->>E: covers.held
        T->>O: covers.held (Requested → Held)
        alt reference is clean
            Note over E: confirmation, outbox row
            E->>O: confirmation.issued (Held → Sold)
        else reference contains POISON
            Note over E: committed Rejected, then the message is dead-lettered
            E->>T: covers.release (compensating command)
            E->>O: confirmation.rejected (Held → Rejected)
            T->>O: covers.released
        end
    else sitting full and the order asked to wait
        Note over T: the attempt is rolled back, then a waitlist row is written
        T->>O: covers.waitlisted (Requested → Waitlisted, number N in line)
        Note over T: later, a release promotes the head of the line: hold + covers.held
    else sitting full without the waitlist, or the guest is over the limit
        Note over T: the attempt is rolled back, so a limit increment never outlives a failed reserve
        T->>O: covers.short (Requested → Rejected, no hold, reason Short or Cap)
    end
    C->>O: POST /api/orders/{id}/refund
    O->>T: covers.release (Held or Sold → Refunded)
    T->>O: covers.released
```

| Module | Owns | Consumes | Emits |
| --- | --- | --- | --- |
| **Identity** (`libs/identity`) | `identity` schema: users, roles, refresh tokens. | | |
| **Orders** (`libs/orders`) | `Orders`: status, covers, total, guest reference. Validation, the poison rule, the total. | `covers.held`, `covers.short`, `confirmation.issued`, `confirmation.rejected`, `covers.released` | `covers.reserve`, `covers.release` (refund) |
| **Sittings** (`libs/sittings`) | `Sittings.CoversLeft` (sole writer after the sitting opens), `SittingGuests` (what each guest holds) and `CoverHolds`. | `covers.reserve`, `covers.release` (prefetch 1) | `covers.held`, `covers.short`, `covers.released` |
| **Confirmations** (`libs/confirmations`) | `Confirmations`. | `covers.held` | `confirmation.issued`, `confirmation.rejected`, `covers.release` (poison) |
| **Shared** (`libs/shared`) | The relay, the consumer loop with retry and dead-letter, the Kafka log, auth primitives, middleware. Each module owns its own `OutboxMessages` and `InboxMessages` tables. | | |

RabbitMQ topology is declared in code (`libs/shared/Messaging/Topology.cs`): exchange `bistro.work`, one queue per module (`sittings.commands`, `confirmations.work`, `orders.events`), a `.retry` queue per module (2 s TTL, then back to the main queue, 3 attempts), and a `.dlq` per module behind `bistro.dlx`. A transient failure retries; a poison message goes straight to the dead-letter queue after its rejection is committed. Kafka has one topic, `bistro.events`, one partition, replication factor 1, keyed by order id.

## Architecture

One deployable (`apps/api`), several libraries. Each business module is its own class library with its own `DbContext`, its own Postgres **schema** (`identity`, `orders`, `sittings`, `confirmations`), its own migrations and its own outbox and inbox tables. No module references another; the only shared code is `Bistro.Shared`, and the one cross-module read (a sitting's existence and pricing inputs) goes through an interface, `ISittingCatalog`, that Sittings implements and Orders consumes.

```mermaid
flowchart TB
    api["apps/api: composition root, middleware, endpoints"]
    api --> identity & orders & covers & confirmations
    identity["libs/identity: users, roles, JWT, refresh tokens"]
    orders["libs/orders: schema orders"]
    covers["libs/sittings: schema sittings"]
    confirmations["libs/confirmations: schema confirmations"]
    identity & orders & covers & confirmations --> shared["libs/shared: messaging, outbox/inbox, auth primitives, middleware"]
    orders -. "ISittingCatalog" .-> covers
```

Modules still talk only through messages: RabbitMQ carries the commands, Kafka keeps the event log, and the outbox and inbox make delivery at-least-once and consumption idempotent.

## Authentication and authorization

| Piece | How it works |
| --- | --- |
| Accounts | ASP.NET Core Identity. Passwords need 10+ characters with upper case, lower case and a digit, and 4 distinct characters. Five wrong passwords (or codes) lock the account for 15 minutes (`429`). A wrong password and an unknown email give the same `401`. |
| Email | Registration mails a confirmation link; with `Auth:RequireConfirmedEmail` on (the default) sign-in is refused (`403`) until it is opened. "Forgot password" mails a one-time reset link and answers `202` for every address, known or not. Messages go through `IAuthMailer`: SMTP when `Mail:Host` is set, the log otherwise. The demo stack also keeps them in a dev mailbox at `/api/dev/mailbox` (off by default). Link tokens are protected with data-protection keys stored in Postgres, so every instance and every restart can read them. |
| Two-factor | TOTP (any authenticator app): `POST /api/auth/mfa/setup` returns the key and an `otpauth://` URI, `enable` proves it with a code and returns eight one-time recovery codes, `disable` needs the password and a code. Once on, sign-in needs a code (`401 A verification code is required.`). |
| Access token | JWT, 15 minutes, held **in memory** by the Angular app and sent as `Authorization: Bearer`. HS256 with a key of at least 32 bytes, or **RS256** when `Jwt:RsaPrivateKeyPem` is set, in which case the public key is served at `/.well-known/jwks.json` so other services can verify tokens without a secret. The API refuses to start without an usable key. |
| Revocation | Each token carries the user's session version. A password change, a reset, turning two-factor off and **"sign out everywhere"** bump it (one atomic `UPDATE`), and every token sold earlier stops working on every instance within `Jwt:RevocationCacheSeconds` (default 5). |
| Refresh token | 256 random bits in an `HttpOnly`, `SameSite=Strict`, `Path=/api/auth` cookie (`Secure` over HTTPS). Only its SHA-256 is stored. Every refresh **rotates** it with a conditional `UPDATE`, so two concurrent refreshes with the same token cannot both win. Presenting a token that was already used revokes the whole family. Refresh and logout also need an `X-Requested-With: bistro-web` header, so another site cannot trigger them. |
| Roles | `Customer` (default at registration), `Staff`, `Admin`. An administrator is seeded from configuration on first start. |
| Rules | Anonymous: `/api/health`, `/api/auth/*`. Any signed-in user: read sittings, book, read and refund **their own** orders and confirmations. `Staff` and `Admin`: open sittings, read everything, read the event log. Another user's order or confirmation is `404`, not `403`, so ids do not leak. The owner of an order is the caller; a body field cannot set it. |
| Messaging | The outbox is drained by any number of relays at once (`FOR UPDATE SKIP LOCKED`, so no row is published twice at the same time) and trimmed on a schedule. Staff can look inside the dead-letter queues and replay them from the API. |
| Hardening | A rate limit on `/api/auth/*` per client (`RateLimit:AuthPerMinute`), **counted in Postgres** so every API instance shares one count, security headers on every response (`nosniff`, `DENY`, strict CSP, `no-referrer`), a correlation id echoed in `X-Correlation-Id` and in every problem response, JSON logs (`Logging:Json`), forwarded headers only when `ForwardedHeaders:Enabled`, a fallback policy that makes every endpoint authenticated unless it opts out, and a readiness endpoint that checks every module schema. |

## Run it

Needs Node 22 (npm 10), .NET SDK 10.0.401 (`global.json`), Docker (arm64 is fine) and python3 for the smoke script.

```bash
npm ci
dotnet test tests/Bistro.UnitTests   # 125 tests, no Docker
docker compose up -d --wait db        # the integration tests use Postgres on 5483 (database bistro_test)
docker compose exec db createdb -U bistro bistro_test   # once
dotnet test tests/Bistro.IntegrationTests
docker compose up --build --wait      # db, kafka, rabbitmq, api, web
./scripts/smoke.sh                    # last line: bistro smoke passed
npx playwright install chromium       # once
npx playwright test                   # the browser specs against http://127.0.0.1:8103
docker compose down                   # keeps the Postgres volume; `down -v` sittings it
```

| Service | Host port |
| --- | --- |
| Web (nginx, proxies `/api/`) | 8103 |
| API (`/scalar/v1`, `/api/health`) | 5223 |
| Postgres 17 | 5483 |
| Kafka 3.8.1 (KRaft, one broker) | 9133 |
| RabbitMQ 3.13 / management UI (`platform` / `platform`) | 5713 / 15713 |

Inside the Compose network the API and nginx listen on 8080 and Kafka's internal listener is 9092.

## Try it with curl

The web app is at <http://127.0.0.1:8103> (`admin@bistro.local` / `BistroAdmin2026` in the demo stack, or register a customer). From the shell:

```bash
API=http://127.0.0.1:5223
login() { curl -s -X POST $API/api/auth/login -H 'content-type: application/json' -d "{\"email\":\"$1\",\"password\":\"$2\"}" | python3 -c 'import sys,json; print(json.load(sys.stdin)["accessToken"])'; }
ADMIN="Authorization: Bearer $(login admin@bistro.local BistroAdmin2026)"

curl -s -X POST $API/api/auth/register -H 'content-type: application/json' \
  -d '{"email":"ana@example.com","password":"Passw0rd-Demo1"}'      # 201; its accessToken goes in $ANA below
ANA="Authorization: Bearer <accessToken>"

curl -s -H "$ADMIN" -X POST $API/api/sittings \
  -H 'content-type: application/json' \
  -d '{"code":"FRI-DINNER","name":"Friday dinner","room":"Terrace","chef":"Chez Marlo","coversTotal":8,"maxPerGuest":4,"priceCents":8000}'

curl -s -H "$ANA" -X POST $API/api/orders \
  -H 'content-type: application/json' \
  -d '{"sittingCode":"FRI-DINNER","reference":"RSV-1001","covers":4}'
# 202, status Requested, totalCents 35840. Use the returned id. A fifth cover for the same account would be refused (limit 4):

curl -s -H "$ANA" $API/api/orders/<id>               # Sold after a few seconds, with a confirmation number
curl -s -H "$ANA" $API/api/confirmations/<id>
curl -s -H "$ANA" $API/api/sittings/FRI-DINNER        # coversLeft 4

# The sitting has 4 left. A party of 6 would be turned away, unless it asks to wait:
curl -s -H "$ANA" -X POST $API/api/orders -H 'content-type: application/json' \
  -d '{"sittingCode":"FRI-DINNER","reference":"RSV-1002","covers":6,"waitlist":true}'
# status Waitlisted, "number 1 in line". When the first party is refunded the 6 are seated by themselves.
curl -s -H "$ADMIN" $API/api/orders/<id>/events        # the Kafka log for this order

curl -s -H "$ANA" -X POST $API/api/orders/<id>/refund \
  -H 'content-type: application/json'                       # covers go back on sale; the confirmation stays
```

An order is validated at the door: guest reference required (at most 40 characters), 1 to 6 covers. A sitting's `maxPerGuest` (default 8, at most the covers in the sitting) caps what one account can hold in it; an order that would pass it is rejected with the reason `Cap` and writes no hold. Send `"waitlist": true` to wait for covers instead of being rejected when the sitting is full. The poison match is a case-sensitive substring: `RSV-POISON-7` and any longer token containing `POISON` match.

## Develop with Nx

The repository is an Nx workspace (`nx.json`, plugin `@nx/dotnet`). `npx nx show projects` lists `api`, `web`, the four libraries and both test projects.

```bash
docker compose up -d --wait db kafka rabbitmq   # the brokers and Postgres, not the app
npx nx serve api                                # http://localhost:5223 (Development: seeded admin, public docs)
npx nx serve web                                # http://localhost:4303, proxies /api to the API
WEB_URL=http://localhost:4303 npx playwright test   # the same browser specs, against the dev servers
npx nx run-many -t typecheck lint -p web        # tsc --noEmit and ESLint (angular-eslint)
npx nx build web && npx nx build api            # what CI builds
```

## What is tested, and where it runs

- **Unit tests** (`tests/Bistro.UnitTests`, 125 tests, no Docker, about 3 seconds): the order rules and fee, the poison rule including integer overflow, token creation and validation, password and lockout behaviour of the auth service on an in-memory store, owner-or-staff access, the retry policy and topology, message serialization, the outbox `DbContext` conventions, and the HTTP middleware (problem details are `application/problem+json`, security headers, correlation ids).
- **Integration tests** (`tests/Bistro.IntegrationTests`, 78 tests, real Postgres): the **waitlist** (a full sitting refuses without the flag and queues with it; releases promote strictly in order; the head of the line blocks smaller parties behind it; leaving the line removes the place and holds nothing; a refund and a leave racing for the same covers never overbook, five rounds; a promotion past the guest limit is refused as `Cap` and the covers move on), the covers race (two tasks, two `DbContext`s, one cover: one `Held`, one `Short`, no hold for the loser), the **limit race** (six concurrent orders by one guest against a limit of two: exactly two held, four refused, the guest counter at two), a stock shortage after the limit passed (the limit rolls back), a release giving the allowance back, and the check constraints behind them; the saga handlers against the real schema (duplicate delivery claimed once, a release returns the covers once, the poison rejection committed before the dead-letter, refund then late final mark); and the **whole HTTP pipeline** through `WebApplicationFactory`: register, login, wrong password, lockout, expired and tampered tokens, refresh rotation, replay revoking the family, two concurrent refreshes (exactly one wins), logout, a missing `X-Requested-With` header, the auth rate limit, and 401/403/404 for every route and ownership case. The race tests were run against a read-then-write version of the hold, where it failed, and against the conditional update, where it passes.
- **`scripts/smoke.sh`** drives the composed stack: authentication and ownership first (401, 403, 404), then validation, the happy path (fee, confirmation, Kafka log), the last cover with three concurrent guests, the per-guest limit (the third cover is refused as `Cap`, stock untouched), the waitlist (queued as number 1 and 2, an unflagged request rejected, a refund seats both in order), the poison reference (rejected, `confirmations.work.dlq` gains one message, covers restored), and a refund that lands between the confirmation and the final mark. To make that last window wide enough to hit on purpose, Compose sets `Orders__FinalMarkDelayMs=1500`; it is a latency knob and defaults to 0.
- **Playwright** (`e2e/specs`, 16 tests, Chromium): `auth.spec.ts` (anonymous sees only the form, wrong password, password policy, a customer books and sees only their own orders with no staff controls, sign-out, the session surviving a reload through the cookie while `document.cookie` stays empty); `book-covers.spec.ts` (open a sitting, book, confirmation, reload, refund); `ui-flows.spec.ts` (refusal messages, the poison rejection, the last cover and the per-guest limit, a refund giving the allowance back, the waitlist seating a guest after a cancellation, the Kafka event log). They run against the composed web port and against the Nx dev servers.
- **CI** (`.github/workflows/ci.yml`) has two jobs. `verify`: `npm ci`, Prettier check, `dotnet format whitespace --verify-no-changes`, unit tests, integration tests (Postgres 17 service) with coverage uploaded, a NuGet vulnerability check, `npm audit`, `nx typecheck` and `nx lint` for web (zero warnings allowed) and `nx build`. `e2e`: builds the Compose stack, runs the smoke and every Playwright spec, and uploads traces and prints logs on failure. Dependabot watches NuGet, npm, Actions and Docker.

## Cloud mapping

This repository runs on Compose. Nothing in it calls a cloud account.

| Here | On Azure |
| --- | --- |
| Postgres 17 | Azure Database for PostgreSQL |
| Kafka 3.8 | Event Hubs |
| RabbitMQ 3.13 | Service Bus queue with its dead-letter sub-queue |
| The API and the web app | Container Apps |
| JWT signing key and seeded admin | Key Vault, injected as environment variables |

## API

JSON over HTTP. Everything except health and `/api/auth/register|login|refresh|logout` needs a bearer token. Interactive docs are at `http://127.0.0.1:5223/scalar/v1` (OpenAPI at `/openapi/v1.json`) when `Docs:Public` is true, which Compose and Development set; otherwise they need a staff token.

| Method and path | What it does | Responses |
| --- | --- | --- |
| `GET /api/health`, `GET /api/health/ready` | Liveness; readiness (every module schema reachable). | `200`, `503` |
| `POST /api/auth/register` | Creates a customer and mails a confirmation link. Signs them in unless confirmation is required. | `201` signed in, `202` check your inbox, `400` policy, `409` email taken, `429` |
| `POST /api/auth/login` | Signs in (with `code` when two-factor is on); sets the refresh cookie. | `200`, `401`, `403` unconfirmed, `429` locked or rate limited |
| `POST /api/auth/refresh`, `POST /api/auth/logout` | Rotate or revoke the refresh cookie. Need `X-Requested-With: bistro-web`. | `200`/`204`, `400` header missing, `401` |
| `POST /api/auth/confirm-email`, `resend-confirmation` | Confirm an address with the emailed token; ask for a new link (always `202`). | `204`/`202`, `400` |
| `POST /api/auth/forgot-password`, `reset-password` | Mail a reset link (always `202`); set a new password with it (ends every session). | `202`/`204`, `400` |
| `POST /api/auth/change-password`, `logout-everywhere` | Signed in: change the password (ends the other sessions and returns a fresh one); end every session on every device. | `200`/`204`, `400`, `401` |
| `GET /api/auth/mfa`, `POST /api/auth/mfa/setup`, `enable`, `disable` | Two-factor status and lifecycle (see above). | `200`/`204`, `400`, `409` |
| `GET /.well-known/jwks.json` | The public key set when RS256 is on. | `200`, `404` |
| `GET /api/auth/me` | The caller. | `200`, `401` |
| `POST /api/sittings` | Opens a sitting with its covers on sale, a price in cents and an optional `maxPerGuest` (default 8). Staff. | `201`, `400` invalid, `403`, `409` code exists |
| `GET /api/sittings`, `GET /api/sittings/{code}` | Sittings with the live covers left. | `200`, `404` |
| `POST /api/orders` | Accepts an order (1 to 6 covers) for the caller and starts the saga. | `202` Requested, `400` invalid, `404` unknown sitting, `409` duplicate reference (a full sitting or an over-limit guest is rejected, or waitlisted, later, by the saga) |
| `GET /api/orders?sittingCode=&page=&pageSize=` | The caller's orders, newest first, a page at a time (`pageSize` at most 100, default 100). Staff: everyone's. The total is in `X-Total-Count`. | `200` |
| `GET /api/orders/{id}` | One order and its status. Owner or staff. | `200`, `404` |
| `POST /api/orders/{id}/refund` | Moves a waitlisted, held or sold order to `Refunded`, puts the covers back (or removes the place in the line) and seats whoever is next. The confirmation is not touched. Owner or staff. | `202`, `404`, `409` wrong status |
| `GET /api/orders/{id}/events` | The Kafka event log for one order. Staff. | `200`, `403` |
| `GET /api/admin/dead-letters`, `POST /api/admin/dead-letters/{queue}/replay` | What waits in each dead-letter queue, with the first messages; publish them again. Staff. A message that still fails for the same reason is dead-lettered again. | `200`, `403`, `503` brokers off |
| `GET /api/confirmations/{orderId}` | The confirmation row. Owner or staff. | `200`, `404` |

Errors come back as `application/problem+json` with a `title` and the `correlationId`.

## Configuration

Compose sets these on the API container. Defaults in `apps/api/appsettings.json` point at the published host ports, so `nx serve api` works against `docker compose up -d --wait db kafka rabbitmq`.

| Setting | Compose value | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Bistro` | `Host=db;Port=5432;Database=bistro;Username=bistro;Password=bistro` | The one Postgres database. Migrations run on startup. |
| `RabbitMq__Host` / `__Port` / `__User` / `__Password` | `rabbitmq` / `5672` / `platform` / `platform` | Work queue. Virtual host `/`. |
| `Kafka__BootstrapServers` | `kafka:9092` | Event log. |
| `Jwt__Key` | `${JWT_KEY}` or a demo key | HS256 signing key, at least 32 bytes. Required; startup fails without it. |
| `Jwt__AccessMinutes` / `__RefreshDays` / `__SecureCookies` | `15` / `14` / `false` | Token lifetimes; set `SecureCookies` when TLS ends in front of the API. |
| `Identity__SeedAdmin__Email` / `__Password` | `${ADMIN_EMAIL}` / `${ADMIN_PASSWORD}` | Administrator created on first start. |
| `Auth__RequireConfirmedEmail` / `Auth__PublicUrl` | `false` (demo) / `http://127.0.0.1:8103` | Sign-in waits for email confirmation (code default `true`); the address links in emails point to. |
| `Mail__Host` / `__Port` / `__User` / `__Password` / `__From` / `__StartTls` | unset | SMTP for confirmation and reset emails. Unset means the log only. |
| `Mail__DevMailbox` | `true` (demo) | Serve the last messages at `/api/dev/mailbox`. Never on in production. |
| `Jwt__RsaPrivateKeyPem` / `Jwt__KeyId` | unset | Sign with RS256 and publish the public key at `/.well-known/jwks.json`. |
| `Jwt__RevocationCacheSeconds` | `2` (code default `5`) | How long an instance trusts a cached session version before re-reading it. |
| `RateLimit__AuthPerMinute` | `120` (code default `20`) | Requests a minute per client to `/api/auth/*`. |
| `ForwardedHeaders__Enabled` | `true` | Trust `X-Forwarded-*` from the reverse proxy (nginx here). Leave off when the API is reached directly. |
| `Docs__Public` | `true` | Serve Scalar and OpenAPI without a token. |
| `Logging__Json` | `true` | Structured JSON console logs. |
| `Messaging__OutboxRetentionDays` / `Messaging__InboxRetentionDays` | `7` / `30` | Sent outbox rows and old inbox claims are deleted after this long. The inbox is kept longer because it must outlive any redelivery. |
| `Orders__FinalMarkDelayMs` | `1500` | Milliseconds the final mark waits. A latency knob so the smoke can land a refund inside the window. `0` outside Compose. |
| `BISTRO_TEST_DB` (tests only) | | Postgres connection for `dotnet test`. Defaults to `localhost:5483`, database `bistro_test`. CI points it at its service container. |

## Look inside and fix things

```bash
docker compose ps                      # five services, all healthy
docker compose logs -f api             # relay, consumers, poison and retry lines
docker compose exec db psql -U bistro -d bistro -c 'select "RoutingKey","SentAt" from "OutboxMessages" order by "Id" desc limit 10'
open http://127.0.0.1:15713        # RabbitMQ management, platform / platform
```

- **Dead-lettered messages** sit in `confirmations.work.dlq`. Staff can list and replay them from `/api/admin/dead-letters`, or inspect them in the management UI. Retries wait in `<queue>.retry` for 2 seconds.
- **A port is already in use**: `lsof -nP -i :5223` shows who holds it. Compose publishes only on `127.0.0.1`.
- **`dotnet test` cannot connect**: start Postgres first with `docker compose up -d --wait db`.
- **The smoke cannot reach the API**: run `docker compose up --build --wait` first; override the target with `API=... RABBIT=... ./scripts/smoke.sh`.
- **Start clean**: `docker compose down -v` sittings Postgres, Kafka and RabbitMQ data. Plain `down` keeps them, and the smoke is safe on kept data because every code carries a run id.
- **The Kafka log looks empty right after an order**: the relay publishes within a second or so; reload the event log.

## Known limits

- The per-guest limit is per account. Nothing stops one person from registering a second account; tying it to an identity check is a product decision this repository does not make.
- The waitlist is strict first come, first served: a large party at the head of the line holds up smaller parties behind it until enough covers return. Nobody is notified when they are seated; the UI and the API show the new status.
- Sign-in is email and password plus optional TOTP. There are no external identity providers (Google, Microsoft) and no WebAuthn passkeys.
- Revocation is eventual by design: an instance may honour a revoked access token for up to `Jwt:RevocationCacheSeconds` seconds. Set it to `0` to check the database on every request.
- Delivery is at-least-once, not exactly-once. The inbox makes repeats harmless for the saga; the Kafka log can hold a duplicate event after a relay crash.
- Compose runs one of each broker (one Kafka partition, replication factor 1) and one API instance. The code itself is built for several API instances: the relay claims outbox rows with `FOR UPDATE SKIP LOCKED`, and the rate limit and revocation live in Postgres. A production Kafka and RabbitMQ would be clustered; that is configuration, not code.
- `GET .../events` reads the Kafka topic for one key and puts up after ten seconds, so it scans the topic. Fine here, not something to expose at scale.
- Replaying a dead letter only helps when the cause was fixed: the poison reference fails again by design.
- The browser specs drive Chromium at a desktop size only, and the `e2e` CI job needs Docker and takes a few minutes.

## Layout

```
apps/api       thin host: composition root, middleware, endpoints
apps/web       Angular 21, styled with Tailwind 4 utilities only (sign-in, role-aware workspace)
libs/shared    messaging (outbox, inbox, relay, consumers), auth primitives, middleware
libs/identity  users, roles, JWT, refresh tokens          (schema identity)
libs/orders   order rules and the saga's first step      (schema orders)
libs/sittings    the only writer of the covers left and the limit (schema sittings)
libs/confirmations confirmations                              (schema confirmations)
tests/         Bistro.UnitTests, Bistro.IntegrationTests
e2e/           Playwright
scripts/       smoke.sh
compose.yaml   Postgres, Kafka, RabbitMQ, API, web
```

Nx 23.2.1 orchestrates `nx build web` and `nx build api`; Each module's EF Core migrations are committed under its library and the API applies them on startup. Packages are pinned centrally in `Directory.Packages.props`, and warnings are errors (`Directory.Build.props`).

## Other systems in this .NET track

- [dotnet-freight](https://github.com/joshbarros/dotnet-freight): container space on ocean sailings.
- [dotnet-clinic](https://github.com/joshbarros/dotnet-clinic): appointment slots in a provider session.
- [dotnet-ledger](https://github.com/joshbarros/dotnet-ledger): outgoing payments from an account balance.
- [dotnet-warehouse](https://github.com/joshbarros/dotnet-warehouse): warehouse stock and outbound orders.
- [dotnet-academy](https://github.com/joshbarros/dotnet-academy): course cohort seats and enrollments.
- [dotnet-compute](https://github.com/joshbarros/dotnet-compute): GPU-hour reservations in a pool.
- [dotnet-hotel](https://github.com/joshbarros/dotnet-hotel): hotel room-nights.
- [dotnet-tickets](https://github.com/joshbarros/dotnet-tickets): event ticket tiers and orders.
- [dotnet-carbon](https://github.com/joshbarros/dotnet-carbon): carbon credit batches and purchases.
- [dotnet-charging](https://github.com/joshbarros/dotnet-charging): EV charger slot reservations.
- [dotnet-promo](https://github.com/joshbarros/dotnet-promo): limited-use promo codes and redemptions.
- [dotnet-pledge](https://github.com/joshbarros/dotnet-pledge): matched donations from a sponsor's matching pool.
- [dotnet-mint](https://github.com/joshbarros/dotnet-mint): limited-edition drops with a per-collector limit.
- [dotnet-flash](https://github.com/joshbarros/dotnet-flash): flash-sale deals with holds that expire.
- [dotnet-encore](https://github.com/joshbarros/dotnet-encore): gig seats with price steps as the gig sells.
- [dotnet-atelier](https://github.com/joshbarros/dotnet-atelier): workshop seats with an all-or-nothing laptop kit bundle.
