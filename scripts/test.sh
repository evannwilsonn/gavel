#!/usr/bin/env sh
# The same checks CI runs: unit tests, the Fable build, and the JavaScript/.NET parity check.
set -e
cd "$(dirname "$0")/.."
dotnet test tests/Gavel.Tests -c Release
./scripts/build-js
node scripts/check-js.mjs
