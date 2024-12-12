using Newtonsoft.Json;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace MusicDimension
{
    public class PkceTest
    {
        private const string RedirectUri = "http://localhost:8888/callback"; // Muss mit der Redirect URI im Spotify Dashboard übereinstimmen
        private const string ClientId = "c97ae89ab8cd4704bc2f83d3cd3efb6d"; // Deine Client ID hier einfügen
        private const string SpotifyTokenUrl = "https://accounts.spotify.com/api/token";

        private string accessToken;
        private string refreshToken;
        private int expiresIn;
        private DateTime createdAt;

        private async Task<string?> RefreshAccessTokenAsync(string clientId, string lastKnownRefreshToken)
        {
            using var httpClient = new HttpClient();

            // Prepare the request content
            var content = new StringContent(
                $"grant_type=refresh_token&refresh_token={lastKnownRefreshToken}&client_id={clientId}",
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

            return tokenResponse?.AccessToken;
        }

        private Task<bool> SaveTokenAsync()
        {
            var tokens = new LastKnownTokens
            {
                ExpiresIn = expiresIn,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                CreatedAt = createdAt
            };

            JsonFileHandler.SaveObjectToJsonFile($@".\tokens.json", tokens);

            return Task.FromResult(true);
        }

        public async Task Test()
        {
            // check for token file
            var tokens = JsonFileHandler.ReadObjectFromJsonFile<LastKnownTokens>($@".\tokens.json");

            // refresh else new connect
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (tokens != null)
            {
                accessToken = tokens.AccessToken;
                refreshToken = tokens.RefreshToken;
                expiresIn = tokens.ExpiresIn;
                createdAt = tokens.CreatedAt;

                if (tokens.IsExpired)
                {
                    var newAccessToken = await RefreshAccessTokenAsync(ClientId, refreshToken);
                }
            }
            else
            {
                // 1. PKCE Code Verifier und Challenge generieren
                var codeVerifier = GenerateCodeVerifier();
                var codeChallenge = GenerateCodeChallenge(codeVerifier);

                // 2. Authorize URL erstellen
                var loginRequest = new LoginRequest(new Uri(RedirectUri), ClientId, LoginRequest.ResponseType.Code)
                {
                    CodeChallengeMethod = "S256",
                    CodeChallenge = codeChallenge,
                    Scope = [Scopes.UserReadPrivate, Scopes.UserReadPlaybackState, Scopes.UserModifyPlaybackState]
                };

                var authUri = loginRequest.ToUri();

                // 3. Browser öffnen, um Benutzer zu authentifizieren
                Console.WriteLine("Öffne den Browser für die Authentifizierung...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = authUri.ToString(),
                    UseShellExecute = true
                });

                // 4. Redirect URI abwarten (HTTP-Listener starten)
                var authorizationCode = await WaitForAuthorizationCode();

                // 5. Access Token mit PKCE anfordern
                var tokenRequest = new PKCETokenRequest(ClientId, authorizationCode, new Uri(RedirectUri), codeVerifier);
                var oauthClient = new OAuthClient();
                var tokenResponse = await oauthClient.RequestToken(tokenRequest);

                Console.WriteLine($"Access Token: {tokenResponse.AccessToken}");
                Console.WriteLine($"Refresh Token: {tokenResponse.RefreshToken}");
                Console.WriteLine($"Token expires in: {tokenResponse.ExpiresIn} seconds");

                accessToken = tokenResponse.AccessToken;
                refreshToken = tokenResponse.RefreshToken;
                expiresIn = tokenResponse.ExpiresIn;
                createdAt = tokenResponse.CreatedAt;

                var success = await SaveTokenAsync();
            }

            // 6. Spotify Client mit dem Access Token verwenden
            var spotify = new SpotifyClient(accessToken);

            // Beispiel: Benutzerinformationen abrufen
            var profile = await spotify.UserProfile.Current();
            Console.WriteLine($"Hello, {profile.DisplayName}! Your Spotify ID is {profile.Id}");

            // start meins
            //const string trackUri = "spotify:track:3n3Ppam7vgaVa1iaRUc9Lp";
            var devices = await spotify.Player.GetAvailableDevices();
            var deviceId = devices.Devices.FirstOrDefault(d => d.Type == "Smartphone")?.Id;

            var searchRequest = new SearchRequest(SearchRequest.Types.Artist | SearchRequest.Types.Album, "welle:erdball Tanzmusik für Roboter");
            var searchResult = await spotify.Search.Item(searchRequest);
            // spotify:album:6r5Z1GCgD8diUD9inajKvd
            // ende meins



            // Song auf dem Gerät abspielen
            var playbackRequest = new PlayerResumePlaybackRequest
            {
                ContextUri = searchResult.Albums.Items[9].Uri,
                //Uris = new List<string> {  },
                DeviceId = deviceId
            };
            await spotify.Player.ResumePlayback(playbackRequest);
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
        private static async Task<string?> WaitForAuthorizationCode()
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
            return code;
        }
    }
}
