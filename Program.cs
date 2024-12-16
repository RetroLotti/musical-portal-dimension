namespace MusicDimension
{
    using LegoTagEventArgs = LegoDimensions.LegoTagEventArgs;

    internal static class Program
    {
        private static readonly string MusicFileName = Path.Combine($"{Environment.CurrentDirectory}", "config", "music.json");
        private static readonly string ConfigFileName = Path.Combine($"{Environment.CurrentDirectory}", "config", "config.json");
        private static readonly string TokenFileName = Path.Combine($"{Environment.CurrentDirectory}", "config", "tokens.json");

        private static PortalHandler _portalHandler = null!;
        private static SpotifyHandler _spotifyHandler = null!;

        private static Config _config = null!;
        private static MusicConfig _musicConfig = null!;

        private static void Main(string[] args)
        {
            // check for config files
            if (!CheckFiles()) return;

            // read general config from file
#pragma warning disable CS8601 // Possible null reference assignment.
            _config = JsonFileHandler.ReadObjectFromJsonFile<Config>(ConfigFileName);
#pragma warning restore CS8601 // Possible null reference assignment.
            if (_config == null) return;
            _config.TokenFileName = TokenFileName;

            // TODO: remove
            if (!string.IsNullOrWhiteSpace(_config.ClientId)) { Console.WriteLine("ClientId found!"); }
            if (!string.IsNullOrWhiteSpace(_config.ClientSecret)) { Console.WriteLine("ClientSecret found!"); }

            // read music config from file
            _musicConfig = JsonFileHandler.ReadObjectFromJsonFile<MusicConfig>(MusicFileName) ?? new MusicConfig();

            // init handler
            _portalHandler = new PortalHandler();
            _spotifyHandler = new SpotifyHandler(_config);

            // try to connect/identify the portal
            _portalHandler.Connect();

            // check if connection was successful
            if (!_portalHandler.IsConnected)
            {
                Console.WriteLine("Press any key to exit");
                Console.ReadKey();
                return;
            }

            // add event to react on placing an nfc tag on any pad
            _portalHandler.Portal!.LegoTagEvent += PortalOnLegoTagEvent;

            Console.WriteLine("Press any key to exit");
            while (!Console.KeyAvailable)
            {
                Thread.Sleep(1000);
            }

            _portalHandler.Disconnect();
        }

        private static bool CheckFiles()
        {
            var configFile = new FileInfo(ConfigFileName);
            if (!configFile.Exists) Console.WriteLine($"{configFile.FullName} does not exist or permission is denied.");
            var musicFile = new FileInfo(MusicFileName);
            if(!musicFile.Exists) Console.WriteLine($"{musicFile.FullName} does not exist or permission is denied.");

            return configFile.Exists && musicFile.Exists;
        }

        // ReSharper disable once AsyncVoidMethod
        private static async void PortalOnLegoTagEvent(object? sender, LegoTagEventArgs e)
        {
            if (!e.Present)
            {
                await _spotifyHandler.StopAsync(); 
                return;
            }

            Console.WriteLine($"Tag is present: {e.Present} - UID: {BitConverter.ToString(e.CardUid)}");
            Console.WriteLine($"Tag is a {e.LegoTag?.GetType().Name} - Name= {e.LegoTag?.Name}");

            // if tag is read => play associated song / album
            var songs = GetMusic(BitConverter.ToString(e.CardUid));

            Console.WriteLine($"{songs.Count} song(s) found.");

            if (songs.Count > 0)
            {
                await _spotifyHandler.PlayAsync("spotify:track:63y2aL2dT6zzw8FOSLaNrp");
            }
        }

        private static List<Song> GetMusic(string tagUid)
        {
            foreach (var tag in _musicConfig.RegisteredTags.Where(tag => tag.Uid.Equals(tagUid)))
            {
                return tag.Songs;
            }

            return [];
        }
    }
}
