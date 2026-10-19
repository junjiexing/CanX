# aria2-to-mpv Stream Gateway spike

This NativeAOT spike starts a sequential libaria2 download and exposes it as an HTTP/1.1 byte-range resource:

- ranges already available in the partial file are streamed while aria2 continues writing;
- a seek into a missing range is proxied to the origin when it supports `Range`;
- libmpv receives only the loopback URL, not the origin URL;
- the download continues to a byte-identical persisted file.

Run the complete local test from a graphical Linux x64 session:

```sh
scripts/verify-stream-gateway.sh
```

The repeatable matrix uses throttled local origins and verifies:

- MP4 with a tail index, including byte-exact middle and suffix ranges;
- fast-start MP4 progressive rendering;
- Matroska progressive rendering and MIME propagation;
- an origin without Range support, which must produce an explicit `503` for unavailable random access;
- one forced mid-response disconnect, after which aria2 must resume and persist a byte-identical file.

Every case reports `CANX_STREAM_CASE_OK`; the complete run ends with `CANX_STREAM_GATEWAY_MATRIX_OK`.
