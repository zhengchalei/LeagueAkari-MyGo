using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.CompilerServices;
using Windows.Storage.Streams;
namespace LeagueAkari.WinUI.Services;

public sealed class NativeImages(BackendClient backend)
{
    private static readonly ConditionalWeakTable<BackendClient, ImageCache> Caches = new();
    private readonly ImageCache _cache = Caches.GetValue(backend, static value => new(value));
    private readonly ConditionalWeakTable<Image, Request> _requests = new();
    private sealed class Request { public int Version; }

    public async Task SetAsync(Image image, string path)
    {
        var request = _requests.GetOrCreateValue(image);
        int version = ++request.Version;
        // Decode for the displayed size rather than retaining full portraits for small icons.
        int width = (int)Math.Clamp(Math.Ceiling((double.IsFinite(image.Width) ? image.Width : 64) * 2), 32, 1024);
        try
        {
            var bitmap = await _cache.BitmapAsync(path, width);
            if (request.Version == version) image.Source = bitmap;
        }
        catch
        {
            if (request.Version == version) image.Source = null;
        }
    }

    private sealed class ImageCache(BackendClient backend)
    {
        private readonly Dictionary<string, Task<byte[]?>> _bytes = new();
        private readonly Dictionary<(string Path, int Width), Task<BitmapImage?>> _bitmaps = new();
        private readonly Queue<(string Path, Task<byte[]?> Task)> _byteOrder = new();
        private readonly Queue<((string Path, int Width) Key, Task<BitmapImage?> Task)> _bitmapOrder = new();
        private long _byteSize;

        public Task<BitmapImage?> BitmapAsync(string path, int width)
        {
            var key = (path, width);
            if (_bitmaps.TryGetValue(key, out var existing) && (!existing.IsCompleted || existing.IsCompletedSuccessfully && existing.Result is not null)) return existing;
            var task = DecodeAsync(path, width);
            _bitmaps[key] = task;
            _bitmapOrder.Enqueue((key, task));
            while ((_bitmaps.Count > 256 || _bitmapOrder.Count > 512) && _bitmapOrder.TryDequeue(out var oldest))
                if (_bitmaps.TryGetValue(oldest.Key, out var current) && ReferenceEquals(current, oldest.Task)) _bitmaps.Remove(oldest.Key);
            return task;
        }

        private async Task<BitmapImage?> DecodeAsync(string path, int width)
        {
            byte[]? bytes;
            if (_bytes.TryGetValue(path, out var pending) && (!pending.IsCompleted || pending.IsCompletedSuccessfully && pending.Result is not null)) bytes = await pending;
            else
            {
                pending = backend.ImageAsync(path);
                _bytes[path] = pending;
                _byteOrder.Enqueue((path, pending));
                bytes = await pending;
                if (_bytes.TryGetValue(path, out var current) && ReferenceEquals(current, pending)) _byteSize += bytes?.LongLength ?? 0;
                while ((_byteSize > 12 * 1024 * 1024 || _bytes.Count > 128 || _byteOrder.Count > 256) && _byteOrder.TryDequeue(out var oldest))
                    if (_bytes.TryGetValue(oldest.Path, out var removed) && ReferenceEquals(removed, oldest.Task))
                    {
                        _bytes.Remove(oldest.Path);
                        if (removed.IsCompletedSuccessfully) _byteSize -= removed.Result?.LongLength ?? 0;
                    }
            }
            if (bytes is null) return null;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = width };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
    }
}
