using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Services;

/// <summary>
/// Checks an installation against the fixed 13-item checklist (IncUploadFormats).
///
/// This is a copy of the installer panel's InstallationChecklist and MUST stay in
/// step with it: the installer panel uses the same rules to accept a submission,
/// and the admin uses them to gate approval and the INC commission. Per item:
///   • photos (tagged with that item's FormatItemId) between MinPhotos and MaxPhotos
///     — an item with MaxPhotos = 0 takes no photos;
///   • at least VideoCount videos;
///   • written details when RemarkRequired (latest non-deleted Remark entry).
///
/// Callers must pass only non-deleted rows; the DbContext query filters do that.
/// </summary>
public static class InstallationChecklist
{
    /// <summary>What has been filed for one checklist item, plus what is still wrong with it.</summary>
    public sealed class LineStatus
    {
        public IncUploadFormat Format { get; init; } = null!;
        public int Photos { get; init; }
        public int Videos { get; init; }
        public string? Remark { get; init; }
        public string? Problem { get; init; }
        public bool IsComplete => Problem == null;
    }

    public static List<LineStatus> Evaluate(
        IEnumerable<IncUploadFormat> formats,
        IEnumerable<InstallationPhoto> photos,
        IEnumerable<InstallationChecklistEntry> entries)
    {
        var photoList = photos.ToList();
        var entryList = entries.ToList();

        return formats.Where(f => f.IsActive).OrderBy(f => f.SrNo).Select(f =>
        {
            var p = photoList.Count(x => x.FormatItemId == f.Id);
            var v = entryList.Count(x => x.FormatItemId == f.Id && x.EntryType == ChecklistEntryType.Video);
            var remark = entryList
                .Where(x => x.FormatItemId == f.Id && x.EntryType == ChecklistEntryType.Remark)
                .OrderByDescending(x => x.Id)
                .Select(x => x.RemarkText)
                .FirstOrDefault();
            return new LineStatus
            {
                Format = f,
                Photos = p,
                Videos = v,
                Remark = remark,
                Problem = ProblemFor(f, p, v, remark)
            };
        }).ToList();
    }

    /// <summary>Why this item is not complete yet, or null when it is.</summary>
    public static string? ProblemFor(IncUploadFormat f, int photos, int videos, string? remark)
    {
        var issues = new List<string>();
        if (f.MaxPhotos > 0)
        {
            if (photos < f.MinPhotos)
                issues.Add($"at least {f.MinPhotos} photo(s) needed, {photos} uploaded");
            else if (photos > f.MaxPhotos)
                issues.Add($"maximum {f.MaxPhotos} photo(s) allowed, {photos} uploaded");
        }
        if (f.VideoCount > 0 && videos < f.VideoCount)
            issues.Add(f.VideoCount == 1 ? "video is required" : $"{f.VideoCount} videos are required");
        if (f.RemarkRequired && string.IsNullOrWhiteSpace(remark))
            issues.Add("detail is required");

        return issues.Count == 0 ? null : $"{f.SrNo}. {f.Work} — {string.Join(", ", issues)}";
    }

    /// <summary>All problems, in item order. Empty = the whole checklist is filled.</summary>
    public static List<string> Problems(IEnumerable<LineStatus> lines) =>
        lines.Where(l => !l.IsComplete).Select(l => l.Problem!).ToList();

    /// <summary>
    /// An installation submitted before the checklist existed: no photo is tagged
    /// with a checklist item and no video / written-details entry exists. Such
    /// installations have no checklist and keep the old "at least one photo" rule.
    /// </summary>
    public static bool IsLegacy(IEnumerable<InstallationPhoto> photos, IEnumerable<InstallationChecklistEntry> entries) =>
        !photos.Any(p => p.FormatItemId.HasValue) && !entries.Any();
}
