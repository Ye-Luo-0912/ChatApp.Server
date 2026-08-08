#!/usr/bin/env bash
set -euo pipefail

# Run the committed baseline and the candidate on the same runner.  This is
# the fallback for machines that do not have a reviewed, hardware-matched
# baseline.  The script deliberately resets the database between runs and
# keeps the same rate, duration, k6 binary and process role for both sides.

server_root="${SERVER_ROOT:?SERVER_ROOT is required}"
baseline_commit="${BASELINE_COMMIT:?BASELINE_COMMIT is required}"
runner_id="${RUNNER_ID:?RUNNER_ID is required}"
rate="${RATE:-20}"
duration="${DURATION:-3m}"
rounds="${ROUNDS:-3}"
baseline_port="${BASELINE_PORT:-5087}"
database_name="${PGDATABASE:-ChatAppDatabase}"
database_user="${PGUSER:-postgres}"
database_host="${PGHOST:-127.0.0.1}"
database_password="${PGPASSWORD:-postgres}"
worktree="${RUNNER_TEMP:-/tmp}/chatapp-server-baseline-${GITHUB_RUN_ID:-$$}"
pid=""

cleanup() {
  set +e
  if [[ -n "$pid" ]]; then
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
  if [[ -d "$worktree" ]]; then
    if [[ -f "$worktree/paired-baseline.log" ]]; then
      cp "$worktree/paired-baseline.log" "$server_root/tests/load/paired-baseline.log" 2>/dev/null || true
    fi
    git -C "$server_root" worktree remove --force "$worktree" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

reset_database() {
  PGPASSWORD="$database_password" psql \
    -h "$database_host" -U "$database_user" -d "$database_name" \
    -v ON_ERROR_STOP=1 \
    -c 'DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;' >/dev/null
}

wait_for_api() {
  for _ in $(seq 1 60); do
    if curl -fsS "http://127.0.0.1:${baseline_port}/health/ready" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done
  echo "baseline API failed to become ready" >&2
  [[ -f "$worktree/paired-baseline.log" ]] && cat "$worktree/paired-baseline.log" >&2
  return 1
}

if ! git -C "$server_root" cat-file -e "${baseline_commit}^{commit}" 2>/dev/null; then
  git -C "$server_root" fetch --no-tags origin "$baseline_commit"
fi

if [[ -e "$worktree" ]]; then
  echo "refusing to reuse existing baseline worktree: $worktree" >&2
  exit 1
fi

git -C "$server_root" worktree add --detach "$worktree" "$baseline_commit"

# Pre-nupkg baselines (P0-7 era) reference the Realtime source tree via
# ..\ChatApp.RealtimeServices (sln) and $(RealtimeSourcePath) (csproj).
# Check out the pinned commit as a sibling of the baseline worktree so both
# resolution forms agree, and export RealtimeSourcePath so every MSBuild
# invocation (build, ef, run) resolves it.
if [[ -f "$worktree/Realtime.version" ]]; then
  realtime_commit=$(grep "^Commit=" "$worktree/Realtime.version" | cut -d= -f2)
  realtime_repo=$(grep "^Repo=" "$worktree/Realtime.version" | cut -d= -f2)
  if [[ -z "$realtime_commit" || -z "$realtime_repo" ]]; then
    echo "Realtime.version is missing Commit= or Repo= in $worktree/Realtime.version" >&2
    exit 1
  fi
  realtime_sibling="$(dirname "$worktree")/ChatApp.RealtimeServices"
  if [[ ! -d "$realtime_sibling/.git" ]]; then
    git clone --quiet --no-single-branch "$realtime_repo" "$realtime_sibling"
  fi
  git -C "$realtime_sibling" checkout --quiet --force "$realtime_commit"
  export RealtimeSourcePath="$realtime_sibling"
  echo "baseline uses RealtimeServices source at $realtime_commit ($realtime_sibling)"
fi

dotnet build "$worktree/ChatApp.Server.sln" -c Release --nologo

reset_database
(
  cd "$worktree"
  dotnet ef database update --project Infrastructure --startup-project Infrastructure --context UserDbContext
  USE_DOCKER=1 SEED_COUNT=60 TEST_USER_PREFIX=loaduser \
    PGUSER="$database_user" PGDATABASE="$database_name" PGHOST="$database_host" \
    PGPASSWORD="$database_password" bash tests/load/seed-users.sh
)

(
  cd "$worktree"
  env \
    DatabasePool__Role=Api \
    LoginRisk__Enabled=false \
    GeoLocation__AllowExternalFallback=false \
    ASPNETCORE_URLS="http://127.0.0.1:${baseline_port}" \
    dotnet run --project ChatApp.Server.csproj -c Release --no-build --no-launch-profile \
    > "$worktree/paired-baseline.log" 2>&1
) &
pid=$!
wait_for_api

(
  cd "$worktree/tests/load"
  TOKEN_PORT="$baseline_port" TOKEN_COUNT=50 TOKEN_MIN_SUCCESS=40 TOKEN_CONCURRENCY=4 \
    node generate-preset-tokens.mjs
  k6 run \
    -e BASE_URL="http://127.0.0.1:${baseline_port}" \
    -e TOKENS_FILE=./tokens.json \
    -e PROFILE=steady -e RATE=10 -e DURATION=90s \
    mixed-workload.k6.js > paired-baseline-warmup.log 2>&1 || true
  for round in $(seq 1 "$rounds"); do
    echo "=== paired baseline round ${round}/${rounds} ==="
    k6 run \
      -e BASE_URL="http://127.0.0.1:${baseline_port}" \
      -e TOKENS_FILE=./tokens.json \
      -e PROFILE=steady -e RATE="$rate" -e DURATION="$duration" \
      --out json="paired-baseline-${round}.json" \
      mixed-workload.k6.js
    node extract-summary.mjs \
      --input "paired-baseline-${round}.json" \
      --output "paired-baseline-${round}-summary.json" \
      --commit "$baseline_commit" \
      --runner "$runner_id" \
      --runtime "paired baseline steady ${duration} RATE=${rate} API role=Api LoginRisk=disabled"
  done
  node aggregate-runs.mjs \
    $(for round in $(seq 1 "$rounds"); do printf '%s ' --input "paired-baseline-${round}-summary.json"; done) \
    --output "$server_root/tests/load/baseline-paired.json" \
    --runner "$runner_id" \
    --runtime "paired baseline steady ${duration} RATE=${rate}; ${rounds} rounds; median + noise interval"
)

kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
pid=""
echo "paired baseline written to $server_root/tests/load/baseline-paired.json"
