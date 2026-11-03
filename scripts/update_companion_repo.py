#!/usr/bin/env python3
"""Publish a Drift iOS companion entry after the matching GitHub release exists."""
import argparse
import json
from datetime import datetime, timezone
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
    marketing_version = args.version[:-(len(str(args.build)) + 1)] if args.version.endswith("." + str(args.build)) else args.version
    entry = {
        "version": marketing_version,
        "buildVersion": str(args.build),
        "date": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "localizedDescription": notes,
        "changelog": notes,
        "downloadURL": args.url,
        "size": args.artifact.stat().st_size,
        "minOSVersion": "16.0",
    }
    app["versions"] = [entry] + [v for v in app.get("versions", []) if str(v.get("buildVersion")) != str(args.build)][:29]
    app.update({
        "version": entry["version"],
        "buildVersion": entry["buildVersion"],
        "versionDate": entry["date"],
        "downloadURL": entry["downloadURL"],
        "size": entry["size"],
        "minOSVersion": entry["minOSVersion"],
    })
    serialized = json.dumps(catalog, indent=2, ensure_ascii=False) + "\n"
    path.write_text(serialized, encoding="utf-8")
    # Some sideloaders resolve a GitHub repository to /repo.json rather than
    # honoring /source.json. Keep both public URLs exactly synchronized.
    Path("repo.json").write_text(serialized, encoding="utf-8")


if __name__ == "__main__":
    main()
