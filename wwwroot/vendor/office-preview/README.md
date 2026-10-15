# Local Office preview libraries

Vendored browser builds, served by the Navigator itself. No runtime CDN requests.

- docx-preview 0.4.1, Apache-2.0: https://github.com/VolodymyrBaydalka/docxjs
- JSZip 3.10.2, MIT option: https://github.com/Stuk/jszip
- SheetJS CE 0.20.3, Apache-2.0: https://cdn.sheetjs.com/xlsx-0.20.3/package/dist/xlsx.full.min.js

License texts are included beside the builds. The renderer lives in a sandboxed
iframe with a restrictive CSP and no network connections. It receives document
bytes from its parent, fetched from the existing same-origin download endpoint.
DOCX layout is approximate; XLSX is a values/merged-cells preview, not Excel.
Preview limits: 20 MiB compressed, 100 MiB declared uncompressed archive size,
10,000 archive entries; XLSX display up to 1,000 rows and 100 columns per sheet.
