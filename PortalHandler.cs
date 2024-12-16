namespace MusicDimension
{
    using LegoDimensions;
    using LegoDimensions.Portal;

    public class PortalHandler
    {
        public LegoPortal? Portal { get; private set; }

        public bool IsConnected { get; private set; }

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

            Portal.SetColor(Pad.Left, Color.Green);
            Thread.Sleep(500);
            Portal.SetColor(Pad.Left, Color.Black);
            Thread.Sleep(500);
            Portal.SetColor(Pad.Center, Color.Green);
            Thread.Sleep(500);
            Portal.SetColor(Pad.Center, Color.Black);
            Thread.Sleep(500);
            Portal.SetColor(Pad.Right, Color.Green);
            Thread.Sleep(500);
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
