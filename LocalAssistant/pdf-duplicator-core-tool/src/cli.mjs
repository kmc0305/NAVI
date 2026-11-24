#!/usr/bin/env node
import { imagesToPdf } from "./images-to-pdf.mjs";

const [, , outputPath, ...imagePaths] = process.argv;

if (!outputPath || imagePaths.length === 0) {
  console.error("사용법: imgtopdf <output.pdf> <image1> [image2 ...]");
  process.exit(1);
}

const { totalPages } = await imagesToPdf(imagePaths, outputPath);
console.log(`완료: ${totalPages}장 -> ${outputPath}`);
