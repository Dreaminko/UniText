#!/usr/bin/env python3
"""VPM package archiver and GitHub Pages listing generator for UniText."""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import os
import sys
import urllib.request
import zipfile
from pathlib import Path
from typing import Dict, List, Optional, Sequence

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PKG_DIR = REPO_ROOT / "unity" / "Packages" / "dev.dreaminko.unitext"
DEFAULT_SOURCE_JSON = REPO_ROOT / "vpm" / "source.json"


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def verify_package_metas(pkg_dir: Path) -> None:
    missing: List[str] = []
    for item in sorted(pkg_dir.rglob("*")):
        if item.name.endswith(".meta"):
            continue
        if item.name in {".DS_Store", "Thumbs.db"}:
            continue
        meta_path = Path(f"{item}.meta")
        if not meta_path.is_file():
            missing.append(str(item.relative_to(pkg_dir)))
    if missing:
        raise AssertionError(
            "Missing Unity .meta files inside VPM package:\n  " + "\n  ".join(missing)
        )


def cmd_pack(args: argparse.Namespace) -> int:
    pkg_dir: Path = args.package_dir
    out_dir: Path = args.out_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    manifest_path = pkg_dir / "package.json"
    if not manifest_path.is_file():
        raise FileNotFoundError(f"package.json not found at {manifest_path}")

    verify_package_metas(pkg_dir)

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    pkg_id = manifest["name"]
    version = args.version or manifest["version"]
    tag = args.tag or f"v{version}"
    repo = args.repo

    zip_name = f"{pkg_id}-{version}.zip"
    zip_path = out_dir / zip_name

    # Build deterministic VPM zip containing package contents at root
    files = sorted(
        p for p in pkg_dir.rglob("*") if p.is_file() and p.name not in {".DS_Store", "Thumbs.db"}
    )
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for file_path in files:
            rel = file_path.relative_to(pkg_dir).as_posix()
            if rel == "package.json":
                # Write updated version if overridden by --version
                updated = dict(manifest)
                updated["version"] = version
                zf.writestr("package.json", json.dumps(updated, indent=2) + "\n")
            else:
                zf.write(file_path, arcname=rel)

    digest = sha256_file(zip_path)
    release_manifest = dict(manifest)
    release_manifest["version"] = version
    release_manifest["url"] = (
        f"https://github.com/{repo}/releases/download/{tag}/{zip_name}"
    )
    release_manifest["zipSHA256"] = digest

    out_manifest = out_dir / "package.json"
    out_manifest.write_text(json.dumps(release_manifest, indent=2) + "\n", encoding="utf-8")

    print(f"Packed VPM archive: {zip_path}")
    print(f"zipSHA256:          {digest}")
    print(f"Release manifest:   {out_manifest}")
    return 0


def fetch_github_release_manifests(repo: str, token: Optional[str]) -> List[dict]:
    url = f"https://api.github.com/repos/{repo}/releases?per_page=30"
    headers = {
        "Accept": "application/vnd.github+json",
        "User-Agent": "UniText-VPM-Listing/1.0",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"

    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            releases = json.loads(resp.read().decode("utf-8"))
    except Exception as exc:
        print(f"Warning: could not fetch releases for {repo}: {exc}", file=sys.stderr)
        return []

    manifests: List[dict] = []
    for rel in releases:
        if rel.get("draft"):
            continue
        for asset in rel.get("assets", []):
            if asset.get("name") == "package.json":
                dl_url = asset.get("browser_download_url")
                if not dl_url:
                    continue
                try:
                    dl_req = urllib.request.Request(dl_url, headers=headers)
                    with urllib.request.urlopen(dl_req, timeout=30) as aresp:
                        manifests.append(json.loads(aresp.read().decode("utf-8")))
                except Exception as exc:
                    print(f"Warning: failed downloading {dl_url}: {exc}", file=sys.stderr)
    return manifests


def render_landing_html(listing: dict, source: dict) -> str:
    repo_url = f"https://github.com/{source['githubRepos'][0]}"
    listing_url = listing["url"]
    vcc_uri = f"vcc://vpm/addRepo?url={listing_url}"

    pkg_rows: List[str] = []
    for pkg_id, pkg_data in listing.get("packages", {}).items():
        versions = pkg_data.get("versions", {})
        for ver in sorted(versions.keys(), reverse=True):
            meta = versions[ver]
            sha = html.escape(meta.get("zipSHA256", "")[:16]) + "..."
            zip_url = html.escape(meta.get("url", ""))
            pkg_rows.append(
                f"<tr><td><code>{html.escape(pkg_id)}</code></td>"
                f"<td><strong>{html.escape(ver)}</strong></td>"
                f"<td><code>{sha}</code></td>"
                f"<td><a href=\"{zip_url}\">Download VPM .zip</a></td></tr>"
            )

    rows_html = "\n          ".join(pkg_rows) or "<tr><td colspan=\"4\">No versions published yet.</td></tr>"

    return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>{html.escape(listing["name"])}</title>
  <style>
    :root {{ color-scheme: dark; }}
    body {{
      font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
      background: #0b1020; color: #e2e8f0; margin: 0; padding: 2.5rem 1.25rem;
      line-height: 1.6;
    }}
    main {{ max-width: 780px; margin: 0 auto; }}
    .card {{
      background: #131b31; border: 1px solid #263455; border-radius: 14px;
      padding: 1.75rem; margin-bottom: 1.5rem;
    }}
    h1 {{ margin-top: 0; font-size: 2rem; color: #ffffff; }}
    .actions {{ display: flex; flex-wrap: wrap; gap: 0.75rem; margin: 1.25rem 0; }}
    .btn {{
      display: inline-block; padding: 0.7rem 1.2rem; border-radius: 8px;
      font-weight: 600; text-decoration: none; cursor: pointer; border: none; font-size: 0.95rem;
    }}
    .btn-primary {{ background: #38bdf8; color: #082f49; }}
    .btn-secondary {{ background: #1e293b; color: #e2e8f0; border: 1px solid #334155; }}
    code, pre {{
      background: #090e1a; padding: 0.2rem 0.45rem; border-radius: 6px;
      font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 0.9rem;
    }}
    table {{ width: 100%; border-collapse: collapse; margin-top: 0.75rem; }}
    th, td {{ text-align: left; padding: 0.65rem 0.5rem; border-bottom: 1px solid #22304d; }}
    a {{ color: #38bdf8; }}
  </style>
</head>
<body>
  <main>
    <div class="card">
      <h1>{html.escape(listing["name"])}</h1>
      <p>{html.escape(source.get("description", ""))}</p>
      <div class="actions">
        <a class="btn btn-primary" href="{html.escape(vcc_uri)}">Add to VCC</a>
        <button class="btn btn-secondary" onclick="navigator.clipboard.writeText('{html.escape(listing_url)}'); this.textContent='Copied URL!';">Copy VPM URL</button>
        <a class="btn btn-secondary" href="{html.escape(repo_url)}">GitHub Repository</a>
        <a class="btn btn-secondary" href="{html.escape(repo_url)}/releases">Offline .unitypackage Releases</a>
      </div>
      <p>VPM Listing URL: <code>{html.escape(listing_url)}</code></p>
      <p><strong>Important:</strong> Choose either VCC/VPM <em>or</em> <code>.unitypackage</code> — never install both into the same Unity project.</p>
    </div>
    <div class="card">
      <h2>Available Versions</h2>
      <table>
        <thead>
          <tr><th>Package ID</th><th>Version</th><th>SHA-256</th><th>Archive</th></tr>
        </thead>
        <tbody>
          {rows_html}
        </tbody>
      </table>
    </div>
  </main>
</body>
</html>
"""


def cmd_listing(args: argparse.Namespace) -> int:
    source = json.loads(args.source_json.read_text(encoding="utf-8"))
    out_dir: Path = args.out_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    token = args.token or os.environ.get("GITHUB_TOKEN")
    manifests: List[dict] = []

    if not args.offline:
        for repo in source.get("githubRepos", []):
            manifests.extend(fetch_github_release_manifests(repo, token))

    for local_manifest in args.local_package_json or []:
        manifests.append(json.loads(local_manifest.read_text(encoding="utf-8")))

    packages: Dict[str, Dict[str, Dict[str, dict]]] = {}
    for m in manifests:
        pkg_id = m["name"]
        ver = m["version"]
        packages.setdefault(pkg_id, {"versions": {}})["versions"][ver] = m

    listing = {
        "name": source["name"],
        "id": source["id"],
        "url": source["url"],
        "author": source.get("author", {}).get("name", "Dreaminko"),
        "packages": packages,
    }

    index_json = out_dir / "index.json"
    index_json.write_text(json.dumps(listing, indent=2) + "\n", encoding="utf-8")

    index_html = out_dir / "index.html"
    index_html.write_text(render_landing_html(listing, source), encoding="utf-8")

    print(f"Generated VPM listing: {index_json}")
    print(f"Generated landing page: {index_html}")
    return 0


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build UniText VPM archives and GitHub Pages listing.")
    sub = parser.add_subparsers(dest="command", required=True)

    p_pack = sub.add_parser("pack", help="Pack unity/Packages/dev.dreaminko.unitext into a VPM zip.")
    p_pack.add_argument("--package-dir", type=Path, default=DEFAULT_PKG_DIR)
    p_pack.add_argument("--out-dir", type=Path, required=True)
    p_pack.add_argument("--repo", default="Dreaminko/UniText")
    p_pack.add_argument("--version", default=None)
    p_pack.add_argument("--tag", default=None)

    p_list = sub.add_parser("listing", help="Generate VPM index.json and landing index.html.")
    p_list.add_argument("--source-json", type=Path, default=DEFAULT_SOURCE_JSON)
    p_list.add_argument("--out-dir", type=Path, required=True)
    p_list.add_argument("--local-package-json", type=Path, action="append", default=[])
    p_list.add_argument("--token", default=None)
    p_list.add_argument("--offline", action="store_true", help="Skip GitHub Releases API query.")

    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)
    if args.command == "pack":
        return cmd_pack(args)
    if args.command == "listing":
        return cmd_listing(args)
    raise AssertionError(f"Unhandled command: {args.command}")


if __name__ == "__main__":
    raise SystemExit(main())
