namespace MusicDimension.misc
{
    public sealed class ReaderCaptureState
    {
        private readonly object _sync = new();
        private bool _isCapturing;
        private string? _capturedUid;
        private string? _lastUid;

        public void BeginCapture()
        {
            lock (_sync)
            {
                _capturedUid = null;
                _isCapturing = true;
            }
        }

        public bool TagPresented(string uid)
        {
            lock (_sync)
            {
                _lastUid = uid;
                if (!_isCapturing) return false;

                _capturedUid = uid;
                _isCapturing = false;
                return true;
            }
        }

        public ReaderCaptureSnapshot GetSnapshot(bool readerConnected)
        {
            lock (_sync)
            {
                return new ReaderCaptureSnapshot(readerConnected, _isCapturing, _capturedUid, _lastUid);
            }
        }
    }

    public sealed record ReaderCaptureSnapshot(bool ReaderConnected, bool IsCapturing, string? CapturedUid, string? LastUid);
}