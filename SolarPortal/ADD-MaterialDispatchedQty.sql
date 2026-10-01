/* ===========================================================================
   Material dispatch - prepared vs. actually sent.

       dbo.MaterialDispatchItems.Quantity           <- quantity PREPARED (unchanged)
       dbo.MaterialDispatchItems.DispatchedQuantity <- total actually SENT so far

   NULL = not dispatched yet. Final Dispatch sets it to what went out (e.g.
   prepared 10, sent 8). The shortfall (10 - 8 = 2) is "pending" and can be sent
   later with "Dispatch Pending" - each such dispatch adds to this total. The
   installer stays the one assigned first; the project stage is not touched.

   Hand-rolled like the rest of this schema. Safe to re-run.
   RUN THIS BEFORE deploying the admin build that carries this change.
   =========================================================================== */

IF COL_LENGTH('dbo.MaterialDispatchItems', 'DispatchedQuantity') IS NULL
BEGIN
    ALTER TABLE dbo.MaterialDispatchItems ADD DispatchedQuantity decimal(18,2) NULL;
    PRINT 'Added dbo.MaterialDispatchItems.DispatchedQuantity.';
END
ELSE
    PRINT 'dbo.MaterialDispatchItems.DispatchedQuantity already exists.';
