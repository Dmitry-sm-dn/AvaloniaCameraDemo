using DirectShowLib;
using LibVLCSharp.Shared;
using SkiaSharp;
using StreamA.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;

namespace StreamA.Desktop
{
    public interface IFrameSender
    {
        bool IsOpened { get; }
        event Action<string>? StatusChanged; // Для UI-индикации

        void Start();
        void Stop();
    }

    internal class CameraProvider : ICameraProvider, IStatusProvider
    {
        public bool IsWorked => _sender?.IsOpened == true;
        public event Action<string>? StatusChanged;

        private IFrameSender? _sender;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private string _host = "127.0.0.1";
        private int _port = 12345;
        private Action<string>? _statusHandler;// Обработчик статуса для UI-индикации

        private IFrameSender CreateCameraSender()
        {
            if (OperatingSystem.IsLinux())
                return new LibVlcSharpFrameSenderLinux(SendFrame, "/dev/video0", 640, 480);//linux
            if (OperatingSystem.IsWindows())
            {
                var cameraName = LibVlcSharpFrameSenderWindows.GetVideoDeviceNames().FirstOrDefault() ?? "";
                return new LibVlcSharpFrameSenderWindows(SendFrame, cameraName, 640, 480);//windows
            }

            throw new PlatformNotSupportedException();
        }

        public void Start(string host, int port)
        {
            if (_sender == null)
            {
                _host = host;
                _port = port;
                _client = new TcpClient(_host, _port);
                _stream = _client.GetStream();

                _sender = CreateCameraSender();
                _statusHandler = msg => StatusChanged?.Invoke(msg);
                _sender.StatusChanged += _statusHandler;
            }
            _sender.Start();
        }

        public void Stop()
        {
            if (_sender != null)
            {
                _sender.StatusChanged -= _statusHandler;
                _sender.Stop();
                _sender = null;
                _stream?.Dispose();
                _client?.Close();
            }
        }

        public void SwitchCamera() => StatusChanged?.Invoke($"Not implemented!"); // Пока заглушка
        public void ConfigurationChanged() { } // Для десктопа, возможно, неактуально

        private void SendFrame(byte[] data)
        {
            try
            {
                var length = BitConverter.GetBytes(data.Length);
                _stream?.Write(length, 0, length.Length);
                _stream?.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Send error: {ex.Message}");
            }
        }

        #region -- --
        public sealed class LibVlcSharpFrameSenderWindows : IFrameSender
        {
            public bool IsOpened => _mediaPlayer?.State == VLCState.Playing;
            public event Action<string>? StatusChanged; // Для UI-индикации

            private MediaPlayer? _mediaPlayer;
            private readonly string _deviceName;
            private readonly uint _width, _height;
            private readonly Action<byte[]> _onFrame;

            public LibVlcSharpFrameSenderWindows(Action<byte[]> onFrame, string deviceName, uint width, uint height)
            {
                _onFrame = onFrame;
                _deviceName = deviceName;
                _width = width;
                _height = height;
            }

            public void Start()
            {
                var libVlc = new LibVLC();
                //libVlc.Log += (s, e) => File.AppendAllText("vlc_log.txt", $"{e.Level}: {e.Message}\n");

                //using var media = new Media(libVlc, new Uri("http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/ElephantsDream.mp4"));
                //media.AddOption(":no-audio");
                using var media = new Media(libVlc, $"dshow://", FromType.FromLocation);
                media.AddOption($":dshow-vdev={_deviceName}");   // Название, не путь!
                media.AddOption(":dshow-adev=none");

                _mediaPlayer = new MediaPlayer(media) { EnableHardwareDecoding = true };
                _mediaPlayer.SetVideoFormat("RV32", _width, _height, _width * 4);

                SKBitmap? skBitmap = null;//to do => optomization!
                _mediaPlayer.SetVideoCallbacks(
                    lockCb: (opaque, planes) =>
                    {
                        skBitmap = new SKBitmap(new SKImageInfo((int)_width, (int)_height, SKColorType.Bgra8888));
                        Marshal.WriteIntPtr(planes, skBitmap.GetPixels());
                        return IntPtr.Zero;
                    },
                    unlockCb: (opaque, picture, planes) => { },
                    displayCb: (opaque, picture) =>
                    {
                        if (skBitmap?.Encode(SKEncodedImageFormat.Jpeg, 90) is SKData skData)
                        {
                            _onFrame(skData.ToArray()); // передаём кадр в обработку
                            skData.Dispose();
                        }
                        skBitmap?.Dispose();
                    }
                );

                _mediaPlayer.Play();
            }

            public void Stop()
            {
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
            }

            #region -- direct show information from camera for windows --
            public static List<string> GetVideoDeviceNames()
            {
                var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
                return devices.Select(d => d.Name).ToList();
            }
            public static List<(string Name, List<(int Width, int Height)> Resolutions)> GetCameraModes()
            {
                var result = new List<(string, List<(int, int)>)>();
                var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);

                foreach (var device in devices)
                {
                    var capsList = new List<(int, int)>();
                    IAMStreamConfig? config = null;

                    try
                    {
                        var graph = (IGraphBuilder)new FilterGraph();
                        if (typeof(DsDevice).GetField("CLSID_CaptureGraphBuilder2", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is Guid captureGraphBuilder)
                            if (Type.GetTypeFromCLSID(captureGraphBuilder) is Type captureGraphBuilderType)
                            {
                                var capFilter = (IBaseFilter?)Activator.CreateInstance(captureGraphBuilderType);
                                graph.AddFilter(capFilter, "Capture Filter");

                                var moniker = device.Mon;
                                Guid guidBaseFilter = typeof(IBaseFilter).GUID;
                                moniker.BindToObject(null, null, ref guidBaseFilter, out object objFilter);
                                var baseFilter = (IBaseFilter)objFilter;

                                graph.AddFilter(baseFilter, "Video Capture");
                                var captureGraph = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
                                captureGraph.SetFiltergraph(graph);
                                captureGraph.FindInterface(PinCategory.Capture, DirectShowLib.MediaType.Video, baseFilter, typeof(IAMStreamConfig).GUID, out object obj);

                                config = obj as IAMStreamConfig;

                                if (config != null)
                                {
                                    config.GetNumberOfCapabilities(out int count, out int size);
                                    var ptr = Marshal.AllocCoTaskMem(size);
                                    for (int i = 0; i < count; i++)
                                    {
                                        config.GetStreamCaps(i, out AMMediaType media, ptr);
                                        var v = (VideoInfoHeader)Marshal.PtrToStructure(media.formatPtr, typeof(VideoInfoHeader))!;
                                        capsList.Add((v.BmiHeader.Width, v.BmiHeader.Height));
                                        DsUtils.FreeAMMediaType(media);
                                    }
                                    Marshal.FreeCoTaskMem(ptr);
                                }
                            }

                        result.Add((device.Name, capsList));
                    }
                    catch
                    {
                        result.Add((device.Name, new()));
                    }
                    finally
                    {
                        if (config is not null and IDisposable d) d.Dispose();
                    }
                }
                return result;
            }
            #endregion
        }
        #endregion

        #region -- incapsulate camera frame linux --
        public class LibVlcSharpFrameSenderLinux : IFrameSender
        {
            public bool IsOpened => _mediaPlayer?.State == VLCState.Playing;
            public event Action<string>? StatusChanged; // Для UI-индикации

            private MediaPlayer? _mediaPlayer;
            private readonly string _devicePath;
            private readonly uint _width, _height;
            private readonly Action<byte[]> _onFrame;
            public LibVlcSharpFrameSenderLinux(Action<byte[]> onFrame, string devicePath, uint width, uint height)
            {
                Core.Initialize();

                _onFrame = onFrame;
                _devicePath = devicePath;
                _width = width;
                _height = height;
            }

            public void Start()
            {
                var libVlc = new LibVLC();
                using var media = new Media(libVlc, $"v4l2://{_devicePath}", FromType.FromLocation);
                
                _mediaPlayer = new MediaPlayer(media);
                _mediaPlayer.SetVideoFormat("RV32", _width, _height, _width * 4);

                SKBitmap? skBitmap = null;
                _mediaPlayer.SetVideoCallbacks(
                    lockCb: (opaque, planes) =>
                    {
                        skBitmap = new SKBitmap(new SKImageInfo((int)_width, (int)_height, SKColorType.Bgra8888));
                        Marshal.WriteIntPtr(planes, skBitmap.GetPixels());
                        return IntPtr.Zero;
                    },
                    unlockCb: (opaque, picture, planes) => { },
                    displayCb: (opaque, picture) =>
                    {
                        if (skBitmap?.Encode(SKEncodedImageFormat.Jpeg, 90) is SKData skData)
                        {
                            _onFrame(skData.ToArray()); // передаём кадр в обработку
                            skData.Dispose();
                        }
                        skBitmap?.Dispose();
                    }
                );

                _mediaPlayer.Play();
            }

            public void Stop()
            {
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
            }
        }
        #endregion
    }
}
