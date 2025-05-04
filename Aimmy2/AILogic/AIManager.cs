using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Aimmy2.Class;
using Aimmy2.InputLogic;
using Aimmy2.Other;
using Aimmy2.WinformsReplacement;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Win32;
using SharpGen.Runtime;
using Supercluster.KDTree;
using Visuality;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Aimmy2.AILogic
{
    internal class AIManager : IDisposable
    {
        #region Variables

        private const int IMAGE_SIZE = 640;
        private const int NUM_DETECTIONS = 8400;

        private DateTime lastSavedTime = DateTime.MinValue;
        private List<string>? _outputNames;
        private RectangleF LastDetectionBox;

        // Preallocate once for IMAGE_SIZE×IMAGE_SIZE RGB input
        private readonly float[] _inputBuffer = new float[3 * IMAGE_SIZE * IMAGE_SIZE];
        private readonly DenseTensor<float> _inputTensor;
        private readonly List<NamedOnnxValue> _inputContainer;


        private ID3D11Device _device;
        private ID3D11DeviceContext _context;
        private IDXGIOutputDuplication _outputDuplication;
        private ID3D11Texture2D _desktopImage;

        private Bitmap? _captureBitmap;

        private int ScreenWidth = WinAPICaller.ScreenWidth;
        private int ScreenHeight = WinAPICaller.ScreenHeight;

        private readonly RunOptions? _modeloptions;
        private InferenceSession? _onnxModel;

        private Thread? _aiLoopThread;
        private bool _isAiLoopRunning;

        private const int MAXSAMPLES = 100;
        private double[] frameTimes = new double[MAXSAMPLES];
        private int frameTimeIndex = 0;
        private double totalFrameTime = 0;

        private double CenterXTranslated = 0;
        private double CenterYTranslated = 0;

        private int iterationCount = 0;
        private long totalTime = 0;

        private int detectedX { get; set; }
        private int detectedY { get; set; }

        public double AIConf = 0;
        private static int targetX, targetY;

        #endregion Variables

        public AIManager(string modelPath)
        {
            _modeloptions = new RunOptions();

            var sessionOptions = new SessionOptions
            {
                EnableCpuMemArena = true,
                EnableMemoryPattern = true,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_PARALLEL,
                IntraOpNumThreads = Environment.ProcessorCount
            };
            _inputTensor = new DenseTensor<float>(_inputBuffer, new[] { 1, 3, IMAGE_SIZE, IMAGE_SIZE });
            _inputContainer = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("images", _inputTensor)
    };

            SystemEvents.DisplaySettingsChanged += (s, e) =>
            {
                if (Dictionary.dropdownState["Screen Capture Method"] == "DirectX")
                {
                    ReinitializeD3D11();
                }
                else
                {
                    _captureBitmap?.Dispose();
                    _captureBitmap = null;
                }
            };

            // Attempt to load via CUDA (else fallback to CPU)
            Task.Run(() =>
            {
                _ = InitializeModel(sessionOptions, modelPath);
            });

            if (Dictionary.dropdownState["Screen Capture Method"] == "DirectX")
            {
                InitializeDirectX();
            }
        }
        #region DirectX
        private void InitializeDirectX()
        {
            try
            {
                DisposeD311();

                // Initialize Direct3D11 device and context
                FeatureLevel[] featureLevels = new[]
                   {
                        FeatureLevel.Level_12_1,
                        FeatureLevel.Level_12_0,
                        FeatureLevel.Level_11_1,
                        FeatureLevel.Level_11_0,
                        FeatureLevel.Level_10_1,
                        FeatureLevel.Level_10_0,
                        FeatureLevel.Level_9_3,
                        FeatureLevel.Level_9_2,
                        FeatureLevel.Level_9_1
                    };
                var result = D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    featureLevels,
                    out _device,
                    out _context
                );
                if (result != Result.Ok || _device == null || _context == null)
                {
                    throw new InvalidOperationException($"Failed to create Direct3D11 device or context. HRESULT: {result}");
                }

                using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
                using var adapterForOutput = dxgiDevice.GetAdapter();
                var resultEnum = adapterForOutput.EnumOutputs(0, out var outputTemp);
                if (resultEnum != Result.Ok || outputTemp == null)
                {
                    throw new InvalidOperationException("Failed to enumerate outputs.");
                }

                using var output = outputTemp.QueryInterface<IDXGIOutput1>() ?? throw new InvalidOperationException("Failed to acquire IDXGIOutput1.");

                _outputDuplication = output.DuplicateOutput(_device);

                FileManager.LogInfo("Direct3D11 device, context, and output duplication initialized.");
            }
            catch (Exception ex)
            {
                FileManager.LogError("Error initializing Direct3D11: " + ex);
            }
        }

        #endregion
        #region Models

        private async Task InitializeModel(SessionOptions sessionOptions, string modelPath)
        {
            string useCuda = Dictionary.dropdownState["Execution Provider Type"];
            try
            {
                await LoadModelAsync(sessionOptions, modelPath, useCUDA: useCuda == "CUDA").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    FileManager.LogError($"Error starting the model via alternative method: {ex.Message}\n\nFalling back to CUDA, performance may be poor.", true);
                    await LoadModelAsync(sessionOptions, modelPath, useCUDA: false).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    FileManager.LogError($"Error starting the model via alternative method: {e.Message}, you won't be able to use aim assist at all.", true);
                }
            }
            finally
            {
                FileManager.CurrentlyLoadingModel = false;
            }
        }

        private Task LoadModelAsync(SessionOptions sessionOptions, string modelPath, bool useCUDA)
        {
            try
            {
                if (useCUDA)
                {
                    FileManager.LogError("loading model with CUDA");
                    sessionOptions.AppendExecutionProvider_CUDA();
                }
                else
                {
                    var tensorrtOptions = new OrtTensorRTProviderOptions();

                    tensorrtOptions.UpdateOptions(new Dictionary<string, string>
                    {
                        { "device_id", "0" },
                        { "trt_fp16_enable", "1" },
                        { "trt_engine_cache_enable", "1" },
                        { "trt_engine_cache_path", "bin/models" }
                    });

                    FileManager.LogError(modelPath + " " + Path.ChangeExtension(modelPath, ".engine"));
                    FileManager.LogError("loading model with tensort");

                    sessionOptions.AppendExecutionProvider_Tensorrt(tensorrtOptions);
                }

                _onnxModel = new InferenceSession(modelPath, sessionOptions);
                _outputNames = new List<string>(_onnxModel.OutputMetadata.Keys);

                FileManager.LogError("successfully loaded model");

                // Validate the onnx model output shape (ensure model is OnnxV8)
                ValidateOnnxShape();
            }
            catch (OnnxRuntimeException ex)
            {
                FileManager.LogError($"ONNXRuntime had an error: {ex}");

                string? message = null;
                string? title = null;

                // just in case
                if (ex.Message.Contains("TensorRT execution provider is not enabled in this build") ||
                    (ex.Message.Contains("LoadLibrary failed with error 126") && ex.Message.Contains("onnxruntime_providers_tensorrt.dll")))
                {
                    if (RequirementsManager.IsTensorRTInstalled())
                    {
                        message = "TensorRT has been found by Aimmy, but not by ONNX. Please check your configuration.\nHint: Check CUDNN and your CUDA, and install dependencies to PATH correctly.";
                        title = "Configuration Error";
                    }
                    else
                    {
                        message = "TensorRT execution provider has not been found on your build. Please check your configuration.\nHint: Download TensorRT 10.3.0 and install the LIB folder to path.";
                        title = "TensorRT Error";
                    }
                }
                else if (ex.Message.Contains("CUDA execution provider is not enabled in this build") ||
                         (ex.Message.Contains("LoadLibrary failed with error 126") && ex.Message.Contains("onnxruntime_providers_cuda.dll")))
                {
                    if (RequirementsManager.IsCUDAInstalled() && RequirementsManager.IsCUDNNInstalled())
                    {
                        message = "CUDA & CUDNN have been found by Aimmy, but not by ONNX. Please check your configuration.\nHint: Check CUDNN and your CUDA installations, path, etc. PATH directories should point directly towards the DLLS.";
                        title = "Configuration Error";
                    }
                    else
                    {
                        message = "CUDA execution provider has not been found on your build. Please check your configuration.\nHint: Download CUDA 12.6. Then install CUDNN 9.3 to your PATH";
                        title = "CUDA Error";
                    }
                }

                if (message != null && title != null)
                {
                    MessageBox.Show(message, title, (MessageBoxButton)MessageBoxButtons.OK, (MessageBoxImage)MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                FileManager.LogError($"Error starting the model: {ex}");
                _onnxModel?.Dispose();
            }

            // Begin the loop
            // Begin the loop
            _isAiLoopRunning = true;
            _aiLoopThread = new Thread(AiLoop);
            // ↓ ensure COM-based backends (LG HUB, etc.) work without crashing:
            _aiLoopThread.SetApartmentState(ApartmentState.STA);
            _aiLoopThread.IsBackground = true;
            _aiLoopThread.Priority = ThreadPriority.AboveNormal;
            _aiLoopThread.Start();


            return Task.CompletedTask;
        }

        private void ValidateOnnxShape()
        {
            var expectedShape = new int[] { 1, 5, NUM_DETECTIONS };
            if (_onnxModel != null)
            {
                var outputMetadata = _onnxModel.OutputMetadata;
                if (!outputMetadata.Values.All(metadata => metadata.Dimensions.SequenceEqual(expectedShape)))
                {
                    FileManager.LogError($"Output shape does not match the expected shape of {string.Join("x", expectedShape)}.\n\nThis model will not work with Aimmy, please use an YOLOv8 model converted to ONNXv8.", true);
                }
            }
        }

        #endregion Models

        #region AI

        private static bool ShouldPredict() => Dictionary.toggleState["Show Detected Player"] || Dictionary.toggleState["Constant AI Tracking"] || InputBindingManager.IsHoldingBinding("Aim Keybind") || InputBindingManager.IsHoldingBinding("Second Aim Keybind");
        private static bool ShouldProcess() => Dictionary.toggleState["Aim Assist"] || Dictionary.toggleState["Show Detected Player"] || Dictionary.toggleState["Auto Trigger"];

        private void UpdateFps(double newFrameTime)
        {
            totalFrameTime += newFrameTime - frameTimes[frameTimeIndex];
            frameTimes[frameTimeIndex] = newFrameTime;

            if (++frameTimeIndex >= MAXSAMPLES)
            {
                frameTimeIndex = 0;
            }
        }

        private async void AiLoop()
        {
            Stopwatch stopwatch = new();
            DetectedPlayerWindow? DetectedPlayerOverlay = Dictionary.DetectedPlayerOverlay;

            stopwatch.Start();
            int fpsUpdateCounter = 0;
            const int fpsUpdateInterval = 10;

            float scaleX = ScreenWidth / (float)IMAGE_SIZE;
            float scaleY = ScreenHeight / (float)IMAGE_SIZE;
            while (_isAiLoopRunning)
            {
                double frameTime = stopwatch.Elapsed.TotalSeconds;
                stopwatch.Restart();

                if (Dictionary.toggleState["Show FPS"])
                {
                    UpdateFps(frameTime);
                    if (++fpsUpdateCounter >= fpsUpdateInterval)
                    {
                        fpsUpdateCounter = 0;
                        _ = Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (DetectedPlayerOverlay != null)
                            {
                                DetectedPlayerOverlay.FpsLabel.Content = $"FPS: {MAXSAMPLES / totalFrameTime:F2}";
                            } // turn on esp, FPS usually is around 160 fps - was 30 fps.
                        });
                    }
                }

                _ = UpdateFOV();

                if (iterationCount == 1000 && Dictionary.toggleState["Debug Mode"])
                {
                    double averageTime = totalTime / 1000.0;
                    FileManager.LogInfo($"Average loop iteration time: {averageTime} ms", true);
                    totalTime = 0;
                    iterationCount = 0;
                }

                if (ShouldProcess() && ShouldPredict())
                {
                    var closestPredictionTask = Task.Run(() => GetClosestPrediction());
                    var closestPrediction = await closestPredictionTask.ConfigureAwait(false);
                    if (closestPrediction == null)
                    {
                        DisableOverlay(DetectedPlayerOverlay!);
                        continue;
                    }

                    _ = AutoTrigger();

                    CalculateCoordinates(DetectedPlayerOverlay!, closestPrediction, scaleX, scaleY);

                    HandleAim(closestPrediction);

                    totalTime += stopwatch.ElapsedMilliseconds;
                    iterationCount++;
                }
            }
            stopwatch.Stop();
        }
        #endregion
        #region AI Loop Functions
        #region misc
        private async Task AutoTrigger()
        {
            if (!Dictionary.toggleState["Auto Trigger"]) return;

            bool isHoldingAimKey = InputBindingManager.IsHoldingBinding("Aim Keybind") || InputBindingManager.IsHoldingBinding("Second Aim Keybind");
            bool shouldTrigger = isHoldingAimKey || Dictionary.toggleState["Constant AI Tracking"];

            if (shouldTrigger)
            {
                await MouseManager.DoTriggerClick();
            }
        }

        private async Task UpdateFOV()
        {
            if (Dictionary.dropdownState["Detection Area Type"] != "Closest to Mouse" || !Dictionary.toggleState["FOV"] || Dictionary.FOVWindow == null) return;

            var mousePosition = WinAPICaller.GetCursorPosition();
            double marginLeft = mousePosition.X / WinAPICaller.scalingFactorX - 320;
            double marginTop = mousePosition.Y / WinAPICaller.scalingFactorY - 320;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Dictionary.FOVWindow.FOVStrictEnclosure.Margin = new Thickness(marginLeft, marginTop, 0, 0);
            });
        }
        #endregion
        #region ESP
        private static void DisableOverlay(DetectedPlayerWindow DetectedPlayerOverlay)
        {
            if (!Dictionary.toggleState["Show Detected Player"] || Dictionary.DetectedPlayerOverlay == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (Dictionary.toggleState["Show AI Confidence"])
                {
                    DetectedPlayerOverlay.DetectedPlayerConfidence.Opacity = 0;
                }
                if (Dictionary.toggleState["Show Tracers"])
                {
                    DetectedPlayerOverlay.DetectedTracers.Opacity = 0;
                }

                DetectedPlayerOverlay.DetectedPlayerFocus.Opacity = 0;
            });
        }

        private void UpdateOverlay(DetectedPlayerWindow detectedPlayerOverlay)
        {
            double scalingFactorX = WinAPICaller.scalingFactorX;
            double scalingFactorY = WinAPICaller.scalingFactorY;

            double centerX = LastDetectionBox.X / scalingFactorX + (LastDetectionBox.Width / 2.0);
            double centerY = LastDetectionBox.Y / scalingFactorY;
            double boxWidth = LastDetectionBox.Width;
            double boxHeight = LastDetectionBox.Height;

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpdateConfidence(detectedPlayerOverlay, centerX, centerY);
                UpdateTracers(detectedPlayerOverlay, centerX, centerY, boxHeight);
                UpdateFocusBox(detectedPlayerOverlay, centerX, centerY, boxWidth, boxHeight);
            });
        }
        private void UpdateConfidence(DetectedPlayerWindow detectedPlayerOverlay, double centerX, double centerY)
        {
            if (!Dictionary.toggleState["Show AI Confidence"])
            {
                detectedPlayerOverlay.DetectedPlayerConfidence.Opacity = 0;
                return;
            }

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                detectedPlayerOverlay.DetectedPlayerConfidence.Opacity = 1;
                detectedPlayerOverlay.DetectedPlayerConfidence.Content = $"{Math.Round((AIConf * 100), 2)}%";

                double labelEstimatedHalfWidth = detectedPlayerOverlay.DetectedPlayerConfidence.ActualWidth / 2;
                detectedPlayerOverlay.DetectedPlayerConfidence.Margin = new Thickness(centerX - labelEstimatedHalfWidth, centerY - detectedPlayerOverlay.DetectedPlayerConfidence.ActualHeight - 2, 0, 0);
            });
        }

        private void UpdateTracers(DetectedPlayerWindow detectedPlayerOverlay, double centerX, double centerY, double boxHeight)
        {
            if (!Dictionary.toggleState["Show Tracers"])
            {
                detectedPlayerOverlay.DetectedTracers.Opacity = 0;
                return;
            }

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                detectedPlayerOverlay.DetectedTracers.Opacity = 1;
                detectedPlayerOverlay.DetectedTracers.X2 = centerX;
                detectedPlayerOverlay.DetectedTracers.Y2 = centerY + boxHeight;
            });
        }

        private void UpdateFocusBox(DetectedPlayerWindow detectedPlayerOverlay, double centerX, double centerY, double boxWidth, double boxHeight)
        {
            detectedPlayerOverlay.DetectedPlayerFocus.Opacity = 1;
            detectedPlayerOverlay.DetectedPlayerFocus.Margin = new Thickness(centerX - (boxWidth / 2.0), centerY, 0, 0);
            detectedPlayerOverlay.DetectedPlayerFocus.Width = boxWidth;
            detectedPlayerOverlay.DetectedPlayerFocus.Height = boxHeight;

            detectedPlayerOverlay.Opacity = Dictionary.sliderSettings["Opacity"];
        }
        #endregion
        #region Coordinates
        private void CalculateCoordinates(DetectedPlayerWindow detectedPlayerOverlay, Prediction closestPrediction, float scaleX, float scaleY)
        {
            // Store latest confidence
            AIConf = closestPrediction.Confidence;

            // If ESP is on, update the overlay—but bail out if Aim Assist is off
            if (Dictionary.toggleState["Show Detected Player"] && Dictionary.DetectedPlayerOverlay != null)
            {
                UpdateOverlay(detectedPlayerOverlay);
                if (!Dictionary.toggleState["Aim Assist"])
                    return;
            }

            // The model’s Rectangle is already in absolute screen pixels
            var rect = closestPrediction.Rectangle;

            // Read user‐configured offsets
            double xOffset = Dictionary.sliderSettings["X Offset (Left/Right)"];
            double yOffset = Dictionary.sliderSettings["Y Offset (Up/Down)"];
            double xOffsetPercent = Dictionary.sliderSettings["X Offset (%)"];
            double yOffsetPercent = Dictionary.sliderSettings["Y Offset (%)"];

            // Compute the target X in screen pixels
            double x;
            if (Dictionary.toggleState["X Axis Percentage Adjustment"])
                x = rect.X + rect.Width * (xOffsetPercent / 100.0) + xOffset;
            else
                x = rect.X + rect.Width / 2.0 + xOffset;

            // Compute the target Y in screen pixels
            double y;
            if (Dictionary.toggleState["Y Axis Percentage Adjustment"])
            {
                // From bottom of box up by a percentage
                y = rect.Y + rect.Height - rect.Height * (yOffsetPercent / 100.0) + yOffset;
            }
            else
            {
                // Anchor at Top, Center, or Bottom of the box
                string alignment = Dictionary.dropdownState["Aiming Boundaries Alignment"];
                double anchorFraction = alignment switch
                {
                    "Center" => 0.5,
                    "Bottom" => 1.0,
                    _ => 0.0   // “Top” or any other value
                };
                y = rect.Y + rect.Height * anchorFraction + yOffset;
            }

            // Round to integer mouse‐move targets
            detectedX = (int)Math.Round(x);
            detectedY = (int)Math.Round(y);
        }

        #endregion
        #region Mouse Movement
        private void HandleAim(Prediction closestPrediction)
        {
            if (!Dictionary.toggleState["Aim Assist"]) return;

            bool isTracking = Dictionary.toggleState["Constant AI Tracking"] ||
                InputBindingManager.IsHoldingBinding("Aim Keybind") ||
                InputBindingManager.IsHoldingBinding("Second Aim Keybind");

            if (!isTracking) return;

            MouseManager.MoveCrosshair(detectedX, detectedY);
        }
        #endregion
        #region Prediction (AI Work)
        private Rectangle ClampRectangle(Rectangle rect, int screenWidth, int screenHeight)
        {
            int x = Math.Max(0, Math.Min(rect.X, screenWidth - rect.Width));
            int y = Math.Max(0, Math.Min(rect.Y, screenHeight - rect.Height));
            int width = Math.Min(rect.Width, screenWidth - x);
            int height = Math.Min(rect.Height, screenHeight - y);

            return new Rectangle(x, y, width, height);
        }


        private Task<Prediction?> GetClosestPrediction(bool useMousePosition = true)
        {
            // ── 1  Define the crop rectangle (640×640 centred on mouse or screen) ──
            var cursor = WinAPICaller.GetCursorPosition();
            targetX = Dictionary.dropdownState["Detection Area Type"] == "Closest to Mouse"
                ? cursor.X
                : ScreenWidth / 2;
            targetY = Dictionary.dropdownState["Detection Area Type"] == "Closest to Mouse"
                ? cursor.Y
                : ScreenHeight / 2;

            var box = new Rectangle(
                targetX - IMAGE_SIZE / 2,
                targetY - IMAGE_SIZE / 2,
                IMAGE_SIZE,
                IMAGE_SIZE
            );
            box = ClampRectangle(box, ScreenWidth, ScreenHeight);

           bool captureOk;
            if (Dictionary.dropdownState["Screen Capture Method"] == "DirectX")
            {
                // D3D11Screen now returns true when the buffer is ready
                captureOk = D3D11Screen(box);
            }
            else
            {
                // GDI path – still returns a Bitmap; convert it
                Bitmap? bmp = GDIScreen(box);
                captureOk = bmp != null;
                if (captureOk)
                    BitmapToFloatArray(bmp!, _inputBuffer);
            }

            if (!captureOk || _onnxModel == null)
                return Task.FromResult<Prediction?>(null);
            // ── 3  Run inference (tensor & container are pre‑allocated) ────────────
            using var results = _onnxModel.Run(_inputContainer, _outputNames, _modeloptions);
            var output = results[0].AsTensor<float>();
            // ── 4  Filter by FOV and confidence, collect surviving boxes ───────────
            float fovSize = (float)Dictionary.sliderSettings["FOV Size"];
            float half = (IMAGE_SIZE - fovSize) / 2;
            float fovMinX = half, fovMaxX = half + fovSize;
            float fovMinY = half, fovMaxY = half + fovSize;

            var (pts, preds) = PrepareKDTreeData(output, box, fovMinX, fovMaxX, fovMinY, fovMaxY);
            if (pts.Count == 0)
                return Task.FromResult<Prediction?>(null);

            // ── 5  Pick the detection closest to the crop centre ───────────────────
            double centre = IMAGE_SIZE / 2.0;
            int bestIdx = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                double dx = pts[i][0] - centre;
                double dy = pts[i][1] - centre;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestDist)
                {
                    bestDist = d2;
                    bestIdx = i;
                }
            }

            var best = preds[bestIdx];

            // ── 6  Translate the rectangle back into absolute screen space ────────
            best.Rectangle = new RectangleF(
                best.Rectangle.X + box.Left,
                best.Rectangle.Y + box.Top,
                best.Rectangle.Width,
                best.Rectangle.Height
            );

            return Task.FromResult<Prediction?>(best);
        }






        private static (List<double[]> points, List<Prediction> preds)
    PrepareKDTreeData(
        Tensor<float> output,
        Rectangle detectionBox,
        float fovMinX, float fovMaxX,
        float fovMinY, float fovMaxY)
        {
            float minConf = (float)Dictionary.sliderSettings["AI Minimum Confidence"] / 100f;
            var points = new List<double[]>(NUM_DETECTIONS);
            var preds = new List<Prediction>(NUM_DETECTIONS);

            for (int i = 0; i < NUM_DETECTIONS; i++)
            {
                float conf = output[0, 4, i];
                if (conf < minConf) continue;

                float xC = output[0, 0, i], yC = output[0, 1, i];
                float w = output[0, 2, i], h = output[0, 3, i];

                float xMin = xC - w / 2, xMax = xC + w / 2;
                float yMin = yC - h / 2, yMax = yC + h / 2;
                if (xMin < fovMinX || xMax > fovMaxX ||
                    yMin < fovMinY || yMax > fovMaxY)
                    continue;

                // <-- FIX: explicitly create a double[] so it matches List<double[]>
                points.Add(new double[] { xC, yC });

                preds.Add(new Prediction
                {
                    Rectangle = new RectangleF(xMin, yMin, w, h),
                    Confidence = conf,
                    CenterXTranslated = (xC - detectionBox.Left) / IMAGE_SIZE,
                    CenterYTranslated = (yC - detectionBox.Top) / IMAGE_SIZE
                });
            }

            return (points, preds);
        }

        #endregion
        #endregion AI Loop Functions

        #region Screen Capture
        public Bitmap? ScreenGrab(Rectangle detectionBox)
        {
            try
            {
                if (Dictionary.dropdownState["Screen Capture Method"] == "DirectX")
                {
                    // Fast DX path fills _inputBuffer; we do not materialise a Bitmap.
                    bool ok = D3D11Screen(detectionBox);
                    return ok ? null : null;        // keeps signature, avoids compile error
                }
                else
                {
                    return GDIScreen(detectionBox); // legacy path
                }
            }
            catch (Exception e)
            {
                FileManager.LogError("Error capturing screen:" + e);
                return null;
            }
        }

        // --- high‑speed capture that fills _inputBuffer in‑place ---------------
        private bool D3D11Screen(Rectangle box)
        {
            try
            {
                if (_device == null || _context == null || _outputDuplication == null)
                    ReinitializeD3D11();

                var hr = _outputDuplication!.AcquireNextFrame(500, out _, out var desktopResource);
                if (hr != Result.Ok) { _outputDuplication.ReleaseFrame(); return false; }

                using var fullTex = desktopResource.QueryInterface<ID3D11Texture2D>();

                // (Re)create staging tex only if size changed
                if (_desktopImage == null ||
                    _desktopImage.Description.Width != box.Width ||
                    _desktopImage.Description.Height != box.Height)
                {
                    _desktopImage?.Dispose();
                    _desktopImage = _device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)box.Width,
                        Height = (uint)box.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        BindFlags = BindFlags.None
                    });
                }

                _context.CopySubresourceRegion(
                    _desktopImage, 0, 0, 0, 0,
                    fullTex, 0,
                    new Box(box.Left, box.Top, 0, box.Right, box.Bottom, 1)
                );

                // Map once, convert directly into _inputBuffer
                var map = _context.Map(_desktopImage, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                ConvertBGRA8ToCHWFloat(map, box.Width, box.Height, _inputBuffer);
                _context.Unmap(_desktopImage, 0);
                _outputDuplication.ReleaseFrame();

                return true;        // buffer is ready
            }
            catch (Exception ex)
            {
                FileManager.LogError("DX11 capture failed: " + ex.Message);
                ReinitializeD3D11();
                return false;
            }
        }




        private Bitmap GDIScreen(Rectangle detectionBox)
        {
            if (detectionBox.Width <= 0 || detectionBox.Height <= 0)
            {
                throw new ArgumentException("Detection box dimensions must be greater than zero. (The enemy is too small)");
            }

            if (_device != null || _context != null || _outputDuplication != null)
            {
                FileManager.LogWarning("D3D11 was not properly disposed, disposing now...", true, 1500);
                DisposeD311();
            }

            if (_captureBitmap == null || _captureBitmap.Width != detectionBox.Width || _captureBitmap.Height != detectionBox.Height)
            {
                _captureBitmap?.Dispose();
                _captureBitmap = new Bitmap(detectionBox.Width, detectionBox.Height, PixelFormat.Format32bppArgb);
            }

            try
            {
                using (var g = Graphics.FromImage(_captureBitmap))
                    g.CopyFromScreen(detectionBox.Left, detectionBox.Top, 0, 0, detectionBox.Size, CopyPixelOperation.SourceCopy);

            }
            catch (Exception ex)
            {
                FileManager.LogError($"Failed to capture screen: {ex.Message}");
                throw;
            }

            return _captureBitmap;
        }



        private void SaveFrame(Bitmap frame, Prediction? DoLabel = null)
        {
            if (!Dictionary.toggleState["Collect Data While Playing"] || Dictionary.toggleState["Constant AI Tracking"] || (DateTime.Now - lastSavedTime).TotalMilliseconds < 500) return;

            lastSavedTime = DateTime.Now;
            string uuid = Guid.NewGuid().ToString();

            string imagePath = Path.Combine("bin", "images", $"{uuid}.jpg");
            frame.Save(imagePath);
            if (Dictionary.toggleState["Auto Label Data"] && DoLabel != null)
            {
                var labelPath = Path.Combine("bin", "labels", $"{uuid}.txt");

                float x = (DoLabel!.Rectangle.X + DoLabel.Rectangle.Width / 2) / frame.Width;
                float y = (DoLabel!.Rectangle.Y + DoLabel.Rectangle.Height / 2) / frame.Height;
                float width = DoLabel.Rectangle.Width / frame.Width;
                float height = DoLabel.Rectangle.Height / frame.Height;

                File.WriteAllText(labelPath, $"0 {x} {y} {width} {height}");
            }
        }

        #region Reinitialization, Clamping, Misc
        public void ReinitializeD3D11()
        {
            try
            {
                DisposeD311();
                InitializeDirectX();
                FileManager.LogError("Reinitializing D3D11, timing out for 1000ms");
                Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                FileManager.LogError("Error during D3D11 reinitialization: " + ex);
            }
        }
        #endregion
        #endregion Screen Capture

        #region bitmap and friends

        public static Func<double[], double[], double> L2Norm_Squared_Double = (x, y) =>
        {
            double dist = 0f;
            for (int i = 0; i < x.Length; i++)
            {
                dist += (x[i] - y[i]) * (x[i] - y[i]);
            }

            return dist;
        };

        public static unsafe void BitmapToFloatArray(Bitmap image, float[] buffer)
        {
            int w = image.Width, h = image.Height, px = w * h;
            var rect = new Rectangle(0, 0, w, h);
            var bmpData = image.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format16bppRgb555);

            byte* ptr = (byte*)bmpData.Scan0;
            float mul = 1f / 31f;
            int stride = bmpData.Stride;

            for (int y = 0; y < h; y++)
            {
                byte* row = ptr + y * stride;
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    ushort pix = (ushort)(row[2 * x] | (row[2 * x + 1] << 8));
                    buffer[idx] = ((pix >> 10) & 0x1F) * mul;
                    buffer[px + idx] = ((pix >> 5) & 0x1F) * mul;
                    buffer[2 * px + idx] = (pix & 0x1F) * mul;
                }
            }

            image.UnlockBits(bmpData);
        }


        #endregion complicated math

        /// <summary>Fast BGRA‑8 → CHW float32 normalised 0‑1.</summary>
        private static unsafe void ConvertBGRA8ToCHWFloat(
            Vortice.Direct3D11.MappedSubresource src, int width, int height, float[] dst)
        {
            byte* pSrc = (byte*)src.DataPointer;
            int pitch = (int)src.RowPitch;
            int px = width * height;

            fixed (float* pDst = dst)
            {
                float* r = pDst;           // [0 ..  px)
                float* g = pDst + px;      // [px .. 2px)
                float* b = pDst + 2 * px;  // [2px.. 3px)

                for (int y = 0; y < height; y++)
                {
                    byte* row = pSrc + y * pitch;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = y * width + x;
                        b[idx] = row[x * 4 + 0] / 255f;   // B
                        g[idx] = row[x * 4 + 1] / 255f;   // G
                        r[idx] = row[x * 4 + 2] / 255f;   // R
                    }
                }
            }
        }





        public void Dispose()
        {
            _isAiLoopRunning = false;
            if (_aiLoopThread != null && _aiLoopThread.IsAlive && !_aiLoopThread.Join(TimeSpan.FromSeconds(1)))
            {
                _aiLoopThread.Interrupt();

            }
            DisposeResources();
        }
        private void DisposeD311()
        {
            _desktopImage?.Dispose();
            _desktopImage = null;

            _outputDuplication?.Dispose();
            _outputDuplication = null;

            _context?.Dispose();
            _context = null;

            _device?.Dispose();
            _device = null;
        }
        private void DisposeResources()
        {
            if (Dictionary.dropdownState["Screen Capture Method"] == "DirectX")
            {
                DisposeD311();
            }
            else
            {
                _captureBitmap?.Dispose();
            }

            _onnxModel?.Dispose();
            _modeloptions?.Dispose();
        }

        public class Prediction
        {
            public RectangleF Rectangle { get; set; }
            public float Confidence { get; set; }
            public float CenterXTranslated { get; set; }
            public float CenterYTranslated { get; set; }
        }
    }
}