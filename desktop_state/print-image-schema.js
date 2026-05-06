const fs = require('fs');
const s = JSON.parse(fs.readFileSync('docs/openapi.actions.json', 'utf8'));
const out = {
  schemas: {
    ActionImage: s.components.schemas.ActionImage,
    ImageResult: s.components.schemas.ImageResult,
    ImageToolResponse: s.components.schemas.ImageToolResponse,
    ScreenshotDesktopRequest: s.components.schemas.ScreenshotDesktopRequest,
    ViewImageRequest: s.components.schemas.ViewImageRequest,
  },
  paths: {
    viewImage: s.paths['/tools/view-image'],
    screenshotDesktop: s.paths['/tools/screenshot-desktop'],
  },
};
console.log(JSON.stringify(out, null, 2));
