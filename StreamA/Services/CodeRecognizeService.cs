using Avalonia;
using Avalonia.Media.Imaging;
using Compunet.YoloSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Splat;
using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ZXing;
using ZXing.Common;

namespace StreamA.Services;
public interface ICodeRecognizeService: IDisposable
{
    IObservable<CodeRecognizeService.YoloDetection[]?> Codes { get; }
    Result? CodeRecognize(Bitmap bitmap, PixelRect rect);// Распознавание кода из Bitmap
}
public class CodeRecognizeService:ICodeRecognizeService
{
    //результат распознавания кода(потом куда-то вынести)
    //соответствие результату модели библиотеки, вынесено для возможности использования в модулях проекта Avalonia
    public class YoloDetection
    {
        public string Name { get; set; } = string.Empty;// Имя класса обнаруженного объекта (например, "barcode")
        public PixelRect Bound { get; set; }// Прямоугольник границ обнаруженного объекта
        public float Confidence { get; set; }// Уверенность в распознавании(%)
    }

    public IObservable<YoloDetection[]?> Codes => _codeSubject;
    private readonly Subject<YoloDetection[]?> _codeSubject = new();

    private readonly SemaphoreSlim _predictorLock = new(1, 1);// Семафор для синхронизации доступа к обработке использования YoloPredictor
    private IDisposable? _cameraSubscription;// Подписка на кадры камеры
    private readonly YoloPredictor? _yoloPredictor;// Yolo для обнаружения баркодов
    private BarcodeReaderGeneric? _barcoseReader; //ZXing для распознавания баркодов
    public CodeRecognizeService()
    {
        var cameraService = Locator.Current.GetService<ICameraService>();

        _barcoseReader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                ReturnCodabarStartEnd = true,
                PossibleFormats =
                [
                    BarcodeFormat.CODE_128,
                    BarcodeFormat.QR_CODE,
                    BarcodeFormat.EAN_13
                ]
            }
        };

        var pathOnnx = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolo8v_barcode.onnx");
        var options = new YoloPredictorOptions
        {
            UseCuda = false,
            Configuration = new YoloConfiguration
            {
                Confidence = 0.3f,//Порог уверенности (например, 0.25f)
                IoU = 0.5f,//Порог IoU для подавления перекрытий (например, 0.45f)
            },
        };
        _yoloPredictor = new YoloPredictor(pathOnnx, options);

        _cameraSubscription = cameraService?.Frames.Subscribe(bitmap =>
        {
            if (_predictorLock.Wait(0)) //пропускаем кадры, пока идет обработка(зависит от ЦПУ- гибко!)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var image = ConvertBitmapFast(bitmap);// Преобразуем Bitmap в ImageSharp Image
                        var results = await _yoloPredictor.DetectAsync(image);// Используем Yolo для обнаружения баркодов
                        var pad = 25; // Паддинг для корректировки границ(корректное распознание)
                        var detections = results?
                            .Where(r => r.Bounds.Left - pad >= 0 
                                        && r.Bounds.Top - pad >= 0 
                                        && r.Bounds.Right + pad <= image.Width 
                                        && r.Bounds.Bottom + pad <= image.Height)
                            .Select(r => new YoloDetection
                            {
                                Name = r.Name.Name,
                                Bound = new PixelRect (
                                    r.Bounds.Left - pad,
                                    r.Bounds.Top - pad,
                                    (r.Bounds.Right + pad) - (r.Bounds.Left - pad),
                                    (r.Bounds.Bottom + pad) - (r.Bounds.Top - pad)
                                ),
                                Confidence = r.Confidence
                            }).ToArray();
                        image.Dispose();// Освобождаем ресурсы ImageSharp

                        _codeSubject.OnNext(detections);
                        await Task.Delay(25);//Задержка для предотвращения перегрузки(25 miliseconds)
                    }
                    catch (Exception) { }
                    finally
                    {
                        _predictorLock.Release();
                    }
                });
        });
    }
    public static Image<Rgba32> ConvertBitmapFast(Bitmap bitmap)
    {
        // Создаём WriteableBitmap с теми же параметрами
        var writeable = new WriteableBitmap(
            bitmap.PixelSize,
            bitmap.Dpi,
            bitmap.Format,
            bitmap.AlphaFormat
        );
        // Копируем пиксели из Bitmap в WriteableBitmap
        bitmap.CopyPixels(
            new PixelRect(bitmap.PixelSize),
            writeable.Lock().Address,
            writeable.Lock().RowBytes * writeable.PixelSize.Height,
            writeable.Lock().RowBytes
        );

        // Теперь можно использовать writeable.Lock() и получить доступ к пикселям
        var fb = writeable.Lock();
        int width = fb.Size.Width;
        int height = fb.Size.Height;
        int stride = fb.RowBytes;

        // Копируем байты напрямую
        byte[] pixelData = new byte[stride * height];
        Marshal.Copy(fb.Address, pixelData, 0, pixelData.Length);

        fb.Dispose();
        writeable.Dispose();

        // Создаём ImageSharp Image из байтов
        var image = Image.LoadPixelData<Rgba32>(pixelData, width, height);
        return image;
    }

    public Result? CodeRecognize(Bitmap bitmap, PixelRect rect)
    {
        var source = ConvertToLuminanceSource(bitmap, rect, RGBLuminanceSource.BitmapFormat.Gray8);
        return _barcoseReader?.Decode(source); ;
    }

    public static RGBLuminanceSource ConvertToLuminanceSource(Bitmap bitmap, PixelRect rect, RGBLuminanceSource.BitmapFormat format)
    {
        var stride = (int)rect.Width * 4; // 4 байта на пиксель (RGBA)
        // Буфер для хранения пикселей в формате BGRA(сырые RGBA-данные)
        byte[] rgbaBytes = new byte[(int)rect.Height * stride];
        GCHandle handle = GCHandle.Alloc(rgbaBytes, GCHandleType.Pinned);
        try
        {
            var ptr = handle.AddrOfPinnedObject();
            bitmap.CopyPixels(rect, ptr, rgbaBytes.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        byte[]? bytes = null;
        switch (format)
        {
            case RGBLuminanceSource.BitmapFormat.BGR24:// Преобразуем BGRA → RGB
                {
                    bytes = new byte[(int)rect.Width * (int)rect.Height * 3];
                    for (int i = 0, j = 0; i < rgbaBytes.Length; i += 4, j += 3)
                    {
                        bytes[j] = rgbaBytes[i + 2];     // R
                        bytes[j + 1] = rgbaBytes[i + 1]; // G
                        bytes[j + 2] = rgbaBytes[i];     // B
                    }
                }
                break;
            case RGBLuminanceSource.BitmapFormat.Gray8:
               {
                    bytes = new byte[(int)rect.Width * (int)rect.Height];
                    for (int i = 0, j = 0; i < rgbaBytes.Length; i += 4, j++)
                    {
                        int r = rgbaBytes[i + 2]; // R
                        int g = rgbaBytes[i + 1]; // G
                        int b = rgbaBytes[i];     // B
                        // Взвешенное преобразование в оттенки серого (стандартное)
                        bytes[j] = (byte)((r * 19562 + g * 38550 + b * 7424) >> 16);
                    }
                }
               break;
            default:
                throw new ArgumentException($"Неподдерживаемый формат: {format}");
        }

        return new RGBLuminanceSource(bytes, (int)rect.Width, (int)rect.Height, format);
    }

    public void Dispose()
    {
        _cameraSubscription?.Dispose();
        _codeSubject.OnCompleted(); // <-- завершение публикации

        _predictorLock.Wait(); // дождаться, пока поток завершится
        _predictorLock.Dispose();

        try { _yoloPredictor?.Dispose(); } // Освобождаем ресурсы подписки YoloPredictor
        catch (Exception) { }// Игнорируем ошибки при освобождении ресурсов */
    }

    #region -- архивный код, хороший, быстрый, но не используется --
    //using var skBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888));
    //bitmap.CopyPixels(new PixelRect(0, 0, width, height), skBitmap.GetPixels(), width* height * 4, width* 4);

    /*public static byte[] GetRawBytesFromRegion(Image<Rgba32> image, Rectangle region)
    {
        var cropped = image.Clone(ctx => ctx.Crop(region));
        var pixelData = new Rgba32[cropped.Width * cropped.Height];// Создаём массив под пиксели
        cropped.CopyPixelDataTo(pixelData);// Копируем пиксели в массив
        cropped.Dispose();// Освобождаем ресурсы
        // Преобразуем в byte[]
        return MemoryMarshal.AsBytes(pixelData.AsSpan()).ToArray();
    }*/
    #endregion
}
