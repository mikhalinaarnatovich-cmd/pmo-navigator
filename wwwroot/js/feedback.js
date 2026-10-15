(() => {
    'use strict';
    const $ = id => document.getElementById(id);
    const labels = { issue: 'Ошибка', idea: 'Предложение', question: 'Вопрос' };
    let base = '', access, page = 1, total = 0, submitting = false, requestId, sequence = 0;
    function newId() {
        const b = crypto.getRandomValues(new Uint8Array(16));
        b[6] = (b[6] & 15) | 64; b[8] = (b[8] & 63) | 128;
        const h = Array.from(b, x => x.toString(16).padStart(2, '0')).join('');
        return `${h.slice(0,8)}-${h.slice(8,12)}-${h.slice(12,16)}-${h.slice(16,20)}-${h.slice(20)}`;
    }
    const api = path => `${base}/api/feedback${path || ''}`;
    function status(text = '', type = '') { $('feedbackStatus').textContent = text; $('feedbackStatus').className = `fb-status ${type}`; }
    function currentSection() {
        const sections = { resourceView: 'Ресурсный план', portfolioView: 'Ганта портфеля',
            pkView: 'Проектный комитет', crView: 'Запросы на изменение',
            dashboardView: 'Дашборд', docCtrlView: 'Контроль документов' };
        for (const [id, label] of Object.entries(sections)) {
            const el = $(id);
            if (el && el.getClientRects().length && getComputedStyle(el).display !== 'none') return label;
        }
        return 'Портфель';
    }
    function tab(inbox) {
        $('feedbackFormPanel').hidden = inbox;
        $('feedbackInboxPanel').hidden = !inbox;
        $('feedbackWriteTab').setAttribute('aria-selected', String(!inbox));
        $('feedbackInboxTab').setAttribute('aria-selected', String(inbox));
        status();
        if (inbox) { page = 1; loadList(); }
    }
    async function open(prefix) {
        base = String(prefix || '').replace(/\/+$/, '');
        access = null;
        $('feedbackSection').value = currentSection();
        if (!requestId) requestId = newId();
        $('feedbackDialog').showModal();
        tab(false);
        $('feedbackSend').disabled = true;
        $('feedbackExport').disabled = true;
        $('feedbackNotice').textContent = 'Проверяем доступ…';
        try {
            const response = await fetch(api('/access'), { credentials: 'same-origin', cache: 'no-store' });
            if (!response.ok) throw new Error('Не удалось проверить доступ к обратной связи.');
            access = await response.json();
            $('feedbackNotice').classList.toggle('test', access.testReview);
            $('feedbackNotice').textContent = access.testReview
                ? 'Тестовый доступ на Render: список обращений и Excel доступны всем посетителям стенда. Отправляйте только тестовые сообщения, без рабочих и персональных данных.'
                : 'Обращения сохраняются в Навигаторе. Список и экспорт доступны только владельцу раздела.';
            $('feedbackAuthor').readOnly = access.authenticated;
            if (access.authenticated) $('feedbackAuthor').value = access.author;
            $('feedbackAuthorHint').textContent = access.authenticated
                ? 'Определено по рабочей учётной записи.' : 'Необязательно. В тестовом режиме имя не подтверждается.';
            $('feedbackSend').disabled = !access.canSubmit || submitting;
            $('feedbackExport').disabled = !access.canReview;
            if (!access.canSubmit) status('Для отправки войдите под рабочей учётной записью.', 'error');
            if (!$('feedbackInboxPanel').hidden) loadList();
        } catch (error) { access = null; status(error.message, 'error'); }
    }
    async function errorText(response, fallback) {
        if (response.status === 429) return 'Слишком много отправок. Подождите минуту и повторите.';
        const data = await response.json().catch(() => ({}));
        return data.message || fallback;
    }
    $('feedbackForm').addEventListener('input', () => {
        if (!submitting) requestId = newId();
        $('feedbackLength').textContent = `${$('feedbackMessage').value.length} / 5000`;
    });
    $('feedbackForm').addEventListener('submit', async event => {
        event.preventDefault();
        if (submitting || !access?.canSubmit) return;
        if (!$('feedbackForm').reportValidity()) return;
        if ($('feedbackMessage').value.trim().length < 5) { status('Напишите сообщение не короче 5 символов.', 'error'); return; }
        submitting = true; $('feedbackSend').disabled = true; $('feedbackSend').textContent = 'Отправка…';
        $('feedbackForm').querySelectorAll('input,select,textarea').forEach(el => el.disabled = true);
        status();
        const sentMessage = $('feedbackMessage').value;
        try {
            const response = await fetch(api(), {
                method: 'POST', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': access.token },
                body: JSON.stringify({ requestId, kind: $('feedbackKind').value, message: sentMessage,
                    section: $('feedbackSection').value, authorName: $('feedbackAuthor').value })
            });
            if (!response.ok) throw new Error(await errorText(response, 'Не удалось отправить. Закройте и откройте форму, затем повторите; текст сохранён.'));
            const result = await response.json();
            status(`Обращение №${result.id} сохранено. Спасибо за обратную связь!`, 'success');
            if ($('feedbackMessage').value === sentMessage) $('feedbackMessage').value = '';
            $('feedbackLength').textContent = `${$('feedbackMessage').value.length} / 5000`;
            requestId = newId();
        } catch (error) { status(error.message || 'Нет связи с сервером. Текст оставлен в форме.', 'error'); }
        finally {
            submitting = false;
            $('feedbackForm').querySelectorAll('input,select,textarea').forEach(el => el.disabled = false);
            $('feedbackSend').disabled = !access?.canSubmit; $('feedbackSend').textContent = 'Отправить';
        }
    });
    function filters() {
        const p = new URLSearchParams();
        if ($('feedbackFilterKind').value) p.set('kind', $('feedbackFilterKind').value);
        if ($('feedbackFrom').value) p.set('from', $('feedbackFrom').value);
        if ($('feedbackTo').value) p.set('to', $('feedbackTo').value);
        return p;
    }
    function cell(row, value) { const td = row.insertCell(); td.textContent = value ?? ''; return td; }
    async function loadList() {
        const mine = ++sequence;
        $('feedbackRows').replaceChildren();
        $('feedbackEmpty').hidden = false; $('feedbackEmpty').textContent = 'Загрузка обращений…';
        $('feedbackPrev').disabled = $('feedbackNext').disabled = true;
        if (!access?.canReview) { $('feedbackEmpty').textContent = 'Просмотр и экспорт доступны только владельцу раздела.'; return; }
        try {
            const p = filters(); p.set('page', String(page));
            const response = await fetch(api('?' + p), { cache: 'no-store', credentials: 'same-origin' });
            if (!response.ok) throw new Error(await errorText(response, 'Не удалось загрузить обращения.'));
            const result = await response.json();
            if (mine !== sequence) return;
            total = result.total;
            for (const item of result.items) {
                const row = $('feedbackRows').insertRow();
                cell(row, item.id);
                cell(row, new Date(item.createdAt).toLocaleString('ru-RU', { timeZone: 'Europe/Minsk', dateStyle: 'short', timeStyle: 'short' }));
                cell(row, labels[item.kind] || item.kind);
                const author = cell(row, item.authorName || 'Без имени');
                const note = document.createElement('span'); note.className = 'fb-author-note';
                note.textContent = item.isAnonymous ? 'Тест: автор не подтверждён' : item.authorLogin;
                author.appendChild(note);
                cell(row, item.section); cell(row, item.message);
            }
            $('feedbackEmpty').hidden = result.items.length > 0;
            $('feedbackEmpty').textContent = 'Обращений по выбранным фильтрам пока нет.';
            $('feedbackPage').textContent = `Всего: ${total}. Страница ${page} из ${Math.max(1, Math.ceil(total / 50))}`;
            $('feedbackPrev').disabled = page <= 1;
            $('feedbackNext').disabled = page * 50 >= total;
        } catch (error) {
            if (mine !== sequence) return;
            $('feedbackEmpty').textContent = error.message; $('feedbackPage').textContent = '';
            status(error.message, 'error');
        }
    }
    $('feedbackExport').addEventListener('click', async () => {
        if (!access?.canReview) return;
        $('feedbackExport').disabled = true; status('Подготовка Excel…');
        try {
            const response = await fetch(api('/export/excel?' + filters()), { cache: 'no-store', credentials: 'same-origin' });
            if (!response.ok) throw new Error(await errorText(response, 'Не удалось скачать Excel.'));
            const blob = await response.blob();
            const url = URL.createObjectURL(blob), a = document.createElement('a');
            a.href = url; a.download = 'pmo-feedback.xlsx'; document.body.appendChild(a); a.click(); a.remove();
            setTimeout(() => URL.revokeObjectURL(url), 30000);
            status('Excel подготовлен. Проверьте загрузки браузера.', 'success');
        } catch (error) { status(error.message, 'error'); }
        finally { $('feedbackExport').disabled = !access?.canReview; }
    });
    $('feedbackWriteTab').addEventListener('click', () => tab(false));
    $('feedbackInboxTab').addEventListener('click', () => tab(true));
    $('feedbackRefresh').addEventListener('click', () => { page = 1; status(); loadList(); });
    ['feedbackFilterKind', 'feedbackFrom', 'feedbackTo'].forEach(id =>
        $(id).addEventListener('change', () => { page = 1; status(); loadList(); }));
    $('feedbackPrev').addEventListener('click', () => { page--; loadList(); });
    $('feedbackNext').addEventListener('click', () => { page++; loadList(); });
    $('feedbackClose').addEventListener('click', () => $('feedbackDialog').close());
    window.PmoFeedback = { open };
})();
