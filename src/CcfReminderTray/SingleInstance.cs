using System.IO.Pipes;
using System.Text;

namespace CcfReminderTray;

/// <summary>
/// 单实例控制：命名 Mutex 保证只有一个常驻进程；
/// 命名管道用于“再次启动时唤起已有实例的详情窗”这一进程间通知。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\CcfReminderTray.SingleInstance";
    private const string PipeName = "CcfReminderTray.SingleInstance.Pipe";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private Thread? _listenerThread;

    /// <summary>另一个进程请求唤起详情窗时触发。触发于监听线程，订阅方需自行封送到 UI 线程。</summary>
    public event EventHandler? ShowRequested;

    /// <summary>
    /// 尝试成为主实例。返回 false 表示已有实例在运行。
    /// 注意：本方法只占位，不启动管道监听；主实例必须先订阅 <see cref="ShowRequested"/>，
    /// 再调用 <see cref="StartListening"/>，否则两次快速启动时早期到达的唤起请求会丢失。
    /// </summary>
    public bool TryBecomePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        return true;
    }

    /// <summary>开始监听“唤起详情窗”请求。必须在订阅 <see cref="ShowRequested"/> 之后调用。</summary>
    public void StartListening()
    {
        if (_mutex is null || _cts is not null)
        {
            return; // 非主实例或已启动
        }

        _cts = new CancellationTokenSource();
        _listenerThread = new Thread(ListenLoop)
        {
            IsBackground = true, // 后台线程：进程退出时自动结束，不阻塞“退出”
            Name = "SingleInstanceListener",
        };
        _listenerThread.Start();
    }

    /// <summary>非主实例调用：通知主实例唤起详情窗。返回是否成功送达（写入管道完成）。</summary>
    public bool NotifyPrimary() => SendShowMessage();

    private void ListenLoop()
    {
        while (_cts is { IsCancellationRequested: false })
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();
                using var reader = new StreamReader(server, Encoding.UTF8);
                _ = reader.ReadToEnd();
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // 管道异常（含进程退出打断）时重建监听；正在取消则退出循环
            }
        }
    }

    private bool SendShowMessage()
    {
        // 主实例可能仍在启动、管道尚未创建：Connect 本身会等待管道出现，
        // 再做有限次重试，覆盖主实例更长的启动窗口；最终失败则如实报告未送达
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(timeout: 1000);
                byte[] payload = Encoding.UTF8.GetBytes("SHOW");
                client.Write(payload, 0, payload.Length);
                return true;
            }
            catch (TimeoutException) { }
            catch (IOException) { }

            Thread.Sleep(300);
        }
        return false;
    }

    public void Dispose()
    {
        if (_cts is not null)
        {
            // 仅启动过监听的主实例需要：自连一次，解除 WaitForConnection 的阻塞。
            // 第二实例（非主实例）在 NotifyPrimary() 之后不得再次发送 SHOW。
            _cts.Cancel();
            SendShowMessage();
            _cts.Dispose();
            _cts = null;
        }

        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* 非持有线程调用时忽略 */ }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
