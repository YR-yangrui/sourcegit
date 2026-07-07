#!/usr/bin/env bash

set -euo pipefail

require_env() {
  local name="$1"
  if [ -z "${!name:-}" ]; then
    echo "$name is required." >&2
    exit 1
  fi
}

require_env UPDATE_CHANNEL
require_env RELEASE_KIND
require_env CI_API_V4_URL
require_env CI_PROJECT_ID

case "$RELEASE_KIND" in
  stable)
    keep_count=30
    expected_channel="stable"
    ;;
  scheduled-nightly)
    keep_count=90
    expected_channel="nightly"
    ;;
  manual-nightly)
    keep_count=10
    expected_channel="nightly"
    ;;
  *)
    echo "RELEASE_KIND must be stable, scheduled-nightly, or manual-nightly." >&2
    exit 1
    ;;
esac

if [ "$UPDATE_CHANNEL" != "$expected_channel" ]; then
  echo "UPDATE_CHANNEL must be $expected_channel for RELEASE_KIND=$RELEASE_KIND." >&2
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

urlencode() {
  python3 - "$1" <<'PY'
import sys
import urllib.parse

print(urllib.parse.quote(sys.argv[1], safe=""))
PY
}

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

should_delete_git_tag() {
  [ "$RELEASE_KIND" = "scheduled-nightly" ] || [ "$RELEASE_KIND" = "manual-nightly" ]
}

delete_git_tag() {
  local tag="$1"
  local encoded_tag
  local response
  local status

  encoded_tag="$(urlencode "$tag")"
  response="$(mktemp)"

  echo "Deleting old $RELEASE_KIND git tag $tag"
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
      echo "Git tag $tag was already absent."
      ;;
    *)
      echo "Unable to delete git tag '$tag'; HTTP $status" >&2
      cat "$response" >&2
      rm -f "$response"
      exit 1
      ;;
  esac
}

fetch_paginated_json "$api/projects/$project/releases?order_by=released_at&sort=desc" "releases"
combine_pages "releases" "build/releases.json"

mapfile -t delete_entries < <(python3 - "$RELEASE_KIND" "$keep_count" <<'PY'
import json
import re
import sys

release_kind = sys.argv[1]
keep_count = int(sys.argv[2])

patterns = {
    "stable": re.compile(r"^stable-\d+\.\d+\.\d+\.[0-9a-fA-F]+$"),
    "scheduled-nightly": re.compile(r"^nightly-\d{4}\.\d{2}\.\d{2}\.[0-9a-fA-F]+$"),
    "manual-nightly": re.compile(r"^nightly-\d{4}\.\d{2}\.\d{2}\.[0-9a-fA-F]+\([1-9][0-9]*\)$"),
}

def package_version_from_tag(tag):
    stable = re.match(r"^stable-(\d+\.\d+\.\d+)\.([0-9a-fA-F]+)$", tag)
    if stable:
        return f"{stable.group(1)}.stable.{stable.group(2)}"

    scheduled = re.match(r"^nightly-(\d{4}\.\d{2}\.\d{2})\.([0-9a-fA-F]+)$", tag)
    if scheduled:
        return f"{scheduled.group(1)}.nightly.{scheduled.group(2)}"

    manual = re.match(r"^nightly-(\d{4}\.\d{2}\.\d{2})\.([0-9a-fA-F]+)\(([1-9][0-9]*)\)$", tag)
    if manual:
        return f"{manual.group(1)}.nightly.{manual.group(2)}.{manual.group(3)}"

    return tag

with open("build/releases.json", "r", encoding="utf-8") as fh:
    releases = json.load(fh)

pattern = patterns[release_kind]
selected = [
    str(release.get("tag_name", ""))
    for release in releases
    if pattern.match(str(release.get("tag_name", "")))
]

for tag in selected[keep_count:]:
    print(f"{tag}\t{package_version_from_tag(tag)}")
PY
)

if [ "${#delete_entries[@]}" -eq 0 ]; then
  echo "No old $RELEASE_KIND releases to delete."
  exit 0
fi

package_versions=()
git_tags=()
for entry in "${delete_entries[@]}"; do
  tag="${entry%%$'\t'*}"
  package_version="${entry#*$'\t'}"
  package_versions+=("$package_version")
  if should_delete_git_tag; then
    git_tags+=("$tag")
  fi
done

fetch_paginated_json "$api/projects/$project/packages?package_name=sourcegit&order_by=created_at&sort=desc" "packages"
combine_pages "packages" "build/packages.json"

mapfile -t delete_package_ids < <(python3 - "${package_versions[@]}" <<'PY'
import json
import sys

versions = set(sys.argv[1:])

with open("build/packages.json", "r", encoding="utf-8") as fh:
    packages = json.load(fh)

for package in packages:
    if str(package.get("version", "")) in versions:
        package_id = package.get("id")
        if package_id is not None:
            print(package_id)
PY
)

for entry in "${delete_entries[@]}"; do
  tag="${entry%%$'\t'*}"
  encoded_tag="$(urlencode "$tag")"

  echo "Deleting old $RELEASE_KIND release $tag"
  curl --fail --silent --show-error \
    "${auth_args[@]}" \
    -X DELETE \
    "$api/projects/$project/releases/$encoded_tag"
done

if [ "${#delete_package_ids[@]}" -eq 0 ]; then
  echo "No matching package versions to delete for old $RELEASE_KIND releases."
else
  for id in "${delete_package_ids[@]}"; do
    echo "Deleting old $RELEASE_KIND package $id"
    curl --fail --silent --show-error \
      "${auth_args[@]}" \
      -X DELETE \
      "$api/projects/$project/packages/$id"
  done
fi

for tag in "${git_tags[@]}"; do
  delete_git_tag "$tag"
done
