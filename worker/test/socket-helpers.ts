import { SELF } from "cloudflare:test";
import { b64url, BASE, deviceKeyId, genSigningKeyPair, type KeyPair, pairDevices, signedFetch, signEnvelope } from "./helpers";

/** A test-side live connection that queues every message it receives. */
export class TestSocket {
  private readonly queue: Record<string, unknown>[] = [];
  private waiters: ((m: Record<string, unknown>) => void)[] = [];
  closed: { code: number; reason: string } | null = null;
  private closeWaiters: (() => void)[] = [];

  constructor(readonly ws: WebSocket) {
    ws.addEventListener("message", (event) => {
      let message: Record<string, unknown>;
      try { message = JSON.parse(event.data as string) as Record<string, unknown>; } catch { message = { raw: event.data }; }
      const waiter = this.waiters.shift();
      if (waiter) waiter(message);
      else this.queue.push(message);
    });
    ws.addEventListener("close", (event) => {
      this.closed = { code: event.code, reason: event.reason };
      for (const w of this.closeWaiters.splice(0)) w();
    });
  }

  next(timeoutMs = 2000): Promise<Record<string, unknown>> {
    const queued = this.queue.shift();
    if (queued) return Promise.resolve(queued);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.waiters = this.waiters.filter((w) => w !== done);
        reject(new Error("no message"));
      }, timeoutMs);
      const done = (m: Record<string, unknown>) => { clearTimeout(timer); resolve(m); };
      this.waiters.push(done);
    });
  }

  /** Resolves to null when nothing arrives within `ms`. */
  async maybeNext(ms = 300): Promise<Record<string, unknown> | null> {
    try { return await this.next(ms); } catch { return null; }
  }

  send(message: unknown): void {
    this.ws.send(typeof message === "string" ? message : JSON.stringify(message));
  }

  waitClosed(timeoutMs = 2000): Promise<void> {
    if (this.closed) return Promise.resolve();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("not closed")), timeoutMs);
      this.closeWaiters.push(() => { clearTimeout(timer); resolve(); });
    });
  }

  close(): void {
    try { this.ws.close(1000, "done"); } catch { /* already closed */ }
  }
}

export async function openSocket(device: string): Promise<{ response: Response; socket: TestSocket | null }> {
  const response = await SELF.fetch(`${BASE}/v1/socket?device=${device}`, { headers: { upgrade: "websocket" } });
  if (!response.webSocket) return { response, socket: null };
  response.webSocket.accept();
  return { response, socket: new TestSocket(response.webSocket) };
}

export async function signChallenge(privateKey: CryptoKey, device: string, nonce: string): Promise<string> {
  const base = `oathbound-socket-v1\n${device}\n${nonce}`;
  return b64url(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, privateKey, new TextEncoder().encode(base)));
}

export interface Device {
  key: KeyPair;
  id: string;
}

export async function newDevice(): Promise<Device> {
  const key = await genSigningKeyPair();
  return { key, id: await deviceKeyId(key.publicKeyJwk) };
}

/** Opens and authenticates a device's live connection. */
export async function connect(device: Device): Promise<TestSocket> {
  const { socket } = await openSocket(device.id);
  if (!socket) throw new Error("no socket");
  const challenge = await socket.next();
  socket.send({ type: "auth", deviceKeyId: device.id, signature: await signChallenge(device.key.privateKey, device.id, challenge.nonce as string) });
  const ok = await socket.next();
  if (ok.type !== "auth-ok") throw new Error(`auth failed: ${JSON.stringify(ok)}`);
  return socket;
}

/** A frame with opaque random ciphertext; the relay never reads it. */
export function frame(pairIdHash: string, pairEpoch: number, from: "owner" | "sub", seq: number, extra: Record<string, unknown> = {}) {
  return {
    type: "send",
    id: `m${seq}`,
    safety: false,
    pairIdHash,
    pairEpoch,
    from,
    seq,
    sentAt: Math.floor(Date.now() / 1000),
    nonce: b64url(crypto.getRandomValues(new Uint8Array(12))),
    ciphertext: b64url(crypto.getRandomValues(new Uint8Array(40))),
    ...extra,
  };
}

export async function revoke(pairIdHash: string, pairEpoch: number, by: Device, sequence = 1) {
  const now = Math.floor(Date.now() / 1000);
  const unsigned = {
    type: "revocation", schemaVersion: 1, pairIdHash, pairEpoch, sequence, reason: "unpair",
    issuedByDeviceKeyId: by.id, createdAt: now, expiresAt: now + 604800,
  };
  const signature = await signEnvelope(by.key.privateKey, unsigned);
  return signedFetch("/v1/revocations", "POST", { ...unsigned, signature }, by.key.privateKey, by.id);
}

export interface LivePair {
  owner: Device;
  sub: Device;
  pairIdHash: string;
}

/** A paired Owner and Sub (epoch 0), both with device keys on file. */
export async function livePair(): Promise<LivePair> {
  const owner = await newDevice();
  const sub = await newDevice();
  const { pair } = await pairDevices(owner.key, owner.id, sub.key, sub.id, "owner");
  return { owner, sub, pairIdHash: pair.pairIdHash as string };
}
