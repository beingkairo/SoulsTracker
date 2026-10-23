// Native Node/Web Crypto checks for the actual gateway modules. No dependencies,
// Workers process, live credentials, network, or persistent test data are used.
import { test, beforeEach, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { stripTypeScriptTypes } from 'node:module';
import { generateKeyPairSync, sign } from 'node:crypto';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const modules = new Map();
function moduleURL(path) {
  if (modules.has(path)) return modules.get(path);
  const source = stripTypeScriptTypes(readFileSync(path, 'utf8')).replace(
    /from\s+["']([^"']+)["']/g, (_, specifier) => {
      assert.ok(specifier.startsWith('.'));
      return `from ${JSON.stringify(moduleURL(resolve(dirname(path), specifier + '.ts')))}`;
    });
  const url = `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;
  modules.set(path, url);
  return url;
}
const { verifyAccess } = await import(moduleURL(resolve(root, 'operator_gateway/src/access.ts')));
const { default: gateway } = await import(moduleURL(resolve(root, 'operator_gateway/src/index.ts')));
const pair = generateKeyPairSync('rsa', { modulusLength: 2048 });
const otherPair = generateKeyPairSync('rsa', { modulusLength: 2048 });
const jwk = { ...pair.publicKey.export({ format: 'jwk' }), kid: 'synthetic', alg: 'RS256', use: 'sig' };
const issuer = 'https://synthetic.cloudflareaccess.com', operator = 'owner@example.invalid';
const readKey = 'a'.repeat(64), applyKey = 'b'.repeat(64);
const expected = { issuer, operator, audience: 'read' };
const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
function jwt(audience = 'read', overrides = {}, key = pair.privateKey, header = {}) {
  const now = Math.floor(Date.now() / 1000);
  const claims = { iss: issuer, aud: [audience], email: operator, sub: 'synthetic-owner',
    type: 'app', nbf: now - 1, iat: now - 1, exp: now + 300, ...overrides };
  const input = `${encode({ alg: 'RS256', kid: 'synthetic', typ: 'JWT', ...header })}.${encode(claims)}`;
  return `${input}.${sign('RSA-SHA256', Buffer.from(input), key).toString('base64url')}`;
}
const realFetch = globalThis.fetch;
beforeEach(() => {
  globalThis.fetch = async (url, options) => {
    assert.equal(url, `${issuer}/cdn-cgi/access/certs`);
    assert.equal(options.redirect, 'manual');
    return new Response(JSON.stringify({ keys: [jwk] }));
  };
});
after(() => { globalThis.fetch = realFetch; });

test('documented identity app JWT verifies with none of policy/amr/auth_time', async () => {
  await verifyAccess(jwt(), expected);
  await verifyAccess(jwt('apply'), { ...expected, audience: 'apply' });
});

for (const [name, overrides] of [
  ['organization token', { type: 'org' }], ['missing token type', { type: undefined }],
  ['wrong issuer', { iss: issuer + '/other' }], ['wrong role', { aud: ['apply'] }],
  ['multi-application audience', { aud: ['read', 'apply'] }],
  ['repeated audience', { aud: ['read', 'read'] }], ['missing audience', { aud: undefined }],
  ['wrong owner', { email: 'other@example.invalid' }], ['missing owner', { email: undefined }],
  ['service identity', { sub: '' }], ['missing subject', { sub: undefined }],
  ['expired token', { exp: 1 }], ['future not-before', { nbf: 9999999999 }],
  ['future issuance', { iat: 9999999999 }], ['overlong token', { iat: 1 }],
  ['noninteger issuance', { iat: '1' }], ['negative issuance', { iat: -1 }],
  ['missing expiration', { exp: undefined }], ['missing not-before', { nbf: undefined }]
]) test(`rejects ${name}`, async () => {
  await assert.rejects(verifyAccess(jwt('read', overrides), expected));
});

test('900-second token limit is an issuance limit, never an MFA attestation', async () => {
  const seconds = Math.floor(Date.now() / 1000);
  await verifyAccess(jwt('read', { iat: seconds, exp: seconds + 900 }), expected, seconds * 1000);
  await assert.rejects(verifyAccess(jwt('read', { iat: seconds, exp: seconds + 901 }), expected, seconds * 1000));
  // These optional claims cannot replace verified Access enforcement. The
  // gateway interlock remains required even when a JWT asserts fresh MFA.
  await verifyAccess(jwt('read', { policy: 'untrusted-expectation', amr: ['mfa'], auth_time: seconds }), expected);
});
test('rejects forged, altered, wrong-algorithm and duplicate-field tokens', async () => {
  await assert.rejects(verifyAccess(jwt('read', {}, otherPair.privateKey), expected));
  const parts = jwt().split('.');
  const payload = Buffer.from(parts[1], 'base64url').toString();
  const duplicate = Buffer.from(payload.replace('"type":"app"', '"type":"app","type":"app"')).toString('base64url');
  await assert.rejects(verifyAccess(`${parts[0]}.${duplicate}.${parts[2]}`, expected));
  await assert.rejects(verifyAccess(jwt('read', {}, pair.privateKey, { alg: 'none' }), expected));
  await assert.rejects(verifyAccess(jwt() + '=', expected));
});
for (const [name, response] of [
  ['duplicate keys', () => new Response(JSON.stringify({ keys: [jwk, jwk] }))],
  ['missing signing key', () => new Response(JSON.stringify({ keys: [] }))],
  ['redirect', () => new Response(null, { status: 302 })],
  ['oversized certificates', () => new Response(' '.repeat(32769))],
  ['certificate outage', () => { throw new Error('synthetic outage'); }]
]) test(`fails closed on ${name}`, async () => {
  globalThis.fetch = async () => response();
  await assert.rejects(verifyAccess(jwt(), expected));
});

let readCalls = 0, applyCalls = 0;
function env(overrides = {}) {
  readCalls = applyCalls = 0;
  return { OPERATOR_ENABLED: 'true', OPERATOR_HOST: 'operator.example.invalid', ACCESS_ISSUER: issuer,
    ACCESS_AUTHENTICATION_PROFILE: 'independent-mfa-every-login-15m',
    ACCESS_READ_AUDIENCE: 'read', ACCESS_APPLY_AUDIENCE: 'apply',
    ACCESS_READ_OPERATOR: operator, ACCESS_APPLY_OPERATOR: operator,
    POLICY_READ: { status: async () => { readCalls++; return { ok: false, error: 'read_unavailable' }; } },
    POLICY_APPLY: { apply: async () => { applyCalls++; return { ok: true, status: 'applied', revision: 1 }; } },
    ...overrides };
}
function request(role = 'read', token = jwt(role), headers = {}) {
  return new Request(`https://operator.example.invalid/operator/policy/${role === 'read' ? 'status' : 'apply'}`, {
    method: role === 'read' ? 'GET' : 'POST',
    headers: { Authorization: `Bearer ${role === 'read' ? readKey : applyKey}`,
      'Cf-Access-Jwt-Assertion': token, ...(role === 'apply' ? { 'Content-Type': 'application/json' } : {}), ...headers },
    ...(role === 'apply' ? { body: '{"expectedRevision":0}' } : {})
  });
}
test('separate application tokens reach only their configured role RPC', async () => {
  const settings = env();
  assert.equal((await gateway.fetch(request(), settings)).status, 503);
  assert.equal((await gateway.fetch(request('apply'), settings)).status, 200);
  assert.equal(readCalls, 1); assert.equal(applyCalls, 1);
});
test('rejects crossed application tokens in both directions before any RPC', async () => {
  const settings = env();
  assert.equal((await gateway.fetch(request('apply', jwt('read')), settings)).status, 403);
  assert.equal((await gateway.fetch(request('read', jwt('apply')), settings)).status, 403);
  assert.equal(readCalls + applyCalls, 0);
});
test('absent, invalid or disabled security interlock denies even a signed app JWT', async () => {
  for (const config of [
    { ACCESS_AUTHENTICATION_PROFILE: undefined }, { ACCESS_AUTHENTICATION_PROFILE: 'unverified' },
    { OPERATOR_ENABLED: 'false' }, { ACCESS_APPLY_AUDIENCE: 'read' },
    { ACCESS_READ_AUDIENCE: undefined }, { ACCESS_READ_OPERATOR: undefined }
  ]) {
    const settings = env(config);
    for (const role of ['read', 'apply']) {
      // Missing Read owner affects only Read; every other fixture denies both.
      if (config.ACCESS_READ_OPERATOR === undefined && Object.hasOwn(config, 'ACCESS_READ_OPERATOR') && role === 'apply') continue;
      assert.equal((await gateway.fetch(request(role), settings)).status, 403);
    }
    assert.equal(readCalls + applyCalls, 0);
  }
});
test('spoofed headers cannot set the interlock, owner or replace a verified assertion', async () => {
  const settings = env({ ACCESS_AUTHENTICATION_PROFILE: undefined });
  assert.equal((await gateway.fetch(request('read', jwt(), {
    'ACCESS_AUTHENTICATION_PROFILE': 'independent-mfa-every-login-15m',
    'Cf-Access-Authenticated-User-Email': operator
  }), settings)).status, 403);
  const configured = env();
  for (const token of ['', 'forged', jwt('read', { email: 'other@example.invalid' })]) {
    assert.equal((await gateway.fetch(request('read', token, {
      'Cf-Access-Authenticated-User-Email': operator
    }), configured)).status, 403);
  }
  for (const header of ['Cookie', 'Origin']) {
    assert.equal((await gateway.fetch(request('read', jwt(), { [header]: 'synthetic' }), configured)).status, 403);
  }
  assert.equal(readCalls + applyCalls, 0);
});
