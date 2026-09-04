using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using WinRT.Interop;
using OpenCvSharp;
using OpenCvSharp.DnnSuperres;

using Window = Microsoft.UI.Xaml.Window;

namespace App
{
    public sealed partial class UpscaleWindow : Window
    {
        private string _inputVideoPath;

        public UpscaleWindow(string videoPath)
        {
            this.InitializeComponent();
            _inputVideoPath = videoPath;

            // Make the window slightly taller to accommodate the new dashboard
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(520, 720));
        }

        private async void BtnStartUpscale_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_inputVideoPath) || !File.Exists(_inputVideoPath))
            {
                ContentDialog errDialog = new ContentDialog { Title = "Error", Content = "Load a video first.", CloseButtonText = "OK" };
                if (this.Content is FrameworkElement elem) errDialog.XamlRoot = elem.XamlRoot;
                await errDialog.ShowAsync();
                return;
            }

            string modelPath = Path.Combine(AppContext.BaseDirectory, "EDSR_x2.pb");
            if (!File.Exists(modelPath))
            {
                ContentDialog errDialog = new ContentDialog
                {
                    Title = "Missing AI Model",
                    Content = "Please place the EDSR_x2.pb AI model file in your output directory to enable neural upscaling.",
                    CloseButtonText = "OK"
                };
                if (this.Content is FrameworkElement elem) errDialog.XamlRoot = elem.XamlRoot;
                await errDialog.ShowAsync();
                return;
            }

            // Lock UI and reveal the Progress Dashboard
            BtnStartUpscale.IsEnabled = false;
            BtnStartUpscale.Content = "RENDERING...";
            ProgressDashboard.Visibility = Visibility.Visible;

            bool useGpu = ChkGpuAccel.IsChecked == true;
            string outputPath = _inputVideoPath.Replace(".mp4", "_AI_UPSCALED.mp4");
            bool success = true;
            string errorMsg = "";

            await Task.Run(() =>
            {
                try
                {
                    using VideoCapture cap = new VideoCapture(_inputVideoPath);
                    if (!cap.IsOpened()) throw new Exception("Failed to read input video.");

                    int width = (int)cap.Get(VideoCaptureProperties.FrameWidth);
                    int height = (int)cap.Get(VideoCaptureProperties.FrameHeight);
                    double fps = cap.Get(VideoCaptureProperties.Fps);
                    int totalFrames = (int)cap.Get(VideoCaptureProperties.FrameCount);
                    if (fps <= 0) fps = 30.0;

                    int targetWidth = width * 2;
                    int targetHeight = height * 2;

                    using VideoWriter writer = new VideoWriter(outputPath, FourCC.MP4V, fps, new OpenCvSharp.Size(targetWidth, targetHeight));
                    if (!writer.IsOpened()) throw new Exception("Failed to initialize upscale VideoWriter.");

                    // Initialize the AI Network
                    using DnnSuperResImpl superRes = new DnnSuperResImpl();
                    superRes.ReadModel(modelPath);
                    superRes.SetModel("edsr", 2);

                    // --- THE GPU ACCELERATION FIX ---
                    if (useGpu)
                    {
                        try
                        {
                            // This routes the neural processing directly to your graphics card
                            superRes.SetPreferableBackend(OpenCvSharp.Dnn.Backend.CUDA);
                            superRes.SetPreferableTarget(OpenCvSharp.Dnn.Target.CUDA);
                        }
                        catch
                        {
                            // If the system lacks NVIDIA cuDNN binaries, it silently falls back to CPU
                        }
                    }

                    using Mat frame = new Mat();
                    using Mat upscaledFrame = new Mat();

                    Stopwatch sw = Stopwatch.StartNew();
                    int processedFrames = 0;

                    // Set progress bar maximum on the UI thread
                    DispatcherQueue.TryEnqueue(() => {
                        RenderProgressBar.Maximum = totalFrames > 0 ? totalFrames : 100;
                    });

                    while (true)
                    {
                        cap.Read(frame);
                        if (frame.Empty()) break;

                        // AI Processing
                        superRes.Upsample(frame, upscaledFrame);
                        writer.Write(upscaledFrame);

                        processedFrames++;

                        // Update Dashboard Metrics every frame
                        if (processedFrames % 1 == 0 || processedFrames == totalFrames)
                        {
                            double elapsedSec = sw.Elapsed.TotalSeconds;
                            double currentFps = elapsedSec > 0 ? processedFrames / elapsedSec : 0;
                            double remainingSec = currentFps > 0 ? (totalFrames - processedFrames) / currentFps : 0;
                            int currentCount = processedFrames;

                            DispatcherQueue.TryEnqueue(() =>
                            {
                                RenderProgressBar.Value = currentCount;
                                double percent = totalFrames > 0 ? (double)currentCount / totalFrames * 100 : 0;

                                ProgressText.Text = $"Frame: {currentCount} / {totalFrames} ({percent:F1}%)";
                                FpsText.Text = $"GPU Speed: {currentFps:F2} FPS";
                                TimeText.Text = $"Time Remaining: {TimeSpan.FromSeconds(remainingSec):hh\\:mm\\:ss}";
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    errorMsg = ex.Message;
                }
            });

            if (success)
            {
                ContentDialog successDialog = new ContentDialog { Title = "AI Render Complete", Content = $"Your video has been neurologically upscaled and saved to:\n{outputPath}", CloseButtonText = "OK" };
                if (this.Content is FrameworkElement rootElem) successDialog.XamlRoot = rootElem.XamlRoot;
                await successDialog.ShowAsync();
            }
            else
            {
                ContentDialog errorDialog = new ContentDialog { Title = "Render Failed", Content = $"An error occurred:\n{errorMsg}", CloseButtonText = "OK" };
                if (this.Content is FrameworkElement rootElem) errorDialog.XamlRoot = rootElem.XamlRoot;
                await errorDialog.ShowAsync();
            }

            this.Close();
        }
    }
}