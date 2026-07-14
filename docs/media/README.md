# README media

This directory is the stable home for README preview media. Keep product captures factual and use the native REPlayer monochrome UI without decorative mock-device frames.

## Screenshot names

The top-level README reserves these paths:

- `workbench.png` — full main workbench with the native emulator display;
- `agent-center.png` — concurrent task queue and selected result;
- `runtime.png` — runtime or instance-management workflow;
- `evidence.png` — case evidence, diagnostics, or network capture.

Use PNG at 16:9 where practical, crop only outside the application window, and remove user names, API keys, package samples, case identifiers, and local filesystem paths before committing.

Replace each reserved code-form path in the README with an image element:

```html
<img src="docs/media/workbench.png" alt="REPlayer main workbench" width="100%">
```

## Video preview

Store a static cover image here and link it to a GitHub user attachment, release asset, or approved video host:

```html
<a href="VIDEO_URL">
  <img src="docs/media/showcase-cover.png" alt="Watch the REPlayer showcase" width="100%">
</a>
```

Do not commit large MP4 files to the Git history. Attach them to a GitHub Release or upload them through GitHub's media flow, then link the generated URL from the README.