-- Removes roles created by previous MonitorRFC scripts.
SET XACT_ABORT ON;
GO
DECLARE @sql nvarchar(max)=N'';
SELECT @sql=STRING_AGG(N'ALTER ROLE '+QUOTENAME(r.name)+N' DROP MEMBER '+QUOTENAME(m.name)+N';',CHAR(10))
FROM sys.database_role_members rm
JOIN sys.database_principals r ON r.principal_id=rm.role_principal_id
JOIN sys.database_principals m ON m.principal_id=rm.member_principal_id
WHERE r.name IN (N'mrf_api',N'mrf_ingestor');
IF @sql<>N'' EXEC sys.sp_executesql @sql;
IF DATABASE_PRINCIPAL_ID(N'mrf_api') IS NOT NULL DROP ROLE mrf_api;
IF DATABASE_PRINCIPAL_ID(N'mrf_ingestor') IS NOT NULL DROP ROLE mrf_ingestor;
IF NOT EXISTS(SELECT 1 FROM dbo.MigracionEsquema WHERE Version=8) INSERT dbo.MigracionEsquema(Version) VALUES(8);
GO
