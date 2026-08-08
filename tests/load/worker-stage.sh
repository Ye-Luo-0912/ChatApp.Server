#!/usr/bin/env bash
# Worker-only performance stage.
#
# The producer is deliberately a small SQL fixture: it creates durable
# notification jobs, then observes the real Worker process draining them. The
# API is not started in this stage, so Worker DB/Redis activity cannot enter an
# API latency or allocation sample.
set -euo pipefail

NOTIFICATION_COUNT="${NOTIFICATION_COUNT:-1000}"
DURATION_SECONDS="${DURATION_SECONDS:-180}"
TEST_USER_PREFIX="${TEST_USER_PREFIX:-workerload}"
RUN_ID="${WORKER_RUN_ID:-${GITHUB_RUN_ID:-local-${RANDOM}}}"
WORKER_BASE_URL="${WORKER_BASE_URL:-http://127.0.0.1:5090}"
METRICS_FILE="${WORKER_METRICS_FILE:-worker-stage.csv}"

if [[ ! "$NOTIFICATION_COUNT" =~ ^[1-9][0-9]{0,5}$ ]]; then
  echo "NOTIFICATION_COUNT 必须是正整数" >&2
  exit 1
fi
if [[ ! "$DURATION_SECONDS" =~ ^[1-9][0-9]{0,5}$ ]]; then
  echo "DURATION_SECONDS 必须是正整数" >&2
  exit 1
fi
if [[ ! "$TEST_USER_PREFIX" =~ ^[A-Za-z0-9_-]+$ ]]; then
  echo "TEST_USER_PREFIX 包含非法字符" >&2
  exit 1
fi
if [[ ! "$RUN_ID" =~ ^[A-Za-z0-9_-]+$ ]]; then
  echo "WORKER_RUN_ID 包含非法字符" >&2
  exit 1
fi

run_psql() {
  if [ "${USE_DOCKER:-0}" = "1" ]; then
    local cid
    cid=$(docker ps -q --filter ancestor=postgres:16.8 | head -n 1)
    if [ -z "$cid" ]; then
      echo "未找到 postgres 容器" >&2
      exit 1
    fi
    docker exec "$cid" psql -X -qAt -U "${PGUSER:-postgres}" \
      -d "${PGDATABASE:-ChatAppDatabase}" -v ON_ERROR_STOP=1 -c "$1"
    return
  fi

  export PGHOST="${PGHOST:-127.0.0.1}"
  export PGPORT="${PGPORT:-5432}"
  export PGUSER="${PGUSER:-postgres}"
  export PGPASSWORD="${PGPASSWORD:-postgres}"
  export PGDATABASE="${PGDATABASE:-ChatAppDatabase}"
  psql -X -qAt -v ON_ERROR_STOP=1 -c "$1"
}

id_prefix="worker-stage-${RUN_ID}"
echo "写入 ${NOTIFICATION_COUNT} 个 Worker notification jobs（prefix=${TEST_USER_PREFIX}）..."
run_psql "
  WITH worker_users AS (
    SELECT \"Id\", row_number() OVER (ORDER BY \"Id\") - 1 AS user_index
    FROM \"AspNetUsers\"
    WHERE \"UserName\" LIKE '${TEST_USER_PREFIX}%'
  ), jobs AS (
    SELECT generate_series(1, ${NOTIFICATION_COUNT}) AS job_index
  )
  INSERT INTO \"T_NotificationOutbox\" (
    \"UserId\", \"Type\", \"Title\", \"Body\", \"PreferEmail\",
    \"Status\", \"AttemptCount\", \"CreatedAt\", \"UpdatedAt\",
    \"NextAttemptAt\", \"IdempotencyKey\"
  )
  SELECT
    worker_users.\"Id\",
    'worker-stage',
    'Worker stage',
    'durable worker throughput probe',
    FALSE,
    0,
    0,
    NOW(),
    NOW(),
    NOW(),
    '${id_prefix}-' || jobs.job_index::text
  FROM jobs
  JOIN worker_users
    ON worker_users.user_index = ((jobs.job_index - 1) % GREATEST(
      1, (SELECT COUNT(*) FROM worker_users)))
  ON CONFLICT DO NOTHING;
" > /dev/null

worker_jobs=$(run_psql "SELECT COUNT(*) FROM \"T_NotificationOutbox\" WHERE \"IdempotencyKey\" LIKE '${id_prefix}-%';")
if [[ "$worker_jobs" != "$NOTIFICATION_COUNT" ]]; then
  echo "Worker jobs 写入数量不符：${worker_jobs} != ${NOTIFICATION_COUNT}" >&2
  exit 1
fi

printf 'timestamp,pending,processing,dead,oldest_age_seconds\n' > "$METRICS_FILE"
deadline=$((SECONDS + DURATION_SECONDS))
last_pending=$NOTIFICATION_COUNT

while (( SECONDS < deadline )); do
  row=$(run_psql "
    SELECT
      COUNT(*) FILTER (WHERE \"Status\" IN (0, 1, 3)),
      COUNT(*) FILTER (WHERE \"Status\" = 1),
      COUNT(*) FILTER (WHERE \"Status\" = 4),
      COALESCE(EXTRACT(EPOCH FROM (NOW() - (MIN(\"CreatedAt\") FILTER (WHERE \"Status\" IN (0, 1, 3))))), 0)
    FROM \"T_NotificationOutbox\"
    WHERE \"IdempotencyKey\" LIKE '${id_prefix}-%';
  ")
  IFS='|' read -r pending processing dead oldest_age <<< "$row"
  pending="${pending:-0}"
  processing="${processing:-0}"
  dead="${dead:-0}"
  oldest_age="${oldest_age:-0}"
  last_pending="$pending"
  printf '%s,%s,%s,%s,%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    "$pending" "$processing" "$dead" "$oldest_age" | tee -a "$METRICS_FILE"

  if [[ "$dead" -gt 0 ]]; then
    echo "Worker stage 出现 Dead notification jobs" >&2
    exit 1
  fi
  if [[ "$pending" -eq 0 ]]; then
    echo "Worker stage drain 完成"
    break
  fi
  sleep 2
done

if [[ "$last_pending" -ne 0 ]]; then
  echo "Worker stage 超时，剩余 active jobs=${last_pending}" >&2
  exit 1
fi

if command -v curl >/dev/null 2>&1; then
  curl -fsS "${WORKER_BASE_URL}/debug/metrics" > worker-metrics.json
fi

echo "Worker stage 通过：active jobs 已归零，记录见 ${METRICS_FILE}"
