using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PetDesktop;

public sealed class Settings
{
    public string? Token { get; set; }
    public int Size { get; set; } = 64;
    public string? SheetUrl { get; set; }
    public string? SheetFile { get; set; }
    public string? Name { get; set; }
    public int Cols { get; set; } = 4;
    public int Rows { get; set; } = 3;
    public string? Character { get; set; }
}

public sealed class StartReply { public string? Nonce { get; set; } }
public sealed class PollReply { public string? Token { get; set; } public bool Waiting { get; set; } public bool Expired { get; set; } }
public sealed class PairReply { public string? Token { get; set; } public string? Error { get; set; } }
public sealed class PetReply { public DeskPet? Pet { get; set; } public DeskEgg? Egg { get; set; } public DeskPets? Pets { get; set; } public string? Message { get; set; } public string? Error { get; set; } }
/// <summary>The owner's pets, to choose whom this computer shows (Chosen null — the one chosen on the site).</summary>
public sealed class DeskPets
{
    public string? Chosen { get; set; }
    public bool? Baby { get; set; }
    public List<DeskPetItem> List { get; set; } = new();
}
public sealed class DeskPetItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Stage { get; set; } = "";
    public bool CanBeBaby { get; set; }
}
public sealed class ChooseBody { public string? PetId { get; set; } public bool? Baby { get; set; } }
public sealed class DeskEgg
{
    public string Id { get; set; } = "";
    public DeskLook? Look { get; set; }
    public int MinutesLeft { get; set; }
    public double WarmLeft { get; set; }
    public double WarmPerTap { get; set; }
}
public sealed class DeskPet
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Stage { get; set; } = "";
    public string? Sheet { get; set; }
    public int Cols { get; set; } = 4;
    public int Rows { get; set; } = 3;
    public DeskLook? Look { get; set; }
    public bool Asleep { get; set; }
    public bool Away { get; set; }
    public bool Hungry { get; set; }
    public bool Bored { get; set; }
    public bool Tired { get; set; }
}
public sealed class DeskLook
{
    public string? Character { get; set; }
    public string? Size { get; set; }
    public string? Base { get; set; }
    public string? Mark { get; set; }
    public string? Pattern { get; set; }
    public bool Rare { get; set; }
}
public sealed class EventsBody { public int Strokes { get; set; } public int Minutes { get; set; } public int Warm { get; set; } }
public sealed class PairBody { public string Code { get; set; } = ""; }

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(PairReply))]
[JsonSerializable(typeof(StartReply))]
[JsonSerializable(typeof(PollReply))]
[JsonSerializable(typeof(PetReply))]
[JsonSerializable(typeof(EventsBody))]
[JsonSerializable(typeof(PairBody))]
[JsonSerializable(typeof(ChooseBody))]
partial class Json : JsonSerializerContext;

/// <summary>
/// Everything the app says to the internet goes through here, to one address only, and every request is written to the
/// journal (menu «Что я отправлял»): when, where, and exactly what was sent. The key itself is shown masked.
/// </summary>
static class Api
{
    public const string Site = "https://pet.91i.ru";
    static string BaseUrl => Environment.GetEnvironmentVariable("PUSHISHI_SITE") is { Length: > 0 } s ? s.TrimEnd('/') : Site;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "91iPet");
    public static string JournalFile => Path.Combine(Folder, "journal.txt");
    static readonly object JournalLock = new();

    public static string SiteUrl => BaseUrl;

    static void Journal(string line)
    {
        lock (JournalLock)
        {
            Directory.CreateDirectory(Folder);
            var f = new FileInfo(JournalFile);
            if (f.Exists && f.Length > 512 * 1024) f.Delete(); // keeps itself small
            File.AppendAllText(JournalFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
    }

    static string? _lastQuiet;

    static async Task<(int status, string body)> Send(HttpMethod method, string path, string? json, string? token, bool quiet = false)
    {
        var url = BaseUrl + path;
        using var req = new HttpRequestMessage(method, url);
        if (token != null) req.Headers.Add("X-Desk-Token", token);
        if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var sent = $"{method} {url}" + (token != null ? $"  [ключ приложения: {token[..4]}…]" : "") + (json != null ? $"  отправлено: {Flat(json)}" : "  (ничего не отправлено, только запрос)");
        try
        {
            using var resp = await Http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            // the wait for the site's confirmation asks every 2 s: written to the journal once, not hundreds of times
            var line = $"{sent}  → {(int)resp.StatusCode}";
            if (!quiet || line != _lastQuiet) Journal(quiet ? line + "  (повторяется каждые 2 с, пока ждём подтверждения на сайте)" : line);
            if (quiet) _lastQuiet = line;
            return ((int)resp.StatusCode, body);
        }
        catch (Exception e)
        {
            Journal($"{sent}  → нет связи ({e.GetType().Name})");
            return (0, "");
        }
    }

    static string Flat(string json) => string.Join(" ", json.Split('\n').Select(l => l.Trim()));

    public static async Task<PairReply> Pair(string code)
    {
        var (status, body) = await Send(HttpMethod.Post, "/api/desk/pair", JsonSerializer.Serialize(new PairBody { Code = code }, Json.Default.PairBody), null);
        if (status == 0) return new PairReply { Error = "Нет связи с сайтом. Проверьте интернет." };
        try { return JsonSerializer.Deserialize(body, Json.Default.PairReply) ?? new PairReply { Error = "Непонятный ответ" }; }
        catch { return new PairReply { Error = "Непонятный ответ сайта" }; }
    }

    /// <summary>A one-time link to open the site with (the player confirms there, in their own browser).</summary>
    public static async Task<string?> DeskStart()
    {
        var (status, body) = await Send(HttpMethod.Post, "/api/desk/start", "{}", null);
        if (status != 200) return null;
        try { return JsonSerializer.Deserialize(body, Json.Default.StartReply)?.Nonce; } catch { return null; }
    }

    public static async Task<PollReply?> DeskPoll(string nonce)
    {
        var (status, body) = await Send(HttpMethod.Get, "/api/desk/poll?nonce=" + Uri.EscapeDataString(nonce), null, null, quiet: true);
        if (status != 200) return null;
        try { return JsonSerializer.Deserialize(body, Json.Default.PollReply); } catch { return null; }
    }

    public static async Task<(int status, PetReply? reply)> Pet(string token)
    {
        var (status, body) = await Send(HttpMethod.Get, "/api/desk/pet", null, token);
        if (status == 0) return (0, null);
        try { return (status, JsonSerializer.Deserialize(body, Json.Default.PetReply)); } catch { return (status, null); }
    }

    /// <summary>Whom this computer shows (null — as on the site) and how old (null — as on the site).</summary>
    public static Task Choose(string token, string? petId, bool? baby) =>
        Send(HttpMethod.Post, "/api/desk/choose", JsonSerializer.Serialize(new ChooseBody { PetId = petId, Baby = baby }, Json.Default.ChooseBody), token);

    public static Task Events(string token, int strokes, int minutes, int warm) =>
        Send(HttpMethod.Post, "/api/desk/events", JsonSerializer.Serialize(new EventsBody { Strokes = strokes, Minutes = minutes, Warm = warm }, Json.Default.EventsBody), token);

    /// <summary>The pet's picture sheet (a public PNG). Kept on disk, so the pet lives on without internet.</summary>
    public static async Task<string?> Download(string url)
    {
        try
        {
            using var resp = await Http.GetAsync(url);
            Journal($"GET {url}  (картинка питомца)  → {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) return null;
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, "pet-sheet.png");
            await File.WriteAllBytesAsync(file + ".new", await resp.Content.ReadAsByteArrayAsync());
            File.Move(file + ".new", file, true);
            return file;
        }
        catch (Exception e) { Journal($"GET {url}  → нет связи ({e.GetType().Name})"); return null; }
    }

    // ───── Settings ─────
    static string SettingsFile => Path.Combine(Folder, "settings.json");

    public static Settings LoadSettings()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(SettingsFile), Json.Default.Settings) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public static void SaveSettings(Settings s)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s, Json.Default.Settings));
    }
}
