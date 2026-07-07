#!/usr/bin/env python3

import argparse
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request


STABLE_PATTERN = re.compile(r"^stable-\d+\.\d+\.\d+\.[0-9a-fA-F]+$")
SCHEDULED_NIGHTLY_PATTERN = re.compile(
    r"^nightly-\d{4}\.\d{2}\.\d{2}\.[0-9a-fA-F]+$"
)
SEPARATOR_PATTERN = re.compile(r"^\s*----------------\s*$", re.MULTILINE)
NO_CHANGELOG_MARKER = "(NO CHANGELOG)"
INITIAL_STABLE_RELEASE_NOTES = "- Initial stable release.\n----------------\n- 首个 stable 发布版本。"
INITIAL_SCHEDULED_NIGHTLY_RELEASE_NOTES = (
    "- Initial scheduled nightly release.\n----------------\n- 首个 scheduled nightly 发布版本。"
)


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


def git(args):
    try:
        return subprocess.check_output(
            ["git", *args],
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
    except subprocess.CalledProcessError as exc:
        raise SystemExit(f"git {' '.join(args)} failed.\n{exc.output}") from exc


def get_releases(api_url, project_id):
    headers = auth_headers()
    encoded_project = urllib.parse.quote(project_id, safe="")
    base_url = f"{api_url.rstrip('/')}/projects/{encoded_project}/releases"
    releases = []
    page = 1

    while True:
        query = urllib.parse.urlencode(
            {
                "order_by": "released_at",
                "sort": "desc",
                "per_page": 100,
                "page": page,
            }
        )
        request = urllib.request.Request(f"{base_url}?{query}", headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                batch = json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", errors="replace")
            raise SystemExit(
                f"GitLab releases request failed: HTTP {exc.code}\n{body}"
            ) from exc
        except urllib.error.URLError as exc:
            raise SystemExit(f"GitLab releases request failed: {exc}") from exc

        if not batch:
            break

        releases.extend(batch)
        page += 1

    return releases


def get_release_manifest_commit(release):
    for link in release.get("assets", {}).get("links", []):
        if link.get("name") != "sourcegit-update.json":
            continue

        try:
            url = link.get("url") or link.get("direct_asset_url")
            if not url:
                return None

            request = urllib.request.Request(url, headers=auth_headers())
            with urllib.request.urlopen(request, timeout=30) as response:
                manifest = json.loads(response.read().decode("utf-8"))
            commit = str(manifest.get("commit", "")).strip()
        except (
            AttributeError,
            json.JSONDecodeError,
            OSError,
            TypeError,
            UnicodeDecodeError,
            ValueError,
            urllib.error.HTTPError,
            urllib.error.URLError,
        ):
            return None

        return commit or None

    return None


def previous_release(release_kind, release_version, api_url, project_id):
    if release_kind == "stable":
        pattern = STABLE_PATTERN
    elif release_kind == "scheduled-nightly":
        pattern = SCHEDULED_NIGHTLY_PATTERN
    else:
        return None

    for release in get_releases(api_url, project_id):
        tag = str(release.get("tag_name", ""))
        if tag != release_version and pattern.match(tag):
            return release

    return None


def try_resolve_commit(ref):
    if not ref:
        return None

    try:
        return git(["rev-parse", f"{ref}^{{commit}}"]).strip()
    except SystemExit:
        return None


def resolve_commit(ref):
    commit = try_resolve_commit(ref)
    if commit:
        return commit

    git(["fetch", "--tags", "--force", "--quiet"])
    commit = try_resolve_commit(ref)
    if commit:
        return commit

    raise SystemExit(f"Unable to resolve Git ref {ref}.")


def commit_hashes(previous_commit, current_commit):
    if previous_commit:
        output = git(["log", "--reverse", "--format=%H", f"{previous_commit}..{current_commit}"])
    else:
        output = git(["log", "--reverse", "--format=%H", current_commit])

    return [line.strip() for line in output.splitlines() if line.strip()]


def normalize_commit_body(body):
    normalized = body.rstrip()
    if not normalized:
        return ""

    if "\n" not in normalized and ("\\n" in normalized or "\\r" in normalized):
        normalized = (
            normalized.replace("\\r\\n", "\n")
            .replace("\\n", "\n")
            .replace("\\r", "\n")
        )

    return normalized


def add_items(section, target):
    for line in section.splitlines():
        candidate = line.strip()
        if candidate.startswith("- ") and not candidate.upper().endswith(NO_CHANGELOG_MARKER):
            target.append(candidate)


def parse_commit_body(commit, english_items, chinese_items):
    body = normalize_commit_body(git(["show", "-s", "--format=%b", commit]))
    if not body:
        return

    match = SEPARATOR_PATTERN.search(body)
    if match:
        english = body[: match.start()]
        chinese = body[match.end() :]
    else:
        english = body
        chinese = ""

    add_items(english, english_items)
    add_items(chinese, chinese_items)


def build_release_notes(release_kind, release_version, commit):
    if release_kind == "manual-nightly":
        return "- Manual nightly validation build.\n----------------\n- 手动 nightly 验证构建。"

    api_url = require_env("CI_API_V4_URL")
    project_id = require_env("CI_PROJECT_ID")
    previous = previous_release(release_kind, release_version, api_url, project_id)
    if release_kind == "stable" and not previous:
        return INITIAL_STABLE_RELEASE_NOTES
    if release_kind == "scheduled-nightly" and not previous:
        return INITIAL_SCHEDULED_NIGHTLY_RELEASE_NOTES

    previous_commit = try_resolve_commit(get_release_manifest_commit(previous))
    if not previous_commit:
        previous_tag = str(previous.get("tag_name", ""))
        previous_commit = resolve_commit(previous_tag) if previous_tag else None

    english_items = []
    chinese_items = []
    for item in commit_hashes(previous_commit, commit):
        parse_commit_body(item, english_items, chinese_items)

    if not english_items and not chinese_items:
        return "- Maintenance update.\n----------------\n- 维护更新。"

    return "\n".join([*english_items, "----------------", *chinese_items])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-kind", required=True)
    parser.add_argument("--release-version", required=True)
    parser.add_argument("--commit", required=True)
    args = parser.parse_args()

    if args.release_kind not in ("stable", "scheduled-nightly", "manual-nightly"):
        raise SystemExit("release kind must be stable, scheduled-nightly, or manual-nightly.")

    print(build_release_notes(args.release_kind, args.release_version, args.commit))


if __name__ == "__main__":
    main()
