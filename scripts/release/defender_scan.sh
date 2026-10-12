#!/usr/bin/env bash
# The release hook for scripts/release/defender_scan.ps1, for the bash release recipe. Hand it
# the staging folder and the zip, after the zip is made and before anything is uploaded:
#
#     bash scripts/release/defender_scan.sh "BuildStaging$TAG" "$ZIP" || exit 1
#
# Add -FailOnHistory to refuse a release while Defender on this PC has flagged a file of the
# same name before (the frozen launcher was, on 2026-10-11).
#
# It changes no Defender setting and quarantines nothing; see the .ps1 for what it reads.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
args=()
for p in "$@"; do case "$p" in -*) args+=("$p") ;; *) args+=("$(cygpath -w "$p")") ;; esac; done
powershell -NoProfile -File "$(cygpath -w "$here/defender_scan.ps1")" "${args[@]}"
