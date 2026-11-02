"""Tests for the watchdog in llarp_core (stuck llarpd, VPN reconnect) and the state file.

Run on Linux or inside the LLARP WSL distro (it needs fcntl):
    python3 tests/test_watchdog.py [path/to/dir/with/llarp_core.py]
"""

import json
import multiprocessing as mp
import os
import subprocess
import sys
import tempfile
import time
import types
from pathlib import Path

HERE = Path(__file__).resolve().parent
CORE_DIR = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE.parent / "payload"
os.environ["XDG_CONFIG_HOME"] = tempfile.mkdtemp()
sys.path.insert(0, str(CORE_DIR))
import llarp_core as core  # noqa: E402

EXIT = "j8ag4xwcbbezr13os189oh5ptq93y9f1xo1s95sfbbsawnpst7bo.loki"
failed = 0


def check(ok, what):
    global failed
    print(("ok    " if ok else "FAIL  ") + what)
    failed += not ok


def controller(vpn_state="trouble", use_exit=EXIT):
    ctl = core.Controller()
    ctl.settings["use_exit"] = use_exit
    ctl.reload_if_changed = lambda: False
    ctl.vpn_status = lambda: {"state": vpn_state(), "detail": ""} if callable(vpn_state) else {"state": vpn_state, "detail": ""}
    return ctl


# ---------------------------------------------------------------- VPN reconnect timing


def test_reconnect_timing():
    ctl = controller("trouble")
    w = {"bad_since": None, "reconnects": 0}
    t0 = 1000.0
    check(ctl.vpn_needs_reconnect(w, t0) is None and w["bad_since"] == t0, "first bad check only starts the clock")
    check(ctl.vpn_needs_reconnect(w, t0 + 179) is None, "no reconnect before 3 minutes")
    reason = ctl.vpn_needs_reconnect(w, t0 + 181)
    check(reason is not None and "reconnect #1" in reason and EXIT in reason, f"reconnect after 3 minutes: {reason}")
    check(w["bad_since"] is None and w["reconnects"] == 1, "clock restarts after a reconnect")

    # backoff: 6, 12, 24, then capped at 30 minutes
    t = t0 + 200
    for n, wait in ((2, 360), (3, 720), (4, 1440), (5, 1800), (6, 1800)):
        ctl.vpn_needs_reconnect(w, t)  # starts the clock again
        early = ctl.vpn_needs_reconnect(w, t + wait - 1)
        late = ctl.vpn_needs_reconnect(w, t + wait + 1)
        check(early is None and late is not None and f"#{n}" in late, f"reconnect #{n} waits {wait // 60} min")
        t += wait + 10


def test_reset_and_ignored_states():
    state = {"s": "trouble"}
    ctl = controller(lambda: state["s"])
    w = {"bad_since": None, "reconnects": 3}
    ctl.vpn_needs_reconnect(w, 0)
    state["s"] = "on"
    check(ctl.vpn_needs_reconnect(w, 10) is None and w == {"bad_since": None, "reconnects": 0},
          "a working VPN resets the backoff")
    for s in ("pending", "stopped"):
        state["s"] = "trouble"
        w = {"bad_since": None, "reconnects": 0}
        ctl.vpn_needs_reconnect(w, 0)
        state["s"] = s
        check(ctl.vpn_needs_reconnect(w, 10_000) is None and w["bad_since"] is None, f"'{s}' never triggers a restart")
    state["s"] = "connecting"  # e.g. stuck "looking for X on the network"
    w = {"bad_since": None, "reconnects": 0}
    ctl.vpn_needs_reconnect(w, 0)
    check(ctl.vpn_needs_reconnect(w, 200) is not None, "stuck 'connecting' counts too")
    off = controller("trouble", use_exit=None)
    w = {"bad_since": None, "reconnects": 2}
    check(off.vpn_needs_reconnect(w, 0) is None and off.vpn_needs_reconnect(w, 10_000) is None
          and w["reconnects"] == 0, "VPN off: never restarts")


# ---------------------------------------------------------------- run_foreground's loop


class FakeProc:
    """A llarpd that runs until `life` waits have passed, then exits with code 0."""

    def __init__(self, life):
        self.life, self.waits, self.returncode = life, 0, None

    def wait(self, timeout=None):
        if self.returncode is not None:
            return self.returncode
        self.waits += 1
        if self.waits > self.life:
            self.returncode = 0
            return 0
        raise subprocess.TimeoutExpired("llarpd", timeout)

    def terminate(self):
        self.returncode = -15

    def kill(self):
        self.returncode = -9

    def send_signal(self, sig):
        pass


def run_watchdog(health, reconnect, lives):
    ctl = core.Controller()
    procs = []
    lives = iter(lives)
    ctl._launch = lambda: procs.append(FakeProc(next(lives))) or procs[-1]
    ctl.path_health = lambda: next(health, {"stuck": False, "for": 0})
    ctl.vpn_needs_reconnect = lambda w: next(reconnect, None)
    notes = []
    ctl.note = notes.append
    old = core.WATCHDOG_EVERY
    core.WATCHDOG_EVERY = 0
    try:
        code = ctl.run_foreground()
    finally:
        core.WATCHDOG_EVERY = old
    return code, procs, notes


def test_run_foreground():
    code, procs, notes = run_watchdog(iter([{"stuck": True, "for": 900}]), iter([]), [50, 2])
    check(len(procs) == 2 and procs[0].returncode == -15 and code == 0, "stuck llarpd: restarted once")
    check(any("failed to build paths for 15 min" in n for n in notes), "stuck restart is logged")

    code, procs, notes = run_watchdog(iter([]), iter(["no traffic has got through x.loki for 3 min (reconnect #1)"]), [50, 2])
    check(len(procs) == 2 and procs[0].returncode == -15 and code == 0, "dead VPN: llarpd restarted for a fresh session")
    check(any("watchdog: no traffic has got through" in n for n in notes), "VPN reconnect is logged")

    code, procs, notes = run_watchdog(iter([]), iter([]), [3])
    check(len(procs) == 1 and code == 0, "healthy llarpd: left alone")


# ---------------------------------------------------------------- state file under concurrency


def _hammer(n):
    ctl = core.Controller()
    for i in range(200):
        ctl._update_state(**{f"w{n}": i})


def test_state_file():
    core.CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    core.STATE_FILE.write_text('{\n  "pid": 5648,\n  "started_at": 1.5\n}}')
    check(core.Controller().state() == {"pid": 5648, "started_at": 1.5}, "damaged state file ({...}}) is salvaged")
    core.STATE_FILE.unlink()
    procs = [mp.Process(target=_hammer, args=(n,)) for n in range(8)]
    for p in procs:
        p.start()
    for p in procs:
        p.join()
    final = json.loads(core.STATE_FILE.read_text())
    check(final == {f"w{n}": 199 for n in range(8)}, "8 processes x 200 updates: intact, every last write kept")
    check(not [f for f in os.listdir(core.CONFIG_DIR) if f.endswith(".tmp")], "no temp files left behind")


if __name__ == "__main__":
    test_reconnect_timing()
    test_reset_and_ignored_states()
    test_run_foreground()
    test_state_file()
    print("ALL OK" if not failed else f"{failed} FAILED")
    sys.exit(1 if failed else 0)
