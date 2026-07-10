using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Zimbar;

public record EmailAccount(
    string Id,
    string Provider,
    string DisplayName,
    string Address,
    string? AccessToken = null,
    string? RefreshToken = null,
    DateTimeOffset? ExpiresAt = null);

public record EmailItem(
    string Provider,
    string AccountId,
    string AccountName,
    string MessageId,
    string From,
    string Subject,
    string Snippet,
    string Body,
    DateTimeOffset When,
    bool Unread,
    string WebLink);

public record EmailOAuthSettings(string GmailClientId = "", string OutlookClientId = "", string GmailClientSecret = "");

public static class EmailAccounts
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zimbar");
    private static readonly string PathFile = Path.Combine(Dir, "email_accounts.json");
    private static readonly string SettingsFile = Path.Combine(Dir, "email_oauth.json");

    public static List<EmailAccount> Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return new List<EmailAccount>();
            var accounts = JsonSerializer.Deserialize<List<EmailAccount>>(File.ReadAllText(PathFile)) ?? new List<EmailAccount>();
            return accounts
                .Where(a => !string.IsNullOrWhiteSpace(a.Provider))
                .Select(Normalize)
                .Select(a => a with
                {
                    AccessToken = Unprotect(a.AccessToken),
                    RefreshToken = Unprotect(a.RefreshToken)
                })
                .ToList();
        }
        catch { return new List<EmailAccount>(); }
    }

    public static void Save(List<EmailAccount> accounts)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var stored = accounts.Select(Normalize).Select(a => a with
            {
                AccessToken = Protect(a.AccessToken),
                RefreshToken = Protect(a.RefreshToken)
            }).ToList();
            File.WriteAllText(PathFile, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void Upsert(EmailAccount account)
    {
        var accounts = Load();
        accounts.RemoveAll(a => a.Id == account.Id);
        accounts.Add(account);
        Save(accounts);
    }

    public static bool CanFetch(EmailAccount account)
        => !string.IsNullOrWhiteSpace(account.AccessToken) || !string.IsNullOrWhiteSpace(account.RefreshToken);

    public static EmailOAuthSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new EmailOAuthSettings();
            var settings = JsonSerializer.Deserialize<EmailOAuthSettings>(File.ReadAllText(SettingsFile)) ?? new EmailOAuthSettings();
            return settings with { GmailClientSecret = Unprotect(settings.GmailClientSecret) ?? "" };
        }
        catch { return new EmailOAuthSettings(); }
    }

    public static void SaveSettings(EmailOAuthSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var stored = settings with { GmailClientSecret = Protect(settings.GmailClientSecret) ?? "" };
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static string ClientIdFor(string provider)
    {
        var s = LoadSettings();
        return provider.Equals("gmail", StringComparison.OrdinalIgnoreCase)
            ? s.GmailClientId.Trim()
            : s.OutlookClientId.Trim();
    }

    public static string GmailClientSecret()
        => LoadSettings().GmailClientSecret.Trim();

    public static bool IsClientIdPlausible(string provider, string clientId)
    {
        clientId = clientId.Trim();
        if (provider.Equals("gmail", StringComparison.OrdinalIgnoreCase))
            return clientId.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase);
        return Guid.TryParse(clientId, out _);
    }

    public static string ClientIdHint(string provider)
        => provider.Equals("gmail", StringComparison.OrdinalIgnoreCase)
            ? "O Client ID do Google precisa ser de app Desktop e terminar com .apps.googleusercontent.com."
            : "Use o Application (client) ID. Para Outlook pessoal, o app do Azure precisa aceitar personal Microsoft accounts.";

    public static string ProviderLabel(string provider)
        => provider.Equals("gmail", StringComparison.OrdinalIgnoreCase) ? "Gmail" : "Outlook";

    public static string FolderLabel(string folder) => folder switch
    {
        "spam" => "Spam",
        "trash" => "Lixo",
        "gmail_social" => "Social",
        "gmail_promotions" => "Promocoes",
        "gmail_updates" => "Atualizacoes",
        "gmail_forums" => "Foruns",
        _ => "Entrada"
    };

    public static void OpenFolder(EmailAccount account, string folder)
    {
        string url = account.Provider.Equals("gmail", StringComparison.OrdinalIgnoreCase)
            ? GmailFolderUrl(account.Address, folder)
            : OutlookFolderUrl(account.Address, folder);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static void OpenMessage(EmailItem item)
        => Process.Start(new ProcessStartInfo(item.WebLink) { UseShellExecute = true });

    private static string GmailFolderUrl(string address, string folder)
    {
        string hash = folder switch
        {
            "spam" => "spam",
            "trash" => "trash",
            "gmail_social" => "category/social",
            "gmail_promotions" => "category/promotions",
            "gmail_updates" => "category/updates",
            "gmail_forums" => "category/forums",
            _ => "inbox"
        };
        string account = string.IsNullOrWhiteSpace(address) ? "0" : Uri.EscapeDataString(address.Trim());
        return $"https://mail.google.com/mail/u/{account}/#{hash}";
    }

    private static string OutlookFolderUrl(string address, string folder)
    {
        string path = folder switch { "spam" => "junkemail", "trash" => "deleteditems", _ => "inbox" };
        string hint = string.IsNullOrWhiteSpace(address) ? "" : "?login_hint=" + Uri.EscapeDataString(address.Trim());
        return $"https://outlook.live.com/mail/0/{path}{hint}";
    }

    private static EmailAccount Normalize(EmailAccount account)
    {
        string provider = account.Provider.Equals("gmail", StringComparison.OrdinalIgnoreCase) ? "gmail" : "outlook";
        string name = string.IsNullOrWhiteSpace(account.DisplayName) ? ProviderLabel(provider) : account.DisplayName.Trim();
        string id = string.IsNullOrWhiteSpace(account.Id) ? Guid.NewGuid().ToString("N") : account.Id;
        return account with { Id = id, Provider = provider, DisplayName = name, Address = account.Address.Trim() };
    }

    private static string? Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("dpapi:", StringComparison.Ordinal))
            return value;
        try
        {
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(data);
        }
        catch { return value; }
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("dpapi:", StringComparison.Ordinal))
            return value;
        try
        {
            var data = Convert.FromBase64String(value["dpapi:".Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }
}

public static class EmailOAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    public static async Task<EmailAccount> ConnectAsync(string provider)
    {
        string clientId = EmailAccounts.ClientIdFor(provider);
        if (clientId.Length == 0)
            throw new InvalidOperationException("client_id nao configurado");

        var token = await RunOAuthAsync(provider, clientId);
        return provider.Equals("gmail", StringComparison.OrdinalIgnoreCase)
            ? await BuildGmailAccount(token)
            : await BuildOutlookAccount(token);
    }

    public static async Task<List<EmailItem>> FetchAsync(EmailAccount account, string folder, int max = 25)
    {
        var fresh = await EnsureTokenAsync(account);
        if (string.IsNullOrWhiteSpace(fresh.AccessToken)) return new List<EmailItem>();
        return fresh.Provider.Equals("gmail", StringComparison.OrdinalIgnoreCase)
            ? await FetchGmailAsync(fresh, folder, max)
            : await FetchOutlookAsync(fresh, folder, max);
    }

    public static async Task<string> FetchBodyAsync(EmailAccount account, EmailItem item)
    {
        var fresh = await EnsureTokenAsync(account);
        if (string.IsNullOrWhiteSpace(fresh.AccessToken) || item.MessageId.Length == 0) return "";
        return fresh.Provider.Equals("gmail", StringComparison.OrdinalIgnoreCase)
            ? await FetchGmailBodyAsync(fresh, item.MessageId)
            : await FetchOutlookBodyAsync(fresh, item.MessageId);
    }

    private static async Task<EmailAccount> EnsureTokenAsync(EmailAccount account)
    {
        if (!string.IsNullOrWhiteSpace(account.AccessToken)
            && account.ExpiresAt is DateTimeOffset exp
            && exp > DateTimeOffset.UtcNow.AddMinutes(2))
            return account;

        if (string.IsNullOrWhiteSpace(account.RefreshToken))
            return account;

        string clientId = EmailAccounts.ClientIdFor(account.Provider);
        if (clientId.Length == 0) return account;

        string tokenUrl = account.Provider == "gmail"
            ? "https://oauth2.googleapis.com/token"
            : "https://login.microsoftonline.com/common/oauth2/v2.0/token";

        var refreshForm = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = account.RefreshToken
        };
        if (account.Provider == "gmail")
        {
            string secret = EmailAccounts.GmailClientSecret();
            if (secret.Length > 0) refreshForm["client_secret"] = secret;
        }

        var json = await TokenRequest(tokenUrl, refreshForm);

        string access = json["access_token"]?.GetValue<string>() ?? account.AccessToken ?? "";
        string refresh = json["refresh_token"]?.GetValue<string>() ?? account.RefreshToken ?? "";
        int expires = json["expires_in"]?.GetValue<int>() ?? 3600;
        var fresh = account with
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires)
        };
        EmailAccounts.Upsert(fresh);
        return fresh;
    }

    private static async Task<JsonObject> RunOAuthAsync(string provider, string clientId)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(18));
        int port = FreePort();
        string redirect = provider.Equals("outlook", StringComparison.OrdinalIgnoreCase)
            ? $"http://localhost:{port}"
            : $"http://127.0.0.1:{port}";
        string listenerPrefix = redirect + "/";

        string scope = provider == "gmail"
            ? "https://www.googleapis.com/auth/gmail.readonly"
            : "offline_access User.Read Mail.Read";
        string authUrl = provider == "gmail"
            ? "https://accounts.google.com/o/oauth2/v2/auth"
            : "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
        string tokenUrl = provider == "gmail"
            ? "https://oauth2.googleapis.com/token"
            : "https://login.microsoftonline.com/common/oauth2/v2.0/token";

        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["prompt"] = provider == "gmail" ? "consent" : "select_account"
        };
        if (provider == "gmail") query["access_type"] = "offline";

        using var listener = new HttpListener();
        listener.Prefixes.Add(listenerPrefix);
        listener.Start();
        Process.Start(new ProcessStartInfo(authUrl + "?" + Form(query)) { UseShellExecute = true });

        var ctx = await listener.GetContextAsync();
        string code = ctx.Request.QueryString["code"] ?? "";
        string gotState = ctx.Request.QueryString["state"] ?? "";
        string html = "<html><body style='font-family:Segoe UI;padding:32px'>Zimbar conectado. Pode fechar esta janela.</body></html>";
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
        listener.Stop();

        if (code.Length == 0 || gotState != state)
            throw new InvalidOperationException("login cancelado");

        var tokenForm = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier
        };
        if (provider == "gmail")
        {
            string secret = EmailAccounts.GmailClientSecret();
            if (secret.Length == 0)
                throw new InvalidOperationException("Google Client Secret nao configurado");
            tokenForm["client_secret"] = secret;
        }

        return await TokenRequest(tokenUrl, tokenForm);
    }

    private static async Task<EmailAccount> BuildGmailAccount(JsonObject token)
    {
        string access = token["access_token"]?.GetValue<string>() ?? "";
        int expires = token["expires_in"]?.GetValue<int>() ?? 3600;
        string refresh = token["refresh_token"]?.GetValue<string>() ?? "";
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/profile");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        var res = await Http.SendAsync(req);
        var profile = await ResponseJson(res);
        string email = profile["emailAddress"]?.GetValue<string>() ?? "gmail";
        return new EmailAccount(Guid.NewGuid().ToString("N"), "gmail", "Gmail", email, access, refresh, DateTimeOffset.UtcNow.AddSeconds(expires));
    }

    private static async Task<EmailAccount> BuildOutlookAccount(JsonObject token)
    {
        string access = token["access_token"]?.GetValue<string>() ?? "";
        int expires = token["expires_in"]?.GetValue<int>() ?? 3600;
        string refresh = token["refresh_token"]?.GetValue<string>() ?? "";
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=displayName,mail,userPrincipalName");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        var res = await Http.SendAsync(req);
        var me = await ResponseJson(res);
        string email = me["mail"]?.GetValue<string>() ?? me["userPrincipalName"]?.GetValue<string>() ?? "outlook";
        string name = me["displayName"]?.GetValue<string>() ?? "Outlook";
        return new EmailAccount(Guid.NewGuid().ToString("N"), "outlook", name, email, access, refresh, DateTimeOffset.UtcNow.AddSeconds(expires));
    }

    private static async Task<List<EmailItem>> FetchGmailAsync(EmailAccount account, string folder, int max)
    {
        string label = folder switch
        {
            "spam" => "SPAM",
            "trash" => "TRASH",
            "gmail_social" => "CATEGORY_SOCIAL",
            "gmail_promotions" => "CATEGORY_PROMOTIONS",
            "gmail_updates" => "CATEGORY_UPDATES",
            "gmail_forums" => "CATEGORY_FORUMS",
            _ => "INBOX"
        };
        var list = await ApiJson($"https://gmail.googleapis.com/gmail/v1/users/me/messages?labelIds={label}&maxResults={max}", account.AccessToken);
        var messages = list["messages"] as JsonArray ?? new JsonArray();
        var items = new List<EmailItem>();
        foreach (var msg in messages.OfType<JsonObject>())
        {
            string id = msg["id"]?.GetValue<string>() ?? "";
            if (id.Length == 0) continue;
            var full = await ApiJson("https://gmail.googleapis.com/gmail/v1/users/me/messages/" + id
                + "?format=metadata&metadataHeaders=Subject&metadataHeaders=From&metadataHeaders=Date", account.AccessToken);
            var headers = full["payload"]?["headers"] as JsonArray ?? new JsonArray();
            string H(string name) => headers.OfType<JsonObject>()
                .FirstOrDefault(h => (h["name"]?.GetValue<string>() ?? "").Equals(name, StringComparison.OrdinalIgnoreCase))?["value"]?.GetValue<string>() ?? "";
            long internalMs = long.TryParse(full["internalDate"]?.GetValue<string>(), out var ms) ? ms : 0;
            string threadId = full["threadId"]?.GetValue<string>() ?? id;
            string hash = folder switch
            {
                "spam" => "spam",
                "trash" => "trash",
                "gmail_social" => "category/social",
                "gmail_promotions" => "category/promotions",
                "gmail_updates" => "category/updates",
                "gmail_forums" => "category/forums",
                _ => "inbox"
            };
            items.Add(new EmailItem("gmail", account.Id, account.DisplayName, id, H("From"), H("Subject"),
                full["snippet"]?.GetValue<string>() ?? "", "",
                DateTimeOffset.FromUnixTimeMilliseconds(internalMs),
                (full["labelIds"] as JsonArray)?.Any(x => x?.GetValue<string>() == "UNREAD") == true,
                $"https://mail.google.com/mail/u/{Uri.EscapeDataString(account.Address)}/#{hash}/{threadId}"));
        }
        return items;
    }

    private static async Task<List<EmailItem>> FetchOutlookAsync(EmailAccount account, string folder, int max)
    {
        if (folder.StartsWith("gmail_", StringComparison.OrdinalIgnoreCase))
            return new List<EmailItem>();
        string mailFolder = folder switch { "spam" => "junkemail", "trash" => "deleteditems", _ => "inbox" };
        string url = $"https://graph.microsoft.com/v1.0/me/mailFolders/{mailFolder}/messages?$top={max}&$select=id,subject,from,receivedDateTime,bodyPreview,webLink,isRead&$orderby=receivedDateTime desc";
        var json = await ApiJson(url, account.AccessToken);
        var arr = json["value"] as JsonArray ?? new JsonArray();
        return arr.OfType<JsonObject>().Select(m => new EmailItem(
            "outlook",
            account.Id,
            account.DisplayName,
            m["id"]?.GetValue<string>() ?? "",
            m["from"]?["emailAddress"]?["name"]?.GetValue<string>() ?? "",
            m["subject"]?.GetValue<string>() ?? "(sem assunto)",
            m["bodyPreview"]?.GetValue<string>() ?? "",
            "",
            DateTimeOffset.TryParse(m["receivedDateTime"]?.GetValue<string>(), out var dt) ? dt : DateTimeOffset.Now,
            !(m["isRead"]?.GetValue<bool>() ?? true),
            m["webLink"]?.GetValue<string>() ?? "https://outlook.live.com/mail/0/inbox")).ToList();
    }

    private static async Task<string> FetchGmailBodyAsync(EmailAccount account, string messageId)
    {
        var full = await ApiJson("https://gmail.googleapis.com/gmail/v1/users/me/messages/" + Uri.EscapeDataString(messageId) + "?format=full", account.AccessToken);
        return ExtractGmailBody(full["payload"] as JsonObject);
    }

    private static async Task<string> FetchOutlookBodyAsync(EmailAccount account, string messageId)
    {
        var msg = await ApiJson("https://graph.microsoft.com/v1.0/me/messages/" + Uri.EscapeDataString(messageId) + "?$select=body", account.AccessToken);
        return HtmlToText(msg["body"]?["content"]?.GetValue<string>() ?? "");
    }

    private static string ExtractGmailBody(JsonObject? payload)
    {
        if (payload is null) return "";
        string mime = payload["mimeType"]?.GetValue<string>() ?? "";
        string data = payload["body"]?["data"]?.GetValue<string>() ?? "";
        if (data.Length > 0)
        {
            string text = DecodeBase64Url(data);
            return mime.Equals("text/html", StringComparison.OrdinalIgnoreCase) ? HtmlToText(text) : text.Trim();
        }

        var parts = payload["parts"] as JsonArray;
        if (parts is null) return "";
        var collected = new List<string>();
        foreach (var part in parts.OfType<JsonObject>())
        {
            string text = ExtractGmailBody(part);
            if (text.Length > 0) collected.Add(text);
        }
        return string.Join("\n\n", collected).Trim();
    }

    private static string DecodeBase64Url(string data)
    {
        string s = data.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - s.Length % 4) % 4), '=');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return ""; }
    }

    private static string HtmlToText(string html)
    {
        if (html.Length == 0) return "";
        string text = System.Text.RegularExpressions.Regex.Replace(html, "<(br|p|div|tr|li)[^>]*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, "[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, "\n{3,}", "\n\n");
        return text.Trim();
    }

    private static async Task<JsonObject> ApiJson(string url, string? accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken ?? "");
        var res = await Http.SendAsync(req);
        return await ResponseJson(res);
    }

    private static async Task<JsonObject> TokenRequest(string url, Dictionary<string, string> form)
    {
        var res = await Http.PostAsync(url, new FormUrlEncodedContent(form));
        return await ResponseJson(res);
    }

    private static async Task<JsonObject> ResponseJson(HttpResponseMessage res)
    {
        string body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(CompactOAuthError(body, (int)res.StatusCode, res.ReasonPhrase ?? "erro"));
        return JsonNode.Parse(body) as JsonObject ?? new JsonObject();
    }

    private static string CompactOAuthError(string body, int code, string reason)
    {
        try
        {
            var json = JsonNode.Parse(body) as JsonObject;
            string err = json?["error"]?.GetValue<string>() ?? "";
            string desc = json?["error_description"]?.GetValue<string>()
                ?? json?["message"]?.GetValue<string>()
                ?? "";
            string text = (err.Length > 0 ? err + ": " : "") + desc;
            return text.Length > 0 ? Trunc(text, 260) : $"{code} {reason}";
        }
        catch
        {
            return body.Length > 0 ? Trunc(body, 260) : $"{code} {reason}";
        }
    }

    private static string Trunc(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "...";
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static string Form(Dictionary<string, string> values)
        => string.Join("&", values.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class EmailOAuthSettingsDialog : Window
{
    private readonly TextBox _gmail = new();
    private readonly TextBox _gmailSecret = new();
    private readonly TextBox _outlook = new();

    public EmailOAuthSettingsDialog()
    {
        Title = "OAuth do Email";
        Width = 560;
        Height = 345;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var settings = EmailAccounts.LoadSettings();
        var form = new StackPanel { Margin = new Thickness(18) };
        Content = form;

        form.Children.Add(new TextBlock
        {
            Text = "Credenciais OAuth",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        form.Children.Add(new TextBlock
        {
            Text = "Google: app Desktop, cole Client ID e Client Secret. Microsoft: use Application (client) ID; para Outlook pessoal, Supported account types deve incluir personal Microsoft accounts; redirect URI http://localhost.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        form.Children.Add(Label("Google OAuth Client ID"));
        _gmail.Text = settings.GmailClientId;
        form.Children.Add(_gmail);
        form.Children.Add(Label("Google Client Secret"));
        _gmailSecret.Text = settings.GmailClientSecret;
        form.Children.Add(_gmailSecret);
        form.Children.Add(Label("Microsoft/Azure Application Client ID"));
        _outlook.Text = settings.OutlookClientId;
        form.Children.Add(_outlook);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Salvar", Padding = new Thickness(12, 5, 12, 5) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        save.Click += (_, _) =>
        {
            string gmail = _gmail.Text.Trim();
            string gmailSecret = _gmailSecret.Text.Trim();
            string outlook = _outlook.Text.Trim();
            if (gmail.Length > 0 && !EmailAccounts.IsClientIdPlausible("gmail", gmail))
            {
                MessageBox.Show(this, EmailAccounts.ClientIdHint("gmail"), "Zimbar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (gmail.Length > 0 && gmailSecret.Length == 0)
            {
                MessageBox.Show(this, "Cole tambem o Google Client Secret do mesmo OAuth Client.", "Zimbar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (outlook.Length > 0 && !EmailAccounts.IsClientIdPlausible("outlook", outlook))
            {
                MessageBox.Show(this, EmailAccounts.ClientIdHint("outlook"), "Zimbar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            EmailAccounts.SaveSettings(new EmailOAuthSettings(gmail, outlook, gmailSecret));
            DialogResult = true;
            Close();
        };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        form.Children.Add(actions);
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Margin = new Thickness(0, 8, 0, 3)
    };
}
