using Aimmy2.Class;
using Aimmy2.InputLogic;
using Aimmy2.MouseMovementLibraries.GHubSupport;
using Aimmy2.MouseMovementLibraries.RazerSupport;
using Aimmy2.Other;
using Aimmy2.UILibrary;
using Aimmy2.WinformsReplacement;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using UILibrary;
using Visuality;
using Application = System.Windows.Application;

namespace Aimmy2
{
    public partial class MainWindow : Window
    {
        #region Main Variables

        private InputBindingManager bindingManager;
        private FileManager fileManager;
        private static FOV FOVWindow = new();
        private static DetectedPlayerWindow DPWindow = new();
        private static GithubManager githubManager = new();
        public UI uiManager = new();

        private Dictionary<string, AToggle> toggleInstances = [];

        private bool CurrentlySwitching;
        private ScrollViewer? CurrentScrollViewer;

        private HashSet<string> AvailableModels = new();
        private HashSet<string> AvailableConfigs = new();

        // Flags for Lazy Loading
        private bool isAimMenuLoaded = false;
        private bool isSettingsMenuLoaded = false;
        private bool isCreditsMenuLoaded = false;
        private bool isStoreMenuLoaded = false;

        private static double ActualFOV = 640;

        #endregion Main Variables

        #region Loading Window

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // This event should only be responsible for wiring up the UI after it's loaded.
            // The heavy lifting is now done in the Preload sequence.
        }

        public async Task PreloadAsync()
        {
            // Runs file I/O on a background thread
            await LoadDataAsync();

            // Runs on the UI thread, creating all UI controls asynchronously
            await CreateUIAsync();

            // Runs network I/O on a background thread, then updates UI
            await LoadStoreDataAsync();
        }

        private Task LoadDataAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    RequirementsManager.MainWindowInit();

                    if (!File.Exists("bin\\labels\\labels.txt")) { File.WriteAllText("bin\\labels\\labels.txt", "Enemy"); }

                    SaveDictionary.LoadJSON(Dictionary.minimizeState, "bin\\minimize.cfg");
                    SaveDictionary.LoadJSON(Dictionary.bindingSettings, "bin\\binding.cfg");
                    SaveDictionary.LoadJSON(Dictionary.colorState, "bin\\colors.cfg");
                    SaveDictionary.LoadJSON(Dictionary.filelocationState, "bin\\filelocations.cfg");

                    Dictionary<string, dynamic> tempToggleState = new() { { "Debug Mode", false } };
                    SaveDictionary.LoadJSON(tempToggleState, "bin\\toggleState.cfg", false);

                    if (tempToggleState["Debug Mode"])
                    {
                        Dictionary.toggleState["Debug Mode"] = true;
                        FileManager.LogInfo("Debug mode was found ON during last launch, automatically enabling...");
                    }
                }
                catch (Exception ex)
                {
                    FileManager.LogError("Error during background data loading: " + ex, true);
                }
            });
        }

        private async Task CreateUIAsync()
        {
            try
            {
                // Must be on UI thread
                fileManager = new FileManager(ModelListBox, SelectedModelNotifier, ConfigsListBox, SelectedConfigNotifier);
                FileManager.LoadConfig(uiManager: uiManager);

                CurrentScrollViewer = FindName("AimMenu") as ScrollViewer;
                if (CurrentScrollViewer == null) throw new NullReferenceException("CurrentScrollViewer is null");

                Dictionary.DetectedPlayerOverlay = DPWindow;
                Dictionary.FOVWindow = FOVWindow;

                bindingManager = new InputBindingManager();
                bindingManager.SetupDefault("Aim Keybind", Dictionary.bindingSettings["Aim Keybind"]);

                // LAZY LOADING: Load the default menu (AimMenu) on startup, but not the others.
                await LoadAimMenuAsync();
                isAimMenuLoaded = true;

                SaveDictionary.LoadJSON(Dictionary.dropdownState, "bin\\dropdown.cfg");
                LoadAimDropdownStates();

                LoadMenuMinimizers();
                VisibilityXY();

                ActualFOV = Dictionary.sliderSettings["FOV Size"];
                PropertyChanger.ReceiveNewConfig = (configPath, load) => FileManager.LoadConfig(uiManager);

                PropertyChanger.PostNewFOVSize(Dictionary.sliderSettings["FOV Size"]);
                await Task.Delay(1);
                PropertyChanger.PostColor((Color)ColorConverter.ConvertFromString(Dictionary.colorState["FOV Color"]));
                PropertyChanger.PostDPColor((Color)ColorConverter.ConvertFromString(Dictionary.colorState["Detected Player Color"]));
                PropertyChanger.PostDPFontSize((int)Dictionary.sliderSettings["AI Confidence Font Size"]);
                await Task.Delay(1);
                PropertyChanger.PostDPWCornerRadius((int)Dictionary.sliderSettings["Corner Radius"]);
                PropertyChanger.PostDPWBorderThickness((double)Dictionary.sliderSettings["Border Thickness"]);
                PropertyChanger.PostDPWOpacity((double)Dictionary.sliderSettings["Opacity"]);

                Topmost = Dictionary.toggleState["UI TopMost"];
                AboutSpecs.Content = $"{GetProcessorName()} • {GetVideoControllerName()} • {GetFormattedMemorySize()}GB RAM";
            }
            catch (Exception ex)
            {
                FileManager.LogError("Error creating UI: " + ex, true);
            }
        }
        
        private Task LoadStoreDataAsync()
        {
            return Task.Run(async () =>
            {
                try
                {
                    FileManager.LogInfo("Fetching models and configs from Github in the background...");
                    
                    Task models = FileManager.RetrieveAndAddFiles("https://api.github.com/repos/Babyhamsta/Aimmy/contents/models", "bin\\models", AvailableModels);
                    Task configs = FileManager.RetrieveAndAddFiles("https://api.github.com/repos/Babyhamsta/Aimmy/contents/configs", "bin\\configs", AvailableConfigs);

                    // Wait for data to be fetched, but do not build the UI yet.
                    await Task.WhenAll(models, configs);
                }
                catch (Exception ex)
                {
                    FileManager.LogError("Error loading store data: " + ex, false);
                }
            });
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            fileManager.InQuittingState = true;

            Dictionary.toggleState["Aim Assist"] = false;
            Dictionary.toggleState["FOV"] = false;
            Dictionary.toggleState["Show Detected Player"] = false;

            FOVWindow.Close();
            DPWindow.Close();

            if (Dictionary.dropdownState["Mouse Movement Method"] == "LG HUB")
            {
                LGMouse.Close();
            }

            SaveDictionary.WriteJSON(Dictionary.sliderSettings);
            SaveDictionary.WriteJSON(Dictionary.minimizeState, "bin\\minimize.cfg");
            SaveDictionary.WriteJSON(Dictionary.bindingSettings, "bin\\binding.cfg");
            SaveDictionary.WriteJSON(Dictionary.dropdownState, "bin\\dropdown.cfg");
            SaveDictionary.WriteJSON(Dictionary.colorState, "bin\\colors.cfg");
            SaveDictionary.WriteJSON(Dictionary.filelocationState, "bin\\filelocations.cfg");
            SaveDictionary.WriteJSON(Dictionary.toggleState, "bin\\toggleState.cfg");

            FileManager.AIManager?.Dispose();

            Application.Current.Shutdown();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        #endregion Loading Window

        #region Menu Logic

        private string CurrentMenu = "AimMenu";

        private async void MenuSwitch(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button clickedButton && !CurrentlySwitching && CurrentMenu != clickedButton.Tag.ToString())
            {
                CurrentlySwitching = true;
                string? menuName = clickedButton.Tag.ToString();
                Animator.ObjectShift(TimeSpan.FromMilliseconds(350), MenuHighlighter, MenuHighlighter.Margin, clickedButton.Margin);

                // LAZY LOADING IMPLEMENTATION
                if (menuName == "SettingsMenu" && !isSettingsMenuLoaded)
                {
                    await LoadSettingsMenuAsync();
                    isSettingsMenuLoaded = true;
                }
                else if (menuName == "CreditsMenu" && !isCreditsMenuLoaded)
                {
                    await LoadCreditsMenuAsync();
                    isCreditsMenuLoaded = true;
                }
                else if (menuName == "StoreMenu" && !isStoreMenuLoaded)
                {
                    // Now that we need it, build the Store UI
                    DownloadGateway(ModelStoreScroller, AvailableModels, "models");
                    DownloadGateway(ConfigStoreScroller, AvailableConfigs, "configs");
                    isStoreMenuLoaded = true;
                }

                await SwitchScrollPanels(FindName(menuName) as ScrollViewer ?? throw new NullReferenceException("Scrollpanel is null"));
                CurrentMenu = menuName!;
            }
        }

        private async Task SwitchScrollPanels(ScrollViewer MovingScrollViewer)
        {
            MovingScrollViewer.Visibility = Visibility.Visible;
            Animator.Fade(MovingScrollViewer);
            Animator.ObjectShift(TimeSpan.FromMilliseconds(350), MovingScrollViewer, MovingScrollViewer.Margin, new Thickness(50, 50, 0, 0));

            Animator.FadeOut(CurrentScrollViewer!);
            Animator.ObjectShift(TimeSpan.FromMilliseconds(350), CurrentScrollViewer!, CurrentScrollViewer!.Margin, new Thickness(50, 450, 0, -400));
            await Task.Delay(350);

            CurrentScrollViewer.Visibility = Visibility.Collapsed;
            CurrentScrollViewer = MovingScrollViewer;
            CurrentlySwitching = false;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateVisibilityBasedOnSearchText((TextBox)sender, ModelStoreScroller);
        }

        private void CSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateVisibilityBasedOnSearchText((TextBox)sender, ConfigStoreScroller);
        }

        private static void UpdateVisibilityBasedOnSearchText(TextBox textBox, Panel panel)
        {
            string searchText = textBox.Text.ToLower();

            foreach (ADownloadGateway item in panel.Children.OfType<ADownloadGateway>())
            {
                item.Visibility = item.Title.Content.ToString()?.ToLower().Contains(searchText) == true
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void Main_Background_Gradient(object sender, MouseEventArgs e)
        {
            if (Dictionary.toggleState["Mouse Background Effect"])
            {
                var mousePosition = WinAPICaller.GetCursorPosition();
                var translatedMousePos = PointFromScreen(new System.Windows.Point(mousePosition.X, mousePosition.Y));

                double targetAngle = Math.Atan2(translatedMousePos.Y - (MainBorder.ActualHeight * 0.5), translatedMousePos.X - (MainBorder.ActualWidth * 0.5)) * (180 / Math.PI);

                double angleDifference = CalculateAngleDifference(targetAngle, 360, 180, 1);
                currentGradientAngle = (currentGradientAngle + angleDifference + 360) % 360;
                RotaryGradient.Angle = currentGradientAngle;
            }
        }

        private void ApplyChangesButton_Click(object sender, RoutedEventArgs e)
        {
            // Background
            var themeColorPicker = FindName("ThemeColorPicker") as MaterialDesignThemes.Wpf.ColorPicker;
            var gradientStop = FindName("RadialGradientColorStop") as GradientStop;
            if (themeColorPicker != null && gradientStop != null)
            {
                gradientStop.Color = themeColorPicker.Color;
            }

            // Accent
            var accentColorPicker = FindName("SecondColorPicker") as ColorPicker.StandardColorPicker;
            var accentGradientStop = FindName("BlackGradientStop") as GradientStop;
            if (accentColorPicker != null && accentGradientStop != null)
            {
                accentGradientStop.Color = accentColorPicker.SelectedColor;
            }

            // Font
            var fontColorPicker = FindName("FontColorPicker") as ColorPicker.StandardColorPicker;
            if (fontColorPicker != null)
            {
                var newBrush = new SolidColorBrush(fontColorPicker.SelectedColor);
                ChangeAllTextForegroundColor(this, newBrush);
            }
        }

        private void PreviewButton_Click(object sender, RoutedEventArgs e)
        {
            // For now, preview will do the same as apply.
            ApplyChangesButton_Click(sender, e);
        }

        private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            // Reset Background
            var gradientStop = FindName("RadialGradientColorStop") as GradientStop;
            if (gradientStop != null)
            {
                gradientStop.Color = (Color)ColorConverter.ConvertFromString("#FF120338");
            }

            // Reset Accent
            var accentGradientStop = FindName("BlackGradientStop") as GradientStop;
            if (accentGradientStop != null)
            {
                accentGradientStop.Color = Colors.Black;
            }

            // Reset Font
            ChangeAllTextForegroundColor(this, new SolidColorBrush(Colors.White));

            // Also reset the color pickers themselves
            var themeColorPicker = FindName("ThemeColorPicker") as MaterialDesignThemes.Wpf.ColorPicker;
            if (themeColorPicker != null)
            {
                themeColorPicker.Color = (Color)ColorConverter.ConvertFromString("#FF120338");
            }
            var accentColorPicker = FindName("SecondColorPicker") as ColorPicker.StandardColorPicker;
            if (accentColorPicker != null)
            {
                accentColorPicker.SelectedColor = Colors.Black;
            }
            var fontColorPicker = FindName("FontColorPicker") as ColorPicker.StandardColorPicker;
            if (fontColorPicker != null)
            {
                fontColorPicker.SelectedColor = Colors.White;
            }
        }

        private void ChangeAllTextForegroundColor(DependencyObject parent, Brush brush)
        {
            if (parent == null) return;

            if (parent is FrameworkElement fe && (fe.Name == "FantaEngineLabel" || fe.Name == "ForkedAndChangedBySection"))
            {
                return;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is TextBlock textBlock)
                {
                    textBlock.Foreground = brush;
                }
                else if (child is Label label)
                {
                    label.Foreground = brush;
                }
                else if (child is System.Windows.Controls.Button button)
                {
                    button.Foreground = brush;
                }
                else if (child is AntWpf.Controls.Button antButton)
                {
                    antButton.Foreground = brush;
                }

                ChangeAllTextForegroundColor(child, brush);
            }
        }

        private void LoadAimDropdownStates()
        {
            // Detection Area Type Dropdown
            uiManager.D_DetectionAreaType!.DropdownBox.SelectedIndex = Dictionary.dropdownState["Detection Area Type"] switch
            {
                "Closest to Mouse" => 1,
                // Add more cases as needed
                _ => 0 // Default case
            };

            // Aiming Boundaries Alignment Dropdown
            uiManager.D_AimingBoundariesAlignment!.DropdownBox.SelectedIndex = Dictionary.dropdownState["Aiming Boundaries Alignment"] switch
            {
                "Top" => 1,
                "Bottom" => 2,
                _ => 0 // Default case if none of the above matches
            };
        }

        private void LoadSettingsDropdownStates()
        {
            // Mouse Movement Method Dropdown
            uiManager.D_MouseMovementMethod!.DropdownBox.SelectedIndex = Dictionary.dropdownState["Mouse Movement Method"] switch
            {
                "SendInput" => 1,
                "LG HUB" => 2,
                "Razer Synapse (Require Razer Peripheral)" => 3,
                _ => 0 // Default case if none of the above matches
            };
            uiManager.D_ExecutionProvider!.DropdownBox.SelectedIndex = Dictionary.dropdownState["Execution Provider Type"] switch
            {
                "CUDA" => 0,
                "TensorRT" => 1,
                _ => 0 // Default case if none of the above matches
            };
        }

        static void OnExecutionProviderSelected(object sender, RoutedEventArgs e)
        {
            if (Dictionary.dropdownState["Execution Provider Type"] == "TensorRT")
            {
                if (!RequirementsManager.IsTensorRTInstalled())
                {
                    FileManager.LogWarning("TensorRT may not be installed, the program may not work with TensorRT", true);
                }
            }

            FileManager.LogWarning("Load a new model to initialize new Execution Provider", true);
        }

        private AToggle AddToggle(StackPanel panel, string title)
        {
            if (!Dictionary.toggleState.ContainsKey(title))
            {
                Dictionary.toggleState[title] = false;
            }

            var toggle = new AToggle(title);
            toggleInstances[title] = toggle;

            // Load Toggle State
            (Dictionary.toggleState[title] ? (Action)(() => toggle.EnableSwitch()) : () => toggle.DisableSwitch())();

            toggle.Reader.Click += (sender, e) =>
            {
                Dictionary.toggleState[title] = !Dictionary.toggleState[title];

                UpdateToggleUI(toggle, Dictionary.toggleState[title]);

                Toggle_Action(title);
            };

            panel.Children.Add(toggle);
            return toggle;
        }

        private static void UpdateToggleUI(AToggle toggle, bool isEnabled)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (isEnabled)
                {
                    toggle.EnableSwitch();
                }
                else
                {
                    toggle.DisableSwitch();
                }
            });
        }

        private void Toggle_Action(string title)
        {
            switch (title)
            {
                case "FOV":
                    FOVWindow.Visibility = Dictionary.toggleState[title] ? Visibility.Visible : Visibility.Hidden;
                    break;

                case "Show Detected Player":
                    ShowHideDPWindow();
                    DPWindow.DetectedPlayerFocus.Visibility = Dictionary.toggleState[title] ? Visibility.Visible : Visibility.Collapsed;
                    break;

                case "Show AI Confidence":
                    DPWindow.DetectedPlayerConfidence.Visibility = Dictionary.toggleState[title] ? Visibility.Visible : Visibility.Collapsed;
                    break;

                case "Show FPS":
                    DPWindow.FpsLabel.Visibility = Dictionary.toggleState[title] ? Visibility.Visible : Visibility.Collapsed;
                    break;

                case "Mouse Background Effect":
                    if (!Dictionary.toggleState[title]) { RotaryGradient.Angle = 0; }
                    break;

                case "UI TopMost":
                    Topmost = Dictionary.toggleState[title];
                    break;

                case "EMA Smoothening":
                    MouseManager.IsEMASmoothingEnabled = Dictionary.toggleState[title];
                    if (uiManager.S_EMASmoothing != null)
                        uiManager.S_EMASmoothing.Visibility = Dictionary.toggleState[title] ? Visibility.Visible : Visibility.Collapsed;
                    break;

                case "WindMouse":
                    bool isWindMouseEnabled = Dictionary.toggleState[title];
                    if (uiManager.S_WindMouseGravity != null)
                        uiManager.S_WindMouseGravity.Visibility = isWindMouseEnabled ? Visibility.Visible : Visibility.Collapsed;
                    if (uiManager.S_WindMouseWindStrength != null)
                        uiManager.S_WindMouseWindStrength.Visibility = isWindMouseEnabled ? Visibility.Visible : Visibility.Collapsed;
                    if (uiManager.S_WindMouseTargetJitter != null)
                        uiManager.S_WindMouseTargetJitter.Visibility = isWindMouseEnabled ? Visibility.Visible : Visibility.Collapsed;
                    break;

                case "X Axis Percentage Adjustment":
                    VisibilityXY();
                    break;

                case "Y Axis Percentage Adjustment":
                    VisibilityXY();
                    break;

                case "Bezier Curve":
                    bool isEnabled = Dictionary.toggleState[title];
                    uiManager.S_BezierStrength.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
                    uiManager.S_BezierSteps.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
                    break;
            }
        }

        private AKeyChanger AddKeyChanger(StackPanel panel, string title, string keybind)
        {
            var keyChanger = new AKeyChanger(title, keybind);
            panel.Children.Add(keyChanger);

            keyChanger.Reader.Click += (sender, e) =>
            {
                keyChanger.KeyNotifier.Content = "...";
                bindingManager.StartListeningForBinding(title);

                // Event handler for setting the binding
                Action<string, string>? bindingSetHandler = null;
                bindingSetHandler = (bindingId, key) =>
                {
                    if (bindingId == title)
                    {
                        keyChanger.KeyNotifier.Content = key;
                        Dictionary.bindingSettings[bindingId] = key;
                        bindingManager.OnBindingSet -= bindingSetHandler; // Unsubscribe after setting

                        keyChanger.KeyNotifier.Content = KeybindNameManager.ConvertToRegularKey(key);
                    }
                };

                bindingManager.OnBindingSet += bindingSetHandler;
            };

            return keyChanger;
        }

        // All Keybind Listening is moved to a seperate function because having it stored in "AddKeyChanger" was making these functions run several times.
        // Nori

        private static AColorChanger AddColorChanger(StackPanel panel, string title)
        {
            var colorChanger = new AColorChanger(title);
            colorChanger.ColorChangingBorder.Background = (Brush)new BrushConverter().ConvertFromString(Dictionary.colorState[title]);
            panel.Children.Add(colorChanger);
            return colorChanger;
        }

        private static ASlider AddSlider(StackPanel panel, string title, string label, double frequency, double buttonsteps, double min, double max, bool For_Anti_Recoil = false)
        {
            var slider = new ASlider(title, label, buttonsteps)
            {
                Slider = { Minimum = min, Maximum = max, TickFrequency = frequency }
            };

            // Determine the correct settings dictionary based on the slider type
            var settings = For_Anti_Recoil ? Dictionary.AntiRecoilSettings : Dictionary.sliderSettings;
            slider.Slider.Value = settings.TryGetValue(title, out var value) ? value : min;

            // Update the settings when the slider value changes
            slider.Slider.ValueChanged += (s, e) => settings[title] = slider.Slider.Value;

            panel.Children.Add(slider);
            return slider;
        }

        private static ADropdown AddDropdown(StackPanel panel, string title)
        {
            var dropdown = new ADropdown(title, title);
            panel.Children.Add(dropdown);
            return dropdown;
        }

        private static AFileLocator AddFileLocator(StackPanel panel, string title, string filter = "All files (*.*)|*.*", string DLExtension = "")
        {
            var afilelocator = new AFileLocator(title, title, filter, DLExtension);
            panel.Children.Add(afilelocator);
            return afilelocator;
        }

        private ComboBoxItem AddDropdownItem(ADropdown dropdown, string title)
        {
            var dropdownitem = new ComboBoxItem();
            dropdownitem.Content = title;
            dropdownitem.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
            dropdownitem.FontFamily = TryFindResource("Atkinson Hyperlegible") as FontFamily;

            dropdownitem.Selected += (s, e) =>
            {
                string? key = dropdown.DropdownTitle.Content?.ToString();
                if (key != null) Dictionary.dropdownState[key] = title;
                else throw new NullReferenceException("dropdown.DropdownTitle.Content.ToString() is null");
            };

            dropdown.DropdownBox.Items.Add(dropdownitem);
            return dropdownitem;
        }

        private static ATitle AddTitle(StackPanel panel, string title, bool CanMinimize = false)
        {
            var titleControl = new ATitle(title, CanMinimize);
            panel.Children.Add(titleControl);
            return titleControl;
        }

        private static APButton AddButton(StackPanel panel, string title)
        {
            var button = new APButton(title);
            panel.Children.Add(button);
            return button;
        }

        private static void AddCredit(StackPanel panel, string name, string role)
        {
            var credit = new ACredit
            {
                Title = name,
                Description = role
            };
            panel.Children.Add(credit);
        }

        private static void AddSeparator(StackPanel panel)
        {
            panel.Children.Add(new ARectangleBottom());
            panel.Children.Add(new ASpacer());
        }

        #endregion Menu Logic

        #region Menu Loading
        private async Task LoadAimMenuAsync()
        {
            #region Aim Assist

            uiManager.AT_Aim = AddTitle(AimAssist, "Aim Assist", true);
            await Task.Delay(1);
            uiManager.T_AimAligner = AddToggle(AimAssist, "Aim Assist");
            uiManager.T_AimAligner.Reader.Click += (s, e) =>
            {
                if (Dictionary.toggleState["Aim Assist"] && Dictionary.lastLoadedModel == "N/A")
                {
                    Dictionary.toggleState["Aim Assist"] = false;
                    UpdateToggleUI(uiManager.T_AimAligner, false);

                    new NoticeBar("Please load a model first", 5000).Show();
                }
            };
            await Task.Delay(1);
            uiManager.C_Keybind = AddKeyChanger(AimAssist, "Aim Keybind", Dictionary.bindingSettings["Aim Keybind"]);
            await Task.Delay(1);
            uiManager.T_ConstantAITracking = AddToggle(AimAssist, "Constant AI Tracking");
            uiManager.T_ConstantAITracking.Reader.Click += (s, e) =>
            {
                if (Dictionary.toggleState["Constant AI Tracking"] && Dictionary.lastLoadedModel == "N/A")
                {
                    Dictionary.toggleState["Constant AI Tracking"] = false;
                    UpdateToggleUI(uiManager.T_ConstantAITracking, false);
                }
                else if (Dictionary.toggleState["Constant AI Tracking"])
                {
                    Dictionary.toggleState["Aim Assist"] = true;
                    UpdateToggleUI(uiManager.T_AimAligner, true);
                }
            };
            await Task.Delay(1);
            uiManager.T_EMASmoothing = AddToggle(AimAssist, "EMA Smoothening");
            await Task.Delay(1);
            AddSeparator(AimAssist);
            await Task.Delay(1);

            #endregion Aim Assist

            #region Aim Config

            uiManager.AT_AimConfig = AddTitle(AimConfig, "Aim Config", true);
            await Task.Delay(1);

            uiManager.D_DetectionAreaType = AddDropdown(AimConfig, "Detection Area Type");
            await Task.Delay(1);
            uiManager.DDI_ClosestToCenterScreen = AddDropdownItem(uiManager.D_DetectionAreaType, "Closest to Center Screen");
            uiManager.DDI_ClosestToCenterScreen.Selected += async (sender, e) =>
            {
                await Task.Delay(100);
                await System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                    FOVWindow.FOVStrictEnclosure.Margin = new Thickness(
                    Convert.ToInt16((WinAPICaller.ScreenWidth / 2) / WinAPICaller.scalingFactorX) - 320,
                    Convert.ToInt16((WinAPICaller.ScreenHeight / 2) / WinAPICaller.scalingFactorY) - 320,
                0, 0));
            };
            await Task.Delay(1);

            AddDropdownItem(uiManager.D_DetectionAreaType, "Closest to Mouse");
            await Task.Delay(1);

            uiManager.D_AimingBoundariesAlignment = AddDropdown(AimConfig, "Aiming Boundaries Alignment");
            await Task.Delay(1);

            AddDropdownItem(uiManager.D_AimingBoundariesAlignment, "Center");
            await Task.Delay(1);
            AddDropdownItem(uiManager.D_AimingBoundariesAlignment, "Top");
            await Task.Delay(1);
            AddDropdownItem(uiManager.D_AimingBoundariesAlignment, "Bottom");
            await Task.Delay(1);

            uiManager.S_MouseSensitivity = AddSlider(AimConfig, "Mouse Sensitivity (+/-)", "Sensitivity", 0.01, 0.01, 0.01, 1);
            uiManager.S_MouseSensitivity.Slider.PreviewMouseLeftButtonUp += (sender, e) =>
            {
                if (uiManager.S_MouseSensitivity.Slider.Value >= 0.98) new NoticeBar("The Mouse Sensitivity you have set can cause Aimmy to be unable to aim, please decrease if you suffer from this problem", 10000).Show();
                else if (uiManager.S_MouseSensitivity.Slider.Value <= 0.1) new NoticeBar("The Mouse Sensitivity you have set can cause Aimmy to be unstable to aim, please increase if you suffer from this problem", 10000).Show();
            };
            await Task.Delay(1);

            uiManager.S_XOffset = AddSlider(AimConfig, "X Offset (Left/Right)", "Offset", 1, 1, -150, 150);
            await Task.Delay(1);
            uiManager.S_XOffsetPercent = AddSlider(AimConfig, "X Offset (%)", "Percent", 1, 1, 0, 100);
            await Task.Delay(1);

            uiManager.S_YOffset = AddSlider(AimConfig, "Y Offset (Up/Down)", "Offset", 1, 1, -150, 150);
            await Task.Delay(1);
            uiManager.S_YOffsetPercent = AddSlider(AimConfig, "Y Offset (%)", "Percent", 1, 1, 0, 100);
            await Task.Delay(1);

            uiManager.S_AIMinimumConfidence = AddSlider(AimConfig, "AI Minimum Confidence", "% Confidence", 1, 1, 1, 100);
            uiManager.S_AIMinimumConfidence.Slider.PreviewMouseLeftButtonUp += (sender, e) =>
            {
                if (uiManager.S_AIMinimumConfidence.Slider.Value >= 95) new NoticeBar("The minimum confidence you have set for Aimmy to be too high and may be unable to detect players.", 10000).Show();
                else if (uiManager.S_AIMinimumConfidence.Slider.Value <= 35) new NoticeBar("The minimum confidence you have set for Aimmy may be too low can cause false positives.", 10000).Show();
            };
            await Task.Delay(1);

            uiManager.T_BezierCurve = AddToggle(AimConfig, "Bezier Curve");
            uiManager.T_BezierCurve.Reader.Click += (s, e) =>
            {
                bool isEnabled = Dictionary.toggleState["Bezier Curve"];
                uiManager.S_BezierStrength.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
                uiManager.S_BezierSteps.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
            };
            await Task.Delay(1);

            uiManager.S_EMASmoothing = AddSlider(AimConfig, "EMA Smoothening", "Amount", 0.01, 0.01, 0.01, 1);
            uiManager.S_EMASmoothing.Visibility = Dictionary.toggleState["EMA Smoothening"] ? Visibility.Visible : Visibility.Collapsed;
            await Task.Delay(1);

            uiManager.S_BezierStrength = AddSlider(
             AimConfig,
             "Bezier Strength",
             "% Curve",
             1,    // tick every 1%
             1,    // arrow-button step
             0,    // min
             100   // max
         );
            await Task.Delay(1);

            uiManager.S_BezierSteps = AddSlider(
                AimConfig,
                "Bezier Steps",
                "Steps",
                1,    // tick every 1 step
                1,    // arrow-button step
                1,    // min 1
                50    // max 50
            );
            await Task.Delay(1);

            bool isBezierEnabled = Dictionary.toggleState["Bezier Curve"];
            uiManager.S_BezierStrength.Visibility = isBezierEnabled ? Visibility.Visible : Visibility.Collapsed;
            uiManager.S_BezierSteps.Visibility = isBezierEnabled ? Visibility.Visible : Visibility.Collapsed;

            uiManager.T_WindMouse = AddToggle(AimConfig, "WindMouse");
            await Task.Delay(1);
            uiManager.S_WindMouseGravity = AddSlider(AimConfig, "WindMouse Gravity", "Gravity", 1, 1, 1, 50);
            await Task.Delay(1);
            uiManager.S_WindMouseWindStrength = AddSlider(AimConfig, "WindMouse Wind Strength", "Strength", 1, 1, 1, 50);
            await Task.Delay(1);
            uiManager.S_WindMouseTargetJitter = AddSlider(AimConfig, "WindMouse Target Jitter", "Jitter", 1, 1, 0, 20);
            await Task.Delay(1);

            bool isWindMouseEnabled = Dictionary.toggleState["WindMouse"];
            uiManager.S_WindMouseGravity.Visibility = isWindMouseEnabled ? Visibility.Visible : Visibility.Collapsed;
            uiManager.S_WindMouseWindStrength.Visibility = isWindMouseEnabled ? Visibility.Visible : Visibility.Collapsed;
            uiManager.S_WindMouseTargetJitter.Visibility = isWindMouseEnabled ? Visibility.Visible : Visibility.Collapsed;

            AddSeparator(AimConfig);
            await Task.Delay(1);

            #endregion Config

            #region Trigger Bot

            uiManager.AT_TriggerBot = AddTitle(TriggerBot, "Auto Trigger", true);
            await Task.Delay(1);
            uiManager.T_AutoTrigger = AddToggle(TriggerBot, "Auto Trigger");
            await Task.Delay(1);
            uiManager.S_AutoTriggerDelay = AddSlider(TriggerBot, "Auto Trigger Delay", "Seconds", 0.01, 0.1, 0.01, 1);
            await Task.Delay(1);
            AddSeparator(TriggerBot);
            await Task.Delay(1);

            #endregion Trigger Bot

            #region FOV Config

            uiManager.AT_FOV = AddTitle(FOVConfig, "FOV Config", true);
            await Task.Delay(1);
            uiManager.T_FOV = AddToggle(FOVConfig, "FOV");
            await Task.Delay(1);
            uiManager.CC_FOVColor = AddColorChanger(FOVConfig, "FOV Color");
            uiManager.CC_FOVColor.ColorChangingBorder.Background = (Brush)new BrushConverter().ConvertFromString(Dictionary.colorState["FOV Color"]);
            uiManager.CC_FOVColor.Reader.Click += (s, x) =>
            {
                System.Windows.Forms.ColorDialog colorDialog = new();
                if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    uiManager.CC_FOVColor.ColorChangingBorder.Background = new SolidColorBrush(Color.FromArgb(colorDialog.Color.A, colorDialog.Color.R, colorDialog.Color.G, colorDialog.Color.B));
                    Dictionary.colorState["FOV Color"] = Color.FromArgb(colorDialog.Color.A, colorDialog.Color.R, colorDialog.Color.G, colorDialog.Color.B).ToString();
                    PropertyChanger.PostColor(Color.FromArgb(colorDialog.Color.A, colorDialog.Color.R, colorDialog.Color.G, colorDialog.Color.B));
                }
            };
            await Task.Delay(1);

            uiManager.S_FOVSize = AddSlider(FOVConfig, "FOV Size", "Size", 1, 1, 10, 640);
            uiManager.S_FOVSize.Slider.ValueChanged += (s, x) =>
            {
                double FovSize = uiManager.S_FOVSize.Slider.Value;
                ActualFOV = FovSize;
                PropertyChanger.PostNewFOVSize(ActualFOV);
            };
            await Task.Delay(1);
            uiManager.S_EMASmoothing.Slider.ValueChanged += (s, x) =>
            {
                if (Dictionary.toggleState["EMA Smoothening"])
                {
                    MouseManager.smoothingFactor = uiManager.S_EMASmoothing.Slider.Value;
                    //Debug.WriteLine(MouseManager.smoothingFactor);
                }
            };
            AddSeparator(FOVConfig);
            await Task.Delay(1);

            #endregion FOV Config

            #region ESP Config

            uiManager.AT_DetectedPlayer = AddTitle(ESPConfig, "ESP Config", true);
            await Task.Delay(1);
            uiManager.T_ShowDetectedPlayer = AddToggle(ESPConfig, "Show Detected Player");
            await Task.Delay(1);
            uiManager.T_ShowAIConfidence = AddToggle(ESPConfig, "Show AI Confidence");
            await Task.Delay(1);
            uiManager.T_ShowTracers = AddToggle(ESPConfig, "Show Tracers");
            await Task.Delay(1);
            uiManager.T_ShowFPS = AddToggle(ESPConfig, "Show FPS");
            await Task.Delay(1);
            uiManager.CC_DetectedPlayerColor = AddColorChanger(ESPConfig, "Detected Player Color");
            uiManager.CC_DetectedPlayerColor.ColorChangingBorder.Background = (Brush)new BrushConverter().ConvertFromString(Dictionary.colorState["Detected Player Color"]);
            uiManager.CC_DetectedPlayerColor.Reader.Click += (s, x) =>
            {
                System.Windows.Forms.ColorDialog colorDialog = new();
                if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    uiManager.CC_DetectedPlayerColor.ColorChangingBorder.Background = new SolidColorBrush(Color.FromArgb(colorDialog.Color.A, colorDialog.Color.R, colorDialog.Color.G, colorDialog.Color.B));
                    Dictionary.colorState["Detected Player Color"] = Color.FromArgb(colorDialog.Color.A, colorDialog.Color.R, colorDialog.Color.G, colorDialog.Color.B).ToString();
                    PropertyChanger.PostDPColor(Color.FromArgb(colorDialog.Color.A, colorDialog.Color.R, colorDialog.Color.G, colorDialog.Color.B));
                }
            };
            await Task.Delay(1);

            uiManager.S_DPFontSize = AddSlider(ESPConfig, "AI Confidence Font Size", "Size", 1, 1, 1, 30);
            uiManager.S_DPFontSize.Slider.ValueChanged += (s, x) => PropertyChanger.PostDPFontSize((int)uiManager.S_DPFontSize.Slider.Value);
            await Task.Delay(1);

            uiManager.S_DPCornerRadius = AddSlider(ESPConfig, "Corner Radius", "Radius", 1, 1, 0, 100);
            uiManager.S_DPCornerRadius.Slider.ValueChanged += (s, x) => PropertyChanger.PostDPWCornerRadius((int)uiManager.S_DPCornerRadius.Slider.Value);
            await Task.Delay(1);

            uiManager.S_DPBorderThickness = AddSlider(ESPConfig, "Border Thickness", "Thickness", 0.1, 1, 0.1, 10);
            uiManager.S_DPBorderThickness.Slider.ValueChanged += (s, x) => PropertyChanger.PostDPWBorderThickness(uiManager.S_DPBorderThickness.Slider.Value);
            await Task.Delay(1);

            uiManager.S_DPOpacity = AddSlider(ESPConfig, "Opacity", "Opacity", 0.1, 0.1, 0, 1);
            await Task.Delay(1);

            AddSeparator(ESPConfig);
            await Task.Delay(1);

            #endregion ESP Config
        }

        private async Task LoadSettingsMenuAsync()
        {
            uiManager.AT_SettingsMenu = AddTitle(SettingsConfig, "Settings Menu", true);
            await Task.Delay(1);

            uiManager.D_MouseMovementMethod = AddDropdown(SettingsConfig, "Mouse Movement Method");
            await Task.Delay(1);
            AddDropdownItem(uiManager.D_MouseMovementMethod, "Mouse Event");
            await Task.Delay(1);
            AddDropdownItem(uiManager.D_MouseMovementMethod, "SendInput");
            await Task.Delay(1);
            uiManager.DDI_LGHUB = AddDropdownItem(uiManager.D_MouseMovementMethod, "LG HUB");
            await Task.Delay(1);

            uiManager.DDI_LGHUB.Selected += (sender, e) =>
            {
                if (!new LGHubMain().Load())
                {
                    SelectMouseEvent();
                }
            };

            uiManager.DDI_RazerSynapse = AddDropdownItem(uiManager.D_MouseMovementMethod, "Razer Synapse (Requires Razer Peripheral)");
            await Task.Delay(1);
            uiManager.DDI_RazerSynapse.Selected += async (sender, e) =>
            {
                if (!await RZMouse.Load())
                {
                    SelectMouseEvent();
                }
            };

            uiManager.D_ExecutionProvider = AddDropdown(SettingsConfig, "Execution Provider Type");
            await Task.Delay(1);
            uiManager.DDI_CUDA = AddDropdownItem(uiManager.D_ExecutionProvider, "CUDA");
            await Task.Delay(1);
            uiManager.DDI_TensorRT = AddDropdownItem(uiManager.D_ExecutionProvider, "TensorRT");
            await Task.Delay(1);

            //uiManager.D_MonitorSelection = AddDropdown(SettingsConfig, "Monitor Selection");

            uiManager.D_ScreenCaptureMethod = AddDropdown(SettingsConfig, "Screen Capture Method");
            await Task.Delay(1);
            AddDropdownItem(uiManager.D_ScreenCaptureMethod, "DirectX");
            await Task.Delay(1);

            AddDropdownItem(uiManager.D_ScreenCaptureMethod, "GDI");
            await Task.Delay(1);

            uiManager.T_CollectDataWhilePlaying = AddToggle(SettingsConfig, "Collect Data While Playing");
            await Task.Delay(1);
            uiManager.T_AutoLabelData = AddToggle(SettingsConfig, "Auto Label Data");
            await Task.Delay(1);

            uiManager.T_MouseBackgroundEffect = AddToggle(SettingsConfig, "Mouse Background Effect");
            await Task.Delay(1);
            uiManager.T_UITopMost = AddToggle(SettingsConfig, "UI TopMost");
            await Task.Delay(1);
            uiManager.T_DebugMode = AddToggle(SettingsConfig, "Debug Mode");
            await Task.Delay(1);
            uiManager.B_SaveConfig = AddButton(SettingsConfig, "Save Config");
            uiManager.B_SaveConfig.Reader.Click += (s, e) => new ConfigSaver().ShowDialog();
            await Task.Delay(1);

            AddSeparator(SettingsConfig);
            await Task.Delay(1);

            uiManager.AT_XYPercentageAdjustmentEnabler = AddTitle(XYPercentageEnablerMenu, "X/Y Percentage Adjustment", true);
            await Task.Delay(1);
            uiManager.T_XAxisPercentageAdjustment = AddToggle(XYPercentageEnablerMenu, "X Axis Percentage Adjustment");
            await Task.Delay(1);
            uiManager.T_YAxisPercentageAdjustment = AddToggle(XYPercentageEnablerMenu, "Y Axis Percentage Adjustment");
            await Task.Delay(1);

            uiManager.T_XAxisPercentageAdjustment.Reader.Click += (s, e) => VisibilityXY();
            uiManager.T_YAxisPercentageAdjustment.Reader.Click += (s, e) => VisibilityXY();

            AddSeparator(XYPercentageEnablerMenu);
            await Task.Delay(1);

            LoadSettingsDropdownStates();
            uiManager.DDI_TensorRT.Selected += OnExecutionProviderSelected;
            uiManager.DDI_CUDA.Selected += OnExecutionProviderSelected;
        }

        private async Task LoadCreditsMenuAsync()
        {
            AddTitle(CreditsPanel, "Developers");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "Booby", "This Entire Repo");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "Taylor", "Original Repo");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "Babyhamsta", "AI Logic");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "MarsQQ - #1 OF Model", "Design");
            await Task.Delay(1);
            AddSeparator(CreditsPanel);
            await Task.Delay(1);

            AddTitle(CreditsPanel, "Contributors");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "whoswhip", "Bug fixes & EMA");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "HakaCat", "Idea for Auto Labelling Data");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "Themida - Sexy Man", "LGHub check");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "Ninja - Booby", "MarsQQ's emotional support");
            await Task.Delay(1);
            AddSeparator(CreditsPanel);
            await Task.Delay(1);

            AddTitle(CreditsPanel, "Model Creators");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "Babyhamsta", "UniversalV4, Phantom Forces");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "Natdog400", "AIO V2, V7");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "Themida - Sexy Man", "Arsenal, Strucid, Bad Business, Blade Ball, etc.");
            await Task.Delay(1);
            AddCredit(CreditsPanel, "Hogthewog", "Da Hood, FN, etc.");
            await Task.Delay(1);
            AddSeparator(CreditsPanel);
            await Task.Delay(1);
        }

        private void DownloadGateway(StackPanel Scroller, HashSet<string> entries, string folder)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Scroller.Children.Clear();

                if (entries.Count > 0)
                {
                    foreach (var entry in entries)
                    {
                        ADownloadGateway gateway = new(entry, folder);
                        Scroller.Children.Add(gateway);
                    }
                }
                else
                {
                    LackOfConfigsText.Visibility = Visibility.Visible;
                    LackOfModelsText.Visibility = Visibility.Visible;
                }
            });
        }

        #endregion Menu Loading

        #region Menu Minizations
        private void VisibilityXY()
        {
            bool isMenuMinimized = Dictionary.minimizeState["Aim Config"];

            bool xPercentageAdjustment = Dictionary.toggleState["X Axis Percentage Adjustment"];
            bool yPercentageAdjustment = Dictionary.toggleState["Y Axis Percentage Adjustment"];

            if (uiManager?.S_XOffset != null && uiManager?.S_XOffsetPercent != null)
            {
                if (!isMenuMinimized)
                {
                    uiManager.S_XOffset.Visibility = xPercentageAdjustment ? Visibility.Collapsed : Visibility.Visible;
                    uiManager.S_XOffsetPercent.Visibility = xPercentageAdjustment ? Visibility.Visible : Visibility.Collapsed;
                }
            }


            if (uiManager?.S_YOffset != null && uiManager?.S_YOffsetPercent != null)
            {
                if (!isMenuMinimized)
                {
                    uiManager.S_YOffset.Visibility = yPercentageAdjustment ? Visibility.Collapsed : Visibility.Visible;
                    uiManager.S_YOffsetPercent.Visibility = yPercentageAdjustment ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private void ToggleAimMenu() => SetMenuVisibility(AimAssist, !Dictionary.minimizeState["Aim Assist"]);


        private void ToggleAimConfig()
        {
            SetMenuVisibility(AimConfig, !Dictionary.minimizeState["Aim Config"]);
            VisibilityXY();
        }

        private void ToggleAutoTrigger() => SetMenuVisibility(TriggerBot, !Dictionary.minimizeState["Auto Trigger"]);

        private void ToggleFOVConfigMenu() => SetMenuVisibility(FOVConfig, !Dictionary.minimizeState["FOV Config"]);

        private void ToggleESPConfigMenu() => SetMenuVisibility(ESPConfig, !Dictionary.minimizeState["ESP Config"]);

        private void ToggleSettingsMenu() => SetMenuVisibility(SettingsConfig, !Dictionary.minimizeState["Settings Menu"]);

        private void ToggleXYPercentageAdjustmentEnabler() => SetMenuVisibility(XYPercentageEnablerMenu, !Dictionary.minimizeState["X/Y Percentage Adjustment"]);

        private void LoadMenuMinimizers()
        {
            ToggleAimMenu();
            ToggleAimConfig();
            ToggleAutoTrigger();
            ToggleFOVConfigMenu();
            ToggleESPConfigMenu();
            ToggleSettingsMenu();
            ToggleXYPercentageAdjustmentEnabler();

            uiManager.AT_Aim.Minimize.Click += (s, e) => ToggleAimMenu();

            uiManager.AT_AimConfig.Minimize.Click += (s, e) => ToggleAimConfig();

            uiManager.AT_TriggerBot.Minimize.Click += (s, e) => ToggleAutoTrigger();

            uiManager.AT_FOV.Minimize.Click += (s, e) => ToggleFOVConfigMenu();

            uiManager.AT_DetectedPlayer.Minimize.Click += (s, e) => ToggleESPConfigMenu();

            uiManager.AT_SettingsMenu.Minimize.Click += (s, e) => ToggleSettingsMenu();

            uiManager.AT_XYPercentageAdjustmentEnabler.Minimize.Click += (s, e) => ToggleXYPercentageAdjustmentEnabler();
        }

        private static void SetMenuVisibility(StackPanel panel, bool isVisible)
        {
            foreach (UIElement child in panel.Children)
            {
                if (!(child is ATitle || child is ASpacer || child is ARectangleBottom))
                {
                    child.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    child.Visibility = Visibility.Visible;
                }
            }

        }

        #endregion Menu Minizations

        #region Open Folder

        private void OpenFolderB_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button clickedButton && clickedButton.Tag != null)
            {
                string folderPath = Path.Combine(Directory.GetCurrentDirectory(), "bin", clickedButton.Tag.ToString());
                if (Directory.Exists(folderPath))
                {
                    Process.Start("explorer.exe", folderPath);
                }
                else
                {
                    MessageBox.Show($"The folder '{folderPath}' does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion Open Folder

        #region Menu Functions

        private async void SelectMouseEvent()
        {
            await Task.Delay(500);
            uiManager.D_MouseMovementMethod!.DropdownBox.SelectedIndex = 0;
        }



        #endregion Menu Functions

        #region System Information

        private static string? GetProcessorName() => GetSpecs.GetSpecification("Win32_Processor", "Name");

        private static string? GetVideoControllerName() => GetSpecs.GetSpecification("Win32_VideoController", "Name");

        private static string? GetFormattedMemorySize()
        {
            long totalMemorySize = long.Parse(GetSpecs.GetSpecification("CIM_OperatingSystem", "TotalVisibleMemorySize")!);
            return Math.Round(totalMemorySize / (1024.0 * 1024.0), 0).ToString();
        }

        #endregion System Information

        #region Fancy UI Calculations

        private double currentGradientAngle = 0;

        private double CalculateAngleDifference(double targetAngle, double fullCircle, double halfCircle, double clamp)
        {
            double angleDifference = (targetAngle - currentGradientAngle + fullCircle) % fullCircle;
            if (angleDifference > halfCircle) { angleDifference -= fullCircle; }
            return Math.Max(Math.Min(angleDifference, clamp), -clamp);
        }

        #endregion Fancy UI Calculations

        #region Window Handling

        private static void ShowHideDPWindow()
        {
            if (!Dictionary.toggleState["Show Detected Player"]) { DPWindow.Hide(); }
            else { DPWindow.Show(); }
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            UpdateManager updateManager = new UpdateManager();
            await updateManager.CheckForUpdate("v2.2.0");
            updateManager.Dispose();
        }

        #endregion Window Handling

    }
}