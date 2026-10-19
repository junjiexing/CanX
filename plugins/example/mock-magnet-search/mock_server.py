#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later

"""Dependency-free HTTP API used by the Mock Magnet Search CanX plugin."""

from __future__ import annotations

import argparse
import binascii
import hashlib
import json
import struct
import zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, quote_plus, unquote, urlparse


class MockSearchHandler(BaseHTTPRequestHandler):
    server_version = "CanXMockSearch/1.1"

    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        request = urlparse(self.path)
        if request.path == "/health":
            self._write_json(200, {"status": "ok"})
            return
        if request.path == "/search":
            self._search(parse_qs(request.query))
            return
        if request.path.startswith("/resolve/"):
            self._resolve(unquote(request.path[len("/resolve/") :]))
            return
        if request.path.startswith("/covers/") and request.path.endswith(".png"):
            self._cover(unquote(request.path[len("/covers/") : -len(".png")]))
            return
        self._write_json(404, {"error": "not_found"})

    def log_message(self, message: str, *args: object) -> None:
        print(f"{self.client_address[0]} - {message % args}", flush=True)

    def _search(self, parameters: dict[str, list[str]]) -> None:
        query = parameters.get("q", [""])[0].strip()
        if not query:
            self._write_json(400, {"error": "query_required"})
            return
        limit = _bounded_int(parameters.get("limit", ["20"])[0], 1, 100, 20)
        cursor = _bounded_int(parameters.get("cursor", ["0"])[0], 0, 1000, 0)
        total = 8
        stop = min(cursor + limit, total)
        items = []
        for index in range(cursor, stop):
            item_id = hashlib.sha1(f"{query}:{index}".encode("utf-8")).hexdigest()[:20]
            items.append(
                {
                    "id": item_id,
                    "title": f"{query} mock resource {index + 1}",
                    "description": "Local mock result returned over the plugin HTTP API.",
                    "coverUrl": None if index % 4 == 3 else self._cover_url(item_id),
                    "sizeBytes": (index + 1) * 734_003_200,
                    "seeders": 80 - index * 3,
                    "leechers": 4 + index,
                }
            )
        result: dict[str, object] = {"items": items}
        if stop < total:
            result["nextCursor"] = str(stop)
        self._write_json(200, result)

    def _resolve(self, item_id: str) -> None:
        if not item_id or len(item_id) > 256:
            self._write_json(400, {"error": "invalid_id"})
            return
        info_hash = hashlib.sha1(item_id.encode("utf-8")).hexdigest()
        display_name = quote_plus(f"CanX mock resource {item_id[:8]}")
        self._write_json(
            200,
            {
                "kind": "magnet",
                "source": f"magnet:?xt=urn:btih:{info_hash}&dn={display_name}",
            },
        )

    def _cover(self, item_id: str) -> None:
        if not item_id or len(item_id) > 256:
            self._write_json(400, {"error": "invalid_id"})
            return
        self._write_bytes(200, "image/png", _cover_png(item_id))

    def _cover_url(self, item_id: str) -> str:
        authority = self.headers.get("Host")
        if not authority:
            host, port = self.server.server_address[:2]
            authority = f"{host}:{port}"
        return f"http://{authority}/covers/{item_id}.png"

    def _write_json(self, status: int, value: object) -> None:
        body = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _write_bytes(self, status: int, content_type: str, body: bytes) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "public, max-age=3600")
        self.end_headers()
        self.wfile.write(body)


def _bounded_int(value: str, minimum: int, maximum: int, fallback: int) -> int:
    try:
        return min(max(int(value), minimum), maximum)
    except ValueError:
        return fallback


def _cover_png(seed: str) -> bytes:
    """Build a small deterministic PNG so the demo has no external image dependency."""
    width, height = 96, 128
    digest = hashlib.sha256(f"{seed}:cover".encode("utf-8")).digest()
    primary = tuple(48 + component % 144 for component in digest[:3])
    accent = tuple(72 + component % 168 for component in digest[3:6])
    pixels = bytearray()
    for y in range(height):
        pixels.append(0)
        for x in range(width):
            diagonal = ((x + y + digest[6]) // 14) % 2
            source = accent if diagonal else primary
            shade = 18 * y // height
            pixels.extend(min(255, component + shade) for component in source)
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    png = b"\x89PNG\r\n\x1a\n" + _png_chunk(b"IHDR", header)
    png += _png_chunk(b"IDAT", zlib.compress(bytes(pixels), level=9))
    png += _png_chunk(b"IEND", b"")
    return png


def _png_chunk(kind: bytes, data: bytes) -> bytes:
    payload = kind + data
    return struct.pack(">I", len(data)) + payload + struct.pack(">I", binascii.crc32(payload) & 0xFFFFFFFF)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", default=8765, type=int)
    arguments = parser.parse_args()
    server = ThreadingHTTPServer((arguments.host, arguments.port), MockSearchHandler)
    host, port = server.server_address[:2]
    print(f"Listening on http://{host}:{port}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
