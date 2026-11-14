namespace MusicDimension.misc
{
    using MusicDimension.models;
    using Newtonsoft.Json;
    using SpotifyAPI.Web;
    using System.Diagnostics;
    using System.Net;
    using System.Security.Cryptography;
    using System.Text;

    public class SpotifyHandler(Config config, SpotifyClientConfig? clientConfiguration = null, HttpClient? tokenHttpClient = null)
    {
        private Config _config = config;
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        public Config Configuration => _config;
        private string SoloistKeyFile => Path.Combine(Path.GetDirectoryName(_config.TokenFileName)!, "soloist-key.json");
        public bool HasSoloistApiKey
        {
            get
            {
                try { return File.Exists(SoloistKeyFile) && !string.IsNullOrEmpty(JsonConvert.DeserializeObject<string>(File.ReadAllText(SoloistKeyFile))); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return false; }
            }
        }

        private const string SpotifyTokenUrl = "https://accounts.spotify.com/api/token";
        private const string RedirectUri = "http://127.0.0.1:8888/callback";
        private static readonly HttpClient DefaultTokenHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

        private string _accessToken = string.Empty;
        private string _refreshToken = string.Empty;
        private int _expiresIn;
        private DateTime _createdAt;
        private PendingAuthorization? _pendingAuthorization;
        private string? _authorizationError;

        private sealed record PendingAuthorization(string SessionId, string State, string Verifier, string ClientId, DateTimeOffset ExpiresAt);

        /// <summary>A one-time PKCE authorization link without tokens or the code verifier.</summary>
        public sealed record SpotifyAuthorizationStart(string AuthorizationUrl, string RedirectUri, DateTimeOffset ExpiresAt);

        /// <summary>Saved authorization and pending login status without credentials.</summary>
        public sealed record SpotifyAuthorizationStatus(bool Authorized, bool Pending, DateTimeOffset? AccessTokenExpiresAt, string? Message);

        public static async Task<SpotifyHandler> CreateAsync(Config config)
        {
            var instance = new SpotifyHandler(config);
            await instance.AuthorizeInteractivelyAsync();
            return instance;
        }

        public async Task InitializeAsync()
        {
            if (!_config.Enable) return;
            if (!File.Exists(_config.TokenFileName))
            {
                Console.WriteLine("Spotify needs authorization. Use Connect Spotify in web settings, or run with --spotify-login. Local playback remains available.");
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
                if (configuration.Receiver.SoloistApiKey != null)
                {
                    await SaveJsonAsync(SoloistKeyFile, configuration.Receiver.SoloistApiKey);
                    configuration.Receiver.SoloistApiKey = null;
                }
                if (_config.ClientId != configuration.ClientId)
                {
                    _pendingAuthorization = null;
                    _authorizationError = null;
                }
                _config = configuration;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task UpdateMediaMaxFileSizeMbAsync(int megabytes, string fileName)
        {
            if (megabytes is < 1 or > 4096)
                throw new ArgumentOutOfRangeException(nameof(megabytes), "File size must be between 1 and 4096 MB.");

            await _operationLock.WaitAsync();
            try
            {
                var previous = _config.MediaMaxFileSizeMb;
                _config.MediaMaxFileSizeMb = megabytes;
                try
                {
                    await SaveJsonAsync(fileName, _config);
                }
                catch
                {
                    _config.MediaMaxFileSizeMb = previous;
                    throw;
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task UpdateInitialVolumeAsync(int? volume, string fileName)
        {
            if (volume is < 0 or > 100)
                throw new ArgumentOutOfRangeException(nameof(volume), "Initial volume must be between 0 and 100.");

            await _operationLock.WaitAsync();
            try
            {
                var previous = _config.InitialVolume;
                _config.InitialVolume = volume;
                try
                {
                    await SaveJsonAsync(fileName, _config);
                }
                catch
                {
                    _config.InitialVolume = previous;
                    throw;
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task SetVolumeAsync(int volume)
        {
            if (volume is < 0 or > 100)
                throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 100.");

            await ExecuteAsync(async spotify =>
            {
                var deviceId = await GetDeviceIdAsync(spotify);
                await spotify.Player.SetVolume(new PlayerVolumeRequest(volume) { DeviceId = deviceId });
            });
        }

        public async Task<SpotifyAuthorizationStart> BeginAuthorizationAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            await _operationLock.WaitAsync(cancellationToken);
            try
            {
                if (string.IsNullOrWhiteSpace(_config.ClientId))
                    throw new InvalidOperationException("Save a Spotify client ID before connecting.");
                var verifier = GenerateCodeVerifier();
                var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                var pending = new PendingAuthorization(sessionId, state, verifier, _config.ClientId, DateTimeOffset.UtcNow.AddMinutes(10));
                var login = new LoginRequest(new Uri(RedirectUri), pending.ClientId, LoginRequest.ResponseType.Code)
                {
                    State = state,
                    CodeChallengeMethod = "S256",
                    CodeChallenge = GenerateCodeChallenge(verifier),
                    Scope = [Scopes.UserReadPrivate, Scopes.UserReadPlaybackState, Scopes.UserModifyPlaybackState]
                };
                _pendingAuthorization = pending;
                return new(login.ToUri().ToString(), RedirectUri, pending.ExpiresAt);
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task CompleteAuthorizationAsync(string sessionId, string? callbackUrl, CancellationToken cancellationToken = default)
        {
            await _operationLock.WaitAsync(cancellationToken);
            try
            {
                var pending = _pendingAuthorization;
                if (pending == null || pending.ExpiresAt <= DateTimeOffset.UtcNow || pending.SessionId != sessionId || pending.ClientId != _config.ClientId)
                    throw new InvalidOperationException("Spotify login expired or belongs to another browser. Start Connect Spotify again.");
                if (string.IsNullOrWhiteSpace(callbackUrl) || callbackUrl.Length > 16384
                    || !Uri.TryCreate(callbackUrl, UriKind.Absolute, out var callback)
                    || callback.GetLeftPart(UriPartial.Path) != RedirectUri || !string.IsNullOrEmpty(callback.Fragment) || !string.IsNullOrEmpty(callback.UserInfo))
                    throw new ArgumentException("Paste the full returned http://127.0.0.1:8888/callback URL from the browser address bar.");
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(callback.Query);
                if (!query.TryGetValue("state", out var state) || state.Count != 1 || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(state.ToString()), Encoding.UTF8.GetBytes(pending.State)))
                    throw new ArgumentException("The Spotify callback does not match this login. Use the URL returned by the current authorization link.");
                if (query.ContainsKey("error"))
                {
                    _pendingAuthorization = null;
                    throw new InvalidOperationException("Spotify authorization was declined. Start Connect Spotify again when ready.");
                }
                if (!query.TryGetValue("code", out var code) || code.Count != 1 || string.IsNullOrWhiteSpace(code.ToString()))
                    throw new ArgumentException("The callback URL does not contain a Spotify authorization code.");
                _pendingAuthorization = null;
                var tokens = await RequestTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code.ToString(),
                    ["redirect_uri"] = RedirectUri,
                    ["client_id"] = pending.ClientId,
                    ["code_verifier"] = pending.Verifier
                }, cancellationToken);
                if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
                    throw new InvalidOperationException("Spotify did not return a refresh token. Start Connect Spotify again and approve access.");
                _accessToken = tokens.AccessToken;
                _refreshToken = tokens.RefreshToken;
                _expiresIn = tokens.ExpiresIn;
                _createdAt = DateTime.UtcNow;
                await SaveTokenAsync();
                _authorizationError = null;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task<SpotifyAuthorizationStatus> GetAuthorizationStatusAsync(CancellationToken cancellationToken = default)
        {
            await _operationLock.WaitAsync(cancellationToken);
            try
            {
                var tokens = JsonFileHandler.ReadObjectFromJsonFile<LastKnownTokens>(_config.TokenFileName);
                var authorized = tokens != null && !string.IsNullOrWhiteSpace(tokens.RefreshToken)
                    && (string.IsNullOrEmpty(tokens.ClientId) || tokens.ClientId == _config.ClientId) && _authorizationError == null;
                DateTimeOffset? expiresAt = authorized && tokens != null
                    ? new DateTimeOffset(DateTime.SpecifyKind(tokens.CreatedAt, DateTimeKind.Utc)).AddSeconds(tokens.ExpiresIn) : null;
                return new(authorized, _pendingAuthorization?.ExpiresAt > DateTimeOffset.UtcNow, expiresAt,
                    _authorizationError ?? (authorized ? null : "Spotify authorization is required. Connect Spotify to continue."));
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task RefreshAuthorizationAsync(CancellationToken cancellationToken = default)
        {
            await _operationLock.WaitAsync(cancellationToken);
            try
            {
                await CheckToken(forceRefresh: true, cancellationToken: cancellationToken);
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
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(temporaryFile, options))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    await using var writer = new StreamWriter(stream);
                    await writer.WriteAsync(JsonConvert.SerializeObject(value, Formatting.Indented));
                }
                File.Move(temporaryFile, fileName, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
            }
        }

        private async Task ExecuteAsync(Func<SpotifyClient, Task> action, CancellationToken cancellationToken = default, bool requireEnabled = true)
        {
            await _operationLock.WaitAsync(cancellationToken);
            try
            {
                if (requireEnabled && !_config.Enable) throw new InvalidOperationException("Spotify is disabled.");
                await CheckToken(cancellationToken: cancellationToken);
                try
                {
                    await action(new SpotifyClient((clientConfiguration ?? SpotifyClientConfig.CreateDefault()).WithToken(_accessToken)));
                }
                catch (APIException exception) when (exception.Response?.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await CheckToken(forceRefresh: true, cancellationToken: cancellationToken);
                    await action(new SpotifyClient((clientConfiguration ?? SpotifyClientConfig.CreateDefault()).WithToken(_accessToken)));
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task<IReadOnlyList<SpotifyDeviceResponse>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            var results = new List<SpotifyDeviceResponse>();
            await ExecuteAsync(async spotify =>
            {
                var response = await spotify.Player.GetAvailableDevices(cancellationToken);
                foreach (var device in response.Devices)
                {
                    results.Add(new(device.Name, device.Type, device.IsActive, device.IsRestricted));
                }
            }, cancellationToken, requireEnabled: false);
            return results;
        }

        /// <summary>A Spotify Connect device with its exact name and playback-control availability.</summary>
        public sealed record SpotifyDeviceResponse(string Name, string Type, bool IsActive, bool IsRestricted);

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

        public Task PlayAsync(string spotifyType, string spotifyId)
        {
            if (!Enum.TryParse<SpotifyTypes>(spotifyType, ignoreCase: true, out var type) || !Enum.IsDefined(type))
                throw new ArgumentException("Choose a Spotify track, album, or playlist.", nameof(spotifyType));
            return PlayAsync([new Song { Type = SourceTypes.Spotify, SpotifyType = type, SpotifyId = spotifyId }]);
        }

        public async Task PlayAsync(IReadOnlyList<Song> songs)
        {
            if (songs.Count is < 1 or > 100 || songs.Any(song => song == null || song.Type != SourceTypes.Spotify
                || song.SpotifyType == null || !Enum.IsDefined(song.SpotifyType.Value) || string.IsNullOrWhiteSpace(song.SpotifyId)))
                throw new ArgumentException("Choose between 1 and 100 valid Spotify tracks, or one album or playlist.", nameof(songs));
            if (songs.Count > 1 && songs.Any(song => song.SpotifyType != SpotifyTypes.Track))
                throw new ArgumentException("Spotify albums and playlists must be played on their own.", nameof(songs));

            await ExecuteAsync(async spotify =>
            {
                var deviceId = await GetDeviceIdAsync(spotify, activate: true);
                var playbackRequest = new PlayerResumePlaybackRequest { DeviceId = deviceId };
                var first = songs[0];
                if (first.SpotifyType is SpotifyTypes.Album or SpotifyTypes.Playlist)
                {
                    playbackRequest.ContextUri = $"spotify:{first.SpotifyType.ToString()!.ToLowerInvariant()}:{first.SpotifyId!.Trim()}";
                }
                else
                {
                    playbackRequest.Uris = songs.Select(song => $"spotify:track:{song.SpotifyId!.Trim()}").ToList();
                }

                await spotify.Player.ResumePlayback(playbackRequest);
            });
        }

        public async Task<SpotifyPlaybackState?> CapturePlaybackStateAsync(IReadOnlyList<Song> songs)
        {
            if (!IsValidSpotifyQueue(songs)) return null;

            SpotifyPlaybackState? state = null;
            await ExecuteAsync(async spotify =>
            {
                var deviceId = await GetDeviceIdAsync(spotify);
                var playback = await spotify.Player.GetCurrentPlayback();
                if (playback?.Device?.Id != deviceId || playback.Item is not FullTrack track || string.IsNullOrWhiteSpace(track.Uri)) return;

                var first = songs[0];
                if (first.SpotifyType == SpotifyTypes.Track)
                {
                    var index = songs.ToList().FindIndex(song => string.Equals(TrackUri(song), track.Uri, StringComparison.Ordinal));
                    if (index < 0) return;
                    state = new SpotifyPlaybackState(QueueSignature(songs), null, track.Uri, index, Math.Max(0, playback.ProgressMs));
                    return;
                }

                var contextUri = ContextUri(first);
                if (!string.Equals(playback.Context?.Uri, contextUri, StringComparison.Ordinal)) return;
                state = new SpotifyPlaybackState(QueueSignature(songs), contextUri, track.Uri, -1, Math.Max(0, playback.ProgressMs));
            });
            return state;
        }

        public async Task<bool> TryResumeAsync(IReadOnlyList<Song> songs, SpotifyPlaybackState state)
        {
            if (!IsValidSpotifyQueue(songs)
                || !string.Equals(state.QueueSignature, QueueSignature(songs), StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(state.TrackUri)
                || state.PositionMs < 0)
                return false;

            var first = songs[0];
            var playbackRequest = new PlayerResumePlaybackRequest { PositionMs = state.PositionMs };
            if (first.SpotifyType is SpotifyTypes.Album or SpotifyTypes.Playlist)
            {
                var contextUri = ContextUri(first);
                if (!string.Equals(state.ContextUri, contextUri, StringComparison.Ordinal)
                    || !state.TrackUri.StartsWith("spotify:track:", StringComparison.Ordinal))
                    return false;
                playbackRequest.ContextUri = contextUri;
                playbackRequest.OffsetParam = new PlayerResumePlaybackRequest.Offset { Uri = state.TrackUri };
            }
            else
            {
                if (state.ContextUri != null || state.QueueIndex < 0 || state.QueueIndex >= songs.Count
                    || !string.Equals(state.TrackUri, TrackUri(songs[state.QueueIndex]), StringComparison.Ordinal))
                    return false;
                playbackRequest.Uris = songs.Select(TrackUri).ToList();
                playbackRequest.OffsetParam = new PlayerResumePlaybackRequest.Offset { Position = state.QueueIndex };
            }

            await ExecuteAsync(async spotify =>
            {
                playbackRequest.DeviceId = await GetDeviceIdAsync(spotify, activate: true);
                await spotify.Player.ResumePlayback(playbackRequest);
            });
            return true;
        }

        private static bool IsValidSpotifyQueue(IReadOnlyList<Song> songs) =>
            songs.Count is > 0 and <= 100
            && songs.All(song => song.Type == SourceTypes.Spotify && song.SpotifyType != null && !string.IsNullOrWhiteSpace(song.SpotifyId))
            && (songs.Count == 1 || songs.All(song => song.SpotifyType == SpotifyTypes.Track));

        private static string TrackUri(Song song) => $"spotify:track:{song.SpotifyId!.Trim()}";

        private static string ContextUri(Song song) =>
            $"spotify:{song.SpotifyType!.Value.ToString().ToLowerInvariant()}:{song.SpotifyId!.Trim()}";

        private static string QueueSignature(IReadOnlyList<Song> songs) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", songs.Select(song => $"{song.SpotifyType}:{song.SpotifyId?.Trim()}")))));

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

        public async Task ChangeVolumeAsync(int delta)
        {
            await ExecuteAsync(async spotify =>
            {
                var deviceId = await GetDeviceIdAsync(spotify);
                var playback = await spotify.Player.GetCurrentPlayback();
                if (playback?.Device?.Id != deviceId || playback.Device.VolumePercent == null)
                    throw new InvalidOperationException("The configured Spotify device has no controllable playback volume.");
                var volume = (int)Math.Clamp((long)playback.Device.VolumePercent.Value + delta, 0, 100);
                await spotify.Player.SetVolume(new PlayerVolumeRequest(volume) { DeviceId = deviceId });
            });
        }

        public async Task ToggleShuffleAsync()
        {
            await ExecuteAsync(async spotify =>
            {
                var deviceId = await GetDeviceIdAsync(spotify);
                var playback = await spotify.Player.GetCurrentPlayback();
                if (playback?.Device?.Id != deviceId)
                    throw new InvalidOperationException("The configured Spotify device has no playback to shuffle.");
                await spotify.Player.SetShuffle(new PlayerShuffleRequest(!playback.ShuffleState) { DeviceId = deviceId });
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

        private async Task<SpotifyTokenResponse> RequestTokenAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken = default)
        {
            using var content = new FormUrlEncodedContent(parameters);
            using var response = await (tokenHttpClient ?? DefaultTokenHttpClient).PostAsync(SpotifyTokenUrl, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("Spotify authorization is invalid or has been revoked. Connect Spotify again in Spotify settings and check the client ID.");
                throw new HttpRequestException("Spotify's token service is unavailable. Try again later; saved credentials have not been removed.", null, response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var tokenResponse = JsonConvert.DeserializeObject<SpotifyTokenResponse>(json);
            if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken) || tokenResponse.ExpiresIn <= 0)
                throw new HttpRequestException("Spotify returned an incomplete token response. Try again later.");
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

        private async Task CheckToken(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            // check for token file
            var tokens = JsonFileHandler.ReadObjectFromJsonFile<LastKnownTokens>(_config.TokenFileName);

            // refresh else new connect
            if (tokens != null)
            {
                if (!string.IsNullOrEmpty(tokens.ClientId) && tokens.ClientId != _config.ClientId)
                {
                    throw new InvalidOperationException("Spotify client ID changed. Connect Spotify again in Spotify settings.");
                }
                _accessToken = tokens.AccessToken;
                _refreshToken = tokens.RefreshToken;
                _expiresIn = tokens.ExpiresIn;
                _createdAt = tokens.CreatedAt;

                if (string.IsNullOrWhiteSpace(_refreshToken))
                    throw new InvalidOperationException("Spotify has no saved refresh token. Connect Spotify in Spotify settings.");

                if (forceRefresh || string.IsNullOrWhiteSpace(_accessToken) || tokens.ExpiresIn <= 60
                    || tokens.CreatedAt <= DateTime.UtcNow.AddSeconds(60d - tokens.ExpiresIn))
                {
                    SpotifyTokenResponse newToken;
                    try
                    {
                        newToken = await RequestTokenAsync(new Dictionary<string, string>
                        {
                            ["grant_type"] = "refresh_token",
                            ["refresh_token"] = _refreshToken,
                            ["client_id"] = _config.ClientId
                        }, cancellationToken);
                    }
                    catch (InvalidOperationException exception)
                    {
                        _authorizationError = exception.Message;
                        throw;
                    }
                    _accessToken = newToken.AccessToken;
                    _expiresIn = newToken.ExpiresIn;
                    _createdAt = DateTime.UtcNow;
                    if (!string.IsNullOrWhiteSpace(newToken.RefreshToken))
                    {
                        _refreshToken = newToken.RefreshToken;
                    }
                    await SaveTokenAsync();
                    _authorizationError = null;
                }
            }
            else
            {
                throw new InvalidOperationException("Spotify login is required. Connect Spotify in Spotify settings, or run with --spotify-login.");
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

        private async Task AuthorizeInteractivelyAsync()
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:8888/");
            listener.Start();
            var sessionId = Guid.NewGuid().ToString("N");
            var authorization = await BeginAuthorizationAsync(sessionId);
            Console.WriteLine($"Open this link to authenticate:\n{authorization.AuthorizationUrl}");
            try
            {
                if (!OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                    Process.Start(new ProcessStartInfo { FileName = authorization.AuthorizationUrl, UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.WriteLine("Open the printed login URL manually.");
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                if (context.Request.Url?.AbsolutePath != "/callback")
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }
                try
                {
                    await CompleteAuthorizationAsync(sessionId, context.Request.Url.AbsoluteUri, timeout.Token);
                    var buffer = Encoding.UTF8.GetBytes("Spotify connected. You can close this window.");
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    context.Response.Headers["Cache-Control"] = "no-store";
                    await context.Response.OutputStream.WriteAsync(buffer, timeout.Token);
                    return;
                }
                finally
                {
                    context.Response.Close();
                }
            }
        }
    }
}
