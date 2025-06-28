using Avalonia.Media.Imaging;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace StreamA.Services;
public interface ICameraProvider
{
    bool IsWorked { get; }//признак камера работает/не работает
    void Start(string host, int port);
    void Stop();
    void SwitchCamera(); // Для переключения между фронтальной и основной камерой
    void ConfigurationChanged();// Для обработки изменения ориентации экрана
}
public interface IStatusProvider
{
    event Action<string> StatusChanged;
}

public interface ICameraService : IDisposable
{
    bool IsWorked { get; }//признак сервис запущен/остановлен
    IObservable<Bitmap> Frames { get; }
    void Start(string host, int port);
    void Stop();
}
public class CameraService : ICameraService
{
    public bool IsWorked => this._running;

    public IObservable<Bitmap> Frames => _frameSubject;
    private readonly Subject<Bitmap> _frameSubject = new();

    private TcpListener? _listener;
    private bool _running;
    private CancellationTokenSource? _cts;

    public void Start(string host, int port)
    {
        if (!_running)
        {
            if (!IPAddress.TryParse(host, out var ip))
                throw new ArgumentException($"Некорректный IP-адрес: {host}");

            _listener = new TcpListener(ip, port);
            _listener.Start();
            _running = true;
            _cts = new CancellationTokenSource();
            _ = AcceptLoop(_cts.Token);
        }
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (_running && _listener != null && !token.IsCancellationRequested)
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var lengthBuffer = new byte[4];

                        while (_running && !token.IsCancellationRequested)
                        {
                            int read = await stream.ReadAsync(lengthBuffer, 0, 4);
                            if (read < 4) break;
                            int length = BitConverter.ToInt32(lengthBuffer, 0);

                            var buffer = new byte[length];
                            int offset = 0;
                            while (offset < length)
                            {
                                int r = await stream.ReadAsync(buffer, offset, length - offset);
                                if (r == 0) break;
                                offset += r;
                            }

                            using var ms = new MemoryStream(buffer);
                            var bitmap = new Bitmap(ms);
                            _frameSubject.OnNext(bitmap);
                        }
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                // Корректное завершение через токен
                break;
            }
            catch (Exception)
            {
                // Логирование или обработка ошибок
            }
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _listener?.Stop();
    }

    public void Dispose()
    {
        Stop();
    }
    ~CameraService()//logical finalizer
    {
        Dispose();
        _frameSubject.Dispose();
    }
}