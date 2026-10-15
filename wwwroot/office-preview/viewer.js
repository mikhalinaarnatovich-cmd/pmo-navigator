/* Sandboxed local renderer. No uploads, macros, formula execution or external links. */
(() => {
    'use strict';
    let requestId, received = false, workbook;
    const $ = id => document.getElementById(id);
    const reply = type => parent.postMessage({ type, id: requestId }, '*');
    $('zoom').addEventListener('change', () => { $('word').style.zoom = $('zoom').value; });
    $('sheetSelect').addEventListener('change', () => {
        try { showSheet(); }
        catch { reply('pmo-preview-error'); }
    });
    document.addEventListener('click', event => {
        if (event.target.closest('a')) event.preventDefault();
    }, true);
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape') reply('pmo-preview-close');
    });

    async function checkArchive(buffer) {
        const zip = await JSZip.loadAsync(buffer);
        const entries = Object.values(zip.files);
        if (entries.length > 10000) throw new Error('Archive too complex');
        // JSZip exposes the declared uncompressed length before inflation.
        const size = entries.reduce((sum, file) => sum + (file._data?.uncompressedSize || 0), 0);
        if (size > 100 * 1024 * 1024) throw new Error('Archive too large');
        return zip;
    }

    function showSheet() {
        const sheet = workbook.Sheets[$('sheetSelect').value];
        const host = $('sheet');
        host.replaceChildren();
        if (!sheet || !sheet['!ref']) {
            host.textContent = sheet?.['!fullref']
                ? 'В доступном фрагменте листа нет данных. Полную книгу смотрите в оригинале.'
                : 'Лист пуст.';
            $('note').textContent = 'Для больших листов просмотр ограничен первыми 1000 строками.';
            return;
        }
        const range = XLSX.utils.decode_range(sheet['!ref']);
        const rowEnd = Math.min(range.e.r, range.s.r + 999);
        const colEnd = Math.min(range.e.c, range.s.c + 99);
        const table = document.createElement('table');
        const header = table.createTHead().insertRow();
        header.appendChild(document.createElement('th'));
        for (let col = range.s.c; col <= colEnd; col++) {
            const th = document.createElement('th');
            th.textContent = XLSX.utils.encode_col(col);
            header.appendChild(th);
        }
        const tbody = table.createTBody();
        // Render values as text, never spreadsheet-provided HTML or hyperlinks.
        const merges = (sheet['!merges'] || []).filter(m =>
            m.s.r >= range.s.r && m.s.r <= rowEnd && m.s.c >= range.s.c && m.s.c <= colEnd);
        const covered = new Set(), spans = new Map();
        let mergedCells = 0;
        if (merges.length > 5000) throw new Error('Too many merged ranges');
        for (const merge of merges) {
            const endR = Math.min(merge.e.r, rowEnd), endC = Math.min(merge.e.c, colEnd);
            mergedCells += (endR - merge.s.r + 1) * (endC - merge.s.c + 1);
            if (mergedCells > 200000) throw new Error('Merged ranges too complex');
            spans.set(`${merge.s.r}:${merge.s.c}`, [endR - merge.s.r + 1, endC - merge.s.c + 1]);
            for (let r = merge.s.r; r <= endR; r++)
                for (let c = merge.s.c; c <= endC; c++)
                    if (r !== merge.s.r || c !== merge.s.c) covered.add(`${r}:${c}`);
        }
        for (let row = range.s.r; row <= rowEnd; row++) {
            const tr = tbody.insertRow();
            const th = document.createElement('th');
            th.textContent = String(row + 1); tr.appendChild(th);
            for (let col = range.s.c; col <= colEnd; col++) {
                const key = `${row}:${col}`;
                if (covered.has(key)) continue;
                const td = tr.insertCell();
                const cell = sheet[XLSX.utils.encode_cell({ r: row, c: col })];
                if (cell) {
                    td.textContent = cell.w ?? (cell.v != null ? XLSX.utils.format_cell(cell) : cell.f ? 'Нет сохранённого результата' : '');
                    if (cell.f) td.title = 'Формула: показан сохранённый результат, пересчёт не выполняется.';
                }
                if (spans.has(key)) [td.rowSpan, td.colSpan] = spans.get(key);
            }
        }
        host.appendChild(table);
        const fullRange = XLSX.utils.decode_range(sheet['!fullref'] || sheet['!ref']);
        const limited = fullRange.e.r > rowEnd || fullRange.e.c > colEnd;
        $('note').textContent = 'Просмотр значений и объединений; без точного оформления, диаграмм и пересчёта формул.'
            + (limited ? ' Показан фрагмент: не более 1000 строк и 100 столбцов. Полная книга — в оригинале.' : '');
        $('viewport').scrollTo(0, 0);
    }

    window.addEventListener('message', async event => {
        if (received || event.source !== parent || event.data?.type !== 'pmo-preview-open') return;
        received = true;
        requestId = event.data.id;
        const { ext, buffer } = event.data;
        try {
            if (!(buffer instanceof ArrayBuffer) || buffer.byteLength > 20 * 1024 * 1024) throw new Error('Invalid file');
            const zip = await checkArchive(buffer);
            if (ext === 'docx') {
                if (!zip.file('word/document.xml')) throw new Error('Not DOCX');
                await docx.renderAsync(buffer, $('word'), $('wordStyles'), {
                    inWrapper: true, breakPages: true, ignoreLastRenderedPageBreak: false,
                    ignoreWidth: false, ignoreHeight: false, ignoreFonts: false,
                    renderHeaders: true, renderFooters: true, renderFootnotes: true,
                    renderEndnotes: true, renderAltChunks: false, renderComments: false,
                    useBase64URL: true, experimental: false
                });
                $('note').textContent = 'Word: возможны отличия шрифтов, переносов и расположения элементов.';
            } else if (ext === 'xlsx') {
                if (!zip.file('xl/workbook.xml')) throw new Error('Not XLSX');
                workbook = XLSX.read(buffer, { type: 'array', sheetRows: 1001, cellHTML: false, cellFormula: true });
                const visible = workbook.SheetNames.filter((name, i) => !workbook.Workbook?.Sheets?.[i]?.Hidden);
                if (!visible.length) throw new Error('No visible sheets');
                $('zoomLabel').hidden = true; $('word').hidden = true;
                $('sheetLabel').hidden = false; $('sheet').hidden = false;
                for (const name of visible) {
                    const option = document.createElement('option');
                    option.value = name; option.textContent = name;
                    $('sheetSelect').appendChild(option);
                }
                showSheet();
            } else throw new Error('Unsupported format');
            reply('pmo-preview-ready');
        } catch {
            reply('pmo-preview-error');
            $('note').textContent = 'Не удалось открыть документ. Скачайте оригинал.';
        }
    });
    parent.postMessage({ type: 'pmo-preview-boot', id: Number(location.hash.slice(1)) }, '*');
})();
