# async-scheduler

[![CI](https://github.com/bklooste/async-scheduler/actions/workflows/ci.yml/badge.svg)](https://github.com/bklooste/async-scheduler/actions/workflows/ci.yml)
[![Image](https://img.shields.io/badge/ghcr.io-example--service-blue)](https://github.com/bklooste/async-scheduler/pkgs/container/async-scheduler%2Fservice)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

HTTP callback scheduler: durable delayed callbacks and long-running async jobs, driven by config, an HTTP API, and an operator dashboard. (Skeleton — extraction from lode in progress.)

## Quick start

```bash
docker compose up
curl http://localhost:8080/health
```

Or just the image, no dependencies:

```bash
docker run --rm -p 8080:8080 ghcr.io/bklooste/async-scheduler/service:latest
```

Images are public on GHCR — no pull secret needed. Multi-arch (`linux/amd64`, `linux/arm64`).

## Configuration

The configuration surface **is** the public API of a container. Settings come from environment
variables (ASP.NET Core `Section__Key` form), an optional mounted `appsettings.json`, or both — env wins.
Invalid configuration fails at startup with a clear message in the container log.

| Env var | Type | Default | Description |
|---|---|---|---|
| `Service__KeyPrefix` | string | `example` | Prefix for every Redis key the service writes, so several environments can share one Redis. `[A-Za-z0-9:_-]+`. |
| `Service__RedisConnectionString` | string | _(empty)_ | Redis connection string. Empty disables the Redis health check (quick-start mode). |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | url | _(unset)_ | Standard OpenTelemetry. Unset = nothing exported. Also honours `OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_PROTOCOL`, etc. |
| `ASPNETCORE_HTTP_PORTS` | string | `8080` | Listening port. |

**Secrets.** Any setting can be supplied from a file by adding `_FILE` to its env var, e.g.
`Service__RedisConnectionString_FILE=/run/secrets/redis` — use this with Docker/Kubernetes secret
mounts so credentials don't appear in `docker inspect`. The service never logs its configuration.

## API reference

| Method | Path | Description |
|---|---|---|
| GET | `/health` | `200` when healthy; includes per-dependency checks. |

## Deployment

Runs on **any Kubernetes node**; it only needs network access to its dependencies. Redis is an
external endpoint, never something the service ships or pins a node for.

```bash
kubectl apply -f deploy/k8s/deployment.yaml
```

The manifest deliberately has no `nodeSelector`, `affinity` or tolerations. Pin an explicit image
version (`:1.4`), not `:latest`, in production.

## Development

```bash
dotnet build AsyncScheduler.slnx
dotnet test AsyncScheduler.slnx
docker build -t async-scheduler .
```

Versions come from [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
(`version.json`); every push to `main` that passes tests publishes a new patch version. Bump
`version.json` for a minor/major. See [CHANGELOG.md](CHANGELOG.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

[MIT](LICENSE). Third-party licences of the shipped image are listed in [NOTICE](NOTICE).
