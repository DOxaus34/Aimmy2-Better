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

        // State for smoothed target coordinates
        private static double smoothedX = 0;
        private static double smoothedY = 0;
        
        // WindMouse State
        private static double wind_veloX = 0, wind_veloY = 0, wind_windX = 0, wind_windY = 0;
        private static readonly Random wind_random = new Random();
        private static PointF wind_mousePos = PointF.Empty;
        private static PointF wind_destination = PointF.Empty;

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
                // 0. Guard against un-initialised screen metrics
                if (ScreenWidth == 0 || ScreenHeight == 0) return;

                // Apply EMA Smoothing to the target coordinates if enabled
                if (IsEMASmoothingEnabled)
                {
                    // Initialize smoothed coordinates on the first run
                    if (smoothedX == 0 && smoothedY == 0)
                    {
                        smoothedX = detectedX;
                        smoothedY = detectedY;
                    }
                    smoothedX = EmaSmoothing(smoothedX, detectedX, smoothingFactor);
                    smoothedY = EmaSmoothing(smoothedY, detectedY, smoothingFactor);
                }
                else
                {
                    smoothedX = detectedX;
                    smoothedY = detectedY;
                }

                if (Dictionary.toggleState["WindMouse"])
                {
                    // Use absolute coordinates for WindMouse state
                    if (wind_mousePos == PointF.Empty)
                    {
                        var p = WinAPICaller.GetCursorPosition();
                        wind_mousePos = new PointF(p.X, p.Y);
                    }
                    // Update destination
                    wind_destination = new PointF((float)smoothedX, (float)smoothedY);
                    WindMouse();
                }
                else
                {
                    // If WindMouse is not active, reset its state.
                    if (wind_mousePos != PointF.Empty)
                    {
                        wind_mousePos = PointF.Empty;
                        wind_destination = PointF.Empty;
                        wind_veloX = 0;
                        wind_veloY = 0;
                        wind_windX = 0;
                        wind_windY = 0;
                    }
                    
                int rx = (int)(smoothedX - ScreenWidth / 2);
                int ry = (int)(smoothedY - ScreenHeight / 2);

                    if (Dictionary.toggleState["Bezier Curve"])
                    {
                        // Bezier Path
                        double strength = GetSlider("Bezier Strength", 20) / 100.0;
                        int steps = Math.Max(1, GetSliderInt("Bezier Steps", 10));
                        double sens = 1 - GetSlider("Mouse Sensitivity (+/-)", 0);

                Point start = new(0, 0), end = new(rx, ry);

                        double len = Math.Max(1, Math.Sqrt(rx * rx + ry * ry));
                        double px = -ry / len, py = rx / len;
                double bend = strength * len * 0.5;

                Point c1 = new((int)(rx / 3 + px * bend), (int)(ry / 3 + py * bend));
                Point c2 = new((int)(2 * rx / 3 + px * bend), (int)(2 * ry / 3 + py * bend));

                Point prev = start;
                for (int s = 1; s <= steps; ++s)
                {
                    double t = (s / (double)steps) * sens;
                    Point p = CubicBezier(start, end, c1, c2, t);
                    DispatchRelativeMove(p.X - prev.X, p.Y - prev.Y);
                    prev = p;
                }
                        previousX = prev.X;
                previousY = prev.Y;
                    }
                    else
                    {
                        // Linear Path
                        double sens = 1 - GetSlider("Mouse Sensitivity (+/-)", 0);
                        int moveX = (int)(rx * sens);
                        int moveY = (int)(ry * sens);
                        DispatchRelativeMove(moveX, moveY);
                        previousX = moveX;
                        previousY = moveY;
                    }
                }

                if (Dictionary.toggleState["Auto Trigger"])
                    _ = Task.Run(DoTriggerClick);
            }
            catch (Exception ex)
            {
                // Visible in Output-Debug window, keeps app alive
                Debug.WriteLine($"[MoveCrosshair] {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine(ex.StackTrace);
            }
        }

        private static void WindMouse()
        {
            // Vector from current virtual position to destination
            double targetX = wind_destination.X - wind_mousePos.X;
            double targetY = wind_destination.Y - wind_mousePos.Y;

            double dist = Math.Sqrt(targetX * targetX + targetY * targetY);
            if (dist < 1.0)
            {
                // We are at the destination. Stop.
                wind_veloX = 0;
                wind_veloY = 0;
                return;
            }

            // Get params from reference and make them configurable via sliders eventually
            double G_0 = GetSlider("WindMouse Gravity", 9);
            double W_0 = GetSlider("WindMouse Wind Strength", 3);
            double M_0 = 15.0; // Max speed
            double D_0 = 12.0; // Dampen range
            
            // Calculate wind based on distance
            double W_mag = Math.Min(W_0, dist);
            if (dist >= D_0)
            {
                wind_windX = wind_windX / Math.Sqrt(3) + (wind_random.NextDouble() * 2 - 1) * W_mag / Math.Sqrt(5);
                wind_windY = wind_windY / Math.Sqrt(3) + (wind_random.NextDouble() * 2 - 1) * W_mag / Math.Sqrt(5);
            }
            else // Within dampen range
            {
                wind_windX /= Math.Sqrt(3);
                wind_windY /= Math.Sqrt(3);
                if (M_0 < 3)
                {
                    M_0 = wind_random.NextDouble() * 3 + 3;
                }
                else
                {
                    M_0 /= Math.Sqrt(5);
                }
            }

            // Update velocity with wind and gravity
            wind_veloX += wind_windX + G_0 * targetX / dist;
            wind_veloY += wind_windY + G_0 * targetY / dist;

            // Clip velocity
            double v_mag = Math.Sqrt(wind_veloX * wind_veloX + wind_veloY * wind_veloY);
            if (v_mag > M_0)
            {
                double v_clip = M_0 / 2 + wind_random.NextDouble() * M_0 / 2;
                wind_veloX = (wind_veloX / v_mag) * v_clip;
                wind_veloY = (wind_veloY / v_mag) * v_clip;
            }

            // Calculate the move for this frame
            double moveX = wind_veloX;
            double moveY = wind_veloY;

            // Don't overshoot
            if (Math.Sqrt(moveX * moveX + moveY * moveY) > dist)
            {
                moveX = targetX;
                moveY = targetY;
                wind_veloX = 0; // Stop momentum
                wind_veloY = 0;
            }

            // Update our virtual position
            wind_mousePos.X += (float)moveX;
            wind_mousePos.Y += (float)moveY;

            // And move the real mouse
            DispatchRelativeMove((int)Math.Round(moveX), (int)Math.Round(moveY));
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