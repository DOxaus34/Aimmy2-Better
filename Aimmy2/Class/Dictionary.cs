﻿using Visuality;
using Vortice.DXGI;

namespace Aimmy2.Class
{
    public static class Dictionary
    {
        public static string lastLoadedModel = "N/A";
        public static string lastLoadedConfig = "N/A";
        public static DetectedPlayerWindow? DetectedPlayerOverlay;
        public static FOV? FOVWindow;

        public static Dictionary<string, dynamic> bindingSettings = new()
        {
            { "Aim Keybind", "Right"},
            { "Second Aim Keybind", "LMenu"},
            { "Dynamic FOV Keybind", "Left"},
            { "Emergency Stop Keybind", "Delete"},
            { "Anti Recoil Keybind", "Left"},
            { "Disable Anti Recoil Keybind", "Oem6"},
            { "Gun 1 Key", "D1"},
            { "Gun 2 Key", "D2"},
        };

        public static Dictionary<string, dynamic> sliderSettings = new()
        {
            { "Suggested Model", ""},
            { "FOV Size", 640 },
              { "Bezier Strength", 20 },   // % curve, sensible mid‑value
             { "Bezier Steps", 10 },       // number of micro‑moves
            { "Dynamic FOV Size", 200 },
            { "Mouse Sensitivity (+/-)", 0.80 },
            { "Mouse Jitter", 0 },
            { "Y Offset (Up/Down)", 0 },
            { "Y Offset (%)", 50 },
            { "X Offset (Left/Right)", 0 },
            { "X Offset (%)", 50 },
            { "EMA Smoothening", 0.5},
            { "Auto Trigger Delay", 0.1 },
            { "AI Minimum Confidence", 45 },
            { "AI Confidence Font Size", 20 },
            { "Corner Radius", 0 },
            { "Border Thickness", 1 },
            { "Opacity", 1 }
        };

        // Make sure the Settings Name is the EXACT Same as the Toggle Name or I will smack you :joeangy:
        // nori
        public static Dictionary<string, dynamic> toggleState = new()
        {
            { "Aim Assist", false },
            { "Constant AI Tracking", false },
            { "Predictions", false },
            { "EMA Smoothening", false },
            { "Enable Gun Switching Keybind", false },
            { "Auto Trigger", false },
            { "Anti Recoil", false },
            { "FOV", false },
            { "Dynamic FOV", false },
            { "Masking", false },
            { "Show Detected Player", false },
            { "Show AI Confidence", false },
            { "Show Tracers", false },
            { "Collect Data While Playing", false },
            { "Auto Label Data", false },
            { "LG HUB Mouse Movement", false },
            { "Mouse Background Effect", true },
            { "UI TopMost", false },
            { "X Axis Percentage Adjustment", false },
            { "Y Axis Percentage Adjustment", false },
            { "Debug Mode", false },
            { "Show FPS", false },
        };

        public static Dictionary<string, dynamic> minimizeState = new()
        {
            { "Aim Assist", false },
            { "Aim Config", false },
            { "Auto Trigger", false },
            { "Anti Recoil", false},
            { "Anti Recoil Config", false },
            { "FOV Config", false },
            { "ESP Config", false },
            { "Settings Menu", false },
            { "X/Y Percentage Adjustment", false }
        };

        public static Dictionary<string, dynamic> dropdownState = new()
        {
            { "Prediction Method", "Kalman Filter" },
            { "Detection Area Type", "Closest to Center Screen" },
            { "Aiming Boundaries Alignment", "Center" },
            { "Mouse Movement Method", "Mouse Event" },
            { "Screen Capture Method", "DirectX" },
            { "Execution Provider Type", "CUDA" }
        };

        //public static IDXGIAdapter1 SelectedAdapter = null;
        //public static List<IDXGIAdapter1> adapters = new List<IDXGIAdapter1>();

      //  public static List<(int adapterIndex, int outputIndex, IDXGIOutput output)> monitors = new List<(int, int, IDXGIOutput)>();
        public static Dictionary<string, dynamic> colorState = new()
        {
            { "FOV Color", "#FF8080FF"},
            { "Detected Player Color", "#FF00FFFF"}
        };

        public static Dictionary<string, dynamic> AntiRecoilSettings = new()
        {
            { "Hold Time", 10 },
            { "Fire Rate", 200 },
            { "Y Recoil (Up/Down)", 10 },
            { "X Recoil (Left/Right)", 0 }
        };

        public static Dictionary<string, dynamic> filelocationState = new()
        {
            { "ddxoft DLL Location", ""},
            { "Gun 1 Config", "" },
            { "Gun 2 Config", "" }
        };

        public static T GetValueOrDefault<T>(Dictionary<string, T> dictionary, string key, T defaultValue)
        {
            if (dictionary.TryGetValue(key, out T? value))
            {
                return value;
            }
            else
            {
                return defaultValue;
            }
        }
    }
}