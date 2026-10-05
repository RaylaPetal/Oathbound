import { describe, expect, it } from "vitest";
import { env } from "cloudflare:test";
import { enforceQuota } from "../src/lib/quotas";
import { createInvitation, deviceKeyId, genSigningKeyPair } from "./helpers";

async function counter(scope: string, windowSeconds: number) {
  const bucket = Math.floor(Date.now() / 1000 / windowSeconds) * windowSeconds;
  return (await env.RELAY_DB.prepare("SELECT count FROM quota_counters WHERE scope = ?1 AND window_start = ?2").bind(scope, bucket).first<{ count: number }>())?.count;
}

describe("per-device quota enforcement", () => {
  it("trips after the configured number of invitation creations in the window and reports Retry-After", async () => {
    const inviter = await genSigningKeyPair();
    const inviterId = await deviceKeyId(inviter.publicKeyJwk);

    const statuses: number[] = [];
    let limitedResponse: Awaited<ReturnType<typeof createInvitation>> | null = null;
    for (let i = 0; i < 11; i++) {
      const r = await createInvitation(inviter, inviterId);
      statuses.push(r.status);
      if (r.status === 429) limitedResponse = r;
    }

    expect(statuses.filter((s) => s === 200)).toHaveLength(10);
    expect(statuses.filter((s) => s === 429)).toHaveLength(1);
    expect(limitedResponse?.headers.get("retry-after")).toBeTruthy();
    const bucket = Math.floor(Date.now() / 1000 / 3600) * 3600;
    const counter = await env.RELAY_DB.prepare(
      "SELECT count FROM quota_counters WHERE scope = ?1 AND window_start = ?2",
    ).bind(`deviceInvitationCreate:${inviterId}`, bucket).first<{ count: number }>();
    expect(counter?.count).toBe(10);
  });

  it("charges multi-unit amounts and rejects one that would overflow without changing the counter", async () => {
    // deviceInvitationCreate holds 10 units per hour.
    await enforceQuota(env, "deviceInvitationCreate", "units-test", 0, 6);
    await expect(enforceQuota(env, "deviceInvitationCreate", "units-test", 0, 6)).rejects.toMatchObject({ code: "rate_limited" });
    expect(await counter("deviceInvitationCreate:units-test", 3600)).toBe(6);
    await enforceQuota(env, "deviceInvitationCreate", "units-test", 0, 4);
    expect(await counter("deviceInvitationCreate:units-test", 3600)).toBe(10);
    await expect(enforceQuota(env, "deviceInvitationCreate", "units-test", 0, 11)).rejects.toMatchObject({ code: "rate_limited" });
  });
});
