#!/usr/bin/env python3
"""Deterministic VPM package archiver and release manifest generator for UniText."""

from __future__ import annotations

import argparse
import hashlib
import json
import zipfile
from pathlib import Path
from typing import List, Optional, Sequence, Tuple

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PKG_DIR = REPO_ROOT / "unity" / "Packages" / "dev.dreaminko.unitext"
FIXED_ZIP_TIME = (2020, 1, 1, 0, 0, 0)
IGNORED_FILES = {".DS_Store", "Thumbs.db"}


def sha256_file(path: Path) -> str:
    """Compute the hex SHA-256 digest of a file."""
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def verify_package_metas(pkg_dir: Path) -> None:
    """Ensure every non-meta file and directory inside pkg_dir has a sibling .meta file."""
    missing: List[str] = []
    for item in sorted(pkg_dir.rglob("*")):
        if item.name.endswith(".meta"):
            continue
        if item.name in IGNORED_FILES:
            continue
        meta_path = Path(f"{item}.meta")
        if not meta_path.is_file():
            missing.append(str(item.relative_to(pkg_dir)))
    if missing:
        raise AssertionError(
            "Missing Unity .meta files inside VPM package:\n  " + "\n  ".join(missing)
        )


def pack_package(
    pkg_dir: Path,
    out_dir: Path,
    repo: str,
    version: Optional[str] = None,
    tag: Optional[str] = None,
) -> Tuple[Path, Path]:
    """Pack a Unity VPM package directory into a deterministic zip and release package.json."""
    out_dir.mkdir(parents=True, exist_ok=True)

    manifest_path = pkg_dir / "package.json"
    if not manifest_path.is_file():
        raise FileNotFoundError(f"package.json not found at {manifest_path}")

    verify_package_metas(pkg_dir)

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    pkg_id = manifest["name"]
    resolved_version = version or manifest["version"]
    resolved_tag = tag or resolved_version

    zip_name = f"{pkg_id}-{resolved_version}.zip"
    zip_path = out_dir / zip_name

    files = sorted(
        (p for p in pkg_dir.rglob("*") if p.is_file() and p.name not in IGNORED_FILES),
        key=lambda p: p.relative_to(pkg_dir).as_posix(),
    )
    with zipfile.ZipFile(zip_path, "w") as zf:
        for file_path in files:
            rel = file_path.relative_to(pkg_dir).as_posix()
            zi = zipfile.ZipInfo(filename=rel, date_time=FIXED_ZIP_TIME)
            zi.compress_type = zipfile.ZIP_DEFLATED
            zi.external_attr = 0o644 << 16
            if rel == "package.json":
                updated = dict(manifest)
                updated["version"] = resolved_version
                data = (json.dumps(updated, indent=2) + "\n").encode("utf-8")
            else:
                data = file_path.read_bytes()
            zf.writestr(zi, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)

    digest = sha256_file(zip_path)
    release_manifest = dict(manifest)
    release_manifest["version"] = resolved_version
    release_manifest["url"] = (
        f"https://github.com/{repo}/releases/download/{resolved_tag}/{zip_name}"
    )
    release_manifest["zipSHA256"] = digest

    out_manifest = out_dir / "package.json"
    out_manifest.write_text(json.dumps(release_manifest, indent=2) + "\n", encoding="utf-8")

    print(f"Packed VPM archive: {zip_path}")
    print(f"zipSHA256:          {digest}")
    print(f"Release manifest:   {out_manifest}")
    return zip_path, out_manifest


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    """Parse CLI arguments for VPM package archiving."""
    parser = argparse.ArgumentParser(
        description="Pack UniText VPM package into a deterministic zip archive and release package.json."
    )
    parser.add_argument("--package-dir", type=Path, default=DEFAULT_PKG_DIR)
    parser.add_argument("--out-dir", type=Path, required=True)
    parser.add_argument("--repo", default="Dreaminko/UniText")
    parser.add_argument("--version", default=None)
    parser.add_argument("--tag", default=None)
    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)
    pack_package(
        pkg_dir=args.package_dir,
        out_dir=args.out_dir,
        repo=args.repo,
        version=args.version,
        tag=args.tag,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
