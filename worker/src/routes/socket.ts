import type { Env } from "../env";
import { RelayError } from "../lib/errors";
import { chargeLive } from "../lib/quotas";

const HEX64 = /^[0-9a-f]{64}$/;

/**
 * collar/relay-messaging: hands a WebSocket upgrade to the device's hub. The `device` parameter only routes it; the hub
 * accepts nothing until the client signs its challenge with that device's key.
 */
export async function openSocket(request: Request, env: Env): Promise<Response> {
  if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") throw new RelayError("invalid_request");
  const device = new URL(request.url).searchParams.get("device");
  if (!device || !HEX64.test(device)) throw new RelayError("invalid_request");
  // A spent live budget doesn't refuse the connection: a Sub still needs it for a safeword.
  await chargeLive(env);

  const headers = new Headers(request.headers);
  headers.set("x-hub-device", device);
  const hub = env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(device));
  return hub.fetch(new Request(request.url, { method: "GET", headers }));
}
