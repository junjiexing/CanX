#!/usr/bin/python3
# SPDX-License-Identifier: GPL-3.0-or-later

"""Small AT-SPI assertion helper used by the Linux UI acceptance test."""

from __future__ import annotations

import argparse
import time

import gi

gi.require_version("Atspi", "2.0")
from gi.repository import Atspi  # noqa: E402


def read_node(node):
    try:
        name = node.get_name() or ""
        role = node.get_role_name() or "unknown"
        states = node.get_state_set()
        focused = states.contains(Atspi.StateType.FOCUSED)
        focusable = states.contains(Atspi.StateType.FOCUSABLE)
        children = [node.get_child_at_index(index) for index in range(node.get_child_count())]
        return name, role, focused, focusable, children
    except Exception:
        return "", "unavailable", False, False, []


def snapshot():
    result = []
    pending = [Atspi.get_desktop(0)]
    while pending:
        node = pending.pop()
        name, role, focused, focusable, children = read_node(node)
        result.append((name, role, focused, focusable))
        pending.extend(reversed(children))
    return result


def find_actionable_node(target_name):
    pending = [Atspi.get_desktop(0)]
    while pending:
        node = pending.pop()
        try:
            name = node.get_name() or ""
            if name == target_name and node.get_n_actions() > 0:
                return node
            pending.extend(
                node.get_child_at_index(index) for index in range(node.get_child_count())
            )
        except Exception:
            continue
    return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--require", action="append", default=[])
    parser.add_argument("--require-focusable", action="append", default=[])
    parser.add_argument("--focused")
    parser.add_argument("--invoke")
    parser.add_argument("--timeout", type=float, default=5.0)
    parser.add_argument("--dump", action="store_true")
    args = parser.parse_args()

    if args.invoke is not None:
        deadline = time.monotonic() + args.timeout
        target = None
        while time.monotonic() < deadline:
            target = find_actionable_node(args.invoke)
            if target is not None:
                break
            time.sleep(0.1)
        if target is None:
            raise SystemExit(f"missing actionable accessible control: {args.invoke}")
        if target.do_action(0) is False:
            raise SystemExit(f"failed to invoke accessible control: {args.invoke}")
        print(f"CANX_ATSPI_INVOKED name={args.invoke}")
        if not args.require and not args.require_focusable and args.focused is None and not args.dump:
            return

    deadline = time.monotonic() + args.timeout
    nodes = []
    while time.monotonic() < deadline:
        nodes = snapshot()
        names = {name for name, _, _, _ in nodes if name}
        focused_names = {name for name, _, focused, _ in nodes if focused and name}
        focusable_names = {name for name, _, _, focusable in nodes if focusable and name}
        if all(name in names for name in args.require) and all(
            name in focusable_names for name in args.require_focusable
        ) and (
            args.focused is None or args.focused in focused_names
        ):
            break
        time.sleep(0.1)

    names = {name for name, _, _, _ in nodes if name}
    focusable_names = {name for name, _, _, focusable in nodes if focusable and name}
    focused_names = {name for name, _, focused, _ in nodes if focused and name}
    missing = [name for name in args.require if name not in names]
    not_focusable = [name for name in args.require_focusable if name not in focusable_names]

    if args.dump:
        for name, role, focused, focusable in nodes:
            if name:
                print(
                    f"role={role} focused={str(focused).lower()} "
                    f"focusable={str(focusable).lower()} name={name}"
                )

    if missing:
        raise SystemExit("missing accessible names: " + ", ".join(missing))
    if not_focusable:
        raise SystemExit("missing focusable accessible controls: " + ", ".join(not_focusable))
    if args.focused is not None and args.focused not in focused_names:
        current = ", ".join(sorted(focused_names)) or "none"
        raise SystemExit(f"expected focus on {args.focused!r}; focused: {current}")

    required = len(args.require) + len(args.require_focusable)
    focused = next(iter(focused_names), "none")
    print(f"CANX_ATSPI_OK required={required} focused={focused}")


if __name__ == "__main__":
    main()
