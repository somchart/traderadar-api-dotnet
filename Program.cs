// TradeRadar API v8.1 — .NET 8 Minimal API
// All config driven by appsettings.json + environment variable overrides
// KnownSymbols and SymbolMap are read from appsettings.json (no hardcoding)

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ── PORT — Railway injects PORT at runtime, must bind before Build() ─────────
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://+:{port}");

// ── IConfiguration is auto-loaded from:
//    1. appsettings.json
//    2. appsettings.{Environment}.json
//    3. Environment variables (override — Railway sets these)
//    4. Command-line args
//
// Railway env var mapping:  TradeRadar__ApiKey  →  TradeRadar:ApiKey
// (double underscore = colon separator for nested config sections)
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("TradeRadar"));

// KnownSymbols list — read from appsettings.json "KnownSymbols" array
// Can be overridden by environment or appsettings.{env}.json
var knownSymbols = builder.Configuration
    .GetSection("KnownSymbols")
    .Get<List<SearchResult>>() ?? [];

// SymbolMap dictionary — Finnhub symbol mapping from "SymbolMap" section
var symbolMap = builder.Configuration
    .GetSection("SymbolMap")
    .Get<Dictionary<string, string>>() ?? new Dictionary<string, string>();

builder.Services.AddSingleton(knownSymbols);
builder.Services.AddSingleton(new SymbolMapConfig(symbolMap));
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<RateLimiterService>();
builder.Services.AddSingleton<FinnhubService>();
builder.Services.AddSingleton<AnthropicService>();

builder.Services.AddHttpClient("finnhub", c =>
{
    c.BaseAddress = new Uri("https://finnhub.io/api/v1/");
    c.Timeout     = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient("anthropic", c =>
{
    c.BaseAddress = new Uri("https://api.anthropic.com/");
    c.Timeout     = TimeSpan.FromSeconds(35);
});
builder.Services.AddHttpClient("external", c =>
{
    c.Timeout = TimeSpan.FromSeconds(8);
    c.DefaultRequestHeaders.Add("User-Agent", "TradeRadar/8.1");
});

var app = builder.Build();

// ── CORS — manual to handle preflight with X-API-Key ─────────────────────────
app.Use(async (ctx, next) =>
{
    var cfg    = ctx.RequestServices.GetRequiredService<IOptions<AppConfig>>().Value;
    var origin = ctx.Request.Headers.Origin.ToString();
    var allowed = cfg.AllowedOrigin == "*"
               || cfg.AllowedOrigin.Split(',').Any(o => o.Trim() == origin);

    if (allowed || cfg.AllowedOrigin == "*")
        ctx.Response.Headers["Access-Control-Allow-Origin"] =
            cfg.AllowedOrigin == "*" ? "*" : origin;

    ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-API-Key, Authorization";
    ctx.Response.Headers["Access-Control-Max-Age"]       = "86400";

    if (ctx.Request.Method == "OPTIONS")
    {
        ctx.Response.StatusCode = 204;
        return;
    }
    await next();
});

// ── Auth + Rate Limit ─────────────────────────────────────────────────────────
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path == "/health" || path == "/") { await next(); return; }

    var cfg     = ctx.RequestServices.GetRequiredService<IOptions<AppConfig>>().Value;
    var rateSvc = ctx.RequestServices.GetRequiredService<RateLimiterService>();

    var ip  = ctx.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } fwd
              ? fwd.Split(',')[0].Trim()
              : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    if (!rateSvc.Allow(ip, cfg.RateLimit))
    {
        ctx.Response.StatusCode = 429;
        await ctx.Response.WriteAsJsonAsync(new { error = $"Rate limit: max {cfg.RateLimit} req/min" });
        return;
    }

    var key = ctx.Request.Headers["X-API-Key"].FirstOrDefault()
           ?? ctx.Request.Query["apikey"].FirstOrDefault()
           ?? "";

    if (string.IsNullOrEmpty(key) || key != cfg.ApiKey)
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid or missing X-API-Key header" });
        return;
    }

    await next();
});

// ── Routes ────────────────────────────────────────────────────────────────────

app.MapGet("/health", (IOptions<AppConfig> opts, CacheService cache) =>
{
    var c = opts.Value;
    return Results.Ok(new
    {
        status  = "ok",
        uptime  = (int)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalSeconds + "s",
        version = "8.1.0",
        runtime = "dotnet8",
        cache   = cache.Stats(),
        keys = new
        {
            finnhub   = !string.IsNullOrEmpty(c.FinnhubKey)   ? "✅ configured" : "❌ MISSING — add FINNHUB_KEY",
            anthropic = !string.IsNullOrEmpty(c.AnthropicKey) ? "✅ configured" : "⚠️ add ANTHROPIC_KEY for AI",
            rapidapi  = !string.IsNullOrEmpty(c.RapidApiKey)  ? "✅ configured" : "— optional",
        },
        time = DateTime.UtcNow.ToString("o"),
    });
});

app.MapGet("/", () => Results.Ok(new
{
    name    = "TradeRadar API v8.1",
    runtime = ".NET 8 Minimal API",
    config  = "appsettings.json + Railway environment variable overrides",
    endpoints = new Dictionary<string, string>
    {
        ["GET  /health"]                         = "Server status + key config",
        ["GET  /api/quote?symbols=AAPL,GC=F"]    = "Stock quotes (Finnhub)",
        ["GET  /api/spark?symbol=AAPL&range=1mo"] = "OHLCV chart data",
        ["GET  /api/search?q=apple"]              = "Symbol search (KnownSymbols + Finnhub)",
        ["GET  /api/fear-greed"]                  = "Fear & Greed index",
        ["GET  /api/fx?from=USD&to=THB"]          = "FX exchange rate",
        ["POST /api/ai"]                          = "Claude AI proxy (fixes browser CORS)",
        ["GET  /api/cache/clear"]                 = "Clear in-memory cache",
    },
}));

app.MapGet("/api/quote", async (
    string? symbols, string? symbol,
    FinnhubService fh, CacheService cache) =>
{
    var raw = (symbols ?? symbol ?? "").Trim();
    if (string.IsNullOrEmpty(raw))
        return Results.BadRequest(new { error = "symbols param required. Example: ?symbols=AAPL,PTT.BK,GC=F" });

    var syms = raw.Split(',')
                  .Select(s => s.Trim().ToUpperInvariant())
                  .Where(s => s.Length > 0)
                  .Take(20)
                  .ToList();

    var cacheKey = "quote:" + string.Join(",", syms);
    if (cache.TryGet<List<QuoteResult>>(cacheKey, out var hit))
        return Results.Ok(new { ok = true, cached = true, count = hit!.Count, quotes = hit, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

    var tasks   = syms.Select(s => fh.GetQuoteAsync(s)).ToList();
    var results = await Task.WhenAll(tasks);

    var quotes = results.Where(r => r is not null).Cast<QuoteResult>().ToList();
    var failed = syms.Zip(results)
                     .Where(x => x.Second is null)
                     .Select(x => x.First)
                     .ToList();

    if (failed.Count > 0)
        app.Logger.LogWarning("[quote] no data for: {Syms}", string.Join(", ", failed));

    if (quotes.Count == 0)
    {
        // Return 200 with empty quotes so frontend can show "—" gracefully
        // (502 causes browser to log errors and makes debugging confusing)
        app.Logger.LogWarning("[quote] all symbols failed: {Syms}", string.Join(",", syms));
        return Results.Ok(new { ok = false, cached = false, count = 0, quotes = Array.Empty<object>(), failed, hint = "Check FINNHUB_KEY in Railway Variables", ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    cache.Set(cacheKey, quotes);
    return Results.Ok(new { ok = true, cached = false, count = quotes.Count, quotes, failed, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
});

app.MapGet("/api/spark", async (
    string? symbol, string? range,
    FinnhubService fh, CacheService cache) =>
{
    if (string.IsNullOrEmpty(symbol))
        return Results.BadRequest(new { error = "symbol param required" });

    var sym = symbol.ToUpperInvariant();
    var r   = new[] { "1d", "5d", "1mo", "3mo" }.Contains(range) ? range! : "1mo";

    var cacheKey = $"spark:{sym}:{r}";
    if (cache.TryGet<SparkResult>(cacheKey, out var hit))
        return Results.Ok(new { ok = true, cached = true, symbol = sym, range = r, data = hit });

    var data = await fh.GetCandlesAsync(sym, r);
    // Always return 200 — empty SparkResult means "no data available" (not an error)
    var sparkData = data ?? new SparkResult { Timestamps=[], Closes=[], Opens=[], Highs=[], Lows=[], Volumes=[], Count=0, Source="unavailable" };
    cache.Set(cacheKey, sparkData, sparkData.Count > 0 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1));
    return Results.Ok(new { ok = true, cached = false, symbol = sym, range = r, data = sparkData });
});

app.MapGet("/api/search", async (
    string? q,
    FinnhubService fh,
    List<SearchResult> knownSymbols) =>
{
    if (string.IsNullOrEmpty(q))
        return Results.BadRequest(new { error = "q param required" });

    var upper   = q.ToUpperInvariant();
    var matches = knownSymbols
        .Where(s => s.Symbol.Contains(upper, StringComparison.OrdinalIgnoreCase)
                 || s.Shortname.Contains(q,   StringComparison.OrdinalIgnoreCase))
        .ToList();

    // Augment with Finnhub search if local results are few
    if (matches.Count < 4)
    {
        var fhResults = await fh.SearchAsync(q);
        var seen      = matches.Select(k => k.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        matches.AddRange(fhResults.Where(r => !seen.Contains(r.Symbol)));
    }

    return Results.Ok(new { ok = true, quotes = matches.Take(8) });
});

app.MapGet("/api/fear-greed", async (
    FinnhubService fh, CacheService cache,
    IHttpClientFactory httpFactory) =>
{
    if (cache.TryGet<FearGreedResult>("fng", out var hit))
        return Results.Ok(new { ok = true, cached = true, hit!.Value, hit.Classification });

    // Try alternative.me (works if Railway doesn't block it)
    try
    {
        var http = httpFactory.CreateClient("external");
        var json = await http.GetStringAsync("https://api.alternative.me/fng/?limit=1&format=json");
        using var doc  = JsonDocument.Parse(json);
        var item       = doc.RootElement.GetProperty("data")[0];
        var valueStr   = item.GetProperty("value").GetString() ?? "50";
        var result     = new FearGreedResult
        {
            Value          = int.Parse(valueStr),
            Classification = item.GetProperty("value_classification").GetString() ?? "Neutral",
        };
        cache.Set("fng", result, TimeSpan.FromHours(4));
        return Results.Ok(new { ok = true, cached = false, result.Value, result.Classification });
    }
    catch { /* blocked — fall through to VIX estimate */ }

    // Fallback: estimate from VIXY (VIX proxy) via Finnhub
    var vixy = await fh.GetQuoteAsync("VIXY");
    if (vixy is not null)
    {
        var vix   = (double)(vixy.RegularMarketPrice ?? 20m);
        var value = vix > 30 ? Math.Max(5,  (int)(40 - (vix - 30) * 2))
                  : vix > 20 ? (int)(50 - (vix - 20) * 3)
                  : vix > 15 ? (int)(65 - (vix - 15) * 3)
                  :            Math.Min(95, (int)(80 + (15 - vix) * 3));
        var cls   = value < 25 ? "Extreme Fear"
                  : value < 45 ? "Fear"
                  : value < 55 ? "Neutral"
                  : value < 75 ? "Greed"
                  :              "Extreme Greed";
        var result = new FearGreedResult { Value = value, Classification = cls };
        cache.Set("fng", result, TimeSpan.FromHours(1));
        return Results.Ok(new { ok = true, cached = false, result.Value, result.Classification, note = "estimated from VIX" });
    }

    return Results.Ok(new { ok = true, cached = false, Value = 50, Classification = "Neutral", note = "fallback" });
});

app.MapGet("/api/fx", async (
    string? from, string? to,
    FinnhubService fh, CacheService cache,
    IOptions<AppConfig> opts) =>
{
    var f        = ((from ?? "USD").ToUpperInvariant() + "   ")[..3];
    var t        = ((to   ?? "THB").ToUpperInvariant() + "   ")[..3];
    var cacheKey = $"fx:{f}:{t}";

    if (cache.TryGet<FxResult>(cacheKey, out var hit))
        return Results.Ok(new { ok = true, cached = true, from = f, to = t, hit!.Rate, hit.Date });

    var cfg = opts.Value;
    if (!string.IsNullOrEmpty(cfg.FinnhubKey))
    {
        try
        {
            var http = fh.HttpFactory.CreateClient("finnhub");
            var json = await http.GetStringAsync($"forex/rates?base={f}&token={cfg.FinnhubKey}");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("quote", out var quote) &&
                quote.TryGetProperty(t, out var rateEl))
            {
                var result = new FxResult { Rate = Math.Round(rateEl.GetDecimal(), 4), Date = DateTime.UtcNow.ToString("yyyy-MM-dd") };
                cache.Set(cacheKey, result, TimeSpan.FromHours(1));
                return Results.Ok(new { ok = true, cached = false, from = f, to = t, result.Rate, result.Date });
            }
        }
        catch (Exception e) { app.Logger.LogWarning("[fx] Finnhub failed: {Msg}", e.Message); }
    }

    // Hard-coded fallback rates (approximate — only if Finnhub unavailable)
    var fallbacks = new Dictionary<string, decimal>
    {
        ["USDTHB"] = 33.5m, ["USDJPY"] = 149.5m,
        ["EURUSD"] = 1.08m, ["GBPUSD"] = 1.27m,
    };
    if (fallbacks.TryGetValue(f + t, out var fb))
        return Results.Ok(new { ok = true, cached = false, from = f, to = t, Rate = fb, Date = "fallback", note = "approximate" });

    return Results.Json(new { error = $"No rate for {f}/{t}" }, statusCode: 502);
});

app.MapPost("/api/ai", async (HttpContext ctx, AnthropicService ai) =>
{
    if (string.IsNullOrEmpty(ai.Config.AnthropicKey))
        return Results.Json(
            new { error = "ANTHROPIC_KEY not set. Add to Railway Variables (console.anthropic.com → API Keys)." },
            statusCode: 503);

    using var reader = new StreamReader(ctx.Request.Body);
    return await ai.ProxyAsync(await reader.ReadToEndAsync());
});

app.MapGet("/api/cache/clear", (CacheService cache) =>
    Results.Ok(new { ok = true, cleared = cache.Clear() }));

// ── Start ─────────────────────────────────────────────────────────────────────
var startupCfg = app.Services.GetRequiredService<IOptions<AppConfig>>().Value;
Console.WriteLine($"""
╔═══════════════════════════════════════════╗
║    TradeRadar API v8.1 (.NET 8)           ║
╠═══════════════════════════════════════════╣
║  Port     : {port,-30}║
║  Cache    : {startupCfg.CacheTtlMs / 1000}s{new string(' ', 29)}║
║  Finnhub  : {(!string.IsNullOrEmpty(startupCfg.FinnhubKey) ? "✅ configured" : "❌ MISSING — set FINNHUB_KEY"),-30}║
║  AI Proxy : {(!string.IsNullOrEmpty(startupCfg.AnthropicKey) ? "✅ configured" : "⚠️  add ANTHROPIC_KEY"),-30}║
║  Env      : {app.Environment.EnvironmentName,-30}║
╚═══════════════════════════════════════════╝
""");

if (string.IsNullOrEmpty(startupCfg.FinnhubKey))
    Console.Error.WriteLine("⛔ FINNHUB_KEY not set! Set via Railway Variables or appsettings.json");

app.Run();


// ════════════════════════════════════════════════════════════════════════════
// CONFIG
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Bound from appsettings.json "TradeRadar" section.
/// Override per-key in Railway: TradeRadar__FinnhubKey=xxx
/// </summary>
public class AppConfig
{
    public string ApiKey        { get; set; } = "change-me-in-env";
    public string FinnhubKey    { get; set; } = "";
    public string AnthropicKey  { get; set; } = "";
    public string RapidApiKey   { get; set; } = "";
    public int    CacheTtlMs    { get; set; } = 60_000;
    public int    RateLimit     { get; set; } = 120;
    public string AllowedOrigin { get; set; } = "*";
}

/// <summary>Wraps SymbolMap dictionary from appsettings.json</summary>
public record SymbolMapConfig(Dictionary<string, string> Map);


// ════════════════════════════════════════════════════════════════════════════
// SERVICES
// ════════════════════════════════════════════════════════════════════════════

public class CacheService(IOptions<AppConfig> opts)
{
    private readonly ConcurrentDictionary<string, (object? Value, DateTime ExpiresAt)> _store = new();
    private readonly int _defaultTtlMs = opts.Value.CacheTtlMs;

    public bool TryGet<T>(string key, out T? value)
    {
        if (_store.TryGetValue(key, out var e) && e.ExpiresAt > DateTime.UtcNow)
        { value = (T?)e.Value; return true; }
        _store.TryRemove(key, out _);
        value = default; return false;
    }
    public void Set<T>(string key, T value, TimeSpan? ttl = null)
        => _store[key] = (value, DateTime.UtcNow.Add(ttl ?? TimeSpan.FromMilliseconds(_defaultTtlMs)));
    public int Clear() { var n = _store.Count; _store.Clear(); return n; }
    public object Stats()
    {
        var valid = _store.Values.Count(e => e.ExpiresAt > DateTime.UtcNow);
        return new { total = _store.Count, valid, ttl_ms = _defaultTtlMs };
    }
}

public class RateLimiterService
{
    private readonly ConcurrentDictionary<string, (int Count, DateTime Reset)> _buckets = new();
    public bool Allow(string ip, int limit)
    {
        var now   = DateTime.UtcNow;
        var entry = _buckets.GetOrAdd(ip, _ => (0, now.AddMinutes(1)));
        if (now > entry.Reset) entry = (0, now.AddMinutes(1));
        entry = (entry.Count + 1, entry.Reset);
        _buckets[ip] = entry;
        return entry.Count <= limit;
    }
}

public class FinnhubService(IOptions<AppConfig> opts, SymbolMapConfig symMap, IHttpClientFactory factory)
{
    public AppConfig          Config      => opts.Value;
    public IHttpClientFactory HttpFactory => factory;

    // Throttle: 300ms between calls = ~3/sec (free tier = 60/min)
    // With batched frontend requests, this is sufficient
    private readonly SemaphoreSlim _gate     = new(1, 1);
    private          DateTime      _lastCall = DateTime.MinValue;
    private const    int           ThrottleMs = 300;

    private async Task<string> FhGetAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            var wait = ThrottleMs - (int)(DateTime.UtcNow - _lastCall).TotalMilliseconds;
            if (wait > 0) await Task.Delay(wait);
            _lastCall = DateTime.UtcNow;

            var http = factory.CreateClient("finnhub");
            var sep  = path.Contains('?') ? "&" : "?";
            return await http.GetStringAsync($"{path}{sep}token={opts.Value.FinnhubKey}");
        }
        finally { _gate.Release(); }
    }

    private string ToFhSym(string sym)
    {
        if (symMap.Map.TryGetValue(sym, out var mapped)) return mapped;
        // Thai .BK stocks: try without suffix (Finnhub free doesn't support SET exchange well)
        if (sym.EndsWith(".BK")) return sym[..^3];
        return sym;
    }

    public async Task<QuoteResult?> GetQuoteAsync(string sym)
    {
        if (string.IsNullOrEmpty(opts.Value.FinnhubKey)) return null;
        try
        {
            // FX special case: get THB from forex rates
            if (sym == "THBX=X")
            {
                var fx = await FhGetAsync("forex/rates?base=USD");
                using var fxDoc = JsonDocument.Parse(fx);
                if (fxDoc.RootElement.TryGetProperty("quote", out var q) &&
                    q.TryGetProperty("THB", out var thb))
                {
                    var rate = thb.GetDecimal();
                    return new QuoteResult { Symbol = "THBX=X", ShortName = "USD/THB",
                        RegularMarketPrice = rate, RegularMarketChange = 0,
                        RegularMarketChangePercent = 0, Source = "finnhub-forex" };
                }
                return null;
            }

            // Gold: use OANDA:XAUUSD direct quote (more reliable than forex/rates XAU)
            if (sym == "GC=F" || sym == "XAUUSD" || sym == "GC.F")
            {
                // Try OANDA:XAUUSD first
                try
                {
                    var goldResult = await GetQuoteForSymbol("OANDA:XAUUSD", sym, "Gold Spot USD");
                    if (goldResult?.RegularMarketPrice > 0) return goldResult;
                }
                catch { }

                // Fallback: GLD ETF (gold ETF, always available on free tier)
                try
                {
                    var gldResult = await GetQuoteForSymbol("GLD", sym, "Gold (GLD ETF)");
                    if (gldResult?.RegularMarketPrice > 0) return gldResult;
                }
                catch { }

                // Last resort: forex/rates XAU
                try
                {
                    var fxJson = await FhGetAsync("forex/rates?base=USD");
                    using var fxDoc2 = JsonDocument.Parse(fxJson);
                    if (fxDoc2.RootElement.TryGetProperty("quote", out var q2) &&
                        q2.TryGetProperty("XAU", out var xau))
                    {
                        var xauPerUsd = xau.GetDecimal();
                        var price = xauPerUsd > 0 ? Math.Round(1m / xauPerUsd, 2) : 0m;
                        if (price > 0)
                            return new QuoteResult { Symbol = sym, ShortName = "Gold Spot USD",
                                RegularMarketPrice = price, RegularMarketChange = 0,
                                RegularMarketChangePercent = 0, Source = "finnhub-forex" };
                    }
                }
                catch { }
                return null;
            }

            var fSym = ToFhSym(sym);
            return await GetQuoteForSymbol(fSym, sym);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[quote] {sym}: {e.Message}");
            return null;
        }
    }

    private async Task<QuoteResult?> GetQuoteForSymbol(string fSym, string originalSym, string? displayName = null)
    {
        var json = await FhGetAsync($"quote?symbol={Uri.EscapeDataString(fSym)}");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var c    = root.TryGetProperty("c",  out var cv)  ? cv.GetDecimal()  : 0m;
        if (c == 0m) return null;
        var pc  = root.TryGetProperty("pc", out var pcv) ? pcv.GetDecimal() : c;
        var chg = c - pc;
        var pct = pc != 0m ? chg / pc * 100m : 0m;

        return new QuoteResult
        {
            Symbol                     = originalSym,
            ShortName                  = displayName ?? fSym,
            RegularMarketPrice         = Math.Round(c,   4),
            RegularMarketChange        = Math.Round(chg, 4),
            RegularMarketChangePercent = Math.Round(pct, 4),
            RegularMarketVolume        = root.TryGetProperty("v",  out var vv) ? vv.GetInt64()   : null,
            RegularMarketOpen          = root.TryGetProperty("o",  out var ov) ? ov.GetDecimal() : null,
            RegularMarketDayHigh       = root.TryGetProperty("h",  out var hv) ? hv.GetDecimal() : null,
            RegularMarketDayLow        = root.TryGetProperty("l",  out var lv) ? lv.GetDecimal() : null,
            RegularMarketPreviousClose = Math.Round(pc, 4),
            Source                     = "finnhub",
        };
    }

    // Finnhub free tier does NOT support candle/chart endpoints for stocks.
    // We generate a synthetic chart using today's quote OHLC data.
    // For a real chart, upgrade to Finnhub paid or add RapidAPI key.
    public async Task<SparkResult?> GetCandlesAsync(string sym, string range)
    {
        if (string.IsNullOrEmpty(opts.Value.FinnhubKey)) return null;
        try
        {
            var quote = await GetQuoteAsync(sym);

            // If no quote data, return a placeholder so the chart renders (not 502)
            if (quote?.RegularMarketPrice is null)
            {
                // Return empty-but-valid result — chart will show "no data" gracefully
                return new SparkResult
                {
                    Timestamps = [], Closes = [], Opens = [],
                    Highs = [], Lows = [], Volumes = [],
                    Count = 0, Source = "no-data",
                };
            }

            var nowTs     = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var prevTs    = nowTs - 86400;
            var prevClose = quote.RegularMarketPreviousClose ?? quote.RegularMarketPrice.Value;
            var cur       = quote.RegularMarketPrice.Value;

            return new SparkResult
            {
                Timestamps = [prevTs, nowTs],
                Closes     = [Math.Round(prevClose, 4), Math.Round(cur, 4)],
                Opens      = [Math.Round(prevClose, 4), Math.Round(quote.RegularMarketOpen    ?? cur, 4)],
                Highs      = [Math.Round(prevClose, 4), Math.Round(quote.RegularMarketDayHigh ?? cur, 4)],
                Lows       = [Math.Round(prevClose, 4), Math.Round(quote.RegularMarketDayLow  ?? cur, 4)],
                Volumes    = [null, quote.RegularMarketVolume],
                Count      = 2,
                Source     = "finnhub-synthetic",
            };
        }
        catch (Exception e)
        {
            Console.WriteLine($"[spark] {sym}: {e.Message}");
            return new SparkResult { Timestamps=[], Closes=[], Opens=[], Highs=[], Lows=[], Volumes=[], Count=0, Source="error" };
        }
    }

    public async Task<List<SearchResult>> SearchAsync(string q)
    {
        try
        {
            var json = await FhGetAsync($"search?q={Uri.EscapeDataString(q)}");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var results)) return [];
            return results.EnumerateArray().Take(5)
                .Select(r => new SearchResult
                {
                    Symbol    = r.TryGetProperty("symbol",      out var s) ? s.GetString()! : "",
                    Shortname = r.TryGetProperty("description", out var d) ? d.GetString()! : "",
                    Type      = r.TryGetProperty("type",        out var t) ? t.GetString()! : "Stock",
                })
                .Where(r => !string.IsNullOrEmpty(r.Symbol))
                .ToList();
        }
        catch { return []; }
    }
}

public class AnthropicService(IOptions<AppConfig> opts, IHttpClientFactory factory)
{
    public AppConfig Config => opts.Value;

    public async Task<IResult> ProxyAsync(string requestBody)
    {
        try
        {
            // Parse and rebuild payload (avoid JsonDocument disposal issues)
            using var reqDoc = JsonDocument.Parse(requestBody);
            var payloadDict  = reqDoc.RootElement.EnumerateObject()
                                  .ToDictionary(p => p.Name, p => p.Value.Clone()); // Clone before disposal

            if (!payloadDict.ContainsKey("model"))
                payloadDict["model"] = JsonDocument.Parse("\"claude-sonnet-4-20250514\"").RootElement.Clone();

            var payloadJson = JsonSerializer.Serialize(payloadDict);

            var http = factory.CreateClient("anthropic");
            http.DefaultRequestHeaders.Clear();
            http.DefaultRequestHeaders.Add("x-api-key",        opts.Value.AnthropicKey);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            var response = await http.PostAsync("v1/messages",
                new StringContent(payloadJson, Encoding.UTF8, "application/json"));
            var responseBody = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"[ai] Anthropic {(int)response.StatusCode}: {responseBody[..Math.Min(100, responseBody.Length)]}");

            if (!response.IsSuccessStatusCode)
                return Results.Content(responseBody, "application/json", statusCode: (int)response.StatusCode);

            // Return raw JSON string directly — avoids JsonDocument disposal bug
            return Results.Content(responseBody, "application/json");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[ai] Proxy error: {e.Message}\n{e.StackTrace?[..Math.Min(300, e.StackTrace?.Length ?? 0)]}");
            return Results.Json(new { error = e.Message }, statusCode: 502);
        }
    }
}


// ════════════════════════════════════════════════════════════════════════════
// MODELS
// ════════════════════════════════════════════════════════════════════════════

public class QuoteResult
{
    [JsonPropertyName("symbol")]                     public string   Symbol                     { get; set; } = "";
    [JsonPropertyName("shortName")]                  public string   ShortName                  { get; set; } = "";
    [JsonPropertyName("regularMarketPrice")]         public decimal? RegularMarketPrice         { get; set; }
    [JsonPropertyName("regularMarketChange")]        public decimal? RegularMarketChange        { get; set; }
    [JsonPropertyName("regularMarketChangePercent")] public decimal? RegularMarketChangePercent { get; set; }
    [JsonPropertyName("regularMarketVolume")]        public long?    RegularMarketVolume        { get; set; }
    [JsonPropertyName("regularMarketOpen")]          public decimal? RegularMarketOpen          { get; set; }
    [JsonPropertyName("regularMarketDayHigh")]       public decimal? RegularMarketDayHigh       { get; set; }
    [JsonPropertyName("regularMarketDayLow")]        public decimal? RegularMarketDayLow        { get; set; }
    [JsonPropertyName("regularMarketPreviousClose")] public decimal? RegularMarketPreviousClose { get; set; }
    [JsonPropertyName("fiftyTwoWeekHigh")]           public decimal? FiftyTwoWeekHigh           { get; set; }
    [JsonPropertyName("fiftyTwoWeekLow")]            public decimal? FiftyTwoWeekLow            { get; set; }
    [JsonPropertyName("fiftyDayAverage")]            public decimal? FiftyDayAverage            { get; set; }
    [JsonPropertyName("twoHundredDayAverage")]       public decimal? TwoHundredDayAverage       { get; set; }
    [JsonPropertyName("trailingPE")]                 public decimal? TrailingPE                 { get; set; }
    [JsonPropertyName("marketCap")]                  public long?    MarketCap                  { get; set; }
    [JsonPropertyName("currency")]                   public string?  Currency                   { get; set; }
    [JsonPropertyName("exchangeName")]               public string?  ExchangeName               { get; set; }
    [JsonPropertyName("_src")]                       public string   Source                     { get; set; } = "";
}

public class SparkResult
{
    [JsonPropertyName("timestamps")] public List<long>     Timestamps { get; set; } = [];
    [JsonPropertyName("closes")]     public List<decimal?> Closes     { get; set; } = [];
    [JsonPropertyName("opens")]      public List<decimal?> Opens      { get; set; } = [];
    [JsonPropertyName("highs")]      public List<decimal?> Highs      { get; set; } = [];
    [JsonPropertyName("lows")]       public List<decimal?> Lows       { get; set; } = [];
    [JsonPropertyName("volumes")]    public List<long?>    Volumes    { get; set; } = [];
    [JsonPropertyName("count")]      public int            Count      { get; set; }
    [JsonPropertyName("_src")]       public string         Source     { get; set; } = "";
}

public class SearchResult
{
    [JsonPropertyName("symbol")]    public string Symbol    { get; set; } = "";
    [JsonPropertyName("shortname")] public string Shortname { get; set; } = "";
    [JsonPropertyName("type")]      public string Type      { get; set; } = "";
    [JsonPropertyName("exchange")]  public string Exchange  { get; set; } = "";
}

public class FearGreedResult
{
    public int    Value          { get; set; } = 50;
    public string Classification { get; set; } = "Neutral";
}

public record FxResult
{
    public decimal Rate { get; init; }
    public string  Date { get; init; } = "";
}
