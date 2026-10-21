// Generates the small synthetic fixture files used by the bulk scoring mock source.
// Usage: node tests/fixtures/bulk-scoring-requests/generate-files.mjs
// Everything is synthetic; no real candidate data. Output is deterministic.
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, 'files');
mkdirSync(out, { recursive: true });

function pdf(pages, { encrypted = false } = {}) {
  const objects = [];
  const add = (body) => { objects.push(body); return objects.length; };
  const catalogId = add('');
  const pagesId = add('');
  const fontId = add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>');
  const pageIds = [];
  for (const lines of pages) {
    const text = lines.map((l, i) => `BT /F1 12 Tf 72 ${720 - i * 18} Td (${l.replace(/[()\\]/g, '')}) Tj ET`).join('\n');
    const contentId = add(`<< /Length ${text.length} >>\nstream\n${text}\nendstream`);
    pageIds.push(add(`<< /Type /Page /Parent ${pagesId} 0 R /MediaBox [0 0 612 792] /Contents ${contentId} 0 R /Resources << /Font << /F1 ${fontId} 0 R >> >> >>`));
  }
  objects[catalogId - 1] = '<< /Type /Catalog /Pages ' + pagesId + ' 0 R >>';
  objects[pagesId - 1] = `<< /Type /Pages /Kids [${pageIds.map((i) => `${i} 0 R`).join(' ')}] /Count ${pageIds.length} >>`;
  let encryptId = 0;
  if (encrypted) encryptId = add('<< /Filter /Standard /V 1 /R 2 /O (0000000000000000) /U (0000000000000000) /P -4 >>');
  let body = '%PDF-1.4\n';
  const offsets = [];
  objects.forEach((o, i) => { offsets.push(body.length); body += `${i + 1} 0 obj\n${o}\nendobj\n`; });
  const xref = body.length;
  body += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  offsets.forEach((o) => { body += `${String(o).padStart(10, '0')} 00000 n \n`; });
  body += `trailer\n<< /Size ${objects.length + 1} /Root ${catalogId} 0 R${encrypted ? ` /Encrypt ${encryptId} 0 R` : ''} >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(body, 'latin1');
}

function geometricPdf(pageSpecs) {
  const objects = [];
  const add = (body) => { objects.push(body); return objects.length; };
  const catalogId = add('');
  const pagesId = add('');
  const fontId = add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>');
  const pageIds = [];
  for (const page of pageSpecs) {
    const text = (page.marks ?? [])
      .map(({ x, y, size = 18, text }) => `BT /F1 ${size} Tf ${x} ${y} Td (${text.replace(/[()\\]/g, '')}) Tj ET`)
      .join('\n');
    const contentId = add(`<< /Length ${text.length} >>\nstream\n${text}\nendstream`);
    pageIds.push(add(`<< /Type /Page /Parent ${pagesId} 0 R /MediaBox [0 0 ${page.width} ${page.height}] /Contents ${contentId} 0 R /Resources << /Font << /F1 ${fontId} 0 R >> >> >>`));
  }
  objects[catalogId - 1] = `<< /Type /Catalog /Pages ${pagesId} 0 R >>`;
  objects[pagesId - 1] = `<< /Type /Pages /Kids [${pageIds.map((i) => `${i} 0 R`).join(' ')}] /Count ${pageIds.length} >>`;
  let body = '%PDF-1.4\n';
  const offsets = [];
  objects.forEach((object, index) => {
    offsets.push(body.length);
    body += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xref = body.length;
  body += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  offsets.forEach((offset) => { body += `${String(offset).padStart(10, '0')} 00000 n \n`; });
  body += `trailer\n<< /Size ${objects.length + 1} /Root ${catalogId} 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(body, 'latin1');
}

const crcTable = new Uint32Array(256).map((_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
function crc32(buf) {
  let c = 0xffffffff;
  for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function zip(entries) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const [name, content] of entries) {
    const nameBuf = Buffer.from(name);
    const data = Buffer.from(content);
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(0, 6); local.writeUInt16LE(0, 8);
    local.writeUInt16LE(0x21, 10); local.writeUInt16LE(0x21, 12); local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(nameBuf.length, 26); local.writeUInt16LE(0, 28);
    locals.push(local, nameBuf, data);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt16LE(0, 8); central.writeUInt16LE(0, 10);
    central.writeUInt16LE(0x21, 12); central.writeUInt16LE(0x21, 14); central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20); central.writeUInt32LE(data.length, 24); central.writeUInt16LE(nameBuf.length, 28);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, nameBuf);
    offset += 30 + nameBuf.length + data.length;
  }
  const centralBuf = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(entries.length, 8); end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralBuf.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, centralBuf, end]);
}

const docx = zip([
  ['[Content_Types].xml', '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'],
  ['_rels/.rels', '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'],
  ['word/document.xml', '<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Carla Example - Data Analyst. Synthetic test CV.</w:t></w:r></w:p></w:body></w:document>'],
]);

const files = {
  'pdf-1page.pdf': pdf([['Alice Example', 'Software Engineer', '8 years of TypeScript and C#', 'Synthetic test CV']]),
  'pdf-2page.pdf': pdf([
    ['Bob Example', 'Platform Engineer', 'Azure, Terraform, Kubernetes', 'Synthetic test CV'],
    ['Page 2', 'Education: BSc Computer Science', 'Languages: English, German'],
  ]),
  'cv.docx': docx,
  'cv.md': Buffer.from('# Dana Example\n\nData Scientist. Python, SQL, statistics.\n\nSynthetic test CV.\n'),
  'cv.txt': Buffer.from('Eli Example\nSupport Engineer\nSynthetic test CV.\n'),
  'cv.png': Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64'),
  'cv.jpg': Buffer.from('/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=', 'base64'),
  'corrupt.pdf': Buffer.concat([Buffer.from('%PDF-1.4\n'), Buffer.from([0, 255, 17, 34, 51, 68, 85, 102, 119, 136, 153, 170])]),
  'encrypted.pdf': pdf([['Encrypted synthetic CV']], { encrypted: true }),
  'archive.zip': zip([['readme.txt', 'not a candidate document']]),
  'empty.txt': Buffer.alloc(0),
  'multibyte.txt': Buffer.from('Résumé — synthetic UTF-8 fixture — 東京\n', 'utf8'),
  'binary.bin': Buffer.from([0x00, 0xff, 0x10, 0x80, 0x7f, 0x42]),
};

for (const [name, data] of Object.entries(files)) writeFileSync(join(out, name), data);

// The large tree stays a compact deterministic manifest rather than committing 2,500 copies.
// Both stack mocks expand this description identically and can therefore exercise the 1,000 /
// 1,000 / 500 discovery steps without consuming repository space.
const largeFolder = Array.from({ length: 2500 }, (_, index) => {
  const ordinal = index + 1;
  return {
    id: `large-${String(ordinal).padStart(4, '0')}`,
    parentId: `large-subfolder-${String(Math.floor(index / 100) + 1).padStart(2, '0')}`,
    name: `candidate-${String(ordinal).padStart(4, '0')}.pdf`,
    file: ordinal % 250 === 0 ? 'pdf-2page.pdf' : 'pdf-1page.pdf',
    inaccessible: ordinal === 1750,
  };
});
writeFileSync(
  join(here, 'generated-large-folder.json'),
  JSON.stringify(
    {
      $comment: 'Synthetic deterministic expansion used by both stacks.',
      eligibleCount: 2500,
      discoveryStepCounts: [1000, 1000, 500],
      items: largeFolder,
    },
    null,
    2,
  ) + '\n',
);

const rasterOut = join(here, 'raster');
mkdirSync(rasterOut, { recursive: true });
const portrait = {
  width: 612,
  height: 792,
  marks: [
    { x: 8, y: 770, size: 14, text: 'P1-TOP-LEFT' },
    { x: 455, y: 18, size: 14, text: 'P1-BOTTOM-RIGHT' },
    { x: 240, y: 390, size: 24, text: 'PORTRAIT' },
  ],
};
const landscape = {
  width: 792,
  height: 612,
  marks: [
    { x: 8, y: 590, size: 14, text: 'P2-TOP-LEFT' },
    { x: 625, y: 18, size: 14, text: 'P2-BOTTOM-RIGHT' },
    { x: 325, y: 300, size: 24, text: 'LANDSCAPE' },
  ],
};
const custom = {
  width: 500,
  height: 700,
  marks: [
    { x: 8, y: 678, size: 14, text: 'P3-TOP-LEFT' },
    { x: 340, y: 18, size: 14, text: 'P3-BOTTOM-RIGHT' },
    { x: 175, y: 345, size: 24, text: 'CUSTOM' },
    { x: 246, y: 351, size: 5, text: '.' },
  ],
};
const rasterFixtures = {
  'one-page-source.pdf': geometricPdf([portrait]),
  'one-page-identical.pdf': geometricPdf([portrait]),
  'multi-page-source.pdf': geometricPdf([portrait, landscape, custom]),
  'multi-page-identical.pdf': geometricPdf([portrait, landscape, custom]),
  'multi-page-reordered.pdf': geometricPdf([landscape, portrait, custom]),
  'multi-page-blank.pdf': geometricPdf([portrait, { width: 792, height: 612, marks: [] }, custom]),
  'multi-page-cropped.pdf': geometricPdf([
    portrait,
    landscape,
    { ...custom, marks: custom.marks.filter((mark) => !mark.text.includes('BOTTOM-RIGHT')) },
  ]),
  'near-blank-source.pdf': geometricPdf([{ width: 612, height: 792, marks: [{ x: 300, y: 396, size: 5, text: '.' }] }]),
  'near-blank-identical.pdf': geometricPdf([{ width: 612, height: 792, marks: [{ x: 300, y: 396, size: 5, text: '.' }] }]),
};
for (const [name, data] of Object.entries(rasterFixtures)) {
  writeFileSync(join(rasterOut, name), data);
}

console.log(
  JSON.stringify({
    outputDirectory: out,
    fixtureFileCount: Object.keys(files).length,
    generatedEligibleCount: largeFolder.length,
    discoveryStepCounts: [1000, 1000, 500],
    rasterFixtureCount: Object.keys(rasterFixtures).length,
  }),
);
