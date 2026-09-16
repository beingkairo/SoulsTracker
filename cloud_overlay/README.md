# Hosted overlay state service

This package contains an unconnected Worker API and SQLite-backed Durable Object.
The desktop application and current local overlay do not use it. There are no
browser assets, sockets, deployment scripts, public provisioning endpoints, or
account credentials in this package.

## Local verification

Use Node 22.23.2 or a compatible supported Node release. From this directory:

```sh
npm ci
npm run check
npm run build
npm test
npm audit
```

`build` runs Wrangler with `--dry-run` and writes the local bundle to `dist/`.
It does not deploy. Do not omit that flag. Tests use the official Cloudflare
Vitest pool and local workerd with SQLite storage. They inject synthetic
verifiers through `runInDurableObject`; no application database is accessed.
The checked-in configuration admits no overlay identities. There is no local
provisioning command or production HTTP override.

The pool is pinned with its matching Wrangler version and compatibility date.
Vitest 4.1 is required by this pool; Vitest 5 is outside its supported peer range.
The exact `sharp` override fixes the local tooling's transitive libheif advisory
(GHSA-rgj7-g3m4-5g8c). Image handling is not used by the service.

The suite exercises real SQLite rollback using failing SQL triggers, concurrent
requests, response-loss retries, graceful object eviction and forced object
restart. These checks establish local storage behavior. They do not prove
Cloudflare network failover, deployed hibernation, deployment migration, or OBS
behavior. No live infrastructure is needed for these tests.

## Write API

Only these HTTPS endpoints exist under `/api/v1/overlays/{id}`:

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
null. That internal result is the future broadcast boundary; no broadcaster or
event history exists here.

Rotation requires the current write capability, the observed credential generation
from publisher status as `expectedGeneration`, and one or both replacements.
Missing or invalid generation is rejected with 400. A new rotation atomically
compares its expected generation with the current generation; a mismatch returns
409 without changing any records. Never rebase an old request automatically.
It advances the epoch and credential generation, clears the writer session and
sequence, and preserves both channels and their revisions. Read generation
advances only when the read capability changes, so a write-only rotation does not
invalidate the eventual read URL. Retry the latest rotation with its identical
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

## Before any live use

Live deployment, operator provisioning/recovery, pairing delivery, host approval,
edge abuse/rate controls and socket authorization remain external prerequisites.
The configuration disables worker/preview URLs and observability and contains
neither account IDs nor domains. Do not treat passing local tests as authorization
to deploy or to connect the desktop application.
