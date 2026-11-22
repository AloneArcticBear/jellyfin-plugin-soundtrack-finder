// #claude
using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SoundtrackFinder.Api;

/// <summary>
/// Serves the "Soundtracks" page for all users: https://&lt;jellyfin&gt;/SoundtrackFinder/App.
/// The page and its scripts contain no data and are public; all data is loaded with the
/// user's Jellyfin login (SoundtrackController).
/// </summary>
[ApiController]
[Route("SoundtrackFinder")]
[AllowAnonymous]
public class AppPageController : ControllerBase
{
    private static readonly string Ns = typeof(Plugin).Namespace + ".Web.";

    /// <summary>The page.</summary>
    [HttpGet("App")]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult App()
    {
        var basePath = Request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
        var template = Resource("app.html");
        var admin = Resource("configPage.html");

        // reuse the markup of the dashboard page, in user mode and hidden until the login is checked
        int start = admin.IndexOf("<div id=\"SoundtrackFinderPage\"", StringComparison.Ordinal);
        int end = admin.LastIndexOf("</body>", StringComparison.Ordinal);
        var page = admin[start..end];
        page = page.Replace("<div id=\"SoundtrackFinderPage\"", "<div id=\"SoundtrackFinderPage\" data-mode=\"user\" style=\"display:none\"", StringComparison.Ordinal);
        page = Regex.Replace(page, "\\s(data-role|data-require|data-controller)=\"[^\"]*\"", string.Empty);

        var html = template
            .Replace("{{BASE}}", WebUtility.HtmlEncode(basePath + "/SoundtrackFinder/"), StringComparison.Ordinal)
            .Replace("{{WEB}}", WebUtility.HtmlEncode(basePath + "/web/"), StringComparison.Ordinal)
            .Replace("{{PAGE}}", page, StringComparison.Ordinal);
        Response.Headers.CacheControl = "no-cache";
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>Texts of the page in a language (de, en, fr, es, it; missing texts in English/German).</summary>
    [HttpGet("I18n/{lang}")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult I18n([FromRoute] string lang)
    {
        Response.Headers.CacheControl = "no-cache";
        return new JsonResult(Services.Loc.Dictionary(lang));
    }

    /// <summary>Start script of the page.</summary>
    [HttpGet("AppScript")]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult AppScript() => Script("appStart.js");

    /// <summary>Timeline code (shared with the dashboard page).</summary>
    [HttpGet("AppTimeline")]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult AppTimeline() => Script("configPage.js");

    private ContentResult Script(string name)
    {
        Response.Headers.CacheControl = "no-cache";
        return Content(Resource(name), "text/javascript; charset=utf-8");
    }

    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Ns + name)
            ?? throw new FileNotFoundException(Ns + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
