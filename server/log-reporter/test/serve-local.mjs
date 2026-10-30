// The Worker as a local HTTP server under plain Node, with the bucket kept in a folder. A stand-in for
// `wrangler dev` (not installed). `node test/serve-local.mjs [port] [dir]`; admin token: local-admin-token-0123456789
import http from "node:http";
import worker from "../src/index.js";
import { env as makeEnv } from "./mock-env.mjs";

const port = Number(process.argv[2] || 8787);
const dir = process.argv[3] || new URL("../.local-bucket", import.meta.url).pathname.replace(/^\/(\w:)/, "$1");
const env = makeEnv(dir);

http.createServer(async (req, res) => {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  const headers = new Headers();
  for (const [k, v] of Object.entries(req.headers)) if (typeof v === "string") headers.set(k, v);
  headers.set("cf-connecting-ip", req.socket.remoteAddress || "local");
  const body = ["GET", "HEAD"].includes(req.method) ? undefined : Buffer.concat(chunks);
  const r = await worker.fetch(new Request(`http://localhost:${port}${req.url}`, { method: req.method, headers, body }), env);
  res.writeHead(r.status, Object.fromEntries(r.headers));
  res.end(Buffer.from(await r.arrayBuffer()));
  console.log(`${req.method} ${req.url} -> ${r.status}`);
}).listen(port, "127.0.0.1", () => console.log(`log reporter on http://127.0.0.1:${port} (bucket: ${dir})`));
