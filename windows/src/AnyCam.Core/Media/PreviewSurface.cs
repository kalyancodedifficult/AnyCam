using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AnyCam.Core.Media;

/// <summary>A flip-model swap chain bound to a window (the preview area). Used only on the engine thread.</summary>
public sealed class PreviewSurface : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly IntPtr _hwnd;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _target;
    private int _width;
    private int _height;
    private int _pendingWidth;
    private int _pendingHeight;

    public PreviewSurface(GpuDevice gpu, IntPtr hwnd, int width, int height)
    {
        _gpu = gpu;
        _hwnd = hwnd;
        _pendingWidth = Math.Max(width, 1);
        _pendingHeight = Math.Max(height, 1);
    }

    public IntPtr Hwnd => _hwnd;
    public int Width => _width;
    public int Height => _height;

    /// <summary>Records a new client size; the swap chain is resized before the next frame.</summary>
    public void Resize(int width, int height)
    {
        _pendingWidth = Math.Max(width, 1);
        _pendingHeight = Math.Max(height, 1);
    }

    public bool BeginFrame(out ID3D11RenderTargetView target, out int width, out int height)
    {
        target = null!;
        width = height = 0;
        try
        {
            if (_swapChain is null)
                Create();
            else if (_pendingWidth != _width || _pendingHeight != _height)
                ResizeBuffers();
        }
        catch
        {
            ReleaseTarget();
            _swapChain?.Dispose();
            _swapChain = null;
            return false;
        }

        target = _target!;
        width = _width;
        height = _height;
        return target is not null;
    }

    public void Present()
    {
        if (_swapChain is null)
            return;
        // Never block the engine thread on vsync; a skipped preview frame doesn't matter.
        var result = _swapChain.Present(0, PresentFlags.DoNotWait);
        if (result.Failure && result.Code != Vortice.DXGI.ResultCode.WasStillDrawing.Code)
        {
            ReleaseTarget();
            _swapChain.Dispose();
            _swapChain = null;
        }
    }

    private void Create()
    {
        using var dxgiDevice = _gpu.Device.QueryInterface<IDXGIDevice1>();
        dxgiDevice.MaximumFrameLatency = 1;
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        _width = _pendingWidth;
        _height = _pendingHeight;
        var description = new SwapChainDescription1((uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, false,
            Usage.RenderTargetOutput, 2, Scaling.Stretch, SwapEffect.FlipDiscard, AlphaMode.Ignore, SwapChainFlags.None);
        _swapChain = factory.CreateSwapChainForHwnd(_gpu.Device, _hwnd, description, null, null);
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
        CreateTarget();
    }

    private void ResizeBuffers()
    {
        ReleaseTarget();
        _gpu.Context.ClearState();
        _width = _pendingWidth;
        _height = _pendingHeight;
        _swapChain!.ResizeBuffers(2, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        CreateTarget();
    }

    private void CreateTarget()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _target = _gpu.Device.CreateRenderTargetView(backBuffer, null);
    }

    private void ReleaseTarget()
    {
        _target?.Dispose();
        _target = null;
    }

    public void Dispose()
    {
        ReleaseTarget();
        _swapChain?.Dispose();
        _swapChain = null;
    }
}
