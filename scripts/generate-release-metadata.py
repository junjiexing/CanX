#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later

"""Generate release notices, a license bundle, and an SPDX 2.3 SBOM."""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as et
from pathlib import Path
from typing import Any


LICENSE_FILE = re.compile(r"^(?:licen[cs]e|copying|notice|copyright)(?:[._-].*)?$", re.IGNORECASE)
SPDX_SAFE = re.compile(r"^[A-Za-z0-9.+()\- ]+(?:\s+(?:AND|OR|WITH)\s+[A-Za-z0-9.+()\- ]+)*$")
MAXIMUM_LICENSE_BYTES = 4 * 1024 * 1024


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository-root", type=Path, required=True)
    parser.add_argument("--publish-directory", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument("--rid", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--native-lock", type=Path, required=True)
    parser.add_argument("--static-source-root", type=Path)
    parser.add_argument("--quickjs-lock", type=Path, required=True)
    parser.add_argument("--quickjs-source-root", type=Path)
    parser.add_argument("--assets-file", type=Path, required=True)
    return parser.parse_args()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def run(repository: Path, *command: str) -> str:
    return subprocess.check_output(command, cwd=repository, text=True).strip()


def child_text(parent: et.Element, name: str) -> str | None:
    child = parent.find(f"{{*}}{name}")
    if child is None or not child.text:
        return None
    return child.text.strip()


def safe_name(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]+", "-", value).strip("-") or "component"


def spdx_id(kind: str, name: str, version: str) -> str:
    digest = hashlib.sha256(f"{kind}\0{name}\0{version}".encode()).hexdigest()[:16]
    return f"SPDXRef-{safe_name(kind)}-{safe_name(name)}-{digest}"


def declared_license(value: str | None) -> str:
    if not value:
        return "NOASSERTION"
    normalized = value.split(";", 1)[0].strip()
    normalized = re.sub(r"\s+\(build-only\)$", "", normalized, flags=re.IGNORECASE)
    return normalized if SPDX_SAFE.fullmatch(normalized) else "NOASSERTION"


def copy_license_candidates(source: Path | None, destination: Path, maximum_depth: int) -> list[str]:
    if source is None or not source.is_dir():
        return []
    copied: list[str] = []
    for candidate in sorted(source.rglob("*")):
        if not candidate.is_file():
            continue
        relative = candidate.relative_to(source)
        if len(relative.parts) > maximum_depth or not LICENSE_FILE.fullmatch(candidate.name):
            continue
        if candidate.stat().st_size > MAXIMUM_LICENSE_BYTES:
            continue
        target_name = safe_name("--".join(relative.parts))
        target = destination / target_name
        suffix = 2
        while target.exists() and target.read_bytes() != candidate.read_bytes():
            target = destination / f"{target_name}.{suffix}"
            suffix += 1
        destination.mkdir(parents=True, exist_ok=True)
        if not target.exists():
            shutil.copyfile(candidate, target)
        copied.append(f"licenses/{target.relative_to(destination.parent.parent)}")
    return copied


def load_managed_packages(assets_file: Path, rid: str, licenses_root: Path) -> list[dict[str, Any]]:
    assets = json.loads(assets_file.read_text(encoding="utf-8"))
    target_name = next((name for name in assets["targets"] if name.endswith(f"/{rid}")), None)
    if target_name is None:
        raise ValueError(f"project.assets.json has no target for {rid}")
    package_folders = [Path(path) for path in assets.get("packageFolders", {})]
    packages: list[dict[str, Any]] = []
    for key, target in sorted(assets["targets"][target_name].items(), key=lambda item: item[0].lower()):
        library = assets.get("libraries", {}).get(key, {})
        if target.get("type") != "package" or library.get("type") != "package":
            continue
        name, version = key.rsplit("/", 1)
        relative_path = library.get("path", f"{name.lower()}/{version}")
        package_root = next((root / relative_path for root in package_folders if (root / relative_path).is_dir()), None)
        nuspec = next(iter(package_root.glob("*.nuspec")), None) if package_root else None
        license_value = None
        license_type = None
        project_url = None
        repository_url = None
        repository_commit = None
        license_files: list[str] = []
        if nuspec:
            metadata = et.parse(nuspec).getroot().find("{*}metadata")
            if metadata is not None:
                license_element = metadata.find("{*}license")
                if license_element is not None and license_element.text:
                    license_value = license_element.text.strip()
                    license_type = license_element.attrib.get("type")
                project_url = child_text(metadata, "projectUrl")
                repository = metadata.find("{*}repository")
                if repository is not None:
                    repository_url = repository.attrib.get("url")
                    repository_commit = repository.attrib.get("commit")
        license_destination = licenses_root / "managed" / safe_name(f"{name}-{version}")
        if package_root:
            if license_type == "file" and license_value:
                explicit = (package_root / license_value).resolve()
                if explicit.is_file() and explicit.is_relative_to(package_root.resolve()):
                    license_destination.mkdir(parents=True, exist_ok=True)
                    target_path = license_destination / safe_name(explicit.name)
                    shutil.copyfile(explicit, target_path)
                    license_files.append(str(target_path.relative_to(licenses_root.parent)))
            license_files.extend(copy_license_candidates(package_root, license_destination, 1))
        nupkg = next(iter(package_root.glob("*.nupkg")), None) if package_root else None
        packages.append(
            {
                "kind": "NuGet",
                "name": name,
                "version": version,
                "license": license_value if license_type == "expression" else "NOASSERTION",
                "licenseDetail": f"file: {license_value}" if license_type == "file" else license_value,
                "source": repository_url or project_url or "NOASSERTION",
                "commit": repository_commit,
                "purl": f"pkg:nuget/{name}@{version}",
                "checksum": sha256(nupkg) if nupkg else None,
                "licenseFiles": sorted(set(license_files)),
                "buildOnly": name.startswith("Microsoft.DotNet.IL") or name.startswith("runtime.") and "ILCompiler" in name,
            }
        )
    return packages


def load_native_packages(lock_file: Path, source_root: Path | None, licenses_root: Path) -> list[dict[str, Any]]:
    packages: list[dict[str, Any]] = []
    for raw_line in lock_file.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        name, repository, revision, license_detail = line.split("|", 3)
        source = source_root / name if source_root else None
        license_destination = licenses_root / "native" / safe_name(name)
        packages.append(
            {
                "kind": "native",
                "name": name,
                "version": revision,
                "license": declared_license(license_detail),
                "licenseDetail": license_detail,
                "source": repository,
                "commit": revision,
                "purl": None,
                "checksum": None,
                "licenseFiles": copy_license_candidates(source, license_destination, 3),
                "buildOnly": "build-only" in license_detail.lower(),
            }
        )
    return packages


def load_shell_lock(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for raw_line in path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        key, value = line.split("=", 1)
        values[key] = value
    return values


def quickjs_package(lock_file: Path, source_root: Path | None, licenses_root: Path) -> dict[str, Any]:
    values = load_shell_lock(lock_file)
    destination = licenses_root / "native" / "quickjs"
    return {
        "kind": "native",
        "name": "quickjs",
        "version": values["version"],
        "license": "MIT",
        "licenseDetail": "MIT",
        "source": values["url"],
        "commit": None,
        "purl": None,
        "checksum": values["sha256"],
        "licenseFiles": copy_license_candidates(source_root, destination, 2),
        "buildOnly": False,
    }


def copy_dotnet_notices(licenses_root: Path) -> list[str]:
    roots: list[Path] = []
    dotnet_root = os.environ.get("DOTNET_ROOT")
    if dotnet_root:
        roots.append(Path(dotnet_root))
    executable = shutil.which("dotnet")
    if executable:
        roots.append(Path(executable).resolve().parent)
    copied: list[str] = []
    destination = licenses_root / "dotnet-runtime"
    for root in roots:
        for name in ("LICENSE.txt", "ThirdPartyNotices.txt"):
            candidate = root / name
            if candidate.is_file():
                destination.mkdir(parents=True, exist_ok=True)
                target = destination / name
                if not target.exists():
                    shutil.copyfile(candidate, target)
                copied.append(str(target.relative_to(licenses_root.parent)))
        if copied:
            break
    return copied


def spdx_package(component: dict[str, Any]) -> dict[str, Any]:
    package: dict[str, Any] = {
        "SPDXID": spdx_id(component["kind"], component["name"], component["version"]),
        "name": component["name"],
        "versionInfo": component["version"],
        "downloadLocation": component["source"] or "NOASSERTION",
        "filesAnalyzed": False,
        "licenseConcluded": "NOASSERTION",
        "licenseDeclared": declared_license(component.get("license")),
        "copyrightText": "NOASSERTION",
    }
    external_references = []
    if component.get("purl"):
        external_references.append(
            {
                "referenceCategory": "PACKAGE-MANAGER",
                "referenceType": "purl",
                "referenceLocator": component["purl"],
            }
        )
    if external_references:
        package["externalRefs"] = external_references
    if component.get("checksum"):
        package["checksums"] = [{"algorithm": "SHA256", "checksumValue": component["checksum"]}]
    details = []
    if component.get("licenseDetail") and component["licenseDetail"] != component.get("license"):
        details.append(f"Declared license metadata: {component['licenseDetail']}")
    if component.get("commit"):
        details.append(f"Source revision: {component['commit']}")
    if component.get("buildOnly"):
        details.append("Build-only dependency; it is not shipped as a runtime component.")
    if details:
        package["comment"] = " ".join(details)
    return package


def write_notices(path: Path, components: list[dict[str, Any]], dotnet_files: list[str]) -> None:
    lines = [
        "# CanX third-party notices",
        "",
        "This inventory is generated from the resolved Linux x64 NuGet graph and pinned native source locks.",
        "The bundled license files remain authoritative; this inventory is not legal advice.",
        "",
        "## .NET runtime",
        "",
        "CanX NativeAOT incorporates the .NET runtime. Its license and notices are included at:",
        "",
    ]
    lines.extend(f"- `{item}`" for item in dotnet_files)
    for heading, kind in (("Managed packages", "NuGet"), ("Native sources", "native")):
        lines.extend(["", f"## {heading}", "", "| Component | Version/revision | Declared license | Source | License files |", "|---|---|---|---|---|"])
        for component in (item for item in components if item["kind"] == kind):
            values = [
                component["name"],
                component["version"],
                component.get("licenseDetail") or component.get("license") or "NOASSERTION",
                component.get("source") or "NOASSERTION",
                ", ".join(f"`{item}`" for item in component["licenseFiles"]) or "—",
            ]
            escaped = [str(value).replace("|", "\\|").replace("\n", " ") for value in values]
            lines.append("| " + " | ".join(escaped) + " |")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    args = arguments()
    repository = args.repository_root.resolve()
    publish_directory = args.publish_directory.resolve()
    output = args.output_directory.resolve()
    binary = publish_directory / "CanX.App"
    if not binary.is_file():
        raise FileNotFoundError(f"NativeAOT executable not found: {binary}")
    output.mkdir(parents=True, exist_ok=True)
    licenses_root = output / "licenses"
    licenses_root.mkdir(parents=True, exist_ok=True)

    commit = run(repository, "git", "rev-parse", "HEAD")
    commit_time = int(run(repository, "git", "show", "-s", "--format=%ct", "HEAD"))
    created = dt.datetime.fromtimestamp(commit_time, tz=dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")

    shutil.copyfile(repository / "LICENSE", output / "LICENSE")
    managed = load_managed_packages(args.assets_file.resolve(), args.rid, licenses_root)
    native = load_native_packages(
        args.native_lock.resolve(),
        args.static_source_root.resolve() if args.static_source_root else None,
        licenses_root,
    )
    native.append(
        quickjs_package(
            args.quickjs_lock.resolve(),
            args.quickjs_source_root.resolve() if args.quickjs_source_root else None,
            licenses_root,
        )
    )
    components = managed + native
    dotnet_files = copy_dotnet_notices(licenses_root)

    app_id = spdx_id("application", "CanX", args.version)
    app_package = {
        "SPDXID": app_id,
        "name": "CanX",
        "versionInfo": args.version,
        "downloadLocation": "https://github.com/junjiexing/CanX",
        "filesAnalyzed": False,
        "licenseConcluded": "GPL-3.0-or-later",
        "licenseDeclared": "GPL-3.0-or-later",
        "copyrightText": "NOASSERTION",
        "checksums": [{"algorithm": "SHA256", "checksumValue": sha256(binary)}],
        "externalRefs": [
            {
                "referenceCategory": "SECURITY",
                "referenceType": "cpe23Type",
                "referenceLocator": f"cpe:2.3:a:canx:canx:{args.version}:*:*:*:*:*:*:*",
            }
        ],
        "comment": f"Git commit: {commit}; target: {args.rid}; NativeAOT executable: CanX.App",
    }
    spdx_components = [spdx_package(component) for component in components]
    relationships = [{"spdxElementId": "SPDXRef-DOCUMENT", "relationshipType": "DESCRIBES", "relatedSpdxElement": app_id}]
    for component, package in zip(components, spdx_components, strict=True):
        if component["buildOnly"]:
            relationships.append(
                {"spdxElementId": package["SPDXID"], "relationshipType": "BUILD_DEPENDENCY_OF", "relatedSpdxElement": app_id}
            )
        else:
            relationships.append(
                {"spdxElementId": app_id, "relationshipType": "DEPENDS_ON", "relatedSpdxElement": package["SPDXID"]}
            )
    sbom = {
        "spdxVersion": "SPDX-2.3",
        "dataLicense": "CC0-1.0",
        "SPDXID": "SPDXRef-DOCUMENT",
        "name": f"CanX-{args.version}-{args.rid}",
        "documentNamespace": f"https://github.com/junjiexing/CanX/sbom/{args.version}/{args.rid}/{commit}",
        "creationInfo": {"created": created, "creators": ["Tool: CanX-release-metadata-generator"]},
        "documentDescribes": [app_id],
        "packages": [app_package, *spdx_components],
        "relationships": relationships,
    }
    (output / "sbom.spdx.json").write_text(json.dumps(sbom, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    write_notices(output / "THIRD-PARTY-NOTICES.md", components, dotnet_files)

    source_lines = [
        "# Corresponding source information",
        "",
        f"- CanX version: `{args.version}`",
        f"- Target: `{args.rid}`",
        f"- CanX commit: `{commit}`",
        "- Repository: <https://github.com/junjiexing/CanX>",
        "",
        "CanX is licensed GPL-3.0-or-later. The complete corresponding source for this binary",
        "is the public repository at the commit above; the pinned native repository and revision",
        "list is recorded in `THIRD-PARTY-NOTICES.md` and `sbom.spdx.json`.",
        "Rebuild the Linux x64 artifact by checking out that commit on a Docker/Podman host with:",
        "",
        "```bash",
        "scripts/run-ubuntu-22.04-linux-x64.sh",
        "```",
        "",
        "The resulting ELF must pass the GLIBC 2.35 and native dependency audits before distribution.",
    ]
    (output / "SOURCE-CODE.md").write_text("\n".join(source_lines) + "\n", encoding="utf-8")
    print(
        f"CANX_RELEASE_METADATA_OK managed={len(managed)} native={len(native)} "
        f"licenses={sum(len(item['licenseFiles']) for item in components) + len(dotnet_files)}"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, et.ParseError) as exception:
        print(f"release metadata generation failed: {exception}", file=sys.stderr)
        raise SystemExit(1) from exception
