# README media

This directory is the stable home for README preview media. Keep product captures factual and use the native REPlayer monochrome UI without decorative mock-device frames.

## Current screenshots

- `workbench.png` — actual REPlayer WPF chrome composited with the matching ADB framebuffer because GPU-rendered native child windows do not support `PrintWindow` capture;
- `agent-center.png` — actual Agent Center WPF render from the deterministic `center-showcase` surface with a public-safe evidence root.

Future captures can add runtime management, evidence, and diagnostics. Use PNG at 16:9 where practical, crop only outside the application window, and remove user names, API keys, package samples, case identifiers, and local filesystem paths before committing.

## Video preview

Store a static cover image here and link it to a GitHub user attachment, release asset, or approved video host:

```html
<a href="VIDEO_URL">
  <img src="docs/media/showcase-cover.png" alt="Watch the REPlayer showcase" width="100%">
</a>
```

Do not commit large MP4 files to the Git history. Attach them to a GitHub Release or upload them through GitHub's media flow, then link the generated URL from the README.