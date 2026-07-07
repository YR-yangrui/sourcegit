#!/usr/bin/env bash

set -euo pipefail

require_env() {
  local name="$1"
  if [ -z "${!name:-}" ]; then
    echo "$name is required." >&2
    exit 1
  fi
}

urlencode() {
  python3 - "$1" <<'PY'
import sys
import urllib.parse

print(urllib.parse.quote(sys.argv[1], safe=""))
PY
}

require_env DELETE_RELEASE
require_env CI_API_V4_URL
require_env CI_PROJECT_ID

if [ "${CI_PIPELINE_SOURCE:-}" != "web" ]; then
  echo "DELETE_RELEASE can only be used from a manually triggered web pipeline." >&2
  exit 1
fi

auth_args=()
if [ -n "${GITLAB_RELEASE_TOKEN:-}" ]; then
  auth_args=(-H "PRIVATE-TOKEN: $GITLAB_RELEASE_TOKEN")
elif [ -n "${CI_JOB_TOKEN:-}" ]; then
  auth_args=(-H "JOB-TOKEN: $CI_JOB_TOKEN")
else
  echo "CI_JOB_TOKEN or GITLAB_RELEASE_TOKEN is required." >&2
  exit 1
fi

api="${CI_API_V4_URL%/}"
project="$CI_PROJECT_ID"
mkdir -p build

fetch_paginated_json() {
  local endpoint="$1"
  local output_prefix="$2"
  local page=1
  local separator="&"

  if [[ "$endpoint" != *"?"* ]]; then
    separator="?"
  fi

  rm -f "build/$output_prefix-page-"*.json
  while true; do
    local page_file="build/$output_prefix-page-$page.json"
    curl --fail --silent --show-error \
      "${auth_args[@]}" \
      "${endpoint}${separator}per_page=100&page=$page" \
      -o "$page_file"

    local count
    count="$(python3 - "$page_file" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as fh:
    print(len(json.load(fh)))
PY
)"

    if [ "$count" = "0" ]; then
      break
    fi

    page=$((page + 1))
  done
}

combine_pages() {
  local input_prefix="$1"
  local output="$2"

  python3 - "$input_prefix" "$output" <<'PY'
import glob
import json
import re
import sys

prefix, output = sys.argv[1:3]

def page_number(path):
    match = re.search(rf"{re.escape(prefix)}-page-(\d+)\.json$", path)
    return int(match.group(1)) if match else 0

items = []
for path in sorted(glob.glob(f"build/{prefix}-page-*.json"), key=page_number):
    with open(path, "r", encoding="utf-8") as fh:
        page = json.load(fh)
    if page:
        items.extend(page)

with open(output, "w", encoding="utf-8") as fh:
    json.dump(items, fh)
PY
}

is_nightly_tag() {
  local tag="$1"
  local nightly_pattern='^nightly-[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9a-fA-F]+(\([1-9][0-9]*\))?$'

  [[ "$tag" =~ $nightly_pattern ]]
}

delete_git_tag() {
  local tag="$1"
  local encoded_tag
  local response
  local status

  encoded_tag="$(urlencode "$tag")"
  response="$(mktemp)"

  echo "Deleting git tag '$tag'."
  status="$(curl --silent --show-error --output "$response" --write-out "%{http_code}" \
    "${auth_args[@]}" \
    -X DELETE \
    "$api/projects/$project/repository/tags/$encoded_tag")"

  case "$status" in
    200|202|204)
      rm -f "$response"
      ;;
    404)
      rm -f "$response"
      echo "Git tag '$tag' was already absent."
      ;;
    *)
      echo "Unable to delete git tag '$tag'; HTTP $status" >&2
      cat "$response" >&2
      rm -f "$response"
      exit 1
      ;;
  esac
}

fetch_paginated_json "$api/projects/$project/releases?order_by=released_at&sort=desc" "delete-releases"
combine_pages "delete-releases" "build/delete-releases.json"

match_file="build/delete-release-match.tsv"
python3 - "$DELETE_RELEASE" "$match_file" <<'PY'
import json
import sys

target, output = sys.argv[1:3]

with open("build/delete-releases.json", "r", encoding="utf-8") as fh:
    releases = json.load(fh)

matches = [
    release
    for release in releases
    if str(release.get("name", "")) == target
]

if len(matches) != 1:
    print(f"DELETE_RELEASE must match exactly one release title; found {len(matches)}.", file=sys.stderr)
    print("", file=sys.stderr)
    print("Recent release titles:", file=sys.stderr)
    for release in releases[:30]:
        name = str(release.get("name", ""))
        tag = str(release.get("tag_name", ""))
        print(f"- {name} ({tag})", file=sys.stderr)
    sys.exit(1)

match = matches[0]
tag = str(match.get("tag_name", ""))
name = str(match.get("name", ""))
if not tag:
    print("Matched release has no tag_name.", file=sys.stderr)
    sys.exit(1)

with open(output, "w", encoding="utf-8") as fh:
    fh.write(f"{tag}\t{name}\n")
PY

tag="$(cut -f1 "$match_file")"
name="$(cut -f2- "$match_file")"
encoded_tag="$(urlencode "$tag")"
response="$(mktemp)"

echo "Deleting release '$name' with tag '$tag'."
status="$(curl --silent --show-error --output "$response" --write-out "%{http_code}" \
  "${auth_args[@]}" \
  -X DELETE \
  "$api/projects/$project/releases/$encoded_tag")"

case "$status" in
  200|202|204)
    rm -f "$response"
    if is_nightly_tag "$tag"; then
      delete_git_tag "$tag"
      echo "Deleted release '$name' with tag '$tag'. Git tag was deleted; package registry entries were not deleted."
    else
      echo "Deleted release '$name' with tag '$tag'. Git tag and package registry entries were not deleted."
    fi
    ;;
  *)
    echo "Unable to delete release '$name' with tag '$tag'; HTTP $status" >&2
    cat "$response" >&2
    rm -f "$response"
    exit 1
    ;;
esac
