/* Local, read-only Office previews. Never sends documents to a third-party viewer. */
(() => {
    'use strict';
    const MAX_BYTES = 20 * 1024 * 1024;
    let controller, timer, generation = 0, previousFocus, pending;
    const element = id => document.getElementById(id);

    function close() {
        generation++;
        controller?.abort();
        pending = null;
        clearTimeout(timer);
        element('filePreviewIframe').onload = null;
        element('filePreviewIframe').src = 'about:blank';
        element('filePreviewModal').classList.remove('open');
        previousFocus?.focus?.();
    }

    function message(text, error = false) {
        const status = element('filePreviewStatus');
        status.textContent = text;
        status.classList.toggle('is-error', error);
    }

    async function readLimited(response) {
        if (Number(response.headers.get('content-length')) > MAX_BYTES)
            throw new Error('Файл больше 20 МБ. Для просмотра скачайте оригинал.');
        const reader = response.body.getReader();
        const parts = [];
        let size = 0;
        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            size += value.length;
            if (size > MAX_BYTES) {
                await reader.cancel();
                throw new Error('Файл больше 20 МБ. Для просмотра скачайте оригинал.');
            }
            parts.push(value);
        }
        const bytes = new Uint8Array(size);
        let offset = 0;
        for (const part of parts) { bytes.set(part, offset); offset += part.length; }
        return bytes.buffer;
    }

    async function open(path, base) {
        close();
        const current = generation;
        previousFocus = document.activeElement;
        const prefix = String(base || '').replace(/\/+$/, '');
        const name = path.split(/[\\/]/).pop();
        const ext = name.split('.').pop().toLowerCase();
        const oldFrame = element('filePreviewIframe');
        const frame = oldFrame.cloneNode(false);
        oldFrame.replaceWith(frame);
        const url = prefix + '/api/file/download?path=' + encodeURIComponent(path);
        element('filePreviewName').textContent = name;
        element('filePreviewDownload').href = url;
        element('filePreviewDownload').download = name;
        element('filePreviewModal').classList.add('open');
        element('filePreviewClose').focus();
        frame.hidden = true;
        frame.onload = null;
        frame.removeAttribute('sandbox');
        message('Загрузка документа…');

        const nativeTypes = ['pdf', 'png', 'jpg', 'jpeg', 'gif', 'bmp', 'svg', 'txt', 'csv', 'mp4', 'webm'];
        if (nativeTypes.includes(ext)) {
            frame.hidden = false;
            message('Просмотр оригинала. Если документ не открылся, используйте «Скачать оригинал».');
            frame.src = prefix + '/api/file/preview?path=' + encodeURIComponent(path);
            return;
        }
        if (!['docx', 'xlsx'].includes(ext)) {
            message('Просмотр этого формата не поддерживается. Скачайте оригинал; для Word используйте DOCX, для Excel — XLSX.');
            return;
        }

        controller = new AbortController();
        timer = setTimeout(() => {
            if (generation !== current) return;
            controller.abort();
            frame.onload = null;
            frame.src = 'about:blank';
            frame.hidden = true;
            message('Просмотр занял слишком много времени. Попробуйте снова или скачайте оригинал.', true);
        }, 60000);
        try {
            const response = await fetch(url, { credentials: 'same-origin', signal: controller.signal });
            if (!response.ok) {
                const detail = response.status === 404 ? 'Файл не найден.'
                    : [401, 403].includes(response.status) ? 'Нет доступа к файлу.'
                    : `Не удалось получить файл (HTTP ${response.status}).`;
                throw new Error(detail + ' Проверьте скачивание оригинала.');
            }
            const buffer = await readLimited(response);
            if (generation !== current) return;
            if (/text\/html/i.test(response.headers.get('content-type') || ''))
                throw new Error('Вместо документа сервер вернул страницу. Проверьте доступ и скачивание оригинала.');
            frame.setAttribute('sandbox', 'allow-scripts');
            frame.hidden = false;
            pending = { type: 'pmo-preview-open', id: current, ext, buffer };
            frame.src = prefix + '/office-preview/viewer.html?v=1#' + current;
        } catch (error) {
            if (generation !== current || error.name === 'AbortError') return;
            clearTimeout(timer);
            message(error.message || 'Не удалось открыть документ. Скачайте оригинал.', true);
        }
    }

    window.addEventListener('message', event => {
        const frame = element('filePreviewIframe');
        const data = event.data;
        if (event.source !== frame.contentWindow || !data || data.id !== generation) return;
        if (data.type === 'pmo-preview-boot' && pending) {
            const payload = pending;
            pending = null;
            frame.contentWindow.postMessage(payload, '*', [payload.buffer]);
            return;
        }
        if (data.type === 'pmo-preview-close') { close(); return; }
        if (!['pmo-preview-ready', 'pmo-preview-error'].includes(data.type)) return;
        clearTimeout(timer);
        if (data.type === 'pmo-preview-error') {
            frame.hidden = true;
            frame.src = 'about:blank';
            message('Не удалось разобрать документ: файл может быть повреждён, защищён паролем или слишком сложным. Скачайте оригинал.', true);
        } else {
            message('Локальный просмотр, только чтение. Оформление и разбиение на страницы могут отличаться от оригинала. Документ не отправляется во внешние сервисы.');
        }
    });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && element('filePreviewModal').classList.contains('open')) close();
    });
    window.PmoFilePreview = { open, close };
})();
