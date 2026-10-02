using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Helpers;

/// <summary>
/// Resolves a Member ID to the member and all their project(s) for the Add Fund
/// form — the JSON both Payment Verification and the (now unlinked) Add Fund page
/// call before an amount can be entered.
///
/// It lives here, and not in either controller, because Add Fund was folded into
/// Payment Verification: two entry points asking the same question must not answer
/// it differently.
/// </summary>
public static class MemberFundLookup
{
    /// <summary>
    /// The member is looked up in m_membermaster FIRST, separately from their solar
    /// requests. Checking only SolarRequests reported "No member found" for a real
    /// member who simply has not filed a solar request yet — which sent the admin
    /// hunting for a typo that was never there.
    /// </summary>
    public static async Task<object> ResolveAsync(
        string? memberId, ApplicationDbContext db, IUnitOfWork uow, IPaymentService payments)
    {
        var id = (memberId ?? string.Empty).Trim();
        if (id.Length == 0)
            return new { found = false, message = "Enter a Member ID." };

        // Trim BOTH sides: legacy columns are frequently CHAR-padded, and an exact
        // == against a padded value is the classic silent no-match.
        var member = await db.Members.AsNoTracking()
                             .FirstOrDefaultAsync(m => m.IdNo != null && m.IdNo.Trim() == id);

        var requests = (await uow.SolarRequests.FindAsync(r => r.UserId != null && r.UserId.Trim() == id))
                       .OrderByDescending(r => r.CreatedAt)
                       .ToList();

        if (member == null && requests.Count == 0)
            return new { found = false, message = $"No member found with ID '{id}'. Check the spelling." };

        // Every project is offered, completed ones included: a fund can be added at
        // any time, whatever stage the project is at.
        var live = requests;

        if (live.Count == 0)
        {
            var who = member != null ? $" ({member.FullName})" : "";
            return new { found = false, message = $"Member {id}{who} exists but has no solar request yet — a fund needs a request to attach to." };
        }

        var rows = new List<object>();
        foreach (var r in live)
        {
            var paid = await payments.GetVerifiedPaidAsync(r.Id);
            rows.Add(new
            {
                id = r.Id,
                requestNumber = r.RequestNumber,
                plan = r.SelectedPlan,
                total = r.PlanAmount,
                paid,
                due = Math.Max(0m, r.PlanAmount - paid),
                stage = r.CurrentStage.ToString()
            });
        }

        var first = live[0];
        var name = member?.FullName;
        if (string.IsNullOrWhiteSpace(name))
            name = string.IsNullOrWhiteSpace(first.MemberFullName) ? first.ApplicantName : first.MemberFullName;

        return new
        {
            found = true,
            memberId = id,
            name,
            mobile = member?.Mobl?.ToString("0") ?? first.MobileNumber,
            city = member?.City ?? first.City,
            requests = rows
        };
    }
}
