#!/bin/sh
set -u

ASSETS_DIR=/app/public/_nuxt
PUBLIC_DIR=/app/public
# The CDN keys its cache on the raw Accept-Encoding string, not on normalised buckets, so each value
# real clients send must be warmed separately; the second line is Googlebot's and Safari's.
ENCODINGS='gzip, deflate, br, zstd
gzip, deflate, br
gzip, deflate
none'
BATCH=8
PROBE_ATTEMPTS=60
PROBE_DELAY=5

fetch() {
  if [ "$2" = none ]; then
    wget -q -T 30 -O /dev/null "$1"
  else
    wget -q -T 30 -O /dev/null --header="Accept-Encoding: $2" "$1"
  fi
}

warm_cdn() {
  base=$(printf '%s' "${NUXT_APP_CDN_URL:-}" | sed 's|/*$||')
  [ -n "$base" ] || return 0
  [ -d "$ASSETS_DIR" ] || return 0

  list=$(mktemp) || return 0
  find "$ASSETS_DIR" -type f | sed "s|^$PUBLIC_DIR||" | sort >"$list"
  total=$(wc -l <"$list" | tr -d ' ')
  if [ "$total" -eq 0 ]; then
    rm -f "$list"
    return 0
  fi

  # Traefik routes here only once the health check passes; warming earlier caches the deploy error page.
  attempt=0
  until fetch "$base$(head -n 1 "$list")" none; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge "$PROBE_ATTEMPTS" ]; then
      echo "cdn-warm: $base unreachable after $attempt attempts, skipping" >&2
      rm -f "$list"
      return 0
    fi
    sleep "$PROBE_DELAY"
  done

  failed=$(mktemp) || {
    rm -f "$list"
    return 0
  }
  n=0
  saved_ifs=$IFS
  # Header values contain spaces, so only newline may separate them.
  IFS='
'
  for encoding in $ENCODINGS; do
    while read -r path; do
      { fetch "$base$path" "$encoding" || echo "$path ($encoding)" >>"$failed"; } &
      n=$((n + 1))
      if [ $((n % BATCH)) -eq 0 ]; then wait; fi
    done <"$list"
    wait
  done
  IFS=$saved_ifs

  echo "cdn-warm: $total assets x $(printf '%s\n' "$ENCODINGS" | wc -l | tr -d ' ') encodings via $base, $(wc -l <"$failed" | tr -d ' ') failed"
  rm -f "$list" "$failed"
}

warm_cdn &

exec "$@"
