using System.Reflection.Metadata.Ecma335;
using HidSharp;
using LegoDimensions;
using LegoDimensions.Portal;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;

namespace MusicDimension
{
    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            /*
            pkce token holen + merken

            */


            //await PlaySongChatGptAsync();

            //const int vendorId = 3695;
            //const int productId = 577;

            //var devices = DeviceList.Local.GetHidDevices();
            //var legoDimensionPad = devices.FirstOrDefault(device => device.VendorID.Equals(vendorId) && device.ProductID.Equals(productId));

            //using var portal = LegoPortal.GetFirstPortal();
            //portal.LegoTagEvent += PortalOnLegoTagEvent;

            ////Console.WriteLine("Pad left blue");
            ////portal.SetColor(Pad.Left, Color.Blue);
            ////Thread.Sleep(1000);
            ////Console.WriteLine("Pad center white");
            ////portal.SetColor(Pad.Center, Color.White);
            ////Thread.Sleep(1000);
            ////Console.WriteLine("Pad right purple");
            ////portal.SetColor(Pad.Right, Color.Red);
            ////Thread.Sleep(1000);
            ////Console.WriteLine("All yellow");
            ////portal.SetColor(Pad.All, Color.Yellow);

            //Console.WriteLine("Press any key to exit");
            //while (!Console.KeyAvailable)
            //{
            //    Thread.Sleep(1000);
            //}
            var pkce = new PkceTest();
            await pkce.Test();


            Console.WriteLine("End of test");
        }

        private static void PortalOnLegoTagEvent(object? sender, LegoTagEventArgs e)
        {
            Console.WriteLine($"Tag is present: {e.Present} - UID: {BitConverter.ToString(e.CardUid)}");
            if (!e.Present) return;

            Console.WriteLine($"Tag is a {e.LegoTag?.GetType().Name} - Name= {e.LegoTag?.Name}");
            Dump((LegoPortal)sender!);
        }

        private static void Dump(LegoPortal? portal)
        {
            if (portal == null) return;

            Console.WriteLine("Place at least 1 tag on the portal, this will dump the full tag.");
            while (!portal.PresentTags.Any())
            {
                Thread.Sleep(1000);
            }

            var idx = portal.PresentTags.First().Index;
            for (byte i = 0; i < 0x2c; i += 4)
            {
                var tag = portal.ReadTag(idx, i);
                Console.WriteLine(tag.Length == 0
                    ? $"Error reading card page 0x{i:X2}"
                    : $"Tag: {BitConverter.ToString(tag)}");
            }
        }

        //private static async Task PlaySongChatGptAsync()
        //{
            
        //    var request = new ClientCredentialsRequest("c97ae89ab8cd4704bc2f83d3cd3efb6d", "7124aaee211a435e820aee7516e73936");
        //    var requestPKCE = new PKCETokenRequest("c97ae89ab8cd4704bc2f83d3cd3efb6d", )
        //    var client = new OAuthClient();
        //    var response = await client.RequestToken(request);

        //    if (!string.IsNullOrEmpty(response.AccessToken))
        //    {
        //        Console.WriteLine($"Access Token: {response.AccessToken}");
        //        Console.WriteLine($"Token Type: {response.TokenType}");
        //        Console.WriteLine($"Expires In: {response.ExpiresIn} seconds");
        //    }

        //    // OAuth-Token einfügen (vorher über das Spotify Developer Portal generieren)
        //    var accessToken = response.AccessToken;

        //    // Spotify-Client einrichten
        //    var spotify = new SpotifyClient(accessToken);

        //    // Track-ID des Liedes einfügen (kann über die Spotify-App oder die API gefunden werden)
        //    string trackUri = "spotify:track:3n3Ppam7vgaVa1iaRUc9Lp"; // Beispiel: Song-ID von "Smells Like Teen Spirit"

        //    // Wiedergabe auf einem spezifischen Gerät starten (z.B. Smartphone)
        //    var devices = await spotify.Player.GetAvailableDevices();
        //    var deviceId = devices.Devices.FirstOrDefault(d => d.Type == "Smartphone")?.Id;

        //    if (deviceId != null)
        //    {
        //        // Song auf dem Gerät abspielen
        //        var request1 = new PlayerResumePlaybackRequest
        //        {
        //            Uris = new List<string> { trackUri },
        //            DeviceId = deviceId
        //        };
        //        await spotify.Player.ResumePlayback(request1);

        //        Console.WriteLine("Song wird auf dem Smartphone abgespielt.");
        //    }
        //    else
        //    {
        //        Console.WriteLine("Kein Smartphone-Gerät gefunden.");
        //    }
        //}

        private static async Task PlaySong()
        {
            var spotify = new SpotifyClient("YourAccessToken");

            var track = await spotify.Tracks.Get("1s6ux0lNiTziSrd7iUAADH");
            Console.WriteLine(track.Name);
        }

        private static async void ChipReader(HidDevice device)
        {
            try
            {
                using (var stream = device.Open())
                {
                    Console.WriteLine("Gerät verbunden. Warte auf RFID-Daten...");

                    byte[] buffer = new byte[64];
                    while (true)
                    {
                        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                        if (bytesRead > 0)
                        {
                            Console.WriteLine("RFID-Daten empfangen:");
                            Console.WriteLine(BitConverter.ToString(buffer, 0, bytesRead));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler: {ex.Message}");
            }
        }
    }
}
