#!/usr/bin/env bash
# Builds the whole solution in Release. Warnings are errors (Directory.Build.props).
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build SoR.sln -c Release
