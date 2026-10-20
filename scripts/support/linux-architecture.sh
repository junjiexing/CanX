case "$(uname -m)" in
  x86_64) linux_host_architecture=x64 ;;
  aarch64|arm64) linux_host_architecture=arm64 ;;
  *) echo "unsupported Linux build architecture: $(uname -m)" >&2; exit 2 ;;
esac
linux_architecture="${CANX_LINUX_ARCHITECTURE:-$linux_host_architecture}"
case "$linux_architecture" in
  x64) linux_multiarch=x86_64-linux-gnu ;;
  arm64) linux_multiarch=aarch64-linux-gnu ;;
  *) echo "CANX_LINUX_ARCHITECTURE must be x64 or arm64" >&2; exit 2 ;;
esac
if [[ "$linux_architecture" != "$linux_host_architecture" ]]; then
  echo "Linux native builds require a $linux_architecture host; found $linux_host_architecture" >&2
  exit 2
fi
linux_runtime="linux-$linux_architecture"
export CANX_LINUX_ARCHITECTURE="$linux_architecture"