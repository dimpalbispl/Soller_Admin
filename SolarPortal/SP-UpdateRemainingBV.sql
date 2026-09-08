/* ============================================================================
   Sp_UpdateReaminingBV  --  Remaining BV ko legacy MLM ledger me post karta hai.

   Kab chalta hai:
     Admin panel > Update Remaining BV > APPROVE button.
     RemainingBvController.PostRepurchaseIncomeAsync() ise call karta hai,
     status Approved hone se PEHLE. FAILED aaya to approval hoti hi nahi.

   Kya karta hai:
     1. RepurchIncome me ek entry daalta hai (member ke FormNo par, BillType 'T',
        SoldBy 'HO') -- yahi entry MLM income calculation me count hoti hai.
     2. RemainingBvUpdates.SessID column ko aaj ki date se bharta hai,
        112 format me (yyyymmdd) -- wahi format jo RepurchIncome.DSessid me
        ja raha hai, taki dono taraf ka din match kare.

   @RbvId  = RemainingBvUpdates.Id. Naya OPTIONAL parameter (default 0), isliye
             purane callers jo 4 parameter bhejte the wo bina change ke chalte
             rahenge. 0 bhejne par member ke IdNo se match hota hai (saari rows),
             isliye admin panel hamesha exact Id bhejta hai.

   Output  : ek single column 'Result' -- 'SUCCESS' ya 'FAILED: <reason>'.

   NOTE: ye script idempotent hai -- baar baar chala sakte hain.
============================================================================ */

IF OBJECT_ID('dbo.Sp_UpdateReaminingBV', 'P') IS NULL
    EXEC('CREATE PROCEDURE dbo.Sp_UpdateReaminingBV AS SET NOCOUNT ON;');
GO

ALTER PROCEDURE dbo.Sp_UpdateReaminingBV
    @IDNo        Varchar(50),
    @FinalBV     Numeric(18,2),
    @BillNo      Numeric(24,0),
    @productName Varchar(250),
    @RbvId       Int = 0
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Msg     Varchar(500) = 'FAILED';
    DECLARE @SessID  Numeric(18,0);
    DECLARE @MSessID Numeric(18,0);
    DECLARE @FormNo  Numeric(18,0);

    -- Aaj ka din 112 (yyyymmdd) format me. RepurchIncome.DSessid aur
    -- RemainingBvUpdates.SessID dono me YAHI value jaati hai.
    DECLARE @DSessID Varchar(8) = CONVERT(varchar(8), GETDATE(), 112);

    SELECT @MSessID = ISNULL(MAX(SessID), 1) FROM M_MonthSessnMaster;
    SELECT @SessID  = ISNULL(MAX(SessID), 1) FROM M_SessnMaster;
    SELECT @FormNo  = FormNo FROM M_MemberMaster WHERE IDNo = @IDNo;

    -- FormNo nahi mila to ledger me kachra row daalne ka koi matlab nahi.
    IF @FormNo IS NULL
    BEGIN
        SELECT 'FAILED: member ' + ISNULL(@IDNo, '') + ' not found in M_MemberMaster' AS Result;
        RETURN;
    END

    BEGIN TRY
        BEGIN TRANSACTION;

        INSERT INTO RepurchIncome
            (SessID, FormNo, BillNo, BillDate, RepurchIncome, Imported, BillType,
             SoldBy, MSessID, KitID, Remarks, DSessid, PVValue)
        VALUES
            (@SessID,
             @FormNo,
             @BillNo,
             CAST(CONVERT(varchar, GETDATE(), 106) AS DateTime),
             @FinalBV,
             'N',
             'T',
             'HO',
             @MSessID,
             0,
             -- Remarks is varchar(150) but @productName is varchar(250) --
             -- trim, warna lamba plan name "String or binary data would be
             -- truncated" de dega.
             LEFT(@productName, 150),
             @DSessID,
             0);

        -- Naya SessID column RemainingBvUpdates par.
        UPDATE dbo.RemainingBvUpdates
        SET SessID = @DSessID
        WHERE (@RbvId > 0 AND Id = @RbvId)
           OR (@RbvId = 0 AND MemberIdNo = @IDNo AND ISNULL(IsDeleted, 0) = 0);

        COMMIT TRANSACTION;
        SET @Msg = 'SUCCESS';
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        SET @Msg = 'FAILED: ' + ERROR_MESSAGE();
    END CATCH

    SELECT @Msg AS Result;
END
GO


/* ----------------------------------------------------------------------------
   Column jo SP expect karta hai. Agar aapne DB me pehle se add kar liya hai to
   ye block kuch nahi karega.
---------------------------------------------------------------------------- */
IF COL_LENGTH('dbo.RemainingBvUpdates', 'SessID') IS NULL
BEGIN
    -- numeric(18,0), wahi type jo RepurchIncome.DSessid ka hai. '20260827'
    -- varchar isme implicitly convert ho jaata hai.
    ALTER TABLE dbo.RemainingBvUpdates ADD SessID Numeric(18,0) NULL;
END
GO
