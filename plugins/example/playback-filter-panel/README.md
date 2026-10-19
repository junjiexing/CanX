# Playback Filter Panel example

This example contributes a playback sidebar panel, a plugin setting and a command-palette action. Every interaction maps to the bounded `playback.filter.video.set` or `playback.filter.clear` API; arbitrary mpv commands are never accepted. The selected preset is saved in the plugin's isolated storage.

```sh
npm install
npm test
npm run lint
npm run pack:canx
```
