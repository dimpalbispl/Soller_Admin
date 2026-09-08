using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Services;

/// <summary>One member as the legacy MLM DB knows them.</summary>
public class SponsorMemberDto
{
    public decimal FormNo { get; set; }
    public string IdNo { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    /// <summary>Sponsor's IdNo (m_membermaster.RefFormNo -> that row's IdNo). Null at the tree root.</summary>
    public string? SponsorIdNo { get; set; }
    public string? SponsorName { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>Result of checking a typed-in IdNo against a member's sponsor tree.</summary>
public class SponsorRelationResult
{
    public SponsorRelation Relation { get; set; } = SponsorRelation.NotFound;

    /// <summary>Name of the typed-in member, when one was found.</summary>
    public string? Name { get; set; }

    /// <summary>The IdNo exactly as m_membermaster stores it.</summary>
    public string? IdNo { get; set; }

    /// <summary>1 = direct sponsor, 2 = sponsor's sponsor, ... Only set for Upline.</summary>
    public int Level { get; set; }

    /// <summary>The only relation a Remaining-BV income head accepts.</summary>
    public bool IsAcceptable => Relation == SponsorRelation.Upline;

    /// <summary>Ready-to-show message.</summary>
    public string Message => Relation switch
    {
        SponsorRelation.Upline    => $"Verified: {Name} ({IdNo}) is in the sponsor upline, at level {Level}.",
        SponsorRelation.Self      => "This is the member's own ID, which can only be used in 'Self' mode.",
        SponsorRelation.Downline  => $"{Name} ({IdNo}) is in the downline. Only an upline ID is allowed.",
        SponsorRelation.Unrelated => $"{Name} ({IdNo}) is not in the sponsor upline.",
        _                         => "This ID number was not found."
    };
}

/// <summary>
/// Reads the SPONSOR tree out of the legacy MLM tables (m_membermaster) with raw
/// ADO.NET - same pattern as BasicProductService / LiveDbAuthBridge.
///
/// The sponsor link is <c>m_membermaster.RefFormNo -&gt; m_membermaster.FormNo</c>.
/// It is NOT <c>UplnFormNo</c>, which is the binary PLACEMENT upline and answers a
/// different question. The legacy VB confirms the mapping:
/// <c>Left Join M_MemberMaster c ON a.RefFormno = c.Formno ... c.Idno AS SponsorId</c>
/// (Dt.aspx.vb, GetMemDetails).
/// </summary>
public interface ISponsorTreeService
{
    /// <summary>The member plus their direct sponsor. Null when the IdNo is unknown.</summary>
    Task<SponsorMemberDto?> GetMemberAsync(string idNo);

    /// <summary>
    /// Where <paramref name="candidateIdNo"/> sits relative to <paramref name="memberIdNo"/>
    /// in the sponsor tree. Enforces "must be up, never down".
    /// </summary>
    Task<SponsorRelationResult> CheckRelationAsync(string memberIdNo, string candidateIdNo);
}
