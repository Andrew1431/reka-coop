"""Scripted co-op regression run (PLAN.md phase 0).

Starts `play` (host) and `client` (guest) on a copy of a fixture save in its own save folder, drives a scenario with
dev commands (cmd.request), and checks the two copies agree and nobody is stuck.

  python mod/regress.py                 every scenario, host-led then guest-led
  python mod/regress.py door guest      one scenario, one leader
  python mod/regress.py --keep ...      leave the games running afterwards

Fixtures live in local/saves/<name>/ (a save folder such as "- 1 -" inside). Exit code 0 = all checks passed.

Making pre-door-1.0.32 again (host alone, empty save name, dev commands through game/play/cmd.request): `click New Game`,
`ccdone`, `click Let’s begin...`, `fields TutorialManager_v2 invoke SolveCurrentTask` x6, `interact PlayerDetection all`,
`click OK`, `click CLAIM`, SolveCurrentTask until `tutorial` says block 1 task 3 done, `click Continue`; copy the save
folder with its first autosave.
"""
import os, re, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = os.path.join(ROOT, "game")
HOST, GUEST = "play", "client"
SAVE_NAME = "regress"


def log_path(inst): return os.path.join(GAME, inst, "BepInEx", "LogOutput.log")


def read_log(inst):
    try:
        with open(log_path(inst), encoding="utf-8", errors="replace") as f: return f.read()
    except OSError:
        return ""


def ps(script, *args):
    # -Command, not -File: with -File "a,b" arrives as one string instead of an array.
    line = "& '" + os.path.join(ROOT, "mod", script) + "' " + " ".join(args)
    return subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", line],
                          capture_output=True, text=True, stdin=subprocess.DEVNULL)


def cmd(inst, text, wait=8.0):
    """Runs a dev command; returns its result (the text after '->', plus following lines)."""
    before = len(read_log(inst))
    req = os.path.join(GAME, inst, "cmd.request")
    with open(req + ".tmp", "w", newline="") as f: f.write(text)
    os.replace(req + ".tmp", req)  # whole at once: the game reads and deletes it every frame
    marker = f"[cmd] {text} "
    end = time.time() + wait
    while time.time() < end:
        new = read_log(inst)[before:]
        i = new.find(marker)
        if i >= 0 and "\n" in new[i:]:
            time.sleep(0.3)
            new = read_log(inst)[before:]
            out = new[i + len(marker):]
            return out[3:].strip() if out.startswith("-> ") else out.strip()
        time.sleep(0.2)
    raise TimeoutError(f"{inst}: no answer to '{text}'")


def wait_log(inst, pattern, timeout, since=0):
    end = time.time() + timeout
    rx = re.compile(pattern)
    while time.time() < end:
        m = rx.search(read_log(inst)[since:])
        if m: return m
        time.sleep(1)
    return None


def pos(inst):
    m = re.search(r"pos \(([-\d.]+), ([-\d.]+), ([-\d.]+)\)", cmd(inst, "pos"))
    return tuple(float(g) for g in m.groups()) if m else None


class Run:
    def __init__(self, name): self.name, self.results = name, []

    def check(self, what, ok, detail=""):
        self.results.append((what, bool(ok), detail))
        print(f"  {'ok  ' if ok else 'FAIL'} {what}{(': ' + detail) if detail and not ok else ''}", flush=True)

    @property
    def passed(self): return all(ok for _, ok, _ in self.results)


def start(fixture):
    ps("stop.ps1")
    time.sleep(3)
    src = os.path.join(ROOT, "local", "saves", fixture)
    if not os.path.isdir(src): raise SystemExit(f"no fixture {src}")
    dst = os.path.join(GAME, HOST, "CoopSaves", SAVE_NAME)
    if os.path.isdir(dst):  # the last run's world: kept, not deleted
        keep = os.path.join(ROOT, "local", "backup", "regress-runs", time.strftime("%Y-%m-%d_%H%M%S"))
        os.makedirs(os.path.dirname(keep), exist_ok=True)
        shutil.move(dst, keep)
    shutil.copytree(src, dst)
    for inst in (HOST, GUEST):
        try: os.remove(os.path.join(GAME, inst, "cmd.request"))
        except OSError: pass
        # The last launch's log out of the way, so its lines aren't taken for this launch's.
        try: os.replace(log_path(inst), log_path(inst) + ".prev")
        except OSError: pass
    r = ps("launch.ps1", "-Instances", f"{HOST},{GUEST}", "-Names", f"{SAVE_NAME},{SAVE_NAME}-guest",
           "-ExtraArgs", "-coopautoload")
    if r.returncode != 0: raise SystemExit(f"launch failed: {r.stdout}\n{r.stderr}")
    # In the world on both: the guest has the host's world and sees the host's body.
    if not wait_log(GUEST, r"Names: player 0", 420): raise SystemExit("guest never got into the host's world")
    if not wait_log(HOST, r"Names: player 1", 120): raise SystemExit("host never saw the guest")
    time.sleep(15)


def stop(): ps("stop.ps1")


def common_checks(run):
    """What must hold at the end of any scenario."""
    for inst in (HOST, GUEST):
        probe = cmd(inst, "dlgprobe")
        run.check(f"{inst}: no dialogue left on screen", "talking=False" in probe and "options=False" in probe, probe[:160])
        a = pos(inst)
        x, _, z = a
        cmd(inst, f"walkto {x + 3:.1f} {z + 3:.1f} 4")
        time.sleep(4.5)
        b = pos(inst)
        moved = ((a[0] - b[0]) ** 2 + (a[2] - b[2]) ** 2) ** 0.5
        run.check(f"{inst}: can walk", moved > 0.7, f"moved {moved:.2f} m")
    # Two digest rounds (15 s each) after things settle.
    time.sleep(40)
    d = cmd(GUEST, "digest")
    cats = [c.strip() for c in d.split(";") if c.strip()]
    bad = [c for c in cats if " same" not in c]
    run.check("guest matches host (story flags, trials, progress, objects, pack)", cats and not bad, "; ".join(bad)[:400])
    for inst in (HOST, GUEST):
        log = read_log(inst)
        errs = [l for l in log.splitlines() if re.search(r"\[Error|Exception", l) and "[cmd]" not in l]
        run.check(f"{inst}: no errors in the log", not errs, f"{len(errs)}: {errs[0][:200] if errs else ''}")


def gate_state(inst):
    g = cmd(inst, "gates")
    return ("completed=True" in g and "open=True" in g), g


# --- scenarios -------------------------------------------------------------------------------------------------

def door(run, leader):
    """Baba Jaga's door: walk up, she starts her talk, the yard gate opens. Fixture: just before the door."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST):
        g = cmd(inst, "gates")
        shut = "completed=False" in g and "open=False" in g
        run.check(f"{inst}: gate shut at the start", shut, g[:200])
        if not shut: raise SystemExit("the fixture isn't before the door talk")
    # The other waits a few steps back; the leader walks up to the door once the detectors they both crossed on the
    # way in have been forgotten (the host holds "who set this off" for 10 s).
    cmd(other, "tp 97 25")
    cmd(lead, "tp 96 24")
    time.sleep(15)
    mark = len(read_log(HOST))
    cmd(lead, "walkto 88 15 12")
    time.sleep(13)
    probe = cmd(HOST, "dlgprobe")
    run.check("the door talk started on the host", "talking=True" in probe, probe[:160])
    led_by_guest = "conversation led by " + SAVE_NAME + "-guest" in read_log(HOST)[mark:]
    run.check(f"the {leader} leads the talk", led_by_guest == (leader == "guest"), f"led by guest: {led_by_guest}")
    for _ in range(25):
        if "talking=False" in cmd(HOST, "dlgprobe"): break
        cmd(lead, "next")
        time.sleep(1.5)
    time.sleep(6)
    for inst in (HOST, GUEST):
        ok, g = gate_state(inst)
        run.check(f"{inst}: gate open after the talk", ok, g[:200])


SCENARIOS = {"door": ("pre-door-1.0.32", door)}


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    keep = "--keep" in sys.argv
    names = [a for a in args if a in SCENARIOS] or list(SCENARIOS)
    leaders = [a for a in args if a in ("host", "guest")] or ["host", "guest"]
    runs = []
    for name in names:
        fixture, fn = SCENARIOS[name]
        for leader in leaders:
            run = Run(f"{name} / {leader} leads")
            print(f"== {run.name}", flush=True)
            try:
                start(fixture)
                fn(run, leader)
                common_checks(run)
            except (TimeoutError, SystemExit) as e:
                run.check("run completed", False, str(e))
            runs.append(run)
    if not keep: stop()
    print()
    for run in runs:
        failed = [w for w, ok, _ in run.results if not ok]
        print(f"{'PASS' if run.passed else 'FAIL'}  {run.name}" + (f"  ({len(failed)} failed: {'; '.join(failed)})" if failed else ""))
    sys.exit(0 if all(r.passed for r in runs) else 1)


if __name__ == "__main__":
    main()
