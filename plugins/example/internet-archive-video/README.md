# Internet Archive Video example

This CanX plugin publishes `org.canx.media.search@1.0` and `org.canx.media.resolve@1.0` services. It queries Internet Archive metadata and returns up to four MP4 sources, largest first. The test suite supplies a mock HTTP handler and never accesses the network.

```sh
npm install
npm test
npm run lint
npm run pack:canx
```

Only use media that you are entitled to access. The example does not bypass access controls or DRM.
