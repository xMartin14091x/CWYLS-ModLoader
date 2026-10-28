namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Collapses the duplicate click lines the input postfix produces.
    /// <para>
    /// <c>InputController.GetClickDown</c> is polled by more than one caller per frame, and every
    /// caller that sees it return true reaches the postfix, so one physical click wrote two
    /// <c>Input.click</c> lines. The truth is "one click per frame at one position", which is what
    /// this keeps.
    /// </para>
    /// Pure, so the rule is tested rather than observed in a log.
    /// </summary>
    public sealed class ClickDedupe
    {
        /// <summary>Frame number that means "nothing accepted yet".</summary>
        public const int NoFrame = int.MinValue;

        private readonly object _gate = new object();

        private int _frame = NoFrame;
        private int _x;
        private int _y;

        /// <summary>
        /// Whether this click should be written.
        /// </summary>
        /// <param name="frame">Unity's frame counter.</param>
        /// <param name="x">Cursor x in pixels; rounded to whole pixels before comparing.</param>
        /// <param name="y">Cursor y in pixels; rounded to whole pixels before comparing.</param>
        /// <returns>
        /// False when the same frame already wrote a click at the same whole-pixel position;
        /// true otherwise. A second click at a different position in the same frame is written,
        /// because that is a different event, not a duplicate report of one.
        /// </returns>
        public bool ShouldWrite(int frame, float x, float y)
        {
            var px = Round(x);
            var py = Round(y);

            lock (_gate)
            {
                if (_frame == frame && _x == px && _y == py)
                {
                    return false;
                }

                _frame = frame;
                _x = px;
                _y = py;
                return true;
            }
        }

        /// <summary>Forgets the last accepted click.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _frame = NoFrame;
            }
        }

        // Away-from-zero, matching the "0" format the click line is written with, so the dedupe
        // and the text can never disagree about which pixel a click was on.
        private static int Round(float value) => (int)System.Math.Round(value, System.MidpointRounding.AwayFromZero);
    }
}
