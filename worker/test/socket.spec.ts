import { env, runInDurableObject } from "cloudflare:test";
import { afterEach, describe, expect, it } from "vitest";
import type { DeviceHub } from "../src/hub/DeviceHub";
import { relayFetch } from "./helpers";
import { connect, livePair, newDevice, openSocket, signChallenge, type TestSocket } from "./socket-helpers";

/** Moves every socket's open time on the device's hub into the past, as if its auth deadline had passed. */
async function backdatePending(device: string): Promise<void> {
  const stub = env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(device));
  await runInDurableObject(stub, (_hub: DeviceHub, state) => {
    for (const ws of state.getWebSockets()) {
      const a = ws.deserializeAttachment() as { state: string; openedAt: number };
      if (a.state === "pending") ws.serializeAttachment({ ...a, openedAt: a.openedAt - 60 });
    }
  });
}

const open: TestSocket[] = [];
const track = (s: TestSocket | null) => { if (s) open.push(s); return s; };
afterEach(() => { for (const s of open.splice(0)) s.close(); });

describe("live connection", () => {
  it("refuses a request that isn't a WebSocket upgrade", async () => {
    const r = await relayFetch(`/v1/socket?device=${"a".repeat(64)}`);
    expect(r.status).toBe(400);
    expect((await r.json() as { code: string }).code).toBe("invalid_request");
  });

  it("refuses a malformed device id", async () => {
    const { response } = await openSocket("not-a-device");
    expect(response.status).toBe(400);
  });

  it("sends a challenge and accepts a valid answer", async () => {
    const { owner } = await livePair();
    const s = track(await connect(owner))!;
    expect(s.closed).toBeNull();
  });

  it("closes the connection on a signature from the wrong key", async () => {
    const { owner } = await livePair();
    const other = await newDevice();
    const s = track((await openSocket(owner.id)).socket)!;
    const challenge = await s.next();
    expect(challenge.type).toBe("challenge");
    expect(typeof challenge.serverTime).toBe("number");
    s.send({ type: "auth", deviceKeyId: owner.id, signature: await signChallenge(other.key.privateKey, owner.id, challenge.nonce as string) });
    expect((await s.next()).code).toBe("unauthorized");
    await s.waitClosed();
  });

  it("closes the connection when a device without a key on file answers", async () => {
    const stranger = await newDevice();
    const s = track((await openSocket(stranger.id)).socket)!;
    const challenge = await s.next();
    s.send({ type: "auth", deviceKeyId: stranger.id, signature: await signChallenge(stranger.key.privateKey, stranger.id, challenge.nonce as string) });
    expect((await s.next()).code).toBe("unauthorized");
    await s.waitClosed();
  });

  it("accepts nothing but auth before the challenge is answered", async () => {
    const { owner } = await livePair();
    const s = track((await openSocket(owner.id)).socket)!;
    await s.next();
    s.send({ type: "send", id: "m1" });
    expect((await s.next()).code).toBe("unauthorized");
    await s.waitClosed();
  });

  it("closes the older connection when the same device connects again", async () => {
    const { owner } = await livePair();
    const first = track(await connect(owner))!;
    track(await connect(owner));
    expect((await first.next()).code).toBe("replaced");
    await first.waitClosed();
  });

  it("answers the ping keepalive", async () => {
    const { owner } = await livePair();
    const s = track(await connect(owner))!;
    s.ws.send("ping");
    expect((await s.next()).raw).toBe("pong");
  });

  it("closes a connection that doesn't answer the challenge in time once the hub next runs", async () => {
    const { owner } = await livePair();
    const s = track((await openSocket(owner.id)).socket)!;
    await s.next();
    await backdatePending(owner.id);
    track(await connect(owner));
    expect((await s.next()).code).toBe("unauthorized");
    await s.waitClosed();
  });

  it("refuses an auth that arrives after the deadline", async () => {
    const { owner } = await livePair();
    const s = track((await openSocket(owner.id)).socket)!;
    const challenge = await s.next();
    await backdatePending(owner.id);
    s.send({ type: "auth", deviceKeyId: owner.id, signature: await signChallenge(owner.key.privateKey, owner.id, challenge.nonce as string) });
    expect((await s.next()).code).toBe("unauthorized");
    await s.waitClosed();
  });

  it("sets no alarm for a normal connection, so an idle hub can hibernate", async () => {
    const { owner } = await livePair();
    track(await connect(owner));
    const stub = env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(owner.id));
    expect(await runInDurableObject(stub, (_hub: DeviceHub, state) => state.storage.getAlarm())).toBeNull();
  });
});
