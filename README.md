# async-scheduler

[![CI](https://github.com/bklooste/async-scheduler/actions/workflows/ci.yml/badge.svg)](https://github.com/bklooste/async-scheduler/actions/workflows/ci.yml)
[![Image](https://img.shields.io/badge/ghcr.io-async--scheduler-blue)](https://github.com/bklooste/async-scheduler/pkgs/container/async-scheduler%2Fservice)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

A small HTTP service that **calls your URLs back later**. Two things it is good at:

1. **Durable delayed callbacks** — "POST this to `https://my-app/expire` in 30 seconds" (or at a time, or on a cron).
   The job is stored, survives restarts (with Redis), and is retried if your endpoint fails.
2. **Async HTTP for sync callers** — hand a long-running job to the scheduler, get a job id back
   immediately, and let the scheduler make the call. The caller's connection is never held open.

It is a thin, self-contained host around [Hangfire](https://www.hangfire.io/). Second granularity, not
milliseconds — see [Timing](#timing).

Jobs come in through three doors: the **HTTP API**, a declarative **`Jobs` config** applied at startup, and an
optional **operator dashboard** for seeing why a job failed and retrying it.

## Quick start

```bash
git clone https://github.com/bklooste/async-scheduler && cd async-scheduler
docker compose up -d
```

That starts the scheduler and an `echo` container that stands in for *your app* — it prints every request it
receives. Now schedule some calls:

```bash
# Call http://echo:8080/hello right now, with a JSON body and a header
curl -X POST "localhost:8080/v1/jobs/http/enqueue?destinationUrl=http://echo:8080/hello" \
     -H 'Content-Type: application/json' -H 'X-Demo: 1' -d '{"order":42}'
# -> "3"        (the job id)

# Call it 10 seconds from now
curl -X POST "localhost:8080/v1/jobs/http/enqueue?delay=10&destinationUrl=http://echo:8080/later" -d '{"remind":"me"}'

# Watch the callbacks arrive
docker compose logs -f echo
```

You will also see `GET /from-config` every 10 seconds — that is the declarative `Jobs` entry in
[docker-compose.yml](docker-compose.yml), no API call needed.

Open the dashboard at <http://localhost:8080/hangfire> (`admin` / `change-me`, set in the compose file).

The quick start uses **in-memory storage**: jobs vanish when the container restarts. For durable jobs:

```bash
SCHEDULER_STORAGE=Redis docker compose --profile redis up -d
```

> **Status:** early (`0.x`). Redis and Postgres storage both pass a reliability suite (once-and-only-once across
> replicas, worker crash, store outage, recurring across rolling restarts, fire-lag) — see
> [spike/](spike/README.md) for what was and was not tested. Not yet exercised: real Redis Sentinel/Cluster
> failover and long soak runs.

## API

All job endpoints take the **destination as a query parameter** and forward the **request body and headers**
(except `Content-*` and `Host`) to it. `method` defaults to `POST`. A job id is returned as a JSON string.

| Method | Path | Query | Description |
|---|---|---|---|
| `POST` | `/v1/jobs/http/enqueue` | `destinationUrl`, `delay`? (seconds), `method`? | Run now, or after `delay` seconds. |
| `POST` | `/v1/jobs/http/schedule` | `destinationUrl`, `scheduledTime` (ISO 8601), `method`? | Run at a specific time. |
| `POST` | `/v1/jobs/http/recurring` | `destinationUrl`, `cron`, `name`?, `id`?, `method`? | Create/update a recurring job. Id is `{name}-{id}`; omit `id` for a random one. Re-posting the same id updates it. |
| `DELETE` | `/v1/jobs/background/{jobId}` | | Cancel a queued/scheduled job. `200`, or `404` if unknown. |
| `DELETE` | `/v1/jobs/recurring/{id}` | | Remove a recurring job. `200` even if it did not exist. |
| `GET` | `/health` | | `200` when healthy. |

`destinationUrl` must be an absolute `http(s)` URL (otherwise `400`). Cron uses standard 5 fields, or 6 with a
leading seconds field (`*/10 * * * * *`).

```bash
# Every minute, until deleted
curl -X POST "localhost:8080/v1/jobs/http/recurring?name=sync&id=eu&cron=*%20*%20*%20*%20*&destinationUrl=http://my-app/sync"
# -> "sync-eu"
curl -X DELETE localhost:8080/v1/jobs/recurring/sync-eu

# At a fixed time
curl -X POST "localhost:8080/v1/jobs/http/schedule?scheduledTime=2026-12-01T09:00:00Z&destinationUrl=http://my-app/open" -d '{}'

# Cancel
curl -X DELETE localhost:8080/v1/jobs/background/3
```

**Callback behaviour.** A `2xx` marks the job done. Any other status, or a network error, fails the attempt and
Hangfire retries with backoff. **`404` is treated as done** (the target is gone; retrying won't help) — it is
logged, not retried. Each attempt times out after `Scheduler__CallbackTimeoutSeconds`.

Delivery is **at-least-once**: a callback can arrive twice (e.g. a worker dies after your endpoint replied).
Make your endpoint idempotent.

## Declarative jobs (no API call needed)

Recurring jobs can be declared in configuration; they are upserted at startup. This is how a platform team
stands up scheduled work with no code and no API calls.

```yaml
# docker-compose / env vars                    # or appsettings.json:
Jobs__0__Id: nightly-report                    # "Jobs": [ { "Id": "nightly-report",
Jobs__0__Cron: "0 2 * * *"                     #            "Cron": "0 2 * * *",
Jobs__0__Url: http://reports:8080/run          #            "Url": "http://reports:8080/run",
Jobs__0__Method: POST                          #            "Method": "POST" } ]
```

- `Id` is the key: startup is an **idempotent upsert**, so redeploying never duplicates and a changed `Cron` is applied.
- Config jobs send **no body and no headers**.
- A malformed entry (missing field, bad URL) **fails startup** rather than being silently skipped.
- **Removing an entry from config removes the job on the next start** (`Scheduler__ReconcileConfigJobs`, default on).
  Only jobs registered from config are tracked, so recurring jobs created through the API are never touched.
  A job you also created through the API under the same id as a config entry is treated as config's.

## Configuration

Settings come from environment variables (`Section__Key`), an optional mounted `appsettings.json`, or both — env
wins. Invalid configuration fails at startup with a clear message in the log.

| Env var | Type | Default | Description |
|---|---|---|---|
| `Scheduler__Storage` | `InMemory` \| `Redis` \| `Postgres` | `InMemory` | `InMemory` loses jobs on restart (quick start / tests only). `Redis` and `Postgres` are durable. |
| `Scheduler__RedisConnectionString` | string | _(empty)_ | Required when `Storage=Redis`. |
| `Scheduler__PostgresConnectionString` | string | _(empty)_ | Npgsql connection string. Required when `Storage=Postgres`. |
| `Scheduler__RedisPrefix` | string | `scheduler:{hangfire}:` | Redis key prefix. **Must contain the literal `{hangfire}`** (a hash tag, required on clustered Redis). Give each environment its own prefix if they share a Redis. |
| `Scheduler__WorkerCount` | int | `5` | Concurrent callbacks per instance. |
| `Scheduler__ServerTimeoutSeconds` | int | `30` | Seconds without a heartbeat before a worker is presumed dead and its jobs re-queued. |
| `Scheduler__PollIntervalSeconds` | int | `1` | How often due jobs are picked up. Bounds fire-lag. |
| `Scheduler__CallbackTimeoutSeconds` | int | `30` | Per-callback HTTP timeout. |
| `Scheduler__InvisibilityTimeoutSeconds` | int | `120` | After a worker crashes mid-job, seconds before the job is re-queued and run again. Must be at least `CallbackTimeoutSeconds`+30. |
| `Scheduler__ReconcileConfigJobs` | bool | `true` | On startup, remove recurring jobs that were registered from `Jobs` config earlier but are no longer in it. Never touches jobs created through the API. |
| `Scheduler__DashboardEnabled` | bool | `false` | Serve the dashboard at `/hangfire`. |
| `Scheduler__DashboardAuthMode` | `Basic` \| `None` | `Basic` | `Basic` needs the two settings below. `None` means you front it with your own auth proxy — never expose the dashboard unauthenticated. |
| `Scheduler__DashboardUsername` | string | _(empty)_ | Basic-auth user. |
| `Scheduler__DashboardPassword` | string | _(empty)_ | Basic-auth password. |
| `Jobs__N__Id/Cron/Url/Method` | | | Declarative recurring jobs (above). |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | url | _(unset)_ | Standard OpenTelemetry; unset = nothing exported. Also `OTEL_SERVICE_NAME` etc. Traces cover HTTP, jobs and Redis. |
| `ASPNETCORE_HTTP_PORTS` | string | `8080` | Listening port. |

**Secrets.** Any setting can come from a file by adding `_FILE`, e.g.
`Scheduler__RedisConnectionString_FILE=/run/secrets/redis` — use Docker/Kubernetes secret mounts so
credentials do not show in `docker inspect`. The service never logs its configuration.

## Operating it

See the **[runbook](docs/RUNBOOK.md)** for failed jobs, late callbacks, crashes, store outages and upgrades.

**Dashboard.** With `Scheduler__DashboardEnabled=true` you can see the job list, open a *failed* job and read its
exception, **retry** it, trigger a recurring job now, and delete jobs. Exposing it publicly is a risk: put it behind
your ingress/auth proxy even with Basic auth on.

**Multiple instances.** Run several replicas against the same Redis for availability; they share one job store and
each job runs on one of them. Every replica applies the `Jobs` config at startup (an idempotent upsert, so this is
safe).

<a id="timing"></a>
**Timing.** This is a *durable delayed callback* service at **second** granularity: a due job fires within about
`PollIntervalSeconds` of its time (default ≈ 1 s), plus your endpoint's latency (measured p99 < 1 s on Redis, see
[spike/](spike/README.md)). It is not a millisecond timer. A failed callback is retried by Hangfire with backoff
(first retry after roughly 15 to 45 s); a connection-level failure is first retried once immediately.

**Crash recovery.** If a worker dies mid-callback, the job is re-run after `Scheduler__InvisibilityTimeoutSeconds`
(default 120 s), so callbacks are at-least-once — keep your endpoint idempotent.

## Deployment

Runs on **any Kubernetes node**; it only needs network access to Redis or Postgres (if used) and to the URLs it calls.

```bash
kubectl apply -f deploy/k8s/deployment.yaml
```

The manifest deliberately has no `nodeSelector`, `affinity` or tolerations. Pin an explicit image version
(`:0.2`), not `:latest`, in production. Images are public on GHCR, multi-arch (`linux/amd64`, `linux/arm64`).

## Development

```bash
dotnet build AsyncScheduler.slnx
dotnet test AsyncScheduler.slnx      # ~25 s; starts real hosts and a real receiver, no Docker needed
docker build -t async-scheduler .
```

Versions come from [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning) (`version.json`);
every push to `main` that passes tests publishes a new patch version. See [CHANGELOG.md](CHANGELOG.md) and
[CONTRIBUTING.md](CONTRIBUTING.md).

## Roadmap

- Real Redis Sentinel/Cluster failover and long-soak testing (the [spike](spike/README.md) covers pause/restart only).

## Licence

[MIT](LICENSE). Third-party licences of the shipped image are listed in [NOTICE](NOTICE).
