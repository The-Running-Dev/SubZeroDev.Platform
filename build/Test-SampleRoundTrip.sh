#!/usr/bin/env bash
# Runs both sample hosts against one shared store, in a non-Development environment, and proves
# S1's health/readiness/correlation, S3's peer registration and S5.3's kill-and-restart delivery —
# the brief's first and third CI assertions. Shared by two callers: .github/workflows/build.yml
# (against project references) and .github/workflows/release.yml's verify-restore job (against the
# packages just published to GitHub Packages, per S9.2/S9.3) -- one script rather than two
# near-identical copies drifting apart.
#
# Assumes both samples are already built at samples/*/bin/Release/net10.0 (via `dotnet build -c
# Release`, with whichever MSBuild properties the caller needs) and ASPNETCORE_ENVIRONMENT is set
# by the caller.
set -euo pipefail

root=${1:-$PWD}
db_file=${2:-shared-sample.db}

# Both roles point at one store, outside either's own output directory — the only way PeerHost and
# SettingsFingerprint see anything besides "no peer ever registered here".
export Platform__Persistence__ConnectionString="Data Source=$root/$db_file"

# Migrate mode creates the shared store and puts its SQLite file in WAL — a host that finds a file
# still in the default journal mode aborts startup, per the contract, and a store with no schema
# has nothing for the standard call's checks to be healthy against.
( cd "$root/samples/SubZeroDev.Platform.Sample.Web/bin/Release/net10.0" \
  && ./SubZeroDev.Platform.Sample.Web migrate )
( cd "$root/samples/SubZeroDev.Platform.Sample.Worker/bin/Release/net10.0" \
  && ./SubZeroDev.Platform.Sample.Worker migrate )

# The built executables rather than `dotnet run`, so the process signalled at the end is the host
# itself and its exit status is the host's. Each runs from its own output directory, because
# appsettings.json sits beside the binary and the content root defaults to the working directory.
# `exec` replaces the subshell, so $! is the host.
( cd "$root/samples/SubZeroDev.Platform.Sample.Web/bin/Release/net10.0" \
  && ASPNETCORE_URLS=http://127.0.0.1:5199 exec ./SubZeroDev.Platform.Sample.Web \
) > web.log 2>&1 &
web=$!

( cd "$root/samples/SubZeroDev.Platform.Sample.Worker/bin/Release/net10.0" \
  && exec ./SubZeroDev.Platform.Sample.Worker \
) > worker.log 2>&1 &
worker=$!

fail() { echo "::error::$1"; cat web.log worker.log worker-restart.log 2>/dev/null || true; kill $web $worker 2>/dev/null || true; exit 1; }

for attempt in $(seq 1 30); do
  if curl -fsS http://127.0.0.1:5199/health/ready > /dev/null 2>&1 \
    && curl -fsS http://127.0.0.1:5100/health/ready > /dev/null 2>&1; then
    break
  fi
  kill -0 $web 2>/dev/null || fail "the web host exited before serving"
  kill -0 $worker 2>/dev/null || fail "the worker host exited before serving"
  sleep 1
  [ "$attempt" -lt 30 ] || fail "neither probe answered within 30 seconds"
done

curl -fsS http://127.0.0.1:5199/health/live > /dev/null || fail "web liveness did not answer"

# The worker probe is loopback-only, and its readiness answered above. Its absence from the
# machine's routable address is asserted in the test suite rather than here.
echo "Both roles served their probes in $ASPNETCORE_ENVIRONMENT."

# Stop dispatch before committing the order. The web process is then killed rather than shut down,
# proving the durable row—not process memory—bridges commit to delivery.
kill -TERM $worker
worker_status=0
wait "$worker" || worker_status=$?
[ "$worker_status" -eq 0 ] || fail "the first worker exited with status $worker_status"

order=$(curl -fsS -X POST http://127.0.0.1:5199/orders \
  -H 'content-type: application/json' \
  --data '{"name":"restart-survivor","quantity":1}') \
  || fail "the sample order did not commit"
order_id=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["orderId"])' <<<"$order")
[ -n "$order_id" ] || fail "the committed order response carried no id"

kill -KILL $web
web_status=0
wait "$web" || web_status=$?
[ "$web_status" -ne 0 ] || fail "the web process was expected to be killed"

( cd "$root/samples/SubZeroDev.Platform.Sample.Worker/bin/Release/net10.0" \
  && exec ./SubZeroDev.Platform.Sample.Worker \
) > worker-restart.log 2>&1 &
worker=$!

# Queried from the outbox table itself rather than grepped from worker-restart.log: the log line
# is real evidence of dispatch, but it reaches that file through Serilog's async sink
# (WriteTo.Async, by design non-blocking per S8) and then through this process's redirected
# stdout, so its on-disk appearance can lag well behind the dispatch it reports. processed_at is
# written by the same transaction that runs the handler, so it is set the instant delivery
# actually commits.
for attempt in $(seq 1 30); do
  processed_at=$(sqlite3 "$root/$db_file" \
    "SELECT processed_at FROM platform_outbox WHERE type = 'sample.order-placed' AND json_extract(payload, '\$.orderId') = '$order_id';")
  if [ -n "$processed_at" ]; then
    break
  fi
  kill -0 $worker 2>/dev/null || fail "the restarted worker exited before dispatch"
  sleep 1
  [ "$attempt" -lt 30 ] || fail "the committed outbox row was not delivered after restart"
done

echo "The outbox row survived process death and dispatched order $order_id after restart."

# SIGTERM is the ordinary shutdown signal, and a graceful worker exits zero on it.
kill -TERM $worker
worker_status=0
wait "$worker" || worker_status=$?
[ "$worker_status" -eq 0 ] || fail "the restarted worker exited with status $worker_status"
