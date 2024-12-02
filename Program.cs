using HidSharp;
using LegoDimensions;
using LegoDimensions.Portal;

namespace MusicDimension
{
    internal class Program
    {
        static void Main(string[] args)
        {
            const int vendorId = 3695;
            const int productId = 577;

            var devices = DeviceList.Local.GetHidDevices();
            var legoDimensionPad = devices.FirstOrDefault(device => device.VendorID.Equals(vendorId) && device.ProductID.Equals(productId));

            using var portal = LegoPortal.GetFirstPortal();
            portal.LegoTagEvent += PortalOnLegoTagEvent;

            //Console.WriteLine("Pad left blue");
            //portal.SetColor(Pad.Left, Color.Blue);
            //Thread.Sleep(1000);
            //Console.WriteLine("Pad center white");
            //portal.SetColor(Pad.Center, Color.White);
            //Thread.Sleep(1000);
            //Console.WriteLine("Pad right purple");
            //portal.SetColor(Pad.Right, Color.Red);
            //Thread.Sleep(1000);
            //Console.WriteLine("All yellow");
            //portal.SetColor(Pad.All, Color.Yellow);

            Console.WriteLine("Press any key to exit");
            while (!Console.KeyAvailable)
            {
                Thread.Sleep(1000);
            }

            Console.WriteLine("End of test");
        }

        private static void PortalOnLegoTagEvent(object? sender, LegoTagEventArgs e)
        {
            Console.WriteLine($"Tag is present: {e.Present} - UID: {BitConverter.ToString(e.CardUid)}");
            if (e.Present)
            {
                Console.WriteLine($"Tag is a {e.LegoTag?.GetType().Name} - Name= {e.LegoTag?.Name}");
                Dump((LegoPortal)sender!);
            }
        }

        private static void Dump(LegoPortal? portal)
        {
            Console.WriteLine("Place at least 1 tag on the portal, this will dump the full tag.");
            while (!portal.PresentTags.Any())
            {
                Thread.Sleep(1000);
            }

            var idx = portal.PresentTags.First().Index;
            for (byte i = 0; i < 0x2c; i += 4)
            {
                var tag = portal.ReadTag(idx, i);
                if (tag.Length == 0)
                {
                    Console.WriteLine($"Error reading card page 0x{i:X2}");
                }
                else
                {
                    Console.WriteLine($"Tag: {BitConverter.ToString(tag)}");
                }
            }
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
