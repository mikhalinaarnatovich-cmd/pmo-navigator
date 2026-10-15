/* Manual-only read-only tour. No API calls, cookies, storage or auto-start. */
(() => {
    'use strict';
    const $ = id => document.getElementById(id);
    const steps = [
        {
            selectors: ['#sidebarNav'], placement: 'right', title: 'Проекты по статусам',
            text: 'Слева можно выбрать статус проекта. «Все» показывает проекты, кроме завершённых и отменённых.\nЦифры помогают оценить количество проектов с учётом поиска и фильтров.'
        },
        {
            selectors: ['#mainView .toolbar'], placement: 'bottom', title: 'Поиск и фильтры',
            text: 'Найдите проект по названию, ID, заказчику или руководителю. Уточните список по отделу, приоритету и другим фильтрам.\nПоиск и фильтры работают вместе с выбранным статусом.'
        },
        {
            selectors: ['#projectsTbody tr[onclick]'], fallback: '#mainView .projects-table thead',
            placement: 'bottom', title: 'Карточка проекта и документы',
            text: 'Нажатие на строку открывает карточку проекта: основные сведения, документы, прогресс и полноту документации.\nWord и Excel можно посмотреть внутри Навигатора; оформление может отличаться от оригинала.',
            empty: 'Сейчас в списке нет подходящей строки. Можно продолжить тур, а после него изменить фильтры и открыть проект.'
        },
        {
            selectors: ['#btnPortfolio'], placement: 'bottom', title: 'Ганта портфеля',
            text: 'Здесь показаны сроки проектов. При входе включено «Только активные»; галочку можно снять.\nЦвет полосы обозначает статус, красная рамка и знак «!» отмечают просрочку. Значения цветов есть в легенде.'
        },
        {
            selectors: ['#btnResource'], placement: 'bottom', title: 'Ресурсный план',
            text: 'Выберите месяц, сотрудника и вид деятельности, затем укажите часы или процент загрузки.\nВ этом разделе также доступны контроль заполнения, выгрузки CSV/Excel и управление периодом. Во время тура записи не создаются.'
        },
        {
            selectors: ['#btnCR'], placement: 'bottom', title: 'Запросы на изменение',
            text: 'Отдельный список документов по изменениям проектов. Можно выбрать проект, открыть документ для просмотра или скачать оригинал.'
        },
        {
            selectors: ['#btnDocCtrl'], placement: 'bottom', title: 'Контроль документов',
            text: 'Раздел помогает найти недостающие документы: статус-отчёт за месяц, документ для инициации или итоговый отчёт.\nИз результатов проверки можно перейти к карточке проекта.'
        },
        {
            selectors: ['#btnPK', '#btnDashboard'], placement: 'bottom', title: 'ПК и дашборд',
            text: 'В ПК собраны проекты для обсуждения, их оценки и комментарии. Дашборд показывает сводную картину портфеля на графиках.\nЭто два разных представления тех же проектов.'
        },
        {
            selectors: ['#btnFeedback'], placement: 'bottom', title: 'Обратная связь',
            text: 'Сообщите об ошибке, предложите улучшение или задайте вопрос через форму.\nПросмотр обращений и экспорт в Excel зависят от доступа. На тестовом стенде учитывайте предупреждение: используйте только тестовые сообщения.'
        },
        {
            selectors: ['#btnHelp'], placement: 'bottom', title: 'Подсказки всегда рядом',
            text: 'В «Справке» есть подробные объяснения разделов и условных обозначений. Этот тур можно повторить кнопкой «Пройти обучение».\nОбучение запускается только по кнопке. После завершения вернётся исходный экран.'
        }
    ];
    let index = 0, running = false, snapshot = [], scrollState = [], observer, raf = 0;
    const dialog = $('pmoTour'), bubble = $('tourBubble'), highlight = $('tourHighlight');
    function visible(el) { return el && el.getClientRects().length && getComputedStyle(el).display !== 'none'; }
    function savePresentation() {
        const ids = ['mainView','sidebarNav','portfolioView','pkView','crView','dashboardView','docCtrlView','resourceView','detailPanel'];
        const elements = [...ids.map($).filter(Boolean), ...document.querySelectorAll('.header button'), ...document.querySelectorAll('aside')];
        snapshot = [...new Set(elements)].map(el => ({ el, cls: el.getAttribute('class'), style: el.getAttribute('style') }));
        scrollState = [...document.querySelectorAll('.table-wrap,.sidebar,.header,#portfolioGanttWrap,.detail-body')]
            .map(el => ({ el, x: el.scrollLeft, y: el.scrollTop }));
        // Change presentation only. Navigation flags, selections, form values and data stay untouched.
        ['portfolioView','pkView','crView','dashboardView','docCtrlView','resourceView'].forEach(id => {
            const el = $(id); if (el) { el.classList.remove('active'); el.style.display = 'none'; }
        });
        $('mainView').style.display = '';
        $('sidebarNav').style.display = '';
        document.querySelectorAll('aside').forEach(el => el.style.display = 'none');
        document.querySelectorAll('.header button').forEach(el => el.classList.remove('active'));
        $('btnMain')?.classList.add('active');
    }
    function restorePresentation() {
        for (const { el, cls, style } of snapshot) {
            if (cls === null) el.removeAttribute('class'); else el.setAttribute('class', cls);
            if (style === null) el.removeAttribute('style'); else el.setAttribute('style', style);
        }
        for (const { el, x, y } of scrollState) el.scrollTo(x, y);
        snapshot = []; scrollState = [];
    }
    function targets(step) {
        const found = step.selectors.map(s => document.querySelector(s)).filter(visible);
        const missing = found.length === 0;
        if (missing && step.fallback) {
            const fallback = document.querySelector(step.fallback);
            if (visible(fallback)) found.push(fallback);
        }
        return { found, missing };
    }
    function layout() {
        raf = 0;
        if (!running) return;
        const step = steps[index], { found, missing } = targets(step);
        const hint = missing ? (step.empty || 'Элемент сейчас недоступен. Можно продолжить обучение.') : '';
        $('tourHint').textContent = hint; $('tourHint').hidden = !hint;
        dialog.classList.toggle('no-target', !found.length); highlight.hidden = !found.length;
        const w = innerWidth, h = innerHeight, pad = 6, margin = 12, gap = 16;
        const box = bubble.getBoundingClientRect(), bw = box.width, bh = box.height;
        if (!found.length) {
            bubble.style.left = `${Math.max(margin, (w - bw) / 2)}px`;
            bubble.style.top = `${Math.max(margin, (h - bh) / 2)}px`;
            return;
        }
        const rects = found.map(el => el.getBoundingClientRect());
        const r = {
            left: Math.max(4, Math.min(...rects.map(r => r.left)) - pad),
            top: Math.max(4, Math.min(...rects.map(r => r.top)) - pad),
            right: Math.min(w - 4, Math.max(...rects.map(r => r.right)) + pad),
            bottom: Math.min(h - 4, Math.max(...rects.map(r => r.bottom)) + pad)
        };
        if (r.right <= r.left || r.bottom <= r.top) {
            highlight.hidden = true; dialog.classList.add('no-target');
            bubble.style.left = `${Math.max(margin, (w - bw) / 2)}px`;
            bubble.style.top = `${Math.max(margin, (h - bh) / 2)}px`;
            return;
        }
        Object.assign(highlight.style, { left:r.left+'px', top:r.top+'px', width:(r.right-r.left)+'px', height:(r.bottom-r.top)+'px' });
        const clampX = x => Math.max(margin, Math.min(x, w - bw - margin));
        const clampY = y => Math.max(margin, Math.min(y, h - bh - margin));
        const candidates = {
            right: { x:r.right+gap, y:clampY((r.top+r.bottom-bh)/2) },
            left: { x:r.left-bw-gap, y:clampY((r.top+r.bottom-bh)/2) },
            bottom: { x:clampX((r.left+r.right-bw)/2), y:r.bottom+gap },
            top: { x:clampX((r.left+r.right-bw)/2), y:r.top-bh-gap }
        };
        const order = [step.placement, 'bottom', 'right', 'left', 'top'];
        const fit = order.map(p => candidates[p]).find(p => p.x >= margin && p.y >= margin && p.x+bw <= w-margin && p.y+bh <= h-margin);
        const point = fit || { x:clampX((w-bw)/2), y:clampY(h-bh-margin) };
        bubble.style.left = point.x+'px'; bubble.style.top = point.y+'px';
    }
    function schedule() { if (running && !raf) raf = requestAnimationFrame(layout); }
    function show(next) {
        index = Math.max(0, Math.min(next, steps.length - 1));
        const step = steps[index];
        $('tourStep').textContent = `Шаг ${index + 1} из ${steps.length}`;
        $('tourTitle').textContent = step.title;
        $('tourText').textContent = step.text;
        $('tourBack').disabled = index === 0;
        $('tourNext').textContent = index === steps.length - 1 ? 'Готово' : 'Далее';
        $('tourProgress').replaceChildren(...steps.map((_, i) => {
            const dot = document.createElement('span'); dot.className = i === index ? 'current' : i < index ? 'complete' : '';
            return dot;
        }));
        targets(step).found.forEach(el => el.scrollIntoView({ block:'nearest', inline:'nearest', behavior:'instant' }));
        layout();
        $('tourNext').focus({ preventScroll:true });
    }
    function start() {
        if (running || document.querySelector('dialog[open]')) return;
        savePresentation();
        $('helpModal').classList.add('hidden');
        running = true;
        dialog.showModal();
        observer = new MutationObserver(schedule);
        observer.observe(document.querySelector('.app'), { childList:true, subtree:true });
        show(0);
    }
    function end() {
        if (!running) return;
        running = false; observer?.disconnect(); cancelAnimationFrame(raf); raf = 0;
        dialog.close(); restorePresentation();
        $('btnHelp')?.focus({ preventScroll:true });
    }
    function next() { if (index === steps.length - 1) end(); else show(index + 1); }
    $('btnStartTour').addEventListener('click', start);
    $('tourNext').addEventListener('click', next);
    $('tourBack').addEventListener('click', () => show(index - 1));
    $('tourSkip').addEventListener('click', end);
    $('tourClose').addEventListener('click', end);
    dialog.addEventListener('cancel', event => { event.preventDefault(); end(); });
    dialog.addEventListener('keydown', event => {
        if (event.key === 'ArrowRight') { event.preventDefault(); next(); }
        if (event.key === 'ArrowLeft') { event.preventDefault(); show(index - 1); }
    });
    window.addEventListener('resize', schedule);
    document.addEventListener('scroll', schedule, true);
    window.PmoTour = { start, close:end };
})();
