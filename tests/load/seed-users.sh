#!/usr/bin/env bash
# 预置性能测试用户到 Postgres。
# 用法：
#   直连模式：SEED_COUNT=50 bash seed-users.sh  （依赖本地 psql + PG* 环境变量）
#   Docker 模式：USE_DOCKER=1 SEED_COUNT=50 bash seed-users.sh
#
# 密码统一为 Passw0rd!，BCrypt(work factor=10) 哈希预先在本地生成并验证。
# NormalizedEmail/NormalizedUserName 使用 upper()，与 AuthService.RegisterAsync 保持一致。
set -euo pipefail

SEED_COUNT="${SEED_COUNT:-50}"
PASSWORD_HASH='$2a$10$CO8BKw/dIMsCuniPeLZVH.dXEu4JvqHofp7xCzxrLC5kB66Acnemm'
TEST_PREFIX="${TEST_USER_PREFIX:-loaduser}"

if [[ ! "$SEED_COUNT" =~ ^[1-9][0-9]{0,4}$ ]] || ((10#$SEED_COUNT > 10000)); then
  echo "SEED_COUNT 必须是 1..10000 的整数" >&2
  exit 1
fi

if [[ ! "$TEST_PREFIX" =~ ^[A-Za-z0-9_-]+$ ]] || ((${#TEST_PREFIX} > 128)); then
  echo "TEST_USER_PREFIX 只能包含字母、数字、下划线和连字符，且长度不超过 128" >&2
  exit 1
fi

# psql 调用：直连或通过 docker exec 进入 postgres 容器
run_psql() {
  if [ "${USE_DOCKER:-0}" = "1" ]; then
    local cid
    cid=$(docker ps -q --filter ancestor=postgres:16.8 | head -n 1)
    if [ -z "$cid" ]; then
      echo "未找到 postgres 容器" >&2
      exit 1
    fi
    docker exec "$cid" psql -X -qAt -U "${PGUSER:-postgres}" -d "${PGDATABASE:-ChatAppDatabase}" -v ON_ERROR_STOP=1 -c "$1"
  else
    export PGHOST="${PGHOST:-127.0.0.1}"
    export PGPORT="${PGPORT:-5432}"
    export PGUSER="${PGUSER:-postgres}"
    export PGPASSWORD="${PGPASSWORD:-postgres}"
    export PGDATABASE="${PGDATABASE:-ChatAppDatabase}"
    psql -X -qAt -v ON_ERROR_STOP=1 -c "$1"
  fi
}

echo "预置 ${SEED_COUNT} 个测试用户（前缀 ${TEST_PREFIX}，密码 Passw0rd!）..."

run_psql "
  INSERT INTO \"AspNetUsers\" (
    \"Id\", \"UserName\", \"NormalizedUserName\", \"Email\", \"NormalizedEmail\",
    \"EmailConfirmed\", \"PasswordHash\", \"CreatedDate\", \"Gender\", \"Status\",
    \"LockoutEnabled\", \"AccessFailedCount\", \"PhoneNumberConfirmed\", \"TwoFactorEnabled\"
  )
  SELECT
    seed.id::bigint,
    '${TEST_PREFIX}' || seed.id::text,
    UPPER('${TEST_PREFIX}' || seed.id::text),
    '${TEST_PREFIX}' || seed.id::text || '@load.test',
    UPPER('${TEST_PREFIX}' || seed.id::text || '@load.test'),
    TRUE, '${PASSWORD_HASH}', NOW(), FALSE, 0, TRUE, 0, FALSE, FALSE
  FROM generate_series(1, ${SEED_COUNT}) AS seed(id)
  ON CONFLICT (\"NormalizedUserName\") DO NOTHING;
" > /dev/null

count=$(run_psql "
  SELECT COUNT(*)
  FROM \"AspNetUsers\" AS users
  WHERE users.\"UserName\" IN (
    SELECT '${TEST_PREFIX}' || seed.id::text
    FROM generate_series(1, ${SEED_COUNT}) AS seed(id)
  );
")
echo "完成。现有测试用户数：${count}"
