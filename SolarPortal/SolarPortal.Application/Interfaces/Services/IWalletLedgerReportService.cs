namespace SolarPortal.Application.Interfaces.Services;

/// <summary>
/// Read-only report over the wallet ledger (IncTrnvoucher): every credit and
/// debit that has touched a wallet, whatever wrote it — INC commission, a
/// withdrawal, or an Extra Payment Refund / debit.
/// </summary>
public interface IWalletLedgerReportService
{
    /// <param name="from">Voucher date on or after this day (null = no lower bound).</param>
    /// <param name="to">Voucher date on or before this day (null = no upper bound).</param>
    /// <param name="acType">IncVouchertype.Actype to limit to one wallet (null = every wallet).</param>
    /// <param name="accountId">Member IdNo / worker id to limit to one account (null = everyone).</param>
    /// <param name="top">Row cap, so an open-ended range can never load the whole ledger.</param>
    Task<List<WalletLedgerRow>> GetAsync(DateTime? from, DateTime? to, string? acType, string? accountId, int top);

    /// <summary>The wallets that exist, for the filter dropdown.</summary>
    Task<List<WalletOption>> GetWalletsAsync();
}

/// <summary>One wallet in IncVouchertype.</summary>
public record WalletOption(int Acid, string WalletName, string Actype);

public class WalletLedgerRow
{
    public long VoucherId { get; set; }
    public string? VoucherNo { get; set; }
    public DateTime? VoucherDate { get; set; }

    /// <summary>When the row was actually written — VoucherDate is date-only.</summary>
    public DateTime? PostedAt { get; set; }

    /// <summary>The wallet this row belongs to (IncVouchertype.Actype / WalletName).</summary>
    public string? AcType { get; set; }
    public string? WalletName { get; set; }

    /// <summary>'C' credit, 'D' debit, 'W' withdrawal debit.</summary>
    public string? VType { get; set; }

    /// <summary>True for money INTO the wallet.</summary>
    public bool IsCredit => string.Equals(VType, "C", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whose wallet moved: the credited account on a credit, the debited one
    /// otherwise. The ledger keeps them in two columns ('0' on the unused side).
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>Member name when AccountId is a member IdNo, installer name when it is a worker id.</summary>
    public string? AccountName { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Narration — the remark that says what the entry was for.</summary>
    public string? Remark { get; set; }

    public string? RefNo { get; set; }
}
