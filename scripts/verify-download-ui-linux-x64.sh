#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app_output="${CANX_APP_OUTPUT:-$repository_root/artifacts/verify/linux-x64}"
screenshot_output="${CANX_UI_SCREENSHOT_OUTPUT:-$repository_root/artifacts/ui/download-center/linux-x64}"

if [[ "${CANX_UI_DBUS_SESSION:-0}" != "1" ]]; then
  exec dbus-run-session -- env CANX_UI_DBUS_SESSION=1 "$0" "$@"
fi

if [[ "${1:-}" == "--skip-publish" ]]; then
  shift
else
  CANX_INCLUDE_SMOKE_TESTS=true CANX_APP_OUTPUT="$app_output" \
    "$repository_root/scripts/publish-static-app-linux-x64.sh" "$@"
fi

if [[ $# -ne 0 ]]; then
  printf 'usage: %s [--skip-publish]\n' "$0" >&2
  exit 2
fi

for tool in Xvfb xdotool import identify compare ffmpeg /usr/bin/python3; do
  command -v "$tool" >/dev/null 2>&1 || {
    printf 'missing UI test tool: %s\n' "$tool" >&2
    exit 2
  }
done

if [[ ! -x "$app_output/CanX.App" ]]; then
  printf 'CanX.App was not found at %s\n' "$app_output" >&2
  exit 2
fi

work_dir="$(mktemp -d /tmp/canx-download-ui.XXXXXX)"
app_pid=""
xvfb_pid=""
cleanup() {
  if [[ -n "$app_pid" ]]; then
    kill "$app_pid" 2>/dev/null || true
    wait "$app_pid" 2>/dev/null || true
  fi
  if [[ -n "$xvfb_pid" ]]; then
    kill "$xvfb_pid" 2>/dev/null || true
    wait "$xvfb_pid" 2>/dev/null || true
  fi
  if [[ "${CANX_KEEP_TEST_WORK:-0}" == "1" ]]; then
    printf 'CANX_DOWNLOAD_UI_WORK=%s\n' "$work_dir"
  else
    rm -rf -- "$work_dir"
  fi
}
trap cleanup EXIT

media_file="$work_dir/player-fixture.mkv"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'testsrc2=size=640x360:rate=30' \
  -t 8 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -an "$media_file"

display_file="$work_dir/display"
Xvfb -displayfd 3 -screen 0 5000x3200x24 -nolisten tcp -ac \
  >"$work_dir/xvfb.log" 2>&1 3>"$display_file" &
xvfb_pid="$!"
for _ in {1..50}; do
  [[ -s "$display_file" ]] && break
  kill -0 "$xvfb_pid" 2>/dev/null || {
    sed -n '1,160p' "$work_dir/xvfb.log" >&2
    exit 1
  }
  sleep 0.1
done
[[ -s "$display_file" ]]
display=":$(tr -d '[:space:]' <"$display_file")"
DISPLAY="$display" xdpyinfo >/dev/null
mkdir -p "$screenshot_output"

stop_app() {
  if [[ -n "$app_pid" ]]; then
    kill "$app_pid" 2>/dev/null || true
    wait "$app_pid" 2>/dev/null || true
    app_pid=""
  fi
}

window_id=""
start_app() {
  local scale="$1"
  local data_dir="$work_dir/data-$scale"
  mkdir -p "$data_dir"
  env -u WAYLAND_DISPLAY \
    DISPLAY="$display" \
    XDG_SESSION_TYPE=x11 \
    CANX_FORCE_X11=1 \
    AVALONIA_GLOBAL_SCALE_FACTOR="$scale" \
    CANX_DATA_DIRECTORY="$data_dir" \
    "$app_output/CanX.App" >"$work_dir/app-$scale.log" 2>&1 &
  app_pid="$!"
  window_id=""
  for _ in {1..100}; do
    window_id="$(DISPLAY="$display" xdotool search --name '^CanX$' 2>/dev/null | head -1 || true)"
    [[ -n "$window_id" ]] && break
    if ! kill -0 "$app_pid" 2>/dev/null; then
      sed -n '1,200p' "$work_dir/app-$scale.log" >&2
      printf 'CanX.App exited before its X11 window was mapped\n' >&2
      exit 1
    fi
    sleep 0.1
  done
  if [[ -z "$window_id" ]]; then
    sed -n '1,200p' "$work_dir/app-$scale.log" >&2
    printf 'CanX.App did not map an X11 window\n' >&2
    exit 1
  fi
  sleep 0.5
}

capture_and_check() {
  local scale="$1"
  local name="$2"
  local screenshot="$screenshot_output/$name.png"
  local expected_width=$((1320 * scale))
  local expected_height=$((820 * scale))
  local geometry
  local width
  local height
  local colors

  geometry="$(DISPLAY="$display" xwininfo -id "$window_id")"
  width="$(sed -n 's/^  Width: //p' <<<"$geometry")"
  height="$(sed -n 's/^  Height: //p' <<<"$geometry")"
  if [[ "$width" -ne "$expected_width" || "$height" -ne "$expected_height" ]]; then
    printf 'unexpected window size at %s%%: %sx%s, expected %sx%s\n' \
      "$((scale * 100))" "$width" "$height" "$expected_width" "$expected_height" >&2
    exit 1
  fi

  DISPLAY="$display" import -window "$window_id" "$screenshot"
  read -r width height colors < <(identify -format '%w %h %k\n' "$screenshot")
  if [[ "$width" -ne "$expected_width" || "$height" -ne "$expected_height" || "$colors" -lt 32 ]]; then
    printf 'invalid screenshot %s: %sx%s, colors=%s\n' "$screenshot" "$width" "$height" "$colors" >&2
    exit 1
  fi
  printf 'CANX_UI_SCREENSHOT scale=%s size=%sx%s colors=%s file=%s\n' \
    "$((scale * 100))" "$width" "$height" "$colors" "$screenshot"
}

capture_player_and_check() {
  local screenshot="$screenshot_output/scale-100-player.png"
  local geometry
  local width
  local height
  local colors

  geometry="$(DISPLAY="$display" xwininfo -id "$window_id")"
  width="$(sed -n 's/^  Width: //p' <<<"$geometry")"
  height="$(sed -n 's/^  Height: //p' <<<"$geometry")"
  if [[ "$width" -ne 1200 || "$height" -ne 760 ]]; then
    printf 'unexpected player window size: %sx%s, expected 1200x760\n' "$width" "$height" >&2
    exit 1
  fi

  DISPLAY="$display" import -window "$window_id" "$screenshot"
  read -r width height colors < <(identify -format '%w %h %k\n' "$screenshot")
  if [[ "$colors" -lt 32 ]]; then
    printf 'invalid player screenshot %s: %sx%s, colors=%s\n' "$screenshot" "$width" "$height" "$colors" >&2
    exit 1
  fi
  printf 'CANX_UI_SCREENSHOT scale=100 size=%sx%s colors=%s file=%s\n' \
    "$width" "$height" "$colors" "$screenshot"
}

start_app 1
capture_and_check 1 scale-100

"$repository_root/scripts/support/atspi_probe.py" \
  --require "CanX" \
  --require "下载引擎已就绪" \
  --require-focusable "下载中任务筛选" \
  --require-focusable "已完成任务筛选" \
  --require-focusable "添加下载任务" \
  --require-focusable "下载任务列表" \
  --require-focusable "设置"

DISPLAY="$display" xdotool windowfocus "$window_id"
DISPLAY="$display" xdotool mousemove --window "$window_id" 45 795
DISPLAY="$display" xdotool click 1
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" \
  --require "设置内容" \
  --require-focusable "应用主题" \
  --require-focusable "启用日志记录" \
  --require-focusable "默认下载目录"
capture_and_check 1 scale-100-settings
DISPLAY="$display" xdotool key --clearmodifiers F6
"$repository_root/scripts/support/atspi_probe.py" --focused "应用主题"
DISPLAY="$display" xdotool mousemove --window "$window_id" 45 112
DISPLAY="$display" xdotool click 1
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" --require-focusable "下载任务列表"

# Exercise the primary download path and its keyboard navigation before a
# second top-level window is opened. A window-manager-free Xvfb server does not
# restore X keyboard focus to an owner after its child window closes.
"$repository_root/scripts/support/atspi_probe.py" --invoke "添加下载任务"
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" --focused "下载地址或种子文件"
if DISPLAY="$display" xdotool search --all --maxdepth 1 --pid "$app_pid" --onlyvisible --name '^添加下载任务$' >/dev/null 2>&1; then
  printf 'Add download UI created an unexpected native window\n' >&2
  exit 1
fi
DISPLAY="$display" import -window "$window_id" "$screenshot_output/scale-100-add-source-focus.png"
DISPLAY="$display" xdotool key --clearmodifiers Tab
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" --focused "浏览种子文件"
DISPLAY="$display" import -window "$window_id" "$screenshot_output/scale-100-add-browse-focus.png"
difference="$(compare -metric AE "$screenshot_output/scale-100-add-source-focus.png" \
  "$screenshot_output/scale-100-add-browse-focus.png" null: 2>&1 || true)"
difference="${difference%% *}"
if [[ ! "$difference" =~ ^[0-9]+$ ]] || [[ "$difference" -eq 0 ]]; then
  printf 'keyboard focus did not produce a visible screenshot difference: %s\n' "$difference" >&2
  exit 1
fi

DISPLAY="$display" xdotool key --clearmodifiers shift+Tab
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" --focused "下载地址或种子文件"
DISPLAY="$display" xdotool key --clearmodifiers Escape
sleep 0.2
DISPLAY="$display" xdotool key --clearmodifiers ctrl+shift+l
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" --focused "下载任务列表"
DISPLAY="$display" xdotool key --clearmodifiers F6
"$repository_root/scripts/support/atspi_probe.py" --focused "设置"
DISPLAY="$display" xdotool key --clearmodifiers shift+F6
"$repository_root/scripts/support/atspi_probe.py" --focused "下载任务列表"

# Media browsing and source management stay in the main window.
main_window_id="$window_id"
DISPLAY="$display" xdotool windowfocus "$main_window_id"
sleep 0.2
DISPLAY="$display" xdotool mousemove --window "$main_window_id" 45 151
DISPLAY="$display" xdotool click 1
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" \
  --require "播放列表" \
  --require "最近播放列表" \
  --require-focusable "当前来源：本地媒体" \
  --require-focusable "添加其他媒体源" \
  --require-focusable "媒体文件或直连地址" \
  --require-focusable "选择本地媒体文件" \
  --require-focusable "打开本地媒体文件夹" \
  --require-focusable "添加文件到播放列表"
capture_and_check 1 scale-100-media

DISPLAY="$display" xdotool mousemove --window "$main_window_id" 300 780
DISPLAY="$display" xdotool click 1
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" \
  --require "此区域暂留，后续将在这里添加网络媒体源和插件提供的媒体源。"
DISPLAY="$display" xdotool mousemove --window "$main_window_id" 300 100
DISPLAY="$display" xdotool click 1
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" --focused "媒体文件或直连地址"
DISPLAY="$display" xdotool type --delay 1 -- "$media_file"
sleep 0.2
DISPLAY="$display" xdotool mousemove --window "$main_window_id" 1035 200
DISPLAY="$display" xdotool click 1

# Opening a selected source creates a top-level window containing only the
# video surface and playback controls.
player_window_id=""
for _ in {1..100}; do
  # xdotool's regex matcher is unreliable with CJK window names on some CI images.
  player_window_id="$(DISPLAY="$display" xdotool search --name '^CanX ' 2>/dev/null | head -1 || true)"
  [[ -n "$player_window_id" ]] && break
  sleep 0.1
done
if [[ -z "$player_window_id" || "$player_window_id" == "$main_window_id" ]]; then
  printf 'standalone player window was not created\n' >&2
  exit 1
fi
window_id="$player_window_id"
DISPLAY="$display" xdotool windowfocus "$player_window_id"
sleep 0.2
capture_player_and_check
"$repository_root/scripts/support/atspi_probe.py" \
  --require "CanX 播放器" \
  --require "视频画面" \
  --require-focusable "播放或暂停" \
  --require-focusable "前进五秒" \
  --require-focusable "音量" \
  --require-focusable "播放器播放列表" \
  --require-focusable "轨道与字幕" \
  --require-focusable "章节与书签"
# Open the in-window playlist panel using keyboard region navigation.
DISPLAY="$display" xdotool key --clearmodifiers F6 F6 F6 space
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" \
  --require "播放器播放列表面板" \
  --require "播放器播放列表" \
  --require-focusable "关闭播放器播放列表"
DISPLAY="$display" import -window "$player_window_id" \
  "$screenshot_output/scale-100-player-playlist.png"
DISPLAY="$display" xdotool key --clearmodifiers Escape
sleep 0.2
# Open the in-window panel and verify its adaptive track/subtitle controls.
DISPLAY="$display" xdotool key --clearmodifiers F6 space
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" \
  --require "轨道与字幕面板" \
  --require "视频轨道" \
  --require "音频轨道" \
  --require "字幕轨道" \
  --require "加载外部字幕" \
  --require "显示字幕" \
  --require "字幕延迟" \
  --require "字幕大小" \
  --require "字幕位置"
DISPLAY="$display" import -window "$player_window_id" \
  "$screenshot_output/scale-100-player-tracks.png"
DISPLAY="$display" xdotool key --clearmodifiers Escape
sleep 0.2
# Open the chapter/bookmark panel and verify both navigation regions.
DISPLAY="$display" xdotool key --clearmodifiers F6 space
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" \
  --require "章节与书签面板" \
  --require "章节列表" \
  --require "书签列表" \
  --require "书签名称" \
  --require "在当前位置添加书签" \
  --require-focusable "关闭章节与书签"
DISPLAY="$display" import -window "$player_window_id" \
  "$screenshot_output/scale-100-player-chapters.png"
DISPLAY="$display" xdotool key --clearmodifiers Escape
sleep 0.2
# Open the advanced playback panel and verify its adaptive controls.
DISPLAY="$display" xdotool key --clearmodifiers F6 space
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" \
  --require "高级播放面板" \
  --require "后退一帧" \
  --require "设置A循环点" \
  --require "截取含字幕画面" \
  --require "音频输出设备" \
  --require "视频滤镜表达式" \
  --require "音频滤镜表达式" \
  --require "用户Shader列表" \
  --require-focusable "关闭高级播放面板"
DISPLAY="$display" import -window "$player_window_id" \
  "$screenshot_output/scale-100-player-advanced.png"
DISPLAY="$display" xdotool key --clearmodifiers Escape
sleep 0.2
# Open the decoder/HDR/runtime diagnostics panel.
DISPLAY="$display" xdotool key --clearmodifiers F6 space
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" \
  --require "播放诊断面板" \
  --require "解码与渲染路径" \
  --require "色彩元数据" \
  --require "性能统计" \
  --require "mpv运行时属性名" \
  --require "mpv运行时命令名" \
  --require-focusable "关闭播放诊断面板"
DISPLAY="$display" import -window "$player_window_id" \
  "$screenshot_output/scale-100-player-diagnostics.png"
DISPLAY="$display" xdotool key --clearmodifiers Escape
sleep 0.2
# Moving inside the player keeps the overlay visible; inactivity hides it.
DISPLAY="$display" xdotool mousemove --window "$player_window_id" 600 300
sleep 3.3
DISPLAY="$display" import -window "$player_window_id" "$screenshot_output/scale-100-player-idle.png"
overlay_difference="$(compare -metric AE "$screenshot_output/scale-100-player.png" \
  "$screenshot_output/scale-100-player-idle.png" null: 2>&1 || true)"
overlay_difference="${overlay_difference%% *}"
if [[ ! "$overlay_difference" =~ ^[0-9]+$ ]] || [[ "$overlay_difference" -eq 0 ]]; then
  printf 'playback overlay did not hide after pointer inactivity: %s\n' "$overlay_difference" >&2
  exit 1
fi

# Moving again reveals the overlay. Pointer-exit delivery is not available in
# this window-manager-free Xvfb setup; the application handles PointerExited
# on its playback root and video surface on desktop backends.
DISPLAY="$display" xdotool mousemove --window "$player_window_id" 620 300
sleep 0.2
"$repository_root/scripts/support/atspi_probe.py" --require-focusable "播放或暂停"

# OpenGlControlBase is not consistently the pointer hit-test target. Exercise
# each custom gesture through the parent playback input layer so regressions in
# routed-event placement are caught even on this software-only Xvfb run.
gesture_index=0
for gesture in '600 300 800 300' '250 400 250 220' '950 400 950 220'; do
  gesture_index=$((gesture_index + 1))
  read -r start_x start_y end_x end_y <<<"$gesture"
  DISPLAY="$display" xdotool mousemove --window "$player_window_id" "$start_x" "$start_y"
  sleep 0.1
  DISPLAY="$display" import -window "$player_window_id" "$work_dir/gesture-$gesture_index-before.png"
  DISPLAY="$display" xdotool mousedown 1
  # Send a real drag trajectory instead of one synthetic jump. Avalonia may
  # coalesce an instantaneous XTest move before the dispatcher renders the
  # feedback layer, which would make this smoke test intermittently miss it.
  for step in 1 2 3 4; do
    next_x=$((start_x + ((end_x - start_x) * step / 4)))
    next_y=$((start_y + ((end_y - start_y) * step / 4)))
    DISPLAY="$display" xdotool mousemove --sync --window "$player_window_id" "$next_x" "$next_y"
    sleep 0.03
  done
  sleep 0.2
  DISPLAY="$display" import -window "$player_window_id" \
    "$screenshot_output/scale-100-player-gesture-$gesture_index.png"
  gesture_difference="$(compare -metric AE "$work_dir/gesture-$gesture_index-before.png" \
    "$screenshot_output/scale-100-player-gesture-$gesture_index.png" null: 2>&1 || true)"
  gesture_difference="${gesture_difference%% *}"
  if [[ ! "$gesture_difference" =~ ^[0-9]+$ ]] || [[ "$gesture_difference" -eq 0 ]]; then
    printf 'playback gesture %s did not produce visible feedback: %s\n' \
      "$gesture_index" "$gesture_difference" >&2
    exit 1
  fi
  DISPLAY="$display" xdotool mouseup 1
  sleep 0.7
done
# This UI test intentionally uses a plain Xvfb server, which does not expose
# the OpenGL context required by OpenGlControlBase. Actual mpv decoding and
# frame rendering are covered by verify-playback-linux-x64.sh on GL-capable
# Wayland/X11 backends.
"$repository_root/scripts/support/atspi_probe.py" --invoke "关闭播放器窗口"
sleep 0.4
window_id="$main_window_id"
stop_app

for scale in 2 3; do
  start_app "$scale"
  capture_and_check "$scale" "scale-$((scale * 100))"
  stop_app
done

sha256sum "$screenshot_output"/*.png >"$screenshot_output/SHA256SUMS"
printf 'CANX_DOWNLOAD_UI_OK backend=X11 scales=100,200,300 accessibility=at-spi keyboard=true playback-overlay=true playback-gestures=true playback-tracks=true playback-playlist=true playback-chapters=true playback-advanced=true playback-diagnostics=true screenshots=17 output=%s\n' \
  "$screenshot_output"
