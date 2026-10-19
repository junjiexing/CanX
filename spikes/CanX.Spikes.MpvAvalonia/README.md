# libmpv + Avalonia Render API spike

This spike embeds libmpv 0.41 through `mpv_render_context` in Avalonia's `OpenGlControlBase`. It deliberately overlays a normal Avalonia control on the video surface to verify that the design avoids native-child-window airspace issues.

The native bindings use source-generated P/Invoke and unmanaged entry points, so the same code is exercised in the NativeAOT executable. The native package preparation script is local to Ubuntu 26.04 and is not a production redistribution recipe.

Run from a graphical Linux x64 session:

```sh
scripts/verify-mpv-avalonia.sh
```

Success is reported as `CANX_MPV_RENDER_OK` after libmpv has requested and rendered multiple frames into Avalonia's framebuffer.
