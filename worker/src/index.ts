// Claude-Usage-Projector sync Worker.
//
// Two endpoints behind Cloudflare Access (Service Auth policy):
//
//   POST /observations        { observations: [...] } -> { inserted: N }
//   GET  /observations?since=<unix>&account_id=<id>&limit=<n>
//                              -> { observations: [...] }
//
// All authentication is delegated to Cloudflare Access in front of this
// Worker — by the time a request hits our `fetch`, the service token has
// already been validated. We do a paranoid check that the Cf-Access-Client-Id
// header is present, but we don't re-verify the JWT ourselves.
//
// Data model and rationale live in DECISIONS.md ADR-012 in the parent repo.

/// <reference types="@cloudflare/workers-types" />

export interface Env {
    DB: D1Database;
}

interface Observation {
    ts: number;
    account_id: string;
    machine_id: string;
    used_pct: number | null;
    refresh_at: number | null;
}

export default {
    async fetch(req: Request, env: Env): Promise<Response> {
        if (!req.headers.get("Cf-Access-Client-Id")) {
            return new Response("forbidden", { status: 403 });
        }

        const url = new URL(req.url);
        try {
            if (req.method === "POST" && url.pathname === "/observations") {
                return await postObservations(req, env);
            }
            if (req.method === "GET" && url.pathname === "/observations") {
                return await getObservations(url, env);
            }
            if (req.method === "GET" && url.pathname === "/healthz") {
                return Response.json({ ok: true });
            }
            return new Response("not found", { status: 404 });
        } catch (err) {
            const msg = err instanceof Error ? err.message : String(err);
            return new Response(`error: ${msg}`, { status: 500 });
        }
    },
};

async function postObservations(req: Request, env: Env): Promise<Response> {
    let body: { observations?: unknown };
    try {
        body = await req.json<{ observations?: unknown }>();
    } catch {
        return new Response("expected JSON body", { status: 400 });
    }
    if (!Array.isArray(body.observations)) {
        return new Response("expected { observations: [...] }", { status: 400 });
    }

    const valid: Observation[] = [];
    for (const raw of body.observations) {
        const o = raw as Partial<Observation>;
        if (
            typeof o.ts !== "number" ||
            typeof o.account_id !== "string" ||
            typeof o.machine_id !== "string"
        ) {
            // Skip malformed rows rather than reject the whole batch — keeps
            // retries useful in the face of a single bad row.
            continue;
        }
        valid.push({
            ts: o.ts,
            account_id: o.account_id,
            machine_id: o.machine_id,
            used_pct: typeof o.used_pct === "number" ? o.used_pct : null,
            refresh_at: typeof o.refresh_at === "number" ? o.refresh_at : null,
        });
    }

    if (valid.length === 0) {
        return Response.json({ inserted: 0, skipped: (body.observations as unknown[]).length });
    }

    const stmt = env.DB.prepare(
        "INSERT OR IGNORE INTO observations (ts, account_id, machine_id, used_pct, refresh_at) VALUES (?, ?, ?, ?, ?)"
    );
    const batch = valid.map((o) =>
        stmt.bind(o.ts, o.account_id, o.machine_id, o.used_pct, o.refresh_at)
    );
    const results = await env.DB.batch(batch);
    const inserted = results.reduce((sum, r) => sum + (r.meta?.changes ?? 0), 0);
    return Response.json({
        inserted,
        skipped: (body.observations as unknown[]).length - valid.length,
    });
}

async function getObservations(url: URL, env: Env): Promise<Response> {
    const since = clampInt(url.searchParams.get("since"), 0, 0, Number.MAX_SAFE_INTEGER);
    const limit = clampInt(url.searchParams.get("limit"), 5000, 1, 10_000);
    const accountId = url.searchParams.get("account_id");

    const stmt = accountId
        ? env.DB
              .prepare(
                  "SELECT ts, account_id, machine_id, used_pct, refresh_at FROM observations " +
                      "WHERE account_id = ? AND ts >= ? ORDER BY ts ASC LIMIT ?"
              )
              .bind(accountId, since, limit)
        : env.DB
              .prepare(
                  "SELECT ts, account_id, machine_id, used_pct, refresh_at FROM observations " +
                      "WHERE ts >= ? ORDER BY ts ASC LIMIT ?"
              )
              .bind(since, limit);

    const { results } = await stmt.all<Observation>();
    return Response.json({ observations: results, count: results.length });
}

function clampInt(raw: string | null, fallback: number, lo: number, hi: number): number {
    if (raw === null) return fallback;
    const n = parseInt(raw, 10);
    if (Number.isNaN(n)) return fallback;
    return Math.max(lo, Math.min(hi, n));
}
