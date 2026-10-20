import { validateHostedJsonTokens } from "../../../web_overlay/src/hosted-contracts";

export interface AccessPolicy { issuer: string; audience: string; operator: string; policy: string }

const decode = (part: string): Uint8Array => {
  if (!/^[A-Za-z0-9_-]+$/.test(part) || part.length > 8192) throw new Error("invalid");
  const binary = atob(part.replace(/-/g, "+").replace(/_/g, "/"));
  const canonical = btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  if (canonical !== part) throw new Error("invalid");
  return Uint8Array.from(binary, c => c.charCodeAt(0));
};
const object = (bytes: Uint8Array): Record<string, unknown> => {
  const text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  validateHostedJsonTokens(text);
  const value: unknown = JSON.parse(text);
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("invalid");
  return value as Record<string, unknown>;
};

// Adapted from the separately reviewed temporary diagnostic verifier. No Access
// header is trusted without signature and role-specific claim verification.
export async function verifyAccess(jwt: string, expected: AccessPolicy, now = Date.now()): Promise<void> {
  const { issuer, audience, operator, policy } = expected;
  if (!/^https:\/\/[a-z0-9-]+\.cloudflareaccess\.com$/.test(issuer) ||
      !audience || !operator || !policy || !Number.isSafeInteger(now) || now < 0) throw new Error("invalid");
  const parts = jwt.split(".");
  if (jwt.length > 16384 || parts.length !== 3 || parts.some(p => !p)) throw new Error("invalid");
  const h = object(decode(parts[0])), p = object(decode(parts[1]));
  const audienceMatches = p.aud === audience || (Array.isArray(p.aud) && p.aud.length === 1 && p.aud[0] === audience);
  if (Object.keys(h).some(k => !["alg", "kid", "typ"].includes(k)) || h.alg !== "RS256" ||
      (h.typ !== undefined && h.typ !== "JWT") || typeof h.kid !== "string" || !/^[\w-]{1,128}$/.test(h.kid) ||
      p.iss !== issuer || !audienceMatches || p.email !== operator || typeof p.sub !== "string" || !p.sub ||
      p.policy !== policy || !Array.isArray(p.amr) || !p.amr.includes("mfa") ||
      ![p.exp, p.nbf, p.iat, p.auth_time].every(v => typeof v === "number" && Number.isSafeInteger(v) && v >= 0) ||
      (p.nbf as number) > now / 1000 || (p.iat as number) > now / 1000 || (p.exp as number) <= now / 1000 ||
      (p.auth_time as number) > (p.iat as number) || (p.iat as number) >= (p.exp as number) ||
      (p.nbf as number) >= (p.exp as number) || (p.exp as number) - (p.auth_time as number) > 900 ||
      now / 1000 - (p.auth_time as number) > 900) throw new Error("invalid");
  const response = await fetch(`${issuer}/cdn-cgi/access/certs`, { cache: "no-store", redirect: "manual",
    signal: AbortSignal.timeout(3000) });
  if (!response.ok || !response.body) throw new Error("unavailable");
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = []; let size = 0;
  try {
    for (;;) {
      const next = await reader.read(); if (next.done) break;
      size += next.value.length;
      if (size > 32768) { await reader.cancel(); throw new Error("invalid"); }
      chunks.push(next.value);
    }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(size); let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  const jwks = object(bytes);
  if (!Array.isArray(jwks.keys)) throw new Error("invalid");
  const keys = jwks.keys.filter(key => key && typeof key === "object" && key.kid === h.kid &&
    key.kty === "RSA" && key.alg === "RS256" && key.use === "sig");
  if (keys.length !== 1) throw new Error("invalid");
  const key = await crypto.subtle.importKey("jwk", keys[0] as JsonWebKey,
    { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["verify"]);
  if (!await crypto.subtle.verify("RSASSA-PKCS1-v1_5", key, decode(parts[2]),
    new TextEncoder().encode(`${parts[0]}.${parts[1]}`))) throw new Error("invalid");
}
