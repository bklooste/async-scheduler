# Operations runbook

What to do when something looks wrong. Everything here was exercised against the running service unless marked
*(untested)*. Evidence for the reliability claims is in [../spike/README.md](../spike/README.md).

## A job failed — why, and how do I retry it?

1. Open `/hangfire` (on by default; user `admin`). If you did not set `Scheduler__DashboardPassword`, the password was generated at startup and logged once: `kubectl logs <pod> | grep GENERATED` (each replica has its own). Set the password from a Secret for anything long-lived.
2. **Retries** lists jobs whose callback failed and are waiting for the next automatic attempt. **Failed** lists
   jobs that ran out of attempts (Hangfire's default is 10 attempts with growing backoff).
3. Open the job: the detail page shows the **exception** (e.g. `HttpRequestException: Name or service not known`
   or `Callback POST http://… failed: 500`), the full state history and the job's arguments (URL, method).
4. **Requeue** runs it again now. **Delete** drops it. On a recurring job (**Recurring Jobs** tab) **Trigger now**
   runs it immediately.

The scheduler treats any non-2xx as a failed attempt, except `404`, which counts as done (the target is gone).
Fix the receiving endpoint first, then Requeue; requeueing against a still-broken target just fails again.

Without the dashboard: `DELETE /v1/jobs/background/{id}` cancels a queued/scheduled job and
`DELETE /v1/jobs/recurring/{id}` removes a recurring one. There is no API to retry; use the dashboard or re-submit.

## A callback arrived late

- **Normal lateness** is 0 to about 2 s: due times are rounded up to a whole second (a job never fires *early*), then picked up within `Scheduler__PollIntervalSeconds` (default 1 s), plus your endpoint's latency.
- **15 to 45 s late** almost always means the first attempt failed and was retried on Hangfire's first backoff.
  Look at the job's history in the dashboard. A connection-level failure is already retried once immediately;
  a non-2xx response or timeout is not.
- **Not arriving at all**: check the job is not in **Scheduled** for a time in the future (timezone: pass
  `scheduledTime` with a `Z`), then that a worker is alive (dashboard **Servers**; `/health` on each replica).

## A worker crashed mid-callback

The job is re-run **once the invisibility timeout passes** (`Scheduler__InvisibilityTimeoutSeconds`, default 120 s;
measured 121 s after a `kill -9`). Expect a duplicate if the target had already processed the first call, so make
endpoints idempotent. Lowering the timeout speeds recovery but must stay above
`CallbackTimeoutSeconds` + 30, or a healthy slow callback would be re-run concurrently (startup refuses lower values).

## The store (Redis / Postgres) went away

Workers stay up and recover on their own: a 15 s pause and a full restart of the store were both survived, with
in-flight and later jobs still delivered. `/health` reports unhealthy while Redis is disconnected. *(Untested:
a real Sentinel/Cluster failover, and outages longer than the invisibility timeout — jobs in flight at that point
may be re-run.)*

## Deploying / upgrading

- Roll replicas one at a time. A rolling restart of every replica lost **no** recurring firings and caused no
  double-firing in testing.
- Every replica applies the `Jobs` config at startup. That is an idempotent upsert, and with
  `Scheduler__ReconcileConfigJobs` (default on) a job removed from config is removed from the store on the next start.
  If replicas run different `Jobs` configs during a rollout they will remove each other's jobs: roll config and image together.
- First boot of several replicas against an **empty Postgres** logs a harmless `duplicate key … pg_namespace_nspname_index`
  from schema creation. Start one replica first to avoid the noise.

## Moving from `plat-scheduler` (the old Orange-based image)

**Do not point this image at the old image's Redis prefix expecting the jobs to carry over. They will not run.**
Tried in a real dev cluster (2026-09-21): the new engine found the old scheduled and recurring jobs (the key layout is
compatible), but they could not be loaded, because each stored job names the old assembly
(`Scheduler.GenericProxy, plat-scheduler`). They failed with `JobLoadException` / `FileNotFoundException`, retried,
and the recurring job was disabled.

Use a **fresh** `Scheduler__RedisPrefix` and treat the switch as a drain: let the jobs scheduled under the old image
fire (or re-create them through the API) before switching, because anything still pending under the old prefix is
dropped. The `/v1/jobs/http/*` API is unchanged, so callers need no change beyond the base URL if it moves.

## Useful signals

Set `OTEL_EXPORTER_OTLP_ENDPOINT`. Traces cover the HTTP API, each job execution (span name `JOB {id}`), outgoing
callbacks and (Redis) Redis commands. Logs at `Information` include one line per callback:
`Callback POST http://… -> 200`, and `Removed job {id}: no longer in Jobs config` on reconciliation.
