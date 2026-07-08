import argparse
import json
import os
import sys

# prefab_diff.py redirects stdout on import unless it detects embedded output.
# This bridge must keep stdout open because SourceGit reads JSON from it.
os.environ.setdefault("PREFAB_DIFF_PRINT_OUTPUT", "1")

from prefab_diff import (
    convert_to_structured,
    diff_prefab,
    parse_structured_text,
)
from prefab_html_renderer import build_prefab_report_data


def _read_text(path):
    if not path:
        return ""
    with open(path, "r", encoding="utf-8", errors="replace") as file:
        return file.read()


def _hint_path(repo, path, fallback):
    if repo and path:
        return os.path.join(repo, path.replace("/", os.sep))
    return fallback or path or ""


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--old", required=True)
    parser.add_argument("--new", required=True)
    parser.add_argument("--repo", default="")
    parser.add_argument("--path", default="")
    parser.add_argument("--project-root", default="")
    parser.add_argument("--filename", default="prefab")
    args = parser.parse_args(argv)

    project_root = args.project_root or args.repo
    hint = _hint_path(args.repo, args.path, args.new)

    # Reuse Unity-Prefab-Diff's semantic parser, but emit its data contract as
    # JSON so SourceGit can keep the native Avalonia hierarchy view.
    old_text = convert_to_structured(_read_text(args.old), hint, "", project_root)
    new_text = convert_to_structured(_read_text(args.new), hint, "", project_root)
    old_nodes = parse_structured_text(old_text)
    new_nodes = parse_structured_text(new_text)
    report = build_prefab_report_data(args.filename, diff_prefab(old_nodes, new_nodes), old_nodes, new_nodes)

    json.dump(report, sys.stdout, ensure_ascii=False, separators=(",", ":"))


if __name__ == "__main__":
    main()
