# Operations runbook

What to do when something looks wrong. Everything here was exercised against the running service unless marked
*(untested)*. Evidence for the reliability claims is in [../spike/README.md](../spike/README.md).

## A job failed — why, and how do I retry it?

1. Open `/hangfire` (on by default; user `admin`). If you did not set `Scheduler__DashboardPassword`, the password was generated at startup and logged once: `kubectl logs <pod> | grep GENERATED` (each replica has its own). Set the password from a Secret for anything long-lived.
2. **Retries** lists jobs whose callback failed and are waiting for the next automatic attempt. **Failed** lists
   jobs that ran out of attempts (Hangfire's default is 10 *retries*, so up to 11 calls, with growing backoff;
   see [Hangfire behaviour](#hangfire-behaviour-read-from-source)). Kept-result jobs (`keepResult=true`) never reach
   **Failed** for an HTTP outcome: after `attempts` (default 3) the last outcome is stored and the job is *Succeeded*.
   Read the outcome with `GET /v1/jobs/{jobKey}/result`.
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

## A result read returns 202 for too long, or 404

- `202` with `"state":"Enqueued"` for long: the pool is full. Default-queue jobs use `Scheduler__WorkerCount`
  workers, `queue=async` jobs `Scheduler__AsyncWorkerCount`. Check the dashboard **Servers** page (two servers per
  replica, `<host>:default` and `<host>:async`) and **Processing**.
- `"state":"Retrying"`: the target answered `5xx`, timed out or refused the connection; see the job's history.
- `404`: the key is unknown, or `userId` is not the job's `owner` (the two are deliberately indistinguishable), or
  the result outlived `resultTtl`. A key without a result expires after a day.
- Results are hashes in the job store: on Redis, `<RedisPrefix>async-scheduler:job-key:<jobKey>` with a TTL.

## Hangfire behaviour, read from source

Read on 2026-11-05 from the packages this image ships (`Hangfire.Core` 1.8.25,
`Fork.Hangfire.Redis.StackExchange.Enhanced` 2.0.3), decompiled.

- **Default retry count: 10 retries, so up to 11 calls.** `AutomaticRetryAttribute.DefaultRetryAttempts = 10`, and a
  failure is rescheduled while `RetryCount + 1 <= Attempts`. Delay before retry *n* is `(n-1)^4 + 15 + rand(0..29)*n`
  seconds (so 15-44 s, then 16-74 s, ...). Kept-result jobs override this on their job method: delays 1, 2, 5, 10,
  30 s, and they stop at `attempts` themselves.
- **A job still running at `InvisibilityTimeout` IS re-queued (Redis storage).** Fetching a job writes a `Fetched`
  timestamp on the job hash once (`RedisConnection.FetchNextJob`); nothing extends it while the job runs. The
  `FetchedJobsWatcher` re-queues any dequeued job whose `Fetched` is older than `InvisibilityTimeout`
  (`TimedOutByFetchedTime`, reason "InvisibilityTimeout"), whether or not its server is alive. So a call that runs
  longer than `Scheduler__InvisibilityTimeoutSeconds` is started a second time while the first is still running. That
  is why `CallbackTimeoutSeconds` must stay 30 s under it (enforced at startup) and a per-job `timeout` 10 s under it
  (enforced at enqueue). The storage has a `SkipRequeueOnTimeout` option; this image does not set it.
- **Retention.** Succeeded and deleted jobs expire after `JobStorage.JobExpirationTimeout`, 1 day by default; failed
  jobs are kept until deleted. The Redis storage also trims its succeeded and deleted lists to 499 entries (dashboard
  lists only). Kept results do not depend on either: they live in their own hashes for `resultTtl`.

**Not verified:** the same behaviour for the Postgres and in-memory storages (only their support for per-job queues and
hash expiry was checked); a real Redis Sentinel/Cluster failover.

## Useful signals

Set `OTEL_EXPORTER_OTLP_ENDPOINT`. Traces cover the HTTP API, each job execution (span name `JOB {id}`), outgoing
callbacks and (Redis) Redis commands. Logs at `Information` include one line per callback:
`Callback POST http://… -> 200`, and `Removed job {id}: no longer in Jobs config` on reconciliation.
