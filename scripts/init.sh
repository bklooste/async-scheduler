#!/usr/bin/env bash
# Turn this template into a real repo.   scripts/init.sh <repo-name> <ProjectName> [owner]
#   scripts/init.sh async-scheduler AsyncScheduler bklooste
set -euo pipefail
repo="${1:?repo name, e.g. async-scheduler}"; proj="${2:?PascalCase project, e.g. AsyncScheduler}"; owner="${3:-bklooste}"
cd "$(dirname "$0")/.."
git mv src/Example.Service "src/$proj" 2>/dev/null || mv src/Example.Service "src/$proj"
git mv tst/Example.Service.Tests "tst/$proj.Tests" 2>/dev/null || mv tst/Example.Service.Tests "tst/$proj.Tests"
for f in src/$proj/Example.Service.csproj tst/$proj.Tests/Example.Service.Tests.csproj; do mv "$f" "$(echo "$f" | sed "s/Example.Service/$proj/")"; done
mv Example.slnx "$proj.slnx"
grep -rlE 'Example\.Service|Example\.|example-service|bklooste' . --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj --exclude=init.sh --exclude=LICENSE \
  | xargs sed -i -e "s/Example\.Service/$proj/g" -e "s/Example\.slnx/$proj.slnx/g" -e "s/example-service/$repo/g" -e "s#bklooste/#$owner/#g"
echo "Done. Now: edit README (description, config table, API), ServiceOptions, then dotnet test."
