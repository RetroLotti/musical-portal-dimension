namespace MusicDimension.misc
{
    using LegoDimensions;
    using LegoDimensions.Portal;

    public class PortalHandler
    {
        public LegoPortal? Portal { get; private set; }

        public bool IsConnected { get; private set; }

        public bool IsTransferring { get; set; }

        public void Connect()
        {
            try
            {
                Portal = LegoPortal.GetFirstPortal();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            if (Portal == null)
            {
                IsConnected = false;
                return;
            }

            Portal.SetColor(Pad.Center, Color.Red);
            Thread.Sleep(100);
            Portal.SetColor(Pad.Center, Color.OrangeRed);
            Thread.Sleep(100);
            Portal.SetColor(Pad.Center, Color.Orange);
            Thread.Sleep(100);
            Portal.SetColor(Pad.Center, Color.Yellow);
            Thread.Sleep(100);
            Portal.SetColor(Pad.Center, Color.YellowGreen);
            Thread.Sleep(100);
            Portal.SetColor(Pad.Center, Color.Green);
            Thread.Sleep(100);
            Portal.SetColor(Pad.Center, Color.Black);

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
