﻿// Copyright © Amer Koleci and Contributors.
// Licensed under the MIT License (MIT). See LICENSE in the repository root for more information.

#nullable disable

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Framework;
using Vortice.Mathematics;

static class Program
{
    internal unsafe class DrawTexturedCube : D3D11Application
    {
        private ID3D11Buffer _vertexBuffer;
        private ID3D11Buffer _indexBuffer;
        private D3D11ConstantBuffer<Matrix4x4> _constantBuffer;
        private D3D11ConstantBuffer<LightBufferData> _lightConstantBuffer;
        private ID3D11Texture2D _texture;
        private ID3D11ShaderResourceView _textureSRV;
        private ID3D11SamplerState _textureSampler;

        private ID3D11VertexShader _vertexShader;
        private ID3D11PixelShader _pixelShader;
        private ID3D11InputLayout _inputLayout;
        private Stopwatch _clock;

        protected override void Initialize()
        {
            MeshData mesh = MeshUtilities.CreateCube(5.0f);
            _vertexBuffer = Device.CreateBuffer(mesh.Vertices, BindFlags.VertexBuffer);
            _indexBuffer = Device.CreateBuffer(mesh.Indices, BindFlags.IndexBuffer);

            _constantBuffer = new(Device);
            _lightConstantBuffer = new(Device);

            ReadOnlySpan<Color> pixels = stackalloc Color[16] {
                0xFFFFFFFF,
                0x00000000,
                0xFFFFFFFF,
                0x00000000,
                0x00000000,
                0xFFFFFFFF,
                0x00000000,
                0xFFFFFFFF,
                0xFFFFFFFF,
                0x00000000,
                0xFFFFFFFF,
                0x00000000,
                0x00000000,
                0xFFFFFFFF,
                0x00000000,
                0xFFFFFFFF,
            };
            _texture = Device.CreateTexture2D(Format.R8G8B8A8_UNorm, 4, 4, pixels);
            _textureSRV = Device.CreateShaderResourceView(_texture);
            _textureSampler = Device.CreateSamplerState(SamplerDescription.PointWrap);

            ReadOnlyMemory<byte> vertexShaderByteCode = CompileBytecode("Cube.hlsl", "VSMain", "vs_4_0");
            ReadOnlyMemory<byte> pixelShaderByteCode = CompileBytecode("Cube.hlsl", "PSMain", "ps_4_0");

            _vertexShader = Device.CreateVertexShader(vertexShaderByteCode.Span);
            _pixelShader = Device.CreatePixelShader(pixelShaderByteCode.Span);
            _inputLayout = Device.CreateInputLayout(VertexPositionNormalTexture.InputElements, vertexShaderByteCode.Span);

            _clock = Stopwatch.StartNew();
        }

        protected override void Dispose(bool dispose)
        {
            if (dispose)
            {
                _vertexBuffer.Dispose();
                _indexBuffer.Dispose();
                _constantBuffer.Dispose();
                _lightConstantBuffer.Dispose();
                _textureSRV.Dispose();
                _textureSampler.Dispose();
                _texture.Dispose();
                _vertexShader.Dispose();
                _pixelShader.Dispose();
                _inputLayout.Dispose();
            }

            base.Dispose(dispose);
        }

        protected override void OnRender()
        {
            DeviceContext.ClearRenderTargetView(ColorTextureView, Colors.CornflowerBlue);
            DeviceContext.ClearDepthStencilView(DepthStencilView, DepthStencilClearFlags.Depth, 1.0f, 0);

            var time = _clock.ElapsedMilliseconds / 1000.0f;
            Matrix4x4 world = Matrix4x4.CreateRotationX(time) * Matrix4x4.CreateRotationY(time * 2) * Matrix4x4.CreateRotationZ(time * .7f);

            Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 25), new Vector3(0, 0, 0), Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView((float)Math.PI / 4, AspectRatio, 0.1f, 100);
            Matrix4x4 viewProjection = Matrix4x4.Multiply(view, projection);
            Matrix4x4 worldViewProjection = Matrix4x4.Multiply(world, viewProjection);

            // Update constant buffer data
            _constantBuffer.SetData(DeviceContext, ref worldViewProjection);

            float Luminosity = 0.5f;
            LightBufferData lightData = new(Vector3.Zero, 1.0f, 0.0f, 0.0f);
            _lightConstantBuffer.SetData(DeviceContext, ref lightData);

            // Update texture data
            ReadOnlySpan<Color> pixels = stackalloc Color[16] {
                (Color)Colors.Red,
                0x00000000,
                (Color)Colors.Green,
                0x00000000,
                0x00000000,
                (Color)Colors.Blue,
                0x00000000,
                0xFFFFFFFF,
                0xFFFFFFFF,
                0x00000000,
                0xFFFFFFFF,
                0x00000000,
                0x00000000,
                0xFFFFFFFF,
                0x00000000,
                0xFFFFFFFF,
            };
            DeviceContext.WriteTexture(_texture, 0, 0, pixels);

            DeviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            DeviceContext.VSSetShader(_vertexShader);
            DeviceContext.PSSetShader(_pixelShader);
            DeviceContext.IASetInputLayout(_inputLayout);
            DeviceContext.VSSetConstantBuffer(0, _constantBuffer);
            DeviceContext.PSSetConstantBuffer(1, _lightConstantBuffer);
            DeviceContext.PSSetShaderResource(0, _textureSRV);
            DeviceContext.PSSetSampler(0, _textureSampler);
            DeviceContext.IASetVertexBuffer(0, _vertexBuffer, VertexPositionNormalTexture.SizeInBytes);
            DeviceContext.IASetIndexBuffer(_indexBuffer, Format.R16_UInt, 0);
            DeviceContext.DrawIndexed(36, 0, 0);
        }

        struct LightBufferData
        {
            public Vector4 Position;
            public Vector4 Color;

            public LightBufferData(Vector3 position, float r, float g, float b)
            {
                Position = new Vector4(position, 1);
                Color = new Vector4(r, g, b, 1);
            }
        }
    }

    static void Main()
    {
        using DrawTexturedCube app = new();
        app.Run();
    }
}
