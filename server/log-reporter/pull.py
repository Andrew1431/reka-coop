"""Pull playtest reports from the log reporter and group them. Standard library only.

    python pull.py pull --url https://<worker> [--out DIR]     # token in REKA_LOGS_TOKEN
    python pull.py group [--out DIR] [--version 0.3.14] [--top 25]
    python pull.py show REPORT_ID [--out DIR]                  # every bundle of one F8 report, side by side

Bundles land in DIR (default ../../local/reports, outside git) at their R2 key; already pulled ones are skipped.
"""
import argparse
import collections
import gzip
import json
import os
import re
import sys
import urllib.parse
import urllib.request
from pathlib import Path

DEFAULT_OUT = Path(__file__).resolve().parent.parent.parent / "local" / "reports"


def request(url, token):
    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {token}", "User-Agent": "reka-pull"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read()


def pull(args):
    token = os.environ.get("REKA_LOGS_TOKEN")
    if not token:
        sys.exit("set REKA_LOGS_TOKEN")
    base = args.url.rstrip("/")
    out = Path(args.out)
    cursor, new, seen = None, 0, 0
    while True:
        q = "?" + urllib.parse.urlencode({"cursor": cursor}) if cursor else ""
        page = json.loads(request(f"{base}/v1/list{q}", token))
        for o in page["objects"]:
            seen += 1
            key = o["key"]
            dest = out.joinpath(*key.split("/"))
            if not key.startswith("reports/") or ".." in key or dest.exists():
                continue
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(request(f"{base}/v1/object/{urllib.parse.quote(key, safe='')}", token))
            new += 1
        cursor = page.get("cursor")
        if not cursor:
            break
    print(f"{seen} on the server, {new} new, in {out}")


def load(out, version=None):
    for p in sorted(Path(out).rglob("*.json.gz")):
        try:
            b = json.loads(gzip.decompress(p.read_bytes()))
        except Exception as e:  # untrusted input: skip what doesn't parse
            print(f"skipping {p}: {e}", file=sys.stderr)
            continue
        if version and b.get("mod") != version:
            continue
        b["_path"] = p
        yield b


LEVEL = re.compile(r"^(?:\[[\d:.]+\]\s*)?\[(Warning|Error|Fatal)\s*:\s*([^\]]+)\]\s*(.*)")
STAMP = re.compile(r"^\[\d\d:\d\d:\d\d\]\s*")


def shape(line):
    """A warning with its changing parts (numbers, ids, hashes, coordinates) folded, so repeats group together."""
    s = re.sub(r"0x[0-9A-Fa-f]+", "0x#", line)
    s = re.sub(r"\b[0-9a-f]{8,}\b", "#", s)
    s = re.sub(r"-?\d+(\.\d+)?", "#", s)
    return s[:220]


def desync_lines(text):
    """(category, key) for every 'key: host X, here Y' line, and the summary/repair lines."""
    cat = None
    for raw in (text or "").splitlines():
        line = STAMP.sub("", raw)
        m = re.match(r"^(\w+): (\d+) differ from the host", line)
        if m:
            cat = m.group(1)
            yield ("summary", f"{cat}: N differ")
            continue
        if re.match(r"^\w+: (switched|\d+ values changed)", line):
            yield ("repair", shape(line))
            continue
        m = re.match(r"^\s+(.+?): host .*, here ", raw)
        if m and cat:
            yield (cat, m.group(1))


def group(args):
    bundles = list(load(args.out, args.version))
    if not bundles:
        print("no reports")
        return
    by_version = collections.defaultdict(collections.Counter)
    testers = collections.defaultdict(set)
    warnings = collections.Counter()
    warn_versions = collections.defaultdict(set)
    desyncs = collections.Counter()
    repairs = collections.Counter()
    reports = collections.defaultdict(list)
    for b in bundles:
        v = b.get("mod", "?")
        by_version[v][b.get("kind", "?")] += 1
        testers[v].add(b.get("testerId"))
        seen_here = set()
        for line in (b.get("log") or "").splitlines():
            m = LEVEL.match(line)
            if m:
                key = f"{m.group(1)} [{m.group(2).strip()}] {shape(m.group(3))}"
                if key not in seen_here:  # count bundles, not repeats inside one
                    seen_here.add(key)
                    warnings[key] += 1
                    warn_versions[key].add(v)
        for cat, key in set(desync_lines(b.get("desync"))):
            if cat == "repair":
                repairs[key] += 1
            elif cat != "summary":
                desyncs[(cat, key)] += 1
        if b.get("reportId"):
            reports[b["reportId"]].append(b)

    print("== versions")
    for v in sorted(by_version, key=lambda s: [int(x) for x in s.split(".")] if re.match(r"^\d+\.\d+\.\d+$", s) else [0]):
        kinds = ", ".join(f"{k} {n}" for k, n in sorted(by_version[v].items()))
        print(f"  {v}: {kinds}; {len(testers[v])} testers")
    print(f"\n== warnings and errors (bundles they appear in, top {args.top})")
    for k, n in warnings.most_common(args.top):
        print(f"  {n:4}  {k}   [{', '.join(sorted(warn_versions[k]))}]")
    print(f"\n== desync keys (bundles, top {args.top})")
    for (cat, key), n in desyncs.most_common(args.top):
        print(f"  {n:4}  {cat}: {key}")
    if repairs:
        print("\n== desync repairs")
        for k, n in repairs.most_common(args.top):
            print(f"  {n:4}  {k}")
    if reports:
        print("\n== F8 reports")
        for rid, bs in sorted(reports.items(), key=lambda kv: kv[1][0].get("reportedUtc") or ""):
            first = bs[0]
            print(f"  {rid}  {first.get('reportedUtc', '?')}  {first.get('mod')}  {len(bs)} logs  \"{first.get('note', '')}\"")


def show(args):
    bs = [b for b in load(args.out) if b.get("reportId") == args.report_id]
    if not bs:
        sys.exit("no such report pulled")
    for b in bs:
        s = b.get("session") or {}
        print(f"===== {b['_path']}\n{b.get('kind')} {b.get('mod')} p{b.get('protocol')} role {s.get('role')} "
              f"by {b.get('reportedBy')} note \"{b.get('note', '')}\"")
        for line in (b.get("log") or "").splitlines()[-args.tail:]:
            print("  " + line)
        if b.get("desync"):
            print("  --- desync.log")
            for line in b["desync"].splitlines()[-args.tail:]:
                print("  " + line)


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("pull"); p.add_argument("--url", required=True); p.set_defaults(fn=pull)
    g = sub.add_parser("group"); g.add_argument("--version"); g.add_argument("--top", type=int, default=25); g.set_defaults(fn=group)
    s = sub.add_parser("show"); s.add_argument("report_id"); s.add_argument("--tail", type=int, default=80); s.set_defaults(fn=show)
    for x in (p, g, s):
        x.add_argument("--out", default=str(DEFAULT_OUT))
    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
