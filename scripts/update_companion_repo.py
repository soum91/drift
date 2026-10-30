#!/usr/bin/env python3
"""Publish a Drift iOS companion entry after the matching GitHub release exists."""
import argparse
import json
from datetime import date
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--build", type=int, required=True)
    parser.add_argument("--url", required=True)
    parser.add_argument("--artifact", type=Path, required=True)
    parser.add_argument("--notes", type=Path, required=True)
    args = parser.parse_args()

    path = Path("source.json")
    catalog = json.loads(path.read_text(encoding="utf-8"))
    app = next(item for item in catalog["apps"] if item["bundleIdentifier"] == "dev.soum91.drift.companion")
    notes = args.notes.read_text(encoding="utf-8").strip() or "Maintenance release"
    entry = {
        "version": args.version,
        "buildVersion": str(args.build),
        "date": date.today().isoformat(),
        "localizedDescription": notes,
        "changelog": notes,
        "downloadURL": args.url,
        "size": args.artifact.stat().st_size,
        "minOSVersion": "16.0",
    }
    app["versions"] = [entry] + [v for v in app.get("versions", []) if v.get("version") != args.version][:29]
    serialized = json.dumps(catalog, indent=2, ensure_ascii=False) + "\n"
    path.write_text(serialized, encoding="utf-8")
    # Some sideloaders resolve a GitHub repository to /repo.json rather than
    # honoring /source.json. Keep both public URLs exactly synchronized.
    Path("repo.json").write_text(serialized, encoding="utf-8")


if __name__ == "__main__":
    main()
