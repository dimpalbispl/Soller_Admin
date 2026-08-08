namespace SolarPortal.Application.Interfaces.Services;

/// <summary>
/// Money an "Already Active" member has already paid into the legacy cPanel for
/// the product order that activated their ID.
///
/// Change request point 1: "Agar ID already Active hai to jitne rupaye ka order
/// lagaya, wo amount Solar Panel par jama dikhega — Solar ki request lagate time
/// utna amount kam lagega."
///
/// The user panel already treats this as a credit against the project. The admin
/// panel has to agree, or Payment Verification shows a due that the member has in
/// fact already paid — which is exactly what it did before this existed.
///
/// Only AlreadyActiveOnlyRequest draws on it: a With-Activation member pays for
/// their product as part of the same submission, so counting it again would be
/// double-crediting.
/// </summary>
public interface IActiveIdDepositService
{
    /// <summary>Approved legacy order total for one member id. 0 when there is none.</summary>
    Task<decimal> GetForMemberAsync(string memberIdNo);

    /// <summary>
    /// requestId → deposit, for the Already-Active requests among those given.
    /// Requests of any other type are simply absent from the result.
    /// </summary>
    Task<Dictionary<int, decimal>> GetForRequestsAsync(IEnumerable<Domain.Entities.SolarRequest> requests);
}
