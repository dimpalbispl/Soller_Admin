/* ===========================================================================
   DCR & Work Upload - admin ab DCR ke saath Work document bhi upload karta hai
   (exactly 2 PDF). Doosri PDF ka path is naye column mein jaata hai:

       dbo.DCRDocuments.WorkDocumentPath   <- Work PDF
       dbo.DCRDocuments.DocumentPath       <- DCR PDF (pehle jaisa)

   Hand-rolled like the rest of this schema. Safe to re-run.
   RUN THIS BEFORE deploying the admin build that carries this change.
   =========================================================================== */

IF COL_LENGTH('dbo.DCRDocuments', 'WorkDocumentPath') IS NULL
BEGIN
    ALTER TABLE dbo.DCRDocuments ADD WorkDocumentPath nvarchar(max) NULL;
    PRINT 'Added dbo.DCRDocuments.WorkDocumentPath.';
END
ELSE
    PRINT 'dbo.DCRDocuments.WorkDocumentPath already exists.';
