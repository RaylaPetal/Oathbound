import type { Env } from "../env";
import { READ_ONLY, resolverFromStoredOrBodyKey, verifySignedRequest } from "../lib/auth";
import { rememberDeviceKey } from "../lib/deviceKeys";
import { RelayError } from "../lib/errors";
import { asRecord } from "../lib/validate";

/**
 * Once per install the relay has never seen (no pairing, no Looking for pair profile): puts its key on file so its live
 * connection can sign in and count as online. Read-only signing: a replay only re-remembers the same key.
 */
export async function presenceHello(request: Request, env: Env): Promise<Response> {
  const { publicKeyJwk, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredOrBodyKey(env), READ_ONLY);
  const body = asRecord(bodyJson);
  if (body.type !== "presence-hello" || body.schemaVersion !== 1) throw new RelayError("invalid_request");
  await rememberDeviceKey(env, publicKeyJwk);
  return Response.json({ type: "presence-hello-result", schemaVersion: 1 });
}
