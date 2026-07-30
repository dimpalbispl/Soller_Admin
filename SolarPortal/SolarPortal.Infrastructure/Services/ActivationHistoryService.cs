using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Assembles the Activation History report. See IActivationHistoryService for
/// the why; this file is the how.
///
/// Timestamp caveat (deliberate, documented rather than silently "fixed"):
/// portal rows carry DateTime.UtcNow values while the legacy MLM rows carry
/// SQL Server GETDATE() (server-local). Both are rendered as stored — same as
/// every other admin screen. Events minutes apart across that boundary can
/// therefore sort slightly out of order; events days apart (which is what this
/// report is about) are unaffected.
/// </summary>
public class ActivationHistoryService : IActivationHistoryService
{
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<ActivationHistoryService> _log;

    public ActivationHistoryService(
        ApplicationDbContext db,
        IConfiguration config,
        ILogger<ActivationHistoryService> log)
    {
        _db = db;
        _config = config;
        _log = log;
    }

    public async Task<IReadOnlyList<MemberActivationHistoryDto>> GetAllAsync()
        => await BuildAsync(null);

    public async Task<MemberActivationHistoryDto?> GetByMemberIdAsync(string memberIdNo)
    {
        if (string.IsNullOrWhiteSpace(memberIdNo)) return null;
        var rows = await BuildAsync(memberIdNo.Trim());
        return rows.FirstOrDefault();
    }

    private async Task<List<MemberActivationHistoryDto>> BuildAsync(string? onlyMemberIdNo)
    {
        var query = _db.SolarRequests.AsNoTracking();
        if (onlyMemberIdNo != null)
            query = query.Where(r => r.UserId == onlyMemberIdNo);

        var candidates = await query.ToListAsync();
        if (candidates.Count == 0) return new List<MemberActivationHistoryDto>();

        // Payments are pulled whole and joined in memory — the admin Payments
        // page already loads the full ledger, and this avoids an IN(...) list
        // that could blow past SQL Server's 2100-parameter cap.
        var allPayments = await _db.Payments.AsNoTracking().ToListAsync();
        var paymentsByRequest = allPayments
            .GroupBy(p => p.SolarRequestId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.PaymentDate).ToList());

        // ── Drop ONLY the untouched registration stub ────────────────────────
        // EVERY mode — With Activation, Without Activation, Active Now — is read
        // from SolarRequests; the legacy table only supplies the activation date.
        // So this test has to stay conservative, otherwise a genuinely submitted
        // request disappears from the report. A request counts as real if it has
        // a plan, a product, a capacity, an amount, ANY payment, an admin
        // decision, or has reached Completed. Earlier this checked only
        // plan/product/capacity/amount, so a submitted request whose amount
        // happened to be 0 would have been silently hidden.
        var requests = candidates.Where(r =>
            r.SolarProjectId != null ||
            r.ExternalProductId != null ||
            r.KVCapacity != 0m ||
            r.PlanAmount != 0m ||
            r.CurrentStage == ProjectStatus.Completed ||
            r.ApprovalStatus != ApprovalStatus.Pending ||
            paymentsByRequest.ContainsKey(r.Id)).ToList();

        if (requests.Count == 0) return new List<MemberActivationHistoryDto>();

        var memberIds = requests
            .Select(r => (r.UserId ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var memberMap = await LoadMembersAsync(memberIds);
        var legacyMap = await LoadLegacyActivationsAsync(memberIds);

        var result = new List<MemberActivationHistoryDto>();

        foreach (var grp in requests.GroupBy(r => (r.UserId ?? string.Empty).Trim(),
                                             StringComparer.OrdinalIgnoreCase))
        {
            var memberIdNo = grp.Key;
            if (memberIdNo.Length == 0) continue;

            var ordered = grp.OrderBy(r => r.CreatedAt).ToList();
            var latest = ordered[^1];
            memberMap.TryGetValue(memberIdNo, out var member);

            var dto = new MemberActivationHistoryDto
            {
                MemberIdNo = memberIdNo,
                MemberName = !string.IsNullOrWhiteSpace(member?.FullName)
                    ? member!.FullName
                    : latest.ApplicantName,
                Mobile = latest.MobileNumber,
                City = latest.City,
                State = latest.State,
                IsActiveNow = string.Equals(member?.ActiveStatus?.Trim(), "Y",
                                            StringComparison.OrdinalIgnoreCase),
                TotalRequests = ordered.Count,
                FirstRequestOn = ordered[0].CreatedAt,
                LastRequestOn = latest.CreatedAt,
                TotalProjectAmount = ordered.Sum(r => r.PlanAmount)
            };

            // ─── Portal-side events: requests + their money trail ────────────
            foreach (var r in ordered)
            {
                var payments = paymentsByRequest.TryGetValue(r.Id, out var pl)
                    ? pl
                    : new List<Payment>();
                var verifiedPaid = payments.Where(p => p.IsVerified).Sum(p => p.Amount);

                dto.Requests.Add(new ActivationRequestRowDto
                {
                    Id = r.Id,
                    RequestNumber = r.RequestNumber,
                    RequestType = r.RequestType,
                    CreatedAt = r.CreatedAt,
                    SelectedPlan = r.SelectedPlan,
                    PlanAmount = r.PlanAmount,
                    VerifiedPaid = verifiedPaid,
                    CurrentStage = r.CurrentStage,
                    ApprovalStatus = r.ApprovalStatus,
                    OriginalRequestType = r.OriginalRequestType,
                    WithoutActivationOn = r.WithoutActivationOn,
                    WithActivationOn = r.WithActivationOn,
                    AlreadyActiveOn = r.AlreadyActiveOn
                });

                // The mode the request was SUBMITTED in - r.RequestType is the
                // mode it is in NOW, which "Activate Now" may have overwritten.
                var submittedAs = r.OriginalRequestType ?? r.RequestType;

                dto.Events.Add(new ActivationEventDto
                {
                    When = r.CreatedAt,
                    Kind = ActivationEventKind.RequestSubmitted,
                    Title = $"Request submitted — {ModeLabel(submittedAs)}",
                    Detail = BuildRequestDetail(r),
                    Amount = r.PlanAmount > 0 ? r.PlanAmount : null,
                    Reference = r.RequestNumber
                });

                // The upgrade itself. It happens on the SAME row with the same
                // request number, so without this the timeline jumps from
                // "Without Activation" to the legacy activation order with
                // nothing explaining the switch.
                if (r.OriginalRequestType == RequestType.OnlySolarWithoutActivation
                    && r.WithActivationOn.HasValue)
                {
                    dto.Events.Add(new ActivationEventDto
                    {
                        When = r.WithActivationOn.Value,
                        Kind = ActivationEventKind.ActivationRequested,
                        Title = "Upgraded to With Activation (Activate Now)",
                        Detail = $"Without Activation taken on " +
                                 $"{(r.WithoutActivationOn ?? r.CreatedAt):dd MMM yyyy} — product picked here.",
                        Amount = r.PlanAmount > 0 ? r.PlanAmount : null,
                        Reference = r.RequestNumber
                    });
                }

                foreach (var p in payments)
                {
                    // A decided payment gets ONE row (verified / rejected) with the
                    // submission date in the detail — emitting "submitted" as well
                    // would double every line for no extra information.
                    if (p.IsVerified)
                    {
                        dto.Events.Add(new ActivationEventDto
                        {
                            When = p.VerifiedAt ?? p.PaymentDate,
                            Kind = ActivationEventKind.PaymentVerified,
                            Title = "Payment verified by admin",
                            Detail = $"{r.RequestNumber} · submitted {p.PaymentDate:dd MMM yyyy}" +
                                     (string.IsNullOrWhiteSpace(p.PaymentMethod) ? "" : $" · {p.PaymentMethod}"),
                            Amount = p.Amount,
                            Reference = p.UTRNumber
                        });
                    }
                    else if (p.Status == PaymentStatus.Rejected)
                    {
                        dto.Events.Add(new ActivationEventDto
                        {
                            When = p.VerifiedAt ?? p.UpdatedAt ?? p.PaymentDate,
                            Kind = ActivationEventKind.PaymentRejected,
                            Title = "Payment rejected by admin",
                            Detail = $"{r.RequestNumber} · submitted {p.PaymentDate:dd MMM yyyy}",
                            Amount = p.Amount,
                            Reference = p.UTRNumber
                        });
                    }
                    else
                    {
                        dto.Events.Add(new ActivationEventDto
                        {
                            When = p.PaymentDate,
                            Kind = ActivationEventKind.PaymentSubmitted,
                            Title = "Payment submitted — awaiting verification",
                            Detail = r.RequestNumber,
                            Amount = p.Amount,
                            Reference = p.UTRNumber
                        });
                    }
                }
            }

            dto.TotalVerifiedPaid = dto.Requests.Sum(r => r.VerifiedPaid);
            dto.FirstWithoutActivationOn = FirstDateOf(ordered, RequestType.OnlySolarWithoutActivation);
            dto.FirstWithActivationOn = FirstDateOf(ordered, RequestType.WithActivation);
            dto.FirstAlreadyActiveOn = FirstDateOf(ordered, RequestType.AlreadyActiveOnlyRequest);

            // ─── Legacy MLM activation leg ───────────────────────────────────
            // One legacy ORDER can span several detail rows; collapse to one
            // activation event per OrderNo.
            if (legacyMap.TryGetValue(memberIdNo, out var legacyRows))
            {
                foreach (var order in legacyRows.GroupBy(x => x.OrderNo ?? string.Empty))
                {
                    var head = order.OrderBy(x => x.RequestedOn ?? DateTime.MaxValue).First();
                    var amount = order.Sum(x => x.Amount);
                    var orderRef = string.IsNullOrWhiteSpace(head.OrderNo) ? null : $"Order {head.OrderNo}";

                    if (head.RequestedOn.HasValue)
                    {
                        dto.Events.Add(new ActivationEventDto
                        {
                            When = head.RequestedOn.Value,
                            Kind = ActivationEventKind.ActivationRequested,
                            Title = "Activation order placed in MLM system",
                            Detail = head.ProductName,
                            Amount = amount > 0 ? amount : null,
                            Reference = orderRef
                        });
                    }

                    if (head.IsApprove == "Y")
                    {
                        var when = head.ApprovedOn ?? head.RequestedOn;
                        if (when.HasValue)
                        {
                            dto.Events.Add(new ActivationEventDto
                            {
                                When = when.Value,
                                Kind = ActivationEventKind.Activated,
                                Title = "ID ACTIVATED",
                                Detail = "Legacy activation order is marked approved (TrnProductorderDetail.IsApprove = 'Y').",
                                Amount = amount > 0 ? amount : null,
                                Reference = orderRef,
                                IsApproximate = !head.ApprovedOn.HasValue
                            });
                        }
                    }
                    else if (head.IsApprove == "R")
                    {
                        var when = head.ApprovedOn ?? head.RequestedOn;
                        if (when.HasValue)
                        {
                            dto.Events.Add(new ActivationEventDto
                            {
                                When = when.Value,
                                Kind = ActivationEventKind.ActivationRejected,
                                Title = "Activation order rejected",
                                Reference = orderRef,
                                IsApproximate = !head.ApprovedOn.HasValue
                            });
                        }
                    }
                }
            }

            var activationEvent = dto.Events
                .Where(e => e.Kind == ActivationEventKind.Activated)
                .OrderBy(e => e.When)
                .FirstOrDefault();

            if (activationEvent != null)
            {
                dto.ActivatedOn = activationEvent.When;
                dto.ActivatedOnApproximate = activationEvent.IsApproximate;
            }
            else if (dto.IsActiveNow)
            {
                // The member IS active in m_membermaster but we found no legacy
                // activation row (legacy DB unreachable, or the ID was activated
                // outside this portal). Fall back to the verified payment that
                // would have triggered it, and mark the date approximate so the
                // report never presents a guess as an audited fact.
                var trigger = FindActivationTriggerPayment(ordered, paymentsByRequest);
                if (trigger != null)
                {
                    dto.ActivatedOn = trigger.VerifiedAt ?? trigger.PaymentDate;
                    dto.ActivatedOnApproximate = true;
                    dto.Events.Add(new ActivationEventDto
                    {
                        When = dto.ActivatedOn.Value,
                        Kind = ActivationEventKind.Activated,
                        Title = "ID ACTIVATED",
                        Detail = "No legacy activation record found — date inferred from the verified payment that triggers activation.",
                        Reference = trigger.UTRNumber,
                        IsApproximate = true
                    });
                }
            }

            dto.Events = dto.Events
                .OrderBy(e => e.When)
                .ThenBy(e => (int)e.Kind)
                .ToList();

            if (dto.ActivatedOn.HasValue)
            {
                foreach (var row in dto.Requests)
                    row.AfterActivation = row.CreatedAt >= dto.ActivatedOn.Value;
            }
            dto.RequestsAfterActivation = dto.Requests.Count(r => r.AfterActivation);
            dto.RequestsBeforeActivation = dto.TotalRequests - dto.RequestsAfterActivation;

            result.Add(dto);
        }

        return result
            .OrderByDescending(m => m.LastRequestOn ?? DateTime.MinValue)
            .ToList();
    }

    /// <summary>
    /// The payment that would have flipped the ID active: the first verified
    /// payment on a "With Activation" request, else the first verified payment
    /// on any request.
    /// </summary>
    private static Payment? FindActivationTriggerPayment(
        List<SolarRequest> ordered,
        Dictionary<int, List<Payment>> paymentsByRequest)
    {
        Payment? Pick(Func<SolarRequest, bool> match) => ordered
            .Where(match)
            .SelectMany(r => paymentsByRequest.TryGetValue(r.Id, out var pl) ? pl : new List<Payment>())
            .Where(p => p.IsVerified)
            .OrderBy(p => p.VerifiedAt ?? p.PaymentDate)
            .FirstOrDefault();

        return Pick(r => r.RequestType == RequestType.WithActivation) ?? Pick(_ => true);
    }

    /// <summary>
    /// Earliest date the member entered <paramref name="type"/>, across all their
    /// requests.
    ///
    /// Reads the per-mode stamps (WithoutActivationOn / WithActivationOn /
    /// AlreadyActiveOn) rather than RequestType. "Activate Now" OVERWRITES
    /// RequestType on the row it upgrades, so a RequestType scan reports zero
    /// Without-Activation requests for exactly the members this report exists
    /// for. CreatedAt is the fallback for rows written before the stamps existed.
    /// </summary>
    private static DateTime? FirstDateOf(List<SolarRequest> ordered, RequestType type)
    {
        var dates = new List<DateTime>();
        foreach (var r in ordered)
        {
            var stamp = type switch
            {
                RequestType.OnlySolarWithoutActivation => r.WithoutActivationOn,
                RequestType.WithActivation => r.WithActivationOn,
                RequestType.AlreadyActiveOnlyRequest => r.AlreadyActiveOn,
                _ => null
            };

            if (stamp.HasValue) dates.Add(stamp.Value);
            else if (r.OriginalRequestType == null && r.RequestType == type) dates.Add(r.CreatedAt);
        }
        return dates.Count == 0 ? null : dates.Min();
    }

    private static string ModeLabel(RequestType type) => type switch
    {
        RequestType.WithActivation => "With Activation",
        RequestType.OnlySolarWithoutActivation => "Without Activation",
        RequestType.AlreadyActiveOnlyRequest => "Active Now (Already Active)",
        _ => type.ToString()
    };

    private static string BuildRequestDetail(SolarRequest r)
    {
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.SelectedPlan)) bits.Add(r.SelectedPlan!);
        if (r.KVCapacity > 0) bits.Add($"{r.KVCapacity} kV");
        bits.Add(r.CurrentStage.ToString());
        return string.Join(" · ", bits);
    }

    private async Task<Dictionary<string, MMemberMaster>> LoadMembersAsync(List<string> memberIds)
    {
        var map = new Dictionary<string, MMemberMaster>(StringComparer.OrdinalIgnoreCase);
        if (memberIds.Count == 0) return map;

        try
        {
            // Chunked so the IN(...) list stays well under SQL Server's
            // 2100-parameter limit on large member sets.
            foreach (var chunk in memberIds.Chunk(500))
            {
                var ids = chunk.ToList();
                var rows = await _db.Members.AsNoTracking()
                    .Where(m => m.IdNo != null && ids.Contains(m.IdNo.Trim()))
                    .ToListAsync();

                foreach (var m in rows)
                {
                    var key = m.IdNo!.Trim();
                    if (key.Length > 0) map[key] = m;
                }
            }
        }
        catch (Exception ex)
        {
            // Member master is decoration (name + live active flag) — the report
            // is still meaningful without it.
            _log.LogWarning(ex, "Activation History: m_membermaster lookup failed; falling back to portal applicant names.");
        }

        return map;
    }

    private async Task<Dictionary<string, List<LegacyActivationRow>>> LoadLegacyActivationsAsync(
        List<string> memberIds)
    {
        var map = new Dictionary<string, List<LegacyActivationRow>>(StringComparer.OrdinalIgnoreCase);
        if (memberIds.Count == 0) return map;

        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            _log.LogWarning("Activation History: no connection string for the legacy activation lookup.");
            return map;
        }

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            foreach (var chunk in memberIds.Chunk(500))
            {
                var names = Enumerable.Range(0, chunk.Length).Select(i => "@p" + i).ToArray();
                var sql = $@"
SELECT LTRIM(RTRIM(m.Idno)) AS IdNo,
       tpd.OrderNo, tpd.IsApprove, tpd.RecTimeStamp, tpd.Approvedate,
       tpd.NetAmount, tpd.ProductName
FROM TrnProductorderDetail tpd
INNER JOIN M_MemberMaster m ON m.Formno = tpd.FormNo
WHERE tpd.ForType = 'A'
  AND LTRIM(RTRIM(m.Idno)) IN ({string.Join(", ", names)})
ORDER BY tpd.RecTimeStamp;";

                await using var cmd = new SqlCommand(sql, conn);
                for (var i = 0; i < chunk.Length; i++)
                    cmd.Parameters.AddWithValue(names[i], chunk[i]);

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var idNo = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                    if (idNo.Length == 0) continue;

                    var row = new LegacyActivationRow
                    {
                        OrderNo = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1))?.Trim(),
                        IsApprove = (reader.IsDBNull(2) ? string.Empty : Convert.ToString(reader.GetValue(2)) ?? string.Empty)
                                    .Trim().ToUpperInvariant(),
                        RequestedOn = reader.IsDBNull(3) ? null : Convert.ToDateTime(reader.GetValue(3)),
                        ApprovedOn = reader.IsDBNull(4) ? null : Convert.ToDateTime(reader.GetValue(4)),
                        Amount = reader.IsDBNull(5) ? 0m : Convert.ToDecimal(reader.GetValue(5)),
                        ProductName = reader.IsDBNull(6) ? null : Convert.ToString(reader.GetValue(6))?.Trim()
                    };

                    if (!map.TryGetValue(idNo, out var list))
                    {
                        list = new List<LegacyActivationRow>();
                        map[idNo] = list;
                    }
                    list.Add(row);
                }
            }
        }
        catch (Exception ex)
        {
            // Best-effort by design: without the legacy leg the report still
            // shows the request/payment story and marks activation dates as
            // inferred.
            _log.LogWarning(ex,
                "Activation History: legacy TrnProductorderDetail lookup failed; falling back to portal data only.");
            return new Dictionary<string, List<LegacyActivationRow>>(StringComparer.OrdinalIgnoreCase);
        }

        return map;
    }

    private sealed class LegacyActivationRow
    {
        public string? OrderNo { get; init; }
        public string IsApprove { get; init; } = string.Empty;   // 'N' | 'Y' | 'R'
        public DateTime? RequestedOn { get; init; }
        public DateTime? ApprovedOn { get; init; }
        public decimal Amount { get; init; }
        public string? ProductName { get; init; }
    }
}
