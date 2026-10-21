#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repository_root/scripts/support/linux-architecture.sh"
image="${CANX_UBUNTU_2204_IMAGE:-canx-$linux_runtime-ubuntu22.04}"
docker_architecture=amd64
[[ "$linux_architecture" != arm64 ]] || docker_architecture=arm64
snapshot="${CANX_UBUNTU_SNAPSHOT-20250901T000000Z}"
if [[ -n "$snapshot" && ! "$snapshot" =~ ^[0-9]{8}T[0-9]{6}Z$ ]]; then
  echo 'CANX_UBUNTU_SNAPSHOT must be an Ubuntu snapshot timestamp or empty for current Jammy repositories.' >&2
  exit 2
fi

if command -v docker >/dev/null 2>&1; then
  container_engine=docker
elif command -v podman >/dev/null 2>&1; then
  container_engine=podman
else
  echo 'Docker or Podman is required for the Ubuntu 22.04 release build.' >&2
  exit 2
fi

"$container_engine" build \
  --platform "linux/$docker_architecture" \
  --build-arg "UBUNTU_SNAPSHOT=$snapshot" \
  --file "$repository_root/native/linux-x64-ubuntu22.04.Dockerfile" \
  --tag "$image" "$repository_root"

if [[ $# -eq 0 ]]; then
  command_args=(scripts/publish-static-app-linux-x64.sh)
else
  command_args=("$@")
fi

"$container_engine" run --rm \
  --user "$(id -u):$(id -g)" \
  --env HOME=/tmp/canx-home \
  --env DOTNET_CLI_HOME=/tmp/canx-home \
  --env NUGET_PACKAGES=/workspace/.cache/nuget/packages \
  --env "CANX_LINUX_ARCHITECTURE=$linux_architecture" \
  --env "CANX_STATIC_ROOT=/workspace/.cache/static-native-ubuntu22.04/$linux_runtime" \
  --volume "$repository_root:/workspace" \
  --workdir /workspace \
  "$image" \
  bash -c 'mkdir -p "$HOME"; git config --global --add safe.directory /workspace; exec "$@"' \
  bash "${command_args[@]}"
