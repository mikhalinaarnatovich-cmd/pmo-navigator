-- Additive update for installations using MS SQL, not PostgreSQL.
IF COL_LENGTH('dbo.employees','employeetype') IS NULL
    ALTER TABLE dbo.employees ADD employeetype NVARCHAR(100) NULL;
IF COL_LENGTH('dbo.resourceallocations','employeeallocationpercent') IS NULL
    ALTER TABLE dbo.resourceallocations ADD employeeallocationpercent DECIMAL(12,6) NULL;
IF COL_LENGTH('dbo.resourceallocations','ratesnapshot') IS NULL
    ALTER TABLE dbo.resourceallocations ADD ratesnapshot DECIMAL(8,4) NULL;
IF COL_LENGTH('dbo.resourceallocations','projectnamesnapshot') IS NULL
    ALTER TABLE dbo.resourceallocations ADD projectnamesnapshot NVARCHAR(1024) NULL;
