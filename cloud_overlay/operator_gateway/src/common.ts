import { verifyAccess, type AccessPolicy } from "./access";

export interface GatewaySettings {
  OPERATOR_ENABLED?: string;
  OPERATOR_HOST?: string;
  ACCESS_ISSUER?: string;
  // Operator-controlled release interlock, NOT an attestation from a JWT or
  // request header. Leave unset until effective Access configuration AND
  // renewal behavior establish independent MFA on every issuance, <=15m tokens.
  ACCESS_AUTHENTICATION_PROFILE?: string;
}
export const reply = (status: number, body: unknown) => Response.json(body, { status, headers: {
  "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer"
} });

export async function once<T>(operation: Promise<T>, milliseconds = 10_000): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([operation, new Promise<never>((_, reject) => {
      timer = setTimeout(() => reject(new Error("outcome_unknown")), milliseconds);
    })]);
  } finally { if (timer !== undefined) clearTimeout(timer); }
}

export async function authenticate(request: Request, env: GatewaySettings, policy: Omit<AccessPolicy, "issuer">): Promise<string> {
  const url = new URL(request.url);
  if (env.OPERATOR_ENABLED !== "true" || env.ACCESS_AUTHENTICATION_PROFILE !== "independent-mfa-every-login-15m" ||
      !env.OPERATOR_HOST || url.protocol !== "https:" ||
      url.host !== env.OPERATOR_HOST || url.username || url.password || url.search || url.hash ||
      ["Cookie", "Origin", "Upgrade", "Content-Encoding"].some(k => request.headers.has(k))) throw new Error("denied");
  const credential = /^Bearer ([0-9a-f]{64})$/.exec(request.headers.get("Authorization") ?? "")?.[1];
  const jwt = request.headers.get("Cf-Access-Jwt-Assertion");
  if (!credential || !jwt) throw new Error("denied");
  await once(verifyAccess(jwt, { issuer: env.ACCESS_ISSUER ?? "", ...policy }), 3500);
  return credential;
}
