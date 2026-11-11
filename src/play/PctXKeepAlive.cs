using System.Collections.Generic;
using UnityEngine;

namespace InsideDev.Play
{
    // PctXKeepAlive.cs: the one owner of Application.runInBackground inside the Play module. The agent input (AgentCore,
    // owner "input") and the game clock (Stepper, owner "time") both need the game to keep running without window focus;
    // the value the player had is saved once, when the first owner comes in, and put back when the last one leaves, so a
    // partial restore of one of them can never leave the other's "true" behind. Main thread only.
    internal static class KeepAlive
    {
        static readonly List<string> owners = new List<string>(4);
        static bool saved, before;

        public static bool Held { get { return owners.Count > 0; } }

        public static void Acquire(string owner)
        {
            if (!owners.Contains(owner)) owners.Add(owner);
            if (!saved)
            {
                saved = true;
                before = Application.runInBackground;
            }
            if (!Application.runInBackground) Application.runInBackground = true;
        }

        // null while another owner still holds it (or owner never came in); else what was put back
        public static string Release(string owner)
        {
            if (!owners.Remove(owner) || owners.Count > 0 || !saved) return null;
            saved = false;
            if (Application.runInBackground != before) Application.runInBackground = before;
            return "runInBackground " + (before ? "on" : "off");
        }

        // every frame while an owner is in: something else switched it off
        public static void Hold()
        {
            if (owners.Count > 0 && !Application.runInBackground) Application.runInBackground = true;
        }
    }
}
