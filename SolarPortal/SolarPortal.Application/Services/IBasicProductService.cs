namespace SolarPortal.Application.Services;

/// <summary>
/// DTO mirroring the columns we surface to the UI from V#SpProductDetail.
/// Lives in Application so views/controllers can reference it without taking
/// a dependency on Infrastructure (which would be a backwards layering).
/// </summary>
public class BasicProductDto
{
    public int ProdId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal MRP { get; set; }
    public decimal DP { get; set; }      // Dealer Price — payable amount per spec
    public decimal BV { get; set; }
    public int CatId { get; set; }
    public decimal Stock { get; set; }
}

/// <summary>
/// Reads "basic products" for the With Activation flow from the legacy
/// V#SpProductDetail view. The implementation (raw ADO.NET) lives in
/// Infrastructure because Application has no EF / Microsoft.Data.SqlClient
/// references — keeping the layering clean.
///
/// Per spec: when user picks "With Activation" mode, the form shows these
/// products as cards. User picks one, and we store its ProdId on the
/// SolarRequest.ExternalProductId column with DP as the PlanAmount.
/// </summary>
public interface IBasicProductService
{
    Task<List<BasicProductDto>> GetActiveAsync();
    Task<BasicProductDto?> GetByIdAsync(int prodId);
}
