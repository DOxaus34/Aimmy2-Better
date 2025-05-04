using Aimmy2.Class;
using Aimmy2.MouseMovementLibraries.GHubSupport;
using Aimmy2.MouseMovementLibraries.RazerSupport;
using Aimmy2.MouseMovementLibraries.SendInputSupport;
using Aimmy2.WinformsReplacement;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Aimmy2.InputLogic
{
    internal class MouseManager
    {
        private static readonly double ScreenWidth = WinAPICaller.ScreenWidth;
        private static readonly double ScreenHeight = WinAPICaller.ScreenHeight;

        private static DateTime LastClickTime = DateTime.MinValue;
        private static int LastAntiRecoilClickTime = 0;



        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private static double previousX = 0;
        private static double previousY = 0;
        public static double smoothingFactor = 0.5;
        public static bool IsEMASmoothingEnabled = false;
        // === Bezier slider bindings ===
        private static double GetSlider(string key, double fallback = 0.0) =>
    Dictionary.sliderSettings.TryGetValue(key, out var v) ? (double)v : fallback;

        private static int GetSliderInt(string key, int fallback = 0) =>
            (int)GetSlider(key, fallback);



        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
        private static Random MouseRandom = new();

        private static Point CubicBezier(Point start, Point end, Point control1, Point control2, double t)
        {
            double u = 1 - t;
            double tt = t * t;
            double uu = u * u;
            double uuu = uu * u;
            double ttt = tt * t;

            double x = uuu * start.X + 3 * uu * t * control1.X + 3 * u * tt * control2.X + ttt * end.X;
            double y = uuu * start.Y + 3 * uu * t * control1.Y + 3 * u * tt * control2.Y + ttt * end.Y;

            if (IsEMASmoothingEnabled)
            {
                x = EmaSmoothing(previousX, x, smoothingFactor);
                y = EmaSmoothing(previousY, y, smoothingFactor);
            }

            return new Point((int)x, (int)y);
        }



        private static double EmaSmoothing(double previousValue, double currentValue, double smoothingFactor) => currentValue * smoothingFactor + previousValue * (1 - smoothingFactor);

        public static async Task DoTriggerClick()
        {
            int timeSinceLastClick = (int)(DateTime.UtcNow - LastClickTime).TotalMilliseconds;
            int triggerDelayMilliseconds = (int)(Dictionary.sliderSettings["Auto Trigger Delay"] * 1000);
            const int clickDelayMilliseconds = 20;

            if (timeSinceLastClick < triggerDelayMilliseconds && LastClickTime != DateTime.MinValue)
            {
                return;
            }

            string mouseMovementMethod = Dictionary.dropdownState["Mouse Movement Method"];
            Action mouseDownAction, mouseUpAction;

            (mouseDownAction, mouseUpAction) = GetMouseActions(mouseMovementMethod);

            mouseDownAction.Invoke();
            await Task.Delay(clickDelayMilliseconds);
            mouseUpAction.Invoke();

            LastClickTime = DateTime.UtcNow;

            static (Action, Action) GetMouseActions(string method)
            {
                return method switch
                {
                    "SendInput" => (
                        () => SendInputMouse.SendMouseCommand(MOUSEEVENTF_LEFTDOWN),
                        () => SendInputMouse.SendMouseCommand(MOUSEEVENTF_LEFTUP)
                    ),
                    "LG HUB" => (
                        () => LGMouse.Move(1, 0, 0, 0),
                        () => LGMouse.Move(0, 0, 0, 0)
                    ),
                    "Razer Synapse (Require Razer Peripheral)" => (
                        () => RZMouse.mouse_click(1),
                        () => RZMouse.mouse_click(0)
                    ),
                    _ => (
                        () => mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0),
                        () => mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
                    )
                };
            }
        }

        public static void DoAntiRecoil()
        {
            int timeSinceLastClick = Math.Abs(DateTime.UtcNow.Millisecond - LastAntiRecoilClickTime);

            if (timeSinceLastClick < Dictionary.AntiRecoilSettings["Fire Rate"])
            {
                return;
            }

            int xRecoil = (int)Dictionary.AntiRecoilSettings["X Recoil (Left/Right)"];
            int yRecoil = (int)Dictionary.AntiRecoilSettings["Y Recoil (Up/Down)"];

            switch (Dictionary.dropdownState["Mouse Movement Method"])
            {
                case "SendInput":
                    SendInputMouse.SendMouseCommand(MOUSEEVENTF_MOVE, xRecoil, yRecoil);
                    break;

                case "LG HUB":
                    LGMouse.Move(0, xRecoil, yRecoil, 0);
                    break;

                case "Razer Synapse (Require Razer Peripheral)":
                    RZMouse.mouse_move(xRecoil, yRecoil, true);
                    break;

                default:
                    mouse_event(MOUSEEVENTF_MOVE, (uint)xRecoil, (uint)yRecoil, 0, 0);
                    break;
            }

            LastAntiRecoilClickTime = DateTime.UtcNow.Millisecond;
        }

        public static void MoveCrosshair(int detectedX, int detectedY)
        {
            try
            {
                // 0. Guard against un‑initialised screen metrics
                if (ScreenWidth == 0 || ScreenHeight == 0) return;

                // 1. Parameters -------------------------------------------------------
                double strength = GetSlider("Bezier Strength", 20) / 100.0;
                int steps = Math.Max(1, GetSliderInt("Bezier Steps", 10));

                double sens = 1 - GetSlider("Mouse Sensitivity (+/-)", 0);

                // 2. Raw relative vector ---------------------------------------------
                int rx = (int)(detectedX - ScreenWidth / 2);
                int ry = (int)(detectedY - ScreenHeight / 2);

                int jitter = GetSliderInt("Mouse Jitter", 0);
                rx = Math.Clamp(rx + MouseRandom.Next(-jitter, jitter), -150, 150);  //:contentReference[oaicite:5]{index=5}
                ry = Math.Clamp(ry + MouseRandom.Next(-jitter, jitter), -150, 150);

                // 3. Build Bezier path -----------------------------------------------
                Point start = new(0, 0), end = new(rx, ry);

                double len = Math.Max(1, Math.Sqrt(rx * rx + ry * ry));             // avoid /0
                double px = -ry / len, py = rx / len;                                // perpendicular
                double bend = strength * len * 0.5;

                Point c1 = new((int)(rx / 3 + px * bend), (int)(ry / 3 + py * bend));
                Point c2 = new((int)(2 * rx / 3 + px * bend), (int)(2 * ry / 3 + py * bend));

                // 4. Step along the curve --------------------------------------------
                Point prev = start;
                for (int s = 1; s <= steps; ++s)
                {
                    double t = (s / (double)steps) * sens;
                    Point p = CubicBezier(start, end, c1, c2, t);

                    DispatchRelativeMove(p.X - prev.X, p.Y - prev.Y);
                    prev = p;
                }

                previousX = prev.X;  // EMA smoothing uses these
                previousY = prev.Y;

                if (Dictionary.toggleState["Auto Trigger"])
                    _ = Task.Run(DoTriggerClick);
            }
            catch (Exception ex)
            {
                // Visible in Output‑Debug window, keeps app alive
                Debug.WriteLine($"[MoveCrosshair] {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine(ex.StackTrace);
            }
        }



        private static void DispatchRelativeMove(int dx, int dy)
        {
            switch (Dictionary.dropdownState["Mouse Movement Method"])
            {
                case "SendInput":
                    SendInputMouse.SendMouseCommand(MOUSEEVENTF_MOVE, dx, dy);
                    break;

                case "LG HUB":
                    LGMouse.Move(0, dx, dy, 0);
                    break;

                case "Razer Synapse (Require Razer Peripheral)":
                    RZMouse.mouse_move(dx, dy, true);
                    break;

                default:
                    mouse_event(MOUSEEVENTF_MOVE, (uint)dx, (uint)dy, 0, 0);
                    break;
            }
        }


    }

}