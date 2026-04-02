

using LibVLCSharp.Shared;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Gif;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using TurboJpegWrapper;
using WpfAnimatedGif;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Path = System.IO.Path;

namespace ComicViewer
{
    public partial class MainWindow : System.Windows.Window
    {
        private ZipArchive _archive;
        private List<ZipArchiveEntry> _pages = new List<ZipArchiveEntry>();
        private int _currentPage = 0;
        private bool _isPageLoading = false;
        //private bool lockScrollTop = false;
        //private bool lockScrollBottom = false;
        //private double _scrollAccumulator = 0;
        private static SemaphoreSlim semImg = new SemaphoreSlim(1, 1);
        private static SemaphoreSlim semQ = new SemaphoreSlim(3, 3);
        private String _currentFile = "";
        private String _currentFilePath = "";
        private double scrollWait = 0;
        DispatcherTimer timer;
        private bool titleBarLocked = false;
        // 1200 is roughly 10 standard mouse wheel "notches" (1 notch = 120 delta)
        //private const double ScrollFast = 120;
        //private const double ScrollSlow = 450;
        //private double ScrollThresholdLimit = ScrollSlow;
        //private bool isAtBottom = false;
        //private bool isAtTop = false;
        private long lastScrollStart = DateTime.Now.Ticks;
        private System.Windows.Point _lastMousePosition;
        private double _startVerticalOffset;
        private bool _isDragging = false;
        private Thread th;
        private string dimensionResized = "";
        private string dimensionOriginal = "";
        private string size = "";
        private string jsonPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\" + "comic.json";
        private System.Windows.Point _lastTitlePos;
        // The Cache: Stores pre-decoded BitmapSources
        //private ConcurrentDictionary<int, ImageContainer> _cache = new ConcurrentDictionary<int, ImageContainer>();
        private ConcurrentDictionary<int, ImageContainer> _cache = new ConcurrentDictionary<int, ImageContainer>();
        private bool _isFitWidth = false;
        private JsonComic jsonComic = new JsonComic();
        private ComicItem _currentComicItem = null;
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
        private MemoryStream _mediaStream;
        private StreamMediaInput _mediaInput;
        private Media _media;
        private SolidColorBrush mainBackground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1e, 0x1e, 0x1e, 0x1e));
        private GFG compare = new GFG();
        //private ImageContainer currentImage = null;
        //private BitmapSource currentImage = null;
        private Thread gifThread = null;
        private Thread thSizeChanged = null;
        private Thread thSliderChanged = null;
        private MemoryStream gifStream = null;
        private bool ultraScaling = true;
        private bool isResized = false;
        private BitmapSource gifImg = null;

        public const string libJxlMain = "jxl/bin/jxl.dll";
        public const string libJxlThreads = "jxl/bin/jxl_threads.dll";

        public MainWindow()
        {
            InitializeComponent();


            this.Top = 0;

            JsonSerializerSettings js = new JsonSerializerSettings();

            System.Diagnostics.Debug.WriteLine(jsonPath);
            if (!File.Exists(jsonPath))
            {
                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(new JsonComic(), Formatting.None));
            }
            else
            {
                jsonComic = JsonConvert.DeserializeObject<JsonComic>(File.ReadAllText(jsonPath));

            }
            this.Width = jsonComic.WindowWidth > 0 ? jsonComic.WindowWidth : this.Width;
            this.Left = jsonComic.WindowX > 0 ? jsonComic.WindowX : this.Left;
            this.Height = jsonComic.WindowHeight > 0 ? jsonComic.WindowHeight : this.Height;

            videoView.Loaded += VideoView_Loaded;



            //var options = new string[]
            //{
            //    "--demux=avcodec","--input-repeat=65535"
            //    // VLC options can be given here. Please refer to the VLC command line documentation.
            //};

        }

        private void _mediaPlayer_MediaChanged(object sender, MediaPlayerMediaChangedEventArgs e)
        {
            _mediaPlayer.Play();

            System.Diagnostics.Debug.WriteLine("Media Changed");
        }

        private void _mediaPlayer_EndReached(object sender, EventArgs e)
        {
            //_mediaPlayer.Media = _media;
            ////_mediaPlayer.Play();
            System.Diagnostics.Debug.WriteLine("End Video");
        }

        private void _mediaPlayer_Opening(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("Open Video");
        }

        private void VideoView_Loaded(object sender, RoutedEventArgs e)
        {
            Core.Initialize();
            _libVLC = new LibVLC(["--demux=avcodec", "--input-repeat=65535"]);
            _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC)
            {
                EnableMouseInput = true,
            };

            videoView.MediaPlayer = _mediaPlayer;

            _mediaPlayer.Opening += _mediaPlayer_Opening;

            _mediaPlayer.EndReached += _mediaPlayer_EndReached;
            _mediaPlayer.MediaChanged += _mediaPlayer_MediaChanged;




            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Contains(".zip"))
                {
                    System.Diagnostics.Debug.WriteLine("Open zip: " + args[i]);
                    LoadArchive(args[i]);
                    break;
                }
            }

            Thread th = (new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (!(_currentPage < 0 || _currentPage >= _pages.Count) && _mediaPlayer != null && _mediaPlayer.IsPlaying)
                        {
                            this.Dispatcher.Invoke(new Action(() =>
                            {
                                if (!(_currentPage < 0 || _currentPage >= _pages.Count) && _mediaPlayer != null && _mediaPlayer.IsPlaying)
                                {
                                    SetTitleText(" | Time: " + (_mediaPlayer.Time / 1000 + "/" + _mediaPlayer.Length / 1000));
                                    if (_mediaPlayer.Length != 0)
                                    {
                                        mediaSlider.Value = ((double)_mediaPlayer.Time / (double)_mediaPlayer.Length) * 100;
                                    }
                                }
                            }));

                        }
                        Thread.Sleep(200);
                    }
                    catch (ThreadInterruptedException exception)
                    {
                        System.Diagnostics.Debug.WriteLine(exception.Message);
                    }
                }

            }));
            th.Start();
        }

        private void SetTitleText(String append)
        {
            String fileName = _currentFile.Length > 100 ? _currentFile.Substring(0, 100) + "..." : _currentFile;
            String pageName = _pages[_currentPage].Name.Length > 20 ? _pages[_currentPage].Name.Substring(0, 20) + "..." : _pages[_currentPage].Name;
            String extension = System.IO.Path.GetExtension(_pages[_currentPage].Name).ToUpper().Replace(".", "");


            TitleText.Text = fileName + " | " + pageName + " | " + extension + " | Page: " + (_currentPage + 1) + "/" + _pages.Count + append;
        }

        public enum SizeUnits
        {
            Byte, KB, MB, GB, TB, PB, EB, ZB, YB
        }

        public string ToSize(long value, SizeUnits unit)
        {
            return (value / (double)Math.Pow(1024, (Int64)unit)).ToString("0.00");
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog { Filter = "Comic Archives|*.zip;*.cbz" };
            if (openFileDialog.ShowDialog() == true) LoadArchive(openFileDialog.FileName);
        }

        private void LoadArchive(string filePath)
        {


            _currentFile = Path.GetFileName(filePath);
            _currentFilePath = filePath;
            _currentPage = 0;


            _currentComicItem = jsonComic.List.Find(item =>
            {
                if (item.Name == _currentFile) return true;
                else return false;
            });
            System.Diagnostics.Debug.WriteLine("currentComicItem: " + _currentComicItem);
            if (_currentComicItem == null)
            {
                _currentComicItem = new ComicItem();
                _currentComicItem.Name = _currentFile;
                jsonComic.List.Add(_currentComicItem);
            }
            _currentPage = _currentComicItem.Pos;


            _archive?.Dispose();
            _cache.Clear();
            _archive = ZipFile.OpenRead(filePath);
            _pages = _archive.Entries
                .Where(entry => new[] { ".jpg", ".jpeg", ".png", ".webp", ".jxl", ".jxr", ".tif", ".gif", ".webm", ".mkv", ".mp4" }.Contains(System.IO.Path.GetExtension(entry.FullName).ToLowerInvariant()))
                .OrderBy(entry => entry.FullName, new NaturalSortComparer()) // Custom comparer for 1.jpg, 2.jpg, 10.jpg
                .ToList();

            Slider.Minimum = 0;
            Slider.Maximum = _pages.Count - 1;


            if (_pages.Any()) {; DisplayPage(1, 0); }


            WindowFit(_currentComicItem.FitToWindow);


        }

        public bool MetadataExist(MemoryStream ms, string name, string descriptiom)
        {
            foreach (MetadataExtractor.Directory dir in ImageMetadataReader.ReadMetadata(ms))
            {
                foreach (Tag tag in dir.Tags)
                {
                    if (tag.Name.Equals(name) && tag.Description.Equals(descriptiom))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private async void DisplayPage(int pageDiff, int method)
        {
            System.Diagnostics.Debug.WriteLine("DisplayPage: " + method);
            gifImg = null;
            if (gifThread != null && gifThread.IsAlive)
            {
                gifThread.Interrupt();
            }

            if (_currentPage < 0 || _currentPage >= _pages.Count) return;

            // Lock the UI from further scroll-triggered page turns
            _isPageLoading = true;

            try
            {
                var keysToRemove = _cache.Keys.Where(k =>
                {
                    if (pageDiff >= 0)
                    {
                        return k < _currentPage - 1;
                    }
                    else
                    {
                        return k > _currentPage + 1;
                    }
                }).ToList();
                foreach (var key in keysToRemove)
                {
                    _cache.TryRemove(key, out _);
                }
                ImageBehavior.SetAnimatedSource(ComicDisplay, null);

                if (_pages[_currentPage].Name.ToLower().Contains(".webm") ||
                    _pages[_currentPage].Name.ToLower().Contains(".mp4") ||
                    _pages[_currentPage].Name.ToLower().Contains(".mkv"))
                {
                    mainWindow.Background = System.Windows.Media.Brushes.Black;
                    videoViewGrid.Visibility = Visibility.Visible;
                    videoView.Visibility = Visibility.Visible;
                    ComicDisplay.Source = null;
                    //WindowFit(true);
                    Stream stream = _pages[_currentPage].Open();
                    _mediaStream = new MemoryStream();
                    stream.CopyTo(_mediaStream);
                    _mediaStream.Position = 0;

                    _mediaInput = new StreamMediaInput(_mediaStream);
                    _media = new Media(_libVLC, _mediaInput);
                    _mediaPlayer.Media = _media;
                }
                else
                {
                    mainWindow.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x1e, 0x1e, 0x1e));
                    videoViewGrid.Visibility = Visibility.Hidden;
                    videoView.Visibility = Visibility.Hidden;
                    if (_mediaPlayer != null)
                    {
                        _mediaPlayer.Stop();
                    }

                    BitmapSource imageToShow;
                    //RenderOptions.SetBitmapScalingMode(ComicDisplay, BitmapScalingMode.NearestNeighbor);
                    //RenderOptions.SetBitmapScalingMode(ComicDisplay, BitmapScalingMode.HighQuality);


                    bool isAnimated = false;
                    if (_pages[_currentPage].Name.ToLower().Contains(".webp"))
                    {
                        MemoryStream ms = new MemoryStream();
                        _pages[_currentPage].Open().CopyTo(ms);
                        ms.Position = 0;
                        IEnumerable<MetadataExtractor.Directory> directories = ImageMetadataReader.ReadMetadata(ms);
                        foreach (var directory in directories)
                            foreach (var item in directory.Tags)
                            {
                                if (item.Name.ToLower().Contains("animation") && item.Description.ToLower().Contains("true"))
                                {
                                    isAnimated = true;
                                }
                            }
                    }




                    if (_pages[_currentPage].Name.ToLower().Contains(".gif"))
                    {
                        StartGifAnimation(_pages[_currentPage].Open());
                    }
                    else if (_pages[_currentPage].Name.ToLower().Contains(".webp") && isAnimated)
                    {
                        StartWebpAnimation(_pages[_currentPage].Open());
                    }
                    else
                    {
                        if (_cache.TryGetValue(_currentPage, out ImageContainer cachedImage))
                        {
                            imageToShow = cachedImage.ResizedImage;

                        }
                        else
                        {
                            imageToShow = await LoadAndProcessImage(_currentPage);
                        }

                        if (imageToShow != null)
                        {
                            ComicDisplay.Source = imageToShow;
                            if (_currentComicItem != null)
                                WindowFit(_currentComicItem.FitToWindow);


                            if (_currentPage + 1 <= _pages.Count - 1 && pageDiff > 0)
                            {
                                LoadAndProcessImage(_currentPage + 1);
                            }
                            if (_currentPage - 1 >= 0 && pageDiff < 0)
                            {
                                LoadAndProcessImage(_currentPage - 1);
                            }

                        }
                    }
                    UpdateInfo();

                    if (pageDiff < 0)
                    {
                        MainScroll.ScrollToBottom();
                        if (_isFitWidth)
                        {
                            scrollWait = 200 + DateTime.Now.Ticks / 10000;
                        }
                    }
                    else
                    {
                        MainScroll.ScrollToTop();
                        if (_isFitWidth)
                        {
                            scrollWait = 200 + DateTime.Now.Ticks / 10000;
                        }

                    }
                }

                Slider.Value = _currentPage;


                if (th != null && th.IsAlive)
                {
                    th.Interrupt();
                }


                th = (new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < 5; i++)
                        {

                            Thread.Sleep(1000);
                        }
                        System.Diagnostics.Debug.WriteLine("Update json");
                        SaveJson();
                    }
                    catch
                    {

                    }
                }));
                th.Start();

                //

                await Task.Delay(20);


            }
            catch (Exception ex)
            {

                System.Diagnostics.Debug.WriteLine(ex.Message);
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
            finally
            {
                // Unlock once the image is visible and the scroll is reset

                _isPageLoading = false;
            }

            // Trigger Pre-caching in the background
            //_ = Task.Run(() => UpdateCache());
        }
        public static List<(BitmapSource Bitmap, int Delay)> ExtractWebPFrames(Stream webpStream)
        {
            byte[] webpBytes;
            using (var ms = new MemoryStream())
            {
                webpStream.CopyTo(ms);
                webpBytes = ms.ToArray();
            }

            var vipsImage = NetVips.Image.WebploadBuffer(webpBytes, n: -1);

            int frameCount = (int)vipsImage.Get("n-pages");
            int pageHeight = (int)vipsImage.Get("page-height");
            int canvasWidth = vipsImage.Width;
            int[] delays = (int[])vipsImage.Get("delay");

            // Pre-allocate fixed-size array so parallel writes are safe
            var result = new (BitmapSource Bitmap, int Delay)[frameCount];

            Parallel.For(0, frameCount, i =>
            {
                // Each iteration gets its own vips image slice — thread safe
                var frame = vipsImage.ExtractArea(0, i * pageHeight, canvasWidth, pageHeight);

                if (frame.Bands < 4)
                    frame = frame.Bandjoin(255);

                // Swap to BGRA in one operation using a LUT instead of band-by-band
                var reordered = frame.ExtractBand(2)
                                     .Bandjoin(new[] { frame.ExtractBand(1),
                                               frame.ExtractBand(0),
                                               frame.ExtractBand(3) });

                byte[] pixels = reordered.WriteToMemory();
                int stride = canvasWidth * 4;
                int delay = delays != null && i < delays.Length ? Math.Max(delays[i], 20) : 100;

                // BitmapSource.Create is thread safe as long as we Freeze immediately
                var bitmap = BitmapSource.Create(canvasWidth, pageHeight, 96, 96,
                                                 PixelFormats.Bgra32, null, pixels, stride);
                bitmap.Freeze();

                result[i] = (bitmap, delay);
            });

            return result.ToList();
        }
        public void StartGifAnimation(Stream gifStream)
        {
            MemoryStream ms = new MemoryStream();
            gifStream.CopyTo(ms);
            ms.Position = 0;

            if (timer != null)
            {
                timer.Stop();
                timer = null;
            }
            var decoder = new GifBitmapDecoder(
                ms,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frames = decoder.Frames.ToList();
            int frameIndex = 0;
            BitmapFrame image = frames[0];
            List<(int, int)> framesList = new List<(int, int)>();
            List<(int, bool)> framesControl = new List<(int, bool)>();
            ms.Position = 0;
            IEnumerable<MetadataExtractor.Directory> directories = GifMetadataReader.ReadMetadata(ms);
            var directoriesImage = directories.Where(dir => dir.Name.ToLower().Contains("image")).ToList();
            var directoriesControl = directories.Where(dir => dir.Name.ToLower().Contains("control")).ToList();
            var directoriesHeader = directories.Where(dir => dir.Name.ToLower().Contains("head")).ToList();


            //foreach (var directory in directories)
            //foreach (var tag in directory.Tags)
            //{
            //System.Diagnostics.Debug.WriteLine("{0} ", directory.GetDescription(2));
            //System.Diagnostics.Debug.WriteLine("{0} : {1} | {2} | {3}", tag.Name, tag.Description, tag.Type, tag.DirectoryName);
            //}

            foreach (var directory in directoriesImage)
            {
                framesList.Add((Int32.Parse(directory.GetString(1)), Int32.Parse(directory.GetString(2))));
            }
            foreach (var directory in directoriesControl)
            {
                bool restore = false;
                //System.Diagnostics.Debug.WriteLine(directory.GetBoolean(2));
                if (directory.GetDescription(2).ToLower().Contains("restore"))
                {
                    restore = true;
                }
                framesControl.Add((Int32.Parse(directory.GetString(1)), restore));
            }

            gifImg = image;
            if (gifThread != null && gifThread.IsAlive)
            {
                gifThread.Interrupt();
            }
            gifThread = new Thread(() =>
               {
                   try
                   {
                       while (true)
                       {
                           //System.Diagnostics.Deb/ug.WriteLine(framesControl[frameIndex].Item1 + ", " + framesControl[frameIndex].Item2 + ", " + frameIndex);

                           //if (framesList.Count > frameIndex && !framesControl[frameIndex].Item2 && frameIndex > 0)
                           //{
                           //if (0 == frameIndex)
                           //{
                           //    image = frames[frameIndex];
                           //}
                           //else
                           //{
                           image = OverlayBitmapFrames(image, frames[frameIndex], framesList[frameIndex].Item1, framesList[frameIndex].Item2, framesControl[frameIndex].Item2);
                           //}


                           image.Freeze();
                           //}
                           //else if (frames.Count > frameIndex)
                           //{
                           //    image = frames[frameIndex];
                           //    image.Freeze();
                           //}
                           // Freeze for cross-thread access
                           this.Dispatcher.Invoke(new Action(() =>
                           {
                               ComicDisplay.Source = image;
                               try
                               {
                                   SetTitleText(" | Frame: " + (frameIndex + 1) + "/" + frames.Count);
                               }
                               catch
                               {

                               }
                           }), DispatcherPriority.Send);



                           if (framesControl.Count > frameIndex)
                           {
                               Thread.Sleep(framesControl[frameIndex].Item1 * 10);
                           }
                           else
                           {
                               Thread.Sleep(1000);
                           }

                           frameIndex = (frameIndex + 1) % frames.Count;



                       }
                   }
                   catch (ThreadInterruptedException exception)
                   {
                       //System.Diagnostics.Debug.WriteLine(exception.Message);
                   }

               });

            gifThread.Start();
        }

        public void StartWebpAnimation(Stream webpStream)
        {
            List<(BitmapSource Bitmap, int Delay)> frames = ExtractWebPFrames(webpStream);


            int frameIndex = 0;
            BitmapSource image = frames[0].Bitmap;
            if (gifThread != null && gifThread.IsAlive)
            {
                gifThread.Interrupt();
            }

            gifImg = image;
            gifThread = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        image = frames[frameIndex].Bitmap;
                        image.Freeze(); // Freeze for cross-thread access
                        this.Dispatcher.Invoke(new Action(() =>
                        {
                            ComicDisplay.Source = image;
                            try
                            {
                                SetTitleText(" | Frame: " + (frameIndex + 1) + "/" + frames.Count);
                            }
                            catch { }
                        }), DispatcherPriority.Send);

                        Thread.Sleep(frames[frameIndex].Delay);

                        frameIndex = (frameIndex + 1) % frames.Count;
                    }
                }
                catch (ThreadInterruptedException exception)
                {
                    //System.Diagnostics.Debug.WriteLine(exception.Message);
                }

            });

            gifThread.Start();
        }

        public BitmapFrame OverlayBitmapFrames(BitmapFrame background, BitmapFrame overlay, int x, int y, bool restore)
        {
            int width = background.PixelWidth;
            int height = background.PixelHeight;
            double dpiX = background.DpiX;
            double dpiY = background.DpiY;

            var drawingVisual = new DrawingVisual();

            using (var dc = drawingVisual.RenderOpen())
            {

                // Draw background first
                if (restore)
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(0, 0, width, height));
                }
                else
                {
                    dc.DrawImage(background, new Rect(0, 0, width, height));
                }


                // Draw overlay on top (same size, or specify a different Rect to position/scale it)
                dc.DrawImage(overlay, new Rect(x, y, overlay.Width, overlay.Height));
            }

            // Render both layers into a single bitmap
            var renderTarget = new RenderTargetBitmap(width, height, dpiX, dpiY, PixelFormats.Pbgra32);
            renderTarget.Render(drawingVisual);

            // Wrap result back into a BitmapFrame
            return BitmapFrame.Create(renderTarget);
        }
        private void UpdateInfo()
        {
            if (_cache.ContainsKey(_currentPage) || gifImg != null)
            {


                ImageContainer currentImage = new ImageContainer(gifImg != null ? gifImg : _cache[_currentPage].ResizedImage, gifImg != null ? (int)gifImg.PixelWidth : _cache[_currentPage].OriginalWidth, gifImg != null ? (int)gifImg.PixelHeight : _cache[_currentPage].OriginalHeight);

                dimensionResized = (int)currentImage.ResizedImage.PixelWidth + "x" + (int)currentImage.ResizedImage.PixelHeight;
                dimensionOriginal = (int)currentImage.OriginalWidth + "x" + (int)currentImage.OriginalHeight;
                string dimensionStr = dimensionResized == dimensionOriginal ? dimensionResized : dimensionResized + " (Original: " + dimensionOriginal + ")";

                if (currentImage.ResizedImage.PixelWidth == currentImage.OriginalWidth)
                {
                    dimensionStr = dimensionOriginal;
                }
                else if (currentImage.ResizedImage.PixelWidth < currentImage.OriginalWidth)
                {
                    dimensionStr = dimensionOriginal + " (Down: " + dimensionResized + ")";
                }
                else if (currentImage.ResizedImage.PixelWidth > currentImage.OriginalWidth)
                {
                    dimensionStr = dimensionOriginal + " (Up: " + dimensionResized + ")";
                }
                dimensionStr += " (View: " + MainScroll.ViewportWidth + "x" + MainScroll.ViewportHeight + ")";
                size = ToSize(_pages[_currentPage].Length, SizeUnits.MB) + " MB";

                string strResized = currentImage.ResizedImage != null ? "Resized" : "Original";
                string cachedStr = "";



                for (int i = 0; i < _cache.Keys.ToList().Count; i++)
                {
                    cachedStr += _cache.Keys.ToList()[i] + 1;
                    if (i + 1 < _cache.Count)
                    {
                        cachedStr += ", ";
                    }
                }
                TextSlider.Text = dimensionStr + "\n" + size + "\n" + (_currentPage + 1) + "/" + _pages.Count + " (Cached: " + cachedStr + ")";
                SetTitleText("");
            }
        }

        private BitmapSource ScaleImage(BitmapSource imageToShow, byte[] imageData, int index)
        {
            System.Diagnostics.Debug.WriteLine("ScaleImage");
            _cache.TryRemove(index, out _);
            System.GC.Collect();
            int OWidth = (int)imageToShow.PixelWidth;
            int OHeight = (int)imageToShow.PixelHeight;

            int viewWidth = (int)MainScroll.ViewportWidth;
            int viewHeight = (int)MainScroll.ViewportHeight;


            System.Diagnostics.Debug.WriteLine("Original: " + OWidth + "x" + OHeight);
            System.Diagnostics.Debug.WriteLine("View: " + viewWidth + "x" + viewHeight);
            //this.Dispatcher.Invoke(new Action(() =>
            //{

            //    RenderOptions.SetBitmapScalingMode(ComicDisplay, BitmapScalingMode.NearestNeighbor);
            //}));
            double ratioView = (double)viewWidth / (double)MainScroll.ViewportHeight;
            double ratioImg = (double)OWidth / (double)OHeight;

            double ratioWidth = (double)OWidth / (double)viewWidth;
            double ratioHeight = (double)OHeight / (double)viewHeight;

            int newWidth = (int)viewWidth;
            int newHeight = (int)Math.Round(OHeight / ratioWidth);


            // Upscale if the image is smaller than the viewport,
            // otherwise just decode with BitmapImage which is faster for large images that don't need to be resized as much
            //if (viewWidth > OWidth && _isFitWidth ||
            //    !_isFitWidth && ((viewWidth > OWidth && ratioImg > ratioView) || (viewHeight > OHeight && ratioView > ratioImg)))
            if (viewWidth > OWidth && _isFitWidth ||
             !_isFitWidth && ((viewWidth > OWidth && ratioImg > ratioView) || (viewHeight > OHeight && ratioView > ratioImg)))
            {
                BitmapSource bImg;
                if (!_isFitWidth)
                {
                    if (ratioImg > ratioView)
                    {
                        newHeight = (int)Math.Round(viewWidth / ratioImg);

                    }
                    else
                    {
                        newHeight = (int)viewHeight;
                        newWidth = (int)Math.Round(OWidth / ratioHeight);
                    }

                }
                bImg = NetVipsLanczosUpscaler.Upscale(imageToShow, imageData, newWidth, newHeight);

                _cache.TryAdd(index, new ImageContainer(bImg, OWidth, OHeight));
                System.GC.Collect();
                return bImg;
            }
            // Downscale with BitmapImage's built in decoder which is faster than resizing with
            // Lanczos for large images that don't need to be resized as much
            else
            {
                BitmapImage bImg = new BitmapImage();
                BmpBitmapEncoder encoder = new BmpBitmapEncoder();
                MemoryStream memoryStream = new MemoryStream();
                // 2. Push the BitmapSource into the encoder
                encoder.Frames.Add(BitmapFrame.Create(imageToShow));
                // 3. Save the encoder's data to the stream
                encoder.Save(memoryStream);
                // 4. Initialize the BitmapImage from the stream
                bImg.BeginInit();
                bImg.StreamSource = new MemoryStream(memoryStream.ToArray());
                bImg.CacheOption = BitmapCacheOption.OnLoad; // Important for memory management
                if (!_isFitWidth)
                {
                    if (ratioImg > ratioView)
                    {
                        bImg.DecodePixelWidth = viewWidth;
                    }
                    else
                    {
                        bImg.DecodePixelHeight = viewHeight;
                    }
                }
                else
                {
                    bImg.DecodePixelWidth = viewWidth;
                }

                bImg.EndInit();
                bImg.Freeze(); // Makes it cross-thread accessible
                memoryStream.Close();
                _cache.TryAdd(index, new ImageContainer(bImg, OWidth, OHeight));
                System.GC.Collect();

                return bImg;
            }

        }

        private async Task<BitmapSource> LoadAndProcessImage(int index)
        {

            return await Task.Run(async () =>
            {
                if (semQ.CurrentCount == 0)
                {
                    return null;
                }

                if (_cache.TryGetValue(index, out ImageContainer c1))
                {
                    semImg.Release();
                    return c1.ResizedImage;
                }

                semQ.Wait();
                semImg.Wait();
                semQ.Release();

                if (_cache.TryGetValue(index, out ImageContainer c2))
                {
                    semImg.Release();
                    return c2.ResizedImage;
                }

                BitmapSource imageToShow = null;

                var stream = _pages[index].Open();
                var ms = new MemoryStream();
                try
                {
                    stream.CopyTo(ms);
                    ms.Position = 0;

                    if (_pages[index].Name.ToLower().Contains(".jxl"))
                    {
                        JxlDecodeOptions options = new JxlDecodeOptions
                        {
                            Threads = Environment.ProcessorCount,
                            SkipOrientation = false,
                        };

                        var imageJxl = JxlDecoder.DecodeAsBitmapSource(ms.ToArray(), options);

                        //image.Freeze();
                        imageJxl.Freeze();
                        BitmapSource resized = ScaleImage(imageJxl, null, index);

                        resized.Freeze();

                        //_cache.TryAdd(index, new ImageContainer(resized, image));
                        imageToShow = resized;
                    }
                    else if (_pages[index].Name.ToLower().Contains(".jpg") || _pages[index].Name.ToLower().Contains(".jpeg"))
                    {
                        TJDecompressor td = new TJDecompressor();
                        DecompressedImage di = td.Decompress(ms.ToArray(), TJPixelFormats.TJPF_BGRA, TJFlags.ACCURATEDCT);
                        int stride = (int)di.Width * 4;
                        BitmapSource imageJpg = BitmapSource.Create(di.Width, di.Height, 96, 96, PixelFormats.Bgra32, null, di.Data, stride);

                        imageJpg.Freeze();
                        td.Dispose();

                        BitmapSource resized = ScaleImage(imageJpg, null, index);
                        resized.Freeze();

                        imageToShow = resized;
                    }
                    else
                    {

                        int OWidth = 0;
                        int OHeight = 0;
                        int newWidth = 0;
                        int newHeight = 0;

                        try
                        {
                            ms.Position = 0;
                            var directories = ImageMetadataReader.ReadMetadata(ms);
                            foreach (var directory in directories)
                                foreach (var tag in directory.Tags)
                                {
                                    if (tag.Name.Contains("Width") && int.TryParse(Regex.Replace(tag.Description, @"[^0-9]", ""), out int width))
                                    {
                                        OWidth = width;
                                    }
                                    else if (tag.Name.Contains("Height") && int.TryParse(Regex.Replace(tag.Description, @"[^0-9]", ""), out int height))
                                    {
                                        OHeight = height;
                                    }
                                    //System.Diagnostics.Debug.WriteLine("{0} : {1}", tag.Name, tag.Description);
                                }
                        }
                        catch (Exception ex)
                        {
                            //System.Diagnostics.Debug.WriteLine(ex.Message);
                            //System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                        }
                        bool useUpscale = true;

                        if (_isFitWidth && OWidth > (int)MainScroll.ViewportWidth)
                        //if (_isFitWidth)
                        {
                            newWidth = (int)MainScroll.ViewportWidth;
                            useUpscale = false;
                        }
                        else if (!_isFitWidth && (OHeight > (int)MainScroll.ViewportHeight || OWidth > (int)MainScroll.ViewportWidth))
                        //else if (!_isFitWidth)
                        {
                            double ratioView = (double)MainScroll.ViewportWidth / (double)MainScroll.ViewportHeight;
                            double ratioImg = (double)OWidth / (double)OHeight;
                            if (ratioImg > ratioView)
                            {
                                newWidth = (int)MainScroll.ViewportWidth;
                            }
                            else
                            {
                                newHeight = (int)MainScroll.ViewportHeight;
                            }
                            useUpscale = false;
                        }

                        BitmapSource image = null;

                        image = new BitmapImage();


                        ms.Position = 0;
                        ((BitmapImage)image).BeginInit();
                        ((BitmapImage)image).CacheOption = BitmapCacheOption.OnLoad;
                        if (newWidth > 0)
                        {
                            ((BitmapImage)image).DecodePixelWidth = newWidth;
                        }
                        else if (newHeight > 0)
                        {
                            ((BitmapImage)image).DecodePixelHeight = newHeight;
                        }
                        ((BitmapImage)image).StreamSource = ms;
                        ((BitmapImage)image).EndInit();
                        if (useUpscale)
                        {
                            OWidth = (int)image.PixelWidth;
                            OHeight = (int)image.PixelHeight;
                            image = ScaleImage(image, null, index);
                        }
                        //}
                        image.Freeze();

                        _cache.TryAdd(index, new ImageContainer(image, OWidth, OHeight));

                        imageToShow = image;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                    System.Diagnostics.Debug.WriteLine(ex.StackTrace);

                }
                finally
                {
                    ms.Close();
                }
                this.Dispatcher.Invoke(new Action(() =>
                {
                    UpdateInfo();
                    semImg.Release();
                }));
                System.GC.Collect();

                return imageToShow;
            });
        }

        private void SetFitWindow(object sender, RoutedEventArgs e)
        {
            WindowFit(true);
        }

        private void SetFitWidth(object sender, RoutedEventArgs e)
        {
            WindowFit(false);
        }

        private void WindowFit(bool fitToWindow)
        {
            _currentComicItem.FitToWindow = fitToWindow;
            double ratioImage = ratioImage = (double)ComicDisplay.ActualWidth / (double)ComicDisplay.ActualHeight;
            double ratioView = (double)MainScroll.ActualWidth / (double)MainScroll.ActualHeight;

            System.Diagnostics.Debug.WriteLine("FitWindow: " + fitToWindow + ", ratioImage: " + ratioImage + ", ratioView: " + ratioView);
            if (fitToWindow || ratioImage >= ratioView && ComicDisplay.Source != null)
            {
                _isFitWidth = false;
                //ScrollThresholdLimit = ScrollFast;
                // 1. Remove the explicit width/height constraints so it can shrink
                ComicDisplay.ClearValue(FrameworkElement.WidthProperty);
                ComicDisplay.ClearValue(FrameworkElement.HeightProperty);

                // 2. Set stretch to fit the whole image inside the box
                ComicDisplay.Stretch = System.Windows.Media.Stretch.Uniform;

                // 3. Kill the scrollbars entirely
                MainScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                MainScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            else
            {
                _isFitWidth = true;
                //ScrollThresholdLimit = ScrollSlow;
                // 1. Re-enable vertical scrolling
                MainScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
                MainScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

                // 2. Force the image width to match the window width
                // We use a binding or direct assignment
                ComicDisplay.Stretch = System.Windows.Media.Stretch.Uniform;
                ComicDisplay.Width = MainScroll.Width;


            }

            //SaveJson();
            //_cache.Clear();
            //System.Diagnostics.Debug.WriteLine("WindowFit");
            //DisplayPage(0, 55);
        }

        private void SaveJson()
        {

            if (_currentFile == "" || _currentFile == null)
            {
                return;
            }

            if (_currentComicItem != null)
            {
                //Stopwatch sw = new Stopwatch();
                //sw.Start();

                //int index =  jsonComic.List.BinarySearch(currentComicItem, compare);
                //jsonComic.List.RemoveAt(index);
                jsonComic.List.Remove(_currentComicItem);
                //sw.Stop();

                //System.Diagnostics.Debug.WriteLine("Search={0}, index={1}, size={2}", sw.Elapsed, 0, jsonComic.List.Count);
            }
            else
            {
                _currentComicItem = new ComicItem();
                _currentComicItem.Name = _currentFile;

            }
            if (_currentFilePath != null && File.Exists(_currentFilePath))
            {
                _currentComicItem.Parent = new FileInfo(_currentFilePath).Directory.FullName;
            }
            _currentComicItem.LastOpened = DateTime.Now.Ticks;
            _currentComicItem.Pos = _currentPage;
            _currentComicItem.FitToWindow = !_isFitWidth;


            jsonComic.List.Add(_currentComicItem);
            jsonComic.List.Sort(compare);
            jsonComic.WindowWidth = (int)this.Width;
            jsonComic.WindowX = (int)this.Left;
            jsonComic.WindowHeight = (int)this.Height;

            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(jsonComic, Formatting.None));
        }

        // --- NAVIGATION & SCROLLING ---

        private void MainScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (_isPageLoading)
            {
                lastScrollStart = DateTime.Now.Ticks;
                e.Handled = true;
                return;
            }
            if (scrollWait > DateTime.Now.Ticks / 10000)
            {
                //scrollWait = scrollWait - (DateTime.Now.Ticks - lastScrollStart) / 10000;
                e.Handled = true;
                return;
            }
            long scrollTime = DateTime.Now.Ticks;
            //


            MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset - e.Delta * 2);
            bool isAtBottom = MainScroll.VerticalOffset >= MainScroll.ScrollableHeight;
            bool isAtTop = MainScroll.VerticalOffset <= 0;

            //System.Diagnostics.Debug.WriteLine("Scroll");
            if (!_isFitWidth || !((scrollTime - lastScrollStart) / 10000 < 200 && (isAtBottom || isAtTop)))
            {
                if (e.Delta < 0)
                {
                    if (isAtBottom)
                    {
                        //_scrollAccumulator += Math.Abs(e.Delta); // Accumulate the "push"

                        //if (_scrollAccumulator >= ScrollThresholdLimit)
                        //{
                        if (_currentPage < _pages.Count - 1)
                        {
                            //_scrollAccumulator = 0; // Reset for the next page
                            _currentPage++;
                            DisplayPage(1, 1);
                            lastScrollStart = DateTime.Now.Ticks;
                        }
                        //}
                        e.Handled = true; // Block the actual scroll movement while at edge
                    }
                    else
                    {
                        // If they are scrolling down but NOT at the bottom, 
                        // reset the accumulator so they don't have "pre-charged" flips.
                        //_scrollAccumulator = 0;
                    }
                }
                // SCROLLING UP (Delta > 0)
                else if (e.Delta > 0)
                {
                    if (isAtTop)
                    {
                        //_scrollAccumulator += Math.Abs(e.Delta);

                        //if (_scrollAccumulator >= ScrollThresholdLimit)
                        //{
                        if (_currentPage > 0)
                        {
                            //_scrollAccumulator = 0;
                            _currentPage--;
                            DisplayPage(-1, 6);
                            lastScrollStart = DateTime.Now.Ticks;
                            //// Force UI update to calculate new ScrollableHeight 
                            //// before moving to the bottom of the previous page
                            //MainScroll.UpdateLayout();
                            ////MainScroll.ScrollToBottom();
                        }
                        //}
                        e.Handled = true;
                    }
                    else
                    {
                        //_scrollAccumulator = 0;
                    }
                }
            }

            lastScrollStart = DateTime.Now.Ticks;
        }
        private void MainScroll_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Only drag if the left mouse button is pressed
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                _lastMousePosition = e.GetPosition(MainScroll);
                _startVerticalOffset = MainScroll.VerticalOffset;
                _isDragging = true;

                // Capture the mouse so it doesn't "slip" off the window while dragging
                MainScroll.CaptureMouse();
            }
            else if (e.MiddleButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                ChangeWindowState();
                //Application.Current.MainWindow.WindowStyle = WindowStyle.None;
                //Application.Current.MainWindow.WindowState = WindowState.Maximized;
                //Application.Current.MainWindow.ResizeMode = System.Windows.ResizeMode.NoResize;

            }
        }

        private static void ChangeWindowState()
        {
            if (Application.Current.MainWindow.WindowState == WindowState.Maximized)
            {
                Application.Current.MainWindow.WindowState = WindowState.Normal;
                Application.Current.MainWindow.WindowStyle = WindowStyle.SingleBorderWindow;
                Application.Current.MainWindow.ResizeMode = System.Windows.ResizeMode.CanResize;
                Application.Current.MainWindow.BorderThickness = new Thickness(2, 0, 2, 0);

            }
            else if (Application.Current.MainWindow.WindowState == WindowState.Normal)
            {
                Application.Current.MainWindow.WindowStyle = WindowStyle.None;
                Application.Current.MainWindow.WindowState = WindowState.Maximized;
                Application.Current.MainWindow.ResizeMode = System.Windows.ResizeMode.NoResize;
                Application.Current.MainWindow.BorderThickness = new Thickness(7, 7, 7, 7);
            }
        }

        private void MainScroll_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDragging && !_isPageLoading)
            {
                System.Windows.Point currentPosition = e.GetPosition(MainScroll);

                // Calculate how far the mouse has moved
                double deltaY = currentPosition.Y - _lastMousePosition.Y;

                // Update the scroll position. 
                // We subtract the delta because "pulling" the mouse down 
                // should move the scrollbar UP (scrolling towards the top).
                MainScroll.ScrollToVerticalOffset(_startVerticalOffset - deltaY * 2);

                // Optional: If you want to use the "Push to turn page" logic while dragging,
                // you would check the boundaries here. However, usually dragging 
                // is best kept strictly for viewing the current page.
            }
        }

        private void MainScroll_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                MainScroll.ReleaseMouseCapture();
            }
        }

        private void Prev_Click(object sender, RoutedEventArgs e) { if (_currentPage > 0) { _currentPage--; DisplayPage(-1, 2); } }
        private void Next_Click(object sender, RoutedEventArgs e) { if (_currentPage < _pages.Count - 1) { _currentPage++; DisplayPage(1, 3); } }

        private void StackPanel_MouseEnter(object sender, MouseEventArgs e)
        {
            ((StackPanel)e.Source).Opacity = 0.6;
        }

        private void StackPanel_MouseLeave(object sender, MouseEventArgs e)
        {
            ((StackPanel)e.Source).Opacity = 0;
        }


        private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key.Equals(Key.Left))
            {
                if (_currentPage > 0) { _currentPage--; DisplayPage(-1, 2); }
            }
            else if (e.Key.Equals(Key.Right))
            {
                if (_currentPage < _pages.Count - 1) { _currentPage++; DisplayPage(1, 3); }
            }
            else if (e.Key.Equals(Key.F11))
            {
                ChangeWindowState();
            }
        }
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key.Equals(Key.Up))
            {
                MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset - 300);
            }
            else if (e.Key.Equals(Key.Down))
            {
                MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset + 300);
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            Exit();
        }
        private void Menu_Closing(object sender, RoutedEventArgs e)
        {
            Exit();
        }
        public void Exit()
        {
            SaveJson();
            try
            {
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }
            catch
            {

            }
            Environment.Exit(0);
        }

        private void MoveToTrash(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Delete: " + _currentFile + " ?", "", MessageBoxButton.YesNo);
            if (result == MessageBoxResult.Yes && _currentFilePath != null && File.Exists(_currentFilePath))
            {
                _archive?.Dispose();
                FileSystem.DeleteFile(_currentFilePath, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
                jsonComic.List.Remove(_currentComicItem);
                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(jsonComic, Formatting.None));

                Application.Current.Shutdown();
            }
        }
        private void ExploreZip(object sender, RoutedEventArgs e)
        {
            if (_currentFilePath != null && File.Exists(_currentFilePath))
            {
                String run = "explorer.exe /select, " + "\"" + _currentFilePath + "\"";
                ProcessStartInfo ProcessInfo;
                ProcessInfo = new ProcessStartInfo("cmd.exe", "/K " + run);
                ProcessInfo.CreateNoWindow = true;

                Process.Start(ProcessInfo);
            }

        }
        private void Scaling_Click1(object sender, RoutedEventArgs e)
        {
            ultraScaling = false;
            RenderOptions.SetBitmapScalingMode(ComicDisplay, BitmapScalingMode.HighQuality);
        }

        private void Scaling_Click2(object sender, RoutedEventArgs e)
        {
            ultraScaling = false;
            RenderOptions.SetBitmapScalingMode(ComicDisplay, BitmapScalingMode.LowQuality);
        }

        private void Scaling_Click3(object sender, RoutedEventArgs e)
        {
            ultraScaling = false;
            RenderOptions.SetBitmapScalingMode(ComicDisplay, BitmapScalingMode.NearestNeighbor);
        }
        private void Scaling_Click4(object sender, RoutedEventArgs e)
        {
            ultraScaling = true;
            RenderOptions.SetBitmapScalingMode(ComicDisplay, BitmapScalingMode.NearestNeighbor);
        }

        private void Forward_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.IsPlaying)
            {
                //System.Diagnostics.Debug.WriteLine("Forward! {}, {}", _mediaPlayer.Time, _mediaPlayer.Length);
                _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(Math.Min((_mediaPlayer.Time + 2000), _mediaPlayer.Length)));
            }
        }
        private void Backward_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(Math.Max(_mediaPlayer.Time - 2000, 0)));
            }
        }

        private void videoView_GotFocus(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("Focus! ");
        }

        private void videoView_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            mainWindow.Focus();
            contextMenu.IsOpen = true;
        }
        private void videoView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            mainWindow.Focus();
        }

        private void ComicDisplay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if ((int)ComicDisplay.ActualHeight <= (int)MainScroll.ActualHeight)
            {
                System.Diagnostics.Debug.WriteLine("Window!");
                _isFitWidth = false;
                //ScrollThresholdLimit = ScrollFast;

            }
            else
            {
                System.Diagnostics.Debug.WriteLine("Width!");
                _isFitWidth = true;
                //ScrollThresholdLimit = ScrollSlow;

            }


        }

        private void mainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            //if (!ultraScaling || (currentImage != null && MainScroll.ActualWidth >= currentImage.OriginalImage.PixelWidth))
            //{
            //    return;
            //}


            windowSizeChanged();
        }

        private void windowSizeChanged()
        {
            if (thSizeChanged != null && thSizeChanged.IsAlive)
            {
                thSizeChanged.Interrupt();
            }


            thSizeChanged = (new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 10; i++)
                    {

                        Thread.Sleep(20);
                    }
                    System.Diagnostics.Debug.WriteLine("Size changed! ");
                    if (semImg.CurrentCount > 0)
                    {
                        this.Dispatcher.Invoke(new Action(() =>
                        {
                            _cache.Clear();
                            DisplayPage(0, 6);

                        }));
                    }

                }
                catch (ThreadInterruptedException ex)
                {

                }
            }));
            thSizeChanged.Start();
        }

        private void mainWindow_StateChanged(object sender, EventArgs e)
        {
            windowSizeChanged();
        }

        private void Slider_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed && e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            //int value = (int)Slider.Value;
            if (thSliderChanged != null && thSliderChanged.IsAlive)
            {
                thSliderChanged.Interrupt();
            }


            thSliderChanged = (new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 10; i++)
                    {

                        Thread.Sleep(20);
                    }

                    if (semImg.CurrentCount > 0)
                    {

                        _cache.Clear();
                        System.Diagnostics.Debug.WriteLine("thSliderChanged");
                        this.Dispatcher.Invoke(new Action(() =>
                        {

                            _currentPage = (int)Slider.Value;
                            DisplayPage(0, 4);
                        }));

                    }
                }
                catch (Exception ex)
                {

                }
            }));
            thSliderChanged.Start();
        }
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            titleBarLocked = !titleBarLocked;
            TitleBar.Opacity = 0.7;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void mainWindow_MouseMove(object sender, MouseEventArgs e)
        {

            System.Windows.Point position = e.GetPosition(this);
            //System.Diagnostics.Debug.WriteLine("Mouse move: " + position.Y);
            if (!titleBarLocked)
            {
                if (position.Y <= 30)
                {
                    TitleBar.Opacity = 0.6;
                }
                else
                {

                    TitleBar.Opacity = 0;
                }
            }

        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                int deltaX = (int)(e.GetPosition(this).X - _lastTitlePos.X);
                int deltaY = (int)(e.GetPosition(this).Y - _lastTitlePos.Y);

                this.Left += deltaX;
                this.Top += deltaY;

                if (this.Top < 0)
                {
                    this.Top = 0;
                }
                //System.Windows.Point GetMousePos() => );
                //if (_lastTitlePos != null)
                //{

                //}
                //mainWindow.PointToScreen(Mouse.GetPosition(mainWindow))
                //System.Diagnostics.Debug.WriteLine("Mouse move: " + _lastTitlePos.X);
            }
            else
            {
                _lastTitlePos = e.GetPosition(this);
            }

        }
    }

    // Helper to ensure "Page 2.jpg" comes before "Page 10.jpg"
    public class NaturalSortComparer : IComparer<string>
    {
        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);
        public int Compare(string x, string y) => StrCmpLogicalW(x, y);
    }
    class GFG : IComparer<ComicItem>
    {
        public int Compare(ComicItem a, ComicItem b)
        {
            return a.Name.CompareTo(b.Name);
        }
    }
}