/* ===========================================================================
   Solar Wallet - the member's own wallet for everything on the solar side.

   Why its own pair of tables: legacy dbo.TrnVoucher / dbo.Vouchertype are the
   MLM wallets and are not touched, and dbo.IncTrnvoucher belongs to the INC
   (installer) commission wallet. Solar money is neither, so it gets the same
   shape again under its own name:

       dbo.SolarVouchertype  <- dbo.IncVouchertype jaisi
       dbo.SolarTrnvoucher   <- dbo.IncTrnvoucher  jaisi (same columns/types)

   Kaise chalta hai:
     Member solar request par payment karta hai (ya admin Add Fund karta hai)
       -> admin us payment ko VERIFY karta hai
       -> SolarTrnvoucher mein credit row banti hai (AcType 'S', VType 'C'),
          CrTo = member ka IdNo, RefNo = PAY/<payment id>

     Fund Transfer page se admin credit ya debit karta hai
       -> approve hone par wahi row banti hai (credit 'C', debit 'D')

   Hand-rolled like the rest of this schema (__EFMigrationsHistory is empty on
   the live DB). Safe to re-run.
   =========================================================================== */

/* ---- 1. Wallet master ---------------------------------------------------- */
IF OBJECT_ID('dbo.SolarVouchertype', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SolarVouchertype
    (
        Acid         numeric(18,0) IDENTITY(1,1) NOT NULL,
        WalletName   varchar(100)  NOT NULL,
        Actype       char(1)       NOT NULL,
        ActiveStatus char(1)       NOT NULL,
        CONSTRAINT PK_SolarVouchertype PRIMARY KEY CLUSTERED (Acid)
    );
    PRINT 'Created dbo.SolarVouchertype.';
END
ELSE
    PRINT 'dbo.SolarVouchertype already exists.';

IF NOT EXISTS (SELECT 1 FROM dbo.SolarVouchertype WHERE Actype = 'S')
    INSERT INTO dbo.SolarVouchertype (WalletName, Actype, ActiveStatus)
    VALUES ('Solar Wallet', 'S', 'Y');


/* ---- 2. Wallet ledger ---------------------------------------------------- */
IF OBJECT_ID('dbo.SolarTrnvoucher', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SolarTrnvoucher
    (
        VoucherId    numeric(18,0) IDENTITY(1,1) NOT NULL,
        VoucherNo    numeric(18,0) NOT NULL,
        VoucherDate  datetime      NOT NULL,
        DrTo         varchar(100)  NULL,
        CrTo         varchar(100)  NOT NULL,
        Amount       numeric(18,2) NOT NULL,
        Narration    varchar(2500) NULL,
        RefNo        varchar(150)  NULL,
        AcType       char(1)       NOT NULL,
        RecTimeStamp datetime      NOT NULL,
        VType        char(1)       NOT NULL,
        SessID       numeric(18,0) NOT NULL,
        WSessID      numeric(18,0) NOT NULL,
        Balance      numeric(18,2) NOT NULL,
        UserId       numeric(18,0) NOT NULL,
        FromID       numeric(18,0) NULL,
        CONSTRAINT PK_SolarTrnvoucher PRIMARY KEY CLUSTERED (VoucherId)
    );

    -- A wallet statement always filters on (AcType, CrTo/DrTo).
    CREATE INDEX IX_SolarTrnvoucher_AcType_CrTo ON dbo.SolarTrnvoucher (AcType, CrTo);
    CREATE INDEX IX_SolarTrnvoucher_AcType_DrTo ON dbo.SolarTrnvoucher (AcType, DrTo);
    -- RefNo is what stops the same money being posted twice.
    CREATE INDEX IX_SolarTrnvoucher_RefNo       ON dbo.SolarTrnvoucher (RefNo);

    PRINT 'Created dbo.SolarTrnvoucher.';
END
ELSE
    PRINT 'dbo.SolarTrnvoucher already exists.';
GO
