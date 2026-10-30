// Runs the Worker's fetch handler under Node against mock bindings. `node test/test.mjs`
import assert from "node:assert/strict";
import zlib from "node:zlib";
import worker from "../src/index.js";
import { env as makeEnv } from "./mock-env.mjs";

const bundle = (over = {}) => ({
  schema: 1, mod: "0.3.14", protocol: 48, game: "26298851", kind: "session",
  testerId: "0123456789abcdef0123456789abcdef", reportId: null, log: "[Info   :Reka Co-op] hello\n", desync: "", ...over,
});
const gz = (obj) => zlib.gzipSync(Buffer.from(typeof obj === "string" ? obj : JSON.stringify(obj)));

let ipCounter = 0;
async function post(env, body, headers = {}) {
  const req = new Request("https://logs.test/v1/report", {
    method: "POST",
    body,
    headers: { "x-reka-key": "reka-coop-playtest-1", "cf-connecting-ip": `10.0.0.${++ipCounter % 250}`, ...headers },
  });
  return worker.fetch(req, env);
}
const get = (env, path, token) =>
  worker.fetch(new Request(`https://logs.test${path}`, { headers: token ? { authorization: `Bearer ${token}` } : {} }), env);

const cases = [];
const test = (name, fn) => cases.push([name, fn]);

test("stores a good bundle with metadata", async () => {
  const env = makeEnv();
  const r = await post(env, gz(bundle({ kind: "report", reportId: "a1b2c3d4e5f6", note: "door stuck" })));
  assert.equal(r.status, 201);
  const [[key, o]] = [...env.REPORTS.objects];
  assert.match(key, /^reports\/0\.3\.14\/\d{4}-\d\d-\d\d\/report\/.+\.json\.gz$/);
  assert.deepEqual(o.meta, { mod: "0.3.14", protocol: "48", kind: "report", tester: "0123456789abcdef0123456789abcdef", report: "a1b2c3d4e5f6" });
  assert.equal(JSON.parse(zlib.gunzipSync(o.data)).note, "door stuck");
});

test("wrong or missing key", async () => {
  const env = makeEnv();
  assert.equal((await post(env, gz(bundle()), { "x-reka-key": "nope" })).status, 403);
  assert.equal(env.REPORTS.objects.size, 0);
});

test("not gzip / bad json / bad fields", async () => {
  const env = makeEnv();
  assert.equal((await post(env, Buffer.from(JSON.stringify(bundle())))).status, 400);
  assert.equal((await post(env, gz("{not json"))).status, 400);
  assert.equal((await post(env, gz("[1,2]"))).status, 400);
  assert.equal((await post(env, gz(bundle({ kind: "x" })))).status, 400);
  assert.equal((await post(env, gz(bundle({ testerId: "../../etc" })))).status, 400);
  assert.equal((await post(env, gz(bundle({ reportId: "ZZ" })))).status, 400);
  assert.equal((await post(env, gz(bundle({ mod: "0.3.14/../x" })))).status, 400);
  assert.equal((await post(env, gz(bundle({ log: { a: 1 } })))).status, 400);
  assert.equal(env.REPORTS.objects.size, 0);
});

test("version check", async () => {
  const env = makeEnv();
  assert.equal((await post(env, gz(bundle({ mod: "0.3.13" })))).status, 426);
  assert.equal((await post(env, gz(bundle({ protocol: 47 })))).status, 426);
  assert.equal((await post(env, gz(bundle({ mod: "0.4.0", protocol: 49 })))).status, 201);
});

test("size caps: body and unpacked (zip bomb)", async () => {
  const env = makeEnv();
  const big = Buffer.alloc((1 << 20) + 10, 7);
  assert.equal((await post(env, big)).status, 413);
  const bomb = gz(bundle({ log: "a".repeat(9 << 20) }));
  assert.ok(bomb.length < 1 << 20);
  assert.equal((await post(env, bomb)).status, 413);
  assert.equal(env.REPORTS.objects.size, 0);
});

test("per-IP rate limit", async () => {
  const env = makeEnv();
  const codes = [];
  for (let i = 0; i < 8; i++) codes.push((await post(env, gz(bundle()), { "cf-connecting-ip": "203.0.113.9" })).status);
  assert.deepEqual(codes, [201, 201, 201, 201, 201, 201, 429, 429]);
  assert.equal((await post(env, gz(bundle()), { "cf-connecting-ip": "203.0.113.10" })).status, 201);
});

test("admin list and fetch need the token", async () => {
  const env = makeEnv();
  await post(env, gz(bundle()));
  assert.equal((await get(env, "/v1/list")).status, 401);
  assert.equal((await get(env, "/v1/list", "wrong-token-wrong-token")).status, 401);
  const l = await (await get(env, "/v1/list", env.ADMIN_TOKEN)).json();
  assert.equal(l.objects.length, 1);
  assert.equal(l.objects[0].meta.kind, "session");
  const o = await get(env, `/v1/object/${encodeURIComponent(l.objects[0].key)}`, env.ADMIN_TOKEN);
  assert.equal(o.status, 200);
  assert.equal(JSON.parse(zlib.gunzipSync(Buffer.from(await o.arrayBuffer()))).mod, "0.3.14");
  assert.equal((await get(env, "/v1/object/other/x", env.ADMIN_TOKEN)).status, 400);
});

test("admin disabled without a token set", async () => {
  const env = makeEnv(undefined, "");
  assert.equal((await get(env, "/v1/list", "")).status, 401);
});

test("unknown routes", async () => {
  const env = makeEnv();
  assert.equal((await get(env, "/")).status, 404);
  assert.equal((await worker.fetch(new Request("https://logs.test/v1/report"), env)).status, 404);
});

let failed = 0;
for (const [name, fn] of cases) {
  try { await fn(); console.log(`ok   ${name}`); }
  catch (e) { failed++; console.log(`FAIL ${name}\n     ${e.stack}`); }
}
console.log(failed ? `${failed} failed` : `all ${cases.length} passed`);
process.exit(failed ? 1 : 0);
