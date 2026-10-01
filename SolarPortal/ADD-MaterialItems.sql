/* ===========================================================================
   Material dispatch list.

       dbo.MaterialItems          <- master list (Solar Module, Inverter, ...)
       dbo.MaterialDispatchItems  <- quantity of each item on a dispatch

   Prepare for Dispatch loads every active MaterialItems row and asks for a
   quantity against each; the filled-in lines are saved to
   MaterialDispatchItems (one row per item, per MaterialDispatches row).

   To add / rename / hide an item later, edit dbo.MaterialItems directly
   (IsActive = 0 hides it from the form without touching past dispatches).

   Hand-rolled like the rest of this schema. Safe to re-run.
   RUN THIS BEFORE deploying the admin build that carries this change.
   =========================================================================== */

/* ---- 1. Master list ------------------------------------------------------ */
IF OBJECT_ID('dbo.MaterialItems', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MaterialItems
    (
        Id        int IDENTITY(1,1) NOT NULL,
        Name      nvarchar(150) NOT NULL,
        SortOrder int           NOT NULL CONSTRAINT DF_MaterialItems_SortOrder DEFAULT (0),
        IsActive  bit           NOT NULL CONSTRAINT DF_MaterialItems_IsActive  DEFAULT (1),
        CreatedAt datetime2     NOT NULL CONSTRAINT DF_MaterialItems_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2     NULL,
        CreatedBy nvarchar(max) NULL,
        UpdatedBy nvarchar(max) NULL,
        IsDeleted bit           NOT NULL CONSTRAINT DF_MaterialItems_IsDeleted DEFAULT (0),
        CONSTRAINT PK_MaterialItems PRIMARY KEY CLUSTERED (Id)
    );
    PRINT 'Created dbo.MaterialItems.';
END
ELSE
    PRINT 'dbo.MaterialItems already exists.';

/* ---- 2. Seed the 32 items (only those not already present) -------------- */
;WITH src (SortOrder, Name) AS
(
    SELECT * FROM (VALUES
        ( 1, N'Solar Module'),
        ( 2, N'Inverter'),
        ( 3, N'GP Pipe'),
        ( 4, N'J-Hook / J-Bolt'),
        ( 5, N'Base Plate'),
        ( 6, N'Fastener Nut'),
        ( 7, N'DC Cable'),
        ( 8, N'AC Cable'),
        ( 9, N'Lightning Arrester'),
        (10, N'Earthing Wire'),
        (11, N'MC4 Connector'),
        (12, N'Cable Tray'),
        (13, N'Chemical Bag'),
        (14, N'Cable Tie'),
        (15, N'Tape Roll'),
        (16, N'Screw (Black)'),
        (17, N'Self Screw'),
        (18, N'Dowel / Gitti'),
        (19, N'Lug 4mm / 6mm / 10mm'),
        (20, N'Welding Rod'),
        (21, N'Grinder Cutter Blade'),
        (22, N'PVC Pipe'),
        (23, N'Elbow'),
        (24, N'Bend'),
        (25, N'Tee'),
        (26, N'GI Clip / PVC Clip'),
        (27, N'ACDB Box'),
        (28, N'DCDB Box'),
        (29, N'Meter Box'),
        (30, N'Zinc Spray'),
        (31, N'LA & Earthing Nut'),
        (32, N'L-Nuka / L-Kunda')
    ) v (SortOrder, Name)
)
INSERT INTO dbo.MaterialItems (Name, SortOrder)
SELECT s.Name, s.SortOrder
FROM src s
WHERE NOT EXISTS (SELECT 1 FROM dbo.MaterialItems m WHERE m.Name = s.Name);
PRINT CONCAT('Seeded ', @@ROWCOUNT, ' material item(s).');

/* ---- 3. Per-dispatch quantities ----------------------------------------- */
IF OBJECT_ID('dbo.MaterialDispatchItems', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MaterialDispatchItems
    (
        Id                 int IDENTITY(1,1) NOT NULL,
        MaterialDispatchId int           NOT NULL,
        MaterialItemId     int           NOT NULL,
        ItemName           nvarchar(150) NOT NULL,
        Quantity           nvarchar(50)  NOT NULL,
        CreatedAt          datetime2     NOT NULL CONSTRAINT DF_MaterialDispatchItems_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt          datetime2     NULL,
        CreatedBy          nvarchar(max) NULL,
        UpdatedBy          nvarchar(max) NULL,
        IsDeleted          bit           NOT NULL CONSTRAINT DF_MaterialDispatchItems_IsDeleted DEFAULT (0),
        CONSTRAINT PK_MaterialDispatchItems PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_MaterialDispatchItems_MaterialDispatches
            FOREIGN KEY (MaterialDispatchId) REFERENCES dbo.MaterialDispatches (Id) ON DELETE CASCADE,
        CONSTRAINT FK_MaterialDispatchItems_MaterialItems
            FOREIGN KEY (MaterialItemId) REFERENCES dbo.MaterialItems (Id)
    );
    CREATE INDEX IX_MaterialDispatchItems_MaterialDispatchId
        ON dbo.MaterialDispatchItems (MaterialDispatchId);
    PRINT 'Created dbo.MaterialDispatchItems.';
END
ELSE
    PRINT 'dbo.MaterialDispatchItems already exists.';
