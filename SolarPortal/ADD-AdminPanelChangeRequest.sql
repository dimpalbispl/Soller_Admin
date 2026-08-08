/*
    Admin-panel change request (handwritten notes, 07-Aug-2026).

    ⚠ RUN THIS **AND** SolarPortal/ADD-UserPanelIncPoints.sql from the user /
      installer panel repo. Both apps share ONE database and each script owns a
      different set of objects. Order does not matter; both are idempotent.

    Every statement is guarded, so re-running is safe and no existing column's
    data is touched.

    Point  What THIS script adds
    ─────  ──────────────────────────────────────────────────────────────────
    OTP    AdminLoginOtps          - admin login OTP (issue / verify / audit)
      6    MaterialDispatches      - Prepare-for-Dispatch vs Final Dispatch
      7    Payments                - Add Fund vs Approve Fund (maker-checker)
      9    SolarRequests           - PM Surya Ghar "accepted by" claim columns
    Perm   AdminPermissions        - admin -> user permission grid

    Owned by ADD-UserPanelIncPoints.sql instead (the panel that WRITES them):
      2    SolarRequests.ProductRequestBlocked
      8    IncKycDocuments         - three-section INC KYC, one row per worker
     11    InstallationPhotos      - INC mark-installed photos (up to 30)
           Installations           - ApprovalStatus / RejectionReason /
                                     ReviewedAt / ReviewedBy / SubmittedAt /
                                     CommissionCredited
*/

/* ═══════════════════════════════════════════════════════════════════════════
   Admin Login with OTP
   Mirrors the legacy VB flow (Default.aspx.vb, CompID 1091): an OTP is mailed
   to the admin's m_usermaster e-mail, stored, and then verified. Kept in its
   own table rather than the legacy AdminLogin one so the portal never writes
   into a table the old ASP.NET app owns.
   ═════════════════════════════════════════════════════════════════════════ */
IF OBJECT_ID('dbo.AdminLoginOtps', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AdminLoginOtps
    (
        Id          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AdminLoginOtps PRIMARY KEY,
        UserName    nvarchar(100)  NOT NULL,
        EmailId     nvarchar(256)  NULL,
        MobileNo    nvarchar(20)   NULL,
        Otp         nvarchar(10)   NOT NULL,
        IssuedAt    datetime2      NOT NULL CONSTRAINT DF_AdminLoginOtps_IssuedAt DEFAULT (SYSUTCDATETIME()),
        ExpiresAt   datetime2      NOT NULL,
        AttemptCount int           NOT NULL CONSTRAINT DF_AdminLoginOtps_Attempts DEFAULT (0),
        IsUsed      bit            NOT NULL CONSTRAINT DF_AdminLoginOtps_IsUsed DEFAULT (0),
        UsedAt      datetime2      NULL,
        IpAddress   nvarchar(45)   NULL
    );
    CREATE INDEX IX_AdminLoginOtps_UserName_IssuedAt ON dbo.AdminLoginOtps (UserName, IssuedAt DESC);
END
GO

/* ═══════════════════════════════════════════════════════════════════════════
   Point 6 — Material Dispatch becomes two steps.
   "Prepare for Dispatch" writes the row and stamps IsPrepared; "Final Dispatch"
   flips IsDispatched and advances the project. Existing rows were all created
   by the old one-shot flow, so they are back-filled as already prepared —
   otherwise every historical dispatch would reappear in the Prepare queue.
   ═════════════════════════════════════════════════════════════════════════ */
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MaterialDispatches') AND name = 'IsPrepared')
    ALTER TABLE dbo.MaterialDispatches ADD IsPrepared bit NOT NULL CONSTRAINT DF_MaterialDispatches_IsPrepared DEFAULT (0);
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MaterialDispatches') AND name = 'PreparedAt')
    ALTER TABLE dbo.MaterialDispatches ADD PreparedAt datetime2 NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MaterialDispatches') AND name = 'PreparedBy')
    ALTER TABLE dbo.MaterialDispatches ADD PreparedBy nvarchar(450) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MaterialDispatches') AND name = 'PrepareRemark')
    ALTER TABLE dbo.MaterialDispatches ADD PrepareRemark nvarchar(1000) NULL;
GO

UPDATE dbo.MaterialDispatches
   SET IsPrepared = 1,
       PreparedAt = COALESCE(PreparedAt, CreatedAt)
 WHERE IsDispatched = 1 AND IsPrepared = 0;
GO

/* ═══════════════════════════════════════════════════════════════════════════
   Point 7 — Add Fund becomes two steps (maker-checker).
   A fund added by an admin lands as an UNVERIFIED payment flagged IsAdminFund,
   and a second admin approves it from the Approve Fund menu. Payments recorded
   before this change were auto-verified by the old single-step flow; they keep
   IsAdminFund = 0 so they never show up in the new approval queue.
   ═════════════════════════════════════════════════════════════════════════ */
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Payments') AND name = 'IsAdminFund')
    ALTER TABLE dbo.Payments ADD IsAdminFund bit NOT NULL CONSTRAINT DF_Payments_IsAdminFund DEFAULT (0);
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Payments') AND name = 'FundAddedBy')
    ALTER TABLE dbo.Payments ADD FundAddedBy nvarchar(450) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Payments') AND name = 'FundApprovedBy')
    ALTER TABLE dbo.Payments ADD FundApprovedBy nvarchar(450) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Payments') AND name = 'FundApprovedAt')
    ALTER TABLE dbo.Payments ADD FundApprovedAt datetime2 NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Payments') AND name = 'FundRejectionReason')
    ALTER TABLE dbo.Payments ADD FundRejectionReason nvarchar(1000) NULL;
GO

/* ═══════════════════════════════════════════════════════════════════════════
   Points 8 and 11 — owned by ADD-UserPanelIncPoints.sql, NOT by this script.
   ───────────────────────────────────────────────────────────────────────────
   dbo.IncKycDocuments, dbo.InstallationPhotos and the review columns on
   dbo.Installations are created by the USER/INSTALLER panel's script, because
   that panel WRITES those rows — this app only reads them and stamps the
   admin's decision.

   They were briefly defined here too, with different column names
   (InstallationPhotos.FileSize vs FileSizeBytes, Installations.PhotoStatus vs
   ApprovalStatus, and a one-row-per-document IncKycDocuments instead of the
   three-section one-row-per-worker shape). Two definitions of one table in one
   shared database can only ever produce "Invalid column name" in whichever app
   lost the race, so the duplicates are deliberately gone. The admin entities
   now mirror the installer panel's.

   → Run BOTH scripts. Order does not matter; each is idempotent.
   ═════════════════════════════════════════════════════════════════════════ */

/* ═══════════════════════════════════════════════════════════════════════════
   Point 9 — PM Surya Ghar is claimed before it is decided.
   An admin "accepts" the case; only that admin can then approve or reject its
   documents. Rejecting a document releases the claim (the columns go back to
   NULL) so any admin can pick it up again after the user re-uploads.
   ═════════════════════════════════════════════════════════════════════════ */
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SolarRequests') AND name = 'PmSuryaAcceptedBy')
    ALTER TABLE dbo.SolarRequests ADD PmSuryaAcceptedBy nvarchar(450) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SolarRequests') AND name = 'PmSuryaAcceptedByName')
    ALTER TABLE dbo.SolarRequests ADD PmSuryaAcceptedByName nvarchar(200) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SolarRequests') AND name = 'PmSuryaAcceptedAt')
    ALTER TABLE dbo.SolarRequests ADD PmSuryaAcceptedAt datetime2 NULL;
GO

/* ═══════════════════════════════════════════════════════════════════════════
   Admin -> user permission grid.
   One row per (admin user, menu key). Absence of a row means "not granted",
   so an empty table leaves every existing admin on the default full access
   that the code falls back to.
   ═════════════════════════════════════════════════════════════════════════ */
IF OBJECT_ID('dbo.AdminPermissions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AdminPermissions
    (
        Id         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AdminPermissions PRIMARY KEY,
        UserName   nvarchar(100) NOT NULL,   -- m_usermaster.UserName
        MenuKey    nvarchar(100) NOT NULL,   -- e.g. "Operations.MaterialDispatch"
        CanView    bit           NOT NULL CONSTRAINT DF_AdminPermissions_CanView DEFAULT (1),
        CanEdit    bit           NOT NULL CONSTRAINT DF_AdminPermissions_CanEdit DEFAULT (0),
        UpdatedAt  datetime2     NOT NULL CONSTRAINT DF_AdminPermissions_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy  nvarchar(450) NULL
    );
    CREATE UNIQUE INDEX UX_AdminPermissions_User_Menu ON dbo.AdminPermissions (UserName, MenuKey);
END
GO

/* ═══════════════════════════════════════════════════════════════════════════
   Sanity check — did an EARLIER version of this script create the shared
   tables in the admin-only shape?

   That version defined InstallationPhotos with FileSize and IncKycDocuments as
   one row per document. If those tables already existed in that shape, the
   installer panel's script would have skipped them (its guard is
   IF OBJECT_ID IS NULL) and one of the two apps would fail at runtime with
   "Invalid column name". Reports only — dropping a table with live rows is not
   something a setup script should decide on its own.
   ═════════════════════════════════════════════════════════════════════════ */
IF OBJECT_ID('dbo.InstallationPhotos', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.InstallationPhotos', 'FileSizeBytes') IS NULL
    PRINT 'WARNING: dbo.InstallationPhotos is in the OLD admin-only shape (FileSize, not FileSizeBytes). ' +
          'It has to be rebuilt by ADD-UserPanelIncPoints.sql — drop it if it holds no rows, otherwise rename the column.';

IF OBJECT_ID('dbo.IncKycDocuments', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.IncKycDocuments', 'AddressStatus') IS NULL
    PRINT 'WARNING: dbo.IncKycDocuments is in the OLD admin-only shape (one row per document). ' +
          'It has to be rebuilt by ADD-UserPanelIncPoints.sql — drop it if it holds no rows.';

IF OBJECT_ID('dbo.Installations', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.Installations', 'ApprovalStatus') IS NULL
    PRINT 'WARNING: dbo.Installations has no ApprovalStatus column. ' +
          'Run ADD-UserPanelIncPoints.sql — installation photo approval will fail without it.';

IF COL_LENGTH('dbo.Installations', 'PhotoStatus') IS NOT NULL
    PRINT 'NOTE: dbo.Installations.PhotoStatus / PhotoRemark / PhotoReviewedBy / PhotoReviewedAt / ' +
          'CommissionCreditedAt are left over from the earlier admin-only design. Nothing reads them ' +
          'any more; they can be dropped once you are happy the new flow works.';
GO

PRINT 'Admin-panel objects are present. Remember to also run ADD-UserPanelIncPoints.sql.';
