# Reliability spike

Evidence for whether Redis (and Postgres) storage is safe to run this scheduler on. Not a deployment example.

```bash
STORAGE=Redis    python3 spike/run.py          # all checks, ~7 min
STORAGE=Postgres python3 spike/run.py e d      # or pick checks: a b c d e r
REPLICAS=1 STORAGE=Redis python3 spike/run.py e
```

Needs Docker. It builds the image, starts 3 scheduler replicas on one shared store plus a counting receiver,
and writes `results-<storage>.json`.

| | Check | How |
|---|---|---|
| a | fires **once and only once** | 300 delayed jobs across 3 replicas; every path must be called exactly once |
| b | **worker crash re-runs the job** | `kill -9` the only worker mid-job; the job must run again |
| c | **store outage doesn't kill workers** | pause the store 15 s, then restart it, mid-job; workers must stay up, recover, and run new jobs |
| d | recurring jobs **don't double-fire** across restarts | a 2 s recurring job while every replica is restarted in turn; no 2 s slot may have two calls |
| r | **config reconcile** | run a job from `Jobs` config, restart without it: it stops; an API-created recurring job keeps firing |
| e | **fire-lag** | 200 jobs scheduled at known instants; seconds between due time and arrival |

`kill -9` and pause/restart are the closest a single-host stack gets to a crash and a failover. A real Redis
Sentinel/Cluster failover is **not** exercised.

## Findings

1. **Worker-crash recovery needs `Scheduler__InvisibilityTimeoutSeconds`.** With the storage default the killed job
   was not re-run within 240 s. At `120` it re-ran 121 s after the kill, on both Redis and Postgres. The setting is
   now first-class, defaults to 120, and must exceed `CallbackTimeoutSeconds` + 30 (validated at startup).
2. **A stale keep-alive connection cost 15 to 45 s.** In roughly half of the early fire-lag runs, one job of 200 arrived
   17 to 44 s late. Logs showed `HttpIOException: The response ended prematurely`: the callback attempt failed on a
   pooled connection the receiver had just closed, and Hangfire's first retry backoff (about 15 s plus 0 to 30 s)
   delivered it late. The sender now retries once immediately on a connection-level failure (no response received).
   After that change: 12 of 12 runs with no job more than 5 s late. (The receiver's HTTP/1.0 close-per-request is
   the worst case for this race, but real servers hit it too.)
3. **Fire-lag** is bounded by `PollIntervalSeconds` (1 s). It is second-granularity by design, not a millisecond timer.
4. **Concurrent first boot on an empty Postgres is noisy.** Three replicas started at once race to create the
   schema; one or two log `fail: Hangfire.PostgreSql ... duplicate key ... pg_namespace_nspname_index`. All replicas
   stay up and healthy and the schema ends up correct, so it is cosmetic, but start one replica first (or accept
   the log lines). Redis has no equivalent.

## Results (final code, 2026-09-21)

| | Redis | Postgres |
|---|---|---|
| a. once-and-only-once (300 jobs, 3 replicas) | PASS: 0 missing, 0 duplicated | PASS: 0 missing, 0 duplicated |
| b. worker `kill -9` mid-job re-runs it | PASS: re-ran 121 s after kill (`InvisibilityTimeoutSeconds=120`) | PASS: re-ran 121 s after kill |
| c. store paused 15 s / restarted mid-job | PASS: workers up, recovered, new job fired, in-flight job ran once | PASS: same |
| d. recurring, rolling restart of all replicas | PASS: 0 double-fired, 0 missed slots | PASS: 0 double-fired, 0 missed slots |
| r. config job removed → removed on restart; API job survives | PASS | PASS |
| e. fire-lag, 200 jobs (p50 / p99 / max) | 12 back-to-back runs: p99 0.2 to 0.82 s, none over 5 s late | 1 run: -0.13 / 0.92 / 0.93 s |

Negative p50 is real: Hangfire stores due times at whole-second resolution, so jobs can fire up to about a second early.
Redis fire-lag was measured many times; **Postgres only once on the final code** (eight runs earlier, before the
connection-retry fix, showed p99 0.85 to 2.6 s with one 42 s outlier from the stale-connection issue above), so treat
the Postgres number as indicative.

## Not covered

- Real Sentinel/Cluster failover; network partitions rather than pause/restart.
- Long soak (hours); memory growth; very large job volumes.
- More than 3 replicas; heterogeneous clock skew.
- The dashboard's retry/requeue workflow under failure.
