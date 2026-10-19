(() => {
    'use strict';
    const $ = id => document.getElementById(id);
    let base = '', data, loadId = 0;
    const fmt = (v, digits = 6) => v == null ? '—' :
        (Math.abs(Number(v)) < 1e-10 ? 0 : Number(v)).toLocaleString('ru-RU', { maximumFractionDigits:digits });
    const month = v => new Date(`${String(v).slice(0,7)}-01T12:00:00`).toLocaleDateString('ru-RU', { month:'short', year:'numeric' });
    const keyMonth = v => String(v).slice(0,7);
    function sum(values) { return values.some(v => v == null) ? null : values.reduce((a,b) => a + Number(b), 0); }
    function cell(row, text, tag = 'td') { const e = document.createElement(tag); e.textContent = text; row.appendChild(e); return e; }
    function header(table) {
        table.replaceChildren();
        const row = table.createTHead().insertRow(); cell(row, 'Сотрудник / подразделение', 'th');
        for (const p of data.periods) cell(row, month(p), 'th').classList.toggle('mp-selected', keyMonth(p) === $('masterSort').value);
        return table.createTBody();
    }
    function totalsRow(body, title, employees, cls) {
        const row = body.insertRow(); row.className = cls; cell(row, title, 'th');
        for (let i = 0; i < data.periods.length; i++) {
            const value = sum(employees.map(e => e.cells[i].freeFte));
            const td = cell(row, fmt(value));
            if (value < -0.000001) td.classList.add('mp-over');
        }
    }
    function render() {
        if (!data) return;
        const employees = data.employees, selected = $('masterSort').value;
        const index = data.periods.findIndex(p => keyMonth(p) === selected);
        const cells = employees.map(e => e.cells[Math.max(0,index)]);
        const cards = [
            ['Сотрудников в выборке', employees.length],
            ['Доступно ставок, FTE', sum(cells.map(c => c.rate))],
            ['Запланировано, FTE', sum(cells.map(c => c.plannedFte))],
            ['Свободно, FTE', sum(cells.map(c => c.freeFte))]
        ];
        $('masterCards').replaceChildren(...cards.map(([label,value]) => {
            const card = document.createElement('div'); card.className = 'mp-card';
            const title = document.createElement('small'); title.textContent = label;
            const number = document.createElement('strong'); number.textContent = fmt(value);
            if (value < 0) number.style.color = '#991B1B';
            card.append(title, number); return card;
        }));
        $('masterCardsPeriod').textContent = `Итоги за ${month(data.periods[Math.max(0,index)])}.`;
        const body = header($('masterResources'));
        const types = new Map();
        for (const e of employees) {
            const t = e.employeeType || 'Тип специалиста не указан';
            if (!types.has(t)) types.set(t, []);
            types.get(t).push(e);
        }
        for (const [type, people] of types) {
            totalsRow(body, type, people, 'mp-type');
            const units = new Map();
            for (const e of people) {
                const unit = [e.department || 'Отдел не указан', e.sector && e.sector !== e.department ? e.sector : ''].filter(Boolean).join(' / ');
                if (!units.has(unit)) units.set(unit, []); units.get(unit).push(e);
            }
            for (const [unit, members] of units) {
                totalsRow(body, unit, members, 'mp-dept');
                for (const e of members) {
                    const row = body.insertRow(); row.className = 'mp-person'; cell(row, e.name);
                    for (const c of e.cells) {
                        const td = cell(row, fmt(c.freeFte));
                        td.className = c.freeFte == null ? '' : c.freeFte < -0.000001 ? 'mp-over' : c.freeFte > 0.000001 ? 'mp-free' : 'mp-zero';
                        td.title = `Ставка: ${fmt(c.rate)}; план: ${fmt(c.percent,2)}%; занято: ${fmt(c.plannedFte)} FTE` +
                            (c.rateConflict ? '. Ставка в назначениях отличается от текущей.' : '');
                    }
                }
            }
        }
        if (!employees.length) { const tr=body.insertRow(); const td=cell(tr,'Сотрудников по выбранным фильтрам нет.'); td.colSpan=data.periods.length+1; }
        renderProjects();
        const warnings = [];
        if (data.missingCalendar.length) warnings.push('Нет нормы календаря за: ' + data.missingCalendar.map(month).join(', ') + '. Часы без нормы показаны прочерком.');
        if (data.unassignedRecords) warnings.push(`Назначений без активной записи в справочнике: ${data.unassignedRecords}. Они не добавлены в свободную ёмкость сотрудников.`);
        if (employees.some(e=>e.cells.some(c=>c.rate==null))) warnings.push('Есть сотрудники без ставки: соответствующие итоги свободных ресурсов не рассчитываются.');
        if (employees.some(e=>e.cells.some(c=>c.rateConflict))) warnings.push('Есть расхождения между ставкой в назначениях и справочнике. Проверьте историю изменения ставок.');
        $('masterWarnings').textContent = warnings.join(' ');
    }
    function renderProjects() {
        const metric = $('masterUnit').value, chosen = $('masterSort').value, selectedEmployee = $('masterEmployee').value;
        const assignments = data.assignments.filter(a => !selectedEmployee || a.employee === selectedEmployee);
        const groups = new Map();
        for (const a of assignments) {
            const key = a.kind === 'Project' ? `Project:${a.projectId}` : `${a.kind}:${a.activity}`;
            if (!groups.has(key)) groups.set(key, { name:a.activity, kind:a.kind, projectId:a.projectId, rows:[] });
            groups.get(key).rows.push(a);
        }
        const value = (group,p) => sum(group.rows.filter(a=>keyMonth(a.period)===p).map(a=>a[metric]));
        const list = [...groups.values()].sort((a,b)=>
            (value(b,chosen) ?? -Infinity) - (value(a,chosen) ?? -Infinity) || a.name.localeCompare(b.name,'ru'));
        const table = $('masterProjects'); table.replaceChildren();
        const head = table.createTHead().insertRow();
        cell(head,'Проект / активность','th');
        for (const p of data.periods) cell(head,`${month(p)} · ${metric==='fte'?'FTE':'часы'}`,'th').classList.toggle('mp-selected',keyMonth(p)===chosen);
        const body = table.createTBody();
        for (const group of list) {
            const row=body.insertRow(), label=cell(row,'');
            const details=document.createElement('details'), summary=document.createElement('summary');
            summary.textContent=(group.kind==='Project'?`#${group.projectId} · `:'')+group.name; details.append(summary);
            const names=[...new Set(group.rows.map(a=>a.employee))].sort((a,b)=>a.localeCompare(b,'ru'));
            for(const name of names){
                const entries=group.rows.filter(a=>a.employee===name&&keyMonth(a.period)===chosen);
                if(!entries.length)continue;
                const p=document.createElement('p');
                p.textContent=`${name}: ${fmt(sum(entries.map(a=>a[metric])),metric==='fte'?6:2)} ${metric==='fte'?'FTE':'ч.'}`;
                details.append(p);
            }
            label.append(details);
            for(const p of data.periods) cell(row,fmt(value(group,keyMonth(p)),metric==='fte'?6:2));
        }
        if(!list.length){const r=body.insertRow();const td=cell(r,'Плановых назначений по выбранным фильтрам нет.');td.colSpan=data.periods.length+1;}
        $('masterSortNote').textContent=`По убыванию ${metric==='fte'?'FTE':'часов'} за ${month(chosen)}. Нажмите название, чтобы увидеть сотрудников за этот месяц.`;
    }
    function fill(id, values) {
        const selected=$(id).value; $(id).replaceChildren();
        const all=document.createElement('option'); all.value=''; all.textContent='Все'; $(id).append(all);
        for(const text of values){const o=document.createElement('option');o.value=text;o.textContent=text;$(id).append(o);}
        if(values.includes(selected))$(id).value=selected;
    }
    async function load() {
        const version=++loadId;
        const from=$('masterFrom').value,to=$('masterTo').value;
        if(!from||!to){$('masterMessage').textContent='Выберите начало и конец периода.';return;}
        $('masterMessage').className='mp-note';$('masterMessage').textContent='Загрузка мастер-плана…';
        const params=new URLSearchParams({from:from+'-01',to:to+'-01'});
        for(const [id,name] of [['masterDept','department'],['masterSector','sector'],['masterManager','manager'],['masterType','employeeType']])
            if($(id).value)params.set(name,$(id).value);
        try{
            const response=await fetch(`${base}/api/resource-plan/master?${params}`,{cache:'no-store'});
            if(!response.ok){const error=await response.json().catch(()=>({}));throw new Error(error.message||'Не удалось получить мастер-план. Проверьте подключение к базе.');}
            const result=await response.json(); if(version!==loadId)return;data=result;
            fill('masterDept',data.filters.departments);fill('masterSector',data.filters.sectors);
            fill('masterManager',data.filters.managers);fill('masterType',data.filters.employeeTypes);
            fill('masterEmployee',data.employees.map(e=>e.name));
            const previous=$('masterSort').value; $('masterSort').replaceChildren();
            for(const p of data.periods){const o=document.createElement('option');o.value=keyMonth(p);o.textContent=month(p);$('masterSort').append(o);}
            if(data.periods.some(p=>keyMonth(p)===previous))$('masterSort').value=previous;
            $('masterMessage').textContent='';render();
        }catch(e){
            if(version!==loadId)return;
            data=null;$('masterCards').replaceChildren();$('masterResources').replaceChildren();$('masterProjects').replaceChildren();
            $('masterWarnings').textContent='';$('masterMessage').className='mp-note mp-error';$('masterMessage').textContent=e.message;
        }
    }
    function open(prefix){
        base=String(prefix||'').replace(/\/+$/,'');
        if(!$('masterFrom').value){
            const source=$('resourcePeriod').value||new Date().toISOString().slice(0,7);
            $('masterFrom').value=source;const d=new Date(source+'-01T12:00:00');d.setMonth(d.getMonth()+2);
            $('masterTo').value=`${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}`;
        }
        $('masterDialog').showModal();load();
    }
    $('masterClose').addEventListener('click',()=>$('masterDialog').close());
    $('masterLoad').addEventListener('click',load);
    $('masterSort').addEventListener('change',render);
    $('masterUnit').addEventListener('change',()=>data&&renderProjects());
    $('masterEmployee').addEventListener('change',()=>data&&renderProjects());
    window.ResourceMaster={open};
})();
