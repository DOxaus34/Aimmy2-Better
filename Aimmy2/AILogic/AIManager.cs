using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
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
using Visuality;
using Vortice;
using Vortice.D3DCompiler;
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

        private const int IMAGE_SIZE = 320;
        private const int NUM_DETECTIONS = 2100;

        private List<string>? _outputNames;
        private readonly DenseTensor<float> _inputTensor;
        private readonly List<NamedOnnxValue> _inputContainer;

        // Preallocate once for IMAGE_SIZE×IMAGE_SIZE RGB input
        private readonly float[] _inputBuffer = new float[3 * IMAGE_SIZE * IMAGE_SIZE];

        private ID3D11Device _device;
        private ID3D11DeviceContext _context;
        private IDXGIOutputDuplication _outputDuplication;
        private ID3D11Texture2D _stagingTexture;

        // Compute Shader Resources
        private ID3D11ComputeShader _computeShader;
        private ID3D11Texture2D _computeInputTexture;
        private ID3D11ShaderResourceView _computeInputSRV;
        private ID3D11Buffer _computeOutputBuffer;
        private ID3D11UnorderedAccessView _computeOutputUAV;
        private ID3D11Buffer[] _computeStagingBuffers;
        private int _d3dFrameIndex = 0;

        private bool _computeShaderHasRunOnce = false;
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
        private readonly Stopwatch _uiUpdateTimer = new Stopwatch();
        private const int UI_UPDATE_RATE = 60; // FPS
        private readonly TimeSpan _uiUpdateInterval = TimeSpan.FromSeconds(1.0 / UI_UPDATE_RATE);

        private long totalTime = 0;
        private int iterationCount = 0;
        private int detectedX { get; set; }
        private int detectedY { get; set; }

        public double AIConf = 0;
        private static int targetX, targetY;

        private KalmanFilter? _kalmanFilter;
        private Prediction? _lastPrediction;

        private readonly Prediction _reusablePrediction = new Prediction();

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
                ReinitializeD3D11();
            };

            // Attempt to load via CUDA (else fallback to CPU)
            Task.Run(() =>
            {
                _ = InitializeModel(sessionOptions, modelPath);
            });

            InitializeDirectX();
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
                    FeatureLevel.Level_11_0,
                };
                var result = D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    featureLevels,
                    out _device,
                    out _context
                );
                if (result.Failure)
                {
                    throw new InvalidOperationException($"Failed to create Direct3D11 device or context. HRESULT: {result}");
                }

                // Create the staging texture for CPU-based processing (fallback/debug)
                var stagingDesc = new Texture2DDescription
                {
                    Width = IMAGE_SIZE,
                    Height = IMAGE_SIZE,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                };
                _stagingTexture = _device.CreateTexture2D(stagingDesc);

                // --- Compute Shader Setup ---
                // 1. Load and compile the shader from the source file
                try
                {
                    const string shaderFileName = "AILogic/Convert.hlsl";
                    string shaderSource = File.ReadAllText(shaderFileName);

                    // Compile the shader using the correct method signature.
                    var shaderBytecode = Compiler.Compile(
                        shaderSource,
                        "main",
                        shaderFileName,
                        "cs_5_0",
                        ShaderFlags.OptimizationLevel3
                    );

                    _computeShader = _device.CreateComputeShader(shaderBytecode.Span);
                    FileManager.LogInfo("Compute shader compiled successfully from source.");
                }
                catch (Exception ex)
                {
                    // Catch file not found, SharpGenException from compilation, etc.
                    throw new InvalidOperationException($"Failed to initialize compute shader: {ex.Message}", ex);
                }

                // 2. Create input texture, which we will copy the screen region to
                var computeInputDesc = new Texture2DDescription
                {
                    Width = IMAGE_SIZE,
                    Height = IMAGE_SIZE,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                };
                _computeInputTexture = _device.CreateTexture2D(computeInputDesc);
                _computeInputSRV = _device.CreateShaderResourceView(_computeInputTexture);


                // 3. Create the output buffer on the GPU
                int bufferSize = 3 * IMAGE_SIZE * IMAGE_SIZE * sizeof(float);
                var computeOutputDesc = new BufferDescription
                {
                    ByteWidth = (uint)bufferSize,
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                    MiscFlags = ResourceOptionFlags.BufferStructured,
                    StructureByteStride = (uint)sizeof(float)
                };
                _computeOutputBuffer = _device.CreateBuffer(computeOutputDesc);
                _computeOutputUAV = _device.CreateUnorderedAccessView(_computeOutputBuffer);

                // 4. Create a staging buffer on the CPU to read the results back
                var stagingBufferDesc = new BufferDescription
                {
                    ByteWidth = (uint)bufferSize,
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                };
                
                // Create two staging buffers for pipelining to avoid GPU stalls
                _computeStagingBuffers = new ID3D11Buffer[2];
                for (int i = 0; i < _computeStagingBuffers.Length; i++)
                {
                    _computeStagingBuffers[i] = _device.CreateBuffer(stagingBufferDesc);
                }
                // --- End Compute Shader Setup ---


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
            _uiUpdateTimer.Start();
            int fpsUpdateCounter = 0;
            const int fpsUpdateInterval = 10;

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

                if (iterationCount == 1000 && Dictionary.toggleState["Debug Mode"])
                {
                    double averageTime = totalTime / 1000.0;
                    FileManager.LogInfo($"Average loop iteration time: {averageTime} ms", true);
                    totalTime = 0;
                    iterationCount = 0;
                }
                
                if (ShouldProcess() && ShouldPredict())
                {
                    var closestPrediction = GetClosestPrediction();
                    if (closestPrediction == null)
                    {
                        if (_uiUpdateTimer.Elapsed > _uiUpdateInterval)
                        {
                            DisableOverlay(DetectedPlayerOverlay!);
                            _uiUpdateTimer.Restart(); // Reset timer after updating
                        }
                        _kalmanFilter = null; // Reset filter when target is lost
                        _lastPrediction = null;
                        continue;
                    }

                    // --- Kalman Filter Prediction ---
                    PointF predictedPoint;
                    if (_kalmanFilter == null || IsNewTarget(closestPrediction))
                    {
                        // Initialize or reset the filter for a new target
                        _kalmanFilter = new KalmanFilter(initialState: closestPrediction.GetCenter());
                        predictedPoint = closestPrediction.GetCenter();
                    }
                    else
                    {
                        _kalmanFilter.Predict();
                        predictedPoint = _kalmanFilter.GetPredictedPosition();
                    }

                    // Update the filter with the current measurement
                    _kalmanFilter.Correct(closestPrediction.GetCenter());
                    // --- End Kalman Filter ---

                    _ = AutoTrigger();

                    // Aim coordinates should be calculated every frame for responsiveness.
                    // Use the PREDICTED point for calculations
                    CalculateAimCoordinates(closestPrediction, predictedPoint);

                    // But the visual overlay update is throttled.
                    if (_uiUpdateTimer.Elapsed > _uiUpdateInterval)
                    {
                        _ = UpdateFOV();
                        
                        if (Dictionary.toggleState["Show Detected Player"] && DetectedPlayerOverlay != null)
                        {
                            UpdateOverlay(DetectedPlayerOverlay, closestPrediction.Rectangle, closestPrediction.Confidence);
                        }
                        _uiUpdateTimer.Restart(); // Reset timer after updating
                    }

                    HandleAim(closestPrediction);
                    
                    if (_lastPrediction == null)
                    {
                        _lastPrediction = new Prediction();
                    }
                    _lastPrediction.Rectangle = closestPrediction.Rectangle;
                    _lastPrediction.Confidence = closestPrediction.Confidence;

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

        private void UpdateOverlay(DetectedPlayerWindow detectedPlayerOverlay, RectangleF detectionBox, float confidence)
        {
            AIConf = confidence;

            double scalingFactorX = WinAPICaller.scalingFactorX;
            double scalingFactorY = WinAPICaller.scalingFactorY;

            double centerX = detectionBox.X / scalingFactorX + (detectionBox.Width / 2.0);
            double centerY = detectionBox.Y / scalingFactorY;
            double boxWidth = detectionBox.Width;
            double boxHeight = detectionBox.Height;

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
        private void CalculateAimCoordinates(Prediction closestPrediction, PointF targetPoint)
        {
            if (!Dictionary.toggleState["Aim Assist"])
                    return;

            // The model's Rectangle is already in absolute screen pixels
            var rect = closestPrediction.Rectangle;

            // Read user-configured offsets
            double xOffset = Dictionary.sliderSettings["X Offset (Left/Right)"];
            double yOffset = Dictionary.sliderSettings["Y Offset (Up/Down)"];
            double xOffsetPercent = Dictionary.sliderSettings["X Offset (%)"];
            double yOffsetPercent = Dictionary.sliderSettings["Y Offset (%)"];

            // NOTE: The core calculation now uses the predicted 'targetPoint'
            // instead of deriving from the prediction's rectangle.
            // However, the rectangle width/height is still needed for percentage-based offsets.

            // Compute the target X in screen pixels
            double x;
            if (Dictionary.toggleState["X Axis Percentage Adjustment"])
                x = targetPoint.X - (rect.Width / 2) + rect.Width * (xOffsetPercent / 100.0) + xOffset;
            else
                x = targetPoint.X + xOffset;

            // Compute the target Y in screen pixels
            double y;
            if (Dictionary.toggleState["Y Axis Percentage Adjustment"])
            {
                // From bottom of box up by a percentage, based on predicted center
                y = targetPoint.Y + (rect.Height / 2) - rect.Height * (yOffsetPercent / 100.0) + yOffset;
            }
            else
            {
                // Anchor at Top, Center, or Bottom of the box, relative to predicted center
                string alignment = Dictionary.dropdownState["Aiming Boundaries Alignment"];
                double anchorFraction = alignment switch
                {
                    "Center" => 0.0, // Center of the predicted point
                    "Bottom" => rect.Height / 2.0, // Bottom of the original box, offset from predicted center
                    _ => -rect.Height / 2.0   // "Top" or any other value
                };
                y = targetPoint.Y + anchorFraction + yOffset;
            }

            // Round to integer mouse-move targets
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

        private bool IsNewTarget(Prediction currentPrediction)
        {
            if (_lastPrediction == null) return true;

            const float maxDistance = 100.0f; // Max pixels a target can move between frames
            var lastCenter = _lastPrediction.GetCenter();
            var currentCenter = currentPrediction.GetCenter();

            float dx = lastCenter.X - currentCenter.X;
            float dy = lastCenter.Y - currentCenter.Y;

            return (dx * dx + dy * dy) > (maxDistance * maxDistance);
        }

        private Prediction? GetClosestPrediction(bool useMousePosition = true)
        {
            // ── 1  Define the crop rectangle (640×640 centred on mouse or screen) ──
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

            bool captureOk = D3D11Screen(box);

            if (!captureOk || _onnxModel == null)
                return null;
            // ── 3  Run inference (tensor & container are pre-allocated) ────────────
            using var results = _onnxModel.Run(_inputContainer, _outputNames, _modeloptions);
            var output = results[0].AsTensor<float>();

            // ── 4 & 5: Filter, find, and select the best prediction in a single pass ──
            if (!FindBestPrediction(output, box, _reusablePrediction))
                return null;


            // ── 6  Translate the rectangle back into absolute screen space ────────
            _reusablePrediction.Rectangle = new RectangleF(
                _reusablePrediction.Rectangle.X + box.Left,
                _reusablePrediction.Rectangle.Y + box.Top,
                _reusablePrediction.Rectangle.Width,
                _reusablePrediction.Rectangle.Height
            );

            return _reusablePrediction;
        }

        private bool FindBestPrediction(Tensor<float> output, Rectangle detectionBox, Prediction predictionToFill)
        {
            float minConf = (float)Dictionary.sliderSettings["AI Minimum Confidence"] / 100f;
            float fovSize = (float)Dictionary.sliderSettings["FOV Size"];
            float half = (IMAGE_SIZE - fovSize) / 2;
            float fovMinX = half, fovMaxX = half + fovSize;
            float fovMinY = half, fovMaxY = half + fovSize;

            double centre = IMAGE_SIZE / 2.0;

            double bestDist = double.MaxValue;
            bool foundPrediction = false;

            // Get a span of the tensor's data for faster access.
            // The model output is planar (SoA): [1, 5, 2100], so all xC are contiguous, then all yC, etc.
            // This memory layout is not ideal for cache locality, but we can at least avoid
            // the overhead of the multi-dimensional indexer.
            // We cast to DenseTensor to access the underlying buffer, which should be safe.
            var outputSpan = (output as DenseTensor<float>)!.Buffer.Span;
            int numDetections = output.Dimensions[2];

            // Pre-calculate offsets to each data plane
            int xPlaneOffset = 0 * numDetections;
            int yPlaneOffset = 1 * numDetections;
            int wPlaneOffset = 2 * numDetections;
            int hPlaneOffset = 3 * numDetections;
            int confPlaneOffset = 4 * numDetections;

            for (int i = 0; i < numDetections; i++)
            {
                float conf = outputSpan[confPlaneOffset + i];
                if (conf < minConf) continue;

                float xC = outputSpan[xPlaneOffset + i];
                float yC = outputSpan[yPlaneOffset + i];
                float w = outputSpan[wPlaneOffset + i];
                float h = outputSpan[hPlaneOffset + i];

                float xMin = xC - w / 2, xMax = xC + w / 2;
                float yMin = yC - h / 2, yMax = yC + h / 2;

                if (xMin < fovMinX || xMax > fovMaxX || yMin < fovMinY || yMax > fovMaxY)
                    continue;

                // Check distance
                double dx = xC - centre;
                double dy = yC - centre;
                double d2 = dx * dx + dy * dy;

                if (d2 < bestDist)
                {
                    bestDist = d2;
                    foundPrediction = true;
                    predictionToFill.Rectangle = new RectangleF(xMin, yMin, w, h);
                    predictionToFill.Confidence = conf;
                }
            }

            return foundPrediction;
        }

        #endregion
        #endregion AI Loop Functions

        #region Screen Capture

        private bool D3D11Screen(Rectangle box)
        {
            try
            {
                if (_device == null || _context == null || _outputDuplication == null || _stagingTexture == null)
                {
                    ReinitializeD3D11();
                    if (_device == null || _context == null || _outputDuplication == null || _stagingTexture == null)
                        return false;
                }

                var hr = _outputDuplication!.AcquireNextFrame(0, out _, out var desktopResource);
                if (hr.Failure)
                {
                    desktopResource?.Dispose();
                    if (hr.Code != Vortice.DXGI.ResultCode.WaitTimeout.Code)
                    {
                        FileManager.LogError($"AcquireNextFrame failed with HRESULT: {hr}");
                        ReinitializeD3D11();
                    }
                    return false;
                }

                using (desktopResource)
                {
                    using var fullTex = desktopResource.QueryInterface<ID3D11Texture2D>();
                    
                    // Copy the relevant part of the screen to our input texture
                    _context.CopySubresourceRegion(
                        _computeInputTexture, 0, 0, 0, 0,
                        fullTex, 0,
                        new Box(box.Left, box.Top, 0, box.Right, box.Bottom, 1)
                    );
                }
                _outputDuplication.ReleaseFrame();

                // Run Compute Shader
                _context.CSSetShader(_computeShader);
                _context.CSSetShaderResource(0, _computeInputSRV);
                _context.CSSetUnorderedAccessView(0, _computeOutputUAV);
                _context.Dispatch(IMAGE_SIZE / 16, IMAGE_SIZE / 16, 1); // 320/16 = 20

                // Unbind resources
                _context.CSSetShader(null);
                _context.CSSetShaderResource(0, null);
                _context.CSSetUnorderedAccessView(0, null);
                
                if (!_computeShaderHasRunOnce)
                {
                    FileManager.LogInfo("Compute shader executed successfully for the first time.");
                    _computeShaderHasRunOnce = true;
                }

                int currentIndex = _d3dFrameIndex % 2;
                int nextIndex = (_d3dFrameIndex + 1) % 2;

                // Kick off the copy for the current frame's data to its staging buffer.
                // We will read this data on the *next* frame.
                _context.CopyResource(_computeStagingBuffers[currentIndex], _computeOutputBuffer);

                // On the first frame, we have nothing to read, so we return.
                if (_d3dFrameIndex == 0)
                {
                    _d3dFrameIndex++;
                    return false;
                }

                // --- Read back the data from the PREVIOUS frame ---
                // This avoids a stall by allowing the CPU to work on old data
                // while the GPU processes the current frame's copy command.
                var map = _context.Map(_computeStagingBuffers[nextIndex], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                
                // Copy the data from the mapped pointer to our float array
                Marshal.Copy(map.DataPointer, _inputBuffer, 0, _inputBuffer.Length);

                _context.Unmap(_computeStagingBuffers[nextIndex], 0);

                _d3dFrameIndex++;
                return true;
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || ex.ResultCode.Code == Vortice.DXGI.ResultCode.DeviceReset.Code)
            {
                FileManager.LogError("DX11 device was lost: " + ex.Message);
                ReinitializeD3D11();
                return false;
            }
            catch (Exception ex)
            {
                FileManager.LogError("DX11 capture failed unexpectedly: " + ex.Message);
                return false;
            }
        }

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
        #endregion Screen Capture

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
            _outputDuplication?.Dispose();
            _outputDuplication = null;
            
            _stagingTexture?.Dispose();
            _stagingTexture = null;

            // Dispose Compute Shader Resources
            _computeShader?.Dispose();
            _computeShader = null;
            _computeInputTexture?.Dispose();
            _computeInputTexture = null;
            _computeInputSRV?.Dispose();
            _computeInputSRV = null;
            _computeOutputBuffer?.Dispose();
            _computeOutputBuffer = null;
            _computeOutputUAV?.Dispose();
            _computeOutputUAV = null;

            if (_computeStagingBuffers != null)
            {
                foreach (var buffer in _computeStagingBuffers)
                {
                    buffer?.Dispose();
                }
                _computeStagingBuffers = null;
            }

            _context?.Dispose();
            _context = null;

            _device?.Dispose();
            _device = null;
        }
        private void DisposeResources()
        {
            DisposeD311();

            _onnxModel?.Dispose();
            _modeloptions?.Dispose();
        }

        public class Prediction
        {
            public RectangleF Rectangle { get; set; }
            public float Confidence { get; set; }
            public PointF GetCenter() => new PointF(Rectangle.X + Rectangle.Width / 2, Rectangle.Y + Rectangle.Height / 2);
        }
    }
}