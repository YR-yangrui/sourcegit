#!/usr/bin/env python3

import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone


SEMVER_PATTERN = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")
STABLE_RELEASE_PATTERN = re.compile(r"^stable-(\d+)\.(\d+)\.(\d+)\.[0-9a-fA-F]+$")
SCHEDULED_NIGHTLY_PATTERN = re.compile(
    r"^nightly-(\d{4})\.(\d{2})\.(\d{2})\.[0-9a-fA-F]+$"
)
MANUAL_NIGHTLY_PATTERN = re.compile(
    r"^nightly-(\d{4})\.(\d{2})\.(\d{2})\.[0-9a-fA-F]+\((\d+)\)$"
)


class GitLabApi:
    def __init__(self):
        api_url = require_env("CI_API_V4_URL").rstrip("/")
        project_id = require_env("CI_PROJECT_ID")
        self.project = urllib.parse.quote(project_id, safe="")
        self.base_url = f"{api_url}/projects/{self.project}"
        self.headers = auth_headers()

    def get_json(self, url):
        request = urllib.request.Request(url, headers=self.headers)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", errors="replace")
            raise SystemExit(f"GitLab API request failed: HTTP {e.code} {url}\n{body}") from e
        except urllib.error.URLError as e:
            raise SystemExit(f"GitLab API request failed: {url}\n{e}") from e

    def get_releases(self):
        releases = []
        page = 1
        while True:
            query = urllib.parse.urlencode(
                {
                    "per_page": 100,
                    "page": page,
                    "order_by": "released_at",
                    "sort": "desc",
                }
            )
            batch = self.get_json(f"{self.base_url}/releases?{query}")
            if not batch:
                break
            releases.extend(batch)
            page += 1
        return releases

    def get_manifest(self, release):
        for link in release.get("assets", {}).get("links", []):
            if link.get("name") == "sourcegit-update.json":
                url = link.get("url") or link.get("direct_asset_url")
                if not url:
                    return None

                request = urllib.request.Request(url, headers=self.headers)
                try:
                    with urllib.request.urlopen(request, timeout=30) as response:
                        return json.loads(response.read().decode("utf-8"))
                except Exception as e:
                    print(
                        f"Unable to read manifest from {release.get('tag_name', '<unknown>')}; "
                        f"continuing build. {e}"
                    )
                    return None
        return None


def require_env(name):
    value = os.environ.get(name, "")
    if not value:
        raise SystemExit(f"{name} is required.")
    return value


def auth_headers():
    token = os.environ.get("GITLAB_RELEASE_TOKEN", "")
    if token:
        return {"PRIVATE-TOKEN": token}

    token = os.environ.get("CI_JOB_TOKEN", "")
    if token:
        return {"JOB-TOKEN": token}

    raise SystemExit("GITLAB_RELEASE_TOKEN or CI_JOB_TOKEN is required.")


def parse_bool_var(name, default):
    value = os.environ.get(name, default).strip().lower()
    if value not in ("true", "false"):
        raise SystemExit(f"{name} must be true or false.")
    return value


def parse_semver(value, name):
    match = SEMVER_PATTERN.match(value)
    if not match:
        raise SystemExit(f"{name} must match MAJOR.MINOR.PATCH.")
    return tuple(int(part) for part in match.groups())


def format_semver(version):
    return ".".join(str(part) for part in version)


def stable_base_from_tag(tag_name):
    match = STABLE_RELEASE_PATTERN.match(tag_name)
    if match:
        return tuple(int(part) for part in match.groups())

    return None


def manual_nightly_number_from_tag(tag_name, date_base):
    match = MANUAL_NIGHTLY_PATTERN.match(tag_name)
    if not match:
        return None

    release_date = ".".join(match.groups()[:3])
    if release_date == date_base:
        return int(match.group(4))

    return None


def released_at_sort_key(release):
    return release.get("released_at") or release.get("created_at") or ""


def latest_stable_base(releases):
    bases = []
    for release in releases:
        base = stable_base_from_tag(release.get("tag_name", ""))
        if base is not None:
            bases.append(base)

    if not bases:
        return None

    return max(bases)


def next_stable_base(releases):
    latest = latest_stable_base(releases)
    if latest is None:
        return (1, 0, 0)

    major, minor, patch = latest
    return (major, minor, patch + 1)


def compute_stable_version(releases, short_sha):
    requested = os.environ.get("STABLE_VERSION", "").strip()
    latest = latest_stable_base(releases)

    if requested:
        requested_base = parse_semver(requested, "STABLE_VERSION")
        if latest is not None and requested_base <= latest:
            raise SystemExit(
                "STABLE_VERSION must be greater than the latest stable base version "
                f"{format_semver(latest)}."
            )
        base = requested_base
    else:
        base = next_stable_base(releases)

    base_version = format_semver(base)
    release_version = f"stable-{base_version}.{short_sha}"
    package_version = f"{base_version}.stable.{short_sha}"
    return "stable", base_version, release_version, package_version


def compute_scheduled_nightly_version(short_sha, date_base):
    release_version = f"nightly-{date_base}.{short_sha}"
    package_version = f"{date_base}.nightly.{short_sha}"
    return "scheduled-nightly", date_base, release_version, package_version


def compute_manual_nightly_version(releases, short_sha, date_base):
    numbers = []
    for release in releases:
        number = manual_nightly_number_from_tag(release.get("tag_name", ""), date_base)
        if number is not None:
            numbers.append(number)

    next_number = max(numbers, default=0) + 1
    release_version = f"nightly-{date_base}.{short_sha}({next_number})"
    package_version = f"{date_base}.nightly.{short_sha}.{next_number}"
    return "manual-nightly", date_base, release_version, package_version


def should_skip_scheduled_nightly(releases, current_commit):
    scheduled = [
        release
        for release in releases
        if SCHEDULED_NIGHTLY_PATTERN.match(release.get("tag_name", ""))
    ]
    if not scheduled:
        return False

    scheduled.sort(key=released_at_sort_key, reverse=True)
    latest_release = scheduled[0]
    manifest = GitLabApi().get_manifest(latest_release)
    if not manifest:
        print(
            "Latest scheduled nightly release has no readable sourcegit-update.json; "
            "continuing build."
        )
        return False

    published_commit = manifest.get("commit", "")
    if published_commit == current_commit:
        print(
            f"Current commit {current_commit} is already published in "
            f"{latest_release.get('tag_name', '<unknown>')}."
        )
        return True

    print(f"Nightly will build because published commit is {published_commit or '<empty>'}.")
    return False


def write_dotenv(values):
    os.makedirs("build", exist_ok=True)
    with open("build/release.env", "w", encoding="utf-8") as fh:
        for key, value in values.items():
            fh.write(f"{key}={value}\n")


def main():
    channel = os.environ.get("UPDATE_CHANNEL", "")
    source = os.environ.get("CI_PIPELINE_SOURCE", "")
    short_sha = require_env("CI_COMMIT_SHORT_SHA")
    current_commit = require_env("CI_COMMIT_SHA")
    build_date = datetime.now(timezone.utc)
    build_date_text = build_date.strftime("%Y-%m-%dT%H:%M:%SZ")
    date_base = build_date.strftime("%Y.%m.%d")

    if channel not in ("stable", "nightly"):
        raise SystemExit(f"Unsupported UPDATE_CHANNEL: {channel}")

    if channel == "stable" and source != "web":
        if source == "schedule":
            raise SystemExit("Stable releases must not be triggered by scheduled pipelines.")
        raise SystemExit("Stable releases must be triggered manually from the GitLab web UI.")

    if channel == "nightly" and source not in ("schedule", "web"):
        raise SystemExit("Nightly releases must be triggered by schedule or GitLab web UI.")

    skip_macos = parse_bool_var("SKIP_MACOS_PACKAGES", "true")
    skip_linux = parse_bool_var("SKIP_LINUX_PACKAGES", "true")

    releases = GitLabApi().get_releases()
    skip_nightly = False

    if channel == "stable":
        release_kind, base_version, release_version, package_version = compute_stable_version(
            releases, short_sha
        )
    elif source == "schedule":
        release_kind, base_version, release_version, package_version = (
            compute_scheduled_nightly_version(short_sha, date_base)
        )
        skip_nightly = should_skip_scheduled_nightly(releases, current_commit)
    else:
        release_kind, base_version, release_version, package_version = (
            compute_manual_nightly_version(releases, short_sha, date_base)
        )

    write_dotenv(
        {
            "UPDATE_CHANNEL": channel,
            "RELEASE_KIND": release_kind,
            "BASE_VERSION": base_version,
            "RELEASE_VERSION": release_version,
            "PACKAGE_VERSION": package_version,
            "BUILD_DATE": build_date_text,
            "SKIP_NIGHTLY_BUILD": "true" if skip_nightly else "false",
            "SKIP_MACOS_PACKAGES": skip_macos,
            "SKIP_LINUX_PACKAGES": skip_linux,
        }
    )

    print(f"Prepared {release_kind} release {release_version}.")
    if package_version != release_version:
        print(f"Package version: {package_version}")


if __name__ == "__main__":
    try:
        main()
    except BrokenPipeError:
        sys.exit(1)
