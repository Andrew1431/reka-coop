// Stand-ins for the Worker's bindings, for running it under plain Node without wrangler.
import fs from "node:fs";
import path from "node:path";

// R2 bucket in memory, or mirrored to a folder when `dir` is given.
export function mockR2(dir) {
  const objects = new Map();
  const file = (key) => path.join(dir, ...key.split("/"));
  if (dir && fs.existsSync(path.join(dir, "index.json")))
    for (const [k, v] of Object.entries(JSON.parse(fs.readFileSync(path.join(dir, "index.json"), "utf8"))))
      objects.set(k, { ...v, data: fs.readFileSync(file(k)), uploaded: new Date(v.uploaded) });
  const save = () => {
    if (!dir) return;
    const index = {};
    for (const [k, v] of objects) index[k] = { meta: v.meta, uploaded: v.uploaded };
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, "index.json"), JSON.stringify(index, null, 1));
  };
  return {
    objects,
    async put(key, body, opts = {}) {
      const data = Buffer.from(body);
      objects.set(key, { data, meta: opts.customMetadata || {}, uploaded: new Date() });
      if (dir) { fs.mkdirSync(path.dirname(file(key)), { recursive: true }); fs.writeFileSync(file(key), data); }
      save();
    },
    async get(key) {
      const o = objects.get(key);
      return o ? { body: new Blob([o.data]).stream(), customMetadata: o.meta } : null;
    },
    async list({ prefix = "", cursor, limit = 1000 } = {}) {
      const keys = [...objects.keys()].filter((k) => k.startsWith(prefix)).sort();
      const start = cursor ? Number(cursor) : 0;
      const page = keys.slice(start, start + limit);
      const truncated = start + limit < keys.length;
      return {
        objects: page.map((k) => ({ key: k, size: objects.get(k).data.length, uploaded: objects.get(k).uploaded, customMetadata: objects.get(k).meta })),
        truncated,
        cursor: truncated ? String(start + limit) : undefined,
      };
    },
  };
}

// Same shape as the Workers rate limiting binding: `limit` calls per `period` seconds per key.
export function mockLimiter(limit, period) {
  const hits = new Map();
  return {
    async limit({ key }) {
      const now = Date.now();
      const recent = (hits.get(key) || []).filter((t) => now - t < period * 1000);
      if (recent.length >= limit) { hits.set(key, recent); return { success: false }; }
      recent.push(now);
      hits.set(key, recent);
      return { success: true };
    },
  };
}

export function env(dir, adminToken = "local-admin-token-0123456789") {
  return {
    UPLOAD_KEY: "reka-coop-playtest-1",
    MAX_UPLOAD_BYTES: "1048576",
    MAX_UNPACKED_BYTES: "8388608",
    MIN_VERSION: "0.3.14",
    MIN_PROTOCOL: "48",
    ADMIN_TOKEN: adminToken,
    REPORTS: mockR2(dir),
    UPLOAD_LIMIT: mockLimiter(6, 60),
  };
}
