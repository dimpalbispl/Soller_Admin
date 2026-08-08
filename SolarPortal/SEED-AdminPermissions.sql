/* ============================================================================
   SEED-AdminPermissions.sql   (OPTIONAL — you usually do not need this)
   ----------------------------------------------------------------------------
   AdminPermissions is empty by design after a data clear, and an EMPTY table is
   the safe state: the code treats "this admin has no rows" as FULL ACCESS. So an
   empty table means nobody is restricted — not that anybody is locked out.

   Run this ONLY if you want the rows to exist up front, e.g. to hand one admin a
   deliberately reduced menu and manage it from the User Permissions screen.

   Set @UserName below to the m_usermaster.UserName you want to configure.
   ============================================================================ */

SET NOCOUNT ON;

DECLARE @UserName nvarchar(100) = N'admin';     -- <<< change me
DECLARE @UpdatedBy nvarchar(450) = N'seed';

/* Refuse to run against an unknown account: a typo here would create rows that
   restrict nobody and confuse whoever reads the screen next. */
IF NOT EXISTS (SELECT 1 FROM dbo.m_usermaster WHERE LTRIM(RTRIM(UserName)) = @UserName)
BEGIN
    RAISERROR('No m_usermaster row with UserName = %s. Nothing inserted.', 16, 1, @UserName);
    RETURN;
END

/* Every menu key the application knows about, exactly as AdminMenus.All lists
   them. A key that does not match is simply ignored by the app, so they must be
   spelled the same. CanView = may open it, CanEdit = may act inside it. */
DECLARE @Menus TABLE (MenuKey nvarchar(100) PRIMARY KEY, CanView bit, CanEdit bit);

INSERT INTO @Menus (MenuKey, CanView, CanEdit) VALUES
    (N'Dashboard',                        1, 0),
    (N'Projects',                         1, 0),
    (N'Payments',                         1, 1),
    (N'Funds.Add',                        1, 1),
    (N'Funds.Approve',                    1, 1),
    (N'ActivationHistory',                1, 0),
    (N'AdminAccess.Logs',                 1, 0),
    (N'PMSurya',                          1, 1),
    (N'Operations.MeterDispatch',         1, 1),
    (N'SiteSurvey',                       1, 1),
    (N'Operations.PrepareDispatch',       1, 1),
    (N'Operations.FinalDispatch',         1, 1),
    (N'Operations.Installation',          1, 1),
    (N'Operations.InstallationApprovals', 1, 1),
    (N'Operations.DCRUpdate',             1, 1),
    (N'SolarProjects',                    1, 1),
    (N'Workers',                          1, 1),
    (N'Commission',                       1, 1),
    (N'Inc.Connections',                  1, 1),
    (N'Inc.Kyc',                          1, 1),
    (N'Inc.Withdrawals',                  1, 1),
    (N'AdminAccess.Permissions',          1, 1);

/* Idempotent: re-running updates rather than duplicating. There is a unique
   index on (UserName, MenuKey), so a plain INSERT would fail the second time. */
MERGE dbo.AdminPermissions AS t
USING (SELECT @UserName AS UserName, MenuKey, CanView, CanEdit FROM @Menus) AS s
    ON t.UserName = s.UserName AND t.MenuKey = s.MenuKey
WHEN MATCHED THEN
    UPDATE SET t.CanView = s.CanView, t.CanEdit = s.CanEdit,
               t.UpdatedAt = SYSUTCDATETIME(), t.UpdatedBy = @UpdatedBy
WHEN NOT MATCHED THEN
    INSERT (UserName, MenuKey, CanView, CanEdit, UpdatedAt, UpdatedBy)
    VALUES (s.UserName, s.MenuKey, s.CanView, s.CanEdit, SYSUTCDATETIME(), @UpdatedBy);

SELECT MenuKey, CanView, CanEdit
  FROM dbo.AdminPermissions
 WHERE UserName = @UserName
 ORDER BY MenuKey;

PRINT 'Done. Remember: an admin with NO rows already has full access - these rows '
    + 'only matter once you start un-ticking menus.';
