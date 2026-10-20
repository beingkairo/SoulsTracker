# Hosted overlay state service

This package contains a Worker API, SQLite-backed Durable Object,
and hosted browser assets from `web_overlay`. Desktop composes the publisher for
https://overlay.beingkairo.com only. First use creates one anonymous identity through
a fixed verifier-only route. Desktop contains no account or provider credential.

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
The tracked production configuration includes anonymous-create admission bindings
and a versioned provisioning authority. Its current lifetime ceiling is a temporary
rollout safety stop, not a permanent product capacity policy. The separate test
Worker configuration supplies deterministic local authority and limiter fixtures.
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

`POST /api/v1/overlays` is the only public create operation. It accepts no path ID,
authorization, cookie, account, or management authority. Before checking the method,
headers, content type, body, or namespace, every create-surface attempt consumes a
provider client/network admission check and a service-wide admission check. Missing,
malformed, denied, or throwing admission bindings fail closed with bounded generic
responses.

The strict body contains a 256-bit request identity plus separate request-bound read
and write verifiers. Raw capabilities remain protected in Desktop and never cross the
create boundary. One global Durable Object serializes a transactional allocation
ledger and hard ceiling, mints a namespace identity, and initializes that exact empty
target through an internal-only method. Reserved allocations remain counted and
replayable. Exact replay returns the same identity even at the ceiling; changed replay,
existing target state, collision, and ceiling denial fail generically. Creation does
not acquire a publisher session, publish state, advance revisions, or broadcast.

## Write API

These write-authorized HTTPS endpoints exist under `/api/v1/overlays/{id}`:

- `GET /publisher`: epoch, credential generation, channel revisions and normalized content digests.
- `POST /session`: `{v, expectedEpoch, sessionRequestId}`.
- `PUT /state`: `{v, epoch, sessionRequestId, sequence, death?, appearance?}`.
- `POST /credentials`: `{v, rotationId, expectedGeneration, readCapability?, writeCapability?}`.

Desktop v2.1.1 identifies itself with Hosted Overlay `protocolVersion:2`, separate
from the `v:1` transport shape. Publisher GET uses the `X-SoulsTracker-Protocol: 2`
header; mutations carry `protocolVersion:2` in the JSON body and the matching
header. Missing/unsupported versions on otherwise authenticated, valid mutations
return 409 `client_update_required`. They cannot publish or rotate credentials;
they can only refresh a 180-second incompatible-contact marker. Legacy publisher
GET and browser readers never mark contact. The current client confirms death or
appearance on the existing state route and sends presence-only state requests
every 60 seconds without changing channel revisions. Each accepted request grants
a 180-second server-clock publisher lease. Exact replay does not extend the lease.
Expired publishers leave stored identity, URL, and last values intact. A new
compatible session must confirm state or renew presence to become live. The v2
reader shows `Please update SoulsTracker.` only during recent authenticated legacy
contact with no live compatible publisher; otherwise an inactive publisher shows
`Please open SoulsTracker.` Old browser assets require refresh to display these
states, so a rollout must retain prior hashed assets while existing readers drain.

Every endpoint requires `Authorization: Bearer <write-capability>`. Query
credentials, cookie authentication, CORS access and other routes/methods are not
supported. Responses are `no-store`. Capabilities are separate random 256-bit
values encoded as 64 lowercase hexadecimal digits. Legacy IDs are lowercase 32-digit
hexadecimal strings; version-2 request identities and namespace-issued identities use
canonical 64-digit lowercase hexadecimal forms. Version-1 verifiers remain identity-bound, while
version-2 verifiers are request-bound. Equality checks use the runtime's timing-safe primitive.

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

The credential-free `/soulstracker/` page accepts only the fragment shape
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

## Provisioning and rollout gate

Existing protected Desktop pairings and reserved create requests reuse their
original identity and URL. Disabling Hosted Overlay stops network activity but does
not revoke identity or remove the last stored display. The checked-in deployment
configuration retains its versioned lifetime ceiling. The authority also supports
an explicitly configured window budget for new allocations, verified locally with
synthetic settings. No Production window budget has been selected or enabled. Do not
increase or remove the existing stop based on a guessed Cloudflare account limit. First
reconcile aggregate active/reserved ledger counts, legacy seed count, distinct union
and overlap, effective deployed and persisted configuration, account plan, usage,
and resource headroom using an authorized read-only operator report. Record an
evidence-backed creation budget and emergency stop procedure before changing the
authority policy. Existing reads, authenticated compatible writes and exact pending
replay must remain available when new creation is stopped. Deployment also requires
an explicit checked-in/deployed/persisted config comparison; a source match to
deployed metadata alone does not establish persisted authority equality.

### Windowed allocation policy

`PROVISIONING_ALLOCATION_POLICY` is an optional structured Worker variable with
exactly these fields. It is not supplied by the public creation request:

| Field | Contract |
| --- | --- |
| `v` | Policy schema version, exactly `1` |
| `revision` | Positive safe integer; first activation is `1`, changes advance exactly one revision |
| `enabled` | Boolean; `false` stops new reservations |
| `windowSeconds` | Positive integer duration whose milliseconds and server-time deadline are safe integers |
| `maxNewAllocations` | Positive safe integer maximum new reservations in one window |

No default duration or budget is inferred. Without the variable and without prior
activation, the existing lifetime ceiling continues to apply. Once activated,
missing, malformed, stale, skipped, or conflicting policy settings deny new
allocations. The retained authority version, ceiling, hard maximum and seed digest
must still match their separate configuration contract. Windowed admission does
not use the lifetime ceiling as an identity limit; it does not delete or recycle
any identity. Removing the variable after activation cannot restore the old mode.

One singleton `allocation_policy` row tracks revision, configuration digest,
window deadline, consumed reservations and last observed server time. Explicit
application starts the first fixed-duration window. At expiry, the next request
starts one new window. Budget is consumed atomically with the allocation INSERT,
before target initialization, and survives restart. A failed target keeps its
charged reservation for recovery; it receives no refund. An allocation INSERT
failure rolls back its charge. No full-ledger count runs on windowed admission.
Policy changes retain unexpired consumption and can extend its deadline; stop,
resume and duration edits cannot reset a consumed budget. Server clock regression,
unsafe arithmetic and missing/corrupt policy state fail closed without repair.

To stop new reservations, configure `enabled=false` in the next consecutive policy
revision and explicitly apply it. Configuration mismatch already denies new
reservations while application is pending. Resume requires explicit application
of another consecutive revision with an approved budget.
Exact verifier-bound replay and reserved-target recovery run before this gate and
spend no additional slots, including while stopped or when policy settings are
unavailable. Existing authority configuration checks still apply. Publisher and
viewer authentication, leases, rates and socket limits remain independent.
The anonymous create route retains both pre-parse rate limiters, fixed origin,
bounded strict body and request-bound verifier checks. It does not establish a
human identity or prevent distributed abuse by itself. The authority budget is
separate from the provider's per-key admission counters.

Before activation, review real persisted authority/seed/allocation aggregates,
account-wide plan/usage/headroom, expected growth and spending tolerance. Measure
reservation/heartbeat storage writes and global-authority throughput. No test
budget is a recommended operational value. Deployment needs a separately reviewed
policy revision, additive storage transition and rollback that retains policy
enforcement; an older binary does not enforce the new singleton. The local
version-1 reconciliation report returns `unsupported_policy` after activation,
with null aggregates. The separate policy status report below supports policy
state; it does not supply identity-population or account-resource aggregates.

### Explicit policy application and readback

The authority RPC `applyConfiguredPolicy(credential, expectedRevision)` implements
the `apply-configured-policy` operation. It reads only the reviewed
`PROVISIONING_ALLOCATION_POLICY` Worker variable; the caller cannot submit a policy.
It requires a matching persisted authority configuration, including seed equality,
before doing any policy work. It never initializes authority configuration, inserts
seed/allocation rows, generates identities or accesses overlay objects. Public
creation cannot initialize the policy table or advance its revision. Configured
but unapplied policy, missing policy after activation and any mismatch fail closed
for new reservations. Exact reservation replay retains its existing checks.

`expectedRevision` is the persisted revision the operator has reviewed, or `0`
before first activation. The configured revision must be exactly one greater.
Conflicting content at the same revision and stale/skipped revisions are rejected.
An exact repeat with the original expected revision returns `already_applied`
without changing storage, consumption, clock or deadline. Applying a new revision
preserves an unexpired window's consumption and never shortens its deadline;
the deadline may extend to the new duration measured from application. An expired
window starts a new window with zero consumption. A disabled policy consumes no
new reservations. Clock regression or unsafe deadline arithmetic rejects apply.

Apply requires the independent secret `PROVISIONING_POLICY_APPLY_KEY`; status
requires `PROVISIONING_POLICY_READ_KEY`. Each is a separately generated 32-byte
lowercase hex credential supplied as a Worker secret, never a checked-in variable,
URL parameter or log value. Missing/malformed keys and using the same key for both
permissions fail closed. Each RPC authenticates independently before storage
access. Possession of a Service Binding or namespace binding alone does not confer
either permission. Remove an apply credential to disable apply access without
removing read access. Publisher/read capabilities authorize neither operation.
No HTTP, public creation, publisher or browser route dispatches these RPCs.

`policyStatus(credential)` performs only SELECT statements within one synchronous
authority transaction. It never initializes schema, repairs state, rolls windows,
updates the clock or calls overlay objects. Its schema-version-1 report includes
activation state, persisted revision, configured-policy validity and equality,
configured enabled state, stored window consumption/deadline/last observed time,
observation time, clock validity and window expiry. `new_allocation_state` describes
only this policy gate, not overall creation eligibility or available account
resources. `active` means policy storage exists and is valid, including a stopped
policy. It is not publisher activity. Missing/corrupt storage and failed reads
produce explicit unavailable/null fields. An absent table means never activated;
an existing table with a missing singleton is invalid and is never repaired.

The persisted row stores an equality digest, not a reconstructable policy.
`persisted_enabled` and `persisted_limits` are therefore null with an explicit
reason. Configured enabled state must not be reported as persisted truth when
equality fails. No report returns credentials, digests, identity rows or URLs.
Worker configuration is outside the storage transaction; verify the deployed
artifact/configuration separately. The legacy aggregate report remains separate
and must not label a retained lifetime ceiling as an active window-policy cap.

Any apply transport failure, timeout or `outcome_unknown` is ambiguous. There is
no automatic retry. Obtain separately authorized readback and compare revision,
equality, consumption and deadline before deciding whether to perform an explicit
repeat with the same expected revision. If readback is unavailable, stop and
escalate operationally; do not infer failure or advance the revision. An unexpected
storage failure during apply is also reported as unknown without exception details.

### Rollback and ongoing emergency-stop gate

Before activation, a rollback retaining the legacy configuration and absent policy
uses the existing lifetime gate. After activation, rollback must retain this
explicit-apply and exact-match admission implementation, the policy table, the
legacy-write rejection boundary, existing namespace/classes/migrations, seed list,
authority version and all identity/reservation rows. Removing the policy variable
or reverting to a lower configured revision blocks new reservations while exact
recovery and existing identities remain available. Never drop the policy table or
restore a pre-policy binary to resume creation. Resume with a reviewed consecutive
revision; do not reuse an old revision or refund consumed budget. Pin and verify
a compatible rollback artifact before any live change.

No ongoing Production operator ingress is installed by this implementation.
Temporary diagnostic ingress is not an emergency-stop mechanism and its separate
branch is not integrated here. Before activation, establish and independently
review a supported operator mechanism that can invoke apply with its own credential
and status with the separate read credential, remains accessible after temporary
diagnostic teardown, has tested credential custody/revocation and ambiguous-outcome
handling, and can stop allocations without provisioning or revoking identities.
If diagnostic teardown would remove the only operator path, do not activate or
tear down that path while relying on it for emergency stop. Ongoing authenticated
emergency-stop access remains an unresolved activation gate. This code does not
invent a replacement ingress or authorize its deployment.

## Before any live use

Live deployment, legacy seeding, production creation rates/ceiling, edge abuse
controls, and real network/browser parity require separate owner-approved verification.
The configuration disables worker/preview URLs and observability and contains no account ID.
Do not treat passing local tests as proof of deployed browser behavior.

The bounded Windows operator tool in `operator/provision.py` remains only for controlled
version-1 compatibility preparation and recovery. It creates an owner-only directory
under LocalAppData/SoulsTrackerOperator. It is not part of the Worker bundle and never
deploys resources. Anonymous creation has no operator-issued user artifact.

Temporary `BOOTSTRAP` deployment configuration contains only version, exact
overlayId and role/ID-bound read/write verifiers. An authenticated GET publisher
can initialize an empty matching object transactionally. Existing control always
takes precedence; replay cannot replace credentials, sessions or channels.
Remove the binding by deploying the generated runtime configuration without
`--keep-vars`, then verify persisted status again.
Never deploy test entries. Keep the Worker name, class, binding and v1 migration
unchanged across deployments. A custom domain must not replace an existing DNS
resource. Account selection, DNS inspection and deployment access are operator
prerequisites, not application settings.

The deployed browser assets must match the reviewed Desktop appearance contract before
anonymous creation is enabled. Retained local preview-equivalence and prefix-gap tests
cover custom skull color, prefix mode, title visibility, scale, clipping, and D1 geometry;
the currently deployed production artifact still requires a later parity rollout.

Run operator checks with `python -m unittest discover -s cloud_overlay/operator -v`
from the repository root. They use synthetic capabilities and no network access;
the Windows ACL case creates and removes an empty private test directory.
