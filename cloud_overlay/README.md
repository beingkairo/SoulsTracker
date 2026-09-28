# Hosted overlay state service

This package contains a Worker API, SQLite-backed Durable Object,
and hosted browser assets from `web_overlay`. Desktop composes the hosted publisher
behind explicit protected pairing to https://overlay.beingkairo.com only.
The configuration admits no identities until operator provisioning. The narrowly
scoped provisioning route accepts only pre-admitted slots and their one-time setup
grants; there is no anonymous identity creation or operator credential in Desktop.

## Local verification

Use Node 22.23.2 or a compatible supported Node release. From this directory:

```sh
npm ci
npm ci --prefix ../web_overlay
npm run build --prefix ../web_overlay
npm run check
npm run build
npm test
npm run check --prefix ../web_overlay
npm test --prefix ../web_overlay
npm audit
```

`build` runs Wrangler with `--dry-run` and writes the local bundle to `dist/`.
It does not deploy. Do not omit that flag. Tests use the official Cloudflare
Vitest pool and local workerd with SQLite storage. They inject synthetic
verifiers through `runInDurableObject`; no application database is accessed.
The production configuration admits no overlay identities by default.
There is no production HTTP override. Playwright starts a separate test-only
Wrangler configuration on local TLS at port 8799. That harness injects synthetic
credentials into isolated workerd storage and can disconnect readers; its entry
and test routes are never bundled by the production configuration. Both npm
packages must be installed before the browser suite can start this harness.

The pool is pinned with its matching Wrangler version and compatibility date.
Vitest 4.1 is required by this pool; Vitest 5 is outside its supported peer range.
The exact `sharp` override fixes the local tooling's transitive libheif advisory
(GHSA-rgj7-g3m4-5g8c). Image handling is not used by the service.

The suite exercises real SQLite rollback using failing SQL triggers, concurrent
requests, response-loss retries, graceful object eviction and forced object
restart. These checks establish local storage behavior. They do not prove
Cloudflare network failover, deployed hibernation, deployment migration, or browser
behavior. No live infrastructure is needed for these tests.

## Provisioning API

`POST /api/v1/overlays/{id}/provision` exists only for an identity present in both
`PROVISIONED_IDS` and the bounded `PROVISIONING_SLOTS` configuration. It requires
`Authorization: Setup <grant>`, exact-origin HTTPS, no cookies, strict JSON containing
one request ID plus separate role/identity-bound read and write verifiers, and a
dedicated rate-limit admission before Durable Object lookup. Unknown/non-slot IDs,
incorrect grants, unavailable admission, malformed input, and used/conflicting slots
fail closed.

An empty matching Durable Object installs the supplied verifiers, default channels,
and exact request digest transactionally. The same authenticated request is
idempotent; changed replay, existing control, orphan records, or partial state cannot
replace authority. Provisioning does not acquire a publisher session, publish state,
advance revisions, or broadcast. Raw read/write capabilities are generated and
retained by Desktop and never sent on this route.

## Write API

These write-authorized HTTPS endpoints exist under `/api/v1/overlays/{id}`:

- `GET /publisher`: epoch, credential generation, channel revisions and normalized content digests.
- `POST /session`: `{v, expectedEpoch, sessionRequestId}`.
- `PUT /state`: `{v, epoch, sessionRequestId, sequence, death?, appearance?}`.
- `POST /credentials`: `{v, rotationId, expectedGeneration, readCapability?, writeCapability?}`.

Every endpoint requires `Authorization: Bearer <write-capability>`. Query
credentials, cookie authentication, CORS access and other routes/methods are not
supported. Responses are `no-store`. IDs and request IDs are lowercase 32-digit
hexadecimal strings. Capabilities are separate random 256-bit values encoded as
64 lowercase hexadecimal digits. Only SHA-256 verifiers bound to identity and
role are stored; equality checks use the runtime's timing-safe primitive.

Mutations require `Content-Type: application/json`, version `1`, strict field
allowlists, valid UTF-8, unique JSON keys, and at most 8192 bytes. Compressed bodies
are rejected. Epoch, sequence, credential generation and revisions are canonical nonnegative Int64
decimal strings. Exhausted counters return a conflict and never wrap.

Channel values use the shared hosted contract, without caller-assigned revisions.
An omitted channel remains unchanged. A death object can explicitly present
unavailable with `value:null` or the retained zero placeholder. A top-level
`death:null` is not a valid write; it represents the initial persisted state only.
Appearance always contains a complete validated value and begins at shared
contract defaults.

Session acquisition atomically checks `expectedEpoch`. The current identical
acquisition returns its original acknowledgement. An obsolete acquisition is a
409 conflict. State writes check the session and epoch before sequence ordering.
Identical normalized requests at the same sequence return the saved acknowledgement;
lower or reused conflicting sequences return 409. Higher sequences advance
ordering even when both channels are unchanged. Only changed channels increment
their own revision. Totals are never used to determine ordering.

Channel digests are lowercase SHA-256 hex of UTF-8 canonical JSON: shared-contract
field order and normalization, with revision replaced by `"0"`; initial death
uses the JSON literal `null`. State acknowledgements include status, session ID,
sequence and the channel names changed by that original acceptance. A retry
returns the same acknowledgement, but its internal committed-change result is
null. Only a changed committed result broadcasts complete changed channels to
authenticated readers. No event history is stored.

Rotation requires the current write capability, the observed credential generation
from publisher status as `expectedGeneration`, and one or both replacements.
Missing or invalid generation is rejected with 400. A new rotation atomically
compares its expected generation with the current generation; a mismatch returns
409 without changing any records. Never rebase an old request automatically.
It advances the epoch and credential generation, clears the writer session and
sequence, and preserves both channels and their revisions. Read generation
advances only when the read capability changes, so a write-only rotation does not
invalidate the read URL or disconnect current read clients. Retry the latest rotation with its identical
ID/body and current (new, when replaced) write capability. Old write credentials
have no recovery exception. Rotation retries never fence a subsequently acquired
session again. Only the latest exact ID and normalized body (including the original
expected generation) can return the saved acknowledgement before the generation
comparison. Reusing that latest ID with changed content returns 409. A superseded
rotation with its old generation returns 409 under current write authority; a
deliberate new rotation using the current generation can proceed. Publisher status
and session/state acknowledgements include `generation`; public snapshot contracts
do not. There is no read-capability recovery path.

The database contains only three bounded latest-value records: control/auth,
death and appearance. Session, write and rotation acknowledgements in control
are latest-only retry metadata. No application history, expiration, alarm, queue,
KV, D1 or second service is used.

## Read sockets and static assets

`GET /api/v1/overlays/{id}/live` upgrades a same-origin TLS WebSocket. Admission
requires a provisioned routing ID and the exact explicitly configured HTTPS
`BROWSER_ORIGIN`, before obtaining a Durable Object stub. Missing or malformed
configuration denies access. Request origins must also match the configured origin.
Cookies, Authorization headers, query credentials and WebSocket subprotocols
are rejected on this route.

The credential-free `/overlay/` page accepts only the fragment shape
`#id=<overlay-id>&read=<read-capability>`, with canonical lowercase hexadecimal
values. Queries, duplicate/extra fields and old style fragments are invalid.
The read capability is sent in the first socket message as
`{v:1,type:"auth",readCapability:<read-capability>}`; it never goes into a socket
URL, DOM or static asset. Read credentials cannot write state.

Hibernatable sockets use persisted attachments, two pending slots, eight
authenticated slots and a five-second pending authentication deadline. Fixed
`ping`/`pong` responses do not extend pending authentication. After authentication
the object sends one complete snapshot in the same serialized event. It reads
durable latest state independently of Desktop; before the first death publication
the page remains transparent. Every broadcast checks the current read generation.
Read rotation closes revoked clients, while write-only rotation retains readers.

The browser holds channels and Int64 revisions in memory, renders combined
updates once, ignores older/equal revisions and retains its DOM on disconnect.
One owner handles full-jitter reconnect from one second to a thirty-second cap,
reset after valid hydration only, with thirty-second pings and a ninety-second
no-response deadline. Close code 4401 stops retries until reload or a corrected
fragment; 4400 is malformed protocol and 4429 is capacity. No state polling,
local storage, service worker or local transport fallback is used. Cold offline
loading cannot recover a state it has never received.

Appearance comes only from the validated wire values. The hosted entry adapts
the existing compact, left-aligned title/icon/text effects and fixed 2.5rem
number-only size. Fonts use standard local resolution with sans-serif fallback;
no font service or upload is involved.

The build produces content-hashed JS/CSS and the existing bundled skull raster.
The stable shell revalidates with an ETag; hashed assets are immutable for one
year. Builds retain earlier hashed files in `web_overlay/dist/hosted/assets`.
Keep that asset directory with the preceding build when preparing a rollout
artifact; a clean checkout alone does not contain prior hashes. Do not delete
previously referenced hashes during rollout overlap. This package does not
automate publishing or a retention/deletion policy for deployed assets.

Static headers restrict content and connections to the same origin, prohibit
referrers and set nosniff. Rendering uses textContent and typed CSS properties,
without inline scripts, style text or external resources. Dynamic responses are
no-store and do not enable CORS or authentication cookies. Local browser tests
check actual CSP behavior, asset loading and cache headers.

## Before any live use

Live deployment, operator provisioning/recovery, private setup-code delivery, edge
abuse/rate controls, and real network/browser parity require separate verification. The configuration
disables worker/preview URLs and observability and contains no account ID.
Do not treat passing local tests as proof of deployed browser behavior.

The bounded Windows operator tool in `operator/provision.py` prepares a new owner-only
directory under LocalAppData/SoulsTrackerOperator. `prepare-setup` generates bounded
pre-admitted identities, independent setup grants, private setup codes, and runtime
configuration containing setup verifiers only. Deliver each code privately and never
copy it into a command, log, report, or source-controlled file. The legacy `prepare`
action remains available for controlled version-1 compatibility preparation. The tool
is not part of the Worker bundle and never deploys resources.

Temporary `BOOTSTRAP` deployment configuration contains only version, exact
overlayId and role/ID-bound read/write verifiers. An authenticated GET publisher
can initialize an empty matching object transactionally. Existing control always
takes precedence; replay cannot replace credentials, sessions or channels.
Remove the binding by deploying the generated runtime configuration without
`--keep-vars`, then verify persisted status again before issuing a setup code.
Never deploy test entries. Keep the Worker name, class, binding and v1 migration
unchanged across deployments. A custom domain must not replace an existing DNS
resource. Account selection, DNS inspection and deployment access are operator
prerequisites, not application settings.

Run operator checks with `python -m unittest discover -s cloud_overlay/operator -v`
from the repository root. They use synthetic capabilities and no network access;
the Windows ACL case creates and removes an empty private test directory.
