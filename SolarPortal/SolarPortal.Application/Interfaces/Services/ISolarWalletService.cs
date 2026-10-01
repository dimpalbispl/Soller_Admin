namespace SolarPortal.Application.Interfaces.Services;

/// <summary>
/// The member's SOLAR wallet - its own ledger (dbo.SolarTrnvoucher), separate
/// from the legacy MLM wallets (TrnVoucher) and from the INC installer wallet
/// (IncTrnvoucher).
///
/// What lands here:
///   * every solar-request payment, once the admin VERIFIES it (credit), and
///   * Fund Transfer entries an admin raises and approves (credit or debit) -
///     e.g. the member's loan came in higher than the project, so the balance is
///     transferred back to them.
///
/// Nothing here is written twice: every post carries a RefNo and the writer
/// refuses a second row with the same RefNo and direction.
/// </summary>
public interface ISolarWalletService
{
    /// <summary>Wallet code in SolarVouchertype - 'S'.</summary>
    const string SolarWalletAcType = "S";

    /// <summary>
    /// Posts one row. isCredit true = money INTO the member's wallet
    /// (DrTo '0', CrTo member, VType 'C'); false = out of it
    /// (DrTo member, CrTo '0', VType 'D').
    /// </summary>
    /// <returns>True when a row was written, false when RefNo was already posted.</returns>
    Task<bool> PostAsync(string memberIdNo, decimal amount, bool isCredit, string refNo, string narration);

    /// <summary>
    /// Credit for a verified solar payment. Keyed on the payment id, so verifying
    /// the same payment twice can never credit the wallet twice.
    /// </summary>
    Task<bool> CreditVerifiedPaymentAsync(int paymentId, string memberIdNo, decimal amount,
                                          string? requestNumber, string? utrNumber);

    /// <summary>The wallets this ledger knows (SolarVouchertype) - for the pickers.</summary>
    Task<List<SolarWalletOption>> GetWalletsAsync();

    /// <summary>Current balance of one member's solar wallet: credits minus debits.</summary>
    Task<decimal> GetBalanceAsync(string memberIdNo);
}

public record SolarWalletOption(int Acid, string WalletName, string Actype);
