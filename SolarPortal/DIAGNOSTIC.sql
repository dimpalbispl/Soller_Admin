/* =====================================================================
   DIAGNOSTIC — Run this in SSMS and send me the output
   =====================================================================
   Make sure top dropdown says: solfitenergy
   ===================================================================== */

USE solfitenergy;
GO

PRINT '════════════════ CURRENT DATABASE ════════════════';
SELECT DB_NAME() AS CurrentDatabase;

PRINT '════════════════ ALL AspNet TABLES ════════════════';
SELECT 
    TABLE_SCHEMA + '.' + TABLE_NAME AS FullTableName
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME LIKE 'AspNet%'
ORDER BY TABLE_NAME;

PRINT '════════════════ COLUMNS in AspNetUsers ════════════════';
SELECT 
    COLUMN_NAME,
    DATA_TYPE,
    CHARACTER_MAXIMUM_LENGTH,
    IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'AspNetUsers'
ORDER BY ORDINAL_POSITION;

PRINT '════════════════ COLUMN COUNT ════════════════';
SELECT 
    'AspNetUsers' AS TableName,
    COUNT(*) AS ColumnCount
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'AspNetUsers';

PRINT '════════════════ Test: Can we SELECT NormalizedEmail? ════════════════';
BEGIN TRY
    SELECT TOP 1 NormalizedEmail FROM AspNetUsers;
    PRINT '✓ NormalizedEmail column EXISTS';
END TRY
BEGIN CATCH
    PRINT '❌ NormalizedEmail does NOT exist: ' + ERROR_MESSAGE();
END CATCH;

PRINT '════════════════ Test: Can we SELECT custom columns? ════════════════';
BEGIN TRY
    SELECT TOP 1 FullName, AadharNumber, FatherName, MobileNumber FROM AspNetUsers;
    PRINT '✓ All custom columns EXIST';
END TRY
BEGIN CATCH
    PRINT '❌ Custom columns missing: ' + ERROR_MESSAGE();
END CATCH;

GO
