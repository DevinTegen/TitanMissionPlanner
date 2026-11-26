using System;
using System.Drawing;

namespace MissionPlanner.GCSViews
{
    public partial class FlightPlanner
    {
        DateTime MouseDownTime;
        Point MouseDownFirstLocal;

        /// <summary>a short click that stayed put (a hold or a drag is not a click)</summary>
        bool IsQuickClick(Point up)
        {
            var moved = Math.Abs(up.X - MouseDownFirstLocal.X) + Math.Abs(up.Y - MouseDownFirstLocal.Y);
            return moved < 6 && (DateTime.Now - MouseDownTime).TotalMilliseconds < 350;
        }
    }
}
