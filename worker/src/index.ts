import type { Env } from "./env";
import { RelayError } from "./lib/errors";
import { logEvent } from "./lib/log";
import { acceptInvitation, cancelInvitation, consumeInvitation, createInvitation, fetchInvitation } from "./routes/invitations";
import { deleteBackup, fetchBackup, putBackup } from "./routes/backups";
import { fetchPair, putCollarStatus } from "./routes/pairs";
import { checkRevocations, publishRevocation } from "./routes/revocations";
import { consumeCatalogResponse, createCatalogRequest, fetchCatalogRequest, uploadCatalogResponse } from "./routes/catalog";
import { consumeMailboxSnapshot, fetchMailboxKey, mailboxStatus, publishMailboxKey, uploadMailboxSnapshot } from "./routes/mailbox";
import { consumeRulebookItem, fetchRulebookKey, publishRulebookKey, uploadRulebookItem } from "./routes/rulebook";
import { health } from "./routes/health";
import { runScheduledCleanup } from "./scheduled";
import { enforceQuota } from "./lib/quotas";
import { originScope } from "./lib/origin";

/** Matches `simple.period` of the ORIGIN_RATE_LIMITER binding in wrangler.toml. */
const ORIGIN_RATE_LIMIT_PERIOD_SECONDS = 60;

async function route(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const method = request.method.toUpperCase();
  const segments = url.pathname.split("/").filter(Boolean);

  if (segments[0] !== "v1") return new RelayError("not_found").toResponse();

  // Account-level budget guard. Safety traffic has an independent reserve so scans/pairing abuse cannot
  // consume its allowance.
  const safetyRoute = segments[1] === "revocations";
  await enforceQuota(env, safetyRoute ? "globalDailySafety" : "globalDailyWork", "all");
  // Applied before authentication/body parsing so malformed and oversized anonymous traffic is bounded
  // too. The key is a one-way hash; raw client IPs are never retained. Per Cloudflare location, not global.
  const origin = await env.ORIGIN_RATE_LIMITER.limit({ key: await originScope(request) });
  if (!origin.success) throw new RelayError("rate_limited", ORIGIN_RATE_LIMIT_PERIOD_SECONDS);

  if (method === "GET" && segments.length === 2 && segments[1] === "health") {
    return health(env);
  }

  if (segments[1] === "invitations") {
    if (method === "POST" && segments.length === 2) return createInvitation(request, env);
    if (method === "GET" && segments.length === 3) return fetchInvitation(request, env, segments[2]!);
    if (method === "POST" && segments.length === 4 && segments[3] === "accept") return acceptInvitation(request, env, segments[2]!);
    if (method === "POST" && segments.length === 4 && segments[3] === "consume") return consumeInvitation(request, env, segments[2]!);
    if (method === "POST" && segments.length === 4 && segments[3] === "cancel") return cancelInvitation(request, env, segments[2]!);
  }

  if (segments[1] === "backups" && segments.length === 3) {
    if (method === "PUT") return putBackup(request, env, segments[2]!);
    if (method === "GET") return fetchBackup(request, env, segments[2]!);
    if (method === "DELETE") return deleteBackup(request, env, segments[2]!);
  }

  if (segments[1] === "pairs") {
    if (method === "GET" && segments.length === 3) return fetchPair(request, env, segments[2]!);
    if (method === "POST" && segments.length === 4 && segments[3] === "collar-status") return putCollarStatus(request, env, segments[2]!);
  }

  if (segments[1] === "revocations") {
    if (method === "POST" && segments.length === 2) return publishRevocation(request, env);
    if (method === "GET" && segments.length === 3) return checkRevocations(request, env, segments[2]!);
  }

  if (segments[1] === "catalog" && segments[2] === "requests") {
    if (method === "POST" && segments.length === 3) return createCatalogRequest(request, env);
    if (method === "GET" && segments.length === 4) return fetchCatalogRequest(request, env, segments[3]!);
    if (method === "POST" && segments.length === 5 && segments[4] === "upload") return uploadCatalogResponse(request, env, segments[3]!);
    if (method === "POST" && segments.length === 5 && segments[4] === "consume") return consumeCatalogResponse(request, env, segments[3]!);
  }

  if (segments[1] === "catalog" && segments[2] === "mailbox" && method === "POST") {
    const action = segments.slice(3).join("/");
    if (action === "key") return publishMailboxKey(request, env);
    if (action === "key/fetch") return fetchMailboxKey(request, env);
    if (action === "upload") return uploadMailboxSnapshot(request, env);
    if (action === "status") return mailboxStatus(request, env);
    if (action === "consume") return consumeMailboxSnapshot(request, env);
  }

  if (segments[1] === "rulebook" && method === "POST") {
    const action = segments.slice(2).join("/");
    if (action === "key") return publishRulebookKey(request, env);
    if (action === "key/fetch") return fetchRulebookKey(request, env);
    if (action === "upload") return uploadRulebookItem(request, env);
    if (action === "consume") return consumeRulebookItem(request, env);
  }

  return new RelayError("not_found").toResponse();
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      return await route(request, env);
    } catch (error) {
      if (error instanceof RelayError) return error.toResponse();
      logEvent("unhandled_error", { message: error instanceof Error ? error.message : "unknown" });
      return new RelayError("invalid_request").toResponse();
    }
  },

  async scheduled(_controller: ScheduledController, env: Env): Promise<void> {
    await runScheduledCleanup(env);
  },
};
