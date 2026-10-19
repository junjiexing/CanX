#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later

"""Minimal HTTP tracker that advertises one local integration-test seeder."""

from __future__ import annotations

import argparse
import ipaddress
import struct
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def bencoded_tracker_response(peer_ip: str, peer_port: int) -> bytes:
    compact_peer = ipaddress.ip_address(peer_ip).packed + struct.pack("!H", peer_port)
    return b"d8:intervali1e5:peers6:" + compact_peer + b"e"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--peer-ip", default="127.0.0.1")
    parser.add_argument("--peer-port", type=int, required=True)
    args = parser.parse_args()
    response = bencoded_tracker_response(args.peer_ip, args.peer_port)

    class TrackerHandler(BaseHTTPRequestHandler):
        def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
            self.send_response(200)
            self.send_header("Content-Type", "text/plain")
            self.send_header("Content-Length", str(len(response)))
            self.end_headers()
            self.wfile.write(response)

        def log_message(self, _format: str, *_args: object) -> None:
            return

    ThreadingHTTPServer((args.bind, args.port), TrackerHandler).serve_forever()


if __name__ == "__main__":
    main()
