using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class SiteSurvey : BaseEntity
{
    public int SolarRequestId { get; set; }
    public string? AssignedToUserId { get; set; }
    public DateTime? SurveyDate { get; set; }

    // ──────── Section 1: General Information (PDF) ────────
    public string? PropertyType { get; set; }      // "Residential" | "Commercial"
    public string? RoofAvailable { get; set; }     // "Yes" | "No" | "Partial"
    public string? RoofType { get; set; }          // "RCC" | "TinShed" | "TileRoof" | "Other"
    public string? RoofTypeOther { get; set; }
    public decimal? RoofTotalAreaSqft { get; set; }

    // ──────── Section 2: Electrical / Connection (PDF) ────────
    public string? DiscomName { get; set; }
    public string? ConsumerNumber { get; set; }
    public string? MeterType { get; set; }         // "SinglePhase" | "ThreePhase"
    public string? GridType { get; set; }          // "OnGrid" | "OffGrid" | "Hybrid"

    // ──────── Section 3: Site Conditions (PDF) ────────
    public string? ShadowOnRoof { get; set; }      // "Yes" | "No" | "Partial"
    public string? Obstructions { get; set; }      // CSV: "PaaniTanki,Makan,MeterArea"
    public string? RoofDirection { get; set; }     // "South" | "EastWest" | "Other"
    public string? RoofDirectionOther { get; set; }
    public string? EarthingAvailable { get; set; } // "Yes" | "No"
    public string? InternetAvailable { get; set; } // "Yes" | "No"

    // ──────── Section 4: Photos & Notes ────────
    public string? GpsPhotoPath { get; set; }
    public string? RoofPhotoPath { get; set; }
    public string? SurveyNotes { get; set; }
    public string? SurveyPhotoPath { get; set; }   // legacy combined path
    public string? OperationsRemark { get; set; }

    // ──────── Status ────────
    public bool IsCompleted { get; set; } = false;
    public DateTime? CompletedAt { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
    public virtual ApplicationUser? AssignedTo { get; set; }
}
