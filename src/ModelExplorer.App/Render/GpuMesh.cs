using System.Numerics;
using ModelExplorer.Geometry;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace ModelExplorer.App.Render;

/// <summary>
/// A mesh's vertex buffers on the GPU. Created on a loading thread, drawn on the
/// UI thread, released by whoever drops it last.
/// </summary>
/// <remarks>
/// Positions and normals go up as two streams straight from <see cref="MeshData"/>'s
/// arrays: no interleaved copy is made, so the upload costs one pass over the data
/// and no transient doubling of memory.
///
/// The unwelded meshes STL produces are drawn without an index buffer, which saves
/// a third of the memory, and in chunks of at most <see cref="ChunkTriangles"/>
/// triangles so that a very large print never asks for a buffer beyond what the
/// driver will create.
/// </remarks>
internal sealed unsafe class GpuMesh : IDisposable
{
    public const int ChunkTriangles = 1 << 20;

    private readonly Chunk[] _chunks;
    private ID3D11Buffer* _indices;
    private int _disposed;

    private GpuMesh(Chunk[] chunks, ID3D11Buffer* indices, int indexCount, long bytes, TimeSpan uploadTime)
    {
        _chunks = chunks;
        _indices = indices;
        IndexCount = indexCount;
        Bytes = bytes;
        UploadTime = uploadTime;
    }

    public long Bytes { get; }

    /// <summary>How long creating the buffers took, for the status bar.</summary>
    public TimeSpan UploadTime { get; }

    public int IndexCount { get; }

    public ReadOnlySpan<Chunk> Chunks => _chunks;

    public bool IsIndexed => _indices != null;

    public ID3D11Buffer* Indices => _indices;

    public static GpuMesh Create(ID3D11Device* device, MeshData mesh, CancellationToken token = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var sequential = IsSequential(mesh.Indices);
        var chunks = new List<Chunk>();
        ID3D11Buffer* indices = null;
        long bytes = 0;
        try
        {
            if (sequential)
            {
                var triangles = mesh.Indices.Length / 3;
                for (var first = 0; first < triangles; first += ChunkTriangles)
                {
                    token.ThrowIfCancellationRequested();
                    var count = Math.Min(ChunkTriangles, triangles - first);
                    chunks.Add(new Chunk(
                        CreateVertexBuffer(device, mesh.Positions.AsSpan(first * 3, count * 3)),
                        CreateVertexBuffer(device, mesh.Normals.AsSpan(first * 3, count * 3)),
                        count * 3,
                        first));
                    bytes += count * 3L * 24;
                }
            }
            else
            {
                chunks.Add(new Chunk(
                    CreateVertexBuffer(device, mesh.Positions),
                    CreateVertexBuffer(device, mesh.Normals),
                    mesh.Positions.Length,
                    0));
                indices = CreateBuffer(device, MemoryMarshalBytes(mesh.Indices), D3D11_BIND_FLAG.D3D11_BIND_INDEX_BUFFER);
                bytes = (mesh.Positions.Length * 24L) + (mesh.Indices.Length * 4L);
            }

            return new GpuMesh([.. chunks], indices, sequential ? 0 : mesh.Indices.Length, bytes, stopwatch.Elapsed);
        }
        catch
        {
            foreach (var chunk in chunks)
            {
                chunk.Release();
            }

            if (indices != null)
            {
                indices->Release();
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var chunk in _chunks)
        {
            chunk.Release();
        }

        if (_indices != null)
        {
            _indices->Release();
            _indices = null;
        }
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Whether index i is i throughout: an unwelded mesh, drawable without the index buffer.</summary>
    private static bool IsSequential(int[] indices)
    {
        for (var i = 0; i < indices.Length; i++)
        {
            if (indices[i] != i)
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<byte> MemoryMarshalBytes<T>(ReadOnlySpan<T> values) where T : unmanaged =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(values);

    private static ID3D11Buffer* CreateVertexBuffer(ID3D11Device* device, ReadOnlySpan<Vector3> values) =>
        CreateBuffer(device, MemoryMarshalBytes(values), D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER);

    internal static ID3D11Buffer* CreateBuffer(ID3D11Device* device, ReadOnlySpan<byte> data, D3D11_BIND_FLAG bind)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("A GPU buffer cannot be empty.", nameof(data));
        }

        D3D11_BUFFER_DESC desc = default;
        desc.ByteWidth = (uint)data.Length;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE;
        desc.BindFlags = bind;
        ID3D11Buffer* buffer;
        fixed (byte* bytes = data)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = bytes };
            Hr.Check(device->CreateBuffer(&desc, &initial, &buffer), "CreateBuffer");
        }

        return buffer;
    }

    /// <summary>One draw call's worth: a run of triangles with its two vertex streams.</summary>
    internal readonly struct Chunk(ID3D11Buffer* positions, ID3D11Buffer* normals, int vertexCount, int firstTriangle)
    {
        public ID3D11Buffer* Positions { get; } = positions;

        public ID3D11Buffer* Normals { get; } = normals;

        public int VertexCount { get; } = vertexCount;

        /// <summary>Triangles drawn by the chunks before this one, for per-triangle data.</summary>
        public int FirstTriangle { get; } = firstTriangle;

        public void Release()
        {
            if (Positions != null)
            {
                Positions->Release();
            }

            if (Normals != null)
            {
                Normals->Release();
            }
        }
    }
}

/// <summary>A float per triangle on the GPU, which the deviation view colours by.</summary>
internal sealed unsafe class GpuDistances : IDisposable
{
    private ID3D11Buffer* _buffer;
    private ID3D11ShaderResourceView* _view;

    private GpuDistances(ID3D11Buffer* buffer, ID3D11ShaderResourceView* view)
    {
        _buffer = buffer;
        _view = view;
    }

    public ID3D11ShaderResourceView* View => _view;

    public static GpuDistances Create(ID3D11Device* device, float[] distances)
    {
        var buffer = GpuMesh.CreateBuffer(device, System.Runtime.InteropServices.MemoryMarshal.AsBytes(distances.AsSpan()),
            D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
        D3D11_SHADER_RESOURCE_VIEW_DESC desc = default;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT;
        desc.ViewDimension = Windows.Win32.Graphics.Direct3D.D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_BUFFER;
        desc.Anonymous.Buffer.Anonymous2.NumElements = (uint)distances.Length;
        ID3D11ShaderResourceView* view;
        var hr = device->CreateShaderResourceView((ID3D11Resource*)buffer, &desc, &view);
        if (hr.Failed)
        {
            buffer->Release();
            Hr.Check(hr, "CreateShaderResourceView");
        }

        return new GpuDistances(buffer, view);
    }

    public void Dispose()
    {
        if (_view != null)
        {
            _view->Release();
            _view = null;
        }

        if (_buffer != null)
        {
            _buffer->Release();
            _buffer = null;
        }
    }
}
