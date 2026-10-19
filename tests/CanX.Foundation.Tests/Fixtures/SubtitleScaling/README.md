These fixtures exercise the native `--subtitle-slider-smoke <video> <subtitle>`
check. Use a video at least 30 seconds long. Each subtitle lasts 90 seconds:

- `plain.srt`: the default text style.
- `styled.ass`: a script-defined font size and color.
- `inline.ass`: a positioned line with inline font size and color overrides.

The check drives the actual playback window sliders without releasing them,
checks synchronization, then pauses and measures subtitle pixels in real mpv
screenshots at 75%, 100%, and 200%. The ASS checks also reproduce the original
bug: changing `sub-font-size` alone leaves the rendered size unchanged.

Set `CANX_DATA_DIRECTORY` to an isolated test directory before launching. Images
are written to its `subtitle-render-smoke` subdirectory. Success is reported as
`CANX_SUBTITLE_SLIDERS_OK ... rendered=true`.
