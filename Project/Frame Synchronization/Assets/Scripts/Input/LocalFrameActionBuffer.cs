namespace FrameSyncDemo
{
    public sealed class LocalFrameActionBuffer
    {
        private int _lastCapturedRenderFrame = int.MinValue;
        private bool _pickupPressed;
        private bool _shootReleased;
        private bool _endMatchPressed;

        public void Capture(
            int renderFrame,
            bool pickupPressed,
            bool shootReleased)
        {
            Capture(renderFrame, pickupPressed, shootReleased, false);
        }

        public void Capture(
            int renderFrame,
            bool pickupPressed,
            bool shootReleased,
            bool endMatchPressed)
        {
            if (renderFrame == _lastCapturedRenderFrame)
                return;

            _lastCapturedRenderFrame = renderFrame;
            _pickupPressed |= pickupPressed;
            _shootReleased |= shootReleased;
            _endMatchPressed |= endMatchPressed;
        }

        public FrameInput Consume(FrameInput sustainedInput)
        {
            if (_pickupPressed)
                sustainedInput.pickupPressed = true;
            if (_shootReleased)
                sustainedInput.shootReleased = true;
            if (_endMatchPressed)
                sustainedInput.endMatchPressed = true;

            _pickupPressed = false;
            _shootReleased = false;
            _endMatchPressed = false;
            return sustainedInput;
        }

        public void Clear()
        {
            _pickupPressed = false;
            _shootReleased = false;
            _endMatchPressed = false;
        }
    }
}
