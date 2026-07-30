using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.DTOs;

/// <summary>
/// What kind of thing happened on a member's activation journey. Drives the
/// icon / colour of each timeline row in the Activation History report.
/// </summary>
public enum ActivationEventKind
{
    RequestSubmitted = 1,
    PaymentSubmitted = 2,
    PaymentVerified = 3,
    PaymentRejected = 4,
    ActivationRequested = 5,   // legacy TrnProductorderDetail row written (ForType='A')
    Activated = 6,             // legacy row approved → Sp_ActivateMember_New ran
    ActivationRejected = 7
}

/// <summary>One dated row on a member's activation timeline.</summary>
public class ActivationEventDto
{
    public DateTime When { get; set; }
    public ActivationEventKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public decimal? Amount { get; set; }
    public string? Reference { get; set; }   // request number / UTR / legacy OrderNo

    /// <summary>
    /// True when the exact timestamp isn't recorded anywhere and we inferred it
    /// from the triggering event (see MemberActivationHistoryDto.ActivatedOnApproximate).
    /// </summary>
    public bool IsApproximate { get; set; }
}

/// <summary>A single solar request in the member's history, positioned relative to activation.</summary>
public class ActivationRequestRowDto
{
    public int Id { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public RequestType RequestType { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? SelectedPlan { get; set; }
    public decimal PlanAmount { get; set; }
    public decimal VerifiedPaid { get; set; }
    public ProjectStatus CurrentStage { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; }

    /// <summary>Taken AFTER the member's ID was activated (i.e. the "Active Now" phase).</summary>
    public bool AfterActivation { get; set; }
}

/// <summary>
/// One member's full "without activation → paid → activated → Active Now" story.
///
/// Built per spec (Hinglish): "without activation liya fir payment complete karke
/// apni id active ki, fir Active Now se project le raha hu — user ko pata rehna
/// chahiye ki kya kab hua."
/// </summary>
public class MemberActivationHistoryDto
{
    public string MemberIdNo { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string? Mobile { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }

    /// <summary>Live m_membermaster.ActiveStatus == 'Y'.</summary>
    public bool IsActiveNow { get; set; }

    /// <summary>When the legacy MLM chain actually activated the ID (TrnProductorderDetail.Approvedate).</summary>
    public DateTime? ActivatedOn { get; set; }

    /// <summary>
    /// True when the legacy activation row carried no Approvedate (or the legacy
    /// DB was unreachable) and ActivatedOn was inferred from the verified payment
    /// that triggered activation. The view renders these with a "≈" marker so
    /// admin doesn't read an inferred date as an audited one.
    /// </summary>
    public bool ActivatedOnApproximate { get; set; }

    // First occurrence of each request mode — the three columns that answer
    // "pehle kya liya, phir kya liya".
    public DateTime? FirstWithoutActivationOn { get; set; }
    public DateTime? FirstWithActivationOn { get; set; }
    public DateTime? FirstAlreadyActiveOn { get; set; }

    public DateTime? FirstRequestOn { get; set; }
    public DateTime? LastRequestOn { get; set; }

    public int TotalRequests { get; set; }
    public int RequestsBeforeActivation { get; set; }
    public int RequestsAfterActivation { get; set; }

    public decimal TotalProjectAmount { get; set; }
    public decimal TotalVerifiedPaid { get; set; }

    public List<ActivationRequestRowDto> Requests { get; set; } = new();
    public List<ActivationEventDto> Events { get; set; } = new();

    /// <summary>
    /// The journey the report exists for: member started on a non-activation
    /// mode and only became active later. This is the "pehle without activation,
    /// baad me activate" cohort.
    /// </summary>
    public bool StartedWithoutActivation =>
        FirstWithoutActivationOn.HasValue &&
        (!FirstWithActivationOn.HasValue || FirstWithoutActivationOn < FirstWithActivationOn);

    public bool ActivatedLater => StartedWithoutActivation && IsActiveNow;

    /// <summary>Short human phase label for the grid.</summary>
    public string Phase =>
        !IsActiveNow                    ? "Not activated yet"
        : FirstAlreadyActiveOn.HasValue ? "Active — taking projects"
                                        : "Activated";
}
