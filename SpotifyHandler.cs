using Newtonsoft.Json;
using SpotifyAPI.Web;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace MusicDimension
{
    public class SpotifyHandler(Config config)
    {
        private const string SpotifyTokenUrl = "https://accounts.spotify.com/api/token";
        private const string RedirectUri = "http://localhost:8888/callback"; 

        private string _accessToken = string.Empty;
        private string _refreshToken = string.Empty;
        private int _expiresIn;
        private DateTime _createdAt;

        public async Task PlayAsync(string uri)
        {
            // check for or refresh access token
            await CheckToken();

            // create client
            var spotify = new SpotifyClient(_accessToken);

            var devices = await spotify.Player.GetAvailableDevices();
            var deviceId = devices.Devices.FirstOrDefault(d => d.Name == config.SpotifyDeviceName)?.Id;

            // actually play an album or song
            // TODO: save position of current track before changing the song or resume from saved position
            var playbackRequest = new PlayerResumePlaybackRequest
            {
                // TODO: extend so that more can be given than only one track
                //ContextUri = searchResult.Albums.Items[9].Uri, // for an album
                Uris = new List<string> { uri },                  // for a sinlge song or track from contexturi album
                DeviceId = deviceId
            };

            await spotify.Player.ResumePlayback(playbackRequest);
        }

        public async Task StopAsync()
        {
            // check for or refresh access token
            await CheckToken();

            // create client
            var spotify = new SpotifyClient(_accessToken);

            var devices = await spotify.Player.GetAvailableDevices();
            var deviceId = devices.Devices.FirstOrDefault(d => d.Name == config.SpotifyDeviceName)?.Id;

            await spotify.Player.PausePlayback();
        }

        private static async Task<SpotifyTokenResponse?> RefreshTokenAsync(string clientId, string refreshToken)
        {
            using var httpClient = new HttpClient();

            // Prepare the request content
            var content = new StringContent(
                $"grant_type=refresh_token&refresh_token={refreshToken}&client_id={clientId}",
                Encoding.UTF8,
                "application/x-www-form-urlencoded");

            // Send the POST request
            var response = await httpClient.PostAsync(SpotifyTokenUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to refresh token. Status Code: {response.StatusCode}, Response: {await response.Content.ReadAsStringAsync()}");
            }

            // Parse the response JSON
            var json = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonConvert.DeserializeObject<SpotifyTokenResponse>(json);

            return tokenResponse;
        }

        private Task<bool> SaveTokenAsync()
        {
            var tokens = new LastKnownTokens
            {
                ExpiresIn = _expiresIn,
                AccessToken = _accessToken,
                RefreshToken = _refreshToken,
                CreatedAt = _createdAt
            };

            JsonFileHandler.SaveObjectToJsonFile(config.TokenFileName, tokens);

            return Task.FromResult(true);
        }

        private async Task CheckToken()
        {
            // check for token file
            var tokens = JsonFileHandler.ReadObjectFromJsonFile<LastKnownTokens>(config.TokenFileName);

            // refresh else new connect
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (tokens != null)
            {
                _accessToken = tokens.AccessToken;
                _refreshToken = tokens.RefreshToken;
                _expiresIn = tokens.ExpiresIn;
                _createdAt = tokens.CreatedAt;

                if (tokens.IsExpired)
                {
                    var newToken = await RefreshTokenAsync(config.ClientId, _refreshToken);
                    if (newToken != null)
                    {
                        _accessToken = newToken.AccessToken;
                        _expiresIn = newToken.ExpiresIn;
                        _createdAt = newToken.CreatedAt;

                        await SaveTokenAsync();
                    }
                    else
                    {
                        throw new NullReferenceException(nameof(newToken));
                    }
                }
            }
            else
            {
                // 1. PKCE Code Verifier und Challenge generieren
                var codeVerifier = GenerateCodeVerifier();
                var codeChallenge = GenerateCodeChallenge(codeVerifier);

                // 2. Authorize URL erstellen
                var loginRequest = new LoginRequest(new Uri(RedirectUri), config.ClientId, LoginRequest.ResponseType.Code)
                {
                    CodeChallengeMethod = "S256",
                    CodeChallenge = codeChallenge,
                    Scope = [Scopes.UserReadPrivate, Scopes.UserReadPlaybackState, Scopes.UserModifyPlaybackState]
                };

                var authUri = loginRequest.ToUri();

                Process.Start(new ProcessStartInfo
                {
                    FileName = authUri.ToString(),
                    UseShellExecute = true
                });

                // 4. Redirect URI abwarten (HTTP-Listener starten)
                var authorizationCode = await WaitForAuthorizationCode();

                // 5. Access Token mit PKCE anfordern
                var tokenRequest = new PKCETokenRequest(config.ClientId, authorizationCode, new Uri(RedirectUri), codeVerifier);
                var oauthClient = new OAuthClient();
                var tokenResponse = await oauthClient.RequestToken(tokenRequest);

                _accessToken = tokenResponse.AccessToken;
                _refreshToken = tokenResponse.RefreshToken;
                _expiresIn = tokenResponse.ExpiresIn;
                _createdAt = tokenResponse.CreatedAt;

                await SaveTokenAsync();
            }
        }
        
        // Funktion zum Code-Verifier-Generieren
        private static string GenerateCodeVerifier()
        {
            var bytes = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // Funktion zum Code-Challenge-Generieren
        private static string GenerateCodeChallenge(string codeVerifier)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
            return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // Funktion, die auf die Weiterleitung wartet und den Code extrahiert
        private static async Task<string> WaitForAuthorizationCode()
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(RedirectUri + "/");
            listener.Start();

            Console.WriteLine("Warte auf die Weiterleitung...");

            var context = await listener.GetContextAsync();
            var code = context.Request.QueryString["code"];
            var response = context.Response;

            const string responseString = "<html><body>Du kannst dieses Fenster schließen.</body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();

            listener.Stop();
            return code ?? string.Empty;
        }
    }
}
