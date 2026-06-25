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
    RelationProof = 12         // blood-relation proof for light bill (Task 3)
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