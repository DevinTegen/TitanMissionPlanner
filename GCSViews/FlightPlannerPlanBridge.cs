using MissionPlanner.Utilities;
using System;
using System.Collections.Generic;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// Lets the QGC-style Plan screen read and write the same mission the classic Flight Planner edits,
    /// so Upload/Download, saving, the terrain tools and the classic view all stay in step.
    /// </summary>
    public partial class FlightPlanner
    {
        /// <summary>home (altitude in metres AMSL) and the mission items (altitudes in metres)</summary>
        public bool TryGetMissionForPlan(out PointLatLngAlt home, out List<Locationwp> items)
        {
            home = null;
            items = new List<Locationwp>();
            try
            {
                EnsureMissionType();
                double.TryParse(TXT_homelat.Text, out var lat);
                double.TryParse(TXT_homelng.Text, out var lng);
                double.TryParse(TXT_homealt.Text, out var alt);
                home = new PointLatLngAlt(lat, lng, alt / CurrentState.multiplieralt, "H");
                items = GetCommandList();
                return true;
            }
            catch (Exception ex)
            {
                log.Error(ex);
                return false;
            }
        }

        /// <summary>replace the mission with the Plan screen's version</summary>
        public void SetMissionFromPlan(PointLatLngAlt home, List<Locationwp> items)
        {
            EnsureMissionType();

            // set home first so loading the list does not ask whether to reset home
            TXT_homelat.Text = home.Lat.ToString();
            TXT_homelng.Text = home.Lng.ToString();
            TXT_homealt.Text = (home.Alt * CurrentState.multiplieralt).ToString("0.00");

            if (items.Count == 0)
            {
                quickadd = true;
                Commands.CurrentCell = null;
                while (Commands.Rows.Count > 0)
                    Commands.Rows.Clear();
                quickadd = false;
                writeKML();
                return;
            }

            // processToScreen expects the home position as the first entry (and removes it again)
            var list = new List<Locationwp>
            {
                new Locationwp
                {
                    id = (ushort) MAVLink.MAV_CMD.WAYPOINT, lat = home.Lat, lng = home.Lng, alt = (float) home.Alt,
                    frame = (byte) MAVLink.MAV_FRAME.GLOBAL
                }
            };
            list.AddRange(items);
            processToScreen(list);
        }

        void EnsureMissionType()
        {
            if ((MAVLink.MAV_MISSION_TYPE) cmb_missiontype.SelectedValue != MAVLink.MAV_MISSION_TYPE.MISSION)
                cmb_missiontype.SelectedValue = MAVLink.MAV_MISSION_TYPE.MISSION;
        }

        /// <summary>default altitude for new waypoints from the Flight Planner box, metres</summary>
        public double DefaultAltitudeMetres
        {
            get
            {
                if (double.TryParse(TXT_DefaultAlt.Text, out var v) && v > 0)
                    return v / CurrentState.multiplieralt;
                return 100;
            }
        }
    }
}
