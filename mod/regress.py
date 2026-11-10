"""Scripted co-op regression run (PLAN.md phase 0).

Starts `play` (host) and `client` (guest) on a copy of a fixture save in its own save folder, drives a scenario with
dev commands (cmd.request), and checks the two copies agree and nobody is stuck.

  python mod/regress.py                 one chained playthrough of every scenario in story order, host-led then guest-led
  python mod/regress.py guest           the same, guest-led only
  python mod/regress.py home guest      one scenario alone, from its own fixture (several names: each alone)
  python mod/regress.py --each          every scenario alone
  python mod/regress.py --fixtures      chained, and the host's world before each step is saved as that step's fixture
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


def wait_log(inst, pattern, timeout, since=0, every=1.0):
    end = time.time() + timeout
    rx = re.compile(pattern)
    while time.time() < end:
        m = rx.search(read_log(inst)[since:])
        if m: return m
        time.sleep(every)
    return None


def until(pred, timeout, every=0.5):
    """Waits until pred() is true (at most timeout seconds); returns whether it became true."""
    end = time.time() + timeout
    while True:
        try:
            if pred(): return True
        except TimeoutError:
            pass
        if time.time() >= end: return False
        time.sleep(every)


def walk(inst, x, z, secs):
    """Walks there with real movement input and returns when the walk has stopped (arrived, or out of time)."""
    mark = len(read_log(inst))
    cmd(inst, f"walkto {x} {z} {secs}")
    wait_log(inst, r"walkto: stopped", secs + 3, since=mark, every=0.25)
    time.sleep(0.5)


def talking(inst=None): return "talking=True" in cmd(inst or HOST, "dlgprobe")


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
        except FileNotFoundError: pass
        except OSError as e: raise SystemExit(f"{inst}'s log is held by another process ({e}); the games would log elsewhere")
    r = ps("launch.ps1", "-Instances", f"{HOST},{GUEST}", "-Names", f"{SAVE_NAME},{SAVE_NAME}-guest",
           "-ExtraArgs", "'-coopautoload','-coopnoinput'")
    if r.returncode != 0: raise SystemExit(f"launch failed: {r.stdout}\n{r.stderr}")
    # In the world on both: the guest has the host's world and sees the host's body.
    if not wait_log(GUEST, r"Names: player 0", 420): raise SystemExit("guest never got into the host's world")
    if not wait_log(HOST, r"Names: player 1", 120): raise SystemExit("host never saw the guest")
    time.sleep(15)


def stop(): ps("stop.ps1")


def common_checks(run):
    """What must hold at the end of any scenario."""
    until(lambda: not talking(HOST) and not talking(GUEST), 8)
    time.sleep(1.5)
    for inst in (HOST, GUEST):
        probe = cmd(inst, "dlgprobe")
        run.check(f"{inst}: no dialogue left on screen", "talking=False" in probe and "options=False" in probe, probe[:160])
        dismiss(inst)
        moved = 0
        for dx, dz in ((3, 3), (-3, -3), (3, -3)):   # indoors one of these is a wall
            a = pos(inst)
            walk(inst, round(a[0] + dx, 1), round(a[2] + dz, 1), 4)
            b = pos(inst)
            moved = ((a[0] - b[0]) ** 2 + (a[2] - b[2]) ** 2) ** 0.5
            if moved > 0.7: break
        run.check(f"{inst}: can walk", moved > 0.7, f"moved {moved:.2f} m")
    # A digest round runs every 15 s: one full round after things settle, then up to two more for it to come out equal.
    time.sleep(16)
    def same():
        cats = [c.strip() for c in cmd(GUEST, "digest").splitlines()[0].split(";") if c.strip()]
        return cats and all(" same" in c for c in cats)
    until(same, 32, every=4)
    d = cmd(GUEST, "digest").splitlines()[0]  # log lines written just after it follow
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
    run.check("the door talk started on the host", until(talking, 16))
    led_by_guest = "conversation led by " + SAVE_NAME + "-guest" in read_log(HOST)[mark:]
    run.check(f"the {leader} leads the talk", led_by_guest == (leader == "guest"), f"led by guest: {led_by_guest}")
    run.check("the host's own story started it (no callback-less copy)", "nothing here started" not in read_log(HOST)[mark:])
    for _ in range(25):
        if "talking=False" in cmd(HOST, "dlgprobe"): break
        cmd(lead, "next")
        time.sleep(1.5)
    until(lambda: all(gate_state(i)[0] for i in (HOST, GUEST)), 10)
    for inst in (HOST, GUEST):
        ok, g = gate_state(inst)
        run.check(f"{inst}: gate open after the talk", ok, g[:200])


def tutorial_at(inst):
    m = re.search(r"tutorial block (\d+) task (\d+)", cmd(inst, "tutorial"))
    return (int(m.group(1)), int(m.group(2))) if m else None


def finish_talk(lead, tries=70):
    for _ in range(tries):
        if "talking=False" in cmd(HOST, "dlgprobe"): return True
        cmd(lead, "next")
        time.sleep(1.0)
    return False


def dismiss(inst):
    """Presses OK / Continue on whatever popups are up (they hold the player until answered)."""
    n = 0
    for _ in range(6):
        b = (cmd(inst, "buttons").splitlines() or [""])[0]
        label = next((l for l in ("OK", "Continue", "CLAIM") if f":'{l}'" in b), None)
        if not label: break
        cmd(inst, f"click {label}")
        n += 1
        time.sleep(1.5)
    return n


GATE, INWARDS = (82.78, 16.34), (-0.66, -0.75)   # the yard gate, and the way in through it


def in_yard(inst):
    x, _, z = pos(inst)
    return (x - GATE[0]) * INWARDS[0] + (z - GATE[1]) * INWARDS[1] > 0


def yard(run, leader):
    """After the door talk: in through the gate (it shuts behind), the old woman's talk, the book on the bench, the
    three journal tasks, the gate opens again and she has one more thing to say. Fixture: after the door talk."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    walk(lead, 84.2, 17.9, 8)   # in front of the gate, then through it
    walk(lead, 80.6, 13.6, 8)
    for inst in (HOST, GUEST):
        g = cmd(inst, "gates")
        run.check(f"{inst}: gate shut behind the {leader}", "open=False" in g and "locked=True" in g, g[:200])
        run.check(f"{inst}: is in the yard", in_yard(inst), str(pos(inst)))
    mark = len(read_log(HOST))
    r = cmd(lead, "interact AtHome")
    run.check("the old woman can be talked to", "interacted with Baba Jaga_AtHome" in r, r[:160])
    run.check("her talk started on the host", until(talking, 8))
    run.check("her talk ended", finish_talk(lead))
    until(lambda: all("Collectible_Journal/Player Detection on=True" in cmd(i, "detections 12") for i in (HOST, GUEST)), 8)
    run.check("the host's story ran it", "nothing here started" not in read_log(HOST)[mark:])
    for inst in (HOST, GUEST):
        d = cmd(inst, "detections 12")
        run.check(f"{inst}: the book can be taken", "Collectible_Journal/Player Detection on=True" in d, d[:300])
    walk(lead, 82.3, 13.7, 6)
    r = cmd(lead, "interact Journal")
    run.check("the book is taken", "interacted with Collectible_Journal" in r, r[:160])
    until(lambda: tutorial_at(HOST) == (2, 1) and tutorial_at(GUEST) == (2, 1), 12)
    steps = [(None, 1, "took the book"), ("journal open", 2, "opened the journal"),
             ("journal page Quests", 3, "went to the Quests tab"), ("journal closeui", 4, "closed the journal")]
    mark = len(read_log(HOST))
    for c, task, what in steps:
        if c:
            for _ in range(5):   # the journal opens once this copy knows it has it
                if "manager on" in cmd(lead, c): break
                time.sleep(2)
            until(lambda: tutorial_at(HOST) == (2, task) and tutorial_at(GUEST) == (2, task), 10)
        for inst in (HOST, GUEST):
            at = tutorial_at(inst)
            run.check(f"{inst}: tutorial moved on when the {leader} {what}", at == (2, task), f"at {at}")
    until(lambda: all(gate_state(i)[0] for i in (HOST, GUEST)), 10)
    for inst in (HOST, GUEST):
        ok, g = gate_state(inst)
        run.check(f"{inst}: gate open after the journal", ok, g[:200])
    # She has one more thing to say as they head out of the yard.
    walk(other, 84.6, 18.3, 6)
    cmd(lead, "walkto 84.2 17.9 6")
    run.check("her next talk started on the host as they left", until(talking, 20))
    ended = False
    for _ in range(30):
        if "talking=False" in cmd(HOST, "dlgprobe"): ended = True; break
        for inst in (lead, other): cmd(inst, "next")   # whoever leads it
        time.sleep(1.5)
    run.check("her next talk ended", ended)
    for inst in (HOST, GUEST):   # "you have been told about a location", "her birds are yours to use"
        until(lambda: any(f":'{l}'" in cmd(inst, "buttons") for l in ("OK", "Continue", "CLAIM")), 6)
        run.check(f"{inst}: got the popups that follow it", dismiss(inst) > 0)
    time.sleep(2)
    run.check("the host's story ran that too", "nothing here started" not in read_log(HOST)[mark:])
    run.check("both tutorials at the same place", tutorial_at(HOST) == tutorial_at(GUEST), f"{tutorial_at(HOST)} / {tutorial_at(GUEST)}")


def trial_tasks(inst):
    """The current trial's tasks as a string of x (done) and . (open)."""
    m = re.search(r"tasks \[([x.]*)\]", cmd(inst, "trials"))
    return m.group(1) if m else ""


def tasks_done(run, upto, why):
    """Tasks 0..upto of the trial are done on both copies (the host decides, the guest follows)."""
    want = "x" * (upto + 1)
    for _ in range(10):
        if all(trial_tasks(i).startswith(want) for i in (HOST, GUEST)): break
        time.sleep(1.5)
    for inst in (HOST, GUEST):
        got = trial_tasks(inst)
        run.check(f"{inst}: task {upto} done once {why}", got.startswith(want) and not got.startswith(want + "x"), got)


def collect(inst, kind, n):
    got = 0
    for _ in range(n):
        if "Collected" in cmd(inst, f"collect {kind}").splitlines()[0].split(":")[-1]: got += 1
        time.sleep(1.2)
    return got


def lutki(run, leader):
    """After the yard: mushrooms and honeycombs for the soup (both players gather), the Lutki's four planks (both take
    some), the talisman (the leader takes it)."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST): dismiss(inst)
    a, b = collect(lead, "Mushroom", 5), collect(other, "Mushroom", 5)
    run.check("ten mushrooms gathered between them", a + b == 10, f"{a} + {b}")
    tasks_done(run, 0, "the mushrooms are in")
    a, b = collect(lead, "Honey", 1), collect(other, "Honey", 1)
    run.check("honeycombs gathered by both", a + b == 2, f"{a} + {b}")
    tasks_done(run, 1, "the honeycombs are in")
    # The Lutki village: planks lie on a trapped Lutki; with all four of its planks gone it stands and the talisman can be taken.
    cmd(lead, "tp 0 108 9"); cmd(other, "tp 2 108 9")
    time.sleep(4)
    for inst in (HOST, GUEST):
        d = cmd(inst, "detections 25")
        run.check(f"{inst}: the talisman can't be taken yet", "talisman_01/Player Detection on=False" in d, d[:200])
    for k in range(12):
        if "nothing" in cmd(lead if k % 2 == 0 else other, "collect Wood").splitlines()[0]: break
        time.sleep(1.5)
    until(lambda: all("talisman_01/Player Detection on=True" in cmd(i, "detections 25") for i in (HOST, GUEST)), 12)
    for inst in (HOST, GUEST):
        d = cmd(inst, "detections 25")
        run.check(f"{inst}: the talisman can be taken once the planks are gone", "talisman_01/Player Detection on=True" in d, d[:200])
    r = cmd(lead, "interact talisman")
    run.check("the talisman is taken", "interacted with" in r, r[:160])
    tasks_done(run, 2, f"the {leader} has the talisman")
    for inst in (HOST, GUEST):
        st = cmd(inst, "status")
        run.check(f"{inst}: has the teleport charm", "Backport" in st, st[-120:])
        until(lambda: any(f":'{l}'" in cmd(inst, "buttons") for l in ("OK", "Continue", "CLAIM")), 8)
        run.check(f"{inst}: got the popup for it", dismiss(inst) > 0)


def home(run, leader):
    """With the talisman: teleport home, back into the yard (her talk starts by itself), then inside to talk to her;
    she sends them for the pine branch."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST): dismiss(inst)
    cmd(lead, "tp 60 40 4")
    time.sleep(3)
    cmd(lead, "backport port")
    until(lambda: abs(pos(lead)[0] - 83.8) < 3 and abs(pos(lead)[2] - 16.6) < 3, 14)
    time.sleep(2)
    for inst in (HOST, GUEST): dismiss(inst)
    x, _, z = pos(lead)
    run.check(f"the {leader} is back at the gate", abs(x - 83.8) < 3 and abs(z - 16.6) < 3, f"{x:.1f}, {z:.1f}")
    tasks_done(run, 3, f"the {leader} teleported home")
    mark = len(read_log(HOST))
    cmd(lead, "walkto 80.6 13.6 8")   # into the yard: the yard's trigger switches itself off here (it crashed the host once)
    run.check("her talk started on the host as they came in", until(talking, 15))
    run.check("her talk ended", finish_talk(lead))
    until(lambda: "current=Tutorial/C00_Tutorial/BabaJaga/005" in cmd(HOST, "pieces"), 6)
    p = cmd(HOST, "pieces")
    run.check("she waits inside with the next talk", "current=Tutorial/C00_Tutorial/BabaJaga/005" in p, p[:80])
    walk(lead, 80.6, 13.6, 8)    # the talk stopped them somewhere on the way in: line up with her door first,
    walk(lead, 78.3, 9.5, 10)    # a straight walk from elsewhere wedges into the wall beside it
    x, _, z = pos(lead)
    run.check(f"the {leader} is inside her house", abs(x - 78.3) < 1.6 and abs(z - 9.5) < 1.6, f"{x:.1f}, {z:.1f}")
    r = cmd(lead, "interact AtHome")
    run.check("she can be talked to inside", "interacted with Baba Jaga_AtHome" in r, r[:160])
    run.check("that talk started on the host", until(talking, 8))
    run.check("that talk ended", finish_talk(lead))
    tasks_done(run, 4, "she has been talked to inside")
    run.check("the host's story ran both talks", "nothing here started" not in read_log(HOST)[mark:])
    for inst in (HOST, GUEST): dismiss(inst)
    run.check("both tutorials at the same place", tutorial_at(HOST) == tutorial_at(GUEST), f"{tutorial_at(HOST)} / {tutorial_at(GUEST)}")


# In story order. Each starts from its own fixture when run alone; chained, each starts where the one before ended.
SCENARIOS = {"door": ("pre-door-1.0.32", door), "yard": ("after-door-1.0.32", yard),
             "lutki": ("after-yard-1.0.32", lutki), "home": ("talisman-1.0.32", home)}


def between_checks(run, name):
    """After a step of a chained run: nobody left in a talk, and both copies at the same place in the tutorial."""
    for inst in (HOST, GUEST): dismiss(inst)
    until(lambda: not talking(HOST) and not talking(GUEST), 8)
    run.check(f"after {name}: no dialogue left on screen", not talking(HOST) and not talking(GUEST))
    run.check(f"after {name}: both tutorials at the same place", tutorial_at(HOST) == tutorial_at(GUEST), f"{tutorial_at(HOST)} / {tutorial_at(GUEST)}")


def save_fixture(name):
    """The host's world as it is now becomes local/saves/<name>; an existing one moves to local/backup, never deleted."""
    cmd(HOST, "save")
    time.sleep(10)
    folder = os.path.join(GAME, HOST, "CoopSaves", SAVE_NAME, "- 1 -")
    slot = max(os.listdir(folder), key=lambda d: os.path.getmtime(os.path.join(folder, d)))
    dst = os.path.join(ROOT, "local", "saves", name)
    if os.path.isdir(dst):
        old = os.path.join(ROOT, "local", "backup", "fixtures", name + "_" + time.strftime("%Y-%m-%d_%H%M%S"))
        os.makedirs(os.path.dirname(old), exist_ok=True)
        shutil.move(dst, old)
    shutil.copytree(os.path.join(folder, slot), os.path.join(dst, "- 1 -", "1"))
    print(f"  saved the host's world as fixture {name} (from slot {slot})", flush=True)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    keep = "--keep" in sys.argv
    each = "--each" in sys.argv            # every scenario from its own fixture, instead of one chained playthrough
    fixtures = "--fixtures" in sys.argv    # chained: save the host's world before each step as that step's fixture
    names = [n for n in SCENARIOS if n in args]
    leaders = [a for a in args if a in ("host", "guest")] or ["host", "guest"]
    order = list(SCENARIOS)
    runs = []
    t0 = time.time()
    if names or each:
        # Isolated: the named scenarios (or all with --each), each from its own fixture.
        for name in names or order:
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
    else:
        # Chained: one playthrough per leader, every scenario in story order in the same session.
        for leader in leaders:
            run = Run(f"playthrough ({', '.join(order)}) / {leader} leads")
            print(f"== {run.name}", flush=True)
            try:
                start(SCENARIOS[order[0]][0])
                for i, name in enumerate(order):
                    print(f" -- {name}", flush=True)
                    if fixtures and i > 0 and leader == leaders[0]: save_fixture(SCENARIOS[name][0])
                    SCENARIOS[name][1](run, leader)
                    if i < len(order) - 1: between_checks(run, name)
                common_checks(run)
            except (TimeoutError, SystemExit) as e:
                run.check("run completed", False, str(e))
            runs.append(run)
    if not keep: stop()
    print()
    for run in runs:
        failed = [w for w, ok, _ in run.results if not ok]
        print(f"{'PASS' if run.passed else 'FAIL'}  {run.name}" + (f"  ({len(failed)} failed: {'; '.join(failed)})" if failed else ""))
    print(f"{(time.time() - t0) / 60:.1f} min")
    sys.exit(0 if all(r.passed for r in runs) else 1)


if __name__ == "__main__":
    main()
