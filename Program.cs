using System;
using System.Reflection.Metadata.Ecma335;
using HidSharp;
using LegoDimensions;
using LegoDimensions.Portal;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using LegoTagEventArgs = LegoDimensions.LegoTagEventArgs;

namespace MusicDimension
{
    internal static class Program
    {
        private const string MusicFileName = @".\config\music.json";
        private const string ConfigFileName = @".\config\config.json";
        private const string TokenFileName = @".\config\tokens.json";

        private static PortalHandler _portalHandler = null!;
        private static SpotifyHandler _spotifyHandler = null!;

        private static Config _config = null!;
        private static MusicConfig _musicConfig = null!;

        private static void Main(string[] args)
        {
            // read general config from file
            _config = JsonFileHandler.ReadObjectFromJsonFile<Config>(ConfigFileName) ?? new Config
            {
                ClientId = string.Empty,
                ClientSecret = string.Empty,
                SpotifyDeviceName = string.Empty,
                TokenFileName = string.Empty
            };
            _config.TokenFileName = TokenFileName;

            // read music config from file
            _musicConfig = JsonFileHandler.ReadObjectFromJsonFile<MusicConfig>(MusicFileName) ?? new MusicConfig();

            // init handler
            _portalHandler = new PortalHandler();
            _spotifyHandler = new SpotifyHandler(_config);

            // try to connect/identify the portal
            _portalHandler.Connect();

            if (!_portalHandler.IsConnected)
            {
                Console.WriteLine("No portal found!");
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
