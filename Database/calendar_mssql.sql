-- Apply after the existing resource schema, with a backup and the app stopped.
IF OBJECT_ID('dbo.vacationperiods','U') IS NULL
BEGIN
 CREATE TABLE dbo.vacationperiods (
  id uniqueidentifier NOT NULL PRIMARY KEY, employeename nvarchar(256) NOT NULL,
  startdate date NOT NULL, enddate date NOT NULL, ratesnapshot decimal(8,4) NOT NULL,
  calendarversion nvarchar(64) NOT NULL, comment nvarchar(2000) NULL,
  revision int NOT NULL, cancelled bit NOT NULL, updatedat datetime2 NOT NULL,
  updatedby nvarchar(256) NULL
 );
 CREATE INDEX ix_vacationperiods_employee_dates ON dbo.vacationperiods(employeename,startdate,enddate);
END;
IF COL_LENGTH('dbo.resourceallocations','vacationperiodid') IS NULL
BEGIN
 ALTER TABLE dbo.resourceallocations ADD vacationperiodid uniqueidentifier NULL;
 CREATE INDEX ix_resourceallocations_vacation ON dbo.resourceallocations(vacationperiodid);
END;
