using System.Collections;
using System.Collections.Concurrent;
using System.Drawing;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TradingPlatform.BusinessLayer;
using TradingPlatform.PresentationLayer.Plugins;

namespace ThetaQuantowerBridgeV2;

public sealed record TickEvent(
    long Sequence,
    string Symbol,
    long ExchangeTimeMsc,
    long ReceiveTimeMsc,
    double Price,
    double Volume,
    string Side,
    double Bid,
    double Ask,
    string Source);

public sealed class BoundedTickBuffer
{
    private readonly object gate = new();
    private readonly Queue<TickEvent> queue = new();
    private readonly int capacity;

    public BoundedTickBuffer(int capacity) => this.capacity = Math.Clamp(capacity, 1_000, 1_000_000);

    public void Add(TickEvent item)
    {
        lock (gate)
        {
            queue.Enqueue(item);
            while (queue.Count > capacity) queue.Dequeue();
        }
    }

    public TickEvent[] After(long sequence, int limit)
    {
        lock (gate)
            return queue.Where(item => item.Sequence > sequence).Take(Math.Clamp(limit, 1, 10_000)).ToArray();
    }

    public long Count { get { lock (gate) return queue.Count; } }
}

public sealed class SymbolSubscription : IDisposable
{
    private readonly List<(EventInfo Event, Delegate Handler)> handlers = new();
    private readonly Symbol symbol;
    private readonly Action<Symbol, string, object[]> callback;

    public SymbolSubscription(Symbol symbol, Action<Symbol, string, object[]> callback)
    {
        this.symbol = symbol;
        this.callback = callback;
        Attach("NewLast");
        Attach("NewQuote");
        Attach("NewLevel2");
    }

    private void Attach(string eventName)
    {
        var eventInfo = symbol.GetType().GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance);
        if (eventInfo?.EventHandlerType is null) return;
        var invoke = eventInfo.EventHandlerType.GetMethod("Invoke");
        if (invoke is null) return;
        var parameters = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
        var boxed = Expression.NewArrayInit(typeof(object), parameters.Select(parameter => Expression.Convert(parameter, typeof(object))));
        var call = Expression.Call(
            Expression.Constant(this),
            typeof(SymbolSubscription).GetMethod(nameof(Dispatch), BindingFlags.NonPublic | BindingFlags.Instance)!,
            Expression.Constant(eventName),
            boxed);
        var handler = Expression.Lambda(eventInfo.EventHandlerType, call, parameters).Compile();
        eventInfo.AddEventHandler(symbol, handler);
        handlers.Add((eventInfo, handler));
    }

    private void Dispatch(string eventName, object[] args) => callback(symbol, eventName, args);

    public void Dispose()
    {
        foreach (var (eventInfo, handler) in handlers)
        {
            try { eventInfo.RemoveEventHandler(symbol, handler); } catch { }
        }
        handlers.Clear();
    }
}

public class ThetaQuantowerBridgeV2 : Plugin
{
    private const string Prefix = "http://127.0.0.1:17843/";
    private const int MaxResponseBytes = 2_000_000;
    private readonly ConcurrentDictionary<string, BoundedTickBuffer> buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SymbolSubscription> subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource stop = new();
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private HttpListener? listener;
    private Task? serverTask;
    private long sequence;
    private string? token;

    public static PluginInfo GetInfo() => new()
    {
        Name = "ThetaQuantowerBridgeV2",
        Title = "THETA Quantower Bridge V2",
        Group = PluginGroup.Trading,
        ShortName = "THETA V2"
    };

    public override Size DefaultSize => new(700, 420);

    public override void Initialize()
    {
        base.Initialize();
        token = Environment.GetEnvironmentVariable("THETA_QUANTOWER_TOKEN");
        listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        try
        {
            listener.Start();
            serverTask = Task.Run(() => ServeAsync(stop.Token));
        }
        catch (Exception error)
        {
            try { listener.Close(); } catch { }
            Application.Instance.Notifications.ShowError($"THETA Quantower Bridge V2 failed to start: {error.Message}");
        }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener?.IsListening == true)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch { continue; }
            _ = Task.Run(() => HandleAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsAuthorized(context.Request)) { context.Response.StatusCode = 401; return; }
            context.Response.Headers["Access-Control-Allow-Origin"] = "http://127.0.0.1";
            context.Response.Headers["Cache-Control"] = "no-store";
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? string.Empty;
            var query = context.Request.QueryString;
            switch (path)
            {
                case "/health": await WriteAsync(context, new { apiVersion = "2", connected = Core.Instance?.Connections?.Any(connection => connection.State == ConnectionState.Connected) == true, symbols = Core.Instance?.Symbols?.Count ?? 0, connections = Core.Instance?.Connections?.Count ?? 0, stream = true, execution = "disabled" }); break;
                case "/symbols": await WriteAsync(context, GetSymbols()); break;
                case "/quote": await WriteAsync(context, GetQuote(query["symbol"])); break;
                case "/trades": await WriteAsync(context, GetTrades(query["symbol"], ParseLong(query["after"]), ParseInt(query["limit"], 1_000))); break;
                case "/stream": await StreamAsync(context, query["symbol"], ParseLong(query["after"]), cancellationToken); break;
                case "/market-book": await WriteAsync(context, GetMarketBook(query["symbol"])); break;
                case "/bars": await WriteAsync(context, GetBars(query["symbol"], query["timeframe"], ParseInt(query["limit"], 500))); break;
                default: context.Response.StatusCode = 404; await WriteAsync(context, new { error = "Not found" }); break;
            }
        }
        catch (Exception error)
        {
            context.Response.StatusCode = 500;
            try { await WriteAsync(context, new { error = error.Message[..Math.Min(400, error.Message.Length)] }); } catch { }
        }
        finally { try { context.Response.Close(); } catch { } }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (string.IsNullOrWhiteSpace(token)) return true;
        var header = request.Headers["Authorization"];
        return string.Equals(header, $"Bearer {token}", StringComparison.Ordinal);
    }

    private async Task StreamAsync(HttpListenerContext context, string? symbolName, long cursor, CancellationToken cancellationToken)
    {
        var symbol = FindSymbol(symbolName);
        if (symbol is null) { context.Response.StatusCode = 404; await WriteAsync(context, new { error = "Symbol not found" }); return; }
        EnsureSubscribed(symbol);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;
        using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false), 1024, leaveOpen: true);
        while (!cancellationToken.IsCancellationRequested && context.Response.OutputStream.CanWrite)
        {
            var events = GetBuffer(symbol).After(cursor, 2_000);
            foreach (var item in events)
            {
                await writer.WriteAsync($"id: {item.Sequence}\ndata: {JsonSerializer.Serialize(item, json)}\n\n");
                cursor = item.Sequence;
            }
            await writer.FlushAsync(cancellationToken);
            await Task.Delay(100, cancellationToken);
        }
    }

    private object[] GetSymbols()
    {
        return (Core.Instance?.Symbols ?? Array.Empty<Symbol>()).Take(200_000).Select(symbol => new
        {
            ticker = symbol.Name,
            id = symbol.Id,
            description = symbol.Description,
            path = symbol.Exchange?.Name,
            type = symbol.SymbolType.ToString(),
            digits = symbol.GetType().GetProperty("Digits")?.GetValue(symbol),
            point = symbol.TickSize,
            currency = symbol.QuotingCurrency?.Name
        }).ToArray();
    }

    private object GetQuote(string? symbolName)
    {
        var symbol = FindSymbol(symbolName) ?? throw new InvalidOperationException("Symbol not found");
        EnsureSubscribed(symbol);
        return new { ticker = symbol.Name, bid = symbol.Bid, ask = symbol.Ask, last = symbol.Last, volume = symbol.Volume, time = symbol.LastDateTime.ToUniversalTime().ToString("O") };
    }

    private object[] GetTrades(string? symbolName, long after, int limit)
    {
        var symbol = FindSymbol(symbolName) ?? throw new InvalidOperationException("Symbol not found");
        EnsureSubscribed(symbol);
        return GetBuffer(symbol).After(Math.Max(0, after), Math.Clamp(limit, 1, 10_000));
    }

    private object[] GetMarketBook(string? symbolName)
    {
        var symbol = FindSymbol(symbolName) ?? throw new InvalidOperationException("Symbol not found");
        var depth = symbol.DepthOfMarket;
        if (depth is null) return Array.Empty<object>();
        var result = new List<object>();
        foreach (var side in new[] { "Bids", "Asks" })
        {
            var values = depth.GetType().GetProperty(side)?.GetValue(depth) as IEnumerable;
            if (values is null) continue;
            foreach (var item in values.Cast<object>().Take(20_000)) result.Add(new { side = side[..^1].ToUpperInvariant(), price = ReadNumber(item, "Price"), volume = ReadNumber(item, "Size") });
        }
        return result.ToArray();
    }

    private object[] GetBars(string? symbolName, string? timeframe, int limit)
    {
        var symbol = FindSymbol(symbolName) ?? throw new InvalidOperationException("Symbol not found");
        var (basePeriod, multiplier, duration) = ParseTimeframe(timeframe);
        var period = Activator.CreateInstance(typeof(Period), basePeriod, multiplier) ?? throw new InvalidOperationException("Unable to create period");
        var method = symbol.GetType().GetMethod("GetHistory", new[] { typeof(Period), typeof(DateTime), typeof(DateTime) }) ?? throw new InvalidOperationException("Quantower history API is unavailable");
        var history = method.Invoke(symbol, new object[] { period, DateTime.UtcNow.Subtract(duration), DateTime.UtcNow }) as IEnumerable;
        if (history is null) return Array.Empty<object>();
        return history.Cast<object>().TakeLast(Math.Clamp(limit, 1, 5_000)).Select(item => new { time = ReadDate(item, "TimeRight").ToUnixTimeMilliseconds(), open = ReadNumber(item, "Open"), high = ReadNumber(item, "High"), low = ReadNumber(item, "Low"), close = ReadNumber(item, "Close"), volume = ReadNumber(item, "Volume") }).ToArray();
    }

    private void EnsureSubscribed(Symbol symbol)
    {
        subscriptions.GetOrAdd(symbol.Id, _ => new SymbolSubscription(symbol, OnSymbolEvent));
        buffers.GetOrAdd(symbol.Id, _ => new BoundedTickBuffer(100_000));
    }

    private void OnSymbolEvent(Symbol symbol, string eventName, object[] args)
    {
        if (!string.Equals(eventName, "NewLast", StringComparison.Ordinal)) return;
        var last = args.FirstOrDefault(item => item?.GetType().GetProperty("Price") is not null);
        if (last is null) return;
        var price = ReadNumber(last, "Price");
        var volume = Math.Max(0, ReadNumber(last, "Size"));
        if (!double.IsFinite(price) || price <= 0) return;
        var exchange = ReadDate(last, "DateTime");
        var receive = DateTimeOffset.UtcNow;
        var item = new TickEvent(Interlocked.Increment(ref sequence), symbol.Name, exchange.ToUnixTimeMilliseconds(), receive.ToUnixTimeMilliseconds(), price, volume, ReadText(last, "AggressorFlag"), symbol.Bid, symbol.Ask, "quantower");
        GetBuffer(symbol).Add(item);
    }

    private BoundedTickBuffer GetBuffer(Symbol symbol) => buffers.GetOrAdd(symbol.Id, _ => new BoundedTickBuffer(100_000));
    private Symbol? FindSymbol(string? name) => string.IsNullOrWhiteSpace(name) ? null : (Core.Instance?.Symbols ?? Array.Empty<Symbol>()).FirstOrDefault(symbol => string.Equals(symbol.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(symbol.Id, name, StringComparison.OrdinalIgnoreCase));

    private async Task WriteAsync(HttpListenerContext context, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, json);
        if (bytes.Length > MaxResponseBytes) throw new InvalidOperationException("Response exceeded safety limit");
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private static int ParseInt(string? value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;
    private static long ParseLong(string? value) => long.TryParse(value, out var parsed) && parsed >= 0 ? parsed : 0;
    private static string ReadText(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value)?.ToString() ?? "FLAT";
    private static double ReadNumber(object value, string name) => Convert.ToDouble(value.GetType().GetProperty(name)?.GetValue(value) ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    private static DateTimeOffset ReadDate(object value, string name) { var raw = value.GetType().GetProperty(name)?.GetValue(value); return raw is DateTime dt ? new DateTimeOffset(dt.ToUniversalTime()) : raw is DateTimeOffset dto ? dto.ToUniversalTime() : DateTimeOffset.UtcNow; }

    private static (BasePeriod Base, int Multiplier, TimeSpan Duration) ParseTimeframe(string? value)
    {
        var text = (value ?? "1m").Trim().ToLowerInvariant();
        var unit = text.EndsWith("h") ? 'h' : text.EndsWith("d") ? 'd' : 'm';
        var numberText = text.TrimEnd('m', 'h', 'd');
        var multiplier = Math.Clamp(int.TryParse(numberText, out var parsed) ? parsed : 1, 1, 1440);
        var basePeriod = unit switch { 'h' => BasePeriod.Hour, 'd' => BasePeriod.Day, _ => BasePeriod.Minute };
        var duration = unit switch { 'h' => TimeSpan.FromHours(multiplier * 500), 'd' => TimeSpan.FromDays(multiplier * 500), _ => TimeSpan.FromMinutes(multiplier * 500) };
        return (basePeriod, multiplier, duration);
    }
}
