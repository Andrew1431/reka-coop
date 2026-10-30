"""Compares each player's real path with the other copy's puppet of them, from two trace.csv files (dev `trace`).

Usage: python -I mod/synccheck.py [host_dir] [guest_dir] [--tolerance 0.5]
Both copies run on this PC, so their wall clocks are the same. The puppet is shown a little behind (interpolation and
the network), so each pairing is checked at the lag that fits best, and that lag is reported too.
Exit code 1 when the 95th percentile error is over the tolerance in either direction.
"""
import bisect
import csv
import math
import sys
from pathlib import Path


def load(path):
    rows = {}
    with open(path, newline='') as f:
        for r in csv.DictReader(f):
            rows.setdefault(r['who'], []).append((int(r['utc_ms']), float(r['x']), float(r['y']), float(r['z'])))
    return rows


def at(track, t):
    times = [p[0] for p in track]
    i = bisect.bisect_left(times, t)
    if i <= 0 or i >= len(track):
        return None
    a, b = track[i - 1], track[i]
    f = (t - a[0]) / (b[0] - a[0]) if b[0] > a[0] else 0
    return tuple(a[k] + (b[k] - a[k]) * f for k in (1, 2, 3))


def compare(real, shown):
    best = None
    for lag in range(0, 501, 10):
        errs = []
        for t, x, y, z in shown:
            p = at(real, t - lag)
            if p is not None:
                errs.append(math.hypot(x - p[0], z - p[2]))
        if len(errs) < 20:
            continue
        errs.sort()
        mean = sum(errs) / len(errs)
        if best is None or mean < best[1]:
            best = (lag, mean, errs[int(len(errs) * 0.95)], errs[-1], len(errs))
    return best


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    tol = float(sys.argv[sys.argv.index('--tolerance') + 1]) if '--tolerance' in sys.argv else 0.5
    root = Path(__file__).resolve().parent.parent / 'game'
    host = Path(args[0]) if args else root / 'play'
    guest = Path(args[1]) if len(args) > 1 else root / 'client'
    h, g = load(host / 'trace.csv'), load(guest / 'trace.csv')
    ok = True
    for name, real, shown in (('guest as the host sees them', g.get('self'), h.get('puppet1')),
                              ('host as the guest sees them', h.get('self'), g.get('puppet0'))):
        if not real or not shown:
            print(f'{name}: no data')
            ok = False
            continue
        moved = math.hypot(real[-1][1] - real[0][1], real[-1][3] - real[0][3])
        lag, mean, p95, worst, n = compare(real, shown)
        good = p95 <= tol
        ok &= good
        print(f'{name}: {"OK" if good else "OVER"} lag {lag} ms, error mean {mean:.2f} m, p95 {p95:.2f} m, max {worst:.2f} m '
              f'({n} frames; real path moved {moved:.1f} m end to end)')
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
