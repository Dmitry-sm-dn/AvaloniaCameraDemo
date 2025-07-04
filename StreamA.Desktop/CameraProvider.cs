using DirectShowLib;
using LibVLCSharp.Shared;
using SkiaSharp;
using StreamA.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

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
            static (string? Name, int Width, int Height) CameraParam(List<(string Name, List<(string Format, List<(int Width, int Height)> Resolutions)> Modes)> devices)
            {
                var device = devices.FirstOrDefault() != default ? devices.FirstOrDefault()
                    : (Name: null, Modes: [(Format: null, Resolutions: [(Width: 640, Height: 480)])]);
                var resolution = device.Modes.First().Resolutions.OrderByDescending(r => r.Width * r.Height).FirstOrDefault(r => r.Width * r.Height <= 1920 * 1080);
                return (device.Name, resolution.Width, resolution.Height);
            };

            if (OperatingSystem.IsWindows())
            {
                var cameraParam = CameraParam(LibVlcSharpFrameSenderWindows.DShowCameraHelper.GetCameraModes());
                return new LibVlcSharpFrameSenderWindows(SendFrame, cameraParam.Name??"none", (uint)cameraParam.Width, (uint)cameraParam.Height);
            }
            if (OperatingSystem.IsLinux())
            {
                var cameraParam = CameraParam(LibVlcSharpFrameSenderLinux.V4L2CameraHelper.GetCameraModes());
                return new LibVlcSharpFrameSenderLinux(SendFrame, cameraParam.Name ?? "dev/video0"/*to do=>/dev*/, (uint)cameraParam.Width, (uint)cameraParam.Height);
            }
            if (OperatingSystem.IsMacOS())
            {
                var cameraParam = CameraParam(LibVlcSharpFrameSenderMacOS.AVFoundationCameraHelper.GetCameraModes());
                return new LibVlcSharpFrameSenderMacOS(SendFrame, cameraParam.Name ?? "0x810000046d0825", (uint)cameraParam.Width, (uint)cameraParam.Height);
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
            public static class DShowCameraHelper
            {
                public static List<(string Name, List<(string Format, List<(int Width, int Height)> Resolutions)> Modes)> GetCameraModes()
                {
                    var results = new List<(string Name, List<(string Format, List<(int Width, int Height)> Resolutions)> Modes)>();

                    foreach (var device in DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice))
                    {
                        var resolutions = new List<(int W, int H)>();

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
                            results.Add((Name: device.Name, Modes: [(Format: null, Resolutions: resolutions?.Where(r => r.W > 0 && r.H > 0).ToList())!]));
                        }
                        catch
                        {
                            results.Add((device.Name, new()));
                        }
                    }

                    return results;
                }
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
            private readonly string _deviceUID;
            private readonly uint _width, _height;
            private readonly Action<byte[]> _onFrame;
            public LibVlcSharpFrameSenderLinux(Action<byte[]> onFrame, string deviceUID, uint width, uint height)
            {
                _onFrame = onFrame;
                _deviceUID = deviceUID;
                _width = width;
                _height = height;
            }

            public void Start()
            {
                var libVlc = new LibVLC(enableDebugLogs: true);
                using var media = new Media(libVlc, $"v4l2://{_deviceUID}", FromType.FromLocation);

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
                StatusChanged?.Invoke($"Camera started => Vendor uid: {_deviceUID}, backend API: {media.Mrl}, Resolution: {_width}x{_height}");
            }

            public void Stop()
            {
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
            }

            #region -- v4l2 information from camera for linux --
            public static class V4L2CameraHelper
            {
                #region -- public methods --
                public static List<(string Device, List<(string Format, List<(int W, int H)> Sizes)>)> GetCameraModes()
                {
                    var result = new List<(string, List<(string, List<(int, int)>)>)>();

                    foreach (var device in Directory.GetFiles("/dev", "video*").OrderBy(x => x))
                    {
                        int fd = LibC.open(device, LibC.O_RDWR);
                        if (fd < 0) continue;

                        var cap = new LibC.v4l2_capability();
                        if (LibC.ioctl(fd, LibC.VIDIOC_QUERYCAP, ref cap) != 0) continue;//to do=>Marshal.GetLastWin32Error();
                        Console.WriteLine("Driver: " + Encoding.ASCII.GetString(cap.driver));

                        var fourCCodes = EnumeratePixelFormats(fd);
                        var deviceInfo = new List<(string, List<(int, int)>)>();

                        foreach (var fourCCode in fourCCodes)
                        {
                            //FourCC расшифровывается как Four-Character Code — это 4-байтовый (32-битный) идентификатор медиа-формата
                            var fourCC = Encoding.ASCII.GetString(BitConverter.GetBytes(fourCCode));
                            var sizes = GetResolutions(fd, fourCCode);
                            if (sizes.Count > 0)
                                deviceInfo.Add((fourCC, sizes));
                        }

                        LibC.close(fd);
                        if (deviceInfo.Count > 0)
                            result.Add((device, deviceInfo));
                    }

                    return result;
                }
                #endregion

                #region -- private implementation --
                private static List<uint> EnumeratePixelFormats(int fd)
                {
                    var formats = new List<uint>();
                    var fmt = new LibC.v4l2_fmtdesc
                    {
                        type = LibC.V4L2_BUF_TYPE_VIDEO_CAPTURE,
                        description = new byte[32],
                        reserved = new uint[4]
                    };

                    for (uint i = 0; ; i++)
                    {
                        fmt.index = i;
                        if (LibC.ioctl(fd, LibC.VIDIOC_ENUM_FMT, ref fmt) != 0)
                            break;

                        formats.Add(fmt.pixelformat);
                    }

                    return formats;
                }

                private static List<(int Width, int Height)> GetResolutions(int fd, uint format)
                {
                    var resolutions = new List<(int, int)>();
                    for (uint i = 0; i < 20; i++)
                    {
                        var frmsize = new LibC.v4l2_frmsizeenum
                        {
                            index = i,
                            pixel_format = format,
                            reserved0 = 0,
                            reserved1 = 0
                        };

                        int result = LibC.ioctl(fd, LibC.VIDIOC_ENUM_FRAMESIZES, ref frmsize);
                        if (result != 0)
                        {
                            int errNo = Marshal.GetLastWin32Error();
                            string message = new Win32Exception(errNo).Message;
                            Console.WriteLine($"ioctl ErrNo:{errNo}; Message: {message}");
                            break;
                        }

                        if (frmsize.type == LibC.V4L2_FRMSIZE_TYPE_DISCRETE)
                            resolutions.Add(((int)frmsize.discrete.width, (int)frmsize.discrete.height));
                        else if (frmsize.type == LibC.V4L2_FRMSIZE_TYPE_CONTINUOUS || frmsize.type == LibC.V4L2_FRMSIZE_TYPE_STEPWISE)
                        {
                            Console.WriteLine($"→ Stepwise: {frmsize.stepwise.min_width}x{frmsize.stepwise.min_height}..{frmsize.stepwise.max_width}x{frmsize.stepwise.max_height}");
                            break;
                        }
                    }

                    return resolutions;
                }
                #endregion

                #region -- Error constants --
                //https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499-
                public static class Win32Error
                {
                    public const int ERROR_BAD_COMMAND  = 22;//The device does not recognize the command.
                    public const int ERROR_BAD_LENGTH   = 24;//The program issued a command but the command length is incorrect.
                    public const int ERROR_SEEK         = 25;//The drive cannot locate a specific area or track on the disk.
                }
                #endregion

                #region -- Video for Linux API --
                //https://docs.huihoo.com/doxygen/linux/kernel/3.7/uapi_2linux_2videodev2_8h_source.html
                public static class LibC
                {
                    public const int O_RDWR = 2;
                    public const uint VIDIOC_ENUM_FMT = 0xC0405602;
                    public const uint VIDIOC_ENUM_FRAMESIZES = 0xC02C564A;
                    public const uint VIDIOC_QUERYCAP = 0x80685600;
                    public const uint VIDIOC_G_FMT = 0xc0cc5604;

                    public const uint V4L2_BUF_TYPE_VIDEO_CAPTURE   = 1;
                    public const uint V4L2_FRMSIZE_TYPE_DISCRETE    = 1;
                    public const uint V4L2_FRMSIZE_TYPE_CONTINUOUS  = 2;
                    public const uint V4L2_FRMSIZE_TYPE_STEPWISE    = 3;

                    [DllImport("libc", SetLastError = true)]
                    public static extern int open(string pathname, int flags);

                    [DllImport("libc", SetLastError = true)]
                    public static extern int close(int fd);

                    [DllImport("libc", SetLastError = true)]
                    public static extern int ioctl(int fd, uint request, ref v4l2_capability arg);

                    [DllImport("libc", SetLastError = true)]
                    public static extern int ioctl(int fd, uint request, ref v4l2_format arg);

                    [DllImport("libc", SetLastError = true)]
                    public static extern int ioctl(int fd, uint request, ref v4l2_frmsizeenum arg);

                    [DllImport("libc", SetLastError = true)]
                    public static extern int ioctl(int fd, uint request, ref v4l2_fmtdesc data);

                    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
                    public struct v4l2_capability
                    {
                        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
                        public byte[] driver;
                        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
                        public byte[] card;
                        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
                        public byte[] bus_info;
                        public uint version;
                        public uint capabilities;
                        public uint device_caps;
                        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
                        public uint[] reserved;
                    }

                    [StructLayout(LayoutKind.Sequential)]
                    public struct v4l2_fmtdesc
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
                    public struct v4l2_frmsizeenum//44
                    {
                        [FieldOffset(0)] public uint index;
                        [FieldOffset(4)] public uint pixel_format;
                        [FieldOffset(8)] public uint type;
                        //union
                        [FieldOffset(12)] public v4l2_frmsize_discrete discrete;//8
                        [FieldOffset(12)] public v4l2_frmsize_stepwise stepwise;//24
                        //
                        [FieldOffset(36)] public uint reserved0;
                        [FieldOffset(40)] public uint reserved1;
                    }

                    [StructLayout(LayoutKind.Sequential)]
                    public struct v4l2_pix_format
                    {
                        public uint width;
                        public uint height;
                        public uint pixelformat;
                        public uint field;
                        public uint bytesperline;
                        public uint sizeimage;
                        public uint colorspace;
                        public uint priv;
                        //not document extension
                        public uint flags;
                        public uint xfer_func;
                        public uint ycbcr_enc;
                        public uint quantization;
                    }
                    [StructLayout(LayoutKind.Sequential)]
                    public struct v4l2_format
                    {
                        public uint type;
                        public v4l2_pix_format fmt;//12*4=48
                        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 152)]//200-48
                        public byte[] reserved;
                    }
                }
                #endregion
            }
            #endregion
        }
        #endregion

        #region -- incapsulate camera frame macos --
        public class LibVlcSharpFrameSenderMacOS : IFrameSender
        {
            public bool IsOpened => _mediaPlayer?.State == VLCState.Playing;
            public event Action<string>? StatusChanged; // Для UI-индикации

            private MediaPlayer? _mediaPlayer;
            private readonly string _deviceUID;
            private readonly uint _width, _height;
            private readonly Action<byte[]> _onFrame;
            public LibVlcSharpFrameSenderMacOS(Action<byte[]> onFrame, string deviceUID, uint width, uint height)
            {
                _onFrame = onFrame;
                _deviceUID = deviceUID;
                _width = width;
                _height = height;
            }

            public void Start()
            {
                var libVlc = new LibVLC(enableDebugLogs: true);
                using var media = new Media(libVlc, $"avcapture://{_deviceUID}", FromType.FromLocation);

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
                StatusChanged?.Invoke($"Camera started => Vendor uid: {_deviceUID}, backend API: {media.Mrl}, Resolution: {_width}x{_height}");
            }

            public void Stop()
            {
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
            }

            #region -- AVFoundation через P/Invoke в macOS --
            public static class AVFoundationCameraHelper
            {
                #region -- public methods --
                public static List<(string Device, List<(string Format, List<(int W, int H)> Sizes)>)> GetCameraModes()
                {
                    var result = new List<(string, List<(string, List<(int, int)>)>)>();

                    var videoDevices = AVFoundationCameraHelper.GetVideoDevices();
                    foreach (var device in videoDevices)
                    {

                        result.Add((device.UniqueID, default!));
                    }

                    return result;
                }
                #endregion

                private const string OBJC_LIB = "/usr/lib/libobjc.A.dylib";

                [DllImport(OBJC_LIB)]
                private static extern IntPtr objc_getClass(string className);

                [DllImport(OBJC_LIB)]
                private static extern IntPtr sel_registerName(string selectorName);

                [DllImport(OBJC_LIB)]
                private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
                [DllImport(OBJC_LIB)]
                private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr mediaType);

                private static List<(string Name, string UniqueID)> GetVideoDevices()
                {
                    var devices = new List<(string Name, string UniqueID)>();

                    IntPtr avCaptureDeviceClass = objc_getClass("AVCaptureDevice");
                    IntPtr selDevicesWithMediaType = sel_registerName("devicesWithMediaType:");
                    IntPtr selLocalizedName = sel_registerName("localizedName");
                    IntPtr selUniqueID = sel_registerName("uniqueID");

                    IntPtr mediaType = GetAVMediaTypeVideo();//.Create("vide"); // "vide" → AVMediaTypeVideo

                    IntPtr devicesArray = objc_msgSend(avCaptureDeviceClass, selDevicesWithMediaType, mediaType);
                    int count = NSArray.GetCount(devicesArray);

                    for (int i = 0; i < count; i++)
                    {
                        IntPtr device = NSArray.GetObjectAtIndex(devicesArray, i);
                        string name = NSString.FromNSObject(objc_msgSend(device, selLocalizedName));
                        string uid = NSString.FromNSObject(objc_msgSend(device, selUniqueID));

                        devices.Add((name, uid));
                    }

                    return devices;
                }

                public static IntPtr GetAVMediaTypeVideo()
                {
                    // Класс AVMediaTypeVideo хранится в Objective-C как NSString переменная, которая возвращается методом
                    IntPtr mediaTypeClass = objc_getClass("AVMediaTypeVideo");
                    if (mediaTypeClass != IntPtr.Zero)
                        return mediaTypeClass;

                    // Альтернатива через AVFoundation класс:
                    IntPtr avClass = objc_getClass("AVFoundation");
                    IntPtr selAVMediaTypeVideo = sel_registerName("AVMediaTypeVideo");
                    return objc_msgSend(avClass, selAVMediaTypeVideo);
                }

                public static class NSArray
                {
                    [DllImport("/System/Library/Frameworks/Foundation.framework/Foundation")]
                    public static extern ulong objc_msgSend_ulong(IntPtr receiver, IntPtr selector);

                    public static int GetCount(IntPtr nsArray)
                    {
                        IntPtr selCount = sel_registerName("count");
                        return (int)objc_msgSend_ulong(nsArray, selCount);
                    }

                    public static IntPtr GetObjectAtIndex(IntPtr nsArray, int index)
                    {
                        IntPtr selObjectAtIndex = sel_registerName("objectAtIndex:");
                        return objc_msgSend(nsArray, selObjectAtIndex, (IntPtr)index);
                    }
                }

                public static class NSString
                {
                    private const string AVF_LIB = "/System/Library/Frameworks/Foundation.framework/Foundation";

                    [DllImport(AVF_LIB)]
                    public static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
                    //[DllImport(AVF_LIB)]
                    //public static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, string str);
                    //[DllImport(AVF_LIB)]
                    //public static extern IntPtr AVMediaTypeVideo();

                    //public static IntPtr Create(string str)
                    //{
                    //    IntPtr nsStringClass = objc_getClass("NSString");
                    //    IntPtr selStringWithUTF8String = sel_registerName("stringWithUTF8String:");
                    //    return objc_msgSend(nsStringClass, selStringWithUTF8String, str);
                    //}

                    public static string FromNSObject(IntPtr nsString)
                    {
                        IntPtr selUTF8String = sel_registerName("UTF8String");
                        IntPtr cStrPtr = objc_msgSend(nsString, selUTF8String);
                        return Marshal.PtrToStringUTF8(cStrPtr);
                    }
                }
            }
            #endregion
        }
        #endregion
    }
}
