// Copyright � Amer Koleci and Contributors.
// Licensed under the MIT License (MIT). See LICENSE in the repository root for more information.

using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;
using static Vortice.Framework.Utilities;
using Vortice.Direct3D12.Debug;
using System.Diagnostics;
using Vortice.DXGI.Debug;
using SharpGen.Runtime;
using Vortice.Mathematics;
using Vortice.Dxc;
using System.Runtime.InteropServices;

namespace Vortice.Framework;

/// <summary>
/// Class that handles all logic to have D3D12 running application.
/// </summary>
public abstract class D3D12Application : Application
{
    private readonly Format _colorFormat;
    private readonly Format _depthStencilFormat;
    private readonly int _backBufferCount;
    private readonly FeatureLevel _minFeatureLevel;
    private readonly bool _dxgiDebug;
    private IDXGIFactory4 _dxgiFactory;
    private readonly bool _isTearingSupported;

    private readonly ulong[] _fenceValues;
    private readonly ID3D12Fence _frameFence;
    private readonly AutoResetEvent _frameFenceEvent;

    private readonly ID3D12DescriptorHeap _rtvDescriptorHeap;
    private readonly int _rtvDescriptorSize;
    private readonly ID3D12Resource[] _renderTargets;
    private int _backBufferIndex;

    private readonly ID3D12DescriptorHeap? _dsvDescriptorHeap;

    private readonly ID3D12CommandAllocator[] _commandAllocators;

    protected D3D12Application(Format colorFormat = Format.B8G8R8A8_UNorm,
        Format depthStencilFormat = Format.D32_Float,
        int backBufferCount = 2,
        FeatureLevel minFeatureLevel = FeatureLevel.Level_11_0)
    {
        _colorFormat = colorFormat;
        _depthStencilFormat = depthStencilFormat;
        _backBufferCount = backBufferCount;
        _minFeatureLevel = minFeatureLevel;

#if DEBUG
        // Enable the debug layer (requires the Graphics Tools "optional feature").
        //
        // NOTE: Enabling the debug layer after device creation will invalidate the active device.
        {
            if (D3D12GetDebugInterface(out ID3D12Debug? debugController).Success)
            {
                debugController!.EnableDebugLayer();
                debugController.Dispose();
            }
            else
            {
                Debug.WriteLine("WARNING: Direct3D Debug Device is not available");
            }

            if (DXGIGetDebugInterface1(out IDXGIInfoQueue? dxgiInfoQueue).Success)
            {
                _dxgiDebug = true;

                dxgiInfoQueue!.SetBreakOnSeverity(DebugAll, InfoQueueMessageSeverity.Error, true);
                dxgiInfoQueue!.SetBreakOnSeverity(DebugAll, InfoQueueMessageSeverity.Corruption, true);

                int[] hide = new[]
                {
                    80 /* IDXGISwapChain::GetContainingOutput: The swapchain's adapter does not control the output on which the swapchain's window resides. */,
                };
                DXGI.Debug.InfoQueueFilter filter = new();
                filter.DenyList = new DXGI.Debug.InfoQueueFilterDescription
                {
                    Ids = hide
                };
                dxgiInfoQueue!.AddStorageFilterEntries(DebugDxgi, filter);
                dxgiInfoQueue.Dispose();

            }
        }
#endif

        _dxgiFactory = CreateDXGIFactory2<IDXGIFactory4>(_dxgiDebug);

        using (IDXGIFactory5? factory5 = _dxgiFactory.QueryInterfaceOrNull<IDXGIFactory5>())
        {
            if (factory5 != null)
            {
                _isTearingSupported = factory5.PresentAllowTearing;
            }
        }

        ID3D12Device2? device = default;
        using (IDXGIFactory6? factory6 = _dxgiFactory.QueryInterfaceOrNull<IDXGIFactory6>())
        {
            if (factory6 != null)
            {
                for (int adapterIndex = 0; factory6.EnumAdapterByGpuPreference(adapterIndex, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Success; adapterIndex++)
                {
                    AdapterDescription1 desc = adapter!.Description1;

                    // Don't select the Basic Render Driver adapter.
                    if ((desc.Flags & AdapterFlags.Software) != AdapterFlags.None)
                    {
                        adapter.Dispose();

                        continue;
                    }

                    if (D3D12CreateDevice(adapter, _minFeatureLevel, out device).Success)
                    {
                        adapter.Dispose();

                        break;
                    }
                }
            }
            else
            {
                for (int adapterIndex = 0;
                    _dxgiFactory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Success;
                    adapterIndex++)
                {
                    AdapterDescription1 desc = adapter.Description1;

                    // Don't select the Basic Render Driver adapter.
                    if ((desc.Flags & AdapterFlags.Software) != AdapterFlags.None)
                    {
                        adapter.Dispose();

                        continue;
                    }

                    if (D3D12CreateDevice(adapter, _minFeatureLevel, out device).Success)
                    {
                        break;
                    }
                }
            }
        }

        Device = device!;

#if DEBUG
        // Configure debug device (if active).
        {
            using ID3D12InfoQueue? d3dInfoQueue = Device.QueryInterfaceOrNull<ID3D12InfoQueue>();
            if (d3dInfoQueue != null)
            {
                d3dInfoQueue!.SetBreakOnSeverity(MessageSeverity.Corruption, true);
                d3dInfoQueue!.SetBreakOnSeverity(MessageSeverity.Error, true);
                MessageId[] hide = new[]
                {
                    MessageId.MapInvalidNullRange,
                    MessageId.UnmapInvalidNullRange,
                    // Workarounds for debug layer issues on hybrid-graphics systems
                    MessageId.ExecuteCommandListsWrongSwapChainBufferReference,
                    MessageId.ResourceBarrierMismatchingCommandListType,
                };

                Direct3D12.Debug.InfoQueueFilter filter = new();
                filter.DenyList = new Direct3D12.Debug.InfoQueueFilterDescription
                {
                    Ids = hide
                };
                d3dInfoQueue.AddStorageFilterEntries(filter);
            }
        }
#endif

        FeatureLevel = Device.CheckMaxSupportedFeatureLevel();

        // Create Command queue.
        DirectQueue = Device.CreateCommandQueue(CommandListType.Direct);
        DirectQueue.Name = "Direct Queue";

        // Create synchronization objects.
        _fenceValues = new ulong[_backBufferCount];
        _frameFence = Device.CreateFence(_fenceValues[0]);
        _frameFence.Name = "Frame Fence";
        _frameFenceEvent = new AutoResetEvent(false);

        // Create SwapChain
        IntPtr hwnd = MainWindow.Handle;
        int backBufferWidth = Math.Max(MainWindow.ClientSize.Width, 1);
        int backBufferHeight = Math.Max(MainWindow.ClientSize.Height, 1);
        Format backBufferFormat = ToSwapChainFormat(colorFormat);

        SwapChainDescription1 swapChainDesc = new()
        {
            Width = backBufferWidth,
            Height = backBufferHeight,
            Format = backBufferFormat,
            BufferCount = _backBufferCount,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = SampleDescription.Default,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = _isTearingSupported ? SwapChainFlags.AllowTearing : SwapChainFlags.None
        };

        using (IDXGISwapChain1 tempSwapChain = _dxgiFactory.CreateSwapChainForHwnd(DirectQueue, hwnd, swapChainDesc))
        {
            _dxgiFactory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

            SwapChain = tempSwapChain.QueryInterface<IDXGISwapChain3>();
        }

        // Create RTV heap to handle SwapChain RTVs
        _rtvDescriptorHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, _backBufferCount));
        _rtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        // Create frame resources.
        {
            CpuDescriptorHandle rtvHandle = _rtvDescriptorHeap.GetCPUDescriptorHandleForHeapStart();

            // Create a RTV for each frame.
            _renderTargets = new ID3D12Resource[swapChainDesc.BufferCount];
            for (int i = 0; i < swapChainDesc.BufferCount; i++)
            {
                _renderTargets[i] = SwapChain.GetBuffer<ID3D12Resource>(i);
                Device.CreateRenderTargetView(_renderTargets[i], null, rtvHandle);
                rtvHandle += _rtvDescriptorSize;
            }
        }

        if (_depthStencilFormat != Format.Unknown)
        {
            ResourceDescription depthStencilDesc = ResourceDescription.Texture2D(_depthStencilFormat, (uint)swapChainDesc.Width, (uint)swapChainDesc.Height, 1, 1);
            depthStencilDesc.Flags |= ResourceFlags.AllowDepthStencil;

            DepthStencilTexture = Device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                depthStencilDesc,
                ResourceStates.DepthWrite,
                new(_depthStencilFormat, 1.0f, 0)
                );
            DepthStencilTexture.Name = "DepthStencil Texture";

            DepthStencilViewDescription dsViewDesc = new()
            {
                Format = _depthStencilFormat,
                ViewDimension = DepthStencilViewDimension.Texture2D
            };

            _dsvDescriptorHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
            Device.CreateDepthStencilView(DepthStencilTexture, dsViewDesc, _dsvDescriptorHeap.GetCPUDescriptorHandleForHeapStart());
        }

        _commandAllocators = new ID3D12CommandAllocator[swapChainDesc.BufferCount];
        for (int i = 0; i < swapChainDesc.BufferCount; i++)
        {
            _commandAllocators[i] = Device.CreateCommandAllocator(CommandListType.Direct);
        }

        // Create a command list for recording graphics commands.
        CommandList = Device.CreateCommandList<ID3D12GraphicsCommandList4>(CommandListType.Direct, _commandAllocators[0]);
        CommandList.Close();
    }

    public ID3D12Device2 Device { get; }
    public FeatureLevel FeatureLevel { get; }

    public ID3D12CommandQueue DirectQueue { get; }

    public IDXGISwapChain3 SwapChain { get; }
    public int BackBufferIndex => _backBufferIndex;

    public Format ColorFormat => _colorFormat;
    public ID3D12Resource ColorTexture => _renderTargets[_backBufferIndex];
    public CpuDescriptorHandle ColorTextureView
    {
        get => new(_rtvDescriptorHeap.GetCPUDescriptorHandleForHeapStart(), _backBufferIndex, _rtvDescriptorSize);
    }

    public Format DepthStencilFormat => _depthStencilFormat;
    public ID3D12Resource? DepthStencilTexture { get; private set; }
    public CpuDescriptorHandle? DepthStencilView
    {
        get => _dsvDescriptorHeap != null ? _dsvDescriptorHeap.GetCPUDescriptorHandleForHeapStart() : default;
    }

    public ID3D12GraphicsCommandList4 CommandList { get; }
    public ID3D12CommandAllocator CommandAllocator => _commandAllocators[_backBufferIndex];

    /// <summary>
    /// Gets the viewport.
    /// </summary>
    public Viewport Viewport => new(MainWindow.ClientSize.Width, MainWindow.ClientSize.Height);

    public bool UseRenderPass { get; set; }

    protected override void Dispose(bool dispose)
    {
        if (dispose)
        {
            WaitForGpu();

            DepthStencilTexture?.Dispose();
            _dsvDescriptorHeap?.Dispose();

            for (int i = 0; i < _commandAllocators.Length; i++)
            {
                _commandAllocators[i].Dispose();
                _renderTargets[i].Dispose();
            }
            CommandList.Dispose();

            _frameFence.Dispose();
            _rtvDescriptorHeap.Dispose();

            SwapChain.Dispose();
            DirectQueue.Dispose();
            Device.Dispose();
            _dxgiFactory.Dispose();

#if DEBUG
            if (DXGIGetDebugInterface1(out IDXGIDebug1? dxgiDebug).Success)
            {
                dxgiDebug!.ReportLiveObjects(DebugAll, ReportLiveObjectFlags.Summary | ReportLiveObjectFlags.IgnoreInternal);
                dxgiDebug!.Dispose();
            }
#endif
        }

        base.Dispose(dispose);
    }

    protected void WaitForGpu()
    {
        // Schedule a Signal command in the GPU queue.
        ulong fenceValue = _fenceValues[_backBufferIndex];
        DirectQueue.Signal(_frameFence, fenceValue);

        // Wait until the Signal has been processed.
        if (_frameFence.SetEventOnCompletion(fenceValue, _frameFenceEvent).Success)
        {
            _frameFenceEvent.WaitOne();

            // Increment the fence value for the current frame.
            _fenceValues[_backBufferIndex]++;
        }
    }

    private void HandleDeviceLost()
    {

    }

    private void ResizeSwapchain()
    {
    }

    protected internal override void Render()
    {
        CommandAllocator.Reset();
        CommandList.Reset(CommandAllocator);
        CommandList.BeginEvent("Frame");

        // Indicate that the back buffer will be used as a render target.
        CommandList.ResourceBarrierTransition(_renderTargets[_backBufferIndex], ResourceStates.Present, ResourceStates.RenderTarget);

        if (!UseRenderPass)
        {
            CommandList.OMSetRenderTargets(ColorTextureView, DepthStencilView);
        }

        CommandList.RSSetViewport(Viewport);
        CommandList.RSSetScissorRect(MainWindow.ClientSize.Width, MainWindow.ClientSize.Height);

        OnRender();

        if (UseRenderPass)
        {
            CommandList.EndRenderPass();
        }

        // Indicate that the back buffer will now be used to present.
        CommandList.ResourceBarrierTransition(_renderTargets[_backBufferIndex], ResourceStates.RenderTarget, ResourceStates.Present);
        CommandList.EndEvent();
        CommandList.Close();

        // Execute the command list.
        DirectQueue.ExecuteCommandList(CommandList);
    }

    protected abstract void OnRender();

    protected override bool BeginDraw()
    {
        // Check for window size changes and resize the swapchain if needed.
        SwapChainDescription1 swapChainDesc = SwapChain.Description1;

        if (MainWindow.ClientSize.Width != swapChainDesc.Width ||
            MainWindow.ClientSize.Height != swapChainDesc.Height)
        {
            ResizeSwapchain();
        }

        return true;
    }

    protected override void EndDraw()
    {
        int syncInterval = 1;
        PresentFlags presentFlags = PresentFlags.None;
        if (!EnableVerticalSync)
        {
            syncInterval = 0;
            if (_isTearingSupported)
            {
                presentFlags = PresentFlags.AllowTearing;
            }
        }

        Result result = SwapChain.Present(syncInterval, presentFlags);

        // If the device was reset we must completely reinitialize the renderer.
        if (result == DXGI.ResultCode.DeviceRemoved || result == DXGI.ResultCode.DeviceReset)
        {
#if DEBUG
            Result logResult = (result == DXGI.ResultCode.DeviceRemoved) ? Device.DeviceRemovedReason : result;
            Debug.WriteLine($"Device Lost on Present: Reason code {logResult}");
#endif
            HandleDeviceLost();
        }
        else
        {
            result.CheckError();

            MoveToNextFrame();

            if (!_dxgiFactory.IsCurrent)
            {
                UpdateColorSpace();
            }
        }
    }

    private void MoveToNextFrame()
    {
        // Schedule a Signal command in the queue.
        ulong currentFenceValue = _fenceValues[_backBufferIndex];
        DirectQueue.Signal(_frameFence, currentFenceValue);

        // Update the back buffer index.
        _backBufferIndex = SwapChain.CurrentBackBufferIndex;

        // If the next frame is not ready to be rendered yet, wait until it is ready.
        if (_frameFence.CompletedValue < _fenceValues[_backBufferIndex])
        {
            _frameFence.SetEventOnCompletion(_fenceValues[_backBufferIndex], _frameFenceEvent).CheckError();
            _frameFenceEvent.WaitOne();
        }

        // Set the fence value for the next frame.
        _fenceValues[_backBufferIndex] = currentFenceValue + 1;
    }

    private void UpdateColorSpace()
    {
        if (!_dxgiFactory.IsCurrent)
        {
            // Output information is cached on the DXGI Factory. If it is stale we need to create a new factory.
            _dxgiFactory.Dispose();
            _dxgiFactory = CreateDXGIFactory2<IDXGIFactory4>(_dxgiDebug);
        }

        // TODO:
    }

    protected static ReadOnlyMemory<byte> CompileBytecode(
        DxcShaderStage stage,
        string shaderName,
        string entryPoint,
        DxcShaderModel? shaderModel = default)
    {
        string assetsPath = Path.Combine(AppContext.BaseDirectory, "Shaders");
        string fileName = Path.Combine(assetsPath, shaderName);
        string shaderSource = File.ReadAllText(fileName);

        DxcCompilerOptions options = new();
        if (shaderModel != null)
        {
            options.ShaderModel = shaderModel.Value;
        }
        else
        {
            options.ShaderModel = DxcShaderModel.Model6_4;
        }

        using (ShaderIncludeHandler includeHandler = new(assetsPath))
        {
            using IDxcResult results = DxcCompiler.Compile(stage, shaderSource, entryPoint, options,
                fileName: fileName,
                includeHandler: includeHandler);
            if (results.GetStatus().Failure)
            {
                throw new Exception(results.GetErrors());
            }

            return results.GetObjectBytecodeMemory();
        }
    }

    private class ShaderIncludeHandler : CallbackBase, IDxcIncludeHandler
    {
        private readonly string[] _includeDirectories;
        private readonly Dictionary<string, SourceCodeBlob> _sourceFiles = new();

        public ShaderIncludeHandler(params string[] includeDirectories)
        {
            _includeDirectories = includeDirectories;
        }

        protected override void DisposeCore(bool disposing)
        {
            foreach (var pinnedObject in _sourceFiles.Values)
                pinnedObject?.Dispose();

            _sourceFiles.Clear();
        }

        public Result LoadSource(string fileName, out IDxcBlob? includeSource)
        {
            if (fileName.StartsWith("./"))
                fileName = fileName.Substring(2);

            var includeFile = GetFilePath(fileName);

            if (string.IsNullOrEmpty(includeFile))
            {
                includeSource = default;

                return Result.Fail;
            }

            if (!_sourceFiles.TryGetValue(includeFile, out SourceCodeBlob? sourceCodeBlob))
            {
                byte[] data = NewMethod(includeFile);

                sourceCodeBlob = new SourceCodeBlob(data);
                _sourceFiles.Add(includeFile, sourceCodeBlob);
            }

            includeSource = sourceCodeBlob.Blob;

            return Result.Ok;
        }

        private static byte[] NewMethod(string includeFile) => File.ReadAllBytes(includeFile);

        private string? GetFilePath(string fileName)
        {
            for (int i = 0; i < _includeDirectories.Length; i++)
            {
                var filePath = Path.GetFullPath(Path.Combine(_includeDirectories[i], fileName));

                if (File.Exists(filePath))
                    return filePath;
            }

            return null;
        }


        private class SourceCodeBlob : IDisposable
        {
            private byte[] _data;
            private GCHandle _dataPointer;
            private IDxcBlobEncoding? _blob;

            internal IDxcBlob? Blob { get => _blob; }

            public SourceCodeBlob(byte[] data)
            {
                _data = data;

                _dataPointer = GCHandle.Alloc(data, GCHandleType.Pinned);

                _blob = DxcCompiler.Utils.CreateBlob(_dataPointer.AddrOfPinnedObject(), data.Length, Vortice.Dxc.Dxc.DXC_CP_UTF8);
            }

            public void Dispose()
            {
                //_blob?.Dispose();
                _blob = null;

                if (_dataPointer.IsAllocated)
                    _dataPointer.Free();
                _dataPointer = default;
            }
        }
    }
}
