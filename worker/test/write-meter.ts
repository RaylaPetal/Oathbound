import { env } from "cloudflare:test";
import worker from "../src/index";
import type { Env } from "../src/env";

export interface WriteTally {
  /** D1's own rows_written, summed over every statement. */
  rowsWritten: number;
  /** Rows an INSERT or UPDATE touched; each can cost at most one more write when the cron later clears it. */
  rowsTouched: number;
}

/**
 * Wraps a D1 binding so every statement's meta is tallied. `first()` is served through `all()` because only
 * `all()`/`run()` return meta.
 */
export function meteredDb(db: D1Database, tally: WriteTally): D1Database {
  const raw = new WeakMap<object, D1PreparedStatement>();
  const sqlOf = new WeakMap<object, string>();
  const record = (sql: string, meta: { rows_written?: number; changes?: number }) => {
    tally.rowsWritten += meta.rows_written ?? 0;
    if (/^\s*(INSERT|UPDATE)\b/i.test(sql)) tally.rowsTouched += meta.changes ?? 0;
  };
  const wrap = (stmt: D1PreparedStatement, sql: string): D1PreparedStatement => {
    const wrapped = {
      bind: (...values: unknown[]) => wrap(stmt.bind(...values), sql),
      run: async () => {
        const result = await stmt.run();
        record(sql, result.meta);
        return result;
      },
      all: async () => {
        const result = await stmt.all();
        record(sql, result.meta);
        return result;
      },
      first: async (column?: string) => {
        const result = await stmt.all<Record<string, unknown>>();
        record(sql, result.meta);
        const row = result.results[0] ?? null;
        return column === undefined ? row : (row?.[column] ?? null);
      },
      raw: (options?: { columnNames?: boolean }) => stmt.raw(options as { columnNames: true }),
    } as unknown as D1PreparedStatement;
    raw.set(wrapped, stmt);
    sqlOf.set(wrapped, sql);
    return wrapped;
  };
  return {
    prepare: (sql: string) => wrap(db.prepare(sql), sql),
    batch: async (statements: D1PreparedStatement[]) => {
      const results = await db.batch(statements.map((s) => raw.get(s) ?? s));
      results.forEach((result, i) => record(sqlOf.get(statements[i]!) ?? "", result.meta));
      return results;
    },
    exec: (query: string) => db.exec(query),
    dump: () => db.dump(),
    withSession: () => {
      throw new Error("withSession is not metered");
    },
  } as unknown as D1Database;
}

/** A fetcher that runs the worker in-process with a metered D1, adding each request's writes to `tally`. */
export function meteredFetcher(tally: WriteTally): (request: Request) => Promise<Response> {
  const meteredEnv: Env = { ...(env as Env), RELAY_DB: meteredDb((env as Env).RELAY_DB, tally) };
  return (request) => worker.fetch(request, meteredEnv);
}

export function emptyTally(): WriteTally {
  return { rowsWritten: 0, rowsTouched: 0 };
}
