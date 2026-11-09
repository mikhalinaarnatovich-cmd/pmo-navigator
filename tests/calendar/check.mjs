import assert from 'node:assert/strict';
import fs from 'node:fs';
import {execFileSync} from 'node:child_process';
import {randomUUID} from 'node:crypto';
const base='http://127.0.0.1:5088/pmo-nav/api/resource-plan';
const sql=query=>execFileSync('psql',['-X','-v','ON_ERROR_STOP=1','-h','127.0.0.1','-U','pmo_feedback_qa','-d','pmo_master_qa','-At','-c',query],
 {env:{...process.env,PGPASSWORD:'local-feedback-qa'},encoding:'utf8'}).trim();
assert.equal(sql('select current_database()'),'pmo_master_qa');
sql("DELETE FROM dbo.resourceallocationaudits WHERE employeename LIKE 'QC %'; DELETE FROM dbo.resourceallocations WHERE employeename LIKE 'QC %'; DELETE FROM dbo.vacationperiods WHERE employeename LIKE 'QC %'; DELETE FROM dbo.periodlocks WHERE groupvalue='QC calendar'; INSERT INTO dbo.employees(fullname,department,rate,isactive,employeetype) VALUES ('QC quarter','QC calendar',0.25,true,'QC type'),('QC other','QC calendar',1,true,'QC type'),('QC unknown','QC calendar',0,true,'QC type') ON CONFLICT(fullname) DO UPDATE SET rate=excluded.rate, department=excluded.department,employeetype=excluded.employeetype,isactive=true;");
async function api(path='',body,method=body?'POST':'GET') {
 const r=await fetch(base+path,{method,headers:body?{'Content-Type':'application/json'}:{},body:body?JSON.stringify(body):undefined});
 return {status:r.status,body:await r.json()};
}
const original=fs.readFileSync('/tmp/pmo-calendar-qa.csv','utf8');
const draft=(startDate='2026-11-01',endDate='2026-11-03',employeeName='QC quarter')=>({id:randomUUID(),revision:0,employeeName,startDate,endDate,comment:null});
const ok=r=>assert.equal(r.status,200,JSON.stringify(r.body));
const bad=r=>assert.equal(r.status,400,JSON.stringify(r.body));
try {
 let d=draft(), r=await api('/vacations/calculate',d); ok(r);
 assert.equal(r.body.parts[0].standardHours,15); assert.equal(r.body.parts[0].monthHours,151); assert.equal(r.body.parts[0].hours,3.75);
 assert.equal(r.body.parts[0].percent,9.933775);
 r=await api('/vacations/calculate',draft('2026-11-07','2026-11-07')); ok(r); assert.equal(r.body.parts[0].hours,2); // working Saturday
 r=await api('/vacations/calculate',draft('2026-11-02','2026-11-02')); ok(r); assert.equal(r.body.parts[0].hours,0);
 r=await api('/vacations/calculate',draft('2026-11-01','2026-11-01')); ok(r); assert.equal(r.body.parts[0].hours,1.75);
 r=await api('/vacations/calculate',draft('2026-12-20','2027-01-02')); ok(r); assert.deepEqual(r.body.parts.map(x=>x.hours),[2,4]);
 bad(await api('/vacations/calculate',draft('2027-02-01','2027-02-02')));
 bad(await api('/vacations/calculate',draft('2026-11-03','2026-11-01')));
 bad(await api('/vacations/calculate',draft('2026-11-01','2026-11-03','QC unknown')));
 const pre=await api('/vacations/calculate',d); fs.appendFileSync('/tmp/pmo-calendar-qa.csv','\n');
 bad(await api('/vacations',{...d,calendarVersion:pre.body.calendarVersion}));
 fs.writeFileSync('/tmp/pmo-calendar-qa.csv',original);
 for (const malformed of [
   original+'2026-11-01|8\n',
   original.replace('2026-11-01|7','2026-11-01|-1'),
   original.replace('2026-11-04|8\n',''),
   original.replace('2026-11-01|7','2026-99-01|7')
 ]) { fs.writeFileSync('/tmp/pmo-calendar-qa.csv',malformed); bad(await api('/vacations/calculate',d)); }
 fs.writeFileSync('/tmp/pmo-calendar-qa.csv',original+'2026-11-01|7\n');
 r=await api('/vacations/calculate',d);ok(r);assert.equal(r.body.parts[0].monthHours,151);
 fs.writeFileSync('/tmp/pmo-calendar-qa.csv',original);
 r=await api('/vacations',d);ok(r);assert.equal(r.body.revision,1);
 ok(await api('/vacations',d));assert.equal(sql(`SELECT count(*) FROM dbo.resourceallocations WHERE vacationperiodid='${d.id}'`),'1');
 bad(await api('/vacations',draft('2026-11-03','2026-11-04')));
 const id=sql(`SELECT resourceallocationid FROM dbo.resourceallocations WHERE vacationperiodid='${d.id}'`);
 bad(await api('/'+id,undefined,'DELETE'));
 bad(await api('',{employeeName:'QC quarter',kind:'Vacation',activityName:'Отпуск 01.11.2026 - 03.11.2026',periodStart:'2026-11-01',allocationPercent:5}));
 r=await api('/vacations',{...d,revision:0,endDate:'2026-11-04'});assert.equal(r.status,409);
 r=await api('/vacations',{...d,revision:1,endDate:'2026-11-04'});ok(r);assert.equal(r.body.revision,2);
 ok(await api('/vacations',{...d,revision:1,endDate:'2026-11-04'})); // response lost after update
 sql("INSERT INTO dbo.periodlocks(periodstart,grouptype,groupvalue,isopen,updatedat) VALUES('2026-11-01','Department','QC calendar',false,now())");
 bad(await api('/vacations',{...d,revision:2,startDate:'2026-12-01',endDate:'2026-12-02'}));
 bad(await api(`/vacations/${d.id}?revision=2`,undefined,'DELETE'));
 sql("DELETE FROM dbo.periodlocks WHERE groupvalue='QC calendar'");
 ok(await api(`/vacations/${d.id}?revision=2`,undefined,'DELETE'));
 ok(await api(`/vacations/${d.id}?revision=2`,undefined,'DELETE'));
 assert.equal(sql(`SELECT count(*) FROM dbo.resourceallocations WHERE vacationperiodid='${d.id}'`),'0');
 const manual={employeeName:'QC quarter',kind:'Operational',activityName:'Операционная деятельность',periodStart:'2026-11-01',allocationPercent:95};
 ok(await api('',manual));
 bad(await api('/vacations',draft()));
 assert.equal(sql("SELECT count(*) FROM dbo.vacationperiods WHERE employeename='QC quarter' AND NOT cancelled"),'0');
 sql("DELETE FROM dbo.resourceallocations WHERE employeename='QC quarter'");
 ok(await api('',{...manual,kind:'Vacation',activityName:'Отпуск',allocationPercent:10}));
 bad(await api('/vacations',draft()));
 sql("DELETE FROM dbo.resourceallocations WHERE employeename='QC quarter'");
 sql("INSERT INTO dbo.periodlocks(periodstart,grouptype,groupvalue,isopen,updatedat) VALUES('2026-12-01','Department','QC calendar',false,now())");
 const cross=draft('2026-11-20','2026-12-02');bad(await api('/vacations',cross));
 assert.equal(sql(`SELECT count(*) FROM dbo.vacationperiods WHERE id='${cross.id}'`),'0');
 sql("DELETE FROM dbo.periodlocks WHERE groupvalue='QC calendar'");
 ok(await api('/vacations',cross));
 assert.equal(sql(`SELECT count(*) FROM dbo.resourceallocations WHERE vacationperiodid='${cross.id}'`),'2');
 const race=await Promise.all([api('/vacations',draft('2026-11-01','2026-11-10','QC other')),api('/vacations',draft('2026-11-03','2026-11-12','QC other'))]);
 assert.equal(race.filter(x=>x.status===200).length,1,JSON.stringify(race));
 r=await api('?period=2026-11-01&employeeType=QC%20type');ok(r);assert.equal(r.body.employees.length,3);assert.equal(r.body.canManagePeriods,false);
 r=await api('/locks',{periodStart:'2026-11-01',groupType:'All',groupValues:['*'],isOpen:false});assert.equal(r.status,403);
 const access=await fetch(base.replace('/resource-plan','/feedback/access')).then(r=>r.json()); assert.equal(access.canReview,false);
 for(const path of ['','/export/excel']) { const r=await fetch(base.replace('/resource-plan','/feedback')+path);assert.equal(r.status,403); }
 sql("INSERT INTO dbo.resourceallocations(employeename,kind,activityname,periodstart,allocationpercent) VALUES('QC quarter','Operational','legacy-calendar-test','2026-10-01',12.5)");
 r=await api('/history?employee=QC%20quarter');ok(r);
 assert.equal(r.body.find(x=>x.periodStart==='2026-10-01').plannedHours,20);
 fs.writeFileSync('/tmp/pmo-calendar-qa.csv','ДатаКалендаряСПраздниками|СтандартныеЧасыРаботы\n');
 r=await api('?period=2026-10-01');ok(r);assert.equal(r.body.calendarHours,null);
 assert.equal(r.body.items.find(x=>x.employeeName==='QC quarter').plannedHours,null);
 r=await api('/history?employee=QC%20quarter');ok(r);
 assert.equal(r.body.find(x=>x.periodStart==='2026-10-01').plannedHours,null);
 const csvResponse=await fetch(base+'/export/csv?from=2026-10-01&to=2026-10-01');
 assert.equal(csvResponse.status,200);
 const csvText=new TextDecoder('windows-1251').decode(await csvResponse.arrayBuffer());
 assert.equal(csvText.split('\n').find(x=>x.includes('QC quarter')).split(';')[9],'');
 console.log('PASS calendar: date/hour parsing, shortened/zero/working-Saturday, year/month split, rate once, missing/duplicate/changed calendar, CRUD/retry/version, overlap/race, legacy-row protection, locks/cap/atomicity, type filter, anonymous role denials.');
} finally { fs.writeFileSync('/tmp/pmo-calendar-qa.csv',original); }
