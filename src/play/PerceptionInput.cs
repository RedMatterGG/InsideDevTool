using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace InsideDev.Play
{
    // PerceptionInput.cs (owner OBSERVE): who drives the controlled body's input, and the huddle's grab, for
    // observe.body (stick_forced, forced_by, grab, holding) and for input tracks (TrackFrame.Forced). The game takes the
    // input over by assigning another BoyInput to Boy.input / Huddle.input (their own default is the BoyInput component on
    // the character's GameObject: DefaultBoyInput / DefaultHuddleInput, FP/Boy.cs:363-378, FP/Huddle.cs:251-266):
    //   BoySetInput (AC/BoySetInput.cs), added by the PlayMaker action PD_BoySetInput to the FSM's GameObject; it holds
    //   the stick x / y and buttons it is told to (useStickVal_X / Y, caps, useJumpVal ...) while scriptActive. Example:
    //   the cafeteria railing, _PM RailingCrash state "She was an idiot", stick (1, 0) for 3 s.
    //   HuddleFallTest (AC/HuddleFallTest.cs:33), the CEO wall: stick (1, 0) until 2 s after the smash.
    //   anything else on another GameObject (BoySpeedTrigger, BoyLimitInput, ...): reported by type.
    // Huddle grab: HuddleGrabState.IsGrabbing / state (child state: Beam, Box, Container, Item, Shield, SitOnTop) /
    // body / collider (FP/HuddleGrabState.cs:296-310, 442).
    public static partial class Perception
    {
        // main thread: the BoyInput that the game put in place of the character's own input, or null
        public static BoyInput ForcedInput(Character ch)
        {
            BoyInput inp = null;
            var hud = ch as Huddle;
            var boy = ch as Boy;
            if (hud != null) inp = hud.input;
            else if (boy != null) inp = boy.input;
            if (inp == null || ch == null) return null;
            var bsi = inp as BoySetInput;
            if (bsi != null) return bsi.isActivated() ? inp : null;
            if (inp is HuddleFallTest) return inp;
            return inp.gameObject != ch.gameObject ? inp : null;
        }

        // main thread: what the forcing input does; stick = it overrides or scales the stick
        internal static string ForcedReason(BoyInput inp, out bool stick)
        {
            stick = true;
            string where = inp.gameObject != null ? inp.gameObject.name : "?";
            var bsi = inp as BoySetInput;
            if (bsi != null)
            {
                var parts = new List<string>();
                bool capX = !bsi.useStickVal_X && (bsi.posCapX != 1f || Mathf.Abs(bsi.negCapX) != 1f);
                bool capY = !bsi.useStickVal_Y && (bsi.posCapY != 1f || Mathf.Abs(bsi.negCapY) != 1f);
                if (bsi.useStickVal_X) parts.Add("stick x " + F(bsi.stickVal.x));
                if (bsi.useStickVal_Y) parts.Add("stick y " + F(bsi.stickVal.y));
                if (capX) parts.Add("stick x scaled");
                if (capY) parts.Add("stick y scaled");
                if (bsi.useJumpVal) parts.Add("jump " + (bsi.jumpVal ? "held" : "off"));
                if (bsi.useActionVal) parts.Add("action " + (bsi.actionVal ? "held" : "off"));
                if (bsi.useGrabVal) parts.Add("grab " + (bsi.grabVal ? "held" : "off"));
                stick = bsi.useStickVal_X || bsi.useStickVal_Y || capX || capY;
                string fsm = FsmStateOf(inp.gameObject);
                return "BoySetInput on " + where + (fsm != null ? " (" + fsm + ")" : "") + ": " + (parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "input passed through");
            }
            if (inp is HuddleFallTest) return "HuddleFallTest on " + where + ": stick held right (1, 0) until 2 s after the CEO wall smash";
            return inp.GetType().Name + " on " + where + " drives the input";
        }

        static string FsmStateOf(GameObject go)
        {
            if (go == null) return null;
            try
            {
                var fsm = go.GetComponent<PlayMakerFSM>();
                if (fsm == null) return null;
                string st = fsm.ActiveStateName;
                return string.IsNullOrEmpty(st) ? fsm.FsmName : fsm.FsmName + ": " + st;
            }
            catch (Exception) { return null; }
        }

        static void FillForced(Ctl c, Character ch)
        {
            BoyInput inp = null;
            try { inp = ForcedInput(ch); } catch (Exception) { inp = null; }
            if (inp == null) return;
            bool stick;
            c.forcedBy = StateNames.Clip(ForcedReason(inp, out stick), 160);
            c.stickForced = stick;
        }

        static void FillGrab(Ctl c, Huddle hud)
        {
            c.grab = "none";
            HuddleGrabState gs = null;
            try { gs = hud.grabState; } catch (Exception) { gs = null; }
            if (gs == null || !gs.IsGrabbing()) return;
            var child = gs.state;
            c.grab = child != null ? StateNames.Short(child.GetType().Name) : "Grab";
            var body = gs.body;
            var col = gs.collider;
            if (body != null) c.holding = body.gameObject;
            else if (col != null) c.holding = col.gameObject;
        }

        static string F(float v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
    }
}
