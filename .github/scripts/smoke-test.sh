#!/usr/bin/env bash
# Installs the tool, starts it over stdio, and checks that tools/list returns every tool.
#   smoke-test.sh            packs this checkout and installs that package only (dotnet.yml, release.yml)
#   smoke-test.sh <version>  installs <version> as published on nuget.org (release.yml, after the push)
set -euo pipefail

EXPECTED_TOOLS=11
EXPECTED_READ_ONLY=7
WORK=$(mktemp -d)

if [ $# -eq 0 ]; then
  # A version that can never exist on nuget.org, so the install below cannot pick up
  # the published package instead of the one just built.
  VERSION=0.0.0-ci
  dotnet pack DotNetCoverageMcp.csproj --configuration Release -p:IsPacking=true \
    -p:Version="$VERSION" -o "$WORK/nupkg" --nologo
  dotnet tool install dotnet-coverage-mcp --tool-path "$WORK/tool" \
    --source "$WORK/nupkg" --version "$VERSION"
else
  VERSION=$1
  # nuget.org can list a version shortly before every endpoint serves it, so retry.
  for attempt in 1 2 3 4 5; do
    dotnet tool install dotnet-coverage-mcp --tool-path "$WORK/tool" --version "$VERSION" && break
    [ "$attempt" -lt 5 ] || { echo "Could not install $VERSION from nuget.org" >&2; exit 1; }
    sleep 30
  done
fi

request() {
  printf '%s\n' \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"ci","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
  # Keep stdin open: the server shuts down on EOF, and closing it straight after
  # the last request makes it exit before it has written any response.
  sleep 10
}

request | "$WORK/tool/dotnet-coverage-mcp" > "$WORK/out.json" 2> "$WORK/err.log" || true

# The tools/list reply is the line with id 2; count the tool entries in it.
fail() {
  echo "Smoke test failed: $1" >&2
  echo "--- stdout" >&2; cat "$WORK/out.json" >&2
  echo "--- stderr" >&2; cat "$WORK/err.log" >&2
  exit 1
}

tools=$(grep '"id":2' "$WORK/out.json" || true)
count=$(echo "$tools" | grep -o '"inputSchema"' | wc -l | tr -d ' ')
[ "$count" -eq "$EXPECTED_TOOLS" ] || fail "expected $EXPECTED_TOOLS tools, got $count"

# Clients decide what to auto-approve from these hints; a wrong one prompts on every call
# or skips a prompt that should happen. Only cleanup_session deletes anything.
destructive=$(echo "$tools" | grep -o '"destructiveHint":true' | wc -l | tr -d ' ')
[ "$destructive" -eq 1 ] || fail "expected 1 destructive tool (cleanup_session), got $destructive"
readonly=$(echo "$tools" | grep -o '"readOnlyHint":true' | wc -l | tr -d ' ')
[ "$readonly" -eq "$EXPECTED_READ_ONLY" ] || fail "expected $EXPECTED_READ_ONLY read-only tools, got $readonly"

grep '"id":1' "$WORK/out.json" | grep -q '"instructions":' || fail "initialize result has no server instructions"

echo "Smoke test passed: $count tools listed, annotations and instructions present."
