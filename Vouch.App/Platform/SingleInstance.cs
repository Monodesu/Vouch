using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Vouch.App.Platform;

/// <summary>
/// Single-instance guard. The first process owns a named mutex and listens on a named pipe; a second
/// process fails to acquire the mutex, pings the pipe (so the running instance shows its window) and
/// exits. Cross-platform (mutex + pipe both work on Windows/Linux/macOS).
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Global\Vouch-SingleInstance-8f3a1c";
    private const string PipeName = "Vouch-SingleInstance-Pipe-8f3a1c";
    private static Mutex? _mutex; // held for the process lifetime

    /// <summary>True if this is the primary instance. If false, the existing instance has been told to
    /// show its window and the caller should exit immediately.</summary>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (createdNew) return true;
        }
        catch (AbandonedMutexException) { return true; } // previous owner died — we take over
        catch { return true; } // if the mutex can't be used, don't block startup

        SignalExisting();
        return false;
    }

    private static void SignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            client.WriteByte(1);
        }
        catch { /* the other instance may be starting or closing — nothing to do */ }
    }

    /// <summary>Listens for a "show" ping from a second instance and invokes <paramref name="onShow"/>.</summary>
    public static void StartServer(Action onShow)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    await server.WaitForConnectionAsync();
                    server.ReadByte();
                    onShow();
                }
                catch { await Task.Delay(500); }
            }
        });
    }
}
