import math
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


def keep_logs(run):
    """A failed run's logs go to local/logs before the next start overwrites them."""
    if run.passed: return
    tag = time.strftime("%Y-%m-%d_%H%M%S") + "_" + re.sub(r"[^a-z]+", "-", run.name.split(" / ")[-1])
    os.makedirs(os.path.join(ROOT, "local", "logs"), exist_ok=True)
    for inst in (HOST, GUEST):
        try: shutil.copyfile(log_path(inst), os.path.join(ROOT, "local", "logs", f"{tag}_{inst}.log"))
        except OSError: pass
    print(f"  logs kept as local/logs/{tag}_*.log", flush=True)


def log_tail(inst, offset):
    """The log from a byte offset on (the whole log is megabytes late in a run; a command only needs what follows it)."""
    try:
        with open(log_path(inst), "rb") as f:
            f.seek(offset)
            return f.read().decode("utf-8", errors="replace")
    except OSError:
        return ""


def cmd(inst, text, wait=8.0):
    """Runs a dev command; returns its result (the text after '->', plus following lines). A teleport is checked: a
    talk that is just ending puts a player back where it gathered them, and the step that follows then acted from
    16 m away (seen once the suite got fast)."""
    r = send(inst, text, wait)
    m = re.match(r"tp (-?[\d.]+) (-?[\d.]+) (-?[\d.]+)$", text)
    if not m: return r
    x, z = float(m.group(1)), float(m.group(2))
    for _ in range(4):
        time.sleep(0.5)
        at = re.search(r"pos \(([-\d.]+), ([-\d.]+), ([-\d.]+)\)", send(inst, "pos"))
        if not at or (abs(float(at.group(1)) - x) < 2 and abs(float(at.group(3)) - z) < 2): break
        r = send(inst, text, wait)
    return r


def send(inst, text, wait=8.0):
    """One dev command, as it is."""
    try: before = os.path.getsize(log_path(inst))
    except OSError: before = 0
    req = os.path.join(GAME, inst, "cmd.request")
    with open(req + ".tmp", "w", newline="") as f: f.write(text)
    os.replace(req + ".tmp", req)  # whole at once: the game reads and deletes it every frame
    ok, bad = f"[cmd] {text} -> ", f"[cmd] {text} failed"   # whole command: "useitem X" is not "useitem X at 4"
    end = time.time() + wait
    while time.time() < end:
        new = log_tail(inst, before)
        i, n = new.find(ok), len(ok)
        if i < 0: i, n = new.find(bad), len(bad) - len("failed")
        if i >= 0 and "\n" in new[i:]:
            time.sleep(0.08)   # lines the command logged right after its answer
            return log_tail(inst, before)[i + n:].strip()
        time.sleep(0.04)
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
    until(same, 48, every=4)
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


def tutorial_started(inst, task):
    """The game has put that prompt of the current tutorial block up (doing what it asks before then doesn't count)."""
    return any(l.strip().startswith(f"{task}: ") and "started=True" in l for l in cmd(inst, "tutorial").splitlines())


def finish_talk(lead, tries=70):
    """The talk clicked to its end by lead, as fast as the game takes clicks (the game does the clicking: dev
    `talkthrough`). False when it is still going after about tries seconds, or stops at a choice."""
    if "talking=False" in cmd(HOST, "dlgprobe"): return True
    cmd(lead, f"talkthrough {tries}")
    end = time.time() + tries
    while time.time() < end:
        probe = cmd(HOST, "dlgprobe")
        if "talking=False" in probe: return True
        if "options=True" in probe: break
        time.sleep(0.3)
    cmd(lead, "talkthrough off")
    return False


def dismiss(inst):
    """Presses OK / Continue on whatever popups are up (they hold the player until answered)."""
    n = 0
    for _ in range(6):
        b = (cmd(inst, "buttons").splitlines() or [""])[0]
        label = next((l for l in ("OK", "Continue", "CLAIM") if f":'{l}'" in b), None)
        if not label and b.strip() == "IconButton:''": label = "IconButton"   # the chapter-complete card's only button
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
            # The prompt has to be up first: the suite used to be slow enough for that by accident.
            until(lambda: tutorial_started(HOST, task - 1) or tutorial_at(HOST) != (2, task - 1), 15)
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
    time.sleep(2.5)
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


def box_trace(inst, since):
    """What dev `dlgtrace` logged since a log mark: (in a talk, dialogue generation, box visible, camera session live)."""
    rx = re.compile(r"\[dlgtrace\].*? talk=(\w+) passive=(\w+) gen=(\d+) .*? root=(\w+) .*?\| live=(\w+)")
    return [(m.group(1) == "True" and m.group(2) == "False", int(m.group(3)), m.group(4) == "True", m.group(5) == "True")
            for m in rx.finditer(read_log(inst)[since:])]


def box_summary(inst, since):
    """Of a talk as that copy's box ran it: its dialogues (generations) in order, how often the box went away inside
    one of them after it had been up, and how many camera sessions started."""
    gens, hidden, sessions, shown, live = [], 0, 0, set(), False
    for talk, gen, root, cam in box_trace(inst, since):
        if talk:
            if gen not in gens: gens.append(gen)
            if root: shown.add(gen)
            elif gen in shown: hidden += 1; shown.discard(gen)
        if cam and not live: sessions += 1
        live = cam
    return gens, hidden, sessions


VILLAGER = "POI_HangingMan"   # in the tutorial village; has things to ask about, and one answer of three lines


def villager(run, leader):
    """A villager with things to ask about: a greeting, the options, an answer of several lines, the options again,
    goodbye. Each of those is its own dialogue on the host, and the other copy must run the same ones: one box per
    dialogue (not one per line), the box up under the options, one camera session."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST): dismiss(inst)
    cmd(other, f"facenpc {VILLAGER}")
    time.sleep(2)
    cmd(lead, f"facenpc {VILLAGER}")
    time.sleep(13)   # the detectors both crossed arriving are forgotten (the host holds "who set this off" for 10 s)
    for inst in (HOST, GUEST): cmd(inst, "dlgtrace on")
    marks = {inst: len(read_log(inst)) for inst in (HOST, GUEST)}

    def options(inst): return "options=True" in cmd(inst, "dlgprobe")

    def to_options():
        for _ in range(12):
            if options(HOST): return True
            cmd(lead, "next")
            time.sleep(1.5)
        return False

    r = cmd(lead, f"say {VILLAGER}")
    run.check("the villager's talk started on the host", until(talking, 8), r[:160])
    led_by_guest = "conversation led by " + SAVE_NAME + "-guest" in read_log(HOST)[marks[HOST]:]
    run.check(f"the {leader} leads the talk", led_by_guest == (leader == "guest"), f"led by guest: {led_by_guest}")
    run.check("the options opened after the greeting", to_options())
    run.check("the guest has the options too", until(lambda: options(GUEST), 5))
    for inst in (HOST, GUEST):
        p = cmd(inst, "dlgprobe")
        run.check(f"{inst}: the greeting stays up under the options", p.startswith("talking=True"), p[:80])
    cmd(lead, "pick 0")
    run.check("the answer started on the host", until(lambda: talking(HOST) and not options(HOST), 6))
    run.check("the options opened again after the answer", to_options())
    run.check("the guest has them again", until(lambda: options(GUEST), 5))
    m = re.search(r"offered=(.*?) text=", cmd(HOST, "dlgprobe"), re.S)
    offered = [o.strip() for o in m.group(1).split("|")] if m else []
    run.check("the last option is the goodbye", bool(offered) and offered[-1] == "Goodbye", str(offered)[:200])
    cmd(lead, f"pick {max(len(offered) - 1, 0)}")
    time.sleep(2)
    ended = False
    for _ in range(8):
        if "talking=False" in cmd(HOST, "dlgprobe"): ended = True; break
        cmd(lead, "next")
        time.sleep(1.5)
    run.check("the talk ended on the host", ended)
    run.check("and on the guest", until(lambda: not talking(GUEST) and not options(GUEST), 6))
    time.sleep(1.5)
    h, g = box_summary(HOST, marks[HOST]), box_summary(GUEST, marks[GUEST])
    for inst in (HOST, GUEST): cmd(inst, "dlgtrace off")
    run.check("the host's talk was several dialogues", len(h[0]) >= 3, str(h))
    run.check("the guest's box ran one dialogue per dialogue of the host's", len(g[0]) == len(h[0]), f"host {h[0]}, guest {g[0]}")
    run.check("the guest's box never went away inside a dialogue", g[1] == 0, f"{g[1]} times")
    run.check("the guest's camera kept one session", g[2] == 1, f"{g[2]} (host {h[2]})")


# In story order. Each starts from its own fixture when run alone; chained, each starts where the one before ended.
def detector(inst, name):
    """The `detectors` line of the player detector whose path ends with name ('' when it isn't listed)."""
    return next((l for l in cmd(inst, "detectors 150").splitlines() if name + " on=" in l), "")


def chicken(run, leader):
    """After the indoor talk: the crooked pine's branch, a chicken carried to the old woman behind the house, her talk
    at the ritual site, the chicken put in the nest. The leader does all of it; the other stands away."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST): dismiss(inst)
    mark = len(read_log(HOST))
    cmd(lead, "tp 102 41 4"); cmd(other, "tp 100 43 4")
    # Leaving the village for the first time has a villager call after them ("you tread carefully"); whoever left
    # first leads it and the talk gathers both beside them, away from the pine.
    if until(talking, 4):
        finish_any(lead, other)
        cmd(lead, "tp 102 41 4"); cmd(other, "tp 100 43 4")
        time.sleep(2.5)
    r = cmd(lead, "interact Pinewood")
    run.check("the pine branch is taken", "interacted with" in r, r[:160])
    tasks_done(run, 5, f"the {leader} has the pine branch")
    for inst in (HOST, GUEST):
        run.check(f"{inst}: has the pine branch as a tool", "PineBranch" in cmd(inst, "status"))
        dismiss(inst)
    carrier, stander = (other, lead) if SPLIT else (lead, other)
    # A chicken from the yard; the old woman's lines behind the house depend on someone carrying one.
    cmd(carrier, "tp 80 20 4"); cmd(stander, "tp 60 30 4")
    time.sleep(2.5)
    r = cmd(carrier, "interact Chicken_01")
    run.check("a chicken is picked up", "interacted with" in r, r[:160])
    time.sleep(4)
    for inst in (HOST, GUEST):
        d = detector(inst, "Dialogue_BringChicken")
        run.check(f"{inst}: she will greet whoever brings the chicken", "on=True" in d, d[-200:] or "not listed")
    cmd(carrier, "tp 86 4 4")
    time.sleep(3)
    walk(carrier, 84, -1, 8)
    run.check("her greeting started on the host", until(lambda: talking(HOST), 10))
    run.check("it is the one for a chicken brought", "Didn" not in cmd(HOST, "dlgprobe"), cmd(HOST, "dlgprobe")[-120:])
    run.check("her greeting ended", finish_any(carrier, stander))
    # She walks to the ritual site; her talk there starts when a player comes up to her.
    run.check("she reached the ritual site", until(lambda: "on=True" in detector(HOST, "Dialogue_SiteDescription/PlayerTrigger"), 90, 2))
    cmd(carrier, "tp 50 -19 5")
    time.sleep(3)
    walk(carrier, 41.6, -22.6, 10)
    run.check("her talk at the site started on the host", until(lambda: talking(HOST), 10))
    run.check("that talk ended", finish_any(carrier, stander))
    tasks_done(run, 6, f"the {leader} brought the chicken")
    walk(carrier, 31, -26.5, 12)
    r = cmd(carrier, "drop")
    run.check("the chicken is put down at the nest", "dropped" in r, r[:120])
    run.check("she speaks about the branch next", until(lambda: talking(HOST), 15))
    run.check("that talk ended", finish_any(carrier, stander))
    tasks_done(run, 7, "the chicken is in the nest")
    log = read_log(HOST)[mark:]
    run.check("the host placed the chicken once", log.count("Ritual: PlacedChicken") == 1, str(log.count("Ritual: PlacedChicken")))
    for inst in (HOST, GUEST): dismiss(inst)


def sweep(inst, ring, spots=5):
    """Sweeps every spot of a ritual ring with the pine branch, standing on each in turn."""
    tools = cmd(inst, "equip")
    m = re.search(r"(\d+) PineBranch", tools)
    if not m: return False
    cmd(inst, f"equip {m.group(1)}")
    time.sleep(1.5)
    for spot in range(spots):
        if "at spot" not in cmd(inst, f"useitem {ring} at {spot}"): return False
        time.sleep(0.8)
        for _ in range(6):
            cmd(inst, "useitem press")
            time.sleep(0.3)
        cmd(inst, "useitem release")
        time.sleep(1)
    return True


def ring(inst, name):
    """A ring's spots as the `useitem` command shows them, e.g. 'True [x(off)1.0/1.0 ...]'."""
    return first_line(cmd(inst, f"useitem {name}").split("solved=")[-1])


def cleanse(run, leader):
    """The four rings of the ritual site, swept with the pine branch: the leader takes the first and third, the other
    the second and fourth. Each counts once, on the host."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST): dismiss(inst)
    mark = len(read_log(HOST))
    for n, who in ((1, lead), (2, other), (3, lead), (4, other)):
        name = f"Cleanse_0{n}"
        run.check(f"ring {n} is ready", until(lambda: ring(HOST, name).startswith("False [.") and "(off)" not in ring(HOST, name), 90, 2), ring(HOST, name)[:80])
        run.check(f"ring {n} swept by {who}", sweep(who, name))
        ok = until(lambda: all(ring(i, name).startswith("True") for i in (HOST, GUEST)), 12)
        run.check(f"ring {n} is done on both copies", ok, f"{ring(HOST, name)[:60]} / {ring(GUEST, name)[:60]}")
        run.check(f"she says so after ring {n}", until(lambda: talking(HOST), 10))
        run.check(f"that talk ended", finish_talk(who))
        time.sleep(3)
    tasks_done(run, 8, "all four rings are swept")
    log = read_log(HOST)[mark:]
    n = log.count("Ritual: CleansedSingleSite")
    run.check("the host counted four rings", n == 4, str(n))
    top = max([int(x) for x in re.findall(r"tutorial_ritualSitesCleansed=(\d+)", log)] or [0])
    run.check("the tracker counted four rings", top == 4, str(top))
    for inst in (HOST, GUEST): dismiss(inst)


SPLIT = False   # a "mixed" run: the two players share each step (one starts it, the other ends it) and swap who starts


def finish_any(lead, other):
    """Clicks a talk to its end whoever leads it: the leader of the step first, then the other."""
    if "talking=False" in cmd(HOST, "dlgprobe"): return True
    for inst in (lead, other): cmd(inst, "talkthrough 60")
    end = time.time() + 60
    while time.time() < end:
        probe = cmd(HOST, "dlgprobe")
        if "talking=False" in probe: return True
        if "options=True" in probe: break
        time.sleep(0.3)
    for inst in (lead, other): cmd(inst, "talkthrough off")
    return False


def count(log, what): return log.count(what)


def egg(run, leader):
    """After the rings: the big egg from the old woman's house, carried to the site, put into the oven."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST): dismiss(inst)
    mark = len(read_log(HOST))
    cmd(other, "tp 45 -35 5"); cmd(lead, "tp 78.5 12 4.5")
    time.sleep(2.5)
    r = cmd(lead, "interact Ritual_Egg")
    run.check("the egg is picked up", "interacted with" in r, r[:160])
    time.sleep(4)
    run.check("the host knows who carries the egg", "Ritual_Egg" in cmd(HOST, "carry"), cmd(HOST, "carry")[:120])
    cmd(lead, "tp 34 -30 5")
    run.check("her talk about the egg started on the host", until(lambda: talking(HOST), 15))
    run.check("that talk ended", finish_talk(lead))
    tasks_done(run, 9, f"the {leader} brought the egg")
    placer, bringer = (other, lead) if SPLIT else (lead, other)
    if SPLIT:
        cmd(bringer, "drop")
        time.sleep(3)
        cmd(placer, "tp 34 -30 5")
        time.sleep(3)
        cmd(placer, "interact Ritual_Egg")
        time.sleep(4)
        run.check("the other player took the egg over", "Ritual_Egg" in cmd(HOST, "carry") and "Ritual_Egg" in cmd(placer, "carry"), cmd(HOST, "carry")[:120])
        cmd(bringer, "tp 45 -35 5")
    walk(placer, 29.5, -27, 10)
    r = cmd(placer, "interact RitualEggSpot")
    run.check("the egg is put into the oven", "interacted with" in r, r[:160])
    run.check("she speaks once it is in", until(lambda: talking(HOST), 15))
    run.check("that talk ended", finish_any(placer, bringer))
    tasks_done(run, 10, "the egg is in the oven")
    for inst in (HOST, GUEST):
        run.check(f"{inst}: nobody carries the egg any more", "nothing carried" in cmd(inst, "carry"), cmd(inst, "carry")[:120])
    log = read_log(HOST)[mark:]
    run.check("the host placed the egg once", count(log, "Ritual: PlacedEgg") == 1, str(count(log, "Ritual: PlacedEgg")))
    for inst in (HOST, GUEST): dismiss(inst)


def pack_count(inst, item):
    m = re.search(r"(\d+) " + item + r"\b", cmd(inst, "inv"))
    return int(m.group(1)) if m else 0


def offerings(run, leader):
    """Honeycomb into the first bowl, the walk to the next, grain (gathered by her house) into the second."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST): dismiss(inst)
    mark = len(read_log(HOST))
    honey = pack_count(HOST, "Item_Collected_Tier1_Honeycomb")
    cmd(other, "tp 45 -35 5"); cmd(lead, "tp 29 -29 4")
    time.sleep(2.5)
    r = cmd(lead, "interact honeycombs_bunch")
    run.check("honeycomb goes into the first bowl", "interacted with" in r, r[:160])
    run.check("she speaks about the next bowl", until(lambda: talking(HOST), 15))
    run.check("that talk ended", finish_any(lead, other))
    for inst in (HOST, GUEST):
        n = pack_count(inst, "Item_Collected_Tier1_Honeycomb")
        run.check(f"{inst}: the shared pack paid one honeycomb", n == honey - 1, f"{honey} before, {n} now")
    # She walks to the second bowl and calls from there (nobody set that talk off, so the host leads it): it follows
    # so closely that the clicking above usually ends it too.
    if until(lambda: talking(HOST), 20, 2): run.check("her call from the second bowl ended", finish_any(HOST, GUEST))
    # Her call can also start a moment after that check (seen: it then stood open on the host for good, and the bowl
    # only takes grain once it is over), so it is ended whenever it shows up while waiting for the bowl.
    def bowl_ready():
        if talking(HOST): finish_any(HOST, GUEST)
        return "grain_bunch on=True" in cmd(HOST, "detections 80")
    run.check("the second bowl can be filled", until(bowl_ready, 90, 3))
    second, first = (other, lead) if SPLIT else (lead, other)
    cmd(second, "tp 85 4 4")
    time.sleep(2.5)
    for _ in range(3):
        cmd(second, "collect Grain")
        time.sleep(2.5)
    run.check("grain is in the shared pack", all(pack_count(i, "Item_Collected_Tier0_Grain") == 3 for i in (HOST, GUEST)), cmd(HOST, "inv")[:200])
    cmd(second, "tp 27 -24 4")
    time.sleep(2.5)
    r = cmd(second, "interact grain_bunch")
    run.check("grain goes into the second bowl", "interacted with" in r, r[:160])
    run.check("she speaks once both are filled", until(lambda: talking(HOST), 15))
    run.check("that talk ended", finish_any(second, first))
    tasks_done(run, 11, "both offerings are placed")
    log = read_log(HOST)[mark:]
    run.check("the host counted two offerings", count(log, "Ritual: PlacedSingleOffering") == 2, str(count(log, "Ritual: PlacedSingleOffering")))
    run.check("and all of them once", count(log, "Ritual: PlacedAllOfferings") == 1, str(count(log, "Ritual: PlacedAllOfferings")))
    for inst in (HOST, GUEST): dismiss(inst)


def dissolved(inst):
    m = re.search(r"amountDissolved=([\d.]+)", cmd(inst, "fields BoneDissolver"))
    return float(m.group(1)) if m else -1.0


def bones(run, leader):
    """The talk that hands over the Svarog charm, then the bones dissolved by walking past them with it. Only the
    leader walks; the other stands away with empty hands."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST): dismiss(inst)
    mark = len(read_log(HOST))
    cmd(other, "tp 60 -30 5"); cmd(lead, "tp 28 -22 4")
    time.sleep(2.5)
    r = cmd(lead, "interact InteractDialogue_OfferingComplete")
    run.check("she is talked to", "interacted with" in r, r[:160])
    run.check("her talk started on the host", until(lambda: talking(HOST), 10))
    same_view(run, "her talk")
    run.check("that talk ended", finish_talk(lead))
    tasks_done(run, 12, f"the {leader} talked to her")
    for inst in (HOST, GUEST):
        run.check(f"{inst}: has the charm", "SvarogCharm" in cmd(inst, "equip"), cmd(inst, "equip")[:160])
    # A talk gathers everyone beside the starter: away again, and with empty hands, so only the leader's charm counts.
    # Split: both walk the circle, on opposite sides, and every bone still counts once.
    if not SPLIT: cmd(other, "tp 60 -30 5"); cmd(other, "equip 0")
    for who in ((lead, other) if SPLIT else (lead,)):
        if "holding SvarogCharm" not in cmd(who, "equip"):
            m = re.search(r"(\d+) SvarogCharm", cmd(who, "equip"))
            if m: cmd(who, f"equip {m.group(1)}")
    done = False
    for k in range(40):   # five times round at most; a slow PC needs more than three
        a = k * math.pi / 4
        cmd(lead, f"tp {27 + 4 * math.cos(a):.1f} {-27 + 4 * math.sin(a):.1f} 4.5")
        if SPLIT: cmd(other, f"tp {27 - 4 * math.cos(a):.1f} {-27 - 4 * math.sin(a):.1f} 4.5")
        time.sleep(2.5)
        if trial_tasks(HOST).startswith("x" * 14): done = True; break
    run.check("the bones are gone", done, f"host {dissolved(HOST)}, guest {dissolved(GUEST)}")
    run.check("both copies show the same bones gone", until(lambda: abs(dissolved(HOST) - dissolved(GUEST)) < 0.01, 20, 2), f"host {dissolved(HOST)}, guest {dissolved(GUEST)}")
    if until(lambda: talking(HOST), 10): run.check("her talk after the bones ended", finish_any(lead, other))
    tasks_done(run, 13, "every bone is dissolved")
    log = read_log(HOST)[mark:]
    top = max([int(x) for x in re.findall(r"tutorial_bonesDissolved=(\d+)", log)] or [0])
    run.check("no bone was counted twice", 0 < top <= 28, str(top))
    if SPLIT:
        asked = len(set(re.findall(r"Bones: bone (\d+) started for", log)))
        run.check("some bones fell to the guest's charm and some to the host's", 0 < asked < top, f"{asked} of {top} by the guest")
    run.check("the host finished the bones once", count(log, "Ritual: DissolvedAllBones") == 1, str(count(log, "Ritual: DissolvedAllBones")))
    for inst in (HOST, GUEST): dismiss(inst)


def embers(run, leader):
    """Her talk about the embers, the living embers from her fireplace carried to her, handed over."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST): dismiss(inst)
    mark = len(read_log(HOST))
    cmd(other, "tp 45 -35 5"); cmd(lead, "tp 34 -30 5")
    time.sleep(2.5)
    r = cmd(lead, "interact InteractDialogue_EmbersStart")
    run.check("she is talked to", "interacted with" in r, r[:160])
    run.check("her talk started on the host", until(lambda: talking(HOST), 10))
    same_view(run, "her talk")
    run.check("that talk ended", finish_talk(lead))
    time.sleep(4)
    # The fire tool's unlock popup: until it is answered nothing can be interacted with on that copy.
    for inst in (HOST, GUEST): dismiss(inst)
    fetcher, talker = (other, lead) if SPLIT else (lead, other)
    cmd(talker, "tp 45 -35 5"); cmd(fetcher, "tp 79.5 9 4.5")
    time.sleep(2.5)
    r = cmd(fetcher, "interact CarriableEmbers")
    time.sleep(4)
    run.check("the embers are picked up", "CarriableEmbers" in cmd(HOST, "carry"), r[:120] + " / " + cmd(HOST, "carry")[:120])
    tasks_done(run, 14, f"the {leader} has the embers")
    cmd(fetcher, "tp 40 -29 5")
    time.sleep(2.5)
    walk(fetcher, 42.5, -27, 8)
    run.check("she asks for them", until(lambda: talking(HOST), 15))
    run.check("that talk ended", finish_any(fetcher, talker))
    time.sleep(3)
    cmd(fetcher, "tp 40 -28.5 4")
    time.sleep(3)
    cmd(fetcher, "fields POI_PlayerAreaTrigger@InteractDialogue_GiveEmbers invoke TryToInteract")
    run.check("handing them over starts her talk", until(lambda: talking(HOST), 10))
    run.check("that talk ended", finish_any(fetcher, talker))
    tasks_done(run, 15, "she has the embers")
    for inst in (HOST, GUEST):
        run.check(f"{inst}: nobody carries the embers any more", "nothing carried" in cmd(inst, "carry"), cmd(inst, "carry")[:120])
    log = read_log(HOST)[mark:]
    for step in ("BroughtEmbers", "GaveEmbersToBaba"):
        run.check(f"the host ran {step} once", count(log, "Ritual: " + step) == 1, str(count(log, "Ritual: " + step)))
    # The cutscene starts at once and the game can't be saved while it plays: the finale has no fixture of its own.
    finale(run, leader)


def finale(run, leader):
    """The ritual's cutscene and the house standing up, her words, the house told to sit down, her last talk."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    mark = len(read_log(HOST))
    run.check("the house stands and she speaks", until(lambda: talking(HOST), 120, 2))
    run.check("that talk ended", finish_any(lead, other))
    time.sleep(4)
    for inst in (HOST, GUEST): dismiss(inst)
    cmd(other, "tp 45 -35 5"); cmd(lead, "tp 29 -25 4.5")
    time.sleep(2.5)
    r = cmd(lead, "interact Interact_SitLegsDown")
    run.check("the house is told to sit", "interacted with" in r, r[:160])
    run.check("those words started on the host", until(lambda: talking(HOST), 10))
    run.check("they ended", finish_talk(lead))
    tasks_done(run, 16, "the ritual is finished")
    run.check("her last talk started", until(lambda: talking(HOST), 30, 2))
    run.check("that talk ended", finish_any(HOST, GUEST))
    # Her gifts, the building tool, then one parting line.
    for inst in (HOST, GUEST): dismiss(inst)
    for n in range(4):   # her parting line, then (the building lesson being skipped in co-op) her last task
        if not until(lambda: talking(HOST), 20, 2): break
        run.check(f"her talk {n + 1} after the gifts ended", finish_any(HOST, GUEST))
        for inst in (HOST, GUEST): dismiss(inst)
    for inst in (HOST, GUEST):
        dismiss(inst)
        run.check(f"{inst}: the ritual counts as finished", "ritualFinished=True" in cmd(inst, "status"), cmd(inst, "status")[:160])
        run.check(f"{inst}: can build", "HouseBuilding" in cmd(inst, "status"), cmd(inst, "status")[-120:])
    log = read_log(HOST)[mark:]
    run.check("the host finished the ritual once", count(log, "Ritual: RitualFinished") == 1, str(count(log, "Ritual: RitualFinished")))
    for inst in (HOST, GUEST): dismiss(inst)


def first_line(text): return (text.splitlines() or [""])[0]


def travel(run, leader):
    """The trial done, the house travels on to the first generated map, the game's own way: the host answers the
    question at the map's edge and picks the destination on the travel screen. A guest's try does nothing. The next trial's title card and the old woman's words wait until the guest stands in the new world.
    (Not exercised: walking the house to the edge, and choosing food for the journey.)"""
    for inst in (HOST, GUEST): dismiss(inst)
    hm, gm = len(read_log(HOST)), len(read_log(GUEST))
    cmd(GUEST, "travel")
    time.sleep(6)
    run.check("a guest can't send the house off", "guest map change (new map) skipped" in read_log(GUEST)[gm:]
              and "host travels" not in read_log(HOST)[hm:])
    # What the game does when the house is walked into the fog at the map's edge (the walk itself is not driven here).
    cmd(HOST, "fields MapBorderFogManager invoke TriggerTravel")
    run.check("the host is asked whether to travel on", until(lambda: "Travel onwards!" in cmd(HOST, "buttons"), 10), first_line(cmd(HOST, "buttons"))[:120])
    cmd(HOST, "click Travel onwards!")
    time.sleep(4)
    ui = first_line(cmd(HOST, "travelui"))
    run.check("the travel screen offers the forest spirit's map", "LeshyFirstOffering" in ui, ui[:200])
    cmd(HOST, "travelui dest 0")
    time.sleep(1)
    cmd(HOST, "travelui go")
    run.check("the host's house travels", wait_log(HOST, r"World transfer: host travels", 20, since=hm))
    run.check("the guest is told", wait_log(GUEST, r"the host is travelling to another map", 20, since=gm))
    run.check("the trial's title card waits for the guest", wait_log(HOST, r"World transfer: holding the chapter card", 300, since=hm))
    # The hint about walking into the fog comes up again on the way; while any window is open the game won't save,
    # and the save made on arrival is what guests load.
    for _ in range(4):
        time.sleep(3)
        cmd(HOST, "click OK")
    run.check("the host arrives and sends the new world", wait_log(HOST, r"World transfer: sent .* to player 1", 300, since=hm))
    run.check("the guest loads it", wait_log(GUEST, r"World transfer: new world \(generated map\)", 420, since=gm))
    run.check("the guest's screen is covered until its game has settled", wait_log(GUEST, r"World transfer: settled in the new world after .* cover off", 90, since=gm))
    run.check("the host hears the guest has settled", wait_log(HOST, r"World transfer: player 1 has settled in the new world", 20, since=hm))
    run.check("the card is shown once the guest is there", wait_log(HOST, r"World transfer: everyone arrived; showing what was held", 120, since=hm))
    cmd(GUEST, "click Continue"); dismiss(GUEST)   # the guest's copy of the question at the edge
    # The card, then her words about the forest spirit, then two notes about places on the map.
    spoke = False
    for _ in range(40):
        time.sleep(3)
        b = first_line(cmd(HOST, "buttons"))
        if "IconButton" in b: cmd(HOST, "click IconButton")
        elif "'OK'" in b: cmd(HOST, "click OK")
        elif talking(HOST): spoke = True; cmd(HOST, "next")
        elif spoke: break
        for g in ("IconButton", "OK", "Continue"): cmd(GUEST, "click " + g)
    run.check("the old woman spoke of the new trial", spoke)
    # Her words end in her menu (ask about the forest, the hut, or leave); whoever leads the talk answers.
    for _ in range(3):
        for inst in (GUEST, HOST):
            for way_out in ("Goodbye", "Leave"):
                if f"'{way_out}'" in cmd(inst, "buttons"): cmd(inst, "click " + way_out); time.sleep(3)
        if talking(HOST): finish_any(HOST, GUEST)   # her parting line
    time.sleep(5)
    for inst in (HOST, GUEST): dismiss(inst); cmd(inst, "click OK"); cmd(inst, "click Continue")
    run.check("the guest heard her on the new map", "I have not been to this place" in read_log(GUEST)[read_log(GUEST).rfind("new world (generated map)"):])
    for inst in (HOST, GUEST):
        run.check(f"{inst}: the forest spirit's trial has begun", "trial 1" in cmd(inst, "trials") and "Leshy/InProgress" in cmd(inst, "status"), first_line(cmd(inst, "trials"))[:80])
    a, b = pos(HOST), pos(GUEST)
    apart = ((a[0] - b[0]) ** 2 + (a[2] - b[2]) ** 2) ** 0.5
    run.check("both stand in the house on the new map", apart < 12, f"{apart:.1f} m apart")
    hut = [re.search(r"hut=\(([^)]*)\)", cmd(i, "status")) for i in (HOST, GUEST)]
    run.check("the house stands in the same place on both", all(hut) and hut[0].group(1) == hut[1].group(1), str([h and h.group(1) for h in hut]))
    # From the guest's arrival on: what it asked for on the old map while the host was already leaving doesn't count.
    arrived = read_log(HOST).rfind("World transfer: sent ")
    lost = [l for l in read_log(HOST)[max(hm, arrived):].splitlines() if "not found here" in l]
    run.check("nothing a guest asked for was missing on the host", not lost, f"{len(lost)}: {lost[0][:160] if lost else ''}")


def places(inst, name):
    """Where the objects whose path ends with name stand on that copy: [(x, height, z)]."""
    cmd(inst, "where " + name)
    out = []
    with open(os.path.join(GAME, inst, "where.txt"), encoding="utf-8", errors="replace") as f:
        for l in f.read().splitlines():
            m = re.match(r"\s*[+-] (\S.*?) @\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)", l)
            if m and m.group(1).endswith(name): out.append((float(m.group(2)), float(m.group(3)), float(m.group(4))))
    return sorted(out)


def favours(inst):
    """The forest spirit's count of kinds of favour done, and the line it comes from."""
    r = first_line(cmd(inst, "leshy"))
    m = re.search(r"favours=(\d+)", r)
    return (int(m.group(1)) if m else -1), r


def click_through(lead, other, tries=80):
    """A talk with its cards and notes clicked to the end by whoever may; the lines heard on the host."""
    heard = []
    for k in range(tries):
        d = first_line(cmd(HOST, "dlgprobe"))
        if "talking=True" not in d:
            quiet = True
            for inst in (HOST, GUEST):
                b = first_line(cmd(inst, "buttons"))
                for label in ("OK", "Continue", "IconButton"):
                    if label in b: cmd(inst, "click " + label); quiet = False
            if quiet and k > 2: break
            time.sleep(2)
            continue
        line = d.split("text=")[-1][:80]
        if not heard or heard[-1] != line: heard.append(line)
        cmd(lead, "next"); cmd(other, "next")
        time.sleep(1.2)
    return heard


def hares(run, leader):
    """On the forest spirit's map: hares caught in traps. Breaking a trap frees one; the first freed is a favour to the
    spirit, who then speaks for the first time. The leader breaks the first trap, the other the second: one favour on
    both copies either way, and every trap broken on both."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST): dismiss(inst)
    traps = places(HOST, "RabbitTrap_Destructible")
    run.check("hare traps stand in the same places on both copies", traps == places(GUEST, "RabbitTrap_Destructible"), f"{len(traps)} on the host")
    if len(traps) < 2:   # a map generated in this run (the chained playthrough) may have none
        print("  (this map has fewer than two hare traps; nothing more to do here)", flush=True)
        return
    run.check("no favour done yet", favours(HOST)[0] == 0 and favours(GUEST)[0] == 0, favours(HOST)[1][:120])
    for n, (who, away) in enumerate(((lead, other), (other, lead))):
        x, y, z = traps[n]
        mark = len(read_log(HOST))
        cmd(away, "tp 44 54 17")
        cmd(who, f"tp {x + 2:.1f} {z + 2:.1f} {y + 1:.1f}")
        time.sleep(2.5)
        r = first_line(cmd(who, "hit 1000 RabbitTrap"))
        run.check(f"trap {n + 1} is broken by {who}", "destroyed=True" in r, r[:160])
        time.sleep(8)
        heard = click_through(who, away)
        if n == 0: run.check("the forest spirit speaks for the first time", any("stranger has stumbled" in h for h in heard), str(heard[:2]))
        else: run.check("he says nothing the second time", not heard, str(heard[:2]))
        time.sleep(4)
        for inst in (HOST, GUEST):
            f = favours(inst)
            run.check(f"{inst}: freeing hares counts as one favour", f[0] == 1 and "hares=True" in f[1], f[1][:120])
        log = read_log(HOST)[mark:]
        run.check("the host heard of the trap once", count(log, "RabbitTrap_Destructible") >= 1 and "[Error" not in log)
    left = [len(places(i, "RabbitTrap_Destructible")) for i in (HOST, GUEST)]
    run.check("both copies have the same traps left", left[0] == left[1], str(left))


def stuck(run, leader):
    """Lutki stuck in the villagers' woodpiles, the same small place three times over on one map. Pulling one out is a
    press of the key: it gives a little loot, counts towards the Lutki's quest and solves that place. The leader frees
    the first and third, the other the second; each time it is the host's copy of that very Lutki that is freed, once."""
    lead, other = (HOST, GUEST) if leader == "host" else (GUEST, HOST)
    for inst in (HOST, GUEST): dismiss(inst)
    spots = places(HOST, "StuckLutki_01(Clone)")
    run.check("stuck Lutki are in the same places on both copies", spots == places(GUEST, "StuckLutki_01(Clone)"), f"{len(spots)} on the host")
    if len(spots) < 3:
        print("  (this map has fewer than three stuck Lutki; nothing more to do here)", flush=True)
        return
    mark = len(read_log(HOST))
    for n, (who, away) in enumerate(((lead, other), (other, lead), (lead, other))):
        x, y, z = spots[n]
        at = len(read_log(HOST))
        cmd(away, "tp 44 54 17")
        for _ in range(3):   # a talk just ended may still pull a player back beside its starter
            cmd(who, f"tp {x + 1.2:.1f} {z + 1.2:.1f} {y + 1:.1f}")
            time.sleep(2.5)
            here = pos(who)
            if here and abs(here[0] - x) < 3 and abs(here[2] - z) < 3: break
        r = first_line(cmd(who, "interact StuckLutki"))
        near = re.search(r" at (\d+) m", r)
        run.check(f"Lutki {n + 1} is pulled out by {who}", "interacted with" in r and near and int(near.group(1)) <= 5, r[:120])
        ok = until(lambda: "Popups: sent the loot box" in read_log(HOST)[at:], 12)
        run.check(f"the host pays out for Lutki {n + 1}", ok)
        time.sleep(3)
        for inst in (HOST, GUEST): dismiss(inst)   # the loot card; while it is up on the host nothing can be used there
        click_through(who, away, 6)
    # Which Lutki the host freed: its copy of each place changed (the host compares places in rounds, so this lags).
    ids = lambda: set(re.findall(r"Places: StuckLutki_01\(Clone\) \(([0-9a-f-]+)\) changed", read_log(HOST)[mark:]))
    until(lambda: len(ids()) >= 3, 90, 3)
    run.check("three different Lutki were freed on the host", len(ids()) == 3, str(ids()))
    log = read_log(HOST)[mark:]
    run.check("three payouts, no more", count(log, "Popups: sent the loot box") == 3, str(count(log, "Popups: sent the loot box")))
    packs = [first_line(cmd(i, "inv")).split("; money")[-1].split(" block kinds")[0] for i in (HOST, GUEST)]
    run.check("the loot is in the shared pack on both", packs[0] == packs[1], f"{packs[0][-120:]} / {packs[1][-120:]}")
    left = [len([l for l in cmd(i, "detections 400").splitlines() if "StuckLutki/Player Detection on=True" in l]) for i in (HOST, GUEST)]
    for inst in (HOST, GUEST): cmd(inst, "tp 44 54 17")
    time.sleep(2.5)


SCENARIOS = {"door": ("pre-door-1.0.32", door), "yard": ("after-door-1.0.32", yard),
             "lutki": ("after-yard-1.0.32", lutki), "home": ("talisman-1.0.32", home),
             "villager": ("talisman-1.0.32", villager),
             "chicken": ("pine-1.0.40", chicken), "cleanse": ("cleanse-1.0.40", cleanse),
             "egg": ("egg-1.0.40", egg), "offerings": ("offerings-1.0.40", offerings),
             "bones": ("charm-1.0.40", bones), "embers": ("embers-1.0.40", embers),
             "travel": ("hut-1.0.40", travel), "hares": ("leshy-1.0.40", hares),
             "stuck": ("leshy-1.0.40", stuck)}


def view(inst):
    """Where that copy's main camera is: (x, y, z), or None."""
    m = re.search(r"tag=MainCamera target=\S+ pos=\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)", cmd(inst, "cams"))
    return tuple(float(v) for v in m.groups()) if m else None


def same_view(run, what):
    """Both copies show a talk through the same camera (the leader's), not each through its own."""
    def apart():
        a, b = view(HOST), view(GUEST)
        return 99.0 if not a or not b else math.dist(a, b)
    ok = until(lambda: apart() < 0.3, 6, 0.5)
    run.check(f"{what}: both see it through the same camera", ok, f"host {view(HOST)}, guest {view(GUEST)}")


def between_checks(run, name):
    """After a step of a chained run: nobody left in a talk, and both copies at the same place in the tutorial."""
    for inst in (HOST, GUEST): dismiss(inst)
    # A last answer can start a moment after the step's own checks are through (a villager's goodbye); let it come
    # and go, or it eats the next step's first press.
    time.sleep(2)
    if talking(HOST) and "options=True" not in cmd(HOST, "dlgprobe"): finish_any(HOST, GUEST)
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
    only = next((a.split("=", 1)[1] for a in sys.argv if a.startswith("--start=")), None)
    if only:  # --start=<fixture>: both games up in that fixture and left running, for driving by hand (dev commands)
        start(only)
        print(f"started in {only}", flush=True)
        return
    names = [n for n in SCENARIOS if n in args]
    leaders = [a for a in args if a in ("host", "guest", "mixed")] or ["host", "guest", "mixed"]
    global SPLIT
    def who(leader, i): return leader if leader != "mixed" else ("host", "guest")[i % 2]
    order = list(SCENARIOS)
    runs = []
    t0 = time.time()
    if names or each:
        # Isolated: the named scenarios (or all with --each), each from its own fixture.
        for name in names or order:
            fixture, fn = SCENARIOS[name]
            for leader in leaders:
                run = Run(f"{name} / {leader} leads" if leader != "mixed" else f"{name} / shared, host first")
                print(f"== {run.name}", flush=True)
                SPLIT = leader == "mixed"
                try:
                    start(fixture)
                    fn(run, who(leader, 0))
                    common_checks(run)
                except (Exception, SystemExit) as e:   # a slip in the script too: the run is reported and the games are closed
                    run.check("run completed", False, f"{type(e).__name__}: {e}")
                keep_logs(run); runs.append(run)
    else:
        # Chained: one playthrough per leader, every scenario in story order in the same session.
        for leader in leaders:
            run = Run(f"playthrough ({', '.join(order)}) / " + (f"{leader} leads" if leader != "mixed" else "shared, taking turns"))
            print(f"== {run.name}", flush=True)
            SPLIT = leader == "mixed"
            try:
                start(SCENARIOS[order[0]][0])
                for i, name in enumerate(order):
                    print(f" -- {name}", flush=True)
                    if fixtures and i > 0 and leader == leaders[0]: save_fixture(SCENARIOS[name][0])
                    t1 = time.time()
                    SCENARIOS[name][1](run, who(leader, i))
                    print(f"    ({name}: {(time.time() - t1) / 60:.1f} min)", flush=True)
                    if i < len(order) - 1: between_checks(run, name)
                common_checks(run)
            except (Exception, SystemExit) as e:   # a slip in the script too: the run is reported and the games are closed
                run.check("run completed", False, f"{type(e).__name__}: {e}")
            keep_logs(run); runs.append(run)
    if not keep: stop()
    print()
    for run in runs:
        failed = [w for w, ok, _ in run.results if not ok]
        print(f"{'PASS' if run.passed else 'FAIL'}  {run.name}" + (f"  ({len(failed)} failed: {'; '.join(failed)})" if failed else ""))
    print(f"{(time.time() - t0) / 60:.1f} min")
    sys.exit(0 if all(r.passed for r in runs) else 1)


if __name__ == "__main__":
    main()
