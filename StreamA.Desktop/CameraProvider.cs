using DirectShowLib;
using LibVLCSharp.Shared;
using SkiaSharp;
using StreamA.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
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

        private IFrameSender FabricaCameraSender()
        {
            (string? Name, int Width, int Height) CameraParam(List<(string Name, List<(int Width, int Height)> Resolutions)> modes)
            {
                var mode = modes.FirstOrDefault() != default ? modes.FirstOrDefault()
                    : (Name: null, Resolutions: [(Width: 640, Height: 480)]);
                var resolution = mode.Resolutions.OrderByDescending(r => r.Width * r.Height).FirstOrDefault(r => r.Width * r.Height <= 1920 * 1080);
                return (mode.Name, resolution.Width, resolution.Height);
            };
            
            if (OperatingSystem.IsLinux())
            {
                var cameraParam = CameraParam(LibVlcSharpFrameSenderLinux.V4L2CameraHelper.GetCameraModes());
                return new LibVlcSharpFrameSenderLinux(SendFrame, cameraParam.Name?? "/dev/video0"/*to do=>/dev*/, (uint)cameraParam.Width, (uint)cameraParam.Height);//linux
            }
            if (OperatingSystem.IsWindows())
            {
                var cameraParam = CameraParam(LibVlcSharpFrameSenderWindows.GetCameraModes());
                return new LibVlcSharpFrameSenderWindows(SendFrame, cameraParam.Name??"none", (uint)cameraParam.Width, (uint)cameraParam.Height);//windows
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

                _sender = FabricaCameraSender();
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

        #region -- incapsulate camera frame windows  --
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
                StatusChanged?.Invoke($"Camera started => Name: {_deviceName}, backend API: {media.Mrl}, Resolution: {_width}x{_height}");
            }

            public void Stop()
            {
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
                StatusChanged?.Invoke($"Camera stoped");
            }

            #region -- direct show information from camera for windows --
            public static List<(string Name, List<(int Width, int Height)> Resolutions)> GetCameraModes()
            {
                var results = new List<(string Name, List<(int Width, int Height)> Resolutions)>();

                foreach (var device in DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice))
                {
                    var resolutions = new List<(int, int)>();

                    try
                    {
                        // Создаем Filter Graph
                        var graph = (IFilterGraph2)new FilterGraph();

                        // Создаем Capture Graph Builder
                        var captureGraph = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
                        captureGraph.SetFiltergraph(graph);

                        // Получаем IBaseFilter устройства
                        Guid guidBaseFilter = typeof(IBaseFilter).GUID;
                        device.Mon.BindToObject(null!, null, ref guidBaseFilter, out object sourceObj);
                        var sourceFilter = (IBaseFilter)sourceObj;
                        graph.AddFilter(sourceFilter, "Video Capture");

                        // Получаем интерфейс настройки форматов
                        captureGraph.FindInterface(PinCategory.Capture, DirectShowLib.MediaType.Video, sourceFilter, typeof(IAMStreamConfig).GUID, out object configObj);
                        var config = (IAMStreamConfig)configObj;

                        config.GetNumberOfCapabilities(out int count, out int size);
                        var taskMemPointer = Marshal.AllocCoTaskMem(size);

                        for (int i = 0; i < count; i++)
                        {
                            config.GetStreamCaps(i, out AMMediaType mediaType, taskMemPointer);
                            var header = Marshal.PtrToStructure<VideoInfoHeader>(mediaType.formatPtr);
                            resolutions.Add((header?.BmiHeader.Width ?? 0, header?.BmiHeader.Height ?? 0));
                            DsUtils.FreeAMMediaType(mediaType);
                        }

                        Marshal.FreeCoTaskMem(taskMemPointer);
                        results.Add((device.Name, resolutions.Where(r => r.Item1 > 0 && r.Item2 > 0).ToList()));
                    }
                    catch
                    {
                        results.Add((device.Name, new()));
                    }
                }

                return results;
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
                _onFrame = onFrame;
                _devicePath = devicePath;
                _width = width;
                _height = height;
            }

            public void Start()
            {
                var libVlc = new LibVLC(enableDebugLogs: true);
                using var media = new Media(libVlc, $"v4l2://{_devicePath}", FromType.FromLocation);

                _mediaPlayer = new MediaPlayer(media) { EnableHardwareDecoding = true };
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
                StatusChanged?.Invoke($"Camera started => Vendor name: {_devicePath}, backend API: {media.Mrl}, Resolution: {_width}x{_height}");
            }

            public void Stop()
            {
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
            }

            #region -- v4l2 information from camera for linux --
            public static class V4L2CameraHelper
            {
                public static List<(string Device, List<(string Format, List<(int W, int H)> Sizes)>)> GetCameraModes()
                {
                    var result = new List<(string, List<(string, List<(int, int)>)>)>();

                    foreach (var device in Directory.GetFiles("/dev", "video*").OrderBy(x => x))
                    {
                        int fd = open(device, O_RDWR);
                        if (fd < 0) continue;

                        var formats = EnumeratePixelFormats(fd);
                        var deviceInfo = new List<(string, List<(int, int)>)>();

                        foreach (var format in formats)
                        {
                            var fourcc = FourCC(format);
                            var sizes = GetResolutions(fd, format);
                            if (sizes.Count > 0)
                                deviceInfo.Add((fourcc, sizes));
                        }

                        close(fd);
                        if (deviceInfo.Count > 0)
                            result.Add((device, deviceInfo));
                    }

                    return result;
                }

                //=================================================

                private const int O_RDWR = 2;
                private const uint VIDIOC_ENUM_FMT = 0xC0405602;
                private const uint VIDIOC_ENUM_FRAMESIZES = 0xC02C560A;

                [DllImport("libc", SetLastError = true)]
                private static extern int open(string pathname, int flags);

                [DllImport("libc", SetLastError = true)]
                private static extern int close(int fd);

                [DllImport("libc", SetLastError = true)]
                private static extern int ioctl(int fd, uint request, ref v4l2_frmsizeenum data);
                [DllImport("libc", SetLastError = true)]
                private static extern int ioctl(int fd, uint request, ref v4l2_fmtdesc data);

                [StructLayout(LayoutKind.Sequential)]
                public struct v4l2_frmsize_discrete
                {
                    public uint width;
                    public uint height;
                }
                [StructLayout(LayoutKind.Sequential)]
                public struct v4l2_frmsize_stepwise
                {
                    public uint min_width;
                    public uint max_width;
                    public uint step_width;
                    public uint min_height;
                    public uint max_height;
                    public uint step_height;
                }
                [StructLayout(LayoutKind.Explicit)]
                public struct v4l2_frmsizeenum
                {
                    [FieldOffset(0)] public uint index;
                    [FieldOffset(4)] public uint pixel_format;
                    [FieldOffset(8)] public uint type;
                    [FieldOffset(12)] public v4l2_frmsize_discrete discrete;
                    [FieldOffset(12)] public v4l2_frmsize_stepwise stepwise;
                    [FieldOffset(36)] public uint reserved0;
                    [FieldOffset(40)] public uint reserved1;
                }

                private static List<(int Width, int Height)> GetResolutions(int fd, uint format)
                {
                    var resolutions = new List<(int, int)>();
                    for (uint i = 0; i < 20; i++)
                    {
                        var frmsize = new v4l2_frmsizeenum
                        {
                            index = i,
                            pixel_format = format,
                            reserved0 = 0,
                            reserved1 = 0
                        };

                        int result = ioctl(fd, VIDIOC_ENUM_FRAMESIZES, ref frmsize);
                        if (result != 0)
                        {
                            int errno = Marshal.GetLastWin32Error();
                            if (errno == 22) break; // EINVAL — конец перебора
                            Console.WriteLine($"ioctl error (FRAMESIZES): {errno}");
                            break;
                        }

                        if (frmsize.type == 1)
                        {
                            resolutions.Add(((int)frmsize.discrete.width, (int)frmsize.discrete.height));
                        }
                        else if (frmsize.type == 2 || frmsize.type == 3)
                        {
                            // stepwise/continuous: ты можешь добавить расширенную логику, но пока просто выведем диапазон
                            Console.WriteLine($"→ Stepwise: {frmsize.stepwise.min_width}x{frmsize.stepwise.min_height}..{frmsize.stepwise.max_width}x{frmsize.stepwise.max_height}");
                            break;
                        }
                    }

                    return resolutions;
                }

                //======================================================
                private const uint V4L2_BUF_TYPE_VIDEO_CAPTURE = 1;

                [StructLayout(LayoutKind.Sequential)]
                private struct v4l2_fmtdesc
                {
                    public uint index;
                    public uint type; // V4L2_BUF_TYPE_VIDEO_CAPTURE = 1
                    public uint flags;
                    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
                    public byte[] description;
                    public uint pixelformat;
                    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
                    public uint[] reserved;
                }

                private static List<uint> EnumeratePixelFormats(int fd)
                {
                    var formats = new List<uint>();
                    var fmt = new v4l2_fmtdesc
                    {
                        type = V4L2_BUF_TYPE_VIDEO_CAPTURE,
                        description = new byte[32],
                        reserved = new uint[4]
                    };

                    for (uint i = 0; i < 20; i++)
                    {
                        fmt.index = i;
                        if (ioctl(fd, VIDIOC_ENUM_FMT, ref fmt) != 0)
                            break;

                        formats.Add(fmt.pixelformat);
                    }

                    return formats;
                }
                private static string FourCC(uint format)
                {
                    var bytes = BitConverter.GetBytes(format);
                    return System.Text.Encoding.ASCII.GetString(bytes);
                }
            }
            #endregion
        }
        #endregion
    }
}
