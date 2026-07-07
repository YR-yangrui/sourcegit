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

require_env UPDATE_CHANNEL
require_env RELEASE_KIND
require_env BASE_VERSION
require_env RELEASE_VERSION
require_env PACKAGE_VERSION
require_env BUILD_DATE
require_env CI_API_V4_URL
require_env CI_PROJECT_ID
require_env CI_COMMIT_SHA

case "$RELEASE_KIND" in
  stable)
    expected_channel="stable"
    release_title="SourceGit $RELEASE_VERSION"
    ;;
  scheduled-nightly)
    expected_channel="nightly"
    release_title="SourceGit $RELEASE_VERSION nightly"
    ;;
  manual-nightly)
    expected_channel="nightly"
    release_title="SourceGit $RELEASE_VERSION manual nightly"
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
package_name="sourcegit"
package_base="$api/projects/$project/packages/generic/$package_name/$PACKAGE_VERSION"
release_base="$api/projects/$project/releases"
release_tag="$RELEASE_VERSION"
encoded_release_tag="$(urlencode "$release_tag")"

release_status="$(curl --silent --show-error --output /dev/null --write-out "%{http_code}" \
  "${auth_args[@]}" \
  "$release_base/$encoded_release_tag")"

if [ "$release_status" = "200" ]; then
  if [ "$RELEASE_KIND" = "stable" ]; then
    echo "Stable release $release_tag already exists; skipping package upload and release creation."
    exit 0
  fi

  echo "Immutable release $release_tag already exists." >&2
  exit 1
elif [ "$release_status" != "404" ]; then
  echo "Unable to inspect release $release_tag; HTTP $release_status" >&2
  exit 1
fi

if [ ! -f build/sourcegit-update.json ]; then
  echo "build/sourcegit-update.json is required." >&2
  exit 1
fi

python3 - "$RELEASE_VERSION" "$PACKAGE_VERSION" "$UPDATE_CHANNEL" "$CI_COMMIT_SHA" <<'PY'
import json
import sys

release_version, package_version, channel, commit = sys.argv[1:5]

with open("build/sourcegit-update.json", "r", encoding="utf-8-sig") as fh:
    manifest = json.load(fh)

expected = {
    "version": release_version,
    "packageVersion": package_version,
    "channel": channel,
    "commit": commit,
}

for key, value in expected.items():
    actual = manifest.get(key)
    if actual != value:
        raise SystemExit(
            f"sourcegit-update.json has {key}={actual!r}; expected {value!r}."
        )

if not isinstance(manifest.get("assets"), list):
    raise SystemExit("sourcegit-update.json must contain an assets array.")

if not manifest.get("releaseNotes"):
    raise SystemExit("sourcegit-update.json must contain releaseNotes.")
PY

files=()
add_file() {
  local file="$1"
  [ -f "$file" ] || return 0
  local existing
  for existing in "${files[@]}"; do
    [ "$existing" = "$file" ] && return 0
  done
  files+=("$file")
}

add_file build/sourcegit-update.json
for file in \
  "build/sourcegit_${PACKAGE_VERSION}".*.zip \
  "build/sourcegit-${PACKAGE_VERSION}"*.AppImage \
  "build/sourcegit_${PACKAGE_VERSION}"*.deb \
  "build/sourcegit-${PACKAGE_VERSION}"*.rpm; do
  add_file "$file"
done

if [ "${#files[@]}" -eq 0 ]; then
  echo "No release assets found in build/." >&2
  exit 1
fi

upload_package_file() {
  local file="$1"
  local name
  local status
  local response

  name="$(basename "$file")"
  response="$(mktemp)"
  status="$(curl --silent --show-error --output "$response" --write-out "%{http_code}" \
    "${auth_args[@]}" \
    --upload-file "$file" \
    "$package_base/$name")"

  case "$status" in
    200|201|202)
      echo "Uploaded $name"
      rm -f "$response"
      return 0
      ;;
    409)
      echo "Package file $name already exists; continuing."
      rm -f "$response"
      return 0
      ;;
    400)
      if grep -Eiq 'already exists|already been taken|has already been taken' "$response"; then
        echo "Package file $name already exists; continuing."
        rm -f "$response"
        return 0
      fi
      ;;
  esac

  echo "Unable to upload $name; HTTP $status" >&2
  cat "$response" >&2
  rm -f "$response"
  exit 1
}

for file in "${files[@]}"; do
  echo "Uploading $(basename "$file")"
  upload_package_file "$file"
done

release_description="$(python3 - <<'PY'
import json

with open("build/sourcegit-update.json", "r", encoding="utf-8-sig") as fh:
    manifest = json.load(fh)

print(manifest.get("releaseNotes") or "SourceGit release.")
PY
)"

create_release_payload() {
  local tag="$1"
  local title="$2"
  local description="$3"
  local output="$4"
  shift 4

  python3 - "$tag" "$title" "$description" "$output" "$package_base" "$CI_COMMIT_SHA" "$@" <<'PY'
import json
import os
import sys

tag, title, description, output, package_base, ref = sys.argv[1:7]
files = sys.argv[7:]
links = []
for path in files:
    name = os.path.basename(path)
    links.append({
        "name": name,
        "url": f"{package_base}/{name}",
        "direct_asset_path": f"/{name}",
    })

payload = {
    "name": title,
    "tag_name": tag,
    "ref": ref,
    "description": description,
    "assets": {"links": links},
}

with open(output, "w", encoding="utf-8") as fh:
    json.dump(payload, fh, ensure_ascii=False)
PY
}

post_release() {
  local payload="$1"
  local response
  local status

  response="$(mktemp)"
  status="$(curl --silent --show-error --output "$response" --write-out "%{http_code}" \
    "${auth_args[@]}" \
    -H "Content-Type: application/json" \
    --data-binary "@$payload" \
    "$release_base")"

  case "$status" in
    200|201)
      cat "$response"
      rm -f "$response"
      return 0
      ;;
  esac

  echo "Unable to create release $release_tag; HTTP $status" >&2
  cat "$response" >&2
  rm -f "$response"
  exit 1
}

release_payload="$(mktemp)"
create_release_payload \
  "$release_tag" \
  "$release_title" \
  "$release_description" \
  "$release_payload" \
  "${files[@]}"

echo "Creating immutable $RELEASE_KIND release $release_tag"
post_release "$release_payload"
