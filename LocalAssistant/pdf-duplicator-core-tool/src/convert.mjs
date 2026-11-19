import { readFile, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { createCanvas } from "@napi-rs/canvas";
import { getDocument } from "pdfjs-dist/legacy/build/pdf.mjs";
import { PDFDocument } from "pdf-lib";

const DEFAULT_RENDER_SCALE = 2; // ~144 DPI raster quality for the embedded page images

// Non-embedded standard fonts (Helvetica, Times, etc.) need pdfjs-dist's own
// font metrics data to render correctly instead of silently coming out blank.
// pdfjs-dist's Node fetch path (node_utils.js) does a plain fs.readFile(url),
// so this must be a filesystem path, not a file:// URL.
const require = createRequire(import.meta.url);
const standardFontDataUrl =
  path.join(path.dirname(require.resolve("pdfjs-dist/package.json")), "standard_fonts").replaceAll("\\", "/") + "/";

// Headless replacement for the M1 FlaUI/Polaris pipeline. M1's job was always
// to *duplicate* a PDF (see "PDF 복제 도구"); it rendered each page and pasted
// it into Polaris only because, at the time, that was the only available
// conversion path. This renders each page directly and re-embeds it into a
// fresh PDF — no Windows/Polaris/Chrome dependency, runnable inside Core's own
// (Linux/Docker) Node.js runtime.
export async function duplicatePdf(pdfPath, outputPath, { scale = DEFAULT_RENDER_SCALE } = {}) {
  const data = new Uint8Array(await readFile(pdfPath));
  const srcPdf = await getDocument({ data, standardFontDataUrl }).promise;

  const outPdf = await PDFDocument.create();

  for (let pageNum = 1; pageNum <= srcPdf.numPages; pageNum++) {
    const page = await srcPdf.getPage(pageNum);
    const unscaledViewport = page.getViewport({ scale: 1 });

    const renderViewport = page.getViewport({ scale });
    const canvas = createCanvas(Math.ceil(renderViewport.width), Math.ceil(renderViewport.height));
    const context = canvas.getContext("2d");
    await page.render({ canvasContext: context, viewport: renderViewport }).promise;

    const pngImage = await outPdf.embedPng(canvas.toBuffer("image/png"));

    // Output page matches the source page's exact point size, so the image
    // fills it 1:1 — no fit/letterbox math needed like the PPTX version.
    const outPage = outPdf.addPage([unscaledViewport.width, unscaledViewport.height]);
    outPage.drawImage(pngImage, { x: 0, y: 0, width: unscaledViewport.width, height: unscaledViewport.height });
  }

  await writeFile(outputPath, await outPdf.save());

  return { totalPages: srcPdf.numPages };
}
