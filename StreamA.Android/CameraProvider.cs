using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using StreamA.Services;

namespace StreamA.Android;
public class CameraProvider : ICameraProvider, IStatusProvider
{
    public bool IsWorked => _frameSender?.IsWorked ?? false;
    public event Action<string>? StatusChanged;

    private MainActivity? _mainActivity;
    private Camera2FrameSender? _frameSender;
    private string _host = "127.0.0.1";
    private int _port = 12345;
    private Action<string>? _statusHandler;// Обработчик статуса для UI-индикации

    public CameraProvider(MainActivity mainActivity) =>
        _mainActivity = mainActivity ?? throw new ArgumentNullException(nameof(mainActivity));

    public void Start(string host, int port) =>
        _mainActivity?.RunOnUiThread(() => 
        {
            if (_frameSender == null)
            {
                _frameSender = new Camera2FrameSender(_mainActivity);
                _statusHandler = msg => StatusChanged?.Invoke(msg);
                _frameSender.StatusChanged += _statusHandler;
            }

            // Start the camera with the specified host and port
            _host = host;
            _port = port;
            _ = _frameSender.StartAsync(_host, _port);
        });

    public void Stop()
    {
        if (_frameSender != null)
        {
            _frameSender.StatusChanged -= _statusHandler;
            _frameSender.Stop();
            _frameSender = null;
        }
    }

    public void SwitchCamera() => 
        _mainActivity?.RunOnUiThread(() =>
        {
            if (_frameSender != null)
            {
                // Переключаем facing
                var newFacing = _frameSender.CurrentFacing == CameraFacing.Back ? CameraFacing.Front : CameraFacing.Back;
                _frameSender.Stop();
                _frameSender.SetCameraFacing(newFacing);
                _ = _frameSender.StartAsync(_host, _port);
            }
        });

    public void ConfigurationChanged() => _frameSender?.UpdateJpegOrientation();

    #region -- incapsulate camera frame --
    public enum CameraFacing
    {
        Back,
        Front
    }
    public class Camera2FrameSender : CameraCaptureSession.StateCallback, ImageReader.IOnImageAvailableListener
    {
        public event Action<string>? StatusChanged; // Для UI-индикации
        public bool IsWorked => _isRunning && _isConnecting;
        public CameraFacing CurrentFacing => _facing;

        private readonly Context _context;
        private CameraDevice? _cameraDevice;
        private CameraCaptureSession? _captureSession;
        private ImageReader? _imageReader;
        private Handler? _backgroundHandler;
        private string _cameraId = "";
        private TcpClient? _tcpClient;
        private bool _isRunning;
        private bool _isConnecting;
        private CameraFacing _facing = CameraFacing.Back;
        private int _jpegOrientation = 0;// Угол поворота JPEG (0, 90, 180, 270)

        public Camera2FrameSender(Context context)
        {
            _context = context;
        }

        public async Task<bool> StartAsync(string host, int port, int reconnectAttempts = 3)
        {
            if (_isRunning || _isConnecting)
            {
                StatusChanged?.Invoke("Уже запущено");
                return false;
            }

            _isConnecting = true;
            StatusChanged?.Invoke("Подключение...");

            for (int attempt = 1; attempt <= reconnectAttempts; attempt++)
            {
                try
                {
                    _tcpClient = new TcpClient();
                    await _tcpClient.ConnectAsync(host, port);
                    _isRunning = true;
                    _isConnecting = false;
                    StatusChanged?.Invoke("Подключено");
                    StartBackgroundThread();
                    OpenCamera();
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Ошибка подключения (попытка {attempt}): {ex}");
                    StatusChanged?.Invoke($"Ошибка подключения (попытка {attempt})");
                    await Task.Delay(1000);
                }
            }

            _isConnecting = false;
            StatusChanged?.Invoke("Ошибка подключения");
            return false;
        }

        public void Stop()
        {
            if (!_isRunning && !_isConnecting)
                return;

            _isRunning = false;
            _isConnecting = false;
            _captureSession?.Close();
            _cameraDevice?.Close();
            _imageReader?.Close();
            _tcpClient?.Close();
            StopBackgroundThread();

            StatusChanged?.Invoke("Остановлено");
        }

        private void OpenCamera()
        {
            try
            {
                var manager = (CameraManager)_context.GetSystemService(Context.CameraService)!;
                var cameraIdList = manager.GetCameraIdList();
                _cameraId = cameraIdList[0];

                CameraCharacteristics? characteristics = null;
                // Выбор камеры по facing
                foreach (var id in cameraIdList)
                {
                    characteristics = manager.GetCameraCharacteristics(id);
                    var lensFacing = (LensFacing)(int)characteristics.Get(CameraCharacteristics.LensFacing)!;
                    if ((_facing == CameraFacing.Back && lensFacing == LensFacing.Back) ||
                        (_facing == CameraFacing.Front && lensFacing == LensFacing.Front))
                    {
                        _cameraId = id;
                        break;
                    }
                }

                characteristics = manager.GetCameraCharacteristics(_cameraId);
                var streamConfigMap = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap)!;
                var size = streamConfigMap.GetOutputSizes((int)ImageFormatType.Jpeg)
                    .OrderByDescending(s => s.Width * s.Height)
                    .FirstOrDefault(os => os.Width * os.Height <= 1920 * 1080) ?? new Size(800, 600);
                UpdateJpegOrientation();

                _imageReader = ImageReader.NewInstance(size.Width, size.Height, ImageFormatType.Jpeg, 4);
                _imageReader.SetOnImageAvailableListener(this, _backgroundHandler);

                manager.OpenCamera(_cameraId, new CameraStateCallback(this), _backgroundHandler);
                StatusChanged?.Invoke($"Open resolution: {size.Width}x{size.Height}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка открытия камеры: {ex}");
                StatusChanged?.Invoke("Ошибка камеры");
                Stop();
            }
        }

        private void StartBackgroundThread()
        {
            var thread = new HandlerThread("CameraBackground");
            thread.Start();
            _backgroundHandler = new Handler(thread.Looper!);
        }

        private void StopBackgroundThread()
        {
            _backgroundHandler?.Looper.QuitSafely();
            _backgroundHandler = null;
        }

        private class CameraStateCallback : CameraDevice.StateCallback
        {
            private readonly Camera2FrameSender _sender;
            public CameraStateCallback(Camera2FrameSender sender) => _sender = sender;

            public override void OnOpened(CameraDevice camera)
            {
                _sender._cameraDevice = camera;
                _sender.CreateCameraCaptureSession();
            }

            public override void OnDisconnected(CameraDevice camera)
            {
                camera.Close();
                _sender.StatusChanged?.Invoke("Камера отключена");
            }
            public override void OnError(CameraDevice camera, [GeneratedEnum] CameraError error)
            {
                camera.Close();
                _sender.StatusChanged?.Invoke($"Ошибка камеры: {error}");
            }
        }

        private void CreateCameraCaptureSession()
        {
            if (_cameraDevice == null || _imageReader == null || _imageReader.Surface == null)
            {
                StatusChanged?.Invoke("Ошибка: Surface или другие компоненты не инициализированы");
                return;
            }

            _cameraDevice.CreateCaptureSession(
                new[] { _imageReader.Surface },
                this,
                _backgroundHandler
            );
        }

        public override void OnConfigured(CameraCaptureSession session)
        {
            if (_cameraDevice == null || _imageReader == null || _imageReader.Surface == null)
            {
                StatusChanged?.Invoke("Ошибка: Surface или другие компоненты не инициализированы");
                return;
            }

            _captureSession = session;
            var captureRequestBuilder = _cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);// Создаем запрос на захват
            captureRequestBuilder.AddTarget(_imageReader.Surface); // Убедимся, что Surface не равен null
            session.SetRepeatingRequest(captureRequestBuilder.Build(), null, _backgroundHandler);
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            StatusChanged?.Invoke("Ошибка конфигурации камеры");
        }

        public void OnImageAvailable(ImageReader? reader)
        {
            if (!_isRunning || reader == null) return; // Ensure reader is not null

            var image = reader.AcquireLatestImage();
            if (image == null)
                return;

            var planes = image.GetPlanes();
            if (planes == null || planes.Length == 0 || planes[0].Buffer == null) // Ensure planes and buffer are not null
            {
                image.Close();
                return;
            }

            var buffer = planes[0].Buffer!;
            byte[] bytes = new byte[buffer.Remaining()];
            buffer.Get(bytes);
            image.Close();

            try
            {
                // --- Программный поворот JPEG ---
                // Декодируем JPEG в Bitmap
                var originalBitmap = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                if (originalBitmap == null)
                    return;

                // Поворачиваем Bitmap
                var matrix = new Matrix();
                if (_jpegOrientation != 0)
                    matrix.PostRotate(_jpegOrientation);

                var rotatedBitmap = Bitmap.CreateBitmap(
                    originalBitmap, 0, 0,
                    originalBitmap.Width, originalBitmap.Height,
                    matrix, true);

                // Перекодируем обратно в JPEG
                using var ms = new MemoryStream();
                rotatedBitmap.Compress(Bitmap.CompressFormat.Jpeg!, 90, ms);
                var rotatedBytes = ms.ToArray();

                // Освобождаем ресурсы
                originalBitmap.Recycle();
                rotatedBitmap.Recycle();

                // --- Отправка JPEG-кадра по TCP ---
                if (_tcpClient?.Connected == true)
                {
                    var stream = _tcpClient.GetStream();
                    var lengthBytes = BitConverter.GetBytes(rotatedBytes.Length);
                    stream.Write(lengthBytes, 0, lengthBytes.Length);
                    stream.Write(rotatedBytes, 0, rotatedBytes.Length);
                    stream.Flush();
                }
                else
                {
                    StatusChanged?.Invoke("Потеряно соединение");
                    Stop();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка отправки кадра: {ex}");
                StatusChanged?.Invoke("Ошибка передачи");
                Stop();
            }
        }

        public void SetCameraFacing(CameraFacing facing)
        {
            _facing = facing;
        }

        public void UpdateJpegOrientation()
        {
            var windowManager = _context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
            var rotation = windowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;

            int deviceRotation = rotation switch
            {
                SurfaceOrientation.Rotation0 => 0,
                SurfaceOrientation.Rotation90 => 90,
                SurfaceOrientation.Rotation180 => 180,
                SurfaceOrientation.Rotation270 => 270,
                _ => 0
            };

            var manager = (CameraManager)_context.GetSystemService(Context.CameraService)!;
            var characteristics = manager.GetCameraCharacteristics(_cameraId);
            var sensorOrientation = (int)characteristics.Get(CameraCharacteristics.SensorOrientation)!;

            int jpegOrientation;
            if (_facing == CameraFacing.Front)
                jpegOrientation = (sensorOrientation + deviceRotation - 360) % 360;// зеркалим для фронтальной
            else
                jpegOrientation = (sensorOrientation - deviceRotation + 360) % 360;

            _jpegOrientation = jpegOrientation;
        }
    }
    #endregion
}