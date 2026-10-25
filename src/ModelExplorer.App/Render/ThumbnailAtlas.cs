using System.Numerics;
using ModelExplorer.Core.Thumbnails;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace ModelExplorer.App.Render;

/// <summary>Where a thumbnail sits in the atlas: which texture, which part of it, and its size in pixels.</summary>
internal readonly struct AtlasSlot
{
    /// <summary>The page's shader resource view, as the UI library wants it.</summary>
    public nint Texture { get; init; }

    public Vector2 Uv0 { get; init; }

    public Vector2 Uv1 { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

/// <summary>
/// Thumbnails on the GPU: a few large textures divided into equal cells, so a
/// whole grid of them is drawn with a handful of draw calls. Thumbnails are stored
/// at the size they are shown at, one texel per screen pixel, which needs no mip
/// levels and little memory. When every cell is taken, the one unused for longest
/// goes to the newcomer. UI thread only.
/// </summary>
internal sealed unsafe class ThumbnailAtlas : IDisposable
{
    private const int PageSize = 2048;

    private readonly GraphicsDevice _gpu;
    private readonly int _maxPages;
    private readonly List<nint> _textures = [];
    private readonly List<nint> _views = [];
    private readonly Dictionary<long, int> _cellOf = [];
    private Cell[] _cells = [];
    private int _cellSize, _cellsPerRow, _cellsPerPage, _nextFree, _frame;

    public ThumbnailAtlas(GraphicsDevice gpu, int cellSize, int maxPages = 8)
    {
        _gpu = gpu;
        _maxPages = maxPages;
        Reset(cellSize);
    }

    public int CellSize => _cellSize;

    /// <summary>Empties the atlas and sets the cell size. The textures are kept and reused.</summary>
    public void Reset(int cellSize)
    {
        _cellSize = Math.Clamp(cellSize, 16, PageSize);
        _cellsPerRow = PageSize / _cellSize;
        _cellsPerPage = _cellsPerRow * _cellsPerRow;
        _cells = new Cell[_cellsPerPage * _maxPages];
        _cellOf.Clear();
        _nextFree = 0;
    }

    /// <summary>Starts a frame; cells looked up from now on count as in use.</summary>
    public void BeginFrame() => _frame++;

    public bool TryGet(long key, out AtlasSlot slot)
    {
        if (!_cellOf.TryGetValue(key, out var index))
        {
            slot = default;
            return false;
        }

        ref var cell = ref _cells[index];
        cell.LastUsed = _frame;
        int page = index / _cellsPerPage, within = index % _cellsPerPage;
        float x = within % _cellsPerRow * _cellSize, y = within / _cellsPerRow * _cellSize;
        slot = new AtlasSlot
        {
            Texture = _views[page],
            Uv0 = new Vector2(x / PageSize, y / PageSize),
            Uv1 = new Vector2((x + cell.Width) / PageSize, (y + cell.Height) / PageSize),
            Width = cell.Width,
            Height = cell.Height,
        };
        return true;
    }

    /// <summary>Stores an RGBA thumbnail no larger than the cell size.</summary>
    public void Put(long key, RgbaImage image)
    {
        if (image.Width > _cellSize || image.Height > _cellSize)
        {
            return;
        }

        if (!_cellOf.TryGetValue(key, out var index))
        {
            index = Allocate();
            if (index < 0)
            {
                return;
            }

            _cellOf[key] = index;
        }

        _cells[index] = new Cell { Key = key, LastUsed = _frame, Width = (ushort)image.Width, Height = (ushort)image.Height };
        int page = index / _cellsPerPage, within = index % _cellsPerPage;
        uint x = (uint)(within % _cellsPerRow * _cellSize), y = (uint)(within / _cellsPerRow * _cellSize);

        // The whole cell, so a smaller image does not leave the previous tenant's
        // pixels at its edges for a linear filter to pick up.
        var padded = new byte[_cellSize * _cellSize * 4];
        for (var row = 0; row < image.Height; row++)
        {
            Buffer.BlockCopy(image.Pixels, row * image.Width * 4, padded, row * _cellSize * 4, image.Width * 4);
        }

        var box = new D3D11_BOX { left = x, top = y, front = 0, right = x + (uint)_cellSize, bottom = y + (uint)_cellSize, back = 1 };
        fixed (byte* pixels = padded)
        {
            _gpu.Context->UpdateSubresource((ID3D11Resource*)_textures[page], 0, &box, pixels, (uint)(_cellSize * 4), 0);
        }
    }

    public void Clear() => Reset(_cellSize);

    public void Dispose()
    {
        foreach (var view in _views)
        {
            ((ID3D11ShaderResourceView*)view)->Release();
        }

        foreach (var texture in _textures)
        {
            ((ID3D11Texture2D*)texture)->Release();
        }

        _views.Clear();
        _textures.Clear();
        _cellOf.Clear();
    }

    /// <summary>A free cell, a cell on a new page, or the cell unused for longest. -1 if every cell is on screen.</summary>
    private int Allocate()
    {
        var capacity = _textures.Count * _cellsPerPage;
        if (_nextFree < capacity)
        {
            return _nextFree++;
        }

        if (_textures.Count < _maxPages && AddPage())
        {
            return _nextFree++;
        }

        var oldest = -1;
        for (var i = 0; i < capacity; i++)
        {
            if (_cells[i].LastUsed >= _frame - 1)
            {
                continue; // on screen now
            }

            if (oldest < 0 || _cells[i].LastUsed < _cells[oldest].LastUsed)
            {
                oldest = i;
            }
        }

        if (oldest >= 0)
        {
            _cellOf.Remove(_cells[oldest].Key);
        }

        return oldest;
    }

    private bool AddPage()
    {
        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = PageSize;
        desc.Height = PageSize;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        ID3D11Texture2D* texture;
        if (_gpu.Device->CreateTexture2D(&desc, null, &texture).Failed)
        {
            return false; // out of video memory: recycle cells instead
        }

        ID3D11ShaderResourceView* view;
        if (_gpu.Device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view).Failed)
        {
            texture->Release();
            return false;
        }

        _textures.Add((nint)texture);
        _views.Add((nint)view);
        return true;
    }

    private struct Cell
    {
        public long Key;
        public int LastUsed;
        public ushort Width, Height;
    }
}
