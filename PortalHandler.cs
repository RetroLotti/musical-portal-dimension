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
        public LegoPortal? Portal { get; private set; }

        public bool IsConnected { get; private set; }

        public void Connect()
        {
            Portal = LegoPortal.GetFirstPortal();

            if (Portal == null)
            {
                IsConnected = false;
                return;
            }

            Portal.SetColor(Pad.Center, Color.Green);
            Portal.SetColor(Pad.Left, Color.Black);
            Portal.SetColor(Pad.Right, Color.Black);

            IsConnected = true;
        }

        public void Disconnect()
        {
            // TODO: is that the color so switch leds off?
            Portal?.SetColorAll(Color.Black, Color.Black, Color.Black);
            IsConnected = false;
        }
    }
}
