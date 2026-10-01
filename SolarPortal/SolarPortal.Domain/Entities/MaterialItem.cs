using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

/// <summary>
/// Master list of materials that can go out on a dispatch (Solar Module, Inverter,
/// GP Pipe, ...). Prepare for Dispatch loads every active row and asks for a
/// quantity against each. Seeded by ADD-MaterialItems.sql.
/// </summary>
public class MaterialItem : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
