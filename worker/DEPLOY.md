# Deploying the Oathbound Relay

This deploys the fixed Oathbound relay endpoint used by released plugin builds. Users cannot configure or
redirect the relay URL; changing environments requires updating `RelayClient.RelayOrigin` in a reviewed
plugin release.

## Prerequisites

- A Cloudflare account (free tier is enough to start).
- Node.js (already required to have gotten this far - see `worker/package.json`).
- Run everything below from the `worker/` directory.

```bash
cd worker
npm install
```

## 1. Log in to Cloudflare

```bash
npx wrangler login
```

This opens a browser to authorize Wrangler (the CLI) against your Cloudflare account. No manual API token
needed for this path.

## 2. Create the D1 database and R2 bucket

Pick one environment to start with — `staging` is the lower-stakes choice; you can repeat this later for
`production` with different resource names.

```bash
npx wrangler d1 create oathbound-relay-staging
npx wrangler r2 bucket create oathbound-relay-catalog-staging
```

The `d1 create` command prints a `database_id` (a UUID). Copy it.

## 3. Fill in `wrangler.toml`

Open `worker/wrangler.toml` and replace the placeholder for the environment you're deploying:

```toml
[[env.staging.d1_databases]]
binding = "RELAY_DB"
database_name = "oathbound-relay-staging"
database_id = "REPLACE_WITH_STAGING_D1_ID"   # <-- paste the UUID from step 2 here
migrations_dir = "migrations"
```

The R2 bucket name only needs to match what you created (`bucket_name = "oathbound-relay-catalog-staging"`
is already correct if you used the command above verbatim).

## 4. Apply migrations to the real (remote) database

```bash
npx wrangler d1 migrations apply RELAY_DB --env staging --remote
```

(Without `--remote` this would target a local emulated database instead — fine for development, not what
you want here.)

## 5. Deploy

```bash
npm run deploy:staging
```

Wrangler prints the deployed URL at the end, something like:

```
https://oathbound-relay-staging.<your-cloudflare-subdomain>.workers.dev
```

The staging deployment used by current builds must be exactly:

```
https://oathbound-relay-staging.oathbound.workers.dev
```

If Cloudflare assigns a different hostname, update the Worker route or the pinned plugin origin in a new
release; do not expose an editable URL to users.

## 6. Verify it's alive

```bash
curl https://oathbound-relay-staging.<your-subdomain>.workers.dev/v1/health
```

Should return `{"status":"ok",...}`. If you get an error here, pairing won't work either — fix this first.

## Notes

- **Free tier limits**: The Worker reserves 25,000 non-safety requests/day and 10,000 revocation
  requests/day. That leaves headroom below Workers Free's 100,000 requests/day and D1 Free's 100,000
  rows-written/day for nonce, lifecycle, index, and cleanup writes. Rejected quota traffic uses saturating
  counters and does not keep incrementing them. If you ever see `service_unavailable` responses, check
  `CIRCUIT_BREAKER_FORCE_OPEN` in `wrangler.toml` (should be `"false"`) and the `/v1/health` endpoint's
  `globalDailyWorkUsage`/`globalDailyWorkCeiling` fields.
- **Redeploying after a code change**: just re-run `npm run deploy:staging` — no migration step needed
  unless `migrations/` gained a new file.
- **Production**: repeat steps 2-5 with `--env production` and `oathbound-relay-production`-named
  resources once you're ready to stop using staging.
- **Costs**: this is designed to run entirely within Cloudflare's free tier for normal personal use. Nothing
  here requires a paid plan.

## Operations and alarms

- Check `/v1/health` after every deploy. Alert when it is unavailable for five minutes, when rejected work
  approaches the configured global ceiling, or when D1/R2 operations approach 80% of the account quota.
- Review aggregate `rate_limited`, `service_unavailable`, signature-failure, cleanup-count, and latency
  metrics. Never enable request-body, authorization-header, capability, public-key, character, or catalog
  logging while debugging.
- Keep `CIRCUIT_BREAKER_FORCE_OPEN=true` as the emergency cost/safety control. It rejects new invitations
  and catalog work while already-written safety revocations remain retrievable where possible.
- The scheduled trigger runs cleanup every 15 minutes. Investigate if expired-object counts continually
  rise or R2 usage does not fall after cleanup.

## Promotion and rollback

1. Apply new migrations to staging and deploy staging.
2. Run `npm run typecheck`, `npm run lint`, and `npm test`, then exercise the two-client staging matrix.
3. Apply the same migrations to production before deploying the exact tested Worker commit.
4. Change the plugin's pinned origin only in a reviewed release. Never silently redirect an existing
   hostname to an incompatible protocol.
5. To roll back, open the circuit breaker, deploy the last compatible Worker, and retain old schema columns.
   D1 migrations are forward-only; destructive column removal requires a separately reviewed migration.

## Incident and key rotation

The Worker currently relies on Cloudflare TLS and client device signatures; it owns no key capable of
decrypting catalogs. If the account or deployment is compromised, open the circuit breaker, revoke active
Cloudflare credentials, inspect only redacted aggregate telemetry, rotate Wrangler/API credentials, and
redeploy from a clean checkout. Ship a plugin update if the pinned hostname or protocol trust boundary must
change. Device-key compromise is handled by the plugin's confirmed identity reset and fresh pairing flow.

## Taking down a Looking for pair profile

There is no in-plugin reporting. When someone reports a profile (by its profile code), remove it by hand. Use the
environment you're acting on (`--env staging` shown; `--local --env local` to rehearse). Codes are stored
normalized: uppercase, no dash, `I`/`L` as `1` and `O` as `0`.

```
npx wrangler d1 execute RELAY_DB --remote --env staging \
  --command "SELECT profile_id, picture_key FROM lfp_profiles WHERE code = 'ABCDE12345'"
# If picture_key isn't null:
npx wrangler r2 object delete oathbound-relay-catalog-staging/lfp-pictures/<picture_key> --remote
npx wrangler d1 execute RELAY_DB --remote --env staging --command \
  "DELETE FROM lfp_likes WHERE from_profile = '<profile_id>' OR to_profile = '<profile_id>'; DELETE FROM lfp_profiles WHERE profile_id = '<profile_id>'"
```

This leaves the person's matches and the chats in them alone, and nothing stops the same install from making a new
profile; the owner's plugin republishes its saved copy the next time it starts. To keep a profile down, tell them why
through the support channel first.

## Free-tier capacity check

Before widening access, model request volume using the load script and the conservative upper bounds of
four catalog syncs per pair per day and four revocation checks per online client per day. Set the Worker's
global daily ceiling below the smallest relevant Cloudflare quota. Legitimate traffic should be rejected
temporarily rather than permitting an unexpected bill or unbounded anonymous workload.

Recorded conservative model (`npm run capacity`, 2026-09-04):

| Active clients | Requests/month | Normal/day | Safety/day | Catalog upload/month | Peak temporary R2 | Fits app ceilings |
|---:|---:|---:|---:|---:|---:|:---:|
| 1,000 | 278,000 | 5,267 | 4,000 | 7.32 GiB | 0.010 GiB | Yes |
| 10,000 | 2,780,000 | 52,667 | 40,000 | 73.24 GiB | 0.102 GiB | No |
| 100,000 | 27,800,000 | 526,667 | 400,000 | 732.42 GiB | 1.017 GiB | No |

These are capacity projections rather than a promise that every profile fits Cloudflare's current free
tier. Keep the configured global daily ceiling and Cloudflare account spending controls below the budget
you are willing to accept, and revisit the model before moving the plugin from staging to production.
The 1,000-client profile averages about 9,267 requests/day and fits these application pools. Larger
profiles require revised quotas and likely a paid plan after measuring actual D1 rows and Worker CPU.
