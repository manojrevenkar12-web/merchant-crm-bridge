#!/usr/bin/env bash
# Runs a command; when it fails, the last lines of its output become a GitHub annotation, so a red
# step can be read through the API without the raw log.
log=$(mktemp)
"$@" 2>&1 | tee "$log"
status=${PIPESTATUS[0]}
if [ "$status" -ne 0 ]; then
  tail -40 "$log" | grep -v '^\s*$' | python3 -c 'import sys; print("::error title=" + sys.argv[1] + "::" + sys.stdin.read().replace("%", "%25").replace("\r", "").replace("\n", "%0A"))' "$*"
fi
exit "$status"
