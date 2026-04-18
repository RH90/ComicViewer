using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Bson;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Shapes;
using System.Windows.Threading;
using static ComicViewer.Logging;
using Path = System.IO.Path;


namespace ComicViewer
{
    internal class Logging
    {
        public class CustomFileLoggerProvider : ILoggerProvider
        {
            private readonly StreamWriter _logFileWriter;

            public CustomFileLoggerProvider(StreamWriter logFileWriter)
            {
                _logFileWriter = logFileWriter ?? throw new ArgumentNullException(nameof(logFileWriter));
            }

            public ILogger CreateLogger(string categoryName)
            {
                return new CustomFileLogger(categoryName, _logFileWriter);
            }

            public void Dispose()
            {
                _logFileWriter.Dispose();
            }
        }

        // Customized ILogger, writes logs to text files
        public class CustomFileLogger : ILogger
        {
            private readonly string _categoryName;
            private readonly StreamWriter _logFileWriter;

            public CustomFileLogger(string categoryName, StreamWriter logFileWriter)
            {
                _categoryName = categoryName;
                _logFileWriter = logFileWriter;
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                // Ensure that only information level and higher logs are recorded
                return logLevel >= LogLevel.Information;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter)
            {
                // Ensure that only information level and higher logs are recorded
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                // Get the formatted log message
                var message = formatter(state, exception);

                //Write log messages to text file
                _logFileWriter.WriteLine($"[{logLevel}] [{DateTime.Now}] {message}");
                _logFileWriter.Flush();
            }
        }
    }
    public class Log
    {
        public Log()
        {

        }
        public void add(string text, bool error)
        {
            //Create a StreamWriter to write logs to a text file
            Thread th = new Thread(() =>
            {
                try
                {

                    semFile.Wait();
                    FileStream fs = GetStream();
                    using (StreamWriter logFileWriter = new StreamWriter(fs, System.Text.Encoding.UTF8))
                    {
                        //Create an ILoggerFactory
                        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
                        {
                            ////Add console output
                            //builder.AddSimpleConsole(options =>
                            //{
                            //    options.IncludeScopes = true;
                            //    options.SingleLine = true;
                            //    options.TimestampFormat = "HH:mm:ss ";
                            //});

                            //Add a custom log provider to write logs to text files
                            builder.AddProvider(new CustomFileLoggerProvider(logFileWriter));
                        });

                        //Create an ILogger
                        ILogger<MainWindow> logger = loggerFactory.CreateLogger<MainWindow>();

                        // Output some text on the console
                        //using (logger.BeginScope("[scope is enabled]"))
                        //{
                        //    logger.LogInformation("Hello World!");
                        //    logger.LogInformation("Logs contain timestamp and log level.");
                        //    logger.LogInformation("Each log message is fit in a single line.");
                        //}
                        if (error)
                        {
                            logger.LogError(text);
                        }
                        else
                        {
                            logger.LogInformation(text);
                        }
                        Debug.WriteLine("C: " + text);

                    }
                    //fs.Close();
                }
                catch (Exception e)
                {

                    Debug.WriteLine(e.Message);
                    Debug.WriteLine(e.StackTrace);

                }
                finally
                {
                    semFile.Release();
                }

            });
            th.Start();


        }
        private string _folderName = Path.Join(AppDomain.CurrentDomain.BaseDirectory, "log");
        private string _logMain = Path.Join(AppDomain.CurrentDomain.BaseDirectory, "log", "logMain.log");
        private string _logTmp = Path.Join(AppDomain.CurrentDomain.BaseDirectory, "log", "logTmp.log");
        private int maxFileSize = 1000000;
        private SemaphoreSlim semFile = new SemaphoreSlim(1, 1);
        private FileStream GetStream()
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Directory.CreateDirectory(_folderName);
                    //if(File())
                    //File.Create(_log1);
                    //File.Create(_log2);
                    var logMain = File.Open(_logMain, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                    //long fileSize = new FileInfo(_logMain).Length;

                    //var logTmp = File.Open(_logTmp, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

                    //FileStream fileStream = log1;

                    if (logMain.Length > maxFileSize)
                    {
                        logMain.Close();
                        File.Delete(_logTmp);
                        File.Move(_logMain, _logTmp);
                    }
                    else
                    {
                        logMain.Close();
                    }


                    return File.Open(_logMain, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                }
                catch (IOException e)
                {
                    MainWindow.Log.add(e.Message, true);
                    MainWindow.Log.add(e.StackTrace, true);
                    //Debug.WriteLine(e.StackTrace);
                }
                Thread.Sleep(10);
            }

            throw new Exception();
        }
        private static bool IsFileLocked(IOException exception)
        {
            int errorCode = Marshal.GetHRForException(exception) & ((1 << 16) - 1);
            return errorCode == 32 || errorCode == 33;
        }
    }
}
