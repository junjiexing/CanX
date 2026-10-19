#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later

"""Create deterministic multi-file BitTorrent and Metalink test fixtures."""

from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
from typing import Any
from xml.sax.saxutils import escape


def bencode(value: Any) -> bytes:
    if isinstance(value, int):
        return b"i" + str(value).encode("ascii") + b"e"
    if isinstance(value, bytes):
        return str(len(value)).encode("ascii") + b":" + value
    if isinstance(value, str):
        return bencode(value.encode("utf-8"))
    if isinstance(value, list):
        return b"l" + b"".join(bencode(item) for item in value) + b"e"
    if isinstance(value, dict):
        items = sorted(value.items(), key=lambda item: item[0].encode("utf-8"))
        return b"d" + b"".join(bencode(key) + bencode(item) for key, item in items) + b"e"
    raise TypeError(f"Cannot bencode {type(value)!r}")


def deterministic_bytes(label: str, length: int) -> bytes:
    seed = hashlib.sha256(label.encode("utf-8")).digest()
    return (seed * ((length + len(seed) - 1) // len(seed)))[:length]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-dir", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--tracker-url", required=True)
    args = parser.parse_args()

    source_dir = args.source_dir.resolve()
    output_dir = args.output_dir.resolve()
    torrent_root = source_dir / "canx-torrent-fixture"
    metalink_root = source_dir / "metalink"
    torrent_root.mkdir(parents=True, exist_ok=True)
    metalink_root.mkdir(parents=True, exist_ok=True)
    output_dir.mkdir(parents=True, exist_ok=True)

    torrent_files = [
        ("alpha.bin", deterministic_bytes("torrent-alpha", 768 * 1024)),
        ("beta.bin", deterministic_bytes("torrent-beta", 1280 * 1024)),
    ]
    for name, content in torrent_files:
        (torrent_root / name).write_bytes(content)

    piece_length = 64 * 1024
    concatenated = b"".join(content for _, content in torrent_files)
    pieces = b"".join(
        hashlib.sha1(concatenated[offset : offset + piece_length]).digest()
        for offset in range(0, len(concatenated), piece_length)
    )
    torrent_info = {
        "files": [
            {"length": len(content), "path": [name]}
            for name, content in torrent_files
        ],
        "name": torrent_root.name,
        "piece length": piece_length,
        "pieces": pieces,
    }
    torrent = {
        "announce": args.tracker_url,
        "announce-list": [[args.tracker_url]],
        "created by": "CanX integration fixtures",
        "info": torrent_info,
    }
    (output_dir / "canx-fixture.torrent").write_bytes(bencode(torrent))
    (output_dir / "canx-fixture.infohash").write_text(
        hashlib.sha1(bencode(torrent_info)).hexdigest(), encoding="ascii"
    )

    metalink_files = [
        ("metalink-one.bin", deterministic_bytes("metalink-one", 384 * 1024)),
        ("metalink-two.bin", deterministic_bytes("metalink-two", 640 * 1024)),
    ]
    file_elements: list[str] = []
    for name, content in metalink_files:
        (metalink_root / name).write_bytes(content)
        digest = hashlib.sha256(content).hexdigest()
        url = f"{args.base_url.rstrip('/')}/metalink/{name}"
        file_elements.append(
            f'  <file name="{escape(name)}">\n'
            f"    <size>{len(content)}</size>\n"
            f'    <hash type="sha-256">{digest}</hash>\n'
            f"    <url>{escape(url)}</url>\n"
            "  </file>"
        )
    metalink = (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<metalink xmlns="urn:ietf:params:xml:ns:metalink">\n'
        + "\n".join(file_elements)
        + "\n</metalink>\n"
    )
    (output_dir / "canx-fixture.meta4").write_text(metalink, encoding="utf-8")


if __name__ == "__main__":
    main()
