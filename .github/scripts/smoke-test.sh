#!/usr/bin/env bash
# Packs the tool, installs it from the local package only, starts it over stdio,
# and checks that tools/list returns every tool. Used by build.yml and release.yml.
set -euo pipefail

EXPECTED_TOOLS=10
# A version that can never exist on nuget.org, so the install below cannot pick up
# the published package instead of the one just built.
VERSION=0.0.0-ci
WORK=$(mktemp -d)

dotnet pack DotNetCoverageMcp.csproj --configuration Release -p:IsPacking=true \
  -p:Version="$VERSION" -o "$WORK/nupkg" --nologo

dotnet tool install dotnet-coverage-mcp --tool-path "$WORK/tool" \
  --source "$WORK/nupkg" --version "$VERSION"

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
count=$(grep '"id":2' "$WORK/out.json" | grep -o '"inputSchema"' | wc -l | tr -d ' ')

if [ "$count" -ne "$EXPECTED_TOOLS" ]; then
  echo "Smoke test failed: expected $EXPECTED_TOOLS tools, got $count" >&2
  echo "--- stdout" >&2; cat "$WORK/out.json" >&2
  echo "--- stderr" >&2; cat "$WORK/err.log" >&2
  exit 1
fi

echo "Smoke test passed: $count tools listed."
