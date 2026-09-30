using System.Net;
using System.Text;

namespace VoiceOS.Eval.Fixtures;

/// <summary>
/// Eval-only deterministic local website for Pass 2 proof testing: static HTML, a few redirect endpoints,
/// nothing that fakes browser state. Two origins share one listener so cross-origin behavior is real:
/// <c>http://localhost:PORT</c> (the site) and <c>http://127.0.0.1:PORT</c> (the "other" site / interstitial host).
/// Run with: <c>dotnet run --project tests/VoiceOS.Eval -- fixtures [--port 18777]</c>.
/// </summary>
public sealed class FixtureServer(int port) : IDisposable
{
    public const int DefaultPort = 18777;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _stop;

    public string Site => $"http://localhost:{port}";
    public string Other => $"http://127.0.0.1:{port}";

    public void Start()
    {
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _stop = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_stop.Token));
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _listener.Close();
    }

    private async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            var origin = $"{context.Request.Url.Scheme}://{context.Request.Url.Authority}";
            if (Redirect(path, context) is { } location)
            {
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = location;
                context.Response.Close();
                return;
            }
            var qs = context.Request.QueryString;
            var html = Render(path, qs["q"] ?? qs["query"] ?? qs["page"], origin);
            var bytes = Encoding.UTF8.GetBytes(html ?? Page("Not found", "<h1>Not found</h1>"));
            context.Response.StatusCode = html is null ? 404 : 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
        }
        catch { try { context.Response.Abort(); } catch { } }
    }

    // A same-origin redirect, a cross-origin redirect, and an interstitial redirect.
    private string? Redirect(string path, HttpListenerContext context) => path switch
    {
        "/go/quickstart" => "/docs/getting-started",
        "/go/elsewhere" => $"{Other}/docs/getting-started",
        "/go/protected" => $"{Other}/login?next=%2Fdocs%2Fapi",
        "/go/sso" => $"{Site}/login?next=%2Fportal",
        "/go/short" => $"{Site}/docs/api",
        "/lab/hop1" => "/lab/hop2",
        "/lab/hop2" => "/lab/final-report",
        _ => null
    };

    private static string Page(string title, string body, string head = "")
        => $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>{title}</title>{head}</head><body><main>{body}</main></body></html>";

    private static string Nav(params (string Text, string Href)[] links)
        => "<nav><ul>" + string.Join("", links.Select(l => $"<li><a href=\"{l.Href}\">{l.Text}</a></li>")) + "</ul></nav>";

    private static readonly (string Title, string Slug)[] Products =
    [
        ("Alpine Trail Backpack", "alpine-trail-backpack"), ("Summit Insulated Bottle", "summit-insulated-bottle"),
        ("Ridge Lightweight Tent", "ridge-lightweight-tent"), ("Ember Camp Stove", "ember-camp-stove"),
        ("Drift Sleeping Bag", "drift-sleeping-bag"), ("Beacon Headlamp", "beacon-headlamp")
    ];

    private static readonly (string Title, string Slug)[] Stories =
    [
        ("Harbor council approves new ferry terminal", "ferry-terminal"), ("Local bakery wins national award", "bakery-award"),
        ("Rail line reopens after storm repairs", "rail-reopens"), ("Library extends weekend hours", "library-hours"),
        ("Teachers ratify three-year contract", "teachers-contract")
    ];

    private static readonly (string Title, string Slug)[] Videos =
    [
        ("How tides work", "how-tides-work"), ("Sourdough for beginners", "sourdough-basics"),
        ("Inside a lighthouse", "inside-a-lighthouse"), ("Cephalopod camouflage explained", "cephalopod-camouflage")
    ];

    private static string? Render(string path, string? query, string origin)
    {
        path = path.Length > 1 ? path.TrimEnd('/') : path;
        if (path.StartsWith("/docs/") && path.Length > 6 && path[6..] is var slug and not "" && !slug.Contains('/'))
            return Page($"Docs: {slug.Replace('-', ' ')}", $"<h1>{slug.Replace('-', ' ')}</h1><p>Reference material for {slug.Replace('-', ' ')}.</p>{Nav(("Docs home", "/docs"))}");
        if (path.StartsWith("/shop/p/")) return Page(Title(path), $"<h1>{Title(path)}</h1><p>In stock.</p><button type=\"button\">Add to cart</button>{Nav(("Back to shop", "/shop"))}");
        if (path.StartsWith("/news/") && path != "/news") return Page(Title(path), $"<h1>{Title(path)}</h1><article><p>Full story text.</p></article>{Nav(("News home", "/news"))}");
        if (path.StartsWith("/videos/watch/")) return Page($"Watch: {Title(path)}", $"<h1>{Title(path)}</h1><p>Video page.</p>{Nav(("All videos", "/videos"))}");
        if (path.StartsWith("/item/")) return Page($"Item {path[6..]}", $"<h1>Result item {path[6..]}</h1>{Nav(("Search", "/search"))}");
        if (path.StartsWith("/lab")) return Lab(path, query);
        if (path.StartsWith("/validate")) return Validate(path, query);
        if (path.StartsWith("/interact/spa")) return Page("App home", Spa(), SpaScript);
        if (path.StartsWith("/ambiguous/") ) return Page($"Plans {path[11..]}", $"<h1>Plans page {path[11..]}</h1>");

        return path switch
        {
            "/" => Page("Fixtures", "<h1>VoiceOS proof fixtures</h1>" + Nav(("Docs", "/docs"), ("Shop", "/shop"), ("News", "/news"),
                ("Videos", "/videos"), ("Search", "/search"), ("Interactions", "/interact"))),
            "/docs" => Page("Docs home", "<h1>Documentation</h1>" + Nav(("Getting started guide", "/docs/getting-started"),
                ("API reference", "/docs/api"), ("Changelog", "/docs/changelog"), ("Frequently asked questions", "/docs/faq"),
                ("Quick start", "/go/quickstart"), ("Partner integration", "/go/elsewhere"), ("Protected reference", "/go/protected"))),
            "/shop" => Page("Outdoor shop", "<h1>Outdoor gear</h1><ul>" + string.Join("", Products.Select((p, i) =>
                $"<li><article><h2>{p.Title}</h2><a href=\"/shop/p/{p.Slug}\">{p.Title}</a> <span>${19 + i * 12}</span></article></li>")) + "</ul>"),
            "/news" => Page("Daily news", "<h1>Top stories</h1><ol>" + string.Join("", Stories.Select(s =>
                $"<li><a href=\"/news/{s.Slug}\">{s.Title}</a></li>")) + "</ol>"),
            "/videos" => Page("Video library", "<h1>Videos</h1><ul>" + string.Join("", Videos.Select(v =>
                $"<li><a href=\"/videos/watch/{v.Slug}\">{v.Title}</a></li>")) + "</ul>"),
            "/search" or "/search/results" => SearchPage(query),
            "/interact" => Page("Interaction fixtures", "<h1>Interactions</h1>" + Nav(("Same page updates", "/interact/same-page"),
                ("Controls that do nothing", "/interact/noop"), ("Two similar links", "/interact/ambiguous"),
                ("Links that open tabs", "/interact/tabs"), ("Single page app", "/interact/spa"), ("Downloads", "/interact/decoys"), ("External links", "/interact/external"),
                ("Latest", "/interact/read-more"), ("Backpacks", "/interact/siblings"), ("Missing control", "/interact/zero"), ("Vanishing control", "/interact/vanishing"))),
            "/interact/same-page" => Page("Same-page updates", SamePage(), SamePageScript),
            "/interact/noop" => Page("Controls that do nothing",
                "<h1>Nothing happens here</h1><button type=\"button\" onclick=\"return false\">Submit report</button>" +
                "<a href=\"#\" onclick=\"event.preventDefault()\">View details panel</a><a href=\"/interact/noop\">Reload this page</a>"),
            "/interact/ambiguous" => Page("Pricing", "<h1>Pricing</h1>" + Nav(("Pricing plans", "/ambiguous/a"), ("Pricing plans overview", "/ambiguous/b"), ("Contact", "/docs/faq"))),
            "/interact/tabs" => Page("Opens tabs", "<h1>Links that open tabs</h1><a href=\"/docs/getting-started\" target=\"_blank\">Open the guide in a new tab</a>" +
                "<a href=\"/docs/api\" target=\"_blank\" rel=\"noopener\">Open the API reference in a new tab</a>"),
            "/interact/zero" => Page("Support", "<h1>Support</h1>" + Nav(("Docs home", "/docs"), ("Shop", "/shop"))),
            "/interact/vanishing" => Page("Vanishing", "<h1>Limited offer</h1><a id=\"offer\" href=\"/shop\">Claim special offer</a>" +
                "<a href=\"/docs\">Docs home</a><script>setTimeout(()=>document.getElementById('offer')?.remove(), 1200)</script>"),
            "/interact/external" => Page("External links", "<h1>External links</h1><a href=\"" + "http://127.0.0.1:18777/go/sso\">Partner portal</a>" +
                "<a href=\"http://127.0.0.1:18777/go/short\">Short link to the API reference</a><a href=\"http://127.0.0.1:18777/docs/getting-started\">Partner mirror of the guide</a>"),
            "/interact/decoys" => Page("Downloads", "<h1>Downloads</h1><a href=\"" + "/ads/installer\">Download now</a><ul>" +
                "<li><a href=\"/files/windows\">Download for Windows</a></li><li><a href=\"/files/mac\">Download for Mac</a></li><li><a href=\"/files/linux\">Download for Linux</a></li></ul>"),
            "/interact/read-more" => Page("Latest", "<h1>Latest</h1>" + string.Concat(new[] { ("Harbor council approves new ferry terminal", "ferry-terminal"),
                ("Local bakery wins national award", "bakery-award"), ("Rail line reopens after storm repairs", "rail-reopens") }.Select(c =>
                $"<article><h2>{c.Item1}</h2><p>Summary of the story.</p><a href=\"/news/{c.Item2}\">Read more</a></article>"))),
            "/interact/siblings" => Page("Backpacks", "<h1>Backpacks</h1>" + Nav(("Alpine Trail Backpack", "/shop/p/alpine-trail-backpack"),
                ("Alpine Trail Backpack Pro", "/shop/p/alpine-trail-backpack-pro"), ("Alpine Trail Backpack Kids", "/shop/p/alpine-trail-backpack-kids"))),
            "/files/windows" or "/files/mac" or "/files/linux" or "/ads/installer" => Page(path[1..].Replace('/', ' '), $"<h1>{path[1..]}</h1>"),
            "/login" => Page("Sign in", "<h1>Sign in to continue</h1><label>Email <input name=\"email\"></label><button type=\"button\">Sign in</button>"),
            _ => null
        };
    }


    // A second, differently laid-out site used for fresh-scenario checks: tables, breadcrumbs, pagination,
    // sidebars, tab widgets, accordions, multi-hop redirects.
    private static string? Lab(string path, string? query)
    {
        static string Row(string name, string slug, int price) =>
            $"<tr><td>{name}</td><td>${price}</td><td><a href=\"/lab/item/{slug}\">Details</a></td><td><button type=\"button\">Add</button></td></tr>";
        return path switch
        {
            "/lab/catalog" => Page("Equipment catalog", "<h1>Equipment</h1><table><thead><tr><th>Name</th><th>Price</th><th></th><th></th></tr></thead><tbody>" +
                Row("Trail Lantern", "trail-lantern", 34) + Row("Ridge Compass", "ridge-compass", 21) + Row("Camp Kettle", "camp-kettle", 27) + "</tbody></table>"),
            "/lab/guides/setup" => Page("Setup", "<nav aria-label=\"breadcrumb\"><a href=\"/lab\">Home</a> / <a href=\"/lab/guides\">Guides</a> / <span>Setup</span></nav><h1>Setup</h1><p>Steps.</p>"),
            "/lab/guides" => Page("Guides", "<h1>Guides</h1>" + Nav(("Setup", "/lab/guides/setup"))),
            "/lab/list" => Page("Archive", $"<h1>Archive page {query ?? "1"}</h1><nav><a href=\"/lab/list?page=1\">1</a> <a href=\"/lab/list?page=2\">2</a> <a href=\"/lab/list?page=3\">3</a> <a href=\"/lab/list?page={int.Parse(query ?? "1") + 1}\">Next page</a></nav>"),
            "/lab/manual" => Page("Manual", "<aside><ul><li><a href=\"/lab/manual/installation\">Installation</a></li><li><a href=\"/lab/manual/configuration\">Configuration</a></li>" +
                "<li><a href=\"/lab/manual/troubleshooting\">Troubleshooting</a></li></ul></aside><main><h1>Manual</h1></main>"),
            "/lab/tabs" => Page("Account", "<h1>Account</h1><div role=\"tablist\"><button role=\"tab\" id=\"t1\" aria-selected=\"true\">Profile</button><button role=\"tab\" id=\"t2\" aria-selected=\"false\">Billing</button></div><div id=\"panel\">Profile settings</div>",
                "<script>document.addEventListener('DOMContentLoaded',()=>{for(const [id,label] of [['t1','Profile settings'],['t2','Billing details']])document.getElementById(id).onclick=()=>{document.getElementById('panel').textContent=label;document.getElementById('t1').setAttribute('aria-selected',id==='t1');document.getElementById('t2').setAttribute('aria-selected',id==='t2');};});</script>"),
            "/lab/faq" => Page("Help", "<h1>Help</h1><details><summary>How long does shipping take?</summary><p>Three to five days.</p></details><details><summary>Can I return an item?</summary><p>Within 30 days.</p></details>"),
            "/lab/legal" => Page("Legal", "<h1>Legal</h1><a href=\"/lab/terms\" target=\"_blank\" rel=\"noopener noreferrer\">Terms of service</a><a href=\"/lab/privacy\" target=\"_blank\">Privacy notice</a>"),
            "/lab/reports" => Page("Reports", "<h1>Reports</h1><a href=\"/lab/hop1\">Annual report</a><a href=\"/lab/hop2\">Quarterly summary</a>"),
            "/lab/find" => Page("Find", $"<h1>Find</h1><form action=\"/lab/find\"><input type=\"search\" name=\"query\" aria-label=\"Search articles\" value=\"{WebUtility.HtmlEncode(query ?? "")}\"><button type=\"submit\">Go</button></form>" +
                (string.IsNullOrWhiteSpace(query) ? "" : $"<ul><li><a href=\"/lab/item/a\">{WebUtility.HtmlEncode(query)} overview</a></li><li><a href=\"/lab/item/b\">{WebUtility.HtmlEncode(query)} in practice</a></li></ul>")),
            "/lab" => Page("Lab home", "<h1>Lab</h1>" + Nav(("Equipment catalog", "/lab/catalog"), ("Guides", "/lab/guides"), ("Archive", "/lab/list"), ("Manual", "/lab/manual"),
                ("Account", "/lab/tabs"), ("Help", "/lab/faq"), ("Legal", "/lab/legal"), ("Reports", "/lab/reports"), ("Find", "/lab/find"))),
            _ when path.StartsWith("/lab/item/") || path.StartsWith("/lab/manual/") || path is "/lab/terms" or "/lab/privacy" or "/lab/final-report"
                => Page(path[5..].Replace('/', ' ').Replace('-', ' '), $"<h1>{path[5..]}</h1>"),
            _ => null
        };
    }

    // Alpha-correction validation surfaces: reveal vs a same-named link, a real user choice, a gate that may already be
    // cleared, and pagination among durations and counts. Nothing here fakes browser state.
    private static string? Validate(string path, string? query)
    {
        static string Filler(int n) => string.Concat(Enumerable.Range(1, n).Select(i =>
            $"<div style=\"height:900px\"><p>Product information block {i}.</p></div>"));
        return path switch
        {
            "/validate/reveal" => Page("Trail Headlamp", "<h1>Trail Headlamp</h1><p>Rated <a href=\"/validate/reviews-list\">(4 Reviews)</a></p>" +
                Filler(4) + "<section><h2>Specifications</h2><p>Weight 85 g.</p></section>" + Filler(2) +
                "<section><h2>Customer Reviews</h2><p>Bright and light. Five stars.</p></section>" + "<div style=\"height:1400px\"></div>"),
            "/validate/reviews-list" => Page("All reviews", "<h1>All reviews</h1><p>Review list page.</p>"),
            "/validate/choice" => Page("Results for Hello", "<h1>Results for Hello</h1>" +
                "<section><a href=\"/validate/track/adele\">Hello</a><p>Adele</p></section>" +
                "<section><a href=\"/validate/track/richie\">Hello</a><p>Lionel Richie</p></section>"),
            "/validate/track/adele" or "/validate/track/richie" => Page("Track", $"<h1>Hello - {(path.EndsWith("adele") ? "Adele" : "Lionel Richie")}</h1><a href=\"{path}/lyrics\">Lyrics</a>"),
            "/validate/track/adele/lyrics" or "/validate/track/richie/lyrics" => Page("Lyrics", "<h1>Lyrics</h1><p>Hello, it's me.</p>"),
            // The gate is shown until a choice was made; ?page=aged renders the page as it is after a previous visit.
            "/validate/gate" => Page("Shop", (query == "aged" ? "" :
                "<div id=\"gate\" role=\"dialog\" aria-label=\"Age check\"><p>Are you over 18?</p><button id=\"over\" type=\"button\">I am over 18</button> <button id=\"under\" type=\"button\">I am under 18</button></div>") +
                "<h1>Shop</h1><a href=\"/validate/gate/pricing\">Pricing</a>",
                "<script>document.addEventListener('DOMContentLoaded',()=>{const g=document.getElementById('gate');if(!g)return;" +
                "document.getElementById('over').onclick=()=>g.remove();document.getElementById('under').onclick=()=>{g.textContent='Sorry, this site is for adults.';};});</script>"),
            "/validate/gate/pricing" => Page("Pricing", "<h1>Pricing</h1><p>Plans.</p>"),
            "/validate/paginated" => Page("Clips", $"<h1>Clips - page {query ?? "1"}</h1><ul>" +
                "<li><a href=\"/item/1\">Harbor timelapse <span>4:08</span></a></li><li><a href=\"/item/2\">Bakery tour <span>4:00</span></a></li>" +
                "<li><a href=\"/item/3\">Lighthouse story</a> <span>4 reviews</span></li>" +
                "<li><a href=\"/item/4\">Tide tables <span>14:08</span></a></li></ul>" +
                "<nav aria-label=\"pagination\">" + string.Join(" ", Enumerable.Range(1, 10).Select(p => $"<a href=\"/validate/paginated?page={p}\">{p}</a>")) + "</nav>"),
            _ => null
        };
    }

    private static string Title(string path)
    {
        var slug = path[(path.LastIndexOf('/') + 1)..];
        var all = Products.Concat(Stories).Concat(Videos).FirstOrDefault(p => p.Slug == slug);
        return all.Title ?? slug.Replace('-', ' ');
    }

    private static string SearchPage(string? query)
    {
        var form = "<form action=\"/search/results\" method=\"get\"><label>Search the site <input type=\"search\" name=\"q\" value=\"" +
            WebUtility.HtmlEncode(query ?? "") + "\"></label><button type=\"submit\">Search</button></form>";
        if (string.IsNullOrWhiteSpace(query)) return Page("Site search", "<h1>Search</h1>" + form);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = string.Concat(Enumerable.Range(1, 5).Select(i =>
            $"<li><a href=\"/item/{i}\">{WebUtility.HtmlEncode(string.Join(' ', terms))} result number {i}</a></li>"));
        return Page($"{query} - Site search", $"<h1>Results for {WebUtility.HtmlEncode(query)}</h1>{form}<ol>{list}</ol>");
    }

    private static string SamePage() =>
        "<h1>Same-page updates</h1><div id=\"banner\"><span>We use cookies.</span> <button id=\"dismiss\" type=\"button\">Dismiss banner</button></div>" +
        "<button id=\"more\" type=\"button\">Show shipping details</button><div id=\"details\"></div>" +
        "<button id=\"faq\" type=\"button\" aria-expanded=\"false\">Expand FAQ</button><section id=\"faqbody\" hidden><p>Answers.</p></section>" +
        "<label>Email <input id=\"email\" name=\"email\"></label><button id=\"subscribe\" type=\"button\">Subscribe</button><p id=\"thanks\"></p>";

    private static string Spa() =>
        "<h1 id=\"title\">App home</h1><nav><button id=\"pricing\" type=\"button\">Show pricing view</button> <button id=\"team\" type=\"button\">Show team view</button></nav>" +
        "<section id=\"view\">Home view</section>";

    // Same-document navigation: the URL changes with history.pushState and no document is loaded.
    private const string SpaScript = """
        <script>
        document.addEventListener('DOMContentLoaded', () => {
          const go = (name, path) => { history.pushState({}, '', path); document.title = name + ' - App'; document.getElementById('title').textContent = name; document.getElementById('view').textContent = name + ' view'; };
          document.getElementById('pricing').onclick = () => go('Pricing', '/interact/spa/pricing');
          document.getElementById('team').onclick = () => go('Team', '/interact/spa/team');
        });
        </script>
        """;

    private const string SamePageScript = """
        <script>
        document.addEventListener('DOMContentLoaded', () => {
          document.getElementById('dismiss').onclick = () => document.getElementById('banner').remove();
          document.getElementById('more').onclick = () => { document.getElementById('details').innerHTML = '<p>Ships in 3 to 5 business days.</p>'; };
          document.getElementById('faq').onclick = (e) => { const b = document.getElementById('faqbody'); b.hidden = !b.hidden; e.target.textContent = b.hidden ? 'Expand FAQ' : 'Collapse FAQ'; };
          document.getElementById('subscribe').onclick = () => { document.getElementById('thanks').textContent = 'Thanks for subscribing'; };
        });
        </script>
        """;
}
