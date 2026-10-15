using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GuiaFotos.Core;

public sealed record OAuthSession(string ClientId, string Subject, string AccessToken, string RefreshToken, string IdToken, DateTime Expires, string Scope);
public sealed class OpenAiLogin(ISecrets secrets, string hostIdFile, HttpMessageHandler? handler = null)
{
    private static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private string HostId()
    { if (!File.Exists(hostIdFile)) Files.AtomicText(hostIdFile, "urn:uuid:" + Guid.NewGuid()); return File.ReadAllText(hostIdFile).Trim(); }
    public async Task SignIn(Action<string>? launchBrowser = null, CancellationToken token = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromMinutes(5)); token = limit.Token;
        var previous = secrets.Get("openai-oauth") is string stored ? Json.Read<OAuthSession>(stored) : null;
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        var prefix = $"http://127.0.0.1:{port}/"; var redirect = prefix + "auth/callback";
        using var listener = new HttpListener(); listener.Prefixes.Add(prefix); listener.Start();
        using var registration = token.Register(listener.Stop);
        var state = Random(); var nonce = Random(); var verifier = Random(); var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = previous?.ClientId ?? "dynamic_agent_client", ["ext_agent_host_id"] = HostId(),
            ["response_type"] = "code", ["redirect_uri"] = redirect, ["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
            ["resource"] = "https://api.openai.com/v1", ["state"] = state, ["nonce"] = nonce, ["code_challenge_method"] = "S256", ["code_challenge"] = challenge
        };
        if (previous == null) parameters["agent_name_hint"] = "GuiaFotos"; else parameters["id_token_hint"] = previous.IdToken;
        var url = "https://auth.openai.com/api/accounts/authorize?" + string.Join('&', parameters.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
        (launchBrowser ?? (uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })))(url);
        HttpListenerContext context;
        do { context = await listener.GetContextAsync().WaitAsync(token); if (context.Request.Url?.AbsolutePath != "/auth/callback") { context.Response.StatusCode = 404; context.Response.Close(); } }
        while (context.Request.Url?.AbsolutePath != "/auth/callback");
        var query = context.Request.QueryString;
        if (query["state"] != state) { context.Response.StatusCode = 400; context.Response.Close(); throw new InvalidDataException("Retorno OAuth não corresponde a esta tentativa."); }
        if (query["error"] != null) { context.Response.StatusCode = 403; context.Response.Close(); throw new UnauthorizedAccessException("Login não autorizado."); }
        var clientId = query["client_id"] ?? previous?.ClientId;
        if (string.IsNullOrEmpty(clientId) || clientId == "dynamic_agent_client" || previous != null && clientId != previous.ClientId || query["code"] == null)
        { context.Response.StatusCode = 400; context.Response.Close(); throw new InvalidDataException("Registro OAuth incompleto."); }
        var code = query["code"]!;
        var message = Encoding.UTF8.GetBytes("Login recebido. Volte ao GuiaFotos para concluir a validação.");
        await context.Response.OutputStream.WriteAsync(message, token); context.Response.Close(); listener.Stop();
        using var http = new HttpClient(handler ?? new HttpClientHandler(), handler == null);
        using var response = await http.PostAsync("https://auth.openai.com/api/accounts/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["resource"] = "https://api.openai.com/v1" }), token);
        response.EnsureSuccessStatusCode(); using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); var root = data.RootElement;
        var idToken = root.GetProperty("id_token").GetString()!;
        var jwt = await ValidateIdentity(idToken, clientId, nonce, previous?.Subject, http, token);
        var scope = root.GetProperty("scope").GetString()!;
        if (!scope.Split(' ').Contains("chatgpt.tokens.use.direct")) throw new UnauthorizedAccessException("A conta não autorizou uso do plano ChatGPT para inferência.");
        secrets.Set("openai-oauth", Json.Write(new OAuthSession(clientId, jwt.Subject, root.GetProperty("access_token").GetString()!, root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString()! : "", idToken, DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()), scope)));
    }
    public async Task<string> Token(CancellationToken token = default)
    {
        var session = Json.Read<OAuthSession>(secrets.Get("openai-oauth") ?? throw new UnauthorizedAccessException("Entre com ChatGPT primeiro."));
        if (!session.Scope.Split(' ').Contains("chatgpt.tokens.use.direct")) throw new UnauthorizedAccessException("A sessão não autoriza inferência.");
        if (session.Expires > DateTime.UtcNow.AddMinutes(1)) return session.AccessToken;
        if (session.RefreshToken.Length == 0) throw new UnauthorizedAccessException("Entre novamente com ChatGPT.");
        using var http = new HttpClient(handler ?? new HttpClientHandler(), handler == null);
        using var response = await http.PostAsync("https://auth.openai.com/api/accounts/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["client_id"] = session.ClientId, ["refresh_token"] = session.RefreshToken, ["resource"] = "https://api.openai.com/v1" }), token);
        if (!response.IsSuccessStatusCode) throw new UnauthorizedAccessException("Sessão expirada. Entre novamente com ChatGPT.");
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); var root = data.RootElement;
        var scope = root.TryGetProperty("scope", out var granted) ? granted.GetString()! : session.Scope;
        if (!scope.Split(' ').Contains("chatgpt.tokens.use.direct")) { SignOut(); throw new UnauthorizedAccessException("Permissão de inferência revogada; entre novamente."); }
        var idToken = root.TryGetProperty("id_token", out var identity) ? identity.GetString()! : session.IdToken;
        if (root.TryGetProperty("id_token", out _))
        {
            await ValidateIdentity(idToken, session.ClientId, null, session.Subject, http, token);
        }
        session = session with { AccessToken = root.GetProperty("access_token").GetString()!, RefreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString()! : session.RefreshToken,
            Expires = DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()), Scope = scope, IdToken = idToken };
        secrets.Set("openai-oauth", Json.Write(session)); return session.AccessToken;
    }
    public void SignOut() => secrets.Remove("openai-oauth");
    public static async Task<JsonWebToken> ValidateIdentity(string idToken, string clientId, string? nonce, string? subject, HttpClient http, CancellationToken token = default)
    {
        var jwks = new JsonWebKeySet(await http.GetStringAsync("https://auth.openai.com/.well-known/jwks.json", token));
        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        { ValidateIssuer = true, ValidIssuer = "https://auth.openai.com", ValidateAudience = true, ValidAudience = clientId,
            ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKeys = jwks.GetSigningKeys(), ClockSkew = TimeSpan.FromMinutes(1) });
        if (!validated.IsValid) throw new UnauthorizedAccessException("ID token inválido.");
        var jwt = new JsonWebToken(idToken);
        if (nonce != null && (!jwt.TryGetClaim("nonce", out var claim) || claim.Value != nonce) || subject != null && jwt.Subject != subject || string.IsNullOrEmpty(jwt.Subject)) throw new UnauthorizedAccessException("Identidade OAuth não corresponde à tentativa.");
        return jwt;
    }
}
