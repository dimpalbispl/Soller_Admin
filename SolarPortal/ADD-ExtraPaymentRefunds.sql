/* ===========================================================================
   Extra Payment Refund — refunds of money a member paid over and above what
   their project needed.

   An admin raises the refund (member IdNo, amount, which wallet), and a second
   decision approves or rejects it. ONLY an approved refund is written to the
   legacy ledger (IncTrnvoucher, credited to the member's wallet with the
   project's request number on it) — a pending or rejected row moves no money.

   Hand-rolled like the rest of this schema: __EFMigrationsHistory is empty on
   the live DB, so EF migrations are never applied there. Safe to re-run.
   =========================================================================== */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ExtraPaymentRefunds')
BEGIN
    CREATE TABLE dbo.ExtraPaymentRefunds
    (
        Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExtraPaymentRefunds PRIMARY KEY,

        SolarRequestId   INT             NOT NULL,
        RequestNumber    VARCHAR(30)     NULL,
        MemberIdNo       VARCHAR(50)     NOT NULL,
        MemberName       VARCHAR(150)    NULL,

        Amount           DECIMAL(18,2)   NOT NULL,

        /* Which way the money moves: 'C' = credit to the member's wallet,
           'D' = debit out of it (the legacy "Amount deducted..." shape). */
        EntryType        VARCHAR(1)      NOT NULL CONSTRAINT DF_ExtraPaymentRefunds_EntryType DEFAULT ('C'),

        /* IncVouchertype: Acid / Actype / WalletName of the wallet credited. */
        VoucherTypeId    INT             NOT NULL,
        VoucherAcType    VARCHAR(1)      NOT NULL,
        VoucherTypeName  VARCHAR(100)    NULL,

        Remark           VARCHAR(1000)   NULL,

        /* ApprovalStatus: 1 = Pending, 2 = Approved, 3 = Rejected */
        Status           INT             NOT NULL CONSTRAINT DF_ExtraPaymentRefunds_Status DEFAULT (1),

        RequestedBy      VARCHAR(100)    NULL,
        RequestedAt      DATETIME2       NULL,
        DecidedBy        VARCHAR(100)    NULL,
        DecidedAt        DATETIME2       NULL,
        RejectionReason  VARCHAR(1000)   NULL,

        /* RefNo written on the IncTrnvoucher row + when it was posted. PostedAt
           is the double-credit guard: a refund with a value here is never
           posted again. */
        VoucherRefNo     VARCHAR(150)    NULL,
        PostedAt         DATETIME2       NULL,

        /* BaseEntity columns, same shape as every other table here. */
        CreatedAt        DATETIME2       NOT NULL CONSTRAINT DF_ExtraPaymentRefunds_CreatedAt DEFAULT (GETUTCDATE()),
        UpdatedAt        DATETIME2       NULL,
        CreatedBy        NVARCHAR(450)   NULL,
        UpdatedBy        NVARCHAR(450)   NULL,
        IsDeleted        BIT             NOT NULL CONSTRAINT DF_ExtraPaymentRefunds_IsDeleted DEFAULT (0),

        CONSTRAINT FK_ExtraPaymentRefunds_SolarRequests
            FOREIGN KEY (SolarRequestId) REFERENCES dbo.SolarRequests(Id) ON DELETE CASCADE
    );

    CREATE INDEX IX_ExtraPaymentRefunds_SolarRequestId ON dbo.ExtraPaymentRefunds(SolarRequestId);
    CREATE INDEX IX_ExtraPaymentRefunds_MemberIdNo     ON dbo.ExtraPaymentRefunds(MemberIdNo);

    PRINT 'ExtraPaymentRefunds created.';
END
ELSE
BEGIN
    PRINT 'ExtraPaymentRefunds already exists — nothing to do.';
END
GO

/* The screen's menu key is "Refunds" (AdminMenus.All in the app). Admins with no
   AdminPermissions rows at all stay unrestricted and see it straight away; a
   SCOPED admin needs the row, which SEED-AdminPermissions.sql now includes. */

/* Added after the first release: refunds can now be a DEBIT as well as a credit.
   Safe to re-run — the column is only added when it is missing. */
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
               WHERE TABLE_NAME = 'ExtraPaymentRefunds' AND COLUMN_NAME = 'EntryType')
BEGIN
    ALTER TABLE dbo.ExtraPaymentRefunds
        ADD EntryType VARCHAR(1) NOT NULL CONSTRAINT DF_ExtraPaymentRefunds_EntryType DEFAULT ('C');
    PRINT 'EntryType added.';
END
GO

/* Added later: a fund transfer can go to ANY member ID, including one with no
   solar request yet, so SolarRequestId becomes optional. SQL Server will not
   change a column's nullability while an index or foreign key depends on it, so
   both are dropped and put back around the ALTER. Safe to re-run. */
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_NAME = 'ExtraPaymentRefunds' AND COLUMN_NAME = 'SolarRequestId'
             AND IS_NULLABLE = 'NO')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ExtraPaymentRefunds_SolarRequests')
        ALTER TABLE dbo.ExtraPaymentRefunds DROP CONSTRAINT FK_ExtraPaymentRefunds_SolarRequests;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ExtraPaymentRefunds_SolarRequestId'
                                           AND object_id = OBJECT_ID('dbo.ExtraPaymentRefunds'))
        DROP INDEX IX_ExtraPaymentRefunds_SolarRequestId ON dbo.ExtraPaymentRefunds;

    ALTER TABLE dbo.ExtraPaymentRefunds ALTER COLUMN SolarRequestId INT NULL;

    CREATE INDEX IX_ExtraPaymentRefunds_SolarRequestId ON dbo.ExtraPaymentRefunds(SolarRequestId);
    ALTER TABLE dbo.ExtraPaymentRefunds
        ADD CONSTRAINT FK_ExtraPaymentRefunds_SolarRequests
            FOREIGN KEY (SolarRequestId) REFERENCES dbo.SolarRequests(Id) ON DELETE CASCADE;

    PRINT 'SolarRequestId is now optional.';
END
GO