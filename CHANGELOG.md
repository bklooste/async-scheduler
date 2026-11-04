# Changelog

Every push to `main` publishes a new patch version automatically (see `version.json` — height-based,
not manually tagged), so not every version number gets its own entry here. This file tracks what
actually changed.

## 0.3

- `enqueue` takes `jobKey` (dedupe), `owner`, `keepResult`, `resultTtl`, `attempts`, `timeout` and `queue`; all
  optional, and without them `enqueue` behaves as in 0.2.
- `GET /v1/jobs/{jobKey}/result?userId=&wait=` returns a kept result verbatim, `202` while running, `404` for an
  unknown key or another owner, and waits up to `Scheduler__MaxWaitSeconds` (default 50).
- Kept-result jobs: `2xx`/`4xx` final, `5xx`/connection error/timeout retried to `attempts` (default 3), bodies over
  `Scheduler__MaxResultBytes` (default 1 MiB) stored as `ResultTooLarge`.
- A second worker pool for the `async` queue, `Scheduler__AsyncWorkerCount` (default 10).
- **Behaviour change:** a callback now carries the `Content-Type` the job was submitted with, instead of always
  `application/json`. A body submitted without one is still sent as `application/json`. Applies to jobs already
  stored, too.

## 0.2

- Initial release.
