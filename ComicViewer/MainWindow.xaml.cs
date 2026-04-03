

using ComicViewer.Imaging;
using ComicViewer.Objects;
using ExifLibrary;
//using ImageMagick;
using LibVLCSharp.Shared;
using MetadataExtractor;
using MetadataExtractor.Formats.Gif;
using MetadataExtractor.Formats.Heif;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using NetVips;
using Newtonsoft.Json;
using PhotoSauce.MagicScaler;
using SharpCompress.Archives;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Xps.Packaging;
using Image = NetVips.Image;
using Path = System.IO.Path;


namespace ComicViewer
{
    public partial class MainWindow : System.Windows.Window
    {
        private IArchive _archive;
        private List<IArchiveEntry> _pages = new List<IArchiveEntry>();
        private int _currentPage = 0;
        private bool _isPageLoading = false;
        private static SemaphoreSlim semImg = new SemaphoreSlim(1, 1);
        private static SemaphoreSlim semDisplay = new SemaphoreSlim(1, 1);
        private static SemaphoreSlim semQ = new SemaphoreSlim(3, 3);
        private String _currentFile = "";
        private String _currentFilePath = "";
        private double scrollWait = 0;
        private bool titleBarLocked = false;
        private long lastScrollStart = DateTime.Now.Ticks;
        private System.Windows.Point _lastMousePosition;
        private System.Windows.Point _MouseHidePos;
        private double _startVerticalOffset;
        private bool _isDragging = false;
        public static Enums.Kernel _scalingAlgo = Enums.Kernel.Nearest;
        private string size = "";
        private string jsonPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\" + "comic.json";
        private System.Windows.Point _lastTitlePos;
        private static ConcurrentDictionary<int, ImageContainer> _cache = new ConcurrentDictionary<int, ImageContainer>();
        private bool _isFitWidth = false;
        //private JsonComic jsonComic = new JsonComic();
        private ComicItem _currentComicItem = null;
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
        private MemoryStream _mediaStream;
        private StreamMediaInput _mediaInput;
        private Media _media;
        private SolidColorBrush mainBackground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1e, 0x1e, 0x1e, 0x1e));
        private GFG compare = new GFG();
        private Thread gifThread = null;
        private bool _isVideoLoaded = false;
        private BitmapSource gifImg = null;
        public const string libJxlMain = "jxl/bin/jxl.dll";
        public const string libJxlThreads = "jxl/bin/jxl_threads.dll";
        private int _loadTimeMs = 0;
        private bool _IsWebtoon = false;
        private int _WebtoonStartPage = -1;
        private int _webtoonMargin = 0;
        private int _hideMouseCnt = 0;
        private int _updateJsonCnt = 0;
        private int _sizeChangeCnt = 0;
        private int _sliderChangeCnt = 0;
        private bool _isMouseOverSlider = false;
        private bool _MagicScale = false;
        private bool _noScale = false;
        private List<(int, System.Windows.Controls.Image)> webToonList = new List<(int, System.Windows.Controls.Image)>();
        public static Log Log = new Log();
        private bool _skipInQueue = false;


        public MainWindow()
        {

            InitializeComponent();


            this.Top = 0;
            //System.Diagnostics.Debug.WriteLine(jsonPath);
            Log.add(jsonPath, false);


            //videoView.Loaded += VideoView_Loaded;

            ComicDisplay.Loaded += MainScroll_Loaded;


            //var options = new string[]
            //{
            //    "--demux=avcodec","--input-repeat=65535"
            //    // VLC options can be given here. Please refer to the VLC command line documentation.
            //};
        }

        private JsonComic LoadJson()
        {
            JsonComic jsonComic = new JsonComic();

            if (!File.Exists(jsonPath))
            {
                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(new JsonComic(), Formatting.None));
            }
            else
            {
                jsonComic = JsonConvert.DeserializeObject<JsonComic>(File.ReadAllText(jsonPath));
            }
            return jsonComic;
        }

        private void MainScroll_Loaded(object sender, RoutedEventArgs e)
        {
            //JsonSerializerSettings js = new JsonSerializerSettings();
            JsonComic jsonComic = LoadJson();

            this.Width = jsonComic.WindowWidth > 300 ? jsonComic.WindowWidth : this.Width;
            this.Height = jsonComic.WindowHeight > 300 ? jsonComic.WindowHeight : this.Height;
            this.Left = jsonComic.WindowX > 0 ? jsonComic.WindowX : this.Left;


            UpdateJsonTask();
            HideMouse();
            windowSizeChanged();
            SliderChange();
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string argLower = args[i].ToLower();
                if (argLower.Contains(".zip") ||
                    argLower.Contains(".cbz") ||
                    argLower.Contains(".rar") ||
                    argLower.Contains(".7z"))
                {
                    System.Diagnostics.Debug.WriteLine("Open zip: " + args[i]);
                    LoadArchive(args[i]);
                    break;
                }
            }
        }
        private void HideMouse()
        {
            Thread thHideMouse = (new Thread(() =>
               {
                   while (true)
                   {
                       try
                       {
                           Thread.Sleep(100);

                           if (_hideMouseCnt > 0)
                           {
                               while (_hideMouseCnt > 0)
                               {
                                   Thread.Sleep(100);
                                   _hideMouseCnt--;
                               }
                               this.Dispatcher.Invoke(new Action(() =>
                               {
                                   this.Cursor = Cursors.None;
                                   //Debug.WriteLine("Hide mouse");
                               }));
                           }

                       }
                       catch (Exception ex)
                       {

                           //Debug.WriteLine("tttt");
                       }
                   }

               }));
            thHideMouse.Start();
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

        private void VideoView_Inititlize()
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
                                    size = ToSize(_pages[_currentPage].Size, SizeUnits.MB) + " MB";

                                    TextSliderVideo.Text = _pages[_currentPage].Key + "\n" +
                                    "Time: " + (_mediaPlayer.Time / 1000) + "s / " + (_mediaPlayer.Length / 1000) + "s" + "\n" +
                                    size + "\n" + (_currentPage + 1) + "/" + _pages.Count;
                                    if (_mediaPlayer.Length != 0)
                                    {
                                        mediaSlider.Value = ((double)_mediaPlayer.Time / (double)_mediaPlayer.Length) * 100;
                                    }
                                }
                            }));

                        }
                        Thread.Sleep(200);
                    }
                    catch (Exception exception)
                    {
                        //System.Diagnostics.Debug.WriteLine(exception.Message);
                    }
                }

            }));
            th.Start();

            if (videoView.IsLoaded)
            {
                _isVideoLoaded = true;
            }
            else
            {
                videoView.Loaded += VideoView_Loaded;
            }

        }

        private void VideoView_Loaded(object sender, RoutedEventArgs e)
        {
            _isVideoLoaded = true;
        }

        private void SetTitleText(String append)
        {
            String fileName = _currentFile.Length > 100 ? _currentFile.Substring(0, 100) + "..." : _currentFile;
            String pageName = _pages[_currentPage].Key.Length > 30 ?
                "..." + _pages[_currentPage].Key.Substring(_pages[_currentPage].Key.Length - 30, 30) : _pages[_currentPage].Key;
            String extension = System.IO.Path.GetExtension(_pages[_currentPage].Key).ToUpper().Replace(".", "");

            TitleText.Text = fileName + " | " + pageName + " | " + extension + " | Page: " + (_currentPage + 1) + "/" + _pages.Count + append;
            this.Title = fileName;
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
            _scalingAlgo = Enums.Kernel.Nearest;

            JsonComic jsonComic = LoadJson();

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
                //SaveJson(jsonComic);
            }



            _archive?.Dispose();
            _cache.Clear();
            if (System.IO.Path.GetExtension(filePath).ToLowerInvariant().Equals(".7z"))
            {
                _archive = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(filePath);
            }
            else if (System.IO.Path.GetExtension(filePath).ToLowerInvariant().Equals(".rar"))
            {
                _archive = SharpCompress.Archives.Rar.RarArchive.OpenArchive(filePath);
            }
            else
            {
                _archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(filePath);
            }

            _pages = _archive.Entries.ToList()
                .Where(entry =>
                {
                    string str = entry.Key.ToLower();
                    if (
                    (str.Contains(".jpg") ||
                    str.Contains(".jpeg") ||
                    str.Contains(".webp") ||
                    str.Contains(".png") ||
                    str.Contains(".jxl") ||
                    str.Contains(".jxr") ||
                    str.Contains(".tif") ||
                    str.Contains(".gif") ||
                    str.Contains(".webm") ||
                    str.Contains(".mkv") ||
                    str.Contains(".mp4")) &&
                    !(str.Contains("__macosx"))
                    )
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                )
                .OrderBy(entry => entry.Key, new NaturalSortComparer()) // Custom comparer for 1.jpg, 2.jpg, 10.jpg
                .ToList();

            Slider.Minimum = 0;
            Slider.Maximum = _pages.Count - 1;

            _currentPage = Math.Min(_currentComicItem.Pos, _pages.Count - 1);


            if (_pages.Any()) {; DisplayPage(1, 0); }
            WindowFit(_currentComicItem.FitToWindow, false, 0, 0);

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
            if (_WebtoonStartPage == _currentPage)
            {
                return;
            }
            if (_currentPage < 0 || _currentPage >= _pages.Count) return;

            // Lock the UI from further scroll-triggered page turns
            _isPageLoading = true;
            //
            //semDisplay.Wait();
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

                if (_pages[_currentPage].Key.ToLower().Contains(".webm") ||
                    _pages[_currentPage].Key.ToLower().Contains(".mp4") ||
                    _pages[_currentPage].Key.ToLower().Contains(".mkv"))
                {
                    mainWindow.Background = System.Windows.Media.Brushes.Black;
                    videoViewGrid.Visibility = Visibility.Visible;
                    videoView.Visibility = Visibility.Visible;
                    ComicDisplay.Source = null;

                    if (!_isVideoLoaded)
                    {

                        VideoView_Inititlize();
                        Thread thread = new Thread(() =>
                        {
                            while (!_isVideoLoaded)
                            {
                                Thread.Sleep(100);
                            }
                            StartNewVideo();
                        });
                        thread.Start();


                    }
                    else
                    {
                        StartNewVideo();
                    }

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

                    BitmapSource imageToShow = null;
                    //RenderOptions.SetBitmapScalingMode(ComicDisplay, BitmapScalingMode.NearestNeighbor);
                    //RenderOptions.SetBitmapScalingMode(ComicDisplay, BitmapScalingMode.HighQuality);


                    bool isAnimated = false;
                    if (_pages[_currentPage].Key.ToLower().Contains(".webp"))
                    {
                        try
                        {
                            MemoryStream ms = new MemoryStream();
                            _pages[_currentPage].OpenEntryStream().CopyTo(ms);
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
                        catch
                        {

                        }

                    }
                    else if (_pages[_currentPage].Key.ToLower().Contains(".gif"))
                    {
                        MemoryStream ms = new MemoryStream();
                        _pages[_currentPage].OpenEntryStream().CopyTo(ms);
                        ms.Position = 0;
                        IEnumerable<MetadataExtractor.Directory> directories = GifMetadataReader.ReadMetadata(ms);
                        var directoriesImage = directories.Where(dir => dir.Name.ToLower().Contains("image")).ToList();

                        if (directoriesImage.Count > 1)
                        {
                            isAnimated = true;
                        }


                        ms.Close();
                    }




                    if (_pages[_currentPage].Key.ToLower().Contains(".gif") && isAnimated)
                    {
                        StartGifAnimation(_pages[_currentPage].OpenEntryStream());

                    }
                    else if (_pages[_currentPage].Key.ToLower().Contains(".webp") && isAnimated)
                    {
                        StartWebpAnimation(_pages[_currentPage].OpenEntryStream());
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
                            //System.Diagnostics.Debug.WriteLine("wwww " + _currentPage);
                        }

                        if (imageToShow != null)
                        {
                            if (!_IsWebtoon)
                            {
                                _WebtoonStartPage = -1;
                                WindowFit(_currentComicItem.FitToWindow, false, imageToShow.Width, imageToShow.Height);
                                ComicDisplay.Source = imageToShow;
                                if (_currentPage + 1 <= _pages.Count - 1 && pageDiff > 0)
                                {
                                    LoadAndProcessImage(_currentPage + 1);
                                }
                                if (_currentPage - 1 >= 0 && pageDiff < 0)
                                {
                                    LoadAndProcessImage(_currentPage - 1);
                                }
                            }
                            else
                            {
                                WebtoonView(imageToShow);
                            }

                        }
                    }

                    UpdateInfo(imageToShow);

                    if (!_IsWebtoon)
                    {


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
                }

                Slider.Value = _currentPage;

                _updateJsonCnt = 5;

                await Task.Delay(1);
            }
            catch (Exception ex)
            {
                Log.add(ex.Message, true);
                Log.add(ex.StackTrace, true);
                //System.Diagnostics.Debug.WriteLine(ex.Message);
                //System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
            finally
            {
                _isPageLoading = false;
            }
        }

        private void UpdateJsonTask()
        {
            Thread th = (new Thread(() =>
             {
                 while (true)
                 {
                     try
                     {
                         Thread.Sleep(200);

                         if (_updateJsonCnt > 0)
                         {
                             while (_updateJsonCnt > 0)
                             {
                                 Thread.Sleep(1000);
                                 _updateJsonCnt--;
                             }
                             SaveJson(LoadJson());
                             System.GC.Collect();
                             Log.add("Update json", false);
                             //System.Diagnostics.Debug.WriteLine("Update json");
                         }
                     }
                     catch (Exception ex)
                     {

                     }
                 }

             }));
            th.Start();
        }

        private void WebtoonView(BitmapSource imageToShow)
        {
            if (_WebtoonStartPage == -1)
            {
                ComicDisplay.Source = imageToShow;
                ComicDisplay.Height = imageToShow.PixelHeight;
                ComicDisplay.Width = imageToShow.Width;
                ComicDisplay.Margin = new Thickness(0, 0, 0, _webtoonMargin);
                _WebtoonStartPage = _currentPage;
            }
            else
            {
                ComicDisplay.Margin = new Thickness(0, 0, 0, _webtoonMargin);

                double scroll = MainScroll.ScrollableHeight - MainScroll.VerticalOffset;
                for (int i = 0; i < ComicStack.Children.Count; i++)
                {
                    var img = (System.Windows.Controls.Image)ComicStack.Children[i];
                    img.Margin = new Thickness(0, 0, 0, _webtoonMargin);
                    if (i > 3 && i < ComicStack.Children.Count - 3)
                    {
                        img.Source = null;
                    }

                }
                //             < Image UseLayoutRounding = "True" SizeChanged = "ComicDisplay_SizeChanged"
                //x: Name = "ComicDisplay" Stretch = "Uniform"
                //RenderOptions.BitmapScalingMode = "HighQuality"

                System.Windows.Controls.Image image = new System.Windows.Controls.Image();
                image.Source = imageToShow;
                image.Height = imageToShow.PixelHeight;
                image.Margin = new Thickness(0, 0, 0, _webtoonMargin);
                image.Stretch = Stretch.Uniform;


                ComicStack.Children.Add(image);
            }
        }

        private void StartNewVideo()
        {
            Stream stream = _pages[_currentPage].OpenEntryStream();
            _mediaStream = new MemoryStream();
            stream.CopyTo(_mediaStream);
            _mediaStream.Position = 0;
            _mediaInput = new StreamMediaInput(_mediaStream);
            _media = new Media(_libVLC, _mediaInput);
            _mediaPlayer.Media = _media;
        }

        public static List<(BitmapSource Bitmap, int Delay)> ExtractWebPFrames(Stream webpStream)
        {
            byte[] webpBytes;
            using (var ms = new MemoryStream())
            {
                webpStream.CopyTo(ms);
                webpBytes = ms.ToArray();
            }

            var vipsImage = global::NetVips.Image.WebploadBuffer(webpBytes, n: -1);

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
                                                 System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
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
                        image = OverlayBitmapFrames(image, frames[frameIndex], framesList[frameIndex].Item1, framesList[frameIndex].Item2, framesControl[frameIndex].Item2);
                        image.Freeze();
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
                catch (Exception exception)
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
                catch (Exception exception)
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
            var renderTarget = new RenderTargetBitmap(width, height, dpiX, dpiY, System.Windows.Media.PixelFormats.Pbgra32);
            renderTarget.Render(drawingVisual);

            // Wrap result back into a BitmapFrame
            return BitmapFrame.Create(renderTarget);
        }
        private void UpdateInfo(BitmapSource bi)
        {
            if (_IsWebtoon && bi != null)
            {
                TextSlider.Text = String.Format("{0}x{1} (View: {2}x{3})\n{4}/{5}",
                    bi.PixelWidth, bi.PixelHeight, MainScroll.ViewportWidth, MainScroll.ViewportHeight, (_currentPage + 1), _pages.Count);
                SetTitleText("");
            }
            else if (_cache.ContainsKey(_currentPage) || gifImg != null)
            {


                ImageContainer currentImage = new ImageContainer(gifImg != null ? gifImg : _cache[_currentPage].ResizedImage, gifImg != null ? (int)gifImg.PixelWidth : _cache[_currentPage].OriginalWidth, gifImg != null ? (int)gifImg.PixelHeight : _cache[_currentPage].OriginalHeight);

                string dimensionResized = (int)currentImage.ResizedImage.PixelWidth + "x" + (int)currentImage.ResizedImage.PixelHeight;
                string dimensionOriginal = (int)currentImage.OriginalWidth + "x" + (int)currentImage.OriginalHeight;
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
                dimensionStr += String.Format(" (View: {0}x{1})", MainScroll.ViewportWidth, MainScroll.ViewportHeight);
                //    " (View: " + MainScroll.ViewportWidth + "x" + MainScroll.ViewportHeight + ")";

                size = String.Format("{0} MB, Load: {1} ms", ToSize(_pages[_currentPage].Size, SizeUnits.MB), _loadTimeMs);

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
                if (_MagicScale)
                {
                    size += " (MagicScaler)";
                }
                TextSlider.Text = String.Format("{0}\n{1}\n{2}/{3} (Cached: {4})", dimensionStr, size, (_currentPage + 1), _pages.Count, cachedStr);
                SetTitleText("");
            }
        }

        //private BitmapSource ScaleImage(BitmapSource imageToShow, int index, int srcWidth, int scrHeight)
        //{
        //    //System.Diagnostics.Debug.WriteLine("ScaleImage");
        //    _cache.TryRemove(index, out _);
        //    int OWidth = srcWidth;
        //    int OHeight = scrHeight;

        //    if (srcWidth == 0)
        //    {
        //        OWidth = (int)imageToShow.PixelWidth;
        //        OHeight = (int)imageToShow.PixelHeight;
        //    }


        //    int viewWidth = (int)MainScroll.ViewportWidth;
        //    int viewHeight = (int)MainScroll.ViewportHeight;

        //    //WindowFit(_currentComicItem.FitToWindow, false, OWidth, OHeight);
        //    //System.Diagnostics.Debug.WriteLine("Original: " + OWidth + "x" + OHeight);
        //    //System.Diagnostics.Debug.WriteLine("View: " + viewWidth + "x" + viewHeight);
        //    //this.Dispatcher.Invoke(new Action(() =>
        //    //{

        //    //    RenderOptions.SetBitmapScalingMode(ComicDisplay, BitmapScalingMode.NearestNeighbor);
        //    //}));
        //    double ratioView = (double)viewWidth / (double)viewHeight;
        //    double ratioImg = (double)OWidth / (double)OHeight;

        //    double ratioWidth = (double)OWidth / (double)viewWidth;
        //    double ratioHeight = (double)OHeight / (double)viewHeight;

        //    int newWidth = (int)viewWidth;
        //    int newHeight = (int)Math.Round(OHeight / ratioWidth);

        //    if (viewWidth > OWidth && _isFitWidth ||
        //     !_isFitWidth && ((viewWidth > OWidth && ratioImg > ratioView) || (viewHeight > OHeight && ratioView > ratioImg)))
        //    {
        //        //System.Diagnostics.Debug.WriteLine("upscale");
        //        BitmapSource bImg;
        //        if (!_isFitWidth)
        //        {
        //            if (ratioImg > ratioView)
        //            {
        //                newHeight = (int)Math.Round(viewWidth / ratioImg);

        //            }
        //            else
        //            {
        //                newHeight = (int)viewHeight;
        //                newWidth = (int)Math.Round(OWidth / ratioHeight);
        //            }

        //        }
        //        bImg = NetVipsLanczosUpscaler.Upscale(imageToShow, newWidth, newHeight);
        //        //_cache.TryAdd(index, new ImageContainer(bImg, OWidth, OHeight));
        //        return bImg;
        //    }
        //    // Downscale with BitmapImage's built in decoder which is faster than resizing with
        //    // Lanczos for large images that don't need to be resized as much
        //    else
        //    {
        //        System.Diagnostics.Debug.WriteLine("Downscale");
        //        if (!_isFitWidth)
        //        {
        //            if (ratioImg > ratioView)
        //            {
        //                newWidth = viewWidth;

        //            }
        //            else
        //            {
        //                newHeight = viewHeight;
        //                newWidth = (int)Math.Round(OWidth / ratioHeight);
        //            }
        //        }
        //        else
        //        {
        //            newWidth = viewWidth;
        //        }

        //        BitmapSource bImg = null;

        //        bImg = new BitmapImage();

        //        BmpBitmapEncoder encoder = new BmpBitmapEncoder();
        //        //PngBitmapEncoder encoder = new PngBitmapEncoder();
        //        //JpegBitmapEncoder encoder = new JpegBitmapEncoder();
        //        //encoder.QualityLevel = 97; // Set JPEG quality level (0-100), adjust as needed
        //        //encoder.
        //        MemoryStream memoryStream = new MemoryStream();
        //        // 2. Push the BitmapSource into the encoder
        //        encoder.Frames.Add(BitmapFrame.Create(imageToShow));
        //        // 3. Save the encoder's data to the stream
        //        encoder.Save(memoryStream);
        //        // 4. Initialize the BitmapImage from the stream
        //        memoryStream.Position = 0;
        //        ((BitmapImage)bImg).BeginInit();
        //        ((BitmapImage)bImg).StreamSource = memoryStream;
        //        ((BitmapImage)bImg).CacheOption = BitmapCacheOption.OnLoad; // Important for memory management
        //        ((BitmapImage)bImg).DecodePixelWidth = newWidth;
        //        ((BitmapImage)bImg).DecodePixelHeight = newHeight;
        //        ((BitmapImage)bImg).EndInit();


        //        bImg.Freeze();
        //        return bImg;
        //    }

        //}

        private async Task<BitmapSource> LoadAndProcessImage(int index)
        {

            return await Task.Run(() =>
            {

                if (_cache.TryGetValue(index, out ImageContainer c1))
                {
                    return c1.ResizedImage;
                }
                //if (semQ.CurrentCount == 0)
                //{
                //    return null;
                //}
                //_skipInQueue = true;
                semQ.Wait();



                semImg.Wait();


                semQ.Release();

                if (_cache.TryGetValue(index, out ImageContainer c2))
                {
                    semImg.Release();
                    return c2.ResizedImage;
                }
                Stopwatch sw = new Stopwatch();
                sw.Start();
                BitmapSource imageToShow = null;

                var stream = _pages[index].OpenEntryStream();
                var ms = new MemoryStream();
                int OWidth = 0;
                int OHeight = 0;
                Debug.WriteLine(_pages[index].Key);

                try
                {
                    stream.CopyTo(ms);
                    ms.Position = 0;

                    //if (_pages[index].Key.ToLower().Contains(".jxl"))
                    //{
                    //    JxlDecodeOptions options = new JxlDecodeOptions
                    //    {
                    //        Threads = Environment.ProcessorCount,
                    //        SkipOrientation = false,
                    //    };

                    //    var imageJxl = JxlDecoder.DecodeAsBitmapSource(ms.ToArray(), options);

                    //    //image.Freeze();
                    //    //imageJxl.Freeze();
                    //    BitmapSource resized = ScaleImage(imageJxl, index, 0, 0);

                    //    resized.Freeze();

                    //    _cache.TryAdd(index, new ImageContainer(resized, (int)imageJxl.Width, (int)imageJxl.Height));
                    //    imageToShow = resized;
                    //}

                    //else if (_pages[index].Key.ToLower().Contains(".jpg") || _pages[index].Key.ToLower().Contains(".jpeg"))
                    //{
                    //    TJDecompressor td = new TJDecompressor();
                    //    DecompressedImage di = td.Decompress(ms.ToArray(), TJPixelFormats.TJPF_BGRA, TJFlags.ACCURATEDCT);
                    //    int stride = (int)di.Width * 4;
                    //    BitmapSource imageJpg = BitmapSource.Create(di.Width, di.Height, 96, 96, PixelFormats.Bgra32, null, di.Data, stride);

                    //    BitmapSource resized = ScaleImage(imageJpg, index, 0, 0);
                    //    //resized.Freeze();
                    //    imageToShow = resized;
                    //    _cache.TryAdd(index, new ImageContainer(resized, di.Width, di.Height));
                    //    imageJpg = null;
                    //    //Image vipsImage = Image.NewFromBuffer(ms.ToArray());
                    //    //BitmapSource resized = ScaleImage(null, vipsImage, index, vipsImage.Width, vipsImage.Height);
                    //    td.Dispose();
                    //    di = null;
                    //    //resized.Freeze();
                    //    //_cache.TryAdd(index, new ImageContainer(resized, vipsImage.Width, vipsImage.Height));
                    //    imageToShow = resized;

                    //}
                    if (_pages[index].Key.ToLower().Contains(".png") ||
                    _pages[index].Key.ToLower().Contains(".jpg") ||
                    _pages[index].Key.ToLower().Contains(".jpeg") ||
                    _pages[index].Key.ToLower().Contains(".jxr") ||
                    _pages[index].Key.ToLower().Contains(".jxl") ||
                    _pages[index].Key.ToLower().Contains(".webp") ||
                    _pages[index].Key.ToLower().Contains(".gif")
                   )
                    {
                        //BitmapSource image = null;
                        //ms.Position = 0;


                        //image = new BitmapImage();
                        //((BitmapImage)image).BeginInit();
                        //((BitmapImage)image).CacheOption = BitmapCacheOption.OnLoad;

                        //((BitmapImage)image).StreamSource = ms;
                        ////image.Freeze();
                        //((BitmapImage)image).EndInit();
                        //int OWidth, OHeight;
                        BitmapSource bs;

                        //if ((System.Windows.SystemParameters.PrimaryScreenWidth / 2) >= MainScroll.ScrollableWidth && _isFitWidth)
                        //{
                        //    MagicScaler(index, ms, out bs, out OWidth, out OHeight);
                        //}
                        //else
                        //{
                        //    NetVips(index, ms, out OWidth, out OHeight, out bs);
                        //}


                        //if (Enums.Kernel.Mitchell == _scalingAlgo)
                        //{
                        //    MagicScaler(index, ms, out bs, out OWidth, out OHeight);
                        //}
                        //else
                        //{
                        NetVips(index, ms, out OWidth, out OHeight, out bs);
                        //}

                        //bs = Sharpen.UnsharpMask(bs);
                        //bs.Freeze();


                        //bs = ScaleImage(bs, index, 0, 0);
                        //_cache.TryAdd(index, new ImageContainer(bs, OWidth, OHeight));

                        imageToShow = bs;
                    }
                    else
                    {
                        //Debug.WriteLine("Error image read");
                        Log.add("Error image read", true);

                        //OWidth = 0;
                        //OHeight = 0;
                        //int newWidth = 0;
                        //int newHeight = 0;

                        //try
                        //{
                        //    ms.Position = 0;
                        //    var directories = ImageMetadataReader.ReadMetadata(ms);
                        //    foreach (var directory in directories)
                        //        foreach (var tag in directory.Tags)
                        //        {
                        //            if (tag.Name.Contains("Width") && int.TryParse(Regex.Replace(tag.Description, @"[^0-9]", ""), out int width))
                        //            {
                        //                OWidth = width;
                        //            }
                        //            else if (tag.Name.Contains("Height") && int.TryParse(Regex.Replace(tag.Description, @"[^0-9]", ""), out int height))
                        //            {
                        //                OHeight = height;
                        //            }
                        //            //System.Diagnostics.Debug.WriteLine("{0} : {1}", tag.Name, tag.Description);
                        //        }
                        //}
                        //catch (Exception ex)
                        //{
                        //    //System.Diagnostics.Debug.WriteLine(ex.Message);
                        //    //System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                        //}
                        //bool useUpscale = true;

                        //if (_isFitWidth && OWidth > (int)MainScroll.ViewportWidth)
                        ////if (_isFitWidth)
                        //{
                        //    newWidth = (int)MainScroll.ViewportWidth;
                        //    useUpscale = false;
                        //}

                        //else if (!_isFitWidth && (OHeight > (int)MainScroll.ViewportHeight || OWidth > (int)MainScroll.ViewportWidth))
                        ////else if (!_isFitWidth)
                        //{
                        //    double ratioView = (double)MainScroll.ViewportWidth / (double)MainScroll.ViewportHeight;
                        //    double ratioImg = (double)OWidth / (double)OHeight;
                        //    if (ratioImg > ratioView)
                        //    {
                        //        newWidth = (int)MainScroll.ViewportWidth;
                        //    }
                        //    else
                        //    {
                        //        newHeight = (int)MainScroll.ViewportHeight;
                        //    }
                        //    useUpscale = false;
                        //}

                        //BitmapSource image = null;
                        //ms.Position = 0;


                        //image = new BitmapImage();
                        //((BitmapImage)image).BeginInit();
                        //((BitmapImage)image).CacheOption = BitmapCacheOption.OnLoad;
                        //if (newWidth > 0)
                        //{
                        //    ((BitmapImage)image).DecodePixelWidth = newWidth;
                        //}
                        //else if (newHeight > 0)
                        //{
                        //    ((BitmapImage)image).DecodePixelHeight = newHeight;
                        //}
                        //((BitmapImage)image).StreamSource = ms;
                        ////image.Freeze();
                        //((BitmapImage)image).EndInit();
                        //if (useUpscale)
                        //{
                        //    OWidth = (int)image.PixelWidth;
                        //    OHeight = (int)image.PixelHeight;
                        //    image = ScaleImage(image, index, 0, 0);
                        //}

                        //image.Freeze();
                        ////_cache.TryAdd(index, new ImageContainer(image, OWidth, OHeight));

                        //imageToShow = image;
                    }
                }
                catch (Exception ex)
                {
                    //System.Diagnostics.Debug.WriteLine(ex.Message);
                    //System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                    Log.add(ex.Message, true);
                    Log.add(ex.StackTrace, true);

                }
                finally
                {
                    if (!_IsWebtoon)
                    {
                        _cache.TryAdd(index, new ImageContainer(imageToShow, OWidth, OHeight));
                    }
                    sw.Stop();
                    _loadTimeMs = (int)sw.Elapsed.TotalMilliseconds;
                    ms.Close();
                }
                this.Dispatcher.Invoke(new Action(() =>
                {
                    UpdateInfo(null);
                    System.GC.Collect();
                }));
                semImg.Release();

                return imageToShow;
            });
        }

        private void NetVips(int index, MemoryStream ms, out int OWidth, out int OHeight, out BitmapSource bs)
        {
            (OWidth, OHeight) = ImageDimensionReader.Read(ms);

            ms.Position = 0;

            int viewWidth = (int)MainScroll.ViewportWidth;
            int viewHeight = (int)MainScroll.ViewportHeight;
            double ratioView = (double)viewWidth / (double)viewHeight;
            double ratioImg = (double)OWidth / (double)OHeight;

            double ratioWidth = (double)OWidth / (double)viewWidth;
            double ratioHeight = (double)OHeight / (double)viewHeight;

            int newWidth = (int)viewWidth;
            int newHeight = (int)Math.Round(OHeight / ratioWidth);

            if (!_isFitWidth)
            {
                if (ratioImg > ratioView)
                {
                    newWidth = viewWidth;

                }
                else
                {
                    newHeight = viewHeight;
                    newWidth = (int)Math.Round(OWidth / ratioHeight);
                }
            }
            else
            {
                newWidth = viewWidth;
            }
            //Debug.WriteLine("With:{0} Height:{1}", newWidth, newHeight);

            Log.add(String.Format("With:{0} Height:{1}", newWidth, newHeight), false);

            Enums.Kernel scalingAlgo = _scalingAlgo;


            if (_noScale && OWidth < SystemParameters.PrimaryScreenWidth)
            {
                newWidth = OWidth;
                newHeight = OHeight;
            }

            if (scalingAlgo == Enums.Kernel.Nearest)
            {
                if ((SystemParameters.PrimaryScreenWidth * 1.2) <= OWidth)
                {
                    scalingAlgo = Enums.Kernel.Lanczos2;
                    this.Dispatcher.Invoke(() =>
                    {
                        foreach (Control item in ResizeAlgoMenu.Items)
                        {
                            if (item is MenuItem)
                                ((MenuItem)item).IsChecked = false;
                        }
                        ScalingLanczos2.IsChecked = true;
                    });
                }
                else
                {
                    scalingAlgo = Enums.Kernel.Lanczos3;
                    this.Dispatcher.Invoke(() =>
                    {
                        foreach (Control item in ResizeAlgoMenu.Items)
                        {
                            if (item is MenuItem)
                                ((MenuItem)item).IsChecked = false;
                        }
                        ScalingLanczos3.IsChecked = true;
                    });
                }

                if (OWidth * 1.5 < newWidth)
                {
                    ms.Position = 0;

                    //Debug.WriteLine("Magicscaler:" + ((double)newWidth / (double)viewWidth));
                    Log.add(String.Format("Magicscaler:" + ((double)newWidth / (double)viewWidth)), false);

                    MagicScaler(index, ms, out bs, newWidth, newHeight, InterpolationSettings.Lanczos);

                    this.Dispatcher.Invoke(() =>
                    {
                        foreach (Control item in ResizeAlgoMenu.Items)
                        {
                            if (item is MenuItem)
                                ((MenuItem)item).IsChecked = false;
                        }
                        ScalingUltraSharp.IsChecked = true;
                    });
                    //Debug.WriteLine("Magicscaler:" + bs.Height);
                    if (index == _currentPage)
                        _MagicScale = true;
                }
                else
                {
                    bs = VipsImageFactory.Scale(GetVipsImg(index, ms), scalingAlgo, newWidth, newHeight, false);
                    if (index == _currentPage)
                        _MagicScale = false;
                }
            }
            else
            {
                if (scalingAlgo == (Enums.Kernel)11)
                {
                    MagicScaler(index, ms, out bs, newWidth, newHeight, InterpolationSettings.Lanczos);
                    if (index == _currentPage)
                        _MagicScale = true;
                }
                else if (scalingAlgo == (Enums.Kernel)14)
                {
                    MagicScaler(index, ms, out bs, newWidth, newHeight, InterpolationSettings.Mitchell);
                    if (index == _currentPage)
                        _MagicScale = true;
                }
                else if (scalingAlgo == (Enums.Kernel)15)
                {
                    MagicScaler(index, ms, out bs, newWidth, newHeight, InterpolationSettings.Hermite);
                    if (index == _currentPage)
                        _MagicScale = true;
                }
                else if (scalingAlgo == (Enums.Kernel)16)
                {
                    MagicScaler(index, ms, out bs, newWidth, newHeight, InterpolationSettings.CatmullRom);
                    if (index == _currentPage)
                        _MagicScale = true;
                }
                else
                {
                    bool sharpen = false;
                    if (scalingAlgo == (Enums.Kernel)25)
                    {
                        sharpen = true;
                    }

                    bs = VipsImageFactory.Scale(GetVipsImg(index, ms), scalingAlgo, newWidth, newHeight, sharpen);
                    if (index == _currentPage)
                        _MagicScale = false;
                }

            }
            if (OWidth < System.Windows.SystemParameters.PrimaryScreenWidth &&
                       (scalingAlgo == (Enums.Kernel)20 ||
                       scalingAlgo == (Enums.Kernel)21 ||
                       scalingAlgo == (Enums.Kernel)22 ||
                       scalingAlgo == (Enums.Kernel)23))
            {
                double vScale = newWidth / OWidth;
                int AiScale = 2;

                if (vScale > 2.7)
                {
                    AiScale = 4;
                }
                OWidth = OWidth * AiScale;
                OHeight = OHeight * AiScale;
            }


            Debug.WriteLine((System.Windows.SystemParameters.PrimaryScreenWidth * 1.2) + " : " + newWidth + " : " + _scalingAlgo + ":" + ratioWidth);

            ms.Close();
            //bs = VipsImageFactory.Scale(img, FilterType.SincFast, newWidth, newHeight, true);
            //img.Dispose();
        }

        private Image GetVipsImg(int index, MemoryStream ms)
        {
            Image img = null;
            if (_pages[index].Key.ToLower().Contains(".jxl"))
            {
                img = VipsImageFactory.FromJxl(ms.ToArray());
            }
            else if (_pages[index].Key.ToLower().Contains(".jxr"))
            {
                img = VipsImageFactory.FromJxr(ms.ToArray());
            }
            else
            {

                img = VipsImageFactory.FromBuffer(ms.ToArray());
            }

            return img;
        }

        private void MagicScaler(int index, MemoryStream ms, out BitmapSource bs, int width, int height, InterpolationSettings interpolation)
        {
            ////MagicScaler(index, ms, out bs, out width, out height);
            MagicImageFormat fmt = MagicImageFormat.Jpeg;
            if (_pages[index].Key.ToLower().Contains(".jxl"))
            {
                fmt = MagicImageFormat.Jxl;
            }
            else if (_pages[index].Key.ToLower().Contains(".jxr"))
            {
                fmt = MagicImageFormat.Jxr;
            }
            else if (_pages[index].Key.ToLower().Contains(".png"))
            {
                fmt = MagicImageFormat.Png;
            }
            else if (_pages[index].Key.ToLower().Contains(".jpeg"))
            {
                fmt = MagicImageFormat.Jpeg;
            }
            else if (_pages[index].Key.ToLower().Contains(".webp"))
            {
                fmt = MagicImageFormat.WebP;
            }

            //Debug.WriteLine("mmmm: " + _isFitWidth);
            //if (!_isFitWidth)
            //{
            //    (bs, width, height) = MagicScalerImageFactory.Scale(ms.ToArray(), fmt, (int)MainScroll.ViewportWidth);
            //}
            //else
            //{
            //    //(bs, width, height) = MagicScalerImageFactory.Scale(ms.ToArray(), fmt, (int)MainScroll.ViewportWidth);
            //    (bs, width, height) = MagicScalerImageFactory.Scale(ms.ToArray(), fmt,
            //        (int)MainScroll.ViewportWidth, (int)MainScroll.ViewportHeight, true);
            //}
            int OWidth = 0;
            int OHeight = 0;
            (bs, OWidth, OHeight) = MagicScalerImageFactory.Scale(interpolation, ms.ToArray(), fmt,
                 width, height, true);
        }
        private void SetFitWindow(object sender, RoutedEventArgs e)
        {
            _noScale = false;
            _currentComicItem.FitToWindow = true;
            WindowFit(_currentComicItem.FitToWindow, true, 0, 0);
        }

        private void SetFitWidth(object sender, RoutedEventArgs e)
        {
            _noScale = false;
            _currentComicItem.FitToWindow = false;
            WindowFit(_currentComicItem.FitToWindow, true, 0, 0);
        }
        private void SetNoScale(object sender, RoutedEventArgs e)
        {
            _noScale = true;
            WindowFit(_currentComicItem.FitToWindow, true, 0, 0);
        }

        private void WindowFit(bool fitToWindow, bool forceUpdate, double width, double height)
        {
            if (_IsWebtoon)
            {
                return;
            }

            if (_noScale)
            {
                MenuFitWidth.IsChecked = false;
                MenuFitWindow.IsChecked = false;
                MenuNoScale.IsChecked = true;
            }
            else if (fitToWindow)
            {
                MenuNoScale.IsChecked = false;
                MenuFitWidth.IsChecked = false;
                MenuFitWindow.IsChecked = true;
            }
            else
            {
                MenuNoScale.IsChecked = false;
                MenuFitWidth.IsChecked = true;
                MenuFitWindow.IsChecked = false;
            }

            //_currentComicItem.FitToWindow = fitToWindow;
            ComicDisplay.ClearValue(FrameworkElement.MaxHeightProperty);
            double ratioImage = (double)ComicDisplay.ActualWidth / (double)ComicDisplay.ActualHeight;
            if (width > 0 && height > 0)
            {
                ratioImage = width / height;
            }
            double ratioView = (double)MainContainer.ActualWidth / (double)MainContainer.ActualHeight;

            bool checkFit = _isFitWidth;
            //ComicDisplay.ClearValue(FrameworkElement.HeightProperty);
            //ComicStack.ClearValue(FrameworkElement.HeightProperty);
            //ComicStack.ClearValue(FrameworkElement.WidthProperty);
            //ComicDisplay.MaxHeight = ComicDisplay.Source.Height;
            //Log.add(String.Format(ratioImage + ", " + ratioView), false);
            //Log.add(String.Format(ComicDisplay.Width + ", " + MainScroll.ActualWidth + ", " + ComicStack.ActualWidth), false);
            //  Width="{Binding Path=ActualWidth, ElementName=ComicDisplay}"
            Log.add(String.Format($"FitWindow: {fitToWindow}, ratioImage: {Math.Round(ratioImage, 4)}, ratioView: {Math.Round(ratioView, 4)}, page: {_currentPage}"), false);
            if (_noScale)
            {
                ComicDisplay.Stretch = System.Windows.Media.Stretch.None;
                MainScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
                MainScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            else if (fitToWindow || ratioImage >= ratioView && ComicDisplay.Source != null)
            {
                _isFitWidth = false;
                //ScrollThresholdLimit = ScrollFast;
                // 1. Remove the explicit width/height constraints so it can shrink


                ComicDisplay.MaxHeight = MainScroll.ViewportHeight;
                //ComicStack.Height =
                //ComicStack.MaxHeight = MainScroll.ViewportHeight;
                // 2. Set stretch to fit the whole image inside the box
                ComicDisplay.Stretch = System.Windows.Media.Stretch.Uniform;
                //ComicDisplay.Height = MainScroll.ViewportHeight;

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
                //ComicDisplay.Width = MainScroll.Width;


            }

            if (forceUpdate || _isFitWidth != checkFit)
            {
                SaveJson(LoadJson());
                _cache.Clear();
                Log.add(String.Format("WindowFit"), false);
                DisplayPage(0, 55);
                Log.add(String.Format("FitWindow: " + _isFitWidth + ""), false);
            }

        }

        private void SaveJson(JsonComic jsonComic)
        {
            this.Dispatcher.Invoke(() =>
            {
                if (_currentFile == "" || _currentFile == null)
                {
                    return;
                }

                if (_currentComicItem != null)
                {
                    jsonComic.List.RemoveAll((item) =>
                    {
                        return item.Name.Equals(_currentComicItem.Name);
                    });
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
                //_currentComicItem.FitToWindow = !_isFitWidth;
                Log.add("Save Json: " + _currentComicItem.Name + ", " + _currentPage, false);

                jsonComic.List.Add(_currentComicItem);
                jsonComic.List.Sort(compare);

                jsonComic.WindowX = (int)this.Left;
                if ((int)this.Width < System.Windows.SystemParameters.PrimaryScreenWidth)
                {
                    jsonComic.WindowWidth = (int)this.Width;
                    jsonComic.WindowHeight = (int)this.Height;
                }

                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(jsonComic, Formatting.None));
            }, DispatcherPriority.Send);


        }

        // --- NAVIGATION & SCROLLING ---

        private void MainScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            //Debug.WriteLine(MainScroll.VerticalOffset + ", " + MainScroll.ScrollableHeight);


            if (!_IsWebtoon)
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
            else
            {
                MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset - e.Delta * 2);
                double offset = MainScroll.ScrollableHeight - MainScroll.VerticalOffset;
                Debug.WriteLine(offset + " . " + MainScroll.ActualHeight);
                if (offset < (MainScroll.ActualHeight * 1.5) && !_isPageLoading)
                {
                    e.Handled = true;
                    _currentPage++;
                    DisplayPage(0, 577);
                    UpdateInfo(null);
                }
            }




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

        private void ChangeWindowState()
        {

            if (_IsWebtoon)
            {
                return;
            }

            if (Application.Current.MainWindow.WindowState == WindowState.Maximized)
            {
                BorderThickness = new Thickness(2, 0, 2, 0);
                //Application.Current.MainWindow.WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = WindowState.Normal;
                //

            }
            else if (Application.Current.MainWindow.WindowState == WindowState.Normal)
            {
                BorderThickness = new Thickness(7, 7, 7, 7);
                //_inStateChange = true;
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                ResizeMode = ResizeMode.NoResize;
                //_inStateChange = false;
                //Application.Current.MainWindow.ResizeMode = System.Windows.ResizeMode.NoResize;
                //Application.Current.MainWindow.WindowState = WindowState.Maximized;
                //Application.Current.MainWindow.WindowStyle = WindowStyle.None;
            }
            WindowFit(_currentComicItem.FitToWindow, true, 0, 0);
        }
        bool skip = true;
        private void MainScroll_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {


            if (_isDragging && !_isPageLoading && skip)
            {
                Point currentPosition = e.GetPosition(MainScroll);

                // Calculate how far the mouse has moved
                int deltaY = (int)(currentPosition.Y - _lastMousePosition.Y);
                // Update the scroll position. 
                // We subtract the delta because "pulling" the mouse down 
                // should move the scrollbar UP (scrolling towards the top).
                MainScroll.ScrollToVerticalOffset(_startVerticalOffset - deltaY * 2);

                //e.Handled = true;
                // Optional: If you want to use the "Push to turn page" logic while dragging,
                // you would check the boundaries here. However, usually dragging 
                // is best kept strictly for viewing the current page.
            }
            skip = !skip;

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
                MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset - System.Windows.SystemParameters.PrimaryScreenHeight / 3);
            }
            else if (e.Key.Equals(Key.Down))
            {
                MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset + System.Windows.SystemParameters.PrimaryScreenHeight / 3);

            }
            else if (e.Key.Equals(Key.Space))
            {
                ChangeWindowState();
            }
            else if (e.Key.Equals(Key.Enter))
            {
                titleBarLocked = !titleBarLocked;
                if (titleBarLocked)
                {
                    TitleBar.Opacity = 0.7;
                }
                else
                {
                    TitleBar.Opacity = 0;
                }

            }

            if ((e.Key.Equals(Key.Up) || e.Key.Equals(Key.Down)) && _IsWebtoon)
            {
                double offset = MainScroll.ScrollableHeight - MainScroll.VerticalOffset;
                Debug.WriteLine(offset + " . " + MainScroll.ActualHeight);
                if (offset < (MainScroll.ActualHeight * 1.5) && !_isPageLoading)
                {
                    e.Handled = true;
                    _currentPage++;
                    DisplayPage(0, 577);
                    UpdateInfo(null);
                }
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
            if (_IsWebtoon)
            {
                _currentPage = Math.Max(_currentPage - 3, 0);
            }
            SaveJson(LoadJson());
            try
            {
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Dispose();
                    _mediaPlayer = null;
                }

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
                JsonComic jsonComic = LoadJson();
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
            //windowSizeChanged();

        }

        private void mainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _sizeChangeCnt = 5;
            //windowSizeChanged();
        }

        private void windowSizeChanged()
        {
            Thread thSizeChanged = (new Thread(() =>
              {

                  while (true)
                  {
                      try
                      {
                          Thread.Sleep(20);
                          if (_sizeChangeCnt > 0)
                          {
                              while (_sizeChangeCnt > 0)
                              {
                                  Thread.Sleep(20);
                                  _sizeChangeCnt--;
                              }
                              if (_currentComicItem != null)
                              {
                                  this.Dispatcher.Invoke(new Action(() =>
                                  {

                                      if (_sizeChangeCnt == 0 && _currentComicItem != null)
                                      {
                                          _cache.Clear();
                                          DisplayPage(0, 6);
                                      }

                                  }), DispatcherPriority.Send);
                              }


                          }

                      }
                      catch (ThreadInterruptedException ex)
                      {

                      }
                  }

              }));
            thSizeChanged.Start();
        }

        private void SliderChange()
        {
            Thread thSliderChanged = (new Thread(() =>
            {
                while (true)
                    try
                    {
                        Thread.Sleep(20);
                        if (_sliderChangeCnt > 0)
                        {
                            Thread.Sleep(100);
                            while (_sliderChangeCnt > 0)
                            {
                                Thread.Sleep(20);
                                _sliderChangeCnt--;
                            }
                            this.Dispatcher.Invoke(new Action(() =>
                            {
                                if (_sliderChangeCnt == 0 && semImg.CurrentCount > 0 && _currentComicItem != null)
                                {
                                    _currentPage = (int)Slider.Value;
                                    _cache.Clear();
                                    DisplayPage(0, 6);
                                }
                            }), DispatcherPriority.Send);
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
            if (WindowState == WindowState.Maximized)
            {
                ChangeWindowState();
            }

            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ChangeWindowState();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void mainWindow_MouseMove(object sender, MouseEventArgs e)
        {
            //Debug.WriteLine(e.OriginalSource);
            System.Windows.Point p = e.GetPosition(this);

            if (_MouseHidePos.X != p.X || _MouseHidePos.Y != p.Y)
            {
                Cursor = Cursors.Hand;
                //HideMouse();
                _hideMouseCnt = 10;
                _MouseHidePos = p;
            }
            //System.Diagnostics.Debug.WriteLine("Mouse move: " + position.Y);
            if (!titleBarLocked)
            {
                if (p.Y <= 30)
                {
                    TitleBar.Opacity = 0.6;
                }
                else
                {

                    TitleBar.Opacity = 0;
                }
            }

            double xPos = (this.Width - InfoPanel.Width) / 2;
            if (p.Y <= (100 + InfoPanel.Height) && p.Y >= 100 &&
                p.X >= xPos && p.X <= this.Width - xPos)
            {
                InfoPanel.Opacity = 0.6;
                InfoPanel.Margin = new Thickness(0, 100, 0, 0);
            }
            else if (p.Y <= (this.Height - 100) && p.Y >= (this.Height - 100 - InfoPanel.Height) &&
                p.X >= xPos && p.X <= this.Width - xPos)
            {
                InfoPanel.Opacity = 0.6;
                InfoPanel.Margin = new Thickness(0, this.Height - 100 - InfoPanel.Height, 0, 0);
            }
            else
            {
                InfoPanel.Opacity = 0;
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

                if (deltaX != 0)
                {
                    TitleBar.Opacity = 0;
                    titleBarLocked = false; ;
                }
                if (this.Top < 0)
                {
                    this.Top = 0;
                }
            }
            else
            {
                _lastTitlePos = e.GetPosition(this);
            }

        }

        private void Fullscreen_Click(object sender, RoutedEventArgs e)
        {
            ChangeWindowState();
        }

        private void Scaling_Click(object sender, RoutedEventArgs e)
        {
            foreach (Control item in ResizeAlgoMenu.Items)
            {
                if (item is MenuItem)
                    ((MenuItem)item).IsChecked = false;
            }



            if (sender == ScalingLanczos3)
            {
                _scalingAlgo = Enums.Kernel.Lanczos3;
            }
            else if (sender == ScalingLanczos2)
            {
                _scalingAlgo = Enums.Kernel.Lanczos2;
            }
            else if (sender == ScalingLinear)
            {
                _scalingAlgo = Enums.Kernel.Linear;
            }
            else if (sender == ScalingMitchell)
            {
                _scalingAlgo = Enums.Kernel.Mitchell;
            }
            else if (sender == ScalingThumb)
            {
                _scalingAlgo = (Enums.Kernel)12;
            }
            else if (sender == ScalingThumbL)
            {
                _scalingAlgo = (Enums.Kernel)13;
            }
            else if (sender == ScalingCubic)
            {
                _scalingAlgo = Enums.Kernel.Cubic;
            }
            else if (sender == ScalingUltraSharp)
            {
                _scalingAlgo = (Enums.Kernel)11;
            }
            else if (sender == ScalingMitchellMS)
            {
                _scalingAlgo = (Enums.Kernel)14;
            }
            else if (sender == ScalingHermite)
            {
                _scalingAlgo = (Enums.Kernel)15;
            }
            else if (sender == ScalingCatRom)
            {
                _scalingAlgo = (Enums.Kernel)16;
            }
            else if (sender == ScalingAINone)
            {
                _scalingAlgo = (Enums.Kernel)20;
            }
            else if (sender == ScalingAILow)
            {
                _scalingAlgo = (Enums.Kernel)21;
            }
            else if (sender == ScalingAIMedium)
            {
                _scalingAlgo = (Enums.Kernel)22;
            }
            else if (sender == ScalingAIHigh)
            {
                _scalingAlgo = (Enums.Kernel)23;
            }
            else if (sender == ScalingLanczos3Sharp)
            {
                _scalingAlgo = (Enums.Kernel)25;
            }

            Debug.WriteLine(_scalingAlgo);

            ((MenuItem)sender).IsChecked = true;

            _cache.Clear();
            DisplayPage(0, 44);
        }


        private void MenuWebtoon_Click(object sender, RoutedEventArgs e)
        {
            _currentComicItem.FitToWindow = false;
            WindowFit(_currentComicItem.FitToWindow, true, 0, 0);

            _IsWebtoon = true;
            _webtoonMargin = 0;
            ComicDisplay.Width = ComicDisplay.Source.Width;
            mainWindow.ResizeMode = ResizeMode.NoResize;
            _cache.Clear();
            //DisplayPage(0, 66);

        }

        private void MenuWebtoonMargin_Click(object sender, RoutedEventArgs e)
        {
            _currentComicItem.FitToWindow = false;
            WindowFit(_currentComicItem.FitToWindow, true, 0, 0);

            _IsWebtoon = true;
            _webtoonMargin = 10;
            ComicDisplay.Width = ComicDisplay.Source.Width;
            mainWindow.ResizeMode = ResizeMode.NoResize;
            _cache.Clear();
        }

        private void MenuWebtoonRestore_Click(object sender, RoutedEventArgs e)
        {


            _IsWebtoon = false;
            _WebtoonStartPage = -1;
            for (int i = ComicStack.Children.Count - 1; i >= 0; i--)
            {
                if (ComicStack.Children[i] != ComicDisplay)
                {
                    ComicStack.Children.Remove(ComicStack.Children[i]);
                }

            }
            ComicDisplay.Width = double.NaN;
            ComicDisplay.Height = double.NaN;
            ComicDisplay.Margin = new Thickness(0);
            _currentPage = Math.Max(_currentPage - 3, 0);

            DisplayPage(0, 5532);
            mainWindow.ResizeMode = ResizeMode.CanResize;
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isMouseOverSlider)
            {
                _sliderChangeCnt = 5;
            }
        }

        private void Slider_MouseEnter(object sender, MouseEventArgs e)
        {
            _isMouseOverSlider = true;
        }

        private void Slider_MouseLeave(object sender, MouseEventArgs e)
        {
            _isMouseOverSlider = false;
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            using Process fileopener = new Process();

            fileopener.StartInfo.FileName = "explorer";
            fileopener.StartInfo.Arguments = "\"" + Path.Join(AppDomain.CurrentDomain.BaseDirectory, "log", "logMain.log") + "\"";
            fileopener.Start();

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