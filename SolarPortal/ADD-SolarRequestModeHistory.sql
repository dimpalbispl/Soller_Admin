/*
    Mode history for SolarRequests.

    "Activate Now" upgrades a Without-Activation request by OVERWRITING
    RequestType on the same row, so afterwards nothing in the schema shows the
    member ever started without activation, and the admin Activation History
    report loses the whole "pehle without activation liya, baad me with
    activation liya" cohort.

    These columns keep that story:
        OriginalRequestType  - the mode the request was submitted with
        WithoutActivationOn  - when Without Activation was taken
        WithActivationOn     - when With Activation was taken (create or upgrade)
        AlreadyActiveOn      - when Active Now (already-active) was taken

    Each date is stamped ONCE, the first time that mode is entered, so an
    upgrade adds WithActivationOn without disturbing WithoutActivationOn.

    Safe to run more than once. Backfill at the bottom only fills NULLs.
*/

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SolarRequests') AND name = 'OriginalRequestType')
    ALTER TABLE dbo.SolarRequests ADD OriginalRequestType int NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SolarRequests') AND name = 'WithoutActivationOn')
    ALTER TABLE dbo.SolarRequests ADD WithoutActivationOn datetime2 NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SolarRequests') AND name = 'WithActivationOn')
    ALTER TABLE dbo.SolarRequests ADD WithActivationOn datetime2 NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SolarRequests') AND name = 'AlreadyActiveOn')
    ALTER TABLE dbo.SolarRequests ADD AlreadyActiveOn datetime2 NULL;
GO

/* ── Backfill ───────────────────────────────────────────────────────────────
   Rows upgraded before these columns existed left one trace: the marker
   "[Activated by user (payment already complete) on yyyy-MM-dd HH:mm UTC]"
   appended to AdminNotes. Where it is present the row is known to have started
   as Without Activation, and the marker carries the exact upgrade time.
   Where it is not, the row is assumed to have always been in its current mode.
   Only NULLs are written, so re-running never overwrites live stamps.        */

-- TRY_CONVERT needs compatibility level 110+; this database is on 100, so the
-- marker text is date-checked with ISDATE before converting.
;WITH marked AS (
    SELECT Id,
           CreatedAt,
           UpdatedAt,
           MarkerText = SUBSTRING(AdminNotes,
                            CHARINDEX(' on ', AdminNotes, CHARINDEX('[Activated by user', AdminNotes)) + 4,
                            16)
    FROM dbo.SolarRequests
    WHERE AdminNotes LIKE '%[[]Activated by user%'
)
UPDATE r
   SET r.OriginalRequestType = 2,                                    -- OnlySolarWithoutActivation
       r.WithoutActivationOn = COALESCE(r.WithoutActivationOn, m.CreatedAt),
       r.WithActivationOn    = COALESCE(r.WithActivationOn,
                                        CASE WHEN ISDATE(m.MarkerText) = 1
                                             THEN CONVERT(datetime2, m.MarkerText) END,
                                        m.UpdatedAt)
  FROM dbo.SolarRequests r
  JOIN marked m ON m.Id = r.Id;
GO

UPDATE dbo.SolarRequests
   SET OriginalRequestType = COALESCE(OriginalRequestType, RequestType),
       WithActivationOn    = CASE WHEN RequestType = 1 THEN COALESCE(WithActivationOn, CreatedAt)    ELSE WithActivationOn    END,
       WithoutActivationOn = CASE WHEN RequestType = 2 THEN COALESCE(WithoutActivationOn, CreatedAt) ELSE WithoutActivationOn END,
       AlreadyActiveOn     = CASE WHEN RequestType = 3 THEN COALESCE(AlreadyActiveOn, CreatedAt)     ELSE AlreadyActiveOn     END;
GO
