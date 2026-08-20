using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinRT;
using static Vortice.Direct3D11.D3D11;

namespace FH6OpenAssist.Vision;

internal sealed class WindowsGraphicsCaptureSession : IDisposable
{
    private static readonly Guid GraphicsCaptureItemId =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static readonly Guid D3D11Texture2DId =
        new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly object _sync = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _deviceContext;
    private readonly IDirect3DDevice _winRtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private CaptureRequest? _pendingRequest;
    private SizeInt32 _framePoolSize;
    private bool _disposed;

    public WindowsGraphicsCaptureSession(IntPtr windowHandle)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException(
                "Windows.Graphics.Capture não é suportado nesta versão do Windows.");
        }

        D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0
                ],
                out _device,
                out _,
                out _deviceContext)
            .CheckError();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        var result = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winRtDevicePointer);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            _winRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(winRtDevicePointer);
        }
        finally
        {
            Marshal.Release(winRtDevicePointer);
        }

        _item = CreateItemForWindow(windowHandle);
        _framePoolSize = _item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _framePoolSize);
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        _framePool.FrameArrived += FramePool_FrameArrived;
        _item.Closed += Item_Closed;
        _session.StartCapture();
    }

    public async Task<Bitmap> CaptureClientAsync(
        Rectangle clientCaptureBounds,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<Bitmap>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new CaptureRequest(clientCaptureBounds, completion);

        lock (_sync)
        {
            if (_pendingRequest is not null)
            {
                throw new InvalidOperationException("Já existe uma captura pendente.");
            }

            _pendingRequest = request;
        }

        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                "Windows.Graphics.Capture não entregou um frame. Verifique se o Forza não está minimizado.",
                exception);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pendingRequest, request))
                {
                    _pendingRequest = null;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CaptureRequest? pending;
        lock (_sync)
        {
            pending = _pendingRequest;
            _pendingRequest = null;
        }

        pending?.Completion.TrySetException(new ObjectDisposedException(nameof(WindowsGraphicsCaptureSession)));
        _framePool.FrameArrived -= FramePool_FrameArrived;
        _item.Closed -= Item_Closed;
        _session.Dispose();
        _framePool.Dispose();
        (_winRtDevice as IDisposable)?.Dispose();
        _deviceContext.Dispose();
        _device.Dispose();
    }

    private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        SizeInt32? resized = null;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            if (frame.ContentSize.Width != _framePoolSize.Width ||
                frame.ContentSize.Height != _framePoolSize.Height)
            {
                resized = frame.ContentSize;
            }

            CaptureRequest? request;
            lock (_sync)
            {
                request = _pendingRequest;
                _pendingRequest = null;
            }

            if (request is not null)
            {
                try
                {
                    request.Completion.TrySetResult(CopyClientBitmap(frame, request.ClientCaptureBounds));
                }
                catch (Exception exception)
                {
                    request.Completion.TrySetException(exception);
                }
            }
        }
        catch (Exception exception)
        {
            FailPending(exception);
        }

        if (resized is { } newSize && newSize.Width > 0 && newSize.Height > 0 && !_disposed)
        {
            try
            {
                _framePool.Recreate(
                    _winRtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    newSize);
                _framePoolSize = newSize;
            }
            catch (Exception exception)
            {
                FailPending(exception);
            }
        }
    }

    private Bitmap CopyClientBitmap(
        Direct3D11CaptureFrame frame,
        Rectangle requestedClientBounds)
    {
        var frameWidth = frame.ContentSize.Width;
        var frameHeight = frame.ContentSize.Height;
        var crop = NormalizeCrop(requestedClientBounds, frameWidth, frameHeight);

        var surfaceAccess = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var textureId = D3D11Texture2DId;
        Marshal.ThrowExceptionForHR(surfaceAccess.GetInterface(ref textureId, out var texturePointer));
        using var source = new ID3D11Texture2D(texturePointer);

        var stagingDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)crop.Width,
            (uint)crop.Height,
            1,
            1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            1,
            0,
            ResourceOptionFlags.None);
        using var staging = _device.CreateTexture2D(stagingDescription);
        var sourceBox = new Box(crop.Left, crop.Top, 0, crop.Right, crop.Bottom, 1);
        _deviceContext.CopySubresourceRegion(staging, 0, 0, 0, 0, source, 0, sourceBox);

        _deviceContext.Map(
            staging,
            0,
            MapMode.Read,
            Vortice.Direct3D11.MapFlags.None,
            out var mapped).CheckError();
        try
        {
            using var mappedBitmap = new Bitmap(
                crop.Width,
                crop.Height,
                checked((int)mapped.RowPitch),
                PixelFormat.Format32bppArgb,
                mapped.DataPointer);
            return mappedBitmap.Clone(
                new Rectangle(0, 0, crop.Width, crop.Height),
                PixelFormat.Format24bppRgb);
        }
        finally
        {
            _deviceContext.Unmap(staging, 0);
        }
    }

    private static Rectangle NormalizeCrop(Rectangle requested, int frameWidth, int frameHeight)
    {
        if (frameWidth == requested.Width && frameHeight == requested.Height)
        {
            return new Rectangle(0, 0, frameWidth, frameHeight);
        }

        var left = Math.Clamp(requested.Left, 0, Math.Max(0, frameWidth - 1));
        var top = Math.Clamp(requested.Top, 0, Math.Max(0, frameHeight - 1));
        var width = Math.Min(requested.Width, frameWidth - left);
        var height = Math.Min(requested.Height, frameHeight - top);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"Área cliente inválida para o frame WGC: frame={frameWidth}x{frameHeight}, área={requested}.");
        }

        return new Rectangle(left, top, width, height);
    }

    private void Item_Closed(GraphicsCaptureItem sender, object args) =>
        FailPending(new InvalidOperationException("A janela capturada foi fechada."));

    private void FailPending(Exception exception)
    {
        CaptureRequest? pending;
        lock (_sync)
        {
            pending = _pendingRequest;
            _pendingRequest = null;
        }

        pending?.Completion.TrySetException(exception);
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr windowHandle)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemId = GraphicsCaptureItemId;
        var itemPointer = interop.CreateForWindow(windowHandle, ref itemId);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    private sealed record CaptureRequest(
        Rectangle ClientCaptureBounds,
        TaskCompletionSource<Bitmap> Completion);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(ref Guid iid, out IntPtr graphicsInterface);
    }

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
}
