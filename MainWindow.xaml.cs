using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;
using OpenCvSharp;
using Windows.Foundation;

using Window = Microsoft.UI.Xaml.Window;

namespace App
{
    public sealed partial class MainWindow : Window
    {
        private VideoCapture _capture;
        private Mat _currentFrame;
        private DispatcherTimer _playbackTimer;
        private WriteableBitmap _writeableBitmap;

        private bool _isPlaying = false;
        private string _loadedVideoPath = "";
        private int _videoWidth;
        private int _videoHeight;
        private int _totalFrames;

        private OpenCvSharp.Tracking.TrackerCSRT _tracker;
        private OpenCvSharp.Rect _trackingBox;
        private Point2f _objectCenter;
        private bool _isTracking = false;
        private bool _isDrawingBox = false;
        private Windows.Foundation.Point _startPoint;
        private Stopwatch _frameStopwatch = new Stopwatch();
        private CancellationTokenSource _exportCancellationTokenSource;

        private VideoCapture _introCapture;
        private DispatcherTimer _introTimer;
        private WriteableBitmap _introBitmap;

        public MainWindow()
        {
            this.InitializeComponent();
            _currentFrame = new Mat();

            _playbackTimer = new DispatcherTimer();
            _playbackTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0);
            _playbackTimer.Tick += PlaybackTimer_Tick;

            PlayIntroAnimation();
        }

        private void PlayIntroAnimation()
        {
            string introPath = Path.Combine(AppContext.BaseDirectory, "intro.mp4");

            if (!File.Exists(introPath))
            {
                IntroOverlayGrid.Visibility = Visibility.Collapsed;
                IntroOverlayGrid.IsHitTestVisible = false;
                return;
            }

            _introCapture = new VideoCapture(introPath);
            if (!_introCapture.IsOpened())
            {
                IntroOverlayGrid.Visibility = Visibility.Collapsed;
                IntroOverlayGrid.IsHitTestVisible = false;
                return;
            }

            int introWidth = (int)_introCapture.Get(VideoCaptureProperties.FrameWidth);
            int introHeight = (int)_introCapture.Get(VideoCaptureProperties.FrameHeight);
            double introFps = _introCapture.Get(VideoCaptureProperties.Fps);
            if (introFps <= 0) introFps = 30.0;

            _introBitmap = new WriteableBitmap(introWidth, introHeight);
            IntroViewport.Source = _introBitmap;

            _introTimer = new DispatcherTimer();
            _introTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / introFps);
            _introTimer.Tick += IntroTimer_Tick;
            _introTimer.Start();
        }

        private void IntroTimer_Tick(object sender, object e)
        {
            using Mat frame = new Mat();
            _introCapture.Read(frame);

            if (frame.Empty() || _introCapture.PosFrames >= _introCapture.FrameCount - 1)
            {
                _introTimer.Stop();
                _introCapture.Dispose();
                IntroOverlayGrid.Visibility = Visibility.Collapsed;
                IntroOverlayGrid.IsHitTestVisible = false;
                return;
            }

            using Mat hsvFrame = new Mat();
            Cv2.CvtColor(frame, hsvFrame, ColorConversionCodes.BGR2HSV);

            Scalar lowerGreen = new Scalar(35, 40, 40);
            Scalar upperGreen = new Scalar(85, 255, 255);

            using Mat greenMask = new Mat();
            Cv2.InRange(hsvFrame, lowerGreen, upperGreen, greenMask);

            using Mat textMask = new Mat();
            Cv2.BitwiseNot(greenMask, textMask);

            using Mat phonkBg = new Mat(frame.Size(), MatType.CV_8UC3, new Scalar(14, 17, 26));

            using Mat foreground = new Mat();
            Cv2.BitwiseAnd(frame, frame, foreground, textMask);

            using Mat backgroundFill = new Mat();
            Cv2.BitwiseAnd(phonkBg, phonkBg, backgroundFill, greenMask);

            using Mat finalComposite = new Mat();
            Cv2.Add(foreground, backgroundFill, finalComposite);

            using Mat bgraFrame = new Mat();
            Cv2.CvtColor(finalComposite, bgraFrame, ColorConversionCodes.BGR2BGRA);

            unsafe
            {
                long byteSize = bgraFrame.Total() * bgraFrame.ElemSize();
                using (Stream stream = _introBitmap.PixelBuffer.AsStream())
                {
                    var span = new ReadOnlySpan<byte>((byte*)bgraFrame.DataPointer, (int)byteSize);
                    stream.Write(span.ToArray(), 0, (int)byteSize);
                }
            }
            _introBitmap.Invalidate();
        }

        private async void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".avi");

            var file = await picker.PickSingleFileAsync();
            if (file != null) LoadVideo(file.Path);
        }

        private void LoadVideo(string path)
        {
            _loadedVideoPath = path;
            _capture = new VideoCapture(path);
            if (!_capture.IsOpened()) return;

            _totalFrames = (int)_capture.FrameCount;
            TimelineSlider.Minimum = 0;
            TimelineSlider.Maximum = _totalFrames - 1;
            TimelineSlider.Value = 0;

            _videoWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
            _videoHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);

            _writeableBitmap = new WriteableBitmap(_videoWidth, _videoHeight);
            VideoViewport.Source = _writeableBitmap;

            _isTracking = false;
            TrackerDataText.Text = "VIDEO LOADED";

            ReadAndDisplayFrame();
        }

        private void BtnOpenUpscale_Click(object sender, RoutedEventArgs e)
        {
            UpscaleWindow upscaleWindow = new UpscaleWindow(_loadedVideoPath);
            upscaleWindow.Activate();
        }

        private Windows.Foundation.Point GetScaledVideoCoordinates(Windows.Foundation.Point pointerPos)
        {
            if (_currentFrame.Empty() || VideoViewport.ActualWidth == 0 || VideoViewport.ActualHeight == 0)
                return pointerPos;

            double imgActualW = VideoViewport.ActualWidth;
            double imgActualH = VideoViewport.ActualHeight;

            double nativeW = _videoWidth;
            double nativeH = _videoHeight;

            double scaleX = imgActualW / nativeW;
            double scaleY = imgActualH / nativeH;
            double scale = Math.Min(scaleX, scaleY);

            double renderedW = nativeW * scale;
            double renderedH = nativeH * scale;

            double offsetX = (imgActualW - renderedW) / 2.0;
            double offsetY = (imgActualH - renderedH) / 2.0;

            double videoX = (pointerPos.X - offsetX) / scale;
            double videoY = (pointerPos.Y - offsetY) / scale;

            videoX = Math.Clamp(videoX, 0, nativeW);
            videoY = Math.Clamp(videoY, 0, nativeH);

            return new Windows.Foundation.Point((int)videoX, (int)videoY);
        }

        private void VideoViewport_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_capture == null || _isPlaying) return;
            _isDrawingBox = true;

            var rawPos = e.GetCurrentPoint(VideoViewport).Position;
            _startPoint = GetScaledVideoCoordinates(rawPos);
            _trackingBox = new OpenCvSharp.Rect((int)_startPoint.X, (int)_startPoint.Y, 0, 0);
        }

        private void VideoViewport_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDrawingBox) return;

            var rawPos = e.GetCurrentPoint(VideoViewport).Position;
            var pos = GetScaledVideoCoordinates(rawPos);

            int x = (int)Math.Min(_startPoint.X, pos.X);
            int y = (int)Math.Min(_startPoint.Y, pos.Y);
            int width = (int)Math.Abs(_startPoint.X - pos.X);
            int height = (int)Math.Abs(_startPoint.Y - pos.Y);

            _trackingBox = new OpenCvSharp.Rect(x, y, width, height);
            RedrawCurrentFrameOnly();
        }

        private void VideoViewport_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDrawingBox) return;
            _isDrawingBox = false;

            if (_trackingBox.Width > 15 && _trackingBox.Height > 15)
            {
                if (_tracker != null) _tracker.Dispose();
                _tracker = OpenCvSharp.Tracking.TrackerCSRT.Create();
                _tracker.Init(_currentFrame, _trackingBox);

                _objectCenter = new Point2f(_trackingBox.X + (_trackingBox.Width / 2f), _trackingBox.Y + (_trackingBox.Height / 2f));
                _isTracking = true;
                ReadAndDisplayFrame();
            }
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_capture == null || !_capture.IsOpened()) return;

            _isPlaying = !_isPlaying;
            PlayIcon.Symbol = _isPlaying ? Symbol.Pause : Symbol.Play;

            if (_isPlaying) _playbackTimer.Start();
            else _playbackTimer.Stop();
        }

        private void PlaybackTimer_Tick(object sender, object e)
        {
            if (_capture.PosFrames >= _totalFrames - 1)
            {
                _playbackTimer.Stop();
                _isPlaying = false;
                PlayIcon.Symbol = Symbol.Play;
                return;
            }

            ReadAndDisplayFrame();

            TimelineSlider.ValueChanged -= TimelineSlider_ValueChanged;
            TimelineSlider.Value = _capture.PosFrames;
            TimelineSlider.ValueChanged += TimelineSlider_ValueChanged;
        }

        private void TimelineSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_capture != null && _capture.IsOpened())
            {
                _capture.PosFrames = (int)e.NewValue;
                _isTracking = false;
                TrackerDataText.Text = "TRACKING: RESET";
                ReadAndDisplayFrame();
            }
        }

        private void ReadAndDisplayFrame()
        {
            if (_capture == null) return;
            _frameStopwatch.Restart();
            _capture.Read(_currentFrame);
            if (_currentFrame.Empty()) return;

            RedrawCurrentFrameOnly();

            _frameStopwatch.Stop();
            double fps = _frameStopwatch.ElapsedMilliseconds > 0 ? 1000.0 / _frameStopwatch.ElapsedMilliseconds : 60.0;
            MetricsText.Text = $"FPS: {fps:F1} | GPU: ACTIVE";
        }

        private void RedrawCurrentFrameOnly()
        {
            if (_currentFrame.Empty()) return;

            using Mat displayFrame = _currentFrame.Clone();
            ProcessFrameGraphics(displayFrame, BtnCropToggle.IsChecked == true, BtnStabilizeToggle.IsChecked == true, false);

            using Mat bgraMat = new Mat();
            Cv2.CvtColor(displayFrame, bgraMat, ColorConversionCodes.BGR2BGRA);

            unsafe
            {
                long byteSize = bgraMat.Total() * bgraMat.ElemSize();
                using (Stream stream = _writeableBitmap.PixelBuffer.AsStream())
                {
                    var span = new ReadOnlySpan<byte>((byte*)bgraMat.DataPointer, (int)byteSize);
                    stream.Write(span.ToArray(), 0, (int)byteSize);
                }
            }
            _writeableBitmap.Invalidate();
        }

        private void ProcessFrameGraphics(Mat frame, bool isCropEnabled, bool isStabilizeEnabled, bool isExporting)
        {
            if (_isDrawingBox)
            {
                if (!isExporting) Cv2.Rectangle(frame, _trackingBox, Scalar.Yellow, 2);
            }
            else if (_isTracking)
            {
                if (_isPlaying || isExporting)
                {
                    bool success = _tracker.Update(frame, ref _trackingBox);
                    if (!success) _isTracking = false;
                }

                if (_isTracking)
                {
                    _objectCenter = new Point2f(_trackingBox.X + (_trackingBox.Width / 2f), _trackingBox.Y + (_trackingBox.Height / 2f));

                    if (!isExporting)
                    {
                        Cv2.Rectangle(frame, _trackingBox, Scalar.LimeGreen, 2);
                        Cv2.Circle(frame, (int)_objectCenter.X, (int)_objectCenter.Y, 5, Scalar.Red, -1);

                        OpenCvSharp.Rect safeBox = new OpenCvSharp.Rect(
                            Math.Max(0, _trackingBox.X), Math.Max(0, _trackingBox.Y),
                            Math.Min(frame.Width - _trackingBox.X, _trackingBox.Width),
                            Math.Min(frame.Height - _trackingBox.Y, _trackingBox.Height));

                        if (safeBox.Width > 0 && safeBox.Height > 0)
                        {
                            using Mat grayFrame = new Mat();
                            Cv2.CvtColor(frame, grayFrame, ColorConversionCodes.BGR2GRAY);
                            using Mat roi = new Mat(grayFrame, safeBox);

                            Point2f[] corners = Cv2.GoodFeaturesToTrack(roi, 10, 0.01, 5, null, 3, false, 0.04);
                            if (corners != null)
                            {
                                foreach (var corner in corners)
                                {
                                    Cv2.Circle(frame, (int)(safeBox.X + corner.X), (int)(safeBox.Y + corner.Y), 3, Scalar.Cyan, -1);
                                }
                            }
                        }

                        if (isStabilizeEnabled)
                        {
                            using Mat grayFull = new Mat();
                            Cv2.CvtColor(frame, grayFull, ColorConversionCodes.BGR2GRAY);
                            Point2f[] envCorners = Cv2.GoodFeaturesToTrack(grayFull, 50, 0.05, 10, null, 3, false, 0.04);
                            if (envCorners != null)
                            {
                                foreach (var pt in envCorners)
                                {
                                    Cv2.Circle(frame, (int)pt.X, (int)pt.Y, 2, Scalar.Orange, -1);
                                }
                            }
                        }
                    }

                    if (isCropEnabled)
                    {
                        using Mat anchoredCanvas = new Mat(new OpenCvSharp.Size(_videoWidth, _videoHeight), MatType.CV_8UC3, Scalar.Black);
                        float targetScreenCenterX = _videoWidth / 2f;
                        float targetScreenCenterY = _videoHeight / 2f;
                        float shiftX = targetScreenCenterX - _objectCenter.X;
                        float shiftY = targetScreenCenterY - _objectCenter.Y;

                        using Mat translationMatrix = Cv2.GetAffineTransform(
                            new Point2f[] { new Point2f(0, 0), new Point2f(100, 0), new Point2f(0, 100) },
                            new Point2f[] { new Point2f(shiftX, shiftY), new Point2f(100 + shiftX, shiftY), new Point2f(shiftX, 100 + shiftY) }
                        );

                        Cv2.WarpAffine(frame, anchoredCanvas, translationMatrix, new OpenCvSharp.Size(_videoWidth, _videoHeight),
                                       InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);

                        anchoredCanvas.CopyTo(frame);
                    }

                    if (!isExporting)
                    {
                        DispatcherQueue.TryEnqueue(() => {
                            TrackerDataText.Text = $"ANCHOR X: {(int)_objectCenter.X} Y: {(int)_objectCenter.Y}";
                            TrackerDataText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 229, 255));
                        });
                    }
                }
                else
                {
                    if (!isExporting)
                    {
                        DispatcherQueue.TryEnqueue(() => {
                            TrackerDataText.Text = "TRACKING: LOST";
                            TrackerDataText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68));
                        });
                    }
                }
            }
        }

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_capture == null || !_isTracking) return;

            var savePicker = new Windows.Storage.Pickers.FileSavePicker();
            InitializeWithWindow.Initialize(savePicker, WindowNative.GetWindowHandle(this));
            savePicker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            savePicker.FileTypeChoices.Add("MP4 Video", new System.Collections.Generic.List<string>() { ".mp4" });
            savePicker.SuggestedFileName = "Stabilized_Clean_Export";

            var file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            bool cropStateOnExport = BtnCropToggle.IsChecked == true;
            bool stabilizeStateOnExport = BtnStabilizeToggle.IsChecked == true;
            int startFramePos = _capture.PosFrames;
            OpenCvSharp.Rect initialBox = _trackingBox;
            string videoPath = _loadedVideoPath;
            int width = _videoWidth;
            int height = _videoHeight;
            int totalFramesToProcess = _totalFrames - startFramePos;
            double fps = _capture.Get(VideoCaptureProperties.Fps);
            if (fps <= 0) fps = 30.0;

            _playbackTimer.Stop();
            _isPlaying = false;
            PlayIcon.Symbol = Symbol.Play;
            ExportModalOverlay.Visibility = Visibility.Visible;
            ExportModalOverlay.IsHitTestVisible = true;
            ExportProgressBar.Value = 0;
            ExportProgressBar.Maximum = totalFramesToProcess > 0 ? totalFramesToProcess : 100;

            _exportCancellationTokenSource = new CancellationTokenSource();
            var token = _exportCancellationTokenSource.Token;

            bool successExport = true;
            string errorMessage = "";
            Stopwatch exportWatch = Stopwatch.StartNew();

            try
            {
                await Task.Run(() =>
                {
                    using VideoCapture exportCap = new VideoCapture(videoPath);
                    if (!exportCap.IsOpened()) throw new Exception("Could not open source video for export.");

                    // Force even dimensions for OpenCV export stability
                    int safeWidth = width % 2 == 0 ? width : width - 1;
                    int safeHeight = height % 2 == 0 ? height : height - 1;

                    VideoWriter writer = new VideoWriter(file.Path, FourCC.MP4V, fps, new OpenCvSharp.Size(safeWidth, safeHeight));

                    if (!writer.IsOpened())
                    {
                        writer.Dispose();
                        writer = new VideoWriter(file.Path, FourCC.XVID, fps, new OpenCvSharp.Size(safeWidth, safeHeight));
                    }
                    if (!writer.IsOpened())
                    {
                        writer.Dispose();
                        string aviPath = file.Path.Replace(".mp4", ".avi");
                        writer = new VideoWriter(aviPath, FourCC.MJPG, fps, new OpenCvSharp.Size(safeWidth, safeHeight));
                    }
                    if (!writer.IsOpened()) throw new Exception("Failed to initialize OpenCV VideoWriter. Check installed codecs.");

                    using (writer)
                    {
                        Mat exportFrame = new Mat();
                        using OpenCvSharp.Tracking.TrackerCSRT exportTracker = OpenCvSharp.Tracking.TrackerCSRT.Create();

                        exportCap.PosFrames = startFramePos;
                        exportCap.Read(exportFrame);
                        if (!exportFrame.Empty())
                        {
                            exportTracker.Init(exportFrame, initialBox);
                        }

                        exportCap.PosFrames = startFramePos;
                        int processedCount = 0;

                        while (true)
                        {
                            if (token.IsCancellationRequested)
                            {
                                successExport = false;
                                break;
                            }

                            exportCap.Read(exportFrame);
                            if (exportFrame.Empty()) break;

                            exportTracker.Update(exportFrame, ref initialBox);
                            _trackingBox = initialBox;

                            ProcessFrameGraphics(exportFrame, cropStateOnExport, stabilizeStateOnExport, true);
                            writer.Write(exportFrame);

                            processedCount++;
                            int currentCount = processedCount;

                            if (currentCount % 5 == 0 || currentCount == totalFramesToProcess)
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    ExportProgressBar.Value = currentCount;
                                    double percent = totalFramesToProcess > 0 ? (double)currentCount / totalFramesToProcess * 100 : 0;
                                    double elapsedSec = exportWatch.Elapsed.TotalSeconds;
                                    double framesPerSec = elapsedSec > 0 ? currentCount / elapsedSec : 0;
                                    double remainingSec = framesPerSec > 0 ? (totalFramesToProcess - currentCount) / framesPerSec : 0;

                                    ExportStatusText.Text = $"Processing frame {currentCount} / {totalFramesToProcess} ({percent:F1}%) - Remaining: {TimeSpan.FromSeconds(remainingSec):mm\\:ss}";
                                    ExportMetricsOverlayText.Text = $"Processing Speed: {framesPerSec:F1} FPS | Clean Export Mode: ON";
                                });
                            }
                        }
                    }
                }, token);
            }
            catch (Exception ex)
            {
                successExport = false;
                errorMessage = ex.ToString();
            }

            exportWatch.Stop();

            ExportModalOverlay.Visibility = Visibility.Collapsed;
            ExportModalOverlay.IsHitTestVisible = false;

            if (successExport)
            {
                TrackerDataText.Text = "EXPORT COMPLETE!";
                DispatcherQueue.TryEnqueue(async () =>
                {
                    ContentDialog successDialog = new ContentDialog
                    {
                        Title = "Export Successful",
                        Content = $"Your clean, stabilized video has been successfully exported to:\n{file.Path}",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await successDialog.ShowAsync();
                });
            }
            else
            {
                TrackerDataText.Text = token.IsCancellationRequested ? "EXPORT CANCELLED" : "EXPORT FAILED";
                if (!token.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        ContentDialog errorDialog = new ContentDialog
                        {
                            Title = "Export Error",
                            Content = $"An error occurred during rendering:\n{errorMessage}",
                            CloseButtonText = "OK",
                            XamlRoot = this.Content.XamlRoot
                        };
                        await errorDialog.ShowAsync();
                    });
                }
            }
        }

        private void BtnCancelExport_Click(object sender, RoutedEventArgs e)
        {
            _exportCancellationTokenSource?.Cancel();
        }
    }
}