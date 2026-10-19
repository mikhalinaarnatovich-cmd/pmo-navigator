-- Synthetic fixtures; run ONLY against the isolated pmo_master_qa database.
DO $$ BEGIN IF current_database() <> 'pmo_master_qa' THEN RAISE EXCEPTION 'Wrong database'; END IF; END $$;
DELETE FROM dbo.resourceallocationaudits WHERE employeename LIKE 'QA ·%';
DELETE FROM dbo.resourceallocations WHERE employeename LIKE 'QA ·%';
INSERT INTO dbo.employees(fullname,department,sector,managerfullname,employeetype,rate,isactive)
VALUES
 ('QA · Четверть','QA основная','QA сектор','QA руководитель','Аналитик',0.25,true),
 ('QA · Без назначений','QA основная','QA сектор','QA руководитель','Аналитик',1,true),
 ('QA · Старый формат','QA основная','QA сектор','QA руководитель','Разработчик',0.25,true),
 ('QA · Параллельно','QA основная','QA сектор','QA руководитель','Разработчик',1,true),
 ('QA · Неизвестная ставка','QA неизвестная',null,null,null,0,true),
 ('QA · Другой отдел','QA другой',null,'QA другой руководитель','Аналитик',1,true)
ON CONFLICT(fullname) DO UPDATE SET rate=excluded.rate,department=excluded.department,sector=excluded.sector,employeetype=excluded.employeetype,managerfullname=excluded.managerfullname,isactive=true;
INSERT INTO dbo.workcalendars(year,month,workinghours) VALUES(2026,10,160)
ON CONFLICT(year,month) DO UPDATE SET workinghours=160;
INSERT INTO dbo.resourceallocations(employeename,kind,activityname,periodstart,allocationpercent)
VALUES ('QA · Старый формат','Operational','Операционная деятельность','2026-10-01',12.5);
INSERT INTO dbo.resourceallocations(employeename,kind,projectid,projectnamesnapshot,periodstart,allocationpercent,employeeallocationpercent,ratesnapshot)
VALUES ('QA · Другой отдел','Project',630,'Тестовый проект 630','2026-10-01',30,30,1);
