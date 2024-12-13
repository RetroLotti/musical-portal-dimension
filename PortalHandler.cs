using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HidSharp;
using LegoDimensions;
using LegoDimensions.Portal;

namespace MusicDimension
{
    public class PortalHandler
    {
        private const int VendorId = 3695;
        private const int ProductId = 577;

        public LegoPortal? Portal { get; private set; }

        public bool IsConnected { get; private set; }

        public void Connect()
        {
            var devices = DeviceList.Local.GetHidDevices();
            var legoDimensionPadDevice = devices.FirstOrDefault(device => device.VendorID.Equals(VendorId) && device.ProductID.Equals(ProductId));

            if (legoDimensionPadDevice == null) { return; }

            Portal = LegoPortal.GetFirstPortal();

            Portal.SetColor(Pad.Center, Color.Black);
            Portal.SetColor(Pad.Left, Color.Black);
            Portal.SetColor(Pad.Right, Color.Black);

            IsConnected = true;
        }

        public void Disconnect()
        {
            // TODO: is that the color so switch leds off?
            Portal?.SetColorAll(Color.Transparent, Color.Transparent, Color.Transparent);
            IsConnected = false;
        }

        //private static void PortalOnLegoTagEvent(object? sender, LegoTagEventArgs e)
        //{
        //    Console.WriteLine($"Tag is present: {e.Present} - UID: {BitConverter.ToString(e.CardUid)}");
        //    if (!e.Present) return;
        //    Console.WriteLine($"Tag is a {e.LegoTag?.GetType().Name} - Name= {e.LegoTag?.Name}");
        //    //Dump((LegoPortal)sender!);
        //}
    }
}
