using LibVLCSharp.Shared;
using OpenCvSharp;
using StreamA.Services;
using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

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
                return new LibVlcSharpFrameSender(SendFrame, "/dev/video0", 640, 480);//linux
            if (OperatingSystem.IsWindows())
                return new OpenCVSharp4FrameSender(SendFrame); //windous

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

        #region -- incapsulate camera frame linux --
        public class LibVlcSharpFrameSender : IFrameSender
        {
            public bool IsOpened => false;// _capture?.IsOpened() == true;
            public event Action<string>? StatusChanged; // Для UI-индикации

            private readonly string _devicePath;
            private readonly uint _width, _height;
            private MediaPlayer? _mediaPlayer;
            private IntPtr _buffer = IntPtr.Zero;
            private readonly Action<byte[]> _onFrame;

            public LibVlcSharpFrameSender(Action<byte[]> onFrame, string devicePath, uint width, uint height)
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
                var media = new Media(libVlc, $"v4l2://{_devicePath}", FromType.FromLocation);
                _mediaPlayer = new MediaPlayer(media);

                _mediaPlayer.SetVideoFormat("RV32", _width, _height, _width * 4);
                _mediaPlayer.SetVideoCallbacks(
                    lockCb: (opaque, planes) =>
                    {
                        // Выделяем буфер под кадр
                        if (_buffer == IntPtr.Zero)
                            _buffer = Marshal.AllocHGlobal((int)(_width * _height * 4));
                        //planes = _buffer;
                        unsafe
                        {
                            var planePtr = (IntPtr*)planes;
                            planePtr[0] = _buffer;
                        }
                        return IntPtr.Zero;
                    },
                    unlockCb: (opaque, picture, planes) =>
                    {
                        // Здесь можно обработать кадр (planes)
                        int size = (int)(_width * _height * 4);
                        byte[] buffer = new byte[size];
                        //Marshal.Copy(planes, buffer, 0, size);
                        unsafe
                        {
                            var planePtr = (IntPtr*)planes;
                            planePtr[0] = _buffer;
                            Marshal.Copy(planePtr[0], buffer, 0, size);
                        }
                        _onFrame(buffer); // передаём кадр в обработку
                    },
                    displayCb: (opaque, picture) => { }
                );
                _mediaPlayer.Play();
            }

            public void Stop()
            {
                _mediaPlayer?.Stop();
                if (_buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_buffer);
                    _buffer = IntPtr.Zero;
                }
            }
        }
        #endregion

        #region -- incapsulate camera frame windows --
        public class OpenCVSharp4FrameSender: IFrameSender
        {
            public bool IsOpened => _capture?.IsOpened() == true;
            public event Action<string>? StatusChanged; // Для UI-индикации

            private VideoCapture? _capture;
            private Thread? _captureThread;
            private volatile bool _running;
            private string _fourccStr = null!;
            private readonly Action<byte[]> _onFrame;
            private readonly int _deviceIndex;

            public OpenCVSharp4FrameSender(Action<byte[]> onFrame, int deviceIndex = 0)
            {
                _onFrame = onFrame;
                _deviceIndex = deviceIndex;
            }

            public void Start()
            {
                if (_running || IsOpened)
                {
                    StatusChanged?.Invoke("Уже запущено");
                    return;
                }

                var backendApi = GetPreferredApi();
                _capture = new VideoCapture(_deviceIndex, backendApi);
                if (_capture.CaptureType == CaptureType.Camera)
                {
                    if (_capture.FrameWidth > 1920 || _capture.FrameHeight > 1080)
                    {
                        _capture.Set(VideoCaptureProperties.FrameWidth, 1920);//correct for full hd
                        _capture.Set(VideoCaptureProperties.FrameHeight, 1080);//correct for full hd
                    }
                }

                if (!_capture.IsOpened())
                {
                    _capture?.Dispose();
                    _capture = null!;
                    StatusChanged?.Invoke("Camera not opened.");
                    return;
                }

                //Этот код выводит, например, MJPG, YUYV, NV12, GREY и т.д. — то, как драйвер интерпретирует пиксельный формат
                int fourccInt = (int)_capture!.Get(VideoCaptureProperties.FourCC);
                _fourccStr = $"{(char)(fourccInt & 0xFF)}{(char)((fourccInt >> 8) & 0xFF)}{(char)((fourccInt >> 16) & 0xFF)}{(char)((fourccInt >> 24) & 0xFF)}";

                _running = true;
                _captureThread = new Thread(CaptureLoop) { IsBackground = true };
                _captureThread.Start();

                StatusChanged?.Invoke($"Camera started => Backend API: {_capture?.GetBackendName()}; Opened with FourCC: {_fourccStr}; Resolution: {_capture?.FrameWidth}x{_capture?.FrameHeight}");
            }

            public void Stop()
            {
                if (!_running && !IsOpened)
                    return;

                _running = false;
                _captureThread?.Join();
                _capture?.Release();
                _capture?.Dispose();
                _capture = null;

                StatusChanged?.Invoke("Camera stopped");
            }

            private static VideoCaptureAPIs GetPreferredApi()
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return VideoCaptureAPIs.DSHOW; // или MSMF по ситуации

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))//- Некоторые камеры в Linux не отдают MJPEG, и придётся работать с YUYV → ручной CvtColor
                    return VideoCaptureAPIs.V4L2;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))//- На macOS AVFoundation отдаёт NV12 — там тоже потребуется CvtColor → BGR.
                    return VideoCaptureAPIs.AVFOUNDATION;

                return VideoCaptureAPIs.ANY;
            }

            private void CaptureLoop()
            {
                var converter = FrameConverterFactory.GetConverter(_fourccStr);
                using var mat = new Mat();
                using var converted = new Mat();

                while (_running && _capture?.IsOpened() == true)
                {
                    _capture.Read(mat);
                    if (mat.Empty()) continue;

                    //конвертация по => Таблица FourCC форматов для OpenCV
                    converter.Convert(mat, converted);

                    // Кодируем как JPEG
                    Cv2.ImEncode(".jpg", converted, out var buffer);

                    _onFrame(buffer);
                }
            }

            #region -- fabrica frame converter --
//Таблица FourCC форматов для OpenCV
//| FourCC | Формат пикселей | Описание | CvtColor-код | 
//| MJPG | Motion JPEG | Сжатый JPEG-кадр | Cv2.ImDecode(...) | 
//| YUYV | YUV 4:2:2 (interleaved) | Чередующиеся яркость + хрома | YUV2BGR_YUYV / YUV2BGR_YUY2 | 
//| UYVY | YUV 4:2:2 (альтерн.порядок) | Хрома идёт первой | YUV2BGR_UYVY | 
//| NV12 | YUV 4:2:0 planar(macOS) | Яркость + хрома внизу | YUV2BGR_NV12 | 
//| NV21 | YUV 4:2:0 (Android/Some USB) | Хрома в ином порядке | YUV2BGR_NV21 | 
//| RGB3 | Packed RGB | Прямой RGB | не требует конверсии | 
//| BGR3 | Packed BGR | OpenCV-native формат | не требует конверсии | 
//| GREY | Одноканальный(8 бит) | Чёрно-белый | COLOR_GRAY2BGR(если нужно 3-канальный) | 
//🔎 Для MJPG: сначала Cv2.ImDecode(mat.ToBytes(), ImreadModes.Color)
//⚠️ В Linux иногда драйвер отдаёт YUYV, но FourCC возвращает 0000 — в таком случае можно сделать эвристическую проверку по mat.Type() или mat.Step().

            public interface IFrameConverter
            {
                void Convert(Mat input, Mat output);
            }
            //YUYV → BGR
            public class YUYVConverter : IFrameConverter
            {
                public void Convert(Mat input, Mat output) => Cv2.CvtColor(input, output, ColorConversionCodes.YUV2BGR_YUYV);
            }
            //NV12 → BGR (macOS)
            public class NV12Converter : IFrameConverter
            {
                public void Convert(Mat input, Mat output) => Cv2.CvtColor(input, output, ColorConversionCodes.YUV2BGR_NV12);
            }
            //MJPEG → BGR (если нужно)
            public class MJPEGConverter : IFrameConverter
            {
                public void Convert(Mat input, Mat output) => Cv2.ImDecode(input.ToBytes(), ImreadModes.Color).CopyTo(output);
            }
            public class PassConverter : IFrameConverter
            {
                public void Convert(Mat input, Mat output) => input.CopyTo(output);
            }
            public static class FrameConverterFactory
            {
                public static IFrameConverter GetConverter(string fourcc)
                {
                    return fourcc switch
                    {
                        "YUYV" => new YUYVConverter(),
                        "NV12" => new NV12Converter(),
                        "MJPG" => new MJPEGConverter(),
                        _ => new PassConverter()
                    };
                }
            }
            #endregion
        }
        #endregion
    }
}
