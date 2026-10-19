#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later

"""Run an authenticated local FTP or SFTP server for integration tests."""

from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path, PurePosixPath
import socket
import threading
import time


def run_ftp(
    root: Path,
    bind: str,
    port: int,
    username: str,
    password: str,
    ready_file: Path,
    reject_auth_count: int,
    hide_rest_feature: bool,
) -> None:
    from pyftpdlib.authorizers import AuthenticationFailed, DummyAuthorizer
    from pyftpdlib.handlers import FTPHandler
    from pyftpdlib.servers import FTPServer

    class FixtureAuthorizer(DummyAuthorizer):
        remaining_rejections = reject_auth_count

        def validate_authentication(self, candidate: str, supplied: str, handler) -> None:
            if candidate == username and self.remaining_rejections > 0:
                self.remaining_rejections -= 1
                raise AuthenticationFailed("intentional fixture authentication failure")
            super().validate_authentication(candidate, supplied, handler)

    class FixtureFtpHandler(FTPHandler):
        def ftp_FEAT(self, line: str) -> None:
            if not hide_rest_feature:
                super().ftp_FEAT(line)
                return

            features = {"TVFS"}
            if self.encoding.lower() in ("utf8", "utf-8"):
                features.add("UTF8")
            features.update(
                feature
                for feature in ("EPRT", "EPSV", "MDTM", "MFMT", "SIZE")
                if feature in self.proto_cmds
            )
            features.update(self._extra_feats)
            if "MLST" in self.proto_cmds or "MLSD" in self.proto_cmds:
                facts = "".join(
                    fact + ("*;" if fact in self._current_facts else ";")
                    for fact in self._available_facts
                )
                features.add("MLST " + facts)
            self.push("211-Features supported:\r\n")
            self.push("".join(f" {feature}\r\n" for feature in sorted(features)))
            self.respond("211 End FEAT.")

    authorizer = FixtureAuthorizer()
    authorizer.add_user(username, password, str(root), perm="elr")
    handler = FixtureFtpHandler
    handler.authorizer = authorizer
    handler.banner = "CanX fixture FTP ready"
    server = FTPServer((bind, port), handler)
    ready_file.write_text("ready\n", encoding="utf-8")
    server.serve_forever(timeout=0.25, blocking=True, handle_exit=False)


def run_sftp(root: Path, bind: str, port: int, username: str, password: str, ready_file: Path) -> None:
    import paramiko

    host_key = paramiko.RSAKey.generate(2048)

    class PasswordServer(paramiko.ServerInterface):
        def check_auth_password(self, candidate: str, supplied: str) -> int:
            if candidate == username and supplied == password:
                return paramiko.AUTH_SUCCESSFUL
            return paramiko.AUTH_FAILED

        def get_allowed_auths(self, _username: str) -> str:
            return "password"

        def check_channel_request(self, kind: str, _channel_id: int) -> int:
            return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    class RootedSftp(paramiko.SFTPServerInterface):
        class ReadHandle(paramiko.SFTPHandle):
            def stat(self):
                try:
                    return paramiko.SFTPAttributes.from_stat(os.fstat(self.readfile.fileno()))
                except OSError as error:
                    return paramiko.SFTPServer.convert_errno(error.errno)

        def _local_path(self, requested: str) -> Path:
            relative = PurePosixPath(requested).relative_to("/") if requested.startswith("/") else PurePosixPath(requested)
            local = (root / Path(*relative.parts)).resolve()
            if local != root and root not in local.parents:
                raise PermissionError("path escapes the fixture root")
            return local

        def stat(self, path: str):
            try:
                return paramiko.SFTPAttributes.from_stat(self._local_path(path).stat())
            except OSError as error:
                return paramiko.SFTPServer.convert_errno(error.errno)

        def lstat(self, path: str):
            try:
                return paramiko.SFTPAttributes.from_stat(self._local_path(path).lstat())
            except OSError as error:
                return paramiko.SFTPServer.convert_errno(error.errno)

        def open(self, path: str, flags: int, attr):
            del attr
            if flags & (os.O_WRONLY | os.O_RDWR | os.O_APPEND | os.O_CREAT | os.O_TRUNC):
                return paramiko.SFTP_PERMISSION_DENIED
            try:
                handle = self.ReadHandle(flags)
                handle.readfile = self._local_path(path).open("rb")
                return handle
            except OSError as error:
                return paramiko.SFTPServer.convert_errno(error.errno)

    def serve(client: socket.socket) -> None:
        transport = paramiko.Transport(client)
        try:
            transport.add_server_key(host_key)
            transport.set_subsystem_handler("sftp", paramiko.SFTPServer, RootedSftp)
            transport.start_server(server=PasswordServer())
            while transport.is_active():
                time.sleep(0.05)
        finally:
            transport.close()
            client.close()

    with socket.socket() as listener:
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind((bind, port))
        listener.listen(8)
        fingerprint = hashlib.sha1(host_key.asbytes()).hexdigest()
        ready_file.write_text(f"sha-1={fingerprint}\n", encoding="utf-8")
        while True:
            client, _address = listener.accept()
            threading.Thread(target=serve, args=(client,), daemon=True).start()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("protocol", choices=("ftp", "sftp"))
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--username", required=True)
    parser.add_argument("--password", required=True)
    parser.add_argument("--ready-file", type=Path, required=True)
    parser.add_argument("--reject-auth-count", type=int, default=0)
    parser.add_argument("--hide-rest-feature", action="store_true")
    args = parser.parse_args()
    root = args.root.resolve(strict=True)
    args.ready_file.parent.mkdir(parents=True, exist_ok=True)
    if args.protocol == "ftp":
        run_ftp(
            root,
            args.bind,
            args.port,
            args.username,
            args.password,
            args.ready_file,
            args.reject_auth_count,
            args.hide_rest_feature,
        )
    else:
        run_sftp(root, args.bind, args.port, args.username, args.password, args.ready_file)


if __name__ == "__main__":
    main()
