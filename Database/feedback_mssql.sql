-- Для установки, использующей MS SQL вместо PostgreSQL.
-- Выполнить один раз в существующей базе Навигатора, если выбран MS SQL.
IF OBJECT_ID(N'dbo.feedback', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.feedback (
        feedbackid BIGINT IDENTITY(1,1) PRIMARY KEY,
        requestid UNIQUEIDENTIFIER NOT NULL UNIQUE,
        createdat DATETIMEOFFSET NOT NULL,
        kind NVARCHAR(20) NOT NULL,
        message NVARCHAR(5000) NOT NULL,
        section NVARCHAR(100) NOT NULL,
        authorname NVARCHAR(256) NOT NULL,
        authorlogin NVARCHAR(256) NULL,
        isanonymous BIT NOT NULL
    );
    CREATE INDEX ix_feedback_createdat ON dbo.feedback (createdat);
END;
