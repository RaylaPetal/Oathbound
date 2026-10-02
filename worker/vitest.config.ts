import path from "node:path";
import { defineWorkersConfig, readD1Migrations } from "@cloudflare/vitest-pool-workers/config";

export default defineWorkersConfig(async () => {
  const migrationsPath = path.join(__dirname, "migrations");
  const migrations = await readD1Migrations(migrationsPath);

  return {
    test: {
      setupFiles: ["./test/apply-migrations.ts"],
      poolOptions: {
        workers: {
          wrangler: { configPath: "./wrangler.toml", environment: "local" },
          miniflare: {
            compatibilityFlags: ["nodejs_compat"],
            bindings: { TEST_MIGRATIONS: migrations },
            // The pool doesn't read `ratelimits` from wrangler.toml; mirror env.local's binding here.
            ratelimits: { ORIGIN_RATE_LIMITER: { simple: { limit: 120, period: 60 } } },
          },
        },
      },
    },
  };
});
