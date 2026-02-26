-- =====================================================
-- DbSync Central Repository
-- Ejecutar en el SQL Server que alojará el repositorio central
-- =====================================================

CREATE DATABASE DbSyncCentral;
GO

USE DbSyncCentral;
GO

-- Log de ejecuciones del scanner
CREATE TABLE dbo.ScanLogs (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    StartedAt       DATETIME2 NOT NULL,
    CompletedAt     DATETIME2 NULL,
    Status          VARCHAR(30) NOT NULL DEFAULT 'Running',       -- Running, Completed, CompletedWithErrors, Failed
    TriggerType     VARCHAR(20) NOT NULL DEFAULT 'Scheduled',     -- Scheduled, Manual, OnDemand
    TriggeredBy     NVARCHAR(100) NULL,
    TotalClientes   INT NOT NULL DEFAULT 0,
    TotalAmbientes  INT NOT NULL DEFAULT 0,
    TotalObjectsScanned   INT NOT NULL DEFAULT 0,
    TotalChangesDetected  INT NOT NULL DEFAULT 0,
    TotalErrors     INT NOT NULL DEFAULT 0,
    ErrorSummary    NVARCHAR(MAX) NULL
);

CREATE INDEX IX_ScanLogs_StartedAt ON dbo.ScanLogs(StartedAt DESC);
GO

-- Detalle por cliente/ambiente dentro de un scan
CREATE TABLE dbo.ScanLogEntries (
    Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    ScanLogId       INT NOT NULL REFERENCES dbo.ScanLogs(Id),
    ClienteId       INT NOT NULL,
    ClienteCodigo   VARCHAR(50) NOT NULL,
    Ambiente        VARCHAR(10) NOT NULL,
    StartedAt       DATETIME2 NOT NULL,
    CompletedAt     DATETIME2 NULL,
    Success         BIT NOT NULL DEFAULT 1,
    ObjectsFound    INT NOT NULL DEFAULT 0,
    ObjectsNew      INT NOT NULL DEFAULT 0,
    ObjectsModified INT NOT NULL DEFAULT 0,
    ObjectsDeleted  INT NOT NULL DEFAULT 0,
    ErrorMessage    NVARCHAR(MAX) NULL,
    DurationSeconds FLOAT NOT NULL DEFAULT 0
);

CREATE INDEX IX_ScanLogEntries_ScanLogId ON dbo.ScanLogEntries(ScanLogId);
CREATE INDEX IX_ScanLogEntries_ClienteId ON dbo.ScanLogEntries(ClienteId, Ambiente);
GO

-- Snapshots de objetos (tabla principal, SIN definición)
CREATE TABLE dbo.ObjectSnapshots (
    Id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    ScanLogId           INT NOT NULL REFERENCES dbo.ScanLogs(Id),
    ClienteId           INT NOT NULL,
    ClienteNombre       NVARCHAR(200) NOT NULL,
    ClienteCodigo       VARCHAR(50) NOT NULL,
    Ambiente            VARCHAR(10) NOT NULL,
    ObjectFullName      NVARCHAR(300) NOT NULL,
    SchemaName          NVARCHAR(128) NOT NULL,
    ObjectName          NVARCHAR(128) NOT NULL,
    ObjectType          VARCHAR(5) NOT NULL,            -- P, V, FN, TF, IF
    DefinitionHash      VARCHAR(64) NOT NULL,           -- SHA256 hex
    ObjectLastModified  DATETIME2 NOT NULL,
    SnapshotDate        DATETIME2 NOT NULL,
    IsCustom            BIT NOT NULL DEFAULT 0
);

CREATE INDEX IX_ObjectSnapshots_Lookup
    ON dbo.ObjectSnapshots(ClienteId, Ambiente, SnapshotDate DESC);

CREATE INDEX IX_ObjectSnapshots_Object
    ON dbo.ObjectSnapshots(ObjectFullName, ClienteId, Ambiente, SnapshotDate DESC);

CREATE INDEX IX_ObjectSnapshots_ScanLog
    ON dbo.ObjectSnapshots(ScanLogId);

CREATE INDEX IX_ObjectSnapshots_Hash
    ON dbo.ObjectSnapshots(DefinitionHash);
GO

-- Definiciones almacenadas por separado (NVARCHAR(MAX))
CREATE TABLE dbo.ObjectSnapshotDefinitions (
    Id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    ObjectSnapshotId    BIGINT NOT NULL REFERENCES dbo.ObjectSnapshots(Id),
    Definition          NVARCHAR(MAX) NOT NULL
);

CREATE UNIQUE INDEX IX_OSD_SnapshotId
    ON dbo.ObjectSnapshotDefinitions(ObjectSnapshotId);
GO

-- Cambios detectados entre scans consecutivos
CREATE TABLE dbo.DetectedChanges (
    Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    ScanLogId       INT NOT NULL REFERENCES dbo.ScanLogs(Id),
    ClienteId       INT NOT NULL,
    ClienteCodigo   VARCHAR(50) NOT NULL,
    Ambiente        VARCHAR(10) NOT NULL,
    ObjectFullName  NVARCHAR(300) NOT NULL,
    ObjectType      VARCHAR(5) NOT NULL,
    ChangeType      VARCHAR(20) NOT NULL,       -- Created, Modified, Deleted
    PreviousHash    VARCHAR(64) NULL,
    CurrentHash     VARCHAR(64) NULL,
    DetectedAt      DATETIME2 NOT NULL,
    NotificationSent BIT NOT NULL DEFAULT 0
);

CREATE INDEX IX_DetectedChanges_ScanLog ON dbo.DetectedChanges(ScanLogId);
CREATE INDEX IX_DetectedChanges_Cliente ON dbo.DetectedChanges(ClienteId, Ambiente, DetectedAt DESC);
CREATE INDEX IX_DetectedChanges_Pending ON dbo.DetectedChanges(NotificationSent) WHERE NotificationSent = 0;
GO

-- =====================================================
-- Vista: Último snapshot por cliente/ambiente/objeto
-- =====================================================
CREATE OR ALTER VIEW dbo.vw_LatestSnapshots AS
WITH Ranked AS (
    SELECT *,
           ROW_NUMBER() OVER (
               PARTITION BY ClienteId, Ambiente, ObjectFullName
               ORDER BY SnapshotDate DESC
           ) AS rn
    FROM dbo.ObjectSnapshots
)
SELECT Id, ScanLogId, ClienteId, ClienteNombre, ClienteCodigo, Ambiente,
       ObjectFullName, SchemaName, ObjectName, ObjectType,
       DefinitionHash, ObjectLastModified, SnapshotDate, IsCustom
FROM Ranked WHERE rn = 1;
GO
