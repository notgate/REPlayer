# README media

This directory is the stable home for README preview media. Keep product captures factual and use the native REPlayer monochrome UI without decorative mock-device frames.

## Brand assets

- `replayer-logo.svg` — canonical original REPlayer vector mark: an `R`/viewport silhouette with a play core;
- `replayer-logo.png` — 512 px transparent raster for social cards and repository use;
- `replayer-hero-light.png` and `replayer-hero-dark.png` — 1600 × 680 true-alpha README heroes composed from the real captures below.

The hero uses grayscale treatment, transparent packet lanes, shadows, and an editorial crop that retains the successful Agent Center task/evidence regions. It does not redraw, regenerate, or invent application UI. The Windows executable icon is derived from the same mark at `ReVM/Assets/Brand/REPlayer.ico`.

## Current screenshots

- `workbench.png` — actual REPlayer WPF chrome composited with the matching ADB framebuffer because GPU-rendered native child windows do not support `PrintWindow` capture;
- `agent-center.png` — actual Agent Center WPF render from the deterministic `center-showcase` surface with a public-safe evidence root.

Future captures can add runtime management, evidence, and diagnostics. Use PNG at 16:9 where practical, document any editorial crop, never alter status or result text, and remove user names, API keys, package samples, case identifiers, and local filesystem paths before committing.

## Video preview

Store a static cover image here and link it to a GitHub user attachment, release asset, or approved video host:

```html
<a href="VIDEO_URL">
  <img src="media/showcase-cover.png" alt="Watch the REPlayer showcase" width="100%">
</a>
```

Do not commit large MP4 files to the Git history. Attach them to a GitHub Release or upload them through GitHub's media flow, then link the generated URL from the README.