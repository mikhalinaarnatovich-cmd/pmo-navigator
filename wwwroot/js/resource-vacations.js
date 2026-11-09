/* Daily calendar dates are plain ISO dates, not browser-local instants. */
function resourceSearchOptions(type) {
    const employee = type === 'employee';
    const id = employee ? 'resourceEmployee' : 'resourceProject';
    const select = document.getElementById(id), previous = select.value;
    const query = document.getElementById(id + 'Search').value.trim().toLocaleLowerCase('ru');
    const rows = (employee ? resourceEmployees : resourceProjects).filter(x =>
        (employee ? x.name : `${x.projectId} ${x.name}`).toLocaleLowerCase('ru').includes(query));
    select.replaceChildren(new Option(rows.length ? 'Выберите ' + (employee ? 'сотрудника' : 'проект') : 'Ничего не найдено', ''));
    rows.forEach(x => select.add(new Option(employee ? x.name : `#${x.projectId} ${x.name}`, employee ? x.name : x.projectId)));
    select.value = previous;
    if (select.selectedIndex < 0) select.value = '';
    if (employee && select.value !== previous) select.dispatchEvent(new Event('change'));
}
window.ResourceVacation = (() => {
    const $ = id => document.getElementById(id);
    const uuid = () => {
        const b = crypto.getRandomValues(new Uint8Array(16));
        b[6] = (b[6] & 15) | 64; b[8] = (b[8] & 63) | 128;
        const s = [...b].map(x => x.toString(16).padStart(2, '0')).join('');
        return `${s.slice(0,8)}-${s.slice(8,12)}-${s.slice(12,16)}-${s.slice(16,20)}-${s.slice(20)}`;
    };
    let id = uuid(), revision = 0, calendarVersion = null, listSerial = 0, calcSerial = 0;
    const active = () => resourceCurrentKind === 'Vacation' && $('vacationByDates').checked;
    const message = text => { $('vacationCalculation').textContent = text; };
    const date = iso => iso.split('-').reverse().join('.');
    const number = n => Number(n).toLocaleString('ru', { maximumFractionDigits: 2 });
    const body = () => ({
        id, revision, employeeName: $('resourceEmployee').value,
        startDate: $('vacationStart').value, endDate: $('vacationEnd').value,
        comment: $('resourceComment').value.trim() || null, calendarVersion
    });
    async function request(path, method = 'GET', payload) {
        const response = await fetch(resourceApiUrl('/vacations' + path), {
            method, headers: payload ? { 'Content-Type': 'application/json' } : {},
            body: payload ? JSON.stringify(payload) : undefined
        });
        const data = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(data.message || 'Не удалось выполнить действие. Обновите данные и повторите.');
        return data;
    }
    function reset() {
        id = uuid(); revision = 0; calendarVersion = null; calcSerial++;
        $('vacationStart').value = ''; $('vacationEnd').value = '';
        message('Новый отпуск. Выберите даты начала и окончания.');
    }
    async function list() {
        const employee = $('resourceEmployee').value, serial = ++listSerial;
        $('vacationList').textContent = employee ? 'Загрузка…' : 'Выберите сотрудника.';
        if (!employee) return [];
        try {
            const rows = await request('?employee=' + encodeURIComponent(employee));
            if (serial !== listSerial) return [];
            $('vacationList').replaceChildren();
            if (!rows.length) $('vacationList').textContent = 'Отпусков с датами пока нет. Старые записи без дат остаются в истории.';
            rows.forEach(row => {
                const div = document.createElement('div'); div.className = 'vacation-entry';
                const label = document.createElement('span');
                label.textContent = `${date(row.startDate)} – ${date(row.endDate)}: ${number(row.hours)} ч.`;
                const edit = document.createElement('button'); edit.type = 'button'; edit.textContent = 'Изменить даты';
                edit.onclick = () => fill(row);
                const cancel = document.createElement('button'); cancel.type = 'button'; cancel.textContent = 'Удалить весь отпуск';
                cancel.onclick = async () => {
                    if (!confirm('Удалить этот отпуск во всех затронутых месяцах?')) return;
                    cancel.disabled = true;
                    try {
                        await request(`/${row.id}?revision=${row.revision}`, 'DELETE');
                        if (id === row.id) reset();
                        await list(); await resourceLoadPlan();
                        message('Отпуск удалён во всех месяцах.');
                    } catch (e) { message(e.message); cancel.disabled = false; }
                };
                div.append(label, edit, cancel); $('vacationList').append(div);
            });
            return rows;
        } catch (e) { if (serial === listSerial) $('vacationList').textContent = e.message; return []; }
    }
    function fill(row) {
        id = row.id; revision = row.revision; calendarVersion = null; calcSerial++;
        $('vacationByDates').checked = true;
        $('vacationStart').value = row.startDate; $('vacationEnd').value = row.endDate;
        $('resourceComment').value = row.comment || '';
        refreshMode();
        message('Изменение существующего отпуска. Проверяются и новые, и прежние месяцы.');
        $('vacationStart').focus();
    }
    function refreshMode() {
        $('vacationDatesPanel').hidden = resourceCurrentKind !== 'Vacation';
        for (const field of ['resourceHours', 'resourcePercent'])
        {
            $(field).disabled = active();
            $(field).placeholder = active() ? 'По датам' : (field === 'resourceHours' ? 'Часы' : '25');
        }
        for (const field of ['vacationStart','vacationEnd','vacationCalculate'])
            $(field).disabled = !active();
        if (resourceCurrentKind === 'Vacation') list();
    }
    function validate() {
        if (!$('resourceEmployee').value) throw new Error('Выберите сотрудника.');
        if (!$('vacationStart').value || !$('vacationEnd').value) throw new Error('Укажите обе даты отпуска.');
    }
    async function calculate() {
        const serial = ++calcSerial;
        try {
            validate(); calendarVersion = null;
            const result = await request('/calculate', 'POST', body());
            if (serial !== calcSerial) return;
            calendarVersion = result.calendarVersion;
            message(result.parts.map(p => `${p.month.slice(0,7)}: ${number(p.hours)} ч.; ${number(p.percent)}% от ставки ${number(result.rate)}.`).join('\n')
                + '\nПри сохранении дополнительно проверяются общая загрузка, пересечения и закрытые месяцы.');
        } catch (e) { if (serial === calcSerial) message(e.message); }
    }
    async function save() {
        const button = $('btnResourceSave'); button.disabled = true;
        try {
            validate();
            const submitted = body();
            const result = await request('', 'POST', submitted);
            if (id === submitted.id) {
                revision = result.revision;
                $('resourcePeriod').value = submitted.startDate.slice(0,7);
                message('Отпуск сохранён целиком. Часы и проценты рассчитаны на сервере по календарю и ставке.');
            }
            await list(); await resourceLoadPlan();
        } catch (e) { message(e.message); }
        finally { button.disabled = false; }
    }
    async function editById(employee, target) {
        $('resourceEmployeeSearch').value = ''; resourceSearchOptions('employee');
        $('resourceEmployee').value = employee; reset(); resourceSetKind('Vacation');
        const rows = await list(); const row = rows.find(x => x.id === target);
        if (row) fill(row);
        $('vacationDatesPanel').scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
    $('resourceEmployee').addEventListener('change', () => { reset(); if (resourceCurrentKind === 'Vacation') list(); });
    $('vacationByDates').addEventListener('change', refreshMode);
    $('vacationCalculate').addEventListener('click', calculate);
    $('vacationNew').addEventListener('click', reset);
    for (const field of ['vacationStart', 'vacationEnd'])
        $(field).addEventListener('change', () => { calcSerial++; calendarVersion = null; message('Даты изменены. Нажмите «Рассчитать» или «Сохранить».'); });
    return { active, refreshMode, save, editById };
})();
