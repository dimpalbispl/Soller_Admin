using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// "Back to Main" - signs the admin straight into the old VB admin panel.
///
/// Same hand-off the VB BackToMain.aspx uses: "uid=..&amp;pwd=.." encrypted with
/// the legacy Crypto class (TripleDES/ECB, key = MD5 of the shared string), plus
/// an ID of Day+Hour+Year+(Month-1) that the VB login page compares against its
/// own clock. The VB Default.aspx decrypts lgnT and runs enterHomePg(uid, pwd).
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin,SuperAdmin")]
public class BackToMainController : Controller
{
    private const string LegacyKey = "sg75b79-nj48dh02";

    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _config;

    public BackToMainController(ApplicationDbContext db, UserManager<ApplicationUser> userManager, IConfiguration config)
    {
        _db = db;
        _userManager = userManager;
        _config = config;
    }

    // GET: /SolarPanelAdmin/BackToMain
    public async Task<IActionResult> Index()
    {
        var loginUrl = _config["LegacyAdminPanel:LoginUrl"];
        if (string.IsNullOrWhiteSpace(loginUrl))
        {
            TempData["Warning"] = "Main panel URL is not configured (LegacyAdminPanel:LoginUrl).";
            return RedirectToAction("Index", "Dashboard");
        }

        // Bridged admins carry their m_usermaster UserName as the Identity Id.
        var userName = _userManager.GetUserId(User)?.Trim();
        var admin = string.IsNullOrWhiteSpace(userName)
            ? null
            : await _db.AdminUsers.AsNoTracking()
                .Where(u => u.UserName != null && u.Passw != null && u.UserName.Trim() == userName)
                .OrderByDescending(u => u.ActiveStatus == "Y")
                .FirstOrDefaultAsync();

        // An Identity-only admin has no legacy password to hand over - just open
        // the main panel's login page.
        if (admin == null)
            return Redirect(loginUrl);

        var lgnT = Encrypt($"uid={admin.UserName!.Trim()}&pwd={admin.Passw!.Trim()}");
        var now = DateTime.Now;
        var id = $"{now.Day}{now.Hour}{now.Year}{now.Month - 1}";

        var sep = loginUrl.Contains('?') ? "&" : "?";
        return Redirect($"{loginUrl}{sep}lgnT={Uri.EscapeDataString(lgnT)}&ID={id}");
    }

    // Byte-for-byte the VB Crypto.Encrypt: ASCII text, TripleDES ECB/PKCS7,
    // key = MD5(ASCII key), Base64 output.
    private static string Encrypt(string value)
    {
        using var des = TripleDES.Create();
        des.Key = MD5.HashData(Encoding.ASCII.GetBytes(LegacyKey));
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.PKCS7;
        var buffer = Encoding.ASCII.GetBytes(value);
        return Convert.ToBase64String(des.CreateEncryptor().TransformFinalBlock(buffer, 0, buffer.Length));
    }
}
