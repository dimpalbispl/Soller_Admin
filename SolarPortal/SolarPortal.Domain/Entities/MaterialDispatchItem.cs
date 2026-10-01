using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

/// <summary>
/// One material line on a dispatch — how much of a <see cref="MaterialItem"/> is
/// going out. Only items given a quantity are stored. ItemName is a snapshot so
/// the dispatch still reads correctly if the master item is renamed later.
/// </summary>
public class MaterialDispatchItem : BaseEntity
{
    public int MaterialDispatchId { get; set; }
    public int MaterialItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string Quantity { get; set; } = string.Empty; // free text: "6", "20 m", "2 rolls"
}
