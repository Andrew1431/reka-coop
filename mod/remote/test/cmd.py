"""On the test PC: one dev command to a running test copy, or the new part of its log.
  cmd.py cmd <inst> <base64 of the command>     prints the command's result
  cmd.py log <inst> <pattern-base64> [lines]    the last lines of that copy's log matching the regex (default 40)
Base64 because the text crosses ssh and two shells on the way here."""
import base64, os, re, sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
import regress as R

verb, inst, text = sys.argv[1], sys.argv[2], base64.b64decode(sys.argv[3]).decode("utf-8")
if verb == "cmd":
    try: print(R.cmd(inst, text, wait=20))
    except TimeoutError as e: print(f"TIMEOUT: {e}"); sys.exit(1)
else:
    n = int(sys.argv[4]) if len(sys.argv) > 4 else 40
    rx = re.compile(text)
    for line in [l for l in R.read_log(inst).splitlines() if rx.search(l)][-n:]: print(line)
