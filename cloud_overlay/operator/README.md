# Policy operations and local candidate packaging

The operator gateway is separate from the public overlay Worker. Its checked-in
configuration is disabled, has no routes, and has no Access or credential values.
All commands below are local verification or packaging. They grant no authority
to deploy, collect Production data, create credentials, provision an identity or
apply a Production policy. The checked-in authority remains version 3, 100/100,
with no allocation policy configured.

## Permanent control contract

The gateway accepts only these exact HTTPS paths on its configured host:

- `GET /operator/policy/status`, without a body.
- `POST /operator/policy/apply`, with JSON containing only a nonnegative safe
  integer `expectedRevision` (maximum 256 bytes).

Each explicit request supplies an Access JWT in `Cf-Access-Jwt-Assertion` and its
independent authority role credential in `Authorization: Bearer ...`. Use an
approved secure operator client; never put either value in command arguments,
URLs, chat, source, browser assets, captured requests or logs. The gateway stores
neither policy credential. Cookie, browser Origin, upgrade, query, alternate host,
encoding, unsupported method and extra body fields are denied. There is no CORS,
redirect-based authentication, provisioning, SQL, enumeration, configuration-edit,
arbitrary RPC, policy submission or target-selection operation.

Before enablement, supply `OPERATOR_HOST`, `ACCESS_ISSUER`, and distinct
`ACCESS_READ_AUDIENCE`/`ACCESS_APPLY_AUDIENCE` and
`ACCESS_READ_POLICY`/`ACCESS_APPLY_POLICY`, with the exact authorized
`ACCESS_READ_OPERATOR`/`ACCESS_APPLY_OPERATOR`. The same human may hold both roles;
their authorizations and credentials remain distinct. JWT signature, pinned team
issuer, exact audience/operator/policy, subject, MFA and integer time claims are
verified. Authentication age and declared session duration must be at most 15
minutes. Required claims include `amr` containing `mfa`, `policy`, `auth_time`,
`iat`, `nbf`, and `exp`. Missing assurances fail closed. Local signed fixtures do
not prove this account emits these claims. Demonstrate real claim compatibility
through separately authorized sanitized evidence; do not weaken checks to enable
an unsupported account. Certificate responses are bounded, redirects rejected,
and certificate or authentication failure denies access.

`POLICY_READ` binds only to `PolicyReadEntrypoint`; `POLICY_APPLY` binds only to
`PolicyApplyEntrypoint`. The gateway has no Durable Object binding. The target
authority independently checks `PROVISIONING_POLICY_READ_KEY` or
`PROVISIONING_POLICY_APPLY_KEY` before storage. Missing, malformed, crossed or
shared role keys deny access. Diagnostic credentials must also be distinct.
Possession of a Service Binding alone grants no policy permission. Remove an
apply binding/key to revoke application without removing the read path.

Target results pass a closed, allowlisted response schema. Responses are no-store;
no credentials, policy/seed digests, identity rows, URLs or exception details are
returned or logged. Status is SELECT-only, including on cold storage. It reports
persisted revision, policy/configuration equality, consumption/deadline, clock
validity and explicit unavailable fields; it is not population or billing data.

One explicit inbound operation issues at most one target RPC. A timeout does not
cancel that RPC or prove failure. `outcome_unknown`, malformed downstream output,
transport loss and timeouts require a separate authenticated status request before
any operator decision. There is no automatic retry or Retry-After instruction.
An exact explicit repeat with the original predecessor can return
`already_applied`, with no write. This is not an exactly-once transport guarantee.
Replaying an old predecessor cannot apply a later consecutive revision.

## Controlled emergency stop

Apply permission cannot edit Worker configuration. A deployment administrator or
separately approved restricted deployment workflow stages policy changes; routine
operators receive no Cloudflare deployment credential.

1. Acquire status using the read role and independently verify the effective
   configuration/artifact. Resolve unavailable state, clock problems, pending
   configuration and any ambiguous previous operation before choosing a revision.
2. The offline Python function
   `stop_policy.stage_emergency_stop(configuration, status)` produces a deep-copied
   full configuration proposal. It requires matching active policy/readback and
   unchanged authority version/100/100. It changes only `enabled` to `false` and
   revision to the immediate successor, preserving duration, budget and all other
   configuration. It returns the original `expectedRevision` and three explicit
   deployment/apply/readback requirements. Repeating proposal generation is
   deterministic. The helper has no network, credential, file-write or deploy path.
3. Independently review the proposal and exact immutable module/assets. Separately
   authorize its configuration deployment through the restricted workflow. Verify
   full effective configuration and existing namespace linkage after that change.
   Do not include stale configuration, unrelated changes or a new namespace.
4. Explicitly invoke apply with the apply role and original predecessor. The
   authority reads only configured policy. It preserves unexpired consumption and
   cannot shorten the deadline. Pending configuration mismatch denies new claims;
   in-flight reservations admitted before the stop may still finish.
5. Independently read status: require the successor persisted revision,
   `configured_policy_equal=true`, valid clock, and `new_allocation_state=stopped`.
   Compare count/deadline. On ambiguity read first; do not advance again or infer a
   refund. Existing identities, URLs, publisher/viewer access and exact reservation
   recovery remain intact. Resume requires another separately reviewed consecutive
   configured revision and explicit application, never a storage reset.

Ongoing emergency-stop readiness includes availability of both the bounded
configuration workflow and separately credentialed apply/read. Gateway access
alone is insufficient. Changes that widen creation require new budget approval.

## Temporary aggregate reconciliation and removal

Only the optional `src/diagnostic-index.ts` module exports
`DiagnosticReadEntrypoint` and the authenticated aggregate method on the existing
`ProvisioningAuthority` class export. It retains the same Worker name, namespace
bindings and migrations. The default permanent module contains no aggregate RPC.
This is a targeted adaptation of diagnostic candidate `342b784`, not a branch merge.

The separate temporary gateway accepts only `GET /operator/reconcile`. It requires
its own host, Access audience/operator/policy and independent
`PROVISIONING_DIAGNOSTIC_READ_KEY` supplied by the client. The target additionally
requires `PROVISIONING_DIAGNOSTIC_ENABLED=true`; direct aggregate RPC without that
distinct credential is denied before SQL. Permanent credentials authorize neither
the aggregate RPC nor its gateway. Its configuration and resources are separate.

Use aggregates before allocation-policy activation only. Once policy storage
exists, the accepted reconciliation returns `unsupported_policy` with null counts;
the adapted validator preserves this result. Never infer post-activation identity
counts from the legacy ceiling. Neither diagnostic nor policy status initializes
storage or calls target overlays. Worker configuration and legacy objects remain
outside the single-authority transaction; unclassifiable values remain unavailable.

After the separately authorized aggregate read, deny the temporary gateway
immediately, including on ambiguity. Do not retry automatically. Settle/reconcile
in-flight work. Within 24 hours remove its route, Worker, Service Binding and
credential, then restore the permanent target module using current approved
configuration. Check alternate workers.dev/preview URLs, aliases and version-pinned
bindings; remove Access routing only after the ingress is denied/removed. Never
delete either existing Durable Object namespace or its data. Permanent gateway
routes, bindings, keys and entrypoints remain. Independently verify permanent
status/application and absence of aggregate dispatch. An in-flight call or an
already exported report cannot be recalled. Existing diagnostic audit retention
target is 90 days and private aggregate-report retention 30 days; supported,
restricted custody and deletion must be established before live use.

## Local verification and immutable inventory

From the repository root:

```powershell
npm test --prefix cloud_overlay
npm run check --prefix cloud_overlay
npm run check --prefix web_overlay
npm test --prefix web_overlay
python -m unittest discover -s cloud_overlay/operator -v
npm run build --prefix cloud_overlay
npm run build:operator --prefix cloud_overlay
npm run build:diagnostic --prefix cloud_overlay
python cloud_overlay/operator/package_candidate.py
```

Every build command is Wrangler `--dry-run`. Operator tests include real local
multi-Worker named bindings, synthetic signed JWTs and intercepted outbound JWKS;
all other outbound requests fail. Test-only driver methods are excluded from every
deployment bundle. Browser tests retain their isolated local test entry.

Packaging requires clean committed source. `--allow-dirty` permits a clearly
unreviewed precommit inventory only. It copies exact permanent/temporary bundles,
all hosted assets including historical hashed assets, and complete runtime JSON
configuration templates into `dist/operator-candidate/<source-and-inventory>/`.
It excludes source maps, fixtures, private files and credential values. The
manifest records source/tree SHA, all file sizes/hashes, full checked-in target
configuration, secret names only, preservation rules and live evidence blockers.
Run `package_candidate.py --verify <package-directory>` to verify exact inventory
and content against the manifest; independently compare the manifest hash to the
reviewed value. Reproduce dry-run bundle hashes on the pinned source before use.
Do not treat the older `dist/deployment-artifact-manifest.json` as this candidate.

The complete local package is explicitly `deployment_ready=false`: actual
Production namespace IDs/linkage, Access/route settings, secret custody, effective
deployed/persisted parity and account resource evidence are unavailable locally.
Templates preserve class/binding/migration lineage, not unobserved namespace facts.
They are disabled and select no Production budget. Do not deploy them as-is.

## Compatible rollback and authorization boundary

Pin the complete permanent target/operator package and matching hosted assets.
Before rollback reconcile templates with the current approved policy revision,
credential references, authority configuration, seeds, namespace linkage and all
effective deployment settings. Never restore an old configuration alongside a
compatible binary. Stop new reservations, read back the persisted state, deploy
compatible permanent code/assets through a separately approved workflow and
verify readback/identity continuity. Keep policy data, identities, URLs, reservations,
legacy-write rejection, classes/migrations and permanent emergency access. No table
deletion, migration rewind, identity recycling, budget refund, implicit downgrade
or pre-explicit-apply binary is compatible. Missing/stale policy configuration
deliberately fails closed for new allocation while exact recovery remains.

Explicitly authorized Production administrators/deployers remain trusted: they can
replace code or bindings. Gateway code handles role credentials in memory, so its
authorized deployers are inside that trust boundary. Binding administrators cannot
use missing/wrong credentials to bypass target checks; they still must not be
treated as possessing method-level storage restrictions. Keep routine operators
separate from deployment/Access administration, resource-scope grants, independently
review changes and protect/audit deployment credentials. This local package creates
no Production permission, credential or Access policy.

Before live integration, obtain separately authorized sanitized configuration,
namespace and persisted-state/population equality; current plan/quotas and
account-wide usage/headroom; measured request/write/storage/duration/throughput
evidence; growth and spending tolerance; supported Access claims and audit custody.
Then obtain explicit authorization for deployment, diagnostic invocation,
configuration staging, policy application, Hosted Overlay acceptance and release.
