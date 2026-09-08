namespace SolarPortal.Domain.Enums;

public enum ProjectStatus
{
    Registration = 1,
    ProductSelection = 2,
    Payment = 3,
    PMSurvey = 4,
    MeterDispatch = 5,
    SiteSurvey = 6,
    MaterialDispatch = 7,
    Installation = 8,
    DCRUpdate = 9,
    Completed = 10
}

public enum ApprovalStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3
}

public enum ConnectionType
{
    Domestic = 1,
    Commercial = 2
}

public enum PaymentStatus
{
    Pending = 1,
    Partial = 2,
    Completed = 3,
    Rejected = 4   // Admin rejected this submission; user must submit a fresh one.
}

public enum DocumentType
{
    AadharCard = 1,
    PANCard = 2,
    BankPassbook = 3,
    LightBill = 4,
    PropertyDocument = 5,
    PaymentReceipt = 6,
    GPSPhoto = 7,
    DCRDocument = 8,
    SitePhoto = 9,
    PMSuryagramDocument = 10,
    PMApprovalDocument = 11,   // admin-uploaded approval doc (Task 11), user-downloadable
    RelationProof = 12,        // blood-relation proof for light bill (Task 3)
    PMApprovalPhoto = 13,      // admin-uploaded PM Surya approval photo, user-downloadable
    PMApprovalSignature = 14,   // admin-uploaded PM Surya approval signature, user-downloadable
    // Applicant-side (user panel) live photo & signature on the PM Surya page
    Photo = 15,
    Signature = 16
}

public enum RequestType
{
    WithActivation = 1,
    OnlySolarWithoutActivation = 2,
    AlreadyActiveOnlyRequest = 3
}

public enum WorkerType
{
    JOB = 1,
    INC = 2
}

/// <summary>
/// Who receives one of the Remaining-BV income heads (Discom Income / Deal Close).
/// Self  = the member's own IdNo is saved.
/// Other = a typed-in IdNo, which must sit ABOVE the member in the sponsor tree.
/// </summary>
public enum BvBeneficiaryMode
{
    Self = 1,
    Other = 2
}

/// <summary>
/// Where a typed-in IdNo sits relative to the member in the SPONSOR tree
/// (m_membermaster.RefFormNo chain). Only <see cref="Upline"/> may be saved
/// against a Remaining-BV income head.
/// </summary>
public enum SponsorRelation
{
    NotFound  = 0,
    Self      = 1,
    Upline    = 2,
    Downline  = 3,
    Unrelated = 4
}