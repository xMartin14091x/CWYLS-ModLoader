using System;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>Runs each Pack Maker cleanup even when an earlier cleanup throws.</summary>
    public static class MakerShutdown
    {
        /// <summary>
        /// Detaches the routes, closes the surface, then stops the owned listener.
        /// The caller releases its ownership fields first and reports any exception.
        /// If several cleanups throw, the last exception propagates.
        /// </summary>
        public static void Close(Action detach, Action closeSurface, Action stopListener)
        {
            try
            {
                detach();
            }
            finally
            {
                try
                {
                    closeSurface();
                }
                finally
                {
                    stopListener();
                }
            }
        }
    }
}
