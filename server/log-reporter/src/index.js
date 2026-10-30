// Receives playtest log bundles from the Reka co-op mod and keeps them in R2.
// The upload key ships inside the mod, so it only filters out stray traffic: every upload is untrusted.
//
//   POST /v1/report          gzip of the bundle JSON (see mod/RekaCoop/Diagnostics/Bundle.cs); X-Reka-Key header
//   GET  /v1/list            admin: R2 listing with metadata (?cursor=, ?prefix=)
//   GET  /v1/object/<key>    admin: one stored bundle (gzip)
// Admin calls need "Authorization: Bearer <ADMIN_TOKEN>" (a wrangler secret).

const KINDS = new Set(["session", "crash", "report"]);
const VERSION = /^\d{1,3}\.\d{1,3}\.\d{1,3}$/;
const HEX32 = /^[0-9a-f]{32}$/;
const REPORT_ID = /^[0-9a-f]{12}$/;

export default {
  async fetch(request, env) {
    try {
      const url = new URL(request.url);
      if (url.pathname === "/v1/report" && request.method === "POST") return await upload(request, env);
      if (url.pathname === "/v1/list" && request.method === "GET") return await list(request, env, url);
      if (url.pathname.startsWith("/v1/object/") && request.method === "GET") return await object(request, env, url);
      return text(404, "not found");
    } catch (e) {
      console.error("unhandled", e && e.stack || e);
      return text(500, "error");
    }
  },
};

async function upload(request, env) {
  const maxBytes = int(env.MAX_UPLOAD_BYTES, 1 << 20);
  const maxUnpacked = int(env.MAX_UNPACKED_BYTES, 8 << 20);

  if (!env.UPLOAD_KEY || request.headers.get("x-reka-key") !== env.UPLOAD_KEY) return text(403, "bad key");

  const ip = request.headers.get("cf-connecting-ip") || "unknown";
  if (env.UPLOAD_LIMIT) {
    const { success } = await env.UPLOAD_LIMIT.limit({ key: ip });
    if (!success) return text(429, "slow down");
  }

  const declared = Number(request.headers.get("content-length") || "0");
  if (declared > maxBytes) return text(413, "too large");
  const body = await readCapped(request.body, maxBytes);
  if (!body) return text(413, "too large");
  if (body.length < 18 || body[0] !== 0x1f || body[1] !== 0x8b) return text(400, "not gzip");

  let bundle;
  try {
    const raw = await readCapped(new Blob([body]).stream().pipeThrough(new DecompressionStream("gzip")), maxUnpacked);
    if (!raw) return text(413, "too large unpacked");
    bundle = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(raw));
  } catch {
    return text(400, "bad bundle");
  }

  const problem = check(bundle, env);
  if (problem) return text(problem.status, problem.message);

  const now = new Date();
  const day = now.toISOString().slice(0, 10);
  const id = `${now.toISOString().replace(/[:.]/g, "-")}-${randomHex(6)}`;
  const key = `reports/${bundle.mod}/${day}/${bundle.kind}/${id}.json.gz`;
  await env.REPORTS.put(key, body, {
    httpMetadata: { contentType: "application/gzip" },
    customMetadata: {
      mod: bundle.mod,
      protocol: String(bundle.protocol),
      kind: bundle.kind,
      tester: bundle.testerId,
      report: bundle.reportId || "",
    },
  });
  return json(201, { id });
}

// Only the fields the store and the pull script rely on; the rest is free text, read as text.
function check(b, env) {
  if (!b || typeof b !== "object" || Array.isArray(b)) return bad("not an object");
  if (b.schema !== 1) return bad("schema");
  if (typeof b.mod !== "string" || !VERSION.test(b.mod)) return bad("mod version");
  if (!Number.isInteger(b.protocol) || b.protocol < 0 || b.protocol > 100000) return bad("protocol");
  if (!KINDS.has(b.kind)) return bad("kind");
  if (typeof b.testerId !== "string" || !HEX32.test(b.testerId)) return bad("tester id");
  if (b.reportId != null && (typeof b.reportId !== "string" || !REPORT_ID.test(b.reportId))) return bad("report id");
  for (const f of ["log", "desync", "note"])
    if (b[f] != null && typeof b[f] !== "string") return bad(f);
  if (env.MIN_VERSION && compareVersions(b.mod, env.MIN_VERSION) < 0) return { status: 426, message: "mod too old" };
  if (env.MIN_PROTOCOL && b.protocol < int(env.MIN_PROTOCOL, 0)) return { status: 426, message: "protocol too old" };
  return null;
}

const bad = (what) => ({ status: 400, message: `bad ${what}` });

async function list(request, env, url) {
  if (!admin(request, env)) return text(401, "no");
  const opts = { limit: 1000, include: ["customMetadata"] };
  const cursor = url.searchParams.get("cursor");
  const prefix = url.searchParams.get("prefix");
  if (cursor) opts.cursor = cursor;
  if (prefix) opts.prefix = prefix;
  const r = await env.REPORTS.list(opts);
  return json(200, {
    objects: r.objects.map((o) => ({ key: o.key, size: o.size, uploaded: o.uploaded, meta: o.customMetadata || {} })),
    cursor: r.truncated ? r.cursor : null,
  });
}

async function object(request, env, url) {
  if (!admin(request, env)) return text(401, "no");
  const key = decodeURIComponent(url.pathname.slice("/v1/object/".length));
  if (!key.startsWith("reports/") || key.includes("..")) return text(400, "bad key");
  const o = await env.REPORTS.get(key);
  if (!o) return text(404, "not found");
  return new Response(o.body, { headers: { "content-type": "application/gzip" } });
}

function admin(request, env) {
  const token = env.ADMIN_TOKEN;
  if (!token || token.length < 16) return false;
  const got = request.headers.get("authorization") || "";
  return equalTime(got, `Bearer ${token}`);
}

function equalTime(a, b) {
  const x = new TextEncoder().encode(a), y = new TextEncoder().encode(b);
  let diff = x.length ^ y.length;
  for (let i = 0; i < y.length; i++) diff |= (x[i] ?? 0) ^ y[i];
  return diff === 0;
}

// The whole stream as bytes, or null once it passes `max`.
async function readCapped(stream, max) {
  if (!stream) return new Uint8Array(0);
  const reader = stream.getReader();
  const parts = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.length;
    if (total > max) { await reader.cancel(); return null; }
    parts.push(value);
  }
  const out = new Uint8Array(total);
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.length; }
  return out;
}

function compareVersions(a, b) {
  const pa = a.split(".").map(Number), pb = b.split(".").map(Number);
  for (let i = 0; i < 3; i++) if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) - (pb[i] || 0);
  return 0;
}

function randomHex(bytes) {
  const b = crypto.getRandomValues(new Uint8Array(bytes));
  return [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
}

const int = (v, d) => (v != null && v !== "" && Number.isFinite(Number(v)) ? Number(v) : d);
const text = (status, body) => new Response(body, { status, headers: { "content-type": "text/plain" } });
const json = (status, body) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
