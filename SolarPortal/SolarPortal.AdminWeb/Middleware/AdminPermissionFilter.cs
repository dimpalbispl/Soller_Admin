using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.AdminWeb.Middleware;

/// <summary>
/// Enforces the admin → user permission grid on every request into the
/// SolarPanelAdmin area.
///
/// The grid is opt-in: an admin with no rows configured is unrestricted, so
/// switching this on cannot lock anyone out of a screen they were already using.
/// Once a user has rows, only the ticked menus are reachable — including by
/// typed URL, which is why this lives in a filter and not only in the sidebar.
///
/// A denied request goes to AccessDenied rather than 404, so the admin sees a
/// clear "you don't have this menu" instead of a page that looks broken.
/// </summary>
public class AdminPermissionFilter : IAsyncActionFilter
{
    private readonly IAdminPermissionService _permissions;
    private readonly UserManager<ApplicationUser> _userManager;

    public AdminPermissionFilter(IAdminPermissionService permissions, UserManager<ApplicationUser> userManager)
    {
        _permissions = permissions;
        _userManager = userManager;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var route = context.RouteData.Values;
        var area = route["area"]?.ToString();

        // Only the admin area is governed; the login pages and anything anonymous
        // are none of this filter's business.
        if (!string.Equals(area, "SolarPanelAdmin", StringComparison.OrdinalIgnoreCase) ||
            context.HttpContext.User?.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        // SuperAdmin is the escape hatch: whoever configures permissions must not
        // be able to lock themselves out of the screen that configures them.
        if (context.HttpContext.User.IsInRole("SuperAdmin"))
        {
            await next();
            return;
        }

        var userName = _userManager.GetUserId(context.HttpContext.User);
        if (string.IsNullOrWhiteSpace(userName))
        {
            await next();
            return;
        }


        var viewable = await _permissions.GetViewableAsync(userName);
        if (viewable == null)          // not configured → unrestricted
        {
            await next();
            return;
        }

        var controller = route["controller"]?.ToString() ?? "";
        var action = route["action"]?.ToString() ?? "";
        var key = AdminMenus.KeyFor(controller, action);

        // No key means the target is not a menu we govern (a shared partial
        // endpoint, a file stream, and so on). Those stay open — the menus they
        // hang off are already gated.
        if (key == null || viewable.Contains(key))
        {
            await next();
            return;
        }

        context.Result = new RedirectToActionResult("AccessDenied", "Account", new { area = "" });
    }
}
