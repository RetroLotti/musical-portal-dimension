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
        private static readonly PortalHandler PortalHandler = new();
        private static readonly SpotifyHandler SpotifyHandler = new("c97ae89ab8cd4704bc2f83d3cd3efb6d");

        private static void Main(string[] args)
        {
            // TODO: read music config into dictionary
            // 

            // try to connect/identify the portal
            PortalHandler.Connect();

            if (!PortalHandler.IsConnected)
            {
                Console.WriteLine("No usb portal found!");
                Console.WriteLine("Press any key to exit");
                Console.ReadKey();
                return;
            }

            // add event to react on placing an nfc tag on any pad
            PortalHandler.Portal.LegoTagEvent += PortalOnLegoTagEvent;

            Console.WriteLine("Press any key to exit");
            while (!Console.KeyAvailable)
            {
                Thread.Sleep(1000);
            }

            PortalHandler.Disconnect();
        }

        // ReSharper disable once AsyncVoidMethod
        private static async void PortalOnLegoTagEvent(object? sender, LegoTagEventArgs e)
        {
            if (!e.Present)
            {
                await SpotifyHandler.StopAsync(); 
                return;
            }

            Console.WriteLine($"Tag is present: {e.Present} - UID: {BitConverter.ToString(e.CardUid)}");
            Console.WriteLine($"Tag is a {e.LegoTag?.GetType().Name} - Name= {e.LegoTag?.Name}");

            // TODO: if tag is read => play associated song / album
            // 

            // For testing just play a song on smartphone
            await SpotifyHandler.PlayAsync("spotify:track:63y2aL2dT6zzw8FOSLaNrp");
        }
    }
}
