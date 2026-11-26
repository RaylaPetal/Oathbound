import { env } from "cloudflare:test";
import { afterEach, describe, expect, it } from "vitest";
import { signedFetch } from "./helpers";
import { spreadOrigins } from "./lfp-helpers";
import { connect, newDevice, type Device, type TestSocket } from "./socket-helpers";

spreadOrigins();
const open: TestSocket[] = [];
afterEach(() => { for (const s of open.splice(0)) s.close(); });

const hello = (d: Device) =>
  signedFetch("/v1/presence/hello", "POST", { type: "presence-hello", schemaVersion: 1, devicePublicKey: d.key.publicKeyJwk }, d.key.privateKey, d.id);

async function online(): Promise<{ device: Device; socket: TestSocket; first: Record<string, unknown> }> {
  const device = await newDevice();
  expect((await hello(device)).status).toBe(200);
  const socket = await connect(device);
  open.push(socket);
  return { device, socket, first: socket.presence! };
}

/** The count is cached for a minute; tests age it out to see a fresh one. */
const expireCache = () => env.RELAY_DB.prepare(`UPDATE relay_presence SET computed_at = computed_at - 3600`).run();

describe("presence", () => {
  it("lets a device with no pairing connect and tells it how many are online", async () => {
    const a = await online();
    expect(a.first).toMatchObject({ type: "presence", online: 1 });
    await expireCache();
    const b = await online();
    expect(b.first).toMatchObject({ type: "presence", online: 2 });
    expect(JSON.stringify(b.first)).not.toContain(a.device.id);
  });

  it("drops a device once its connection closes, and keeps one whose connection was replaced", async () => {
    const until = (d: Device) =>
      env.RELAY_DB.prepare(`SELECT online_until FROM device_keys WHERE device_key_id = ?1`).bind(d.id).first<{ online_until: number | null }>();
    const a = await online();
    const b = await online();
    b.socket.close();
    await new Promise((r) => setTimeout(r, 100));
    expect((await until(b.device))?.online_until).toBeNull();
    expect((await until(a.device))?.online_until).toBeTypeOf("number");

    const again = await connect(a.device);
    open.push(again);
    await new Promise((r) => setTimeout(r, 100));
    expect((await until(a.device))?.online_until).toBeTypeOf("number");
  });

  it("ignores refreshes that come too fast", async () => {
    const a = await online();
    a.socket.send({ type: "presence" });
    expect(await a.socket.maybeNext(300)).toBeNull();
  });

  it("stops counting a device whose expiry ran out without a close", async () => {
    const a = await online();
    await env.RELAY_DB.prepare(`UPDATE device_keys SET online_until = unixepoch() - 1 WHERE device_key_id = ?1`).bind(a.device.id).run();
    await expireCache();
    expect((await online()).first).toMatchObject({ online: 1 });
  });

  it("hello refuses a key that doesn't match the signer", async () => {
    const a = await newDevice();
    const other = await newDevice();
    const forged = await signedFetch("/v1/presence/hello", "POST", { type: "presence-hello", schemaVersion: 1, devicePublicKey: other.key.publicKeyJwk }, a.key.privateKey, a.id);
    expect(forged.status).toBe(401);
  });
});
