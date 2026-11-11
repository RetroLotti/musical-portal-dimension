namespace MusicDimension.misc
{
    using MusicDimension.models;
    using Newtonsoft.Json;
    using SpotifyAPI.Web;
    using System.Diagnostics;
    using System.Net;
    using System.Text;

    public class SpotifyHandler(Config config, SpotifyClientConfig? clientConfiguration = null)
    {
        private Config _config = config;
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        public Config Configuration => _config;

        private const string SpotifyTokenUrl = "https://accounts.spotify.com/api/token";
        private const string RedirectUri = "http://127.0.0.1:8888/callback";

        private string _accessToken = string.Empty;
        private string _refreshToken = string.Empty;
        private int _expiresIn;
        private DateTime _createdAt;

        public static async Task<SpotifyHandler> CreateAsync(Config config)
        {
            var instance = new SpotifyHandler(config);
            await instance.CheckToken(allowInteractive: true);
            return instance;
        }

        public async Task InitializeAsync()
        {
            if (!_config.Enable) return;
            if (!File.Exists(_config.TokenFileName))
            {
                Console.WriteLine("Spotify needs an initial interactive OAuth login. Local playback and web settings remain available.");
                return;
            }
            await ExecuteAsync(async spotify => { await spotify.Player.GetAvailableDevices(); });
        }

        public async Task UpdateConfigurationAsync(Config configuration, string fileName)
        {
            await _operationLock.WaitAsync();
            try
            {
                if (configuration.ClientId != _config.ClientId)
                {
                    var tokens = JsonFileHandler.ReadObjectFromJsonFile<LastKnownTokens>(_config.TokenFileName);
                    if (tokens != null && string.IsNullOrEmpty(tokens.ClientId))
                    {
                        tokens.ClientId = _config.ClientId;
                        await SaveJsonAsync(_config.TokenFileName, tokens);
                    }
                }
                await SaveJsonAsync(fileName, configuration);
                _config = configuration;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        private static async Task SaveJsonAsync<T>(string fileName, T value)
        {
            var temporaryFile = fileName + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryFile, JsonConvert.SerializeObject(value, Formatting.Indented));
                File.Move(temporaryFile, fileName, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
            }
        }

        private async Task ExecuteAsync(Func<SpotifyClient, Task> action, CancellationToken cancellationToken = default)
        {
            await _operationLock.WaitAsync(cancellationToken);
            try
            {
                if (!_config.Enable) throw new InvalidOperationException("Spotify is disabled.");
                await CheckToken();
                await action(new SpotifyClient((clientConfiguration ?? SpotifyClientConfig.CreateDefault()).WithToken(_accessToken)));
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task<IReadOnlyList<SpotifySearchResult>> SearchAsync(string query, string type, CancellationToken cancellationToken = default)
        {
            var searchType = type switch
            {
                "track" => SearchRequest.Types.Track,
                "album" => SearchRequest.Types.Album,
                "playlist" => SearchRequest.Types.Playlist,
                _ => throw new ArgumentException("Choose tracks, albums, or playlists.", nameof(type))
            };
            var results = new List<SpotifySearchResult>();
            await ExecuteAsync(async spotify =>
            {
                var response = await spotify.Search.Item(new SearchRequest(searchType, query) { Limit = 10 }, cancellationToken);
                if (type == "track")
                {
                    foreach (var track in response.Tracks?.Items ?? [])
                    {
                        if (track?.Id == null) continue;
                        results.Add(new(track.Id, track.Name, type, string.Join(", ", track.Artists.Select(artist => artist.Name)),
                            track.Album?.Images?.LastOrDefault()?.Url));
                    }
                }
                else if (type == "album")
                {
                    foreach (var album in response.Albums?.Items ?? [])
                    {
                        if (album?.Id == null) continue;
                        results.Add(new(album.Id, album.Name, type, string.Join(", ", album.Artists.Select(artist => artist.Name)),
                            album.Images?.LastOrDefault()?.Url));
                    }
                }
                else
                {
                    foreach (var playlist in response.Playlists?.Items ?? [])
                    {
                        if (playlist?.Id == null) continue;
                        results.Add(new(playlist.Id, playlist.Name ?? "Untitled playlist", type, playlist.Owner?.DisplayName ?? playlist.Owner?.Id ?? "",
                            playlist.Images?.LastOrDefault()?.Url));
                    }
                }
            }, cancellationToken);
            return results;
        }

        /// <summary>A Spotify catalog match suitable for assigning to a tag.</summary>
        public sealed record SpotifySearchResult(string Id, string Name, string Type, string Subtitle, string? ImageUrl);

        private async Task<string> GetDeviceIdAsync(SpotifyClient spotify, bool activate = false)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var devices = await spotify.Player.GetAvailableDevices();
                var device = devices.Devices.FirstOrDefault(device =>
                    string.Equals(device.Name, _config.SpotifyDeviceName, StringComparison.OrdinalIgnoreCase));
                if (device?.Id != null)
                {
                    if (device.IsRestricted) throw new InvalidOperationException("The configured Spotify device does not allow playback control.");
                    if (activate && !device.IsActive)
                    {
                        await spotify.Player.TransferPlayback(new PlayerTransferPlaybackRequest(new List<string> { device.Id }) { Play = false });
                    }
                    return device.Id;
                }
                if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(1));
            }

            throw new InvalidOperationException($"Spotify device '{_config.SpotifyDeviceName}' is unavailable. Check that the Pi's Spotify Connect receiver is running and signed in; the Web API cannot start or authenticate the receiver. No other device was selected.");
        }

        public async Task PlayAsync(string spotifyType, string spotifyId)
        {
            await ExecuteAsync(async spotify =>
            {
                var deviceId = await GetDeviceIdAsync(spotify, activate: true);
                var uri = $"spotify:{spotifyType}:{spotifyId}";
                var playbackRequest = new PlayerResumePlaybackRequest { DeviceId = deviceId };

                switch (spotifyType)
                {
                    case "album":
                    case "playlist":
                        playbackRequest.ContextUri = uri;
                        break;
                    default:
                        playbackRequest.Uris = new List<string> { uri };
                        break;
                }

                await spotify.Player.ResumePlayback(playbackRequest);
            });
        }

        public async Task WhateverAsync()
        {
            // check for or refresh access token
            await CheckToken();

            // create client
            var spotify = new SpotifyClient(_accessToken);

            //spotify.Player.GetCurrentlyPlaying()
        }

        public async Task PauseAsync()
        {
            await ExecuteAsync(async spotify =>
            {
                var deviceId = await GetDeviceIdAsync(spotify);
                await spotify.Player.PausePlayback(new PlayerPausePlaybackRequest { DeviceId = deviceId });
            });
        }

        public async Task PreviousTrackAsync()
        {
            await ExecuteAsync(async spotify =>
            {
                var deviceId = await GetDeviceIdAsync(spotify);
                await spotify.Player.SkipPrevious(new PlayerSkipPreviousRequest { DeviceId = deviceId });
            });
        }

        public async Task NextTrackAsync()
        {
            await ExecuteAsync(async spotify =>
            {
                var deviceId = await GetDeviceIdAsync(spotify);
                await spotify.Player.SkipNext(new PlayerSkipNextRequest { DeviceId = deviceId });
            });
        }

        public async Task StopAsync()
        {
            await PauseAsync();
        }

        public async Task ResumeAsync()
        {
            await ExecuteAsync(async spotify =>
            {
                var deviceId = await GetDeviceIdAsync(spotify, activate: true);
                await spotify.Player.ResumePlayback(new PlayerResumePlaybackRequest { DeviceId = deviceId });
            });
        }

        private static async Task<SpotifyTokenResponse?> RefreshTokenAsync(string clientId, string refreshToken)
        {
            using var httpClient = new HttpClient();

            // Prepare the request content
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId
            });

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

        private async Task SaveTokenAsync()
        {
            var tokens = new LastKnownTokens
            {
            ClientId = _config.ClientId,
                ExpiresIn = _expiresIn,
                AccessToken = _accessToken,
                RefreshToken = _refreshToken,
                CreatedAt = _createdAt
            };

            await SaveJsonAsync(_config.TokenFileName, tokens);
        }

        private async Task CheckToken(bool allowInteractive = false)
        {
            // check for token file
            var tokens = JsonFileHandler.ReadObjectFromJsonFile<LastKnownTokens>(_config.TokenFileName);

            // refresh else new connect
            if (tokens != null)
            {
                if (!string.IsNullOrEmpty(tokens.ClientId) && tokens.ClientId != _config.ClientId)
                {
                    throw new InvalidOperationException("Spotify client ID changed. Back up/remove config/tokens.json and complete OAuth again for this client ID.");
                }
                _accessToken = tokens.AccessToken;
                _refreshToken = tokens.RefreshToken;
                _expiresIn = tokens.ExpiresIn;
                _createdAt = tokens.CreatedAt;

                if (tokens.IsExpired)
                {
                    var newToken = await RefreshTokenAsync(_config.ClientId, _refreshToken);
                    if (newToken != null)
                    {
                        _accessToken = newToken.AccessToken;
                        _expiresIn = newToken.ExpiresIn;
                        _createdAt = newToken.CreatedAt;

                        if (!string.IsNullOrWhiteSpace(newToken.RefreshToken))
                        {
                            _refreshToken = newToken.RefreshToken;
                        }

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
                if (!allowInteractive)
                    throw new InvalidOperationException("Spotify login is required. Run MusicDimension with --spotify-login interactively before using Spotify playback.");

                // 1. PKCE Code Verifier und Challenge generieren
                var codeVerifier = GenerateCodeVerifier();
                var codeChallenge = GenerateCodeChallenge(codeVerifier);

                // 2. Authorize URL erstellen
                var loginRequest = new LoginRequest(new Uri(RedirectUri), _config.ClientId, LoginRequest.ResponseType.Code)
                {
                    CodeChallengeMethod = "S256",
                    CodeChallenge = codeChallenge,
                    Scope = [Scopes.UserReadPrivate, Scopes.UserReadPlaybackState, Scopes.UserModifyPlaybackState]
                };

                var authUri = loginRequest.ToUri();

                Console.WriteLine($"open this link to authenticate:\n{authUri}");

                try
                {
                    if (!OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = authUri.ToString(),
                            UseShellExecute = true
                        });
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    Console.WriteLine("Could not open a browser. Open the printed login URL manually.");
                }

                // 4. Redirect URI abwarten (HTTP-Listener starten)
                var authorizationCode = await WaitForAuthorizationCode();

                // 5. Access Token mit PKCE anfordern
                var tokenRequest = new PKCETokenRequest(_config.ClientId, authorizationCode, new Uri(RedirectUri), codeVerifier);
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
