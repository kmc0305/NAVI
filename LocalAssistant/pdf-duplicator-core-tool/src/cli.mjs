#!/usr/bin/env node
import { duplicatePdf } from "./convert.mjs";

const [, , inputPath, outputPath] = process.argv;

if (!inputPath || !outputPath) {
  console.error("사용법: pdfdup <input.pdf> <output.pdf>");
  process.exit(1);
}

const { totalPages } = await duplicatePdf(inputPath, outputPath);
console.log(`완료: ${totalPages} 페이지 -> ${outputPath}`);
