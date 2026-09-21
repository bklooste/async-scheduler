#!/usr/bin/env python3
"""Reliability spike (plan D-5). Usage:  STORAGE=Redis|Postgres python3 spike/run.py [a b c d e]
Brings the stack up itself; needs docker. Writes spike/results-<storage>.json."""
import json, os, statistics, subprocess, sys, time, urllib.request, urllib.parse, datetime as dt

STORAGE = os.environ.get("STORAGE", "Redis")
STORE = "redis" if STORAGE == "Redis" else "postgres"
HERE = os.path.dirname(os.path.abspath(__file__))
PORTS = {"s1": 8081, "s2": 8082, "s3": 8083}
DEST = "http://receiver:8080"
results = {}
LAST_FAILS = []
NAMES = ["s1", "s2", "s3"][:int(os.environ.get("REPLICAS", "3"))]

EXTRA = []   # extra compose files (used by the reconcile check to toggle Jobs config)

def dc(*args, check=True):
    files = [x for f in ["compose.yml", *EXTRA] for x in ("-f", f"{HERE}/{f}")]
    return subprocess.run(["docker", "compose", *files, "-p", f"spike-{STORAGE.lower()}", *args],
                          env={**os.environ, "STORAGE": STORAGE}, capture_output=True, text=True, check=check)

def http(url, method="GET", data=None, timeout=10):
    req = urllib.request.Request(url, method=method, data=data)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r: return r.status, r.read().decode()
    except urllib.error.HTTPError as e: return e.code, ""
    except Exception: return 0, ""

def api(svc, path, method="POST", data=b"{}"):
    return http(f"http://localhost:{PORTS[svc]}{path}", method, data)

def dump(): return json.loads(http("http://localhost:9000/_dump")[1] or "[]")
def reset(): http("http://localhost:9000/_reset")

def healthy(svc, timeout=90):
    end = time.time() + timeout
    while time.time() < end:
        if http(f"http://localhost:{PORTS[svc]}/health", timeout=2)[0] == 200: return True
        time.sleep(1)
    return False

def all_healthy(names=None): return all(healthy(s) for s in (names or NAMES))

def enqueue(svc, path, delay=None):
    q = f"destinationUrl={urllib.parse.quote(DEST + path)}" + (f"&delay={delay}" if delay is not None else "")
    return api(svc, f"/v1/jobs/http/enqueue?{q}")

def wait(cond, timeout, step=0.5):
    end = time.time() + timeout
    while time.time() < end:
        if cond(): return True
        time.sleep(step)
    return cond()

def running(svc):
    return dc("ps", "-q", svc).stdout.strip() != "" and \
        subprocess.run(["docker", "inspect", "-f", "{{.State.Running}}", dc("ps", "-q", svc).stdout.strip()],
                       capture_output=True, text=True).stdout.strip() == "true"

def pct(xs, p): xs = sorted(xs); return xs[min(len(xs) - 1, int(len(xs) * p))]

def test_a():
    """(a) each job fires once and only once with 3 concurrent replicas."""
    reset(); n = 300
    for i in range(n): enqueue(NAMES[i % len(NAMES)], f"/a/{i}", delay=2)
    wait(lambda: len({c["path"] for c in dump()}) >= n, 40); time.sleep(8)   # give duplicates time to appear
    counts = {}
    for c in dump(): counts[c["path"]] = counts.get(c["path"], 0) + 1
    missing = n - len(counts); dups = sum(1 for v in counts.values() if v > 1)
    return dict(ok=missing == 0 and dups == 0, jobs=n, missing=missing, duplicated=dups)

def test_e():
    """(e) fire-lag: seconds between the scheduled time and the callback arriving."""
    reset(); n = 200; sched = {}; base = time.time() + 10; api_s = []; api_bad = 0
    for i in range(n):
        t = base + (i % 20) * 0.25
        sched[f"/lag/{i}"] = t
        iso = dt.datetime.fromtimestamp(t, dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")
        t0 = time.time()
        code, _ = api(NAMES[i % len(NAMES)], f"/v1/jobs/http/schedule?scheduledTime={iso}&destinationUrl={urllib.parse.quote(DEST + f'/lag/{i}')}")
        api_s.append(time.time() - t0); api_bad += code != 200
    wait(lambda: len([c for c in dump() if c["path"].startswith("/lag/")]) >= n, 60)
    lags = [c["start"] - sched[c["path"]] for c in dump() if c["path"] in sched]
    global LAST_FAILS
    logs = dc("logs", "--no-color", *NAMES).stdout
    LAST_FAILS = [l.split("|", 1)[-1].strip()[:220] for l in logs.splitlines() if "failed" in l.lower() or "exception" in l.lower()][:4]
    if not lags: return dict(ok=False, error="no callbacks")
    return dict(ok=len(lags) == n and min(lags) >= 0, arrived=len(lags), min_s=round(min(lags), 2), p50_s=round(pct(lags, .5), 2), p99_s=round(pct(lags, .99), 2), max_s=round(max(lags), 2),
                api_max_s=round(max(api_s), 2), api_total_s=round(sum(api_s), 1), api_non200=api_bad,
                late_jobs_over_5s=sum(1 for l in lags if l > 5), sched_failure_log=LAST_FAILS)

def test_d():
    """(d) a recurring job does not double-fire across a rolling restart of every replica."""
    reset(); api("s1", f"/v1/jobs/http/recurring?id=spike-rec&cron={urllib.parse.quote('*/2 * * * * *')}&destinationUrl={urllib.parse.quote(DEST + '/rec')}")
    time.sleep(10)
    for s in ("s1", "s2", "s3"):
        dc("restart", s); healthy(s); time.sleep(3)
    time.sleep(10)
    api("s2", "/v1/jobs/recurring/spike-rec", "DELETE", None)
    time.sleep(3)
    starts = sorted(c["start"] for c in dump() if c["path"] == "/rec")
    buckets = {}
    for s in starts: buckets[int(s // 2)] = buckets.get(int(s // 2), 0) + 1
    dup = {k: v for k, v in buckets.items() if v > 1}
    first, last = min(buckets), max(buckets)
    missed = [k for k in range(first, last + 1) if k not in buckets]
    return dict(ok=not dup, fires=len(starts), double_fired_slots=len(dup), missed_slots=len(missed), window_s=(last - first + 1) * 2)

def test_b():
    """(b) killing (SIGKILL) the worker mid-job re-runs the job rather than losing it."""
    reset(); dc("stop", "s2", "s3"); healthy("s1")
    enqueue("s1", "/slow/b1")
    started = wait(lambda: any(c["path"] == "/slow/b1" for c in dump()), 20)
    t_kill = time.time(); dc("kill", "s1"); dc("start", "s1"); healthy("s1")
    reran = wait(lambda: sum(1 for c in dump() if c["path"] == "/slow/b1") >= 2, 240, 1)
    lag = None
    if reran:
        lag = round(sorted(c["start"] for c in dump() if c["path"] == "/slow/b1")[1] - t_kill, 1)
    dc("start", "s2", "s3"); all_healthy()
    return dict(ok=bool(started and reran), started=started, rerun=reran, seconds_from_kill_to_rerun=lag, observed_window_s=240)

def test_c():
    """(c) the store going away mid-job (paused, then restarted) does not take workers down or lose later jobs."""
    out = {}
    for mode in ("pause_15s", "restart"):
        reset(); path = f"/slow/c-{mode}"
        enqueue("s1", path)
        wait(lambda: any(c["path"] == path for c in dump()), 20)
        if mode == "pause_15s":
            dc("pause", STORE); time.sleep(15); dc("unpause", STORE)
        else:
            dc("restart", STORE)
        alive = all(running(s) for s in ("s1", "s2", "s3"))
        recovered = all_healthy()
        fresh = f"/c-after-{mode}"; enqueue("s1", fresh)
        got = wait(lambda: any(c["path"] == fresh for c in dump()), 45)
        wait(lambda: sum(1 for c in dump() if c["path"] == path and c["done"]) >= 1, 40)
        time.sleep(35)   # allow a spurious re-run to show up
        runs = sum(1 for c in dump() if c["path"] == path)
        out[mode] = dict(workers_alive=alive, healthy_after=recovered, job_after_recovery_fired=got, inflight_job_runs=runs)
    out["ok"] = all(v["workers_alive"] and v["healthy_after"] and v["job_after_recovery_fired"] for k, v in out.items() if k != "ok")
    return out

def test_r():
    """(r) a job removed from Jobs config disappears on restart; an API-created recurring job survives."""
    reset(); NAMES_ = ["s1"]
    open(f"{HERE}/jobs.override.yml", "w").write(
        "services:\n  s1:\n    environment:\n"
        f"      Jobs__0__Id: cfg-r\n      Jobs__0__Cron: '*/2 * * * * *'\n      Jobs__0__Url: {DEST}/cfg-r\n      Jobs__0__Method: GET\n")
    EXTRA.append("jobs.override.yml"); dc("stop", "s2", "s3"); dc("up", "-d", "s1"); healthy("s1")
    api("s1", f"/v1/jobs/http/recurring?id=api-r&cron={urllib.parse.quote('*/2 * * * * *')}&destinationUrl={urllib.parse.quote(DEST + '/api-r')}")
    fired = wait(lambda: sum(1 for c in dump() if c["path"] == "/cfg-r") >= 2, 20)
    EXTRA.clear(); dc("up", "-d", "s1"); healthy("s1")          # recreated WITHOUT the Jobs entry
    time.sleep(6)
    cfg_before = sum(1 for c in dump() if c["path"] == "/cfg-r"); api_before = sum(1 for c in dump() if c["path"] == "/api-r")
    time.sleep(8)
    cfg_after = sum(1 for c in dump() if c["path"] == "/cfg-r"); api_after = sum(1 for c in dump() if c["path"] == "/api-r")
    api("s1", "/v1/jobs/recurring/api-r", "DELETE", None)
    os.remove(f"{HERE}/jobs.override.yml"); dc("start", "s2", "s3"); all_healthy(["s1", "s2", "s3"])
    return dict(ok=bool(fired and cfg_after == cfg_before and api_after > api_before),
                config_job_fired_before=fired, config_job_fires_after_removal=cfg_after - cfg_before, api_job_still_firing=api_after > api_before)

TESTS = dict(r=test_r, a=test_a, e=test_e, d=test_d, c=test_c, b=test_b)

if __name__ == "__main__":
    picked = sys.argv[1:] or list(TESTS)
    print(f"== spike: STORAGE={STORAGE}", flush=True)
    dc("down", "-v", check=False)
    r = dc("up", "-d", "--build", *NAMES, check=False) if len(NAMES) < 3 else dc("up", "-d", "--build", check=False)
    if r.returncode: print(r.stderr); sys.exit(1)
    assert all_healthy(NAMES), "stack did not become healthy"
    for k in picked:
        t0 = time.time()
        try: results[k] = TESTS[k]()
        except Exception as e: results[k] = dict(ok=False, error=repr(e))
        results[k]["seconds"] = round(time.time() - t0)
        print(f"({k}) {'PASS' if results[k].get('ok') else 'FAIL'} {json.dumps(results[k])}", flush=True)
        all_healthy()
    json.dump(dict(storage=STORAGE, results=results), open(f"{HERE}/results-{STORAGE.lower()}.json", "w"), indent=2)
    dc("down", "-v", check=False)
