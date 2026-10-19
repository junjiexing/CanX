#!/usr/bin/env python3
import argparse
import mimetypes
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlsplit


class RangeHandler(BaseHTTPRequestHandler):
    server_version = "CanXRangeTest/1"

    def do_HEAD(self):
        self._serve(False)

    def do_GET(self):
        self._serve(True)

    def log_message(self, format, *args):
        return

    def _serve(self, include_body):
        failure_attempt = self.server.claim_failure()
        if failure_attempt:
            print(
                f"CANX_SOURCE_REJECTED attempt={failure_attempt} status=503",
                flush=True,
            )
            self.send_response(503)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return

        relative = unquote(urlsplit(self.path).path).lstrip("/")
        root = self.server.root
        target = (root / relative).resolve()
        if root not in target.parents or not target.is_file():
            self.send_error(404)
            return

        total = target.stat().st_size
        start = 0
        end = total - 1
        partial = False
        value = self.headers.get("Range") if self.server.enable_ranges else None
        if value:
            if not value.startswith("bytes=") or "," in value:
                self.send_error(416)
                return
            first, last = value[6:].split("-", 1)
            if first:
                start = int(first)
                end = min(int(last), end) if last else end
            else:
                start = max(0, total - int(last))
            if start >= total or end < start:
                self.send_error(416)
                return
            partial = True

        self.send_response(206 if partial else 200)
        if self.server.enable_ranges:
            self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Type", mimetypes.guess_type(target.name)[0] or "application/octet-stream")
        self.send_header("Content-Length", str(end - start + 1))
        if partial:
            self.send_header("Content-Range", f"bytes {start}-{end}/{total}")
        self.end_headers()

        if not include_body:
            return

        try:
            with target.open("rb") as source:
                source.seek(start)
                remaining = end - start + 1
                sent = 0
                disconnect_attempt = self.server.claim_disconnect()
                while remaining:
                    chunk = source.read(min(self.server.chunk_size, remaining))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    self.wfile.flush()
                    remaining -= len(chunk)
                    sent += len(chunk)
                    if disconnect_attempt and sent >= self.server.disconnect_after_bytes:
                        print(
                            f"CANX_SOURCE_DISCONNECTED path={relative} bytes={sent} attempt={disconnect_attempt}",
                            flush=True,
                        )
                        try:
                            self.connection.shutdown(socket.SHUT_RDWR)
                        except OSError:
                            pass
                        self.connection.close()
                        return
                    if self.server.delay_seconds:
                        time.sleep(self.server.delay_seconds)
        except (BrokenPipeError, ConnectionResetError):
            pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--chunk-size", type=int, default=16384)
    parser.add_argument("--delay-ms", type=float, default=10)
    parser.add_argument("--disable-ranges", action="store_true")
    parser.add_argument("--disconnect-once-after-bytes", type=int, default=0)
    parser.add_argument("--disconnect-first-requests", type=int, default=0)
    parser.add_argument("--fail-first-requests", type=int, default=0)
    parser.add_argument("--ready-file", type=Path)
    args = parser.parse_args()

    server = ThreadingHTTPServer(("127.0.0.1", args.port), RangeHandler)
    server.root = args.root.resolve()
    server.chunk_size = args.chunk_size
    server.delay_seconds = args.delay_ms / 1000
    server.enable_ranges = not args.disable_ranges
    server.disconnect_after_bytes = args.disconnect_once_after_bytes
    server.disconnect_count = 0
    server.disconnect_limit = max(
        args.disconnect_first_requests,
        1 if args.disconnect_once_after_bytes > 0 else 0,
    )
    server.disconnect_lock = threading.Lock()
    server.failure_count = 0
    server.failure_limit = args.fail_first_requests
    server.failure_lock = threading.Lock()

    def claim_disconnect():
        with server.disconnect_lock:
            if server.disconnect_count >= server.disconnect_limit:
                return 0
            server.disconnect_count += 1
            return server.disconnect_count

    server.claim_disconnect = claim_disconnect
    def claim_failure():
        with server.failure_lock:
            if server.failure_count >= server.failure_limit:
                return 0
            server.failure_count += 1
            return server.failure_count

    server.claim_failure = claim_failure
    if args.ready_file:
        args.ready_file.parent.mkdir(parents=True, exist_ok=True)
        args.ready_file.write_text(str(args.port), encoding="ascii")
    server.serve_forever()


if __name__ == "__main__":
    main()
