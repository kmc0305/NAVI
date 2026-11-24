import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { PDFDocument } from "pdf-lib";

// 화면 캡쳐 등으로 이미 만들어진 이미지 여러 장을 한 PDF로 합친다(예: 다운로드가 막힌
// PDF 뷰어를 한 쪽씩 캡쳐한 뒤 하나의 파일로 모을 때). 이미지 1장 = PDF 1쪽이고, 쪽
// 크기는 그 이미지의 실제 픽셀 크기를 그대로 포인트 단위로 써서 1:1로 맞춘다.
export async function imagesToPdf(imagePaths, outputPath) {
  const outPdf = await PDFDocument.create();

  for (const imagePath of imagePaths) {
    const bytes = await readFile(imagePath);
    const ext = path.extname(imagePath).toLowerCase();
    const image = ext === ".png" ? await outPdf.embedPng(bytes) : await outPdf.embedJpg(bytes);
    const page = outPdf.addPage([image.width, image.height]);
    page.drawImage(image, { x: 0, y: 0, width: image.width, height: image.height });
  }

  await writeFile(outputPath, await outPdf.save());
  return { totalPages: imagePaths.length };
}
