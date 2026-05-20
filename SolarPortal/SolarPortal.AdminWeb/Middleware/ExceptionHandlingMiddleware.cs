using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;

namespace SolarPortal.AdminWeb.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    // Loop-breaker cookie. Set when we redirect after an exception so the
    // next round detects repeated failure and shows a static page instead.
    private const string LoopMarker = ".SolarPortal.ErrRedir";

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITempDataDictionaryFactory tempDataFactory)
    {
        try
        {
            await _next(context);

            // On successful page load, clear the loop marker. Guarded against
            // HasStarted to avoid InvalidOperationException on rare cases where
            // a downstream component flushed the response before this point.
            if (!context.Response.HasStarted
                && context.Response.StatusCode < 400
                && context.Request.Cookies.ContainsKey(LoopMarker))
            {
                context.Response.Cookies.Delete(LoopMarker);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on {Path}: {Message}",
                context.Request.Path, ex.Message);

            // ════════════════════════════════════════════════════════════════
            // CRITICAL GUARD — response may have already started streaming.
            // ════════════════════════════════════════════════════════════════
            // If a Razor view threw MID-RENDER (very common for SolarRequest/Create
            // because the view streams >100 KB of HTML), headers are already on
            // the wire. Every one of these throws InvalidOperationException:
            //   - context.Response.StatusCode = ...
            //   - context.Response.ContentType = ...
            //   - context.Response.Cookies.Append/Delete(...)
            //   - context.Response.Redirect(...)
            //   - context.Response.Headers[...] = ...
            // The only safe thing we can do once HasStarted is true is either
            // (a) write a tiny bit more body content, or (b) abort the connection.
            // We choose (a): inject a small visible error block at the end of the
            // partial HTML stream so the user sees SOMETHING, then return.
            //
            // We also do NOT rethrow — rethrowing from middleware after the
            // response has started causes ASP.NET to log a second confusing
            // "response has already started" error, which is what we're trying
            // to eliminate in the first place.
            // ════════════════════════════════════════════════════════════════
            if (context.Response.HasStarted)
            {
                try
                {
                    // Best-effort: inject a visible error notice into the
                    // already-streaming response. The user will see whatever
                    // HTML rendered before the throw, plus this banner.
                    var safeMsg = WebUtility.HtmlEncode(ex.Message);
                    await context.Response.WriteAsync(
                        "\n<!-- exception during stream -->\n" +
                        "<div style=\"position:fixed;left:0;right:0;bottom:0;" +
                        "background:#dc2626;color:#fff;padding:14px 20px;" +
                        "font:14px system-ui,sans-serif;z-index:99999;" +
                        "box-shadow:0 -4px 12px rgba(0,0,0,.2)\">" +
                        "<strong>Server error mid-render.</strong> " +
                        "The page started loading but failed before completing. " +
                        "Please refresh and try again. " +
                        "<span style=\"opacity:.8;font-size:12px\">" + safeMsg + "</span>" +
                        "</div></body></html>");
                }
                catch
                {
                    // Even the best-effort write failed (connection dropped, buffer
                    // already disposed, etc). Nothing more to do — swallow and
                    // return cleanly so the framework doesn't log a phantom
                    // "response has already started" on top of our real exception.
                }
                return;
            }

            // ────────────────────────────────────────────────────────────────
            // Response has NOT started yet — we have full control.
            // ────────────────────────────────────────────────────────────────

            // AJAX/XHR → JSON error response
            if (context.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                context.Response.Clear();
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    success = false,
                    message = "An unexpected error occurred. Please try again."
                }));
                return;
            }

            // ===== Loop-breaker =====
            // If the request that just threw was itself the Dashboard, redirecting
            // there will throw again → infinite ERR_TOO_MANY_REDIRECTS. Detect:
            //   1. Was the failing path already the Dashboard?
            //   2. Or has the loop marker cookie been set from a prior failure?
            // In either case, render a static error page in-place (NO redirect).
            var path = context.Request.Path.Value ?? "";
            var alreadyOnDashboard =
                path.StartsWith("/SolarPanelAdmin/Dashboard", StringComparison.OrdinalIgnoreCase)
             || path.StartsWith("/SolarPanelAdmin/Dashboard",     StringComparison.OrdinalIgnoreCase)
             || path == "/" || string.IsNullOrEmpty(path);
            var hasLoopMarker = context.Request.Cookies.ContainsKey(LoopMarker);

            if (alreadyOnDashboard || hasLoopMarker)
            {
                // Render static error page directly — no redirect, no loop.
                // Response.Clear() must happen BEFORE setting status/content-type
                // to discard any partial buffer that the view started writing.
                context.Response.Clear();
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.ContentType = "text/html; charset=utf-8";

                if (hasLoopMarker)
                    context.Response.Cookies.Delete(LoopMarker);

                var detail = WebUtility.HtmlEncode(ex.GetType().Name + ": " + ex.Message);
                var inner  = WebUtility.HtmlEncode(ex.InnerException?.Message ?? "");
                var dbHint =
                    (ex.Message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase)
                     || (ex.InnerException?.Message?.Contains("Invalid column name",
                            StringComparison.OrdinalIgnoreCase) ?? false))
                    ? "<p style='background:#fef3c7;padding:10px;border-left:4px solid #f59e0b;color:#92400e'>" +
                      "<strong>Likely cause:</strong> a database column the app expects is missing. " +
                      "Run the migration SQL script (<code>MIGRATIONS/00_DEPLOY_ALL.sql</code>) " +
                      "against the SolfitEnergy database, then refresh.</p>"
                    : "";

                await context.Response.WriteAsync($@"<!DOCTYPE html>
<html><head><meta charset='UTF-8'><title>Application error</title>
<style>body{{font-family:system-ui,sans-serif;max-width:720px;margin:60px auto;padding:0 20px;color:#1f2937}}
h1{{color:#dc2626;margin-bottom:6px}}.muted{{color:#6b7280;font-size:13px}}
pre{{background:#f3f4f6;padding:12px;border-radius:6px;font-size:12px;overflow:auto;white-space:pre-wrap;word-break:break-word}}</style>
</head><body>
<h1>Something went wrong</h1>
<p class='muted'>Path: <code>{WebUtility.HtmlEncode(path)}</code></p>
{dbHint}
<p>Please refresh and try again. If the problem persists, share the path above
and the message below with the admin.</p>
<details open><summary>Error details</summary><pre>{detail}{(string.IsNullOrEmpty(inner) ? "" : "\n\nInner: " + inner)}</pre></details>
<p><a href='/Account/Login'>Go to Login</a> · <a href='javascript:history.back()'>Go back</a></p>
</body></html>");
                return;
            }

            // ───── Normal path: set loop marker, toast, redirect to Dashboard.
            // If THAT also throws, next round will hit the loop-breaker above.
            try
            {
                context.Response.Cookies.Append(LoopMarker, "1", new CookieOptions
                {
                    HttpOnly = true,
                    IsEssential = true,
                    MaxAge = TimeSpan.FromSeconds(15)   // short-lived, auto-clears
                });
            }
            catch
            {
                // If we can't even set the cookie, fall through and try to redirect anyway —
                // the loop-breaker fallback above (via path check) still catches Dashboard.
            }

            try
            {
                var temp = tempDataFactory.GetTempData(context);
                if (!temp.ContainsKey("Error") && !temp.ContainsKey("Warning"))
                {
                    temp["Error"] = "Something went wrong while processing your request. Please try again.";
                    temp.Save();
                }
            }
            catch
            {
                // TempData backing store may have been disposed if the exception
                // happened during session teardown. Non-fatal — skip the toast.
            }

            var redirectTo = context.User?.Identity?.IsAuthenticated == true
                ? "/SolarPanelAdmin/Dashboard"
                : "/Account/Login";
            context.Response.Redirect(redirectTo);
        }
    }
}
